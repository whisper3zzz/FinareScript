#nullable disable

using System.Collections.Generic;

namespace Finare.Unreal.Demo;

/// <summary>
/// Minimal lifecycle fixture for a managed script hosted by an ordinary native
/// ActorComponent. The tags are written on the actual receiver, so the native
/// test can verify that each component records only its own callbacks.
/// </summary>
public sealed class NativeReceiverDemoScript : UnrealActorComponentScript
{
    private int _beginCount;
    private int _tickCount;
    private int _endCount;

    protected override void OnBeginPlay()
    {
        ++_beginCount;
        PublishLifecycleCounts();
    }

    protected override void OnTick(float deltaSeconds)
    {
        ++_tickCount;
        PublishLifecycleCounts();
    }

    protected override void OnEndPlay()
    {
        ++_endCount;
        PublishLifecycleCounts();
    }

    private void PublishLifecycleCounts()
    {
        IReadOnlyList<UnrealName> tags = new[]
        {
            new UnrealName($"Begin={_beginCount}"),
            new UnrealName($"Tick={_tickCount}"),
            new UnrealName($"End={_endCount}"),
        };
        Bridge.WriteProperty<IReadOnlyList<UnrealName>>(
            HostHandle,
            "/Script/Engine.ActorComponent:ComponentTags",
            tags);
    }
}
