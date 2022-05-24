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

            //SerialisationHandler.DebugEnum();
            //SerialisationHandler.DebugEnumType(typeof(Space));
            //SerialisationHandler.DebugEnumType(typeof(AnisotropicFiltering));
            SerialisationHandler.Inject<TestMB8>(debugLevel: 5);
        }
    }

}
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
    //public List<Space> spaces;

    public void Start()
    {
        Log($"flag is: {this.flagValue}");
        Log($"tr is: {this.tr.gameObject.name}");
        Log($"space is: {this.space}");
        Log($"testB is: {this.testB}");
        Log($"test is: {this.testEnum}");
        if (this.array1 != null)
        {
            Log($"array is: [{string.Join(", ", this.array1)}]");
        }
        else
        {
            Log($"array is null");
        }
    }
}