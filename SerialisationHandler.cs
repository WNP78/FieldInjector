using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using UnhollowerBaseLib;
using UnhollowerBaseLib.Runtime.VersionSpecific.FieldInfo;
using static MelonLoader.MelonLogger;
using static UnhollowerBaseLib.Runtime.UnityVersionHandler;
using static UnhollowerBaseLib.IL2CPP;
using UnhollowerBaseLib.Runtime;
using MelonLoader;
using UnhollowerBaseLib.Runtime.VersionSpecific.Type;
using UnhollowerRuntimeLib;
using UnityEngine;
using System.Linq;
using UnhollowerBaseLib.Runtime.VersionSpecific.Class;
using UnhollowerBaseLib.Runtime.VersionSpecific.MethodInfo;
using System.Threading;

namespace FieldInjector
{
    public static unsafe class SerialisationHandler
    {
        #region Simple Action<IntPtr> Invoker
        private static IntPtr invokerPtr;

        static SerialisationHandler()
        {
            var del = new InvokerDelegate(StaticVoidIntPtrInvoker);
            GCHandle.Alloc(del, GCHandleType.Normal); // prevent GC of our delegate
            invokerPtr = Marshal.GetFunctionPointerForDelegate(del);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr InvokerDelegate(IntPtr methodPointer, Il2CppMethodInfo* methodInfo, IntPtr obj, IntPtr* args);

        private delegate void StaticVoidIntPtrDelegate(IntPtr intPtr);

        private static IntPtr StaticVoidIntPtrInvoker(IntPtr methodPointer, Il2CppMethodInfo* methodInfo, IntPtr obj, IntPtr* args)
        {
            Marshal.GetDelegateForFunctionPointer<StaticVoidIntPtrDelegate>(methodPointer)(obj);
            return IntPtr.Zero;
        }
        #endregion

        #region Main Serialiser

        private static readonly Dictionary<Type, List<SerialisedField>> serialisationCache = new Dictionary<Type, List<SerialisedField>>();

        private static Il2CppImage* Image;

        public static void Inject<T>(int debugLevel = 1) where T : MonoBehaviour
        {
            //ClassInjector.RegisterTypeInIl2Cpp<T>(false);
            //return;
            InjectRecursive(typeof(T), debugLevel);
        }

        public static void Inject(Type t, int debugLevel = 1)
        {
            //ClassInjector.RegisterTypeInIl2Cpp(t, false);
            //return;
            InjectRecursive(t, debugLevel);
        }

        private static List<SerialisedField> InjectRecursive(Type t, int debugLevel)
        {
            if (t == typeof(MonoBehaviour) || t == typeof(ScriptableObject) || t == typeof(UnityEngine.Object) || t == null)
            {
                return null;
            }

            if (serialisationCache.TryGetValue(t, out var fields))
            {
                return fields;
            }

            fields = InjectRecursive(t.BaseType, debugLevel);
            if (fields == null) { fields = new List<SerialisedField>(); }
            else { fields = new List<SerialisedField>(fields); }

            var result = InjectClassImpl(t, debugLevel, fields);
            fields.AddRange(result);
            serialisationCache.Add(t, fields);
            return fields;
        }

        private static SerialisedField[] InjectClassImpl(Type t, int debugLevel, List<SerialisedField> baseFields)
        {
            if (GetClassPointerForType(t) != IntPtr.Zero)
            {
                throw new InvalidOperationException($"Type {t} already injected in IL2CPP - cannot inject serialisation");
            }

            void Log(string message, int level)
            {
                if (debugLevel >= level)
                {
                    Msg(message);
                }
            }

            Log($"Injecting serialisation for type {t}", 1);

            // Inject class and get a reference to it.
            ClassInjector.RegisterTypeInIl2CppWithInterfaces(t, false, typeof(ISerializationCallbackReceiver));
            var klassPtr = (MyIl2CppClass*)GetClassPointerForType(t, bypassEnums: true);
            var klass = Wrap((Il2CppClass*)klassPtr);

            Image = klass.Image;

            // fix unhollower not setting namespace field if there's no namespace
            if (klassPtr->namespaze == IntPtr.Zero)
            {
                klassPtr->namespaze = Marshal.StringToHGlobalAnsi(string.Empty);
            }

            var baseKlassPtr = (MyIl2CppClass*)GetClassPointerForType(t.BaseType);

            // fix finalizer
            FixFinaliser(klass);

            // Select serialisable fields, make serialiser classes.
            var bflags = BindingFlags.Instance | BindingFlags.DeclaredOnly;
            SerialisedField[] injectedFields =
                t.GetFields(bflags | BindingFlags.Public)
                .Where(field => !field.IsNotSerialized)
                .Select(field => TrySerialise(field))
                .Where(field => field != null)
                .ToArray();

            int numBaseFields = baseFields.Count;
            SerialisedField[] allFields = new SerialisedField[numBaseFields + injectedFields.Length];
            baseFields.CopyTo(allFields);
            injectedFields.CopyTo(allFields, numBaseFields);

            // Unhollower uses the last IntPtr of a class for a GCHandle of the managed object - we inject fields before this
            int offset = (int)klass.ActualSize - IntPtr.Size;

            // Allocate and fill fields array
            var fieldsStore = (MyIl2CppFieldInfo*)Marshal.AllocHGlobal(allFields.Length * Marshal.SizeOf(typeof(MyIl2CppFieldInfo)));

            // Copy base fields
            for (int i = 0; i < numBaseFields; i++)
            {
                fieldsStore[i] = baseKlassPtr->fields[i];
            }

            // Create new fields
            for (int i = 0; i < injectedFields.Length; i++)
            {
                var field = injectedFields[i];
                Log($"Converting field {field.ManagedField} as {field}", 2);

                var nativeField = Wrap((Il2CppFieldInfo*)(fieldsStore + numBaseFields + i));
                field.FillFieldInfoStruct(nativeField, (Il2CppClass*)klassPtr, ref offset);
                field.NativeField = nativeField.Pointer;
            }

            // Assign the field array
            klassPtr->field_count = (ushort)allFields.Length;
            klassPtr->fields = fieldsStore;

            Log($"Injected {injectedFields.Length} fields (for a total of {allFields.Length}), changing class size from {klass.ActualSize} to {offset + IntPtr.Size}", 2);

            // Reassign our new size, remembering the last IntPtr.
            klass.ActualSize = klass.InstanceSize = (uint)(offset + IntPtr.Size);

            // Preparing to do serialisation - find some info about the ISerializationCallbackReceiver
            Il2CppClass* callbackRecieverClass = (Il2CppClass*)Il2CppClassPointerStore<ISerializationCallbackReceiver>.NativeClassPtr;
            var iface = Wrap(callbackRecieverClass);

            int interfaceIndex = 0;
            for (; interfaceIndex < klass.InterfaceCount; interfaceIndex++)
            {
                if (klass.ImplementedInterfaces[interfaceIndex] == callbackRecieverClass)
                {
                    break;
                }
            }

            if (interfaceIndex == klass.InterfaceCount)
            {
                throw new InvalidOperationException("Could not find serialisation callbacks interface!");
            }

            if (interfaceIndex >= klass.InterfaceOffsetsCount)
            {
                throw new InvalidOperationException("interface is >= interface offsets count!");
            }

            int interfaceOffset = klass.InterfaceOffsets[interfaceIndex].offset;

            // Now create the serialisation methods - this code is common to both
            var nativePtr = Expression.Parameter(typeof(IntPtr), "nativeObjPtr");
            var managedObj = Expression.Variable(t, "managedObj");
            var fieldPtr = Expression.Variable(typeof(IntPtr), "fieldPtr");

            MethodInfo getMonoObjectMethod = ((Func<IntPtr, object>)ClassInjectorBase.GetMonoObjectFromIl2CppPointer).Method;

            Expression[] setupExpressions = new Expression[]
            {
                // managedObj = ClassInjectorBase.GetMonoObjectFromIl2CppPointer(nativeObjPtr);
                Expression.Assign(managedObj,
                    Expression.Convert(
                        Expression.Call(getMonoObjectMethod, nativePtr),
                        t)),
            };

            // Create and inject the deserialiser

            var expressions = setupExpressions
                .Concat(
                    allFields
                    .SelectMany(field => field.GetDeserialiseExpression(managedObj, nativePtr, fieldPtr))
                    );

            if (debugLevel >= 3) 
            {
                expressions = expressions.Prepend(LogExpression("Deserialise: ", nativePtr));
                expressions = expressions.Append(LogExpression("Deserialise complete: ", nativePtr));
            }

            var deserialiseExpression = Expression.Block(
                new ParameterExpression[] { managedObj, fieldPtr },
                expressions);

            Log($"Generated deserialiser method:\n{string.Join("\n", deserialiseExpression.Expressions)}", 3);

            var deserialiseMethod = Expression.Lambda<StaticVoidIntPtrDelegate>(deserialiseExpression, nativePtr);
            EmitSerialiserMethod(deserialiseMethod, t, klass, nameof(ISerializationCallbackReceiver.OnAfterDeserialize), iface, interfaceOffset, debugLevel);

            // Now the serialiser
            expressions = setupExpressions.Concat(
                allFields
                .SelectMany(field => field.GetSerialiseExpression(managedObj, nativePtr)));

            if (debugLevel >= 3)
            {
                expressions = expressions.Prepend(LogExpression("Serialise: ", nativePtr));
                expressions = expressions.Append(LogExpression("Serialise complete: ", nativePtr));
            }

            var serialiseExpression = Expression.Block(
                new ParameterExpression[] { managedObj },
                expressions);

            Log($"Generated serialiser method: \n{string.Join("\n", serialiseExpression.Expressions)}", 3);

            var serialiseMethod = Expression.Lambda<StaticVoidIntPtrDelegate>(serialiseExpression, nativePtr);
            EmitSerialiserMethod(serialiseMethod, t, klass, nameof(ISerializationCallbackReceiver.OnBeforeSerialize), iface, interfaceOffset, debugLevel);

            Log($"Completed serialisation injection for type {t}", 2);

            return injectedFields;
        }

        private static void FixFinaliser(INativeClassStruct klass)
        {
            if (klass.HasFinalize)
            {
                var method = Wrap(klass.Methods[0]);
                method.MethodPointer = Marshal.GetFunctionPointerForDelegate(FinalizeDelegate);
                method.InvokerMethod = invokerPtr;
            }
        }

        private static void EmitSerialiserMethod(LambdaExpression lambda, Type monoType, INativeClassStruct klass, string name, INativeClassStruct iface, int interfaceOffset, int debugLevel)
        {
            // Find the VTable slot for our element, and the original interface method
            VirtualInvokeData* vtableElement = default;
            INativeMethodInfoStruct ifaceMethod = default;

            VirtualInvokeData* vtablePtr = (VirtualInvokeData*)klass.VTable;
            for (int i = 0; i < iface.MethodCount; i++)
            {
                ifaceMethod = Wrap(iface.Methods[i]);
                if (Marshal.PtrToStringAnsi(ifaceMethod.Name) == name)
                {
                    vtableElement = vtablePtr + (i + interfaceOffset);
                    if (debugLevel > 3)
                    {
                        Msg($"Injecting {name} in vtable slot {i + interfaceOffset}");
                    }
                    break;
                }
            }

            if (vtablePtr == default)
            {
                throw new InvalidOperationException($"Can't find interface method {name}");
            }

            var compiledDelegate = lambda.Compile();
            GCHandle.Alloc(compiledDelegate, GCHandleType.Normal); // no more GC!

            var generated = NewMethod();
            generated.Name = Marshal.StringToHGlobalAnsi(name);
            generated.Class = klass.ClassPointer;
            generated.ReturnType = ifaceMethod.ReturnType;
            generated.Flags = Il2CppMethodFlags.METHOD_ATTRIBUTE_PUBLIC | Il2CppMethodFlags.METHOD_ATTRIBUTE_HIDE_BY_SIG;

            generated.InvokerMethod = invokerPtr;
            generated.MethodPointer = Marshal.GetFunctionPointerForDelegate(compiledDelegate);

            vtableElement->method = generated.MethodInfoPointer;
            vtableElement->methodPtr = generated.MethodPointer;
        }

        private static SerialisedField TrySerialise(FieldInfo field)
        {
            try
            {
                return SerialisedField.InferFromField(field);
            }
            catch (Exception ex)
            {
                Warning($"Not serialising field {field} due to error: {ex.Message}\n{ex.StackTrace}");
            }

            return null;
        }

        private abstract class SerialisedField
        {
            private FieldInfo field;

            protected abstract IntPtr fieldType { get; }

            public IntPtr NativeField { get; set; }

            public FieldInfo ManagedField => this.field;

            protected virtual Type targetType => this.field.FieldType;

            protected SerialisedField(FieldInfo field)
            {
                this.field = field;
            }

            protected abstract Expression GetNativeToMonoExpression(Expression nativePtr);

            protected abstract Expression GetMonoToNativeExpression(Expression monoObj);

            /// <summary>
            /// Gets the deserialise expression.
            /// </summary>
            /// <param name="monoObj">The mono object.</param>
            /// <param name="nativePtr">The native PTR.</param>
            /// <param name="fieldPtr">The field PTR.</param>
            /// <returns></returns>
            /// <exception cref="System.InvalidOperationException">Something went very wrong</exception>
            public virtual IEnumerable<Expression> GetDeserialiseExpression(Expression monoObj, Expression nativePtr, Expression fieldPtr)
            {
                // OLD:
                // monoObj.field = NativeToMono(il2cpp_field_get_value_object(nativeFieldInfo, nativePtr));
                /* New 
                 * fieldPtr = il2cpp_field_get_value_object(nativeFieldInfo, nativePtr);
                 * monoObj.field = fieldPtr != IntPtr.Zero ? NativeToMono(fieldPtr) : default;
                */
                if (this.NativeField == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Something went very wrong");
                }

                MethodInfo get_value = ((Func<IntPtr, IntPtr, IntPtr>)il2cpp_field_get_value_object).Method;

                Expression fieldValuePtr = Expression.Call(
                    get_value,
                    Expression.Constant(this.NativeField),
                    nativePtr);

                yield return Expression.Assign(fieldPtr, fieldValuePtr);

                Expression hasValue = Expression.NotEqual(fieldPtr, Expression.Constant(IntPtr.Zero));

                yield return Expression.Assign(Expression.Field(monoObj, this.field),
                    Expression.Condition(hasValue,
                        this.GetNativeToMonoExpression(fieldPtr),
                        Expression.Default(this.field.FieldType)
                        )
                    );
            }

            public virtual IEnumerable<Expression> GetSerialiseExpression(Expression monoObj, Expression nativePtr)
            {
                // valueType:
                // field_set_value_object(nativePtr, this.NativeField, MonoToNative(monoObj.field))
                // refType:
                // field_set_value_object(nativePtr, this.NativeField, monoObj.field != null ? MonoToNative(monoObj.field) : IntPtr.Zero)

                if (this.NativeField == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Something went very wrong");
                }

                MethodInfo set_value = ((Action<IntPtr, IntPtr, IntPtr>)field_set_value_object).Method;

                Expression monoValue = Expression.Field(monoObj, this.field);

                Expression fieldValuePtr = this.GetMonoToNativeExpression(monoValue);

                if (!this.field.FieldType.IsValueType)
                {
                    fieldValuePtr = Expression.Condition(
                        Expression.NotEqual(monoValue, Expression.Constant(null)),
                        fieldValuePtr, Expression.Constant(IntPtr.Zero));
                }


                yield return Expression.Call(set_value, 
                    nativePtr,
                    Expression.Constant(this.NativeField),
                    fieldValuePtr);
            }

            private static void field_set_value_object(IntPtr instance, IntPtr field, IntPtr obj)
            {
                il2cpp_field_set_value(instance, field, (void*)obj);
            }

            public static SerialisedField InferFromField(FieldInfo field)
            {
                Type fieldType = field.FieldType;
                if (typeof(Il2CppSystem.Object).IsAssignableFrom(fieldType))
                {
                    return new ObjectField(field);
                }
                else if (fieldType == typeof(string))
                {
                    return new StringField(field);
                }
                else if (fieldType.IsValueType)
                {
                    return new StructField(field);
                }
                else if (fieldType.IsArray && fieldType.GetArrayRank() == 1)
                {
                    return new ArrayField(field);
                }

                throw new NotSupportedException($"Field type not supported: {field}");
            }

            public abstract int GetFieldSize(out int align);

            public void FillFieldInfoStruct(INativeFieldInfoStruct infoOut, Il2CppClass* klassPtr, ref int offset)
            {
                infoOut.Name = Marshal.StringToHGlobalAnsi(this.field.Name);
                infoOut.Parent = klassPtr;

                var typePtr = (MyIl2CppType*)Marshal.AllocHGlobal(Marshal.SizeOf<MyIl2CppType>());
                *typePtr = *(MyIl2CppType*)this.fieldType;

                typePtr->attrs = (ushort)Il2CppSystem.Reflection.FieldAttributes.Public;

                infoOut.Type = (Il2CppTypeStruct*)typePtr;

                //var size = GetTypeSize(Wrap((Il2CppTypeStruct*)typePtr), out uint align);
                var size = this.GetFieldSize(out int align);
                offset = AlignTo(offset, align);

                infoOut.Offset = offset;

                offset += size;
            }

            public override string ToString()
            {
                return this.GetType().Name + ":" + this.targetType;
            }

            private class ObjectField : SerialisedField
            {
                protected override IntPtr fieldType => il2cpp_class_get_type(GetClassPointerForType(this.targetType));

                public override int GetFieldSize(out int align)
                {
                    align = 0;
                    return sizeof(IntPtr);
                }

                public ObjectField(FieldInfo field) : base(field) { }

                protected override Expression GetNativeToMonoExpression(Expression nativePtr)
                {
                    var ctor = this.targetType.GetConstructor(new Type[] { typeof(IntPtr) });
                    return Expression.New(ctor, nativePtr);
                }

                protected override Expression GetMonoToNativeExpression(Expression monoObj)
                {
                    var prop = typeof(Il2CppSystem.Object).GetProperty("Pointer");
                    return Expression.Property(monoObj, prop);
                }
            }

            private class StringField : SerialisedField
            {
                protected override IntPtr fieldType => il2cpp_class_get_type(Il2CppClassPointerStore<string>.NativeClassPtr);

                public StringField(FieldInfo field) : base(field) { }

                public override int GetFieldSize(out int align)
                {
                    align = 0;
                    return sizeof(IntPtr);
                }

                protected override Expression GetNativeToMonoExpression(Expression nativePtr)
                {
                    var method = ((Func<IntPtr, string>)Il2CppStringToManaged).Method;
                    return Expression.Call(method, nativePtr);
                }

                protected override Expression GetMonoToNativeExpression(Expression monoObj)
                {
                    var method = ((Func<string, IntPtr>)ManagedStringToIl2Cpp).Method;
                    return Expression.Call(method, monoObj);
                }
            }

            private class StructField : SerialisedField
            {
                public StructField(FieldInfo field) : base(field)
                {
                    this.fieldClass = GetClassPointerForType(this.targetType);
                    this._fieldType = il2cpp_class_get_type(this.fieldClass);
                    this.tempPtr = Expression.Parameter(typeof(IntPtr), "tempStorage");

                    if (this.targetType.IsEnum)
                    {
                        this.marshalType = this.targetType.GetEnumUnderlyingType();
                    }
                }

                private IntPtr fieldClass;

                private IntPtr _fieldType;

                private ParameterExpression tempPtr;

                private Type marshalType;

                protected override IntPtr fieldType => this._fieldType;

                public unsafe override int GetFieldSize(out int align)
                {
                    align = 0;
                    return (int)(Wrap((Il2CppClass*)this.fieldClass).ActualSize - Marshal.SizeOf<Il2CppObject>());
                }

                private unsafe static IntPtr StructToHGlobal(object value, int size)
                {
                    IntPtr ptr = Marshal.AllocHGlobal(size);
                    Marshal.StructureToPtr(value, ptr, false);
                    return ptr;
                }

                protected Expression AllocAndCopy(Expression monoObj)
                {
                    // StructToHGlobal(monoObj, sizeof(T))
                    var converter = ((Func<object, int, IntPtr>)StructToHGlobal).Method;

                    int size;
                    if (this.marshalType != null)
                    {
                        monoObj = Expression.Convert(monoObj, this.marshalType);
                        size = Marshal.SizeOf(this.marshalType);
                    }
                    else
                    {
                        size = Marshal.SizeOf(monoObj.Type);
                    }

                    return Expression.Call(converter, 
                        Expression.Convert(monoObj, typeof(object)),
                        Expression.Constant(size));
                }

                protected override Expression GetMonoToNativeExpression(Expression monoValue)
                {
                    return this.tempPtr;
                }

                public override IEnumerable<Expression> GetSerialiseExpression(Expression monoObj, Expression nativePtr)
                {
                    var freeMethod = new Action<IntPtr>(Marshal.FreeHGlobal).Method;

                    if (this.NativeField == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("Something went very wrong");
                    }

                    MethodInfo set_value = ((Action<IntPtr, IntPtr, IntPtr>)field_set_value_object).Method;

                    Expression monoValue = Expression.Field(monoObj, this.field);

                    Expression allocAndCopy = Expression.Assign(this.tempPtr, this.AllocAndCopy(monoValue));

                    Expression fieldValuePtr = this.tempPtr;

                    Expression serialise = Expression.Call(set_value,
                        nativePtr,
                        Expression.Constant(this.NativeField),
                        this.tempPtr);

                    Expression free = Expression.Call(freeMethod, this.tempPtr);

                    Msg($"Struct serialiser for {this.field}:\n{allocAndCopy}\n{serialise}\n{free}");

                    yield return Expression.Block(
                        new[] { this.tempPtr },
                        new[]
                        {
                            allocAndCopy,
                            serialise,
                            free,
                        });
                }

                protected override Expression GetNativeToMonoExpression(Expression nativePtr)
                {
                    // for normal struct types
                    // new Il2CppSystem.Object(ptr).Unbox<T>()
                    var ctor = typeof(Il2CppSystem.Object).GetConstructor(new Type[] { typeof(IntPtr) });
                    var unbox = typeof(Il2CppSystem.Object).GetMethod("Unbox")
                                    .MakeGenericMethod(this.targetType);

                    Expression res = Expression.Call(Expression.New(ctor, nativePtr), unbox);

                    if (this.field.FieldType.IsEnum)
                    {
                        res = Expression.Convert(res, this.field.FieldType);
                    }

                    return res;
                }
            }

            private class ArrayField : SerialisedField
            {
                private IntPtr _fieldType;

                private Type _proxyType;

                private Type _proxyBaseType;

                public override int GetFieldSize(out int align)
                {
                    align = 0;
                    return sizeof(IntPtr);
                }

                public ArrayField(FieldInfo field) : base(field)
                {
                    var elementType = field.FieldType.GetElementType();
                    if (elementType.IsValueType)
                    {
                        this._proxyType = typeof(Il2CppStructArray<>).MakeGenericType(elementType);
                        this._proxyBaseType = typeof(Il2CppArrayBase<>).MakeGenericType(elementType);
                    }
                    else if (typeof(Il2CppSystem.Object).IsAssignableFrom(elementType))
                    {
                        this._proxyType = typeof(Il2CppReferenceArray<>).MakeGenericType(elementType);
                        this._proxyBaseType = typeof(Il2CppArrayBase<>).MakeGenericType(elementType);
                    }
                    else if (elementType == typeof(string)) 
                    { 
                        this._proxyType = typeof(Il2CppStringArray);
                        this._proxyBaseType = typeof(Il2CppArrayBase<string>);
                    }
                    else
                    {
                        throw new NotSupportedException($"Array element type not supported: {elementType}");
                    }

                    this._fieldType = Marshal.AllocHGlobal(Marshal.SizeOf<MyIl2CppType>());

                    IntPtr elementTypePtr = il2cpp_class_get_type(GetClassPointerForType(elementType));

                    MyIl2CppType* myType = (MyIl2CppType*)this._fieldType;
                    *myType = new MyIl2CppType()
                    {
                        type = Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                        data = elementTypePtr,
                        attrs = 0,
                        mods_byref_pin = 0,
                    };
                }

                protected override IntPtr fieldType => this._fieldType;

                protected override Expression GetMonoToNativeExpression(Expression monoObj)
                {
                    // ((TArray)obj).Pointer
                    var ptr = typeof(Il2CppSystem.Object).GetProperty("Pointer");
                    return Expression.Property(
                        Expression.Convert(monoObj, this._proxyType),
                        ptr);
                }

                protected override Expression GetNativeToMonoExpression(Expression nativePtr)
                {
                    // T = elementType, targetType = T[]
                    // (T[]) new TArray(ptr)
                    var ctor = this._proxyType.GetConstructor(new Type[] { typeof(IntPtr) });
                    return Expression.Convert(
                        Expression.Convert(
                            Expression.New(ctor, nativePtr),
                            this._proxyBaseType),
                        this.field.FieldType);
                }
            }
        }

        #endregion

        #region Enum Injector

        private static readonly Dictionary<Type, IntPtr> enumClasses = new Dictionary<Type, IntPtr>();

        internal static void DebugEnum()
        {
            string typeToName(Il2CppTypeStruct* tptr)
            {
                return Marshal.PtrToStringAnsi(il2cpp_type_get_name((IntPtr)tptr));
            }

            Msg($"Test:\n\n");

            IntPtr ptr = LookupIl2CppEnum(typeof(Space));

            if (ptr == IntPtr.Zero) { return; }
            

            var klass1 = Wrap((Il2CppClass*)ptr);
            debugInfo(klass1);

            //var img = Wrap((Il2CppClass*)Il2CppClassPointerStore<TMPro.FastAction>.NativeClassPtr).Image;

            unsafe void debugType(INativeTypeStruct tp)
            {
                var tp_ptr = (MyIl2CppType*)tp.Pointer;
                Msg($"Type.Type = {tp_ptr->type}");
                Msg($"Type.mbp = {tp_ptr->mods_byref_pin}");
                Msg($"Type.byref = {tp.ByRef}");
                Msg($"Type.pinned = {tp.Pinned}");
                Msg($"Type.attrs = {tp_ptr->attrs}");
                Msg($"Type.data = {tp.Data}");
            }

            void debugInfo(INativeClassStruct klass)
            {
                Msg($"Klass: {Marshal.PtrToStringAnsi(klass.Name)}");
                Msg($"Sizes: AS={klass.ActualSize}, IS={klass.InstanceSize}, NS={klass.NativeSize}");
                Msg($"Flags: {klass.Flags}");
                Msg($"BVA: {typeToName(klass.ByValArg.TypePointer)} TA: {typeToName(klass.ThisArg.TypePointer)}");
                Msg($"CC: {Marshal.PtrToStringAnsi(Wrap(klass.CastClass).Name)}");
                Msg($"Value: {klass.ValueType} Enum: {klass.EnumType}");
                INativeClassStruct elementClass = Wrap(klass.ElementClass);
                Msg($"Elem: {Marshal.PtrToStringAnsi(elementClass.Name)}, elem sizes: AS={elementClass.ActualSize}, IS={elementClass.InstanceSize}, NS={elementClass.NativeSize}");
                if (klass.Parent == null)
                {
                    Msg("No parent");
                }
                else
                {
                    Msg($"BaseClass: {Marshal.PtrToStringAnsi(Wrap(klass.Parent).Name)}");
                }

                Msg($"Method Count: {klass.MethodCount}");
                Msg($"Vtable Count: {klass.VtableCount}");
                Msg($"Methods: {(IntPtr)klass.Methods}");
                Msg($"Underlying: {il2cpp_class_enum_basetype(klass.Pointer)}");
                //Msg($"HasFinalize: {klass.HasFinalize}");
                
                foreach (var prop in klass.GetType().GetProperties())
                {
                    if (prop.PropertyType.IsByRef) { continue; }
                    object val=  prop.GetValue(klass);
                    Msg($"{prop.Name} = {val}");
                }

                Msg("BVA:");
                debugType(klass.ByValArg);
                Msg($"TA:");
                debugType(klass.ThisArg);

                Msg($"\n\n");
            }


        }

        private static IntPtr GetEnumClassPtr(Type enumType)
        {
            if (!enumType.IsEnum) { throw new InvalidOperationException($"{enumType} is not an enum"); }

            if (enumClasses.TryGetValue(enumType, out var result)) { return result; }

            IntPtr existingClass = LookupIl2CppEnum(enumType);
            Msg($"Got ptr {existingClass} for {enumType.Module.Name}, {enumType.Namespace}, {enumType.Name}");
            if (existingClass != IntPtr.Zero)
            {
                enumClasses.Add(enumType, existingClass);
                return existingClass;
            }

            Type underlyingType = enumType.GetEnumUnderlyingType();

            Msg($"Injecting enum: {enumType.Name} ({underlyingType.Name})");

            IntPtr underlyingClassPtr = GetClassPointerForType(underlyingType);
            IntPtr enumBasePtr = Il2CppClassPointerStore<Il2CppSystem.Enum>.NativeClassPtr;
            var elemKlass = Wrap((Il2CppClass*)underlyingClassPtr);
            var enumKlass = Wrap((Il2CppClass*)enumBasePtr);

            Msg($"Underlying class ptr: {underlyingClassPtr}");
            Msg($"Enum class ptr: {enumBasePtr}");
            Msg($"Enum vtable count: {enumKlass.VtableCount}");

            int vtableSize = enumKlass.VtableCount; // should be 23

            var klass = NewClass(23);
            var classPtr = klass.Pointer;
            klass.Image = Image;
            klass.Parent = (Il2CppClass*)enumBasePtr;
            klass.Class = klass.ClassPointer;
            klass.ValueType = true;
            klass.ElementClass = elemKlass.ClassPointer;
            klass.ActualSize = elemKlass.ActualSize;
            klass.InstanceSize = elemKlass.InstanceSize;
            klass.NativeSize = elemKlass.NativeSize;
            klass.Flags = Il2CppClassAttributes.TYPE_ATTRIBUTE_PUBLIC | Il2CppClassAttributes.TYPE_ATTRIBUTE_SEALED | Il2CppClassAttributes.TYPE_ATTRIBUTE_SERIALIZABLE;

            klass.Initialized = klass.InitializedAndNoError = klass.SizeInited = klass.IsVtableInitialized = true;
            klass.HasFinalize = false;
            klass.MethodCount = 0;
            klass.Methods = null;

            klass.Name = Marshal.StringToHGlobalAnsi(enumType.Name);
            if (enumType.Namespace != null)
            {
                klass.Namespace = Marshal.StringToHGlobalAnsi(enumType.Namespace);
            }

            var vtable = (VirtualInvokeData*)klass.VTable;
            var srcVtable = (VirtualInvokeData*)enumKlass.VTable;
            for (int i = 0; i < vtableSize; i++)
            {
                vtable[i] = srcVtable[i];
            }

            klass.ImplementedInterfaces = enumKlass.ImplementedInterfaces;
            klass.InterfaceCount = enumKlass.InterfaceCount;
            klass.InterfaceOffsets = enumKlass.InterfaceOffsets;
            klass.InterfaceOffsetsCount = enumKlass.InterfaceOffsetsCount;

            var metadataToken = Interlocked.Decrement(ref ClassInjector.ourClassOverrideCounter);
            ClassInjector.FakeTokenClasses[metadataToken] = klass.Pointer;

            klass.ByValArg.Type = Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE;
            klass.ByValArg.Data = (IntPtr)metadataToken;

            klass.ThisArg.Type = Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE;
            klass.ThisArg.ByRef = true;
            klass.ThisArg.Data = (IntPtr)metadataToken;

            ClassInjector.AddToClassFromNameDictionary(enumType, klass.Pointer);
            typeof(Il2CppClassPointerStore<>).MakeGenericType(enumType)
                .GetField(nameof(Il2CppClassPointerStore<int>.NativeClassPtr))
                .SetValue(null, klass.Pointer);

            enumClasses.Add(enumType, klass.Pointer);

            Msg($"Injected enum {enumType.Name} at {klass.Pointer}");

            return classPtr;
        }

        private static IntPtr LookupIl2CppEnum(Type enumType)
        {
            var image = GetIl2CppImage(enumType.Module.Name);
            if (image == IntPtr.Zero) { return IntPtr.Zero; }

            var res = il2cpp_class_from_name(image, enumType.Namespace, enumType.Name);

            return res;
        }

        #endregion Enum Injector

        #region Finalize patch

        private static readonly StaticVoidIntPtrDelegate FinalizeDelegate = Finalize;

        private static void Finalize(IntPtr ptr)
        {
            var gcHandle = ClassInjectorBase.GetGcHandlePtrFromIl2CppObject(ptr);
            if (gcHandle == IntPtr.Zero) { return; }
            GCHandle.FromIntPtr(gcHandle).Free();
        }

        #endregion

        #region Utility

        private static Expression LogExpression(string msg, Expression ex)
        {
            var log = ((Action<string>)Msg).Method;
            var toStr = typeof(object).GetMethod("ToString", new Type[] { });
            var concat = ((Func<string, string, string>)string.Concat).Method;

            Expression str = Expression.Call(Expression.Convert(ex, typeof(object)), toStr);

            if (!ex.Type.IsValueType)
            {
                var isNull = Expression.Equal(ex, Expression.Constant(null));
                str = Expression.Condition(
                    isNull,
                    Expression.Constant("null"),
                    str);
            }

            return Expression.Call(log, Expression.Call(concat, Expression.Constant(msg), str));
        }

        private static int AlignTo(int value, int alignment)
        {
            if (alignment > 0)
            {
                int mod = value % alignment;
                if (mod > 0)
                {
                    value += alignment - mod;
                }
            }

            return value;
        }

        private static unsafe int GetTypeSize(INativeTypeStruct type, out uint align)
        {
            // TODO: (FieldLayout.cpp) FieldLayout::GetTypeSizeAndAlignment
            var t = type.Type;
            align = 0;
        handle_enum:
            switch (t)
            {
                case Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN:
                case Il2CppTypeEnum.IL2CPP_TYPE_I1:
                case Il2CppTypeEnum.IL2CPP_TYPE_U1:
                    return 1;
                case Il2CppTypeEnum.IL2CPP_TYPE_I2:
                case Il2CppTypeEnum.IL2CPP_TYPE_U2:
                    return 2;
                case Il2CppTypeEnum.IL2CPP_TYPE_CHAR:
                    return 2; // I think? Il2CppChar being wchar_t probably
                case Il2CppTypeEnum.IL2CPP_TYPE_I4:
                case Il2CppTypeEnum.IL2CPP_TYPE_U4:
                case Il2CppTypeEnum.IL2CPP_TYPE_R4:
                    return 4;
                case Il2CppTypeEnum.IL2CPP_TYPE_I8:
                case Il2CppTypeEnum.IL2CPP_TYPE_U8:
                case Il2CppTypeEnum.IL2CPP_TYPE_R8:
                    return 8;
                case Il2CppTypeEnum.IL2CPP_TYPE_I:
                case Il2CppTypeEnum.IL2CPP_TYPE_U:
                    return 8; // assuming 64-bit, deal with it
                case Il2CppTypeEnum.IL2CPP_TYPE_STRING:
                case Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY:
                case Il2CppTypeEnum.IL2CPP_TYPE_CLASS:
                case Il2CppTypeEnum.IL2CPP_TYPE_OBJECT:
                case Il2CppTypeEnum.IL2CPP_TYPE_ARRAY:
                case Il2CppTypeEnum.IL2CPP_TYPE_FNPTR:
                case Il2CppTypeEnum.IL2CPP_TYPE_PTR:
                    return IntPtr.Size;
                case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                    var klass1 = Wrap((Il2CppClass*)il2cpp_type_get_class_or_element_class(type.Pointer));
                    if (type.Type == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE && klass1.EnumType)
                    {
                        var chrs = (byte*)klass1.Name;
                        Msg($"Enum Basetype debug: {klass1.Pointer}, {(IntPtr)chrs}");
                        Msg($"Chr: {(char)(*chrs)}");
                        Msg($"elemt={(IntPtr)klass1.ElementClass}");
                        Msg($"elemtname={Marshal.PtrToStringAnsi(il2cpp_type_get_name((IntPtr)klass1.ElementClass))}");
                        t = ((MyIl2CppType*)il2cpp_class_enum_basetype(klass1.Pointer))->type;
                        goto handle_enum;
                    }

                    return il2cpp_class_value_size(il2cpp_class_from_il2cpp_type(type.Pointer), ref align);
                case Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST:
                default:
                    throw new NotImplementedException();
            }
        }

        private static IntPtr GetClassPointerForType<T>()
        {
            if (typeof(T) == typeof(void)) { return Il2CppClassPointerStore<Il2CppSystem.Void>.NativeClassPtr; }
            return Il2CppClassPointerStore<T>.NativeClassPtr;
        }

        private static IntPtr GetClassPointerForType(Type type, bool bypassEnums = false)
        {
            if (type == typeof(void)) { return Il2CppClassPointerStore<Il2CppSystem.Void>.NativeClassPtr; }

            if (type.IsEnum && !bypassEnums)
            {
                return GetEnumClassPtr(type);
            }

            return (IntPtr)typeof(Il2CppClassPointerStore<>).MakeGenericType(type).GetField("NativeClassPtr").GetValue(null);
        }

        internal static void SetClassPointerForType(Type type, IntPtr value)
        {
            typeof(Il2CppClassPointerStore<>).MakeGenericType(type)
                .GetField(nameof(Il2CppClassPointerStore<int>.NativeClassPtr)).SetValue(null, value);
        }

        #endregion Utility

        #region IL2CPP Structs (hacky and version-specific)

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct MyIl2CppFieldInfo // Il2CppFieldInfo_24_1
        {
            public IntPtr name; // const char*
            public Il2CppTypeStruct* type; // const
            public Il2CppClass* parent; // non-const?
            public int offset; // If offset is -1, then it's thread static
            public uint token;
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct MyIl2CppClass // Il2CppClass_24_1_B
        {
            // The following fields are always valid for a Il2CppClass structure
            public Il2CppImage* image; // const
            public IntPtr gc_desc;
            public IntPtr name; // const char*
            public IntPtr namespaze; // const char*
            public MyIl2CppType byval_arg; // not const, no ptr
            public MyIl2CppType this_arg; // not const, no ptr
            public Il2CppClass* element_class; // not const
            public Il2CppClass* castClass; // not const
            public Il2CppClass* declaringType; // not const
            public Il2CppClass* parent; // not const
            public /*Il2CppGenericClass**/ IntPtr generic_class;

            public /*Il2CppTypeDefinition**/
                IntPtr typeDefinition; // const; non-NULL for Il2CppClass's constructed from type defintions

            public /*Il2CppInteropData**/ IntPtr interopData; // const

            public Il2CppClass* klass; // not const; hack to pretend we are a MonoVTable. Points to ourself
            // End always valid fields

            // The following fields need initialized before access. This can be done per field or as an aggregate via a call to Class::Init
            public MyIl2CppFieldInfo* fields; // Initialized in SetupFields
            public Il2CppEventInfo* events; // const; Initialized in SetupEvents
            public Il2CppPropertyInfo* properties; // const; Initialized in SetupProperties
            public Il2CppMethodInfo** methods; // const; Initialized in SetupMethods
            public Il2CppClass** nestedTypes; // not const; Initialized in SetupNestedTypes
            public Il2CppClass** implementedInterfaces; // not const; Initialized in SetupInterfaces
            public Il2CppRuntimeInterfaceOffsetPair* interfaceOffsets; // not const; Initialized in Init
            public IntPtr static_fields; // not const; Initialized in Init

            public /*Il2CppRGCTXData**/ IntPtr rgctx_data; // const; Initialized in Init

            // used for fast parent checks
            public Il2CppClass** typeHierarchy; // not const; Initialized in SetupTypeHierachy
            // End initialization required fields

            public uint initializationExceptionGCHandle;

            public uint cctor_started;

            public uint cctor_finished;

            /*ALIGN_TYPE(8)*/
            private ulong cctor_thread;

            // Remaining fields are always valid except where noted
            public /*GenericContainerIndex*/ int genericContainerIndex;
            public uint instance_size;
            public uint actualSize;
            public uint element_size;
            public int native_size;
            public uint static_fields_size;
            public uint thread_static_fields_size;
            public int thread_static_fields_offset;
            public Il2CppClassAttributes flags;
            public uint token;

            public ushort method_count; // lazily calculated for arrays, i.e. when rank > 0
            public ushort property_count;
            public ushort field_count;
            public ushort event_count;
            public ushort nested_type_count;
            public ushort vtable_count; // lazily calculated for arrays, i.e. when rank > 0
            public ushort interfaces_count;
            public ushort interface_offsets_count; // lazily calculated for arrays, i.e. when rank > 0

            public byte typeHierarchyDepth; // Initialized in SetupTypeHierachy
            public byte genericRecursionDepth;
            public byte rank;
            public byte minimumAlignment; // Alignment of this type
            public byte naturalAligment; // Alignment of this type without accounting for packing
            public byte packingSize;

            // this is critical for performance of Class::InitFromCodegen. Equals to initialized && !has_initialization_error at all times.
            // Use Class::UpdateInitializedAndNoError to update
            public byte bitfield_1;
            /*uint8_t initialized_and_no_error : 1;
    
            uint8_t valuetype : 1;
            uint8_t initialized : 1;
            uint8_t enumtype : 1;
            uint8_t is_generic : 1;
            uint8_t has_references : 1;
            uint8_t init_pending : 1;
            uint8_t size_inited : 1;*/

            public byte bitfield_2;
            /*uint8_t has_finalize : 1;
            uint8_t has_cctor : 1;
            uint8_t is_blittable : 1;
            uint8_t is_import_or_windows_runtime : 1;
            uint8_t is_vtable_initialized : 1;
            uint8_t has_initialization_error : 1;*/

            //VirtualInvokeData vtable[IL2CPP_ZERO_LEN_ARRAY];
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MyIl2CppType
        {
            /*union
            {
                // We have this dummy field first because pre C99 compilers (MSVC) can only initializer the first value in a union.
                void* dummy;
                TypeDefinitionIndex klassIndex; /* for VALUETYPE and CLASS #1#
                const Il2CppType *type;   /* for PTR and SZARRAY #1#
                Il2CppArrayType *array; /* for ARRAY #1#
                //MonoMethodSignature *method;
                GenericParameterIndex genericParameterIndex; /* for VAR and MVAR #1#
                Il2CppGenericClass *generic_class; /* for GENERICINST #1#
            } data;*/
            public IntPtr data;

            public ushort attrs;
            public Il2CppTypeEnum type;
            public byte mods_byref_pin;
            /*unsigned int attrs    : 16; /* param attributes or field flags #1#
            Il2CppTypeEnum type     : 8;
            unsigned int num_mods : 6;  /* max 64 modifiers follow at the end #1#
            unsigned int byref    : 1;
            unsigned int pinned   : 1;  /* valid when included in a local var signature #1#*/
            //MonoCustomMod modifiers [MONO_ZERO_LEN_ARRAY]; /* this may grow */
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct MyIl2CppParameterInfo
        {
            public IntPtr name; // const char*
            public int position;
            public uint token;
            public Il2CppTypeStruct* parameter_type; // const
        }

        #endregion IL2CPP Structs (hacky and version-specific)
    }
}
