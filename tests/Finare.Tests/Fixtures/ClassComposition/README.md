# Pure C# Actor composition

This Editor integration fixture uses three real C# Script Classes:

- `CompositionActor` explicitly requests `/Script/Engine.Actor` and exposes `Seed = 11`.
- `CompositionHealth` is a Finare ActorComponent with `HitPoints = 100`.
- `CompositionMover` is a Finare ActorComponent with `Speed = 3` and a UObject
  reference to Health. Its C# tick reads Health through the generated API.

Business behavior lives in C#. UE still stores generated Blueprint proxies and
SCS component templates. The ordinary Actor owns an SCS `UnifiedScript`
execution carrier; the default Script Class factory continues to create
`AFinareActor` unless the declaration requests another Actor parent.
Component classes currently remain in the `UFinareComponent` family.

From the repository root on the configured UE 5.8 / .NET 10 workstation:

```powershell
./tools/test-class-composition.ps1
```

The CLI and managed host must already be built/staged by the normal project
setup. The command prepares the fixture, builds `FinareHostEditor`, then runs
focused Automation in `UnrealEditor-Cmd` with NullRHI. `-SkipBuild` and
`-SkipPrepare` reuse existing outputs when their inputs have not changed.
Results are written to `artifacts/class-composition/editor.log`.

Preparation emits declarations and stable identities with the real CLI and
assigns unique generated class paths under `/Game/FinareComposition_<id>`.
The persistent test authors explicit values `23 / 135 / 8` and gives Mover a
reference to Health through the exposed UE properties, then saves, unloads and
reopens the generated classes.
Runtime checks must use the restored values and the spawned Health instance;
the test must not repair values or references after spawning.

The save/reload portion watches the editable C# workspace under
`artifacts/class-composition/workspace`. The actual Editor Daily Build queue
launches `build-class-composition-revision.ps1`, which compiles the saved source,
emits declarations, checks class identity, publishes the assembly, and produces
the build receipt. Only the build command is substituted by an Automation-only
hook so the test does not rebuild or publish the user's Demo project. This is
an integration test of source watching, compilation, queue completion and live
batch reload; it is not full `setup-unreal-project.ps1` acceptance.

The successful revision changes observable behavior to `v2`. The next saved
revision changes the behavior marker to `bad` and throws while starting Mover;
all three components must retain generation 2 and the `v2` behavior.
Publication recovery separately injects a file-write
failure and checks the Blueprint, Script Asset and deployment file preimages,
as well as existing child component ownership.

Fixture package files, live assembly/declaration, editable source and scoped
configuration are restored after the tests. The runner also restores UE's
`DefaultEngine.ini` and `DefaultInput.ini` writes. These are Editor checks;
Cook, packaging, arbitrary component adapters and the remaining C-4 parent /
reference ownership cases require their own work.
