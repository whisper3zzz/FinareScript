using UnifiedScript.Core;

namespace UnifiedScript.Graph;

public enum GraphPortDirection
{
    Input,
    Output,
}

public enum GraphPortKind
{
    Control,
    Data,
}

public enum GraphEdgeKind
{
    Control,
    Data,
}

public sealed record GraphPort(
    string Name,
    GraphPortDirection Direction,
    GraphPortKind Kind);

public sealed record GraphNode(
    SemanticId Id,
    string Kind,
    string Label,
    SourceSpan Span,
    IReadOnlyList<GraphPort> Ports,
    IReadOnlyDictionary<string, string> Properties);

public sealed record GraphEdge(
    SemanticId Id,
    GraphEdgeKind Kind,
    SemanticId FromNode,
    string FromPort,
    SemanticId ToNode,
    string ToPort);

public sealed record GraphFunction(
    SemanticId Id,
    string QualifiedName,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges);

public sealed record GraphDocument(
    string SchemaVersion,
    SemanticId SourceCompilationId,
    IReadOnlyList<GraphFunction> Functions);

