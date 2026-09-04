using System.Text.Json;
using UnifiedScript;
using UnifiedScript.Core;
using UnifiedScript.CSharp;
using UnifiedScript.Graph;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealScriptNetworkingTests
{
    private const string Source = """
        using UnifiedScript;
        using UnifiedScript.Unreal;
        [ScriptAsset("Tests/Network")]
        [UnrealScriptClass(UnrealScriptClassKind.Actor, GeneratedClassPath = "/Game/Tests/BP_Network.BP_Network_C")]
        public sealed class NetworkScript : UnrealActorScript
        {
            [Expose(Replicated = true, ReplicationCondition = ReplicationCondition.OwnerOnly)]
            public int Score { get; set; } = 0;
            [UnrealRepNotify(nameof(Score))]
            public void ScoreChanged(int previous) { Notify(previous); }
            [UnrealRpc(UnrealRpcTarget.Server, Reliable = true)]
            public void ServerAdd(int amount) { Score += amount; }
            [Visual]
            public void Request(int amount) { SendRpc(nameof(ServerAdd), amount); }
            private void Notify(int previous) { }
        }
        """;

    private static CSharpAnalysisResult Analyze(string source) => new CSharpSemanticPipeline().Analyze(
        new CSharpCompilationRequest("Network", [new("Network.cs", source)], [typeof(UnrealActorScript).Assembly.Location]));

    [Fact]
    public void NetworkingDeclarationsSerializeAndProjectWithoutNativeFlagValues()
    {
        var result = Analyze(Source);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var asset = Assert.Single(result.ScriptAssets!.Assets);
        Assert.Equal(ReplicationCondition.OwnerOnly, Assert.Single(asset.States).Exposure.ReplicationCondition);
        var rpc = asset.Functions.Single(f => f.Name == "ServerAdd");
        Assert.Equal(new ScriptRpcDeclaration(ScriptRpcTarget.Server, true), rpc.Rpc);
        Assert.Equal("Score", asset.Functions.Single(f => f.Name == "ScoreChanged").RepNotifyProperty);
        string json = ScriptAssetJson.Serialize(result.ScriptAssets);
        Assert.Equal(json, ScriptAssetJson.Serialize(ScriptAssetJson.Deserialize(json)));
        Assert.Contains("\"target\": \"server\"", json);
        var graph = new HirGraphProjector().Project(result.Hir!, result.Semantics!);
        Assert.Equal(3, graph.Functions.Count);
        Assert.Contains(graph.Functions.SelectMany(f => f.Nodes), n => n.Kind == "invocation" && n.Properties["method"].Contains("SendRpc"));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("condition")]
    [InlineData("not-replicated")]
    [InlineData("wrong-notify-type")]
    [InlineData("unknown-property")]
    [InlineData("rpc-return")]
    [InlineData("rpc-ref")]
    [InlineData("wrong-class")]
    [InlineData("conflicting-binding")]
    [InlineData("rpc-many-arguments")]
    [InlineData("rpc-array")]
    public void InvalidNetworkContractsFailBeforePublication(string mutation)
    {
        string source = mutation switch
        {
            "target" => Source.Replace("UnrealRpcTarget.Server", "(UnrealRpcTarget)42"),
            "condition" => Source.Replace("ReplicationCondition.OwnerOnly", "(ReplicationCondition)42"),
            "not-replicated" => Source.Replace("Replicated = true", "Replicated = false"),
            "wrong-notify-type" => Source.Replace("ScoreChanged(int previous)", "ScoreChanged(float previous)").Replace("Notify(previous)", "Notify(0)"),
            "unknown-property" => Source.Replace("UnrealRepNotify(nameof(Score))", "UnrealRepNotify(\"Missing\")"),
            "rpc-return" => Source.Replace("void ServerAdd(int amount) { Score += amount; }", "int ServerAdd(int amount) { return amount; }"),
            "rpc-ref" => Source.Replace("ServerAdd(int amount)", "ServerAdd(ref int amount)"),
            "wrong-class" => Source.Replace("UnrealScriptClassKind.Actor", "UnrealScriptClassKind.Object"),
            "rpc-many-arguments" => Source.Replace("ServerAdd(int amount)", "ServerAdd(int amount, int b, int c, int d, int e)"),
            "rpc-array" => Source.Replace("ServerAdd(int amount) { Score += amount; }", "ServerAdd(System.Collections.Generic.IReadOnlyList<int> amounts) { Score += 1; }"),
            _ => Source.Replace("[UnrealRpc(", "[UnrealBlueprintEvent]\n[UnrealRpc("),
        };
        var result = Analyze(source);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == DeclarationDiagnosticCodes.InvalidNetworkingContract);
    }

    [Fact]
    public void RawJsonCannotBypassNetworkValidation()
    {
        var result = Analyze(Source);
        string json = ScriptAssetJson.Serialize(result.ScriptAssets!);
        Assert.Throws<JsonException>(() => ScriptAssetJson.Deserialize(json.Replace("\"target\": \"server\"", "\"target\": \"none\"")));
        Assert.Throws<JsonException>(() => ScriptAssetJson.Deserialize(json.Replace("\"replicated\": true", "\"replicated\": false")));
        Assert.Throws<JsonException>(() => ScriptAssetJson.Deserialize(json.Replace("\"repNotifyProperty\": \"Score\"", "\"repNotifyProperty\": \"Missing\"")));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void StateFlushHonorsAuthorityAndExplicitRpcKeepsTheReceiveResult(bool authority)
    {
        var bridge = new NetworkBridge(authority);
        var script = new NetworkProbe();
        IUnrealManagedScript lifecycle = script;
        var handle = UnrealObjectHandle.Create(0, 1);
        lifecycle.OnStart(new(bridge, handle, handle, 1, 1));
        lifecycle.OnTick(0.01f);
        Assert.Equal(authority ? 11 : 10, bridge.Score);
        Assert.Equal(bridge.Score, script.Score);
        script.Request(7);
        Assert.Equal(1, bridge.Sends);
        Assert.Equal(authority ? 18 : 17, script.Score);
        Assert.Throws<InvalidOperationException>(() => script.SendNonRpc());
        lifecycle.OnStop();
        Assert.Throws<InvalidOperationException>(() => script.Request(1));
    }

    [UnrealScriptClass(UnrealScriptClassKind.Actor, GeneratedClassPath = "/Game/Tests/BP_Network.BP_Network_C")]
    private sealed class NetworkProbe : UnrealActorScript
    {
        [Expose(Replicated = true)] public int Score { get; set; }
        protected override void OnTick(float deltaSeconds) => Score++;
        [UnrealRpc(UnrealRpcTarget.Server)] public void ServerAdd(int amount) => Score += amount;
        public void Request(int amount) => SendRpc(ServerAdd, amount);
        public void SendNonRpc() => SendRpc(Request, 1);
    }

    private sealed class NetworkBridge(bool authority) : IUnrealInvocationBridge
    {
        public int Score = 10;
        public int Sends;
        public T ReadProperty<T>(UnrealObjectHandle target, string path) => (T)(object)Score;
        public void WriteProperty<T>(UnrealObjectHandle target, string path, T value)
        {
            Assert.True(authority);
            Score = (int)(object)value!;
        }
        public T Invoke<T>(UnrealObjectHandle target, string path, IReadOnlyList<object?> arguments)
        {
            Assert.Equal("/Script/Engine.Actor:HasAuthority", path);
            return (T)(object)authority;
        }
        public void Invoke(UnrealObjectHandle target, string path, IReadOnlyList<object?> arguments)
        {
            Assert.EndsWith(":ServerAdd", path);
            Sends++;
            Score += (int)arguments[0]!;
        }
    }
}
