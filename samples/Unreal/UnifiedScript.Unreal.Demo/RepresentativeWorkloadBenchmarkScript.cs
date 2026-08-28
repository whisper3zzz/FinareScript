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

    public void OnStart(UnrealScriptContext context)
    {
        var component = new UnifiedScriptComponent(context.Host, context.Bridge);
        string workload = component.ManagedState.StartsWith(
            "Run=", StringComparison.Ordinal)
            ? component.ManagedState[4..]
            : string.Empty;
        component.ManagedState = workload switch
        {
            StructContainerWorkload => RunStructContainer(component),
            DelegateWorkload => RunDelegateStorm(component, context.Host),
            _ => $"Representative:v1;Workload={workload};Error=unsupported",
        };
    }

    public void OnTick(float deltaSeconds)
    {
    }

    public void OnStop()
    {
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
