# Script State Override Migration

## Purpose

M8.3 closes the instance-state half of the Visual/C# authoring loop. A C#
declaration default and a value explicitly changed on one Unreal component are
different facts: changing the default should update untouched instances, while
an instance override should survive compatible source and graph edits.

Each exposed state already has a persistent `SemanticId`. Unreal stores that ID
as the `FPropertyBagPropertyDesc::ID`; the component now also serializes the
exact set of descriptor GUIDs overridden by that instance. The editable
`ScriptState` bag therefore holds values, while `ScriptStateOverrideIds` records
which values are authoritative for the instance.

## Migration rules

When Declaration IR is refreshed, started, or reloaded, the materializer builds
a fresh bag from the new C# defaults and calls UE 5.8's
`MigrateToNewBagInstanceWithOverrides` with only compatible override IDs.

| Declaration change | Instance result |
| --- | --- |
| unchanged ID and type, explicitly overridden | retain the instance value |
| unchanged ID and type, not overridden | adopt the current C# default |
| unchanged ID and type, renamed state | retain the override under the new name |
| new ID | use the declaration default |
| removed ID | remove the value and stale override ID |
| unchanged ID with incompatible type | drop the override and use the new typed default |

`FUnifiedScriptStateMigrationReport` records declared, preserved, defaulted,
and dropped counts. The component exposes the active override count and a
bounded `LastStateMigration` summary. Existing components saved before explicit
tracking conservatively treat their old bag values as overrides once, then
establish the new baseline without losing user data.

## Authoring and runtime behavior

- `SetScriptStateInt32` marks the declaration GUID as overridden.
- Details-panel changes mark the exact nested bag field when Unreal supplies it;
  a whole-bag event conservatively marks all fixed-layout fields.
- `ResetScriptStateOverride` and `ResetAllScriptStateOverrides` remove override
  identities and rematerialize the current C# defaults transactionally.
- Start and Reload install the candidate bag and override set before managed
  `OnStart`, so any state write during startup joins the new generation. A
  failed managed load restores the previous bag, override IDs, count, and
  generation.
- Script Asset replacement removes override IDs that do not exist in the new
  declaration.

The upstream identity chain remains compiler-owned. Declaration patches used by
the native graph pin the state identity through a rename; semantic graph edits
retain unaffected state entries; external C# edits reconcile through `.usids`.
The same `ScriptStateOverride(stateId, json)` is consequently valid after each
edit and after a CoreCLR generation reload.

## Verification

The managed reload test executes one instance override through a declaration
rename, body graph edit, external C# rename, and four CoreCLR generations. The
UE Automation suite separately verifies default propagation, rename migration,
incompatible-type fallback, reset behavior, asset replacement, transactional
reload failure, and the live World-host demo.
