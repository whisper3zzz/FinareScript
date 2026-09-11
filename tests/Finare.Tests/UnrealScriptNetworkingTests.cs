using System.Text.Json;
using Finare;
using Finare.Core;
using Finare.CSharp;
using Finare.Graph;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed class UnrealScriptNetworkingTests
{
    private const string Source = """
        using Finare;
        using Finare.Unreal;
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

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ComponentRpcUsesHostAndAuthorityUsesOwnerWithTypedPayloads(bool authority, bool delegates)
    {
        var host = UnrealObjectHandle.Create(1, 1);
        var actor = UnrealObjectHandle.Create(2, 1);
        var bridge = new ComponentBridge(host, actor, authority);
        var script = new ComponentProbe();
        IUnrealManagedScript lifecycle = script;
        lifecycle.OnStart(new(bridge, host, actor, 1, 1));
        Assert.Equal(authority, script.HasAuthority);
        lifecycle.OnTick(0.01f);
        Assert.Equal(authority ? 11 : 10, bridge.Score);
        script.RequestAll(delegates);
        Assert.Equal(5, bridge.Calls.Count);
        Assert.Equal(Enumerable.Range(0, 5), bridge.Calls.Select(c => c.Arguments.Count));
        Assert.Equal(new object?[] { 7, 1.5f, true, new UnrealVector(1, 2, 3) }, bridge.Calls[4].Arguments);
        Assert.Equal(new object?[] { 7, "payload", actor }, bridge.Calls[3].Arguments);
        lifecycle.OnStop();
        Assert.Throws<InvalidOperationException>(() => script.RequestAll(delegates));
    }

    [UnrealScriptClass(UnrealScriptClassKind.ActorComponent, GeneratedClassPath = "/Game/Tests/BP_NetworkComponent.BP_NetworkComponent_C")]
    private sealed class ComponentProbe : UnrealActorComponentScript
    {
        [Expose(Replicated = true)] public int Score { get; set; }
        protected override void OnTick(float deltaSeconds) => Score++;
        [UnrealRpc(UnrealRpcTarget.Server)] public void Zero() { }
        [UnrealRpc(UnrealRpcTarget.Server)] public void One(int a) { }
        [UnrealRpc(UnrealRpcTarget.Server)] public void Two(int a, float b) { }
        [UnrealRpc(UnrealRpcTarget.Server)] public void Three(int a, string b, UnrealObjectHandle c) { }
        [UnrealRpc(UnrealRpcTarget.Server)] public void Four(int a, float b, bool c, UnrealVector d) { }
        public void RequestAll(bool delegates)
        {
            if (delegates)
            {
                SendRpc(Zero);
                SendRpc(One, 7);
                SendRpc(Two, 7, 1.5f);
                SendRpc(Three, 7, "payload", TargetHandle);
                SendRpc(Four, 7, 1.5f, true, new UnrealVector(1, 2, 3));
            }
            else
            {
                SendRpc(nameof(Zero));
                SendRpc(nameof(One), 7);
                SendRpc(nameof(Two), 7, 1.5f);
                SendRpc(nameof(Three), 7, "payload", TargetHandle);
                SendRpc(nameof(Four), 7, 1.5f, true, new UnrealVector(1, 2, 3));
            }
        }
    }

    private sealed class ComponentBridge(UnrealObjectHandle host, UnrealObjectHandle actor, bool authority) : IUnrealInvocationBridge
    {
        public int Score = 10;
        public List<(string Path, IReadOnlyList<object?> Arguments)> Calls = [];
        public T ReadProperty<T>(UnrealObjectHandle target, string path)
        {
            Assert.Equal(host, target);
            return (T)(object)Score;
        }
        public void WriteProperty<T>(UnrealObjectHandle target, string path, T value)
        {
            Assert.Equal(host, target);
            Assert.True(authority);
            Score = (int)(object)value!;
        }
        public T Invoke<T>(UnrealObjectHandle target, string path, IReadOnlyList<object?> arguments)
        {
            Assert.Equal(actor, target);
            Assert.Equal("/Script/Engine.Actor:HasAuthority", path);
            return (T)(object)authority;
        }
        public void Invoke(UnrealObjectHandle target, string path, IReadOnlyList<object?> arguments)
        {
            Assert.Equal(host, target);
            Assert.StartsWith("/Game/Tests/BP_NetworkComponent.BP_NetworkComponent_C:", path);
            Calls.Add((path, arguments));
        }
    }
}
