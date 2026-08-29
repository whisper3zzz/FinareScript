# Unreal Win64 release gate

## Run

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File tools/package-unreal-release.ps1 `
  -EngineRoot D:\UE_5.8
```

The default invocation builds both `Development` and `Shipping`. A focused run
can pass `-Configurations Development` or `-Configurations Shipping`.

The complete UE 5.8.1 support declaration, including Editor Automation and an
SDK-installed consumer project, is produced by:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-unreal-compatibility-matrix.ps1 `
  -EngineRoot D:\UE_5.8
```

See `docs/UNREAL_COMPATIBILITY_MATRIX.md` for the declared and explicitly
unsupported rows.

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

`release-gate.json` uses schema `unifiedscript.unreal-release-gate/v0.2` and
records the exact engine version/changelist, every Build/Cook/Stage/Pak/Archive
phase, executable SHA-256, package/runtime sizes, Pak/IoStore counts, packaged
hostfxr/hostpolicy/CoreCLR/managed paths, process result, and managed lifecycle
result. The launcher snapshots and byte-for-byte restores the selected
project's `DefaultEngine.ini` and `DefaultInput.ini` state.

`-Project <path>` applies the same gate to another compatible host project.
`-SkipManagedBuild` is reserved for a caller such as the compatibility matrix
that has already installed/staged a verified managed runtime and Demo chunk.
The script rejects an engine whose exact version differs from
`-ExpectedEngineVersion` (5.8.1 by default).

## Dependency closure

The Win64 plugin is self-contained for execution. UBT stages these as NonUFS:

- `Binaries/Managed` and `Resources/Managed`;
- the engine-bundled .NET 10 `host/fxr` tree;
- the engine-bundled `shared/Microsoft.NETCore.App` tree.

The gate requires `hostfxr.dll`, `hostpolicy.dll`, `coreclr.dll`,
`System.Private.CoreLib.dll`, and `System.Runtime.dll`, then launches the
package with both external .NET-root environment variables pointing at missing
directories. A pass therefore proves that startup selected the plugin-local
runtime instead of silently using developer-machine state.

The verifier recursively enumerates files before applying the exact filename
match. This is deliberate: Windows PowerShell 5.1 applies `Get-ChildItem
-Filter` before recursive descent and would otherwise skip the version
directory. It polls for at most 15 seconds to tolerate archive materialization,
but still fails if the closure never becomes complete.

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
