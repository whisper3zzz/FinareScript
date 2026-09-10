#nullable disable

using Finare;

namespace Finare.Unreal.Demo;

/// <summary>
/// Managed override surface used by the native Blueprint parent-call fixture.
/// Each method forwards through the explicit parent bridge so the fixture can
/// exercise scalar/ref/out and a result large enough to require a retry.
/// </summary>
public sealed class BlueprintParentInvocationDemoScript : UnrealActorScript
{
    private const string FixturePath =
        "/Script/FinareRuntime.FinareEventExtensionFixture";

    [UnrealBlueprintOverride(FixturePath, "EvaluateInherited")]
    public int EvaluateInherited(
        int nativeInput,
        ref int nativeAccumulator,
        out int nativeOutput)
    {
        UnrealOutputInvocationResult parent = CallParentWithOutputs(
            nameof(EvaluateInherited),
            nativeInput,
            nativeAccumulator);
        nativeAccumulator = (int)parent.GetOutput(0);
        nativeOutput = (int)parent.GetOutput(1);
        return (int)parent.ReturnValue;
    }

    [UnrealBlueprintOverride(FixturePath, "ReceiveInherited")]
    public void ReceiveInherited(string nativeText, UnrealObjectHandle nativeActor)
    {
        CallParent(nameof(ReceiveInherited), nativeText, nativeActor);
    }

    [UnrealBlueprintOverride(FixturePath, "ExchangeInheritedArray")]
    public IReadOnlyList<int> ExchangeInheritedArray(
        IReadOnlyList<int> nativeInput,
        ref IReadOnlyList<int> nativeState,
        out IReadOnlyList<int> nativeOutput)
    {
        UnrealOutputInvocationResult parent = CallParentWithOutputs(
            nameof(ExchangeInheritedArray),
            nativeInput,
            nativeState);
        nativeState = (IReadOnlyList<int>)parent.GetOutput(0);
        nativeOutput = (IReadOnlyList<int>)parent.GetOutput(1);
        return (IReadOnlyList<int>)parent.ReturnValue;
    }
}

public sealed class BlueprintParentInvocationComponentDemoScript : UnrealActorComponentScript
{
    [UnrealBlueprintOverride("/Script/Engine.ActorComponent", "ReceiveTick")]
    public void ReceiveTick(float deltaSeconds)
    {
        CallParent(nameof(ReceiveTick), deltaSeconds);
    }
}
