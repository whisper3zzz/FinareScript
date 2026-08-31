# UnifiedScript product roadmap

This document is the canonical execution queue for UnifiedScript. It records
both the evidence-backed foundation and the ordered path from the current
BlockBreaker package to a production alternative to Puerts. Milestone documents
explain individual contracts; this file decides what is worked on next.

## Product north star

UnifiedScript should provide a practical Unreal gameplay scripting product with:

- Puerts-grade first-use and daily-development ergonomics: install into an
  existing project, create a managed gameplay class, call ordinary
  Blueprint-visible Unreal APIs, press Play, reload, and package without first
  hand-authoring binding metadata;
- one semantic program that can be edited as restricted C# or as a Blueprint-like
  visual graph without maintaining two implementations;
- generated, type-safe Unreal bindings instead of project-specific handwritten
  facades;
- World-safe lifetime, reload, event, async, and packaged-build behavior;
- fast scalar calls plus shared snapshots and batched command/query paths for
  frame-critical gameplay;
- reproducible evidence that compares complete workloads with Puerts rather than
  relying on one favorable API microbenchmark.

The goal is not source-level JavaScript compatibility. The product must first be
as easy to adopt and use for ordinary Unreal gameplay work, then win by combining
stronger authoring, safer generated contracts, and lower end-to-end gameplay
cost. API Contracts, coverage reports, and deployment manifests remain important
implementation and release controls, but they must not be first-run chores for
the gameplay developer.

## How this roadmap is executed

1. **Follow the cursor.** Work starts from the first unchecked slice in the
   active milestone below. An older unchecked research item does not preempt the
   cursor unless it blocks that slice.
2. **Finish vertical slices.** A slice includes implementation, generated
   artifacts, proportional tests, user-facing documentation, and reproducible
   evidence. A compile alone is not completion.
3. **Keep the baseline green.** Every product slice must preserve deterministic
   .NET tests, repository validation, the relevant UE build/Automation gates,
   and any affected packaged or performance gate.
4. **Update this file with the code.** Check off only verified work, record a
   short evidence reference, and advance the cursor to one next slice.
5. **Control scope changes.** Crash, data-loss, ABI, and active-engine
   compatibility failures may interrupt the queue. Other ideas go into the
   later milestone that owns them.

When the user asks to "continue" or "continue iterating" without choosing a
different target, it means: implement the first unchecked slice under the
current cursor, validate it, and advance this document.

## Current execution cursor

Last refreshed: **2026-09-01**

- Active milestone: **M13.2 in progress — Puerts-grade daily authoring and one C#/Graph workflow**
- Completed tranches: **M13.1A-0 packaged transactional `adopt`/`doctor` CLI (`9fe7f70`); M13.1A-1 project-local Editor Setup/Doctor/Build surface; M13.1A-2 clean Blueprint-only native host and cook closure; M13.1B-1 first source-use-to-generated-surface vertical; M13.1B-2 ordinary-source/signature tranche; M13.1B-2 bounded native reflection-discovery tranche; M13.1B-2b exact Blueprint discovery and generated-surface lock; M13.1C-0 attachable Script Class foundation; M13.1C-1a first Build; M13.1C-1b exact edit/reload loop; M13.1C-1c unified diagnostics and recovery; M13.1C-1d packaged Actor/Component execution; M13.1C-1e headless workflow-contract closure; M13.2A-1 canonical Script Asset entry; M13.2A-2a complete-class rename/duplicate transaction; M13.2A-2b1 guarded safe delete; M13.2A-2b2a Roslyn reference plan; M13.2A-2b2b persisted crash recovery; M13.2A-2b2c UE reference and generic duplicate closure; M13.2A-2b2d packaged lifecycle replay; M13.2B dependency-aware Play; M13.2C diagnostics and IDE**
- Current tranche: **M13.2D — edit/reload**
- Current implementation state: **diagnostic records now preserve stage, source/member/graph identity, package path, World host, runtime instance, and generation across authoring, build, runtime, and package domains. UE Message Log exposes exact source, member, graph, package, snapshot, and bounded recovery actions without automatically replaying unsafe work. Adoption also emits a project-local `.slnx`, C#/documentation workspace, typed API links, managed and full Build/Validate tasks, CoreCLR attach configuration, and a machine-readable IDE manifest. The next gap is to turn the existing identity-safe reload primitives into the default transactional edit/reload path for every supported editor state**
- Current verification state: **the UE 5.8.1 native build, refreshed 25/25 `UnifiedScript.Unreal` CMD Automation, 648/648 managed tests, 115/115 repository invariants, 3/3 project-template fixtures, and 2/2 independent packaged-adoption fixtures pass. SDK `61cda211e1da53dfb8855a90d2faa29c98cd24c6a7511123e117059658e18669` installed into a fresh Blueprint-only native consumer: cold Apply took 170,096 ms; immediate no-change Apply executed 0 and skipped 13 stages in 1,497 ms; generated IDE solution/workspace/manifest and documentation resolved without repository paths; ValidateOnly, Editor/Game build, CMD Automation, Win64 Cook/Package, Doctor, zero-change Repair, and exact rollback all passed in 237,535 ms. Evidence is at `artifacts/m13-2c/packaged-native/packaged-native-onboarding-report.json`**
- Current UI finding: **the first exploratory interactive pass exposed the temporary `NewBlueprint` identity route, duplicate entries, focus stealing, and incomplete-creation cleanup risk. The P0 implementation defects are now repaired and covered by build/headless contracts; manual Editor timing is deliberately deferred to M13.2G usability measurement and is not an M13.1 correctness gate**
- Next acceptance: **a compatible Script Class edit reloads through one transaction, preserves declared state and exact runtime identity across supported editor states, and a native/schema-breaking edit is rejected before publication with an actionable restart requirement**
- Next user-visible slice: **edit a Script Class, run the same Build/Play action, and observe either a state-preserving live reload or one explicit restart-required result without manually invoking generators or deployment commands**
- External acceptance candidate: **`D:\Project\LyraStarterGame` (deferred until the golden path works; read-only discovery only, not selected or modified yet)**
- Primary engine gate: **Unreal Engine 5.8.1 installed build at `D:\UE_5.8`**
- Product proof: **self-contained Win64 BlockBreaker demo**
- Competitive objective: **close Puerts' practical Unreal coverage before claiming a general replacement**

## Puerts-grade usability closure scorecard

The immediate product target is workflow parity, not JavaScript compatibility:
an Unreal gameplay developer should be able to install UnifiedScript, create a
class, use ordinary UE APIs, edit, reload, diagnose, and package without knowing
the generator topology. Backend breadth or benchmark wins do not close a row
until the same capability is reachable through the shipped product workflow.

| Workflow | Confirmed current state | Parity gate | Owner |
| --- | --- | --- | --- |
| Install and adopt | Packaged SDK adoption, project-local Setup/Doctor/Repair, deterministic no-change rerun, and rollback are verified | One documented plugin/SDK flow works outside this repository, reports every owned file, and never requires repository paths | M13.1A baseline; replayed by every release gate |
| Create and first Play | Actor and ActorComponent Script Classes are persistent, selectable/attachable, connected to one `Build, Restart & Continue` action, and protected by a headless name-first/rollback contract | A clean installed-product command replay reaches first Play without invoking reflection, binding, thunk, asset-sync, or deployment commands | **M13.1C complete; UI action budget in M13.2G** |
| Edit and reload | The exact created Script Asset opens through Project Setup; installed Workbench publication reaches matching live or stopped instances; compatible state is preserved and breaking state returns `restartRequired` without mutating the active generation | Command-line acceptance proves exact identity, compatible state preservation, and atomic restart-required rejection; the installed UI exposes the same transaction | **M13.1C complete; daily UX in M13.2** |
| Diagnostics and recovery | Compile, generation, coverage, stale-surface, reload, runtime, setup, and package failures share one UE Message Log with source/member/graph/package actions, bounded Retry/Repair/Restart guidance, a privacy-safe bundle, and generated project-local IDE navigation/debug configuration | One UE-facing result links to source/member/graph/package, distinguishes repair from restart, exports one copyable diagnostic bundle, and the installed IDE workspace works without repository knowledge | **M13.2C complete** |
| Unreal API use | First-use source discovery, exact Blueprint discovery, type-safe bindings, surface locks, and coverage reports are verified for the bounded matrix | Ordinary Blueprint-visible APIs used by a representative project generate automatically; unsupported shapes fail with actionable coverage diagnostics rather than handwritten facades | M13.3, M13.4, M13.5 |
| Unreal class-model parity | Actor and ActorComponent creation plus one property/function/lifecycle path are verified | Reviewed UObject, Actor, ActorComponent, and BlueprintFunctionLibrary classes support defaults, events, input, delegates, interfaces, inheritance, and stable asset identity | M13.3 |
| C# and visual authoring | Semantic C#/Graph round-trip and live semantic debugging foundations are verified separately | The same Script Class opens in C# or Graph, preserves IDs/comments, previews destructive semantic changes, and executes equivalently | M13.2 |
| IDE and debugging | Compiler/runtime infrastructure exists; a packaged first-project IDE/debug workflow is not accepted | Generated project references, autocomplete/navigation, and one documented launch/attach path work from the installed product | M13.2 |
| Package and ship | A fresh SDK consumer packages and executes exact Actor and Component generated classes from `.uschunk`; post-smoke ValidateOnly, Doctor, zero-change Repair, and exact user-file-preserving rollback pass | Actor and Component execute in the final packaged build; no-change rebuild, Doctor, and rollback remain clean | **M13.1C-1d complete** |
| Migrate real gameplay | BlockBreaker has a complete UnifiedScript/Puerts evidence suite, but it is not yet a generic migration workflow | One external production-shaped slice reaches observable parity and removes its old runtime owner without project-specific facades | M13.4 |

M13.1C is closed by reproducible command-line build, Automation, managed,
package, no-change, and rollback evidence. Interactive Editor action/time
measurement belongs to M13.2G and does not block correctness work. The ordered
product queue is:

1. **M13.1C-1b (complete):** exact Script Class edit, compatible transactional
   reload, preserved declared state, and atomic restart-required rejection.
2. **M13.1C-1c (complete):** one UE diagnostics surface with navigation and
   support bundle.
3. **M13.1C-1d (complete):** final packaged Actor/Component execution from a
   clean SDK, followed by no-change validation and user-file-preserving rollback.
4. **M13.1C-1e (complete):** headless name-first, single-entry, focus-order, and
   transactional rollback contracts, followed by UE 5.8.1 full Automation.
5. **M13.2 (current):** close the Puerts-grade daily workflow first: one editor-native
   create/open/build/Play/reload path, IDE setup, and default C#/Graph authoring.
6. **M13.3:** expand the now-usable Script Class workflow through the reviewed UE
   class model, Blueprint inheritance/events, input, delegates, and stable asset
   operations.
7. **M13.4 -> M13.6:** external migration proof, ecosystem/network closure,
   distribution/platform support, and remaining measured performance losses.

### Puerts-grade workflow budgets

"As easy as Puerts" is accepted through shipped workflow contracts, not backend
feature count. Headless build/runtime gates remain mandatory. M13.2G may add
human action/time budgets as a separate usability regression signal without
turning interactive Editor replay into a correctness prerequisite.

| User journey | Initial budget | Hard failure condition |
| --- | --- | --- |
| Adopt and open | zero repository commands and zero manual edits to generated/configuration files after choosing the project | the user must know generator, Contract, thunk, deployment, or repository paths |
| Create to first Play | at most 8 deliberate actions, at most one clearly explained native-build restart, and one stable final class identity | a temporary asset name leaks into source, deployment, generated assets, or runtime identity; duplicate product entries offer different behavior |
| Compatible edit to reload | at most 3 deliberate actions, no Editor restart, and declared instance state preserved | the old generation is partially mutated, the user must manually rebuild bindings, or the failure does not say what to do next |
| First use of a supported UE API | write the ordinary typed call and use the same Build/Reload action; no Contract or facade edits | the API is supported by reflection but still requires handwritten bridge metadata |
| Diagnose and recover | one UE-facing error with source/member navigation and exactly one primary Retry, Repair, or Restart action | raw generator/runtime logs are the only explanation or recovery requires repository commands |
| Package | use the project's normal package workflow with no manual managed payload copy | Editor success hides a missing cooked class, chunk, host, or generated surface |

Every accepted headless report must include wall-clock time, failures, repair
actions, final asset/source/type identity, and exact build/runtime evidence.
Optional human walkthrough reports additionally record deliberate action count
and prompts. A failed exploratory pass remains defect evidence, not a release
verdict by itself.

## Verified baseline

The forward plan starts from this locally reproduced baseline. Results remain
machine-local unless an artifact explicitly says otherwise.

| Gate | Verified result |
| --- | --- |
| Runtime source baseline | `5be7dde` (`fix: remove CI snapshots with long paths`) |
| UnifiedScript comparison source | `d2f1fec` (clean frozen BlockBreaker UnifiedScript acceptance revision) |
| Puerts comparison source | `be399cb` (`perf: register benchmark world context`) |
| Managed tests | 648 / 648 passed |
| Repository validator | 115 / 115 passed |
| M13.1B-1 automatic source surface | 6 / 6 focused regressions plus 634 / 634 full managed tests and 97 / 97 repository invariants passed; fresh packaged UE 5.8.1 consumer passed source expansion, Editor/Game build, ValidateOnly, Automation, Win64 BuildCookRun, no-change Repair, Doctor, and rollback in 234,215 ms; report at `artifacts/a/b1/packaged-native-onboarding-report.json`; accepted SDK SHA-256 `e217472bffd951893d45f6e21fc44f285e92f9e46b0eb2fe032a79daa8005997` |
| M13.1B-2 ordinary source/signature matrix | 9 / 9 focused regressions plus 637 / 637 full managed tests and 98 / 98 repository invariants passed; ordinary imports, semantic collision safety, exact overload selection, delegate projection, and the representative Blueprint-visible signature matrix use the real generators; a fresh packaged UE 5.8.1 consumer passed source expansion, Editor/Game build, ValidateOnly, Automation, Win64 BuildCookRun, no-change Repair, Doctor, and rollback in 278,254 ms; report at `artifacts/a/b3/packaged-native-onboarding-report.json`; accepted SDK SHA-256 `eb205ca8313c068ff2e822e930a79e52e7a189feffdabc658bd33e7dbe97210f` |
| M13.1B-2 bounded native reflection discovery | 10 / 10 focused regressions plus 638 / 638 full managed tests and 99 / 99 repository invariants passed; the clean template produced only `Actor`/`Pawn` source hints, UE uniquely added one source-discovered Engine class, and the resulting `Pawn.GetController` plus `Actor.GetActorLocation` surface passed Editor/Game build, ValidateOnly byte locks, Automation, Win64 BuildCookRun, no-change Repair, Doctor, and rollback in 250,077 ms; report at `artifacts/a/b4/packaged-native-onboarding-report.json`; accepted SDK SHA-256 `97995eea92e45af2b37f076c1a9ba89d11128be718125358a19ac819063f2cc8` |
| M13.1B-2b Blueprint discovery and surface lock | 12 / 12 focused regressions plus 640 / 640 full managed tests and 100 / 100 repository invariants passed; full UE 5.8.1 Automation passed 21 / 21 including exact transient Blueprint generated-class discovery; a fresh packaged consumer passed Apply, 3,000-byte exact per-type lock generation, ValidateOnly frozen verification, Editor/Game builds, two project Setup Automation tests, Win64 BuildCookRun, zero-change Repair, Doctor, and rollback in 279,057 ms; report at `artifacts/a/b5/packaged-native-onboarding-report.json`; accepted SDK SHA-256 `01d30c2066570d90b21a6be0adaa602a8fb9d99bd219cabf043df48b651d1b71` |
| M13.1C-0 attachable Script Class foundation | 5 new managed identity regressions plus 645 / 645 full managed tests, 102 / 102 repository invariants, UE 5.8.1 build, and 22 / 22 normal Automation tests passed; isolated creation and reload processes proved persistent Actor/ActorComponent identity, `BeginPlay`/`Tick`/`EndPlay`, World teardown, six-output deterministic ValidateOnly, and Win64 Cook of both proxies and both exact Script Asset sidecars in 210,632 ms; report at `HostProject/Saved/UnifiedScript/ScriptClassGoldenPath/20260831-170500/script-class-golden-path-report.json` |
| M13.1C-1 first-Play connection | first Build, state-safe edit/reload, unified diagnostics, packaged Actor/Component execution, and the headless creation/rollback workflow contract are verified. UE 5.8.1 clean-consumer Editor/Game builds, 645/645 managed tests, and the packaged execution/no-change/rollback proof pass; evidence remains at `artifacts/m13-1c1d/acceptance-preflight-2/packaged-native-onboarding-report.json` and `artifacts/m13-1c1e/automation/unifiedscript-unreal-full-rerun.log` |
| M13.2A-1 native Script Asset entry | UE 5.8.1 build, 24/24 `UnrealEditor-Cmd` + NullRHI Automation tests, 645/645 managed tests, and 107/107 repository invariants pass. Normal Script Asset opening reaches the shared Authoring tab; exact source/generated-class/repair actions are registered; unsafe bare rename/duplicate are rejected. Evidence is at `artifacts/m13-2a/automation/script-asset-workflow.log` and `artifacts/m13-2a/automation/unifiedscript-unreal-full.log` |
| M13.2A-2a complete-class rename/duplicate | UE 5.8.1 build, 4/4 focused Script workflow tests, 25/25 full CMD-mode Automation tests, 645/645 managed tests, and 108/108 repository invariants pass. Rename preserves `ClassId`; duplicate creates a new one; both update the complete proxy/sidecar/source/deployment/first-Play identity and prove byte-exact rollback after injected post-publication failure. Evidence is at `artifacts/m13-2a2/automation/script-workflow-focused.log` and `artifacts/m13-2a2/automation/unifiedscript-unreal-final.log` |
| M13.2A-2b1 guarded safe delete | UE 5.8.1 build, focused CMD-mode `ScriptClass.AssetOperations`, refreshed 25/25 full Automation, 645/645 managed tests, and 109/109 repository invariants pass. Unreferenced complete classes delete through a reversible tombstone; managed referencers are rejected; injected post-publication failure restores source/deployment/request bytes and package paths; bare generated Blueprint destructive actions are rejected. Evidence is at `artifacts/m13-2a2/automation/script-class-delete-focused-final.log` and `artifacts/m13-2a2/automation/unifiedscript-unreal-delete-final.log` |
| M13.2A-2b2a Roslyn reference plan | UE 5.8.1 build, focused CMD-mode `ScriptClass.AssetOperations`, refreshed 25/25 full Automation, 648/648 managed tests, and 110/110 repository invariants pass. Rename rewrites only Roslyn-bound declarations and cross-file type uses while preserving non-semantic text and source encoding; delete reports exact external semantic uses; cross-file conflicts and injected failure prove byte-exact rollback. Evidence is at `artifacts/m13-2a2/automation/script-class-semantic-reference-focused-3.log` and `artifacts/m13-2a2/automation/unifiedscript-unreal-semantic-reference-final.log` |
| M13.2A-2b2b persisted crash recovery | UE 5.8.1 build, focused CMD-mode `ScriptClass.AssetOperations`, refreshed 25/25 full Automation, 648/648 managed tests, and 111/111 repository invariants pass. Every operation writes bounded package/external preimages and explicit phases; live-owner receipts are deferred; orphan recovery archives interrupted bytes before exact restore; success and rollback close their receipts. Evidence is at `artifacts/m13-2a2/automation/script-class-journal-focused-2.log` and `artifacts/m13-2a2/automation/unifiedscript-unreal-journal-final.log` |
| M13.2A-2b2c UE reference and duplicate closure | UE 5.8.1 build, focused UE-reference/rollback, dirty-referencer rejection, Blueprint Asset Definition, and direct-duplicate cleanup CMD tests, refreshed 25/25 full Automation, 648/648 managed tests, and 112/112 repository invariants pass. Rename snapshots and rewrites external UE referencers, deletes old redirectors, verifies dependency closure, and reverses the rewrite on failure. Script Class proxies use `UUnifiedScriptBlueprint`; Content Browser rejects bare rename/duplicate and the Asset Registry guard removes direct Blueprint-only copies. Evidence is at `artifacts/m13-2a2/automation/script-class-ue-reference-duplicate-final.log`, `artifacts/m13-2a2/automation/script-blueprint-definition-focused.log`, and `artifacts/m13-2a2/automation/unifiedscript-unreal-ue-reference-duplicate-final-3.log` |
| M13.2A-2b2d packaged lifecycle replay | Factory create now writes the same bounded recovery receipt as rename/duplicate/delete, saves both UE packages before commit, and restores exact external/package preimages on failure. The source CMD runner and a fresh packaged-SDK consumer replay all four success, injected rollback, and persisted recovery paths with byte-exact baseline restoration and zero pending receipts. The clean consumer also passes first-Play create/reload, Win64 Cook/Package, Validate, Doctor, no-change Repair, and SDK rollback in 569,454 ms; refreshed 25/25 CMD Automation, 648/648 managed tests, and 113/113 repository invariants pass. Evidence is at `artifacts/m13-2a2d/clean-consumer-create-recovery-fix/packaged-native-onboarding-report.json` and `artifacts/m13-2a2d/clean-consumer-create-recovery-fix/script-class-lifecycle-replay/packaged-script-class-lifecycle-report.json` |
| M13.2B dependency-aware Play | One persistent graph fingerprints direct inputs, dependency outputs, and tracked outputs for 13 Build/Play stages, explains every run/skip, propagates only changed output bytes, and commits state only after complete success. A clean packaged consumer cold Apply took 166,899 ms; immediate no-change Apply took 1,506 ms with 0 executed and 13 skipped; ValidateOnly, Cook/Package, Doctor, Repair, and rollback also passed. Windows PowerShell 5.1 graph tests, 648/648 managed tests, and 114/114 repository invariants pass. Evidence is at `artifacts/m13-2b1/packaged-incremental-output-isolation-fix/packaged-native-onboarding-report.json`; SDK SHA-256 `d233f3bc6ed23856da795e0be85e301cbc9479d17f56a42300763213c21e341e` |
| M13.2C diagnostics and IDE | Diagnostic schema v0.2 carries stage, source/member/graph, package, World host, runtime instance, generation, snapshot, and bounded recovery context into UE Message Log. Packaged adoption emits a project-local `.slnx`, C#/documentation workspace, Build/Validate tasks, CoreCLR attach configuration, and IDE manifest with no repository dependency. UE 5.8.1 build, 25/25 CMD Automation, 648/648 managed tests, 115/115 repository invariants, 3/3 template fixtures, 2/2 packaged-adoption fixtures, and the clean native consumer all pass. Evidence is at `artifacts/m13-2c/packaged-native/packaged-native-onboarding-report.json`; SDK SHA-256 `61cda211e1da53dfb8855a90d2faa29c98cd24c6a7511123e117059658e18669` |
| Packaged project adoption | two clean fixtures installed 169 owned files, built managed scripts, passed project-local Doctor, reran project-local Repair with 169 unchanged/zero changed, and rolled back all 169 files; report at `artifacts/adoption/m13-1a1-final-20260831/packaged-adoption-report.json` |
| Blueprint-only native onboarding | clean packaged-SDK consumer passed project-local Apply/Validate, Editor/Game builds, Automation, Win64 BuildCookRun, zero-change Repair, Doctor, and exact rollback in 220,036 ms; report at `artifacts/adoption-native/m13-1a2-final-native-20260831/packaged-native-onboarding-report.json` |
| UE 5.8.1 native build | passed |
| `UnifiedScript.Unreal` Automation | 25 / 25 passed through `UnrealEditor -cmd` + NullRHI, including persisted receipt recovery/live-owner deferral/archive, semantic cross-file complete-class rename/delete, duplicate, referencer rejection, direct-operation guards and byte-exact rollback, the native Script Asset open/action contract, name-first Script Class factory/menu contracts, exact transient Blueprint generated-class reflection, Actor/ActorComponent creation and save, project-local Setup, bounded crash breadcrumbs, failure containment, deterministic World shutdown, workload collectors, gameplay-profiler attribution, Authoring Surface, live World semantic pause/step/reload/detach, repeated success/failure hot-reload leak coverage, Puerts/UnifiedScript lifecycle ownership arbitration, and generated data-plane extension registry coverage |
| M10.2 generated-channel profile | 1,024 rows x 3 median samples: container binding 25,371.973 ns/row, generated snapshot 1.270 ns/row (ratio 0.000050), buffered events 0.586 ns/row, zero managed allocation |
| Representative workload suite | 54 / 54 scheduled captures; nine workloads, three trials, two runtimes, matched machine/build identity, alternating runtime order |
| BlockBreaker workload | 10,000 frames, three trials, one shot, one hit, two command completions per trial |
| Gameplay boundary | generated ABI v3 fast/CallFrame path, zero legacy calls |
| M10.3 weighted suite | qualified at 0.563208 versus the 0.95 maximum; all 14 regression gates passed |
| Matched BlockBreaker frame median | UnifiedScript 0.3902 ms; Puerts 0.4160 ms; ratio 0.937981; p95/p99 ratios 0.934816 / 0.935693 |
| Matched BlockBreaker bridge median | UnifiedScript 118.7061 ms; Puerts 285.9899 ms; ratio 0.415071; calls and logical bytes are both 20% of Puerts |
| Matched package size | UnifiedScript 1,033,732,983 bytes; Puerts 1,009,756,057 bytes; ratio 1.023745 |
| Full migration audit | 74 / 74 inventory items, 19 / 19 shared assets, 40 / 40 generated thunks, zero legacy targets |
| Deliverable smoke | 300 frames, one shot, one hit, two command completions, 3,631 fast/CallFrame calls, zero legacy calls |
| Deliverable report | `artifacts/demo/blockbreaker-20260828-152605/demo-delivery.json` |
| Compiled delivery comparison | 5 alternating trials: chunk startup 75.8024 ms vs loose 78.1151 ms (0.970394); reload 75.2471 ms vs 75.6904 ms (0.994143); generation load ratio 0.728196; one 44,886-byte chunk vs two 43,992-byte loose runtime files |
| M11.1 reliability soak | editor + dedicated-server process lanes passed: 64 travel, 64 PIE, 128 successful + 128 rolled-back reloads, 8,192 object registrations, 34 GC cycles, 131,072 commands, 152 ordered World shutdowns, zero ownership/leak deltas |
| Reliability report | `artifacts/ue/reliability-soak/20260828-160743/reliability-soak.json` |
| M11.1 scale gate | 256 Contract types / 4,096 members, 256 compiled Script Assets, deterministic generation, localized Contract rebuild and one-asset churn, all budgets passed |
| Scale report | `artifacts/ue/scale/20260828_163657/scale-gate.json` |
| Visual/C# round trip | semantic, MethodDef IL, execution cases, Script Assets, two live UE candidates, and two packaged candidates equivalent |
| Round-trip report | `artifacts/roundtrip/blockbreaker-20260828-021802-6305602d/roundtrip-report.json` |
| M12 strict release acceptance | 15 / 15 required gates passed from clean source revision `5be7dde`; evidence at `artifacts/release-readiness/m12-final-5be7dde/release-readiness.json` |

## Release ladder

| Level | Meaning | State |
| --- | --- | --- |
| R0 foundation | Deterministic language, graph, patch, CoreCLR, and UE adapter contracts | Complete |
| R1 demonstrable alpha | A packaged sample runs meaningful gameplay without Puerts | Complete |
| R2 adoptable alpha | A second UE project can install, generate, run, and package without repository-specific glue | Complete |
| R3 product beta | Practical gameplay API coverage, attachable Script Classes, complete visual/code loop, diagnostics, reload, and migration workflow | **In progress under M13; prior backend evidence is complete, but the Puerts-grade authoring loop is not** |
| R4 release candidate | Broad performance, soak, packaging, upgrade, and CI evidence replayed against the product workflow | **Evidence foundation complete; final status waits for R3 workflow replay and external pilot** |
| R5 1.0 | Versioned SDK and plugin distribution with supported compatibility and migration policy | **Not declared; RC1 is a locally accepted foundation artifact, not the finished usability product** |

## Evidence-backed foundation

The completed milestones below are retained as the historical acceptance
record. Their remaining language-completeness items are routed into the forward
product milestones instead of silently competing with the active cursor.

## M0 — Repository foundation

Outcome: deterministic .NET solution with documented boundaries and automated
verification.

- [x] .NET 10 solution and central package versions
- [x] Core, Semantics, CSharp, Graph, CLI, and Tests projects
- [x] repository validator
- [x] architecture and contribution rules

## M1 — Roslyn-to-graph vertical slice

Outcome: a supported `[Visual]` method emits deterministic HIR and graph JSON,
while unsupported syntax fails with actionable diagnostics.

- [x] deterministic semantic ID primitive
- [x] source spans and diagnostics
- [x] profile v0.2 validator (`while` foundation included)
- [x] declaration/statement/expression HIR
- [x] syntax-to-HIR lowering
- [x] graph schema and projection
- [x] CLI emission
- [x] unit tests and sample
- [x] persistent identity sidecar design
- [x] golden JSON schema fixture

## M2 — Semantic model and graph-safe types

Outcome: Roslyn symbols bind into a language-independent type/effect model.

- [x] primitive and nominal type descriptors
- [x] nullable type annotations
- [x] method overload resolution capture
- [x] graph-safe signature validation
- [x] pure/impure/latent effect descriptors
- [x] deterministic bound symbol references
- [x] deterministic multi-document compilation requests
- [x] cross-file type and overload binding
- [x] versioned method-effect catalog overrides
- [x] semantic configuration identity for references and effect rules
- [x] flow-sensitive nullability state
- [x] `.csproj`/MSBuild and NuGet reference loading
- [x] configurable API allow/deny catalog
- [x] semantic diagnostics for unsupported APIs

## M3 — Bidirectional source projection

Outcome: graph patches produce localized, format-preserving C# edits.

Status: complete. Broader control/data-flow editing continues under M4.

- [x] versioned semantic patch protocol
- [x] source compilation and semantic configuration preconditions
- [x] localized literal replacement by graph/HIR `SemanticId`
- [x] atomic rejection of stale, invalid, or semantically unsafe patches
- [x] Roslyn syntax construction with trivia/comment preservation
- [x] source-to-graph-to-source literal round-trip tests
- [x] persistent semantic ID sidecar
- [x] structural/shape/context reconciliation with ambiguity diagnostics
- [x] tombstones for deleted semantic nodes
- [x] explicit identity remapping for graph-authored replacements
- [x] unary/binary operator changes with semantic type preconditions
- [x] same-invocation argument reconnection with type/effect guards
- [x] overlapping structural-operation conflict detection
- [x] local-literal statement insertion with parent/sibling/type/name guards
- [x] safe statement deletion with reference/effect guards and tombstones
- [x] exact sibling preconditions for concurrent structural edits
- [x] HIR use/definition and expression-location indexes
- [x] stable pure-expression extraction into a single-evaluation temporary
- [x] structured block dominance for local value availability
- [x] typed existing-local-to-consumer reconnection with effect guards and tombstones
- [x] atomic temporary-introduction and multi-consumer patch plans
- [x] symbolic plan handles for newly allocated local declaration IDs
- [x] in-memory stage rebasing with late-failure rollback
- [x] atomic plan composition of all bounded patch operations
- [x] insertion-produced local handles and structural gap/endpoint rebasing
- [x] versioned scalar graph-authored expression payload
- [x] first typed `if` synthesis with explicit empty branch blocks
- [x] symbolic branch-block handles and ordered local-literal branch-body population
- [x] typed branch return payloads and terminal-step/unreachable guards
- [x] unaffected-node identity pinning for nested staged insertions
- [x] graph layout sidecar with deterministic JSON, orphan recovery, and CLI sync

## M4 — Structured control flow expansion

Outcome: practical gameplay methods are fully visualizable.

- [x] Graph v0.3 regions for functions, blocks, `if`, `then`, and `else`
- [x] exact direct node ownership and explicit/implicit branch preservation
- [x] CFG entry/exit, reachability, dominance, and post-dominance indexes
- [x] structured branch merge/exit facts and SCC reducibility validation
- [x] CFG-checked bounded local assignment and compatible local-value rewiring
- [x] recursive nested branch construction and terminal-path propagation
- [x] explicit `while`, `break`, and `continue` HIR and C# lowering
- [x] Graph v0.4 `loop`/`loopBody` regions and nearest-loop transfer roles
- [x] loop header/body/exit/back-edge CFG facts and target validation
- [x] bounded Graph-to-C# while synthesis and symbolic body population
- [x] nearest-loop break/continue construction with atomic rollback
- [x] exact typed replacement of existing assignment values and while conditions
- [x] repeatable symbolic condition editing for plan-produced while loops
- [x] exact typed replacement of existing local initializers, if conditions,
  and return values
- [x] repeatable symbolic endpoint editing for plan-produced locals, branches,
  and returns
- [x] typed produced-local references inside later plan expression trees
- [x] same-method, scope, and CFG-dominance validation for generated values
- [x] exact nested binary/unary/cast/invocation-argument Graph input replacement
- [x] old-endpoint/type/effect/trivia checks with unaffected-spine identity retention
- [x] symbolic roots and canonical port paths for expressions authored by
  earlier plan stages
- [x] chainable produced-expression input edits lowered to the ordinary patch
  path with atomic rollback
- [x] first-class conditional-expression HIR/Graph/source round trip with
  lazy-branch effect safety and canonical generated-tree ports
- [x] explicit `&&`/`||` short-circuit Graph evaluation with left-effect
  sequencing and effect-safe conditional right operands
- [x] null-coalescing text/HIR/Graph/patch round trip with nullable result
  typing, lazy-right metadata, and produced-expression editing
- [x] first-class null-conditional access/member binding with single receiver
  evaluation, lifted result/effect safety, bounded visual construction, and
  exact receiver/produced-handle editing
- [x] lossless text-authored `for` HIR/Graph projection with explicit
  initializer/body/increment regions, postfix identity, and correct continue
  routing through incrementors
- [x] dedicated bounded `ForPatchStatement` construction with declaration
  scope, clause ordering, and continue-target proofs
- [x] exact existing-for condition, declaration-initializer,
  assignment-initializer-value, and mutation-incrementor editing, including a
  generated-for condition handle
- [x] lossless `do` loop projection with explicit initial-body entry and
  post-body condition/continue CFG routing
- [x] bounded `do` construction, symbolic body population, and exact
  existing/produced condition editing
- [x] indexed `foreach` over strings and one-dimensional zero-based arrays,
  with collection capture, current-value binding, CFG routing, bounded
  construction, produced-local handles, and exact collection editing
- [x] synchronous graph-safe `IEnumerable<T>` foreach with bound enumerator
  protocol, explicit acquisition/current/disposal Graph nodes, cleanup-region
  ownership, normal/return/exception CFG facts, and visual round trip
- [x] exact typed data-edge reconnection for stable pure expression sources,
  with stale-edge/type/effect/scope/dominance rejection and transactional C#
  reanalysis
- [x] typed data-edge reconnection and typed-default disconnection
- [x] automatic temporary extraction for shared computed values
- [x] structured local/branch/loop/return creation and statement deletion from UE
- [ ] arbitrary control-wire rewiring beyond structured region operations
  (scheduled in M8.1)
- [ ] pattern-only/conditional-disposal, ref, and async `foreach` variants
  (scheduled in M8.5)
- [x] lossless text-authored `switch` and supported-pattern HIR/Graph/CFG
  projection with editable section-body ownership
- [x] deterministic shared-value temporary naming from the complete producer
  semantic identity

## M5 — CoreCLR execution backend

Outcome: text and graph versions compile to and execute the same IL.

- [x] Roslyn IL-emission host
- [x] deterministic in-memory assembly and portable PDB emission
- [x] reloadable collectible development assembly context
- [x] backend-neutral invocation and execution-result v0.1 contract
- [x] text-authored versus visual-plan-authored execution equivalence proof
- [x] deterministic method/node-to-MethodDef/IL/PDB source map v0.1
- [x] PE/PDB/source-map `emit` and `emit-files` CLI artifact commands
- [x] content-addressed bounded program-image cache
- [x] monotonic collectible reload generations with transactional failure
- [x] shared semantic breakpoint bind/migrate/hit identity protocols
- [x] cooperative CoreCLR statement traps with real pause/resume
- [x] live CLR stack, bounded graph-safe per-frame locals, step in/over/out, and
  read-only snapshot evaluation
- [x] generation-safe in-process loopback debug transport
- [x] live UE World cooperative semantic traps and GameThread-safe bridge return
- [x] bounded versioned remote debug transport framing (delivered in M9.3)
- [x] async invocation and cancellation/time-budget contract (delivered in M7.2)

## M5.5 — Script asset and authoring experience

Outcome: state and behavior are text/visual/AI-authorable without Blueprint-only
declarations or one-node-per-expression graphs.

- [x] backend-neutral Script Asset and Declaration IR v0.1
- [x] stable state/property identity, type, default, and exposure metadata
- [x] declaration semantic patching and transactional C# round trip
- [x] compact structure regions with inline ordinary expressions
- [x] actual Declaration/Graph artifacts connected to the editor prototype
- [x] coordinate-free AI outline and typed declaration patch preview
- [x] live CoreCLR pause/locals/step-over painted on real semantic statement cards
- [x] sample source workflow with zero required Blueprint variables
- [x] transactional Workbench declaration/body submission and source write-back
- [x] CoreCLR hot reload with semantic selection and breakpoint migration
- [x] external-file conflict rejection and persistent rename identity

## M6.0 — Generic authoring surface

Outcome: every authoring client consumes one versioned, engine-neutral Surface
instead of interpreting Graph or embedding sample behavior.

- [x] dedicated `UnifiedScript.Authoring` project boundary
- [x] deterministic Authoring Surface v0.1 JSON and strict invariants
- [x] Surface v0.3 complete nodes, typed ports, control/data edges, real block
  source IDs, ordered statement IDs, and available parameter type identities
- [x] Declaration IR + Graph projection for multiple assets and functions
- [x] structured if/loop region hierarchy with compact inline expressions
- [x] source spans, state-reference IDs, and debug-target IDs on actions
- [x] schema-driven literal and operator edit capabilities
- [x] CLI Surface artifact emission and authoring workspace v0.2
- [x] Surface-driven Workbench navigation, cards, inspector, AI lens, and debug
- [x] multi-asset/if/while/for sample, tests, and browser verification
- [x] zero sample-specific behavior names in the UI adapter

## M6.1 — Unreal Engine adapter

Outcome: the proven language pipeline becomes usable from UE.

- [x] UE 5.7/5.8-compatible host project and runtime/editor plugin module boundary
- [x] strict deterministic Unreal reflection manifest v0.1
- [x] selected-module `UClass`/property/function extraction commandlet
- [x] C# binding source generator and public `ue-bindings` CLI
- [x] handle-only managed invocation contract
- [x] initial generated-script host component and reflection fixture
- [x] local engine discovery/build/export/binding verification script
- [x] full UE 5.8 C++ compile and live commandlet export smoke test
- [x] native fixture primitive property/function dispatch with stable failures
- [x] managed `IUnrealInvocationBridge` adapter over the native dispatch contract
- [x] versioned in-process native transport for the managed adapter
- [x] generation-checked weak UObject handle table and deletion invalidation
- [x] live native lifecycle Automation test on installed UE 5.8.1
- [x] world-scoped native host and retained bridge-context ownership
- [x] process-wide hostfxr/CoreCLR bootstrap and load-assembly delegate
- [x] managed World entry and collectible per-script generation contexts
- [x] hosted demo assembly loading and generated-wrapper execution on a live UObject
- [x] component-driven auto-registration, Start/Tick/Stop, and real DLL reload
- [x] stable generated Member IDs, ABI v2 transport, and component native fast thunks
- [x] `UUnifiedScriptAsset`, Content Browser/soft-path resolution, and transactional type reload
- [x] automatic native thunk generation for arbitrary public reflected types
- [x] script declaration materialization on the host component
- [x] Slate integration consuming Authoring Surface v0.3, with native
  GraphEditor pins/wires, semantic selection, source preview, auto reload, and
  typed semantic-patch export
- [x] Graph Presentation v1 with compact value leaves, author-facing pin labels,
  dedicated assignment titles, hidden debug chrome, and lossless Surface access
- [x] transactional Slate Apply through Workbench with regenerated
  Declaration/Surface/DLL/PDB artifacts and matching component reload
- [x] transactional occupied-data-input drag rewiring with server-owned commit,
  Surface refresh, and failure rollback
- [x] Graph Authoring v2 Alpha node palette and transaction-first create/delete
- [x] typed-default wire disconnection without mutating compiler-owned links first
- [x] automatic shared-value temporary plan through the Workbench plan endpoint
- [x] reflection-driven readable-property and callable UE API node insertion
  with structured C# output
- [x] per-function `.usgraph` position persistence and atomic sidecar save
- [ ] arbitrary control-wire authoring beyond structured block operations
  (scheduled in M8.1)
- [x] self-contained Win64 cook and packaging strategy

## M6.2 — Production Unreal interop and release gates

Outcome: generated gameplay bindings cover practical UE values through a
measured call path and survive packaged Win64 builds.

Status: complete for the production interop and packaged-demo baseline. Graph
Authoring Beta and native debugging are deliberately owned by M8 and M9.

- [x] restore and freeze the UE 5.8 editor, World demo, .NET, and validator baseline
- [x] freeze independent ABI v3 value/call-frame/table layouts and ownership rules
- [x] generated managed shapes for all scalar widths, FName/FText, enum and
  generation-safe object/class handles
- [x] generated and live-tested native scalar, enum, FVector, FRotator, and
  FTransform direct thunks
- [x] activate ABI v3 with a strongly typed zero-allocation steady-state path
- [x] arrays, sets, maps, variable-size returns, and return plus `out`/`ref`
- [x] placeable Actor host and managed UObject/Actor lifecycle object model
- [x] World-safe Actor overlap delegates/events and latent generation cancellation
- [x] deterministic UE binding coverage gate and safe reflected-path fallback
- [x] arbitrary reflected delegate signatures, ABI v4 event proxies, attributed
  lifecycle binding, and Graph/C# Event Entry round-trip
- [x] reproducible C++/UnifiedScript benchmark, Puerts comparison gate, and regression thresholds
- [x] append-compatible generated scalar ABI lane that passes the matched Puerts latency gate
- [x] per-World pre/managed/post phase scheduler on UE tick delegates
- [x] fixed-row command buffer with next-frame double-buffered completion events
- [x] single-boundary line, sphere, and box Batch Trace with World-scoped handles
- [x] BlockBreaker shot migration from trace/damage/emitter facade calls to the gameplay data plane
- [x] Win64 Development/Shipping cook, dependency closure, and packaged smoke test
- [x] self-contained 300-frame deliverable BlockBreaker smoke and hashed delivery report

## M6.3 — Adoptable project alpha

Outcome: a developer can add UnifiedScript to another UE 5.8 project, generate
its contracts, run scripts, and package the project without knowing this
repository's internal build sequence.

Status: **complete**.

### M6.3.1 — One-command project onboarding

- [x] accept an engine root or resolved `EngineAssociation`, a `.uproject`,
  selected modules/types, and an output policy as explicit inputs
- [x] discover and validate the plugin, .NET SDK/runtime, Unreal build tools,
  project modules, and writable generated locations before changing files
- [x] run reflection export, contract-driven managed wrapper and
  native thunk generation, deployment-manifest sync, managed build, and native
  validation through one orchestrated command
- [x] support idempotent `-ValidateOnly` and ordinary apply modes with stable,
  actionable failure categories
- [x] prove the same command against both `HostProject` and BlockBreaker with no
  machine-specific absolute path committed to source

Acceptance: start from a clean copy of either consumer, invoke one documented
command, then pass contract validation, native build, managed startup, and a
second no-diff validation run.

Evidence (2026-08-26): `tools/setup-unreal-project.ps1` passed Apply for both
UE 5.8.1 consumers and a following ValidateOnly run. HostProject passed two UE
builds, 68/68 native contract members, one Script Asset, deterministic C#/C++/
node/coverage outputs, and 10/10 Automation tests. BlockBreaker passed two UE
builds, 43/43 native members, two Script Assets, and deterministic outputs.

### M6.3.2 — Distributable SDK and plugin bundle

- [x] assemble the source plugin, managed runtime, CLI/tooling, schemas,
  templates, and version metadata into one deterministic SDK archive
- [x] separate engine-independent managed payloads from engine-version-specific
  native build output
- [x] verify hashes, required files, ABI/schema compatibility, and accidental
  absolute paths before publishing an archive
- [x] install the archive into a clean project copy and remove any dependency
  on the UnifiedScript repository checkout

Acceptance: the installed SDK passes M6.3.1 and packages a runnable Win64 build
from an isolated directory.

Evidence (2026-08-26): `tools/package-unifiedscript-sdk.ps1` produced the same
SHA-256 on two consecutive `0.1.0-m6.3-alpha.1` builds. Its manifest covers all
payload files plus UE 5.8/.NET 10, ABI v2/v3/v4, and schema compatibility while
the plugin payload contains no native `Binaries` or `Intermediate` output. A
Git-exported BlockBreaker copy with its plugin excluded installed solely from
the SDK archive, passed packaged-layout onboarding (38 reflected types, 43/43
native members, two Script Assets, deterministic outputs), built/cooked/staged/
paked, and passed the runnable 300-frame gate with one shot, one hit, two
gameplay commands, 3,627 fast/call-frame calls, and zero legacy calls.

### M6.3.3 — Project-agnostic sample and templates

- [x] convert `HostProject` into the minimal onboarding fixture while keeping
  BlockBreaker as the realistic gameplay fixture
- [x] provide Script Asset, C# type, project-contract, deployment-manifest, and
  build-target templates with placeholders instead of sample-specific names
- [x] ensure generated code contains no BlockBreaker or fixture behavior in the
  runtime/plugin implementation
- [x] add a first-script walkthrough that ends in editor play and packaged play

Acceptance: a new script type can be created, visually opened, hot reloaded,
and packaged without copying files from either sample by hand.

Evidence (2026-08-26): the SDK-installed `new-unifiedscript-project.ps1`
created a clean `HostScaffold.UnifiedScript.FirstScript` project, project-owned
contract/deployment/onboarding files, one Script Asset, and all Graph/HIR/
semantics/Declaration IR/Surface outputs in an isolated Git export. Packaged-
layout onboarding reflected two project types and generated 16/16 native
members with zero fallback or rejected members. The project-agnostic authoring
launcher initialized stable identities and served a ready workspace containing
that script, while the native Slate surface retained its tested Apply & Reload
path. A clean UE 5.8.1 `BuildCookRun` then built, cooked, paked, staged, and
archived the generated managed assembly and authoring artifacts. The plugin
implementation contains no BlockBreaker, Demo, or DamagePolicy fixture behavior;
the packaged-smoke harness now belongs to the HostProject fixture. The final SDK
archive contains 148 hashed files and reproduced SHA-256
`a2f28a31baa2954e5d27e7fc55771a00cdea5e0443bd4787606bbcfe0ba6dd03`
on two consecutive builds. Main-source regression passed 441/441 managed tests,
41/41 repository invariants, two UE editor builds, deterministic regeneration,
asset deployment, and 10/10 Automation tests.

### M6.3.4 — Alpha acceptance and failure UX

- [x] add a single alpha acceptance runner that aggregates clean generation,
  managed tests, validator, UE build, Automation, package smoke, and reports
- [x] print the failed phase, relevant log, generated artifact, and safe retry
  command instead of requiring manual log archaeology
- [x] publish a versioned machine-readable acceptance report and a concise
  human summary

Acceptance: one report proves or rejects the complete R2 claim. Completing this
slice advances the cursor to M7.

Evidence (2026-08-26): `tools/run-unifiedscript-alpha-acceptance.ps1` first
proved its failure contract with `USACC1001`, a persisted failed JSON/Markdown
pair, the owning phase, and a non-destructive retry. Its final complete UE
5.8.1 run then passed in 135,150 ms and wrote
`artifacts/acceptance/20260826-161229/alpha-acceptance.json` using schema
`unifiedscript.alpha-acceptance/v0.1`. The single report records 441/441 managed
tests, 42/42 repository invariants, 68/68 native contract members, 10/10 Automation tests,
two successful editor builds and deterministic regeneration, followed by a
Development Build/Cook/Stage/Pak/Archive containing 251 files and a packaged
CoreCLR Start/Tick/Stop smoke with exit code zero. Every phase owns a separate
log, output list, duration, exit code, and directly executable safe retry.

## M7 — General gameplay API and runtime completeness

Outcome: generated contracts cover ordinary Unreal gameplay code broadly
enough that BlockBreaker and a second gameplay slice need no handwritten
project facade for supported engine operations.

### M7.1 — Binding coverage matrix

- [x] cover inheritance, interfaces, overloads, default parameters, static
  members, Blueprint metadata, nested structs, enums, object/class/soft handles,
  containers, return values, and `out`/`ref` combinations with golden generation
- [x] generate explicit allow/deny policy and stable diagnostics for unsafe or
  unsupported APIs
- [x] classify every selected reflected member as direct thunk, CallFrame,
  data-plane operation, safe reflection fallback, or rejected-with-reason
- [x] fail CI when selected project coverage regresses or handwritten generated
  dispatch reappears

Evidence (2026-08-26): reflection/API Contract/coverage advanced to v0.3/v0.2/v0.2
with a checked-in matrix for inheritance, interfaces, overloads, defaults,
Blueprint metadata, nested types, enums, soft handles, containers, and
return/ref/out shapes. Exact C#, C++, visual-node, and coverage goldens compile
and compare in the 444/444 managed suite. The live contract classifies 73
members as 22 direct thunks, 49 CallFrame members, two safe delegate fallbacks,
and zero rejected members; all 71 native paths are generated. Stable policy and
`USUE5101`-`USUE5106` baseline diagnostics plus the repository handwritten-
dispatch scan passed 42/42 invariants. UE 5.8.1 completed a clean 3,768-action
Editor build, and the final one-command onboarding report
`artifacts/onboarding/m7-1-20260826.json` passed deterministic regeneration,
both native builds, deployment, and 10/10 Automation in 55,664 ms. That run
also found and fixed source-engine-relative Slate API catalog resolution.

### M7.2 — Services, async, and lifecycle

- [x] add generated World/static services for timers, input, component lookup,
  spawn/destroy, resource and async asset loading, collision queries, and common
  gameplay events
- [x] define task/latent completion, cancellation, timeout, World teardown, and
  hot-reload ownership as one versioned contract
- [x] stress object deletion, level travel, PIE restart, multiple Worlds, and
  generation rollover without stale callbacks or leaked managed contexts

Evidence (2026-08-27): Async Service ABI v5 adds a World-owned fixed request,
completion, borrowed-view, and function-table contract for Delay, async object/
class load, per-request cancellation, game-time timeout, generation cancellation,
and teardown rejection. `UnrealObjectScript` now binds latent work to one
instance/generation scope and cancels it before EndPlay or collectible reload.
The generated product contract expanded from 73 to 98 members: World services
now include input polling, component lookup, spawn/destroy, time, resource load,
visibility trace, and the selected 17-field `FHitResult` shape. Coverage reports
24 direct thunks, 72 CallFrame members, two safe fallbacks, 96 native paths, and
zero rejected members. The 448/448 managed suite and 44/44 validator passed;
UE 5.8.1 completed both native builds, deterministic regeneration, deployment,
CoreCLR startup, and 12/12 Automation in 110,938 ms in
`artifacts/onboarding/m7-2-lifecycle-20260827.json`. The dedicated
`UnifiedScript.Unreal.WorldHost.LifecycleStress` gate then completed eight
non-seamless travel handoffs, eight PIE create/stop cycles, four simultaneous
Worlds reusing eight request IDs each, and sixteen managed generation rollovers.
It found and fixed generation-owned async resource handles surviving reload;
the final `artifacts/ue/lifecycle-stress.json` reports zero stale completions,
pending-request, managed-World, native-context, owned-resource, and retired-ALC
leaks. The standalone lifecycle report passes as an independently reproducible
focused gate. M7.2 is complete and the cursor advances to
M7.3 schema-generated gameplay data-plane layouts.

### M7.3 — Schema-generated gameplay data plane

- [x] generate borrowed snapshot, fixed command, completion event, and batched
  query layouts from versioned schemas instead of handwritten row definitions
- [x] add bounded capacity, overflow telemetry, ordering, borrow lifetime, and
  forward-compatible schema checks
- [x] extend the current Transform/Trace/impact/damage slice to representative
  movement, overlap, attribute, spawn, and query workloads only where profiling
  proves per-call binding is the wrong boundary

Evidence (2026-08-27): one strict v0.1 JSON layout schema at compatible revision
2 now deterministically emits the C++ and C# snapshot, command, completion,
trace, overlap, view, enum, limit, offset, default, telemetry, and fingerprint
contracts. Exact checked-in-output tests and
`generate-unreal-data-plane.ps1 -Check` reject drift. The World runtime bounds
all four lanes, reports cumulative
overflow and execution/order counters, preserves borrowed view/handle lifetime,
and accepts size-guarded forward-compatible transport suffixes. Batch Overlap
adds sphere/box requests without exposing `FOverlapResult`. The matched live
CoreCLR profile ran 1,024 operations across three median samples: movement row
submission measured 0.200x scalar-binding cost, Batch Trace 0.0076x matched
per-call trace cost, and fixed-row loops allocated zero managed bytes. Therefore
movement/overlap/query use the data plane, generic attributes remain generated
bindings, and spawn stays hybrid. The 452/452 managed suite, 47/47 repository
invariants, UE 5.8.1 build, focused Gameplay Data/Workload Profile Automations,
and the reproducible profile report passed. The final one-command onboarding
report `artifacts/onboarding/m7-3-20260827.json` then passed deterministic
regeneration, both native builds, deployment, CoreCLR startup, and 13/13
Automation tests in 73,660 ms. SDK `0.1.0-m7.3-alpha.1` packaging also passed
with the data-plane schema in its hashed manifest. M7.3 is complete and the
cursor advances to M7.4 full BlockBreaker runtime migration.

### M7.4 — Full BlockBreaker runtime migration

- [x] inventory all Puerts scripts, Unreal APIs, delegates, resources, globals,
  lifecycle hooks, and remaining facade behavior
- [x] migrate the complete playable logic through generated contracts or the
  generic data plane; keep sample-only gameplay outside the plugin
- [x] remove the Puerts plugin from the UnifiedScript project and prove editor,
  packaged, functional, and asset-parity gates

Acceptance: BlockBreaker is a whole-project migration proof rather than a
single API or shot-path benchmark, and the coverage report contains no
unexplained manual bridge.

Evidence (2026-08-27): `ue-puerts-inventory` now scans the canonical Puerts
source and target native headers into deterministic, fingerprinted inventory,
coverage, and report schemas. BlockBreaker covers all 74 discovered items: four
scripts, three script types, 13 methods, six lifecycle hooks, four imports, two
Puerts APIs, 29 Unreal APIs, three resources, three global patterns, four async
patterns, two decorators, zero delegates, and one explained native facade.
`ReportManagedTick` remains verification-only for the exact-frame acceptance
counter and process exit; gameplay policy does not cross it. Camera/gun mount,
shot counters, hit counters, and status now use generated bindings or direct
generated property access.

The target comparison commit `489ce9b` removes 154 tracked Puerts/TypeScript
files (about 55 MB) and all forbidden legacy roots while preserving 19/19
non-map gameplay assets byte-for-byte. The project contains exactly two Script
Assets and its nine-type, 40-member contract emits 40/40 native thunks with no
fallback or rejection. The 300-frame Editor gate passed with one shot, one hit,
two commands, 3,631 fast/CallFrame calls, and zero legacy calls. The Development
package `artifacts/demo/blockbreaker-20260827-165719` passed the same gameplay
gate across 249 files and 1,030,599,735 bytes, with zero Puerts/JavaScript/
TypeScript package files. The exact 10,000-frame, three-trial comparison also
kept functional parity and zero legacy calls, but its local median was
4,927.191 ms versus Puerts at 4,759.930 ms (ratio 1.035139); this is not a
performance win, and M10 retains the at-least-5%-faster release gate. The
455/455 managed suite and 48/48 repository invariants pass. M7 is complete and
the cursor advances to M8.1.

## M8 — Visual/C# Authoring Beta

Outcome: developers can implement and maintain practical gameplay from either
C# or the UE graph editor, with each view remaining a lossless projection of
the same semantic program.

### M8.1 — Structured graph editing completeness

- [x] add `switch` and supported patterns, deterministic temporary naming, and
  the remaining high-value structured control operations
- [x] support safe control-flow reconnection through region-aware transforms;
  reject irreducible or semantically ambiguous wiring with actionable feedback
- [x] make create, delete, reconnect, extract, collapse, copy/paste, and
  undo/redo transactional and identity-stable
- [x] group several author-selected edits into one named atomic transaction

Progress (2026-08-27): HIR v0.8 and Graph v0.13 now preserve classic labels,
declaration/type/var/discard/relational/logical patterns, guards, typed pattern
variables, switch sections, nearest-breakable CFG facts, and actionable
unsupported-pattern diagnostics. Section regions target their exact editable
HIR body block and exclude case labels from statement order. The UE palette now
authors `do/while`, bounded `for`, `break`, and `continue`; switch-section
`break` is accepted while an unscoped `continue` is rejected. Automatic shared
values use the complete producer semantic ID instead of a six-character prefix.
The control-reconnection path maps a single-path control-pin drag to Semantic
Patch v0.22 `moveStatement` instead of mutating raw edges. It supports same-block
reordering plus ordinary variable/expression statement movement across explicit
function, then/else, loop, and switch-section bodies. Exact source/destination
block and sibling preconditions reject stale surfaces; method-wide `.usids`
pinning preserves nested identity and comments; local ordering, terminal,
function-boundary, compilation, and reducible-CFG gates reject unsafe moves with
actionable diagnostics. Entire branch/loop/switch statements and
return/break/continue remain owned by explicit structured transforms. The Slate
client keeps the old compiler-owned wire until the Workbench commits. Semantic
Patch v0.23 now adds typed `switch` construction plus preconditioned section
insert/relabel/reorder/delete operations across constant, declaration, type,
var, discard, relational, logical, negated, and parenthesized patterns. Direct
statement insertion/deletion/reordering also accepts a switch-section body.
Authoring Surface v0.4 carries normalized Graph properties, including the exact
semantic switch-section identity, so Slate can consume these operations without
parsing presentation text. The Slate palette now creates a typed switch, and
its inspector converts
bounded pattern text into semantic labels, inserts Case/Default sections,
relabels whole sections, moves them by exact sibling gaps, and routes both its
button and Delete-key paths through safe section deletion. Workspace v0.3 now
places accepted declaration, semantic, plan, Undo, and Redo operations behind
one baseline-revision and idempotent-operation gate. A bounded 100-entry history
restores exact stabilized source/identity snapshots, republishes every artifact,
and advances both workspace revision and CoreCLR generation. Browser and Slate
consume the same server history and never maintain a competing edit stack.
Semantic Patch v0.24 now adds a bounded identity-safe copy/paste transaction.
The Slate GraphEditor maps Copy/Paste to a compiler-owned statement identity
and an exact same-block destination gap; the C# applier pins the original
method subtree, gives the copy fresh IDs, preserves comments, generates a
conflict-free name for copied locals, recompiles, and validates the projected
CFG. Ctrl/Cmd+Z/Y use the same Workbench history as the toolbar. User-grouped
history, collapse/expand transforms, and broader multi-selection or cross-region
clipboard semantics remain open, so M8.1 is not yet complete.

Semantic Patch v0.25 now adds reversible lexical scope transforms. Collapse
Scope consumes two or more consecutive selected statement nodes from one
compiler-owned block and creates a real nested C# block; member/comment IDs are
pinned while the wrapper receives a fresh region ID. The tree inspector exposes
Expand Scope for that nested BLOCK and restores its ordered members while
tombstoning only the wrapper. Full compilation and CFG validation reject scope
escape or name-collision changes. This closes the bounded region
collapse/expand slice, but extracted callable subgraphs, user-grouped history,
and wider clipboard semantics remain before M8.1 completion.

Authoring History v0.1 now persists the 100-entry server-owned Undo/Redo graph,
monotonic workspace revision, transaction labels, exact multi-source/identity
snapshots, and operation replay guard across Workbench and UE editor restarts.
The prospective journal is written atomically with C#, `.usids`, Declaration
IR, Surface, DLL, and PDB. Restore validates source/identity/configuration and
snapshot/chain digests; stale or corrupt journals remain untouched, start a
fresh revision-1 history, and surface a rejected state in browser and Slate.
Historical snapshots reanalyze lazily on navigation. This closes persisted
history; user-defined grouping, callable extraction, and wider clipboard
semantics still keep M8.1 open. The 486/486 managed suite, UE 5.8 editor build,
focused native Authoring Surface Automation, and 49/49 repository invariants
pass.

Semantic Patch v0.26 now closes same-method range clipboard semantics. Slate
copies one consecutive multi-selection and can paste it after an ordinary
statement in another explicit structured region. The transaction carries exact
source outer gaps and a distinct destination gap; originals keep their IDs,
copies receive fresh IDs, direct copied locals and their bound references are
renamed together, and external local/loop/pattern references must dominate the
destination. Cross-function transport remains owned by callable extraction,
where parameter, result, visibility, and effect contracts can be explicit.
User-defined grouping and callable extraction are now the remaining M8.1 slice.
The 489/489 managed suite, UE 5.8 editor build, focused native Authoring Surface
Automation, and 49/49 repository invariants pass.

Semantic Patch v0.27 now closes bounded callable extraction. Slate sends an
ordered same-block selection, exact outer gaps, containing method identity, and
the requested name. Roslyn infers up to 16 graph-safe inputs and zero or one
live result, creates a private static/non-static `[Visual]` method matching its
caller, and keeps moved identities stable while assigning fresh identities to
the new declaration, parameters, body, and call site. Caller-visible extracted
locals retain their original identity at the call site. Async/await, escaping
return/break/continue, directives, name conflicts, unsafe types, and multiple
live outputs reject before any write. User-defined transaction grouping is now
the only remaining M8.1 slice. The 495/495 managed suite, UE 5.8 editor build,
focused native Authoring Surface Automation, and 49/49 repository invariants
pass.

Workspace v0.4 now closes user-defined grouping and completes M8.1. Browser and
Slate can begin one named server-owned draft, stage successive declaration,
semantic, and patch-plan edits against regenerated draft Surfaces, then commit
the final snapshot through exactly one source/identity/artifact/history/CoreCLR
publication or cancel it with no writes. Stages use unique replay IDs but create
no public history or runtime generations; commit creates one named Undo entry.
Nested/mismatched groups, stale draft revisions, duplicate stages, empty commit,
ordinary writes, history navigation, and debug start during a draft are rejected.
The cursor advances to M8.2 after the 497/497 managed suite, UE 5.8 editor build,
focused native Authoring Surface Automation, and 49/49 repository invariants
pass.

### M8.2 — Blueprint-grade Unreal experience

- [x] deliver context-sensitive palette search, favorites/recent nodes, pin
  promotion and defaults, comments, reroute nodes, zoom/minimap, selection, and
  keyboard workflows consistent with UE expectations
  - [x] context-derived token search, persistent Favorites, accepted Recent
    actions, and `Ctrl+Space` / Enter / double-click placement
  - [x] source-backed typed Pin Default/Reset and safe Promote to Local Details
    editors
  - [x] comment/reroute authoring, minimap, viewport state, and common
    Blueprint keyboard parity
- [x] show concise author-facing node content by default while retaining exact
  type, effect, source, semantic ID, and generated API information on demand
- [x] provide compiler/contract diagnostics, quick fixes, source navigation,
  diff preview, and failed-transaction rollback in the editor

The first M8.2 tranche replaces the right-click-only discovery path with a
docked native Palette below **My Script**. Its action set is rebuilt from the
active function, bound semantic types, selected source block, loop/switch
ancestry, and generated UE reflection catalog. Search covers titles,
categories, tooltips, stable action IDs, and explicit contract keywords.
Favorites and the bounded eight-entry Recent list are stored in per-project user
config; Recent changes only after an accepted Workbench response. Function-level
Event Entries remain visible without an editable block, while statement actions
do not. The focused UE 5.8 Authoring Surface Automation and 50/50 repository
invariants pass. Later M8.2 tranches completed pin defaults/promotion, canvas
organization, compact/advanced node information, and the final
diagnostics/Diff/rollback safety loop recorded below.

The second M8.2 tranche adds source-backed Pin Authoring. Every connected data
input in native Details can replace its exact compiler-owned edge with a typed
C# literal, reset through a bounded semantic default, or promote the feeding
stable pure expression to a conflict-free local declaration. Semantic Patch
v0.28 carries the edge/endpoints/ports/type plus the closed authored expression;
the Roslyn path validates literal runtime type, source freshness, effect order,
scope, and identity before the ordinary Workbench transaction can publish.
Promote reuses `introduceTemporary` and is deliberately limited to explicit
local initializers and returns where evaluation order is proven unchanged. See
[`PIN_AUTHORING.md`](PIN_AUTHORING.md). The 501/501 managed suite, UE 5.8 Editor
build, focused native Authoring Surface Automation, and 51/51 repository
invariants pass.

The third M8.2 tranche completes native Canvas Authoring without creating a
second executable graph. Unreal Comment Boxes and typed wire Reroutes persist
as `.usgraph` v0.1 comment regions and ordered routing hints; reroute deletion
reconnects the semantic edge and compacts later point indices. A live
bottom-right minimap renders nodes, comments, routed segments, and the visible
viewport, and click/drag navigation centers the GraphEditor. View location,
zoom, and minimap visibility are stored per project/user, keyed by Surface path
and function ID, so personal navigation never causes sidecar conflicts. `C`,
`F`, and `Home` provide comment, focus-selection, and focus-all parity. See
[`CANVAS_AUTHORING.md`](CANVAS_AUTHORING.md). The 501/501 managed suite, UE 5.8
Editor build, focused native Authoring Surface Automation, and 52/52 repository
invariants pass.

The fourth M8.2 tranche separates authoring content from compiler diagnostics.
Cards now use bounded author-facing subtitles: Event Entries show their C#
handler signature instead of the canonical `/Script/...` path, and member reads
show their receiver without repeating the member name. Hover retains the exact
kind, raw effects, graph-safety flag, resolved type/Type ID, port contract,
generated method/member/event identity, source span, and semantic ID. Selecting
an action keeps ordinary authoring controls visible and puts the complete sorted
contract, declaration references, edit capabilities, and source preview inside
an initially collapsed **Advanced Node Details** area. See
[`NODE_DETAILS.md`](NODE_DETAILS.md). The 501/501 managed suite, UE 5.8 Editor
build, focused native Authoring Surface Automation, and 53/53 repository
invariants pass. M8.2 now advances to compiler diagnostics, safe quick fixes,
diff preview, source navigation, and explicit failed-transaction rollback UX.

The fifth M8.2 tranche closes that safety loop. New preview endpoints run the
same declaration/semantic/plan/history/group candidate preparation as commit
without writes and return exact per-source before/after text. Slate requires an
author confirmation and resubmits a SHA-256 preview token; the server rejects a
candidate mismatch before publication. Structured compiler/contract
diagnostics now retain code, severity, semantic identity, and source span in a
native panel with bounded Open Source and read-only Refresh Surface actions.
Every authoring-protocol failure also carries an explicit `unchanged`,
`restored`, or `failed` receipt with the active revision/generation, so the UI
never infers rollback from HTTP status. Group stages preview only the next
draft, while group commit previews the accumulated published delta. See
[`AUTHORING_DIAGNOSTICS.md`](AUTHORING_DIAGNOSTICS.md). The 502/502 managed
suite, UE 5.8 Editor build, focused native Authoring Surface Automation, and
54/54 repository invariants pass. M8.2 is complete and the cursor advances to
M8.3 asset, state, event, and API authoring.

### M8.3 — Asset, state, event, and API authoring

- [x] create and edit Script Assets, exposed state, functions, lifecycle hooks,
  reflected events, and API nodes without editing generated JSON
- [x] drive palette categories, tooltips, defaults, latent markers, thread/World
  restrictions, and deprecation from generated reflection metadata
- [x] preserve instance overrides and compatible semantic identities across
  rename, reload, source edits, and graph edits

The first M8.3 tranche added native create/update/remove workflows for Script
Assets, exposed state, Visual functions, lifecycle hooks, reflected Event
Entries, and API actions through previewed source transactions. The second
tranche upgrades reflection to v0.4 and the API node catalog to v0.2: native
Blueprint metadata now deterministically drives title, category, tooltip,
defaults, deprecation, latent, thread, and World-context policy in Slate. The
checked-in Demo proves a real reflected default through `AddWithDefault`; latent
and current void-statement actions remain visible with exact unavailable
reasons instead of being silently dropped. The final tranche separates
declaration defaults from a serialized set of explicit instance override IDs.
Unreal now inherits edited defaults for untouched fields, preserves compatible
overrides through state renames, removes stale or incompatible overrides, and
restores the prior bag/override set on failed Reload. A managed execution test
reuses the same state-ID override across a declaration graph rename, body graph
edit, external C# rename, and four CoreCLR generations. The 507/507 managed
suite, 54/54 repository invariants, UE 5.8.1 Editor build, and full 13/13 native
Automation suite pass. M8.3 is complete and the cursor advances to M8.4
whole-game round-trip proof. See
[`STATE_OVERRIDE_MIGRATION.md`](STATE_OVERRIDE_MIGRATION.md).

### M8.4 — Whole-game round-trip proof

- [x] author representative BlockBreaker behavior visually, inspect the emitted
  C#, edit the same logic as C#, and reopen an equivalent graph
- [x] run semantic, IL, gameplay-result, asset, and packaged-build equivalence
  gates after both directions
- [x] add conflict and merge tests for external edits, stale surfaces, and
  simultaneous declaration/body changes

Evidence (2026-08-28): the pinned comparison baseline `b1d3de3` exposes real
Player `ShouldFire` and Rifle `ResolveDamage` Visual methods. One full-project
patch changes their stable Graph literals, emits both C# sources, and reconciles
their identity sidecars. Ordinary C# local renames and equivalent lexical
float spellings then reopen as equivalent Graphs at comparison commit
`e62a46e`. `verify-roundtrip` independently passes stable method identity,
bound-semantic digest, exact MethodDef IL, boundary execution cases, and Script
Asset declaration equivalence. The dual-candidate UE 5.8.1 run passes 300 frames,
one shot, one hit, two gameplay command completions, 3,631 fast/CallFrame calls,
zero legacy calls, and byte-identical Player/Rifle Script Assets on both sides.
Both candidates independently Build/Cook/Stage/Pak/Archive 249 files
(1,030,617,655 bytes), contain zero Puerts/TypeScript files, and pass the same
packaged smoke. Workbench regression now rejects an in-group stale Surface with
`UAW1005`, atomically merges declaration and method-body stages, and rejects an
external post-preview edit with `UAW1002` without partially publishing source,
identity, history, artifacts, or runtime generation. The 509/509 managed suite
and 55/55 repository invariants pass. M8.4 is complete and the cursor advances
to M8.5. See [`BLOCKBREAKER_ROUND_TRIP.md`](BLOCKBREAKER_ROUND_TRIP.md).

### M8.5 — Language-profile closure

- [x] implement flow-sensitive nullability and the scheduled `foreach` variants
  needed by real gameplay scripts
- [x] publish the supported Visual C# profile, opaque-code escape boundary, and
  diagnostics for code that cannot be represented visually

Progress (2026-08-28): semantics v0.6 carries Roslyn's exact per-expression
`NullFlowState` independently from type nullability. Graph and Authoring Surface
actions publish validated `typeNullability`/`nullFlowState` properties, and the
round-trip semantic digest includes the flow result. Regression proves a
nullable parameter is `notNull` after an early-return guard and returns to
`maybeNull` after assignment.

HIR v0.9 and semantics v0.6 now also preserve `value` versus `ref readonly`
iteration, scoped `Span<T>`/`ReadOnlySpan<T>` borrowed sequences, graph-safe
pattern enumerators, and exact `none`/`required`/`conditional` disposal. Graph
publishes copied versus borrowed-read-only value semantics and creates cleanup
regions only when C# actually owns disposal. Exact collection editing preserves
the alias mode. `await foreach` over graph-safe `IAsyncEnumerable<T>` now
preserves the exact async enumerator protocol, awaited move-next, and required
or conditional `DisposeAsync` cleanup through Semantics, Graph, strict JSON,
derived CFG, and exact collection replacement. Mutable `ref` iteration and
borrowed async iteration are deliberate profile exclusions rather than implicit
gaps.

Visual C# profile v0.3 and report v0.1 now classify `[Visual]` bodies as
bidirectionally graph-editable and unmarked helpers as source-only opaque code.
Opaque calls project as one validated node, require graph-safe value signatures,
and reject unsupported boundaries with `UVS2009`. CLI analysis publishes the
profile even when visual lowering fails; authoring workspace v0.5, the browser
prototype, and native Slate Workbench validate and display the same boundary.
The 522/522 managed suite and 59/59 repository invariants pass for this slice.
See
[`NULLABILITY_FLOW.md`](NULLABILITY_FLOW.md) and
[`FOREACH_LOOPS.md`](FOREACH_LOOPS.md), and
[`VISUAL_CSHARP_PROFILE.md`](VISUAL_CSHARP_PROFILE.md).

Acceptance: Player and Rifle logic can be changed from either surface and hot
reloaded into the same running scene without a second Blueprint behavior source.

## M9 — Debugging, reload, and observability Beta

Outcome: UnifiedScript failures can be diagnosed inside Unreal at the source,
graph, World, generation, and bridge boundaries.

### M9.1 — Native editor debug integration

- [x] bind semantic breakpoints to current managed generations from the Slate
  graph and C# source
- [x] implement pause/resume, step in/over/out, call stacks, deeper-frame locals,
  watches, bounded expression evaluation, and breakpoint migration on reload
- [x] map exceptions and native bridge failures to source span, graph node,
  UObject handle, World, and script generation

Progress (2026-08-28): the cooperative CoreCLR control plane and browser
workbench now use debug-session v0.2 with non-blocking start/wait, cooperative
Pause, Step In/Over/Out, semantic method/node stack identities, detached locals
for every active Visual frame, and bounded read-only watch evaluation. Native
Slate now consumes the same guarded protocol: graph-card and source-inspector
breakpoints survive graph rebuilds, binding state is generation-specific, all
run controls are native, stack selection navigates semantic graph nodes, and
per-frame locals/watch values remain detached. UE 5.8.1 Editor build, targeted
Authoring Surface Automation, 522/522 managed tests, and 60/60 repository
invariants passed that slice. Semantic statement controls still drive the
loopback generation; attaching those probes to live World execution remains
unfinished, so this milestone is not yet marked complete.

Progress (2026-08-28, live World diagnostics): the Slate debugger now discovers
the matching hosted PIE/Game component and exposes exact World host, script
instance, collectible generation, and UObject handles. Pause/Step Frame/
Continue operate at a non-blocking managed tick boundary. Managed lifecycle
exceptions and native bridge failures cross the host as bounded
`runtime-diagnostic/v0.1` records with exact bridge/handle categories, member
identity, portable-PDB frames, and zero `uint64` JSON precision loss. Native
Runtime parses and retains the record; Slate maps all source frames to the
smallest matching Authoring Surface span, highlights the `DebugTargetId`, and
navigates through Focus Node. Managed 524/524, UE WorldHost lifecycle/stress,
native Authoring Surface mapping, UE 5.8.1 build, and repository 61/61 gates
cover the slice. Live semantic statement suspension still requires a transport
that can preserve the CLR stack without blocking Unreal's Game Thread.

Progress (2026-08-28, live World semantic completion): `debug-serve` now
publishes a generation-guarded instrumented PE/PDB through
`/api/debug/publish-live-world`. Slate reloads the selected PIE/Game component,
binds its graph/source `DebugTargetId` set to the new runtime generation, and
uses the same `debug-session/v0.2` pause, stack, per-frame locals, and control
model as loopback execution. `OnTick` runs on a named debugger worker; statement
traps preserve the real CLR stack while worker-originated generated UObject
calls enter an MPSC queue that the World GameThread pumps. Reload, stop, detach,
and World teardown abort and drain the worker before collectible-context
disposal. Native state reads retry a concurrent running-to-paused size change,
and the script ALC shares the probe runtime with instrumented images.

The checked-in Demo now contains real nested `[Visual]` tick work plus a stable
identity sidecar. Managed HTTP coverage proves stale-generation rejection and
instrumented PE/PDB publication without source mutation. UE World Automation
proves exact statement breakpoints, non-blocking GameThread start, Step In/Out,
two-frame locals, pause-unique frame IDs, generated worker-to-GameThread bridge
calls, generation-two breakpoint rebinding, and detach while suspended. M9.1 is
complete; async state-machine traps remain later work, while process framing
was deferred explicitly to and completed in M9.3.

### M9.2 — Hot-reload reliability

- [x] make compile, artifact publication, instance state migration, event
  rebinding, generation swap, rollback, and old-context collection observable
  phases of one transaction
- [x] soak repeated success/failure reload cycles and detect retained AssemblyLoadContexts,
  UObject handles, delegates, latent work, and stale debug sessions

Evidence (2026-08-28): authoring compile/publication and live World
debug-drain/configuration/state migration/generation load-stop-start/event
rebind/swap/state commit/context retirement and collection now extend one
`unifiedscript.reload-transaction/v0.1` receipt. Compile and publication
failures retain or restore prior artifacts; runtime failures preserve or restart
the old generation and restore the captured state bag. Slate success status and
failure diagnostics consume the same structured phase list. The v0.2 UE
lifecycle soak executes 16 successful and 16 intentionally failed reloads while
holding state, one reflected Host delegate, timers, and the active generation;
it finishes with zero registered-object, delegate, pending-work, native-context,
owned-resource, stale-debug-session, and retired-AssemblyLoadContext deltas.
Managed tests pass 526/526, repository validation passes 63/63, the UE 5.8.1
Editor build passes, and `UnifiedScript.Unreal` Automation passes 14/14. M9.2 is
complete; the cursor advances to M9.3.

### M9.3 — Gameplay and bridge profiler

- [x] attribute time and count to scripts, semantic nodes, World phases, direct
  thunks, CallFrame calls, reflection fallback, batch queries, commands, events,
  allocations, and buffer pressure
- [x] expose frame timeline and aggregate views in Unreal and emit a portable
  machine-readable trace for regression comparison
- [x] add bounded versioned remote debug framing, then reproduce one gameplay
  defect and one performance regression and locate both to the responsible
  source/graph node and runtime boundary

Evidence (2026-08-28): the opt-in managed profiler now owns a bounded 120-frame
ring and emits `unifiedscript.gameplay-profile/v0.3`. Each frame and aggregate
separates script duration/allocation, stable semantic method/node timing, all
three World scheduler phases, direct thunk, CallFrame, reflection fallback,
Command/Event, Batch Trace/Overlap, and buffer-pressure deltas. Gameplay Data
schema revision 3 appends cumulative phase count/nanosecond telemetry without
moving the revision-1/2 prefixes. The native parser rejects incompatible or
unbounded captures; Slate shows the aggregate/latest-frame summary, focuses the
hottest node that resolves in the active graph, and saves exact JSON under
`Saved/UnifiedScript/Profiles`. Each semantic node also retains the bridge and
data-plane counters accumulated while it is active. One live World workload
exercises every attribution family.

The process control plane uses `unifiedscript.debug-frame/v0.1`: a fixed
24-byte `USDB` header, 1 MiB pre-allocation payload cap, bounded compilation and
invocation collections, correlated request IDs, stable `UDBF...` failures, and
one concurrent loopback TCP connection per operation. `debug-frame-serve`
publishes its actual endpoint and loaded generation. A real TCP gate holds a
wait-for-pause request open while independent Pause and Continue operations
complete.

The reproducible `ue-profile-diagnose` fixture joins a detached live-debug local
observation, compiler source spans, and baseline/current per-node profiles. It
locates the wrong `damage` value at the exact Return source/graph node and
`reflectionFallback` boundary, and separately locates a 5.00x Batch Trace
regression at the exact `traceBudget` declaration node and `batchTrace`
boundary. The report is `unifiedscript.gameplay-diagnosis/v0.1` and contains no
custom gameplay logging. Managed tests pass 534/534, repository validation
passes 65/65, the UE 5.8.1 Editor build passes, and `UnifiedScript.Unreal`
Automation passes 15/15. M9.3 is
complete; the cursor advances to M10.1.

Acceptance: a scripted gameplay defect and a performance regression can both be
located to a source/graph node and runtime boundary without custom logging.

## M10 — Performance architecture and honest Puerts comparison

Outcome: UnifiedScript is faster on representative end-to-end gameplay, not
only on a selected scalar call.

### M10.1 — Workload suite

- [x] version workloads for scalar/property calls, struct/container transfer,
  delegate storms, per-frame Actor logic, mass entities, batch queries,
  resource/spawn flows, hot reload, and the complete BlockBreaker project
- [x] capture median, p95/p99 frame time, bridge time, calls, bytes copied,
  allocations, GC, memory high-water mark, startup, reload, and package size
- [x] alternate engines and trials, freeze inputs/assets/build configuration,
  record machine fingerprints, and reject unmatched comparisons

Completed (2026-08-28): suite v1.0.0 produced all 54 scheduled captures under
one UE 5.8.1 machine/build fingerprint and the frozen runtime identities
`sha256:73607b...a7a76b` (UnifiedScript) and
`sha256:4e5f66...76cf0d` (Puerts). Each runtime assembled exactly 27 trials; the
comparison verified all nine workload definitions, three-trial medians, and the
odd/even alternating order. The checked-in schema rejects missing, duplicate,
stale, dirty-mislabeled, mismatched-environment, or misordered evidence.

The formal result is deliberately mixed. UnifiedScript wins the per-frame Actor
(p50 ratio 0.342857), 1,000-entity (0.346390), Batch Trace (bridge ratio
0.330878), and complete BlockBreaker workload (p50/p95/p99 ratios
0.898835/0.955283/0.959456). In BlockBreaker, generated batching reduces bridge
time to 33.60%, calls to 20%, and logical copied bytes to 20% of Puerts, with
zero measured runtime GC collections in the window. It loses scalar calls
(1.211389), struct/container transfer (6.734380), four-argument delegate
dispatch (3.800531), and resource load/spawn/destroy (1.642611). UnifiedScript's
matched package is 2.28% larger and startup is 5.13% slower in the complete
workload. Allocation values stay ratio-free because CLR cumulative thread
allocation and V8 used-heap growth are different collectors. Reload remains a
mechanism-qualified diagnostic because collectible assembly generation reload
and Puerts module-cache reexecution are not equivalent.

This closes M10.1 without making the M10.3 broad-product claim. The measured
losses make generated container snapshots and buffered delegate/event ingress
the first M10.2 optimization slice; direct generated calls remain the safe
fallback while data-plane schemas preserve World/generation ownership.

### M10.2 — Shared data and batching optimization

- [x] use profiler evidence to promote only high-volume stable data into
  versioned shared snapshots, command/event buffers, or batch APIs
- [x] preserve World ownership, generation checks, bounds, deterministic phase
  visibility, and fallback semantics; managed code never retains raw UE pointers
- [x] make schemas generated, extensible, measurable, and debuggable rather than
  accumulating one-off shared-memory structs

Evidence (2026-08-28): schema revision 4 adds a generic 64-byte borrowed view,
bounded per-World channel maps, fixed-container and gameplay-event example rows,
and append-only 104/200-byte Shared/Gameplay transport suffixes. Native
producers publish snapshots immediately and queue events for the next
`BeginFrame`; managed consumers receive `ref struct` `ReadOnlySpan<T>` views and
validate schema ID, row size, World host, publication ID, count/byte bounds, and
borrow kind. The live-CoreCLR v0.3 profile measures 1,024 rows over three median
samples: direct Array+Map binding costs 25,371.973 ns/row, the generated
snapshot costs 1.270 ns/row (ratio 0.000050), and buffered-event iteration costs
0.586 ns/row, with zero managed allocation. Direct generated binding remains
the fallback for low-volume or unstable shapes. The UE 5.8.1 build, 543 managed
tests, and all 16 `UnifiedScript.Unreal` Automation tests pass.

### M10.3 — Performance budgets

- [x] keep zero steady-state managed allocation on generated fixed-value fast
  paths and published data-plane loops
- [x] prevent regressions in the existing ABI and BlockBreaker gates, with
  explicit reviewed threshold changes rather than silently moving targets
- [x] require the weighted matched gameplay suite to beat Puerts by at least 5%
  before making the broad product claim; publish neutral or losing results too

Acceptance: the performance report explains where UnifiedScript wins or loses,
and the broad claim is derived from the versioned suite rather than one API.

Completed (2026-08-28): suite v1.1.0 completed all 54 alternating slots on the
same UE 5.8.1 machine/build identity and refreshed both packages. The reviewed
weighted result is 0.555769 against the 0.95 maximum, so the broad suite claim
qualifies; all 14 absolute regression gates pass. The result still publishes
the losing scalar ratio (1.030885), resource-flow ratio (1.198520), and excluded
hot-reload diagnostic (4.959958) rather than hiding them. BlockBreaker medians
are 0.4340/0.8549/1.2585 ms at p50/p95/p99 versus Puerts at
0.4740/0.9416/1.3735 ms, and package ratio is 1.023121. Full validation is
559/559 managed tests, 69/69 repository invariants, UE 5.8.1 native build, and
16/16 `UnifiedScript.Unreal` Automation tests.

## M11 — Production hardening and release candidate

Outcome: the same versioned product survives real project scale, upgrades,
long sessions, and supported build configurations.

### M11.0 — Deterministic compiled delivery chunks

- [x] define `.uschunk` v0.1 as a deterministic, bounds-checked section
  container for the existing CoreCLR PE/IL program and runtime contracts, not a
  second bytecode VM
- [x] compile canonical authoring inputs into runtime PE, declaration, binding,
  dependency, and identity sections while moving source maps and editor data to
  an optional sidecar
- [x] load and reload chunks transactionally with format/version/hash checks,
  retain loose-file compatibility during migration, and stage chunks in cooked
  packages
- [x] prove byte-for-byte deterministic output, corruption rejection, semantic
  equivalence, reload rollback, and measured startup/package impact

Acceptance: a packaged Script Asset can point at one verified `.uschunk`, load
the same collectible CoreCLR generation as loose artifacts, and start without
runtime JSON parsing on the product path.

Completed (2026-08-28): deterministic binary runtime payload codecs and the
compiler are implemented on top of the strict v0.1 envelope. Managed CoreCLR
and Unreal World hosts verify and load PE/PDB streams directly from `.uschunk`;
native UE verifies the same envelope and materializes Script State from its
binary declaration section. Script Assets are synchronized through a strict
chunk-deployment commandlet, prefer chunks at runtime, and retain an explicit
loose DLL/JSON compatibility switch. Release emission strips the optional PDB
section while keeping loose debug artifacts.

The BlockBreaker proof executes Player and Rifle from one packaged chunk for
300 frames with one shot, one hit, two command completions, 3,631 ABI v3 calls,
and zero legacy calls. Three alternating editor trials preserve the same
gameplay result on both delivery paths: chunk cold startup is 162.0194 ms versus
110.3650 ms loose (1.468032, retained as an M11.1 warmup/ReadyToRun target),
while transactional reload is 75.6999 ms versus 79.1182 ms (0.956795). The
Release chunk is one 44,886-byte file versus two required loose runtime files at
43,992 bytes total (1.020322); the five-file loose migration/debug bundle is
69,749 bytes. Reports are under
`artifacts/comparison/chunk-delivery/20260828-152425` and
`artifacts/demo/blockbreaker-20260828-152605`.

### M11.1 — Reliability and scale

- [x] run long PIE, travel, multi-World, dedicated-server, reload, GC, object
  churn, delegate, command-buffer saturation, and fault-injection soaks
- [x] enforce bounded memory/queues, deterministic shutdown, failure containment,
  and useful crash breadcrumbs
- [x] test large contracts and script sets for generation time, editor startup,
  incremental rebuild, and asset churn

Progress (2026-08-28): the versioned two-lane reliability gate runs the same
configurable v0.4 lifecycle workload in normal editor and `-server` processes.
The verified run completed 64 travel and 64 PIE cycles, 128 successful reloads,
128 missing-type rollback failures, 8,192 registered/released UObject instances,
34 UE GC cycles, and 131,072 commands. It deliberately dropped and counted
65,536 events beyond the fixed buffers, rejected capacity-plus-one async,
command, trace, and overlap requests, and ended with zero managed-World,
native-context, worker-dispatch, async, object, delegate, debug-session, owned
resource, or retired-load-context deltas. All 152 World shutdowns followed the
required worker-drain, managed-destroy, native-detach, service-stop,
handle-invalidation order. A 64-entry detached ring retained failures and
lifecycle phases, published its newest eight records into Unreal CrashContext,
and counted 1,368 oldest-entry evictions. Automation additionally proved that
a bridge-faulted component stops while a healthy sibling keeps ticking. Final
physical-memory growth was 20,193,280 bytes in editor and 31,555,584 bytes in
dedicated-server process mode, both below the 256 MiB budget. This closes the
bounded runtime, shutdown, containment, and crash-breadcrumb slice.

Completed (2026-08-28): the v0.1 large-project gate generated 256 synthetic
Contract types with 4,096 accepted members three times, compiled 256 real C#
Script Assets into one Release `.uschunk`, cold-started UE for validation,
created 256 `.uasset` files, and then updated exactly one through a one-entry
manifest. Median Contract generation was 1,141.3522 ms, one-member rebuild was
965.5414 ms, cold/single-script emits were 5,398.7625/5,123.4274 ms, and UE
validation/create/churn processes were 12,023.0525/12,539.5622/12,443.4847 ms;
all explicit budgets passed and cleanup left no scale assets. The workload also
found and fixed cross-type Roslyn property-reference ID collisions. Evidence is
`artifacts/ue/scale/20260828_163657/scale-gate.json` and
`docs/LARGE_PROJECT_SCALE.md`. M11.1 is complete; M11.2 subsequently declared
the real Win64 matrix and retained packaged `WindowsServer` as unsupported.

### M11.2 — Compatibility and packaging matrix

- [x] keep UE 5.8.1 as the primary gate and validate each additionally supported
  engine/platform/configuration explicitly
- [x] gate Editor, Development, Shipping, cook, stage, pak, archive, installed
  SDK, and clean-machine runtime dependency closure
- [x] add Linux dedicated server and other platforms only when their CoreCLR,
  ABI, cook, and lifecycle gates are real; do not imply support from compilation

Completed (2026-08-28): `tools/run-unreal-compatibility-matrix.ps1` produced
four declared UE 5.8.1 Win64 rows: Editor Development with 17 / 17 Automation,
source Game Development and Shipping with Build/Cook/Stage/Pak/Archive and
packaged `.uschunk` smoke, and an SDK-installed Game Development row built from
a Git-exported project that initially contained no plugin. Both external .NET
root variables were poisoned while each package selected its plugin-local
CoreCLR. The 171-file SDK was generated twice with identical SHA-256
`5ac04abf3edc4c48048e220ade2127034930fbf72172e478967475d3f3b52a8a`.
The gate found and fixed Debug/Release managed staging drift before passing all
nine phases. WindowsServer, Linux, and all other unproved platforms are emitted
as `not-declared`. Evidence is
`artifacts/ue/matrix/20260828-171222/compatibility-matrix.json` and
`docs/UNREAL_COMPATIBILITY_MATRIX.md`.

### M11.3 — Versioning, upgrades, and CI

- [x] publish compatibility policy and migrations for schemas, artifacts,
  bindings, Script Assets, ABI tables, saved graph layout, and generated code
- [x] add clean CI lanes for managed tests, deterministic generation, UE build,
  Automation, package smoke, workload performance, and SDK installation
- [x] support diagnostics bundles and reproducible reports without collecting
  project source or sensitive content by default

M11.3 upgrade evidence (2026-08-28): the SDK now carries
`unifiedscript.compatibility-policy/v0.1` across 16 persistent, derived,
generated, compiled, and ABI families. `upgrade-plan` deterministically
classifies current/rewrite/regenerate/rebuild/blocked inputs without embedding
source, timestamps, or absolute paths. `upgrade-apply` performs atomic,
idempotent read-old/write-current migrations for Unreal reflection v0.1-v0.3
and API Contract v0.1, while refusing partial application when any other action
remains. Five focused legacy/future/chunk/atomicity tests and
`docs/VERSIONING_AND_UPGRADES.md` cover the contract. The checked Unreal sample
scan reported 10/10 current artifacts, while two identical 174-file SDK builds
produced SHA-256
`9a6d3b92b6fccf924c669b23c7a33803552094de7f7fe8b41d0b9450bdb79133`.

M11.3 CI evidence (2026-08-28): `.github/workflows/ci.yml` and
`tools/run-ci-lane.ps1` define seven isolated lanes with clean-tree and empty
output preflight, per-phase logs, failure-safe `unifiedscript.ci-lane/v0.1`
reports, PR-safe hosted lanes, and exact UE 5.8.1 self-hosted lanes. Local
execution passed clean Debug managed tests plus repository validation, and the
generation lane reproduced all six C#/C++/node/coverage/data-plane files twice
with hashes equal to the checked-in outputs. A detached clean worktree at
`b0c67ad` kept `sourceDirty=false` through managed (27,811 ms), deterministic
generation (10,387 ms), and a from-zero 208-action UE Editor build (68,665 ms);
reports are under `artifacts/ci-clean/20260828-174511`. `docs/CI.md` owns runner
labels, environment contracts, triggers, retry isolation, and failure routing.
The remaining M11.3 slice is the privacy-safe diagnostics bundle.

M11.3 diagnostics evidence (2026-08-28): `diagnostics-bundle` and the bundled
`unifiedscript.diagnostics-policy/v0.1` initially admitted 26 exact report schemas,
enforce report/count/bundle bounds, exclude source-derived schemas and raw logs,
redact path/environment/credential/identity/source/message fields, anonymize
entry names, and emit fixed-order/fixed-time ZIPs plus sanitized-entry hashes.
Four focused tests prove byte-identical repeat builds, allowlist exclusion,
redaction, bounded overflow, and CLI use of the bundled policy.
`docs/DIAGNOSTICS_BUNDLES.md` documents the support/review boundary. M11.3 is
complete. A real bundle over the detached clean-CI evidence admitted 6 of 22
files, performed 692 redactions across 59,339 sanitized bytes, and reproduced
SHA-256 `35fffcfc42737510955f7cc59dd4f88b2d7d1d78d9a333dc46fe9d358f927838`
without paths, credentials, source, or raw-log entries. Two 178-payload SDK
archives reproduced SHA-256
`23c4f0665449f6840be679e2ee7cd0667b48660fd5755cd31e33ac15185ba9cf`
and carried the policy, guide, and CLI default copy. Final validation is 570 / 570
managed tests and 76 / 76 repository invariants; the cursor advances to
measured compiled program image v2 work.

### M11.4 — Compiled program image v2

- [x] profile `.uschunk` cold load by file I/O, whole-file/section copies,
  digest verification, binary decode, CoreCLR generation load, and first-use JIT
- [x] measure eager whole-file/section copies and promote bounded zero-copy,
  memory mapping, or interned tables only where results prove a material win;
  current copy time is too small to prioritize this over code generation
- [x] split editor/debug payloads from the Shipping program image and cache
  verified content identities without weakening transactional reload or
  tamper/corruption detection
- [x] run a controlled ReadyToRun experiment against the same exact-identity
  chunk and reject promotion when cold startup, JIT preparation, or delivery
  size fails the materiality budgets
- [x] move bounded entry-method prewarming outside actor-tree activation,
  cancel it with its generation, and schedule initial work after first Tick so
  it cannot delay sibling activation, transactional reload, or World teardown
- [x] retain deterministic CoreCLR PE/IL as executable code and JSON as the
  readable authoring/import/export format; do not create a second custom VM
  whose semantics can drift from C# and the visual graph

M11.4 profiling evidence (2026-08-28): `chunk-profile` publishes the versioned
22-phase `unifiedscript.compiled-chunk-load-profile/v0.1` report, and
`tools/measure-compiled-chunk-load.ps1` aggregates isolated process-first
trials without storing the input path. Two focused tests account for the
baseline stream path's two whole-file copies, every digest/decode/load phase,
PE/PDB materialization, and explicit `RuntimeHelpers.PrepareMethod` work. The
safe diagnostics policy now admits these two profile schemas as 27th and 28th
allowlisted report families without admitting source-derived artifacts.

Five independent .NET 10.0.203 processes loaded a freshly emitted 84,145-byte
Release Demo chunk at a 49.4572 ms median; load plus 12-method JIT preparation
was 59.4990 ms. Dependency validation/hash (10.5419 ms), manifest decode and
first-use code (8.1901 ms), entry-type validation (7.3501 ms), whole-file SHA
(6.3039 ms), and entry-method JIT preparation (6.2841 ms) dominate. The two
whole-file copies plus one 82,432-byte PE copy moved 250,722 bytes but consumed
only 0.1024 ms median, so measurement routes the next slice to verified
dependency caching and ReadyToRun/prewarming before memory mapping. Evidence is
`artifacts/chunk-profile/m11.4-demo-process-v2/process-profile.json`.
Two deterministic 180-payload SDK archives reproduced SHA-256
`f5fb0a649fed106c73462a6cf24f44d90c3c914e39d57120f6a78531a4788403`;
the extracted SDK's packaged runner then passed three process-first trials
against the same chunk without relying on a source-tree CLI.

M11.4 dependency-cache evidence (2026-08-28): CoreCLR and Unreal World hosts
now reuse bounded, content-digest-keyed immutable dependency snapshots across
collectible generations. Misses verify SHA-256 plus CLR name/version/MVID under
a write-excluding file handle; default-context assemblies are permanently bound
to the first accepted digest, and ALCs load the verified bytes rather than
reopening a path. The cache defaults to 128 entries / 256 MiB, uses LRU
eviction, performs file/hash work outside the global cache lock, and atomically
publishes one snapshot under concurrent loads. Three focused cache
tests plus the existing Unreal loader test cover cold/warm counters, changed
disk evidence, clearing, eight-way concurrency, bounds/eviction, and two World
ALC generations.

For the six-dependency Release Demo chunk, warm dependency validation fell from
3.0522 ms to 0.1338 ms median and complete warm load from 4.3903 ms to 1.0748 ms
across 14 warm trials (75.5% lower total load). Five independent processes
still performed six misses each and reported 45.5446 ms cold load versus the
previous 49.4572 ms, so process-first integrity work was not bypassed. The cold
report explicitly accounts for 2,507,264 dependency snapshot bytes and
2,757,986 total copied bytes. Evidence is
`artifacts/chunk-profile/m11.4-dependency-cache-warm-v3.json` and
`artifacts/chunk-profile/m11.4-dependency-cache-process-v2/process-profile.json`.
The 575 / 575 managed suite, 78 / 78 repository invariants, and installed UE
5.8.1 WorldHost Lifecycle Automation pass. Two SDK archives with 179
manifest-listed payloads plus the manifest reproduced SHA-256
`0e969ad5a81f591de21ab4d4fc9f8f6555dd6563d755b95ad3ff0f95eb2c6c92`;
the extracted SDK profiler then passed three independent process-first trials.
The cursor advances to the controlled ReadyToRun/prewarming experiment.

M11.4 ReadyToRun evidence (2026-08-28):
`tools/measure-readytorun-chunk.ps1` extracts the exact chunk IL, requires
matching .NET/Crossgen2 versions, verifies assembly name/version/MVID after
Crossgen2, repacks only the managed section, and emits the privacy-allowlisted
`unifiedscript.readytorun-experiment/v0.1` report. Nine process-first trials per
variant measured the 84,145-byte IL chunk at 51.0447 ms load / 6.9704 ms method
preparation and the 173,745-byte R2R chunk at 52.2050 ms / 6.7121 ms. R2R-to-IL
ratios were 1.022731 cold load, 0.962943 preparation, and 2.064829 bytes, failing
the 0.95 / 0.95 / 1.50 promotion gates. R2R is therefore explicitly rejected
for the collectible memory-stream delivery path. Two SDK archives with 180
manifest-listed payloads plus the manifest reproduced SHA-256
`a401a8c45f6b2c6a235db5e879bc022d84dafff4b203d20cd4631b332c73be38`;
the extracted packaged experiment tool passed a three-trial-per-variant smoke.
Repository validation is 79 / 79; the cursor advances to bounded,
generation-owned background prewarming.

M11.4 background-prewarm evidence (2026-08-28): every collectible World
generation now owns an optional operation. Reload candidates start it after
`OnStart` and event binding succeed; initial generations start it after first
Tick so sibling actor-tree activation cannot compete with their JIT work. It
defaults to 64 methods / 8 ms, enforces hard 1,024 method / 100 ms limits,
contains preparation failures, never waits at Tick, and is cancelled without
joining during rollback, reload retirement, or World teardown. The live
snapshot is an additive field on the existing managed reload transaction and
the environment-disable path is reserved for controlled A/B evidence.

`tools/measure-generation-prewarm.ps1` emitted one deterministic probe chunk
and ran 15 alternating cold processes per variant with the same 16 ms frame
gap. Disabled first Tick was 353,800 ns median versus 26,600 ns with background
prewarm (ratio `0.075184`, 92.5% lower); all 15 runs completed 28 methods before
Tick. Background scheduling was 1,145,300 ns median and cancel plus collectible
session unload was 334,800 ns, below their 2 ms critical-path gates. Eight new
focused tests cover explicit scheduling, count/time bounds, running and
pre-activation cancellation, failure containment, non-blocking first-Tick
observation, CLI evidence, and privacy-safe output. Evidence is
`artifacts/chunk-profile/m11.4-background-prewarm/generation-prewarm-experiment.json`.
The standalone probe proved the operation can finish before a controlled first
Tick, while matched BlockBreaker evidence showed that starting one operation
per component during actor-tree activation creates cross-component contention.
The product policy therefore preserves the bounded operation but starts initial
generation work after first Tick. The remaining M11.4 closure was the
acceptance-level compiled-versus-loose cold-start gate; the product does not
replace deterministic PE/IL with a second VM.
The complete managed suite passes 583 / 583 and repository validation passes
80 / 80. Installed UE 5.8.1 rebuilt both changed Automation translation units;
WorldHost Lifecycle and LifecycleStress passed with the new
`backgroundPrewarm` transaction assertion and zero retained collectible
contexts. Two 182-payload SDK builds reproduced SHA-256
`df0e40d1421f4ac8708746758f12e0137b3c15970f8cb72d07fa53f3f22f717c`,
and the extracted packaged tool passed five trials per variant. A twice-built
31-report diagnostics bundle performed 151 redactions and reproduced SHA-256
`6b99087fe41ce7a7c015674573bf32a875556051d79e4fc3ba14eba658ebfa2c`.

M11.4 cold-start closure evidence (2026-08-28): the seekable chunk reader owns
one exact-length whole-file buffer instead of copying through an intermediate
stream. One process-level bounded loader prewarm prepares dependency snapshots
before binary-codec JIT, never blocks script activation, reports a safe live
snapshot through reload transactions, and contains all failures. The default
limits are 32 dependencies / 64 MiB / 50 ms cooperative active work with hard
256 / 512 MiB / 500 ms caps. A zero-copy section stream was rejected after a
1.737637 BlockBreaker regression. Loader R2R improved an isolated CLI run to
0.820809 of IL but regressed the real five-trial BlockBreaker gate to 1.052608,
so deterministic IL remains the product path.

The strict `unifiedscript.blockbreaker-chunk-delivery/v0.2` gate ran five
alternating cold processes per mode and now exits nonzero above a `1.0` startup
ratio. Chunk startup was 75.8024 ms versus 78.1151 ms loose (`0.970394`), reload
was 75.2471 ms versus 75.6904 ms (`0.994143`), and reload generation load was
1.2190 ms versus 1.6740 ms (`0.728196`). All trials executed the same gameplay,
ABI v3 fast/CallFrame calls, zero legacy calls, committed reload, prepared codec,
and zero prewarm failures. Evidence is
`artifacts/comparison/chunk-delivery/20260828-203527/chunk-delivery.json`.
M11.4 is complete and the cursor advances to M12.1.

Final validation rebuilt the changed UE 5.8.1 native component and both
Automation translation units. WorldHost Lifecycle passed the compiled
Start/Tick/reload/Stop demo, and LifecycleStress passed 8 travel cycles, 8 PIE
cycles, 16 successful plus 16 rolled-back reloads, 256 object registrations,
4,096 commands, 24 ordered shutdowns, and zero retained collectible contexts.
The complete managed suite passes 588 / 588 and repository validation passes
81 / 81. Two 182-payload SDK archives reproduced SHA-256
`3351ad754c7f3d1b7978454d31f500fd4ea7aa27802875dfafb35ce5b70eb507`;
the extracted archive then passed payload verification and transactional install
into an isolated project fixture.

M11.4 acceptance: the compiled path performs no gameplay-authoring JSON parse,
retains loose/chunk semantic equivalence and failure containment, and its
matched cold-start median is no slower than the loose compatibility path.

Acceptance: an RC archive installs and upgrades in clean project copies, passes
the declared matrix, and survives the soak budget with no unresolved blocker.

## M12 — Migration ecosystem and UnifiedScript 1.0

Outcome: teams can evaluate, migrate, ship, and maintain UnifiedScript without
requiring repository authors to operate their project.

### M12.1 — Puerts migration workflow

- [x] scan a Puerts project into an inventory of modules, APIs, lifecycle hooks,
  assets, delegates, async patterns, and unsupported dynamic behavior
- [x] generate a migration plan, API coverage report, stubs/contracts, and
  side-by-side verification harness; do not promise blind one-click conversion
- [x] support incremental coexistence during migration while preventing both
  runtimes from owning the same gameplay lifecycle accidentally

M12.1 scanner evidence (2026-08-29): `ue-puerts-scan` publishes strict
`unifiedscript.puerts-project-scan/v0.1` reports from automatically discovered
or explicitly selected TypeScript/JavaScript and native roots. Authored source
roots win over deployed JavaScript, Puerts and UnifiedScript runtime plugin code
stay outside automatic gameplay ownership, and config/native evidence remains
fingerprinted. Findings classify modules, script types, Unreal/Puerts APIs,
lifecycle, resources, delegates, async work, native surfaces, and unsupported
dynamic behavior with exact locations, migration actions, and info/review/blocker
risk. The scan is bounded, reports incomplete traversal instead of dropping
inputs silently, and `--fail-on-blockers` is suitable for automation.

The real BlockBreaker source reproduced as four authored scripts, five target
native headers, three relevant config files, and 66 findings: all 29 Unreal
member APIs from the older exact inventory plus nine newly exposed type
dependencies, 18 review items, and zero blockers. Its two dynamic review items
are the known `ue` module augmentation and `UE.Object.prototype` extension.
Five scanner tests cover custom roots, alias and instance/type inference, all
required categories, deterministic JSON/fingerprints, source-over-deployment
ownership, pure-JS fallback, excluded dependencies, bounded failure, strict
round trip, and CLI blocker exit behavior.

M12.1 planner evidence (2026-08-29): `ue-puerts-plan` transactionally emits a
ten-file, hash-manifested migration package with a five-phase/66-item plan,
API coverage, grouped Contract/service requests, compile-safe managed TODO
stubs, an exactly-one-runtime World ownership policy, a 64-case side-by-side
harness, and pending evidence templates. The plan has 48 planned, 18 review,
and zero blocked items. Against the current BlockBreaker API Contract, 22 of 38
API/type dependencies are covered: exact/unique-owner bindings, five existing
World/resource service routes, and one property-setter adapter; 15 remain
explicit requests and the prototype extension remains manual. `ue-puerts-verify`
strictly rejects stale metadata, duplicates, missing/pending evidence, and exact
observable mismatches. The pending templates correctly return exit 65, so this
slice does not claim new live runtime equivalence. Four focused planner/verifier
tests plus the five scanner tests pass. See `docs/PUERTS_MIGRATION.md`.

M12.1 coexistence evidence (2026-08-29): the strict deterministic
`unifiedscript.puerts-coexistence/v0.1` manifest defaults every discovered type
to Puerts and promotes only explicit `SourceType=Managed.Type` routes.
`ue-puerts-coexist` and the generated `PuertsOwnership.generated.ts` share the
native World arbiter with `UUnifiedScriptComponent`; a host/lifecycle key can
have only one claimant and runtime owner, stale weak claims are pruned, World
shutdown clears ownership, and live reload cannot switch routes. The real
BlockBreaker manifest retains `TS_BaseGun` and `TS_Player` on Puerts while
promoting `TS_Rifle` to
`BlockBreaker.UnifiedScript.BlockBreakerRifleScript`. Seven focused managed
tests, the UE 5.8.1 native build, and
`UnifiedScript.Unreal.Coexistence.RuntimeOwnership` pass. Full regression is
600/600 managed tests, 84/84 repository invariants, and 18/18
`UnifiedScript.Unreal` Automation tests. The manifest deliberately does not
rewrite spawn/asset routing or claim gameplay parity. M12.1 is complete and the
cursor advances to M12.2's versioned SDK and documentation release surface.

### M12.2 — SDK, documentation, and samples

- [x] publish versioned plugin/SDK archives, release notes, upgrade guides,
  onboarding, authoring, API, debugging, performance, packaging, and migration docs
- [x] ship minimal, gameplay, networking/server, data-plane, and visual/code
  round-trip samples with automated acceptance reports
- [x] provide project templates and an API/diagnostic browser generated from the
  same contracts consumed by the compiler and editor

M12.2 release-surface evidence (2026-08-29): strict
`unifiedscript.product-release/v0.1` now binds `1.0.0-rc.1` to plugin version
1000001, the Win64/UE 5.8.1/.NET 10 support boundary, archive identity,
acceptance gates, release notes, known limits, and a unique 24-entry catalog
covering onboarding, authoring, API, debugging, performance, packaging,
upgrades, support, and Puerts migration. SDK packaging rejects release/plugin
version drift, unsafe or missing documents, machine paths, native build output,
and unmanifested payloads. Its short, bounded system-temp staging also avoids
Windows path-length failure in deep CI output directories.

The clean detached worktree at `e3c8ee1` passed all nine phases in
`artifacts/ue/matrix/m12-2-rc1-clean/compatibility-matrix.json` with
`sourceTreeDirty=false`: 18/18 Editor Automation tests, source Development and
Shipping Build/Cook/Stage/Pak/Archive/runtime smoke, two byte-identical
198-payload SDK archives, clean-project installation, installed-CLI chunk
emission, and installed-project Development package smoke. The 8,365,548-byte
SDK SHA-256 is
`14ea45a52bf5a04515a0b9d49449581e355453480275aa520eff1cf6289a3c41`.
The gate first exposed and then verified fixes for expected native stderr being
misclassified as an Automation failure and 269-character SDK staging paths.
Managed tests pass 600/600 and repository validation passes 86/86.

M12.2 sample-matrix evidence (2026-08-29): the clean source worktree at
`82120e1` executed all five versioned catalog entries through one aggregate
runner. `artifacts/samples/m12-2-final-82120e1/sample-matrix.json` reports 5/5
passed in 492,360 ms, including 600/600 managed tests, 86/86 repository
invariants, 18/18 UE Automation tests, Development package smoke, 300-frame
BlockBreaker gameplay, dedicated-server World lifecycle, measured gameplay
data-plane routes, and Visual/C# semantic, IL, execution, UE, and package
equivalence. The report records `source.preserved=true` and
`comparisons.preserved=true`; all three comparison repositories finished clean.
After replacing machine-specific sample commands with environment variables,
two SDK package runs produced byte-identical 199-payload, 8,369,473-byte
archives with SHA-256
`e8a92010b9c2f5f44f61c31d16b32487c625e6e30e9810297e1bc47b8a689d3a`.
The server sample intentionally does not claim client/server gameplay
replication or a packaged WindowsServer target. The cursor advances to M12.2's
project templates and generated API/diagnostic browser.

M12.2 template/browser evidence (2026-08-29): the versioned
`unifiedscript.project-template-catalog/v0.1` exposes `minimal`,
`visual-gameplay`, and `data-plane` as reviewed starting points with explicit
capabilities and non-claims. The public scaffold rendered, compiled, analyzed,
and generated Script Asset/Surface outputs for all three entries; the
`unifiedscript.project-template-acceptance/v0.1` report at
`artifacts/project-templates/m12-2-second/project-template-acceptance.json`
passes 3/3 in 13,910 ms. Contract generation now emits a deterministic
`unifiedscript.unreal-api-browser/v0.1` beside managed bindings, native thunks,
visual nodes, and coverage. The checked-in UE 5.8.1 browser contains 7 types,
110 Contract members, 121 nodes, and one stable coverage diagnostic; the
Workbench exposes the validated document through read-only
`GET /api/unreal/browser` search and route filters.

Full Apply onboarding rebuilt both native passes, regenerated all five Contract
consumers, recompiled/deployed the content-identified Demo chunk, and passed
18/18 `UnifiedScript.Unreal` Automation tests in
`artifacts/onboarding/m12-2-template-browser-full.json`. Managed regression is
602/602 and repository validation is 87/87. Two final SDK archives are
byte-identical at 204 payloads and 8,401,594 bytes with SHA-256
`9b88bd1c085adbeafa253fd6ddecf79807f5299afd3a2a3972740a942a0f31fb`.
M12.2 is complete and the cursor advances to M12.3's reviewable semantic
graph/C# diff surface.

### M12.3 — Beyond-Puerts differentiators

- [x] make semantic graph/C# diffs reviewable and mergeable without serialized
  Blueprint graph noise
- [x] let AI tools propose typed semantic patch plans that pass the same compiler
  and transaction gates as human graph edits
- [x] integrate graph/source debugging and profiling so authoring, runtime cost,
  and Unreal API boundaries are visible in one place
- [x] keep the generated data-plane architecture open to new workloads without
  weakening safety or ABI compatibility

M12.3 semantic review evidence (2026-08-29): Graph v0.13 now has strict,
deterministic `unifiedscript.semantic-graph-diff/v0.1` and
`unifiedscript.semantic-graph-merge/v0.1` review contracts. Stable semantic IDs
classify signature, control/data flow, value, binding, and structure changes;
source spans, labels, and canvas layout do not create executable noise. The CLI
emits reviewable diffs and performs conservative three-way merges for disjoint,
one-sided, identical, added, and removed Visual functions. Different edits to
the same function produce an explicit two-sided conflict report and no merged
Graph instead of guessing a node-level resolution.

Authoring preview advances to v0.2 and embeds that exact semantic diff beside
the authoritative C# before/after files. Native Slate and the browser Workbench
both show the stable-ID review before resending the same payload, transaction,
baseline/group identity, and SHA-256 preview token; cancellation remains a
zero-write path. Focused semantic/HTTP regression passes 13/13, the full managed
suite passes 610/610, repository validation passes 88/88, and the UE 5.8.1
Editor build plus 18/18 `UnifiedScript.Unreal` Automation tests pass. The merge
artifact is intentionally review evidence rather than a second source of truth:
C# publication still goes through the compiler and authoring transaction gate.
Full Apply evidence is recorded at
`artifacts/onboarding/m12-3-semantic-review-full.json`; both native passes,
deployment, determinism, and managed startup completed in 72,376 ms. Two final
SDK archives are byte-identical at 205 entries and 8,454,033 bytes with SHA-256
`5d56c9391ccb6b14a95531262879dae0497e76b9831bc00c3b9ac3f18258f2aa`.
The cursor advances to M12.3's typed AI semantic patch proposals.

M12.3 typed AI proposal evidence (2026-08-29): Core now publishes strict
`unifiedscript.ai-semantic-patch-proposal-draft/v0.1` and
`unifiedscript.ai-semantic-patch-proposal/v0.1` contracts. A length-delimited
content identity covers bounded human intent, producer/model identity, and the
canonical Patch Plan v0.21; unknown fields, tampering, stale semantic targets,
and invalid polymorphic steps are rejected. `ai-proposal-pack` turns an existing
typed plan into byte-deterministic proposal JSON for external tools.

The loopback host validates drafts, previews the complete proposal through the
existing Roslyn/Graph compiler path, and binds `proposalId` into the same
prepared-candidate SHA-256 token used by human graph edits. Commit must resend
the identical proposal, revision, transaction/group context, and token; a
different valid proposal is rejected with `UAW1012` before source, history,
artifacts, or runtime state changes. The browser Workbench now demonstrates a
real Surface-capability-to-Patch-Plan proposal and opens the normal semantic/C#
review, while native Slate unwraps and labels the same versioned preview and
commit envelopes. The full managed suite passes 614/614 and repository
validation passes 89/89. UE 5.8.1 completed both native builds, deterministic
five-artifact generation, deployment, managed startup, and 18/18 Automation
tests in 99,103 ms; the report is
`artifacts/onboarding/m12-3-ai-proposal-full.json`. Two SDK archives are
byte-identical at 205 payload entries, 26 documentation entries, and 8,604,380
bytes with SHA-256
`0fe681258dfbc96ee87c69b7ce3205b17046e3a1247e1804e582745c499cca65`.
The cursor advances to M12.3's integrated graph/source debugging and profiling.

M12.3 integrated runtime insight evidence (2026-08-29): Unreal now publishes
strict `unifiedscript.authoring-insight-request/v0.1` and
`unifiedscript.authoring-insight/v0.1` contracts that join a bounded gameplay
profile with compiler-owned method/node IDs, graph action intent, exact C#
spans, and the generated API Contract route catalog. Ranked nodes retain exact
hit/time totals, residual `managedCode`, per-node DirectThunk, CallFrame,
reflection, command/event, Trace, and Overlap attribution, current debugger
focus, and explicit unresolved state for stale semantic identities. API catalog
examples are labeled as route context rather than fabricated per-member timing.

The loopback host validates a saved UE trace against its current Surface at
`POST /api/unreal/authoring-insight`. The browser Workbench adds a three-column
Runtime Insight view with aggregate boundaries, semantic hotspots, exact
Graph/Source focus, and generated API route navigation. A live 1280x648 browser
check found no horizontal overflow or console warning/error. Native Slate now
shows the same ranked semantic hotspot, source, residual managed cost, and
dominant Unreal boundary directly below the live World controls; selecting any
row uses the existing semantic debugger navigation. Managed regression passes
616/616 and repository validation passes 90/90. Full Apply onboarding rebuilt
both UE 5.8.1 native passes, regenerated and redeployed the compiled Demo chunk,
verified deterministic outputs, and passed 18/18 `UnifiedScript.Unreal`
Automation tests in 100,980 ms; the report is
`artifacts/onboarding/m12-3-runtime-insight-full.json`. Two final SDK archives
are byte-identical at 206 payload entries, 27 documentation entries, and
8,638,577 bytes with SHA-256
`98f90840b0b6f71c55217e26fe617757c0460d4c83a21e52bc8bdf594b36734d`.
The cursor advances to M12.3's generated data-plane extension point.

M12.3 generated data-plane extension evidence (2026-08-29): the core
Gameplay/Shared Data ABI v4 remains unchanged while strict
`unifiedscript.unreal-data-plane-extension/v0.1` project contracts generate
matched native POD rows/descriptors, managed POD rows/typed channels, and a
machine-readable v0.1 catalog. BlockBreaker's `SpatialCombat` fixture adds a
bounded 88-byte target snapshot and 104-byte impact event without adding a
transport-table suffix or handwritten facade; all generated consumers share
fingerprint `0xDE4BA9E28216D610`.

Each World now owns a descriptor registry that copies metadata, rejects ABI or
compatibility drift, identity/schema collisions, wrong channel kind or native
publication phase, row/byte overflow, and mismatched fingerprints, then clears
before shared buffers and handles during shutdown. Managed acquisition accepts
the generated typed descriptor and applies its tighter bounds before exposing
the existing borrowed zero-copy span. The live data-plane profile publishes
and reads 1,024 rows on both extension channels, reports the exact fingerprint,
and records zero managed allocation; its report is
`artifacts/ue/data-plane-profile.json`.

Managed regression passes 619/619. Nine checked-in Contract/data-plane outputs
regenerate byte-identically, including all three extension artifacts. Full
Apply onboarding rebuilt both UE 5.8.1 native passes, redeployed the compiled
Demo chunk, verified generation, and passed 19/19 `UnifiedScript.Unreal`
Automation tests in 71,265 ms; the report is
`artifacts/onboarding/m12-3-data-plane-extension-full.json`. Two final SDK
archives are byte-identical at 214 payload entries, 28 documentation entries,
and 8,543,382 bytes with SHA-256
`bf1446c29cd9909604d9d58f97d8034b9caea912102b2c23cd630cfd6d6b3c30`.
The extracted SDK independently regenerated the sample extension with its
packaged CLI. M12.3 is complete and the cursor advances to M12 acceptance
closure and the 1.0 release-readiness audit.

Acceptance: the 1.0 release has an installable SDK, supported compatibility
matrix, end-to-end migration guide, complete samples, published workload report,
and no critical acceptance failure.

- [x] bind every release-manifest gate to one versioned release-readiness
  provider, including the complete matched performance workload
- [x] run the full gate from one clean Git revision and publish a strict 15/15
  `unifiedscript.release-readiness/v0.1` verdict
- [x] audit the final report for unsupported claims and critical failures, then
  promote or hold `1.0.0-rc.1` without weakening a gate

M12 acceptance closure (2026-08-29): strict evidence from clean source revision
`5be7ddeb767cf2fb67444d5ed93236a40e409764` is recorded at
`artifacts/release-readiness/m12-final-5be7dde/release-readiness.json`. The
executable 15-gate contract completed with all manifest gates passed: 619/619
managed tests, 92/92 repository invariants,
19/19 UE Automation tests, four declared UE 5.8.1 Win64 compatibility/package
rows, five samples, three project templates, and the complete 54-slot matched
workload schedule. The weighted performance ratio is 0.563208 against the 0.95
maximum, and the matched package-size ratio is 1.023745 against the 1.05
maximum. Source and all three comparison repositories remained clean before and
after the run. Windows PowerShell 5.1 long-path package enumeration and snapshot
cleanup are covered, and the performance lane now restores its comparison
checkout on both success and failure. `1.0.0-rc.1` is locally accepted; no
remote publication is implied by this evidence.

## M13 — Puerts-grade usability and capability closure

M12 proved a deep, complete BlockBreaker vertical slice. M13 turns that
foundation into a product an ordinary Unreal developer can adopt as easily as
Puerts. It does not reproduce JavaScript semantics or make every Unreal API fast
by default. It removes setup and binding ceremony from the gameplay author's
golden path, then applies the existing Contract, lifetime, coverage, and
performance machinery automatically behind that path.

The implementation order is now:

1. one-command adoption and an automatic development API surface;
2. a first-class managed Script Class that can be created and attached in UE;
3. compile, diagnostics, reload, C#/Graph, and packaging as one daily workflow;
4. external-project acceptance and production gameplay-domain coverage;
5. native-extension, platform, distribution, and measured performance closure.

An external pilot is an acceptance gate, not the first user experience. We do
not ask a pilot developer to manually assemble the system that M13 is meant to
productize.

### Ordered M13 delivery queue

This queue is the operational breakdown of the milestones below. Only one row
is active at a time so that setup, generation, and gameplay authoring do not
become three independently configured products.

| Slice | User-visible result | Exit gate | State |
| --- | --- | --- | --- |
| M13.1A-0 — packaged adoption CLI | `adopt` and `doctor` can transactionally install, diagnose, rerun, and roll back a consumer project | public packaged CLI passes clean native/Blueprint-only fixtures with deterministic no-change reruns | Complete (`9fe7f70`) |
| M13.1A-1 — Editor project setup | an adopted project carries its own CLI and exposes Preview, Repair, Doctor, and Build/Onboard in Unreal Editor | Editor actions use exactly the CLI configuration model; native replacement is deferred safely until the Editor exits | Complete |
| M13.1A-2 — native onboarding acceptance | the same adopted project builds its plugin, generated surface, managed program, and cook closure from one action | UE 5.8 build, `UnifiedScript.Unreal` Automation, packaged no-change repair, rollback, and managed cook proof all pass | Complete |
| M13.1B-1 — first automatic reflected API vertical | referencing a supported Blueprint-visible API generates typed C++, C#, and visual descriptions without hand-editing a Contract | deterministic dependency closure, exact coverage, source-linked unsupported-use diagnostics, packaged UE build/cook, and no-change validation pass on a clean consumer | Complete |
| M13.1B-2a — native reflected breadth | ordinary C# use discovers the needed native project/plugin/Engine surface and supports representative gameplay signatures without generator knowledge | type/signature matrix, semantic source discovery, deterministic generation, and zero handwritten dispatch pass on a clean consumer | Complete |
| M13.1B-2b — Blueprint discovery and surface lock | ordinary C# use discovers loaded Blueprint/generated classes by exact asset/class identity and freezes the generated development surface for team/CI use | loaded Blueprint fixture, asset-path identity, per-type localized regeneration, no-change lock, and stale-lock rejection pass | Complete |
| M13.1C-0 — attachable Script Class foundation | **Create UnifiedScript Class** produces one ActorComponent and one Actor proxy with stable native, asset, managed, and semantic identity | both classes are selectable/attachable, receive `BeginPlay`/`Tick`, expose one property/function, survive reopen, and require no handwritten facade | Complete |
| M13.1C-1 — first-Play golden path | the adopted clean project can create, compile, attach, Play, edit, reload, cook, and package the new Script Class through the installed product | the complete workflow replays headlessly from a packaged SDK with diagnostics, no manual generator command, and no source-checkout dependency | **Complete** |
| M13.2 — Puerts-grade daily workflow | Build/Play, diagnostics, reload, IDE navigation, and C#/Graph editing become one discoverable editor-native workflow | command-line contracts remain green; optional human action/time measurements expose UX regressions without replacing correctness gates | **Active** |
| M13.3 — Blueprint class parity | managed types participate in lifecycle, properties, events, input, inheritance, and Blueprint references as ordinary UE classes | proxy identity and behavior survive reload, rename, duplication, PIE multi-World, cook, and package | Pending |
| M13.4 — external production pilot | a non-BlockBreaker project migrates a representative gameplay slice through the published golden path | exact old/new observables match and a second clean checkout reproduces adoption and packaging | Pending |
| M13.5–M13.6 — ecosystem closure | native extensions, networking, platforms, distribution, and reproduced performance losses are covered | every advertised row has build, lifecycle, cook/package, smoke, upgrade/rollback, and workload evidence | Pending |

The completed M13.1A-2 slice closed in this order:

1. make a clean Blueprint-only project a valid source-plugin host by adding a
   minimal, transaction-owned native module and matching Game/Editor targets;
2. teach the project-local setup action to build both native targets before
   reflection, binding generation, managed compilation, Script Asset sync, and
   Automation, while preserving the existing native-project path;
3. prove Preview/Apply/no-change Repair/Doctor/rollback for every newly owned
   host file and `.uproject` module edit, including interrupted Apply and later
   user-edit protection;
4. adopt a fresh project from the packaged SDK and run UE 5.8.1
   Build/Cook/Stage/Pak/Archive, asserting the executable, managed program,
   CoreCLR closure, generated Script Assets, and zero Puerts/JavaScript payload;
5. rerun the managed suite, repository validator, native build, focused
   `UnifiedScript.Unreal` Automation, and packaged acceptance, then record one
   local commit and advance the cursor to M13.1B-1.

M13.1A-2 acceptance (2026-08-31): a fresh packaged SDK adopted a clean
Blueprint-only project with a transaction-owned Runtime module and matching
Game/Editor targets. Project-local Setup exported 2 types, 23 properties, 25
functions, and 2 delegates; generated deterministic C#, native thunks, visual
nodes, coverage, a managed assembly, and an 11,030-byte compiled chunk; built
both native targets; synchronized the Script Asset; and passed the configured
Automation gate. ValidateOnly then passed from the installed project, ordinary
Win64 BuildCookRun produced the executable plus UnifiedScript/CoreCLR managed
closure and cooked Script Asset, and found zero Puerts or project-authored
JavaScript files. Post-cook Doctor passed, Repair reported zero changes and zero
conflicts while preserving all four native host files, and rollback restored the
original `.uproject` SHA-256 and removed the transaction-owned installation.
The full acceptance took 220,036 ms. Evidence is at
`artifacts/adoption-native/m13-1a2-final-native-20260831/packaged-native-onboarding-report.json`;
the accepted SDK SHA-256 is
`749a715b49173d35ad7a79941ad79ddc396fc78bfe621f05fb024bc974433ca8`.

### Product usability contract

The target golden path for an existing Unreal project is:

1. run `unifiedscript adopt <Game.uproject>` or the equivalent editor install
   action;
2. choose **Create UnifiedScript Class**, select an Actor/Component/UObject base,
   and attach or select the generated asset like an ordinary Blueprint class;
3. write restricted C# against generated typed Unreal APIs, or open the same
   semantics in the visual authoring surface;
4. press Play, see compile/runtime diagnostics in the editor, reload the changed
   generation, and package through the normal project workflow.

The first playable script must require no hand-written C++ facade, member ID,
reflection export, API Contract selector, deployment manifest, generated thunk,
or onboarding JSON. Advanced users and CI can freeze and review the generated
surface, but the safety model is a product default rather than an onboarding
prerequisite.

### Puerts-ease definition of done

"As easy as Puerts" is a workflow gate, not a claim that C# reproduces
JavaScript semantics. All P0 rows below must pass from a packaged SDK before the
roadmap may describe UnifiedScript as usability-equivalent.

| P0 workflow gate | Pass condition | Current state | Owning slice |
| --- | --- | --- | --- |
| Adopt an existing project | one Editor or CLI action installs/enables all owned pieces; rerun is no-change; Doctor and rollback are available | **Passed on clean native and Blueprint-only fixtures** | M13.1A |
| Create gameplay behavior | create an ActorComponent or Actor through the installed product, attach/select it, and expose a managed property/function without writing a native host | **Passed through packaged-SDK automated creation/lifecycle and the headless name-first factory contract** | M13.1C |
| Call ordinary Unreal APIs | a newly referenced supported native or loaded-Blueprint API becomes typed and runnable without editing Contract/reflection/thunk metadata | **Passed for bounded native and loaded-Blueprint fixtures through the packaged SDK workflow** | M13.1B-2b |
| Reach first Play | create -> compile -> attach -> Play succeeds from a clean adopted project with no repository command or generated-file edit | **Passed through clean-consumer command replay, one Build/Onboard, cross-process reload, lifecycle, package, and rollback** | M13.1C-1 |
| Edit and reload | save one compatible C#/Graph change, invoke at most one explicit build/reload action, preserve valid instance state, and continue PIE or explain why restart is required | **Headless product path passed for exact asset launch, compatible live reload, stopped-instance refresh, and atomic restart-required rejection; action/time measurement belongs to M13.2G** | M13.1C / M13.2 |
| Diagnose failures | compile, API-coverage, stale-generation, reload, runtime, and package failures appear in UE with source/member navigation and one copyable diagnostic bundle | **Passed for compile/generation/coverage/stale/reload/runtime/setup normalization, source/member navigation, recovery guidance, bounded snapshot, and installed-SDK bundle export; richer IDE/debug workflow remains** | M13.1C-1c / M13.2 |
| Use C# or Graph | the same Script Class opens in either authoring view and round-trips without a second behavior implementation | **Semantic round trip passed; not yet the default class workflow** | M13.2 |
| Package normally | normal project packaging carries the generated class, managed program, runtime closure, and used API surface with actionable omission errors | **Passed in a fresh packaged-SDK consumer: both exact generated classes loaded and executed from `.uschunk` in the final EXE, plugin-local CoreCLR was enforced, post-smoke generation was stable, and rollback preserved user files** | M13.1C-1d |
| Reproduce outside BlockBreaker | a maintainer-independent user repeats the published path in a non-BlockBreaker project and a second clean checkout | **Missing** | M13.4 |

The golden-path regression records elapsed time and exact phase evidence for
adopt-to-first-Play and edit-to-observed-change. Zero manual JSON/C++ edits and
zero direct generator commands are hard gates. Human action/time budgets are an
M13.2G usability signal and are frozen only after the first external
Puerts/UnifiedScript paired replay.

Usability parity is not declared from a demo video or a repository-only fixture.
It requires a clean external project to complete the golden path and a
maintainer-independent command replay from the published SDK; human observation
may supplement that evidence but is not required to run the correctness gate.

### Competitive scope baseline (2026-08-31)

The Puerts baseline below follows its current official Unreal documentation:
[manual](https://github.com/Tencent/puerts/blob/master/doc/unreal/zhcn/manual.md),
[automatic binding](https://github.com/Tencent/puerts/blob/master/doc/unreal/en/getting_started.md),
[mixin](https://github.com/Tencent/puerts/blob/master/doc/unreal/zhcn/mixin.md),
and [repository support matrix](https://github.com/Tencent/puerts). Those are
upstream claims, not locally reproduced evidence. UnifiedScript claims remain
limited to the gates in this repository.

| Capability | Puerts practical baseline | UnifiedScript RC1 baseline | M13 decision |
| --- | --- | --- | --- |
| Reflected Unreal API reach | Blueprint-visible `UCLASS`, `UPROPERTY`, `UFUNCTION`, `USTRUCT`, and `UENUM` APIs are reflection-callable without per-member generation | exact allowlisted Contract; current product surface is seven generated types, 103 selected members, and 101 native paths | P0: generate a complete project-owned surface on demand while retaining exact coverage policy |
| Non-reflected C++ | template static binding covers constructors, overloads, fields, methods, inheritance, callbacks, and custom converters | generated DirectThunk/CallFrame routes plus explicit native extensions | P0: publish a supported native-extension contract and generator rather than handwritten facades |
| Script class model | TypeScript proxy Blueprints, engine-class extension, lifecycle/events/input/RPC, and Blueprint/native mixin | Actor/Component/Script Asset hosts with exposed state and events, but no general managed proxy Blueprint, RPC, RepNotify, or mixin-equivalent model | P0: deliver a generated managed script-class model with explicit lifecycle ownership |
| Runtime and ecosystem | JavaScript/TypeScript on V8, Node.js, or QuickJS; Node.js can consume npm/Node APIs | C# on CoreCLR with deterministic compiled chunks and .NET project loading | intentional difference: migrate behavior, do not emulate `eval`, `Proxy`, prototype mutation, or arbitrary dynamic import |
| Platform breadth | upstream publishes desktop/mobile backend coverage | packaged runtime is declared only for UE 5.8.1 Win64 | P1: add rows only after native ABI, runtime closure, cook/package, and smoke pass |
| Authoring and review | source-oriented TypeScript workflow and engine-generated Blueprint proxies | restricted C#/Graph round trip, stable semantic IDs, transactional edits, semantic diff/merge, typed AI proposals, and graph/source runtime insight | preserve as a product advantage; do not weaken semantics to gain API count |
| Performance shape | mature scalar/reflection/static binding and fast incremental reload | stronger measured batch/container/event/data-plane paths; slower scalar, resource/spawn, and reload paths in the frozen suite | P1: keep broad matched workloads and optimize only reproduced losses |
| Adoption maturity | established ecosystem and multiple UE generations/platforms | one fully migrated gameplay project plus clean SDK consumer fixtures | P0: a non-BlockBreaker production-shaped pilot is required before a general replacement claim |

Until every P0 row has pilot evidence, the supported statement is: UnifiedScript
fully replaces Puerts for the validated BlockBreaker surface and can host
Contract-selected gameplay projects. It is not yet a drop-in replacement for an
arbitrary Puerts project.

### M13.1 — External real-project pilot readiness through zero-config adoption

M13.1 produces the product path required before an external real-project pilot
can begin. Its implementation cursor starts with M13.1A and M13.1B below; the
production-shaped pilot itself is intentionally deferred to M13.4, after
UnifiedScript can install and generate its own first usable surface.

#### M13.1A — One-command existing-project adoption

Outcome: an existing UE project can become a valid UnifiedScript consumer
without learning the repository's internal artifact pipeline.

- [x] add `unifiedscript adopt <Game.uproject>` with preview/apply modes,
  transactional writes, deterministic reruns, backups, rollback, and a clear
  list of user-owned files that will not be overwritten
- [ ] discover the compatible installed engine, project modules, plugin state,
  target platforms, and existing source/Blueprint layout; require explicit
  input only when discovery is genuinely ambiguous
- [x] install/enable the plugin and SDK, create the managed workspace, generated
  directories, build/cook integration, and default diagnostics policy in one
  owned operation
- [x] add `unifiedscript doctor <Game.uproject>` to classify missing runtime,
  stale generation, engine mismatch, cook closure, and conflicting ownership,
  with safe repair actions where possible
- [x] provide the same adopt/doctor path from the Unreal editor without creating
  a separate configuration model
- [x] cover empty Blueprint-only, native C++, and already-adopted project
  fixtures, including upgrade, interrupted apply, rollback, and no-change rerun

Acceptance: from a clean compatible project, one adoption action produces a
buildable consumer; the second run has no diff; rollback restores the original
project; no hand editing of `.uproject`, `.uplugin`, `.Build.cs`, JSON Contract,
reflection, deployment, or generated source is required.

Progress (2026-08-31, M13.1A-0 packaged CLI tranche, commit `9fe7f70`): `adopt` now plans or applies
one project-contained transaction across the `.uproject` plugin merge, source
plugin, managed runtime closure, managed project/first script, onboarding
configuration, Contract, and deployment manifest. Every target is classified as
create/update/unchanged/conflict with exact SHA-256; Apply rechecks the preview
inputs, retains pre-transaction backups, upgrades only an intact previously
owned file, restores every committed file after an injected interruption, and
refuses rollback over later user edits. `doctor` classifies plugin/runtime,
configuration/deployment schema, ownership drift, UE 5.8 compatibility,
generated-surface state, and managed cook staging. Five focused fixtures cover
native, Blueprint-only, no-change, user-conflict, interrupted Apply, safe
upgrade, doctor failure separation, and generation-by-generation rollback. The
complete managed suite passes 628/628 and the repository validator passes 94/94.

The SDK now ships `Tools/unifiedscript.cmd`; a fresh packaged SDK acceptance
used only that public entry point for two clean projects, previewed 100 changes,
applied them, built both generated managed projects with zero warnings/errors,
reported 100/100 unchanged on rerun, and restored the original source surfaces
on rollback. Evidence is at
`artifacts/adoption/m13-1a-commit-20260831/packaged-adoption-report.json`;
SDK archive SHA-256 is
`98427205b0375db9f3caaae78c1cc52c4caef5e041abafb2b73dff4c9be472c2`.
This tranche does not yet claim the UE Editor entry, native Unreal build, first
binding coverage generation, or full M13.1A acceptance. M13.1A-1 now owns the
project-local tools and Editor entry; M13.1A-2 owns the native/cook acceptance.

Progress (2026-08-31, M13.1A-1 Editor project setup tranche): adoption now
installs the framework-dependent CLI, templates, setup script, and deferred
onboarding helper below `Plugins/UEUnifiedScript/Tools`. The native Editor module
registers **UnifiedScript Project Setup** and `UnifiedScript.OpenProjectSetup`;
its Slate surface exposes Preview, Repair, Doctor, and **Save, Exit & Build**.
Attached commands stream output through a non-blocking active timer. The build
path saves dirty packages, launches a helper bound to the current Editor PID,
requests normal exit, and starts native onboarding only after Windows releases
the loaded plugin DLL. Preview and Repair retain separate reports and every
operation is constructed from the same project-local CLI/configuration contract.

The final UE 5.8.1 editor target builds, the new native command-contract test and
the complete `UnifiedScript.Unreal` Automation suite pass 20/20, the managed
suite passes 628/628, and the repository validator passes 95/95. The final SDK
archive SHA-256 is
`fd6e85815b5d52ca0f537455b05c1298577c07b2d14224724a2aece38465c147`.
Its two clean packaged fixtures each installed 169 owned files, built the first
managed project without warnings, passed project-local Doctor, reran adoption
from the project-local SDK root with 169 unchanged and zero changed/conflicts,
and rolled back all 169 files. Evidence is at
`artifacts/adoption/m13-1a1-final-20260831/packaged-adoption-report.json`.

Progress (2026-08-31, M13.1A-2 native onboarding tranche): Blueprint-only
adoption now adds the minimal project Runtime module and Game/Editor targets in
the same ownership transaction. Setup requires both target builds and the Game
receipt, while the packaged source plugin remains `Installed=false`. A clean
packaged-SDK replay passed Apply, ValidateOnly, Automation, BuildCookRun,
post-cook Doctor, zero-change Repair, and exact rollback without a repository
tool path. The managed/CoreCLR closure and cooked Script Asset were present and
Puerts/project JavaScript payloads were absent. Evidence and accepted SDK hash
are recorded in the M13.1A-2 acceptance above. Automatic discovery of broader
platform/target policy remains an unchecked M13.1A product-breadth item; it does
not reopen this verified Win64 native/cook slice or preempt the M13.1B cursor.

#### M13.1B — Automatic development API surface

Outcome: gameplay authors can call ordinary Blueprint-visible Unreal APIs while
the product generates and audits the exact native/managed/visual routes.

The active M13.1B-1 vertical slice closes the existing gap between source use
and the review-only `ue-contract-propose` backend in this order:

1. analyze the adopted managed project against the current reflection and
   generated-surface lock, and emit a versioned dependency request at the exact
   source span for each missing Unreal type/member use;
2. resolve only unique, supported reflected identities into a development
   Contract overlay; keep overload ambiguity, missing reflection, unsupported
   ownership/conversion, and policy rejection as explicit diagnostics rather
   than guessing;
3. fold that overlay into project-local Setup as one bounded
   analyze -> reflect -> expand -> regenerate -> compile cycle with stable input
   fingerprints and no-change determinism; retain the reviewed base Contract as
   the release/CI freeze point;
4. prove one ordinary Actor API expansion plus ambiguous and unsupported cases
   on clean consumer fixtures, and require native/C#/visual/browser/coverage
   artifacts to agree on the same member identity before advancing to M13.1B-2.

M13.1B-1 execution checklist:

- [x] define versioned source dependency-request and development-surface report
  schemas with stable source spans, IDs, and fingerprints
- [x] scan explicit generated-API aliases or fully qualified wrapper references
  without guessing similarly named user types or scanning generated sources
- [x] resolve unique supported requests into an all-or-nothing development
  Contract overlay and preserve ambiguous, unresolved, manual, or policy-rejected
  uses as source-linked diagnostics
- [x] integrate source analysis and bounded overlay regeneration into
  project-local Setup/ValidateOnly while retaining the reviewed base Contract as
  the release boundary
- [x] cover deterministic expansion, ambiguity, missing references,
  policy rejection, generated-source exclusion, and pre-compilation project
  loading and author-facing `K2_` name projection in focused managed regression
  (6/6)
- [x] pass the full managed suite and repository validator, package the SDK, and
  prove fresh UE 5.8.1 Setup/ValidateOnly, generated-surface agreement,
  Automation, BuildCookRun, no-change Repair, Doctor, and rollback

M13.1B-2 execution checklist:

- [x] emit source-linked, collision-safe type hints and uniquely export bounded
  loaded native project, plugin, and Engine `/Script` classes on demand with
  deterministic source/Contract fingerprints and transitive struct/enum closure
- [x] extend bounded discovery to loaded Blueprint/generated classes and asset
  paths without weakening exact identity or native-route safety
- [x] generate typed C# APIs and visual-node descriptors automatically for
  referenced Blueprint-visible classes, properties, functions, structs, enums,
  containers, delegates, soft references, overloads, defaults, and ref/out shapes
- [x] resolve ordinary semantic C# type/member references, not only explicit
  generated-namespace aliases, so a missing reachable dependency triggers
  localized generation instead of a manual Contract edit or generator command
- [x] route each member through DirectThunk, CallFrame, data plane, safe
  cold-path reflection, or rejected-with-reason coverage without name guessing
- [x] keep an automatically generated surface lock for deterministic build/CI;
  require semantic review when a release or team policy freezes/shrinks/expands
  that surface, not before the first local Play
- [x] report unsupported ownership, conversion, editor-only, latent, overload,
  and platform cases at the source use site with a concrete remediation route
- [x] prove localized regeneration, stable IDs, no-change determinism, stale
  lock rejection, and zero hand-written member dispatch across consumer fixtures

B2 exit boundary: this slice closes exact loaded-Blueprint identity, the
generated surface lock, localized regeneration, and source-linked rejection. It
does not wait for exhaustive Engine API coverage, networking, every latent
shape, another platform, or further benchmark optimization. Once those bounded
gates pass, the cursor moves directly to M13.1C-0 so the next proof is a user
creating and running a Script Class rather than another backend-only fixture.

Acceptance: a newly referenced supported UE member becomes typed and runnable
through the normal build action; unsupported use fails with one source-linked
reason; generated native, managed, visual, browser, coverage, and release-lock
views agree on the same semantic member identity.

Progress (2026-08-31, first M13.1 tranche, backend safety foundation):
`ue-contract-propose` closes the dependency-request to review loop without
mutating the shipped allowlist. It consumes the planner's versioned
`contract-requests.json`, the exact reflection export, and the current Contract;
fingerprints all three; preserves policy and coverage baselines; and emits one
explicit decision per request. Unique members enter only the proposed Contract,
existing selectors remain classified, overload/type ambiguity is never guessed,
missing reflection remains unresolved, and type-construction/data-plane work is
held for manual review. The optional open-decision gate writes the diagnosable
proposal before failing. Managed regression covers deterministic input-order
normalization, unique selection, already-selected members, overload ambiguity,
missing type/member, manual routes, stale Contract fingerprints, strict JSON,
and CLI failure semantics. External project install/reflection/package evidence
and the zero-config golden path are still pending; this tranche does not claim
usability parity or the pilot complete. The complete
managed suite passes 622/622 and the repository validator passes 93/93. A local
end-to-end replay of the existing BlockBreaker scan/plan inputs classified all
16 emitted requests as one safe proposed selector plus 15 explicit open
decisions; it is regression evidence for the new loop, not the required
non-BlockBreaker pilot.

Completed (2026-08-31, M13.1B-1 source-use vertical):
ordinary managed source can now declare an explicit alias for its generated UE
API namespace and use a reflected wrapper such as `Actor.GetActorLocation`.
The new source analyzer ignores generated files and emits deterministic,
source-span dependency requests rather than editing the reviewed Contract.
The development-surface planner reuses the reflection/proposal policy, verifies
the candidate through the real managed/native generators and coverage model,
and applies no overlay unless every request is safe. Ambiguous, unresolved,
manual, and generator-policy-rejected requests retain the base Contract and
produce stable source-linked diagnostics, including an explicit unsupported
member route. Project-local Setup now performs bootstrap -> reflect -> analyze
-> expand -> regenerate -> compile, while ValidateOnly regenerates and
byte-compares the request, overlay, and report artifacts. The clean-project
template exercises `Actor.GetActorLocation` without adding it to the reviewed
base Contract; its author-facing name resolves deterministically to UE's real
`K2_GetActorLocation` reflection/native identity. Six focused managed tests
pass. The complete managed suite passes 634/634 and the repository validator
passes 97/97. SDK archive
`artifacts/sdk/m13-1b1-source-surface-k2-20260831/UnifiedScript-SDK-1.0.0-rc.1-win-x64.zip`
has SHA-256
`e217472bffd951893d45f6e21fc44f285e92f9e46b0eb2fe032a79daa8005997`.
A fresh UE 5.8.1 consumer then passed project-local Apply and deterministic
ValidateOnly, generated-surface identity assertions, Editor/Game builds,
Automation, Win64 BuildCookRun, zero-change Repair, Doctor, and exact rollback
in 234,215 ms. The packaged build contains the managed program, compiled chunk,
CoreCLR closure, and cooked Script Asset with zero Puerts or project-authored
JavaScript files. Evidence is at
`artifacts/a/b1/packaged-native-onboarding-report.json`.

This completes the first safe vertical, not the full API-coverage claim. The
first accepted scanner deliberately accepted only explicit generated-namespace
aliases or fully qualified wrapper references, and that acceptance exercised one
ordinary Actor API. M13.1B-2 owns ordinary semantic C# discovery, broader
project/Engine/Blueprint reflection, the Blueprint-visible type/signature
matrix, localized incrementality, and release-lock closure before class
scaffolding begins.

Progress (2026-08-31, M13.1B-2 ordinary-source/signature tranche): ordinary
`using` and `global using` directives now expose bare generated wrapper types;
aliases and fully qualified names remain supported. The scanner compiles source
with the adopted project's real language settings and metadata references, so
source-defined or referenced C# types win same-name collisions instead of being
captured as Unreal wrappers. Semantic receiver analysis covers parameters,
locals, properties, fields, construction, casts, defaults, `as`, and `var`
inference. Invocation analysis selects exact reflected overload selectors from
primitive argument types, `ref`/`out`, named arguments, and optional/default
arity; genuinely ambiguous calls remain source-linked diagnostics. Generated
delegate helpers such as `SubscribeOnChanged` map back to the reflected delegate
identity while preserving the authored source member name.

The real binding generator is exercised against a representative matrix of
class handles, properties, instance and static functions, object/class and soft
object/class references, structs, enums, arrays, maps, sets, multicast delegates,
overloads, defaults, and `ref`/`out`. Nine focused regressions pass, including
user/reference collision safety and generated delegate/set shapes. The complete
managed suite passes 637/637 and the repository validator passes 98/98. SDK
archive
`artifacts/sdk/m13-1b2-signature-matrix-20260831/UnifiedScript-SDK-1.0.0-rc.1-win-x64.zip`
has SHA-256
`eb205ca8313c068ff2e822e930a79e52e7a189feffdabc658bd33e7dbe97210f`.
A fresh UE 5.8.1 consumer used the natural-import first-script template and
passed project-local Apply and deterministic ValidateOnly, generated-surface
identity assertions, Editor/Game builds, Automation, Win64 BuildCookRun,
zero-change Repair, Doctor, and exact rollback in 278,254 ms. The packaged build
contains the managed program, compiled chunk, CoreCLR closure, and cooked Script
Asset with zero Puerts or project-authored JavaScript files. Evidence is at
`artifacts/a/b3/packaged-native-onboarding-report.json`.

This tranche closes ordinary source syntax and representative generator breadth;
at that evidence point it did not yet close discovery outside the current
reflection snapshot. The next M13.1B-2 tranche therefore took bounded on-demand
native reflection discovery before loaded Blueprint support and release locking.

Progress (2026-08-31, M13.1B-2 bounded native reflection discovery): Setup now
runs `ue-reflection-hints` before reflection export. Its strict
`unifiedscript.unreal-api-source-type-hints/v0.1` artifact records only generated-
namespace type names that Roslyn proves do not bind to source-defined or metadata-
referenced C# types, with stable IDs, exact source locations, and normalized
source/base-Contract fingerprints. Existing generated wrappers, unresolved first-
use wrappers, ordinary/global imports, aliases, and qualified names share the
same scan; instance receiver names and `var` never become type hints.

The UE exporter accepts those exact short names independently of the configured
module/class seed, resolves each against loaded native `/Script` classes, rejects
missing or ambiguous candidates, adds only the unique project/plugin/Engine
classes, and retains deterministic struct/enum dependency closure. Apply records
the hint artifact before reflection; ValidateOnly regenerates and byte-compares
it alongside dependency requests, the development Contract/report, and the five
binding consumers. Existing v0.1 onboarding configs remain compatible because a
missing `typeHintsOutput` derives deterministically beside `requestsOutput`.

The clean first-project template now references seed `Actor.GetActorLocation`
and previously unselected `Pawn.GetController`. Ten focused source-surface tests
pass, the complete managed suite passes 638/638, the repository validator passes
99/99, and the repository host Editor target compiles on UE 5.8.1. SDK archive
`artifacts/sdk/m13-1b2-reflection-hints-20260831/UnifiedScript-SDK-1.0.0-rc.1-win-x64.zip`
has SHA-256
`97995eea92e45af2b37f076c1a9ba89d11128be718125358a19ac819063f2cc8`.
A fresh consumer emitted exactly the `Actor` and `Pawn` hints, exported one
source-discovered class, produced a 17-type transitive reflection manifest and
two exact API requests with zero open decisions, then passed Editor/Game builds,
deterministic ValidateOnly, Automation, Win64 BuildCookRun, zero-change Repair,
Doctor, and exact rollback in 250,077 ms. Evidence is at
`artifacts/a/b4/packaged-native-onboarding-report.json`.

This closes bounded native project/plugin/Engine discovery, not the whole B2
milestone. The active cursor remains loaded Blueprint/generated-class and asset-
path discovery, followed by per-type localized regeneration, a generated surface
lock, stale-lock rejection, and the remaining editor-only/latent/platform source
diagnostics.

Completed (2026-08-31, M13.1B-2b Blueprint discovery and surface lock): the reflection
schema now accepts exact content object paths and the UE exporter can resolve an
explicit Blueprint asset path or a unique generated-class type hint through the
Asset Registry. It exports the generated class under its exact `/Game` or plugin
mount identity, records Blueprint discovery separately, and keeps content-class
members on the supported reflected route; the native generator emits a stable
diagnostic instead of inventing a C++ class/header/thunk for a non-`/Script`
type. A focused UE Automation fixture creates and compiles a transient Blueprint,
then proves both exact-asset and generated-name discovery on UE 5.8.1.

The managed generation path now writes
`unifiedscript.unreal-api-generated-surface-lock/v0.1`. The lock fingerprints the
base/effective Contract and each generated type independently, produces stable
`added`, `removed`, and `changed` type diffs, and lets Setup use an authoring
`update` mode or a team/CI `frozen` mode. The CLI verifies a frozen lock before
writing generated outputs, so drift fails atomically with a localized diagnostic.
Strict JSON, deterministic round trip, unchanged verification, per-type localized
change, and stale-lock rejection are covered by the 12 focused managed surface
regressions. The full managed suite passes 640/640, the repository validator
passes 100/100, and the complete UE 5.8.1 Automation filter passes 21/21 after
rebuilding its content-identity-bound compiled chunk.

SDK archive
`artifacts/sdk/m13-1b2b-blueprint-lock-20260831/UnifiedScript-SDK-1.0.0-rc.1-win-x64.zip`
has SHA-256
`01d30c2066570d90b21a6be0adaa602a8fb9d99bd219cabf043df48b651d1b71`.
A fresh packaged consumer passed project-local Apply, exact generated-surface
lock assertions, deterministic ValidateOnly lock verification, Editor/Game
builds, two Setup Automation tests including transient Blueprint creation and
exact generated-class reflection, Win64 BuildCookRun, Doctor, 175-file
zero-change Repair, and exact rollback in 279,057 ms. Evidence is at
`artifacts/a/b5/packaged-native-onboarding-report.json`. This closes the bounded
B2 exit boundary; exhaustive Engine API breadth, additional latent/platform
implementations, and wider project coverage remain later work rather than gates
on the first attachable Script Class. The execution cursor is now M13.1C-0.

#### M13.1C — First managed Script Class scaffold

M13.1C is deliberately split so the first attachable result is not blocked by
the full daily-authoring surface.

M13.1C-0 — attachable class foundation:

- [x] define one stable identity chain across managed class, semantic program,
  generated native proxy, Blueprint/UClass asset, and Script Asset; rename,
  duplicate, or stale identities fail explicitly rather than bind to a
  similarly named type
- [x] prove the exact Blueprint -> Script Asset reference becomes the intended
  repository-host cook entry; packaged-consumer replay belongs to M13.1C-1
- [x] add **Create UnifiedScript Class** for ActorComponent and Actor first;
  defer the general UObject/library breadth until both gameplay bases pass
- [x] generate minimal `BeginPlay` and optional `Tick` source that compiles
  without edits and reaches the automatic API surface
- [x] make the Actor selectable and the Component attachable from ordinary UE
  class/component pickers; expose one editable property and one callable
  function to Blueprint
- [x] prove create, compile, attach/select, lifecycle, stop, World teardown, save,
  reopen, and deterministic no-change regeneration in the repository host

Completed (2026-08-31, M13.1C-0 attachable class foundation):
`[ScriptAsset]` can now pin one persistent `us_...` class ID, the C# lowerer
uses it as the Script Asset semantic identity, and invalid or duplicate IDs
fail before emission. `UUnifiedScriptAsset` stores the matching GUID, Actor or
ActorComponent kind, and exact Blueprint generated-class path; component
startup validates that tuple together with the declaration's full managed type
name. The new Actor and ActorComponent Blueprint factories read the adopted
project configuration, create lifecycle C# under the managed project, append a
deployment entry, create an exact-ID `SA_*` sidecar, configure the generated
Blueprint CDO, and expose the actions in the UE Tools and Content Browser
creation surfaces. UE 5.8.1 Automation creates both kinds, verifies ordinary
Actor/ActorComponent inheritance, inherited Blueprint property/function
visibility, source/semantic/native/asset identity, saves both packages, and
removes the source, manifest mutation, and package files after the test. A
separate golden-path driver then created persistent fixtures in Editor process
10,472, ran normal Build/Onboard, loaded them in process 37,608, spawned the
Actor, attached the Component, executed `BeginPlay`/`Tick`/`EndPlay`, and
verified World teardown. ValidateOnly preserved all six deterministic output
hashes; Win64 BuildCookRun contained both Blueprint proxies and both exact Script
Asset sidecars; final cleanup restored the repository-host baseline. The full
run passed in 210,632 ms at
`HostProject/Saved/UnifiedScript/ScriptClassGoldenPath/20260831-170500/script-class-golden-path-report.json`.

This closes the repository-host foundation, not the user-operated first-Play
claim: M13.1C-1 must reproduce the workflow from a packaged SDK in a clean
adopted project, connect it to ordinary UE actions, and verify compatible edit
and reload plus packaged execution.

M13.1C-1 — first-Play golden path:

- [x] **M13.1C-1a — first Build:** connect class creation to the adopted project's single Build/Onboard
  action so no separate reflection, binding, asset-sync, or native-build command
  is visible to the gameplay author
- [x] **M13.1C-1b — edit/reload:** open the exact newly created Script Class in
  the existing Authoring Surface and prove a compatible transactional reload,
  including preserved declared instance state and atomic rollback; an
  incompatible schema change must keep the old generation live and return an
  explicit restart-required result
- [x] **M13.1C-1c — diagnostics:** expose compile, generation, coverage,
  stale-surface, reload, and runtime errors in the editor with source/member
  navigation, repair-versus-restart guidance, and one copyable diagnostic bundle
- [x] **M13.1C-1d — packaged execution:** package the newly created Actor and Component from a fresh packaged-SDK
  consumer, run the packaged smoke, rerun with no generated diff, then roll back
  the installation without touching user-owned files
- [x] **M13.1C-1e — headless workflow contract:** close name-first creation,
  canonical entry registration, post-AssetTools Setup ordering, and exact
  external-publication rollback; rebuild and replay the complete UE 5.8.1
  command-line Automation suite

M13.1C-1e P0 findings from the first interactive replay (2026-08-31):

- the generic Content Browser create-then-rename path can invoke the factory with
  `NewBlueprint` before the user supplies the final name, allowing temporary
  source, deployment, Script Asset, and managed type identities to escape;
- two visually identical filtered creation results and duplicate Project Setup
  entries make it unclear which path is authoritative;
- AssetTools can foreground the generated Blueprint editor after creation and
  hide the Project Setup surface that owns Build/Restart/Continue;
- an interrupted or failed create must remove or explicitly recover every
  product-owned partial artifact instead of leaving a half-created class.

The corrected route now collects the final name before any file or identity is
written, hides the generic create-then-rename factories, exposes one canonical
product entry, defers Project Setup until AssetTools completes, and rolls back
source, deployment, and first-play request publication on failure. UE 5.8.1 UBT,
the focused factory contract, and the full 23/23 `UnrealEditor-Cmd` + NullRHI
Automation suite pass. The exploratory UI failure remains defect evidence;
manual action/time measurement is deferred to M13.2G and is not a correctness
gate.

Progress (2026-08-31, M13.1C-1 first-Play connection): creating an Actor or
ActorComponent now persists an exact `unifiedscript.first-play-request/v0.1`
receipt and opens the adopted project's Setup surface. **Build, Restart &
Continue** runs the existing project-local dependency graph after the Editor
exits, atomically records `ready` or `failed`, and carries a relaunch marker so
the reopened Editor returns to the pending class; **Locate Generated Class**
then resolves the exact Blueprint asset rather than guessing by name. The
unattended regression uses the same deferred helper but suppresses the visible
Editor relaunch so it cannot leave an interactive process behind.

The packaged RC1 SDK at
`artifacts/m13-1c1/sdk/UnifiedScript-SDK-1.0.0-rc.1-win-x64.zip` (SHA-256
`65f8330857ce41dfb065d5fbf47306f55c416c50c09ad1784609f2bfad94fb98`)
was installed into a fresh Blueprint consumer. Its isolated creation and reload
processes (PID 51,904 and 103,700) completed the first-Play receipt, replayed 11
deterministic generated outputs, passed Actor/Component lifecycle and World
teardown, and cooked both Blueprint proxies plus both Script Asset sidecars in
184,844 ms. The containing acceptance also passed Apply, Validate, normal
Win64 Cook/Stage/Pak/Archive, Doctor, zero-change Repair, and exact rollback in
498,147 ms. Evidence is at
`artifacts/m13-1c1/acceptance/script-class-first-play/script-class-golden-path-report.json`
and `artifacts/m13-1c1/acceptance/packaged-native-onboarding-report.json`.

At this checkpoint only the single-Build connection was complete. Later
M13.1C-1c through M13.1C-1e closed diagnostics, final packaged execution, and the
headless creation/rollback workflow contract.

Progress (2026-08-31, M13.1C-1b edit/reload closure): Project Setup now derives
the exact managed source and Script Asset from the first-Play receipt, launches
the installed project-local Authoring Workbench, and opens that asset in the
Authoring Surface. Apply & Reload uses one observable transaction for running
World components and stopped/editor instances. Compatible publications migrate
stable-ID state and explicit overrides before advancing generation; a publication
that removes overridden state returns `restartRequired` with
`requiredAction=restartEditor`, leaving the old generation, Script Asset, state,
and overrides untouched.

The shipping SDK now owns `start-unifiedscript-authoring.ps1` and the complete
Workbench tree. Clean native and Blueprint-only consumers installed 183 and 187
owned files respectively, passed project-local Doctor, replayed a zero-change
project-local adoption, and rolled back every owned file. UE 5.8.1 Editor build,
22/22 Automation, 645/645 managed tests, and 104/104 repository invariants pass.
The accepted archive is
`artifacts/m13-1c1b/sdk-rerun/UnifiedScript-SDK-1.0.0-rc.1-win-x64.zip`
(SHA-256
`3b1454ce5b392453269151eae0303de23adcdecf04ed6ec5161714496fb5dcd1`).
Evidence is at `artifacts/m13-1c1b/automation-final/index.json` and
`artifacts/m13-1c1b/adoption-final/packaged-adoption-report.json`. At that
checkpoint the remaining M13.1C work was diagnostics, packaged execution, and
the headless workflow contract; those later closed in M13.1C-1c through 1e.

Progress (2026-08-31, M13.1C-1c unified diagnostics closure): the Editor module
registers one **UnifiedScript Diagnostics** Message Log. Authoring protocol
failures, reload receipts, new live-World runtime sequences, and failed Project
Setup operations normalize to one bounded record with domain, code, severity,
message, semantic member, exact source line/column, evidence report, and one
machine-readable recovery action. Source tokens use UE source navigation;
semantic member tokens reopen the Authoring Surface, activate the owning
function, select the exact tree item, and focus its graph node. Restart is shown
only for an explicit restart-required reload receipt; compiler/stale/runtime
failures say Retry, while generation/coverage/setup failures say Repair.

The latest 128 entries persist at
`Saved/UnifiedScript/EditorDiagnostics/latest.json` under the newly allowlisted
`unifiedscript.editor-diagnostics/v0.1` schema. Project Setup's **Export Support
Bundle** runs the installed privacy-safe CLI over `Saved/UnifiedScript`, writes
the ZIP outside the input tree, and copies its path. A repository-host export
included 53 reports with 10,640 redactions. SDK
`artifacts/m13-1c1c/sdk/UnifiedScript-SDK-1.0.0-rc.1-win-x64.zip` has SHA-256
`f3f77dc2bf3d509484525eb510c978386cb653d726f63b107815d2ad78ce7183`.
Clean native and Blueprint-only consumers both exported their own safe-default
ZIP, passed Doctor and zero-change project-local adoption, and rolled back 186
and 190 files. The UE 5.8.1 build, 23/23 Automation, 645/645 managed tests, and
105/105 repository invariants pass. Evidence is at
`artifacts/m13-1c1c/automation-final/index.json`,
`artifacts/m13-1c1c/support/UnifiedScriptSupport.zip`, and
`artifacts/m13-1c1c/adoption/packaged-adoption-report.json`. The active cursor
advances to M13.1C-1d packaged execution.

Progress (2026-08-31, M13.1C-1d packaged execution closure): the transactional
Blueprint-only native-host template now carries a generic, opt-in packaged
Script Class smoke. It accepts exact Actor and ActorComponent `_C` paths,
creates one isolated World, validates each generated-class identity, executes
managed `BeginPlay`/`Tick`/`EndPlay`, and requires two simultaneous managed
scripts plus two World lifecycle claims before teardown returns both counts to
zero. The smoke also requires both programs to resolve to packaged `.uschunk`
files and CoreCLR to load from the plugin-local runtime even when external
`DOTNET_ROOT` values are poisoned.

The fresh `7bc038ae628c92c5619b028c4bb38f1a72ab3e7d9bb006d7ab0b7fc3b47a42ec`
SDK consumer compiled the generated host in both UE 5.8.1 Editor and Game
targets, cooked the Actor/Component proxies and exact Script Asset sidecars,
launched the archived Development EXE, and passed the lifecycle smoke. A
post-smoke ValidateOnly run changed none of 11 deterministic outputs. The outer
acceptance then passed its own BuildCookRun, Doctor, and 0-change/0-conflict
Repair before rolling back 190 product-owned files. The original `.uproject`,
stable `DefaultEngine.ini`, and independent user sentinel retained their exact
SHA-256 values. Total outer duration was 589,585 ms; the nested Script Class
gate took 255,080 ms. Evidence is at
`artifacts/m13-1c1d/acceptance-preflight-2/packaged-native-onboarding-report.json`,
`artifacts/m13-1c1d/acceptance-preflight-2/script-class-first-play/script-class-golden-path-report.json`,
and
`artifacts/m13-1c1d/acceptance-preflight-2/script-class-first-play/packaged-script-class-smoke.txt`.
The active cursor advanced to M13.1C-1e headless workflow-contract closure.

M13.1C-1e closure (2026-08-31): the generic Actor/ActorComponent factories are
hidden from create-then-rename menus, the canonical commands collect the final
name first, Project Setup is deferred until AssetTools finishes, and external
source/deployment/request publication is one rollback-safe transaction. After
restaging one consistent Debug managed host and Demo chunk, the UE 5.8.1 build,
23/23 headless Automation tests, 645/645 managed tests, and 106/106 repository
invariants pass. Evidence is at
`artifacts/m13-1c1e/automation/unifiedscript-unreal-full-rerun.log`.

M13.1 acceptance: a maintainer-independent command replay adopts a clean external
project, creates and runs its first managed gameplay class, calls a previously
ungenerated Blueprint-visible API, reloads it, and packages it without manual
binding/configuration artifacts. Every generated dependency is classified; no
dependency is silently omitted or name-guessed. Interactive UI timing is owned
by M13.2G rather than the M13.1 correctness gate.

### M13.2 — Puerts-grade daily authoring and C#/Graph workflow

Outcome: the common edit-Play-debug loop has one obvious entry point and does
not expose generator ordering or runtime-generation bookkeeping.

- [ ] **M13.2A — editor-native workflow:** provide one authoritative create/open
  entry, name-first transactional creation, normal double-click/open behavior,
  explicit cleanup/repair, and no duplicate or create-then-rename identity route
  - [x] **M13.2A-1 — canonical Script Asset entry:** register a native Asset
    Definition so normal open/double-click reaches the exact shared Authoring
    Surface; expose exact source, generated-class, and repair actions; reject bare
    asset rename/duplicate with actionable identity guidance; cover the contract
    through `UnrealEditor-Cmd` + NullRHI Automation
  - [ ] **M13.2A-2 — transactional full-class operations:** make rename,
    duplicate, delete, cleanup, redirect/reference repair, and failure rollback
    operate on the complete source/proxy/deployment/semantic artifact set
    - [x] **M13.2A-2a — rename and duplicate transaction:** provide explicit
      complete-class actions; preserve `ClassId` on rename, mint it on duplicate,
      update proxy/sidecar/source/deployment/first-Play identities, reject
      concurrent file edits, and roll back both UE assets and external bytes
    - [ ] **M13.2A-2b — delete and recovery closure:** guard the generated
      Blueprint as well as the sidecar, delete the complete class safely, repair
      managed/UE references and redirectors, persist crash-recovery receipts,
      and replay all operations from the packaged SDK
      - [x] **M13.2A-2b1 — guarded safe delete:** reject external UE and managed
        referencers, stage both UE assets through a reversible tombstone, remove
        source/deployment/matching first-Play identity, prove byte-exact rollback,
        and reject bare destructive operations on sidecar or generated Blueprint
      - [x] **M13.2A-2b2 — reference and recovery closure:** rewrite reviewed
        managed/UE references, close generic generated-Blueprint duplication,
        persist and replay crash receipts, and verify the full lifecycle through
        the packaged SDK
        - [x] **M13.2A-2b2a — Roslyn reference plan:** preserve physical MSBuild
          source identity, plan declarations/constructors/type uses by bound
          symbol, rewrite every affected managed file inside the rename
          transaction, reject exact semantic referencers on delete, and restore
          all source encodings and bytes after a failed publication
        - [x] **M13.2A-2b2b — persisted crash recovery:** write a bounded,
          integrity-checked preimage receipt before asset mutation; advance
          explicit transaction phases; defer receipts owned by a live Editor;
          archive interrupted current bytes; and restore every external/package
          path automatically on the next startup
        - [x] **M13.2A-2b2c — UE reference and duplicate closure:** repair UE
          referencers/redirectors during complete-class operations and reject or
          absorb generic generated-Blueprint duplication
        - [x] **M13.2A-2b2d — packaged lifecycle replay:** replay successful and
          interrupted create/rename/duplicate/delete from a clean packaged SDK
          consumer and prove exact baseline restoration with zero pending receipts
- [x] **M13.2B — dependency-aware Play:** make Build/Play run the minimum
  required reflection, binding, semantic, managed compile, native compile, and
  deployment steps from one dependency graph
- [x] **M13.2C — diagnostics and IDE:** surface C# and Graph errors, unsupported
  APIs, stale generations, runtime exceptions, object-lifetime failures, and
  package omissions in UE Message Log with source/graph/member navigation;
  generate IDE solution/project references, typed API navigation, autocomplete,
  documentation links, and debugger launch/attach configuration
- [ ] **M13.2D — edit/reload:** reload changed Script Classes transactionally
  during the supported editor states, preserve declared state when compatible,
  and explain restart-required native/schema changes before applying them
- [ ] **M13.2E — one C#/Graph asset:** open the same semantic class as restricted
  C# or Graph, preserve stable IDs and comments, and show a semantic diff before
  destructive round-trip changes
- [ ] **M13.2F — product guidance:** add a short first-project walkthrough and
  task-oriented recipes for Actor, Component, Blueprint event, asset load,
  spawn, trace, UI, async, and packaging
- [ ] **M13.2G — usability regression gate:** record time/action counts for
  adopt-to-first-Play and edit-to-reload; use those measurements as regression
  budgets instead of claiming ease anecdotally

Acceptance: a new user can complete the published walkthrough from a packaged
SDK within the workflow budgets above, without repository knowledge or manual
generator commands; C# and Graph produce equivalent behavior; expected edit
errors are diagnosable from the UE editor; a compatible change reloads without
restarting the editor.

Progress (2026-08-31, M13.2A-1): `UUnifiedScriptAsset` now owns a native Asset
Definition rather than falling through to the generic Data Asset editor. Normal
open routes the selected identity into `UnifiedScriptAuthoringSurface`; the
class-specific Content Browser menu opens Authoring or the exact C# source,
locates the exact generated Blueprint, and exposes Repair. Unsafe bare rename
and duplicate are blocked until the whole-class transaction exists. UE 5.8.1
build and the focused `ScriptAsset.WorkflowContract` pass; the full CMD-only
`UnrealEditor-Cmd` + NullRHI suite passes 24/24. Evidence is at
`artifacts/m13-2a/automation/script-asset-workflow.log` and
`artifacts/m13-2a/automation/unifiedscript-unreal-full.log`. Interactive Editor
timing remains optional M13.2G usability evidence, not a correctness gate.

Progress (2026-08-31, M13.2A-2a): the Script Asset menu now exposes **Rename
UnifiedScript Class** and **Duplicate UnifiedScript Class** instead of delegating
to UE's one-asset operations. One native transaction validates the exact proxy
owner and deployment entry, transforms only the pinned `[ScriptAsset]` and class
identities, batches Blueprint/sidecar mutation through AssetTools, publishes the
C# source, deployment manifest, and first-Play receipt atomically, and saves both
packages. Rename retains the semantic `ClassId`; duplicate creates a fresh ID
and rewires the new proxy CDO to the new sidecar. Preflight byte snapshots reject
concurrent edits, while injected failures after asset mutation and after external
publication prove rollback of package paths and exact source/deployment/request
bytes. Four focused CMD-mode tests and all 25 `UnifiedScript.Unreal` tests pass
through `UnrealEditor -cmd` + NullRHI. The first full run exposed a stale mixed
managed staging set; re-emitting the consistent Debug Demo chunk restored the
runtime closure, and the authoritative rerun is
`artifacts/m13-2a2/automation/unifiedscript-unreal-final.log`.

Progress (2026-08-31, M13.2A-2b1): **Delete UnifiedScript Class** now treats the
Blueprint proxy, `SA_*` sidecar, managed source, deployment entry, and matching
first-Play request as one identity. Preflight blocks external Asset Registry
referencers and exact managed type/ClassId use in other project sources. The UE
pair is renamed to a unique tombstone before external publication, so injected
failure restores original package paths and exact source/deployment/request
bytes; successful publication then commits both asset deletions. Module-level
Editor guards reject bare destructive operations on either half of the class,
while the internal transaction carries an explicit bypass. UE 5.8.1 build, the
focused headless `ScriptClass.AssetOperations` test, refreshed 25/25 full CMD
Automation, 645/645 managed tests, and 109/109 repository invariants pass.

Progress (2026-09-01, M13.2A-2b2a): the managed project loader now retains the
physical path behind every MSBuild `Compile` item, and
`script-class-reference-plan` uses the loaded Roslyn compilation to identify the
exact target declaration, constructors/destructor, and bound type references.
Aliases, comments, strings, and unrelated same-spelled identifiers remain
untouched. The native complete-class transaction launches the packaged CLI with
the Engine-bundled .NET SDK, verifies raw-file SHA-256 before publication,
rewrites every affected source while preserving its original encoding, and
includes those bytes in the same conflict and rollback boundary as UE assets,
deployment, and first-Play state. Delete now reports exact semantic referencers
with file/line/column rather than scanning text. The UE 5.8.1 native build,
648/648 managed tests, the focused headless asset-operation test, and refreshed
25/25 full `UnrealEditor -cmd` + NullRHI Automation and 110/110 repository
invariants pass.

Progress (2026-09-01, M13.2A-2b2b): every complete-class operation now persists
`unifiedscript.class-operation-receipt/v0.1` before its first UE mutation. The
bounded receipt embeds SHA-256-checked preimages for managed/deployment/request
files plus original and target `.uasset`/`.uexp`/`.ubulk`/`.uptnl` package paths,
then advances through `prepared`, `assetsMutated`, `externalPublished`, and
`committed`. Startup recovery skips receipts whose owner PID is still live; an
orphaned receipt first archives all differing interrupted bytes and a mapping
manifest, then restores the exact preimages and removes newly-created outputs.
Normal success and in-process rollback close their receipts. The UE 5.8.1 build,
focused CMD-mode `ScriptClass.AssetOperations`, refreshed 25/25 full CMD
Automation, 648/648 managed tests, and 111/111 repository invariants pass.

Progress (2026-09-01, M13.2A-2b2c): complete-class rename now discovers every
on-disk UE package that refers to either half of the class before mutation and
adds each `.uasset`/`.uexp`/`.ubulk`/`.uptnl` preimage to the same crash receipt.
It explicitly rewrites soft paths for the generated class, Blueprint, and
sidecar, saves the repaired referencers, runs AssetTools hard-reference fixup,
deletes the old redirectors, and rejects the commit if any old dependency or
redirector remains; loaded referencer packages with unsaved changes are rejected
before mutation instead of being silently saved. Injected post-publication failure reverses the UE reference
rewrite as well as the managed and external publication. New proxies now use
the runtime `UUnifiedScriptBlueprint` type, whose exact Asset Definition rejects
Content Browser rename/duplicate; an Asset Registry creation guard also detects
and removes low-level Blueprint-only copies that bypass that UI contract. The
UE 5.8.1 build, both focused CMD contracts, refreshed 25/25 full CMD Automation,
648/648 managed tests, and 112/112 repository invariants pass.

Progress (2026-09-01, M13.2A-2b2d): Script Class creation now enters the same
write-ahead recovery protocol as rename, duplicate, and delete. Before external
publication the factory snapshots managed source, deployment, first-Play,
generated Blueprint, sidecar, and every UE package sidecar; it persists the
receipt, publishes all surfaces, saves both UE packages, then commits. Injected
post-publication failure and startup recovery both restore byte-exact preimages
and remove newly-created outputs. The receipt reader now accepts `create`, and a
current-process owner is deferred deterministically. The new packaged lifecycle
runner fingerprints the managed/deployment/UE-package surface, runs the full
`ScriptClass.AssetOperations` contract through `UnrealEditor-Cmd` + NullRHI, and
requires an identical final fingerprint with zero pending receipts. A fresh SDK
consumer passed all four success, rollback, and persisted-recovery paths plus
first-Play create/reload, two Win64 Cook/Package passes, Validate, Doctor,
no-change Repair, and SDK rollback in 569,454 ms. Evidence is at
`artifacts/m13-2a2d/clean-consumer-create-recovery-fix/packaged-native-onboarding-report.json`.
The refreshed full gate passes 25/25 CMD Automation, 648/648 managed tests, and
113/113 repository invariants.

Progress (2026-09-01, M13.2B): Apply now evaluates every expensive Build/Play
stage through `unifiedscript.unreal-build-graph-state/v0.1`. Direct inputs,
dependency outputs, and owned outputs have separate fingerprints; downstream
stages observe the produced bytes, so a rerun that reproduces identical output
does not fan out needless work. State replacement is atomic after full success,
and each report records executed/skipped totals plus localized reasons. The
Windows PowerShell 5.1 regression covers cold/no-change runs, output drift,
output-free barriers, isolated managed-body edits, and API-shape transitive
invalidation. SDK
`d233f3bc6ed23856da795e0be85e301cbc9479d17f56a42300763213c21e341e`
installed into a fresh Blueprint-only project: cold Apply executed all 13 graph
stages in 166,899 ms, immediate no-change Apply executed 0 and skipped 13 in
1,506 ms, and ValidateOnly, Editor/Game build, Automation, Win64 Cook/Package,
Doctor, zero-change Repair, and exact rollback all passed in 234,750 ms. The
full managed suite passes 648/648 and repository validation passes 114/114.
Evidence is at
`artifacts/m13-2b1/packaged-incremental-output-isolation-fix/packaged-native-onboarding-report.json`.

Progress (2026-09-01, M13.2C): diagnostics now use
`unifiedscript.diagnostic-snapshot/v0.2` to preserve stage, source/member/graph
identity, package path, World host, runtime instance, generation, snapshot, and
recovery context across producer and report boundaries. UE Message Log renders
separate navigable source, member, graph, package, and recovery tokens; Retry
opens the exact authoring identity, package inspection resolves only existing
package files, and unsafe commands are never replayed implicitly. Adoption now
generates a project-local `.slnx`, `.code-workspace`, `UnifiedScript.ide.json`,
typed API/documentation links, managed and full Build/Validate tasks, and a
CoreCLR process-attach configuration. The packaged SDK copies the required
documentation so those links work without repository knowledge. UE 5.8.1 build,
25/25 CMD Automation, 648/648 managed tests, 115/115 repository invariants, 3/3
template fixtures, and 2/2 independent packaged-adoption fixtures pass. SDK
`61cda211e1da53dfb8855a90d2faa29c98cd24c6a7511123e117059658e18669`
completed fresh native Apply, exact 13-stage no-change skipping, ValidateOnly,
Editor/Game build, CMD Automation, Win64 Cook/Package, Doctor, zero-change
Repair, and exact rollback in 237,535 ms. Evidence is at
`artifacts/m13-2c/packaged-native/packaged-native-onboarding-report.json`.

### M13.3 — Managed Script Class and Blueprint parity

Outcome: the usable M13.2 workflow expands through the ordinary UE class model
instead of requiring a pre-authored host Component for each use case.

- [ ] generate proxy Blueprint/UClass assets for reviewed managed UObject,
  Actor, ActorComponent, and BlueprintFunctionLibrary types
- [ ] support constructor/default state, Blueprint-visible properties and
  functions, lifecycle hooks, Blueprint events, Enhanced Input actions,
  delegates, interfaces, and `Super` dispatch with stable managed/source IDs
- [ ] provide an explicit managed extension/mixin-equivalent contract for
  BlueprintNativeEvent and BlueprintImplementableEvent without runtime prototype
  mutation or ambiguous multiple ownership
- [ ] preserve class/default/subobject identity across compile, cook, reload,
  duplication, rename, redirect, PIE multi-World, and package load
- [ ] make base-class change, property default editing, component composition,
  Blueprint reference, rename, duplicate, and delete ordinary editor operations
  with transactional generated-asset updates

Acceptance: a generated managed Actor is selectable from Blueprint/project
settings, receives lifecycle/input, exposes reviewed members, participates in
Blueprint inheritance/events, survives transactional reload, and packages
without a handwritten proxy class or a second behavior implementation.

### M13.4 — External real-project proof and production gameplay domains

Outcome: the golden path survives production-shaped APIs and assets outside
BlockBreaker. `D:\Project\LyraStarterGame` remains a candidate, but selection and
modification require an explicit project boundary before this slice starts.

- [ ] install the published SDK into one non-BlockBreaker UE project exclusively
  through the M13.1 adoption path and freeze a representative gameplay slice
- [ ] inventory the slice's Puerts/Blueprint/C++ boundary before migration and
  classify every dependency through the automatic coverage model
- [ ] validate UMG/UI construction and event/data binding through generated APIs
- [ ] validate AssetManager/DataAsset/soft-reference and async loading flows
- [ ] validate at least one system-heavy domain selected by the pilot, such as
  Gameplay Ability System, Animation, AI/Navigation, or project networking
- [ ] classify latent actions, dynamic/multicast delegates, interfaces,
  subsystems, components, Blueprint libraries, editor-only members, and common
  engine value/container types through the same coverage model
- [ ] promote only measured high-volume routes to generated batch/shared-memory
  channels; leave cold paths on diagnosable safe reflection
- [ ] compare exact isolated old-runtime and UnifiedScript observables before
  deleting the old owner; convert every defect into a consumer fixture and gate

Acceptance: the pilot gameplay slice no longer requires a parallel Blueprint or
Puerts behavior implementation, and exact isolated Puerts/UnifiedScript
observables match before the old runtime owner is removed. The same published
adoption steps must replay on a second clean checkout.

### M13.5 — Native extensions, networking, and ecosystem closure

- [ ] provide a versioned native-extension generator for non-reflected C++
  constructors, overloads, fields, methods, inheritance, callbacks, converters,
  ownership rules, C# declarations, visual nodes, and coverage diagnostics
- [ ] generate replication metadata, RPC routes, RepNotify callbacks, authority
  diagnostics, and reload-safe network object ownership for Script Classes
- [ ] prove client/server PIE before claiming networking and keep packaged
  WindowsServer unsupported until its independent package/smoke row passes
- [ ] classify latent actions, dynamic/multicast delegates, interfaces,
  subsystems, editor-only APIs, and common engine container/value types through
  the same automatic surface and extension model
- [ ] publish explicit guidance for .NET libraries, threading, async, native
  callbacks, runtime restrictions, AOT/platform limits, and unsupported dynamic
  language patterns rather than implying npm/Node ecosystem equivalence

Acceptance: the external pilot's required reflected, non-reflected, async, and
network routes have generated support or an explicit documented rejection; no
project-specific handwritten facade is needed for a supported shape.

### M13.6 — Distribution, platforms, and remaining performance losses

- [ ] publish a reproducible plugin/SDK distribution path with checksums,
  upgrade/rollback instructions, issue templates, and a supported-version table
- [ ] add privacy-bounded opt-in diagnostics for install, generation, runtime,
  reload, and packaging failures and replay one install/upgrade/rollback/bundle
  cycle outside the repository's HostProject
- [ ] add another explicitly supported UE/platform package-and-smoke row before
  broadening compatibility claims
- [ ] optimize the scalar and resource/spawn paths, which remain slower than
  Puerts in the frozen RC1 workload
- [ ] redesign reload measurement and user experience; keep CoreCLR generation
  replacement and Puerts module-cache reexecution as mechanism-qualified metrics
- [ ] reduce reload latency and retained allocation without weakening rollback,
  state migration, World shutdown, or stale-generation rejection
- [ ] rerun the complete matched workload and package-size suite on every newly
  declared platform row

Acceptance: a versioned SDK installs, upgrades, rolls back, diagnoses, packages,
and meets its declared reliability/performance budgets on every advertised
UE/platform row. Release criteria remain tied to reproduced failures and user
workflow evidence, not feature count.

## Global release gates

Every active milestone inherits these gates when relevant:

- deterministic artifacts: a no-change rerun produces no generated diff;
- semantic safety: graph and C# edits compile to the same intended program and
  stale edits fail atomically;
- runtime safety: no raw UObject pointer crosses as durable managed identity;
- lifecycle safety: World teardown, reload, delegates, async work, and buffers
  cannot target a retired generation;
- packaging: supported builds contain every managed/native dependency and do
  not depend on a source checkout;
- performance honesty: only matched, reproducible artifacts support comparative
  claims;
- documentation: commands and reported guarantees match the validated product.

## Explicit non-goals

- reproducing JavaScript semantics or every Puerts dynamic pattern;
- treating Blueprint assets as a second authoritative implementation;
- exposing UE memory or pointers to managed code for benchmark convenience;
- allowing arbitrary visual control flow that cannot round-trip safely;
- claiming engine or platform compatibility that has not passed its build,
  lifecycle, cook/package, and smoke gates;
- optimizing every reflected API into shared memory without workload evidence.
