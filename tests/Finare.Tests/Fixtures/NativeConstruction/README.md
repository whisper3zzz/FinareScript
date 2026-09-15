# Native C# Character construction

`CharacterScript.cs` declares an ordinary `/Script/Engine.Character` receiver.
`OnConstruct` creates `PlayerCamera` and `WeaponMesh` through UE's actual
`FObjectInitializer::CreateDefaultSubobject`, attaches them through
`SetupAttachment`, and writes the reflected camera reference. The native test
does not build an SCS component tree or create either tested component.

Preparation (after building the Editor target):

```powershell
./tools/prepare-native-construction.ps1
./tools/test-class-composition.ps1 -SkipBuild -SkipPrepare `
  -Tests @('Finare.Unreal.Construction.CharacterDefaults')
```

The preparation tool accepts `-ReflectionPath` and `-CliPath` to reuse a current
full Engine/FinareRuntime export and CLI build. The API-library CI entry also
prepares and executes this fixture.

Construction is synchronous and distinct from BeginPlay. Use `context.Host`
and generated UE wrappers; the gameplay `Bridge`, `Lifetime`, World services
and instance generation are unavailable. Returned wrappers expire immediately
after the callback. Use reflected `partial` properties for references needed
by gameplay, and keep their required source initializers (`= null`, `= 0`).
Ordinary managed fields/resources are not transferred to the gameplay instance.

The test saves an edited camera FOV, unloads/reopens the class, spawns two
Characters, and checks independent native components, references, registration,
BeginPlay and same-program reload. Fixture package bytes are restored when a
file already exists.

Current boundaries: game-thread class loading; already loaded concrete UE
subobject classes; constructor-safe native API calls. Async constructor loading
reports an error rather than moving construction to BeginPlay. Cook/package
and live constructor replacement are not accepted here. A changed program
containing `OnConstruct` requires stopping and rebuilding the Script Class CDO;
this currently includes gameplay-only changes in that program.
