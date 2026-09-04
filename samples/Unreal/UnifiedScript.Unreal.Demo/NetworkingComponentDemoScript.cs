using UnifiedScript;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Unreal.Demo;

/// <summary>A replicated component using its Actor's connection and World-local object identity.</summary>
[ScriptAsset("Unreal/NetworkingComponentDemo", ClassId = "us_762043152e714d68b05a31426591ee02")]
[UnrealScriptClass(UnrealScriptClassKind.ActorComponent,
    GeneratedClassPath = "/Game/UnifiedScriptTests/BP_NetworkingComponentDemo.BP_NetworkingComponentDemo_C")]
public sealed class NetworkingComponentDemoScript : UnrealActorComponentScript
{
    [Expose(Replicated = true)]
    public int Score { get; set; } = 0;

    [Expose(Replicated = true, ReplicationCondition = ReplicationCondition.OwnerOnly)]
    public int OwnerSecret { get; set; } = 0;

    [Expose(Replicated = true, ReplicationCondition = ReplicationCondition.InitialOnly)]
    public int InitialScore { get; set; } = 0;

    [Expose(Replicated = true, ReplicationCondition = ReplicationCondition.SkipOwner)]
    public int ObserverScore { get; set; } = 0;

    [Expose(Replicated = true, ReplicationCondition = ReplicationCondition.InitialOrOwner)]
    public int InitialOrOwnerScore { get; set; } = 0;

    [Expose(Replicated = true)]
    public int Pings { get; set; } = 0;

    [Expose(Replicated = true)]
    public string Label { get; set; } = "";

    public int Notifications { get; set; } = 0;
    public int PreviousScore { get; set; } = 0;
    public int AcknowledgedScore { get; set; } = 0;
    public int BroadcastScore { get; set; } = 0;
    public string PreviousLabel { get; set; } = "";
    public int LabelNotifications { get; set; } = 0;
    public string AcknowledgedLabel { get; set; } = "";
    public UnrealObjectHandle AcknowledgedActor { get; set; } = default;
    public float Gain { get; set; } = 0;
    public bool Enabled { get; set; } = false;
    public UnrealVector Aim { get; set; } = UnrealVector.Zero;
    public UnrealVector RequestedAim { get; set; } = new(1, 2, 3);
    public float BroadcastGain { get; set; } = 0;
    public long ReceivedWide { get; set; } = 0;
    public long AcknowledgedWide { get; set; } = 0;
    public string CommonPayloadError { get; set; } = "";

    [Visual]
    public void InitializeScore(int value)
    {
        Score = value;
        OwnerSecret = value * 10;
        InitialScore = value;
        ObserverScore = value;
        InitialOrOwnerScore = value;
        Label = "Ready";
    }

    [Visual]
    public void RequestAdd(int amount)
    {
        SendRpc(nameof(ServerPing));
        SendRpc(nameof(ServerAdd), amount, 1.5f, true, RequestedAim);
    }

    [UnrealRpc(UnrealRpcTarget.Server, Reliable = true)]
    public void ServerPing()
    {
        Pings += 1;
    }

    [UnrealRpc(UnrealRpcTarget.Server, Reliable = true)]
    public void ServerAdd(int amount, float gain, bool enabled, UnrealVector aim)
    {
        if (amount > 0 && amount <= 10)
        {
            Score += amount;
            OwnerSecret = Score * 10;
            InitialScore = Score;
            ObserverScore = Score;
            InitialOrOwnerScore = Score;
            Gain = gain;
            Enabled = enabled;
            Aim = aim;
            Label = amount == 7 ? "First" : "Second";
            SendRpc(nameof(ClientAccepted), Score, Label, TargetHandle);
            SendRpc(nameof(AllObserved), Score, gain);
        }
    }

    [UnrealRpc(UnrealRpcTarget.Client, Reliable = true)]
    public void ClientAccepted(int value, string label, UnrealObjectHandle actor)
    {
        AcknowledgedScore = value;
        AcknowledgedLabel = label;
        AcknowledgedActor = actor;
    }

    [UnrealRpc(UnrealRpcTarget.Multicast, Reliable = true)]
    public void AllObserved(int value, float gain)
    {
        BroadcastScore = value;
        BroadcastGain = gain;
    }

    [UnrealRepNotify(nameof(Score))]
    public void ScoreChanged(int previous)
    {
        PreviousScore = previous;
        Notifications += 1;
    }

    [UnrealRepNotify(nameof(Label))]
    public void LabelChanged(string previous)
    {
        PreviousLabel = previous;
        LabelNotifications += 1;
    }

    [Visual]
    public void RequestCommonValues()
    {
        SendRpc(nameof(ServerCommonValues), 9007199254740993L, EUnifiedScriptInteropMode.Active,
            RequestedRotation(), RequestedTransform());
    }

    [UnrealRpc(UnrealRpcTarget.Server, Reliable = true)]
    public void ServerCommonValues(long value, EUnifiedScriptInteropMode mode, UnrealRotator rotation, UnrealTransform transform)
    {
        CommonPayloadError = DescribeCommonValueMismatch(value, mode, rotation, transform);
        if (CommonPayloadError == "")
        {
            ReceivedWide = value;
            SendRpc(nameof(ClientCommonValues), value, mode, rotation, transform);
        }
    }

    [UnrealRpc(UnrealRpcTarget.Client, Reliable = true)]
    public void ClientCommonValues(long value, EUnifiedScriptInteropMode mode, UnrealRotator rotation, UnrealTransform transform)
    {
        CommonPayloadError = DescribeCommonValueMismatch(value, mode, rotation, transform);
        if (CommonPayloadError == "") AcknowledgedWide = value;
    }

    private static UnrealRotator RequestedRotation() => new(12.5, -90, 1.25);
    private static UnrealTransform RequestedTransform() =>
        new(new(.5, .5, .5, .5), new(11, -22, 33), new(2, 3, 4));
    // FRotator::NetSerialize uses compressed-short axes. The bridge transports
    // doubles exactly; UE's wire format quantizes and wraps each angle.
    private static double NetworkAngle(double angle) =>
        (((int)System.Math.Floor(angle * (65536.0 / 360.0) + .5)) & 65535) * (360.0 / 65536.0);
    private static UnrealRotator ExpectedNetworkRotation() =>
        new(NetworkAngle(12.5), NetworkAngle(-90), NetworkAngle(1.25));
    private static string DescribeCommonValueMismatch(long value, EUnifiedScriptInteropMode mode, UnrealRotator rotation, UnrealTransform transform) =>
        value == 9007199254740993L && mode == EUnifiedScriptInteropMode.Active &&
        rotation == ExpectedNetworkRotation() && transform == RequestedTransform()
            ? "" : $"Unexpected payload: integer={value}, mode={mode.Value}, rotation={rotation}, transform={transform}";
}
