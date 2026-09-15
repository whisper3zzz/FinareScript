using Finare;

namespace Finare.Unreal.Demo;

[ScriptAsset("Tests/NativeProperties", ClassId = "us_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab")]
[UnrealScriptClass(UnrealScriptClassKind.ActorComponent,
    GeneratedClassPath = "/Game/FinareAutomation/BP_NativeProperties.BP_NativeProperties_C")]
public sealed partial class NativePropertyDemoScript : UnrealActorComponentScript
{
    [Expose] public partial int Health { get; set; } = 100;
    [Expose] public partial IReadOnlyList<int> Scores { get; set; } = [];

    protected override void OnBeginPlay()
    {
        Health += 1;
        Scores = [1, 2];
    }

    protected override void OnTick(float deltaSeconds)
    {
        IReadOnlyList<int> snapshot = Scores;
        if (snapshot is IList<int> mutable) mutable[0] = 99;
        if (Scores[0] == 99)
            throw new InvalidOperationException("A value snapshot changed the native container.");
        Health += 1;
        Scores = [3, 4];
    }
}
