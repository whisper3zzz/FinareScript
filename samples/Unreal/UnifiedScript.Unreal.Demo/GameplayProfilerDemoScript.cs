using System;
using UnifiedScript.Core;
using UnifiedScript.Runtime;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Unreal.Demo;

/// <summary>
/// A live World Tick workload that exercises the M9.3 attribution families.
/// </summary>
public sealed class GameplayProfilerDemoScript : IUnrealManagedScript
{
    public static readonly SemanticId TickMethodId =
        SemanticId.FromKey("gameplay-profiler-demo-tick");
    public static readonly SemanticId BridgeNodeId =
        SemanticId.FromKey("gameplay-profiler-demo-bridge");
    public static readonly SemanticId DataPlaneNodeId =
        SemanticId.FromKey("gameplay-profiler-demo-data-plane");

    private UnrealScriptContext context;

    public void OnStart(UnrealScriptContext value)
    {
        context = value;
        if (!context.DataPlane.SupportsGameplayData ||
            !context.DataPlane.SupportsOverlapQueries)
        {
            throw new InvalidOperationException(
                "The gameplay profiler demo requires the complete World data plane.");
        }
    }

    public void OnTick(float deltaSeconds)
    {
        using IDisposable method = CoreClrDebugProbe.Enter(TickMethodId.Value);
        CoreClrDebugProbe.Hit(
            TickMethodId.Value,
            BridgeNodeId.Value,
            CoreClrDebugProbe.Capture(nameof(deltaSeconds), deltaSeconds));

        var component = new UnifiedScriptComponent(context.Host, context.Bridge);
        _ = component.Add(10, 20);
        component.ManagedState = "GameplayProfilerDemo";
        _ = context.Bridge.Invoke<int>(
            context.Host,
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:Add",
            [1, 2]);

        CoreClrDebugProbe.Hit(TickMethodId.Value, DataPlaneNodeId.Value);
        _ = context.DataPlane.AcquireEvents().Events.Length;
        UnrealSharedObjectHandleV4 target = context.DataPlane.Scope(context.Target);
        using (UnrealCommandBatch commands = context.DataPlane.BeginCommands(1))
        {
            commands.Commands[0] = UnrealCommandV4.SetActorTransform(
                target,
                UnrealTransform.Identity);
            commands.Commit();
        }

        var missStart = new UnrealVector(100_000.0, 100_000.0, 100_000.0);
        var missEnd = new UnrealVector(100_100.0, 100_000.0, 100_000.0);
        using (UnrealTraceBatch traces = context.DataPlane.BeginTraceBatch(1))
        {
            traces.Requests[0] = UnrealTraceRequestV4.Line(
                missStart,
                missEnd,
                collisionChannel: 3);
            _ = traces.Execute().Length;
        }
        using (UnrealOverlapBatch overlaps = context.DataPlane.BeginOverlapBatch(1))
        {
            overlaps.Requests[0] = UnrealOverlapRequestV1.Sphere(
                missStart,
                radius: 10.0,
                collisionChannel: 3);
            _ = overlaps.Execute().Length;
        }

        bool commandOverflowRejected;
        try
        {
            _ = context.DataPlane.BeginCommands(
                checked((int)UnrealGameplayDataAbiV4.MaximumCommandCount + 1));
            commandOverflowRejected = false;
        }
        catch (InvalidOperationException)
        {
            commandOverflowRejected = true;
        }
        if (!commandOverflowRejected)
        {
            throw new InvalidOperationException(
                "The command buffer accepted an oversized reservation.");
        }
        bool traceOverflowRejected;
        try
        {
            _ = context.DataPlane.BeginTraceBatch(
                checked((int)UnrealGameplayDataAbiV4.MaximumTraceCount + 1));
            traceOverflowRejected = false;
        }
        catch (InvalidOperationException)
        {
            traceOverflowRejected = true;
        }
        if (!traceOverflowRejected)
        {
            throw new InvalidOperationException(
                "The trace buffer accepted an oversized reservation.");
        }
        bool overlapOverflowRejected;
        try
        {
            _ = context.DataPlane.BeginOverlapBatch(
                checked((int)UnrealGameplayDataAbiV4.MaximumOverlapCount + 1));
            overlapOverflowRejected = false;
        }
        catch (InvalidOperationException)
        {
            overlapOverflowRejected = true;
        }
        if (!overlapOverflowRejected)
        {
            throw new InvalidOperationException(
                "The overlap buffer accepted an oversized reservation.");
        }
        _ = new byte[32];
    }

    public void OnStop()
    {
    }
}
