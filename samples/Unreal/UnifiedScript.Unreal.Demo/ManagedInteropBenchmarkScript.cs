using System;
using System.Diagnostics;
using System.Globalization;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Unreal.Demo;

/// <summary>Live CoreCLR-to-UObject scalar-call benchmark used by UE Automation.</summary>
public sealed class ManagedInteropBenchmarkScript : IUnrealManagedScript
{
    private const int WarmupIterations = 4_096;
    private const int MeasuredIterations = 100_000;
    private const int SampleCount = 5;

    public void OnStart(UnrealScriptContext context)
    {
        var component = new UnifiedScriptComponent(context.Host, context.Bridge);
        long checksum = 0;
        for (int index = 0; index < WarmupIterations; index++)
        {
            checksum += component.Add(index, 1);
        }

        var samples = new double[SampleCount];
        int collectionsBefore = GC.CollectionCount(0) +
            GC.CollectionCount(1) + GC.CollectionCount(2);
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int sample = 0; sample < samples.Length; sample++)
        {
            long started = Stopwatch.GetTimestamp();
            for (int index = 0; index < MeasuredIterations; index++)
            {
                checksum += component.Add(index, 1);
            }
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            samples[sample] = elapsed.TotalNanoseconds / MeasuredIterations;
        }
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        int collectionCount = GC.CollectionCount(0) +
            GC.CollectionCount(1) + GC.CollectionCount(2) - collectionsBefore;
        double pauseMilliseconds =
            (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;
        Array.Sort(samples);
        double nanosecondsPerCall = samples[samples.Length / 2];
        component.ManagedState = string.Create(
            CultureInfo.InvariantCulture,
            $"Benchmark:v3;Iterations={MeasuredIterations};SampleCount={SampleCount};NanosecondsPerCall={nanosecondsPerCall:F3};AllocatedBytes={allocatedBytes};GcCollectionCount={collectionCount};GcPauseMilliseconds={pauseMilliseconds:F6};Checksum={checksum}");
    }

    public void OnTick(float deltaSeconds)
    {
    }

    public void OnStop()
    {
    }
}
