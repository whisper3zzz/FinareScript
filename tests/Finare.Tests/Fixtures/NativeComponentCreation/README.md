# C# calls UE component creation APIs

`ComponentScripts.cs` uses the generated Engine API library. The Actor has the
ordinary `AActor` parent; its two script components have the ordinary
`USceneComponent` parent. No component list, custom creation facade or C++
gameplay setup is needed. Configure Engine and FinareRuntime in the project's
normal API library and use the generated namespace from that project.

The sample calls `AddComponentByClass`, `FinishAddComponent`,
`AttachToComponent`, `SetStaticMesh` and `DestroyComponent`. It loads native
classes and a C# component class through `LoadClassByPath`. Component ownership,
attachment and registration follow UE's implementation of those operations.
The sample explicitly owns and releases the four components it creates; it
does not delete other components found by type or name.

For properties that must be set before BeginPlay, pass `bDeferredFinish: true`,
set the returned component's properties, then call `FinishAddComponent`. The
two observer instances see their own input values on automatic activation.
Their behavior remains independent, including Tick and destruction.

This is runtime creation. It does not implement native CDO/default-subobject
construction, persist an editor component tree, or run a C# constructor inside
`FObjectInitializer`. `CreateDefaultSubobject` must not be substituted for a
runtime API or called from BeginPlay.

## Reproduce in this checkout

With the managed host and Editor already built/staged:

```powershell
./tools/prepare-native-component-creation.ps1 -EngineRoot D:/UE_5.8
./tools/test-class-composition.ps1 -SkipPrepare -Tests Finare.Unreal.Components.GeneratedCreation
```

Preparation exports Engine and FinareRuntime, generates and compiles their
supported API library before the gameplay source is included, then compiles and
emits the two C# declarations. `-ReflectionPath` and `-CliPath` let the existing
API-library CI lane reuse its fresh export/compiler. Generated files stay under
`artifacts/native-component-creation`; runtime chunks are under
`HostProject/Saved/Finare/NativeComponentCreation`.

The Editor test materializes the emitted classes and spawns the Actor. It never
creates, registers or attaches the four tested components. It observes native
mesh/attachment results, pre-BeginPlay inputs, independent ticks, C#-initiated
destruction, rejected access to a destroyed receiver, reload counts and World
cleanup. Fixed-path fixture packages are restored after the test.

The existing `tools/test-api-library.ps1` gate prepares and runs this test in
the `unreal-automation` CI lane. Its result is Editor evidence, not installed
project or Cook/package acceptance.
