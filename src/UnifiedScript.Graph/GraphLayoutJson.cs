using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.Graph;

public static class GraphLayoutJson
{
    public static string Serialize(GraphLayoutDocument document, bool writeIndented = true)
    {
        GraphLayoutDocument normalized = NormalizeAndValidate(document);
        return JsonSerializer.Serialize(normalized, UnifiedJson.CreateOptions(writeIndented));
    }

    public static GraphLayoutDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        GraphLayoutDocument? document = JsonSerializer.Deserialize<GraphLayoutDocument>(
            json,
            UnifiedJson.CreateOptions());
        if (document is null)
        {
            throw new JsonException("The graph layout document was empty.");
        }

        return NormalizeAndValidate(document);
    }

    public static GraphLayoutDocument NormalizeAndValidate(GraphLayoutDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != GraphLayoutDocument.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported graph layout schema '{document.SchemaVersion}'. " +
                $"Expected '{GraphLayoutDocument.CurrentSchemaVersion}'.");
        }

        if (document.Functions is null || document.OrphanedNodes is null)
        {
            throw new JsonException("Graph layout functions and orphaned nodes cannot be null.");
        }

        var functionIds = new HashSet<SemanticId>();
        var activeNodeKeys = new HashSet<(SemanticId FunctionId, SemanticId NodeId)>();
        foreach (GraphFunctionLayout? function in document.Functions)
        {
            ValidateFunction(function, functionIds, activeNodeKeys);
        }

        var orphanKeys = new HashSet<(SemanticId FunctionId, SemanticId NodeId)>();
        foreach (GraphLayoutOrphan? orphan in document.OrphanedNodes)
        {
            if (orphan is null || orphan.FunctionId.IsEmpty || orphan.Node is null)
            {
                throw new JsonException("A graph layout orphan has invalid required fields.");
            }

            ValidateNode(orphan.Node);
            var key = (orphan.FunctionId, orphan.Node.NodeId);
            if (!orphanKeys.Add(key))
            {
                throw new JsonException(
                    $"Duplicate orphaned node layout '{orphan.FunctionId}/{orphan.Node.NodeId}'.");
            }

            if (activeNodeKeys.Contains(key))
            {
                throw new JsonException(
                    $"Node layout '{orphan.FunctionId}/{orphan.Node.NodeId}' is both active and orphaned.");
            }
        }

        return document with
        {
            Functions = document.Functions
                .OrderBy(static function => function.FunctionId.Value, StringComparer.Ordinal)
                .Select(NormalizeFunction)
                .ToArray(),
            OrphanedNodes = document.OrphanedNodes
                .OrderBy(static orphan => orphan.FunctionId.Value, StringComparer.Ordinal)
                .ThenBy(static orphan => orphan.Node.NodeId.Value, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static void ValidateFunction(
        GraphFunctionLayout? function,
        ISet<SemanticId> functionIds,
        ISet<(SemanticId FunctionId, SemanticId NodeId)> activeNodeKeys)
    {
        if (function is null || function.FunctionId.IsEmpty ||
            function.Nodes is null || function.CommentRegions is null || function.RoutingHints is null)
        {
            throw new JsonException("A graph function layout has invalid required fields.");
        }

        if (!functionIds.Add(function.FunctionId))
        {
            throw new JsonException($"Duplicate graph function layout '{function.FunctionId}'.");
        }

        var nodeIds = new HashSet<SemanticId>();
        foreach (GraphNodeLayout? node in function.Nodes)
        {
            ValidateNode(node);
            if (!nodeIds.Add(node!.NodeId))
            {
                throw new JsonException(
                    $"Duplicate node layout '{function.FunctionId}/{node.NodeId}'.");
            }

            activeNodeKeys.Add((function.FunctionId, node.NodeId));
        }

        var commentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (GraphCommentRegion? region in function.CommentRegions)
        {
            if (region is null || string.IsNullOrWhiteSpace(region.Id) || region.Title is null ||
                region.Bounds is null || region.NodeIds is null)
            {
                throw new JsonException(
                    $"Function layout '{function.FunctionId}' has an invalid comment region.");
            }

            if (!commentIds.Add(region.Id))
            {
                throw new JsonException(
                    $"Function layout '{function.FunctionId}' has duplicate comment region '{region.Id}'.");
            }

            ValidateBounds(region.Bounds, $"comment region '{region.Id}'");
            if (region.NodeIds.Any(static nodeId => nodeId.IsEmpty) ||
                region.NodeIds.Distinct().Count() != region.NodeIds.Count)
            {
                throw new JsonException(
                    $"Comment region '{region.Id}' has empty or duplicate node IDs.");
            }
        }

        var edgeIds = new HashSet<SemanticId>();
        foreach (GraphRoutingHint? hint in function.RoutingHints)
        {
            if (hint is null || hint.EdgeId.IsEmpty || hint.ControlPoints is null ||
                hint.ControlPoints.Count == 0)
            {
                throw new JsonException(
                    $"Function layout '{function.FunctionId}' has an invalid routing hint.");
            }

            if (!edgeIds.Add(hint.EdgeId))
            {
                throw new JsonException(
                    $"Function layout '{function.FunctionId}' has duplicate routing hint '{hint.EdgeId}'.");
            }

            foreach (GraphLayoutPoint? point in hint.ControlPoints)
            {
                ValidatePoint(point, $"routing hint '{hint.EdgeId}'");
            }
        }
    }

    private static void ValidateNode(GraphNodeLayout? node)
    {
        if (node is null || node.NodeId.IsEmpty)
        {
            throw new JsonException("A graph node layout has invalid required fields.");
        }

        switch (node.Placement)
        {
            case GraphNodePlacement.Unplaced when node.Position is not null || node.Size is not null:
                throw new JsonException(
                    $"Unplaced node layout '{node.NodeId}' cannot carry a position or size.");
            case GraphNodePlacement.Placed when node.Position is null || node.Size is null:
                throw new JsonException(
                    $"Placed node layout '{node.NodeId}' requires both a position and size.");
            case GraphNodePlacement.Placed:
                ValidatePoint(node.Position, $"node layout '{node.NodeId}'");
                ValidateSize(node.Size, $"node layout '{node.NodeId}'");
                break;
            case GraphNodePlacement.Unplaced:
                break;
            default:
                throw new JsonException(
                    $"Node layout '{node.NodeId}' has unsupported placement '{node.Placement}'.");
        }
    }

    private static void ValidatePoint(GraphLayoutPoint? point, string owner)
    {
        if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            throw new JsonException($"The {owner} has a non-finite point.");
        }
    }

    private static void ValidateSize(GraphLayoutSize? size, string owner)
    {
        if (size is null || !double.IsFinite(size.Width) || !double.IsFinite(size.Height) ||
            size.Width <= 0 || size.Height <= 0)
        {
            throw new JsonException($"The {owner} requires a finite, positive size.");
        }
    }

    private static void ValidateBounds(GraphLayoutBounds bounds, string owner)
    {
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
            bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new JsonException($"The {owner} requires finite bounds with a positive size.");
        }
    }

    private static GraphFunctionLayout NormalizeFunction(GraphFunctionLayout function) => function with
    {
        Nodes = function.Nodes
            .OrderBy(static node => node.NodeId.Value, StringComparer.Ordinal)
            .ToArray(),
        CommentRegions = function.CommentRegions
            .OrderBy(static region => region.Id, StringComparer.Ordinal)
            .Select(static region => region with
            {
                NodeIds = region.NodeIds
                    .OrderBy(static nodeId => nodeId.Value, StringComparer.Ordinal)
                    .ToArray(),
            })
            .ToArray(),
        RoutingHints = function.RoutingHints
            .OrderBy(static hint => hint.EdgeId.Value, StringComparer.Ordinal)
            .ToArray(),
    };
}
