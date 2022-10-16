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

using ILCollections = Il2CppSystem.Collections.Generic;

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

        public static void Inject<T>(int debugLevel = 0) where T : MonoBehaviour
        {
            InjectRecursive(typeof(T), debugLevel);
        }

        public static void Inject(Type t, int debugLevel = 0)
        {
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
                Log($"[{offset}] Converting field {field.ManagedField} as {field}", 2);

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
            klassPtr->gc_desc = IntPtr.Zero;

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
            MethodInfo getGCHandleMethod = ((Func<IntPtr, IntPtr>)ClassInjectorBase.GetGcHandlePtrFromIl2CppObject).Method;

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
                expressions = expressions.Prepend(LogExpression($"Deserialise {t}:", nativePtr));
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
                expressions = expressions.Prepend(LogExpression($"Serialise {t}:", nativePtr));
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
                else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    return new ListField(field);
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
                    align = sizeof(IntPtr);
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
                    align = sizeof(IntPtr);
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
                    if (base.targetType.IsEnum)
                    {
                        this._serialisedType = base.targetType.GetEnumUnderlyingType();
                    }
                    else
                    {
                        this._serialisedType = base.targetType;
                    }

                    this.fieldClass = GetClassPointerForType(this.targetType);
                    this._fieldType = il2cpp_class_get_type(this.fieldClass);
                    this.tempPtr = Expression.Parameter(typeof(IntPtr), "tempStorage");
                }

                private IntPtr fieldClass;

                private IntPtr _fieldType;

                private ParameterExpression tempPtr;

                private Type _serialisedType;

                protected override IntPtr fieldType => this._fieldType;

                protected override Type targetType => this._serialisedType;

                public unsafe override int GetFieldSize(out int align)
                {
                    align = 0;
                    return (int)(Wrap((Il2CppClass*)this.fieldClass).ActualSize - Marshal.SizeOf<Il2CppObject>());
                }

                public unsafe static T GetValue<T>(IntPtr obj, IntPtr field) where T : unmanaged
                {
                    MyIl2CppFieldInfo* fieldInfo = (MyIl2CppFieldInfo*)field;
                    void* dest = (char*)obj + fieldInfo->offset;

                    return *(T*)dest;
                }

                protected override Expression GetMonoToNativeExpression(Expression monoValue)
                {
                    throw new NotImplementedException();
                }

                private unsafe static void SetValue<T>(T value, IntPtr obj, IntPtr field) where T : unmanaged
                {
                    MyIl2CppFieldInfo* fieldInfo = (MyIl2CppFieldInfo*)field;

                    void* dest = (byte*)obj + fieldInfo->offset;
                    *(T*)dest = value;
                }

                public override IEnumerable<Expression> GetSerialiseExpression(Expression monoObj, Expression nativePtr)
                {
                    var freeMethod = new Action<IntPtr>(Marshal.FreeHGlobal).Method;

                    if (this.NativeField == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("Something went very wrong");
                    }

                    Expression monoValue = Expression.Field(monoObj, this.field);

                    MethodInfo setValue = ((Action<int, IntPtr, IntPtr>)SetValue).Method
                        .GetGenericMethodDefinition()
                        .MakeGenericMethod(monoValue.Type);

                    yield return Expression.Call(setValue, monoValue, nativePtr, Expression.Constant(this.NativeField));
                    //yield break;
                }

                protected override Expression GetNativeToMonoExpression(Expression nativePtr)
                {
                    // for normal struct types
                    // new Il2CppSystem.Object(ptr).Unbox<T>()
                    var ctor = typeof(Il2CppSystem.Object).GetConstructor(new Type[] { typeof(IntPtr) });
                    var unbox = typeof(Il2CppSystem.Object).GetMethod("Unbox")
                                    .MakeGenericMethod(this.targetType);

                    Expression res = Expression.Call(Expression.New(ctor, nativePtr), unbox);

                    if (res.Type != this.field.FieldType)
                    {
                        res = Expression.Convert(res, this.field.FieldType);
                    }

                    return res;
                }
            }

            private class ArrayField : SerialisedField
            {
                protected IntPtr _fieldType;

                protected Type _proxyType;

                public override int GetFieldSize(out int align)
                {
                    align = sizeof(IntPtr);
                    return sizeof(IntPtr);
                }

                protected ArrayField (Type elementType, FieldInfo field) : base(field)
                {
                    if (elementType.IsEnum)
                    {
                        elementType = elementType.GetEnumUnderlyingType();
                    }

                    this._proxyType = typeof(ILCollections.List<>).MakeGenericType(elementType);
                    var classPtr = GetClassPointerForType(this._proxyType);
                    this._fieldType = il2cpp_class_get_type(classPtr);
                }

                public ArrayField(FieldInfo field) : this(field.FieldType.GetElementType(), field) { }

                protected override IntPtr fieldType => this._fieldType;

                protected override Expression GetMonoToNativeExpression(Expression monoObj)
                {
                    return ConvertArrayToIl2CppList(monoObj, this._proxyType);
                }

                protected override Expression GetNativeToMonoExpression(Expression nativePtr)
                {
                    var ctor = this._proxyType.GetConstructor(new Type[] { typeof(IntPtr) });
                    Expression cppList = Expression.New(ctor, nativePtr);

                    return ConvertListToMono(cppList, this.field.FieldType);
                }

                public static Expression ConvertListToMono(Expression cppList, Type monoType)
                {
                    if (!monoType.IsArray) { throw new ArgumentException("monoType is not an array!"); }
                    Type monoElementType = monoType.GetElementType();

                    if (monoElementType.IsValueType)
                    {
                        Type cppElementType = cppList.Type.GetGenericArguments()[0];

                        MethodInfo convertStructList = ((Func<ILCollections.List<int>, int[]>)ConvertStructList<int, int>)
                            .Method.GetGenericMethodDefinition().MakeGenericMethod(monoElementType, cppElementType);

                        return Expression.Call(convertStructList, cppList);
                    }
                    else
                    {
                        MethodInfo convertGeneralList = ((Func<ILCollections.List<int>, int[]>)ConvertGeneralList<int>)
                            .Method.GetGenericMethodDefinition().MakeGenericMethod(monoElementType);

                        return Expression.Call(convertGeneralList, cppList);
                    }
                }

                public static Expression ConvertArrayToIl2CppList(Expression monoArray, Type cppType)
                {
                    Type cppElementType = cppType.GetGenericArguments()[0];
                    Type monoElementType = monoArray.Type.GetElementType();

                    Expression cppList;

                    if (cppElementType.IsValueType)
                    {
                        MethodInfo convertStructArray = ((Func<int[], ILCollections.List<int>>)ConvertStructArray<int, int>)
                            .Method.GetGenericMethodDefinition().MakeGenericMethod(monoElementType, cppElementType);

                        cppList = Expression.Call(convertStructArray, monoArray);
                    }
                    else
                    {
                        MethodInfo convertGeneralArray = ((Func<int[], ILCollections.List<int>>)ConvertGeneralArray<int>)
                            .Method.GetGenericMethodDefinition().MakeGenericMethod(monoElementType);

                        cppList = Expression.Call(convertGeneralArray, monoArray);
                    }

                    var ptr = typeof(Il2CppSystem.Object).GetProperty("Pointer");
                    return Expression.Property(cppList, ptr);
                }

                private static TMono[] ConvertStructList<TMono, TCpp>(ILCollections.List<TCpp> cppList) 
                    where TMono : unmanaged
                    where TCpp : unmanaged
                {
                    int size = sizeof(TMono);
                    if (size != sizeof(TCpp)) { throw new ArgumentException("Size mismatch in array copy."); }

                    TMono[] res = new TMono[cppList.Count];
                    fixed (TMono* resPtr = res)
                    {
                        for (int i = 0; i < res.Length; i++)
                        {
                            *(TCpp*)(resPtr + i) = cppList[i];
                        }
                    }

                    return res;
                }

                private static T[] ConvertGeneralList<T>(ILCollections.List<T> cppList)
                {
                    T[] res = new T[cppList.Count];
                    for (int i = 0;i < res.Length; i++)
                    {
                        res[i] = cppList[i];
                    }

                    return res;
                }

                public static ILCollections.List<TCpp> ConvertStructArray<TMono, TCpp>(TMono[] monoArray)
                    where TMono : unmanaged
                    where TCpp : unmanaged
                {
                    var res = new ILCollections.List<TCpp>(monoArray.Length);
                    fixed (TMono* monoPtr = monoArray)
                    {
                        for (int i = 0; i < monoArray.Length; i++)
                        {
                            TMono* ptr = &monoPtr[i];
                            res.Add(*(TCpp*)ptr);
                        }
                    }

                    return res;
                }

                public static ILCollections.List<T> ConvertGeneralArray<T>(T[] monoArray)
                {
                    var res = new ILCollections.List<T>(monoArray.Length);

                    for (int i = 0; i < monoArray.Length; i++)
                    {
                        res.Add(monoArray[i]);
                    }

                    return res;
                }
            }

            private class ListField : ArrayField
            {
                public ListField(FieldInfo field) : base(field.FieldType.GetGenericArguments()[0], field)
                {
                }

                protected override Expression GetMonoToNativeExpression(Expression monoObj)
                {
                    return Expression.Property(ListToCpp(monoObj, this._proxyType), "Pointer");
                }

                protected override Expression GetNativeToMonoExpression(Expression nativePtr)
                {
                    var ctor = this._proxyType.GetConstructor(new Type[] { typeof(IntPtr) });
                    Expression cppList = Expression.New(ctor, nativePtr);
                    return ListToManaged(cppList, this.field.FieldType);
                }

                public static Expression ListToCpp(Expression list, Type cppType)
                {
                    Type monoElementType = list.Type.GetGenericArguments()[0];

                    MethodInfo converter;
                    if (monoElementType.IsValueType)
                    {
                        Type cppElementType = cppType.GetGenericArguments()[0];
                        converter = ((Func<List<int>, ILCollections.List<int>>)ListToCppStruct<int, int>)
                            .Method.GetGenericMethodDefinition().MakeGenericMethod(monoElementType, cppElementType);
                    }
                    else
                    {
                        converter = ((Func<List<int>, ILCollections.List<int>>)ListToCppRef<int>)
                            .Method.GetGenericMethodDefinition().MakeGenericMethod(monoElementType);
                    }

                    return Expression.Call(converter, list);
                }

                private static ILCollections.List<TCpp> ListToCppStruct<TMono, TCpp>(List<TMono> list)
                    where TMono : unmanaged
                    where TCpp : unmanaged
                {
                    ILCollections.List<TCpp> res = new ILCollections.List<TCpp>(list.Count);

                    TMono monoVal = default;
                    TCpp* ptr = (TCpp*)&monoVal;
                    for (int i = 0; i < list.Count; i++)
                    {
                        monoVal = list[i];
                        res.Add(*ptr);
                    }

                    return res;
                }

                private static ILCollections.List<T> ListToCppRef<T>(List<T> list)
                {
                    ILCollections.List<T> res = new ILCollections.List<T>(list.Count);
                    for (int i = 0; i < list.Count; i++)
                    {
                        res.Add(list[i]);
                    }

                    return res;
                }

                public static Expression ListToManaged(Expression list, Type fieldType)
                {
                    Type monoElementType = fieldType.GetGenericArguments()[0];
                    Type cppElementType = list.Type.GetGenericArguments()[0];
                    MethodInfo converter;

                    if (monoElementType.IsValueType)
                    {
                        converter = ((Func<ILCollections.List<int>, List<int>>)ListToManagedStruct<int, int>)
                            .Method.GetGenericMethodDefinition().MakeGenericMethod(monoElementType, cppElementType);
                    }
                    else
                    {
                        if (monoElementType != cppElementType)
                        {
                            throw new ArgumentException($"Mono type != Cpp type for element in ref list!");
                        }

                        converter = converter = ((Func<ILCollections.List<int>, List<int>>)ListToManagedRef<int>)
                            .Method.GetGenericMethodDefinition().MakeGenericMethod(monoElementType);
                    }

                    return Expression.Call(converter, list);
                }

                private static List<TMono> ListToManagedStruct<TMono, TCpp>(ILCollections.List<TCpp> list)
                    where TMono : unmanaged
                    where TCpp : unmanaged
                {
                    var res = new List<TMono>(list.Count);

                    TCpp cppVal = default;
                    TMono* ptr = (TMono*)&cppVal;

                    for (int i = 0; i < list.Count; i++)
                    {
                        cppVal = list[i];
                        res.Add(*ptr);
                    }

                    return res;
                }

                private static List<T> ListToManagedRef<T>(ILCollections.List<T> list)
                {
                    int count = list.Count;
                    var res = new List<T>(count);

                    for (int i = 0; i < count; i++)
                    {
                        res.Add(list[i]);
                    }

                    return res;
                }
            }
        }

        #endregion

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
                    var klass1 = Wrap((Il2CppClass*)il2cpp_class_from_il2cpp_type(type.Pointer));
                    if (type.Type == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE && klass1.EnumType)
                    {
                        var chrs = (byte*)klass1.Name;
                        Msg($"Enum Basetype debug: {klass1.Pointer}, {(IntPtr)chrs}, {Marshal.StringToHGlobalAnsi("test")}");
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
                throw new NotSupportedException("Trying to get pointer for enum type");
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
        internal unsafe struct MyIl2CppFieldInfo
        {
            public IntPtr name; // const char*
            public Il2CppTypeStruct* type; // const
            public Il2CppClass* parent; // non-const?
            public int offset; // If offset is -1, then it's thread static
            public uint token;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct MyIl2CppClass
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

            public IntPtr unity_user_data;

            public uint initializationExceptionGCHandle;

            public uint cctor_started;

            public uint cctor_finished;

            /*ALIGN_TYPE(8)*/
            private ulong cctor_thread;

            // Remaining fields are always valid except where noted
            public /*GenericContainerIndex*/ IntPtr genericContainerIndex;
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
        internal struct MyIl2CppType
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
        internal unsafe struct MyIl2CppParameterInfo
        {
            public IntPtr name; // const char*
            public int position;
            public uint token;
            public Il2CppTypeStruct* parameter_type; // const
        }

        #endregion IL2CPP Structs (hacky and version-specific)
    }
}
