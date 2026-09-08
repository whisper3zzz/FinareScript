#nullable disable

using System.Collections.Generic;
using UnifiedScript;
using UnifiedScript.Unreal;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Tests.Fixtures.BlueprintInheritedState;

[ScriptAsset("UnifiedScript/Tests/Fixtures/BlueprintInheritedState/BlueprintInheritedStateFixture")]
[UnrealScriptClass(
    UnrealScriptClassKind.Actor,
    GeneratedClassPath = BlueprintInheritedStatePaths.ActorClassPath)]
public sealed class BlueprintInheritedStateFixture : UnrealActorScript
{
    [Expose(InstanceEditable = true, SaveGame = true, Category = "Inherited State")]
    public int Seed { get; set; } = 3;

    [Expose(InstanceEditable = true, SaveGame = true, Category = "Inherited State")]
    public IReadOnlyList<int> Values { get; set; } = [2, 4];

    [Expose(InstanceEditable = true, SaveGame = true, Category = "Inherited State")]
    public IReadOnlySet<int> Enabled { get; set; } = new HashSet<int> { 5, 7 };

    [Expose(InstanceEditable = true, SaveGame = true, Category = "Inherited State")]
    public IReadOnlyDictionary<int, int> Weights { get; set; } = new Dictionary<int, int>
    {
        { 11, 13 },
        { 17, 19 },
    };

    [Expose(InstanceEditable = false, SaveGame = false, Category = "Inherited State")]
    public int TickCount { get; set; } = 0;

    private Actor _actor;
    private UnifiedScriptComponent _host;

    protected override void OnBeginPlay()
    {
        _actor = new Actor(TargetHandle, Bridge);
        _host = new UnifiedScriptComponent(HostHandle, Bridge);
        ApplySignatureRotation();
    }

    protected override void OnTick(float deltaSeconds)
    {
        TickCount++;
        ApplySignatureRotation();
    }

    protected override void OnEndPlay()
    {
        _host = null;
        _actor = null;
    }

    private void ApplySignatureRotation()
    {
        int signature = ComputeSignature();

        double yaw = signature % 360;
        _actor.SetActorRotation(new UnrealRotator(0.0, yaw, 0.0), true);
        _host.ManagedState = $"InheritedState:{signature}:Ticks:{TickCount}";
    }

    private int ComputeSignature()
    {
        int signature = Seed * 17;
        for (int index = 0; index < Values.Count; index++)
        {
            signature += Values[index] * (index + 1);
        }
        foreach (int enabled in Enabled)
        {
            signature += enabled * 3;
        }
        foreach (KeyValuePair<int, int> weight in Weights)
        {
            signature += weight.Key * 5 + weight.Value * 7 + weight.Key * weight.Value * 11;
        }

        return signature;
    }
}
