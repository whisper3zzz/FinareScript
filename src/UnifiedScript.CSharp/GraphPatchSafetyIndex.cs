using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Graph;
using UnifiedScript.Semantics;

namespace UnifiedScript.CSharp;

internal sealed class GraphPatchSafetyIndex
{
    private readonly HirPatchIndex patchIndex;
    private readonly IReadOnlyDictionary<SemanticId, FunctionSafety> functions;

    public GraphPatchSafetyIndex(
        HirCompilationUnit compilation,
        BoundCompilation semantics,
        HirPatchIndex patchIndex)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(semantics);
        ArgumentNullException.ThrowIfNull(patchIndex);
        this.patchIndex = patchIndex;

        GraphDocument graph = new HirGraphProjector().Project(compilation, semantics);
        GraphControlFlowAnalysis analysis = new GraphControlFlowAnalyzer().AnalyzeAndValidate(graph);
        IReadOnlyDictionary<SemanticId, GraphFunction> graphFunctions = graph.Functions
            .ToDictionary(static function => function.Id);
        IReadOnlyDictionary<SemanticId, GraphControlFlowFunction> flowFunctions = analysis.Functions
            .ToDictionary(static function => function.FunctionId);
        functions = compilation.Types
            .SelectMany(static type => type.Methods)
            .ToDictionary(
                static method => method.Id,
                method => new FunctionSafety(
                    graphFunctions[method.Id],
                    flowFunctions[method.Id]));
    }

    public bool IsInsertionReachable(
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex) => ResolveInsertionAnchors(blockLocation, insertionIndex)
        is { Count: > 0 } anchors && anchors.All(static anchor => anchor.IsReachable);

    public bool DoesDefinitionDominateInsertion(
        SemanticId definitionId,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex)
    {
        IReadOnlyList<GraphControlFlowNode> anchors = ResolveInsertionAnchors(
            blockLocation,
            insertionIndex);
        return anchors.Count > 0 &&
            anchors.All(anchor => anchor.IsReachable && anchor.DominatorIds.Contains(definitionId));
    }

    public bool DoesDefinitionDominateStatement(
        SemanticId definitionId,
        HirPatchIndex.StatementLocation statementLocation)
    {
        if (!functions.TryGetValue(statementLocation.Method.Id, out FunctionSafety? function) ||
            definitionId == statementLocation.Statement.Id)
        {
            return false;
        }

        IReadOnlyList<GraphControlFlowNode> entries = GetStatementEntries(
            function,
            statementLocation.Statement);
        return entries.Count > 0 &&
            entries.All(entry => entry.IsReachable && entry.DominatorIds.Contains(definitionId));
    }

    private IReadOnlyList<GraphControlFlowNode> ResolveInsertionAnchors(
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex)
    {
        if (!functions.TryGetValue(blockLocation.Method.Id, out FunctionSafety? function) ||
            insertionIndex < 0 ||
            insertionIndex > blockLocation.Block.Statements.Count)
        {
            return [];
        }

        if (insertionIndex < blockLocation.Block.Statements.Count)
        {
            return GetStatementEntries(function, blockLocation.Block.Statements[insertionIndex]);
        }

        IReadOnlyList<GraphControlFlowNode> exits = GetReachableBlockFallthroughSources(
            function,
            blockLocation.Block);
        if (exits.Count > 0)
        {
            return exits;
        }

        GraphRegion? region = FindBlockRegion(function.Graph, blockLocation.Block.Id);
        if (region is not null && RegionSubtreeHasNodes(function.Graph, region.Id))
        {
            return [];
        }

        GraphControlFlowBranch? branch = region is null
            ? null
            : function.Flow.Branches.SingleOrDefault(candidate =>
                candidate.ThenRegionId == region.Id || candidate.ElseRegionId == region.Id);
        if (branch is not null && function.Nodes.TryGetValue(
                branch.BranchNodeId,
                out GraphControlFlowNode? branchNode))
        {
            return [branchNode];
        }

        GraphControlFlowLoop? loop = region is null
            ? null
            : function.Flow.Loops.SingleOrDefault(candidate => candidate.BodyRegionId == region.Id);
        if (loop is not null && function.Nodes.TryGetValue(
                loop.LoopNodeId,
                out GraphControlFlowNode? loopNode))
        {
            return [loopNode];
        }

        if (blockLocation.ParentBlock is null || blockLocation.EntryStatement is null ||
            !patchIndex.TryGetStatement(
                blockLocation.EntryStatement.Id,
                out HirPatchIndex.StatementLocation? parentStatement) ||
            !patchIndex.TryGetBlock(
                parentStatement!.ParentBlock.Id,
                out HirPatchIndex.BlockLocation? parentLocation))
        {
            return [];
        }

        int parentContinuationIndex = parentStatement!.Index + 1;
        return ResolveInsertionAnchors(parentLocation!, parentContinuationIndex);
    }

    private static IReadOnlyList<GraphControlFlowNode> GetStatementEntries(
        FunctionSafety function,
        HirStatement statement)
    {
        HashSet<SemanticId> statementIds = statement
            .DescendantsAndSelf()
            .Select(static node => node.Id)
            .ToHashSet();
        if (statement is HirExpressionStatement expressionStatement)
        {
            statementIds.Add(SemanticId.FromKey($"{expressionStatement.Id}:graph:evaluate"));
        }

        HashSet<SemanticId> controlIds = statementIds
            .Where(function.Nodes.ContainsKey)
            .ToHashSet();
        return controlIds
            .Select(id => function.Nodes[id])
            .Where(node => node.PredecessorIds.Count == 0 ||
                node.PredecessorIds.Any(predecessor => !controlIds.Contains(predecessor)))
            .OrderBy(static node => node.Id.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<GraphControlFlowNode> GetReachableBlockFallthroughSources(
        FunctionSafety function,
        HirBlockStatement block)
    {
        GraphRegion? region = FindBlockRegion(function.Graph, block.Id);
        if (region is null)
        {
            return [];
        }

        IReadOnlyDictionary<SemanticId, GraphRegion> regions = function.Graph.Regions
            .ToDictionary(static candidate => candidate.Id);
        HashSet<SemanticId> subtreeIds = CollectRegionSubtree(region.Id, regions);
        HashSet<SemanticId> insideNodes = subtreeIds
            .SelectMany(id => regions[id].NodeIds)
            .ToHashSet();
        IReadOnlyDictionary<SemanticId, GraphNode> graphNodes = function.Graph.Nodes
            .ToDictionary(static node => node.Id);
        return function.Flow.Arcs
            .Where(arc => insideNodes.Contains(arc.FromNodeId) &&
                !insideNodes.Contains(arc.ToNodeId) &&
                graphNodes[arc.FromNodeId].Kind is not ("return" or "break" or "continue"))
            .Select(static arc => arc.FromNodeId)
            .Distinct()
            .Select(id => function.Nodes[id])
            .Where(static node => node.IsReachable)
            .OrderBy(static node => node.Id.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static GraphRegion? FindBlockRegion(GraphFunction function, SemanticId blockId) =>
        function.Regions.SingleOrDefault(region =>
            region.SourceNodeId == blockId &&
            region.Kind is GraphRegionKind.Block or GraphRegionKind.Then or GraphRegionKind.Else or
                GraphRegionKind.LoopBody);

    private static bool RegionSubtreeHasNodes(GraphFunction function, SemanticId regionId)
    {
        IReadOnlyDictionary<SemanticId, GraphRegion> regions = function.Regions
            .ToDictionary(static region => region.Id);
        return CollectRegionSubtree(regionId, regions)
            .Any(id => regions[id].NodeIds.Count > 0);
    }

    private static HashSet<SemanticId> CollectRegionSubtree(
        SemanticId rootId,
        IReadOnlyDictionary<SemanticId, GraphRegion> regions)
    {
        var result = new HashSet<SemanticId>();
        var pending = new Stack<SemanticId>();
        pending.Push(rootId);
        while (pending.Count > 0)
        {
            SemanticId current = pending.Pop();
            if (!result.Add(current))
            {
                continue;
            }

            foreach (SemanticId child in regions[current].ChildRegionIds)
            {
                pending.Push(child);
            }
        }

        return result;
    }

    private sealed class FunctionSafety
    {
        public FunctionSafety(
            GraphFunction graph,
            GraphControlFlowFunction flow)
        {
            Graph = graph;
            Flow = flow;
            Nodes = flow.Nodes.ToDictionary(static node => node.Id);
        }

        public GraphFunction Graph { get; }

        public GraphControlFlowFunction Flow { get; }

        public IReadOnlyDictionary<SemanticId, GraphControlFlowNode> Nodes { get; }
    }
}
