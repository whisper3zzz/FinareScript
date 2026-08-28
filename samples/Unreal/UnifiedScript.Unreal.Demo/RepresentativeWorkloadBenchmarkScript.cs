using System;
using System.Diagnostics;
using System.Globalization;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Unreal.Demo;

/// <summary>
/// Versioned representative workloads used by M10. The M10.3 stable
/// container/delegate shapes use schema-generated, borrowed World channels;
/// dynamic and low-volume shapes keep the generated binding fallback.
/// </summary>
public sealed class RepresentativeWorkloadBenchmarkScript : IUnrealManagedScript
{
    private const string StructContainerWorkload = "struct-container-transfer";
    private const string DelegateWorkload = "delegate-storm";
    private const string PerFrameActorWorkload = "per-frame-actor-logic";
    private const string MassEntitiesWorkload = "mass-entities";
    private const string ResourceSpawnWorkload = "resource-spawn-flow";
    private const string BenchmarkActorClassPath = "/Script/Engine.Actor";
    private const int EntityCount = 1_000;

    private UnifiedScriptComponent? _component;
    private UnifiedScriptWorldLibrary? _worldServices;
    private UnrealObjectHandle _worldContext;
    private string _frameWorkload = string.Empty;
    private UnrealVector[]? _entityPositions;
    private UnrealVector[]? _entityVelocities;
    private int _frameIndex;
    private int _collectionsBefore;
    private TimeSpan _pauseBefore;
    private long _allocatedBefore;
    private long _checksum;
    private long _allocatedBytes;
    private int _collectionCount;
    private double _pauseMilliseconds;
    private bool _publishPending;
    private bool _completed;

    public void OnStart(UnrealScriptContext context)
    {
        var component = new UnifiedScriptComponent(context.Host, context.Bridge);
        string workload = component.ManagedState.StartsWith(
            "Run=", StringComparison.Ordinal)
            ? component.ManagedState[4..]
            : string.Empty;
        if (workload is PerFrameActorWorkload or MassEntitiesWorkload)
        {
            InitializeFrameWorkload(component, workload);
            return;
        }
        if (workload == ResourceSpawnWorkload)
        {
            _component = component;
            _frameWorkload = workload;
            _worldContext = context.Target;
            _worldServices = new UnifiedScriptWorldLibrary(
                context.Target,
                context.Bridge);
            component.ManagedState =
                $"Representative:v1;Workload={workload};Status=ready";
            return;
        }

        component.ManagedState = workload switch
        {
            StructContainerWorkload => RunStructContainer(context),
            DelegateWorkload => RunDelegateStorm(context),
            _ => $"Representative:v1;Workload={workload};Error=unsupported",
        };
    }

    public void OnTick(float deltaSeconds)
    {
        if (_component is null || _completed)
        {
            return;
        }
        if (_frameWorkload == ResourceSpawnWorkload)
        {
            _component.ManagedState = RunResourceSpawn(
                _worldServices!,
                _worldContext);
            _completed = true;
            return;
        }
        if (_publishPending)
        {
            _component.ManagedState = FormatFrameResult();
            _publishPending = false;
            _completed = true;
            return;
        }

        int warmupIterations = _frameWorkload == PerFrameActorWorkload ? 300 : 120;
        int measuredIterations = _frameWorkload == PerFrameActorWorkload ? 10_000 : 1_000;
        bool measured = _frameIndex >= warmupIterations;
        int measuredFrame = _frameIndex - warmupIterations;
        long frameChecksum = _frameWorkload == PerFrameActorWorkload
            ? RunActorFrame(_component, measured ? measuredFrame : _frameIndex)
            : RunMassEntityFrame(_component);
        if (measured)
        {
            _checksum += frameChecksum;
        }
        _frameIndex++;

        if (_frameIndex == warmupIterations)
        {
            ResetFrameState(_component);
            _collectionsBefore = TotalCollectionCount();
            _pauseBefore = GC.GetTotalPauseDuration();
            _allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        }
        else if (_frameIndex == warmupIterations + measuredIterations)
        {
            _allocatedBytes =
                GC.GetAllocatedBytesForCurrentThread() - _allocatedBefore;
            _collectionCount = TotalCollectionCount() - _collectionsBefore;
            _pauseMilliseconds =
                (GC.GetTotalPauseDuration() - _pauseBefore).TotalMilliseconds;
            _publishPending = true;
        }
    }

    public void OnStop()
    {
        _component = null;
        _worldServices = null;
        _worldContext = default;
        _entityPositions = null;
        _entityVelocities = null;
    }

    private void InitializeFrameWorkload(
        UnifiedScriptComponent component,
        string workload)
    {
        _component = component;
        _frameWorkload = workload;
        if (workload == MassEntitiesWorkload)
        {
            _entityPositions = new UnrealVector[EntityCount];
            _entityVelocities = new UnrealVector[EntityCount];
            for (int entity = 0; entity < EntityCount; entity++)
            {
                _entityVelocities[entity] = new UnrealVector(
                    entity % 5 + 1,
                    1.0,
                    0.0);
            }
        }
        ResetFrameState(component);
        component.ManagedState =
            $"RepresentativeFrame:v1;Workload={workload};Status=ready";
    }

    private void ResetFrameState(UnifiedScriptComponent component)
    {
        _checksum = 0;
        if (_frameWorkload == PerFrameActorWorkload)
        {
            component.ResetBenchmarkActorState();
            return;
        }
        Array.Clear(_entityPositions!);
    }

    private static long RunActorFrame(
        UnifiedScriptComponent component,
        int frame)
    {
        float moveRight = (frame & 1) == 0 ? 1.0f : -1.0f;
        UnrealVector state = component.ApplyBenchmarkActorInput(
            1.0f,
            moveRight,
            1.0f,
            0.0f);
        return checked((long)(state.X + state.Y + state.Z));
    }

    private long RunMassEntityFrame(UnifiedScriptComponent component)
    {
        UnrealVector[] positions = _entityPositions!;
        UnrealVector[] velocities = _entityVelocities!;
        long checksum = 0;
        for (int entity = 0; entity < EntityCount; entity++)
        {
            UnrealVector position = component.AddBenchmarkVector(
                positions[entity],
                velocities[entity]);
            positions[entity] = position;
            checksum += (long)position.X;
        }
        return checksum;
    }

    private string FormatFrameResult()
    {
        int warmupIterations = _frameWorkload == PerFrameActorWorkload ? 300 : 120;
        int measuredIterations = _frameWorkload == PerFrameActorWorkload ? 10_000 : 1_000;
        long calls = _frameWorkload == PerFrameActorWorkload
            ? 20_000L
            : 1_001_000L;
        long bytesCopied = _frameWorkload == PerFrameActorWorkload
            ? 440_000L
            : 72_004_000L;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"RepresentativeFrame:v1;Workload={_frameWorkload};Status=complete;WarmupIterations={warmupIterations};Iterations={measuredIterations};Calls={calls};BytesCopied={bytesCopied};AllocatedBytes={_allocatedBytes};GcCollectionCount={_collectionCount};GcPauseMilliseconds={_pauseMilliseconds:F6};Checksum={_checksum}");
    }

    private static string RunStructContainer(UnrealScriptContext context)
    {
        const int warmupIterations = 1_024;
        const int measuredIterations = 16_384;
        const long calls = 1;
        const long bytesCopied = 0;
        if (!context.DataPlane.SupportsGeneratedSnapshots)
        {
            throw new InvalidOperationException(
                "The generated fixed-value snapshot channel is unavailable.");
        }

        UnrealGeneratedDataSnapshot<UnrealFixedValueCollectionSnapshotV1> warmup =
            context.DataPlane.AcquireGeneratedSnapshot<
                UnrealFixedValueCollectionSnapshotV1>(
                    UnrealSharedDataAbiV4.FixedValueCollectionSchemaId);
        if (warmup.Entries.Length != measuredIterations)
        {
            throw new InvalidOperationException(
                $"Expected {measuredIterations} fixed-value rows, got " +
                $"{warmup.Entries.Length}.");
        }
        _ = ConsumeFixedValueRows(warmup.Entries[..warmupIterations]);
        int collectionsBefore = TotalCollectionCount();
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        UnrealGeneratedDataSnapshot<UnrealFixedValueCollectionSnapshotV1> snapshot =
            context.DataPlane.AcquireGeneratedSnapshot<
                UnrealFixedValueCollectionSnapshotV1>(
                    UnrealSharedDataAbiV4.FixedValueCollectionSchemaId);
        long checksum = ConsumeFixedValueRows(snapshot.Entries);
        double bridgeMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        int collectionCount = TotalCollectionCount() - collectionsBefore;
        double pauseMilliseconds =
            (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;

        return FormatResult(
            StructContainerWorkload,
            warmupIterations,
            measuredIterations,
            bridgeMilliseconds,
            calls,
            bytesCopied,
            allocatedBytes,
            collectionCount,
            pauseMilliseconds,
            checksum);
    }

    private static long ConsumeFixedValueRows(
        ReadOnlySpan<UnrealFixedValueCollectionSnapshotV1> rows)
    {
        long checksum = 0;
        foreach (ref readonly UnrealFixedValueCollectionSnapshotV1 row in rows)
        {
            checksum += (long)row.VectorX;
            checksum += row.Value0 + row.Value1 + row.Value2 + row.Value3;
            checksum += row.Value4 + row.Value5 + row.Value6 + row.Value7;
            checksum += row.Key0 + row.Key1 + row.Key2 + row.Key3;
            checksum += row.MapValue0 + row.MapValue1 +
                row.MapValue2 + row.MapValue3;
        }
        return checksum;
    }

    private static string RunDelegateStorm(UnrealScriptContext context)
    {
        const int warmupIterations = 4_096;
        const int measuredIterations = 65_536;
        const long calls = 1;
        const long bytesCopied = 0;
        if (!context.DataPlane.SupportsGeneratedEvents)
        {
            throw new InvalidOperationException(
                "The generated gameplay event channel is unavailable.");
        }

        UnrealGeneratedDataSnapshot<UnrealBufferedGameplayEventV1> warmup =
            context.DataPlane.AcquireGeneratedEvents<
                UnrealBufferedGameplayEventV1>(
                    UnrealGameplayDataAbiV4.BufferedGameplayEventSchemaId);
        if (warmup.Entries.Length != measuredIterations)
        {
            throw new InvalidOperationException(
                $"Expected {measuredIterations} buffered events, got " +
                $"{warmup.Entries.Length}.");
        }
        _ = ConsumeBufferedEvents(warmup.Entries[..warmupIterations]);
        int collectionsBefore = TotalCollectionCount();
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        UnrealGeneratedDataSnapshot<UnrealBufferedGameplayEventV1> events =
            context.DataPlane.AcquireGeneratedEvents<
                UnrealBufferedGameplayEventV1>(
                    UnrealGameplayDataAbiV4.BufferedGameplayEventSchemaId);
        long checksum = ConsumeBufferedEvents(events.Entries);
        double bridgeMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        int collectionCount = TotalCollectionCount() - collectionsBefore;
        double pauseMilliseconds =
            (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;

        return FormatResult(
            DelegateWorkload,
            warmupIterations,
            measuredIterations,
            bridgeMilliseconds,
            calls,
            bytesCopied,
            allocatedBytes,
            collectionCount,
            pauseMilliseconds,
            checksum);
    }

    private static long ConsumeBufferedEvents(
        ReadOnlySpan<UnrealBufferedGameplayEventV1> events)
    {
        long checksum = 0;
        foreach (ref readonly UnrealBufferedGameplayEventV1 gameplayEvent in events)
        {
            checksum += gameplayEvent.Value + (long)gameplayEvent.Weight;
            checksum += (gameplayEvent.Flags & 1U) != 0 ? 1 : 0;
            checksum += gameplayEvent.Context.IsWellFormed ? 1 : 0;
        }
        return checksum;
    }

    private static string RunResourceSpawn(
        UnifiedScriptWorldLibrary world,
        UnrealObjectHandle worldContext)
    {
        const int warmupIterations = 16;
        const int measuredIterations = 256;
        const long calls = measuredIterations * 3L;
        const long bytesCopied = measuredIterations * 157L;

        _ = RunResourceSpawnLoop(world, worldContext, warmupIterations);
        int collectionsBefore = TotalCollectionCount();
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        long checksum = RunResourceSpawnLoop(
            world,
            worldContext,
            measuredIterations);
        double bridgeMilliseconds =
            Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        int collectionCount = TotalCollectionCount() - collectionsBefore;
        double pauseMilliseconds =
            (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;

        return FormatResult(
            ResourceSpawnWorkload,
            warmupIterations,
            measuredIterations,
            bridgeMilliseconds,
            calls,
            bytesCopied,
            allocatedBytes,
            collectionCount,
            pauseMilliseconds,
            checksum);
    }

    private static long RunResourceSpawnLoop(
        UnifiedScriptWorldLibrary world,
        UnrealObjectHandle worldContext,
        int iterations)
    {
        long checksum = 0;
        for (int index = 0; index < iterations; index++)
        {
            UnrealClassHandle actorClass = world.LoadClassByPath(
                worldContext,
                BenchmarkActorClassPath);
            if (actorClass.IsNull)
            {
                throw new InvalidOperationException(
                    "The benchmark Actor class could not be loaded.");
            }
            checksum++;

            UnrealObjectHandle actor = world.SpawnActor(
                worldContext,
                actorClass,
                UnrealTransform.Identity);
            if (actor.IsNull)
            {
                throw new InvalidOperationException(
                    "The benchmark Actor could not be spawned.");
            }
            checksum++;

            if (!world.DestroyActor(worldContext, actor))
            {
                throw new InvalidOperationException(
                    "The benchmark Actor could not be destroyed.");
            }
            checksum++;
        }
        return checksum;
    }

    private static int TotalCollectionCount() =>
        GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);

    private static string FormatResult(
        string workload,
        int warmupIterations,
        int measuredIterations,
        double bridgeMilliseconds,
        long calls,
        long bytesCopied,
        long allocatedBytes,
        int collectionCount,
        double pauseMilliseconds,
        long checksum) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Representative:v1;Workload={workload};WarmupIterations={warmupIterations};Iterations={measuredIterations};BridgeTimeMilliseconds={bridgeMilliseconds:F6};Calls={calls};BytesCopied={bytesCopied};AllocatedBytes={allocatedBytes};GcCollectionCount={collectionCount};GcPauseMilliseconds={pauseMilliseconds:F6};Checksum={checksum}");
}

/// <summary>
/// Publishes cumulative runtime counters from every collectible generation so
/// the native M10.1 driver can measure a real reload/retire/collect window.
/// </summary>
public sealed class HotReloadBenchmarkScript : IUnrealManagedScript
{
    private UnifiedScriptComponent? _component;
    private int _generation;

    public void OnStart(UnrealScriptContext context)
    {
        _component = new UnifiedScriptComponent(context.Host, context.Bridge);
        _generation = context.Generation;
        PublishSnapshot();
    }

    public void OnTick(float deltaSeconds) => PublishSnapshot();

    public void OnStop() => _component = null;

    private void PublishSnapshot()
    {
        if (_component is null)
        {
            return;
        }
        long allocatedTotal = GC.GetTotalAllocatedBytes(precise: false);
        int collectionCount =
            GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        double pauseMilliseconds = GC.GetTotalPauseDuration().TotalMilliseconds;
        _component.ManagedState = string.Create(
            CultureInfo.InvariantCulture,
            $"HotReload:v1;Generation={_generation};AllocatedTotal={allocatedTotal};GcCollectionCount={collectionCount};GcPauseMilliseconds={pauseMilliseconds:F6}");
    }
}
