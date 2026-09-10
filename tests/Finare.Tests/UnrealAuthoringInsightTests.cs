using Finare.Core;
using Finare.Unreal;

namespace Finare.Tests;

public sealed class UnrealAuthoringInsightTests
{
    [Fact]
    public void SnapshotJoinsSemanticSourceCostAndGeneratedApiRoutes()
    {
        SemanticId compilationId = SemanticId.FromKey("insight-compilation");
        SemanticId configurationId = SemanticId.FromKey("insight-configuration");
        const string methodId = "us_method_profile";
        const string nodeId = "us_node_trace";
        var span = new SourceSpan(
            "Scripts/BlockBreaker/Gun.cs",
            new SourcePosition(18, 8),
            new SourcePosition(18, 46));
        var trace = CreateTrace(
            new UnrealGameplayProfileNode(
                methodId,
                nodeId,
                4,
                600_000,
                [
                    new UnrealGameplayProfileBoundary("batchTrace", 4, 200_000),
                    new UnrealGameplayProfileBoundary("directThunk", 4, 100_000),
                ]));
        var request = new UnrealAuthoringInsightRequest(
            UnrealAuthoringInsightRequest.CurrentSchemaVersion,
            trace,
            methodId,
            nodeId);

        UnrealAuthoringInsightSnapshot snapshot = UnrealAuthoringInsight.Create(
            compilationId,
            configurationId,
            [new UnrealAuthoringInsightLocation(
                methodId,
                nodeId,
                "us_action_trace",
                "Batch weapon trace",
                "Queues the frame's weapon query.",
                "Call",
                span)],
            request,
            CreateApiBrowser());

        Assert.Equal(UnrealAuthoringInsightSnapshot.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Equal(compilationId, snapshot.SourceCompilationId);
        Assert.Equal(1, snapshot.Summary.ResolvedNodeCount);
        Assert.Equal(0, snapshot.Summary.UnresolvedNodeCount);
        Assert.Equal(1_000_000, snapshot.Summary.AverageFrameNanoseconds);
        UnrealAuthoringInsightNode node = Assert.Single(snapshot.Nodes);
        Assert.True(node.Focused);
        Assert.Equal("Batch weapon trace", node.Title);
        Assert.Equal(span, node.SourceSpan);
        Assert.Equal(150_000, node.AverageHitNanoseconds);
        Assert.Equal(60, node.PercentOfTrace, 6);
        UnrealAuthoringInsightBoundary managed = Assert.Single(
            node.Boundaries,
            static boundary => boundary.Name == "managedCode");
        Assert.Equal(300_000, managed.TotalNanoseconds);
        UnrealAuthoringInsightBoundary direct = Assert.Single(
            node.Boundaries,
            static boundary => boundary.Name == "directThunk");
        Assert.Equal(nameof(UnrealBindingDisposition.DirectThunk), direct.ContractRoute);
        Assert.Equal(2, direct.CatalogMemberCount);
        UnrealAuthoringInsightBoundary dataPlane = Assert.Single(
            node.Boundaries,
            static boundary => boundary.Name == "batchTrace");
        Assert.Equal(nameof(UnrealBindingDisposition.DataPlaneOperation), dataPlane.ContractRoute);
        Assert.Equal(1, dataPlane.CatalogMemberCount);
        Assert.Equal(
            ["/Script/Engine.Actor:GetActorLocation", "/Script/Engine.Actor:SetActorLocation"],
            snapshot.ApiRoutes.Single(static route =>
                route.RuntimeBoundary == "directThunk").CatalogExamples);

        string json = UnrealAuthoringInsightJson.Serialize(snapshot);
        Assert.Equal(json, UnrealAuthoringInsightJson.Serialize(
            UnrealAuthoringInsightJson.Deserialize(json)));
    }

    [Fact]
    public void SnapshotKeepsStaleRuntimeNodeVisibleAsUnresolved()
    {
        UnrealGameplayProfileTrace trace = CreateTrace(
            new UnrealGameplayProfileNode(
                "stale_method",
                "stale_node",
                1,
                25_000,
                []));
        UnrealAuthoringInsightSnapshot snapshot = UnrealAuthoringInsight.Create(
            SemanticId.FromKey("stale-compilation"),
            SemanticId.FromKey("stale-configuration"),
            [],
            new UnrealAuthoringInsightRequest(
                UnrealAuthoringInsightRequest.CurrentSchemaVersion,
                trace,
                null,
                null));

        Assert.Equal(0, snapshot.Summary.ResolvedNodeCount);
        Assert.Equal(1, snapshot.Summary.UnresolvedNodeCount);
        UnrealAuthoringInsightNode node = Assert.Single(snapshot.Nodes);
        Assert.Null(node.ActionId);
        Assert.Null(node.SourceSpan);
        Assert.Equal("Unresolved semantic node", node.Title);
    }

    private static UnrealGameplayProfileTrace CreateTrace(
        params UnrealGameplayProfileNode[] nodes)
    {
        const long totalNanoseconds = 1_000_000;
        var pressure = new UnrealGameplayProfileBufferPressure(0, 0, 0, 0);
        UnrealGameplayProfileBoundary[] boundaries = nodes
            .SelectMany(static node => node.Boundaries)
            .GroupBy(static boundary => boundary.Name, StringComparer.Ordinal)
            .Select(static group => new UnrealGameplayProfileBoundary(
                group.Key,
                group.Sum(static boundary => boundary.Count),
                group.Sum(static boundary => boundary.TotalNanoseconds)))
            .OrderBy(static boundary => boundary.Name, StringComparer.Ordinal)
            .ToArray();
        var frame = new UnrealGameplayProfileFrame(
            1,
            "301",
            7,
            "BlockBreaker.Gun",
            0.016f,
            "ManagedTick",
            [new UnrealGameplayProfileBoundary("ManagedTick", 1, totalNanoseconds)],
            totalNanoseconds,
            128,
            boundaries,
            nodes,
            pressure);
        return new UnrealGameplayProfileTrace(
            UnrealGameplayProfileTrace.CurrentSchemaVersion,
            "world-41-script-7",
            DateTimeOffset.UnixEpoch,
            "41",
            "7",
            120,
            0,
            [frame],
            new UnrealGameplayProfileAggregate(
                1,
                totalNanoseconds,
                128,
                frame.WorldPhases,
                boundaries,
                nodes,
                pressure));
    }

    private static UnrealApiBrowserDocument CreateApiBrowser() => new(
        UnrealApiBrowserDocument.CurrentSchemaVersion,
        "BlockBreaker.Runtime",
        "BlockBreaker.Generated",
        new UnrealEngineIdentity(5, 8, 1, "//UE5/Release-5.8"),
        new UnrealApiBrowserSources("contract", "nodes", "coverage", new string('a', 64)),
        new UnrealApiBrowserSummary(1, 3, 3, 2, 0, 1, 0, 0, 0),
        [new UnrealApiBrowserType(
            "/Script/Engine.Actor",
            "Actor",
            "Actor",
            "Gameplay",
            [
                CreateMember(
                    "/Script/Engine.Actor:SetActorLocation",
                    UnrealBindingDisposition.DirectThunk),
                CreateMember(
                    "/Script/Engine.Actor:GetActorLocation",
                    UnrealBindingDisposition.DirectThunk),
                CreateMember(
                    "/Script/Engine.World:BatchLineTrace",
                    UnrealBindingDisposition.DataPlaneOperation),
            ])],
        []);

    private static UnrealApiBrowserMember CreateMember(
        string path,
        UnrealBindingDisposition disposition) => new(
        $"node:{path}",
        path,
        "0x0000000000000001",
        "function",
        path[(path.LastIndexOf(':') + 1)..],
        "Gameplay",
        path,
        false,
        disposition,
        true,
        new UnrealApiBrowserConstraint(
            UnrealApiThreadRequirement.GameThread,
            UnrealApiWorldRequirement.WorldContext,
            "WorldContextObject",
            false,
            false,
            null,
            false,
            null),
        [],
        []);
}
