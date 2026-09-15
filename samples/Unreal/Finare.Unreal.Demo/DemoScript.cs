using System;
using Finare;
using Finare.Unreal.Generated;

namespace Finare.Unreal.Demo;

[ScriptAsset("Unreal/Demo")]
public sealed class DemoScript : IUnrealManagedScript
{
    private FinareComponent _component = null!;

    [Expose(InstanceEditable = true, SaveGame = true, Category = "Demo")]
    public int InitialValue { get; set; }

    public DemoScript()
    {
        InitialValue = 10;
    }

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

    [UnrealBlueprintEvent]
    public void OnManagedActivated(int amount)
    {
        InitialValue += amount;
        if (InitialValue == 15)
        {
            _component.ManagedState = "Activated:15";
        }
        else
        {
            _component.ManagedState = "Activated:unexpected";
        }
    }

    [UnrealInputAction(
        "/Script/FinareRuntime.FinareInputActionFixture",
        TriggerEvent = UnrealInputTriggerEvent.Triggered,
        ValueType = UnrealInputValueType.Axis1D,
        ConsumeInput = true)]
    public void OnManagedMove(float value)
    {
        _component.ManagedState = value == 0f ? "Input:0" : "Input:nonzero";
    }

    [UnrealInterfaceFunction(
        "/Script/FinareRuntime.FinareInteropContract",
        "TransformManagedValue")]
    public int TransformManagedValue(int value)
    {
        return value * 2;
    }

    [Visual]
    public int UEApiPreview(FinareComponent component, int left, int right)
    {
        int sum = component.Add(left, right);
        return sum;
    }

    [Visual]
    public int UEDefaultPreview(FinareInteropFixture fixture, int value)
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
        _component = new FinareComponent(context.Host, context.Bridge);
        var worldServices = new FinareWorldLibrary(context.Target, context.Bridge);
        if (worldServices.GetWorldTimeSeconds(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(context.Target, context.Bridge)) < 0.0f)
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
        typeof(FinareComponent),
        "/Script/FinareRuntime.FinareComponent:OnManagedSignal",
        Source = UnrealEventSource.Host)]
    private void OnManagedSignal(int value, UnrealObject? signalContext)
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

[ScriptAsset(
    "Unreal/DemoLibrary",
    ClassId = "us_0fdbad6aa2af414e8d8f073115cf03ab")]
[UnrealScriptClass(UnrealScriptClassKind.BlueprintFunctionLibrary)]
public static class DemoLibrary
{
    [Visual]
    public static IReadOnlyList<int> ExchangeArray(
        IReadOnlyList<int> replacement, ref IReadOnlyList<int> value, out IReadOnlyList<int> previous)
    {
        previous = value;
        value = replacement;
        return previous;
    }

    [Visual]
    public static IReadOnlySet<UnrealName> ExchangeSet(
        IReadOnlySet<UnrealName> replacement, ref IReadOnlySet<UnrealName> value, out IReadOnlySet<UnrealName> previous)
    {
        previous = value;
        value = replacement;
        return previous;
    }

    [Visual]
    public static IReadOnlyDictionary<string, UnrealSoftObjectHandle> ExchangeMap(
        IReadOnlyDictionary<string, UnrealSoftObjectHandle> replacement,
        ref IReadOnlyDictionary<string, UnrealSoftObjectHandle> value,
        out IReadOnlyDictionary<string, UnrealSoftObjectHandle> previous)
    {
        previous = value;
        value = replacement;
        return previous;
    }

    [Visual]
    public static UnrealSoftObjectHandle ExchangeSoftObject(
        UnrealSoftObjectHandle replacement, ref UnrealSoftObjectHandle value, out UnrealSoftObjectHandle previous)
    {
        previous = value;
        value = replacement;
        return previous;
    }

    [Visual]
    public static UnrealSoftClassHandle ExchangeSoftClass(
        UnrealSoftClassHandle replacement, ref UnrealSoftClassHandle value, out UnrealSoftClassHandle previous)
    {
        previous = value;
        value = replacement;
        return previous;
    }

    [Visual]
    public static string Describe(
        UnrealObjectHandle value,
        UnrealVector location,
        string label)
    {
        return label;
    }

    [Visual]
    public static UnrealObjectHandle EchoObject(UnrealObjectHandle value)
    {
        return value;
    }

    [Visual]
    public static UnrealVector OffsetVector(UnrealVector value)
    {
        return value;
    }

    [Visual]
    public static EFinareInteropMode EchoMode(EFinareInteropMode value)
    {
        return value;
    }

    [Visual]
    public static HitResult EchoHitResult(HitResult value)
    {
        return value;
    }

    [Visual]
    public static UnrealClassHandle ResolveCommonValues(
        UnrealName name,
        ref UnrealRotator rotation,
        out UnrealText label,
        UnrealTransform transform)
    {
        _ = name;
        _ = transform;
        rotation = default;
        label = default;
        return default;
    }
}
