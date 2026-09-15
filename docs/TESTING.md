# Testing Finare

Run commands from the repository root. Choose checks for the behavior changed.
A test category narrows managed coverage; it does not prove UE execution or
release readiness. [AGENTS.md](../AGENTS.md) owns the working rules and
[CI.md](CI.md) documents the existing combined lanes.

## Test layers

| Layer | Location / entry | What it verifies |
| --- | --- | --- |
| Documentation and repository contracts | `tools/validate_project.py` | Local documentation structure/links and checked repository contracts |
| Python tooling | `tools/tests/` | Checker, selection and Harness behavior without UE |
| Managed behavior | `tests/Finare.Tests/` | Compiler, Graph, authoring, CoreCLR, managed UE contracts and delivery logic |
| Native Editor integration | Runtime/Editor module `Private/Tests/` under `HostProject/Plugins/Finare/Source/` | Real UObject/Blueprint/World behavior and managed/native integration |
| Consumer workflow | SDK/first-project drivers and `samples/External/Lyra/` | Installed product behavior in the relevant external project |
| Release and performance | Existing CI lanes and release-readiness driver | Exact package, platform, SDK and matched workload claims |

Existing managed class names and UE Automation names remain stable. Fixtures
stay beside their owning tests or in the existing shared fixture directories.
Do not move a fixture without checking project copy rules and native consumers.

## Documentation and Python tooling

Python 3.10+ and the standard library are sufficient:

```powershell
python -B -m unittest discover -s tools/tests -p "test_*.py"
python -B tools/validate_project.py
```

The documentation checker verifies structural requirements and local Markdown
targets. Wording changes should not require an exact phrase to remain in a
README or roadmap. Contract/schema/source checks retained by the repository
validator are structural checks, not executed gameplay assertions.

The tooling suite also checks project dependency cycles and prevents Roslyn or
compiler tooling from entering Runtime/Unreal transitively. CI policy cases
exercise skipped jobs, missing/stale reports and empty or failed test results.
The onboarding parser is tested with duplicated UE logs and critical failures.

Historical logs and generated output paths belong in code spans, with an owning
reproduction guide. Do not create a Markdown link to an artifact that a fresh
checkout cannot contain. External links are not fetched by local validation.

## Managed behavior suites

Use the SDK in [global.json](../global.json). Tests default to Debug because
project-loading fixtures resolve Debug project references. Build and run once:

```powershell
dotnet test tests/Finare.Tests/Finare.Tests.csproj -c Debug
```

If the system SDK is older than `global.json` permits, the supported UE install
also provides an SDK. Put that directory first on this shell's `PATH` and set
`DOTNET_ROOT` to it before running tests, so child builds use the same SDK:

```powershell
$sdkDirectory = 'D:\UE_5.8\Engine\Binaries\ThirdParty\DotNet\10.0\win-x64'
$env:PATH = "$sdkDirectory;$env:PATH"
$env:DOTNET_ROOT = $sdkDirectory
dotnet --version
```

Tests use xUnit `Category` traits. Each test class belongs to one of these suites;
partial class files share the classification on their primary declaration.

Runtime prewarm functional tests control their clock while exercising real
dependency/cache and codec work. Separate cases advance that clock to verify
budget expiration. This keeps functional correctness independent of host load
without relaxing production budgets or removing cache assertions.

The two authoring HTTP server test classes share an xUnit collection so their
temporary port discovery and real listeners do not compete with each other.
Other test classes retain their normal parallel execution.

Direct `AuthoringSessionTests` exercise stale previews and idempotent native
publication confirmations/cancellations without a listener. The HTTP tests
continue to cover the transport mapping; native application still requires its
Editor checks. See [CI acceptance](CI.md#meaning-of-ci-acceptance) for which
layers are required by a change and how missing evidence is reported.

Source-only entry regressions must execute an emitted assembly or compiled
chunk and assert the result. A passing profile inspection alone does not prove
the method was compiled, exported or dispatched. Cover mixed visual/source-only
calls, declaration identity across body representation changes, inherited UE
routes, and rejection of graph body edits. Negative cases must keep explicit
`[Visual]` violations, invalid C# and invalid UE declarations fatal. Source-only
method maps may contain real PDB sequence points, but must not invent graph
statement maps or graph breakpoint capabilities.

Inspect the current class inventory without starting .NET or UE:

```powershell
python -B tools/test_suites.py
python -B tools/test_suites.py --json
```

The inventory reads traits from test source; class counts are not executed test
counts. `--suite Authoring` prints the corresponding native `dotnet test` command.

| Category | Behavior | Typical entrypoints |
| --- | --- | --- |
| `Compiler` | Parsing, lowering, identities, semantic/source patches, declaration and serialization contracts | `CSharpScriptAssetLowererTests`, `CSharpSourcePatchApplierTests` |
| `Graph` | Projection, control flow, graph layout and graph serialization | `HirGraphProjectorTests`, `GraphControlFlowAnalyzerTests` |
| `Authoring` | User edits, project/HTTP authoring, ownership, semantic review and proposals | `ProjectAuthoringTests`, `CoreClrAuthoringHttpServerTests` |
| `Runtime` | Execution, chunks, source maps, debug and generation/reload behavior | `CoreClrExecutionHostTests`, `CoreClrReloadDomainTests` |
| `Unreal` | Managed UE bindings, ABI, state, callbacks, object lifetime, class dispatch and networking | `UnrealScriptObjectModelTests`, `InProcessUnrealNativeInvocationTransportTests` |
| `Delivery` | Adoption, compatibility, guidance, migration, diagnostics and workload policy | `UnrealProjectAdoptionTests`, `ProductGuidanceTests` |

Run one suite or a combination after a current build:

```powershell
dotnet test tests/Finare.Tests/Finare.Tests.csproj --no-build --filter "Category=Authoring"
dotnet test tests/Finare.Tests/Finare.Tests.csproj --no-build --filter "Category=Runtime|Category=Unreal"
```

For a specific regression, use its stable class or method name:

```powershell
dotnet test tests/Finare.Tests/Finare.Tests.csproj --filter "FullyQualifiedName~UnrealScriptObjectModelTests"
dotnet test tests/Finare.Tests/Finare.Tests.csproj --filter "FullyQualifiedName~NestedLifecycleEventPreservesOuterNativeState"
```

Omit `--no-build` after changing source, fixtures or test classification.
Check that the filter discovered and executed the intended cases. Zero tests
is not a pass. The full unfiltered managed suite remains the regression gate.
Use `--logger trx --results-directory <directory>` when a retained test report
is needed; do not replace a failure with a narrower passing result.

## Select by user workflow

These are coverage starting points, not a second list of mandatory gates.
Cross-module changes often require more than one managed category.

| Changed behavior | Managed starting point | Native / installed follow-through |
| --- | --- | --- |
| C#/Graph edit reaches the running program | `ProjectAuthoringTests`, source-patch and emitter tests | Project refresh and native authoring/publication tests |
| Event updates survive Tick or reload | `UnrealScriptObjectModelTests`, event transport and reload tests | Object registry/lifecycle cases; affected Lyra health sequence |
| Cached callback references remain valid | Transport object-reference tests and generation/lifetime tests | `Finare.Unreal.ObjectRegistry.UObjectReferenceTransport` and actual GC/World teardown |
| Parent/default/reference edit | Class-base patch, inherited state and ownership tests | Parent-change, inherited-class and `Finare.Unreal.Editor.Authoring.NativePublicationWorlds` cases |
| Pure C# Actor + Health/Mover composition | Real fixture build and declaration emission | `./tools/test-class-composition.ps1`: saved classes, exposed overrides, component references and save-triggered batch reload; [fixture scope](../tests/Finare.Tests/Fixtures/ClassComposition/README.md) |
| Installation preserves a user's project | `UnrealProjectAdoptionTests`, compatibility and guidance tests | Clean SDK install/upgrade/rollback in an authorized consumer |
| Packaged gameplay uses its own runtime | Chunk and deployment contracts | Game build/cook/archive plus execution using the staged chunk and bundled hostfxr |

Observe exact behavior: state values, callback count, retained/released objects,
selected behavior owner, rollback and final cleanup. A generated declaration,
JSON receipt or process exit alone does not prove the whole workflow.

## Native Editor checks

Use UE 5.8.1 and the configured onboarding workflow. For the combined native
build or Automation lane, choose a fresh output directory:

```powershell
& tools/run-ci-lane.ps1 -Lane ue-build -EngineRoot D:\UE_5.8 `
    -OutputDirectory artifacts/ci/local-editor-build
& tools/run-ci-lane.ps1 -Lane unreal-automation -EngineRoot D:\UE_5.8 `
    -OutputDirectory artifacts/ci/local-editor-automation
```

These are broad lanes. For focused checks, use the relevant existing test driver
or onboarding configuration's `validation.automationFilter` and
`minimumAutomationTests`. Preserve a user's configuration; do not reduce checked
in acceptance thresholds to make a run pass. CMD/NullRHI is appropriate for
repeatable headless behavior; rendered interaction needs a different check.

The class-composition plan records an existing
`Finare.Unreal.Editor.AuthoringSurface.Workbench` fixture failure: a one-asset
assumption disagrees with the multi-asset Demo. It is not waived or fixed by
test classification. See the [owning record](plans/2026-09-09-class-composition.md)
before interpreting a broader run containing that case.

## Unified UObject and property behavior

For the unified UObject model and native reflected properties, run the focused
Editor checks after staging the Demo assembly and building FinareHostEditor:

```powershell
& tools/test-class-composition.ps1 -SkipBuild -SkipPrepare -Tests @(
    'Finare.Unreal.ObjectModel.NativeTypesAndProperties',
    'Finare.Unreal.ObjectModel.LiveProperties',
    'Finare.Unreal.Objects.Factory',
    'Finare.Unreal.Objects.ManagedLifecycle',
    'Finare.Unreal.ScriptClass.NativeReceiverLifecycle'
)
```

These exercise typed native properties, immediate C# property access, detached
container snapshots, reload retention, and ordinary SceneComponent lifecycle.
The Object checks cover ordinary native creation, public C# factory calls,
lazy activation, successful and failed reload, failed-start retries, weak
receiver GC and World teardown. They establish Editor behavior, not Cook or
packaged acceptance.

## Installed Lyra regression

Follow [the sample guide](../samples/External/Lyra/README.md) using an authorized
independent Lyra copy and the installed SDK. The health test covers production
damage/heal, reload, pawn replacement, stop/restart and teardown. It requires
rendered offscreen PIE; NullRHI cannot execute that UMG/Slate path.

The runner checks a fresh behavior report, process exit and Ensure/fatal/GC
errors. Audio and final packaged gameplay are outside the recorded acceptance.
The remaining asset/async failure coverage is tracked in the
[pilot plan](plans/2026-09-11-lyra-pilot.md), not silently included in its pass.
Do not run this external workflow for a documentation-only change.

## Release evidence and test maintenance

Use [release readiness](RELEASE_READINESS.md) for a formal release and
[matched workloads](PERFORMANCE_WORKLOAD_SUITE.md) for comparative performance.
The [Harness](HARNESS.md) can combine existing checks; it is optional for focused
work. History retains the revision and scope of each earlier acceptance.

When adding or changing tests:

- Keep cases near their owning behavior and give each managed class one category.
- Assert public outcomes and failure preservation; avoid testing method spelling
  or document prose when a behavioral or structural contract is available.
- Keep negative cases for ownership, stale inputs and rollback. Do not remove
  them because a happy-path demo passes.
- Diagnose a failing fixture or product behavior before changing expectations.
- Distinguish passed, failed and not-run coverage in the handoff. Documentation
  changes alone do not turn historical reports into new runs.
