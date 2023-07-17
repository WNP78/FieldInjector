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

        #region Injection Entrypoint and Dependency processing

        private static bool IsTypeInjected(Type t)
        {
            return GetClassPointerForType(t) != IntPtr.Zero;
        }

        public static void InjectNew<T>(int debugLevel = 5)
        {
            InjectNew(debugLevel, typeof(T));
        }

        public static void InjectNew(int debugLevel = 5, params Type[] t)
        {
            LogLevel = debugLevel;

            var typesToInject = new HashSet<Type>(t);

            Type ProcessFieldType(Type ft)
            {
                if (ft.IsPrimitive) return null;

                if (typesToInject.Contains(ft)) return null;

                if (ft.IsArray) return ProcessFieldType(ft.GetElementType());

                if (ft.IsGenericType)
                {
                    var td = ft.GetGenericTypeDefinition();
                    if (td == typeof(List<>) || td == typeof(Nullable)) return ProcessFieldType(td.GetGenericArguments()[0]);
                }

                if (IsTypeInjected(ft)) return null;

                return ft;
            }

            void CollectDependencies(Type ct)
            {
                if (serialisationCache.ContainsKey(ct)) { return; }
                if (typesToInject.Contains(ct)) { return; }
                typesToInject.Add(ct);

                if (ct.BaseType != null) CollectDependencies(ct.BaseType);

                foreach (var type in ct
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(field => !field.IsNotSerialized)
                    .Select((field) => ProcessFieldType(field.FieldType))
                    .Where(r => r != null))
                {
                    typesToInject.Add(type);
                    CollectDependencies(type);
                }
            }

            foreach (var type in t) CollectDependencies(type);

            InjectBatch(typesToInject.ToArray());
        }

        private struct InjectionProgress
        {
            public bool Failed;
            public MyIl2CppClass* ClassPtr;
            public SerialisedField[] Result;
        }

        private static Dictionary<Type, InjectionProgress> injection = new Dictionary<Type, InjectionProgress>();

        private static void InjectBatch(Type[] typesToInject)
        {
            injection.Clear();
            int n = typesToInject.Length;

            Log($"Serialising a batch of {n} types:", 1);

            // reorder the list to ensure that base types are processed first
            for (int i = 0; i < n; i++)
            {
                int index = Array.IndexOf(typesToInject, typesToInject[i].BaseType);
                if (index != -1 && index < i)
                {
                    // swap
                    (typesToInject[i], typesToInject[index]) = (typesToInject[index], typesToInject[i]);
                }
            }

            if (LogLevel >= 2)
            {
                foreach (var tti in typesToInject)
                {
                    Msg($"  {tti.FullName}");
                }
            }

            // Inject class and get a reference to it.
            foreach (var t in typesToInject)
            {
                try
                {
                    ClassInjector.RegisterTypeInIl2CppWithInterfaces(t, false, typeof(ISerializationCallbackReceiver));
                    var klassPtr = (MyIl2CppClass*)GetClassPointerForType(t, bypassEnums: true);
                    var klass = Wrap((Il2CppClass*)klassPtr);

                    // fix for unhollower not setting namespace field if there's no namespace
                    if (klassPtr->namespaze == IntPtr.Zero)
                    {
                        klassPtr->namespaze = Marshal.StringToHGlobalAnsi(string.Empty);
                    }

                    // fix Finalizer so it doesn't crash
                    FixFinaliser(klass);

                    injection[t] = new InjectionProgress()
                    {
                        Failed = false,
                        ClassPtr = klassPtr,
                    };
                }
                catch (Exception ex)
                {
                    Log($"Failed to do initial injection on type {t.Name}: {ex}", 0);
                    injection[t] = new InjectionProgress()
                    {
                        Failed = true,
                    };
                }
            }

            // Do field injection.
            foreach (var t in typesToInject)
            {
                var inj = injection[t];

                try
                {
                    var klassPtr = inj.ClassPtr;
                    var klass = Wrap((Il2CppClass*)klassPtr);
                    var baseKlassPtr = (MyIl2CppClass*)GetClassPointerForType(t.BaseType);

                    // Select serialisable fields, make serialiser classes.
                    var bflags = BindingFlags.Instance | BindingFlags.DeclaredOnly;
                    SerialisedField[] injectedFields =
                        t.GetFields(bflags | BindingFlags.Public)
                        .Where(field => !field.IsNotSerialized)
                        .Select(field => TrySerialise(field))
                        .Where(field => field != null)
                        .ToArray();

                    SerialisedField[] baseFields = null;

                    if (injection.TryGetValue(t.BaseType, out var baseInj))
                    {
                        baseFields = baseInj.Result;
                    }
                    else serialisationCache.TryGetValue(t.BaseType, out baseFields);

                    int numBaseFields = baseFields?.Length ?? 0;
                    SerialisedField[] allFields = new SerialisedField[numBaseFields + injectedFields.Length];
                    baseFields?.CopyTo(allFields, 0);
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

                    if (LogLevel >= 3)
                    {
                        expressions = expressions.Prepend(LogExpression($"Deserialise {t}:", nativePtr));
                        expressions = expressions.Append(LogExpression("Deserialise complete: ", nativePtr));
                    }

                    var deserialiseExpression = Expression.Block(
                        new ParameterExpression[] { managedObj, fieldPtr },
                        expressions);

                    Log($"Generated deserialiser method:\n{string.Join("\n", deserialiseExpression.Expressions)}", 3);

                    var deserialiseMethod = Expression.Lambda<StaticVoidIntPtrDelegate>(deserialiseExpression, nativePtr);
                    EmitSerialiserMethod(deserialiseMethod, t, klass, nameof(ISerializationCallbackReceiver.OnAfterDeserialize), iface, interfaceOffset, LogLevel);

                    // Now the serialiser
                    expressions = setupExpressions.Concat(
                        allFields
                        .SelectMany(field => field.GetSerialiseExpression(managedObj, nativePtr)));

                    if (LogLevel >= 3)
                    {
                        expressions = expressions.Prepend(LogExpression($"Serialise {t}:", nativePtr));
                        expressions = expressions.Append(LogExpression("Serialise complete: ", nativePtr));
                    }

                    var serialiseExpression = Expression.Block(
                        new ParameterExpression[] { managedObj },
                        expressions);

                    Log($"Generated serialiser method: \n{string.Join("\n", serialiseExpression.Expressions)}", 3);

                    var serialiseMethod = Expression.Lambda<StaticVoidIntPtrDelegate>(serialiseExpression, nativePtr);
                    EmitSerialiserMethod(serialiseMethod, t, klass, nameof(ISerializationCallbackReceiver.OnBeforeSerialize), iface, interfaceOffset, LogLevel);

                    serialisationCache[t] = allFields;

                    Log($"Completed serialisation injection for type {t}", 2);
                }
                catch (Exception ex)
                {
                    Log($"Failed to do field injection on type {t.Name}: {ex}", 0);
                    inj.Failed = true;
                }

                injection[t] = inj;
            }
        }

        #endregion

        #region Main Serialiser

        private static readonly Dictionary<Type, SerialisedField[]> serialisationCache = new Dictionary<Type, SerialisedField[]>();

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

        internal static int LogLevel = 0;

        internal static void Log(string message, int level)
        {
            if (LogLevel >= level)
            {
                Msg(message);
            }
        }

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
    }
}
