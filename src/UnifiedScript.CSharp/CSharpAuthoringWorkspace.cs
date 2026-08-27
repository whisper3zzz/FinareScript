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

public enum CSharpAuthoringHistoryDirection
{
    Undo,
    Redo,
}

public sealed record CSharpAuthoringHistoryState(
    bool CanUndo,
    bool CanRedo,
    int UndoDepth,
    int RedoDepth,
    string? UndoTransactionId,
    string? UndoDescription,
    string? RedoTransactionId,
    string? RedoDescription);

public sealed record CSharpAuthoringHistoryMutation(
    long BaselineRevision,
    CSharpAuthoringHistoryDirection Direction,
    string TransactionId,
    string Description,
    CSharpAuthoringSnapshot Snapshot);

public sealed class CSharpAuthoringWorkspace
{
    private const int HistoryCapacity = 100;
    private readonly CSharpSemanticPipeline pipeline = new();
    private readonly CSharpDeclarationPatchApplier declarationPatchApplier = new();
    private readonly CSharpSourcePatchApplier semanticPatchApplier = new();
    private readonly CSharpPatchPlanApplier patchPlanApplier = new();
    private readonly List<HistoryEntry> undoHistory = [];
    private readonly List<HistoryEntry> redoHistory = [];
    private readonly HashSet<string> transactionIds = new(StringComparer.Ordinal);

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

    public CSharpAuthoringHistoryState History => new(
        undoHistory.Count > 0,
        redoHistory.Count > 0,
        undoHistory.Count,
        redoHistory.Count,
        undoHistory.LastOrDefault()?.TransactionId,
        undoHistory.LastOrDefault()?.Description,
        redoHistory.LastOrDefault()?.TransactionId,
        redoHistory.LastOrDefault()?.Description);

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

    public CSharpAuthoringMutation Prepare(SemanticPatchPlanDocument plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return CreateMutation(patchPlanApplier.Apply(Current.Request, plan));
    }

    public void Commit(CSharpAuthoringMutation mutation) => Commit(
        mutation,
        $"local-{Guid.NewGuid():N}",
        "Authoring transaction");

    public void Commit(
        CSharpAuthoringMutation mutation,
        string transactionId,
        string description)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ValidateTransactionMetadata(transactionId, description);
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

        if (!transactionIds.Add(transactionId))
        {
            throw new InvalidOperationException(
                $"Authoring transaction ID '{transactionId}' was already committed.");
        }

        PushHistory(
            undoHistory,
            new HistoryEntry(transactionId, description, Current, mutation.Snapshot));
        redoHistory.Clear();
        Current = mutation.Snapshot;
    }

    public CSharpAuthoringHistoryMutation PrepareUndo() => PrepareHistoryMutation(
        CSharpAuthoringHistoryDirection.Undo,
        undoHistory,
        "There is no committed authoring transaction to undo.");

    public CSharpAuthoringHistoryMutation PrepareRedo() => PrepareHistoryMutation(
        CSharpAuthoringHistoryDirection.Redo,
        redoHistory,
        "There is no undone authoring transaction to redo.");

    public void Commit(CSharpAuthoringHistoryMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (mutation.BaselineRevision != Current.Revision)
        {
            throw new InvalidOperationException(
                $"Authoring history mutation revision '{mutation.BaselineRevision}' is stale; " +
                $"the current revision is '{Current.Revision}'.");
        }

        List<HistoryEntry> source = mutation.Direction == CSharpAuthoringHistoryDirection.Undo
            ? undoHistory
            : redoHistory;
        List<HistoryEntry> destination = mutation.Direction == CSharpAuthoringHistoryDirection.Undo
            ? redoHistory
            : undoHistory;
        HistoryEntry? entry = source.LastOrDefault();
        if (entry is null ||
            !string.Equals(entry.TransactionId, mutation.TransactionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The authoring history changed after the undo/redo operation was prepared.");
        }

        CSharpAuthoringSnapshot expected = mutation.Direction == CSharpAuthoringHistoryDirection.Undo
            ? entry.Before
            : entry.After;
        if (mutation.Snapshot.Revision != checked(Current.Revision + 1) ||
            mutation.Snapshot.Analysis.Hir?.Id != expected.Analysis.Hir?.Id)
        {
            throw new InvalidOperationException(
                "The prepared undo/redo snapshot no longer matches its history entry.");
        }

        source.RemoveAt(source.Count - 1);
        PushHistory(destination, entry);
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

    private CSharpAuthoringHistoryMutation PrepareHistoryMutation(
        CSharpAuthoringHistoryDirection direction,
        IReadOnlyList<HistoryEntry> history,
        string emptyMessage)
    {
        HistoryEntry entry = history.LastOrDefault() ??
            throw new InvalidOperationException(emptyMessage);
        CSharpAuthoringSnapshot target = direction == CSharpAuthoringHistoryDirection.Undo
            ? entry.Before
            : entry.After;
        return new CSharpAuthoringHistoryMutation(
            Current.Revision,
            direction,
            entry.TransactionId,
            entry.Description,
            target with { Revision = checked(Current.Revision + 1) });
    }

    private static void PushHistory(List<HistoryEntry> history, HistoryEntry entry)
    {
        if (history.Count == HistoryCapacity)
        {
            history.RemoveAt(0);
        }
        history.Add(entry);
    }

    private static void ValidateTransactionMetadata(string transactionId, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (transactionId.Length > 128 || description.Length > 256)
        {
            throw new ArgumentException(
                "Authoring transaction IDs and descriptions must be at most 128 and 256 characters.");
        }
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
            request.EffectCatalog,
            request.Settings,
            request.RuntimeReferencePaths,
            request.IncludeDefaultReferences);
    }

    private sealed record HistoryEntry(
        string TransactionId,
        string Description,
        CSharpAuthoringSnapshot Before,
        CSharpAuthoringSnapshot After);
}
