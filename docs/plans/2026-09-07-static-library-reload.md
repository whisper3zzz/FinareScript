# 2026-09-07 — Static Function Library reload

Status: in-progress

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

## Managed checkpoint — 2026-09-07

The managed slot lifecycle and owned signature identities are implemented.
Release compilation and 13 focused tests passed with the UE-bundled .NET 10 SDK
against the real `UnifiedScript.Tests.csproj`. Tests cover body replacement,
rollback, additive callables, incompatible container/struct/soft-reference
contracts, invalid assemblies and collectible current/pending context disposal.
Evidence: `artifacts/m13-3c-1c-b-static-library-reload/managed-focused-release.log`.

Native integration and cooked acceptance remain unimplemented at this checkpoint.
