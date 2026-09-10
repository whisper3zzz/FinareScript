using System;
using Finare.Unreal.Generated;

namespace Finare.Unreal.Demo;

/// <summary>
/// Deterministic two-phase reload fixture. One component can reject candidate
/// start after native preflight while its siblings have already prepared.
/// </summary>
public sealed class ReloadBatchDemoScript : IUnrealManagedScript
{
    private FinareComponent? _component;

    public void OnStart(UnrealScriptContext context)
    {
        _component = new FinareComponent(context.Host, context.Bridge);
        if (!_component.bTickAutomatically)
        {
            throw new InvalidOperationException(
                "Injected candidate OnStart failure after reload preflight.");
        }

        int initialValue = _component.GetScriptStateInt32("InitialValue", -1);
        _component.ManagedState = $"BatchStarted:{initialValue}";
        _component.bStartAutomatically = false;
    }

    public void OnTick(float deltaSeconds)
    {
        _component!.ManagedState = "BatchTicked";
    }

    public void OnStop()
    {
        _component!.ManagedState = "BatchStopped";
    }
}
