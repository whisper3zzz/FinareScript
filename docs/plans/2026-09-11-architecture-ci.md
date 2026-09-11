# 2026-09-11 — Authoring boundaries and trustworthy CI acceptance

Status: complete — source refactor and local CI verification; remote workflow not published

## Outcome and scope

Separate authoring transactions from HTTP, isolate compilation from the runtime
dependency closure, and make CI acceptance account for every required lane on
the tested revision. Preserve source/asset identities, wire formats and rollback
behavior. Existing documentation/test reorganization remains in this checkout.

## Decisions and remaining work

- Extract a directly testable authoring session and domain failures; HTTP owns
  request parsing and status mapping. Keep publication reservations and recovery.
- Share program-image/source-map contracts without Roslyn. Compiler orchestration
  belongs above the runtime; installed runtime loading must not require Roslyn.
- Run hosted checks on all development branches. Select native coverage by
  changed paths; main, schedules and manual acceptance request the full matrix.
- Fork code stays off persistent native runners. If native coverage is required
  but unavailable, CI acceptance fails with that missing coverage visible.
- Verify lane identity, revision, clean state, child reports and real executed
  tests. Count UE completion identities once and reject critical engine errors.
- Remote workflow execution and branch-protection configuration require a
  separately authorized publication; this task changes and validates the source.

## Relevant verification

Direct session regressions cover stale requests, publication idempotence and
preservation after failure. Both project-reference and actual assembly-reference
checks prevent compiler dependencies from entering Runtime/Unreal.

The local `managed-tests` lane passed all 1,138 tests and 135 repository checks;
`deterministic-generation` passed all nine generated artifact comparisons. The
tooling suite passed 66 tests with one existing Windows symlink-privilege skip.
CI cases inject skipped jobs, stale reports, empty TRX results, failed child
reports and duplicated UE logs. Actionlint and PowerShell syntax checks passed.

A fresh Release publication of Finare.Unreal contains Core, Semantics, Graph,
ProgramModel, Runtime and Unreal assemblies, with no CSharp/Compilation/Roslyn
files or dependency entries. The first publication exposed a missing
BinaryPrimitives import after extraction; the import was restored before the
passing full managed lane and final publication.

Reports live under `artifacts/architecture-ci/managed`,
`artifacts/architecture-ci/generation` and
`artifacts/architecture-ci/runtime-publish-verified`. Local lane reports retain
`sourceDirty: true`; they cannot satisfy clean remote CI acceptance. Native
Editor/packaging and GitHub execution were not performed in this task. Existing
native fixture limitations remain recorded in the testing guide. Assembly moves
require rebuilding tooling consumers; chunk/asset/wire contracts are unchanged.
