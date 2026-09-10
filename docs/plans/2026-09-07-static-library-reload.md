# 2026-09-07 — Static Function Library reload

Status: complete

## Outcome and scope

Daily Build & Reload must replace an already-used managed Blueprint Function
Library in the same World. Invalid candidates keep the old library callable.
The existing typed Blueprint functions, World ownership and frozen call-frame
ABI remain the invocation contract. Static CLR state belongs to a loaded code
generation and resets with that generation; it is not Script Class instance state.

## Decisions and remaining work

- Keep each `(World, resolved program path, type name)` cache slot stable and
  give it current, prepared and retained previous code generations. Prepare
  loads and validates a candidate without publishing it. Commit swaps the active
  generation, Abort can restore the retained previous one, and Finalize retires
  it. World disposal retires every remaining generation. Candidate generation
  identifiers are not reused after an aborted attempt.
- Existing callable names, parameter names/directions, return types and exact
  native value identities must remain compatible. Removing or changing a bound
  function rejects preparation; adding a callable does not invalidate existing
  bindings. Compare signatures without keeping candidate reflection objects in
  a process-global cache. No file polling or hashing is added to ordinary calls.
  Preparation checks loadability and call contracts; it does not execute user
  static constructors. They still run on the first call in that generation.
- First implement and test the managed cache lifecycle. Then add control-plane
  entry points and native Host/World wrappers for the same four phases, keyed
  by World, program, type and candidate generation. A cold library is unmatched,
  not loaded just because a build finished. Existing entry points and value/ABI
  layouts stay unchanged.
  The new prepare entry returns a positive candidate generation, zero for an
  uncached library, or a negative existing entry-status error. Commit returns
  the active generation or a negative error; Abort/Finalize use the existing
  status convention. Only Prepare treats a cold key as unmatched; the remaining
  phases reject a missing key or stale candidate. Resolve these new entry points
  optionally so an older managed host can retain ordinary dispatch; requesting the new reload feature
  without them reports an explicit unavailable-feature error.
- Integrate static assets into the existing authoring reload action. Prepare
  every loaded World copy of one library before committing any; abort the batch
  if a participant fails. Surface the result through the existing reload result
  and diagnostics. Preserve the established component reload route.
  Both the shared daily action and a selected-asset reload must reach this path.
  Deduplicate targets by their resolved program/type cache key, and do not turn
  a cold World into a new library instance merely to count a reload.
  Prepare all eligible Worlds before evaluating the batch barrier, so matched
  and rejected counts include every cached participant. User-visible summaries
  must describe reload targets rather than claiming static libraries are components.
  Match the managed cache's ordinal path/type pair when deduplicating; Unreal's
  default FString equality is case-insensitive. Keep the incoming transaction
  identity and build phases in diagnostics. Batch receipts use
  `scope=staticLibraryBatch` and per-World participants with known generations;
  the legacy root generation fields are zero as a batch-only not-applicable
  sentinel required by the existing v0.1 reader, never inferred active versions.
  Rollback errors must remain visible rather than being labeled successful.
- Finally prove a body change and rejected candidate through an actual Blueprint
  VM caller in the same World, then carry the function fixture through Cook and
  packaged execution. Use HostProject without onboarding over protected files.

## Relevant verification

Managed tests must distinguish old/current/candidate code results, prove abort
both before and after commit, reject signature and load failures, and cover
disposal with a pending candidate. Native CMD checks must exercise the authoring
reload entry, multiple Worlds, and existing component dispatch. The prior VM
checkpoint is `54eb18a`; its World recreation result is not code-reload evidence.

Use a dedicated managed test fixture compiled with two body revisions and one
incompatible signature, copied into one stable active program path. A real
Blueprint Array caller must observe the changed result in two existing Worlds;
a third, cold World remains unmatched. Exercise selected-asset and daily reload,
including duplicate assets resolving to the same cache key. Cooked coverage can
reuse the existing five-shape function fixture without coupling packaging to the
Editor-only reload controls.

For cooked acceptance, retain that generated library/caller package only under
an explicit fixture-output option after its CMD checks succeed. Give the run a
dedicated Content directory and a receipt with exact class paths. Direct UAT
`BuildCookRun` can cook that directory plus `/Engine/Maps/Entry`; do not invoke
onboarding or the whole golden-path setup over protected files. A separate
HostProject runtime verification flag must load the cooked ordinary Blueprint
caller, check its bytecode and all five input/ref/out/return results, then destroy
its World and check host cleanup. Keep the verifier independent of Editor APIs.

## Managed checkpoint — 2026-09-07

The managed slot lifecycle and owned signature identities are implemented.
Release compilation and 13 focused tests passed with the UE-bundled .NET 10 SDK
against the real `Finare.Tests.csproj`. Tests cover body replacement,
rollback, additive callables, incompatible container/struct/soft-reference
contracts, invalid assemblies and collectible current/pending context disposal.
Evidence: `artifacts/m13-3c-1c-b-static-library-reload/managed-focused-release.log`.

## Native integration checkpoint — 2026-09-07

Optional managed exports, native Host/World wrappers and the daily/selected-asset
reload route are implemented. Editor compilation passed; the existing component
batch reload, static and instance Blueprint dispatch regressions passed.
`Finare.Unreal.ScriptClass.StaticLibraryReload` passed through ordinary
Blueprint bytecode in two existing Worlds with three separately compiled DLL
variants. It verifies changed v1/v2 results, restoration after commit/abort,
rejection of incompatible candidates while current code remains callable, a
cold World remaining unmatched, selected-asset reload, daily-action cache-key
deduplication, preserved transaction diagnostics and World-host cleanup.
The subsequent cooked checkpoint below closes function acceptance.

Current native evidence: `ue-host-editor-build-static-test-4.log`,
`editor-static-library-reload-ue.log`,
`editor-reload-batch-preflight-final-ue.log`, `editor-static-dispatch-ue.log`
and `editor-instance-dispatch-final-ue.log`
under `artifacts/m13-3c-1c-b-static-library-reload`.

## Cooked function checkpoint — 2026-09-07

The existing five-shape Blueprint VM test can retain a dedicated package and
write an exact class-path receipt after all assertions pass. The independent
HostProject verifier loads those ordinary caller functions, checks their
bytecode and property types, compares input/ref/out/return/downstream values,
and owns a temporary World context. It also requires cooked data and resolves
the managed chunk and hostfxr underneath the executing packaged project.

Editor compilation, fixture generation, Win64 Development BuildCookRun and
packaged execution passed. UAT completed in 53.48 seconds; the archived runtime
returned PASS after all five typed exchanges and World-host cleanup. Both the
chunk and hostfxr resolve inside the executing archive. The driver removes its
exact temporary Content package after the attempt and retains its receipt/logs.

Evidence under `artifacts/m13-3c-1c-b-function-closure`:
`ue-host-editor-build-verifier-3.log`,
`fixture-output/editor-fixture-generation.log`, `build-cook-run.log`,
`fixture-output/packaged-function-closure.log` and
`fixture-output/function-closure-result.txt`.

Cook and packaged startup still report initial native-function lookup warnings;
the existing post-load rebinding subsequently supplies the working dispatch,
as demonstrated by the actual VM results. This is not a warning-free build
claim. Soft Object/Class checks prove value transport, not loading the fixture's
placeholder asset paths. M13.3C-2 is the next Roadmap tranche.

After the packaged run, the fixture's CallFunction and VariableSet nodes were
given GUIDs to remove the observed missing-NodeGuid warning. The runtime code
did not change. Editor compilation and the ordinary save/reload VM selector
passed again (`ue-host-editor-build-nodeguid.log`,
`editor-nodeguid-selector.log`); Cook was not repeated for that fixture-only
identity correction. The native reload test now also deletes the exact package
that materialization saved implicitly.
