using MelonLoader;

namespace FieldInjector
{
    internal class Mod : MelonMod
    {
#if DEBUG
        private bool _test = false;

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (!this._test)
            {
                this._test = true;
                Test.Testing.Test();
            }
        }

#endif
    }
}