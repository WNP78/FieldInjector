using MelonLoader;
using System;
using UnityEngine;
using Logging = MelonLoader.MelonLogger;

namespace FieldInjector
{
    internal class Mod : MelonMod
    {
        public override void OnApplicationStart()
        {
            
            SerialisationHandler.DebugEnum();
            SerialisationHandler.Inject<TestMB8>(debugLevel: 5);
        }
    }

}
    internal class TestMB8 : MonoBehaviour
    {
#if !UNITY_EDITOR
        public TestMB8(IntPtr ptr) : base(ptr) { }
        void Log(string s) => MelonLogger.Msg(s);
#else
        void Log(string s) => Debug.Log(s);
#endif

        public enum TestEnum : byte
        {
            A,
            B,
            C,
        };

        public AnisotropicFiltering testB;
        public TestEnum testEnum;

        public void Start()
        {
            Log($"testB is: {this.testB}");
            Log($"test is: {this.testEnum}");
        }
    }