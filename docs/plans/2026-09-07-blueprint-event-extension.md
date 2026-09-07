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
  proposed values are `BlueprintParentCalls = 1 << 14`,
  `UnrealNativeOperationV3.InvokeParent = 3` and parent frame flag `1`.
  The existing table prefix and function pointers stay unchanged; capability
  validation precedes use, and read/write paths must still require zero flags.
  The member ID remains the hash of the declared `classPath:functionName`;
  the explicit parent operation distinguishes it from an ordinary native call.
  Native pending results use a distinct operation discriminator as well.
  The native host resolves the script component handle and persisted binding before
  dispatch. The generation bridge must reject calls after retirement.
  Use a separate optional parent-invocation capability and bridge interface;
  do not add required methods to existing user bridge implementations. The
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
  Decode/preflight inputs and output descriptors before user code; publish
  outputs only after every value has been read and encoded successfully.
  The existing `InvokeWithOutputs` path already encodes the reviewed value
  families; reuse it through an operation-aware internal helper. The in-process
  and correlated-scope consumers must preserve the parent operation as well.
  Native parent dispatch must share the Blueprint property codecs and the
  existing call-frame result cache. The older reflection bridge's
  `ReadValue`/`WriteValue` does not carry arbitrary managed struct field order;
  its scalar Soft Reference values also differ from the string representation
  used by Blueprint call frames. Expose the existing `PackValueV2` and
  `UnpackReturnValueV2` through a module-private shared interface, retaining
  persistent field bindings. Do not create a second recursive struct codec or
  weaken the older property lane's type checks. Parent results can become owned
  call-frame values before entering the existing retry cache.

## Lifecycle integration still to resolve

The native component owns the managed host handle even for ActorComponent
scripts whose `TargetHandle` is their Actor. Parent resolution must select the
bound generated class and actual event receiver from that host, including
ordinary Blueprint subclasses, rather than equating TargetHandle with the
event receiver. Generation retirement remains the managed bridge boundary.

Engine Actor BeginPlay starts components before emitting ReceiveBeginPlay.
The current script component, however, calls Super::BeginPlay before starting
managed code and stops managed code before Super::EndPlay. Consequently its
own inherited ReceiveBeginPlay/ReceiveEndPlay cannot simply be dispatched with
the existing order. For component classes with these managed override bindings,
automatic activation must precede Super::BeginPlay and retirement must follow
Super::EndPlay, including an ordinary Blueprint child that calls its parent.
Keep the existing order for unaffected classes and preserve the explicit
start/stop policy; do not lazily start from generic function dispatch. A failed
start must still finish the native lifecycle path and roll back its candidate.
Test this in runtime integration; materialization alone does not establish
lifecycle-event support.

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
does not compile a second dispatch delegate. This managed barrier is implemented;
it does not yet compare the DLL with a saved native Blueprint contract.

Cold activation also needs a loaded-type versus persisted Blueprint contract
check before lifecycle user code. Comparing only the old and new DLLs during
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
checks. Parent-call transport and end-to-end inherited-event execution remain
open; materialization and binding do not prove that runtime behavior.

The managed reload barrier passed 12 focused SDK 10 Release tests. They emit
replacement DLLs to the same path with the same CLR type and exercise actual
event dispatch, commit/finalize, abort and rejection followed by a compatible
replacement. Rejection preserves the old generation and lifecycle history;
event route additions/removals, retargeting, raw path/name casing and method/
return/parameter changes are covered. Existing managed transaction/dispatcher
tests passed 9, static-library reload tests 4 and source override-contract
tests 12. Logs are under `artifacts/m13-3c-2-override-reload/`. These are managed
tests; UE runtime, parent invocation and cooked extension proof remain open.

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
