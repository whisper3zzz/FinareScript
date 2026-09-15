using System;
using Finare;
using Finare.Unreal.Generated;

namespace Finare.Unreal.Demo;

[ScriptAsset("Unreal/InterfaceGameplay")]
public sealed class InterfaceGameplayDemoScript : IUnrealManagedScript
{
    private IUnrealInvocationBridge _bridge = null!;
    private FinareInteropFixture _fixture = null!;
    private FinareInteropContract _selectedInterface = null!;

    [Expose(InstanceEditable = true, SaveGame = true, Category = "Interop")]
    public UnrealObjectHandle InterfaceTarget { get; set; } = default;

    [Expose(Category = "Interop")]
    public int LastInterfaceResult { get; private set; } = 0;

    [Visual]
    public int CallSelectedInterface(int value)
    {
        if (InterfaceTarget.IsNull)
        {
            return 0;
        }

        LastInterfaceResult = _selectedInterface.TransformManagedValue(value);
        return LastInterfaceResult;
    }

    public void OnStart(UnrealScriptContext context)
    {
        _bridge = context.Bridge;
        _fixture = new FinareInteropFixture(context.Target, _bridge);

        if (InterfaceTarget.IsNull)
        {
            InterfaceTarget = (_fixture.InterfaceValue)?.Handle ?? default;
        }

        if (InterfaceTarget.IsNull)
        {
            LastInterfaceResult = 0;
            return;
        }

        _selectedInterface = new(InterfaceTarget, _bridge);
        FinareInteropContract? echoed = _fixture.EchoInterface(_selectedInterface);
        FinareInteropContract? accumulator = null;
        bool mutated = _fixture.MutateInterfaces(echoed, out FinareInteropContract? output, ref accumulator);
        if (!mutated || output != echoed || accumulator != echoed)
        {
            InterfaceTarget = default;
            LastInterfaceResult = 0;
            return;
        }

        _fixture.InterfaceValue = output;
        InterfaceTarget = (_fixture.InterfaceValue)?.Handle ?? default;
        _fixture.Int32Value = CallSelectedInterface(3);
    }

    public void OnTick(float deltaSeconds)
    {
        if (deltaSeconds < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        }
    }

    public void OnStop()
    {
        _fixture = null!;
        _selectedInterface = null!;
        _bridge = null!;
    }
}
