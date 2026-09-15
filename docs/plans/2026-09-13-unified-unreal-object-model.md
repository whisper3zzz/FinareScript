# 2026-09-13 — Unified Unreal object and type model

Status: completed for the documented Editor scope

## Outcome and scope

Implement the user-authorized order: unify UObject identity and generated C#
types, decouple managed behavior ownership from native parent classes, then
converge property and container access semantics. UE remains the owner of its
class hierarchy, object instances, subobjects, and reflected properties.

Preserve the existing uncommitted composition implementation and user files.
This task does not authorize a Git commit, push, or changes to external projects.

## Decisions and remaining work

- Object identity retains the existing World/bridge scope and generation-safe
  handle. Generated wrappers expose reflected object types and known native
  inheritance. Casting validates the actual native type; managed metadata alone
  is not authority. Wrapper caches must not retain unloaded script assemblies.
- Keep handle transport and native validation underneath the public typed
  surface. Reuse the existing reflection invocation channel for native type
  queries instead of creating a parallel object registry or transport ABI.
- World identity and access lifetime are separate: sibling generation bridges
  share the underlying World identity, but a wrapper keeps the generation lease
  through which it was obtained. Same-World arguments are allowed only while
  both leases are live. Equality/hash identity remains stable after disposal;
  native access and checked casts still reject expired leases/handles.
- First implementation: generated inheritance, typed object values and common
  marshalling, checked object views, and their managed/native contract tests.
- Implemented managed object projection across properties, calls, ref/out,
  callbacks and reference-containing structs/containers. Declaration IR keeps
  native object/interface paths; the binary declaration reader/writer uses
  minor 6 only when those reference identities occur. Native materialization
  now creates the corresponding constrained object/interface properties.
- Ordinary ActorComponent receivers now have separate internal execution hosts.
  The exact receiver supplies invocation/property identity; synthetic native
  lifecycle hooks start, tick and stop it even when no lifecycle declaration is
  exported. Unregister suspends the generation; re-register resumes it without
  a duplicate Tick. Empty declarations still bind their runtime Script Asset.
- Ordinary UObject execution now uses explicit World ownership of its execution
  host and a weak receiver. World resolution follows Outer when GetWorld is
  absent. Public creation and first-call activation share function dispatch;
  failed starts roll back hosts, and GC/World shutdown retire generations.
  The materializer publishes Object functions and accepts legal ordinary Object
  parents. UObject scripts do not receive synthetic Actor lifecycle hooks.
- Native inspection located receiver assumptions in `FinareComponent`,
  `FinareBlueprint` function dispatch, `FinareAsset` class validation,
  `FinareInvocationBridge` parent calls, and class factory/materialization.
  Existing `UFinareBlueprintGeneratedClass` already retains the script asset and
  provides class binding/instance hooks. Reuse those and the World subsystem;
  do not add one public Finare base per native component family.
- Script Class state requires partial properties with generated native
  accessors. This includes implicit instance state on `[ScriptAsset]` owners
  and explicit `[Expose]` state on helper bases. Non-partial reflected state is
  rejected; the old lifecycle/RPC/parent-call mirror synchronization is removed.
  Earlier samples and fixtures are migrated to the same property path.
- Container getters return detached value snapshots and setters replace the
  native value. Object elements use typed identity/lease validation. Ordinary
  private managed state remains managed; no shared-memory alias is implied.
- A local compiler experiment confirmed .NET 10 partial properties can retain
  a default initializer on the declaration while a generated implementation
  uses the C# `field` keyword before activation and native access afterwards.
  This is now integrated into the SDK analyzer payload, project loader,
  in-process compilation, declaration lowering and source-patch requests. The
  analyzer targets Roslyn 5.3 so the configured SDK can load it; the compiler
  host can consume it on Roslyn 5.6. Generator bytes enter configuration identity.

## Relevant verification

- Generated bindings must compile and preserve object types through properties,
  calls, outputs, structs and containers. Check nulls, incompatible casts,
  cross-scope arguments, disposed generations, and collectible assembly lifetime.
- Native receiver changes require real UE build/Automation covering lifecycle,
  multiple objects, destruction, reload failure, and publication recovery.
- The existing composition fixture is backend evidence: native test code creates
  its SCS components and installs an isolated build command. It is not evidence
  that the normal C# authoring flow already provides those operations.
- Managed validation: focused object/bridge checks (50), declaration/function
  checks (33), and compiler/Blueprint contract checks (75) passed. Logs are in
  `artifacts/object-model-design/`; these are not UE runtime acceptance evidence.
- Editor builds passed. Real CMD Automation passed
  `Finare.Unreal.ObjectModel.NativeTypesAndProperties`,
  `Finare.Unreal.ScriptClass.NativeReceiverLifecycle`,
  `Finare.Unreal.WorldHost.Lifecycle`, its selected `LifecycleStress` test,
  `Finare.Unreal.ObjectModel.LiveProperties`, and
  `Finare.Unreal.ScriptClass.FactoryContract`.
  LiveProperties covers native edits observed by C#, immediate setters,
  detached array snapshots, replacement and reload retention.
- The integrated managed suite passed 1189/1193. Four failures concern
  unchanged generator formatting and an existing missing packaged-guidance
  reference. Following the root UObject metadata
  correction, 12 relevant object/graph tests and Demo declaration analysis
  passed. Source-property authoring passed 21 checks. Architecture dependency
  tests passed 3 checks and the repository validator passed 135 invariants.
- Real CMD Automation also passed `Finare.Unreal.Objects.Factory` and
  `Finare.Unreal.Objects.ManagedLifecycle`. The latter exercises an ordinary
  PrimaryDataAsset parent, public generated C# creation, native reflected object
  references, ProcessEvent, lazy activation, reload success and candidate
  failure, repeated failed starts, receiver GC and World teardown. A failed
  candidate can restore the previous generation when the previous code can
  still initialize; arbitrary gameplay side effects are not transactional.
- LiveProperties passed again after isolating its fixed-path saved fixture.
  NativeReceiverLifecycle and WorldHost lifecycle/stress passed after the World
  context integration. Four updated typed-object recipe snippets compile.
- These are Editor/managed results, not Cook, packaging or release acceptance.
  Broader normal-authoring composition remains outside this delivered foundation.
  The results listed above predate deletion of the old property synchronization
  path; verification of that deletion is recorded separately below.

### Removal of mirrored state (2026-09-15)

- Removed `UnrealScriptStateSynchronizer` and lifecycle, RPC and parent-call
  hydration/flush. Non-partial reflected state is rejected by the generator and
  declaration lowerer. Samples, the default project template and source patches
  use partial native properties, including the first added property on a class.
- Generator checks (8), object-model checks (19), related compiler/network/
  authoring checks (118) and final focused regressions (33) passed. The integrated
  managed run passed 1193/1196; the remaining failures are the existing packaged
  guidance reference and two data-plane generated-text formatting assertions.
  See `artifacts/remove-state-mirror-suite-final.log`. The final focused run adds
  assertions for parent-failure reads and first-property source edits.
- Demo compilation and declaration/chunk emission passed. Actor samples now
  retrieve their diagnostic component from the native Actor instead of treating
  the receiver handle as a component. The pure C# fixture materializes the actual
  template declaration and configures its carrier as the production factory does.
- Editor compilation and CMD Automation passed networking for Actor and component
  Script Classes, live native properties, ordinary UObject managed lifecycle,
  and Actor/component Blueprint parent calls. The separately selected old
  `BlueprintParentInvocationOwnership` test fails because it expects two running
  execution hosts for one receiver; the unified model rejects the second host.
  That pre-existing test expectation was not changed as part of mirror removal.
- The repository validator passed 135 checks. This is managed/Editor evidence;
  no new Cook, packaging or release result is claimed.
- `Finare.Unreal.PureCSharp.SourceDrivenReload` passed with automatic activation,
  native defaults, an external native edit and a changed C# reload body.
  `Finare.Unreal.ScriptClass.CollectionStateGameplay` passed native container
  defaults/overrides, replacement, asynchronous resource selection, retry,
  reload and teardown. See `artifacts/remove-state-mirror-native-results.log`;
  its two supplemental fixture failures were corrected and checked separately.
- Supplemental `SoftReferenceMaterialization` and `UI.AsyncGameplay` CMD tests
  passed after correcting the stale fixture ID and staged declaration path.
  See `artifacts/remove-state-mirror-native-supplemental.log`.
