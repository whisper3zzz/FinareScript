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

## Reproduced BlockBreaker evidence

The generalized scanner was run against the original Puerts BlockBreaker source
and the migrated UnifiedScript target on 2026-08-29. Automatic ownership
selection found exactly four authored TypeScript files and five project-native
headers. The report contained 51 findings: four modules, three script types, 23
Unreal APIs, three Puerts APIs, six lifecycle hooks, three assets, four async
patterns, two reviewed dynamic extensions, and one remaining verification
surface. It was complete with zero blockers. The two dynamic extensions are the
known `ue` module augmentation and `UE.Object.prototype` helper; both require an
explicit Contract/managed-extension route rather than runtime JavaScript
mutation.

This scan complements the older M7.4 exact-coverage inventory. The v0.1 M7.4
inventory remains stable so existing BlockBreaker migration evidence is not
rewritten. M12.1 consumes the new generalized scan next to generate a migration
plan, API coverage scaffold, contracts/stubs, and a side-by-side verification
harness while retaining the scanner's review and blocker boundaries.
