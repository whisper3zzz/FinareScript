using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Semantics;

namespace UnifiedScript.CSharp;

public sealed record CSharpAnalysisResult(
    HirCompilationUnit? Hir,
    BoundCompilation? Semantics,
    IReadOnlyList<UnifiedDiagnostic> Diagnostics)
{
    public bool Succeeded =>
        Hir is not null &&
        Semantics is not null &&
        Diagnostics.All(static diagnostic => diagnostic.Severity != UnifiedDiagnosticSeverity.Error);
}
