# 2026-09-04 — Generated reflected callback arguments

Status: complete for the reflected callback batch; broader callback families remain in M13.5.

## Outcome and scope

Pass a typed managed callback to an ordinary reflected native API, including a
synchronous return and supported ref/out values. Native code may retain a copy
of the dynamic delegate until its explicit callback lease, generation or World
ends. Generate the API and visual descriptions from reflection/Contract data;
do not add another special-purpose completion service.

Puerts documents this public use case in its
[native-to-script examples](https://github.com/Tencent/puerts/blob/master/doc/unreal/zhcn/engine_call_script.md).
Existing BlueprintAssignable event subscriptions are one-way notifications and
remain compatible; they are not the implementation of this callable route.

## Decisions and remaining work

- Export an explicit callback signature with return type, parameters and
  directions. Generate a typed C# delegate and a disposable callback wrapper.
  Reflected callback parameters use that wrapper, not arbitrary delegate casts.
- Add an optional native callback table without changing the existing event ABI
  or transport prefixes. Reuse v3 value/arena codecs for request and response;
  a callback is invoked once even when its output needs a larger allocation.
- Give callbacks separate generation scopes and explicit disposal. A transient
  UObject proxy supplies the matching dynamic UFunction; the World context owns
  it while active, and the ordinary weak object registry supplies its token.
  Validate signature, scope and World before passing a native delegate copy.
  Unbinding must disable saved copies before managed callback state is freed.
- Marshal callback outputs to temporary native storage and publish them only
  after all outputs validate. Catch managed exceptions at the ABI boundary;
  never claim rollback of arbitrary side effects in the enclosing native API.
- Hold a managed lifetime lease across each forward native invocation, without
  holding its monitor while UE executes or queues the call. Disposal disables
  registrations immediately and defers the context release until active calls
  return; it must not wait for a reentrant caller.
- Append correlated result invocation/cleanup entrypoints to v3 (96-byte table,
  old prefixes unchanged). One logical call ID survives arena retries. Keep at
  most 64 pending/in-flight entries and 64 MiB of encoded pending capacity per
  context, with explicit failure and no silent eviction. Clean up abandoned
  responses and World detach. Old single-slot runtimes retain their old behavior,
  not the new interleaved-retry guarantee.
- Complete reflection/Contract/dependency closure, generated C#/C++ and nodes,
  native registration/invocation, generation cleanup and a retained-callback
  public example. Reuse current Script Class foundations; ordinary C++
  `std::function`, callable return values and nested callbacks are separate scope.

## Relevant verification

Cover typed return/ref/out, native retention and later invocation, output
publication failure, signature mismatch, old transport rejection, reentrancy,
self-disposal, GC, failed reload/rollback, generation replacement and World
detach. Run affected generation/ABI tests, the UE build and relevant CMD tests.
No new cooked, physical UI or Puerts performance claim is implied. Preserve all
`harness.local.json` protected paths.

## Delivered result

Implemented the optional managed table loader, scope/lease ownership, signature
generation, shared v3 request/response codecs, dynamic native proxy and public
`CallbackGameplayDemoScript`. Native userdata is a never-recycled dictionary
identity, not a raw/recycled GCHandle. Local callback references survive handler
self-disposal, and retired identities cannot reach a later registration.

The first native run exposed heap corruption during the second GC. A captured
main-thread stack was in mimalloc page collection. Source inspection showed that
ProcessEvent shallow-copies its parameter block: modifying FString/ref storage in
Stack.Locals corrupts the caller's ownership. The proxy now declares out-parameter
flags, owns deep-copied parameter storage, uses Stack.CurrentNativeFunction, and
publishes outputs through FOutParmRec / RESULT_PARAM. The native lifecycle test
then completed successfully, including the same two GC points.

Final callback verification on 2026-09-04:

- Managed generation/contract/transport tests: 187 passed in
  `artifacts/m13-5-callbacks/managed/callback-contracts-final.trx`.
- UE Host Editor build and Demo chunk emission passed:
  `artifacts/m13-5-callbacks/ue-build-correlated-generated.log`,
  `ue-build-abi-test.log` and `demo-chunk-reentry.log` in that directory.
- Six CMD cases passed in `artifacts/m13-5-callbacks/callback-reentry-native.log`:
  callback NativeLifecycle, GeneratedManagedLifetime and WorkerReentry;
  NativeTransport.CorrelatedResults; native-object ManagedLifetime; and byte-buffer
  ManagedRoundTrip. The same run found the ABI test still expecting an 80-byte
  table. Its updated 96-byte suffix/old-prefix-offset assertions pass separately
  in `abi-correlated-verified.log`. No production code changed after that run.
- WorldHost.Lifecycle and LifecycleStress pass in `world-lifetime-verified.log`,
  with zero deltas in native contexts, pending worker dispatch and managed Worlds.

The cross-thread deadlock was reproduced in two bounded managed regression cases
before replacing the forward-call monitor with lifetime leases. Both variants
(including transport disposal inside the handler) pass after the fix. The real
CoreCLR worker sample queues two calls before UE dispatches them. Each callback
reads/writes a generated UE property and calls `FormatReply`, a nested generated
variable-result function. The test verifies both workers' bool/string/vector
results, exactly two native evaluations, zero retained results and callback
cleanup. Native transport tests additionally interleave pending replies, reject
a changed request under an existing identity, and exercise cache count/byte
bounds and cancellation without evicting another caller's data.

Callback-only nested struct/enum dependencies reach generated bindings and the
node signature. Typed leases cross a `[Visual]` call boundary; handler bodies
remain ordinary C#. API examples, lifetime/threading rules, limits and unsupported
shapes are documented in [UNREAL_API_CONTRACT.md](../UNREAL_API_CONTRACT.md#reflected-callback-arguments).

## Follow-ups outside this batch

Keep cooked callback execution, physical palette interaction, callback-valued
properties/returns, nested callbacks, ordinary C++ `std::function` and unverified
value/direction combinations explicit. There is no new Puerts performance claim.
Return to the M13.5 cross-family comparison to choose the next common gap; this
checkpoint does not start M13.3/M13.4 or require exhaustive callback expansion.
