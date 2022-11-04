using MelonLoader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnhollowerBaseLib;
using UnhollowerRuntimeLib;
using UnhollowerRuntimeLib.XrefScans;
using UnityEngine;
using Logging = MelonLoader.MelonLogger;

namespace FieldInjector
{
    internal class Mod : MelonMod
    {
        private unsafe void LogClassStuff(MyIl2CppClass* c)
        {
            MelonLogger.Msg($"c has {c->method_count} methods");
            MelonLogger.Msg($"this_arg.type: {c->this_arg.type}");
            MelonLogger.Msg($"{c->this_arg.IsByRef}");
            MelonLogger.Msg($"{c->this_arg.mods_byref_pin}");
            MelonLogger.Msg($"by_val_arg.type: {c->byval_arg.type}");
            MelonLogger.Msg($"{c->byval_arg.IsByRef}");
            MelonLogger.Msg($"{c->byval_arg.mods_byref_pin}");
            
            for (int i = 0; i < c->method_count; i++)
            {
                MyMethodInfo* methodInfo = (MyMethodInfo*)c->methods[i];
                var namePtr = methodInfo->name;
                string name = Marshal.PtrToStringAnsi(namePtr);
                if (name == ".ctor")
                {
                    MelonLogger.Msg($"ctor takes {methodInfo->parameters_count} parameters");
                    for (int j = 0; j < methodInfo->parameters_count; j++)
                    {
                        MyIl2CppType* param = methodInfo->parameters[j];
                        MelonLogger.Msg($"param {j}: {param->type}");
                    }
                }
            }
        }
        public override void OnApplicationStart()
        {
            //SerialisationHandler.Inject<TestMB8>(debugLevel: 5);
            unsafe
            {
                MyIl2CppClass* v3 = (MyIl2CppClass*)Il2CppClassPointerStore<Vector3>.NativeClassPtr;
                LogClassStuff(v3);
                MyIl2CppClass* mb = (MyIl2CppClass*)Il2CppClassPointerStore<MonoBehaviour>.NativeClassPtr;
                LogClassStuff(mb);
            }
        }
    }

}
/*
internal class TestMB8 : MonoBehaviour
{
#if !UNITY_EDITOR
    public TestMB8(IntPtr ptr) : base(ptr) { }
    static void Log(string s) => MelonLogger.Msg(s);
#else
    static void Log(string s) => Debug.Log(s);
#endif

    public enum TestEnum : byte
    {
        A,
        B,
        C,
    };

    public Space space;
    public AnisotropicFiltering testB;
    public TestEnum testEnum;
    public int flagValue;
    public Transform tr;
    public TestEnum[] array1;
    public List<Space> spaces;
    public string[] stringArray;
    public List<string> stringList;
    public Transform[] transformArray;
    public List<GameObject> objectList;
    public string testString;

    private static string PrintArray<T>(T[] arr)
    {
        if (arr == null)
        {
            return "null";
        }
        else
        {
            return $"[{arr.Length}]: {string.Join(",", arr)}";
        }
    }

    private static string PrintObjArray(IEnumerable<UnityEngine.Object> arr)
    {
        if (arr == null)
        {
            return "null";
        }
        else
        {
            return $"[{arr.Count()}]: {string.Join(",", arr.Select((a) => a.name))}";
        }
    }

    public void Start()
    {
        Log($"flag is: {this.flagValue}");
        Log($"tr is: {this.tr.gameObject.name}");
        Log($"space is: {this.space}");
        Log($"testB is: {this.testB}");
        Log($"testEnum is: {this.testEnum}");
        Log($"testString is: {this.testString}");
        Log($"array1 = {PrintArray(this.array1)}");
        Log($"spaces = {PrintArray(this.spaces.ToArray())}");
        Log($"stringArray = {PrintArray(this.stringArray)}");
        Log($"stringList = {PrintArray(this.stringList.ToArray())}");
        Log($"transformArray = {PrintObjArray(this.transformArray)}");
        Log($"objectList = {PrintObjArray(this.objectList.ToArray())}");
    }
}*/