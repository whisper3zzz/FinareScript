using UnifiedScript.Core;
using UnifiedScript.Core.Hir;

namespace UnifiedScript.CSharp;

public sealed record IdentityReconciliationResult(
    HirCompilationUnit CompilationUnit,
    IdentitySidecarDocument Sidecar,
    IReadOnlyList<UnifiedDiagnostic> Diagnostics)
{
    public IReadOnlyDictionary<SemanticId, SemanticId> DeclarationIdentities { get; init; } =
        new Dictionary<SemanticId, SemanticId>();

    public bool Succeeded => Diagnostics.All(
        static diagnostic => diagnostic.Severity != UnifiedDiagnosticSeverity.Error);
}
