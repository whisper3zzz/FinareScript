using UnifiedScript.Core;

namespace UnifiedScript.Graph;

public sealed class GraphLayoutSynchronizer
{
    public GraphLayoutDocument Create(GraphDocument graph) =>
        Synchronize(graph, GraphLayoutDocument.CreateEmpty());

    public GraphLayoutDocument Synchronize(GraphDocument graph, GraphLayoutDocument layout)
    {
        ArgumentNullException.ThrowIfNull(graph);
        graph = GraphJson.NormalizeAndValidate(graph);
        GraphLayoutDocument normalized = GraphLayoutJson.NormalizeAndValidate(layout);

        IReadOnlyDictionary<SemanticId, GraphFunctionLayout> previousFunctions =
            normalized.Functions.ToDictionary(static function => function.FunctionId);
        var reusableNodes = normalized.Functions
            .SelectMany(function => function.Nodes.Select(node =>
                KeyValuePair.Create((function.FunctionId, node.NodeId), node)))
            .Concat(normalized.OrphanedNodes.Select(orphan =>
                KeyValuePair.Create((orphan.FunctionId, orphan.Node.NodeId), orphan.Node)))
            .ToDictionary();
        var currentFunctionIds = graph.Functions
            .Select(static function => function.Id)
            .ToHashSet();
        var currentNodeKeys = graph.Functions
            .SelectMany(function => function.Nodes.Select(node => (function.Id, node.Id)))
            .ToHashSet();
        var synchronizedFunctions = new List<GraphFunctionLayout>(graph.Functions.Count);

        foreach (GraphFunction function in graph.Functions)
        {
            previousFunctions.TryGetValue(function.Id, out GraphFunctionLayout? previous);
            HashSet<SemanticId> nodeIds = function.Nodes
                .Select(static node => node.Id)
                .ToHashSet();
            HashSet<SemanticId> edgeIds = function.Edges
                .Select(static edge => edge.Id)
                .ToHashSet();
            GraphNodeLayout[] nodes = function.Nodes
                .Select(node => reusableNodes.TryGetValue((function.Id, node.Id), out GraphNodeLayout? reusable)
                    ? reusable
                    : GraphNodeLayout.CreateUnplaced(node.Id))
                .ToArray();
            GraphCommentRegion[] comments = previous?.CommentRegions
                .Select(region => region with
                {
                    NodeIds = region.NodeIds.Where(nodeIds.Contains).ToArray(),
                })
                .ToArray() ?? [];
            GraphRoutingHint[] routes = previous?.RoutingHints
                .Where(route => edgeIds.Contains(route.EdgeId))
                .ToArray() ?? [];
            synchronizedFunctions.Add(new GraphFunctionLayout(
                function.Id,
                nodes,
                comments,
                routes));
        }

        var orphans = normalized.OrphanedNodes
            .Where(orphan => !currentNodeKeys.Contains((orphan.FunctionId, orphan.Node.NodeId)))
            .ToDictionary(
                static orphan => (orphan.FunctionId, orphan.Node.NodeId),
                orphan => orphan with
                {
                    Reason = currentFunctionIds.Contains(orphan.FunctionId)
                        ? GraphLayoutOrphanReason.MissingNode
                        : GraphLayoutOrphanReason.MissingFunction,
                });
        foreach (GraphFunctionLayout function in normalized.Functions)
        {
            foreach (GraphNodeLayout node in function.Nodes)
            {
                var key = (function.FunctionId, node.NodeId);
                if (currentNodeKeys.Contains(key))
                {
                    continue;
                }

                GraphLayoutOrphanReason reason = currentFunctionIds.Contains(function.FunctionId)
                    ? GraphLayoutOrphanReason.MissingNode
                    : GraphLayoutOrphanReason.MissingFunction;
                orphans[key] = new GraphLayoutOrphan(function.FunctionId, node, reason);
            }
        }

        return GraphLayoutJson.NormalizeAndValidate(new GraphLayoutDocument(
            GraphLayoutDocument.CurrentSchemaVersion,
            synchronizedFunctions,
            orphans.Values.ToArray()));
    }

}
