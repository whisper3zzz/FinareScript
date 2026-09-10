using Finare.Core;

namespace Finare.Graph;

public enum GraphNodePlacement
{
    Unplaced,
    Placed,
}

public enum GraphLayoutOrphanReason
{
    MissingNode,
    MissingFunction,
}

public sealed record GraphLayoutPoint(double X, double Y);

public sealed record GraphLayoutSize(double Width, double Height);

public sealed record GraphLayoutBounds(double X, double Y, double Width, double Height);

public sealed record GraphNodeLayout(
    SemanticId NodeId,
    GraphNodePlacement Placement,
    GraphLayoutPoint? Position,
    GraphLayoutSize? Size,
    bool Collapsed)
{
    public static GraphNodeLayout CreateUnplaced(SemanticId nodeId) => new(
        nodeId,
        GraphNodePlacement.Unplaced,
        null,
        null,
        false);
}

public sealed record GraphCommentRegion(
    string Id,
    string Title,
    GraphLayoutBounds Bounds,
    IReadOnlyList<SemanticId> NodeIds,
    bool Collapsed);

public sealed record GraphRoutingHint(
    SemanticId EdgeId,
    IReadOnlyList<GraphLayoutPoint> ControlPoints);

public sealed record GraphFunctionLayout(
    SemanticId FunctionId,
    IReadOnlyList<GraphNodeLayout> Nodes,
    IReadOnlyList<GraphCommentRegion> CommentRegions,
    IReadOnlyList<GraphRoutingHint> RoutingHints);

public sealed record GraphLayoutOrphan(
    SemanticId FunctionId,
    GraphNodeLayout Node,
    GraphLayoutOrphanReason Reason);

public sealed record GraphLayoutDocument(
    string SchemaVersion,
    IReadOnlyList<GraphFunctionLayout> Functions,
    IReadOnlyList<GraphLayoutOrphan> OrphanedNodes)
{
    public const string CurrentSchemaVersion = "unifiedscript.graph-layout/v0.1";

    public static GraphLayoutDocument CreateEmpty() => new(
        CurrentSchemaVersion,
        [],
        []);
}
