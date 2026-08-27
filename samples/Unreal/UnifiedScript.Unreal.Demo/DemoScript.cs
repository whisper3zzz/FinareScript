using System;
using UnifiedScript;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Unreal.Demo;

[ScriptAsset("Unreal/Demo")]
public sealed class DemoScript : IUnrealManagedScript
{
    private UnifiedScriptComponent _component = null!;

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

    [Visual]
    public int UEDefaultPreview(UnifiedScriptInteropFixture fixture, int value)
    {
        return fixture.AddWithDefault(value);
    }

    [Visual]
    public float ResolveTickDelta(float deltaSeconds)
    {
        float resolved = NormalizeTickDelta(deltaSeconds);
        return resolved;
    }

    [Visual]
    public float NormalizeTickDelta(float deltaSeconds)
    {
        float resolved = deltaSeconds;
        if (resolved < 0f)
        {
            resolved = 0f;
        }

        return resolved;
    }

    public void OnStart(UnrealScriptContext context)
    {
        if (!context.DataPlane.IsAvailable)
        {
            throw new InvalidOperationException("Shared Data ABI v4 is unavailable.");
        }
        UnrealTransformSnapshot snapshot =
            context.DataPlane.AcquireTransformSnapshot();
        if (snapshot.WorldHostId == 0 || snapshot.Generation == 0)
        {
            throw new InvalidOperationException("The World Transform snapshot is invalid.");
        }
        if (!context.DataPlane.SupportsGameplayData ||
            context.DataPlane.GameplayPhase != UnrealGameplayDataPhaseV4.ManagedTick)
        {
            throw new InvalidOperationException("Gameplay Data ABI v4 is unavailable.");
        }
        _ = context.DataPlane.AcquireEvents().Events.Length;
        using (UnrealCommandBatch commands = context.DataPlane.BeginCommands(1))
        {
            commands.Commit(0);
        }
        using (UnrealTraceBatch traces = context.DataPlane.BeginTraceBatch(1))
        {
            traces.Requests[0] = UnrealTraceRequestV4.Line(
                UnrealVector.Zero,
                UnrealVector.Zero,
                collisionChannel: 3);
            ReadOnlySpan<UnrealTraceResultV4> results = traces.Execute();
            if (results.Length != 1 || !results[0].IsSuccess)
            {
                throw new InvalidOperationException("Gameplay batch trace failed.");
            }
        }
        _component = new UnifiedScriptComponent(context.Host, context.Bridge);
        var worldServices = new UnifiedScriptWorldLibrary(context.Target, context.Bridge);
        if (worldServices.GetWorldTimeSeconds(context.Target) < 0.0f)
        {
            throw new InvalidOperationException("Generated World service invocation failed.");
        }
        if (_component.Add(20, 22) != 42)
        {
            throw new InvalidOperationException("Generated wrapper invocation failed.");
        }
        InitialValue = _component.GetScriptStateInt32(nameof(InitialValue), InitialValue);
        _component.ManagedState = $"Started:{InitialValue}";
        _component.bStartAutomatically = false;
    }

    [Visual]
    [UnrealEvent(
        typeof(UnifiedScriptComponent),
        "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:OnManagedSignal")]
    private void OnManagedSignal(int value, UnrealObjectHandle signalContext)
    {
        _component.ManagedState += "|Event:" + value;
    }

    public void OnTick(float deltaSeconds)
    {
        if (deltaSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        }
        _ = ResolveTickDelta(deltaSeconds);
        _component!.ManagedState = "Ticked";
    }

    public void OnStop()
    {
        _component!.ManagedState = "Stopped";
    }
}
