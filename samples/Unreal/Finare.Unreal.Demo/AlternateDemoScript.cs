using System;
using Finare.Unreal.Generated;

namespace Finare.Unreal.Demo;

/** Second entry point used to prove Script Asset reload can change managed type. */
public sealed class AlternateDemoScript : IUnrealManagedScript
{
    private FinareComponent? _component;

    public void OnStart(UnrealScriptContext context)
    {
        _component = new FinareComponent(context.Host, context.Bridge);
        if (_component.Add(21, 21) != 42)
        {
            throw new InvalidOperationException("Alternate generated wrapper invocation failed.");
        }
        _component.ManagedState = "AlternateStarted";
        _component.bStartAutomatically = false;
    }

    public void OnTick(float deltaSeconds)
    {
        if (deltaSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        }
        _component!.ManagedState = "AlternateTicked";
    }

    public void OnStop()
    {
        _component!.ManagedState = "AlternateStopped";
    }
}
