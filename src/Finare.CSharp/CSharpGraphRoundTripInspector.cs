using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Finare.Core;
using Finare.Graph;

namespace Finare.CSharp;

public sealed record CSharpGraphRoundTripReport(
    string SchemaVersion,
    int PreservedSemanticIds,
    int IntroducedSemanticIds,
    int RetiredSemanticIds,
    int PreservedSourceComments,
    int AddedSourceComments,
    int RemovedSourceComments,
    bool LayoutChecked,
    int PreservedNodeLayouts,
    int PreservedGraphComments,
    int PreservedRoutingHints,
    int NewUnplacedNodes)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.csharp-graph-round-trip/v0.1";
}

public sealed record CSharpGraphRoundTripInspection(
    CSharpGraphRoundTripReport Report,
    GraphLayoutDocument? CandidateLayout);

/// <summary>
/// Builds the preservation evidence shared by C# and Graph authoring previews.
/// The layout projection is synchronized in memory and validated before it can
/// join the same publication transaction as source and identity sidecars.
/// </summary>
public sealed class CSharpGraphRoundTripInspector
{
    public CSharpGraphRoundTripInspection Inspect(
        CSharpAuthoringSnapshot baseline,
        CSharpAuthoringSnapshot candidate,
        GraphLayoutDocument? baselineLayout = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        HashSet<SemanticId> baselineIds = GetSemanticIds(baseline.Graph);
        HashSet<SemanticId> candidateIds = GetSemanticIds(candidate.Graph);
        int preservedSemanticIds = baselineIds.Count(candidateIds.Contains);
        int introducedSemanticIds = candidateIds.Count(id => !baselineIds.Contains(id));
        int retiredSemanticIds = baselineIds.Count(id => !candidateIds.Contains(id));

        IReadOnlyDictionary<string, int> baselineComments = GetSourceComments(baseline.Request);
        IReadOnlyDictionary<string, int> candidateComments = GetSourceComments(candidate.Request);
        int preservedSourceComments = baselineComments.Sum(pair =>
            Math.Min(pair.Value, candidateComments.GetValueOrDefault(pair.Key)));
        int baselineCommentCount = baselineComments.Values.Sum();
        int candidateCommentCount = candidateComments.Values.Sum();

        GraphLayoutDocument? candidateLayout = null;
        int preservedNodeLayouts = 0;
        int preservedGraphComments = 0;
        int preservedRoutingHints = 0;
        int newUnplacedNodes = 0;
        if (baselineLayout is not null)
        {
            baselineLayout = GraphLayoutJson.NormalizeAndValidate(baselineLayout);
            candidateLayout = new GraphLayoutSynchronizer().Synchronize(
                candidate.Graph,
                baselineLayout);
            VerifyLayoutPreservation(
                baseline.Graph,
                candidate.Graph,
                baselineLayout,
                candidateLayout,
                out preservedNodeLayouts,
                out preservedGraphComments,
                out preservedRoutingHints,
                out newUnplacedNodes);
        }

        return new CSharpGraphRoundTripInspection(
            new CSharpGraphRoundTripReport(
                CSharpGraphRoundTripReport.CurrentSchemaVersion,
                preservedSemanticIds,
                introducedSemanticIds,
                retiredSemanticIds,
                preservedSourceComments,
                candidateCommentCount - preservedSourceComments,
                baselineCommentCount - preservedSourceComments,
                baselineLayout is not null,
                preservedNodeLayouts,
                preservedGraphComments,
                preservedRoutingHints,
                newUnplacedNodes),
            candidateLayout);
    }

    private static HashSet<SemanticId> GetSemanticIds(GraphDocument graph) => graph.Functions
        .SelectMany(static function =>
            new[] { function.Id }
                .Concat(function.Nodes.Select(static node => node.Id))
                .Concat(function.Edges.Select(static edge => edge.Id))
                .Concat(function.Regions.Select(static region => region.Id)))
        .ToHashSet();

    private static IReadOnlyDictionary<string, int> GetSourceComments(
        CSharpCompilationRequest request)
    {
        var comments = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (CSharpSourceDocument source in request.Sources)
        {
            SyntaxNode root = CSharpSyntaxTree.ParseText(
                source.Source,
                path: source.LogicalPath).GetRoot();
            foreach (SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: true))
            {
                if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
                    !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) &&
                    !trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) &&
                    !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                {
                    continue;
                }

                string key = source.LogicalPath + "\0" + trivia.ToFullString();
                comments[key] = checked(comments.GetValueOrDefault(key) + 1);
            }
        }
        return comments;
    }

    private static void VerifyLayoutPreservation(
        GraphDocument baselineGraph,
        GraphDocument candidateGraph,
        GraphLayoutDocument baselineLayout,
        GraphLayoutDocument candidateLayout,
        out int preservedNodeLayouts,
        out int preservedGraphComments,
        out int preservedRoutingHints,
        out int newUnplacedNodes)
    {
        HashSet<(SemanticId FunctionId, SemanticId NodeId)> baselineGraphNodes = baselineGraph.Functions
            .SelectMany(function => function.Nodes.Select(node => (function.Id, node.Id)))
            .ToHashSet();
        HashSet<(SemanticId FunctionId, SemanticId NodeId)> candidateGraphNodes = candidateGraph.Functions
            .SelectMany(function => function.Nodes.Select(node => (function.Id, node.Id)))
            .ToHashSet();
        HashSet<(SemanticId FunctionId, SemanticId EdgeId)> candidateGraphEdges = candidateGraph.Functions
            .SelectMany(function => function.Edges.Select(edge => (function.Id, edge.Id)))
            .ToHashSet();
        Dictionary<SemanticId, GraphFunctionLayout> candidateFunctions = candidateLayout.Functions
            .ToDictionary(static function => function.FunctionId);

        preservedNodeLayouts = 0;
        preservedGraphComments = 0;
        preservedRoutingHints = 0;
        newUnplacedNodes = 0;
        foreach (GraphFunctionLayout baselineFunction in baselineLayout.Functions)
        {
            if (!candidateFunctions.TryGetValue(
                    baselineFunction.FunctionId,
                    out GraphFunctionLayout? candidateFunction))
            {
                continue;
            }

            Dictionary<SemanticId, GraphNodeLayout> candidateNodes = candidateFunction.Nodes
                .ToDictionary(static node => node.NodeId);
            HashSet<SemanticId> currentNodeIds = candidateGraphNodes
                .Where(key => key.FunctionId == baselineFunction.FunctionId)
                .Select(static key => key.NodeId)
                .ToHashSet();
            foreach (GraphNodeLayout baselineNode in baselineFunction.Nodes)
            {
                if (!currentNodeIds.Contains(baselineNode.NodeId))
                {
                    continue;
                }
                if (!candidateNodes.TryGetValue(baselineNode.NodeId, out GraphNodeLayout? value) ||
                    value != baselineNode)
                {
                    throw new InvalidDataException(
                        $"Graph layout for '{baselineFunction.FunctionId}/{baselineNode.NodeId}' " +
                        "changed during semantic synchronization.");
                }
                preservedNodeLayouts++;
            }

            Dictionary<string, GraphCommentRegion> candidateComments = candidateFunction.CommentRegions
                .ToDictionary(static comment => comment.Id, StringComparer.Ordinal);
            foreach (GraphCommentRegion baselineComment in baselineFunction.CommentRegions)
            {
                GraphCommentRegion expected = baselineComment with
                {
                    NodeIds = baselineComment.NodeIds.Where(currentNodeIds.Contains).ToArray(),
                };
                if (!candidateComments.TryGetValue(
                        baselineComment.Id,
                        out GraphCommentRegion? actual) ||
                    !CommentEquals(actual, expected))
                {
                    throw new InvalidDataException(
                        $"Graph comment '{baselineFunction.FunctionId}/{baselineComment.Id}' " +
                        "changed during semantic synchronization.");
                }
                preservedGraphComments++;
            }

            Dictionary<SemanticId, GraphRoutingHint> candidateRoutes = candidateFunction.RoutingHints
                .ToDictionary(static route => route.EdgeId);
            foreach (GraphRoutingHint baselineRoute in baselineFunction.RoutingHints)
            {
                if (!candidateGraphEdges.Contains(
                        (baselineFunction.FunctionId, baselineRoute.EdgeId)))
                {
                    continue;
                }
                if (!candidateRoutes.TryGetValue(
                        baselineRoute.EdgeId,
                        out GraphRoutingHint? actual) ||
                    !RouteEquals(actual, baselineRoute))
                {
                    throw new InvalidDataException(
                        $"Graph route '{baselineFunction.FunctionId}/{baselineRoute.EdgeId}' " +
                        "changed during semantic synchronization.");
                }
                preservedRoutingHints++;
            }
        }

        var reusableLayoutKeys = baselineLayout.Functions
            .SelectMany(function => function.Nodes.Select(node => (function.FunctionId, node.NodeId)))
            .Concat(baselineLayout.OrphanedNodes.Select(orphan =>
                (orphan.FunctionId, orphan.Node.NodeId)))
            .ToHashSet();
        Dictionary<(SemanticId FunctionId, SemanticId NodeId), GraphNodeLayout> candidateNodesByKey =
            candidateLayout.Functions.SelectMany(function => function.Nodes.Select(node =>
                    KeyValuePair.Create((function.FunctionId, node.NodeId), node)))
                .ToDictionary();
        foreach ((SemanticId functionId, SemanticId nodeId) in candidateGraphNodes.Where(key =>
            !baselineGraphNodes.Contains(key) && !reusableLayoutKeys.Contains(key)))
        {
            if (!candidateNodesByKey.TryGetValue((functionId, nodeId), out GraphNodeLayout? layout) ||
                layout.Placement != GraphNodePlacement.Unplaced)
            {
                throw new InvalidDataException(
                    $"New graph node '{functionId}/{nodeId}' must enter the layout as unplaced.");
            }
            newUnplacedNodes++;
        }
    }

    private static bool CommentEquals(
        GraphCommentRegion actual,
        GraphCommentRegion expected) =>
        string.Equals(actual.Id, expected.Id, StringComparison.Ordinal) &&
        string.Equals(actual.Title, expected.Title, StringComparison.Ordinal) &&
        actual.Bounds == expected.Bounds &&
        actual.Collapsed == expected.Collapsed &&
        actual.NodeIds.SequenceEqual(expected.NodeIds);

    private static bool RouteEquals(
        GraphRoutingHint actual,
        GraphRoutingHint expected) =>
        actual.EdgeId == expected.EdgeId &&
        actual.ControlPoints.SequenceEqual(expected.ControlPoints);
}
