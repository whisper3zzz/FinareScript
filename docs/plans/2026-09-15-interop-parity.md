# 2026-09-15 — General bindings and native data access

Status: complete (bounded Editor interop scope)

## Outcome and scope

Support interface containers, interface/collection-bearing reflected structs,
native collection and struct views, ordinary C++ inheritance and std::function
arguments, and synchronous zero-copy C# access to native contiguous storage.
Preserve the uncommitted native-construction implementation and user-owned host
assets/configuration. No commit or push is requested.

## Decisions and delivered scope

- Value parameters and explicit snapshots keep value semantics. Generated native
  views expose element/field operations without replacing the entire property.
  Views retain an object identity and generation, never an unguarded address.
- Contiguous native data is exposed through synchronous Span callbacks. The
  callback cannot retain a span or await; UE reentry is rejected while borrowing
  memory whose address could be invalidated. Native UObject fields containing
  references are never reinterpreted as arbitrary unmanaged C# structs.
- Ordinary C++ inheritance requires generated pointer adjustment and retains one
  ownership lease; a base view must not independently delete the derived object.
- std::function callbacks reuse generation-owned callback leases and reject
  invocation after retirement. No new script runtime or per-member allowlist.
- All six areas are implemented with the limits in `UNREAL_API_CONTRACT.md`.
  The C# fixture uses generated wrappers through the real in-process transport.
  UHT forbids interface Set elements/Map keys; supported interface collections
  are arrays and Map values. Struct collections use an intermediate USTRUCT,
  not illegal direct nested UHT containers.
- Numeric native memory is byte/int/uint/long/float/double, synchronous,
  game-thread-only and bounded to 64 MiB. std::function inputs have fixed
  signatures; native retention does not keep an old managed generation alive.
- Generated Script Class property access now prefers the ABI v3 path, permitting
  rich struct/container values, and retains reflection fallback. The old test
  transport incorrectly returned ValueTypeMismatch for unknown property IDs;
  its missing-member status now matches the real native fallback contract.
- The fixture generator and both new UE tests are wired into `test-api-library`.
  No external project, commit or push is included.

## Relevant verification

Generator compilation and wire/layout tests; native Automation for interface
collections, nested struct fields, native view mutations, expired views,
zero-copy address identity and mutation visibility, callback retirement, and
base-pointer adjustment/destruction. Timing comparisons are required only for a
specific performance claim; address identity establishes zero-copy behavior.

Recorded on UE 5.8.2 Win64 / .NET 10:

- 166 focused managed tests passed: generators, native object lifetime, optional
  transport ABI, interfaces, partial properties and construction.
- FinareHostEditor native build passed, including generated interop thunks.
- 12 CMD Automation tests passed across InteropParity, NativeObjects and
  Callbacks. The new C# scenario covers in-place writes, exception cleanup,
  resize/reentry denial, read-only denial, interface/struct combinations,
  nonzero base-pointer adjustment, callback disposal and same-process reload.
- The previously exported Engine/FinareRuntime library regenerated and compiled:
  3,281 types / 14,475 available members, zero managed warnings or errors. This
  checks the new generated companions against the broad existing metadata set;
  it is not a new full native/Cook acceptance run.
- Logs: `artifacts/interop-parity-managed.log`,
  `artifacts/interop-parity-native-build.log`,
  `artifacts/interop-parity/regression.log`,
  `artifacts/interop-parity-full-build.log`.
