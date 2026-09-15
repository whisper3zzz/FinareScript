using Finare;

namespace Finare.Unreal.Demo;

/// <summary>A server-owned score, an owner-only value, and explicit bidirectional RPCs.</summary>
[ScriptAsset("Unreal/NetworkingDemo", ClassId = "us_762043152e714d68b05a31426591ee01")]
[UnrealScriptClass(UnrealScriptClassKind.Actor,
    GeneratedClassPath = "/Game/UnifiedScriptTests/BP_NetworkingDemo.BP_NetworkingDemo_C")]
public sealed partial class NetworkingDemoScript : UnrealActorScript
{
    [Expose(Replicated = true)]
    public partial int Score { get; set; } = 0;

    [Expose(Replicated = true, ReplicationCondition = ReplicationCondition.OwnerOnly)]
    public partial int OwnerSecret { get; set; } = 0;

    public partial int Notifications { get; set; } = 0;
    public partial int PreviousScore { get; set; } = 0;
    public partial int AcknowledgedScore { get; set; } = 0;
    public partial int BroadcastScore { get; set; } = 0;

    [Visual]
    public void InitializeScore(int value)
    {
        Score = value;
        OwnerSecret = value * 10;
    }

    [Visual]
    public void RequestAdd(int amount)
    {
        SendRpc(nameof(ServerAdd), amount);
    }

    [UnrealRpc(UnrealRpcTarget.Server, Reliable = true)]
    public void ServerAdd(int amount)
    {
        // Gameplay authorization and payload validation remain game logic.
        if (amount > 0 && amount <= 10)
        {
            Score += amount;
            OwnerSecret = Score * 10;
            SendRpc(nameof(ClientAccepted), Score);
            SendRpc(nameof(AllObserved), Score);
        }
    }

    [UnrealRpc(UnrealRpcTarget.Client, Reliable = true)]
    public void ClientAccepted(int value)
    {
        AcknowledgedScore = value;
    }

    [UnrealRpc(UnrealRpcTarget.Multicast, Reliable = true)]
    public void AllObserved(int value)
    {
        BroadcastScore = value;
    }

    [UnrealRepNotify(nameof(Score))]
    public void ScoreChanged(int previous)
    {
        PreviousScore = previous;
        Notifications += 1;
    }
}
