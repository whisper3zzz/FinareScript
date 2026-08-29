# Puerts Project Migration

UnifiedScript migration starts with an evidence-producing project scan. It does
not promise blind source translation: the scanner establishes source ownership,
identifies static migration work, and calls out dynamic behavior that requires a
deliberate design decision before generated contracts or stubs are trusted.

## Project scan

Run the scanner against a Puerts Unreal project:

```powershell
dotnet run --project src/UnifiedScript.Cli -c Release -- `
  ue-puerts-scan D:\Project\MyPuertsGame `
  --out artifacts\migration\my-game.scan.json `
  --fail-on-blockers
```

If the migrated native target is a different checkout, pass
`--native-project <directory>`. `--project-name <name>` overrides the `.uproject`
name. `--script-root` and `--native-root` may be repeated to replace automatic
root discovery; each path must remain inside its owning project root.

Automatic script discovery treats authored sources as authoritative. It first
looks for `TypeScript`, `Scripts`, and `src`; only when those are absent does it
fall back to deployed `Content/JavaScript` or `JavaScript`. A `tsconfig*.json`
directory is the final fallback. This prevents compiled JavaScript, Puerts'
bundled runtime, and editor tooling from being counted as gameplay source when
the corresponding TypeScript tree is present. Dependency, build, saved,
intermediate, and reparse-point directories are excluded. Explicit roots remain
available for non-standard layouts and intentional multi-root projects.

The native scan automatically covers project `Source` and project plugin source,
while excluding the Puerts and UEUnifiedScript runtime plugin implementations.
Passing explicit native roots can opt those trees back in.

## Scan contract

The strict `unifiedscript.puerts-project-scan/v0.1` artifact contains:

- the selected script/native roots and relevant project configuration files;
- a SHA-256 source fingerprint over normalized script, native, and configuration
  inputs;
- modules, script types, Unreal APIs, Puerts APIs, lifecycle hooks, assets,
  delegates, async patterns, native surfaces, and unsupported dynamic behavior;
- exact source locations plus one proposed migration action per finding;
- `info`, `review`, and `blocker` risk levels, summary counts, scan-completeness
  state, and diagnostics.

`review` means the scanner found a static shape but cannot choose lifecycle,
ownership, or compatibility policy on behalf of the project. `blocker` means the
source shape cannot enter generated typed migration without a rewrite. Current
blockers include `eval`, the `Function` constructor, `Proxy`, non-literal dynamic
module loading, and computed Unreal member access. Prototype mutation, module
augmentation, global mutation, delegates, async work, and native surfaces remain
explicit review items instead of being silently assumed compatible.

The scan is bounded to 50,000 files, 2 MiB per file, and 256 MiB total by
default, with hard library limits above those defaults. Reaching a bound,
encountering an unreadable traversal, or finding no script source marks the scan
incomplete and emits a blocking finding. `--fail-on-blockers` returns exit code
65 for either an incomplete scan or any blocker, after transactionally writing
the report for diagnosis.

## Migration package

Generate a migration package from the scan, optionally assessing it against the
target project's current Unreal API Contract:

```powershell
dotnet run --project src/UnifiedScript.Cli -c Release -- `
  ue-puerts-plan artifacts\migration\my-game.scan.json `
  --contract Contracts\MyGame.unreal-api-contract.json `
  --namespace MyGame.UnifiedScript.Migration `
  --out artifacts\migration\my-game `
  --fail-on-blockers
```

The write is transactional and produces eight deterministic files:

- `migration-plan.json` orders every finding through project foundation, API
  Contract/assets, managed port, World lifecycle, and manual-rewrite phases;
- `api-coverage.json` records exact, unique-owner, property-setter-adapter,
  World/resource-route, type-only, missing, ambiguous, manual, or unassessed
  coverage without treating a name guess as a binding;
- `contract-requests.json` groups remaining type, member, data-plane, World
  service, and resource-loader work into explicit requests;
- `MigrationStubs.cs` provides compile-safe type/lifecycle placeholders carrying
  their work-item IDs and throwing until the port is deliberately implemented;
- `verification-harness.json` defines required observable values for both the
  `puerts` and `unifiedScript` lanes under an exactly-one-runtime-per-World
  lifecycle policy;
- two evidence templates start every case as `pending` with `TODO` observables;
- `migration-package.json` fingerprints and sizes every payload except itself.

The planner can reuse compatible target routes instead of demanding a duplicate
binding. Current adapters include a property write such as `StaticMesh =` to an
existing `SetStaticMesh` Contract member, Puerts object/class loads to the
generated resource loader, and deferred actor construction to `SpawnActor`.
Calls that belong in the command/event data plane, World services, generated
struct/enum support, or a deliberate dynamic rewrite remain visible requests.
Omitting `--contract` is supported, but every static API is then `unassessed`;
that output is planning input, not coverage proof.

The generated C# is a scaffold, not translated gameplay. It never fabricates
method signatures, object ownership, lifecycle order, or behavior from regular
expressions. `--fail-on-blockers` returns 65 after writing the full package so a
team can inspect blocked work without losing diagnostic output.

## Side-by-side verification

Run the same cases in isolated Puerts and UnifiedScript worlds, replace the two
templates with captured evidence, then compare them:

```powershell
dotnet run --project src/UnifiedScript.Cli -c Release -- `
  ue-puerts-verify artifacts\migration\my-game\verification-harness.json `
  --puerts artifacts\migration\my-game\evidence\puerts.json `
  --unified artifacts\migration\my-game\evidence\unifiedscript.json `
  --out artifacts\migration\my-game\verification-report.json `
  --fail-on-mismatch
```

The verifier rejects a blocked plan; stale project, scan, plan, or runtime
metadata; missing or duplicate cases; blocked or pending cases;
placeholder/missing observables; and any non-exact observable mismatch. The
report is still written when the gate
returns 65. Both evidence templates intentionally fail this gate until real
runtime captures replace every `TODO`, so package generation alone is never
reported as gameplay equivalence.

## Reproduced BlockBreaker evidence

The generalized scanner was run against the original Puerts BlockBreaker source
and the migrated UnifiedScript target on 2026-08-29. Automatic ownership
selection found exactly four authored TypeScript files and five project-native
headers plus three relevant configuration files. The report contained 66
findings: four modules, three script types, 38 Unreal API/type dependencies,
three Puerts APIs, six lifecycle hooks, three assets, four async patterns, two
reviewed dynamic extensions, and one native verification surface. It retained
all 29 Unreal member APIs from the older exact inventory and added nine required
type dependencies. The scan was complete with 18 review items and zero blockers.
The two dynamic extensions are the known `ue` module augmentation and
`UE.Object.prototype` helper; both require an explicit Contract/managed-extension
route rather than runtime JavaScript mutation.

This scan complements the older M7.4 exact-coverage inventory. The v0.1 M7.4
inventory remains stable so existing BlockBreaker migration evidence is not
rewritten. Planning produced five phases and 66 work items under plan
`plan-e794b715eaf4db61`: 48 planned, 18 review, zero blocked. Against the current
BlockBreaker Contract, 22 of 38 API/type dependencies are covered (including
five existing service routes and one setter adapter), 15 remain explicit
Contract/service requests, and the prototype extension remains manual. The
64-case side-by-side harness and pending evidence templates were generated and
the template-only mismatch gate correctly returned 65; no new live runtime
equivalence is claimed by this planning slice.
