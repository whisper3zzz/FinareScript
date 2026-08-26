using System.Runtime.InteropServices;

namespace UnifiedScript.Unreal;

public enum UnrealGameplayDataPhaseV4 : byte
{
    Idle = 0,
    NativePreActorTick = 1,
    ManagedTick = 2,
    NativePostActorTick = 3,
    ShuttingDown = 4,
}

public enum UnrealCommandTypeV4 : ushort
{
    None = 0,
    SetActorTransform = 1,
    SetActorVelocity = 2,
    ApplyDamage = 3,
    SpawnActor = 4,
    DestroyActor = 5,
    SpawnEmitter = 6,
}

public enum UnrealCommandStatusV4 : ushort
{
    Success = 0,
    InvalidCommand = 1,
    WorldMismatch = 2,
    StaleHandle = 3,
    TypeMismatch = 4,
    ExecutionFailed = 5,
    BufferOverflow = 6,
    WrongPhase = 7,
}

public enum UnrealGameplayEventKindV4 : ushort
{
    None = 0,
    CommandCompleted = 1,
    ActorSpawned = 2,
    ActorDestroyed = 3,
    DamageApplied = 4,
    EffectSpawned = 5,
}

public enum UnrealTraceShapeV4 : byte
{
    Line = 0,
    Sphere = 1,
    Box = 2,
}

public enum UnrealTraceStatusV4 : byte
{
    Success = 0,
    InvalidRequest = 1,
    WorldMismatch = 2,
    StaleHandle = 3,
    ExecutionFailed = 4,
}

[Flags]
public enum UnrealTraceResultFlagsV4 : byte
{
    None = 0,
    BlockingHit = 1 << 0,
    StartPenetrating = 1 << 1,
}

[Flags]
public enum UnrealGameplayDataCapabilitiesV4 : ulong
{
    None = 0,
    WorldPhaseScheduler = 1UL << 0,
    CommandBuffer = 1UL << 1,
    EventBuffer = 1UL << 2,
    BatchLineTrace = 1UL << 3,
    BatchSphereTrace = 1UL << 4,
    BatchBoxTrace = 1UL << 5,
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealCommandV4
{
    public UnrealCommandTypeV4 Type;
    public ushort Flags;
    public uint Reserved;
    public ulong SequenceId;
    public UnrealSharedObjectHandleV4 Target;
    public UnrealSharedObjectHandleV4 Argument0;
    public UnrealSharedObjectHandleV4 Argument1;
    public UnrealSharedObjectHandleV4 Argument2;
    public double Scalar0;
    public double Scalar1;
    public double Scalar2;
    public double Scalar3;
    public double Vector0X;
    public double Vector0Y;
    public double Vector0Z;
    public double Vector1X;
    public double Vector1Y;
    public double Vector1Z;
    public double RotationX;
    public double RotationY;
    public double RotationZ;
    public double RotationW;
    public double Vector2X;
    public double Vector2Y;
    public double Vector2Z;
    public ulong Payload0;

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

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct UnrealGameplayEventV4
{
    public readonly ulong SequenceId;
    public readonly UnrealGameplayEventKindV4 Kind;
    public readonly UnrealCommandTypeV4 CommandType;
    public readonly UnrealCommandStatusV4 Status;
    public readonly ushort Flags;
    public readonly UnrealSharedObjectHandleV4 Source;
    public readonly UnrealSharedObjectHandleV4 Argument;
    public readonly double Value0;
    public readonly double VectorX;
    public readonly double VectorY;
    public readonly double VectorZ;
    public readonly ulong FrameNumber;

    public bool IsSuccess => Status == UnrealCommandStatusV4.Success;

    public UnrealVector Vector => new(VectorX, VectorY, VectorZ);
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealTraceRequestV4
{
    public ulong SequenceId;
    public UnrealTraceShapeV4 Shape;
    public byte IgnoreActorCount;
    public ushort Flags;
    public uint CollisionChannel;
    public UnrealSharedObjectHandleV4 IgnoreActor0;
    public UnrealSharedObjectHandleV4 IgnoreActor1;
    public UnrealSharedObjectHandleV4 IgnoreActor2;
    public UnrealSharedObjectHandleV4 IgnoreActor3;
    public double StartX;
    public double StartY;
    public double StartZ;
    public double EndX;
    public double EndY;
    public double EndZ;
    public double ExtentX;
    public double ExtentY;
    public double ExtentZ;
    public double RotationX;
    public double RotationY;
    public double RotationZ;
    public double RotationW;

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

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct UnrealTraceResultV4
{
    public readonly ulong SequenceId;
    public readonly UnrealTraceStatusV4 Status;
    public readonly UnrealTraceResultFlagsV4 Flags;
    public readonly ushort Reserved;
    public readonly int FaceIndex;
    public readonly UnrealSharedObjectHandleV4 HitActor;
    public readonly UnrealSharedObjectHandleV4 HitComponent;
    public readonly double Distance;
    public readonly double Time;
    public readonly double LocationX;
    public readonly double LocationY;
    public readonly double LocationZ;
    public readonly double ImpactPointX;
    public readonly double ImpactPointY;
    public readonly double ImpactPointZ;
    public readonly double NormalX;
    public readonly double NormalY;
    public readonly double NormalZ;
    public readonly double ImpactNormalX;
    public readonly double ImpactNormalY;
    public readonly double ImpactNormalZ;
    public readonly int Item;
    public readonly int ElementIndex;

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

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct UnrealCommandBatchViewV4
{
    public uint AbiVersion;
    public uint ViewSize;
    public ulong SchemaId;
    public ulong WorldHostId;
    public ulong FrameNumber;
    public ulong BatchToken;
    public ulong FirstSequenceId;
    public nint Commands;
    public uint CommandCapacity;
    public uint CommandEntrySize;
    public uint Flags;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct UnrealGameplayEventViewV4
{
    public uint AbiVersion;
    public uint ViewSize;
    public ulong SchemaId;
    public ulong WorldHostId;
    public ulong FrameNumber;
    public nint Events;
    public uint EventCount;
    public uint EventCapacity;
    public uint EventEntrySize;
    public uint Flags;
    public ulong DroppedEventCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct UnrealTraceRequestViewV4
{
    public uint AbiVersion;
    public uint ViewSize;
    public ulong SchemaId;
    public ulong WorldHostId;
    public ulong FrameNumber;
    public ulong BatchToken;
    public nint Requests;
    public uint RequestCapacity;
    public uint RequestEntrySize;
    public uint Flags;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct UnrealTraceResultViewV4
{
    public uint AbiVersion;
    public uint ViewSize;
    public ulong SchemaId;
    public ulong WorldHostId;
    public ulong FrameNumber;
    public ulong BatchToken;
    public nint Results;
    public uint ResultCount;
    public uint ResultCapacity;
    public uint ResultEntrySize;
    public uint Flags;
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

public static class UnrealGameplayDataAbiV4
{
    public const uint Version = 4;
    public const ulong CommandSchemaId = 0x5553434D44423401UL;
    public const ulong EventSchemaId = 0x555345564E543401UL;
    public const ulong TraceRequestSchemaId = 0x5553545245513401UL;
    public const ulong TraceResultSchemaId = 0x5553545245533401UL;
    public const int CommandEntrySize = 224;
    public const int CommandBatchViewSize = 72;
    public const int EventEntrySize = 88;
    public const int EventViewSize = 64;
    public const int TraceRequestEntrySize = 184;
    public const int TraceResultEntrySize = 168;
    public const int TraceRequestViewSize = 64;
    public const int TraceResultViewSize = 64;
    public const int TransportTableSize = 104;
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
