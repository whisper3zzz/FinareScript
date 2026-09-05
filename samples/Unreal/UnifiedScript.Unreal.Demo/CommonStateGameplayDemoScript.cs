using System;
using UnifiedScript;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Unreal.Demo;

/// <summary>
/// A persistent Actor Script Class whose editable state uses the generated
/// ETickingGroup enum together with Rotator and Transform values.
/// </summary>
[ScriptAsset("Unreal/CommonStateGameplay")]
[UnrealScriptClass(
    UnrealScriptClassKind.Actor,
    GeneratedClassPath = "/Game/UnifiedScriptDemo/BP_CommonStateGameplay.BP_CommonStateGameplay_C")]
public sealed class CommonStateGameplayDemoScript : UnrealActorScript
{
    [Expose(InstanceEditable = true, SaveGame = true, Category = "Common State")]
    public ETickingGroup TickGroup { get; set; } = ETickingGroup.TG_DuringPhysics;

    [Expose(InstanceEditable = true, SaveGame = true, Category = "Common State")]
    public UnrealRotator Facing { get; set; } = new(0.0, 45.0, 0.0);

    [Expose(InstanceEditable = true, SaveGame = true, Category = "Common State")]
    public UnrealTransform Placement { get; set; } = new(
        new UnrealQuaternion(0.0, 0.0, 0.3826834323650898, 0.9238795325112867),
        new UnrealVector(120.0, 0.0, 40.0),
        new UnrealVector(1.25, 0.75, 1.0));

    private Actor? _actor;

    protected override void OnBeginPlay()
    {
        _actor = new Actor(TargetHandle, Bridge);
        _actor.SetTickGroup(TickGroup);
        if (!_actor.SetActorTransform(
                Placement,
                bSweep: false,
                out HitResult sweepHit,
                bTeleport: true))
        {
            throw new InvalidOperationException(
                $"The configured Actor Transform could not be applied ({sweepHit.Distance}).");
        }
    }

    protected override void OnTick(float deltaSeconds)
    {
        if (deltaSeconds < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        }
        Facing = Facing with { Yaw = Facing.Yaw + deltaSeconds * 10.0 };
        _ = _actor!.SetActorRotation(Facing, bTeleportPhysics: true);
    }

    protected override void OnEndPlay()
    {
        _actor = null;
    }
}
