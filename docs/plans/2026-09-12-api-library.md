# 2026-09-12 — Available Unreal API library

Status: complete (focused feature scope; full CI limitations below)

## Outcome and scope

An adopted C# project can discover supported APIs before using them. Adding a
call within the exported module surface compiles and reloads without changing
reflection metadata or rebuilding native code. Existing generated native-only
declarations, stable IDs and transactional reload semantics remain intact.

## Decisions

- Generate the available API library from UE metadata and capabilities, independently
  of gameplay source. Source-driven selection remains a low-level tooling operation.
- Prefer generic reflection for supported reflected signatures. Keep generated
  native routes for signatures outside that transport and for native-only C++.
- Extend the existing ABI v3 reflection invocation to support ordered ref/out
  values, with descriptors checked before invoking gameplay and correlated retries.
- Cache reflection/library generation on engine, plugin and native inputs; source
  edits invalidate managed compilation and reload only.
- Public value structs use canonical supported field order; excluded fields are
  preserved transactionally. No per-project layout or binding registration file.
- Full Engine export exposed private headers and editor/deprecated fields. Generic
  reflection avoids compiling those types; native output retains only codecs
  required by native signatures. Interface dispatch still needs native code.
- Save notifications debounce into the existing daily-build coordinator and
  transactional reload. Sources linked outside managed project directories use
  the manual Build & Reload entrypoint.

## Relevant verification

Compile generated API declarations before any gameplay calls exist; add a new
call and verify unchanged library/native output. Exercise ref/out conversion,
invalid descriptors without side effects, and two managed revisions in one UE
process. Preserve user-local scratch/settings/crash files.

Full Engine + FinareRuntime declarations compile before gameplay. Both Editor and
Game native targets build. CMD Automation passed ExportScope, GenericOutputs,
NewCallReload and SourceSave: the second generation adds scalar/ref/out/struct
calls, preserves excluded fixed-array state, and executes the retried call once.
The CI wrapper passed end to end, including the added KismetMathLibrary.BreakVector
static-function-library ref/out regression. Focused managed tests (36), CI policy tests
(14), source invalidation and the repository validator pass. All nine deterministic
generation outputs agree between two runs. The gate still fails against the
existing C++ data-plane header snapshot: its difference is indentation only.
The native thunk snapshot test has the same existing formatting issue. Neither
snapshot is rewritten as unrelated cleanup; full CI is not claimed green.
Full release/package acceptance is outside this slice.
