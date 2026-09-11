using System.Text.Json;
using Finare.Core;
using Finare.Runtime;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed class UnrealGameplayProfilerTests
{
    [Fact]
    public void SemanticDebugTickFeedsTheSameProfilerNodeTimeline()
    {
        var transport = new ProfilerTransport();
        var bridge = new NativeUnrealInvocationBridge(transport, worldHostId: 41);
        var profiler = new UnrealGameplayProfiler(41, 7, bridge, frameCapacity: 4);
        var script = new SemanticProfiledScript();
        using var controller = new UnrealWorldDebugController(41, 7);
        var generation = new CoreClrProgramGeneration(
            SemanticId.FromKey("profile-debug-domain"),
            1,
            SemanticId.FromKey("profile-debug-generation"),
            new ProgramImageKey($"sha256:{new string('c', 64)}"),
            SemanticId.FromKey("profile-debug-compilation"),
            SemanticId.FromKey("profile-debug-configuration"));
        string configuration = JsonSerializer.Serialize(
            new UnrealWorldDebugConfiguration(
                UnrealWorldDebugConfiguration.CurrentSchemaVersion,
                RuntimeGeneration: 3,
                generation,
                []),
            UnifiedJson.CreateOptions(writeIndented: false));
        controller.Configure(configuration, currentRuntimeGeneration: 3);
        profiler.SetEnabled(true);

        controller.BeginTick(
            runtimeGeneration: 3,
            deltaSeconds: 0.016f,
            value => profiler.ExecuteTick(
                script,
                3,
                typeof(SemanticProfiledScript).FullName!,
                value,
                attachProbe: false),
            static exception => exception.ToString(),
            profiler);

        Assert.True(SpinWait.SpinUntil(
            () => !controller.IsActive,
            TimeSpan.FromSeconds(5)));
        UnrealGameplayProfileTrace trace = JsonSerializer.Deserialize<
            UnrealGameplayProfileTrace>(
                profiler.ExportJson(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        UnrealGameplayProfileFrame frame = Assert.Single(trace.Frames);
        UnrealGameplayProfileNode node = Assert.Single(frame.Nodes);
        Assert.Equal(SemanticProfiledScript.MethodId.Value, node.MethodId);
        Assert.Equal(SemanticProfiledScript.NodeId.Value, node.NodeId);
    }

    [Fact]
    public void BoundedTraceAttributesFramesNodesReflectionAndAllocations()
    {
        var transport = new ProfilerTransport();
        var bridge = new NativeUnrealInvocationBridge(transport, worldHostId: 41);
        var profiler = new UnrealGameplayProfiler(41, 7, bridge, frameCapacity: 2);
        var script = new ProfiledScript(bridge);

        profiler.SetEnabled(true);
        profiler.ExecuteTick(script, 3, typeof(ProfiledScript).FullName!, 0.016f);
        profiler.ExecuteTick(script, 3, typeof(ProfiledScript).FullName!, 0.017f);
        profiler.ExecuteTick(script, 3, typeof(ProfiledScript).FullName!, 0.018f);

        UnrealGameplayProfileTrace trace = JsonSerializer.Deserialize<
            UnrealGameplayProfileTrace>(
                profiler.ExportJson(),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                })!;
        Assert.Equal(UnrealGameplayProfileTrace.CurrentSchemaVersion, trace.SchemaVersion);
        Assert.Equal("41", trace.WorldHostId);
        Assert.Equal("7", trace.InstanceId);
        Assert.Equal(2, trace.FrameCapacity);
        Assert.Equal(1, trace.DroppedFrameCount);
        Assert.Equal([2L, 3L], trace.Frames.Select(static frame => frame.Sequence));
        Assert.All(trace.Frames, frame =>
        {
            Assert.Equal(3, frame.Generation);
            Assert.True(frame.DurationNanoseconds > 0);
            Assert.True(frame.AllocatedBytes >= 0);
            Assert.Equal(3, frame.WorldPhases.Count);
            Assert.All(frame.WorldPhases, static phase =>
            {
                Assert.True(phase.Count > 0);
                Assert.True(phase.TotalNanoseconds > 0);
            });
            UnrealGameplayProfileBoundary reflection = Assert.Single(
                frame.Boundaries,
                static boundary => boundary.Name == "reflectionFallback");
            Assert.Equal(1, reflection.Count);
            Assert.True(reflection.TotalNanoseconds > 0);
            UnrealGameplayProfileBoundary direct = Assert.Single(
                frame.Boundaries,
                static boundary => boundary.Name == "directThunk");
            Assert.Equal(1, direct.Count);
            Assert.True(direct.TotalNanoseconds > 0);
            UnrealGameplayProfileNode node = Assert.Single(frame.Nodes);
            Assert.Equal("us_method", node.MethodId);
            Assert.Equal("us_node", node.NodeId);
            Assert.Equal(1, node.HitCount);
            Assert.Equal(1, Assert.Single(
                node.Boundaries,
                static boundary => boundary.Name == "reflectionFallback").Count);
            Assert.Equal(1, Assert.Single(
                node.Boundaries,
                static boundary => boundary.Name == "directThunk").Count);
        });
        Assert.Equal(2, trace.Aggregate.FrameCount);
        Assert.Equal(3, trace.Aggregate.WorldPhases.Count);
        Assert.Equal(2, Assert.Single(
            trace.Aggregate.Boundaries,
            static boundary => boundary.Name == "reflectionFallback").Count);
        Assert.Equal(2, Assert.Single(trace.Aggregate.Nodes).HitCount);
        Assert.Equal(2, Assert.Single(
            Assert.Single(trace.Aggregate.Nodes).Boundaries,
            static boundary => boundary.Name == "reflectionFallback").Count);

        profiler.SetEnabled(false);
        profiler.ExecuteTick(script, 3, typeof(ProfiledScript).FullName!, 0.019f);
        UnrealGameplayProfileTrace disabled = JsonSerializer.Deserialize<
            UnrealGameplayProfileTrace>(
                profiler.ExportJson(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Empty(disabled.Frames);
    }

    private sealed class ProfiledScript(NativeUnrealInvocationBridge bridge) : IUnrealManagedScript
    {
        private static readonly UnrealObjectHandle Target =
            UnrealObjectHandle.Create(slotIndex: 4, generation: 2);

        public void OnStart(UnrealScriptContext context)
        {
        }

        public void OnTick(float deltaSeconds)
        {
            using IDisposable frame = CoreClrDebugProbe.Enter("us_method");
            CoreClrDebugProbe.Hit("us_method", "us_node");
            _ = bridge.Invoke<int>(Target, "/Script/Fixture.Profile:Add", [1, 2]);
            _ = bridge.Invoke<int>(
                Target,
                0xA11DUL,
                "/Script/Fixture.Profile:Add",
                (IReadOnlyList<object?>)[1, 2]);
            _ = new byte[8];
        }

        public void OnStop()
        {
        }
    }

    private sealed class SemanticProfiledScript : IUnrealManagedScript
    {
        public static readonly SemanticId MethodId =
            SemanticId.FromKey("profiled-debug-method");
        public static readonly SemanticId NodeId =
            SemanticId.FromKey("profiled-debug-node");

        public void OnStart(UnrealScriptContext context)
        {
        }

        public void OnTick(float deltaSeconds)
        {
            using IDisposable frame = CoreClrDebugProbe.Enter(MethodId.Value);
            CoreClrDebugProbe.Hit(MethodId.Value, NodeId.Value);
        }

        public void OnStop()
        {
        }
    }

    private sealed class ProfilerTransport :
        IUnrealNativeInvocationTransport,
        IUnrealNativeFastInvocationTransport,
        IUnrealSharedSnapshotTransport,
        IUnrealDataPlaneTelemetryTransport,
        IUnrealGameplayDataTransport
    {
        private ulong telemetrySample;

        public bool SupportsSharedData => true;

        public bool SupportsGameplayData => true;

        public bool SupportsFastInvocation => true;

        public UnrealInvocationResponse ReadProperty(
            UnrealObjectHandle target,
            string propertyPath) =>
            UnrealInvocationResponse.Failed(
                UnrealInvocationError.PropertyNotFound,
                propertyPath);

        public UnrealInvocationResponse WriteProperty(
            UnrealObjectHandle target,
            string propertyPath,
            UnrealInvocationValue value) =>
            UnrealInvocationResponse.Failed(
                UnrealInvocationError.PropertyNotFound,
                propertyPath);

        public UnrealInvocationResponse Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<UnrealInvocationValue> arguments) =>
            UnrealInvocationResponse.Succeeded(UnrealInvocationValue.FromInt32(
                arguments.Sum(static argument => argument.Int32Value)));

        public UnrealInvocationResponse ReadPropertyById(
            UnrealObjectHandle target,
            ulong memberId) => UnrealInvocationResponse.Failed(
                UnrealInvocationError.PropertyNotFound,
                memberId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public UnrealInvocationResponse WritePropertyById(
            UnrealObjectHandle target,
            ulong memberId,
            UnrealInvocationValue value) => UnrealInvocationResponse.Failed(
                UnrealInvocationError.PropertyNotFound,
                memberId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public UnrealInvocationResponse InvokeById(
            UnrealObjectHandle target,
            ulong memberId,
            IReadOnlyList<UnrealInvocationValue> arguments) =>
            UnrealInvocationResponse.Succeeded(UnrealInvocationValue.FromInt32(
                arguments.Sum(static argument => argument.Int32Value)));

        public bool TryAcquireTransformSnapshot(out UnrealSharedSnapshotViewV4 view)
        {
            view = default;
            return false;
        }

        public bool TryGetSharedDataTelemetry(out UnrealSharedDataTelemetryV1 telemetry)
        {
            telemetry = default;
            return false;
        }

        public bool TryGetGameplayDataTelemetry(out UnrealGameplayDataTelemetryV1 telemetry)
        {
            ulong sample = ++telemetrySample;
            telemetry = new UnrealGameplayDataTelemetryV1
            {
                SchemaRevision = UnrealDataPlaneLayoutV1.Revision,
                StructSize = UnrealGameplayDataAbiV4.TelemetrySize,
                CompatibilityId = UnrealDataPlaneLayoutV1.CompatibilityId,
                WorldHostId = 41,
                FrameNumber = sample,
                MaximumCommandCount = UnrealGameplayDataAbiV4.MaximumCommandCount,
                MaximumEventCount = UnrealGameplayDataAbiV4.MaximumEventCount,
                MaximumTraceCount = UnrealGameplayDataAbiV4.MaximumTraceCount,
                MaximumOverlapCount = UnrealGameplayDataAbiV4.MaximumOverlapCount,
                Phase = (uint)UnrealGameplayDataPhaseV4.ManagedTick,
                NativePreActorTickCount = sample,
                NativePreActorTickNanoseconds = sample * 100,
                ManagedTickCount = sample,
                ManagedTickNanoseconds = sample * 200,
                NativePostActorTickCount = sample,
                NativePostActorTickNanoseconds = sample * 300,
            };
            return true;
        }

        public bool BeginCommandBatch(uint capacity, out UnrealCommandBatchViewV4 view)
        {
            view = default;
            return false;
        }

        public bool CommitCommandBatch(ulong token, uint count) => false;

        public bool AcquireEvents(out UnrealGameplayEventViewV4 view)
        {
            view = default;
            return false;
        }

        public bool BeginTraceBatch(uint capacity, out UnrealTraceRequestViewV4 view)
        {
            view = default;
            return false;
        }

        public bool ExecuteTraceBatch(
            ulong token,
            uint count,
            out UnrealTraceResultViewV4 view)
        {
            view = default;
            return false;
        }

        public void CancelTraceBatch(ulong token)
        {
        }

        public bool GetPhase(out UnrealGameplayDataPhaseV4 phase, out ulong frameNumber)
        {
            phase = UnrealGameplayDataPhaseV4.ManagedTick;
            frameNumber = telemetrySample;
            return true;
        }
    }
}
