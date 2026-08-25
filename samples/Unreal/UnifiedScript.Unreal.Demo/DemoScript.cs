using System;
using UnifiedScript;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Unreal.Demo;

[ScriptAsset("Unreal/Demo")]
public sealed class DemoScript : IUnrealManagedScript
{
    private UnifiedScriptComponent? _component;

    [Expose(InstanceEditable = true, SaveGame = true, Category = "Demo")]
    public int InitialValue { get; set; } = 10;

    [Visual]
    public int ResolveInitialValue(int bonus)
    {
        int result = InitialValue + bonus;
        if (result > 100)
        {
            result = 100;
        }

        return result;
    }

    [Visual]
    public int RewirePreview(int value, bool doubleValue)
    {
        int doubled = value * 2;
        int incremented = value + 1;
        if (doubleValue)
        {
            return doubled;
        }

        return incremented;
    }

    [Visual]
    public int UEApiPreview(UnifiedScriptComponent component, int left, int right)
    {
        int sum = component.Add(left, right);
        return sum;
    }

    public void OnStart(UnrealScriptContext context)
    {
        _component = new UnifiedScriptComponent(context.Host, context.Bridge);
        if (_component.Add(20, 22) != 42)
        {
            throw new InvalidOperationException("Generated wrapper invocation failed.");
        }
        InitialValue = _component.GetScriptStateInt32(nameof(InitialValue), InitialValue);
        _component.ManagedState = $"Started:{InitialValue}";
        _component.bStartAutomatically = false;
    }

    public void OnTick(float deltaSeconds)
    {
        if (deltaSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        }
        _component!.ManagedState = "Ticked";
    }

    public void OnStop()
    {
        _component!.ManagedState = "Stopped";
    }
}
