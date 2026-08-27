using System;
using UnifiedScript.Runtime;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Unreal.Demo;

/// <summary>
/// Deterministic live-World semantic debugger fixture. The ordinary DemoScript uses
/// compiler-injected probes; this fixture keeps the same probe shape explicit so the
/// native Automation gate can exercise the host ABI without starting an HTTP server.
/// </summary>
public sealed class RuntimeSemanticDebugDemoScript : IUnrealManagedScript
{
    public const string OuterMethodId = "us_11111111111111111111111111111111";
    public const string OuterNodeId = "us_22222222222222222222222222222222";
    public const string ChildMethodId = "us_33333333333333333333333333333333";
    public const string ChildNodeId = "us_44444444444444444444444444444444";
    public const string AfterNodeId = "us_55555555555555555555555555555555";

    private UnifiedScriptComponent _component = null!;

    public void OnStart(UnrealScriptContext context)
    {
        _component = new UnifiedScriptComponent(context.Host, context.Bridge);
        _component.ManagedState = "SemanticDebugReady";
        _component.bStartAutomatically = false;
    }

    public void OnTick(float deltaSeconds)
    {
        using IDisposable outer = CoreClrDebugProbe.Enter(OuterMethodId);
        int counter = 7;
        CoreClrDebugProbe.Hit(
            OuterMethodId,
            OuterNodeId,
            CoreClrDebugProbe.Capture(nameof(deltaSeconds), deltaSeconds),
            CoreClrDebugProbe.Capture(nameof(counter), counter));

        using (CoreClrDebugProbe.Enter(ChildMethodId))
        {
            int nestedValue = 19;
            CoreClrDebugProbe.Hit(
                ChildMethodId,
                ChildNodeId,
                CoreClrDebugProbe.Capture(nameof(nestedValue), nestedValue));
        }

        CoreClrDebugProbe.Hit(
            OuterMethodId,
            AfterNodeId,
            CoreClrDebugProbe.Capture(nameof(counter), counter));
        _component.ManagedState = "SemanticDebugCompleted";
    }

    public void OnStop()
    {
        _component.ManagedState = "SemanticDebugStopped";
    }
}
