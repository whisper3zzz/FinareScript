using System.Runtime.InteropServices;

namespace UnifiedScript.Unreal;

[Flags]
public enum UnrealSharedDataCapabilitiesV4 : ulong
{
    None = 0,
    TransformSnapshot = 1UL << 0,
    DoubleBuffered = 1UL << 1,
    WorldScopedHandles = 1UL << 2,
}

[Flags]
public enum UnrealSharedSnapshotFlagsV4 : uint
{
    None = 0,
    BorrowedUntilNextPublish = 1U << 0,
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly record struct UnrealSharedObjectHandleV4(
    ulong WorldHostId,
    ulong ObjectHandleValue)
{
    public UnrealObjectHandle ObjectHandle => new(ObjectHandleValue);

    public bool IsWellFormed =>
        WorldHostId != 0 && ObjectHandle.IsWellFormed;
}

/// <summary>
/// Fixed 96-byte Transform row shared directly from native World memory.
/// It contains no UObject pointers or engine container layouts.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct UnrealTransformSnapshotEntryV4
{
    public UnrealTransformSnapshotEntryV4(
        UnrealSharedObjectHandleV4 actor,
        UnrealTransform transform)
    {
        Actor = actor;
        TranslationX = transform.Translation.X;
        TranslationY = transform.Translation.Y;
        TranslationZ = transform.Translation.Z;
        RotationX = transform.Rotation.X;
        RotationY = transform.Rotation.Y;
        RotationZ = transform.Rotation.Z;
        RotationW = transform.Rotation.W;
        ScaleX = transform.Scale3D.X;
        ScaleY = transform.Scale3D.Y;
        ScaleZ = transform.Scale3D.Z;
    }

    public readonly UnrealSharedObjectHandleV4 Actor;
    public readonly double TranslationX;
    public readonly double TranslationY;
    public readonly double TranslationZ;
    public readonly double RotationX;
    public readonly double RotationY;
    public readonly double RotationZ;
    public readonly double RotationW;
    public readonly double ScaleX;
    public readonly double ScaleY;
    public readonly double ScaleZ;

    public UnrealTransform Transform => new(
        new UnrealQuaternion(RotationX, RotationY, RotationZ, RotationW),
        new UnrealVector(TranslationX, TranslationY, TranslationZ),
        new UnrealVector(ScaleX, ScaleY, ScaleZ));
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct UnrealSharedSnapshotViewV4
{
    public uint AbiVersion;
    public uint ViewSize;
    public ulong SchemaId;
    public ulong WorldHostId;
    public ulong Generation;
    public nint Entries;
    public uint EntryCount;
    public uint EntryCapacity;
    public uint EntrySize;
    public UnrealSharedSnapshotFlagsV4 Flags;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct UnrealSharedDataTransportTableV4
{
    public uint AbiVersion;
    public uint TableSize;
    public uint SnapshotViewSize;
    public uint TransformEntrySize;
    public ulong TransformSchemaId;
    public UnrealSharedDataCapabilitiesV4 Capabilities;
    public nint RetainContext;
    public nint ReleaseContext;
    public nint AcquireTransformSnapshot;
}

public static class UnrealSharedDataAbiV4
{
    public const uint Version = 4;
    public const ulong TransformSnapshotSchemaId = 0x55535452414E5334UL;
    public const int SnapshotViewSize = 56;
    public const int TransformEntrySize = 96;
    public const int TransportTableSize = 56;
}

internal interface IUnrealSharedSnapshotTransport
{
    bool SupportsSharedData { get; }

    bool TryAcquireTransformSnapshot(out UnrealSharedSnapshotViewV4 view);
}

internal interface IUnrealWorldDataPlaneProvider
{
    UnrealWorldDataPlane DataPlane { get; }
}

/// <summary>
/// Per-World zero-copy data plane. Acquired spans are borrowed and must not be
/// retained after the next managed Tick begins.
/// </summary>
public sealed class UnrealWorldDataPlane
{
    internal static UnrealWorldDataPlane Unavailable { get; } = new(null, 0);

    private readonly IUnrealSharedSnapshotTransport? _transport;
    private readonly IUnrealGameplayDataTransport? _gameplayTransport;
    private readonly ulong _expectedWorldHostId;

    internal UnrealWorldDataPlane(
        IUnrealSharedSnapshotTransport? transport,
        ulong expectedWorldHostId)
    {
        _transport = transport;
        _gameplayTransport = transport as IUnrealGameplayDataTransport;
        _expectedWorldHostId = expectedWorldHostId;
    }

    public bool IsAvailable => _transport?.SupportsSharedData == true;

    public bool SupportsGameplayData =>
        _gameplayTransport?.SupportsGameplayData == true;

    public UnrealSharedObjectHandleV4 Scope(UnrealObjectHandle handle)
    {
        if (handle.IsNull)
        {
            return default;
        }
        if (!handle.IsWellFormed || _expectedWorldHostId == 0)
        {
            throw new InvalidOperationException(
                "A live World-scoped Unreal object handle is required.");
        }
        return new UnrealSharedObjectHandleV4(_expectedWorldHostId, handle.Value);
    }

    public UnrealSharedObjectHandleV4 Scope(UnrealClassHandle handle) =>
        Scope(handle.ObjectHandle);

    public UnrealGameplayDataPhaseV4 GameplayPhase
    {
        get
        {
            IUnrealGameplayDataTransport transport = RequireGameplayTransport();
            if (!transport.GetPhase(out UnrealGameplayDataPhaseV4 phase, out _))
            {
                throw new ObjectDisposedException(nameof(UnrealWorldDataPlane));
            }
            return phase;
        }
    }

    public ulong GameplayFrameNumber
    {
        get
        {
            IUnrealGameplayDataTransport transport = RequireGameplayTransport();
            if (!transport.GetPhase(out _, out ulong frameNumber))
            {
                throw new ObjectDisposedException(nameof(UnrealWorldDataPlane));
            }
            return frameNumber;
        }
    }

    public UnrealCommandBatch BeginCommands(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        IUnrealGameplayDataTransport transport = RequireGameplayTransport();
        if (!transport.BeginCommandBatch(
                checked((uint)capacity),
                out UnrealCommandBatchViewV4 view))
        {
            throw new InvalidOperationException(
                "Command batches can only begin during the managed World phase.");
        }
        ValidateCommandView(view);
        return new UnrealCommandBatch(transport, view);
    }

    public unsafe UnrealGameplayEventSnapshot AcquireEvents()
    {
        IUnrealGameplayDataTransport transport = RequireGameplayTransport();
        if (!transport.AcquireEvents(out UnrealGameplayEventViewV4 view))
        {
            throw new ObjectDisposedException(nameof(UnrealWorldDataPlane));
        }
        if (view.AbiVersion != UnrealGameplayDataAbiV4.Version ||
            view.ViewSize != UnrealGameplayDataAbiV4.EventViewSize ||
            view.SchemaId != UnrealGameplayDataAbiV4.EventSchemaId ||
            view.WorldHostId == 0 ||
            (_expectedWorldHostId != 0 && view.WorldHostId != _expectedWorldHostId) ||
            view.EventEntrySize != UnrealGameplayDataAbiV4.EventEntrySize ||
            view.EventCount > view.EventCapacity ||
            (view.EventCount != 0 && view.Events == nint.Zero))
        {
            throw new InvalidOperationException(
                "The native gameplay event view has an incompatible layout.");
        }
        return new UnrealGameplayEventSnapshot(
            view.WorldHostId,
            view.FrameNumber,
            view.DroppedEventCount,
            new ReadOnlySpan<UnrealGameplayEventV4>(
                (void*)view.Events,
                checked((int)view.EventCount)));
    }

    public UnrealTraceBatch BeginTraceBatch(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        IUnrealGameplayDataTransport transport = RequireGameplayTransport();
        if (!transport.BeginTraceBatch(
                checked((uint)capacity),
                out UnrealTraceRequestViewV4 view))
        {
            throw new InvalidOperationException(
                "A trace batch can only begin during the managed World phase, " +
                "and only one batch may be active at a time.");
        }
        if (view.AbiVersion != UnrealGameplayDataAbiV4.Version ||
            view.ViewSize != UnrealGameplayDataAbiV4.TraceRequestViewSize ||
            view.SchemaId != UnrealGameplayDataAbiV4.TraceRequestSchemaId ||
            view.WorldHostId == 0 ||
            (_expectedWorldHostId != 0 && view.WorldHostId != _expectedWorldHostId) ||
            view.BatchToken == 0 ||
            view.RequestEntrySize != UnrealGameplayDataAbiV4.TraceRequestEntrySize ||
            view.RequestCapacity != checked((uint)capacity) ||
            view.Requests == nint.Zero)
        {
            transport.CancelTraceBatch(view.BatchToken);
            throw new InvalidOperationException(
                "The native trace request view has an incompatible layout.");
        }
        return new UnrealTraceBatch(transport, view);
    }

    public unsafe UnrealTransformSnapshot AcquireTransformSnapshot()
    {
        if (_transport is null || !_transport.SupportsSharedData)
        {
            throw new NotSupportedException(
                "The native World does not advertise Shared Data ABI v4.");
        }
        if (!_transport.TryAcquireTransformSnapshot(out UnrealSharedSnapshotViewV4 view))
        {
            throw new ObjectDisposedException(
                nameof(UnrealWorldDataPlane),
                "The native World data plane has detached.");
        }

        Validate(view);
        var entries = new ReadOnlySpan<UnrealTransformSnapshotEntryV4>(
            (void*)view.Entries,
            checked((int)view.EntryCount));
        return new UnrealTransformSnapshot(
            view.WorldHostId,
            view.Generation,
            entries);
    }

    private void Validate(UnrealSharedSnapshotViewV4 view)
    {
        if (view.AbiVersion != UnrealSharedDataAbiV4.Version ||
            view.ViewSize != UnrealSharedDataAbiV4.SnapshotViewSize ||
            view.SchemaId != UnrealSharedDataAbiV4.TransformSnapshotSchemaId ||
            view.EntrySize != UnrealSharedDataAbiV4.TransformEntrySize ||
            view.WorldHostId == 0 ||
            (_expectedWorldHostId != 0 && view.WorldHostId != _expectedWorldHostId) ||
            view.EntryCount > view.EntryCapacity ||
            (view.EntryCount != 0 && view.Entries == nint.Zero) ||
            (view.Flags & UnrealSharedSnapshotFlagsV4.BorrowedUntilNextPublish) == 0)
        {
            throw new InvalidOperationException(
                "The native Transform snapshot has an incompatible Shared Data ABI v4 layout.");
        }
    }

    internal static void ValidateTraceResultView(
        UnrealTraceResultViewV4 view,
        ulong worldHostId,
        ulong batchToken)
    {
        if (view.AbiVersion != UnrealGameplayDataAbiV4.Version ||
            view.ViewSize != UnrealGameplayDataAbiV4.TraceResultViewSize ||
            view.SchemaId != UnrealGameplayDataAbiV4.TraceResultSchemaId ||
            view.WorldHostId != worldHostId ||
            view.BatchToken != batchToken ||
            view.ResultEntrySize != UnrealGameplayDataAbiV4.TraceResultEntrySize ||
            view.ResultCount > view.ResultCapacity ||
            (view.ResultCount != 0 && view.Results == nint.Zero))
        {
            throw new InvalidOperationException(
                "The native trace result view has an incompatible layout.");
        }
    }

    private void ValidateCommandView(UnrealCommandBatchViewV4 view)
    {
        if (view.AbiVersion != UnrealGameplayDataAbiV4.Version ||
            view.ViewSize != UnrealGameplayDataAbiV4.CommandBatchViewSize ||
            view.SchemaId != UnrealGameplayDataAbiV4.CommandSchemaId ||
            view.WorldHostId == 0 ||
            (_expectedWorldHostId != 0 && view.WorldHostId != _expectedWorldHostId) ||
            view.BatchToken == 0 || view.FirstSequenceId == 0 ||
            view.CommandEntrySize != UnrealGameplayDataAbiV4.CommandEntrySize ||
            view.CommandCapacity == 0 || view.Commands == nint.Zero)
        {
            throw new InvalidOperationException(
                "The native command view has an incompatible layout.");
        }
    }

    private IUnrealGameplayDataTransport RequireGameplayTransport() =>
        _gameplayTransport is { SupportsGameplayData: true } transport
            ? transport
            : throw new NotSupportedException(
                "The native World does not advertise Gameplay Data ABI v4.");
}

/// <summary>A stack-only, allocation-free view over the currently published buffer.</summary>
public readonly ref struct UnrealTransformSnapshot
{
    private readonly ReadOnlySpan<UnrealTransformSnapshotEntryV4> _entries;

    internal UnrealTransformSnapshot(
        ulong worldHostId,
        ulong generation,
        ReadOnlySpan<UnrealTransformSnapshotEntryV4> entries)
    {
        WorldHostId = worldHostId;
        Generation = generation;
        _entries = entries;
    }

    public ulong WorldHostId { get; }

    public ulong Generation { get; }

    public ReadOnlySpan<UnrealTransformSnapshotEntryV4> Entries => _entries;

    public bool TryGetTransform(UnrealObjectHandle actor, out UnrealTransform transform)
    {
        foreach (ref readonly UnrealTransformSnapshotEntryV4 entry in _entries)
        {
            if (entry.Actor.WorldHostId == WorldHostId &&
                entry.Actor.ObjectHandle == actor)
            {
                transform = entry.Transform;
                return true;
            }
        }
        transform = default;
        return false;
    }
}
