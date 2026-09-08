# 2026-09-07 — Existing Blueprint event extension

Status: in-progress

## Outcome and scope

M13.3C-2 must let managed behavior replace an inherited BlueprintNativeEvent or
BlueprintImplementableEvent through the existing Script Class instance. A body
can explicitly call the effective parent implementation. Child Blueprint
overrides, compatible managed reload and package load retain that ownership.
The existing `[UnrealBlueprintEvent]` declaration of a new managed default keeps
its current meaning. Parent-class selection/composition and general managed
class inheritance remain part of C-3/C-4; this work uses the class's actual UE
parent chain and must not silently reparent an asset.

## Contract decisions

- Add `[UnrealBlueprintOverride(declaringClassPath, functionName)]`, with the
  existing Explicit Super policy. The method is public and non-static, names
  exactly one inherited event and cannot also own a new Blueprint event,
  interface, delegate, input, RPC or RepNotify binding. Source method identity
  remains stable across body edits; target metadata is an additional contract.
- Reuse `ScriptBlueprintEventDeclaration` with an optional `Override` record
  containing `DeclaringTypePath` and `FunctionName`. Core stores strings and
  policy only. No-override documents retain v0.1; documents using this feature
  select `unifiedscript.script-asset/v0.2`. New readers accept both, but reject
  override metadata under v0.1. Older native consumers reject v0.2 rather than
  ignoring the override and creating an unrelated function.
- Native preflight resolves the exact declaring class and locally declared
  event, verifies ancestry, overridability and the full type/direction contract,
  and rejects unrelated or user-owned graph collisions before mutation. The
  generated UFunction uses the inherited name; its graph GUID remains the
  source semantic ID. Native parameter names and source argument order need an
  explicit mapping. Const-reference native inputs remain input values; they
  must not be mistaken for mutable ref parameters.
  Use the UE class-signature graph-creation overload: its function entry keeps
  an external parent reference. The UFunction-signature overload only copies
  user pins and is explicitly intended for a new function, so it cannot create
  this override. Remove any auto-generated parent-call body; parent execution
  remains explicit in managed code. Compare inherited allocated pins and
  function references for idempotency, rather than inventing user pins.
  Ownership checks distinguish UE's automatically placed disabled ghost events
  from user-authored or explicitly enabled/disabled events. A fresh Actor
  Blueprint's default placeholders must not block declaring an override;
  accepting a declaration still preserves real user event bodies.
  Scalar managed Object/Class handles can carry a more specific inherited UE
  object/class pin: preserve that native pin and validate writes against its
  reflected class rather than replacing it with UObject. Container contracts
  must still match their codec identities; do not erase a typed leaf merely to
  make a signature compare pass.
- Persist the declaring identity on the generated-class function binding.
  Parent invocation resolves the effective function below that managed owner
  in its actual UE superclass chain. Calling `ProcessEvent` on the object with
  a fresh virtual name lookup would recurse into the override; pass the exact
  resolved parent UFunction instead. An intervening ordinary Blueprint parent
  must retain its implementation. General managed-to-managed parent bodies
  cannot be implemented by recursively dispatching the same method name on the
  current instance; keep that C-3 concern explicit. Reject a parent route whose
  remaining ancestor chain contains another managed owner of the same event,
  even if an ordinary Blueprint lies between them and might Call Parent again.
- Reuse the existing ABI v3 multi-result codecs for parent calls. Keep the
  48-byte value and 72-byte frame layouts unchanged. A capability-gated parent
  operation can use a defined frame flag through the existing Invoke entry;
  ordinary calls retain zero flags and unknown flags remain invalid. The
  assigned values are `BlueprintParentCalls = 1 << 14`,
  `UnrealNativeOperationV3.InvokeParent = 3` and parent frame flag `1`.
  The existing table prefix and function pointers stay unchanged; capability
  validation precedes use, and read/write paths must still require zero flags.
  The member ID remains the hash of the declared `classPath:functionName`;
  the explicit parent operation distinguishes it from an ordinary native call.
  Native pending results use a distinct operation discriminator as well.
  The native host resolves the script component handle and persisted binding before
  dispatch. The generation bridge must reject calls after retirement.
  Use a separate optional parent-invocation capability and bridge interface;
  do not add required methods to existing user bridge implementations.
  `IUnrealParentInvocationBridge.SupportsParentInvocation` reports the actual
  inner transport capability, including through the generation wrapper. Check
  it before state writeback; merely implementing the optional interface is not
  sufficient when an older inner bridge lacks this operation. The
  managed helper uses its own host component handle for both Actor and
  ActorComponent scripts, so native code can select the actual event target
  without guessing the first component on an Actor. A generation-local method
  contract cache can derive argument directions from the override method;
  callers should not have to reproduce native parameter descriptors.
  The public helper can provide `CallParent<TResult>`, void `CallParent`, and
  `CallParentWithOutputs`, taking the managed override method name and only its
  input/ref values. Reflection-derived return types/directions feed the existing
  multi-result representation; no process-global strong reflection cache is
  needed. Input-only helpers reject a method that requires output handling.
  A helper must flush its managed state before entering an ordinary Blueprint
  parent and hydrate after the call, including a failure after parent execution.
  Otherwise the parent observes stale properties, or the enclosing managed
  dispatch flush overwrites the parent's changes. Preserve the original call
  failure if post-call hydration also fails. Validate the helper's local
  contract before flushing state.
- Parent calls must share the correlated-result lifetime so a larger output
  buffer retry never executes the parent twice. The pending identity includes
  the parent operation, host handle, member identity and request contents.
  Require both Parent Calls and Correlated Results capabilities plus an actual
  invocation-scope factory. Use the existing correlated Invoke entry with the
  parent frame flag, and reject parent requests with correlation ID zero before
  user code. The legacy uncorrelated single slot can be overwritten between
  calls from different workers and cannot provide this guarantee. Ordinary
  invocation retains its existing compatibility path and table layout.
  Decode/preflight inputs and output descriptors before user code; publish
  outputs only after every value has been read and encoded successfully.
  The existing `InvokeWithOutputs` path already encodes the reviewed value
  families; reuse it through an operation-aware internal helper. The in-process
  and correlated-scope consumers must preserve the parent operation as well.
  Parent collection inputs may exceed the initial 4 KiB arena. Encode them once
  within the Blueprint 64 KiB limit, then size the actual call arena from the
  encoded input size with the existing minimum. A bounded pooled scratch arena
  can reuse the codec without repeatedly enumerating user input collections.
  Keep small-input calls small so output expansion still exercises result retry;
  do not preallocate the full output budget or widen the ordinary call path's
  input policy as a side effect of this feature.
  Native parent dispatch must share the Blueprint property codecs and the
  existing call-frame result cache. The older reflection bridge's
  `ReadValue`/`WriteValue` does not carry arbitrary managed struct field order;
  its scalar Soft Reference values also differ from the string representation
  used by Blueprint call frames. Expose the existing `PackValueV2` and
  `UnpackReturnValueV2` through a module-private shared interface, retaining
  persistent field bindings. Do not create a second recursive struct codec or
  weaken the older property lane's type checks. Parent results can become owned
  call-frame values before entering the existing retry cache.

## Lifecycle integration

The native component owns the managed host handle even for ActorComponent
scripts whose `TargetHandle` is their Actor. Parent resolution must select the
bound generated class and actual event receiver from that host, including
ordinary Blueprint subclasses, rather than equating TargetHandle with the
event receiver. Generation retirement remains the managed bridge boundary.

Engine Actor BeginPlay starts components before emitting ReceiveBeginPlay.
The original script component called Super::BeginPlay before starting managed
code and stopped managed code before Super::EndPlay, leaving its own inherited
lifecycle events without an active generation. Component classes with a managed
ReceiveBeginPlay override now activate automatically before Super::BeginPlay;
an active ReceiveEndPlay override retires after Super::EndPlay, including an
ordinary Blueprint child that calls its parent. Unaffected classes retain their
existing order and explicit start/stop policy. Generic function dispatch does
not lazily activate a script. Failed startup still finishes the native lifecycle
path and rolls back its candidate. Runtime integration below establishes the
ordering through actual native BeginPlay/EndPlay.

Before activation, select the configured Script Asset (including the
`ScriptAssetPath` configuration route) and verify that its exact generated class
owns the component receiver. Do not use the active-owner helper before an
instance is published. At EndPlay, use the active Script Asset's exact owner so
an edited configuration does not change the running instance's shutdown order.
Exercise actual native BeginPlay/EndPlay and ordinary Blueprint children; the
existing manual ReceiveTick fixture is not evidence for lifecycle ordering.

The typed EndPlay argument must come from generated Unreal reflection bindings.
The preceding checkpoint's binding surface has no `EEndPlayReason` declaration.
Include this enum explicitly with an empty API Contract member list, accepted
only when the reflected kind is Enum. Non-enum empty selectors remain errors;
automatic Contract proposals retain their explicit-review behavior. The native
reflection exporter must collect struct/enum dependencies of exported delegates
as well as Blueprint-visible properties. Actor's exported OnEndPlay delegate
already references this enum, so resolving its omitted dependency supplies the
real engine declaration without introducing an unrelated callable facade.

The managed component fixture records initialization, native ReceiveBeginPlay,
typed ReceiveEndPlay and destruction through the existing generated ManagedState
property. Exercise Actor's native DispatchBeginPlay/RouteEndPlay, ordinary
Blueprint children with and without explicit parent calls, ScriptAssetPath
startup, configuration changes after activation, manual startup and a missing
assembly. A disabled or failed startup must not trigger lazy activation; preserve
the existing exact-owner diagnostics and verify normal native lifecycle cleanup.

Parent helpers are for an activated generation. Reject use during managed
initialization/destruction with an actionable error: native active-instance
fields are published only after OnStart completes, and calling ordinary
Blueprint parent code during candidate initialization could reenter the old
managed instance. Do not mark an uncommitted candidate as active to enable
such a call. The normal inherited-event body runs after activation and can
invoke its parent synchronously.

Instance reload now validates the complete override route set after loading the
candidate and before stopping the old generation. Method identity, raw native
declaring path/function spelling, Super policy and the existing V2 argument/
return signature must remain compatible. A body-only edit may swap; removal,
addition, retargeting or a type/direction change rejects and retires the
candidate while preserving the old instance. The transaction records an
`overrideContractValidation` phase and an unchanged rollback on rejection.
Malformed, hidden or unsupported override methods fail during dispatcher
creation rather than disappearing from the route set. Signature validation
does not compile a second dispatch delegate. This managed-to-managed barrier
retains the richer CLR identity alongside the activation contract below.

Cold activation checks the loaded type against the persisted Blueprint contract
before constructor and lifecycle user code. Comparing only the old and new DLLs during
reload cannot detect an already stale saved Blueprint. The expected contract
must come from the materialized class, not a mutable source file or a freshly
loaded Declaration. Define its boundary before claiming runtime closure; do not
encode this metadata in event names or diagnostic fields. Build the expected
contract from the Script Asset's exact generated class, its persistent bindings
and reflected UFunction properties. These already preserve the native route,
source-to-native order and struct field paths; do not duplicate source metadata
in another generated payload. Share the existing native/managed canonical type
name helpers and codecs. Include enum/struct paths and ordered struct fields,
and distinguish Soft Reference kinds from strings even though their scalar
call-frame representation overlaps. An empty override set is an explicit
contract and must reject an unexpected new override in the DLL.
Use the binding's persisted managed method name when matching the expected
native route. The native class does not know which CLR base type declared that
method; do not fabricate a qualified CLR declaring-method identity from the
Script Asset's concrete type name. The existing managed-to-managed reload check
continues to compare its richer reflection-derived method identity.
Do not reuse that managed-to-managed signature string verbatim for the native
comparison: it also contains CLR property and source parameter names that the
saved class does not retain. Compare ordered parameter directions and codec
shapes, and use the persisted native struct field names in their existing
case-insensitive FName semantics. Raw route strings and the managed method name
remain exact. Keep the existing top-level Object/Class handle compatibility
with a refined native class; collection element contracts remain exact.

Use an optional named managed creation export carrying this bounded contract;
leave the existing transport table and legacy creation signature intact.
Validate before activation and retain the immutable expected contract for
candidate validation. A legacy creation request cannot silently activate a type
that needs inherited-event binding. The native materializer must reject changes
to the override contract while that class has active managed instances, before
asset mutation, with a stop/resynchronize/restart diagnostic. Unchanged contracts
and ordinary Blueprint child bodies remain allowed. This avoids redirecting a
live old generation during class Sync; general live class-shape migration is
outside this checkpoint.

### Activation contract boundary

The native-to-managed creation payload uses
`unifiedscript.blueprint-override-contract/v0.1`, bounded to 256 KiB of UTF-8.
It contains an explicit `overrides` array. Each route has `managedName`, raw
`declaringClassPath` and `functionName`, `superMode: explicit`, a `returnType`
and ordered `parameters` with `direction` (`in`, `ref`, `out`) and `type`.
Value shapes carry `kind`, the shared codec's `nativeName` and `typePath`
(enum/custom struct path; otherwise empty). Custom structs retain ordered
`fields` with native names and recursive types; arrays/sets have `element` and
maps have `key`/`value`. Fixed Vector, Rotator and Transform codecs stay scalar.
`void` is return-only and has empty `nativeName` and `typePath`. Shape recursion
retains the existing depth bound and method arity retains the V2 limit of four
parameters. No source parameter names or CLR field/declaring-type identities
cross this boundary.

`CreateScriptWithBlueprintContract` appends a contract pointer/byte count to the
existing eight creation arguments. Its parsed, immutable expected contract is
stored on the managed instance and checked before constructor activation for
both initial load and reload candidates. The legacy managed export supplies
an explicit empty contract, so it cannot silently activate an override-bearing
type. Internal managed-only fixture creation can still omit the expectation;
production World creation cannot. The new native creation path requires the
named export even for an empty expected set, since absence cannot prove that
the loaded DLL has no unexpected override. Resolving this export remains
optional for World startup and existing legacy native creation callers.

Build the payload after generated-class identity validation and before claiming
candidate gameplay ownership. Select the Script Asset's exact generated class,
including when the receiver is an ordinary Blueprint child. Validate its own
persistent override bindings against local reflected functions and use their
native parameter mapping and struct-field order. A missing asset or a class
without overrides supplies an explicit empty set. Construction failure follows
the current handle/state/ownership rollback and never publishes an instance.

The persisted function binding version is `3`, independently of the managed
dispatcher class's V2 name. Reject unreviewed binding versions. Resolve native
parameters by the binding's ordered names rather than reflected field order,
and require unique names. Local and directly declared parent properties must
have the same native type and direction before projecting their shared managed
codec shape; this retains native object-class restrictions. Consume every
persisted struct/container field path exactly once, including reserved
container markers. Runtime class identity comes from the resolved class object
and strict superclass ancestry, not display casing in `GetPathName()`.

### Live synchronization boundary

Run the live-instance guard after successful Declaration parsing and before
building or applying a synchronization plan, including ValidateOnly and
no-change returns. Match active Script Components across Worlds by their exact
generated-class ownership. Stopped instances and other generated classes do
not block a synchronization request.

For an active class, validate the current persisted contract against its local
and inherited native properties, then compare the complete override set with
the parsed declarations. Reuse the runtime-binding contract comparison without
making Semantic IDs or source parameter names part of the activation ABI.
This comparison relies on `ParseDeclaration` having already validated every
desired override signature against its directly declared native owner. With
an unchanged raw route and the current local/native property check, primitive
and container element types cannot silently change despite the persistent
value binding storing only enum/struct paths and ordered field paths.

Managed names and raw declaring/function strings use explicit case-sensitive
comparison: FString's equality operators ignore case. The materializer's
idempotency checks must use the same rule so a stopped class can persist a
case-only route change. Native parameter and field FNames retain their existing
case-insensitive semantics. Reject a changed live contract before any graph,
class, binding or saved-package mutation, with a stop/synchronize/restart
diagnostic. Same-contract synchronization keeps its existing behavior.

## Implementation sequence and evidence

The managed declaration checkpoint is implemented: the attribute, HIR/lowering
recognition, conditional Declaration versioning, target validation and both
managed dispatchers recognize explicit overrides. Roslyn-resolved attribute
values support constants, `nameof` and the Explicit named policy. Source and
JSON validation reject duplicate routes; old no-override documents retain their
serialized shape. SDK 10 Release tests passed: override contract 12, asset
lowering 51, asset JSON 10, HIR lowering 12 and managed dispatch 6. Logs are
`artifacts/m13-3c-2-{override-contract,csharp-lowerer,script-asset-json,hir-lowerer,managed-dispatch}-release.log`.

The native materialization checkpoint is implemented. Both Declaration readers
accept the new schema. Preflight verifies the actual inherited event and full
signature before mutation; generated graphs preserve source identity and native
parameter mapping, including const-reference inputs. Compile and package reload
retain managed bindings for both event kinds. Product-owned overrides can be
retargeted and resynchronized; user-owned events remain protected. Default Actor
ghost events do not block a new declaration. This retarget test is an asset
materialization test, not evidence for retargeting a running managed instance.

Editor Development compilation and
`UnifiedScript.Unreal.ScriptClass.BlueprintInheritedEventMaterialization` passed,
including real Actor event ownership, invalid declarations, idempotency,
retargeting and transactional collision rejection. The existing
`BlueprintManagedFunctionDispatch` regression passed after fresh Release
deployment. Evidence is under `artifacts/m13-3c-2-native-materialization/`;
`blueprint-inherited-event-materialization-reload-dispatch-assertions.log`,
`editor-development-build-reload-dispatch-assertions.log`, `emit-release-fresh.log` and
`blueprint-managed-function-dispatch-regression-release-staging.log` record these
checks. This checkpoint covers materialization and binding; the parent-call
runtime evidence is recorded separately below.

The managed reload barrier passed 12 focused SDK 10 Release tests. They emit
replacement DLLs to the same path with the same CLR type and exercise actual
event dispatch, commit/finalize, abort and rejection followed by a compatible
replacement. Rejection preserves the old generation and lifecycle history;
event route additions/removals, retargeting, raw path/name casing and method/
return/parameter changes are covered. Existing managed transaction/dispatcher
tests passed 9, static-library reload tests 4 and source override-contract
tests 12. Logs are under `artifacts/m13-3c-2-override-reload/`. These are managed
tests; the runtime parent-call evidence below is a subsequent checkpoint.

The parent producer/transport/consumer is implemented. Ten focused managed
tests pass for helper contracts, initialization/destruction rejection, state
flush/hydration, original-failure preservation, retired-generation rejection,
correlated retry and a container input larger than 4 KiB encoded once. The
final arena reuses one bounded pooled allocation and slices its logical call
capacity to the encoded input size, retaining a 4 KiB minimum. The focused
output bridge regression passes 6. Logs are
`artifacts/m13-3c-2-override-reload/managed-parent-arena-final.log` and
`artifacts/m13-3c-2-override-reload/managed-output-bridge-regression.log`.
Before that arena-only refinement, the existing bridge tests passed 13 and
in-process transport regressions passed 84; those paths did not change in the
refinement. Their logs are under `artifacts/m13-3c-2-parent-invocation/`:
`managed-parent-regressions.log` and `managed-correlated-regressions.log`.

`BlueprintParentInvocation` passes with a real managed override calling native
BNE and empty BIE parents, ordered return/ref/out values, repeated calls and a
large array result that retries without executing the parent twice. An ordinary
intermediate Blueprint also executes its own VM return/ref/out body through
`BlueprintIntermediateParentInvocation`; its native ancestor counter remains
unchanged. The VM fixture uses `UK2Node_VariableSetRef` for the entry's reference
parameter and removes the graph's automatically generated parent-call node.
Evidence: `parent-automation-final-2.log`,
`intermediate-parent-automation-6.log` and `editor-parent-build-12.log` in the
same directory. The Demo was freshly staged before the CLR runs.

`BlueprintParentInvocationComponent` passes with a generated ActorComponent
script hosted on an ordinary Actor. Manual activation and an inherited
ReceiveTick call prove that the receiver is the component while its script
target remains the Actor; this does not establish BeginPlay/EndPlay ordering.
`BlueprintParentInvocationOwnership` passes with an inactive canonical
component followed by an active matching owner, rejects two active owners,
and resumes the original instance after the conflicting instance stops.
Direct invalid Host, result descriptor and uncorrelated requests are rejected
without executing the parent. `UnifiedScript.Unreal.NativeTransport.Abi` also
passes. Evidence is `parent-component-automation-1.log`,
`parent-ownership-automation-4.log`, `editor-parent-ownership-build-5.log` and
`native-transport-abi-parent-automation.log`. Test assets were cleaned up.
After the final managed tests, a fresh Release emit with dependency validation
passed (`emit-release-final.log`). `parent-automation-arena-final.log` records
successful base, Component and Ownership selectors against that staged build,
including the large-result retry. The ordinary Blueprint intermediate and ABI
results above predate only the managed arena refinement and additional tests;
their native implementation was unchanged.

Native integration must cover both Blueprint/state Declaration readers,
materializer planning/rollback/idempotency, persistent generated-class binding,
load/recompile rebinding and the parent call producer/transport/consumer.
Use the existing instance reload coordinator and preserve protected HostProject
files. Generate derived files through the existing tools.

Relevant execution proof includes native and ordinary Blueprint callers for
both event kinds, a child Blueprint with and without an explicit parent call,
type/owner/collision rejection without mutation, same-World body replacement,
retired-generation rejection and World cleanup. Include typed return/ref/out
and one large parent result that needs a buffer retry. Package the working
fixture through HostProject once the runtime contract is complete.

The managed activation boundary is implemented. `CreateScript` now supplies an
explicit empty expectation; `CreateScriptWithBlueprintContract` accepts the
bounded native payload. Production World creation requires an expectation,
and both initial and candidate generation loads compare it before Activator.
The existing full managed-to-managed reload barrier remains in place. The
comparison reports the first mismatched route/member/type position and retains
only immutable, reflection-free expected data on the live instance.

SDK 10 Release validation passed: activation contract 12, actual legacy/new
exports 1, override reload 13 (including the persisted-expectation candidate
test), managed dispatch 6, reload transaction 3 and source override contract 12.
The cold mismatch fixture proves that neither constructor nor OnStart executes.
The same-path DLL reload fixture rejects a changed return type before stopping
the old instance, then accepts a compatible body replacement. Typed comparison
also covers enum identity, struct field order, CLR-only field renaming,
Soft Reference/string distinctions and collection shapes.
Evidence is in `artifacts/m13-3c-2-activation-contract/`:
`activation-contract-tests-final-4.log`, `activation-entrypoint-tests-final.log`,
`override-reload-final.log`, `managed-dispatch-regression.log`,
`managed-reload-regression.log`, `override-source-contract.log` and
`managed-build-final.log`.

The native activation path is integrated. The exact generated class builds the
contract from its own persisted bindings and actual native properties; it does
not read Declaration data to reconstruct the expectation. Component startup
builds that contract before claiming gameplay ownership and invokes the new
validated export. Missing exports fail this path with a staging diagnostic,
while legacy callers and World readiness keep their existing entrypoints.

Fresh Release Demo emission and dependency validation passed
(`native-emit-release.log`), followed by the Editor build
(`native-editor-build-12.log`). Seven focused CMD Automation selectors passed:
`blueprint-activation-shapes-3.log` covers enum, fixed FVector, both Soft
Reference kinds, Array/Set/Map and ordered FHitResult fields, plus invalid field
paths, missing/duplicate/extra container markers and fixed native arrays.
`blueprint-inherited-materialization-contract-3.log` proves that an actual
save/unload/load preserves the complete contract JSON. `blueprint-parent-final.log`
covers the Actor, Component and Ownership selectors, including native-route and
managed-name rejection through the actual startup boundary. Managed count,
registered handles and lifecycle claims remain at their stopped values after
managed rejection; restoring the binding restores normal startup and dispatch.
`blueprint-intermediate-parent-after-contract.log` and
`blueprint-managed-dispatch-after-contract.log` retain ordinary Blueprint parent
and non-override dispatch behavior. All logs are in the activation artifact
directory above. The final build differs from the shape/materializer/intermediate/
dispatch runs only by additional parent-test rollback assertions.

Cold activation agreement is now closed.

Live Sync contract-change protection is implemented and verified on 2026-09-08.
The HostEditor build passed (`host-editor-build-final-4.log`), and
`blueprint-override-live-sync-final-5.log` passed with a normal Automation queue
exit. The real managed fixture runs the same generated class in two Worlds.
It rejects additions, removals, retargeting, managed-name and raw-route case
changes, and native signature mismatches before changing the class. Tests check
the complete activation contract, function graphs, active generations, managed
instances, registered handles and lifecycle claims, then execute both original
instances successfully. One remaining World still blocks a changed route.
After stopping, synchronization persists a case-only route change and restoring
the contract permits restart. Another generated class may remain active while a
stopped target Actor survives synchronization and component reinstancing.

The inherited-event materialization regression passed in
`blueprint-inherited-event-materialization-final.log`; Actor, Component and exact
ownership parent-call regressions passed in `blueprint-parent-invocation-final.log`.
These logs are under `artifacts/m13-3c-2-live-sync/`. Earlier failing runs are retained: test cleanup
used stale/reinstanced objects or called EndPlay before BeginPlay, and the added
route fixture initially omitted its copied parameters. The final passing run
uses current objects and a complete candidate Declaration; it does not mutate
the live bindings to manufacture a source change.

An unintended `BuildPlugin -Rocket` run is recorded separately in
`editor-build-live-sync-final-3.log`. Its UnrealGame compilation failed on missing
`FScriptDelegate`, `FTextProperty` and `UWorld` declarations in the callback
transport, declaration materializer and invocation bridge. These direct
dependencies are now included explicitly. The Host Win64 Development target
check passed (`artifacts/m13-3c-2-runtime/runtime-build-headers.log`); the final
check waited for an existing build and reported the target up to date. This is
non-Editor target evidence; BuildPlugin distribution acceptance was not rerun.

ActorComponent lifecycle integration is implemented and verified on 2026-09-08.
The fixture exercises native DispatchBeginPlay/RouteEndPlay with ScriptAssetPath
startup and configuration changes after activation. Ordinary Blueprint children
with and without explicit parent calls retain their own event bodies. Manual
startup does not replay ReceiveBeginPlay; missing-assembly activation completes
native lifecycle cleanup without leaking managed instances, handles or gameplay
ownership. Typed ReceiveEndPlay observes Quit as 4 before lifetime cancellation,
and managed destruction observes the cancelled lifetime.

The selected API Contract explicitly includes the reflected EEndPlayReason enum.
The exporter now follows dependencies of exported single and multicast delegates,
so Actor.OnEndPlay supplies the engine definition. API Contract tests passed 7/7
(`managed-api-contract-release-2.log`), followed by fresh Release Demo emission
(`emit-demo-lifecycle-final.log`). The configured class-filter reflection export
and binding generation passed (`reflection-export-config.log` and
`binding-generation-lifecycle.log`).

The HostEditor build passed (`editor-build-lifecycle-final-3.log`). Seven CMD
Automation tests passed with normal queue exits: lifecycle
(`cmd-component-lifecycle-final.log`), Actor/Component/exact-owner parent calls
(`cmd-parent-basic-final.log`, three tests), ordinary Blueprint intermediate
parent calls (`cmd-parent-intermediate-final.log`), Live Sync
(`cmd-live-sync-final.log`) and delegate enum export
(`cmd-reflection-exporter-delegate.log`). Evidence is under
`artifacts/m13-3c-2-lifecycle/`. The exporter run predates only native lifecycle
test expansion; its implementation was unchanged. These fixtures execute in
HostEditor and do not establish cooked support.

The broader runtime/cooked extension proof remains open; M13.3C-2 is still in
progress.

## Cooked extension checkpoint in progress

Reuse the checked-in parent-invocation and component-lifecycle managed scripts.
An Editor fixture creates six separate packages under one GUID-named
`/Game/UnifiedScriptEventExtensionFixture_<GUID>/` directory: a managed Actor
over the native fixture; an ordinary Blueprint Actor parent with real scalar
and ReceiveInherited bodies; its managed Actor child; a managed component; and
two ordinary Blueprint component children with and without explicit parent
calls. Separate packages preserve each Script Asset's identity. Persist the
Script Asset configuration on the owning CDO before saving, and retain
project-relative managed payload paths. Saving, unloading and loading must
preserve generated bindings and ordinary Blueprint bytecode.

Only a successful fixture writes a `unifiedscript.event-extension-fixture/v0.1`
receipt. It names the exact package directory, all six package names/filenames,
and five executable generated-class paths. The tool validates that each path
belongs to those packages in the dedicated HostProject Content directory before
cooking or cleanup. It cooks that directory with `/Engine/Maps/Entry`, leaving
existing Demo assets and protected project configuration intact.

The Host verifier loads the cooked classes and uses their persisted startup
configuration. Native calls must enter the managed override and execute its
native or ordinary Blueprint parent, retaining scalar return/ref/out values and
all 258 array results while executing the parent once. The ordinary Blueprint
ReceiveInherited body must observe both its text and Actor arguments. Native
BeginPlay/EndPlay must retain the three reviewed component traces, and teardown
must restore managed instances, handles, lifecycle claims and World counts.
The verifier requires cooked data and a chunk/hostfxr resolved inside the
executing archive; it reports PASS only after these assertions and cleanup.

This checkpoint does not replace the remaining real-UE DLL body replacement
proof. The existing override reload tests use the real managed instance and
transaction machinery with a fixture native transport. A changed body still
needs to be exercised through a live UE generated class before C-2 closes.
