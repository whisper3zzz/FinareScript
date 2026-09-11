using System.Runtime.InteropServices;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed class UnrealSharedDataAbiV4Tests
{
    [Fact]
    public void LayoutMatchesNativeSharedDataContract()
    {
        Assert.Equal(16, Marshal.SizeOf<UnrealSharedObjectHandleV4>());
        Assert.Equal(
            UnrealSharedDataAbiV4.TransformEntrySize,
            Marshal.SizeOf<UnrealTransformSnapshotEntryV4>());
        Assert.Equal(
            UnrealSharedDataAbiV4.SnapshotViewSize,
            Marshal.SizeOf<UnrealSharedSnapshotViewV4>());
        Assert.Equal(
            UnrealSharedDataAbiV4.TransportTableSize,
            Marshal.SizeOf<UnrealSharedDataTransportTableV4>());
        Assert.Equal(
            UnrealSharedDataAbiV4.ExtendedTransportTableSize,
            Marshal.SizeOf<UnrealSharedDataTransportTableV4Extended>());
        Assert.Equal(
            UnrealSharedDataAbiV4.ChannelsExtendedTransportTableSize,
            Marshal.SizeOf<UnrealSharedDataTransportTableV4ChannelsExtended>());
        Assert.Equal(
            UnrealSharedDataAbiV4.GeneratedChannelViewSize,
            Marshal.SizeOf<UnrealGeneratedDataViewV1>());
        Assert.Equal(
            UnrealSharedDataAbiV4.FixedValueCollectionEntrySize,
            Marshal.SizeOf<UnrealFixedValueCollectionSnapshotV1>());
        Assert.Equal(
            UnrealSharedDataAbiV4.TelemetrySize,
            Marshal.SizeOf<UnrealSharedDataTelemetryV1>());
    }

    [Fact]
    public unsafe void ReadsNativeOwnedTransformRowsWithoutCopying()
    {
        using var transport = new FixtureSharedTransport(worldHostId: 37);
        var plane = new UnrealWorldDataPlane(transport, expectedWorldHostId: 37);

        UnrealTransformSnapshot snapshot = plane.AcquireTransformSnapshot();

        Assert.True(plane.IsAvailable);
        Assert.Equal(37UL, snapshot.WorldHostId);
        Assert.Equal(7UL, snapshot.Generation);
        Assert.Single(snapshot.Entries.ToArray());
        UnrealTransformSnapshotEntryV4 entry = snapshot.Entries[0];
        Assert.Equal(37UL, entry.Actor.WorldHostId);
        Assert.Equal(UnrealObjectHandle.Create(4, 9), entry.Actor.ObjectHandle);
        Assert.Equal(new UnrealVector(10.0, 20.0, 30.0), entry.Transform.Translation);
        Assert.True(snapshot.TryGetTransform(entry.Actor.ObjectHandle, out UnrealTransform found));
        Assert.Equal(entry.Transform, found);

        transport.SetTranslationX(73.0);
        Assert.Equal(
            73.0,
            snapshot.Entries[0].TranslationX);
    }

    [Fact]
    public void RejectsSnapshotFromAnotherWorldHost()
    {
        using var transport = new FixtureSharedTransport(worldHostId: 12);
        var plane = new UnrealWorldDataPlane(transport, expectedWorldHostId: 13);

        Assert.Throws<InvalidOperationException>(
            () => Consume(plane.AcquireTransformSnapshot()));

        static void Consume(UnrealTransformSnapshot snapshot) => _ = snapshot.Generation;
    }

    [Fact]
    public void SteadyStateSnapshotReadsAllocateNoManagedBytes()
    {
        using var transport = new FixtureSharedTransport(worldHostId: 91);
        var plane = new UnrealWorldDataPlane(transport, expectedWorldHostId: 91);
        double checksum = 0.0;
        for (int index = 0; index < 512; ++index)
        {
            checksum += plane.AcquireTransformSnapshot().Entries[0].TranslationX;
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; ++index)
        {
            checksum += plane.AcquireTransformSnapshot().Entries[0].TranslationX;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.NotEqual(0.0, checksum);
    }

    private sealed unsafe class FixtureSharedTransport : IUnrealSharedSnapshotTransport, IDisposable
    {
        private readonly nint _entry;
        private readonly ulong _worldHostId;

        public FixtureSharedTransport(ulong worldHostId)
        {
            _worldHostId = worldHostId;
            _entry = (nint)NativeMemory.AlignedAlloc(
                UnrealSharedDataAbiV4.TransformEntrySize,
                8);
            if (_entry == nint.Zero)
            {
                throw new OutOfMemoryException();
            }
            *(UnrealTransformSnapshotEntryV4*)_entry = new(
                new UnrealSharedObjectHandleV4(
                    worldHostId,
                    UnrealObjectHandle.Create(4, 9).Value),
                new UnrealTransform(
                    UnrealQuaternion.Identity,
                    new UnrealVector(10.0, 20.0, 30.0),
                    UnrealVector.One));
        }

        public bool SupportsSharedData => true;

        public bool TryAcquireTransformSnapshot(out UnrealSharedSnapshotViewV4 view)
        {
            view = new UnrealSharedSnapshotViewV4
            {
                AbiVersion = UnrealSharedDataAbiV4.Version,
                ViewSize = UnrealSharedDataAbiV4.SnapshotViewSize,
                SchemaId = UnrealSharedDataAbiV4.TransformSnapshotSchemaId,
                WorldHostId = _worldHostId,
                Generation = 7,
                Entries = _entry,
                EntryCount = 1,
                EntryCapacity = 1,
                EntrySize = UnrealSharedDataAbiV4.TransformEntrySize,
                Flags = UnrealSharedSnapshotFlagsV4.BorrowedUntilNextPublish,
            };
            return true;
        }

        public void SetTranslationX(double value) =>
            *(double*)((byte*)_entry + 16) = value;

        public void Dispose() => NativeMemory.AlignedFree((void*)_entry);
    }
}
