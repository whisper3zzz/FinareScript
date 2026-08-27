using System.Diagnostics;
using System.Text.Json;
using UnifiedScript.Runtime;

namespace UnifiedScript.Unreal;

public sealed record UnrealGameplayProfileBoundary(
    string Name,
    long Count,
    long TotalNanoseconds);

public sealed record UnrealGameplayProfileNode(
    string MethodId,
    string NodeId,
    long HitCount,
    long TotalNanoseconds,
    IReadOnlyList<UnrealGameplayProfileBoundary> Boundaries);

public sealed record UnrealGameplayProfileBufferPressure(
    long CommandOverflowCount,
    long EventOverflowCount,
    long TraceOverflowCount,
    long OverlapOverflowCount);

public sealed record UnrealGameplayProfileFrame(
    long Sequence,
    string WorldFrameNumber,
    int Generation,
    string ScriptType,
    float DeltaSeconds,
    string WorldPhase,
    IReadOnlyList<UnrealGameplayProfileBoundary> WorldPhases,
    long DurationNanoseconds,
    long AllocatedBytes,
    IReadOnlyList<UnrealGameplayProfileBoundary> Boundaries,
    IReadOnlyList<UnrealGameplayProfileNode> Nodes,
    UnrealGameplayProfileBufferPressure BufferPressure);

public sealed record UnrealGameplayProfileAggregate(
    long FrameCount,
    long TotalNanoseconds,
    long AllocatedBytes,
    IReadOnlyList<UnrealGameplayProfileBoundary> WorldPhases,
    IReadOnlyList<UnrealGameplayProfileBoundary> Boundaries,
    IReadOnlyList<UnrealGameplayProfileNode> Nodes,
    UnrealGameplayProfileBufferPressure BufferPressure);

public sealed record UnrealGameplayProfileTrace(
    string SchemaVersion,
    string TraceId,
    DateTimeOffset CapturedAtUtc,
    string WorldHostId,
    string InstanceId,
    int FrameCapacity,
    long DroppedFrameCount,
    IReadOnlyList<UnrealGameplayProfileFrame> Frames,
    UnrealGameplayProfileAggregate Aggregate)
{
    public const string CurrentSchemaVersion = "unifiedscript.gameplay-profile/v0.3";
}

internal readonly record struct UnrealInvocationProfilerSnapshot(
    long DirectThunkCount,
    long DirectThunkStopwatchTicks,
    long CallFrameCount,
    long CallFrameStopwatchTicks,
    long ReflectionFallbackCount,
    long ReflectionFallbackStopwatchTicks,
    long CommandCount,
    long CommandStopwatchTicks,
    long EventCount,
    long EventStopwatchTicks,
    long TraceCount,
    long TraceStopwatchTicks,
    long OverlapCount,
    long OverlapStopwatchTicks)
{
    public static UnrealInvocationProfilerSnapshot operator -(
        UnrealInvocationProfilerSnapshot right,
        UnrealInvocationProfilerSnapshot left) => new(
            Math.Max(0, right.DirectThunkCount - left.DirectThunkCount),
            Math.Max(0, right.DirectThunkStopwatchTicks - left.DirectThunkStopwatchTicks),
            Math.Max(0, right.CallFrameCount - left.CallFrameCount),
            Math.Max(0, right.CallFrameStopwatchTicks - left.CallFrameStopwatchTicks),
            Math.Max(0, right.ReflectionFallbackCount - left.ReflectionFallbackCount),
            Math.Max(0, right.ReflectionFallbackStopwatchTicks - left.ReflectionFallbackStopwatchTicks),
            Math.Max(0, right.CommandCount - left.CommandCount),
            Math.Max(0, right.CommandStopwatchTicks - left.CommandStopwatchTicks),
            Math.Max(0, right.EventCount - left.EventCount),
            Math.Max(0, right.EventStopwatchTicks - left.EventStopwatchTicks),
            Math.Max(0, right.TraceCount - left.TraceCount),
            Math.Max(0, right.TraceStopwatchTicks - left.TraceStopwatchTicks),
            Math.Max(0, right.OverlapCount - left.OverlapCount),
            Math.Max(0, right.OverlapStopwatchTicks - left.OverlapStopwatchTicks));

    public static UnrealInvocationProfilerSnapshot operator +(
        UnrealInvocationProfilerSnapshot left,
        UnrealInvocationProfilerSnapshot right) => new(
            Add(left.DirectThunkCount, right.DirectThunkCount),
            Add(left.DirectThunkStopwatchTicks, right.DirectThunkStopwatchTicks),
            Add(left.CallFrameCount, right.CallFrameCount),
            Add(left.CallFrameStopwatchTicks, right.CallFrameStopwatchTicks),
            Add(left.ReflectionFallbackCount, right.ReflectionFallbackCount),
            Add(left.ReflectionFallbackStopwatchTicks, right.ReflectionFallbackStopwatchTicks),
            Add(left.CommandCount, right.CommandCount),
            Add(left.CommandStopwatchTicks, right.CommandStopwatchTicks),
            Add(left.EventCount, right.EventCount),
            Add(left.EventStopwatchTicks, right.EventStopwatchTicks),
            Add(left.TraceCount, right.TraceCount),
            Add(left.TraceStopwatchTicks, right.TraceStopwatchTicks),
            Add(left.OverlapCount, right.OverlapCount),
            Add(left.OverlapStopwatchTicks, right.OverlapStopwatchTicks));

    private static long Add(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
}

internal interface IUnrealInvocationProfiler
{
    void SetProfilerEnabled(bool enabled);

    UnrealInvocationProfilerSnapshot GetProfilerSnapshot();
}

internal readonly record struct UnrealGameplayTransportProfilerSnapshot(
    long CommandCount,
    long CommandStopwatchTicks,
    long EventCount,
    long EventStopwatchTicks,
    long TraceCount,
    long TraceStopwatchTicks,
    long OverlapCount,
    long OverlapStopwatchTicks);

internal interface IUnrealGameplayTransportProfiler
{
    void SetProfilerEnabled(bool enabled);

    UnrealGameplayTransportProfilerSnapshot GetProfilerSnapshot();
}

internal sealed class UnrealGameplayProfiler : ICoreClrDebugProbeSink
{
    internal const int DefaultFrameCapacity = 120;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly object gate = new();
    private readonly ulong worldHostId;
    private readonly ulong instanceId;
    private readonly NativeUnrealInvocationBridge bridge;
    private readonly UnrealGameplayProfileFrame?[] frames;
    private ActiveFrame? activeFrame;
    private long nextSequence;
    private long droppedFrameCount;
    private int nextFrameIndex;
    private int frameCount;
    private bool enabled;
    private UnrealGameplayDataTelemetryV1? lastWorldPhaseTelemetry;

    public UnrealGameplayProfiler(
        ulong worldHostId,
        ulong instanceId,
        NativeUnrealInvocationBridge bridge,
        int frameCapacity = DefaultFrameCapacity)
    {
        if (worldHostId == 0 || instanceId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(worldHostId),
                "A profiler requires live World and script instance identities.");
        }
        if (frameCapacity is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCapacity));
        }
        this.worldHostId = worldHostId;
        this.instanceId = instanceId;
        this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        frames = new UnrealGameplayProfileFrame[frameCapacity];
    }

    public bool Enabled
    {
        get
        {
            lock (gate)
            {
                return enabled;
            }
        }
    }

    public void SetEnabled(bool value)
    {
        UnrealGameplayDataTelemetryV1? phaseBaseline = value
            ? TryGetGameplayTelemetry()
            : null;
        lock (gate)
        {
            if (enabled == value)
            {
                return;
            }
            enabled = value;
            activeFrame = null;
            Array.Clear(frames);
            nextSequence = 0;
            droppedFrameCount = 0;
            nextFrameIndex = 0;
            frameCount = 0;
            lastWorldPhaseTelemetry = phaseBaseline;
        }
        bridge.SetProfilerEnabled(value);
    }

    public void ExecuteTick(
        IUnrealManagedScript script,
        int generation,
        string scriptType,
        float deltaSeconds,
        bool attachProbe = true)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (!Enabled)
        {
            script.OnTick(deltaSeconds);
            return;
        }

        IDisposable? attachment = attachProbe ? CoreClrDebugProbe.Attach(this) : null;
        try
        {
            UnrealInvocationProfilerSnapshot before = bridge.GetProfilerSnapshot();
            UnrealGameplayDataTelemetryV1? telemetryBefore = TryGetGameplayTelemetry();
            long started = Stopwatch.GetTimestamp();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var capture = new ActiveFrame(started, before);
            lock (gate)
            {
                activeFrame = capture;
            }
            try
            {
                script.OnTick(deltaSeconds);
            }
            finally
            {
                long finished = Stopwatch.GetTimestamp();
                long allocated = Math.Max(
                    0,
                    GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                UnrealInvocationProfilerSnapshot after = bridge.GetProfilerSnapshot();
                UnrealInvocationProfilerSnapshot boundaries = after - before;
                UnrealGameplayDataTelemetryV1? telemetryAfter = TryGetGameplayTelemetry();
                lock (gate)
                {
                    capture.CloseNode(finished, after);
                    if (enabled && ReferenceEquals(activeFrame, capture))
                    {
                        activeFrame = null;
                        UnrealGameplayDataTelemetryV1? phaseBaseline =
                            lastWorldPhaseTelemetry;
                        lastWorldPhaseTelemetry = telemetryAfter ?? telemetryBefore ??
                            lastWorldPhaseTelemetry;
                        AppendFrame(BuildFrame(
                            capture,
                            generation,
                            scriptType,
                            deltaSeconds,
                            finished,
                            allocated,
                            boundaries,
                            phaseBaseline,
                            telemetryBefore,
                            telemetryAfter));
                    }
                }
            }
        }
        finally
        {
            attachment?.Dispose();
        }
    }

    public string ExportJson()
    {
        UnrealGameplayProfileFrame[] snapshot;
        long dropped;
        lock (gate)
        {
            snapshot = SnapshotFrames();
            dropped = droppedFrameCount;
        }
        UnrealGameplayProfileAggregate aggregate = BuildAggregate(snapshot);
        var trace = new UnrealGameplayProfileTrace(
            UnrealGameplayProfileTrace.CurrentSchemaVersion,
            $"world-{worldHostId}-script-{instanceId}",
            DateTimeOffset.UtcNow,
            worldHostId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            instanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            frames.Length,
            dropped,
            snapshot,
            aggregate);
        return JsonSerializer.Serialize(trace, JsonOptions);
    }

    void ICoreClrDebugProbeSink.Hit(
        string methodId,
        string nodeId,
        IReadOnlyList<CoreClrDebugValue> locals,
        IReadOnlyList<CoreClrDebugFrameSnapshot> frames)
    {
        _ = locals;
        _ = frames;
        if (string.IsNullOrWhiteSpace(methodId) || string.IsNullOrWhiteSpace(nodeId))
        {
            return;
        }
        UnrealInvocationProfilerSnapshot boundaries = bridge.GetProfilerSnapshot();
        lock (gate)
        {
            activeFrame?.Hit(
                methodId,
                nodeId,
                Stopwatch.GetTimestamp(),
                boundaries);
        }
    }

    private UnrealGameplayDataTelemetryV1? TryGetGameplayTelemetry()
    {
        try
        {
            return bridge.DataPlane.SupportsGameplayData
                ? bridge.DataPlane.GetGameplayDataTelemetry()
                : null;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or
            ObjectDisposedException)
        {
            return null;
        }
    }

    private UnrealGameplayProfileFrame BuildFrame(
        ActiveFrame capture,
        int generation,
        string scriptType,
        float deltaSeconds,
        long finished,
        long allocatedBytes,
        UnrealInvocationProfilerSnapshot boundaries,
        UnrealGameplayDataTelemetryV1? phaseBaseline,
        UnrealGameplayDataTelemetryV1? telemetryBefore,
        UnrealGameplayDataTelemetryV1? telemetryAfter)
    {
        ulong worldFrame = telemetryAfter?.FrameNumber ?? telemetryBefore?.FrameNumber ?? 0;
        UnrealGameplayDataTelemetryV1? currentTelemetry = telemetryAfter ?? telemetryBefore;
        uint? phase = currentTelemetry?.Phase;
        return new UnrealGameplayProfileFrame(
            checked(++nextSequence),
            worldFrame.ToString(System.Globalization.CultureInfo.InvariantCulture),
            generation,
            scriptType,
            deltaSeconds,
            phase.HasValue && Enum.IsDefined((UnrealGameplayDataPhaseV4)phase.Value)
                ? ((UnrealGameplayDataPhaseV4)phase.Value).ToString()
                : "Unknown",
            BuildWorldPhases(phaseBaseline, currentTelemetry),
            StopwatchTicksToNanoseconds(Math.Max(0, finished - capture.Started)),
            allocatedBytes,
            BuildBoundaries(boundaries),
            capture.Nodes
                .OrderByDescending(static pair => pair.Value.StopwatchTicks)
                .ThenBy(static pair => pair.Key.MethodId, StringComparer.Ordinal)
                .ThenBy(static pair => pair.Key.NodeId, StringComparer.Ordinal)
                .Select(pair => new UnrealGameplayProfileNode(
                    pair.Key.MethodId,
                    pair.Key.NodeId,
                    pair.Value.HitCount,
                    StopwatchTicksToNanoseconds(pair.Value.StopwatchTicks),
                    BuildBoundaries(pair.Value.Boundaries)))
                .ToArray(),
            BuildPressure(telemetryBefore, telemetryAfter));
    }

    private static IReadOnlyList<UnrealGameplayProfileBoundary> BuildWorldPhases(
        UnrealGameplayDataTelemetryV1? before,
        UnrealGameplayDataTelemetryV1? after)
    {
        if (before is not UnrealGameplayDataTelemetryV1 left ||
            after is not UnrealGameplayDataTelemetryV1 right ||
            left.StructSize < UnrealGameplayDataAbiV4.TelemetrySize ||
            right.StructSize < UnrealGameplayDataAbiV4.TelemetrySize)
        {
            return [];
        }
        return
        [
            new("NativePreActorTick",
                Delta(right.NativePreActorTickCount, left.NativePreActorTickCount),
                Delta(right.NativePreActorTickNanoseconds, left.NativePreActorTickNanoseconds)),
            new("ManagedTick",
                Delta(right.ManagedTickCount, left.ManagedTickCount),
                Delta(right.ManagedTickNanoseconds, left.ManagedTickNanoseconds)),
            new("NativePostActorTick",
                Delta(right.NativePostActorTickCount, left.NativePostActorTickCount),
                Delta(right.NativePostActorTickNanoseconds, left.NativePostActorTickNanoseconds)),
        ];
    }

    private static IReadOnlyList<UnrealGameplayProfileBoundary> BuildBoundaries(
        UnrealInvocationProfilerSnapshot value) =>
    [
        new("directThunk", value.DirectThunkCount,
            StopwatchTicksToNanoseconds(value.DirectThunkStopwatchTicks)),
        new("callFrame", value.CallFrameCount,
            StopwatchTicksToNanoseconds(value.CallFrameStopwatchTicks)),
        new("reflectionFallback", value.ReflectionFallbackCount,
            StopwatchTicksToNanoseconds(value.ReflectionFallbackStopwatchTicks)),
        new("commands", value.CommandCount,
            StopwatchTicksToNanoseconds(value.CommandStopwatchTicks)),
        new("events", value.EventCount,
            StopwatchTicksToNanoseconds(value.EventStopwatchTicks)),
        new("batchTrace", value.TraceCount,
            StopwatchTicksToNanoseconds(value.TraceStopwatchTicks)),
        new("batchOverlap", value.OverlapCount,
            StopwatchTicksToNanoseconds(value.OverlapStopwatchTicks)),
    ];

    private static UnrealGameplayProfileBufferPressure BuildPressure(
        UnrealGameplayDataTelemetryV1? before,
        UnrealGameplayDataTelemetryV1? after)
    {
        if (before is not UnrealGameplayDataTelemetryV1 left ||
            after is not UnrealGameplayDataTelemetryV1 right)
        {
            return new(0, 0, 0, 0);
        }
        return new(
            Delta(right.CommandOverflowCount, left.CommandOverflowCount),
            Delta(right.EventOverflowCount, left.EventOverflowCount),
            Delta(right.TraceOverflowCount, left.TraceOverflowCount),
            Delta(right.OverlapOverflowCount, left.OverlapOverflowCount));
    }

    private void AppendFrame(UnrealGameplayProfileFrame frame)
    {
        if (frameCount == frames.Length)
        {
            droppedFrameCount++;
        }
        else
        {
            frameCount++;
        }
        frames[nextFrameIndex] = frame;
        nextFrameIndex = (nextFrameIndex + 1) % frames.Length;
    }

    private UnrealGameplayProfileFrame[] SnapshotFrames()
    {
        var result = new UnrealGameplayProfileFrame[frameCount];
        int first = frameCount == frames.Length ? nextFrameIndex : 0;
        for (int index = 0; index < frameCount; index++)
        {
            result[index] = frames[(first + index) % frames.Length] ??
                throw new InvalidOperationException("The profiler ring contains an empty frame.");
        }
        return result;
    }

    private static UnrealGameplayProfileAggregate BuildAggregate(
        IReadOnlyList<UnrealGameplayProfileFrame> frames)
    {
        var boundaries = new Dictionary<string, (long Count, long Nanoseconds)>(
            StringComparer.Ordinal);
        var worldPhases = new Dictionary<string, (long Count, long Nanoseconds)>(
            StringComparer.Ordinal);
        var nodes = new Dictionary<(string MethodId, string NodeId), (long Hits, long Nanoseconds)>();
        var nodeBoundaries = new Dictionary<
            (string MethodId, string NodeId),
            Dictionary<string, (long Count, long Nanoseconds)>>();
        long duration = 0;
        long allocated = 0;
        long commandOverflow = 0;
        long eventOverflow = 0;
        long traceOverflow = 0;
        long overlapOverflow = 0;
        foreach (UnrealGameplayProfileFrame frame in frames)
        {
            duration = SaturatingAdd(duration, frame.DurationNanoseconds);
            allocated = SaturatingAdd(allocated, frame.AllocatedBytes);
            foreach (UnrealGameplayProfileBoundary phase in frame.WorldPhases)
            {
                worldPhases.TryGetValue(phase.Name, out var current);
                worldPhases[phase.Name] = (
                    SaturatingAdd(current.Count, phase.Count),
                    SaturatingAdd(current.Nanoseconds, phase.TotalNanoseconds));
            }
            foreach (UnrealGameplayProfileBoundary boundary in frame.Boundaries)
            {
                boundaries.TryGetValue(boundary.Name, out var current);
                boundaries[boundary.Name] = (
                    SaturatingAdd(current.Count, boundary.Count),
                    SaturatingAdd(current.Nanoseconds, boundary.TotalNanoseconds));
            }
            foreach (UnrealGameplayProfileNode node in frame.Nodes)
            {
                var key = (node.MethodId, node.NodeId);
                nodes.TryGetValue(key, out var current);
                nodes[key] = (
                    SaturatingAdd(current.Hits, node.HitCount),
                    SaturatingAdd(current.Nanoseconds, node.TotalNanoseconds));
                foreach (UnrealGameplayProfileBoundary boundary in node.Boundaries)
                {
                    if (!nodeBoundaries.TryGetValue(key, out var byName))
                    {
                        byName = new Dictionary<string, (long Count, long Nanoseconds)>(
                            StringComparer.Ordinal);
                        nodeBoundaries.Add(key, byName);
                    }
                    byName.TryGetValue(boundary.Name, out var boundaryCurrent);
                    byName[boundary.Name] = (
                        SaturatingAdd(boundaryCurrent.Count, boundary.Count),
                        SaturatingAdd(
                            boundaryCurrent.Nanoseconds,
                            boundary.TotalNanoseconds));
                }
            }
            commandOverflow = SaturatingAdd(
                commandOverflow,
                frame.BufferPressure.CommandOverflowCount);
            eventOverflow = SaturatingAdd(
                eventOverflow,
                frame.BufferPressure.EventOverflowCount);
            traceOverflow = SaturatingAdd(
                traceOverflow,
                frame.BufferPressure.TraceOverflowCount);
            overlapOverflow = SaturatingAdd(
                overlapOverflow,
                frame.BufferPressure.OverlapOverflowCount);
        }
        return new UnrealGameplayProfileAggregate(
            frames.Count,
            duration,
            allocated,
            worldPhases
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new UnrealGameplayProfileBoundary(
                    pair.Key,
                    pair.Value.Count,
                    pair.Value.Nanoseconds))
                .ToArray(),
            boundaries
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new UnrealGameplayProfileBoundary(
                    pair.Key,
                    pair.Value.Count,
                    pair.Value.Nanoseconds))
                .ToArray(),
            nodes
                .OrderByDescending(static pair => pair.Value.Nanoseconds)
                .ThenBy(static pair => pair.Key.MethodId, StringComparer.Ordinal)
                .ThenBy(static pair => pair.Key.NodeId, StringComparer.Ordinal)
                .Select(pair =>
                {
                    IReadOnlyList<UnrealGameplayProfileBoundary> aggregatedBoundaries =
                        nodeBoundaries.TryGetValue(pair.Key, out var byName)
                            ? byName
                                .OrderBy(static boundary => boundary.Key, StringComparer.Ordinal)
                                .Select(static boundary => new UnrealGameplayProfileBoundary(
                                    boundary.Key,
                                    boundary.Value.Count,
                                    boundary.Value.Nanoseconds))
                                .ToArray()
                            : [];
                    return new UnrealGameplayProfileNode(
                        pair.Key.MethodId,
                        pair.Key.NodeId,
                        pair.Value.Hits,
                        pair.Value.Nanoseconds,
                        aggregatedBoundaries);
                })
                .ToArray(),
            new UnrealGameplayProfileBufferPressure(
                commandOverflow,
                eventOverflow,
                traceOverflow,
                overlapOverflow));
    }

    private static long Delta(ulong right, ulong left) =>
        right >= left && right - left <= long.MaxValue
            ? checked((long)(right - left))
            : 0;

    private static long StopwatchTicksToNanoseconds(long ticks) =>
        ticks <= 0
            ? 0
            : (long)Math.Min(
                long.MaxValue,
                Math.Round(ticks * (1_000_000_000d / Stopwatch.Frequency)));

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed class ActiveFrame(
        long started,
        UnrealInvocationProfilerSnapshot initialBoundaries)
    {
        private (string MethodId, string NodeId)? currentNode;
        private long nodeStarted = started;
        private UnrealInvocationProfilerSnapshot nodeBoundaryStarted = initialBoundaries;

        public long Started { get; } = started;

        public Dictionary<(string MethodId, string NodeId), NodeCounter> Nodes { get; } = [];

        public void Hit(
            string methodId,
            string nodeId,
            long timestamp,
            UnrealInvocationProfilerSnapshot boundaries)
        {
            CloseNode(timestamp, boundaries);
            currentNode = (methodId, nodeId);
            nodeStarted = timestamp;
            nodeBoundaryStarted = boundaries;
            Nodes.TryGetValue(currentNode.Value, out NodeCounter current);
            Nodes[currentNode.Value] = current with
            {
                HitCount = SaturatingAdd(current.HitCount, 1),
            };
        }

        public void CloseNode(
            long timestamp,
            UnrealInvocationProfilerSnapshot boundaries)
        {
            if (currentNode is not { } key)
            {
                return;
            }
            Nodes.TryGetValue(key, out NodeCounter current);
            Nodes[key] = current with
            {
                StopwatchTicks = SaturatingAdd(
                    current.StopwatchTicks,
                    Math.Max(0, timestamp - nodeStarted)),
                Boundaries = current.Boundaries + (boundaries - nodeBoundaryStarted),
            };
            currentNode = null;
            nodeStarted = timestamp;
            nodeBoundaryStarted = boundaries;
        }
    }

    private readonly record struct NodeCounter(
        long HitCount,
        long StopwatchTicks,
        UnrealInvocationProfilerSnapshot Boundaries);
}
