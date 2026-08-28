using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Unreal.Demo;

/// <summary>Frozen struct/container and delegate workloads used by M10.1.</summary>
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
            StructContainerWorkload => RunStructContainer(component),
            DelegateWorkload => RunDelegateStorm(component, context.Host),
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

    private static string RunStructContainer(UnifiedScriptComponent component)
    {
        const int warmupIterations = 1_024;
        const int measuredIterations = 16_384;
        const long calls = measuredIterations * 3L;
        const long bytesCopied = measuredIterations * 200L;
        var left = new UnrealVector(1.0, 2.0, 3.0);
        var right = UnrealVector.One;
        int[] array = [1, 2, 3, 4, 5, 6, 7, 8];
        var map = new Dictionary<int, int>
        {
            [1] = 10,
            [2] = 20,
            [3] = 30,
            [4] = 40,
        };

        _ = RunStructContainerLoop(
            component, left, right, array, map, warmupIterations);
        int collectionsBefore = TotalCollectionCount();
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        long checksum = RunStructContainerLoop(
            component, left, right, array, map, measuredIterations);
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

    private static long RunStructContainerLoop(
        UnifiedScriptComponent component,
        UnrealVector left,
        UnrealVector right,
        IReadOnlyList<int> array,
        IReadOnlyDictionary<int, int> map,
        int iterations)
    {
        long checksum = 0;
        for (int index = 0; index < iterations; index++)
        {
            UnrealVector vector = component.AddBenchmarkVector(left, right);
            IReadOnlyList<int> echoedArray = component.EchoBenchmarkArray(array);
            IReadOnlyDictionary<int, int> echoedMap = component.EchoBenchmarkMap(map);
            checksum += (long)vector.X;
            foreach (int value in echoedArray)
            {
                checksum += value;
            }
            foreach ((int key, int value) in echoedMap)
            {
                checksum += key + value;
            }
        }
        return checksum;
    }

    private static string RunDelegateStorm(
        UnifiedScriptComponent component,
        UnrealObjectHandle context)
    {
        const int warmupIterations = 4_096;
        const int measuredIterations = 100_000;
        const long calls = measuredIterations * 2L;
        const long bytesCopied = measuredIterations * 17L;
        long checksum = 0;
        using IUnrealEventSubscription subscription =
            component.SubscribeOnBenchmarkSignal((value, weight, enabled, valueContext) =>
            {
                checksum += value + (long)weight;
                checksum += enabled ? 1 : 0;
                checksum += valueContext.IsNull ? 0 : 1;
            });

        for (int index = 0; index < warmupIterations; index++)
        {
            component.BroadcastBenchmarkSignal(index, 2.0f, true, context);
        }
        checksum = 0;
        int collectionsBefore = TotalCollectionCount();
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int index = 0; index < measuredIterations; index++)
        {
            component.BroadcastBenchmarkSignal(index, 2.0f, true, context);
        }
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
