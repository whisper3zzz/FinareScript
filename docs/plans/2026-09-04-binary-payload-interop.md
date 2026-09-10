# 2026-09-04 — General binary payload interop

Status: complete for the generated UObject-member payload scope below; broader binary parity remains open.

## Outcome and scope

Pass binary payloads through generated C#/native APIs in both directions, with
ordinary call/return and ref/out behavior, without per-byte value cells or a
gameplay-specific transport. Puerts' [ArrayBuffer examples](https://puerts.github.io/docs/puerts/unreal/script_call_uclass/)
show native buffer access and script-to-native payload calls. That is the use
case, not a promise to reproduce JavaScript memory ownership.

## Decisions and remaining work

- Reuse the existing v3 arena and result-retry transaction. Add an append-only
  ByteBuffer value kind and explicit capability; old transports must reject
  unsupported buffers before invoking native behavior. No changes to ABI sizes
  or the World schema-channel protocol.
- A managed buffer may own a snapshot or retain a borrowed managed byte array.
  The native boundary copies contiguous bytes; returned buffers are owned
  snapshots, safe after arena reuse, reload and World teardown. Native values
  retain immutable shared storage. Local views borrow from that retained owner.
  This is not cross-language shared-memory/zero-copy access.
- Connect public buffer values, codecs, generators and a real UObject native
  extension example. Keep raw-pointer sharing, mutable native views, reflected
  Blueprint state and the ordinary-object fixed ABI lane as explicit follow-ups.
- Support payloads beyond the existing 4 KiB stack arena with bounded managed
  allocation before invocation. Preserve exactly-once side effects during output
  size retries and leave ref/out unchanged on failure.

## Delivered

- Public `UnrealByteBuffer` / `FFinareByteBuffer`, direct property and
  input/return/ref/out generation, typed bridge overloads and visual descriptions.
- Append-only ABI v3 kind 25 and capability bit 11, keeping existing cell/frame
  sizes. One buffer carries contiguous bytes, up to 16 MiB; binary call arenas
  are limited to 64 MiB. Metadata, offset/length, old capabilities, duplicate
  input-span allocation pressure and oversized retry capacities are checked.
- Real CoreCLR example exchanges 64 KiB input and 72 KiB return/ref output,
  retains snapshots, replaces native data, reloads and destroys its World.
- Nested buffer containers and reflected Blueprint declarations fail before
  generation. Native callbacks, the ordinary-object lane, zero-copy views and
  cooked/interactive acceptance remain later work, not claims of this batch.

## Relevant verification

Exercise empty, embedded-zero, sliced, large and malformed payloads; capability
rejection before side effects; output retry without re-invocation; independence
of returned snapshots. Compile native consumers and run CMD/CoreCLR calls,
reload and teardown. Preserve harness.local.json protected paths.

UE 5.8.1 Editor build passed. All 770 managed tests (26 buffer cases) and all
37 CMD tests passed, including the two native/CoreCLR binary tests. Demo chunk
emission and all nine deterministic generation comparisons passed. Results are
in `artifacts/m13-5-binary/managed/managed-complete.trx`, `unreal-suite.log` and
`generation/deterministic-generation.json` beneath that artifact directory.
After the full run, the visual test was strengthened to project generated buffer
calls to Graph; all 26 focused tests pass in `managed/buffers-graph-final.trx`.
Repository validation passed after CMD completion; its earlier deployment-source
check failed while the suite was running, and the checked conditions were all
true once the suite finished. No source or validator rule was changed to pass it.

Next: follow the Roadmap's basic replication/RPC batch. Do not exhaust every
binary or native-object signature before expanding capability breadth.
