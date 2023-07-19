using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using UnhollowerBaseLib;
using UnhollowerBaseLib.Runtime;
using UnhollowerBaseLib.Runtime.VersionSpecific.Class;
using UnhollowerBaseLib.Runtime.VersionSpecific.MethodInfo;
using UnhollowerRuntimeLib;
using UnityEngine;
using static FieldInjector.Util;
using static MelonLoader.MelonLogger;
using static UnhollowerBaseLib.Runtime.UnityVersionHandler;

namespace FieldInjector
{
    public static unsafe class SerialisationHandler
    {
        #region Simple Action<IntPtr> Invoker

        private static readonly IntPtr invokerPtr;

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

        #endregion Simple Action<IntPtr> Invoker

        #region Injection Entrypoint and Dependency processing

        private static bool IsTypeInjected(Type t)
        {
            return GetClassPointerForType(t) != IntPtr.Zero;
        }

        public static void Inject<T>(int debugLevel = 5)
        {
            Inject(debugLevel, typeof(T));
        }

        public static void Inject(int debugLevel = 5, params Type[] t)
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

        private static readonly Dictionary<Type, InjectionProgress> injection = new Dictionary<Type, InjectionProgress>();

        private static void InjectBatch(Type[] typesToInject)
        {
            injection.Clear();
            int n = typesToInject.Length;

            Log($"Serialising a batch of {n} types:", 1);

            //while (!Debugger.IsAttached) { Thread.Sleep(10); }
            Debugger.Break();

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
                    Log($"Initial injection for {t.Name}", 2);
                    ClassInjector.RegisterTypeInIl2CppWithInterfaces(t, false, typeof(ISerializationCallbackReceiver));

                    Log($"Get ptr for {t.Name}", 3);
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
                    Log($"Start field injection for {t.Name}", 3);
                    var klassPtr = inj.ClassPtr;
                    var klass = Wrap((Il2CppClass*)klassPtr);
                    var baseKlassPtr = (MyIl2CppClass*)GetClassPointerForType(t.BaseType);

                    Log($"Initial field serialisation for {t.Name}", 4);
                    // Select serialisable fields, make serialiser classes.
                    var bflags = BindingFlags.Instance | BindingFlags.DeclaredOnly;
                    SerialisedField[] injectedFields =
                        t.GetFields(bflags | BindingFlags.Public)
                        .Where(field => !field.IsNotSerialized)
                        .Select(field => TrySerialise(field))
                        .Where(field => field != null)
                        .ToArray();

                    Log($"Finding base fields for {t.Name}", 3);

                    SerialisedField[] baseFields = null;

                    if (injection.TryGetValue(t.BaseType, out var baseInj))
                    {
                        baseFields = baseInj.Result;
                    }
                    else serialisationCache.TryGetValue(t.BaseType, out baseFields);

                    Log($"Compiling fields for {t.Name}", 4);
                    int numBaseFields = baseFields?.Length ?? 0;
                    SerialisedField[] allFields = new SerialisedField[numBaseFields + injectedFields.Length];
                    baseFields?.CopyTo(allFields, 0);
                    injectedFields.CopyTo(allFields, numBaseFields);

                    // Unhollower uses the last IntPtr of a class for a GCHandle of the managed object - we inject fields before this
                    int offset = (int)klass.ActualSize - IntPtr.Size;

                    Log($"Allocating field info for {t.Name}", 4);
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

        #endregion Injection Entrypoint and Dependency processing

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
                var res = SerialisedField.InferFromField(field);

                Log($"Created field of type {res.GetType().Name} for field {field.FieldType.Name} {field.Name}", 5);

                return res;
            }
            catch (Exception ex)
            {
                Warning($"Not serialising field {field} due to error: {ex.Message}\n{ex.StackTrace}");
            }

            return null;
        }

        #endregion Main Serialiser

        #region Finalize patch

        private static readonly StaticVoidIntPtrDelegate FinalizeDelegate = Finalize;

        private static void Finalize(IntPtr ptr)
        {
            var gcHandle = ClassInjectorBase.GetGcHandlePtrFromIl2CppObject(ptr);
            if (gcHandle == IntPtr.Zero) { return; }
            GCHandle.FromIntPtr(gcHandle).Free();
        }

        #endregion Finalize patch
    }
}