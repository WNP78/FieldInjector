using MelonLoader;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
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

        public static void Inject<T>(int debugLevel = 0)
        {
            Inject(debugLevel, typeof(T));
        }

        public static void Inject(Type type, int debugLevel = 0)
        {
            Inject(debugLevel, type);
        }

        public static void Inject(int debugLevel = 0, params Type[] t)
        {
            LogLevel = debugLevel;

            var typesToInject = new HashSet<Type>();

            Type ProcessType(Type ft)
            {
                if (ft.IsEnum) ft = ft.GetEnumUnderlyingType();

                if (ft.IsPrimitive) return null;

                if (typesToInject.Contains(ft)) return null;

                if (ft.IsArray) return ProcessType(ft.GetElementType());

                if (ft.IsGenericType)
                {
                    var td = ft.GetGenericTypeDefinition();
                    if (td == typeof(List<>) || td == typeof(Nullable)) return ProcessType(ft.GetGenericArguments()[0]);
                }

                if (IsTypeInjected(ft)) return null;

                return ft;
            }

            void CollectDependencies(Type ct)
            {
                if (ct == null) return;
                if (serialisationCache.ContainsKey(ct)) { return; }
                if (typesToInject.Contains(ct)) { return; }
                typesToInject.Add(ct);

                if (!ct.IsValueType && ct.BaseType != null) CollectDependencies(ProcessType(ct.BaseType));

                foreach (var type in ct
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(field => !field.IsNotSerialized)
                    .Select((field) => ProcessType(field.FieldType))
                    .Where(r => r != null))
                {
                    CollectDependencies(type);
                }
            }

            foreach (var type in t) CollectDependencies(ProcessType(type));

            int numClasses = 0, numStructs = 0;
            foreach (var tti in typesToInject)
            {
                if (tti.IsValueType) { numStructs++; }
                else {  numClasses++; }
            }

            Type[] classes = new Type[numClasses];
            Type[] structs = new Type[numStructs];
            int icl = 0, ist = 0;
            foreach (var type in typesToInject)
            {
                if (!type.IsValueType) { classes[icl++] = type; }
                else { structs[ist++] = type; }
            }

            InjectBatch(classes, structs);
        }

        private struct InjectionProgress
        {
            public bool Failed;
            public MyIl2CppClass* ClassPtr;
            public SerialisedField[] Result;
        }

        private static readonly Dictionary<Type, InjectionProgress> injection = new Dictionary<Type, InjectionProgress>();

        #endregion Injection Entrypoint and Dependency processing

        #region Main Serialiser

        // unhollower assigns fake tokens descending by starting at -2 (il2cpp only uses positive tokens, so the all the negative numbers can be used by us safely
        // we used to use a publiciser to allocate them with unhollower's ones, so we decremented their counter
        // however to avoid hooking into unhollower's gubbins too much we can just start at the other end of the negative numbers
        // and if someone injects enough types for them to meet then the universe has literally exploded
        private static long myTokenOverride = long.MinValue + 1;

        private static ConcurrentDictionary<long, IntPtr> _fakeTokenClasses;

        private static IntPtr _fakeImage;
        private static IntPtr _fakeAssembly;
        private static bool _initImage;
        internal static Dictionary<Type, IntPtr> _injectedStructs = new Dictionary<Type, IntPtr>();

        private static Action<Type, IntPtr> AddToClassFromNameDictionary = 
            (Action<Type, IntPtr>)Delegate.CreateDelegate(typeof(Action<Type, IntPtr>), typeof(ClassInjector).GetMethod(
                "AddToClassFromNameDictionary", 
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, Type.DefaultBinder,
                new Type[] { typeof(Type), typeof(IntPtr) }, null));

        private static void InitImage()
        {
            if (_initImage) return; 

            // largely does a similar thing to unhollower
            var img = NewImage();
            var asm = NewAssembly();
            var name = Marshal.StringToHGlobalAnsi("InjectedStructs");

            asm.Name = name;
            img.Assembly = asm.AssemblyPointer;
            img.Dynamic = 1;
            img.Name = name;

            if (img.HasNameNoExt)
            {
                img.NameNoExt = img.Name;
            }

            _fakeImage = img.Pointer;
            _fakeAssembly = asm.Pointer;
            _initImage = true;
        }

        private static IntPtr FakeImage
        {
            get
            {
                if (!_initImage) InitImage();
                return _fakeImage;
            }
        }

        private static IntPtr FakeAssembly
        {
            get
            {
                if (! _initImage) InitImage();
                return _fakeAssembly;
            }
        }

        private static ConcurrentDictionary<long, IntPtr> FakeTokenClasses
        {
            get
            {
                if (_fakeTokenClasses == null)
                {
                    _fakeTokenClasses = typeof(ClassInjector).GetField("FakeTokenClasses", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as ConcurrentDictionary<long, IntPtr>;
                }

                if (_fakeTokenClasses == null)
                {
                    throw new Exception("Can't find fake token classes dictionary");
                }

                return _fakeTokenClasses;
            }
        }

        private static IntPtr InjectStruct(Type type)
        {
            IntPtr result = GetClassPointerForType(type);
            if (result != IntPtr.Zero) { return result; }
            if (type.IsGenericType || type.IsEnum || !type.IsValueType)
            {
                throw new InvalidOperationException($"Type {type} is not valid for struct injection");
            }

            var basePtr = GetClassPointerForType<Il2CppSystem.ValueType>();
            var baseKlass = (MyIl2CppClass*)basePtr;

            // allocate class pointer
            var p = (MyIl2CppClass*)NewClass(baseKlass->vtable_count).Pointer;

            // assign token, set it to lead to our class
            long token = Interlocked.Increment(ref myTokenOverride);
            FakeTokenClasses[token] = (IntPtr)p;

            // start setting up our class properties, mostly referenced from dumped il2cpp structs or unhollower classinjector
            p->image = (Il2CppImage*)FakeImage;
            p->gc_desc = IntPtr.Zero;
            p->name = Marshal.StringToHGlobalAnsi(type.Name);
            p->namespaze = Marshal.StringToHGlobalAnsi(type.Namespace ?? string.Empty);

            p->byval_arg = new MyIl2CppType()
            {
                data = (IntPtr)token,
                attrs = 0,
                type = Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                num_mods = 0,
                byref = false,
                pinned = false,
                valuetype = true,
            };

            p->this_arg = new MyIl2CppType()
            {
                data = (IntPtr)token,
                attrs = 0,
                type = Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                num_mods = 0,
                byref = true,
                pinned = false,
                valuetype = false,
            };

            p->element_class = (Il2CppClass*)p;
            p->castClass = (Il2CppClass*)p;
            p->declaringType = (Il2CppClass*)IntPtr.Zero;
            p->parent = (Il2CppClass*)basePtr;
            p->generic_class = IntPtr.Zero;
            p->typeDefinition = (IntPtr)token;
            p->interopData = IntPtr.Zero;
            p->klass = (Il2CppClass*)p;

            p->events = (Il2CppEventInfo*)IntPtr.Zero;
            p->event_count = 0;

            p->properties = (Il2CppPropertyInfo*)IntPtr.Zero;
            p->property_count = 0;

            // todo-ish : field methods not injected
            p->methods = baseKlass->methods;
            p->method_count = baseKlass->method_count;

            p->nestedTypes = (Il2CppClass**)IntPtr.Zero;
            p->nested_type_count = 0;

            // no interfaces, fine for now I guess, can rework if needed
            p->implementedInterfaces = (Il2CppClass**)IntPtr.Zero;
            p->interfaces_count = 0;
            p->interfaceOffsets = (Il2CppRuntimeInterfaceOffsetPair*)IntPtr.Zero;
            p->interface_offsets_count = 0;

            // nope lol
            p->static_fields = IntPtr.Zero;
            p->static_fields_size = 0;
            p->thread_static_fields_size = 0;
            p->thread_static_fields_offset = 0;

            p->rgctx_data = IntPtr.Zero;

            // build type heirachy
            var typeDepth = baseKlass->typeHierarchyDepth + 1;
            p->typeHierarchyDepth = (byte)typeDepth;
            p->typeHierarchy = (Il2CppClass**)Marshal.AllocHGlobal(typeDepth * IntPtr.Size);
            p->typeHierarchy[typeDepth - 1] = (Il2CppClass*)basePtr;
            for (int i = 0; i < typeDepth; i++)
            {
                p->typeHierarchy[i] = baseKlass->typeHierarchy[i];
            }

            p->unity_user_data = IntPtr.Zero;
            p->initializationExceptionGCHandle = 0;

            // setting these to 1 so it doesn't try any funny business I guess? won't hurt, probably won't do anything
            p->cctor_started = 1;
            p->cctor_finished = 1;

            p->native_size = 0;
            p->instance_size = (uint)sizeof(Il2CppObject);
            p->actualSize = (uint)sizeof(Il2CppObject);

            p->genericContainerIndex = IntPtr.Zero;
            p->element_size = 0;
            p->token = 0; // il2cpp doesn't seem to care about this

            p->vtable_count = baseKlass->vtable_count; //  todo copy vtable
            p->genericRecursionDepth = 1;
            p->rank = 0;
            p->flags = Il2CppClassAttributes.TYPE_ATTRIBUTE_PUBLIC | Il2CppClassAttributes.TYPE_ATTRIBUTE_EXPLICIT_LAYOUT | Il2CppClassAttributes.TYPE_ATTRIBUTE_SEALED;

            p->minimumAlignment = 8;
            p->naturalAligment = 8;
            p->packingSize = 0;

            p->bitfield =
                MyIl2CppClass.ClassFlags.initialized_and_no_error |
                MyIl2CppClass.ClassFlags.valuetype;

            AddToClassFromNameDictionary(type, (IntPtr)p);
            _injectedStructs[type] = (IntPtr)p;

            return (IntPtr)p;
        }

        private static void InjectStructFields(Type type, MyIl2CppClass* klass)
        {
            var serialiser = StructSerialiser<float>.GetSerialiser(type);
            if (serialiser.IsBlittable)
            {
                klass->bitfield |= MyIl2CppClass.ClassFlags.is_blittable;
            }

            serialiser.WriteFields(klass);
        }

        private static void InjectBatch(Type[] classes, Type[] structs)
        {
            injection.Clear();
            int n = classes.Length;
            int m = structs.Length;

            // build a mapping of the structs and their dependencies
            Dictionary<Type, List<Type>> structDependencyMappings = new Dictionary<Type, List<Type>>();
            foreach (var type in structs)
            {
                List<Type> list = null;
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (structs.Contains(field.FieldType))
                    {
                        if (list == null) { list = new List<Type>(); }
                        list.Add(field.FieldType);
                    }
                }

                if (list != null) { structDependencyMappings.Add(type, list); }
            }

            // reorder the list to ensure that structs that are inside other structs are injected before those structs
            for (int i = 0; i < m; i++)
            {
                if (structDependencyMappings.TryGetValue(structs[i], out var dependencies))
                {
                    foreach (var dep in dependencies)
                    {
                        int j = Array.IndexOf(structs, dep);
                        if (j > i) // the dependency needs to be before the dependent, so swap
                        {
                            (structs[i], structs[j]) = (structs[j], structs[i]);
                            i--; // this will set us back so we now check the dependency we just swapped into this slot on the next iteration
                            break;
                        }
                    }
                }
            }

            // reorder the list to ensure that base types are processed first
            for (int i = 0; i < n; i++)
            {
                int index = Array.IndexOf(classes, classes[i].BaseType);
                if (index != -1 && index < i)
                {
                    // swap
                    (classes[i], classes[index]) = (classes[index], classes[i]);
                }
            }

            Log($"Serialising a batch of {n} classes and {m} structs:", 1);
            if (LogLevel >= 1)
            {
                foreach (var tti in structs) Msg($"  {tti.FullName}");
                foreach (var tti in classes) Msg($"  {tti.FullName}");
            }

            // Initial struct injection
            IntPtr[] structPtrs = new IntPtr[m];
            for (int i = 0; i < m; i++)
            {
                Type t = structs[i];
                try
                {
                    Log($"Initial injection for struct {t.Name}", 2);
                    structPtrs[i] = InjectStruct(t);
                }
                catch (Exception ex)
                {
                    Error($"Struct initial injection failed for {t}:", ex);
                }
            }

            // Inject class and get a reference to it.
            foreach (var t in classes)
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

            // Inject struct fields
            for (int i = 0; i < m; i++)
            {
                var t = structs[i];
                try
                {
                    Log($"Writing struct fields for {t.FullName}", 2);
                    var p = structPtrs[i];
                    InjectStructFields(t, (MyIl2CppClass*)p);
                }
                catch (Exception ex)
                {
                    Error($"Struct field injection failed for {t}:", ex);
                }
            }

            // Inject class fields
            foreach (var t in classes)
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