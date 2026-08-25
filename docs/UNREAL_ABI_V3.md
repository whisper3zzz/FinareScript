# Unreal native ABI v3

Status: layout contract frozen and active. The live World host advertises and
executes fixed scalars, enums, generation-safe object/class handles,
vector/rotator, transform, UTF-8 text, recursive containers, and return plus
`out`/`ref` results. Variable-size invocation results use a non-reexecuting
staging protocol owned by the retained World context.

ABI v3 replaces the v1/v2 callback response with a synchronous, caller-owned
call frame. It is a separate function table rather than a v2 suffix because
the ownership and result semantics are intentionally incompatible. Existing
v1 and v2 exports remain available while the managed host migrates.

## Goals and non-goals

The contract is designed to support strongly typed generated calls, common UE
value types, containers, and return plus `out`/`ref` results without allocating
a callback box or transferring allocator ownership across CoreCLR and Unreal.
It does not make reflection or UObject access thread-safe, expose raw UObject
pointers, or advertise a capability before its complete frame path is usable.

## Platform and execution contract

- ABI v3 initially targets 64-bit little-endian UE 5.7/5.8 processes.
- Calls are synchronous and game-thread-only.
- The call frame, argument cells, result cells, and byte arena are owned by the
  caller and borrowed by native code only until the entry point returns.
- Neither side frees memory allocated by the other side.
- Native code resolves every object through the World-scoped generation handle
  registry. `Payload0` never contains a raw `UObject*`.
- `Flags`, `Reserved8`, `Reserved16`, and `Reserved32` must be zero unless a
  later ABI revision assigns a meaning. Non-zero reserved bits produce
  `InvalidFrame`.

## Stable layouts

All structures use 8-byte packing and alignment. Sizes and significant offsets
are compile-time asserted in C++ and tested through `Marshal.OffsetOf` in C#.

### Value cell: 48 bytes

| Offset | Field | Size | Meaning |
| ---: | --- | ---: | --- |
| 0 | `Kind` | 1 | `EUnifiedScriptNativeValueKindV3` |
| 1 | `Flags` | 1 | `Null` and `ArenaBacked` |
| 2 | `Reserved16` | 2 | must be zero |
| 4 | `Auxiliary` | 4 | kind-specific count or descriptor |
| 8 | `TypeId` | 8 | stable canonical Unreal type identity |
| 16 | `Payload0` | 8 | scalar bits, handle, or arena offset |
| 24 | `Payload1` | 8 | second scalar or arena byte length |
| 32 | `Payload2` | 8 | third scalar or kind-specific metadata |
| 40 | `Payload3` | 8 | fourth scalar or kind-specific metadata |

Published kinds are append-only: `Void`, signed and unsigned integer widths,
`Float32`, `Float64`, `Enum`, `Name`, `String`, `Text`, object/class handles,
`Vector3`, `Rotator3`, `Transform`, arbitrary `Struct`, `Array`, `Set`, and
`Map`.

Primitive numeric values store their exact bit representation in `Payload0`.
Object and class values store the 64-bit generation handle in `Payload0`.
`Vector3` and `Rotator3` store three IEEE-754 doubles in `Payload0..2`, matching
UE5 large-world-coordinate scalar precision. `String`, `Name`, `Text`,
`Transform`, `Struct`, and containers set `ArenaBacked`; `Payload0` is the byte
offset and `Payload1` is the byte length. Containers store logical entry count
in `Payload2`, 48-byte child-cell stride in `Payload3`, and a contiguous child
descriptor block followed by any recursive payload bytes. Their shape is
validated against the generated canonical `TypeId`, never a process-local
reflection pointer.

Strings and error messages are UTF-8 byte sequences without a trailing NUL.
Offsets and lengths are validated with overflow-safe bounds checks.

## Call frame: 72 bytes

| Offset | Field | Meaning |
| ---: | --- | --- |
| 0 | `Arguments` | borrowed input value cells |
| 8 | `ArgumentCount` | number of input cells |
| 12 | `Flags` | zero for ABI v3 |
| 16 | `Results` | caller-owned output cells |
| 24 | `ResultCapacity` | available output cells |
| 28 | `ResultCount` | expected result descriptors on entry; produced cells on success |
| 32 | `ByteBuffer` | caller-owned input/output arena |
| 40 | `ByteCapacity` | arena bytes available |
| 44 | `ByteCount` | input bytes occupied on entry; total bytes used on success |
| 48 | `RequiredResultCapacity` | retry capacity on `BufferTooSmall` |
| 52 | `RequiredByteCapacity` | retry capacity on `BufferTooSmall` |
| 56 | `InvocationError` | stable invocation category byte |
| 57 | `HandleError` | stable object-handle category byte |
| 58 | `Status` | overall ABI v3 call disposition |
| 59 | `Reserved8` | must be zero |
| 60 | `ErrorMessageOffset` | UTF-8 error slice in the arena |
| 64 | `ErrorMessageLength` | UTF-8 error byte length |
| 68 | `Reserved32` | must be zero |

Property read takes zero arguments and one expected result descriptor. Property
write takes one argument and zero results. Function invocation consumes the
declared input cells; the caller initializes each expected result cell's
`Kind`, flags, and `TypeId`. On success native code replaces those descriptors
with the ordinary return value first (when non-void), followed by generated
declaration-order `out`/`ref` result cells. This lets native code preflight every
fixed-size output before a side-effecting call is dispatched.

`Success` is the only status that permits produced results. An Unreal dispatch
or conversion failure reports `InvocationFailure` plus the two stable error
category bytes. A detached World context reports `TransportUnavailable`.
Malformed pointers, counts, flags, kinds, offsets, type IDs, or member IDs
report `InvalidFrame` or `InvocationFailure` as appropriate. Arena-backed input
slices must end at or before the entry `ByteCount`. Native output and error
messages are appended at that cursor, so input slices remain unchanged.

If either output buffer is too small for all declared fixed-size outputs, native
code reports `BufferTooSmall` before dispatch. For a variable-size UFunction
result, native dispatch occurs once into owned values; if the caller arena is
too small, those values are staged in the retained World context under the
operation, target, member, arguments, input bytes, and result descriptors.
`BufferTooSmall` sets both required capacities, leaves `ResultCount` at zero,
restores the entry `ByteCount`, and publishes no partial result. The immediate
retry supplies the unchanged input prefix and copies the staged result without
executing the UFunction again. A different request invalidates the single
pending staged result. The managed bridge performs this expansion retry
automatically; property reads can be measured directly because they do not
dispatch a side-effecting UFunction.

## Transport table: 64-byte prefix, 72-byte current export

The independent table header contains `AbiVersion = 3`, table/value/frame
sizes, a 64-bit capability mask, retained context operations, and three stable-ID entry points:
`ReadPropertyById`, `WritePropertyById`, and `InvokeById`. Each entry point has
the same signature `(Context, TargetHandle, MemberId, CallFrame*)`. The current
72-byte export appends an optional `InvokeInt32x2ById` pointer at offset 64 for
generated `int32(int32, int32)` members. Clients first validate the 64-byte
prefix and only read this scalar lane when `TableSize >= 72`; older clients
therefore ignore it safely.

Capabilities independently advertise fixed scalars, enums, object handles,
vector/rotator, transform, UTF-8 inputs, UTF-8 results, container inputs,
container results, and output/reference support. Splitting variable-size input
from result capabilities lets older runtimes decline shapes that lack the
staging protocol.
Every advertised capability must work through all three relevant entry points;
unsupported capabilities deterministically fall back to v2 in the composite
managed bridge. `UEUnifiedScript_GetNativeTransportV3` publishes the independent
table, and the append-only v2 bootstrap table exposes its pointer to upgraded
managed Worlds without changing the established CoreCLR World-entry signature.
Older v2 clients ignore that suffix.

## Stable identities and compatibility

`MemberId` continues to use the existing generated 64-bit FNV-1a identity over
the canonical member path. `TypeId` uses the same 64-bit FNV-1a algorithm over
the canonical Unreal type descriptor, including recursive container shape.
Generators must reject duplicate IDs within one manifest and emit both
canonical names in the diagnostic; runtime tables additionally compare the
expected generated type ID before conversion.

ABI v1/v2 behavior is not changed. The managed host validates the v2 prefix,
then probes the optional v3 pointer, capability mask, context functions, and all
three sizes. It falls back to v2/v1 when v3 or a required capability is absent.
Within v3, enum values and optional transport-table suffixes may only be
appended, while reserved frame/value fields may only be assigned by a new
documented ABI revision. Any change to the frozen 64-byte table prefix or the
frame/value layouts requires v4.

## Activation state

The active subset includes scalars, enums, core structs, generation-safe
object/class handles, UTF-8 input/results, recursive array/set/map
input/results, and declaration-order return plus `out`/`ref`. Generated thunks
use the frame; invalid descriptors fail before dispatch; variable-size overflow
Automation proves a retry increments the generated bridge invocation counter
only once; and managed tests cover automatic arena expansion. The 10,000-call
fixed-scalar test still reports zero steady-state managed allocations. ABI v1/v2
remain available for old hosts and unsupported reflected shapes, while the live
UE 5.8 lifecycle Demo now reports `CallFrameCalls=20` and
`LegacyCalls=0`. Matching two-input/one-result `int32` generated functions use
the scalar suffix and bypass generic frame construction while retaining the
same World context, stable member ID, handle validation, and failure status.
