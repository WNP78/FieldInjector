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
        private static InstanceMethodPtrInvoker invoker = new();

        private static readonly Dictionary<Type, List<SerialisedField>> serialisationCache = new();

        public static void InjectTypes(Type[] types, int debugLevel = 0)
        {
            foreach (Type t in types)
            {
                // TODO: Inline parts of the inject into here so we can handle dependencies correctly
                InjectRecursive(t, debugLevel);
            }
        }

        [Obsolete("Use InjectTypes")]
        public static void Inject<T>(int debugLevel = 0) where T : MonoBehaviour
        {
            InjectRecursive(typeof(T), debugLevel);
        }

        [Obsolete("Use InjectTypes")]
        public static void Inject(Type t, int debugLevel = 0)
        {
            InjectRecursive(t, debugLevel);
        }

        private static List<SerialisedField> InjectRecursive(Type t, int debugLevel)
        {
            if (t.Namespace != null && t.Namespace.StartsWith("System")) return null;
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
            if (Util.GetClassPointerForType(t) != IntPtr.Zero)
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
            var klassPtr = (MyIl2CppClass*)Util.GetClassPointerForType(t, bypassEnums: true);
            var klass = Wrap((Il2CppClass*)klassPtr);

            // fix unhollower not setting namespace field if there's no namespace
            if (klassPtr->namespaze == IntPtr.Zero)
            {
                klassPtr->namespaze = Marshal.StringToHGlobalAnsi(string.Empty);
            }

            var baseKlassPtr = (MyIl2CppClass*)Util.GetClassPointerForType(t.BaseType);

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
                expressions = expressions.Prepend(Util.LogExpression($"Deserialise {t}:", nativePtr));
                expressions = expressions.Append(Util.LogExpression("Deserialise complete: ", nativePtr));
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
                expressions = expressions.Prepend(Util.LogExpression($"Serialise {t}:", nativePtr));
                expressions = expressions.Append(Util.LogExpression("Serialise complete: ", nativePtr));
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
                method.InvokerMethod = invoker.InvokerPtr;
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

            generated.InvokerMethod = invoker.InvokerPtr;
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

        #region Finalize patch

        private static readonly StaticVoidIntPtrDelegate FinalizeDelegate = Finalize;

        private static void Finalize(IntPtr ptr)
        {
            var gcHandle = ClassInjectorBase.GetGcHandlePtrFromIl2CppObject(ptr);
            if (gcHandle == IntPtr.Zero) { return; }
            GCHandle.FromIntPtr(gcHandle).Free();
        }
        #endregion
    }
}
