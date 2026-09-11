using Finare.Core;
using Finare.Core.Hir;
using Finare.Semantics;

namespace Finare.CSharp;

/// <summary>
/// Roslyn-owned method metadata used to keep source declarations available
/// when a method cannot be represented by executable HIR.  A projection never
/// carries a synthetic body; graph bodies remain in <see cref="HirMethodDeclaration"/>.
/// </summary>
public sealed record CSharpMethodProjection(
    SemanticId Id,
    string ContainingTypeName,
    string QualifiedName,
    string Name,
    SourceSpan Span,
    string ReturnType,
    bool IsExplicitVisual,
    bool IsImplicitEntry,
    bool IsPublic,
    bool IsStatic,
    bool IsAsync,
    IReadOnlyList<CSharpMethodParameterProjection> Parameters,
    IReadOnlyList<HirAttribute>? Attributes,
    MethodBodyRepresentation BodyRepresentation,
    IReadOnlyList<UnifiedDiagnostic> CapabilityDiagnostics)
{
    public bool IsGraphEditable => BodyRepresentation == MethodBodyRepresentation.VisualGraph;
}

public sealed record CSharpMethodParameterProjection(
    SemanticId Id,
    SourceSpan Span,
    string Name,
    string TypeName,
    HirParameterDirection Direction);
