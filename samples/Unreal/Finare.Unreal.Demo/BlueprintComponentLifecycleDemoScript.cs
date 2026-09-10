#nullable disable

using System;
using Finare.Unreal.Generated;

namespace Finare.Unreal.Demo;

/// <summary>
/// ActorComponent lifecycle fixture. The managed instance is started before the
/// native BeginPlay event and remains alive until after the native EndPlay event
/// when both inherited overrides are present.
/// </summary>
public sealed class BlueprintComponentLifecycleDemoScript : UnrealActorComponentScript
{
    private FinareComponent _host;

    protected override void OnBeginPlay()
    {
        _host = new FinareComponent(HostHandle, Bridge);
        _host.ManagedState = "Initialized";
    }

    [UnrealBlueprintOverride("/Script/Engine.ActorComponent", "ReceiveBeginPlay")]
    public void ReceiveBeginPlay()
    {
        _host.ManagedState += "|ReceiveBegin";
        CallParent(nameof(ReceiveBeginPlay));
    }

    [UnrealBlueprintOverride("/Script/Engine.ActorComponent", "ReceiveEndPlay")]
    public void ReceiveEndPlay(EEndPlayReason reason)
    {
        if (Lifetime.IsCancellationRequested)
        {
            _host.ManagedState += "|ReceiveEnd:invalid";
            return;
        }

        _host.ManagedState += "|ReceiveEnd:" + reason.Value;
        CallParent(nameof(ReceiveEndPlay), reason);
    }

    protected override void OnEndPlay()
    {
        _host.ManagedState += Lifetime.IsCancellationRequested
            ? "|Stopped"
            : "|Stopped:invalid";
    }
}
