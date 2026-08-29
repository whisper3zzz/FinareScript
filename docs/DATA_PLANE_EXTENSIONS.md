# Generated data-plane extensions

Status: M12.3 project workload extension contract, schema
`unifiedscript.unreal-data-plane-extension/v0.1`.

## Purpose

The core Gameplay/Shared Data ABI v4 stays frozen. A game adds a high-volume
snapshot or event family through a project-owned workload schema instead of
editing the transport tables, copying Unreal containers, or creating another
handwritten facade. One generation step emits:

- C++ POD rows and native extension/channel descriptors;
- C# POD rows and typed channel descriptors;
- a machine-readable authoring and release catalog.

The BlockBreaker sample defines `TargetSnapshots` and `ImpactEvents` in
`samples/Unreal/BlockBreaker.data-plane-extension.json`. Its generated catalog
has fingerprint `0xDE4BA9E28216D610`; the native and managed outputs embed the
same identity.

## Generate and check

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/generate-unreal-data-plane-extension.ps1

powershell -ExecutionPolicy Bypass `
  -File tools/generate-unreal-data-plane-extension.ps1 `
  -Check
```

The lower-level CLI command accepts explicit destinations:

```powershell
dotnet run --project src/UnifiedScript.Cli -- `
  ue-data-plane-extension samples/Unreal/BlockBreaker.data-plane-extension.json `
  --managed-out <extension.g.cs> `
  --native-out <extension.gen.h> `
  --catalog-out <extension.catalog.json>
```

Generation is transactional and deterministic. Unknown JSON fields, duplicate
IDs or names, unsupported field layouts, incorrect row sizes, zero identities,
and channel bounds outside the frozen ABI envelope fail before any output is
published. A schema change produces a new fingerprint and all three artifacts
must be regenerated together.

## Native publication

Include the generated header, register its descriptor with the World host, and
publish only through its generated channel descriptors:

```cpp
using namespace UE::UnifiedScript::Extensions::BlockBreaker;

FString Error;
check(Host->RegisterGeneratedDataPlaneExtension(Descriptor, Error));
Host->PublishGeneratedSnapshotRows(
    Descriptor,
    TargetSnapshots,
    MakeArrayView(TargetRows));
Host->QueueGeneratedEventRows(
    Descriptor,
    ImpactEvents,
    MakeArrayView(ImpactRows));
```

Registration is idempotent only for the exact extension key, revision,
compatibility ID, fingerprint, and channel set. The per-World registry rejects
schema collisions, wrong row kinds/sizes, per-channel row or byte overflow, and
publication outside the native phase. It copies descriptor metadata rather
than retaining project-owned pointers, and World shutdown clears the registry
before shared buffers and object handles are invalidated.

## Managed acquisition

Managed gameplay reads the generated typed channel, so schema identity, row
size, ABI version, compatibility ID, kind, row bound, and byte bound are
validated before a borrowed span is exposed:

```csharp
UnrealGeneratedDataSnapshot<BlockBreakerTargetSnapshotV1> targets =
    Context.DataPlane.AcquireGeneratedSnapshot(
        BlockBreakerDataPlaneExtension.TargetSnapshots);

UnrealGeneratedDataSnapshot<BlockBreakerImpactEventV1> impacts =
    Context.DataPlane.AcquireGeneratedEvents(
        BlockBreakerDataPlaneExtension.ImpactEvents);
```

The returned `ReadOnlySpan<T>` is still World-owned borrowed memory and must not
survive the next native publication. `SharedObjectHandleV4` remains the only
durable Unreal object identity allowed in generated rows; no `UObject*`, Unreal
container, allocator, or engine-private structure crosses CoreCLR.

## Adding another workload

Choose globally stable non-zero extension and schema IDs, declare explicit row
sizes and conservative per-channel limits, and use a stable `profileBoundary`
name. Regenerate, include the native descriptor in the producing module, and
include the managed output in the consuming assembly. Commit the schema,
catalog, both generated outputs, tests, and profiling evidence as one change.

Changing an existing row layout in place is not compatible. Introduce a new
schema ID (and normally a new row type/revision), keep the old consumer route
available for the supported migration window, then regenerate. The extension
contract adds workload families without changing ABI v4 table sizes or the
core layout compatibility ID.

## Acceptance evidence

Managed tests compare checked-in outputs byte-for-byte, reject schema drift and
capacity escape, and validate typed runtime compatibility. UE Automation tests
exercise registration, collision rejection, phase ownership, lifecycle reset,
native snapshot/event publication, and managed zero-copy acquisition. The
data-plane profile additionally reports the exact extension fingerprint and
1,024 rows for both channels, making the extension route visible outside the
test process.
