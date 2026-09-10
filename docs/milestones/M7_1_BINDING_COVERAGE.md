# M7.1 Binding Coverage Matrix

M7.1 turns Unreal binding generation from an open-ended reflection experiment
into a project-owned, regression-gated product surface. One API Contract now
selects members, applies explicit policy, drives every generated artifact, and
records how every selected member crosses the Unreal/managed boundary.

## Supported reflection matrix

`unifiedscript.unreal-reflection/v0.3` and the managed model cover:

- base-class and interface relationships;
- overload selectors and reflected trailing default parameters;
- static functions plus Blueprint display name, category, tooltip, purity,
  deprecation, and assignability metadata;
- nested structs and referenced enum definitions/values;
- object, class, soft-object, and soft-class handles;
- arrays, sets, maps, return values, and `out`/`ref` parameters.

The checked-in `UnrealBindingMatrix` fixture exercises the whole matrix. Its
C#, C++, visual-node, and coverage outputs are exact golden files, and the
generated C# is compiled inside the test suite. The live UE fixture separately
proves UHT/export/generation/native compilation against UE 5.8.1.

## Policy contract

`unifiedscript.unreal-api-contract/v0.2` adds exact allow/deny rules. An allow
rule may retain automatic routing or require one explicit route. A deny rule
records a stable diagnostic code and human-readable reason. Overloads use an
exact `Name(type,...)` selector, avoiding ambiguous member-name selection.

Dependency closure automatically retains required base classes, interfaces,
nested structs, and enums without adding unrelated members to the public API.

## Complete disposition model

`unifiedscript.unreal-binding-coverage/v0.2` gives every selected member exactly
one disposition:

| Disposition | Boundary |
| --- | --- |
| `directThunk` | Generated fixed ABI for supported 0-4 input signatures |
| `callFrame` | Generated ABI v3 for dynamic/container/ref/out shapes |
| `dataPlaneOperation` | Generated batch/command/event gameplay boundary |
| `reflectionFallback` | Explicitly allowed safe cold-path reflection |
| `rejected` | Denied or unsupported, with a stable reason |

The live product contract currently contains 73 selected members: 22 direct
thunks, 49 CallFrame members, two delegate reflection fallbacks, zero data-plane
overrides, and zero rejected members. All 71 native members are generated.

## Regression gates

- API contracts carry monotonic minimum counts per route and a maximum rejected
  count; regressions produce stable `USUE5101`-`USUE5106` diagnostics.
- `ue-bindings --fail-on-rejected` returns data-error status for rejected APIs.
- Exact golden tests prevent semantic or textual drift across all four outputs.
- Repository validation rejects handwritten `switch (MemberId)`/literal member
  dispatch outside generated and test sources.
- The one-command Unreal onboarding gate requires selected coverage to equal
  native, data-plane, and safe-fallback coverage with no unexplained rejection.

## Verification

Verified locally on 2026-08-26:

- managed test suite: 444/444 passed;
- repository validator: 42/42 invariants passed;
- product coverage: 73/73 classified, 71 generated native members, two safe
  delegate fallbacks, zero rejected, baseline gate passed;
- UE 5.8.1 native build and `Finare.Unreal` Automation are recorded in
  the Roadmap acceptance evidence.
