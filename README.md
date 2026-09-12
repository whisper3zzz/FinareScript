# Finare

Finare is an experimental C# gameplay system for Unreal. The default project
uses ordinary C#, Roslyn compilation, CoreCLR execution and an available Unreal
API library. Supported APIs appear in IDE completion before use. Saving gameplay
source queues the Editor's Build & Reload flow.

Graph editing is optional. Only methods in the published
[Visual C# profile](docs/VISUAL_CSHARP_PROFILE.md) promise lossless graph editing.
Ordinary C# methods do not require Graph projection. The compiler,
semantics and graph projects stay independent of Unreal Engine.

## Current status

The declared platform is **Unreal Engine 5.8.1 / Win64 / .NET 10**. The SDK
manifest retains version `1.0.0-rc.1`; its frozen release acceptance belongs to
an earlier revision. Later development adds capabilities with separately scoped
verification. See [capabilities and limits](docs/CAPABILITIES.md) before relying
on a particular class, API, reload or packaging path.

Current product work completes ordinary Actor/component class composition.
The Lyra health/HUD pilot has recorded successful Editor sequences in two
independent copies; asset/async failure coverage remains open. Further
Cook/package validation is deferred in the current work scope. The
[roadmap](docs/ROADMAP.md) owns the next outcome and its completion conditions.

Formerly UEUnifiedScript / UnifiedScript. Assemblies, namespaces, Unreal plugin
and modules were renamed to Finare. Existing managed scripts need rebuilding;
assets referring to old UE types need regeneration or repair. The rename does
not install Core Redirects. Follow the [SDK migration notes](docs/UNREAL_SDK.md).

## Use Finare in an Unreal project

Start with an extracted SDK and follow the
[first-project guide](docs/UNREAL_FIRST_PROJECT.md). It covers adoption,
project-local setup, first Play and a C# edit through Build & Reload.

Continue with [gameplay recipes](docs/UNREAL_GAMEPLAY_RECIPES.md) for actors,
components, events, asset loading, spawning, UI and async work. The
[Lyra sample](samples/External/Lyra/README.md) documents the external health/HUD
workflow and its required independent project copy.

SDK creation and installation are described in
[Unreal SDK distribution](docs/UNREAL_SDK.md). Installation, generated bindings
and build outputs must belong to a compatible managed/native set.

## Build and test the source

Use the .NET SDK selected by [global.json](global.json) and Python 3.10+.
Run these commands from the repository root:

```powershell
dotnet build Finare.slnx
dotnet test tests/Finare.Tests/Finare.Tests.csproj --no-build
python -B tools/validate_project.py
```

The [testing guide](docs/TESTING.md) gives focused managed suites, Python tool
tests, UE integration entrypoints and the boundary between Editor and packaged
acceptance. Full native/package/performance checks depend on the changed
behavior; they are not required for an ordinary documentation edit.

## Documentation

| Need | Start here |
| --- | --- |
| Install and write gameplay | [Product documentation](docs/DOCUMENTATION.md) |
| Check supported scope | [Capabilities and limits](docs/CAPABILITIES.md) |
| Choose the next product task | [Current roadmap](docs/ROADMAP.md) |
| Change the compiler, runtime or adapter | [Architecture](docs/ARCHITECTURE.md), [contributing](CONTRIBUTING.md) |
| Select and interpret tests | [Testing guide](docs/TESTING.md) |
| Combine existing checks | [Optional Harness](docs/HARNESS.md), [CI lanes](docs/CI.md) |
| Find detailed compiler/patch command examples | [Retained CLI examples](docs/history/IMPLEMENTATION_2026-09-11.md#analyze-the-sample) |
| Understand previous milestones or acceptance | [History index](docs/history/README.md) |

Current instructions, capability boundaries and historical evidence have
different owners. Update the relevant document when behavior changes; keep
run-by-run logs in the owning report or implementation plan.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/Finare.Core`, `Finare.Semantics`, `Finare.CSharp`, `Finare.Graph` | Language, identity, lowering and graph projection |
| `src/Finare.ProgramModel`, `Finare.Compilation` | Shared program artifacts and compilation/debug orchestration |
| `src/Finare.Authoring`, `Finare.Cli` | Authoring surface and project/tool commands |
| `src/Finare.Runtime`, `Finare.Unreal` | CoreCLR execution and managed Unreal integration |
| `HostProject/Plugins/Finare` | Native Unreal runtime, editor and Automation cases |
| `tests/Finare.Tests`, `tools/tests` | Managed behavior and Python tooling tests |
| `samples`, `templates`, `comparison` | Examples, consumer scaffolds and matched workloads |
| `docs`, `schemas`, `release` | Documentation, versioned contracts and release declaration |

Generated outputs must be updated through their generators. Repository work
follows [AGENTS.md](AGENTS.md) and [CONTRIBUTING.md](CONTRIBUTING.md).
