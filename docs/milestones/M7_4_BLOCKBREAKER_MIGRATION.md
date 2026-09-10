# M7.4 Full BlockBreaker Runtime Migration

M7.4 turns BlockBreaker from a selected gameplay-path comparison into a
whole-project migration proof. The source Puerts project remains the immutable
reference; the Finare target is inventoried, covered, cleaned, built,
packaged, and exercised as an independent product.

## Deterministic migration inventory

`ue-puerts-inventory` scans the source `TypeScript` tree and the target native
headers, then writes three strict and deterministic artifacts:

- `unifiedscript.puerts-project-inventory/v0.1` records every discovered source
  behavior and target native facade;
- `unifiedscript.puerts-migration-coverage/v0.1` assigns each item an explicit
  migration route and evidence;
- `unifiedscript.puerts-migration-report/v0.1` rejects fingerprint drift,
  uncovered items, stale coverage entries, unsupported routes, and unexplained
  native facades.

The tracked BlockBreaker inventory contains 74 items: four scripts, three
script types, 13 script methods, six lifecycle hooks, four imports, two Puerts
APIs, 29 Unreal APIs, zero delegates, three resources, three global patterns,
four async patterns, two decorators, and one native facade. All 74 items are
covered by generated bindings, Script Assets, managed script, the generic data
plane, the core runtime, project-native construction, deliberate elimination,
or an explained verification-only route.

The only remaining native facade is
`AFinareBlockBreakerCharacter.ReportManagedTick`. It increments the
exact-frame acceptance counter and terminates automated runs. It contains no
movement, weapon, trace, damage, resource, or spawn policy. The former camera
placement, gun mount, shot, hit, and status facade behavior now uses generated
SceneComponent calls and generated property access.

## Whole-project audit

Run the reproducible audit from the main repository:

```powershell
powershell -ExecutionPolicy Bypass `
  -File comparison/audit-blockbreaker-migration.ps1 `
  -EngineRoot D:\UE_5.8 `
  -ComparisonRoot D:\Project\UEUnifiedScriptComparison
```

The audit requires:

- 74/74 inventory items covered with the exact source/target fingerprint;
- no `Plugins/Puerts`, `TypeScript`, `Typing`, `Content/JavaScript`, legacy
  TypeScript Blueprint assets, Puerts config, or `tsconfig.json` in the target;
- only `Finare` enabled and no Puerts/JavaScript/TypeScript module
  dependency in the target build rules;
- 19/19 non-map BlockBreaker gameplay assets byte-identical with the source;
- exactly two deployed Finare Script Assets;
- nine contract types and 40 members producing 40 native thunks, with zero
  reflection fallback and zero rejected bindings.

Comparison commit `489ce9b` removes 154 tracked legacy files, approximately
55 MB, while keeping sample gameplay outside the plugin.

## Runtime and package evidence

The migrated UE 5.8.1 Editor target built and completed a 300-frame headless
run with one shot, one hit, two command completions, 3,631 fast calls, 3,631
CallFrame calls, and zero legacy calls.

The Development package at
`artifacts/demo/blockbreaker-20260827-165719` contains 249 files totaling
1,030,599,735 bytes. Its packaged 300-frame run produced the same functional
result and contains zero package paths matching Puerts, JavaScript, or
TypeScript. `demo-delivery.json` hashes the migration audit beside the package.

The exact three-trial 10,000-frame comparison also completed one shot, one hit,
and both command events per trial with 120,031 fast/CallFrame calls and zero
legacy calls. The local median was 4,927.191 ms for Finare and 4,759.930
ms for Puerts, a ratio of 1.035139. This proves matched functional execution,
not a performance advantage; M10 owns the release gate requiring Finare
to be at least 5% faster on the accepted workload.

## Exit and next cursor

M7.4 is complete when the managed suite, repository validator, whole-project
audit, Editor gate, packaged gate, and asset/package exclusion gates are green.
The next RoadMap slice is M8.1 structured graph editing completeness.
