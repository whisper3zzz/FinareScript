# Unreal SDK distribution

UnifiedScript 1.0 RC1 packages the product as a deterministic Windows SDK that can be used
without a UnifiedScript source checkout. The archive contains the source
plugin, framework-dependent managed runtime, CLI, onboarding and installation
tools, versioned schema examples, starter templates, and documentation. Native
Unreal binaries and the CoreCLR runtime are deliberately not copied from the
build machine: the target UE 5.8 engine builds the plugin and stages its own
matching .NET 10 runtime.

The packaged project-local plugin is intentionally a source plugin with
`Installed=false`. This tells UBT/UAT to compile it as part of the consumer
instead of treating it as a precompiled engine-installed plugin; the SDK still
ships no machine-specific Unreal binary.

## Build an SDK archive

From a UnifiedScript source checkout:

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/package-unifiedscript-sdk.ps1 `
  -Version 1.0.0-rc.1 `
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
  compatibility policy, versioned examples, and release-owned onboarding,
  authoring, API, debugging, performance, packaging, upgrade, and migration guides;
- `release-manifest.json`: the product version, release channel, exact verified
  target, artifact names, documentation catalog, acceptance gates, and known limits;
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

The SDK also bundles the data-plane extension v0.1 schema/catalog examples and
`generate-unreal-data-plane-extension.ps1`. A consumer can therefore generate
matched C++/C# workload rows and an authoring catalog without a UnifiedScript
source checkout; see [Generated data-plane extensions](DATA_PLANE_EXTENSIONS.md).

Before replacing an SDK, run the packaged CLI's `upgrade-plan` command against
project-owned contracts and persistent authoring data. Direct reflection and API
Contract rewrites, generated-output rules, immutable chunk/ABI rebuild rules,
and atomic apply behavior are documented in
[Versioning and upgrades](VERSIONING_AND_UPGRADES.md).

For support evidence, the packaged `diagnostics-bundle` command admits only
declared report schemas and emits a deterministic, redacted ZIP without source
or raw logs. See [Privacy-safe diagnostics bundles](DIAGNOSTICS_BUNDLES.md).

The packaged CLI also exposes `chunk-profile`, while
`Tools/measure-compiled-chunk-load.ps1` runs isolated process-first trials and
reports I/O, copy, verification, binary decode, CoreCLR load, and entry-method
JIT preparation separately. See [Compiled chunks](COMPILED_CHUNKS.md).

`Tools/measure-generation-prewarm.ps1` emits the packaged deterministic probe
from `benchmarks/unreal/generation-prewarm-probe.cs`, alternates disabled and
background-prewarm processes, and gates first-Tick, scheduling, teardown, and
completion-before-Tick behavior without requiring a source checkout.

The installer verifies every hash and rejects missing, duplicate, unsafe, or
unmanifested files. Packaged-mode onboarding independently checks the selected
engine family, managed target, and ABI contract before invoking Unreal.

## Adopt an existing project

Extract the ZIP and run the packaged CLI. This is the recommended first-use
path for both C++ and Blueprint-only projects:

```powershell
$sdk = 'D:\SDK\UnifiedScript-SDK-1.0.0-rc.1-win-x64'
& "$sdk\Tools\unifiedscript.cmd" `
  adopt D:\Project\Game\Game.uproject --sdk-root $sdk
```

`adopt` merges the plugin entry, installs the plugin, managed runtime, CLI,
templates, setup/deferred-build tools, and renders the first managed
project/script plus onboarding, Contract, and deployment inputs in one ownership
transaction. Use `--preview` to emit the
deterministic plan without changing files. A repeated Apply is a no-op; an
unowned or edited collision is reported as a conflict rather than overwritten.
Every changed file has a retained pre-transaction backup and exact content hash
under `Saved/UnifiedScript/Adoption`; `adopt <project> --rollback` restores the
latest transaction only when no post-adoption file has drifted.

For a Blueprint-only project, Adopt also transactionally creates a minimal
native Runtime module plus project Game and Editor targets and merges that
module into the `.uproject`. This is product-owned host scaffolding, not a new
C++ authoring requirement. It makes the project-local source plugin part of the
same native dependency graph used by normal Game builds and packaging. Existing
C++ projects retain their module/target layout. Rollback removes or restores the
owned host files exactly; Unreal-derived `Binaries`, `Intermediate`, and `Saved`
outputs are outside the adoption transaction and may be cleaned normally.

Run the read-only product diagnosis from the installed project-local entry point:

```powershell
$projectTools = 'D:\Project\Game\Plugins\UEUnifiedScript\Tools'
& "$projectTools\unifiedscript.cmd" `
  doctor D:\Project\Game\Game.uproject --engine-root D:\UE_5.8
```

`doctor` checks plugin enablement and payload, required managed runtime files,
onboarding schema, managed workspace, transaction-owned payload drift, engine
resolution, and the generated binding-coverage baseline. Warnings distinguish a
valid adopted project that has not run its first native onboarding build from a
broken installation.

The older `Tools/install-unifiedscript-sdk.ps1` and
`Tools/new-unifiedscript-project.ps1` commands remain compatibility/diagnostic
entry points. They expose installation and scaffolding as separate operations
and therefore are no longer the recommended golden path.

## Build and run project onboarding

After adoption creates `Config/UnifiedScript.onboarding.json`, invoke the
installed build/onboarding tool. The extracted SDK is no longer required:

```powershell
powershell -ExecutionPolicy Bypass `
  -File "$projectTools\setup-unreal-project.ps1" `
  -Project D:\Project\Game\Game.uproject `
  -EngineRoot D:\UE_5.8
```

The command automatically selects embedded mode, uses the installed managed
runtime in place, and executes the same reflection/contract/build/deployment/
determinism pipeline documented in [Unreal project onboarding](UNREAL_ONBOARDING.md).
The generated onboarding report identifies `sdkLayout: embedded`.
Invoking the copy under the extracted SDK remains supported for recovery and
reports `sdkLayout: packaged`; it verifies and reinstalls the packaged runtime
before running the same pipeline.

Setup compiles both the project Editor target and the configured Game target.
The latter must produce a target receipt before Setup succeeds, preventing a
later BuildCookRun from silently packaging a generic target without the plugin's
managed/CoreCLR runtime closure. Generated native thunk source remains managed
output: repeat Repair preserves the generation produced by Setup instead of
restoring the SDK's seed file.

Once the plugin loads, **Tools > UnifiedScript Project Setup** provides Preview,
Repair, Doctor, and **Save, Exit & Build** through the same project-local tools.
The build action waits until the Editor exits before touching native binaries.

See the [first-script walkthrough](UNREAL_FIRST_SCRIPT.md) for the complete
adopt/build/Play path. The companion
`Tools/start-unifiedscript-authoring.ps1` launches the packaged transactional
Workbench for visual/C# edits and hot reload.

## Isolated acceptance

The M6.3.2 acceptance uses a Git-exported BlockBreaker directory that contains
no UnifiedScript plugin and has no access to repository-relative tooling. The
SDK installer and packaged-mode onboarding must pass there before a Win64
`BuildCookRun` archive is accepted. The runnable smoke gate requires exactly
300 managed ticks, one shot, one hit, two gameplay command completions, nonzero
fast/call-frame calls, zero legacy calls, and process exit code zero.

M13.1A-2 adds the complementary first-use gate: a clean Blueprint-only fixture
is adopted from the released ZIP, then uses only its project-local Setup and CLI
for Apply, Validate, UE Automation, Editor/Game native builds, and no-change
Repair before ordinary Win64 BuildCookRun. Acceptance requires the executable,
managed gameplay DLL, compiled chunk, `UnifiedScript.Core` and
`UnifiedScript.Unreal`, `hostfxr`, and cooked Script Asset; Puerts and
project-authored JavaScript payloads must be absent. The final rollback must
restore the original `.uproject` hash and remove the four generated host source
files plus the installed transactional product surface.
