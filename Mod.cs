using MelonLoader;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Logging = MelonLoader.MelonLogger;

namespace FieldInjector
{
    internal class Mod : MelonMod
    {
        public override void OnInitializeMelon()
        {
            SerialisationHandler.Inject<TestMB8>(debugLevel: 5);
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            Test();
        }

        public static void Test()
        {
            Logging.Msg("Creating test object");
            var g1 = new GameObject("Test Source");
            var c = g1.AddComponent<TestMB8>();
            c.space = Space.Self;
            c.testB = AnisotropicFiltering.ForceEnable;
            c.testEnum = TestMB8.TestEnum.B;
            c.flagValue = 92;
            c.tr = g1.transform;
            c.array1 = new TestMB8.TestEnum[] { TestMB8.TestEnum.C, TestMB8.TestEnum.A, TestMB8.TestEnum.B };
            c.spaces = new List<Space>(new Space[] { Space.World, Space.Self, Space.World });
            c.stringArray = new string[] { "one", "two", "three" };
            c.stringList = new List<string>(new string[] { "alpha", "bravo", "charlie" });
            c.transformArray = new Transform[] { g1.transform, g1.transform };
            c.objectList = new List<GameObject>(new GameObject[] { g1 });
            c.testString = "tabloid's real name";

            Logging.Msg("Duplicating test object\n\n\n");
            UnityEngine.Object.Instantiate(c);
        }
    }
}

/*

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

#if !UNITY_EDITOR && !UNITY_2017_1_OR_NEWER
    public TestMBSt(IntPtr ptr) : base(ptr) { }
    static void Log(string s) => MelonLogger.Msg(s);
#else
    static void Log(string s) => Debug.Log(s);
#endif

    void Awake()
    {
        Log($"myStruct.x = {this.myStruct.x}");
        Log($"myStruct.vector = {this.myStruct.vector.ToString()}");
        Log($"myStruct.str = {this.myStruct.str}");
        Log($"myStruct.objRef = {(this.myStruct.objRef == null ? "null" : this.myStruct.objRef.name)}");
    }
}
*/

public class TestMB8 : MonoBehaviour
{
#if !UNITY_EDITOR

    public TestMB8(IntPtr ptr) : base(ptr)
    {
    }

    private static void Log(string s) => MelonLogger.Msg(s);

#else
    static void Log(string s) => Debug.Log(s);
#endif

    public enum TestEnum : int
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
        Log("===============");
        Log("TestMB8.Start()");
        Log("===============");
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
        Log("");
    }
}