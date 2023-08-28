# FieldInjector
MelonLoader mod for BONELAB to inject classes into the IL2CPP runtime with fields and serialisation working. Originally for BONEWORKS, ported forwards to BONELAB. 

## For Players
Releases on: [ThunderStore](https://bonelab.thunderstore.io/package/WNP78/FieldInjector/)

## For Programmers

If you want to utilise custom MonoBehaviours with properly serialised fields from asset bundles, reference this mod as a dependency.
**Note**: instead of using this mod directly, I might recommend using [Maranara's "Cauldron"](/package/Maranara/Marrow_Cauldron/) system for a more unified end-to-end experience and integration with the Unity editor, that uses this mod under the hood. The instructions below apply only if you want to roll your own systems and just use this to apply serialisation.

### Writing a custom MonoBehaviour

This is fairly simple, and there is very little difference between a normal unity MonoBehaviour and this. There is only a small amount of boilerplate code to add:
```cs
using UnityEngine;

class MyScript : MonoBehaviour
{
    // all of your normal MonoBehaviour code can go here

#if !UNITY_EDITOR
    public TestMB(IntPtr ptr) : base(ptr) { }
#endif
}
```
the `#if` instruction means that this code should compile in both the Unity Editor and in a MelonLoader mod, so that you can keep your code unified and test in the editor with ease.

### Injecting a custom MonoBehaviour
This is what this mod is used for, and it is very simple:
```cs
FieldInjector.SerialisationHandler.Inject<MyScript>();
```
in the `OnApplicationStart` method is all that is needed. Do not register the class in Il2Cpp with MelonLoader or UnhollowerBaseLib - this mod does that itself and it won't be able to inject the class with fields if it's already injected without fields. Makers of frameworks that load code should consider registering fields for loaded behaviours automatically.
