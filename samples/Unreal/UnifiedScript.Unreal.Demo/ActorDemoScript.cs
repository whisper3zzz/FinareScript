using System;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Unreal.Demo;

/// <summary>End-to-end Actor behavior used by the live UE lifecycle gate.</summary>
public sealed class ActorDemoScript : UnrealActorScript
{
    private UnifiedScriptActor? _actor;
    private UnifiedScriptComponent? _host;

    protected override void OnBeginPlay()
    {
        if (!Context.HasDistinctTarget)
        {
            throw new InvalidOperationException("An Actor script requires distinct host and Actor handles.");
        }

        _actor = new UnifiedScriptActor(TargetHandle, Bridge);
        _host = new UnifiedScriptComponent(HostHandle, Bridge);
        _actor.ManagedActorState = $"ActorBegin:{Generation}";
        _host.ManagedState = $"ActorHost:{InstanceId}:{Generation}";
        _ = CompleteLatentStepAsync();
    }

    protected override void OnTick(float deltaSeconds)
    {
        if (deltaSeconds < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        }

        _actor!.IncrementManagedTickCount();
        _actor.ManagedActorState = "ActorTick";
    }

    protected override void OnEndPlay()
    {
        if (!Lifetime.IsCancellationRequested)
        {
            throw new InvalidOperationException("Actor lifetime must be canceled before EndPlay.");
        }
        _actor!.ManagedActorState = "ActorEnd";
        _host!.ManagedState = "ActorHost:End";
    }

    protected override void OnActorBeginOverlap(UnrealObjectHandle otherActor)
    {
        _actor!.ManagedActorState = otherActor.IsWellFormed
            ? "ActorBeginOverlap"
            : "ActorBeginOverlap:Invalid";
    }

    protected override void OnActorEndOverlap(UnrealObjectHandle otherActor)
    {
        _actor!.ManagedActorState = otherActor.IsWellFormed
            ? "ActorEndOverlap"
            : "ActorEndOverlap:Invalid";
    }

    private async Task CompleteLatentStepAsync()
    {
        try
        {
            await DelayAsync(0.01f);
            _host!.ManagedState = "ActorLatent";
        }
        catch (OperationCanceledException)
        {
            // Reload and EndPlay synchronously cancel this generation.
        }
    }
}
