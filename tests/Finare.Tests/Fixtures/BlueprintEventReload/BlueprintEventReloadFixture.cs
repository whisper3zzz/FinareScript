#nullable disable

using Finare;
using Finare.Unreal;

namespace Finare.Tests.Fixtures.BlueprintEventReload;

public sealed class BlueprintEventReloadFixture : UnrealActorScript
{
    private const string FixturePath =
        "/Script/FinareRuntime.FinareEventExtensionFixture";

    [UnrealBlueprintOverride(
        FixturePath,
#if INCOMPATIBLE
        "EvaluateInheritedAlternate"
#else
        "EvaluateInherited"
#endif
    )]
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
#if V2
        return (int)parent.ReturnValue + 100;
#else
        return (int)parent.ReturnValue;
#endif
    }
}
