using System.Text.Json;
using Finare.Core;
using Finare.Runtime;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed class UnrealWorldDebugSessionTests
{
    private static readonly SemanticId OuterMethod = SemanticId.FromKey("world-debug-outer-method");
    private static readonly SemanticId OuterNode = SemanticId.FromKey("world-debug-outer-node");
    private static readonly SemanticId ChildMethod = SemanticId.FromKey("world-debug-child-method");
    private static readonly SemanticId ChildNode = SemanticId.FromKey("world-debug-child-node");
    private static readonly SemanticId AfterNode = SemanticId.FromKey("world-debug-after-node");

    [Fact]
    public void LiveWorldProbe_PreservesWorkerStackAndStepsWithoutBlockingCaller()
    {
        using var controller = new UnrealWorldDebugController(worldHostId: 41, instanceId: 73);
        controller.Configure(CreateConfigurationJson([OuterNode]), currentRuntimeGeneration: 3);
        bool completed = false;

        controller.BeginTick(
            runtimeGeneration: 3,
            deltaSeconds: 0.016f,
            _ =>
            {
                using IDisposable outer = CoreClrDebugProbe.Enter(OuterMethod.Value);
                CoreClrDebugProbe.Hit(
                    OuterMethod.Value,
                    OuterNode.Value,
                    CoreClrDebugProbe.Capture("counter", 7));
                using (CoreClrDebugProbe.Enter(ChildMethod.Value))
                {
                    CoreClrDebugProbe.Hit(
                        ChildMethod.Value,
                        ChildNode.Value,
                        CoreClrDebugProbe.Capture("value", 19));
                }
                CoreClrDebugProbe.Hit(OuterMethod.Value, AfterNode.Value);
                completed = true;
            },
            static exception => exception.ToString());

        using JsonDocument first = WaitForState(controller, "paused");
        JsonElement firstRoot = first.RootElement;
        Assert.Equal("breakpoint", firstRoot.GetProperty("pause").GetProperty("reason").GetString());
        Assert.Equal(OuterNode.Value, firstRoot.GetProperty("pause").GetProperty("nodeId").GetString());
        Assert.Equal(7, firstRoot.GetProperty("frameLocals")[0]
            .GetProperty("locals")[0].GetProperty("display").GetString() is string display
                ? int.Parse(display, System.Globalization.CultureInfo.InvariantCulture)
                : -1);
        string firstPauseId = firstRoot.GetProperty("pause").GetProperty("pauseId").GetString()!;
        controller.Control(UnrealWorldDebugControl.StepInto, firstPauseId);

        using JsonDocument second = WaitForState(controller, "paused", ChildNode.Value);
        JsonElement secondRoot = second.RootElement;
        Assert.Equal("step", secondRoot.GetProperty("pause").GetProperty("reason").GetString());
        Assert.Equal("into", secondRoot.GetProperty("pause").GetProperty("stepKind").GetString());
        Assert.Equal(2, secondRoot.GetProperty("frames").GetArrayLength());
        Assert.Equal(2, secondRoot.GetProperty("frameLocals").GetArrayLength());
        Assert.Equal("19", secondRoot.GetProperty("frameLocals")[0]
            .GetProperty("locals")[0].GetProperty("display").GetString());
        string secondPauseId = secondRoot.GetProperty("pause").GetProperty("pauseId").GetString()!;
        controller.Control(UnrealWorldDebugControl.StepOut, secondPauseId);

        using JsonDocument third = WaitForState(controller, "paused", AfterNode.Value);
        Assert.Equal("out", third.RootElement.GetProperty("pause")
            .GetProperty("stepKind").GetString());
        string thirdPauseId = third.RootElement.GetProperty("pause")
            .GetProperty("pauseId").GetString()!;
        controller.Control(UnrealWorldDebugControl.Continue, thirdPauseId);

        using JsonDocument finished = WaitForState(controller, "completed");
        Assert.Equal("returned", finished.RootElement.GetProperty("result")
            .GetProperty("outcome").GetString());
        Assert.True(completed);
        Assert.False(controller.IsActive);
    }

    [Fact]
    public void LiveWorldProbe_RejectsStaleRuntimeGenerationAndRetainsFaultDiagnostic()
    {
        using var controller = new UnrealWorldDebugController(worldHostId: 2, instanceId: 5);
        string configuration = CreateConfigurationJson([]);
        Assert.Throws<JsonException>(() => controller.Configure(configuration, currentRuntimeGeneration: 4));

        controller.Configure(configuration, currentRuntimeGeneration: 3);
        controller.BeginTick(
            runtimeGeneration: 3,
            deltaSeconds: 0.016f,
            static _ => throw new InvalidOperationException("live debug failure"),
            static _ => "{\"schemaVersion\":\"unifiedscript.runtime-diagnostic/v0.1\"}");

        using JsonDocument finished = WaitForState(controller, "completed");
        Assert.Equal("faulted", finished.RootElement.GetProperty("result")
            .GetProperty("outcome").GetString());
        Assert.Contains("runtime-diagnostic/v0.1", finished.RootElement
            .GetProperty("runtimeDiagnostic").GetString(), StringComparison.Ordinal);
    }

    private static string CreateConfigurationJson(IReadOnlyList<SemanticId> breakpoints)
    {
        var generation = new CoreClrProgramGeneration(
            SemanticId.FromKey("world-debug-domain"),
            8,
            SemanticId.FromKey("world-debug-generation"),
            new ProgramImageKey($"sha256:{new string('b', 64)}"),
            SemanticId.FromKey("world-debug-compilation"),
            SemanticId.FromKey("world-debug-configuration"));
        return JsonSerializer.Serialize(
            new UnrealWorldDebugConfiguration(
                UnrealWorldDebugConfiguration.CurrentSchemaVersion,
                RuntimeGeneration: 3,
                generation,
                breakpoints),
            UnifiedJson.CreateOptions(writeIndented: false));
    }

    private static JsonDocument WaitForState(
        UnrealWorldDebugController controller,
        string state,
        string? nodeId = null)
    {
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try
            {
                using JsonDocument candidate = JsonDocument.Parse(controller.GetSnapshotJson());
                JsonElement root = candidate.RootElement;
                return root.GetProperty("state").GetString() == state &&
                    (nodeId is null || root.GetProperty("pause").GetProperty("nodeId").GetString() == nodeId);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(5)));
        return JsonDocument.Parse(controller.GetSnapshotJson());
    }
}
