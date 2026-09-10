using System;
using System.Diagnostics;
using System.Globalization;
using Finare.Unreal.Generated;
using Finare.Unreal.Extensions.BlockBreaker;

namespace Finare.Unreal.Demo;

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
            !context.DataPlane.SupportsOverlapQueries ||
            !context.DataPlane.SupportsGeneratedSnapshots ||
            !context.DataPlane.SupportsGeneratedEvents)
        {
            throw new InvalidOperationException(
                "The schema-generated gameplay data plane is unavailable.");
        }

        var component = new FinareComponent(context.Host, context.Bridge);
        var world = new FinareWorldLibrary(context.Target, context.Bridge);
        if (context.DataPlane.GameplayFrameNumber == 0)
        {
            throw new InvalidOperationException("The World frame is invalid.");
        }
        UnrealSharedObjectHandleV4 target = context.DataPlane.Scope(context.Target);
        var missStart = new UnrealVector(100_000.0, 100_000.0, 100_000.0);
        var missEnd = new UnrealVector(100_100.0, 100_000.0, 100_000.0);

        double checksum = 0.0;
        WarmUp(context, world, target, missStart, missEnd, ref checksum);
        int[] containerValues = [1, 2, 3, 4, 5, 6, 7, 8];
        var containerPairs = new Dictionary<int, int>
        {
            [1] = 10,
            [2] = 20,
            [3] = 30,
            [4] = 40,
        };
        checksum += component.EchoBenchmarkArray(containerValues)[0];
        checksum += component.EchoBenchmarkMap(containerPairs)[1];
        checksum += context.DataPlane.AcquireGeneratedSnapshot<
            UnrealFixedValueCollectionSnapshotV1>(
                UnrealSharedDataAbiV4.FixedValueCollectionSchemaId).Entries[0].Value0;
        checksum += context.DataPlane.AcquireGeneratedEvents<
            UnrealBufferedGameplayEventV1>(
                UnrealGameplayDataAbiV4.BufferedGameplayEventSchemaId).Entries[0].Value;
        UnrealGeneratedDataSnapshot<BlockBreakerTargetSnapshotV1> targetSnapshots =
            context.DataPlane.AcquireGeneratedSnapshot(
                BlockBreakerDataPlaneExtension.TargetSnapshots);
        UnrealGeneratedDataSnapshot<BlockBreakerImpactEventV1> impactEvents =
            context.DataPlane.AcquireGeneratedEvents(
                BlockBreakerDataPlaneExtension.ImpactEvents);
        if (targetSnapshots.Entries.Length != MeasuredIterations ||
            impactEvents.Entries.Length != MeasuredIterations)
        {
            throw new InvalidOperationException(
                "The generated BlockBreaker extension did not publish its bounded workload.");
        }
        checksum += targetSnapshots.Entries[0].Threat;
        checksum += impactEvents.Entries[0].Damage;

        var bindingScalarSamples = new double[SampleCount];
        var commandSamples = new double[SampleCount];
        var bindingTraceSamples = new double[SampleCount];
        var batchTraceSamples = new double[SampleCount];
        var attributeSamples = new double[SampleCount];
        var bindingContainerSamples = new double[SampleCount];
        var generatedSnapshotSamples = new double[SampleCount];
        var generatedEventSamples = new double[SampleCount];

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
        int batchCollectionsBefore = GC.CollectionCount(0) +
            GC.CollectionCount(1) + GC.CollectionCount(2);
        TimeSpan batchPauseBefore = GC.GetTotalPauseDuration();
        long batchAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        MeasureBatchTrace(
            context,
            missStart,
            missEnd,
            batchTraceSamples,
            ref checksum);
        long batchAllocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - batchAllocatedBefore;
        int batchGcCollectionCount = GC.CollectionCount(0) +
            GC.CollectionCount(1) + GC.CollectionCount(2) - batchCollectionsBefore;
        double batchGcPauseMilliseconds =
            (GC.GetTotalPauseDuration() - batchPauseBefore).TotalMilliseconds;
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
        MeasureBindingContainers(
            component,
            containerValues,
            containerPairs,
            bindingContainerSamples,
            ref checksum);
        long generatedAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        MeasureGeneratedSnapshot(
            context,
            generatedSnapshotSamples,
            ref checksum);
        MeasureGeneratedEvents(
            context,
            generatedEventSamples,
            ref checksum);
        long generatedDataAllocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - generatedAllocatedBefore;
        VerifyOverlap(context);

        double bindingScalarNs = Median(bindingScalarSamples);
        double commandSubmitNs = Median(commandSamples);
        double bindingTraceNs = Median(bindingTraceSamples);
        double batchTraceNs = Median(batchTraceSamples);
        double attributeReadNs = Median(attributeSamples);
        double bindingContainerNs = Median(bindingContainerSamples);
        double generatedSnapshotNs = Median(generatedSnapshotSamples);
        double generatedEventNs = Median(generatedEventSamples);
        long batchTraceBytes = checked((long)(
            UnrealGameplayDataAbiV4.TraceRequestEntrySize +
            UnrealGameplayDataAbiV4.TraceResultEntrySize) * MeasuredIterations);
        component.ManagedState = string.Create(
            CultureInfo.InvariantCulture,
            $"DataPlaneProfile:v3;Iterations={MeasuredIterations};SampleCount={SampleCount};" +
            $"BindingScalarNs={bindingScalarNs:F3};CommandSubmitNs={commandSubmitNs:F3};" +
            $"BindingTraceNs={bindingTraceNs:F3};BatchTraceNs={batchTraceNs:F3};" +
            $"AttributeReadNs={attributeReadNs:F3};DataPlaneAllocatedBytes={dataPlaneAllocatedBytes};" +
            $"BatchTraceAllocatedBytes={batchAllocatedBytes};BatchTraceBytes={batchTraceBytes};" +
            $"BatchTraceGcCollectionCount={batchGcCollectionCount};" +
            $"BatchTraceGcPauseMilliseconds={batchGcPauseMilliseconds:F6};" +
            $"BindingContainerNs={bindingContainerNs:F3};" +
            $"GeneratedSnapshotNs={generatedSnapshotNs:F3};" +
            $"GeneratedEventNs={generatedEventNs:F3};" +
            $"GeneratedDataAllocatedBytes={generatedDataAllocatedBytes};" +
            $"GeneratedSnapshotEntries={MeasuredIterations};" +
            $"GeneratedEventEntries={MeasuredIterations};" +
            $"ExtensionSnapshotEntries={targetSnapshots.Entries.Length};" +
            $"ExtensionEventEntries={impactEvents.Entries.Length};" +
            $"ExtensionFingerprint=0x{BlockBreakerDataPlaneExtension.Identity.SchemaFingerprint:X16};" +
            $"Movement=data-plane;Overlap=data-plane;Attribute=binding;" +
            $"Spawn=hybrid;Query=data-plane;Container=generated-snapshot;" +
            $"Delegate=generated-events;Checksum={checksum:F3}");
    }

    public void OnTick(float deltaSeconds)
    {
    }

    public void OnStop()
    {
    }

    private static void WarmUp(
        UnrealScriptContext context,
        FinareWorldLibrary world,
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
        FinareWorldLibrary world,
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
        FinareWorldLibrary world,
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
        FinareComponent component,
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

    private static void MeasureBindingContainers(
        FinareComponent component,
        IReadOnlyList<int> values,
        IReadOnlyDictionary<int, int> pairs,
        double[] samples,
        ref double checksum)
    {
        for (int sample = 0; sample < samples.Length; sample++)
        {
            long started = Stopwatch.GetTimestamp();
            int total = 0;
            for (int index = 0; index < MeasuredIterations; index++)
            {
                IReadOnlyList<int> echoedValues = component.EchoBenchmarkArray(values);
                IReadOnlyDictionary<int, int> echoedPairs =
                    component.EchoBenchmarkMap(pairs);
                total += echoedValues[7] + echoedPairs[4];
            }
            samples[sample] =
                Stopwatch.GetElapsedTime(started).TotalNanoseconds /
                MeasuredIterations;
            checksum += total;
        }
    }

    private static void MeasureGeneratedSnapshot(
        UnrealScriptContext context,
        double[] samples,
        ref double checksum)
    {
        for (int sample = 0; sample < samples.Length; sample++)
        {
            long started = Stopwatch.GetTimestamp();
            UnrealGeneratedDataSnapshot<UnrealFixedValueCollectionSnapshotV1> snapshot =
                context.DataPlane.AcquireGeneratedSnapshot<
                    UnrealFixedValueCollectionSnapshotV1>(
                        UnrealSharedDataAbiV4.FixedValueCollectionSchemaId);
            long total = 0;
            foreach (ref readonly UnrealFixedValueCollectionSnapshotV1 row in
                     snapshot.Entries)
            {
                total += row.Value0 + row.Value1 + row.Value2 + row.Value3 +
                    row.Value4 + row.Value5 + row.Value6 + row.Value7 +
                    row.MapValue0 + row.MapValue1 + row.MapValue2 + row.MapValue3;
            }
            samples[sample] =
                Stopwatch.GetElapsedTime(started).TotalNanoseconds /
                snapshot.Entries.Length;
            checksum += total;
        }
    }

    private static void MeasureGeneratedEvents(
        UnrealScriptContext context,
        double[] samples,
        ref double checksum)
    {
        for (int sample = 0; sample < samples.Length; sample++)
        {
            long started = Stopwatch.GetTimestamp();
            UnrealGeneratedDataSnapshot<UnrealBufferedGameplayEventV1> events =
                context.DataPlane.AcquireGeneratedEvents<
                    UnrealBufferedGameplayEventV1>(
                        UnrealGameplayDataAbiV4.BufferedGameplayEventSchemaId);
            double total = 0.0;
            foreach (ref readonly UnrealBufferedGameplayEventV1 row in events.Entries)
            {
                total += row.Value + row.Weight;
            }
            samples[sample] =
                Stopwatch.GetElapsedTime(started).TotalNanoseconds /
                events.Entries.Length;
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
