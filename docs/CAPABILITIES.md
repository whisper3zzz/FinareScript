# Capabilities and limits

This page summarizes current source capabilities and the scope of recorded
verification. The native acceptance inventory was reviewed against development
revision `2139c7f` on 2026-09-11; later compiler changes do not renew that native
acceptance. Use [ROADMAP.md](ROADMAP.md) for priorities and
[TESTING.md](TESTING.md) for current verification commands.

## Platform and release identity

The release declaration is UE 5.8.1, Win64 and .NET 10. The source manifest
retains `1.0.0-rc.1`. The [frozen RC1 acceptance](RELEASE_READINESS.md#frozen-rc1-acceptance)
belongs to August 29 revision `5be7dde`; later feature checks do not renew that
complete release verdict. SDK receipts record the source revision so artifacts
with the same product version can still be distinguished.

Editor Automation, cooked execution, physical UI interaction and comparative
performance are separate scopes. A successful result in one does not certify
the others. Historical reports live outside Git under their documented paths.

## Developer workflows

| Workflow | Implemented and recorded | Remaining boundary |
| --- | --- | --- |
| Adopt an existing project | SDK adoption, ownership/conflict handling, Doctor, project-local setup and first Play; native and Blueprint-only fixtures | Broader engine, module and project layouts still need consumer evidence |
| Edit C# and Graph | Shared semantic identity, bounded graph patches, source updates, review/history and preserved layout; source-only C# helpers and UE entry bodies | Only the published Visual C# profile is graph-editable; explicit `[Visual]` methods must satisfy it, while UE entry bodies can remain source-only |
| Daily build and reload | Project-backed authoring, external source refresh, native Build & Reload and retained old generations on rejected changes | Native/schema changes may require restart; physical interaction is not implied by CMD tests |
| Asset identity | Reviewed Rename/Duplicate/Delete and recovery; inherited active instances and partial-source identities covered by later fixtures | Arbitrary user asset layouts are not established |
| Lyra health/HUD | Installed generated calls and subscriptions, real GAS damage/heal, reload, respawn, stop/restart and teardown in two Editor copies | Same-project asset/async failure coverage remains open; final packaged gameplay and audio are unverified |

Start with the [first-project guide](UNREAL_FIRST_PROJECT.md),
[gameplay recipes](UNREAL_GAMEPLAY_RECIPES.md) and
[Lyra reproduction guide](../samples/External/Lyra/README.md).

## Script Classes and Blueprint integration

| Area | Implemented and recorded | Remaining boundary |
| --- | --- | --- |
| Class foundation | UObject, Actor, ActorComponent and FunctionLibrary proxies; reviewed state, lifecycle, input, delegate and interface routes | Availability of a class kind does not cover every Unreal property or inheritance combination |
| Blueprint function exchange | Common values and supported ref/out; five container/Soft Reference families through actual Blueprint VM, package reload and Win64 Development cooked execution | Nested containers and known unhashable combinations are rejected |
| Explicit event extension | Inherited native/Blueprint events, exact owner identity, explicit parent calls, compatible body reload and cooked lifecycle fixtures | General prototype/mixin behavior is not promised |
| Inheritance and defaults | Reviewed Actor child/grandchild and managed-base state, component identity, scalar/collection overrides, saved/reloaded classes, two Worlds and cooked fixtures | These recorded C-3 results do not establish all future composition operations |
| Composition and publication | Base edit/clear, publication recovery, stopped descendants; first pure C# ordinary Actor + Health/Mover workflow with saved overrides, scalar component references and transactional batch reload; ordinary SceneComponent receiver lifecycle without a Finare native base | Broader composition edits, container references and remaining default/reference ownership are open. Current C-4 and ordinary component checks are Editor-only |
| Object identity and types | Generated native inheritance, checked typed object/interface views, nullable references and common property/call/ref-out/callback marshalling; identity by World and native handle, access constrained by generation | Known generated type coverage still depends on native reflection; this does not establish arbitrary C++ inheritance or cooked acceptance for the new views |
| Reflected property access | Required partial native accessors for Script Class state, declaration defaults, SDK/in-process compiler integration and preserved source-patch configuration | Non-partial reflected state is rejected; container reads are snapshots and assignment replaces the native value |
| Ordinary UObject scripts | Ordinary native parents, generated function dispatch, public C# `CreateObject`, first-call activation, reload, failed-start cleanup and weak-receiver GC/World teardown | Editor-verified; native references own GC lifetime. No synthetic Actor Tick or blanket coverage of native factory/event requirements |
| Runtime component creation | Generated UE `AddComponentByClass`, deferred `FinishAddComponent`, typed attachment/mesh calls and destruction; an ordinary Actor creates two ordinary SceneComponent script receivers in C#, with independent lifecycle and reload cleanup | Editor-verified. Native CDO/default-subobject construction and persisted editor component trees are separate, unfinished workflows |

Details: [Declaration IR](DECLARATION_IR.md), [Unreal adapter](UNREAL_ADAPTER.md),
[event extension](plans/2026-09-07-blueprint-event-extension.md),
[inheritance](plans/2026-09-08-blueprint-inherited-defaults.md),
[class composition](plans/2026-09-09-class-composition.md).
The [first composition workflow](../tests/Finare.Tests/Fixtures/ClassComposition/README.md)
uses generated Blueprint/SCS storage with pure C# behavior. Its Automation
substitutes the Daily Build command with an isolated real C# build; it does not
renew full project installation or packaged acceptance.

The [native component creation example](../tests/Finare.Tests/Fixtures/NativeComponentCreation/README.md)
uses the module API library generated before gameplay compilation. Its native
test only observes the components created by C#; it does not assemble them.

## API families

| Family | Reviewed support | Open combinations |
| --- | --- | --- |
| Reflected values and collections | Generated calls and common enum/Rotator/Transform, Soft Reference and flat Array/Set/Map state/defaults; selected inheritance fixtures above | Nested/interface collections and broader struct/default combinations |
| Interface references | Scalar property and input/return/ref/out, including native and Blueprint-only implementations | Interface containers, interface-bearing structs/callbacks, Blueprint-defined interfaces and general Script Class interface state |
| Subsystems and native interfaces | Generated native virtual dispatch, World/GameInstance/LocalPlayer lookup and Enhanced Input mapping with reload/cleanup | General native default construction, travel and cooked subsystem scope |
| Reflected callbacks | Typed leases, synchronous results/ref/out, native retention, worker-originated UE reentry and correlated replies | Callback properties/returns, nested callbacks, ordinary C++ std::function and cooked callback scope |
| Ordinary native C++ | Generated constructors, fields, methods, qualified object inputs, owned/borrowed returns and copied FString/std::string values | Richer structs/containers, ownership transfer, inheritance, ref/out and callbacks |
| Binary data | Bounded UObject-member byte exchange, copy/borrow/snapshot operations and exact-once result retries | Cross-language zero-copy, nested buffers, reflected state, ordinary-object arguments and cooked scope |
| UI and async gameplay | Generated UMG events with async load/spawn/cancel; saved Demo, cooked runtime and project-backed edit/reload | General UMG/latent parity and physical appearance/interaction acceptance |
| Networking | Actor and dynamic ActorComponent, five exposed lifetime conditions, RepNotify, supported mixed RPC payloads, ownership and reload on connected Worlds | Full combinations, push model, travel, cooked networking and packaged WindowsServer |

Use the [API contract](UNREAL_API_CONTRACT.md) and generated coverage diagnostics
to assess an actual signature. A supported value in a native call does not by
itself make that value a supported Script Class property, default or RPC payload.

## Runtime, distribution and performance

Compiled chunks, CoreCLR execution, World/generation ownership, state migration,
diagnostics and profiling are implemented. BlockBreaker and selected Script
Class fixtures have recorded Win64 packaged execution. Lyra's current final
acceptance remains Editor-only.

Matched workload tooling and historical batch/shared-data improvements exist.
Scalar, resource/spawn and reload weaknesses remain explicit; there is no
unqualified "faster than Puerts" result. Distinguish CoreCLR generation
replacement from module-cache reexecution when comparing reload.

Details: [runtime architecture](ARCHITECTURE.md),
[SDK distribution](UNREAL_SDK.md), [release readiness](RELEASE_READINESS.md),
[matched workloads](PERFORMANCE_WORKLOAD_SUITE.md).

## Updating this page

Update the relevant row when a supported behavior or boundary changes. Link to
its technical contract or owning verification record; keep per-run counts,
commands and logs there. Preserve both the positive result and its environment
limits. The [September 11 snapshot](history/ROADMAP_2026-09-11.md) retains the
detailed prior comparison and checkpoint narrative.
