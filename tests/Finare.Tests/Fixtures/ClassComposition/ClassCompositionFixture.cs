using Finare;
using Finare.Unreal;
using Finare.Unreal.Generated;

namespace Finare.Tests.Fixtures.ClassComposition;

[ScriptAsset("Finare/Tests/ClassComposition/Actor")]
[UnrealScriptClass(UnrealScriptClassKind.Actor,
    BaseClassPath = "/Script/Engine.Actor",
    GeneratedClassPath = CompositionPaths.ActorClassPath)]
public sealed partial class CompositionActor : UnrealActorScript
{
    [Expose(InstanceEditable = true, SaveGame = true)]
    public partial int Seed { get; set; } = 11;

    protected override void OnBeginPlay() =>
        new FinareComponent(HostHandle, Bridge).ManagedState =
            $"Actor:{CompositionRevision.Label}:{Seed}";
}

[ScriptAsset("Finare/Tests/ClassComposition/Health")]
[UnrealScriptClass(UnrealScriptClassKind.ActorComponent,
    BaseClassPath = "/Script/FinareRuntime.FinareComponent",
    GeneratedClassPath = CompositionPaths.HealthClassPath)]
public sealed partial class CompositionHealth : UnrealActorComponentScript
{
    [Expose(InstanceEditable = true, SaveGame = true)]
    public partial int HitPoints { get; set; } = 100;

    protected override void OnBeginPlay() =>
        new FinareComponent(HostHandle, Bridge).ManagedState =
            $"Health:{CompositionRevision.Label}:{HitPoints}";
}

[ScriptAsset("Finare/Tests/ClassComposition/Mover")]
[UnrealScriptClass(UnrealScriptClassKind.ActorComponent,
    BaseClassPath = "/Script/FinareRuntime.FinareComponent",
    GeneratedClassPath = CompositionPaths.MoverClassPath)]
public sealed partial class CompositionMover : UnrealActorComponentScript
{
    [Expose(InstanceEditable = true, SaveGame = true)]
    public partial int Speed { get; set; } = 3;

    [Expose(InstanceEditable = true, SaveGame = true)]
    public partial UnrealObjectHandle Peer { get; set; } = default;

    protected override void OnBeginPlay()
    {
        if (CompositionRevision.FailMover)
            throw new System.InvalidOperationException("Composition mover candidate rejected.");

        new FinareComponent(HostHandle, Bridge).ManagedState =
            $"Mover:{CompositionRevision.Label}:{Speed}";
    }

    protected override void OnTick(float deltaSeconds)
    {
        var health = new FinareComponent(Peer, Bridge);
        new FinareComponent(HostHandle, Bridge).ManagedState =
            $"Mover:{CompositionRevision.Label}:{Speed}:Peer={health.ManagedState}";
    }
}
