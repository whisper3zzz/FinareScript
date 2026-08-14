using UnifiedScript.Core;
using UnifiedScript.Core.Hir;

namespace UnifiedScript.CSharp;

public sealed record CSharpLoweringResult(
    HirCompilationUnit? CompilationUnit,
    IReadOnlyList<UnifiedDiagnostic> Diagnostics)
{
    public bool Succeeded =>
        CompilationUnit is not null && Diagnostics.All(static diagnostic => diagnostic.Severity != UnifiedDiagnosticSeverity.Error);
}

