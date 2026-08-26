using System.Runtime.InteropServices;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealGameplayDataAbiV4Tests
{
    [Fact]
    public void LayoutMatchesNativeGameplayDataContract()
    {
        Assert.Equal(
            UnrealGameplayDataAbiV4.CommandEntrySize,
            Marshal.SizeOf<UnrealCommandV4>());
        Assert.Equal(
            UnrealGameplayDataAbiV4.CommandBatchViewSize,
            Marshal.SizeOf<UnrealCommandBatchViewV4>());
        Assert.Equal(
            UnrealGameplayDataAbiV4.EventEntrySize,
            Marshal.SizeOf<UnrealGameplayEventV4>());
        Assert.Equal(
            UnrealGameplayDataAbiV4.EventViewSize,
            Marshal.SizeOf<UnrealGameplayEventViewV4>());
        Assert.Equal(
            UnrealGameplayDataAbiV4.TraceRequestEntrySize,
            Marshal.SizeOf<UnrealTraceRequestV4>());
        Assert.Equal(
            UnrealGameplayDataAbiV4.TraceResultEntrySize,
            Marshal.SizeOf<UnrealTraceResultV4>());
        Assert.Equal(
            UnrealGameplayDataAbiV4.TraceRequestViewSize,
            Marshal.SizeOf<UnrealTraceRequestViewV4>());
        Assert.Equal(
            UnrealGameplayDataAbiV4.TraceResultViewSize,
            Marshal.SizeOf<UnrealTraceResultViewV4>());
        Assert.Equal(
            UnrealGameplayDataAbiV4.TransportTableSize,
            Marshal.SizeOf<UnrealGameplayDataTransportTableV4>());
    }

    [Fact]
    public void WritesCommandsAndReleasesUncommittedReservation()
    {
        using var transport = new FixtureGameplayTransport(worldHostId: 41);
        var plane = new UnrealWorldDataPlane(transport, expectedWorldHostId: 41);
        var actor = new UnrealSharedObjectHandleV4(
            41,
            UnrealObjectHandle.Create(3, 7).Value);

        using (UnrealCommandBatch batch = plane.BeginCommands(2))
        {
            batch.Commands[0] = UnrealCommandV4.SetActorVelocity(
                actor,
                new UnrealVector(10.0, 20.0, 30.0));
            batch.Commands[1] = UnrealCommandV4.DestroyActor(actor);
            batch.Commit();
        }

        Assert.True(plane.SupportsGameplayData);
        Assert.Equal(UnrealGameplayDataPhaseV4.ManagedTick, plane.GameplayPhase);
        Assert.Equal(9UL, plane.GameplayFrameNumber);
        Assert.Equal(2U, transport.LastCommittedCount);
        Assert.Equal(UnrealCommandTypeV4.SetActorVelocity, transport.Commands[0].Type);
        Assert.Equal(30.0, transport.Commands[0].Vector0Z);
        Assert.Equal(UnrealCommandTypeV4.DestroyActor, transport.Commands[1].Type);

        using (UnrealCommandBatch ignored = plane.BeginCommands(1))
        {
        }
        Assert.Equal(0U, transport.LastCommittedCount);
    }

    [Fact]
    public void ReadsEventsAndExecutesThreeShapeTraceBatch()
    {
        using var transport = new FixtureGameplayTransport(worldHostId: 73);
        var plane = new UnrealWorldDataPlane(transport, expectedWorldHostId: 73);

        UnrealGameplayEventSnapshot events = plane.AcquireEvents();
        Assert.Empty(events.Events.ToArray());
        Assert.Equal(5UL, events.DroppedEventCount);

        using UnrealTraceBatch batch = plane.BeginTraceBatch(3);
        batch.Requests[0] = UnrealTraceRequestV4.Line(
            UnrealVector.Zero,
            new UnrealVector(100.0, 0.0, 0.0),
            collisionChannel: 3);
        batch.Requests[1] = UnrealTraceRequestV4.Sphere(
            UnrealVector.Zero,
            new UnrealVector(100.0, 0.0, 0.0),
            radius: 25.0,
            collisionChannel: 3);
        batch.Requests[2] = UnrealTraceRequestV4.Box(
            UnrealVector.Zero,
            new UnrealVector(100.0, 0.0, 0.0),
            new UnrealVector(10.0, 20.0, 30.0),
            UnrealQuaternion.Identity,
            collisionChannel: 3);

        ReadOnlySpan<UnrealTraceResultV4> results = batch.Execute();

        Assert.Equal(3, results.Length);
        Assert.Equal(3U, transport.LastTraceCount);
        Assert.Equal(UnrealTraceShapeV4.Line, transport.Requests[0].Shape);
        Assert.Equal(UnrealTraceShapeV4.Sphere, transport.Requests[1].Shape);
        Assert.Equal(25.0, transport.Requests[1].ExtentX);
        Assert.Equal(UnrealTraceShapeV4.Box, transport.Requests[2].Shape);
        Assert.Equal(30.0, transport.Requests[2].ExtentZ);
    }

    private sealed unsafe class FixtureGameplayTransport :
        IUnrealSharedSnapshotTransport,
        IUnrealGameplayDataTransport,
        IDisposable
    {
        private readonly ulong _worldHostId;
        private readonly nint _commands;
        private readonly nint _requests;
        private readonly nint _results;
        private ulong _nextToken = 1;

        public FixtureGameplayTransport(ulong worldHostId)
        {
            _worldHostId = worldHostId;
            _commands = Allocate<UnrealCommandV4>(4);
            _requests = Allocate<UnrealTraceRequestV4>(4);
            _results = Allocate<UnrealTraceResultV4>(4);
        }

        public bool SupportsSharedData => false;
        public bool SupportsGameplayData => true;
        public uint LastCommittedCount { get; private set; }
        public uint LastTraceCount { get; private set; }
        public Span<UnrealCommandV4> Commands => new((void*)_commands, 4);
        public Span<UnrealTraceRequestV4> Requests => new((void*)_requests, 4);

        public bool TryAcquireTransformSnapshot(out UnrealSharedSnapshotViewV4 view)
        {
            view = default;
            return false;
        }

        public bool BeginCommandBatch(uint capacity, out UnrealCommandBatchViewV4 view)
        {
            ulong token = _nextToken++;
            view = new UnrealCommandBatchViewV4
            {
                AbiVersion = UnrealGameplayDataAbiV4.Version,
                ViewSize = UnrealGameplayDataAbiV4.CommandBatchViewSize,
                SchemaId = UnrealGameplayDataAbiV4.CommandSchemaId,
                WorldHostId = _worldHostId,
                FrameNumber = 9,
                BatchToken = token,
                FirstSequenceId = token * 10,
                Commands = _commands,
                CommandCapacity = capacity,
                CommandEntrySize = UnrealGameplayDataAbiV4.CommandEntrySize,
            };
            return capacity <= 4;
        }

        public bool CommitCommandBatch(ulong token, uint count)
        {
            LastCommittedCount = count;
            return token != 0 && count <= 4;
        }

        public bool AcquireEvents(out UnrealGameplayEventViewV4 view)
        {
            view = new UnrealGameplayEventViewV4
            {
                AbiVersion = UnrealGameplayDataAbiV4.Version,
                ViewSize = UnrealGameplayDataAbiV4.EventViewSize,
                SchemaId = UnrealGameplayDataAbiV4.EventSchemaId,
                WorldHostId = _worldHostId,
                FrameNumber = 9,
                Events = nint.Zero,
                EventCount = 0,
                EventCapacity = 0,
                EventEntrySize = UnrealGameplayDataAbiV4.EventEntrySize,
                DroppedEventCount = 5,
            };
            return true;
        }

        public bool BeginTraceBatch(uint capacity, out UnrealTraceRequestViewV4 view)
        {
            ulong token = _nextToken++;
            view = new UnrealTraceRequestViewV4
            {
                AbiVersion = UnrealGameplayDataAbiV4.Version,
                ViewSize = UnrealGameplayDataAbiV4.TraceRequestViewSize,
                SchemaId = UnrealGameplayDataAbiV4.TraceRequestSchemaId,
                WorldHostId = _worldHostId,
                FrameNumber = 9,
                BatchToken = token,
                Requests = _requests,
                RequestCapacity = capacity,
                RequestEntrySize = UnrealGameplayDataAbiV4.TraceRequestEntrySize,
            };
            return capacity <= 4;
        }

        public bool ExecuteTraceBatch(
            ulong token,
            uint count,
            out UnrealTraceResultViewV4 view)
        {
            LastTraceCount = count;
            view = new UnrealTraceResultViewV4
            {
                AbiVersion = UnrealGameplayDataAbiV4.Version,
                ViewSize = UnrealGameplayDataAbiV4.TraceResultViewSize,
                SchemaId = UnrealGameplayDataAbiV4.TraceResultSchemaId,
                WorldHostId = _worldHostId,
                FrameNumber = 9,
                BatchToken = token,
                Results = _results,
                ResultCount = count,
                ResultCapacity = count,
                ResultEntrySize = UnrealGameplayDataAbiV4.TraceResultEntrySize,
            };
            return token != 0 && count <= 4;
        }

        public void CancelTraceBatch(ulong token)
        {
        }

        public bool GetPhase(
            out UnrealGameplayDataPhaseV4 phase,
            out ulong frameNumber)
        {
            phase = UnrealGameplayDataPhaseV4.ManagedTick;
            frameNumber = 9;
            return true;
        }

        public void Dispose()
        {
            NativeMemory.AlignedFree((void*)_commands);
            NativeMemory.AlignedFree((void*)_requests);
            NativeMemory.AlignedFree((void*)_results);
        }

        private static nint Allocate<T>(nuint count) where T : unmanaged
        {
            void* memory = NativeMemory.AlignedAlloc(
                checked((nuint)sizeof(T) * count),
                8);
            if (memory == null)
            {
                throw new OutOfMemoryException();
            }
            NativeMemory.Clear(memory, checked((nuint)sizeof(T) * count));
            return (nint)memory;
        }
    }
}
