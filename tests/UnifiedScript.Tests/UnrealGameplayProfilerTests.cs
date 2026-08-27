using System.Text.Json;
using UnifiedScript.Runtime;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealGameplayProfilerTests
{
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
            UnrealGameplayProfileBoundary reflection = Assert.Single(
                frame.Boundaries,
                static boundary => boundary.Name == "reflectionFallback");
            Assert.Equal(1, reflection.Count);
            Assert.True(reflection.TotalNanoseconds > 0);
            UnrealGameplayProfileNode node = Assert.Single(frame.Nodes);
            Assert.Equal("us_method", node.MethodId);
            Assert.Equal("us_node", node.NodeId);
            Assert.Equal(1, node.HitCount);
        });
        Assert.Equal(2, trace.Aggregate.FrameCount);
        Assert.Equal(2, Assert.Single(
            trace.Aggregate.Boundaries,
            static boundary => boundary.Name == "reflectionFallback").Count);
        Assert.Equal(2, Assert.Single(trace.Aggregate.Nodes).HitCount);

        profiler.SetEnabled(false);
        profiler.ExecuteTick(script, 3, typeof(ProfiledScript).FullName!, 0.019f);
        UnrealGameplayProfileTrace disabled = JsonSerializer.Deserialize<
            UnrealGameplayProfileTrace>(
                profiler.ExportJson(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Empty(disabled.Frames);
    }

    private sealed class ProfiledScript(IUnrealInvocationBridge bridge) : IUnrealManagedScript
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
            _ = new byte[8];
        }

        public void OnStop()
        {
        }
    }

    private sealed class ProfilerTransport : IUnrealNativeInvocationTransport
    {
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
    }
}
