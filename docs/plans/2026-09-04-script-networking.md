# 2026-09-04 — Script Class replication and RPC

Status: in-progress

## Outcome and scope

Use replicated managed state, RepNotify and Server/Client/Multicast RPCs in an
ordinary generated Actor/ActorComponent Script Class, with an actual connected
client/server example. Reuse Unreal replication and the existing generated
Blueprint/native/CoreCLR dispatcher, not a separate network protocol.

Puerts' [RPC example](https://puerts.github.io/docs/puerts/unreal/uclass_extends/#rpc)
sets field replication/notification, lifetime conditions and method direction/
reliability. Compare those observable behaviors, not decorator syntax.

## Decisions and remaining work

- Preserve the existing `[Expose(Replicated = true)]` state declaration. Add
  explicit lifetime conditions and a managed RepNotify handler binding.
  Keep the portable Declaration IR free of Unreal flag values and native types.
- Add a public Unreal RPC attribute and carry its direction/reliability through
  lowering, strict serialization, generated function flags and runtime rebinds.
  RPCs must be instance, void, input-only gameplay methods; reject conflicting
  event/interface/input/delegate ownership before publishing a generated class.
- Use normal UE ProcessEvent/callspace/connection ownership for sends and the
  existing managed function dispatcher for receives. C# network sends must be
  explicit, type-checked API calls; do not silently rewrite normal C# calls into
  network operations or introduce a second implementation of the method body.
- Hydrate received state before managed callbacks. Client managed lifecycle
  writes must not overwrite authoritative replicated properties. Guard nested
  send/receive state synchronization and generation/World lifetime.
- Configure Actor/component replication, conditions and notification identities
  durably across class compilation and package reload. Prove actual replication
  and RPC traffic with owned and non-owned client cases, not role flags alone.
- Keep protected files untouched. No external project modifications, new
  platform claim, packaged WindowsServer claim or Puerts performance claim.

## Relevant verification

Contract/lowering/patch preservation and failure tests; UE build and generated
class save/reload/dispatch checks; connected client/server state and RPC checks
including ownership rejection, RepNotify, repeated updates and teardown.
Leave the networking batch open until the public workflow and connected example
are verified; declarations and local ProcessEvent tests alone are insufficient.

### Actor checkpoint

The C# sample and native materializer now carry the full Actor path. A saved and
reloaded generated class runs on one server and two real loopback clients.
Replication/OwnerOnly, initial and later RepNotify previous values, all three RPC
directions, non-owner rejection, local replicated-write rejection, client reload
and World detach pass in `artifacts/m13-5-network/unreal-suite.log` (38 CMD tests).
The complete managed run passes 784 tests; the later payload/patch-focused run
passes 28 cases (`managed/networking-contracts-final.trx`). UE build, chunk emit
and repository validation pass.

The later affected native rebuild and focused networking run pass in
`artifacts/m13-5-network/networking-verified.log`, including owned notification
parameters across GC, first-previous-value coalescing, cancellation on stop and
the same connected Actor scenario. This run covers the final GC fix and exact
previous-value pin check; the earlier full suite predates those changes.

UE Blueprint compilation clears previous-value RepNotify signatures, so the
generated-class binding table restores them after compile/load. Initial client
notifies precede managed activation; the component retains owned, GC-visible
parameter snapshots and drains them after activation. Stop/failed start drops
them, and stopping does not re-arm initial buffering. Network runtime Automation
uses a scoped precompiled-Play mode to avoid invoking the unrelated daily source
build coordinator; ordinary Play behavior remains unchanged.

Queued parameters belong to a `UFunction`, not a `UScriptStruct`: reference
collection must traverse the function properties directly. The GC regression
test exercises that storage before managed activation.

Next: verify ActorComponent replication/RPC and remaining common payload and
condition shapes. Push-model dirty notifications are implemented but need a
push-enabled connected run. Travel and cooked networking remain unproven. RPC
payloads are currently 0-4 common input values; reject containers, Soft References,
complex structures, return/ref/out and conflicting bindings before publication.
