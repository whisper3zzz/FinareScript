#nullable disable

using System.Collections.Generic;
using Finare;
using Finare.Unreal;
using Finare.Unreal.Generated;

namespace Finare.Tests.Fixtures.BlueprintInheritedState;

public abstract class BlueprintInheritedStateBase : UnrealActorScript
{
    [Expose(InstanceEditable = true, SaveGame = true, Category = "Inherited State")]
    public int Seed { get; set; } = 1;

    [Expose(InstanceEditable = true, SaveGame = true, Category = "Inherited State")]
    public IReadOnlyList<int> Values { get; set; } = new List<int> { 0, 1 };

    public int UnexposedHelper { get; set; } = 77;

    protected BlueprintInheritedStateBase()
    {
        Seed = 8;
        Values = new List<int> { 9, 10 };
    }
}

public abstract class BlueprintInheritedStateMiddle : BlueprintInheritedStateBase
{
    [Expose(InstanceEditable = true, SaveGame = true, Category = "Inherited State")]
    public IReadOnlySet<int> Enabled { get; set; } = new HashSet<int> { 1, 2 };

    [Expose(InstanceEditable = true, SaveGame = true, Category = "Inherited State")]
    public IReadOnlyDictionary<int, int> Weights { get; set; } = new Dictionary<int, int>
    {
        { 1, 1 },
    };

    [Expose(InstanceEditable = false, SaveGame = false, Category = "Inherited State")]
    public int TickCount { get; set; } = 5;

    protected BlueprintInheritedStateMiddle()
    {
        Enabled = new HashSet<int> { 5, 7 };
        Weights = new Dictionary<int, int>
        {
            { 11, 13 },
            { 17, 19 },
        };
        TickCount = 0;
    }
}

[ScriptAsset("Finare/Tests/Fixtures/BlueprintInheritedState/BlueprintInheritedStateFixture")]
[UnrealScriptClass(
    UnrealScriptClassKind.Actor,
    GeneratedClassPath = BlueprintInheritedStatePaths.ActorClassPath)]
public sealed class BlueprintInheritedStateFixture : BlueprintInheritedStateMiddle
{
    private Actor _actor;
    private FinareComponent _host;

    public BlueprintInheritedStateFixture()
    {
        base.Seed = 3;
        base.Values = new List<int> { 2, 4 };
    }

    protected override void OnBeginPlay()
    {
        _actor = new Actor(TargetHandle, Bridge);
        _host = new FinareComponent(HostHandle, Bridge);
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
