using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace UnifiedScript.Unreal;

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
public readonly partial struct UnrealTransformSnapshotEntryV4
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

    public UnrealTransform Transform => new(
        new UnrealQuaternion(RotationX, RotationY, RotationZ, RotationW),
        new UnrealVector(TranslationX, TranslationY, TranslationZ),
        new UnrealVector(ScaleX, ScaleY, ScaleZ));
}

public readonly partial struct UnrealFixedValueCollectionSnapshotV1
{
    public UnrealFixedValueCollectionSnapshotV1(
        ulong sequenceId,
        UnrealVector vector,
        ReadOnlySpan<int> values,
        ReadOnlySpan<KeyValuePair<int, int>> pairs)
    {
        if (values.Length > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(values));
        }
        if (pairs.Length > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(pairs));
        }
        SequenceId = sequenceId;
        VectorX = vector.X;
        VectorY = vector.Y;
        VectorZ = vector.Z;
        ValueCount = checked((uint)values.Length);
        PairCount = checked((uint)pairs.Length);
        Value0 = values.Length > 0 ? values[0] : 0;
        Value1 = values.Length > 1 ? values[1] : 0;
        Value2 = values.Length > 2 ? values[2] : 0;
        Value3 = values.Length > 3 ? values[3] : 0;
        Value4 = values.Length > 4 ? values[4] : 0;
        Value5 = values.Length > 5 ? values[5] : 0;
        Value6 = values.Length > 6 ? values[6] : 0;
        Value7 = values.Length > 7 ? values[7] : 0;
        Key0 = pairs.Length > 0 ? pairs[0].Key : 0;
        Key1 = pairs.Length > 1 ? pairs[1].Key : 0;
        Key2 = pairs.Length > 2 ? pairs[2].Key : 0;
        Key3 = pairs.Length > 3 ? pairs[3].Key : 0;
        MapValue0 = pairs.Length > 0 ? pairs[0].Value : 0;
        MapValue1 = pairs.Length > 1 ? pairs[1].Value : 0;
        MapValue2 = pairs.Length > 2 ? pairs[2].Value : 0;
        MapValue3 = pairs.Length > 3 ? pairs[3].Value : 0;
    }

    public UnrealVector Vector => new(VectorX, VectorY, VectorZ);
}

public readonly partial struct UnrealBufferedGameplayEventV1
{
    public UnrealBufferedGameplayEventV1(
        ulong sequenceId,
        ulong eventId,
        int value,
        uint flags,
        double weight,
        UnrealSharedObjectHandleV4 context)
    {
        SequenceId = sequenceId;
        EventId = eventId;
        Value = value;
        Flags = flags;
        Weight = weight;
        Context = context;
    }
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

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct UnrealSharedDataTransportTableV4Extended
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
    public ulong LayoutCompatibilityId;
    public uint LayoutRevision;
    public uint MaximumTransformSnapshotCount;
    public nint GetTelemetry;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct UnrealSharedDataTransportTableV4ChannelsExtended
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
    public ulong LayoutCompatibilityId;
    public uint LayoutRevision;
    public uint MaximumTransformSnapshotCount;
    public nint GetTelemetry;
    public uint GeneratedChannelViewSize;
    public uint MaximumGeneratedSnapshotCount;
    public ulong MaximumGeneratedChannelBytes;
    public nint AcquireGeneratedSnapshot;
}

public static partial class UnrealSharedDataAbiV4
{
    public const int TransportTableSize = 56;
    public const int ExtendedTransportTableSize = 80;
    public const int ChannelsExtendedTransportTableSize = 104;
}

internal interface IUnrealSharedSnapshotTransport
{
    bool SupportsSharedData { get; }

    bool TryAcquireTransformSnapshot(out UnrealSharedSnapshotViewV4 view);
}

internal interface IUnrealDataPlaneTelemetryTransport
{
    bool TryGetSharedDataTelemetry(out UnrealSharedDataTelemetryV1 telemetry);

    bool TryGetGameplayDataTelemetry(out UnrealGameplayDataTelemetryV1 telemetry);
}

internal interface IUnrealGeneratedDataTransport
{
    bool SupportsGeneratedSnapshots { get; }

    bool SupportsGeneratedEvents { get; }

    bool TryAcquireGeneratedSnapshot(
        ulong schemaId,
        uint entrySize,
        out UnrealGeneratedDataViewV1 view);

    bool TryAcquireGeneratedEvents(
        ulong schemaId,
        uint entrySize,
        out UnrealGeneratedDataViewV1 view);
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
    private readonly IUnrealOverlapDataTransport? _overlapTransport;
    private readonly IUnrealGeneratedDataTransport? _generatedTransport;
    private readonly ulong _expectedWorldHostId;

    internal UnrealWorldDataPlane(
        IUnrealSharedSnapshotTransport? transport,
        ulong expectedWorldHostId)
    {
        _transport = transport;
        _gameplayTransport = transport as IUnrealGameplayDataTransport;
        _overlapTransport = transport as IUnrealOverlapDataTransport;
        _generatedTransport = transport as IUnrealGeneratedDataTransport;
        _expectedWorldHostId = expectedWorldHostId;
    }

    public bool IsAvailable => _transport?.SupportsSharedData == true;

    public bool SupportsGameplayData =>
        _gameplayTransport?.SupportsGameplayData == true;

    public bool SupportsOverlapQueries =>
        _overlapTransport?.SupportsOverlapData == true;

    public bool SupportsGeneratedSnapshots =>
        _generatedTransport?.SupportsGeneratedSnapshots == true;

    public bool SupportsGeneratedEvents =>
        _generatedTransport?.SupportsGeneratedEvents == true;

    public UnrealSharedDataTelemetryV1 GetSharedDataTelemetry()
    {
        if (_transport is not IUnrealDataPlaneTelemetryTransport telemetryTransport ||
            !telemetryTransport.TryGetSharedDataTelemetry(
                out UnrealSharedDataTelemetryV1 telemetry))
        {
            throw new NotSupportedException(
                "The native World does not advertise data-plane capacity telemetry.");
        }
        if (telemetry.SchemaRevision < UnrealDataPlaneLayoutV1.Revision ||
            telemetry.StructSize < UnrealSharedDataAbiV4.TelemetrySize ||
            telemetry.CompatibilityId != UnrealDataPlaneLayoutV1.CompatibilityId ||
            telemetry.WorldHostId == 0 ||
            (_expectedWorldHostId != 0 &&
             telemetry.WorldHostId != _expectedWorldHostId) ||
            telemetry.PublishedEntryCount > telemetry.MaximumEntryCount ||
            telemetry.MaximumEntryCount !=
                UnrealSharedDataAbiV4.MaximumTransformSnapshotCount)
        {
            throw new InvalidOperationException(
                "The native Shared Data telemetry has an incompatible schema or bounds.");
        }
        return telemetry;
    }

    public UnrealGameplayDataTelemetryV1 GetGameplayDataTelemetry()
    {
        if (_transport is not IUnrealDataPlaneTelemetryTransport telemetryTransport ||
            !telemetryTransport.TryGetGameplayDataTelemetry(
                out UnrealGameplayDataTelemetryV1 telemetry))
        {
            throw new NotSupportedException(
                "The native World does not advertise gameplay data-plane telemetry.");
        }
        if (telemetry.SchemaRevision < UnrealDataPlaneLayoutV1.Revision ||
            telemetry.StructSize < UnrealGameplayDataAbiV4.TelemetrySize ||
            telemetry.CompatibilityId != UnrealDataPlaneLayoutV1.CompatibilityId ||
            telemetry.WorldHostId == 0 ||
            (_expectedWorldHostId != 0 &&
             telemetry.WorldHostId != _expectedWorldHostId) ||
            telemetry.MaximumCommandCount != UnrealGameplayDataAbiV4.MaximumCommandCount ||
            telemetry.MaximumEventCount != UnrealGameplayDataAbiV4.MaximumEventCount ||
            telemetry.MaximumTraceCount != UnrealGameplayDataAbiV4.MaximumTraceCount ||
            telemetry.MaximumOverlapCount !=
                UnrealGameplayDataAbiV4.MaximumOverlapCount ||
            telemetry.LastExecutedSequenceId > telemetry.LastCommittedSequenceId ||
            telemetry.Phase > (uint)UnrealGameplayDataPhaseV4.ShuttingDown)
        {
            throw new InvalidOperationException(
                "The native Gameplay Data telemetry has an incompatible schema, order, or bounds.");
        }
        return telemetry;
    }

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

    public UnrealOverlapBatch BeginOverlapBatch(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        IUnrealOverlapDataTransport transport = _overlapTransport is
            { SupportsOverlapData: true } available
                ? available
                : throw new NotSupportedException(
                    "The native World does not advertise Batch Overlap layouts.");
        if (!transport.BeginOverlapBatch(
                checked((uint)capacity),
                out UnrealOverlapRequestViewV1 view))
        {
            throw new InvalidOperationException(
                "An overlap batch can only begin during the managed World phase, " +
                "and only one batch may be active at a time.");
        }
        if (view.AbiVersion != UnrealGameplayDataAbiV4.Version ||
            view.ViewSize != UnrealGameplayDataAbiV4.OverlapRequestViewSize ||
            view.SchemaId != UnrealGameplayDataAbiV4.OverlapRequestSchemaId ||
            view.WorldHostId == 0 ||
            (_expectedWorldHostId != 0 && view.WorldHostId != _expectedWorldHostId) ||
            view.BatchToken == 0 ||
            view.RequestEntrySize != UnrealGameplayDataAbiV4.OverlapRequestEntrySize ||
            view.RequestCapacity != checked((uint)capacity) ||
            view.Requests == nint.Zero)
        {
            transport.CancelOverlapBatch(view.BatchToken);
            throw new InvalidOperationException(
                "The native overlap request view has an incompatible layout.");
        }
        return new UnrealOverlapBatch(transport, view);
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

    public unsafe UnrealGeneratedDataSnapshot<T> AcquireGeneratedSnapshot<T>(
        ulong schemaId)
        where T : unmanaged
    {
        IUnrealGeneratedDataTransport transport =
            _generatedTransport is { SupportsGeneratedSnapshots: true } available
                ? available
                : throw new NotSupportedException(
                    "The native World does not advertise generated snapshot channels.");
        uint entrySize = checked((uint)Unsafe.SizeOf<T>());
        if (schemaId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaId));
        }
        if (!transport.TryAcquireGeneratedSnapshot(schemaId, entrySize, out var view))
        {
            throw new InvalidOperationException(
                "The generated snapshot channel is unavailable or has a different row layout.");
        }
        ValidateGeneratedDataView(
            view,
            schemaId,
            entrySize,
            UnrealSharedDataAbiV4.MaximumGeneratedSnapshotCount,
            UnrealGeneratedDataFlagsV1.Snapshot);
        return new UnrealGeneratedDataSnapshot<T>(
            view.WorldHostId,
            view.PublicationId,
            view.DroppedEntryCount,
            new ReadOnlySpan<T>((void*)view.Entries, checked((int)view.EntryCount)));
    }

    public unsafe UnrealGeneratedDataSnapshot<T> AcquireGeneratedEvents<T>(
        ulong schemaId)
        where T : unmanaged
    {
        IUnrealGeneratedDataTransport transport =
            _generatedTransport is { SupportsGeneratedEvents: true } available
                ? available
                : throw new NotSupportedException(
                    "The native World does not advertise generated event channels.");
        uint entrySize = checked((uint)Unsafe.SizeOf<T>());
        if (schemaId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaId));
        }
        if (!transport.TryAcquireGeneratedEvents(schemaId, entrySize, out var view))
        {
            throw new InvalidOperationException(
                "The generated event channel is unavailable or has a different row layout.");
        }
        ValidateGeneratedDataView(
            view,
            schemaId,
            entrySize,
            UnrealGameplayDataAbiV4.MaximumGeneratedEventCount,
            UnrealGeneratedDataFlagsV1.Events);
        return new UnrealGeneratedDataSnapshot<T>(
            view.WorldHostId,
            view.PublicationId,
            view.DroppedEntryCount,
            new ReadOnlySpan<T>((void*)view.Entries, checked((int)view.EntryCount)));
    }

    private void ValidateGeneratedDataView(
        UnrealGeneratedDataViewV1 view,
        ulong schemaId,
        uint entrySize,
        uint maximumEntryCount,
        UnrealGeneratedDataFlagsV1 requiredKind)
    {
        ulong byteCount = (ulong)view.EntryCount * view.EntrySize;
        if (view.AbiVersion != UnrealSharedDataAbiV4.Version ||
            view.ViewSize != UnrealSharedDataAbiV4.GeneratedChannelViewSize ||
            view.SchemaId != schemaId ||
            view.WorldHostId == 0 ||
            (_expectedWorldHostId != 0 && view.WorldHostId != _expectedWorldHostId) ||
            view.PublicationId == 0 ||
            view.EntrySize != entrySize ||
            view.EntryCount > view.EntryCapacity ||
            view.EntryCount > maximumEntryCount ||
            byteCount > UnrealSharedDataAbiV4.MaximumGeneratedChannelBytes ||
            (view.EntryCount != 0 && view.Entries == nint.Zero) ||
            (view.Flags & UnrealGeneratedDataFlagsV1.BorrowedUntilNextPublish) == 0 ||
            (view.Flags & requiredKind) == 0)
        {
            throw new InvalidOperationException(
                "The generated data channel has an incompatible schema, World, or bounds.");
        }
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

    internal static void ValidateOverlapResultView(
        UnrealOverlapResultViewV1 view,
        ulong worldHostId,
        ulong batchToken)
    {
        if (view.AbiVersion != UnrealGameplayDataAbiV4.Version ||
            view.ViewSize != UnrealGameplayDataAbiV4.OverlapResultViewSize ||
            view.SchemaId != UnrealGameplayDataAbiV4.OverlapResultSchemaId ||
            view.WorldHostId != worldHostId ||
            view.BatchToken != batchToken ||
            view.ResultEntrySize != UnrealGameplayDataAbiV4.OverlapResultEntrySize ||
            view.ResultCount > view.ResultCapacity ||
            (view.ResultCount != 0 && view.Results == nint.Zero))
        {
            throw new InvalidOperationException(
                "The native overlap result view has an incompatible layout.");
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

/// <summary>
/// Stack-only, allocation-free view over one schema-generated snapshot or event buffer.
/// The view is valid only until the native channel publishes again.
/// </summary>
public readonly ref struct UnrealGeneratedDataSnapshot<T>
    where T : unmanaged
{
    private readonly ReadOnlySpan<T> _entries;

    internal UnrealGeneratedDataSnapshot(
        ulong worldHostId,
        ulong publicationId,
        ulong droppedEntryCount,
        ReadOnlySpan<T> entries)
    {
        WorldHostId = worldHostId;
        PublicationId = publicationId;
        DroppedEntryCount = droppedEntryCount;
        _entries = entries;
    }

    public ulong WorldHostId { get; }

    public ulong PublicationId { get; }

    public ulong DroppedEntryCount { get; }

    public ReadOnlySpan<T> Entries => _entries;
}
