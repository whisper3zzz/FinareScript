using System.Runtime.InteropServices;

namespace UnifiedScript.Unreal;

public partial struct UnrealCommandV4
{
    public static UnrealCommandV4 SetActorTransform(
        UnrealSharedObjectHandleV4 target,
        UnrealTransform transform,
        bool sweep = false,
        bool teleportPhysics = false)
    {
        UnrealCommandV4 command = FromTransform(
            UnrealCommandTypeV4.SetActorTransform,
            transform);
        command.Target = target;
        command.Flags = (ushort)((sweep ? 1 : 0) | (teleportPhysics ? 2 : 0));
        return command;
    }

    public static UnrealCommandV4 SetActorVelocity(
        UnrealSharedObjectHandleV4 target,
        UnrealVector velocity) => new()
    {
        Type = UnrealCommandTypeV4.SetActorVelocity,
        Target = target,
        Vector0X = velocity.X,
        Vector0Y = velocity.Y,
        Vector0Z = velocity.Z,
        RotationW = 1.0,
        Vector2X = 1.0,
        Vector2Y = 1.0,
        Vector2Z = 1.0,
    };

    public static UnrealCommandV4 ApplyDamage(
        UnrealSharedObjectHandleV4 target,
        double damage,
        UnrealSharedObjectHandleV4 eventInstigator = default,
        UnrealSharedObjectHandleV4 damageCauser = default,
        UnrealSharedObjectHandleV4 damageTypeClass = default) => new()
    {
        Type = UnrealCommandTypeV4.ApplyDamage,
        Target = target,
        Argument0 = eventInstigator,
        Argument1 = damageCauser,
        Argument2 = damageTypeClass,
        Scalar0 = damage,
        RotationW = 1.0,
        Vector2X = 1.0,
        Vector2Y = 1.0,
        Vector2Z = 1.0,
    };

    public static UnrealCommandV4 SpawnActor(
        UnrealSharedObjectHandleV4 actorClass,
        UnrealTransform transform)
    {
        UnrealCommandV4 command = FromTransform(UnrealCommandTypeV4.SpawnActor, transform);
        command.Argument0 = actorClass;
        return command;
    }

    public static UnrealCommandV4 DestroyActor(UnrealSharedObjectHandleV4 target) => new()
    {
        Type = UnrealCommandTypeV4.DestroyActor,
        Target = target,
        RotationW = 1.0,
        Vector2X = 1.0,
        Vector2Y = 1.0,
        Vector2Z = 1.0,
    };

    public static UnrealCommandV4 SpawnEmitter(
        UnrealSharedObjectHandleV4 particleSystem,
        UnrealTransform transform)
    {
        UnrealCommandV4 command = FromTransform(UnrealCommandTypeV4.SpawnEmitter, transform);
        command.Argument0 = particleSystem;
        return command;
    }

    private static UnrealCommandV4 FromTransform(
        UnrealCommandTypeV4 type,
        UnrealTransform transform) => new()
    {
        Type = type,
        Vector0X = transform.Translation.X,
        Vector0Y = transform.Translation.Y,
        Vector0Z = transform.Translation.Z,
        RotationX = transform.Rotation.X,
        RotationY = transform.Rotation.Y,
        RotationZ = transform.Rotation.Z,
        RotationW = transform.Rotation.W,
        Vector2X = transform.Scale3D.X,
        Vector2Y = transform.Scale3D.Y,
        Vector2Z = transform.Scale3D.Z,
    };
}

public readonly partial struct UnrealGameplayEventV4
{
    public bool IsSuccess => Status == UnrealCommandStatusV4.Success;

    public UnrealVector Vector => new(VectorX, VectorY, VectorZ);
}

public partial struct UnrealTraceRequestV4
{
    public static UnrealTraceRequestV4 Line(
        UnrealVector start,
        UnrealVector end,
        uint collisionChannel,
        ReadOnlySpan<UnrealSharedObjectHandleV4> ignoreActors = default) =>
        Create(
            UnrealTraceShapeV4.Line,
            start,
            end,
            UnrealVector.Zero,
            UnrealQuaternion.Identity,
            collisionChannel,
            ignoreActors);

    public static UnrealTraceRequestV4 Sphere(
        UnrealVector start,
        UnrealVector end,
        double radius,
        uint collisionChannel,
        ReadOnlySpan<UnrealSharedObjectHandleV4> ignoreActors = default) =>
        Create(
            UnrealTraceShapeV4.Sphere,
            start,
            end,
            new UnrealVector(radius, 0.0, 0.0),
            UnrealQuaternion.Identity,
            collisionChannel,
            ignoreActors);

    public static UnrealTraceRequestV4 Box(
        UnrealVector start,
        UnrealVector end,
        UnrealVector halfExtent,
        UnrealQuaternion rotation,
        uint collisionChannel,
        ReadOnlySpan<UnrealSharedObjectHandleV4> ignoreActors = default) =>
        Create(
            UnrealTraceShapeV4.Box,
            start,
            end,
            halfExtent,
            rotation,
            collisionChannel,
            ignoreActors);

    private static UnrealTraceRequestV4 Create(
        UnrealTraceShapeV4 shape,
        UnrealVector start,
        UnrealVector end,
        UnrealVector extent,
        UnrealQuaternion rotation,
        uint collisionChannel,
        ReadOnlySpan<UnrealSharedObjectHandleV4> ignoreActors)
    {
        if (ignoreActors.Length > 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ignoreActors),
                "A batch trace request supports at most four ignored Actors.");
        }
        var request = new UnrealTraceRequestV4
        {
            Shape = shape,
            IgnoreActorCount = checked((byte)ignoreActors.Length),
            CollisionChannel = collisionChannel,
            StartX = start.X,
            StartY = start.Y,
            StartZ = start.Z,
            EndX = end.X,
            EndY = end.Y,
            EndZ = end.Z,
            ExtentX = extent.X,
            ExtentY = extent.Y,
            ExtentZ = extent.Z,
            RotationX = rotation.X,
            RotationY = rotation.Y,
            RotationZ = rotation.Z,
            RotationW = rotation.W,
        };
        if (ignoreActors.Length > 0) request.IgnoreActor0 = ignoreActors[0];
        if (ignoreActors.Length > 1) request.IgnoreActor1 = ignoreActors[1];
        if (ignoreActors.Length > 2) request.IgnoreActor2 = ignoreActors[2];
        if (ignoreActors.Length > 3) request.IgnoreActor3 = ignoreActors[3];
        return request;
    }
}

public readonly partial struct UnrealTraceResultV4
{
    public bool IsSuccess => Status == UnrealTraceStatusV4.Success;
    public bool BlockingHit => (Flags & UnrealTraceResultFlagsV4.BlockingHit) != 0;
    public bool StartPenetrating =>
        (Flags & UnrealTraceResultFlagsV4.StartPenetrating) != 0;
    public UnrealVector Location => new(LocationX, LocationY, LocationZ);
    public UnrealVector ImpactPoint => new(ImpactPointX, ImpactPointY, ImpactPointZ);
    public UnrealVector Normal => new(NormalX, NormalY, NormalZ);
    public UnrealVector ImpactNormal =>
        new(ImpactNormalX, ImpactNormalY, ImpactNormalZ);
}

public partial struct UnrealOverlapRequestV1
{
    public static UnrealOverlapRequestV1 Sphere(
        UnrealVector center,
        double radius,
        uint collisionChannel,
        ReadOnlySpan<UnrealSharedObjectHandleV4> ignoreActors = default) =>
        Create(
            UnrealTraceShapeV4.Sphere,
            center,
            new UnrealVector(radius, 0.0, 0.0),
            UnrealQuaternion.Identity,
            collisionChannel,
            ignoreActors);

    public static UnrealOverlapRequestV1 Box(
        UnrealVector center,
        UnrealVector halfExtent,
        UnrealQuaternion rotation,
        uint collisionChannel,
        ReadOnlySpan<UnrealSharedObjectHandleV4> ignoreActors = default) =>
        Create(
            UnrealTraceShapeV4.Box,
            center,
            halfExtent,
            rotation,
            collisionChannel,
            ignoreActors);

    private static UnrealOverlapRequestV1 Create(
        UnrealTraceShapeV4 shape,
        UnrealVector center,
        UnrealVector extent,
        UnrealQuaternion rotation,
        uint collisionChannel,
        ReadOnlySpan<UnrealSharedObjectHandleV4> ignoreActors)
    {
        if (ignoreActors.Length > 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ignoreActors),
                "A batch overlap request supports at most four ignored Actors.");
        }
        var request = new UnrealOverlapRequestV1
        {
            Shape = shape,
            IgnoreActorCount = checked((byte)ignoreActors.Length),
            CollisionChannel = collisionChannel,
            CenterX = center.X,
            CenterY = center.Y,
            CenterZ = center.Z,
            ExtentX = extent.X,
            ExtentY = extent.Y,
            ExtentZ = extent.Z,
            RotationX = rotation.X,
            RotationY = rotation.Y,
            RotationZ = rotation.Z,
            RotationW = rotation.W,
        };
        if (ignoreActors.Length > 0) request.IgnoreActor0 = ignoreActors[0];
        if (ignoreActors.Length > 1) request.IgnoreActor1 = ignoreActors[1];
        if (ignoreActors.Length > 2) request.IgnoreActor2 = ignoreActors[2];
        if (ignoreActors.Length > 3) request.IgnoreActor3 = ignoreActors[3];
        return request;
    }
}

public readonly partial struct UnrealOverlapResultV1
{
    public bool IsSuccess => Status == UnrealTraceStatusV4.Success;

    public bool HasOverlap =>
        (Flags & UnrealOverlapResultFlagsV1.HasOverlap) != 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct UnrealGameplayDataTransportTableV4
{
    public uint AbiVersion;
    public uint TableSize;
    public uint CommandEntrySize;
    public uint EventEntrySize;
    public uint TraceRequestEntrySize;
    public uint TraceResultEntrySize;
    public UnrealGameplayDataCapabilitiesV4 Capabilities;
    public nint RetainContext;
    public nint ReleaseContext;
    public nint BeginCommandBatch;
    public nint CommitCommandBatch;
    public nint AcquireEvents;
    public nint BeginTraceBatch;
    public nint ExecuteTraceBatch;
    public nint CancelTraceBatch;
    public nint GetPhase;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct UnrealGameplayDataTransportTableV4Extended
{
    public uint AbiVersion;
    public uint TableSize;
    public uint CommandEntrySize;
    public uint EventEntrySize;
    public uint TraceRequestEntrySize;
    public uint TraceResultEntrySize;
    public UnrealGameplayDataCapabilitiesV4 Capabilities;
    public nint RetainContext;
    public nint ReleaseContext;
    public nint BeginCommandBatch;
    public nint CommitCommandBatch;
    public nint AcquireEvents;
    public nint BeginTraceBatch;
    public nint ExecuteTraceBatch;
    public nint CancelTraceBatch;
    public nint GetPhase;
    public ulong LayoutCompatibilityId;
    public uint LayoutRevision;
    public uint MaximumCommandCount;
    public uint MaximumEventCount;
    public uint MaximumTraceCount;
    public nint GetTelemetry;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct UnrealGameplayDataTransportTableV4OverlapExtended
{
    public uint AbiVersion;
    public uint TableSize;
    public uint CommandEntrySize;
    public uint EventEntrySize;
    public uint TraceRequestEntrySize;
    public uint TraceResultEntrySize;
    public UnrealGameplayDataCapabilitiesV4 Capabilities;
    public nint RetainContext;
    public nint ReleaseContext;
    public nint BeginCommandBatch;
    public nint CommitCommandBatch;
    public nint AcquireEvents;
    public nint BeginTraceBatch;
    public nint ExecuteTraceBatch;
    public nint CancelTraceBatch;
    public nint GetPhase;
    public ulong LayoutCompatibilityId;
    public uint LayoutRevision;
    public uint MaximumCommandCount;
    public uint MaximumEventCount;
    public uint MaximumTraceCount;
    public nint GetTelemetry;
    public uint OverlapRequestEntrySize;
    public uint OverlapResultEntrySize;
    public uint MaximumOverlapCount;
    public uint Reserved;
    public nint BeginOverlapBatch;
    public nint ExecuteOverlapBatch;
    public nint CancelOverlapBatch;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct UnrealGameplayDataTransportTableV4ChannelsExtended
{
    public uint AbiVersion;
    public uint TableSize;
    public uint CommandEntrySize;
    public uint EventEntrySize;
    public uint TraceRequestEntrySize;
    public uint TraceResultEntrySize;
    public UnrealGameplayDataCapabilitiesV4 Capabilities;
    public nint RetainContext;
    public nint ReleaseContext;
    public nint BeginCommandBatch;
    public nint CommitCommandBatch;
    public nint AcquireEvents;
    public nint BeginTraceBatch;
    public nint ExecuteTraceBatch;
    public nint CancelTraceBatch;
    public nint GetPhase;
    public ulong LayoutCompatibilityId;
    public uint LayoutRevision;
    public uint MaximumCommandCount;
    public uint MaximumEventCount;
    public uint MaximumTraceCount;
    public nint GetTelemetry;
    public uint OverlapRequestEntrySize;
    public uint OverlapResultEntrySize;
    public uint MaximumOverlapCount;
    public uint Reserved;
    public nint BeginOverlapBatch;
    public nint ExecuteOverlapBatch;
    public nint CancelOverlapBatch;
    public uint GeneratedChannelViewSize;
    public uint MaximumGeneratedEventCount;
    public ulong MaximumGeneratedChannelBytes;
    public nint AcquireGeneratedEvents;
}

public static partial class UnrealGameplayDataAbiV4
{
    public const int TransportTableSize = 104;
    public const int ExtendedTransportTableSize = 136;
    public const int OverlapTransportTableSize = 176;
    public const int ChannelsExtendedTransportTableSize = 200;
}

internal interface IUnrealGameplayDataTransport
{
    bool SupportsGameplayData { get; }
    bool BeginCommandBatch(uint capacity, out UnrealCommandBatchViewV4 view);
    bool CommitCommandBatch(ulong token, uint count);
    bool AcquireEvents(out UnrealGameplayEventViewV4 view);
    bool BeginTraceBatch(uint capacity, out UnrealTraceRequestViewV4 view);
    bool ExecuteTraceBatch(
        ulong token,
        uint count,
        out UnrealTraceResultViewV4 view);
    void CancelTraceBatch(ulong token);
    bool GetPhase(out UnrealGameplayDataPhaseV4 phase, out ulong frameNumber);
}

internal interface IUnrealOverlapDataTransport
{
    bool SupportsOverlapData { get; }
    bool BeginOverlapBatch(uint capacity, out UnrealOverlapRequestViewV1 view);
    bool ExecuteOverlapBatch(
        ulong token,
        uint count,
        out UnrealOverlapResultViewV1 view);
    void CancelOverlapBatch(ulong token);
}

public ref struct UnrealCommandBatch
{
    private readonly IUnrealGameplayDataTransport _transport;
    private readonly ulong _token;
    private bool _committed;

    internal unsafe UnrealCommandBatch(
        IUnrealGameplayDataTransport transport,
        UnrealCommandBatchViewV4 view)
    {
        _transport = transport;
        _token = view.BatchToken;
        WorldHostId = view.WorldHostId;
        FrameNumber = view.FrameNumber;
        FirstSequenceId = view.FirstSequenceId;
        Commands = new Span<UnrealCommandV4>(
            (void*)view.Commands,
            checked((int)view.CommandCapacity));
        _committed = false;
    }

    public ulong WorldHostId { get; }
    public ulong FrameNumber { get; }
    public ulong FirstSequenceId { get; }
    public Span<UnrealCommandV4> Commands { get; }

    public void Commit(int commandCount = -1)
    {
        if (_committed)
        {
            throw new InvalidOperationException("The command batch is already committed.");
        }
        int count = commandCount < 0 ? Commands.Length : commandCount;
        if ((uint)count > (uint)Commands.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(commandCount));
        }
        if (!_transport.CommitCommandBatch(_token, checked((uint)count)))
        {
            throw new InvalidOperationException(
                "The native World rejected the command batch commit.");
        }
        _committed = true;
    }

    public void Dispose()
    {
        if (!_committed)
        {
            _transport.CommitCommandBatch(_token, 0);
            _committed = true;
        }
    }
}

public readonly ref struct UnrealGameplayEventSnapshot
{
    private readonly ReadOnlySpan<UnrealGameplayEventV4> _events;

    internal UnrealGameplayEventSnapshot(
        ulong worldHostId,
        ulong frameNumber,
        ulong droppedEventCount,
        ReadOnlySpan<UnrealGameplayEventV4> events)
    {
        WorldHostId = worldHostId;
        FrameNumber = frameNumber;
        DroppedEventCount = droppedEventCount;
        _events = events;
    }

    public ulong WorldHostId { get; }
    public ulong FrameNumber { get; }
    public ulong DroppedEventCount { get; }
    public ReadOnlySpan<UnrealGameplayEventV4> Events => _events;
}

public ref struct UnrealTraceBatch
{
    private readonly IUnrealGameplayDataTransport _transport;
    private readonly ulong _token;
    private bool _finished;

    internal unsafe UnrealTraceBatch(
        IUnrealGameplayDataTransport transport,
        UnrealTraceRequestViewV4 view)
    {
        _transport = transport;
        _token = view.BatchToken;
        WorldHostId = view.WorldHostId;
        FrameNumber = view.FrameNumber;
        Requests = new Span<UnrealTraceRequestV4>(
            (void*)view.Requests,
            checked((int)view.RequestCapacity));
        _finished = false;
    }

    public ulong WorldHostId { get; }
    public ulong FrameNumber { get; }
    public Span<UnrealTraceRequestV4> Requests { get; }

    public unsafe ReadOnlySpan<UnrealTraceResultV4> Execute(int requestCount = -1)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The trace batch is already completed.");
        }
        int count = requestCount < 0 ? Requests.Length : requestCount;
        if ((uint)count > (uint)Requests.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(requestCount));
        }
        if (!_transport.ExecuteTraceBatch(
                _token,
                checked((uint)count),
                out UnrealTraceResultViewV4 view))
        {
            throw new InvalidOperationException(
                "The native World rejected the trace batch execution.");
        }
        UnrealWorldDataPlane.ValidateTraceResultView(view, WorldHostId, _token);
        _finished = true;
        return new ReadOnlySpan<UnrealTraceResultV4>(
            (void*)view.Results,
            checked((int)view.ResultCount));
    }

    public void Dispose()
    {
        if (!_finished)
        {
            _transport.CancelTraceBatch(_token);
            _finished = true;
        }
    }
}

public ref struct UnrealOverlapBatch
{
    private readonly IUnrealOverlapDataTransport _transport;
    private readonly ulong _token;
    private bool _finished;

    internal unsafe UnrealOverlapBatch(
        IUnrealOverlapDataTransport transport,
        UnrealOverlapRequestViewV1 view)
    {
        _transport = transport;
        _token = view.BatchToken;
        WorldHostId = view.WorldHostId;
        FrameNumber = view.FrameNumber;
        Requests = new Span<UnrealOverlapRequestV1>(
            (void*)view.Requests,
            checked((int)view.RequestCapacity));
        _finished = false;
    }

    public ulong WorldHostId { get; }
    public ulong FrameNumber { get; }
    public Span<UnrealOverlapRequestV1> Requests { get; }

    public unsafe ReadOnlySpan<UnrealOverlapResultV1> Execute(int requestCount = -1)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The overlap batch is already completed.");
        }
        int count = requestCount < 0 ? Requests.Length : requestCount;
        if ((uint)count > (uint)Requests.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(requestCount));
        }
        if (!_transport.ExecuteOverlapBatch(
                _token,
                checked((uint)count),
                out UnrealOverlapResultViewV1 view))
        {
            throw new InvalidOperationException(
                "The native World rejected the overlap batch execution.");
        }
        UnrealWorldDataPlane.ValidateOverlapResultView(view, WorldHostId, _token);
        _finished = true;
        return new ReadOnlySpan<UnrealOverlapResultV1>(
            (void*)view.Results,
            checked((int)view.ResultCount));
    }

    public void Dispose()
    {
        if (!_finished)
        {
            _transport.CancelOverlapBatch(_token);
            _finished = true;
        }
    }
}
