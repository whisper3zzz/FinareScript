using Finare.Core;
using Finare.Core.Hir;

namespace Finare.CSharp;

public sealed record IdentityReconciliationResult(
    HirCompilationUnit CompilationUnit,
    IdentitySidecarDocument Sidecar,
    IReadOnlyList<UnifiedDiagnostic> Diagnostics)
{
    public IReadOnlyDictionary<SemanticId, SemanticId> DeclarationIdentities { get; init; } =
        new Dictionary<SemanticId, SemanticId>();

    /// <summary>
    /// The exported method catalog with the same identity rewrite applied as
    /// to HIR declarations. Opaque methods have no HIR node to rewrite, so
    /// the reconciler returns their catalog projection explicitly.
    /// </summary>
    public IReadOnlyList<CSharpMethodProjection> MethodProjections { get; init; } = [];

    public bool Succeeded => Diagnostics.All(
        static diagnostic => diagnostic.Severity != UnifiedDiagnosticSeverity.Error);
}
