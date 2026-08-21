using UnifiedScript.Core;
using UnifiedScript.Graph;

namespace UnifiedScript.CSharp;

public sealed record CSharpAuthoringSnapshot(
    long Revision,
    CSharpCompilationRequest Request,
    CSharpAnalysisResult Analysis,
    GraphDocument Graph);

public sealed record CSharpAuthoringMutation(
    long BaselineRevision,
    CSharpAuthoringSnapshot? Snapshot,
    IReadOnlyList<UnifiedDiagnostic> Diagnostics)
{
    public bool Succeeded =>
        Snapshot is not null &&
        Diagnostics.All(static diagnostic =>
            diagnostic.Severity != UnifiedDiagnosticSeverity.Error);
}

public sealed class CSharpAuthoringWorkspace
{
    private readonly CSharpSemanticPipeline pipeline = new();
    private readonly CSharpDeclarationPatchApplier declarationPatchApplier = new();
    private readonly CSharpSourcePatchApplier semanticPatchApplier = new();

    public CSharpAuthoringWorkspace(CSharpCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CSharpAnalysisResult analysis = pipeline.Analyze(request);
        if (!analysis.Succeeded)
        {
            throw new InvalidOperationException(
                "The authoring workspace requires a successfully analyzed compilation.");
        }

        CSharpCompilationRequest stabilizedRequest = AttachReconciledSidecars(request, analysis);
        Current = CreateSnapshot(1, stabilizedRequest, analysis);
    }

    public CSharpAuthoringSnapshot Current { get; private set; }

    public CSharpAuthoringMutation Prepare(DeclarationPatchDocument patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return CreateMutation(declarationPatchApplier.Apply(Current.Request, patch));
    }

    public CSharpAuthoringMutation Prepare(SemanticPatchDocument patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return CreateMutation(semanticPatchApplier.Apply(Current.Request, patch));
    }

    public void Commit(CSharpAuthoringMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (!mutation.Succeeded || mutation.Snapshot is null)
        {
            throw new ArgumentException(
                "Only a successful authoring mutation can be committed.",
                nameof(mutation));
        }

        if (mutation.BaselineRevision != Current.Revision)
        {
            throw new InvalidOperationException(
                $"Authoring mutation revision '{mutation.BaselineRevision}' is stale; " +
                $"the current revision is '{Current.Revision}'.");
        }

        Current = mutation.Snapshot;
    }

    private CSharpAuthoringMutation CreateMutation(CSharpPatchResult result)
    {
        if (!result.Succeeded || result.PatchedRequest is null || result.Analysis is null)
        {
            return new CSharpAuthoringMutation(Current.Revision, null, result.Diagnostics);
        }

        CSharpCompilationRequest stabilizedRequest = AttachReconciledSidecars(
            result.PatchedRequest,
            result.Analysis);
        CSharpAuthoringSnapshot snapshot = CreateSnapshot(
            checked(Current.Revision + 1),
            stabilizedRequest,
            result.Analysis);
        return new CSharpAuthoringMutation(
            Current.Revision,
            snapshot,
            result.Diagnostics);
    }

    private static CSharpAuthoringSnapshot CreateSnapshot(
        long revision,
        CSharpCompilationRequest request,
        CSharpAnalysisResult analysis)
    {
        if (!analysis.Succeeded || analysis.Hir is null || analysis.Semantics is null ||
            analysis.ScriptAssets is null)
        {
            throw new ArgumentException(
                "An authoring snapshot requires complete HIR, semantics, and Script Asset artifacts.",
                nameof(analysis));
        }

        GraphDocument graph = new HirGraphProjector().Project(
            analysis.Hir,
            analysis.Semantics,
            analysis.DeclarationReferences);
        return new CSharpAuthoringSnapshot(revision, request, analysis, graph);
    }

    private static CSharpCompilationRequest AttachReconciledSidecars(
        CSharpCompilationRequest request,
        CSharpAnalysisResult analysis)
    {
        IReadOnlyDictionary<string, IdentitySidecarDocument> sidecars = analysis.IdentitySidecars
            .ToDictionary(static sidecar => sidecar.SourcePath, StringComparer.Ordinal);
        CSharpSourceDocument[] sources = request.Sources
            .Select(source => new CSharpSourceDocument(
                source.LogicalPath,
                source.Source,
                sidecars.GetValueOrDefault(source.LogicalPath),
                source.IdentityPins))
            .ToArray();
        return new CSharpCompilationRequest(
            request.AssemblyName,
            sources,
            request.ReferencePaths,
            request.EffectCatalog);
    }
}
