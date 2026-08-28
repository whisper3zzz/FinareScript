# Unreal SDK distribution

M6.3 packages UnifiedScript as a deterministic Windows SDK that can be used
without a UnifiedScript source checkout. The archive contains the source
plugin, framework-dependent managed runtime, CLI, onboarding and installation
tools, versioned schema examples, starter templates, and documentation. Native
Unreal binaries and the CoreCLR runtime are deliberately not copied from the
build machine: the target UE 5.8 engine builds the plugin and stages its own
matching .NET 10 runtime.

## Build an SDK archive

From a UnifiedScript source checkout:

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/package-unifiedscript-sdk.ps1 `
  -Version 0.1.0-m6.3-alpha.1 `
  -EngineRoot D:\UE_5.8
```

`-EngineRoot` may be omitted when `UNIFIEDSCRIPT_UE_ROOT` or the registered
`5.8` engine association resolves to an engine with the bundled .NET 10 SDK.
The result is written under `artifacts/sdk` unless `-OutputDirectory` is set.
Writing the same version twice from the same inputs produces the same ZIP
SHA-256: entries are sorted, timestamps are fixed, and every generated managed
binary is deterministic.

The archive has one top-level `UnifiedScript-SDK-<version>-win-x64` directory:

- `Plugins/UEUnifiedScript`: descriptor, `Source`, and `Resources`; no
  `Binaries` or `Intermediate` output;
- `Managed/Runtime`: `UnifiedScript.Core`, `UnifiedScript.Unreal`, and the
  dependency manifest needed by a consumer;
- `Tools/UnifiedScript.Cli`: the framework-dependent command-line tool and its
  managed dependencies;
- `Tools`: the SDK installer and the same project onboarding entry point used
  by the source repository;
- `Schemas`, `Templates`, and `Documentation`: the machine-readable
  compatibility policy, versioned examples, upgrade guide, and adoption material;
- `sdk-manifest.json`: compatibility and per-file integrity contract.

Packaging fails with `USSDK10xx`/`USSDK11xx`/`USSDK12xx` before producing an
archive when required payloads are absent, native build output leaked into the
plugin, or generated text contains a machine-specific repository/user path.

## Manifest and compatibility

`unifiedscript.sdk-manifest/v0.1` records the SDK version, target platform,
source revision, UE engine family, .NET target framework, every published ABI,
every consumed schema version, and the sorted size/SHA-256 of every payload
file. The compatibility policy is also bundled beside the CLI so upgrade plans
use the exact contract shipped by that SDK. The current package publishes:

- Unreal Engine family `5.8` and target `win-x64`;
- managed target `net10.0`;
- bootstrap/fast-member ABI v2, call-frame ABI v3, and event/shared/gameplay
  data ABI v4;
- compatibility policy v0.1, onboarding v0.1, reflection v0.4, API Contract
  and node/coverage v0.2, data-plane layout v0.1, and deployment v0.1 schemas.

Before replacing an SDK, run the packaged CLI's `upgrade-plan` command against
project-owned contracts and persistent authoring data. Direct reflection and API
Contract rewrites, generated-output rules, immutable chunk/ABI rebuild rules,
and atomic apply behavior are documented in
[Versioning and upgrades](VERSIONING_AND_UPGRADES.md).

The installer verifies every hash and rejects missing, duplicate, unsafe, or
unmanifested files. Packaged-mode onboarding independently checks the selected
engine family, managed target, and ABI contract before invoking Unreal.

## Install into a project

Extract the ZIP and run its installer:

```powershell
$sdk = 'D:\SDK\UnifiedScript-SDK-0.1.0-m6.3-alpha.1-win-x64'
powershell -ExecutionPolicy Bypass `
  -File "$sdk\Tools\install-unifiedscript-sdk.ps1" `
  -SdkRoot $sdk `
  -Project D:\Project\Game\Game.uproject
```

The `.uproject` must explicitly enable `UEUnifiedScript`. Installation is
transactional: the plugin is assembled under a temporary project-local path
and moved into place only after its runtime closure is verified. An existing
plugin is rejected by default. `-Force` preserves it as a timestamped sibling
backup before installing the new SDK rather than deleting it.

The machine-readable `unifiedscript.sdk-install-report/v0.1` is written under
`Saved/UnifiedScript/Install`. It records SDK/project/plugin resolution and the
backup path when an update replaced an existing plugin.

## Run project onboarding

After adding the project-owned `Config/UnifiedScript.onboarding.json`, invoke
the onboarding tool from the extracted SDK:

```powershell
powershell -ExecutionPolicy Bypass `
  -File "$sdk\Tools\setup-unreal-project.ps1" `
  -Project D:\Project\Game\Game.uproject `
  -EngineRoot D:\UE_5.8
```

The command automatically selects packaged SDK mode, copies the verified
managed runtime into the installed plugin, and then executes the same
reflection/contract/build/deployment/determinism pipeline documented in
[Unreal project onboarding](UNREAL_ONBOARDING.md). The generated onboarding
report identifies `sdkLayout: packaged`.

For a new consumer, `Tools/new-unifiedscript-project.ps1` renders all of those
project-owned inputs, creates the first C# Script Asset type and Authoring
Surface, and invokes onboarding in one operation. See the
[first-script walkthrough](UNREAL_FIRST_SCRIPT.md). The companion
`Tools/start-unifiedscript-authoring.ps1` launches the packaged transactional
Workbench for visual/C# edits and hot reload.

## Isolated acceptance

The M6.3.2 acceptance uses a Git-exported BlockBreaker directory that contains
no UnifiedScript plugin and has no access to repository-relative tooling. The
SDK installer and packaged-mode onboarding must pass there before a Win64
`BuildCookRun` archive is accepted. The runnable smoke gate requires exactly
300 managed ticks, one shot, one hit, two gameplay command completions, nonzero
fast/call-frame calls, zero legacy calls, and process exit code zero.
