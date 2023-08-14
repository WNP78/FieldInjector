using MelonLoader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using Logging = MelonLoader.MelonLogger;

namespace FieldInjector.Test
{
    internal static class Testing
    {
        [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern void DebugBreak();

        [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern bool IsDebuggerPresent();

        internal static void Test()
        {
            Logging.Msg("===========");
            Logging.Msg("Mod.Test");
            if (IsDebuggerPresent())
            {
                Logging.Msg("Debug Break");
                DebugBreak();
            }

            Logging.Msg("Injecting test class");
            SerialisationHandler.Inject<TestMBSt>(debugLevel: 5);

            unsafe
            {
                var type = (MyIl2CppClass*)Util.GetClassPointerForType<Vector3>();
                Logging.Msg(type->Debug());
                Logging.Msg("\n\n\n");

                SerialisationHandler.Inject<TestStruct>(debugLevel: 5);
                type = (MyIl2CppClass*)Util.GetClassPointerForType<TestStruct>();
                Logging.Msg(type->Debug());

                //type = (MyIl2CppClass*)Util.GetClassPointerForType<Il2CppSystem.ValueType>();
                //Logging.Msg(type->Debug());
            }

            Logging.Msg("========");

            /*
            Logging.Msg("Creating test object");
            var g1 = new GameObject("Test Source");
            var c = g1.AddComponent<TestMB8>();
             */

            Logging.Msg("Creating test object");
            var g1 = new GameObject("Test Source");
            var script = g1.AddComponent<TestMBSt>();
            var c = new TestStruct
            {
                space = Space.Self,
                testB = AnisotropicFiltering.ForceEnable,
                testEnum = TestEnum.B,
                flagValue = 92,
                tr = g1.transform,
                array1 = new TestEnum[] { TestEnum.C, TestEnum.A, TestEnum.B },
                spaces = new List<Space>(new Space[] { Space.World, Space.Self, Space.World }),
                stringArray = new string[] { "one", "two", "three" },
                stringList = new List<string>(new string[] { "alpha", "bravo", "charlie" }),
                transformArray = new Transform[] { g1.transform, g1.transform },
                objectList = new List<GameObject>(new GameObject[] { g1 }),
                testString = "tabloid's real name"
            };
            script.myStruct = c;
            script.testInt = 123;
            Logging.Msg($"testInt: {script.testInt}");
            script.myStruct.Debug();

            Logging.Msg("Duplicating test object\n\n\n");
            var g2 = UnityEngine.Object.Instantiate(g1);
            Debug.Break();
            g2.GetComponent<TestMBSt>().myStruct.Debug();
            Logging.Msg($"testInt: {g2.GetComponent<TestMBSt>().testInt}");
            GameObject.Destroy(g1);
            GameObject.Destroy(g2);

            Logging.Msg("===========");
        }
    }

    public enum TestEnum : int
    {
        A,
        B,
        C,
    };

    [Serializable]
    internal struct TestStruct
    {
        public int flagValue;
        public Space space;
        public AnisotropicFiltering testB;
        public TestEnum testEnum;
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

        public void Debug()
        {
            Logging.Msg("===============");
            Logging.Msg("TestStruct.Debug()");
            Logging.Msg("===============");
            Logging.Msg($"flag is: {this.flagValue}");
            Logging.Msg($"tr is: {this.tr?.gameObject?.name}");
            Logging.Msg($"space is: {this.space}");
            Logging.Msg($"testB is: {this.testB}");
            Logging.Msg($"testEnum is: {this.testEnum}");
            Logging.Msg($"testString is: {this.testString}");
            Logging.Msg($"array1 = {PrintArray(this.array1)}");
            Logging.Msg($"spaces = {PrintArray(this.spaces?.ToArray())}");
            Logging.Msg($"stringArray = {PrintArray(this.stringArray)}");
            Logging.Msg($"stringList = {PrintArray(this.stringList?.ToArray())}");
            Logging.Msg($"transformArray = {PrintObjArray(this.transformArray)}");
            Logging.Msg($"objectList = {PrintObjArray(this.objectList?.ToArray())}");
            Logging.Msg("");
        }
    }

    internal class TestMBSt : MonoBehaviour
    {
        public TestStruct myStruct;
        public int testInt;

#if !UNITY_EDITOR && !UNITY_2017_1_OR_NEWER

        public TestMBSt(IntPtr ptr) : base(ptr)
        {
        }

        private static void Log(string s) => MelonLogger.Msg(s);

#else
    static void Log(string s) => Debug.Log(s);
#endif
    }

    /*
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
            Log($"tr is: {this.tr.gameObject?.name}");
            Log($"space is: {this.space}");
            Log($"testB is: {this.testB}");
            Log($"testEnum is: {this.testEnum}");
            Log($"testString is: {this.testString}");
            Log($"array1 = {PrintArray(this.array1)}");
            Log($"spaces = {PrintArray(this.spaces?.ToArray())}");
            Log($"stringArray = {PrintArray(this.stringArray)}");
            Log($"stringList = {PrintArray(this.stringList?.ToArray())}");
            Log($"transformArray = {PrintObjArray(this.transformArray)}");
            Log($"objectList = {PrintObjArray(this.objectList?.ToArray())}");
            Log("");
        }
    }
    */

    /*
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
            Log($"tr is: {this.tr.gameObject?.name}");
            Log($"space is: {this.space}");
            Log($"testB is: {this.testB}");
            Log($"testEnum is: {this.testEnum}");
            Log($"testString is: {this.testString}");
            Log($"array1 = {PrintArray(this.array1)}");
            Log($"spaces = {PrintArray(this.spaces?.ToArray())}");
            Log($"stringArray = {PrintArray(this.stringArray)}");
            Log($"stringList = {PrintArray(this.stringList?.ToArray())}");
            Log($"transformArray = {PrintObjArray(this.transformArray)}");
            Log($"objectList = {PrintObjArray(this.objectList?.ToArray())}");
            Log("");
        }
    }
    */
}