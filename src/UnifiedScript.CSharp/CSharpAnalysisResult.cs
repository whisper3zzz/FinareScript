using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Semantics;

namespace UnifiedScript.CSharp;

public sealed record CSharpAnalysisResult(
    HirCompilationUnit? Hir,
    BoundCompilation? Semantics,
    IReadOnlyList<UnifiedDiagnostic> Diagnostics)
{
    public IReadOnlyList<IdentitySidecarDocument> IdentitySidecars { get; init; } = [];

    public ScriptAssetDocument? ScriptAssets { get; init; }

    public IReadOnlyDictionary<SemanticId, SemanticId> DeclarationReferences { get; init; } =
        new Dictionary<SemanticId, SemanticId>();

    public IReadOnlyDictionary<SemanticId, SemanticId> DeclarationOwners { get; init; } =
        new Dictionary<SemanticId, SemanticId>();

    public VisualProfileReport Profile { get; init; } = new(
        VisualProfileReport.CurrentSchemaVersion,
        VisualCSharpProfileContract.Current,
        []);

    public bool Succeeded =>
        Hir is not null &&
        Semantics is not null &&
        ScriptAssets is not null &&
        Diagnostics.All(static diagnostic => diagnostic.Severity != UnifiedDiagnosticSeverity.Error);
}
