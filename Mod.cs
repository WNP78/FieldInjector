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
        public override void OnInitializeMelon()
        {
            SerialisationHandler.Inject<TestMBSt>(debugLevel: 5);
        }
    }

}

[Serializable]
internal struct TestStruct
{
    public float x;
    public Vector3 vector;
    public string str;
    public GameObject objRef;
}

internal class TestMBSt : MonoBehaviour
{
    public TestStruct myStruct;

    void Awake()
    {
        Logging.Msg($"myStruct.x = {this.myStruct.x}");
        Logging.Msg($"myStruct.vector = {this.myStruct.vector.ToString()}");
        Logging.Msg($"myStruct.str = {this.myStruct.str}");
        Logging.Msg($"myStruct.objRef = {(this.myStruct.objRef == null ? "null" : this.myStruct.objRef.name)}");
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