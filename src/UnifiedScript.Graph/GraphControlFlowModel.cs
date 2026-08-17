using UnifiedScript.Core;

namespace UnifiedScript.Graph;

public sealed record GraphControlFlowArc(
    SemanticId Id,
    SemanticId FromNodeId,
    string FromPort,
    SemanticId ToNodeId,
    string ToPort,
    bool IsSynthetic);

public sealed record GraphControlFlowNode(
    SemanticId Id,
    bool IsSyntheticExit,
    bool IsReachable,
    IReadOnlyList<SemanticId> PredecessorIds,
    IReadOnlyList<SemanticId> SuccessorIds,
    IReadOnlyList<SemanticId> DominatorIds,
    SemanticId? ImmediateDominatorId,
    IReadOnlyList<SemanticId> PostDominatorIds,
    SemanticId? ImmediatePostDominatorId);

public sealed record GraphControlFlowBranch(
    SemanticId BranchNodeId,
    SemanticId IfRegionId,
    SemanticId ThenRegionId,
    SemanticId ElseRegionId,
    SemanticId ThenEntryNodeId,
    SemanticId ElseEntryNodeId,
    SemanticId? MergeNodeId,
    bool ThenExitsFunction,
    bool ElseExitsFunction);

public sealed record GraphControlFlowLoop(
    SemanticId LoopNodeId,
    SemanticId LoopRegionId,
    SemanticId BodyRegionId,
    SemanticId HeaderEntryNodeId,
    SemanticId BodyEntryNodeId,
    SemanticId ExitNodeId,
    IReadOnlyList<SemanticId> BackEdgeArcIds,
    IReadOnlyList<SemanticId> BreakNodeIds,
    IReadOnlyList<SemanticId> ContinueNodeIds);

public sealed record GraphControlFlowComponent(
    IReadOnlyList<SemanticId> NodeIds,
    IReadOnlyList<SemanticId> EntryNodeIds,
    bool IsReducible);

public sealed record GraphControlFlowFunction(
    SemanticId FunctionId,
    SemanticId EntryNodeId,
    SemanticId ExitNodeId,
    IReadOnlyList<GraphControlFlowNode> Nodes,
    IReadOnlyList<GraphControlFlowArc> Arcs,
    IReadOnlyList<GraphControlFlowBranch> Branches,
    IReadOnlyList<GraphControlFlowLoop> Loops,
    IReadOnlyList<GraphControlFlowComponent> CyclicComponents,
    IReadOnlyList<SemanticId> UnreachableNodeIds,
    bool HasCompleteExitCoverage,
    bool IsReducible);

public sealed record GraphControlFlowAnalysis(
    SemanticId SourceCompilationId,
    SemanticId? SemanticConfigurationId,
    IReadOnlyList<GraphControlFlowFunction> Functions);
