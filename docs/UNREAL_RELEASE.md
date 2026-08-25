# Unreal Win64 release gate

## Run

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File tools/package-unreal-release.ps1 `
  -EngineRoot D:\UE_5.8
```

The default invocation builds both `Development` and `Shipping`. A focused run
can pass `-Configurations Development` or `-Configurations Shipping`.

Each configuration goes through the real UE pipeline: managed Release build,
UBT build, Cook, Stage, Pak/IoStore, Archive, dependency-closure checks, and a
launch of the packaged internal executable. The smoke creates a Game World,
loads the packaged CoreCLR host and demo assembly, runs managed Start/Tick/Stop
through ABI v3, and exits. A Shipping smoke writes a standalone UTF-8 result,
so the gate does not depend on shipping log settings.

Outputs are placed under a timestamped ignored directory:

```text
artifacts/ue/release/<timestamp>/
  Development/ or Shipping/
  <Configuration>-smoke.txt
  <Configuration>-smoke.log (when UE logging is available)
  release-gate.json
```

`release-gate.json` records the executable SHA-256, package size, packaged
hostfxr/CoreCLR/managed paths, process result, and managed lifecycle result.
The launcher snapshots and byte-for-byte restores local `DefaultEngine.ini`
and `DefaultInput.ini` state.

## Dependency closure

The Win64 plugin is self-contained for execution. UBT stages these as NonUFS:

- `Binaries/Managed` and `Resources/Managed`;
- the engine-bundled .NET 10 `host/fxr` tree;
- the engine-bundled `shared/Microsoft.NETCore.App` tree.

The runtime resolves the packaged plugin-local .NET root first, then explicit
environment roots, and finally the engine development root. A packaged build
therefore does not require a system-wide .NET installation or the source UE
installation. Only the host and `Microsoft.NETCore.App` runtime are copied;
SDK packs, templates, ASP.NET, and WindowsDesktop frameworks are excluded.

The release command passes `-SkipZenStore` to Cook. IoStore remains enabled,
but staging reads the deterministic loose-cook manifest rather than depending
on a short-lived Zen server whose port can change between Cook and Stage.

## Current platform boundary

The checked release gate covers Win64 on the downloaded UE 5.8.1 distribution.
Other platforms still need platform-specific CoreCLR selection, staging rules,
and packaged smoke coverage before they can make the same self-contained claim.
