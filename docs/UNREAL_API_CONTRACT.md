# Source-driven Unreal bindings

Project setup derives bindings from the managed projects in `managed.projects`.
There is no project API Contract, allowlist, development overlay, or frozen binding
lock. Old `bindings.contract` and `bindings.developmentSurface` configuration keys
are errors. The removed `ue-contract-propose`, `ue-surface-expand`, `--contract`,
and reflection-hint `--base` options are not compatibility entrypoints.

## Build flow

1. Load the C# projects through MSBuild, including their actual source files,
   references and compilation settings. Binding generation does not require the
   gameplay project to compile before missing bindings can be generated.
2. Find referenced generated types and ask UE to export their reflected metadata.
   Public reflected members do not need BlueprintCallable or BlueprintReadWrite.
3. Resolve source member references and exact overloads against that metadata.
4. Generate C#, native dispatch, optional Graph nodes, API browser and coverage
   together. Missing, ambiguous or unsupported references fail with source locations
   before any generated output is replaced.

Removing a call removes its unused binding on the next generation. Ordinary C#
helpers remain ordinary C#; Graph authoring is optional. Type-only references,
enums and supported struct fields retain the declarations needed by C# signatures.
The type hint cache under `Saved/Finare/Bindings` is disposable exporter input.
It is not a support checklist or an artifact that a developer approves.

```powershell
finare ue-reflection-hints --project Managed/Game.Finare.csproj `
  --namespace Game.Finare.Generated --out Generated/type-hints.json

finare ue-bindings Generated/Game.reflection.json `
  --project Managed/Game.Finare.csproj --namespace Game.Finare.Generated `
  --out Managed/Generated/Game.bindings.generated.cs `
  --native-out Source/Game/Generated/Game.thunks.cpp `
  --nodes-out Generated/Game.api-nodes.json `
  --browser-out Generated/Game.api-browser.json `
  --report Generated/Game.binding-coverage.json --fail-on-rejected
```

Setup config uses `bindings.namespace` and output paths. The source project list
comes from `managed.projects`. See [project onboarding](UNREAL_ONBOARDING.md).
The low-level `ue-bindings` command also accepts a reflection-only input for
synthetic generator fixtures and benchmarks; project Setup always supplies C# projects.

## Non-reflected C++ declarations

UHT cannot discover ordinary C++ classes or methods. Their actual signatures,
header paths, argument passing and return ownership remain necessary generator
inputs. They live in an optional standalone native-extension declaration file,
not inside a project member selection list. The repository example is
[FinareRuntime.native-extensions.json](../samples/Unreal/FinareRuntime.native-extensions.json).

Use `bindings.nativeExtensions` in Setup, or `--native-extensions <file>` on both
commands above. The generator adds these declarations to reflection before
resolving C# use; unused declared methods are not selected merely because they
are declared. Native-only classes are excluded from UE reflected-class discovery.
No declaration file is needed for ordinary reflected APIs.

C# factory calls and native object ownership helpers keep the existing lifetime
rules: constructor overloads select exact signatures, owned objects are disposed,
and borrowed objects stay tied to their owner and World generation.

## Generator internals and evidence

The generator still uses an in-memory selection model named `UnrealApiContract`
and its projector. JSON fixtures used by generator tests describe synthetic
selection and ABI cases; they are not accepted as project configuration or CLI
selection inputs. No request, overlay or lock files are emitted by Setup.

The shared selection feeds all consumers with the same reflected IDs. Native
routing still chooses a supported generated thunk or safe reflection route;
unsupported signatures are diagnosed, never represented as working bindings.
Blueprint assets retain exact generated-class paths and lifetime rules.

Focused integration tests cover call removal, type-only use, empty UE usage,
invalid projects and all-or-nothing output replacement. The pure C# UE fixture
loads the emitted `.uschunk`, runs gameplay and reloads a second revision while
preserving state. See [first script](UNREAL_FIRST_SCRIPT.md) and
[architecture](ARCHITECTURE.md) for runtime boundaries.
