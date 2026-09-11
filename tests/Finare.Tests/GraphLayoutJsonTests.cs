using System.Text.Json;
using Finare.Core;
using Finare.Graph;

namespace Finare.Tests;

[Trait("Category", "Graph")]
public sealed class GraphLayoutJsonTests
{
    [Fact]
    public void Serialize_NormalizesOrderAndRoundTripsDeterministically()
    {
        SemanticId functionId = SemanticId.FromKey("layout-function");
        SemanticId firstNodeId = SemanticId.FromKey("layout-node-a");
        SemanticId secondNodeId = SemanticId.FromKey("layout-node-b");
        SemanticId edgeId = SemanticId.FromKey("layout-edge");
        var document = new GraphLayoutDocument(
            GraphLayoutDocument.CurrentSchemaVersion,
            [new GraphFunctionLayout(
                functionId,
                [
                    GraphNodeLayout.CreateUnplaced(secondNodeId),
                    new GraphNodeLayout(
                        firstNodeId,
                        GraphNodePlacement.Placed,
                        new GraphLayoutPoint(-24.5, 100),
                        new GraphLayoutSize(240, 96),
                        true),
                ],
                [new GraphCommentRegion(
                    "damage-flow",
                    "Damage flow",
                    new GraphLayoutBounds(-50, 50, 700, 300),
                    [secondNodeId, firstNodeId],
                    false)],
                [new GraphRoutingHint(
                    edgeId,
                    [new GraphLayoutPoint(10, 20), new GraphLayoutPoint(30, 40)])])],
            []);

        string json = GraphLayoutJson.Serialize(document);
        GraphLayoutDocument roundTrip = GraphLayoutJson.Deserialize(json);

        Assert.Equal(firstNodeId, Assert.Single(roundTrip.Functions).Nodes[0].NodeId);
        Assert.Equal(
            [firstNodeId, secondNodeId],
            Assert.Single(Assert.Single(roundTrip.Functions).CommentRegions).NodeIds);
        Assert.Contains("\"placement\": \"placed\"", json, StringComparison.Ordinal);
        Assert.Equal(json, GraphLayoutJson.Serialize(roundTrip));
    }

    [Fact]
    public void Deserialize_RejectsGeometryThatContradictsPlacement()
    {
        SemanticId functionId = SemanticId.FromKey("invalid-layout-function");
        SemanticId nodeId = SemanticId.FromKey("invalid-layout-node");
        var document = new GraphLayoutDocument(
            GraphLayoutDocument.CurrentSchemaVersion,
            [new GraphFunctionLayout(
                functionId,
                [new GraphNodeLayout(
                    nodeId,
                    GraphNodePlacement.Unplaced,
                    new GraphLayoutPoint(0, 0),
                    null,
                    false)],
                [],
                [])],
            []);
        string json = JsonSerializer.Serialize(document, UnifiedJson.CreateOptions());

        JsonException exception = Assert.Throws<JsonException>(() => GraphLayoutJson.Deserialize(json));

        Assert.Contains("cannot carry a position or size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_RejectsNodeThatIsBothActiveAndOrphaned()
    {
        SemanticId functionId = SemanticId.FromKey("duplicate-layout-function");
        GraphNodeLayout node = GraphNodeLayout.CreateUnplaced(
            SemanticId.FromKey("duplicate-layout-node"));
        var document = new GraphLayoutDocument(
            GraphLayoutDocument.CurrentSchemaVersion,
            [new GraphFunctionLayout(functionId, [node], [], [])],
            [new GraphLayoutOrphan(functionId, node, GraphLayoutOrphanReason.MissingNode)]);
        string json = JsonSerializer.Serialize(document, UnifiedJson.CreateOptions());

        JsonException exception = Assert.Throws<JsonException>(() => GraphLayoutJson.Deserialize(json));

        Assert.Contains("both active and orphaned", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_RejectsNonPositiveNodeSize()
    {
        var document = new GraphLayoutDocument(
            GraphLayoutDocument.CurrentSchemaVersion,
            [new GraphFunctionLayout(
                SemanticId.FromKey("size-function"),
                [new GraphNodeLayout(
                    SemanticId.FromKey("size-node"),
                    GraphNodePlacement.Placed,
                    new GraphLayoutPoint(0, 0),
                    new GraphLayoutSize(0, 100),
                    false)],
                [],
                [])],
            []);

        Assert.Throws<JsonException>(() => GraphLayoutJson.Serialize(document));
    }
}
