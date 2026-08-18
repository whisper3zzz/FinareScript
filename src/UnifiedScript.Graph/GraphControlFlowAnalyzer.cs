using UnifiedScript.Core;

namespace UnifiedScript.Graph;

public sealed class GraphControlFlowAnalyzer
{
    public GraphControlFlowAnalysis Analyze(GraphDocument graph)
    {
        GraphDocument normalized = GraphJson.NormalizeAndValidate(graph);
        return new GraphControlFlowAnalysis(
            normalized.SourceCompilationId,
            normalized.SemanticConfigurationId,
            normalized.Functions.Select(AnalyzeFunction).ToArray());
    }

    public GraphControlFlowAnalysis AnalyzeAndValidate(GraphDocument graph)
    {
        GraphControlFlowAnalysis analysis = Analyze(graph);
        GraphControlFlowFunction[] irreducible = analysis.Functions
            .Where(static function => !function.IsReducible)
            .ToArray();
        if (irreducible.Length > 0)
        {
            throw new InvalidOperationException(
                "Graph control flow is irreducible in function(s): " +
                string.Join(
                    ", ",
                    irreducible.Select(static function => function.FunctionId.Value)));
        }

        return analysis;
    }

    private static GraphControlFlowFunction AnalyzeFunction(GraphFunction function)
    {
        IReadOnlyDictionary<SemanticId, GraphNode> graphNodes = function.Nodes
            .ToDictionary(static node => node.Id);
        GraphNode[] controlNodes = function.Nodes
            .Where(IsControlNode)
            .OrderBy(static node => node.Id.Value, StringComparer.Ordinal)
            .ToArray();
        GraphNode[] entries = controlNodes
            .Where(static node => node.Kind == "entry")
            .ToArray();
        if (entries.Length != 1)
        {
            throw new InvalidOperationException(
                $"Graph function '{function.Id}' requires exactly one control-flow entry node.");
        }

        GraphNode entry = entries[0];
        GraphRegion root = function.Regions.Single(region => region.Id == function.RootRegionId);
        if (!root.NodeIds.Contains(entry.Id))
        {
            throw new InvalidOperationException(
                $"Entry node '{entry.Id}' must belong to function region '{root.Id}'.");
        }

        SemanticId exitId = SemanticId.FromKey($"{function.Id}:graph:cfg:exit");
        if (graphNodes.ContainsKey(exitId))
        {
            throw new InvalidOperationException(
                $"Graph function '{function.Id}' collides with its synthetic CFG exit ID '{exitId}'.");
        }

        IReadOnlyDictionary<SemanticId, GraphNode> controlById = controlNodes
            .ToDictionary(static node => node.Id);
        var arcs = new List<GraphControlFlowArc>();
        var arcIds = new HashSet<SemanticId>();
        var transitions = new HashSet<(SemanticId From, string FromPort, SemanticId To, string ToPort)>();
        foreach (GraphEdge edge in function.Edges.Where(static edge => edge.Kind == GraphEdgeKind.Control))
        {
            if (!controlById.TryGetValue(edge.FromNode, out GraphNode? source) ||
                !controlById.TryGetValue(edge.ToNode, out GraphNode? target))
            {
                throw new InvalidOperationException(
                    $"Control edge '{edge.Id}' must connect two control-flow nodes.");
            }

            RequireControlPort(
                source,
                edge.FromPort,
                GraphPortDirection.Output,
                edge.Id);
            RequireControlPort(
                target,
                edge.ToPort,
                GraphPortDirection.Input,
                edge.Id);
            AddArc(
                arcs,
                arcIds,
                transitions,
                new GraphControlFlowArc(
                    edge.Id,
                    edge.FromNode,
                    edge.FromPort,
                    edge.ToNode,
                    edge.ToPort,
                    false));
        }

        foreach (GraphNode node in controlNodes)
        {
            GraphPort[] outputs = node.Ports
                .Where(static port => port.Kind == GraphPortKind.Control &&
                    port.Direction == GraphPortDirection.Output)
                .ToArray();
            if (outputs.Length == 0)
            {
                if (node.Kind != "return")
                {
                    throw new InvalidOperationException(
                        $"Control-flow node '{node.Id}' has no control output and is not a return.");
                }

                AddSyntheticExitArc(
                    function.Id,
                    node.Id,
                    "return",
                    exitId,
                    arcs,
                    arcIds,
                    transitions);
                continue;
            }

            if (outputs.Length > 1 && node.Kind is not ("branch" or "loop"))
            {
                throw new InvalidOperationException(
                    $"Only a branch or loop node may expose multiple control outputs; node '{node.Id}' does so.");
            }

            if (node.Kind == "branch" &&
                (outputs.Length != 2 ||
                    outputs[0].Name != "then" ||
                    outputs[1].Name != "else"))
            {
                throw new InvalidOperationException(
                    $"Branch node '{node.Id}' requires ordered then/else control outputs.");
            }

            if (node.Kind == "loop" &&
                (outputs.Length != 2 ||
                    outputs[0].Name != "body" ||
                    outputs[1].Name != "exit"))
            {
                throw new InvalidOperationException(
                    $"Loop node '{node.Id}' requires ordered body/exit control outputs.");
            }

            foreach (GraphPort output in outputs)
            {
                int outgoingCount = arcs.Count(arc =>
                    arc.FromNodeId == node.Id && arc.FromPort == output.Name);
                if (outgoingCount > 1)
                {
                    throw new InvalidOperationException(
                        $"Control output '{node.Id}.{output.Name}' has more than one successor.");
                }

                if (outgoingCount == 0)
                {
                    AddSyntheticExitArc(
                        function.Id,
                        node.Id,
                        output.Name,
                        exitId,
                        arcs,
                        arcIds,
                        transitions);
                }
            }
        }

        SemanticId[] allNodeIds = [.. controlById.Keys, exitId];
        IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> predecessors = CreateAdjacency(allNodeIds);
        IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> successors = CreateAdjacency(allNodeIds);
        foreach (GraphControlFlowArc arc in arcs)
        {
            successors[arc.FromNodeId].Add(arc.ToNodeId);
            predecessors[arc.ToNodeId].Add(arc.FromNodeId);
        }

        if (predecessors[entry.Id].Count != 0)
        {
            throw new InvalidOperationException(
                $"Control-flow entry '{entry.Id}' cannot have predecessors.");
        }

        HashSet<SemanticId> reachable = Traverse(entry.Id, successors);
        IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> dominators = ComputeDominators(
            entry.Id,
            reachable,
            predecessors);
        HashSet<SemanticId> canReachExit = Traverse(exitId, predecessors);
        IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> postDominators = ComputePostDominators(
            exitId,
            reachable,
            canReachExit,
            successors);

        IReadOnlyDictionary<SemanticId, SemanticId?> immediateDominators = ComputeImmediateRelations(
            dominators);
        IReadOnlyDictionary<SemanticId, SemanticId?> immediatePostDominators = ComputeImmediateRelations(
            postDominators);
        GraphControlFlowNode[] flowNodes = allNodeIds
            .OrderBy(static id => id.Value, StringComparer.Ordinal)
            .Select(id => new GraphControlFlowNode(
                id,
                id == exitId,
                reachable.Contains(id),
                Order(predecessors[id]),
                Order(successors[id]),
                dominators.TryGetValue(id, out HashSet<SemanticId>? nodeDominators)
                    ? Order(nodeDominators)
                    : [],
                immediateDominators.GetValueOrDefault(id),
                postDominators.TryGetValue(id, out HashSet<SemanticId>? nodePostDominators)
                    ? Order(nodePostDominators)
                    : [],
                immediatePostDominators.GetValueOrDefault(id)))
            .ToArray();
        GraphControlFlowBranch[] branches = BuildBranches(
            function,
            controlById,
            exitId,
            arcs,
            successors,
            immediatePostDominators);
        GraphControlFlowLoop[] loops = BuildLoops(
            function,
            controlById,
            arcs);
        GraphControlFlowComponent[] components = BuildCyclicComponents(
            entry.Id,
            exitId,
            reachable,
            predecessors,
            successors,
            dominators);

        return new GraphControlFlowFunction(
            function.Id,
            entry.Id,
            exitId,
            flowNodes,
            arcs.OrderBy(static arc => arc.Id.Value, StringComparer.Ordinal).ToArray(),
            branches,
            loops,
            components,
            controlById.Keys
                .Where(id => !reachable.Contains(id))
                .OrderBy(static id => id.Value, StringComparer.Ordinal)
                .ToArray(),
            reachable.All(canReachExit.Contains),
            components.All(static component => component.IsReducible));
    }

    private static bool IsControlNode(GraphNode node) =>
        node.Kind == "entry" ||
        node.Ports.Any(static port => port.Kind == GraphPortKind.Control);

    private static void RequireControlPort(
        GraphNode node,
        string portName,
        GraphPortDirection direction,
        SemanticId edgeId)
    {
        int matches = node.Ports.Count(port =>
            port.Name == portName &&
            port.Direction == direction &&
            port.Kind == GraphPortKind.Control);
        if (matches != 1)
        {
            throw new InvalidOperationException(
                $"Control edge '{edgeId}' references invalid {direction} port " +
                $"'{node.Id}.{portName}'.");
        }
    }

    private static void AddSyntheticExitArc(
        SemanticId functionId,
        SemanticId sourceNodeId,
        string sourcePort,
        SemanticId exitId,
        ICollection<GraphControlFlowArc> arcs,
        ISet<SemanticId> arcIds,
        ISet<(SemanticId From, string FromPort, SemanticId To, string ToPort)> transitions)
    {
        SemanticId arcId = SemanticId.FromKey(
            $"{functionId}:graph:cfg:exit-arc:{sourceNodeId}:{sourcePort}");
        AddArc(
            arcs,
            arcIds,
            transitions,
            new GraphControlFlowArc(
                arcId,
                sourceNodeId,
                sourcePort,
                exitId,
                "in",
                true));
    }

    private static void AddArc(
        ICollection<GraphControlFlowArc> arcs,
        ISet<SemanticId> arcIds,
        ISet<(SemanticId From, string FromPort, SemanticId To, string ToPort)> transitions,
        GraphControlFlowArc arc)
    {
        if (!arcIds.Add(arc.Id) ||
            !transitions.Add((arc.FromNodeId, arc.FromPort, arc.ToNodeId, arc.ToPort)))
        {
            throw new InvalidOperationException(
                $"Duplicate control-flow arc '{arc.FromNodeId}.{arc.FromPort} -> " +
                $"{arc.ToNodeId}.{arc.ToPort}'.");
        }

        arcs.Add(arc);
    }

    private static IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> CreateAdjacency(
        IEnumerable<SemanticId> nodeIds) => nodeIds.ToDictionary(
            static id => id,
            static _ => new HashSet<SemanticId>());

    private static HashSet<SemanticId> Traverse(
        SemanticId start,
        IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> adjacency)
    {
        var visited = new HashSet<SemanticId>();
        var pending = new Stack<SemanticId>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            SemanticId current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            foreach (SemanticId next in adjacency[current]
                         .OrderByDescending(static id => id.Value, StringComparer.Ordinal))
            {
                pending.Push(next);
            }
        }

        return visited;
    }

    private static IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> ComputeDominators(
        SemanticId entryId,
        IReadOnlySet<SemanticId> reachable,
        IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> predecessors)
    {
        var result = new Dictionary<SemanticId, HashSet<SemanticId>>();
        foreach (SemanticId nodeId in reachable)
        {
            result[nodeId] = nodeId == entryId
                ? [entryId]
                : [.. reachable];
        }

        bool changed;
        do
        {
            changed = false;
            foreach (SemanticId nodeId in reachable
                         .Where(id => id != entryId)
                         .OrderBy(static id => id.Value, StringComparer.Ordinal))
            {
                HashSet<SemanticId>[] incoming = predecessors[nodeId]
                    .Where(reachable.Contains)
                    .Select(id => result[id])
                    .ToArray();
                var next = incoming.Length == 0
                    ? new HashSet<SemanticId>()
                    : new HashSet<SemanticId>(incoming[0]);
                foreach (HashSet<SemanticId> predecessorDominators in incoming.Skip(1))
                {
                    next.IntersectWith(predecessorDominators);
                }

                next.Add(nodeId);
                if (!next.SetEquals(result[nodeId]))
                {
                    result[nodeId] = next;
                    changed = true;
                }
            }
        }
        while (changed);

        return result;
    }

    private static IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> ComputePostDominators(
        SemanticId exitId,
        IReadOnlySet<SemanticId> reachable,
        IReadOnlySet<SemanticId> canReachExit,
        IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> successors)
    {
        HashSet<SemanticId> universe = reachable
            .Where(canReachExit.Contains)
            .ToHashSet();
        var result = new Dictionary<SemanticId, HashSet<SemanticId>>();
        foreach (SemanticId nodeId in universe)
        {
            result[nodeId] = nodeId == exitId
                ? [exitId]
                : [.. universe];
        }

        bool changed;
        do
        {
            changed = false;
            foreach (SemanticId nodeId in universe
                         .Where(id => id != exitId)
                         .OrderBy(static id => id.Value, StringComparer.Ordinal))
            {
                SemanticId[] reachableSuccessors = successors[nodeId]
                    .Where(reachable.Contains)
                    .ToArray();
                HashSet<SemanticId> next;
                if (reachableSuccessors.Length == 0 ||
                    reachableSuccessors.Any(id => !universe.Contains(id)))
                {
                    next = [nodeId];
                }
                else
                {
                    HashSet<SemanticId>[] outgoing = reachableSuccessors
                        .Select(id => result[id])
                        .ToArray();
                    next = new HashSet<SemanticId>(outgoing[0]);
                    foreach (HashSet<SemanticId> successorPostDominators in outgoing.Skip(1))
                    {
                        next.IntersectWith(successorPostDominators);
                    }

                    next.Add(nodeId);
                }

                if (!next.SetEquals(result[nodeId]))
                {
                    result[nodeId] = next;
                    changed = true;
                }
            }
        }
        while (changed);

        return result;
    }

    private static IReadOnlyDictionary<SemanticId, SemanticId?> ComputeImmediateRelations(
        IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> relations)
    {
        var result = new Dictionary<SemanticId, SemanticId?>();
        foreach ((SemanticId nodeId, HashSet<SemanticId> nodeRelations) in relations)
        {
            SemanticId[] strict = nodeRelations
                .Where(id => id != nodeId)
                .OrderBy(static id => id.Value, StringComparer.Ordinal)
                .ToArray();
            SemanticId[] candidates = strict
                .Where(candidate => strict.All(other =>
                    other == candidate || relations[candidate].Contains(other)))
                .ToArray();
            if (candidates.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Control-flow relation for node '{nodeId}' has multiple immediate parents.");
            }

            result[nodeId] = candidates.Length == 1 ? candidates[0] : null;
        }

        return result;
    }

    private static GraphControlFlowBranch[] BuildBranches(
        GraphFunction function,
        IReadOnlyDictionary<SemanticId, GraphNode> controlNodes,
        SemanticId exitId,
        IReadOnlyList<GraphControlFlowArc> arcs,
        IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> successors,
        IReadOnlyDictionary<SemanticId, SemanticId?> immediatePostDominators)
    {
        IReadOnlyDictionary<SemanticId, GraphRegion> regions = function.Regions
            .ToDictionary(static region => region.Id);
        var result = new List<GraphControlFlowBranch>();
        foreach (GraphNode branch in controlNodes.Values
                     .Where(static node => node.Kind == "branch")
                     .OrderBy(static node => node.Id.Value, StringComparer.Ordinal))
        {
            GraphRegion ifRegion = function.Regions.Single(region =>
                region.Kind == GraphRegionKind.If && region.SourceNodeId == branch.Id);
            GraphRegion thenRegion = regions[ifRegion.ChildRegionIds[0]];
            GraphRegion elseRegion = regions[ifRegion.ChildRegionIds[1]];
            SemanticId thenEntry = RequireBranchEntry(branch.Id, "then", arcs);
            SemanticId elseEntry = RequireBranchEntry(branch.Id, "else", arcs);
            SemanticId? immediatePostDominator = immediatePostDominators.GetValueOrDefault(branch.Id);
            SemanticId? merge = immediatePostDominator is SemanticId candidate && candidate != exitId
                ? candidate
                : null;

            result.Add(new GraphControlFlowBranch(
                branch.Id,
                ifRegion.Id,
                thenRegion.Id,
                elseRegion.Id,
                thenEntry,
                elseEntry,
                merge,
                ExitsFunctionWithinRegion(
                    thenEntry,
                    thenRegion.Id,
                    regions,
                    controlNodes.Keys,
                    exitId,
                    successors),
                ExitsFunctionWithinRegion(
                    elseEntry,
                    elseRegion.Id,
                    regions,
                    controlNodes.Keys,
                    exitId,
                    successors)));
        }

        return result.ToArray();
    }

    private static SemanticId RequireBranchEntry(
        SemanticId branchId,
        string port,
        IEnumerable<GraphControlFlowArc> arcs)
    {
        GraphControlFlowArc[] matches = arcs
            .Where(arc => arc.FromNodeId == branchId && arc.FromPort == port)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Branch output '{branchId}.{port}' requires exactly one CFG successor.");
        }

        return matches[0].ToNodeId;
    }

    private static bool ExitsFunctionWithinRegion(
        SemanticId entryId,
        SemanticId regionId,
        IReadOnlyDictionary<SemanticId, GraphRegion> regions,
        IEnumerable<SemanticId> controlNodeIds,
        SemanticId exitId,
        IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> successors)
    {
        HashSet<SemanticId> subtreeRegions = CollectRegionSubtree(regionId, regions);
        HashSet<SemanticId> allowedNodes = subtreeRegions
            .SelectMany(id => regions[id].NodeIds)
            .Intersect(controlNodeIds)
            .ToHashSet();
        var memo = new Dictionary<SemanticId, bool>();
        var active = new HashSet<SemanticId>();

        bool Visit(SemanticId nodeId)
        {
            if (nodeId == exitId)
            {
                return true;
            }

            if (!allowedNodes.Contains(nodeId))
            {
                return false;
            }

            if (memo.TryGetValue(nodeId, out bool known))
            {
                return known;
            }

            if (!active.Add(nodeId))
            {
                return false;
            }

            bool exits = successors[nodeId].Count > 0 && successors[nodeId].All(Visit);
            active.Remove(nodeId);
            memo[nodeId] = exits;
            return exits;
        }

        return Visit(entryId);
    }

    private static GraphControlFlowLoop[] BuildLoops(
        GraphFunction function,
        IReadOnlyDictionary<SemanticId, GraphNode> controlNodes,
        IReadOnlyList<GraphControlFlowArc> arcs)
    {
        IReadOnlyDictionary<SemanticId, GraphRegion> regions = function.Regions
            .ToDictionary(static region => region.Id);
        IReadOnlyDictionary<SemanticId, SemanticId> nodeOwners = function.Regions
            .SelectMany(region => region.NodeIds.Select(nodeId => (nodeId, region.Id)))
            .ToDictionary(static item => item.nodeId, static item => item.Id);
        var claimedTransfers = new HashSet<SemanticId>();
        var result = new List<GraphControlFlowLoop>();
        foreach (GraphRegion loopRegion in function.Regions
                     .Where(static region => region.Kind == GraphRegionKind.Loop)
                     .OrderBy(static region => region.Id.Value, StringComparer.Ordinal))
        {
            GraphNode loopNode = controlNodes[loopRegion.SourceNodeId];
            GraphRegion[] childRegions = loopRegion.ChildRegionIds
                .Select(id => regions[id])
                .ToArray();
            GraphRegion bodyRegion = childRegions.Single(
                static region => region.Kind == GraphRegionKind.LoopBody);
            GraphRegion? initializerRegion = childRegions.SingleOrDefault(
                static region => region.Kind == GraphRegionKind.LoopInitializer);
            GraphRegion? incrementRegion = childRegions.SingleOrDefault(
                static region => region.Kind == GraphRegionKind.LoopIncrement);
            HashSet<SemanticId> directControlNodes = loopRegion.NodeIds
                .Where(controlNodes.ContainsKey)
                .ToHashSet();
            SemanticId[] headerEntries = directControlNodes
                .Where(nodeId => !arcs.Any(arc =>
                    arc.ToNodeId == nodeId &&
                    directControlNodes.Contains(arc.FromNodeId) &&
                    !(arc.FromNodeId == loopNode.Id && arc.FromPort == "body")))
                .OrderBy(static id => id.Value, StringComparer.Ordinal)
                .ToArray();
            if (headerEntries.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Loop region '{loopRegion.Id}' requires one control-flow header entry.");
            }

            SemanticId headerEntry = headerEntries[0];
            SemanticId bodyEntry = RequireControlSuccessor(loopNode.Id, "body", arcs);
            SemanticId loopExit = RequireControlSuccessor(loopNode.Id, "exit", arcs);
            HashSet<SemanticId> bodyRegions = CollectRegionSubtree(bodyRegion.Id, regions);
            HashSet<SemanticId> bodyNodes = bodyRegions
                .SelectMany(id => regions[id].NodeIds)
                .ToHashSet();
            HashSet<SemanticId> incrementNodes = incrementRegion is null
                ? []
                : CollectRegionSubtree(incrementRegion.Id, regions)
                    .SelectMany(id => regions[id].NodeIds)
                    .ToHashSet();
            SemanticId[] incrementControlEntries = incrementNodes
                .Where(controlNodes.ContainsKey)
                .Where(nodeId => !arcs.Any(arc =>
                    arc.ToNodeId == nodeId && incrementNodes.Contains(arc.FromNodeId)))
                .OrderBy(static id => id.Value, StringComparer.Ordinal)
                .ToArray();
            if (incrementControlEntries.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Loop increment region '{incrementRegion!.Id}' requires at most one control entry.");
            }

            SemanticId continueTarget = incrementControlEntries.Length == 1
                ? incrementControlEntries[0]
                : headerEntry;
            SemanticId[] breaks = controlNodes.Values
                .Where(static node => node.Kind == "break")
                .Where(node => nodeOwners.TryGetValue(node.Id, out SemanticId ownerId) &&
                    FindNearestLoopRegion(ownerId, regions) == loopRegion.Id)
                .Select(static node => node.Id)
                .OrderBy(static id => id.Value, StringComparer.Ordinal)
                .ToArray();
            SemanticId[] continues = controlNodes.Values
                .Where(static node => node.Kind == "continue")
                .Where(node => nodeOwners.TryGetValue(node.Id, out SemanticId ownerId) &&
                    FindNearestLoopRegion(ownerId, regions) == loopRegion.Id)
                .Select(static node => node.Id)
                .OrderBy(static id => id.Value, StringComparer.Ordinal)
                .ToArray();
            foreach (SemanticId breakNodeId in breaks)
            {
                claimedTransfers.Add(breakNodeId);
                if (RequireControlSuccessor(breakNodeId, "then", arcs) != loopExit)
                {
                    throw new InvalidOperationException(
                        $"Break node '{breakNodeId}' must target exit '{loopExit}' of its nearest loop.");
                }
            }

            foreach (SemanticId continueNodeId in continues)
            {
                claimedTransfers.Add(continueNodeId);
                if (RequireControlSuccessor(continueNodeId, "then", arcs) != continueTarget)
                {
                    throw new InvalidOperationException(
                        $"Continue node '{continueNodeId}' must target '{continueTarget}' of its nearest loop.");
                }
            }

            SemanticId[] backEdges = arcs
                .Where(arc => !arc.IsSynthetic && arc.ToNodeId == headerEntry &&
                    (bodyNodes.Contains(arc.FromNodeId) ||
                        incrementNodes.Contains(arc.FromNodeId) ||
                        arc.FromNodeId == loopNode.Id && arc.FromPort == "body"))
                .Select(static arc => arc.Id)
                .OrderBy(static id => id.Value, StringComparer.Ordinal)
                .ToArray();
            result.Add(new GraphControlFlowLoop(
                loopNode.Id,
                loopRegion.Id,
                bodyRegion.Id,
                initializerRegion?.Id,
                incrementRegion?.Id,
                headerEntry,
                continueTarget,
                bodyEntry,
                loopExit,
                backEdges,
                breaks,
                continues));
        }

        SemanticId[] unclaimedTransfers = controlNodes.Values
            .Where(static node => node.Kind is "break" or "continue")
            .Select(static node => node.Id)
            .Where(id => !claimedTransfers.Contains(id))
            .OrderBy(static id => id.Value, StringComparer.Ordinal)
            .ToArray();
        if (unclaimedTransfers.Length > 0)
        {
            throw new InvalidOperationException(
                $"Loop-control node '{unclaimedTransfers[0]}' has no enclosing loop region.");
        }

        return result.ToArray();
    }

    private static SemanticId? FindNearestLoopRegion(
        SemanticId regionId,
        IReadOnlyDictionary<SemanticId, GraphRegion> regions)
    {
        GraphRegion current = regions[regionId];
        while (true)
        {
            if (current.Kind == GraphRegionKind.Loop)
            {
                return current.Id;
            }

            if (current.ParentRegionId is not SemanticId parentId)
            {
                return null;
            }

            current = regions[parentId];
        }
    }

    private static SemanticId RequireControlSuccessor(
        SemanticId nodeId,
        string port,
        IEnumerable<GraphControlFlowArc> arcs)
    {
        GraphControlFlowArc[] matches = arcs
            .Where(arc => arc.FromNodeId == nodeId && arc.FromPort == port)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Control output '{nodeId}.{port}' requires exactly one CFG successor.");
        }

        return matches[0].ToNodeId;
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

            foreach (SemanticId childId in regions[current].ChildRegionIds)
            {
                pending.Push(childId);
            }
        }

        return result;
    }

    private static GraphControlFlowComponent[] BuildCyclicComponents(
        SemanticId entryId,
        SemanticId exitId,
        IReadOnlySet<SemanticId> reachable,
        IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> predecessors,
        IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> successors,
        IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> dominators)
    {
        HashSet<SemanticId> candidates = reachable
            .Where(id => id != exitId)
            .ToHashSet();
        IReadOnlyList<IReadOnlyList<SemanticId>> stronglyConnected = FindStronglyConnectedComponents(
            candidates,
            successors);
        return stronglyConnected
            .Where(component => component.Count > 1 || successors[component[0]].Contains(component[0]))
            .Select(component =>
            {
                HashSet<SemanticId> componentSet = component.ToHashSet();
                SemanticId[] entries = component
                    .Where(nodeId => predecessors[nodeId].Any(predecessor =>
                        reachable.Contains(predecessor) && !componentSet.Contains(predecessor)))
                    .OrderBy(static id => id.Value, StringComparer.Ordinal)
                    .ToArray();
                if (entries.Length == 0 && componentSet.Contains(entryId))
                {
                    entries = [entryId];
                }

                bool reducible = entries.Length == 1 && component.All(nodeId =>
                    dominators[nodeId].Contains(entries[0]));
                return new GraphControlFlowComponent(
                    component.OrderBy(static id => id.Value, StringComparer.Ordinal).ToArray(),
                    entries,
                    reducible);
            })
            .OrderBy(static component => component.NodeIds[0].Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<SemanticId>> FindStronglyConnectedComponents(
        IReadOnlySet<SemanticId> nodeIds,
        IReadOnlyDictionary<SemanticId, HashSet<SemanticId>> successors)
    {
        int nextIndex = 0;
        var indexes = new Dictionary<SemanticId, int>();
        var lowLinks = new Dictionary<SemanticId, int>();
        var pending = new Stack<SemanticId>();
        var onStack = new HashSet<SemanticId>();
        var components = new List<IReadOnlyList<SemanticId>>();

        void Connect(SemanticId nodeId)
        {
            indexes[nodeId] = nextIndex;
            lowLinks[nodeId] = nextIndex;
            nextIndex++;
            pending.Push(nodeId);
            onStack.Add(nodeId);

            foreach (SemanticId successor in successors[nodeId]
                         .Where(nodeIds.Contains)
                         .OrderBy(static id => id.Value, StringComparer.Ordinal))
            {
                if (!indexes.ContainsKey(successor))
                {
                    Connect(successor);
                    lowLinks[nodeId] = Math.Min(lowLinks[nodeId], lowLinks[successor]);
                }
                else if (onStack.Contains(successor))
                {
                    lowLinks[nodeId] = Math.Min(lowLinks[nodeId], indexes[successor]);
                }
            }

            if (lowLinks[nodeId] != indexes[nodeId])
            {
                return;
            }

            var component = new List<SemanticId>();
            SemanticId member;
            do
            {
                member = pending.Pop();
                onStack.Remove(member);
                component.Add(member);
            }
            while (member != nodeId);
            components.Add(component);
        }

        foreach (SemanticId nodeId in nodeIds.OrderBy(static id => id.Value, StringComparer.Ordinal))
        {
            if (!indexes.ContainsKey(nodeId))
            {
                Connect(nodeId);
            }
        }

        return components;
    }

    private static SemanticId[] Order(IEnumerable<SemanticId> ids) => ids
        .OrderBy(static id => id.Value, StringComparer.Ordinal)
        .ToArray();
}
