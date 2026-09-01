# Unreal workflow usability budgets

UnifiedScript treats daily usability as a versioned regression contract, not a
subjective claim. The packaged SDK ships
`benchmarks/unreal/usability-budgets.v0.1.json`; the `ue-usability-budget`
command combines existing CMD receipts into one hash-bound report and can fail
automation when a workflow exceeds its reviewed action, restart, time, build,
or runtime-evidence budget.

This gate does not require an interactive Editor session. Human observation is
useful for discovering confusing labels or focus problems, but the repeatable
product gate uses packaged onboarding, `EditorAttached` build receipts, and
`UnrealEditor-Cmd` Automation evidence.

## Frozen v0.1 scenarios

| Scenario | Deliberate actions | Editor restarts | Wall-clock budget | Evidence scope |
| --- | ---: | ---: | ---: | --- |
| Adopt to first Play | at most 8 | at most 1 explained first native-build restart | 720,000 ms | conservative enclosing packaged onboarding, Script Class first Play, lifecycle, cook, package, and smoke run |
| Compatible edit to reload | at most 3 | 0 | 15,000 ms | exact `dailyEditor` receipt plus successful all-instance reload/runtime Automation |

The first scenario currently contracts seven deliberate actions: Preview,
Apply, Doctor, create/accept Script Class, Build/Restart/Continue, place or
attach, and Play. The compatible-edit scenario contracts edit/save,
review/accept Diff, and Build & Reload. It must execute exactly
`managed-gameplay-build` and `compiled-chunks`; native, reflection, or schema
work belongs to the separately explained restart-required path and fails this
compatible-edit scenario.

The v0.1 wall-clock limits are regression ceilings, not performance targets.
The first-Play value deliberately contains more work than the user-visible
path, so its report labels that value `conservativeEnclosingRun` and also emits
the narrower adoption and Script Class phase durations.

## Run the CMD gate

From an extracted SDK, pass the exact receipts produced by the packaged and
daily workflows:

```powershell
$sdk = 'D:\SDK\UnifiedScript-1.0.0-rc.1'
$firstPlay = 'D:\Evidence\packaged-native-onboarding-report.json'
$dailyReload = 'D:\Evidence\daily-managed-edit.json'
$automation = 'D:\Evidence\unifiedscript-unreal-full.log'
$report = 'D:\Evidence\usability-report.json'

& "$sdk\Tools\unifiedscript.cmd" ue-usability-budget `
  --packaged-first-play $firstPlay `
  --daily-reload $dailyReload `
  --automation-evidence $automation `
  --budgets "$sdk\benchmarks\unreal\usability-budgets.v0.1.json" `
  --out $report `
  --fail-on-regression
```

The command always writes the report after it successfully validates the input
schemas. Without `--fail-on-regression`, a measured regression is reported but
the command exits successfully for inspection. With the flag, a failed report
returns exit code `65`. Missing inputs return `66`, malformed arguments return
`64`, and invalid or oversized evidence returns `65`.

## Report contract

`unifiedscript.unreal-usability-regression/v0.1` contains:

- the exact budget ID and deterministic budget digest;
- each scenario's contracted action and restart counts, observed wall clock,
  phase durations, executed/deferred build stages, and violations;
- final project, managed type, asset, and source identity;
- separate failure and bounded repair-action lists;
- every input, deployment manifest, compiled chunk, and runtime Automation log
  with its full path, byte count, SHA-256, schema, and status.

The edit receipt alone proves which build stages ran. State preservation and
runtime behavior remain separate Automation evidence, so a fast compile cannot
masquerade as a successful reload. Likewise, the first-Play enclosing duration
is not presented as Editor click-to-Play latency.

## Current checked-in baseline

The M13.2G baseline assembled from the existing UE 5.8.1 evidence passes:

- adopt to first Play: 7/8 actions, 1/1 restart, 569,454/720,000 ms;
- compatible edit to reload: 3/3 actions, 0 restarts, 6,366/15,000 ms;
- executed daily stages: `managed-gameplay-build`, `compiled-chunks`;
- runtime proof: successful `ReloadBatchPreflight` and the preserved
  `DeclarationState=37`, `ReflectedEvent=73` demo receipt.

The generated report is an ignored build artifact under
`artifacts/m13-2g/usability-report.json`; reproducibility comes from the
checked-in budget and command, not from committing machine-specific evidence
paths.

## Changing a budget

Do not silently widen a ceiling after a regression. Change the versioned JSON
only with a reviewed rationale, retain the old report, state whether the
workflow contract or the implementation changed, and add a revision note. A
new scenario or incompatible meaning requires a new schema/budget version.
