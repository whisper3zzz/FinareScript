using Finare.Core;

namespace Finare.CSharp;

public sealed record CSharpPatchResult(
    CSharpCompilationRequest? PatchedRequest,
    CSharpAnalysisResult? Analysis,
    IReadOnlyList<UnifiedDiagnostic> Diagnostics)
{
    public bool Succeeded =>
        PatchedRequest is not null &&
        Analysis?.Succeeded == true &&
        Diagnostics.All(static diagnostic => diagnostic.Severity != UnifiedDiagnosticSeverity.Error);
}
