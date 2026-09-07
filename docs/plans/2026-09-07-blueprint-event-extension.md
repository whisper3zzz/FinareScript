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
  current instance; keep that C-3 concern explicit.
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
- Parent calls must share the correlated-result lifetime so a larger output
  buffer retry never executes the parent twice. The pending identity includes
  the parent operation, host handle, member identity and request contents.
  Decode/preflight inputs and output descriptors before user code; publish
  outputs only after every value has been read and encoded successfully.
  The existing `InvokeWithOutputs` path already encodes the reviewed value
  families; reuse it through an operation-aware internal helper. The in-process
  and correlated-scope consumers must preserve the parent operation as well.
  Native parent dispatch can reuse `FUnifiedScriptInvocationBridge`'s owned
  `ReadValue`/`WriteValue` codecs and the existing call-frame result cache.

## Implementation sequence and evidence

The managed declaration checkpoint is implemented: the attribute, HIR/lowering
recognition, conditional Declaration versioning, target validation and both
managed dispatchers recognize explicit overrides. Roslyn-resolved attribute
values support constants, `nameof` and the Explicit named policy. Source and
JSON validation reject duplicate routes; old no-override documents retain their
serialized shape. SDK 10 Release tests passed: override contract 12, asset
lowering 51, asset JSON 10, HIR lowering 12 and managed dispatch 6. Logs are
`artifacts/m13-3c-2-{override-contract,csharp-lowerer,script-asset-json,hir-lowerer,managed-dispatch}-release.log`.

Native materialization is the active implementation step. It must accept the
new schema, verify the actual inherited event, preserve its signature and
persist source-to-native parameter mapping through compile and asset reload.
The managed checkpoint alone is not usable in UE; parent-call transport and
end-to-end execution remain open.

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
