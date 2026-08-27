using System;
using System.Diagnostics;
using System.Globalization;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Unreal.Demo;

/// <summary>
/// Matched live-CoreCLR profile used to decide which gameplay workloads belong
/// in the World data plane instead of the generated per-call binding path.
/// </summary>
public sealed class DataPlaneWorkloadProfileScript : IUnrealManagedScript
{
    private const int WarmupIterations = 64;
    private const int MeasuredIterations = 1_024;
    private const int SampleCount = 3;

    public void OnStart(UnrealScriptContext context)
    {
        if (!context.DataPlane.SupportsGameplayData ||
            !context.DataPlane.SupportsOverlapQueries)
        {
            throw new InvalidOperationException(
                "The schema-generated gameplay data plane is unavailable.");
        }

        var component = new UnifiedScriptComponent(context.Host, context.Bridge);
        var world = new UnifiedScriptWorldLibrary(context.Target, context.Bridge);
        if (context.DataPlane.GameplayFrameNumber == 0)
        {
            throw new InvalidOperationException("The World frame is invalid.");
        }
        UnrealSharedObjectHandleV4 target = context.DataPlane.Scope(context.Target);
        var missStart = new UnrealVector(100_000.0, 100_000.0, 100_000.0);
        var missEnd = new UnrealVector(100_100.0, 100_000.0, 100_000.0);

        double checksum = 0.0;
        WarmUp(context, world, target, missStart, missEnd, ref checksum);

        var bindingScalarSamples = new double[SampleCount];
        var commandSamples = new double[SampleCount];
        var bindingTraceSamples = new double[SampleCount];
        var batchTraceSamples = new double[SampleCount];
        var attributeSamples = new double[SampleCount];

        MeasureBindingScalar(
            context,
            world,
            bindingScalarSamples,
            ref checksum);
        long dataPlaneAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        MeasureMovementCommands(
            context,
            target,
            commandSamples,
            ref checksum);
        MeasureBatchTrace(
            context,
            missStart,
            missEnd,
            batchTraceSamples,
            ref checksum);
        long dataPlaneAllocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - dataPlaneAllocatedBefore;
        MeasureBindingTrace(
            context,
            world,
            missStart,
            missEnd,
            bindingTraceSamples,
            ref checksum);
        MeasureAttributeBinding(component, attributeSamples, ref checksum);
        VerifyOverlap(context);

        double bindingScalarNs = Median(bindingScalarSamples);
        double commandSubmitNs = Median(commandSamples);
        double bindingTraceNs = Median(bindingTraceSamples);
        double batchTraceNs = Median(batchTraceSamples);
        double attributeReadNs = Median(attributeSamples);
        component.ManagedState = string.Create(
            CultureInfo.InvariantCulture,
            $"DataPlaneProfile:v1;Iterations={MeasuredIterations};SampleCount={SampleCount};" +
            $"BindingScalarNs={bindingScalarNs:F3};CommandSubmitNs={commandSubmitNs:F3};" +
            $"BindingTraceNs={bindingTraceNs:F3};BatchTraceNs={batchTraceNs:F3};" +
            $"AttributeReadNs={attributeReadNs:F3};DataPlaneAllocatedBytes={dataPlaneAllocatedBytes};" +
            $"Movement=data-plane;Overlap=data-plane;Attribute=binding;" +
            $"Spawn=hybrid;Query=data-plane;Checksum={checksum:F3}");
    }

    public void OnTick(float deltaSeconds)
    {
    }

    public void OnStop()
    {
    }

    private static void WarmUp(
        UnrealScriptContext context,
        UnifiedScriptWorldLibrary world,
        UnrealSharedObjectHandleV4 target,
        UnrealVector missStart,
        UnrealVector missEnd,
        ref double checksum)
    {
        for (int index = 0; index < WarmupIterations; index++)
        {
            checksum += world.GetWorldTimeSeconds(context.Target);
            _ = world.LineTraceSingle(
                context.Target,
                missStart,
                missEnd,
                default,
                out _,
                out _);
        }
        using (UnrealCommandBatch commands =
               context.DataPlane.BeginCommands(WarmupIterations))
        {
            FillMovementCommands(commands.Commands, target);
            checksum += commands.FirstSequenceId;
            commands.Commit();
        }
        using (UnrealTraceBatch traces =
               context.DataPlane.BeginTraceBatch(WarmupIterations))
        {
            FillTraceRequests(traces.Requests, missStart, missEnd);
            checksum += traces.Execute().Length;
        }
    }

    private static void MeasureBindingScalar(
        UnrealScriptContext context,
        UnifiedScriptWorldLibrary world,
        double[] samples,
        ref double checksum)
    {
        for (int sample = 0; sample < samples.Length; sample++)
        {
            long started = Stopwatch.GetTimestamp();
            for (int index = 0; index < MeasuredIterations; index++)
            {
                checksum += world.GetWorldTimeSeconds(context.Target);
            }
            samples[sample] =
                Stopwatch.GetElapsedTime(started).TotalNanoseconds /
                MeasuredIterations;
        }
    }

    private static void MeasureMovementCommands(
        UnrealScriptContext context,
        UnrealSharedObjectHandleV4 target,
        double[] samples,
        ref double checksum)
    {
        for (int sample = 0; sample < samples.Length; sample++)
        {
            long started = Stopwatch.GetTimestamp();
            using UnrealCommandBatch commands =
                context.DataPlane.BeginCommands(MeasuredIterations);
            FillMovementCommands(commands.Commands, target);
            checksum += commands.FirstSequenceId;
            commands.Commit();
            samples[sample] =
                Stopwatch.GetElapsedTime(started).TotalNanoseconds /
                MeasuredIterations;
        }
    }

    private static void MeasureBindingTrace(
        UnrealScriptContext context,
        UnifiedScriptWorldLibrary world,
        UnrealVector start,
        UnrealVector end,
        double[] samples,
        ref double checksum)
    {
        for (int sample = 0; sample < samples.Length; sample++)
        {
            int hits = 0;
            long started = Stopwatch.GetTimestamp();
            for (int index = 0; index < MeasuredIterations; index++)
            {
                if (world.LineTraceSingle(
                        context.Target,
                        start,
                        end,
                        default,
                        out _,
                        out _))
                {
                    hits++;
                }
            }
            samples[sample] =
                Stopwatch.GetElapsedTime(started).TotalNanoseconds /
                MeasuredIterations;
            checksum += hits;
        }
    }

    private static void MeasureBatchTrace(
        UnrealScriptContext context,
        UnrealVector start,
        UnrealVector end,
        double[] samples,
        ref double checksum)
    {
        for (int sample = 0; sample < samples.Length; sample++)
        {
            long started = Stopwatch.GetTimestamp();
            using UnrealTraceBatch traces =
                context.DataPlane.BeginTraceBatch(MeasuredIterations);
            FillTraceRequests(traces.Requests, start, end);
            ReadOnlySpan<UnrealTraceResultV4> results = traces.Execute();
            samples[sample] =
                Stopwatch.GetElapsedTime(started).TotalNanoseconds /
                MeasuredIterations;
            checksum += results.Length;
        }
    }

    private static void MeasureAttributeBinding(
        UnifiedScriptComponent component,
        double[] samples,
        ref double checksum)
    {
        for (int sample = 0; sample < samples.Length; sample++)
        {
            int total = 0;
            long started = Stopwatch.GetTimestamp();
            for (int index = 0; index < MeasuredIterations; index++)
            {
                total += component.MaterializedStateCount;
            }
            samples[sample] =
                Stopwatch.GetElapsedTime(started).TotalNanoseconds /
                MeasuredIterations;
            checksum += total;
        }
    }

    private static void VerifyOverlap(UnrealScriptContext context)
    {
        using UnrealOverlapBatch overlaps = context.DataPlane.BeginOverlapBatch(2);
        overlaps.Requests[0] = UnrealOverlapRequestV1.Sphere(
            UnrealVector.Zero,
            100.0,
            collisionChannel: 3);
        overlaps.Requests[1] = UnrealOverlapRequestV1.Box(
            UnrealVector.Zero,
            new UnrealVector(100.0, 100.0, 100.0),
            UnrealQuaternion.Identity,
            collisionChannel: 3);
        ReadOnlySpan<UnrealOverlapResultV1> results = overlaps.Execute();
        if (results.Length != 2 || !results[0].IsSuccess || !results[1].IsSuccess)
        {
            throw new InvalidOperationException("The Batch Overlap workload failed.");
        }
    }

    private static void FillMovementCommands(
        Span<UnrealCommandV4> commands,
        UnrealSharedObjectHandleV4 target)
    {
        for (int index = 0; index < commands.Length; index++)
        {
            commands[index] = UnrealCommandV4.SetActorTransform(
                target,
                new UnrealTransform(
                    UnrealQuaternion.Identity,
                    new UnrealVector(index & 7, 0.0, 0.0),
                    UnrealVector.One));
        }
    }

    private static void FillTraceRequests(
        Span<UnrealTraceRequestV4> requests,
        UnrealVector start,
        UnrealVector end)
    {
        for (int index = 0; index < requests.Length; index++)
        {
            requests[index] = UnrealTraceRequestV4.Line(
                start,
                end,
                collisionChannel: 3);
        }
    }

    private static double Median(double[] samples)
    {
        Array.Sort(samples);
        return samples[samples.Length / 2];
    }
}
