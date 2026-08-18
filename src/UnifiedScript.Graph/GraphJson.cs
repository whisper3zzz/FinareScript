using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.Graph;

public static class GraphJson
{
    public static string Serialize(GraphDocument document, bool writeIndented = true)
    {
        GraphDocument normalized = NormalizeAndValidate(document);
        return JsonSerializer.Serialize(normalized, UnifiedJson.CreateOptions(writeIndented));
    }

    public static GraphDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        GraphDocument? document = JsonSerializer.Deserialize<GraphDocument>(
            json,
            UnifiedJson.CreateOptions());
        if (document is null)
        {
            throw new JsonException("The graph document was empty.");
        }

        return NormalizeAndValidate(document);
    }

    public static GraphDocument NormalizeAndValidate(GraphDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != GraphDocument.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported graph schema '{document.SchemaVersion}'. " +
                $"Expected '{GraphDocument.CurrentSchemaVersion}'.");
        }

        if (document.SourceCompilationId.IsEmpty ||
            document.SemanticConfigurationId is SemanticId configurationId && configurationId.IsEmpty ||
            document.Functions is null)
        {
            throw new JsonException("A graph document has invalid required fields.");
        }

        var functionIds = new HashSet<SemanticId>();
        foreach (GraphFunction? function in document.Functions)
        {
            ValidateFunction(function, functionIds);
        }

        return document with
        {
            Functions = document.Functions
                .OrderBy(static function => function.QualifiedName, StringComparer.Ordinal)
                .ThenBy(static function => function.Id.Value, StringComparer.Ordinal)
                .Select(NormalizeFunction)
                .ToArray(),
        };
    }

    private static void ValidateFunction(
        GraphFunction? function,
        ISet<SemanticId> functionIds)
    {
        if (function is null || function.Id.IsEmpty ||
            string.IsNullOrWhiteSpace(function.QualifiedName) ||
            function.Nodes is null || function.Edges is null ||
            function.RootRegionId.IsEmpty || function.Regions is null)
        {
            throw new JsonException("A graph function has invalid required fields.");
        }

        if (!functionIds.Add(function.Id))
        {
            throw new JsonException($"Duplicate graph function '{function.Id}'.");
        }

        var nodes = new Dictionary<SemanticId, GraphNode>();
        foreach (GraphNode? node in function.Nodes)
        {
            if (node is null || node.Id.IsEmpty || string.IsNullOrWhiteSpace(node.Kind) ||
                node.Label is null || !IsValidSpan(node.Span) || node.Ports is null ||
                node.Properties is null)
            {
                throw new JsonException($"Graph function '{function.Id}' has an invalid node.");
            }

            if (!nodes.TryAdd(node.Id, node))
            {
                throw new JsonException(
                    $"Graph function '{function.Id}' has duplicate node '{node.Id}'.");
            }
        }

        var edgeIds = new HashSet<SemanticId>();
        foreach (GraphEdge? edge in function.Edges)
        {
            if (edge is null || edge.Id.IsEmpty || edge.FromNode.IsEmpty || edge.ToNode.IsEmpty ||
                string.IsNullOrWhiteSpace(edge.FromPort) || string.IsNullOrWhiteSpace(edge.ToPort))
            {
                throw new JsonException($"Graph function '{function.Id}' has an invalid edge.");
            }

            if (!edgeIds.Add(edge.Id))
            {
                throw new JsonException(
                    $"Graph function '{function.Id}' has duplicate edge '{edge.Id}'.");
            }

            if (!nodes.ContainsKey(edge.FromNode) || !nodes.ContainsKey(edge.ToNode))
            {
                throw new JsonException(
                    $"Graph edge '{edge.Id}' references a node outside function '{function.Id}'.");
            }
        }

        var regions = new Dictionary<SemanticId, GraphRegion>();
        foreach (GraphRegion? region in function.Regions)
        {
            if (region is null || region.Id.IsEmpty || !Enum.IsDefined(region.Kind) ||
                region.SourceNodeId.IsEmpty || !IsValidSpan(region.Span) ||
                region.NodeIds is null || region.ChildRegionIds is null)
            {
                throw new JsonException($"Graph function '{function.Id}' has an invalid region.");
            }

            if (region.IsImplicit && region.Kind is not (
                    GraphRegionKind.Else or
                    GraphRegionKind.LoopInitializer or
                    GraphRegionKind.LoopIncrement))
            {
                throw new JsonException(
                    $"Only else and omitted for-clause regions may be implicit; region " +
                    $"'{region.Id}' is '{region.Kind}'.");
            }

            if (!regions.TryAdd(region.Id, region))
            {
                throw new JsonException(
                    $"Graph function '{function.Id}' has duplicate region '{region.Id}'.");
            }

            if (region.NodeIds.Any(static id => id.IsEmpty) ||
                region.NodeIds.Distinct().Count() != region.NodeIds.Count ||
                region.ChildRegionIds.Any(static id => id.IsEmpty) ||
                region.ChildRegionIds.Distinct().Count() != region.ChildRegionIds.Count)
            {
                throw new JsonException(
                    $"Graph region '{region.Id}' has empty or duplicate node/child IDs.");
            }
        }

        if (!regions.TryGetValue(function.RootRegionId, out GraphRegion? root) ||
            root.Kind != GraphRegionKind.Function || root.ParentRegionId is not null ||
            root.SourceNodeId != function.Id)
        {
            throw new JsonException(
                $"Graph function '{function.Id}' requires a parentless function root region.");
        }

        if (root.ChildRegionIds.Count != 1 ||
            !regions.TryGetValue(root.ChildRegionIds[0], out GraphRegion? body) ||
            body.Kind != GraphRegionKind.Block)
        {
            throw new JsonException(
                $"Graph function '{function.Id}' requires one root body-block region.");
        }

        var childParents = new Dictionary<SemanticId, SemanticId>();
        var nodeOwners = new Dictionary<SemanticId, SemanticId>();
        foreach (GraphRegion region in regions.Values)
        {
            if (region.Id != root.Id && region.Kind == GraphRegionKind.Function)
            {
                throw new JsonException(
                    $"Graph function '{function.Id}' may contain only its root function region.");
            }

            if (region.Id != root.Id &&
                (region.ParentRegionId is not SemanticId parentId || !regions.ContainsKey(parentId)))
            {
                throw new JsonException(
                    $"Graph region '{region.Id}' requires an existing parent region.");
            }

            if (region.Kind is GraphRegionKind.Then or GraphRegionKind.Else &&
                (region.ParentRegionId is not SemanticId branchParentId ||
                    regions[branchParentId].Kind != GraphRegionKind.If))
            {
                throw new JsonException(
                    $"Graph region '{region.Id}' must be a direct child of an if region.");
            }

            if (region.Kind is (GraphRegionKind.LoopInitializer or
                    GraphRegionKind.LoopBody or
                    GraphRegionKind.LoopIncrement) &&
                (region.ParentRegionId is not SemanticId loopParentId ||
                    regions[loopParentId].Kind != GraphRegionKind.Loop))
            {
                throw new JsonException(
                    $"Graph region '{region.Id}' must be a direct child of a loop region.");
            }

            foreach (SemanticId nodeId in region.NodeIds)
            {
                if (!nodes.ContainsKey(nodeId))
                {
                    throw new JsonException(
                        $"Graph region '{region.Id}' references unknown node '{nodeId}'.");
                }

                if (!nodeOwners.TryAdd(nodeId, region.Id))
                {
                    throw new JsonException(
                        $"Graph node '{nodeId}' belongs to more than one direct region.");
                }
            }

            foreach (SemanticId childId in region.ChildRegionIds)
            {
                if (!regions.TryGetValue(childId, out GraphRegion? child) ||
                    child.ParentRegionId != region.Id)
                {
                    throw new JsonException(
                        $"Graph region '{region.Id}' has an inconsistent child '{childId}'.");
                }

                if (!childParents.TryAdd(childId, region.Id))
                {
                    throw new JsonException(
                        $"Graph region '{childId}' belongs to more than one parent.");
                }
            }

            if (region.Kind == GraphRegionKind.If)
            {
                GraphRegion[] children = region.ChildRegionIds.Select(id => regions[id]).ToArray();
                if (children.Length != 2 || children[0].Kind != GraphRegionKind.Then ||
                    children[1].Kind != GraphRegionKind.Else ||
                    !region.NodeIds.Contains(region.SourceNodeId) ||
                    nodes[region.SourceNodeId].Kind != "branch")
                {
                    throw new JsonException(
                        $"If region '{region.Id}' requires its branch node and ordered then/else children.");
                }
            }

            if (region.Kind == GraphRegionKind.Loop)
            {
                GraphRegion[] children = region.ChildRegionIds.Select(id => regions[id]).ToArray();
                bool hasLoopNode = region.NodeIds.Contains(region.SourceNodeId) &&
                    nodes[region.SourceNodeId].Kind == "loop";
                bool isFor = hasLoopNode &&
                    nodes[region.SourceNodeId].Properties.TryGetValue(
                        "loopForm",
                        out string? loopForm) &&
                    string.Equals(loopForm, "for", StringComparison.Ordinal);
                bool hasValidChildren = isFor
                    ? children.Length == 3 &&
                        children[0].Kind == GraphRegionKind.LoopInitializer &&
                        children[1].Kind == GraphRegionKind.LoopBody &&
                        children[2].Kind == GraphRegionKind.LoopIncrement
                    : children.Length == 1 && children[0].Kind == GraphRegionKind.LoopBody;
                if (!hasLoopNode || !hasValidChildren)
                {
                    throw new JsonException(
                        $"Loop region '{region.Id}' requires its loop node and either one while-body " +
                        "child or ordered for initializer/body/increment children.");
                }
            }
        }

        foreach (GraphRegion region in regions.Values.Where(candidate => candidate.Id != root.Id))
        {
            if (!childParents.TryGetValue(region.Id, out SemanticId parentId) ||
                parentId != region.ParentRegionId)
            {
                throw new JsonException(
                    $"Graph region '{region.Id}' is not owned by its declared parent.");
            }
        }

        if (nodeOwners.Count != nodes.Count)
        {
            SemanticId missingNode = nodes.Keys.First(id => !nodeOwners.ContainsKey(id));
            throw new JsonException(
                $"Graph node '{missingNode}' does not belong to a direct region.");
        }

        var visited = new HashSet<SemanticId>();
        var active = new HashSet<SemanticId>();
        VisitRegion(root.Id, regions, visited, active);
        if (visited.Count != regions.Count)
        {
            SemanticId unreachable = regions.Keys.First(id => !visited.Contains(id));
            throw new JsonException(
                $"Graph region '{unreachable}' is not reachable from root '{root.Id}'.");
        }
    }

    private static void VisitRegion(
        SemanticId regionId,
        IReadOnlyDictionary<SemanticId, GraphRegion> regions,
        ISet<SemanticId> visited,
        ISet<SemanticId> active)
    {
        if (!active.Add(regionId))
        {
            throw new JsonException($"Graph region hierarchy contains a cycle at '{regionId}'.");
        }

        if (!visited.Add(regionId))
        {
            active.Remove(regionId);
            return;
        }

        foreach (SemanticId childId in regions[regionId].ChildRegionIds)
        {
            VisitRegion(childId, regions, visited, active);
        }

        active.Remove(regionId);
    }

    private static GraphFunction NormalizeFunction(GraphFunction function) => function with
    {
        Nodes = function.Nodes
            .OrderBy(static node => node.Id.Value, StringComparer.Ordinal)
            .Select(node => node with
            {
                Properties = NormalizeProperties(node.Properties),
            })
            .ToArray(),
        Edges = function.Edges
            .OrderBy(static edge => edge.Id.Value, StringComparer.Ordinal)
            .ToArray(),
        Regions = function.Regions
            .OrderBy(static region => region.Id.Value, StringComparer.Ordinal)
            .ToArray(),
    };

    private static IReadOnlyDictionary<string, string> NormalizeProperties(
        IReadOnlyDictionary<string, string> properties)
    {
        var normalized = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in properties)
        {
            normalized.Add(key, value);
        }

        return normalized;
    }

    private static bool IsValidSpan(SourceSpan? span) =>
        span is not null &&
        !string.IsNullOrWhiteSpace(span.FilePath) &&
        span.Start is not null &&
        span.End is not null &&
        span.Start.Line >= 0 &&
        span.Start.Column >= 0 &&
        span.End.Line >= 0 &&
        span.End.Column >= 0 &&
        (span.End.Line > span.Start.Line ||
            span.End.Line == span.Start.Line && span.End.Column >= span.Start.Column);
}
