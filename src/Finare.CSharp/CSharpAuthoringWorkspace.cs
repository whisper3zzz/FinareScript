using Finare.Core;
using Finare.Graph;

namespace Finare.CSharp;

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
    private readonly Guid lineageId = Guid.NewGuid();
    private long? draftBaselineRevision;
    private SemanticId? draftBaselineCompilationId;

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

    private CSharpAuthoringWorkspace(CSharpAuthoringWorkspace source)
    {
        pipeline = source.pipeline;
        declarationPatchApplier = source.declarationPatchApplier;
        semanticPatchApplier = source.semanticPatchApplier;
        patchPlanApplier = source.patchPlanApplier;
        lineageId = source.lineageId;
        Current = source.Current;
        undoHistory.AddRange(source.undoHistory);
        redoHistory.AddRange(source.redoHistory);
        transactionIds.UnionWith(source.transactionIds);
        LastOperationId = source.LastOperationId;
        draftBaselineRevision = source.draftBaselineRevision;
        draftBaselineCompilationId = source.draftBaselineCompilationId;
    }

    private CSharpAuthoringWorkspace(
        CSharpCompilationRequest template,
        CSharpAuthoringHistoryDocument history)
    {
        CSharpAuthoringHistoryDocument normalized =
            CSharpAuthoringHistoryJson.NormalizeAndValidate(history);
        if (normalized.SemanticConfigurationId != template.ConfigurationId)
        {
            throw new InvalidDataException(
                "The persisted authoring history was created with a different compilation configuration.");
        }

        Dictionary<string, CSharpAuthoringHistorySnapshotDocument> snapshotDocuments =
            normalized.Snapshots.ToDictionary(
                static snapshot => snapshot.SnapshotId,
                StringComparer.Ordinal);
        CSharpAuthoringHistorySnapshotDocument currentDocument =
            snapshotDocuments[normalized.CurrentSnapshotId];
        ValidateCurrentSources(template, currentDocument);
        CSharpCompilationRequest restoredRequest = CreateRequest(template, currentDocument);
        CSharpAnalysisResult analysis = pipeline.Analyze(restoredRequest);
        if (!analysis.Succeeded || analysis.ScriptAssets?.SourceCompilationId !=
            currentDocument.SourceCompilationId)
        {
            throw new InvalidDataException(
                "The persisted authoring history current snapshot no longer analyzes to its recorded compilation.");
        }

        CSharpCompilationRequest stabilizedRequest = AttachReconciledSidecars(restoredRequest, analysis);
        Current = CreateSnapshot(normalized.Revision, stabilizedRequest, analysis);
        var snapshots = new Dictionary<string, StoredSnapshot>(StringComparer.Ordinal)
        {
            [normalized.CurrentSnapshotId] = new StoredSnapshot(Current),
        };
        foreach (CSharpAuthoringHistorySnapshotDocument snapshot in normalized.Snapshots)
        {
            snapshots.TryAdd(
                snapshot.SnapshotId,
                new StoredSnapshot(CreateRequest(template, snapshot), snapshot.SourceCompilationId));
        }

        undoHistory.AddRange(normalized.UndoHistory.Select(entry => RestoreEntry(entry, snapshots)));
        redoHistory.AddRange(normalized.RedoHistory.Select(entry => RestoreEntry(entry, snapshots)));
        transactionIds.UnionWith(normalized.CommittedOperationIds);
        LastOperationId = normalized.LastOperationId;
    }

    public static CSharpAuthoringWorkspace Restore(
        CSharpCompilationRequest request,
        CSharpAuthoringHistoryDocument history)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(history);
        return new CSharpAuthoringWorkspace(request, history);
    }

    public CSharpAuthoringSnapshot Current { get; private set; }

    public string? LastOperationId { get; private set; }

    public bool ContainsOperationId(string operationId) =>
        !string.IsNullOrWhiteSpace(operationId) && transactionIds.Contains(operationId);

    public CSharpAuthoringHistoryState History => new(
        undoHistory.Count > 0,
        redoHistory.Count > 0,
        undoHistory.Count,
        redoHistory.Count,
        undoHistory.LastOrDefault()?.TransactionId,
        undoHistory.LastOrDefault()?.Description,
        redoHistory.LastOrDefault()?.TransactionId,
        redoHistory.LastOrDefault()?.Description);

    public CSharpAuthoringWorkspace CreateDraft()
    {
        if (draftBaselineRevision is not null)
        {
            throw new InvalidOperationException("A staged authoring draft cannot create a nested draft.");
        }

        var draft = new CSharpAuthoringWorkspace(this)
        {
            draftBaselineRevision = Current.Revision,
            draftBaselineCompilationId = Current.Analysis.ScriptAssets?.SourceCompilationId ??
                throw new InvalidOperationException(
                    "An authoring draft requires a source compilation identity."),
        };
        return draft;
    }

    public CSharpAuthoringMutation PrepareDraftCommit(CSharpAuthoringWorkspace draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        SemanticId currentCompilationId = Current.Analysis.ScriptAssets?.SourceCompilationId ??
            throw new InvalidOperationException(
                "The authoring workspace has no source compilation identity.");
        if (draft.lineageId != lineageId ||
            draft.draftBaselineRevision != Current.Revision ||
            draft.draftBaselineCompilationId != currentCompilationId)
        {
            throw new InvalidOperationException(
                "The staged authoring draft no longer belongs to the current workspace snapshot.");
        }
        if (draft.Current.Revision <= Current.Revision)
        {
            throw new InvalidOperationException(
                "A staged authoring draft must contain at least one accepted edit before commit.");
        }

        CSharpAuthoringSnapshot snapshot = draft.Current with
        {
            Revision = checked(Current.Revision + 1),
        };
        return new CSharpAuthoringMutation(Current.Revision, snapshot, []);
    }

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

    public CSharpAuthoringMutation Prepare(CSharpSourceTextPatchDocument patch)
    {
        patch = CSharpSourceTextPatchJson.NormalizeAndValidate(patch);
        CSharpSourceDocument? source = Current.Request.Sources.SingleOrDefault(candidate =>
            string.Equals(candidate.LogicalPath, patch.SourcePath, StringComparison.Ordinal));
        if (source is null)
        {
            return SourceTextFailure(
                CSharpSourceTextPatchDiagnosticCodes.SourceNotFound,
                $"Source '{patch.SourcePath}' is not part of the current authoring workspace.",
                patch.SourcePath);
        }

        string currentDigest = CSharpSourceTextPatchJson.ComputeContentDigest(source.Source);
        if (!string.Equals(
                currentDigest,
                patch.BaselineContentDigest,
                StringComparison.Ordinal))
        {
            return SourceTextFailure(
                CSharpSourceTextPatchDiagnosticCodes.BaselineMismatch,
                $"Source '{patch.SourcePath}' changed after the C# edit buffer was loaded; " +
                "refresh before applying it.",
                patch.SourcePath);
        }
        if (string.Equals(source.Source, patch.SourceText, StringComparison.Ordinal))
        {
            return SourceTextFailure(
                CSharpSourceTextPatchDiagnosticCodes.NoChange,
                $"Source '{patch.SourcePath}' contains no text change to preview.",
                patch.SourcePath);
        }

        CSharpSourceDocument[] sources = Current.Request.Sources
            .Select(candidate => string.Equals(
                    candidate.LogicalPath,
                    patch.SourcePath,
                    StringComparison.Ordinal)
                ? new CSharpSourceDocument(
                    candidate.LogicalPath,
                    patch.SourceText,
                    candidate.IdentitySidecar,
                    candidate.IdentityPins)
                : candidate)
            .ToArray();
        var request = new CSharpCompilationRequest(
            Current.Request.AssemblyName,
            sources,
            Current.Request.ReferencePaths,
            Current.Request.EffectCatalog,
            Current.Request.Settings,
            Current.Request.RuntimeReferencePaths,
            Current.Request.IncludeDefaultReferences);
        CSharpAnalysisResult analysis = pipeline.Analyze(request);
        return CreateMutation(new CSharpPatchResult(
            analysis.Succeeded ? request : null,
            analysis.Succeeded ? analysis : null,
            analysis.Diagnostics));
    }

    /// <summary>Analyze an externally supplied project snapshot without changing the live workspace.</summary>
    public CSharpAuthoringMutation PrepareRefresh(CSharpCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CSharpAnalysisResult analysis = pipeline.Analyze(request);
        return CreateMutation(new CSharpPatchResult(
            analysis.Succeeded ? request : null,
            analysis.Succeeded ? analysis : null,
            analysis.Diagnostics));
    }

    /// <summary>Start history at a new project context while retaining revision and replay protection.</summary>
    public CSharpAuthoringWorkspace Rebase(CSharpAuthoringMutation mutation, string transactionId, string description)
    {
        var rebased = new CSharpAuthoringWorkspace(this);
        rebased.Commit(mutation, transactionId, description);
        rebased.undoHistory.Clear();
        rebased.redoHistory.Clear();
        return rebased;
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
            new HistoryEntry(
                transactionId,
                description,
                new StoredSnapshot(Current),
                new StoredSnapshot(mutation.Snapshot)));
        redoHistory.Clear();
        Current = mutation.Snapshot;
        LastOperationId = transactionId;
    }

    public CSharpAuthoringHistoryDocument PreviewCommit(
        CSharpAuthoringMutation mutation,
        string transactionId,
        string description)
    {
        var preview = new CSharpAuthoringWorkspace(this);
        preview.Commit(mutation, transactionId, description);
        return preview.ExportHistory();
    }

    public CSharpAuthoringHistoryMutation PrepareUndo() => PrepareHistoryMutation(
        CSharpAuthoringHistoryDirection.Undo,
        undoHistory,
        "There is no committed authoring transaction to undo.");

    public CSharpAuthoringHistoryMutation PrepareRedo() => PrepareHistoryMutation(
        CSharpAuthoringHistoryDirection.Redo,
        redoHistory,
        "There is no undone authoring transaction to redo.");

    public void Commit(CSharpAuthoringHistoryMutation mutation) => Commit(
        mutation,
        $"local-{Guid.NewGuid():N}");

    public void Commit(CSharpAuthoringHistoryMutation mutation, string operationId)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ValidateOperationId(operationId);
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

        StoredSnapshot expected = mutation.Direction == CSharpAuthoringHistoryDirection.Undo
            ? entry.Before
            : entry.After;
        if (mutation.Snapshot.Revision != checked(Current.Revision + 1) ||
            mutation.Snapshot.Analysis.ScriptAssets?.SourceCompilationId !=
                expected.SourceCompilationId)
        {
            throw new InvalidOperationException(
                "The prepared undo/redo snapshot no longer matches its history entry.");
        }

        if (!transactionIds.Add(operationId))
        {
            throw new InvalidOperationException(
                $"Authoring transaction ID '{operationId}' was already committed.");
        }

        source.RemoveAt(source.Count - 1);
        PushHistory(destination, entry);
        Current = mutation.Snapshot;
        LastOperationId = operationId;
    }

    public CSharpAuthoringHistoryDocument PreviewCommit(
        CSharpAuthoringHistoryMutation mutation,
        string operationId)
    {
        var preview = new CSharpAuthoringWorkspace(this);
        preview.Commit(mutation, operationId);
        return preview.ExportHistory();
    }

    public CSharpAuthoringHistoryDocument ExportHistory()
    {
        var snapshots = new Dictionary<string, CSharpAuthoringHistorySnapshotDocument>(
            StringComparer.Ordinal);
        CSharpAuthoringHistorySnapshotDocument current = CreateHistorySnapshot(Current);
        snapshots.Add(current.SnapshotId, current);
        CSharpAuthoringHistoryEntryDocument[] undo = undoHistory
            .Select(entry => CreateHistoryEntry(entry, snapshots))
            .ToArray();
        CSharpAuthoringHistoryEntryDocument[] redo = redoHistory
            .Select(entry => CreateHistoryEntry(entry, snapshots))
            .ToArray();
        return CSharpAuthoringHistoryJson.NormalizeAndValidate(
            new CSharpAuthoringHistoryDocument(
                CSharpAuthoringHistoryDocument.CurrentSchemaVersion,
                Current.Revision,
                Current.Request.ConfigurationId,
                current.SnapshotId,
                snapshots.Values.ToArray(),
                undo,
                redo,
                transactionIds.Order(StringComparer.Ordinal).ToArray(),
                LastOperationId));
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

    private CSharpAuthoringMutation SourceTextFailure(
        string code,
        string message,
        string sourcePath) => CreateMutation(new CSharpPatchResult(
            null,
            null,
            [new UnifiedDiagnostic(
                code,
                UnifiedDiagnosticSeverity.Error,
                message,
                SourceSpan.Unknown(sourcePath))]));

    private CSharpAuthoringHistoryMutation PrepareHistoryMutation(
        CSharpAuthoringHistoryDirection direction,
        IReadOnlyList<HistoryEntry> history,
        string emptyMessage)
    {
        HistoryEntry entry = history.LastOrDefault() ??
            throw new InvalidOperationException(emptyMessage);
        StoredSnapshot target = direction == CSharpAuthoringHistoryDirection.Undo
            ? entry.Before
            : entry.After;
        CSharpAuthoringSnapshot snapshot = target.Materialize(
            pipeline,
            checked(Current.Revision + 1));
        return new CSharpAuthoringHistoryMutation(
            Current.Revision,
            direction,
            entry.TransactionId,
            entry.Description,
            snapshot);
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
        ValidateOperationId(transactionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (description.Length > 256)
        {
            throw new ArgumentException(
                "Authoring transaction IDs and descriptions must be at most 128 and 256 characters.");
        }
    }

    private static void ValidateOperationId(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (operationId.Length > 128)
        {
            throw new ArgumentException("Authoring operation IDs must be at most 128 characters.");
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

    private static HistoryEntry RestoreEntry(
        CSharpAuthoringHistoryEntryDocument entry,
        IReadOnlyDictionary<string, StoredSnapshot> snapshots) => new(
            entry.TransactionId,
            entry.Description,
            snapshots[entry.BeforeSnapshotId],
            snapshots[entry.AfterSnapshotId]);

    private static CSharpAuthoringHistoryEntryDocument CreateHistoryEntry(
        HistoryEntry entry,
        IDictionary<string, CSharpAuthoringHistorySnapshotDocument> snapshots)
    {
        CSharpAuthoringHistorySnapshotDocument before = entry.Before.ToDocument();
        CSharpAuthoringHistorySnapshotDocument after = entry.After.ToDocument();
        snapshots.TryAdd(before.SnapshotId, before);
        snapshots.TryAdd(after.SnapshotId, after);
        return new CSharpAuthoringHistoryEntryDocument(
            entry.TransactionId,
            entry.Description,
            before.SnapshotId,
            after.SnapshotId);
    }

    private static CSharpAuthoringHistorySnapshotDocument CreateHistorySnapshot(
        CSharpAuthoringSnapshot snapshot) => CreateHistorySnapshot(
            snapshot.Request,
            snapshot.Analysis.ScriptAssets?.SourceCompilationId ??
                throw new InvalidOperationException(
                    "An authoring snapshot has no source compilation ID."));

    private static CSharpAuthoringHistorySnapshotDocument CreateHistorySnapshot(
        CSharpCompilationRequest request,
        SemanticId sourceCompilationId)
    {
        CSharpAuthoringHistorySourceDocument[] sources = request.Sources
            .Select(static source => new CSharpAuthoringHistorySourceDocument(
                source.LogicalPath,
                source.Source,
                source.IdentitySidecar,
                source.IdentityPins))
            .ToArray();
        return new CSharpAuthoringHistorySnapshotDocument(
            CSharpAuthoringHistoryJson.CreateSnapshotId(sources),
            sourceCompilationId,
            sources);
    }

    private static CSharpCompilationRequest CreateRequest(
        CSharpCompilationRequest template,
        CSharpAuthoringHistorySnapshotDocument snapshot) => new(
            template.AssemblyName,
            snapshot.Sources.Select(static source => new CSharpSourceDocument(
                source.LogicalPath,
                source.Source,
                source.IdentitySidecar,
                source.IdentityPins)),
            template.ReferencePaths,
            template.EffectCatalog,
            template.Settings,
            template.RuntimeReferencePaths,
            template.IncludeDefaultReferences);

    private static void ValidateCurrentSources(
        CSharpCompilationRequest request,
        CSharpAuthoringHistorySnapshotDocument current)
    {
        Dictionary<string, CSharpAuthoringHistorySourceDocument> archived = current.Sources
            .ToDictionary(static source => source.LogicalPath, StringComparer.Ordinal);
        if (request.Sources.Count != archived.Count)
        {
            throw new InvalidDataException(
                "The persisted authoring history source set no longer matches the workspace.");
        }
        foreach (CSharpSourceDocument source in request.Sources)
        {
            if (!archived.TryGetValue(source.LogicalPath, out CSharpAuthoringHistorySourceDocument? other) ||
                !string.Equals(source.Source, other.Source, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Source '{source.LogicalPath}' changed after the authoring history was persisted.");
            }
            if (source.IdentitySidecar is not null &&
                !IsEmptyIdentity(source.IdentitySidecar) &&
                !IdentityEquals(source.IdentitySidecar, other.IdentitySidecar))
            {
                throw new InvalidDataException(
                    $"Identity sidecar for '{source.LogicalPath}' changed after history was persisted.");
            }
            if (source.IdentityPins.Count > 0 &&
                !source.IdentityPins.OrderBy(static pin => pin.Id.Value, StringComparer.Ordinal)
                    .SequenceEqual(other.IdentityPins))
            {
                throw new InvalidDataException(
                    $"Identity pins for '{source.LogicalPath}' changed after history was persisted.");
            }
        }
    }

    private static bool IdentityEquals(
        IdentitySidecarDocument expected,
        IdentitySidecarDocument? actual) => actual is not null && string.Equals(
            IdentitySidecarJson.Serialize(expected, writeIndented: false),
            IdentitySidecarJson.Serialize(actual, writeIndented: false),
            StringComparison.Ordinal);

    private static bool IsEmptyIdentity(IdentitySidecarDocument identity) =>
        identity.Generation == 0 &&
        identity.Entries.Count == 0 &&
        identity.Tombstones.Count == 0;

    private sealed record HistoryEntry(
        string TransactionId,
        string Description,
        StoredSnapshot Before,
        StoredSnapshot After);

    private sealed class StoredSnapshot
    {
        private CSharpAuthoringSnapshot? snapshot;

        public StoredSnapshot(CSharpAuthoringSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            this.snapshot = snapshot;
            Request = snapshot.Request;
            SourceCompilationId = snapshot.Analysis.ScriptAssets?.SourceCompilationId ??
                throw new ArgumentException(
                    "A stored authoring snapshot requires a source compilation ID.",
                    nameof(snapshot));
        }

        public StoredSnapshot(CSharpCompilationRequest request, SemanticId sourceCompilationId)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (sourceCompilationId.IsEmpty)
            {
                throw new ArgumentException(
                    "A stored authoring snapshot requires a source compilation ID.",
                    nameof(sourceCompilationId));
            }
            Request = request;
            SourceCompilationId = sourceCompilationId;
        }

        public CSharpCompilationRequest Request { get; }

        public SemanticId SourceCompilationId { get; }

        public CSharpAuthoringSnapshot Materialize(
            CSharpSemanticPipeline pipeline,
            long revision)
        {
            if (snapshot is null)
            {
                CSharpAnalysisResult analysis = pipeline.Analyze(Request);
                if (!analysis.Succeeded ||
                    analysis.ScriptAssets?.SourceCompilationId != SourceCompilationId)
                {
                    throw new InvalidDataException(
                        "A persisted authoring snapshot no longer compiles to its recorded identity.");
                }
                CSharpCompilationRequest stabilized = AttachReconciledSidecars(Request, analysis);
                snapshot = CreateSnapshot(1, stabilized, analysis);
            }
            return snapshot with { Revision = revision };
        }

        public CSharpAuthoringHistorySnapshotDocument ToDocument() =>
            CreateHistorySnapshot(Request, SourceCompilationId);
    }
}
