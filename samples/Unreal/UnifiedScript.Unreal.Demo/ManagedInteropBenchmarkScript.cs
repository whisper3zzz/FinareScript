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

    public void OnStart(UnrealScriptContext context)
    {
        var component = new UnifiedScriptComponent(context.Host, context.Bridge);
        long checksum = 0;
        for (int index = 0; index < WarmupIterations; index++)
        {
            checksum += component.Add(index, 1);
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int index = 0; index < MeasuredIterations; index++)
        {
            checksum += component.Add(index, 1);
        }
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double nanosecondsPerCall = elapsed.TotalNanoseconds / MeasuredIterations;
        component.ManagedState = string.Create(
            CultureInfo.InvariantCulture,
            $"Benchmark:v1;Iterations={MeasuredIterations};NanosecondsPerCall={nanosecondsPerCall:F3};AllocatedBytes={allocatedBytes};Checksum={checksum}");
    }

    public void OnTick(float deltaSeconds)
    {
    }

    public void OnStop()
    {
    }
}
