using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Finare.Authoring;
using Finare.Core;
using Finare.CSharp;
using Finare.Graph;
using Finare.Runtime;
using Finare.Unreal;

namespace Finare.Cli;

internal sealed class AuthoringSession : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private CoreClrDebugLoopbackTransport transport = new();
    private CSharpAuthoringWorkspace authoring;
    private readonly string sourcePath;
    private string sourceLogicalPath;
    private IReadOnlyDictionary<string, string> sourceFiles;
    private IReadOnlySet<string> readOnlySources;
    private IReadOnlyDictionary<string, string> identityFiles;
    private readonly Func<ProjectAuthoringInput>? refreshProject;
    private readonly Dictionary<string, string> persistedSources = new(StringComparer.Ordinal);
    private readonly string? identityPath;
    private readonly AuthoringArtifactOutputs artifactOutputs;
    private readonly CSharpProgramEmitter emitter = new();
    private readonly CSharpGraphRoundTripInspector roundTripInspector = new();
    private readonly Dictionary<string, PreparedAuthoringPreview> preparedPreviews =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativePublicationReservation> nativeReservations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativePublicationFinishResponse> nativePublicationResults =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileBaseline> persistedArtifactBaselines =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SemanticId> breakpointIds = [];
    private IReadOnlyList<SemanticBreakpointBinding> bindings = [];
    private CoreClrDebugReloadResult reload;
    private CoreClrDebugExecution? execution;
    private CoreClrDebugPause? pause;
    private bool waitInProgress;
    private ActiveAuthoringGroup? activeGroup;
    private string? persistedIdentity;
    private readonly Dictionary<string, string?> persistedIdentities = new(StringComparer.Ordinal);
    private string historyPersistenceState;
    private string? historyPersistenceMessage;

    public AuthoringSession(
        CSharpCompilationRequest request,
        string sourcePath,
        string? identityPath,
        AuthoringArtifactOutputs artifactOutputs,
        IReadOnlyDictionary<string, string>? sourceFiles,
        IReadOnlySet<string>? readOnlySources,
        Func<ProjectAuthoringInput>? refreshProject,
        IReadOnlyDictionary<string, string>? identityFiles)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(artifactOutputs);

        this.sourcePath = Path.GetFullPath(sourcePath);
        this.identityPath = identityPath is null ? null : Path.GetFullPath(identityPath);
        this.artifactOutputs = artifactOutputs.Normalize();
        this.refreshProject = refreshProject;
        this.readOnlySources = readOnlySources ?? new HashSet<string>(StringComparer.Ordinal);
        string normalizedSourcePath = this.sourcePath.Replace('\\', '/');
        CSharpSourceDocument primarySource = request.Sources.SingleOrDefault(source =>
                string.Equals(
                    Path.GetFullPath(sourceFiles is not null && sourceFiles.TryGetValue(source.LogicalPath, out string? physical)
                        ? physical : source.LogicalPath).Replace('\\', '/'),
                    normalizedSourcePath,
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"The authoring source '{this.sourcePath}' is not present in the compilation request.",
                nameof(sourcePath));
        sourceLogicalPath = primarySource.LogicalPath;
        var normalizedSourceFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string logicalPath, string physicalPath) in sourceFiles ??
            new Dictionary<string, string> { [sourceLogicalPath] = this.sourcePath })
        {
            normalizedSourceFiles.Add(logicalPath.Replace('\\', '/'), Path.GetFullPath(physicalPath));
        }
        this.sourceFiles = normalizedSourceFiles;
        var normalizedIdentityFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string logicalPath, string physicalPath) in identityFiles ??
            new Dictionary<string, string>())
        {
            normalizedIdentityFiles[logicalPath.Replace('\\', '/')] = Path.GetFullPath(physicalPath);
        }
        if (this.identityPath is not null)
            normalizedIdentityFiles[sourceLogicalPath] = this.identityPath;
        this.identityFiles = normalizedIdentityFiles;
        foreach (CSharpSourceDocument source in request.Sources)
        {
            if (!this.sourceFiles.TryGetValue(source.LogicalPath, out string? physicalPath))
            {
                throw new ArgumentException(
                    $"Compilation source '{source.LogicalPath}' has no physical authoring path.",
                    nameof(sourceFiles));
            }
            string diskSource = File.ReadAllText(physicalPath);
            if (!string.Equals(source.Source, diskSource, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Compilation source '{source.LogicalPath}' does not match its file on disk.",
                    nameof(request));
            }
            persistedSources.Add(source.LogicalPath, diskSource);
        }
        persistedIdentity = this.identityPath is null
            ? null
            : File.Exists(this.identityPath)
                ? File.ReadAllText(this.identityPath)
                : null;
        foreach ((string logicalPath, string path) in this.identityFiles)
            persistedIdentities[logicalPath] = File.Exists(path) ? File.ReadAllText(path) : null;
        if (this.artifactOutputs.HistoryPath is null)
        {
            authoring = new CSharpAuthoringWorkspace(request);
            historyPersistenceState = "disabled";
        }
        else if (!File.Exists(this.artifactOutputs.HistoryPath))
        {
            authoring = new CSharpAuthoringWorkspace(request);
            historyPersistenceState = "empty";
        }
        else
        {
            try
            {
                CSharpAuthoringHistoryDocument history =
                    CSharpAuthoringHistoryJson.Deserialize(
                        File.ReadAllText(this.artifactOutputs.HistoryPath));
                authoring = CSharpAuthoringWorkspace.Restore(request, history);
                historyPersistenceState = "restored";
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException or ArgumentException)
            {
                authoring = new CSharpAuthoringWorkspace(request);
                historyPersistenceState = "rejected";
                historyPersistenceMessage = exception.Message;
            }
        }
        reload = transport.Reload(authoring.Current.Request);
        CaptureArtifactBaselines();
        Health = CreateHealth(reload.Generation);
        Workspace = CreateWorkspace(authoring.Current, reload.Generation);
    }

    public DebugHealthResponse Health { get; private set; }

    public AuthoringWorkspaceResponse Workspace { get; private set; }

    public void PublishInitialArtifacts()
    {
        EnsureFilesUnchanged();
        Persist(authoring.Current, authoring.ExportHistory(), PrepareGraphLayout(authoring.Current, authoring.Current));
    }

    public async Task<NativePublicationPrepareResponse> PrepareNativePublicationAsync(
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureAuthoringAvailable();
            if (transaction.PreviewToken is null)
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1020",
                    "Native publication requires the exact accepted authoring preview token.");
            }

            if (nativePublicationResults.TryGetValue(transaction.PreviewToken, out NativePublicationFinishResponse? adopted))
            {
                if (!string.Equals(adopted.TransactionId, transaction.TransactionId, StringComparison.Ordinal))
                    throw new AuthoringProtocolException(AuthoringFailureKind.Conflict, "UAW1021", "The preview token belongs to another native publication transaction.");
                return adopted.Prepare;
            }
            if (nativeReservations.TryGetValue(transaction.PreviewToken, out NativePublicationReservation? existing))
            {
                if (!string.Equals(existing.Prepare.TransactionId, transaction.TransactionId, StringComparison.Ordinal))
                    throw new AuthoringProtocolException(AuthoringFailureKind.Conflict, "UAW1021", "The preview token belongs to another native publication transaction.");
                return existing.Prepare;
            }
            ValidateTransaction(transaction);

            EnsureFilesUnchanged();
            PreparedAuthoringPreview prepared = RequirePreparedPreview(
                authoring.Current,
                transaction,
                publishesRuntime: true);
            EnsureNativePublicationIdentityStable(
                authoring.Current.Analysis.ScriptAssets!,
                prepared.Candidate.Analysis.ScriptAssets!);
            CSharpAuthoringMutation? preparedMutation = prepared.HistoryMutation is null
                ? new CSharpAuthoringMutation(
                    authoring.Current.Revision,
                    prepared.Candidate,
                    prepared.Diagnostics)
                : null;
            CSharpAuthoringHistoryDocument history = prepared.HistoryMutation is not null
                ? authoring.PreviewCommit(prepared.HistoryMutation, transaction.TransactionId)
                : authoring.PreviewCommit(
                    preparedMutation!,
                    transaction.TransactionId,
                    transaction.Description);
            PreparedAuthoringOutputs outputs = BuildPreparedOutputs(
                prepared.Candidate,
                history,
                prepared.GraphLayout);
            CoreClrDebugPreparedReload? candidateReload = null;
            try
            {
                candidateReload = transport.PrepareReload(prepared.Candidate.Request);
                NativePublicationPrepareResponse response = CreateNativePublicationPrepare(
                    authoring.Current,
                    prepared,
                    outputs,
                    transaction.TransactionId,
                    transaction.PreviewToken);
                EnsureNativePublicationBaselinesUnchanged(response.Outputs);
                nativeReservations.Add(
                    transaction.PreviewToken,
                    new NativePublicationReservation(
                        response,
                        prepared.Candidate,
                        history,
                        prepared.GraphLayout,
                        prepared.HistoryMutation,
                        candidateReload));
                candidateReload = null;
                return response;
            }
            catch
            {
                candidateReload?.Dispose();
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NativePublicationFinishResponse> FinishNativePublicationAsync(
        AuthoringTransactionContext transaction,
        NativePublicationFinishRequest finish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(finish);
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureAuthoringAvailable();
            if (transaction.PreviewToken is null ||
                !string.Equals(transaction.PreviewToken, finish.PreviewToken, StringComparison.Ordinal))
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.InvalidInput,
                    "UAW1008",
                    "Native publication finish must identify the preview token from its request header.");
            }
            if (nativePublicationResults.TryGetValue(transaction.PreviewToken, out NativePublicationFinishResponse? adopted))
            {
                if (!string.Equals(adopted.TransactionId, transaction.TransactionId, StringComparison.Ordinal))
                    throw new AuthoringProtocolException(AuthoringFailureKind.Conflict, "UAW1021", "The preview token belongs to another native publication transaction.");
                return adopted;
            }
            if (!nativeReservations.TryGetValue(transaction.PreviewToken, out NativePublicationReservation? reservation))
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1021",
                    "The native publication reservation is missing or has already been cancelled.");
            }
            if (!string.Equals(reservation.Prepare.TransactionId, transaction.TransactionId, StringComparison.Ordinal))
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1021",
                    "The preview token belongs to another native publication transaction.");
            }
            ValidateTransaction(transaction, allowNativeReservation: true);
            if (!finish.Succeeded)
            {
                EnsureNativePublicationCancellationBaseline(reservation.Prepare.Outputs);
                nativeReservations.Remove(transaction.PreviewToken);
                preparedPreviews.Remove(transaction.PreviewToken);
                reservation.CandidateReload.Dispose();
                NativePublicationFinishResponse cancelled = new(
                    "unifiedscript.native-publication-finish/v0.1",
                    "cancelled",
                    transaction.TransactionId,
                    reservation.Prepare,
                    null,
                    finish.Error);
                nativePublicationResults[transaction.PreviewToken] = cancelled;
                return cancelled;
            }

            EnsureNativePublicationCandidateOutputs(reservation.Prepare.Outputs);
            AuthoringWorkspaceResponse workspace = AdoptNativePublication(
                reservation,
                transaction);
            NativePublicationFinishResponse result = new(
                "unifiedscript.native-publication-finish/v0.1",
                "adopted",
                transaction.TransactionId,
                reservation.Prepare,
                workspace,
                null);
            nativeReservations.Remove(transaction.PreviewToken);
            nativePublicationResults[transaction.PreviewToken] = result;
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NativePublicationFinishResponse> CancelNativePublicationAsync(
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureAuthoringAvailable();
            if (transaction.PreviewToken is null)
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.InvalidInput,
                    "UAW1008",
                    "Native publication cancel must identify the preview token from its request header.");
            }
            if (nativePublicationResults.TryGetValue(transaction.PreviewToken, out NativePublicationFinishResponse? completed))
            {
                if (!string.Equals(completed.TransactionId, transaction.TransactionId, StringComparison.Ordinal))
                {
                    throw new AuthoringProtocolException(
                        AuthoringFailureKind.Conflict,
                        "UAW1021",
                        "The preview token belongs to another native publication transaction.");
                }
                return completed;
            }
            if (!nativeReservations.TryGetValue(transaction.PreviewToken, out NativePublicationReservation? reservation))
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1021",
                    "The native publication reservation is missing or has already been cancelled.");
            }
            if (!string.Equals(reservation.Prepare.TransactionId, transaction.TransactionId, StringComparison.Ordinal))
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1021",
                    "The preview token belongs to another native publication transaction.");
            }
            ValidateTransaction(transaction, allowNativeReservation: true);
            EnsureNativePublicationCancellationBaseline(reservation.Prepare.Outputs);
            nativeReservations.Remove(transaction.PreviewToken);
            preparedPreviews.Remove(transaction.PreviewToken);
            reservation.CandidateReload.Dispose();
            NativePublicationFinishResponse cancelled = new(
                "unifiedscript.native-publication-finish/v0.1",
                "cancelled",
                transaction.TransactionId,
                reservation.Prepare,
                null,
                "Cancelled by the native participant before authoring adoption.");
            nativePublicationResults[transaction.PreviewToken] = cancelled;
            return cancelled;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AuthoringWorkspaceResponse> RefreshAsync(
        AuthoringTransactionContext transaction, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ValidateTransaction(transaction);
            if (execution is not null)
                throw new AuthoringProtocolException(AuthoringFailureKind.Conflict, "UAW1001",
                    "Finish the active debug execution before refreshing project inputs.");
            ProjectAuthoringInput input;
            CSharpAuthoringMutation mutation;
            try
            {
                input = LoadRefreshInput();
                mutation = authoring.PrepareRefresh(input.Request);
            }
            catch (Exception exception) when (exception is CSharpProjectLoadException or IOException or ArgumentException or JsonException)
            {
                throw new AuthoringProtocolException(AuthoringFailureKind.Conflict, "UAW1018",
                    $"Project refresh failed: {exception.Message}. Existing authoring and disk edits were preserved.", innerException: exception);
            }
            if (!mutation.Succeeded || mutation.Snapshot is null)
                throw new AuthoringProtocolException(AuthoringFailureKind.Conflict, "UAW1018",
                    "External sources did not compile; the old workspace and runtime remain active. Fix the disk sources and refresh again.", mutation.Diagnostics);

            CSharpAuthoringSnapshot candidate = mutation.Snapshot;
            EnsureNativeIdentityUsesNativePublication(authoring.Current, candidate);
            string primaryLogical = input.SourceFiles.Single(pair =>
                string.Equals(pair.Value, sourcePath, StringComparison.OrdinalIgnoreCase)).Key;
            bool samePaths = SamePaths(sourceFiles, input.SourceFiles) &&
                authoring.Current.Request.ReferencePaths.SequenceEqual(input.Request.ReferencePaths, StringComparer.OrdinalIgnoreCase) &&
                authoring.Current.Request.RuntimeReferencePaths.SequenceEqual(input.Request.RuntimeReferencePaths, StringComparer.OrdinalIgnoreCase);
            bool sameIdentityPaths = SamePhysicalPaths(identityFiles, input.IdentityFiles);
            bool sameIdentity = InputIdentity(
                    authoring.Current.Request,
                    sourceFiles,
                    identityFiles,
                    readOnlySources) ==
                InputIdentity(
                    input.Request,
                    input.SourceFiles,
                    input.IdentityFiles,
                    input.ReadOnlySources);
            if (samePaths && sameIdentityPaths && sameIdentity &&
                candidate.Request.ConfigurationId == authoring.Current.Request.ConfigurationId &&
                candidate.Analysis.ScriptAssets!.SourceCompilationId == authoring.Current.Analysis.ScriptAssets!.SourceCompilationId)
            {
                // A normal build may rewrite an equivalent sidecar. Adopt its bytes,
                // but do not publish or invent another runtime generation for a no-op.
                persistedIdentity = ReadIdentityText();
                CapturePersistedIdentityFiles();
                return Workspace;
            }

            bool rebase = !samePaths || !sameIdentityPaths ||
                candidate.Request.ConfigurationId != authoring.Current.Request.ConfigurationId ||
                candidate.Request.Sources.Any(source => source.LogicalPath != primaryLogical &&
                    (!persistedSources.TryGetValue(source.LogicalPath, out string? before) || before != source.Source));
            CSharpAuthoringWorkspace? rebased = rebase
                ? authoring.Rebase(mutation, transaction.TransactionId, "Refresh project from disk") : null;
            CSharpAuthoringHistoryDocument history = rebased?.ExportHistory() ??
                authoring.PreviewCommit(mutation, transaction.TransactionId, "Refresh source from disk");
            AuthoringOutput? archive = null;
            if (rebase && artifactOutputs.HistoryPath is not null &&
                (authoring.History.CanUndo || authoring.History.CanRedo))
            {
                byte[] previousHistory = Encoding.UTF8.GetBytes(CSharpAuthoringHistoryJson.Serialize(authoring.ExportHistory()) + "\n");
                string digest = Convert.ToHexString(SHA256.HashData(previousHistory)).ToLowerInvariant();
                archive = new AuthoringOutput(Path.Combine(artifactOutputs.HistoryPath + ".retired", digest + ".json"), previousHistory);
            }
            var imported = new ImportedProjectInput(input, primaryLogical,
                input.Request.Sources.ToDictionary(source => source.LogicalPath, source => source.Source, StringComparer.Ordinal),
                input.IdentityFiles, archive);
            PreparedGraphLayout? layout = PrepareGraphLayout(authoring.Current, candidate);
            long previousGeneration = reload.Generation.Number;
            long started = Stopwatch.GetTimestamp();
            CoreClrDebugReloadResult refreshed;
            try
            {
                refreshed = transport.Reload(candidate.Request, () =>
                {
                    ValidateGraphLayoutBaseline(layout);
                    Persist(candidate, history, layout, imported);
                });
            }
            catch (Exception exception)
            {
                throw new AuthoringProtocolException(AuthoringFailureKind.Conflict, "UAW1018",
                    $"Project refresh was not published: {exception.Message}. The old loaded generation and external source edits were preserved.",
                    exception is ExecutionCompilationException compilation ? compilation.Diagnostics : [], innerException: exception);
            }
            if (rebased is not null) authoring = rebased;
            else authoring.Commit(mutation, transaction.TransactionId, "Refresh source from disk");
            preparedPreviews.Clear();
            reload = refreshed;
            bindings = reload.Breakpoints.Bindings;
            Health = CreateHealth(reload.Generation);
            historyPersistenceState = artifactOutputs.HistoryPath is null ? "disabled" : "persisted";
            historyPersistenceMessage = rebase
                ? archive is null ? "Project context changed; Undo starts at the refreshed inputs."
                    : $"Project context changed; previous Undo history is retained at '{archive.Path}'."
                : null;
            var receipt = new ReloadTransactionResponse("unifiedscript.reload-transaction/v0.1", transaction.TransactionId,
                "authoring", "published", previousGeneration, refreshed.Generation.Number, refreshed.Generation.Number,
                [CompleteReloadPhase("projectRefresh", started, "succeeded", "Read current inputs, compiled, published matching artifacts and adopted the new project snapshot.")], null);
            Workspace = CreateWorkspace(authoring.Current, reload.Generation, receipt);
            return Workspace;
        }
        finally { gate.Release(); }
    }

    private ProjectAuthoringInput LoadRefreshInput()
    {
        if (refreshProject is not null) return refreshProject();
        CSharpCompilationRequest previous = authoring.Current.Request;
        CSharpSourceDocument[] sources = previous.Sources.Select(source => new CSharpSourceDocument(
            source.LogicalPath, File.ReadAllText(sourceFiles[source.LogicalPath]),
            identityFiles.TryGetValue(source.LogicalPath, out string? path) && File.Exists(path)
                ? IdentitySidecarJson.Deserialize(File.ReadAllText(path)) : source.IdentitySidecar,
            source.IdentityPins)).ToArray();
        return new ProjectAuthoringInput(new CSharpCompilationRequest(previous.AssemblyName, sources,
            previous.ReferencePaths, previous.EffectCatalog, previous.Settings, previous.RuntimeReferencePaths,
            previous.IncludeDefaultReferences), sourcePath, identityPath ?? sourcePath + ".usids", sourceFiles,
            readOnlySources, identityFiles);
    }

    private string? ReadIdentityText() => identityPath is not null && File.Exists(identityPath) ? File.ReadAllText(identityPath) : null;

    private void CapturePersistedIdentityFiles()
    {
        foreach ((string logicalPath, string path) in identityFiles)
            persistedIdentities[logicalPath] = File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static bool SamePaths(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out string? path) &&
            string.Equals(pair.Value, path, StringComparison.OrdinalIgnoreCase));

    private static bool SamePhysicalPaths(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Values.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(right.Values.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

    private static string InputIdentity(ProjectAuthoringInput input) => InputIdentity(
        input.Request, input.SourceFiles, input.IdentityFiles);

    private static string InputIdentity(
        CSharpCompilationRequest request,
        IReadOnlyDictionary<string, string> sourceFiles,
        IReadOnlyDictionary<string, string> identityFiles,
        IReadOnlySet<string>? excludedSources = null) => string.Join("|",
        request.ConfigurationId.Value,
        string.Join("|", sourceFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + pair.Value)),
        string.Join("|", identityFiles.Values.Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase)),
        string.Join("|", request.Sources.Where(source => excludedSources is null ||
                !excludedSources.Contains(source.LogicalPath)).Select(source => source.LogicalPath + ":" +
            CSharpSourceTextPatchJson.ComputeContentDigest(source.Source) + ":" +
            (source.IdentitySidecar is null ? "" : IdentitySidecarJson.Serialize(source.IdentitySidecar)))));

    private sealed record ImportedProjectInput(ProjectAuthoringInput Input, string PrimaryLogical,
        IReadOnlyDictionary<string, string> DiskSources,
        IReadOnlyDictionary<string, string> IdentityFiles,
        AuthoringOutput? ArchivedHistory);

    public AuthoringRollbackResponse CreateRollbackResponse(string state)
    {
        string message = state switch
        {
            "unchanged" =>
                "The rejected transaction performed no source, artifact, history, or runtime-generation write.",
            "restored" =>
                "The failed commit restored the previous source, artifacts, history, and runtime program as the reported generation.",
            "failed" =>
                "The transaction failed and the previous runtime program could not be fully restored.",
            _ => "The transaction rollback state is unknown.",
        };
        return new AuthoringRollbackResponse(
            state,
            Workspace.Revision,
            reload.Generation.Number,
            message);
    }

    public async Task<AuthoringWorkspaceResponse> BeginGroupAsync(
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureAuthoringAvailable();
            if (activeGroup is not null)
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1009",
                    $"Authoring group '{activeGroup.Id}' is already active; commit or cancel it first.");
            }
            if (transaction.GroupId is not null)
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.InvalidInput,
                    "UAW1008",
                    "Beginning an authoring group must not send an existing group ID.");
            }
            ValidateTransaction(transaction);
            EnsureFilesUnchanged();
            activeGroup = new ActiveAuthoringGroup(
                transaction.TransactionId,
                transaction.Description,
                authoring.Current.Revision,
                authoring.CreateDraft(),
                0);
            preparedPreviews.Clear();
            Workspace = CreateWorkspace(activeGroup.Draft.Current, reload.Generation);
            return Workspace;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AuthoringWorkspaceResponse> CommitGroupAsync(
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureAuthoringAvailable();
            EnsureNoNativePublicationReservation();
            ActiveAuthoringGroup group = RequireActiveGroup(transaction);
            EnsureFilesUnchanged();
            if (group.StagedOperationCount == 0)
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1011",
                    "A grouped authoring transaction must stage at least one accepted edit before commit.");
            }

            CSharpAuthoringMutation mutation = PrepareMutation(
                authoring,
                _ => authoring.PrepareDraftCommit(group.Draft),
                transaction,
                publishesRuntime: true,
                out PreparedGraphLayout? graphLayout);
            CSharpAuthoringHistoryDocument persistedHistory = authoring.PreviewCommit(
                mutation,
                group.Id,
                group.Description);
            AuthoringWorkspaceResponse committed = CommitPreparedSnapshot(
                mutation.Snapshot!,
                () => authoring.Commit(mutation, group.Id, group.Description),
                persistedHistory,
                group.Id,
                graphLayout);
            activeGroup = null;
            preparedPreviews.Clear();
            Workspace = CreateWorkspace(
                authoring.Current,
                reload.Generation,
                committed.ReloadTransaction);
            return Workspace;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AuthoringWorkspaceResponse> CancelGroupAsync(
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNoNativePublicationReservation();
            _ = RequireActiveGroup(transaction);
            activeGroup = null;
            preparedPreviews.Clear();
            Workspace = CreateWorkspace(authoring.Current, reload.Generation);
            return Workspace;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DebugSessionResponse> StartAsync(
        DebugStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (activeGroup is not null)
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1009",
                    "Commit or cancel the active authoring group before starting debug execution.");
            }
            if (execution is not null)
            {
                throw new BreakpointProtocolException(
                    "UDB1004",
                    "The current debug execution is running or paused; finish it before starting again.");
            }
            if (request.ExpectedSourceCompilationId is { } expectedCompilation &&
                expectedCompilation != reload.Generation.SourceCompilationId)
            {
                throw new BreakpointProtocolException(
                    "UDB1002",
                    $"The loaded source compilation is '{reload.Generation.SourceCompilationId}', not '{expectedCompilation}'.");
            }
            if (request.ExpectedSemanticConfigurationId is { } expectedConfiguration &&
                expectedConfiguration != reload.Generation.SemanticConfigurationId)
            {
                throw new BreakpointProtocolException(
                    "UDB1002",
                    $"The loaded semantic configuration is '{reload.Generation.SemanticConfigurationId}', not '{expectedConfiguration}'.");
            }

            foreach (SemanticId breakpointId in breakpointIds)
            {
                _ = transport.RemoveBreakpoint(breakpointId);
            }

            breakpointIds.Clear();
            bindings = (request.BreakpointNodeIds ?? [])
                .Distinct()
                .Select(nodeId =>
                {
                    SemanticId breakpointId = SemanticId.FromKey(
                        $"visual-workbench-breakpoint/v0.1|{nodeId}");
                    breakpointIds.Add(breakpointId);
                    return transport.SetBreakpoint(
                            new SemanticBreakpoint(breakpointId, nodeId))
                        .Bindings
                        .Single(binding => binding.BreakpointId == breakpointId);
                })
                .ToArray();
            ExecutionArgument[] arguments = (request.Arguments ?? [])
                .Select(static argument => new ExecutionArgument(argument.GetRawText()))
                .ToArray();
            ScriptAssetReceiver? receiver = request.Receiver is null
                ? null
                : new ScriptAssetReceiver(
                    request.Receiver.AssetId,
                    (request.Receiver.StateOverrides ?? []).Select(static state =>
                        new ScriptStateOverride(state.StateId, state.Value.GetRawText())));
            execution = transport.Start(new ExecutionInvocation(
                request.MethodId,
                arguments,
                receiver));
            pause = null;
            return DebugSessionResponse.Running(
                reload.Generation,
                execution.ExecutionId,
                bindings);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DebugSessionResponse> WaitAsync(
        DebugExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CoreClrDebugExecution currentExecution;
        IReadOnlyList<SemanticBreakpointBinding> currentBindings;
        await gate.WaitAsync(cancellationToken);
        try
        {
            currentExecution = RequireExecution(request.GenerationId, request.ExecutionId);
            if (waitInProgress)
            {
                throw new BreakpointProtocolException(
                    "UDB1011",
                    "A wait request is already active for the current debug execution.");
            }

            waitInProgress = true;
            currentBindings = bindings.ToArray();
        }
        finally
        {
            gate.Release();
        }

        DebugSessionResponse? response = null;
        try
        {
            response = await WaitForNextStateAsync(
                currentExecution,
                currentBindings,
                cancellationToken);
            return response;
        }
        finally
        {
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                waitInProgress = false;
                if (response is not null && execution?.ExecutionId == currentExecution.ExecutionId)
                {
                    if (response.State == "paused")
                    {
                        pause = response.Pause;
                    }
                    else if (response.State == "completed")
                    {
                        execution = null;
                        pause = null;
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    public async Task<DebugSessionResponse> PauseAsync(
        DebugExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await gate.WaitAsync(cancellationToken);
        try
        {
            CoreClrDebugExecution currentExecution = RequireExecution(
                request.GenerationId,
                request.ExecutionId);
            if (pause is not null)
            {
                throw new BreakpointProtocolException(
                    "UDB1006",
                    "The debug execution is already paused.");
            }

            transport.Pause(reload.Generation.GenerationId, currentExecution.ExecutionId);
            return DebugSessionResponse.Running(
                reload.Generation,
                currentExecution.ExecutionId,
                bindings);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DebugSessionResponse> ResumeAsync(
        DebugResumeRequest request,
        CoreClrDebugStepKind? stepKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await gate.WaitAsync(cancellationToken);
        try
        {
            CoreClrDebugExecution currentExecution = RequireExecution(
                request.GenerationId,
                request.ExecutionId);
            CoreClrDebugPause currentPause = RequirePause(request.PauseId);
            switch (stepKind)
            {
                case CoreClrDebugStepKind.Into:
                    transport.StepInto(reload.Generation.GenerationId, currentPause.PauseId);
                    break;
                case CoreClrDebugStepKind.Over:
                    transport.StepOver(reload.Generation.GenerationId, currentPause.PauseId);
                    break;
                case CoreClrDebugStepKind.Out:
                    transport.StepOut(reload.Generation.GenerationId, currentPause.PauseId);
                    break;
                case null:
                    transport.Continue(reload.Generation.GenerationId, currentPause.PauseId);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(stepKind));
            }

            pause = null;
            return DebugSessionResponse.Running(
                reload.Generation,
                currentExecution.ExecutionId,
                bindings);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CoreClrDebugEvaluation> EvaluateAsync(
        DebugEvaluateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await gate.WaitAsync(cancellationToken);
        try
        {
            _ = RequireExecution(request.GenerationId, request.ExecutionId);
            CoreClrDebugPause currentPause = RequirePause(request.PauseId);
            return transport.Evaluate(
                reload.Generation.GenerationId,
                currentPause.PauseId,
                request.FrameId,
                request.Expression);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DebugLiveWorldPublishResponse> PublishLiveWorldAsync(
        DebugLiveWorldPublishRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (activeGroup is not null)
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1009",
                    "Commit or cancel the active authoring group before publishing a live World debug image.");
            }
            if (execution is not null)
            {
                throw new BreakpointProtocolException(
                    "UDB1004",
                    "Finish the loopback execution before publishing a live World debug image.");
            }
            if (request.ExpectedSourceCompilationId != reload.Generation.SourceCompilationId ||
                request.ExpectedSemanticConfigurationId !=
                reload.Generation.SemanticConfigurationId)
            {
                throw new BreakpointProtocolException(
                    "UDB1002",
                    "The live World publish request belongs to stale source or semantic configuration.");
            }
            if (artifactOutputs.ManagedAssemblyPath is null ||
                artifactOutputs.PortablePdbPath is null)
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1004",
                    "Live World debugging requires both --managed-out and --pdb-out.");
            }

            EnsureFilesUnchanged();
            string transactionId = $"live-world-{Guid.NewGuid():N}";
            var phases = new List<ReloadTransactionPhaseResponse>();
            long phaseStart = Stopwatch.GetTimestamp();
            CSharpProgramEmitResult emitted;
            try
            {
                emitted = emitter.Emit(
                    authoring.Current.Request,
                    new CSharpDebugInstrumentation(
                        typeof(CoreClrDebugProbe).Assembly.Location));
            }
            catch (Exception exception)
            {
                phases.Add(CompleteReloadPhase(
                    "compile",
                    phaseStart,
                    "failed",
                    exception.Message));
                phases.Add(new ReloadTransactionPhaseResponse(
                    "rollback",
                    "unchanged",
                    0,
                    "The published live World PE/PDB remained unchanged."));
                var failedTransaction = new ReloadTransactionResponse(
                    "unifiedscript.reload-transaction/v0.1",
                    transactionId,
                    "liveWorldDebug",
                    "rolledBack",
                    reload.Generation.Number,
                    reload.Generation.Number,
                    reload.Generation.Number,
                    phases,
                    exception.Message);
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1004",
                    "The instrumented live World assembly could not be emitted; the active artifact was not changed.",
                    reloadTransaction: failedTransaction,
                    innerException: exception);
            }
            if (!emitted.Succeeded || emitted.Image is null)
            {
                const string message =
                    "The instrumented live World assembly could not be emitted; the active artifact was not changed.";
                phases.Add(CompleteReloadPhase(
                    "compile",
                    phaseStart,
                    "failed",
                    $"The compiler returned {emitted.Diagnostics.Count} diagnostic(s)."));
                phases.Add(new ReloadTransactionPhaseResponse(
                    "rollback",
                    "unchanged",
                    0,
                    "The published live World PE/PDB remained unchanged."));
                var failedTransaction = new ReloadTransactionResponse(
                    "unifiedscript.reload-transaction/v0.1",
                    transactionId,
                    "liveWorldDebug",
                    "rolledBack",
                    reload.Generation.Number,
                    reload.Generation.Number,
                    reload.Generation.Number,
                    phases,
                    message);
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1004",
                    message,
                    emitted.Diagnostics,
                    reloadTransaction: failedTransaction);
            }
            phases.Add(CompleteReloadPhase(
                "compile",
                phaseStart,
                "succeeded",
                "Emitted the instrumented live World PE and portable PDB in memory."));
            phaseStart = Stopwatch.GetTimestamp();
            try
            {
                WriteTransactionally(
                [
                    new AuthoringOutput(
                        artifactOutputs.ManagedAssemblyPath,
                        emitted.Image.PortableExecutable),
                    new AuthoringOutput(
                        artifactOutputs.PortablePdbPath,
                        emitted.Image.PortablePdb),
                ]);
            }
            catch (Exception exception)
            {
                phases.Add(CompleteReloadPhase(
                    "artifactPublication",
                    phaseStart,
                    "failed",
                    exception.Message));
                phases.Add(new ReloadTransactionPhaseResponse(
                    "rollback",
                    "restored",
                    0,
                    "The transactional writer restored the previous live World PE/PDB."));
                var failedTransaction = new ReloadTransactionResponse(
                    "unifiedscript.reload-transaction/v0.1",
                    transactionId,
                    "liveWorldDebug",
                    "rolledBack",
                    reload.Generation.Number,
                    reload.Generation.Number,
                    reload.Generation.Number,
                    phases,
                    exception.Message);
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1013",
                    "The live World debug artifact publication failed; the previous PE/PDB was restored.",
                    rollbackState: "restored",
                    reloadTransaction: failedTransaction,
                    innerException: exception);
            }
            phases.Add(CompleteReloadPhase(
                "artifactPublication",
                phaseStart,
                "succeeded",
                "Atomically replaced the live World PE and portable PDB."));
            var transaction = new ReloadTransactionResponse(
                "unifiedscript.reload-transaction/v0.1",
                transactionId,
                "liveWorldDebug",
                "published",
                reload.Generation.Number,
                reload.Generation.Number,
                reload.Generation.Number,
                phases,
                null);
            return new DebugLiveWorldPublishResponse(
                "unifiedscript.live-world-debug-publish/v0.1",
                reload.Generation,
                artifactOutputs.ManagedAssemblyPath,
                artifactOutputs.PortablePdbPath,
                transaction);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<AuthoringWorkspaceResponse> ApplyAsync(
        DeclarationPatchDocument patch,
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return ApplyAsync(workspace => workspace.Prepare(patch), transaction, cancellationToken);
    }

    public Task<AuthoringWorkspaceResponse> ApplyAsync(
        SemanticPatchDocument patch,
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return ApplyAsync(workspace => workspace.Prepare(patch), transaction, cancellationToken);
    }

    public Task<AuthoringWorkspaceResponse> ApplyAsync(
        SemanticPatchPlanDocument plan,
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ApplyAsync(workspace => workspace.Prepare(plan), transaction, cancellationToken);
    }

    public Task<AuthoringWorkspaceResponse> ApplyAsync(
        CSharpSourceTextPatchDocument patch,
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return ApplyAsync(workspace => workspace.Prepare(patch), transaction, cancellationToken);
    }

    public Task<AuthoringPreviewResponse> PreviewAsync(
        DeclarationPatchDocument patch,
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return PreviewAsync(workspace => workspace.Prepare(patch), transaction, cancellationToken);
    }

    public Task<AuthoringPreviewResponse> PreviewAsync(
        SemanticPatchDocument patch,
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return PreviewAsync(workspace => workspace.Prepare(patch), transaction, cancellationToken);
    }

    public Task<AuthoringPreviewResponse> PreviewAsync(
        SemanticPatchPlanDocument plan,
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return PreviewAsync(workspace => workspace.Prepare(plan), transaction, cancellationToken);
    }

    public Task<AuthoringPreviewResponse> PreviewAsync(
        CSharpSourceTextPatchDocument patch,
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return PreviewAsync(workspace => workspace.Prepare(patch), transaction, cancellationToken);
    }

    public async Task<AuthoringPreviewResponse> PreviewGroupCommitAsync(
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureAuthoringAvailable();
            EnsureNoNativePublicationReservation();
            ActiveAuthoringGroup group = RequireActiveGroup(transaction);
            EnsureFilesUnchanged();
            if (group.StagedOperationCount == 0)
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1011",
                    "A grouped authoring transaction must stage at least one accepted edit before commit.");
            }

            CSharpAuthoringMutation mutation = authoring.PrepareDraftCommit(group.Draft);
            return CreatePreview(
                authoring.Current,
                mutation.Snapshot!,
                mutation.Diagnostics,
                transaction,
                publishesRuntime: true);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AuthoringPreviewResponse> PreviewHistoryAsync(
        CSharpAuthoringHistoryDirection direction,
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureAuthoringAvailable();
            ValidateTransaction(transaction);
            EnsureFilesUnchanged();
            CSharpAuthoringHistoryState history = authoring.History;
            if ((direction == CSharpAuthoringHistoryDirection.Undo && !history.CanUndo) ||
                (direction == CSharpAuthoringHistoryDirection.Redo && !history.CanRedo))
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1007",
                    direction == CSharpAuthoringHistoryDirection.Undo
                        ? "There is no committed authoring transaction to undo."
                        : "There is no undone authoring transaction to redo.");
            }

            CSharpAuthoringHistoryMutation mutation =
                direction == CSharpAuthoringHistoryDirection.Undo
                    ? authoring.PrepareUndo()
                    : authoring.PrepareRedo();
            return CreatePreview(
                authoring.Current,
                mutation.Snapshot,
                [],
                transaction,
                publishesRuntime: true,
                historyMutation: mutation);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AuthoringWorkspaceResponse> NavigateHistoryAsync(
        CSharpAuthoringHistoryDirection direction,
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureAuthoringAvailable();
            ValidateTransaction(transaction);
            EnsureFilesUnchanged();
            CSharpAuthoringHistoryState history = authoring.History;
            if ((direction == CSharpAuthoringHistoryDirection.Undo && !history.CanUndo) ||
                (direction == CSharpAuthoringHistoryDirection.Redo && !history.CanRedo))
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1007",
                    direction == CSharpAuthoringHistoryDirection.Undo
                        ? "There is no committed authoring transaction to undo."
                        : "There is no undone authoring transaction to redo.");
            }

            CSharpAuthoringHistoryMutation mutation =
                direction == CSharpAuthoringHistoryDirection.Undo
                    ? authoring.PrepareUndo()
                    : authoring.PrepareRedo();
            mutation = mutation with
            {
                Snapshot = ResolvePreparedSnapshot(
                    authoring.Current,
                    mutation.Snapshot,
                    transaction,
                    publishesRuntime: true,
                    out PreparedGraphLayout? graphLayout),
            };
            CSharpAuthoringHistoryDocument persistedHistory = authoring.PreviewCommit(
                mutation,
                transaction.TransactionId);
            return CommitPreparedSnapshot(
                mutation.Snapshot,
                () => authoring.Commit(mutation, transaction.TransactionId),
                persistedHistory,
                transaction.TransactionId,
                graphLayout);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        // Request handlers are intentionally detached by the listener. A long-poll
        // handler can still be unwinding through its gate-protected cleanup after
        // the listener has closed, so disposing the semaphore here would race that
        // cleanup. SemaphoreSlim owns no external handle until WaitHandle is used.
        foreach (NativePublicationReservation reservation in nativeReservations.Values)
            reservation.CandidateReload.Dispose();
        transport.Dispose();
    }

    private async Task<AuthoringPreviewResponse> PreviewAsync(
        Func<CSharpAuthoringWorkspace, CSharpAuthoringMutation> prepare,
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureAuthoringAvailable();
            EnsureNoNativePublicationReservation();
            if (transaction.GroupId is not null)
            {
                ActiveAuthoringGroup group = RequireActiveGroup(
                    transaction,
                    requireGroupOperationId: false);
                EnsureFilesUnchanged();
                if (string.Equals(transaction.TransactionId, group.Id, StringComparison.Ordinal))
                {
                    throw new AuthoringProtocolException(
                        AuthoringFailureKind.InvalidInput,
                        "UAW1008",
                        "A staged operation ID must differ from its grouped transaction ID.");
                }
                if (group.Draft.ContainsOperationId(transaction.TransactionId))
                {
                    throw new AuthoringProtocolException(
                        AuthoringFailureKind.Conflict,
                        "UAW1006",
                        $"Staged authoring operation ID '{transaction.TransactionId}' was already accepted.");
                }

                CSharpAuthoringMutation stagedMutation = prepare(group.Draft);
                if (!stagedMutation.Succeeded || stagedMutation.Snapshot is null)
                {
                    throw new AuthoringProtocolException(
                        AuthoringFailureKind.Conflict,
                        "UAW1003",
                        "The staged semantic patch was rejected; the grouped draft was not changed.",
                        stagedMutation.Diagnostics);
                }
                return CreatePreview(
                    group.Draft.Current,
                    stagedMutation.Snapshot,
                    stagedMutation.Diagnostics,
                    transaction,
                    publishesRuntime: false);
            }

            ValidateTransaction(transaction);
            EnsureFilesUnchanged();
            CSharpAuthoringMutation mutation = prepare(authoring);
            if (!mutation.Succeeded || mutation.Snapshot is null)
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1003",
                    "The semantic patch was rejected; no source or runtime generation was changed.",
                    mutation.Diagnostics);
            }
            return CreatePreview(
                authoring.Current,
                mutation.Snapshot,
                mutation.Diagnostics,
                transaction,
                publishesRuntime: true);
        }
        finally
        {
            gate.Release();
        }
    }

    private AuthoringPreviewResponse CreatePreview(
        CSharpAuthoringSnapshot baseline,
        CSharpAuthoringSnapshot candidate,
        IReadOnlyList<UnifiedDiagnostic> diagnostics,
        AuthoringTransactionContext transaction,
        bool publishesRuntime,
        CSharpAuthoringHistoryMutation? historyMutation = null)
    {
        ValidateEditableSources(candidate);
        Dictionary<string, string> beforeSources = baseline.Request.Sources.ToDictionary(
            static source => source.LogicalPath,
            static source => source.Source,
            StringComparer.Ordinal);
        Dictionary<string, string> afterSources = candidate.Request.Sources.ToDictionary(
            static source => source.LogicalPath,
            static source => source.Source,
            StringComparer.Ordinal);
        string[] paths = beforeSources.Keys
            .Concat(afterSources.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        List<AuthoringPreviewFile> files = paths
            .Select(path => new AuthoringPreviewFile(
                path,
                beforeSources.GetValueOrDefault(path, string.Empty),
                afterSources.GetValueOrDefault(path, string.Empty)))
            .Where(static file => !string.Equals(
                file.BeforeText,
                file.AfterText,
                StringComparison.Ordinal))
            .ToList();
        PreparedGraphLayout? graphLayout = publishesRuntime
            ? PrepareGraphLayout(baseline, candidate)
            : null;
        CSharpGraphRoundTripReport roundTrip = graphLayout?.Report ??
            roundTripInspector.Inspect(baseline, candidate).Report;
        if (graphLayout is not null && !string.Equals(
                graphLayout.BaselineText,
                graphLayout.CandidateText,
                StringComparison.Ordinal))
        {
            files.Add(new AuthoringPreviewFile(
                graphLayout.Path,
                graphLayout.BaselineText,
                graphLayout.CandidateText));
        }
        string previewToken = ComputePreviewToken(
            baseline,
            candidate,
            transaction,
            publishesRuntime,
            graphLayout);
        if (preparedPreviews.Count >= 32)
        {
            preparedPreviews.Clear();
        }
        preparedPreviews[previewToken] = new PreparedAuthoringPreview(
            baseline.Revision,
            transaction.BaselineRevision,
            transaction.TransactionId,
            transaction.Description,
            transaction.GroupId,
            transaction.BoundPayloadId,
            publishesRuntime,
            candidate,
            diagnostics,
            graphLayout,
            historyMutation);
        return new AuthoringPreviewResponse(
            "unifiedscript.authoring-preview/v0.3",
            baseline.Revision,
            candidate.Revision,
            transaction.TransactionId,
            transaction.Description,
            publishesRuntime,
            previewToken,
            new SemanticGraphDiffer().Compare(baseline.Graph, candidate.Graph),
            roundTrip,
            files,
            diagnostics,
            candidate.Analysis.ScriptAssets,
            BuildNativeBindingChanges(baseline.Analysis.ScriptAssets!, candidate.Analysis.ScriptAssets!));
    }

    private static IReadOnlyList<NativePublicationBindingChange> BuildNativeBindingChanges(
        ScriptAssetDocument beforeAssets,
        ScriptAssetDocument afterAssets)
    {
        Dictionary<string, ScriptAssetDeclaration> beforeByPath = beforeAssets.Assets.ToDictionary(
            static asset => asset.AssetPath,
            StringComparer.Ordinal);
        Dictionary<string, ScriptAssetDeclaration> afterByPath = afterAssets.Assets.ToDictionary(
            static asset => asset.AssetPath,
            StringComparer.Ordinal);
        var bindings = new List<NativePublicationBindingChange>();
        foreach (string path in beforeByPath.Keys.Concat(afterByPath.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            beforeByPath.TryGetValue(path, out ScriptAssetDeclaration? before);
            afterByPath.TryGetValue(path, out ScriptAssetDeclaration? after);
            // A native ClassBinding is keyed by more than its carrier paths.  A
            // declaration rename or a new semantic asset id changes the native
            // owner identity even when the selected carrier and parent stay the
            // same, so it must go through the same coordinated publication.
            bool hasNativeIdentity = before?.ClassBinding is not null || after?.ClassBinding is not null;
            if (!hasNativeIdentity ||
                (before?.ClassBinding == after?.ClassBinding &&
                    before?.Id == after?.Id &&
                    string.Equals(before?.QualifiedName, after?.QualifiedName, StringComparison.Ordinal)))
                continue;
            bindings.Add(new NativePublicationBindingChange(
                path,
                after?.Id.Value ?? before?.Id.Value ?? string.Empty,
                before?.ClassBinding,
                after?.ClassBinding));
        }
        return bindings;
    }

    private static void EnsureNativePublicationIdentityStable(
        ScriptAssetDocument beforeAssets,
        ScriptAssetDocument afterAssets)
    {
        foreach (NativePublicationBindingChange change in
            BuildNativeBindingChanges(beforeAssets, afterAssets))
        {
            ScriptAssetDeclaration? before = beforeAssets.Assets.SingleOrDefault(
                asset => string.Equals(asset.AssetPath, change.AssetPath, StringComparison.Ordinal));
            ScriptAssetDeclaration? after = afterAssets.Assets.SingleOrDefault(
                asset => string.Equals(asset.AssetPath, change.AssetPath, StringComparison.Ordinal));
            if (before is null || after is null || before.ClassBinding is null || after.ClassBinding is null)
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1031",
                    $"Native Script Asset '{change.AssetPath}' was added or removed. Use the supported Rename/identity operation before native publication.");
            }
            if (before.Id != after.Id ||
                !string.Equals(before.QualifiedName, after.QualifiedName, StringComparison.Ordinal) ||
                !string.Equals(before.ClassBinding.Kind, after.ClassBinding.Kind, StringComparison.Ordinal) ||
                !string.Equals(before.ClassBinding.GeneratedClassPath, after.ClassBinding.GeneratedClassPath, StringComparison.Ordinal))
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1031",
                    $"Native Script Asset identity '{change.AssetPath}' changed class ID, qualified name, carrier kind, or generated class path. Use the supported Rename/identity operation; native parent publication cannot change identity.");
            }
        }
    }

    private static string ComputePreviewToken(
        CSharpAuthoringSnapshot baseline,
        CSharpAuthoringSnapshot candidate,
        AuthoringTransactionContext transaction,
        bool publishesRuntime,
        PreparedGraphLayout? graphLayout)
    {
        var tokenSource = new StringBuilder();
        AppendTokenPart(tokenSource, baseline.Revision.ToString());
        AppendTokenPart(tokenSource, candidate.Revision.ToString());
        AppendTokenPart(tokenSource, transaction.TransactionId);
        AppendTokenPart(tokenSource, transaction.Description);
        AppendTokenPart(tokenSource, transaction.GroupId ?? string.Empty);
        AppendTokenPart(tokenSource, transaction.BaselineRevision?.ToString() ?? string.Empty);
        AppendTokenPart(tokenSource, transaction.BoundPayloadId?.Value ?? string.Empty);
        AppendTokenPart(tokenSource, publishesRuntime ? "publish" : "draft");
        foreach (CSharpSourceDocument source in candidate.Request.Sources.OrderBy(
            static source => source.LogicalPath,
            StringComparer.Ordinal))
        {
            AppendTokenPart(tokenSource, source.LogicalPath);
            AppendTokenPart(tokenSource, source.Source);
            AppendTokenPart(
                tokenSource,
                source.IdentitySidecar is null
                    ? string.Empty
                    : IdentitySidecarJson.Serialize(source.IdentitySidecar, writeIndented: false));
        }
        AppendTokenPart(
            tokenSource,
            ScriptAssetJson.Serialize(candidate.Analysis.ScriptAssets!, writeIndented: false));
        AppendTokenPart(tokenSource, GraphJson.Serialize(candidate.Graph, writeIndented: false));
        if (graphLayout is not null)
        {
            AppendTokenPart(tokenSource, graphLayout.Path);
            AppendTokenPart(tokenSource, graphLayout.BaselineDigest);
            AppendTokenPart(tokenSource, graphLayout.CandidateText);
        }
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(tokenSource.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendTokenPart(StringBuilder builder, string value)
    {
        builder.Append(value.Length).Append(':').Append(value).Append(';');
    }

    private PreparedGraphLayout? PrepareGraphLayout(
        CSharpAuthoringSnapshot baseline,
        CSharpAuthoringSnapshot candidate)
    {
        if (artifactOutputs.GraphLayoutPath is null)
        {
            return null;
        }

        string path = artifactOutputs.GraphLayoutPath;
        string baselineText = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        try
        {
            GraphLayoutDocument baselineLayout = baselineText.Length == 0
                ? GraphLayoutDocument.CreateEmpty()
                : GraphLayoutJson.Deserialize(baselineText);
            CSharpGraphRoundTripInspection inspection = roundTripInspector.Inspect(
                baseline,
                candidate,
                baselineLayout);
            string baselineCanonical = GraphLayoutJson.Serialize(
                baselineLayout,
                writeIndented: false);
            string candidateCanonical = GraphLayoutJson.Serialize(
                inspection.CandidateLayout!,
                writeIndented: false);
            string candidateText = string.Equals(
                    baselineCanonical,
                    candidateCanonical,
                    StringComparison.Ordinal)
                ? baselineText
                : GraphLayoutJson.Serialize(
                    inspection.CandidateLayout!,
                    writeIndented: true) + "\n";
            return new PreparedGraphLayout(
                path,
                ComputeTextDigest(baselineText),
                baselineText,
                candidateText,
                inspection.Report);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or ArgumentException)
        {
            throw new AuthoringProtocolException(
                AuthoringFailureKind.Conflict,
                "UAW1016",
                $"The graph layout cannot join the authoring transaction: {exception.Message}",
                innerException: exception);
        }
    }

    private static string ComputeTextDigest(string text) =>
        "sha256:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    private static string ComputeByteDigest(byte[] bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ValidateGraphLayoutBaseline(PreparedGraphLayout? graphLayout)
    {
        if (graphLayout is null)
        {
            return;
        }
        string currentText = File.Exists(graphLayout.Path)
            ? File.ReadAllText(graphLayout.Path)
            : string.Empty;
        if (!string.Equals(
                ComputeTextDigest(currentText),
                graphLayout.BaselineDigest,
                StringComparison.Ordinal))
        {
            throw new AuthoringProtocolException(
                AuthoringFailureKind.Conflict,
                "UAW1015",
                "The graph layout changed after the diff preview; refresh and review the synchronized layout again.");
        }
    }

    private CSharpAuthoringMutation PrepareMutation(
        CSharpAuthoringWorkspace workspace,
        Func<CSharpAuthoringWorkspace, CSharpAuthoringMutation> prepare,
        AuthoringTransactionContext transaction,
        bool publishesRuntime,
        out PreparedGraphLayout? graphLayout)
    {
        if (transaction.PreviewToken is null)
        {
            CSharpAuthoringMutation mutation = prepare(workspace);
            graphLayout = publishesRuntime && mutation.Succeeded && mutation.Snapshot is not null
                ? PrepareGraphLayout(workspace.Current, mutation.Snapshot)
                : null;
            return mutation;
        }
        PreparedAuthoringPreview prepared = TakePreparedPreview(
            workspace.Current,
            transaction,
            publishesRuntime);
        graphLayout = prepared.GraphLayout;
        return new CSharpAuthoringMutation(
            workspace.Current.Revision,
            prepared.Candidate,
            prepared.Diagnostics);
    }

    private CSharpAuthoringSnapshot ResolvePreparedSnapshot(
        CSharpAuthoringSnapshot baseline,
        CSharpAuthoringSnapshot fallbackCandidate,
        AuthoringTransactionContext transaction,
        bool publishesRuntime,
        out PreparedGraphLayout? graphLayout)
    {
        if (transaction.PreviewToken is null)
        {
            graphLayout = publishesRuntime
                ? PrepareGraphLayout(baseline, fallbackCandidate)
                : null;
            return fallbackCandidate;
        }
        PreparedAuthoringPreview prepared = TakePreparedPreview(
            baseline,
            transaction,
            publishesRuntime);
        graphLayout = prepared.GraphLayout;
        return prepared.Candidate;
    }

    private PreparedAuthoringPreview TakePreparedPreview(
        CSharpAuthoringSnapshot baseline,
        AuthoringTransactionContext transaction,
        bool publishesRuntime)
    {
        PreparedAuthoringPreview prepared = RequirePreparedPreview(
            baseline,
            transaction,
            publishesRuntime);
        if (transaction.PreviewToken is null ||
            !preparedPreviews.Remove(transaction.PreviewToken))
        {
            throw new AuthoringProtocolException(
                AuthoringFailureKind.Conflict,
                "UAW1012",
                "The candidate no longer matches the confirmed diff preview; no source, artifact, history, or runtime generation was changed.");
        }
        return prepared;
    }

    private PreparedAuthoringPreview RequirePreparedPreview(
        CSharpAuthoringSnapshot baseline,
        AuthoringTransactionContext transaction,
        bool publishesRuntime)
    {
        if (transaction.PreviewToken is null ||
            !preparedPreviews.TryGetValue(
                transaction.PreviewToken,
                out PreparedAuthoringPreview? prepared) ||
            nativeReservations.ContainsKey(transaction.PreviewToken) ||
            prepared.BaselineRevision != baseline.Revision ||
            prepared.ClientBaselineRevision != transaction.BaselineRevision ||
            !string.Equals(
                prepared.TransactionId,
                transaction.TransactionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                prepared.Description,
                transaction.Description,
                StringComparison.Ordinal) ||
            !string.Equals(prepared.GroupId, transaction.GroupId, StringComparison.Ordinal) ||
            prepared.BoundPayloadId != transaction.BoundPayloadId ||
                prepared.PublishesRuntime != publishesRuntime)
        {
            throw new AuthoringProtocolException(
                AuthoringFailureKind.Conflict,
                "UAW1012",
                "The candidate no longer matches the confirmed diff preview; no source, artifact, history, or runtime generation was changed.");
        }
        ValidateGraphLayoutBaseline(prepared.GraphLayout);
        return prepared;
    }

    private NativePublicationPrepareResponse CreateNativePublicationPrepare(
        CSharpAuthoringSnapshot baseline,
        PreparedAuthoringPreview prepared,
        PreparedAuthoringOutputs outputs,
        string transactionId,
        string? previewToken)
    {
        if (string.IsNullOrWhiteSpace(previewToken))
        {
            throw new AuthoringProtocolException(
                AuthoringFailureKind.Conflict,
                "UAW1020",
                "Native publication requires the exact accepted authoring preview token.");
        }
        ScriptAssetDocument beforeAssets = baseline.Analysis.ScriptAssets!;
        ScriptAssetDocument afterAssets = prepared.Candidate.Analysis.ScriptAssets!;
        return new NativePublicationPrepareResponse(
            "unifiedscript.native-publication-prepare/v0.1",
            transactionId,
            previewToken,
            prepared.BaselineRevision,
            prepared.Candidate.Revision,
            prepared.PublishesRuntime,
            prepared.HistoryMutation?.TransactionId ?? transactionId,
            prepared.HistoryMutation is null
                ? "commit"
                : prepared.HistoryMutation.Direction == CSharpAuthoringHistoryDirection.Undo
                    ? "undo"
                    : "redo",
            prepared.Candidate.Analysis.ScriptAssets!,
            BuildNativeBindingChanges(beforeAssets, afterAssets),
            outputs.Outputs.Select(CreateNativePublicationOutput).ToArray());
    }

    private NativePublicationOutput CreateNativePublicationOutput(AuthoringOutput output)
    {
        bool exists = File.Exists(output.Path);
        byte[] baseline = exists ? File.ReadAllBytes(output.Path) : [];
        return new NativePublicationOutput(
            output.Path,
            exists,
            ComputeByteDigest(baseline),
            ComputeByteDigest(output.Content),
            output.Content);
    }

    private void EnsureNativePublicationBaselinesUnchanged(
        IReadOnlyList<NativePublicationOutput> outputs)
    {
        foreach (NativePublicationOutput output in outputs)
        {
            bool exists = File.Exists(output.Path);
            byte[] current = exists ? File.ReadAllBytes(output.Path) : [];
            if (exists != output.BaselineExists ||
                !string.Equals(ComputeByteDigest(current), output.BaselineDigest, StringComparison.Ordinal))
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1002",
                    $"Authoring output '{output.Path}' changed after native publication prepare; refresh and prepare again.");
            }
        }
    }

    private async Task<AuthoringWorkspaceResponse> ApplyAsync(
        Func<CSharpAuthoringWorkspace, CSharpAuthoringMutation> prepare,
        AuthoringTransactionContext transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureAuthoringAvailable();
            EnsureNoNativePublicationReservation();
            if (transaction.GroupId is not null)
            {
                ActiveAuthoringGroup group = RequireActiveGroup(
                    transaction,
                    requireGroupOperationId: false);
                EnsureFilesUnchanged();
                if (string.Equals(transaction.TransactionId, group.Id, StringComparison.Ordinal))
                {
                    throw new AuthoringProtocolException(
                        AuthoringFailureKind.InvalidInput,
                        "UAW1008",
                        "A staged operation ID must differ from its grouped transaction ID.");
                }
                if (group.Draft.ContainsOperationId(transaction.TransactionId))
                {
                    throw new AuthoringProtocolException(
                        AuthoringFailureKind.Conflict,
                        "UAW1006",
                        $"Staged authoring operation ID '{transaction.TransactionId}' was already accepted.");
                }

                if (transaction.PreviewToken is not null)
                {
                    PreparedAuthoringPreview guardedPreview = RequirePreparedPreview(
                        group.Draft.Current,
                        transaction,
                        publishesRuntime: false);
                    EnsureNativeIdentityUsesNativePublication(
                        group.Draft.Current,
                        guardedPreview.Candidate);
                }

                CSharpAuthoringMutation stagedMutation = PrepareMutation(
                    group.Draft,
                    prepare,
                    transaction,
                    publishesRuntime: false,
                    out _);
                if (!stagedMutation.Succeeded || stagedMutation.Snapshot is null)
                {
                    throw new AuthoringProtocolException(
                        AuthoringFailureKind.Conflict,
                        "UAW1003",
                        "The staged semantic patch was rejected; the grouped draft was not changed.",
                        stagedMutation.Diagnostics);
                }
                EnsureNativeIdentityUsesNativePublication(
                    group.Draft.Current,
                    stagedMutation.Snapshot);
                group.Draft.Commit(
                    stagedMutation,
                    transaction.TransactionId,
                    transaction.Description);
                activeGroup = group with
                {
                    StagedOperationCount = checked(group.StagedOperationCount + 1),
                };
                preparedPreviews.Clear();
                Workspace = CreateWorkspace(activeGroup.Draft.Current, reload.Generation);
                return Workspace;
            }

            ValidateTransaction(transaction);
            EnsureFilesUnchanged();
            if (transaction.PreviewToken is not null)
            {
                PreparedAuthoringPreview guardedPreview = RequirePreparedPreview(
                    authoring.Current,
                    transaction,
                    publishesRuntime: true);
                EnsureNativeIdentityUsesNativePublication(
                    authoring.Current,
                    guardedPreview.Candidate);
            }
            CSharpAuthoringMutation mutation = PrepareMutation(
                authoring,
                prepare,
                transaction,
                publishesRuntime: true,
                out PreparedGraphLayout? graphLayout);
            if (!mutation.Succeeded || mutation.Snapshot is null)
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1003",
                    "The semantic patch was rejected; no source or runtime generation was changed.",
                    mutation.Diagnostics);
            }
            CSharpAuthoringHistoryDocument persistedHistory = authoring.PreviewCommit(
                mutation,
                transaction.TransactionId,
                transaction.Description);
            return CommitPreparedSnapshot(
                mutation.Snapshot,
                () => authoring.Commit(
                    mutation,
                    transaction.TransactionId,
                    transaction.Description),
                persistedHistory,
                transaction.TransactionId,
                graphLayout);
        }
        finally
        {
            gate.Release();
        }
    }

    private void EnsureAuthoringAvailable()
    {
        if (execution is not null)
        {
            throw new AuthoringProtocolException(
                AuthoringFailureKind.Conflict,
                "UAW1001",
                "Finish the active debug execution before changing the authoring workspace.");
        }
    }

    private CoreClrDebugExecution RequireExecution(
        SemanticId generationId,
        long executionId)
    {
        if (generationId.IsEmpty || generationId != reload.Generation.GenerationId)
        {
            throw new BreakpointProtocolException(
                "UDB1002",
                "The debug command belongs to a stale or different CoreCLR program generation.");
        }

        CoreClrDebugExecution current = execution ??
            throw new BreakpointProtocolException(
                "UDB1005",
                "There is no active debug execution.");
        if (executionId <= 0 || current.ExecutionId != executionId)
        {
            throw new BreakpointProtocolException(
                "UDB1005",
                $"Debug execution '{executionId}' is stale or does not exist.");
        }

        return current;
    }

    private CoreClrDebugPause RequirePause(SemanticId pauseId)
    {
        CoreClrDebugPause current = pause ??
            throw new BreakpointProtocolException(
                "UDB1006",
                "The debug execution is not paused.");
        if (pauseId.IsEmpty || current.PauseId != pauseId)
        {
            throw new BreakpointProtocolException(
                "UDB1006",
                $"Pause '{pauseId}' is stale or is not the current suspended execution.");
        }

        return current;
    }

    private void ValidateTransaction(
        AuthoringTransactionContext transaction,
        bool allowNativeReservation = false)
    {
        if (!allowNativeReservation && nativeReservations.Count > 0)
        {
            throw new AuthoringProtocolException(
                AuthoringFailureKind.Conflict,
                "UAW1022",
                "A native publication is reserved; cancel or finish it before starting another authoring operation.");
        }
        if (activeGroup is not null)
        {
            throw new AuthoringProtocolException(
                AuthoringFailureKind.Conflict,
                "UAW1009",
                $"Authoring group '{activeGroup.Id}' is active; use its group ID or commit/cancel it first.");
        }
        if (transaction.BaselineRevision is long baselineRevision &&
            baselineRevision != authoring.Current.Revision)
        {
            throw new AuthoringProtocolException(
                AuthoringFailureKind.Conflict,
                "UAW1005",
                $"Authoring transaction revision '{baselineRevision}' is stale; " +
                $"the current revision is '{authoring.Current.Revision}'.");
        }

        if (authoring.ContainsOperationId(transaction.TransactionId))
        {
            throw new AuthoringProtocolException(
                AuthoringFailureKind.Conflict,
                "UAW1006",
                $"Authoring transaction ID '{transaction.TransactionId}' was already committed.");
        }
    }

    private void EnsureNoNativePublicationReservation()
    {
        if (nativeReservations.Count > 0)
        {
            throw new AuthoringProtocolException(
                AuthoringFailureKind.Conflict,
                "UAW1022",
                "A native publication is reserved; cancel or finish it before starting another authoring operation.");
        }
    }

    private ActiveAuthoringGroup RequireActiveGroup(
        AuthoringTransactionContext transaction,
        bool requireGroupOperationId = true)
    {
        ActiveAuthoringGroup group = activeGroup ??
            throw new AuthoringProtocolException(
                AuthoringFailureKind.Conflict,
                "UAW1010",
                "There is no active grouped authoring transaction.");
        if (!string.Equals(transaction.GroupId, group.Id, StringComparison.Ordinal))
        {
            throw new AuthoringProtocolException(
                AuthoringFailureKind.Conflict,
                "UAW1009",
                $"Request group '{transaction.GroupId ?? "<none>"}' does not match active group '{group.Id}'.");
        }
        if (requireGroupOperationId &&
            !string.Equals(transaction.TransactionId, group.Id, StringComparison.Ordinal))
        {
            throw new AuthoringProtocolException(
                AuthoringFailureKind.InvalidInput,
                "UAW1008",
                "Commit and cancel requests must use the grouped transaction ID as their operation ID.");
        }
        if (transaction.BaselineRevision is long baselineRevision &&
            baselineRevision != group.Draft.Current.Revision)
        {
            throw new AuthoringProtocolException(
                AuthoringFailureKind.Conflict,
                "UAW1005",
                $"Grouped draft revision '{baselineRevision}' is stale; " +
                $"the current draft revision is '{group.Draft.Current.Revision}'.");
        }
        return group;
    }

    private AuthoringWorkspaceResponse CommitPreparedSnapshot(
        CSharpAuthoringSnapshot snapshot,
        Action commit,
        CSharpAuthoringHistoryDocument persistedHistory,
        string transactionId,
        PreparedGraphLayout? graphLayout)
    {
        CSharpAuthoringSnapshot previous = authoring.Current;
        EnsureNativeIdentityUsesNativePublication(previous, snapshot);
        long fromGeneration = reload.Generation.Number;
        var phases = new List<ReloadTransactionPhaseResponse>();
        long phaseStart = Stopwatch.GetTimestamp();
        CoreClrDebugReloadResult nextReload;
        try
        {
            nextReload = transport.Reload(snapshot.Request);
            phases.Add(CompleteReloadPhase(
                "compile",
                phaseStart,
                "succeeded",
                "Compiled and loaded the candidate Workbench CoreCLR generation."));
        }
        catch (Exception exception)
        {
            phases.Add(CompleteReloadPhase(
                "compile",
                phaseStart,
                "failed",
                exception.Message));
            phases.Add(new ReloadTransactionPhaseResponse(
                "rollback",
                "unchanged",
                0,
                "No source, artifact, history, or active generation was changed."));
            var failedTransaction = new ReloadTransactionResponse(
                "unifiedscript.reload-transaction/v0.1",
                transactionId,
                "authoring",
                "rolledBack",
                fromGeneration,
                fromGeneration + 1,
                fromGeneration,
                phases,
                exception.Message);
            throw new AuthoringProtocolException(
                AuthoringFailureKind.Conflict,
                "UAW1004",
                "The candidate runtime generation was rejected; the previous source, artifacts, history, and runtime generation remain active.",
                reloadTransaction: failedTransaction,
                innerException: exception);
        }
        phaseStart = Stopwatch.GetTimestamp();
        try
        {
            ValidateGraphLayoutBaseline(graphLayout);
            Persist(snapshot, persistedHistory, graphLayout);
            phases.Add(CompleteReloadPhase(
                "artifactPublication",
                phaseStart,
                "succeeded",
                "Atomically published source, identities, graph layout, authoring artifacts, PE/PDB, and history."));
        }
        catch (Exception exception)
        {
            phases.Add(CompleteReloadPhase(
                "artifactPublication",
                phaseStart,
                "failed",
                exception.Message));
            phaseStart = Stopwatch.GetTimestamp();
            try
            {
                reload = transport.Reload(previous.Request);
                Health = CreateHealth(reload.Generation);
                Workspace = CreateWorkspace(previous, reload.Generation);
                phases.Add(CompleteReloadPhase(
                    "rollback",
                    phaseStart,
                    "restored",
                    "Restored the previous runtime program as a new Workbench generation."));
            }
            catch (Exception restoreException)
            {
                phases.Add(CompleteReloadPhase(
                    "rollback",
                    phaseStart,
                    "failed",
                    restoreException.Message));
                var failedTransaction = new ReloadTransactionResponse(
                    "unifiedscript.reload-transaction/v0.1",
                    transactionId,
                    "authoring",
                    "failed",
                    fromGeneration,
                    nextReload.Generation.Number,
                    reload.Generation.Number,
                    phases,
                    restoreException.Message);
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.ExecutionFailed,
                    "UAW1014",
                    "The authoring commit failed and the previous runtime program could not be fully restored.",
                    rollbackState: "failed",
                    reloadTransaction: failedTransaction,
                    innerException: new AggregateException(exception, restoreException));
            }

            var restoredTransaction = new ReloadTransactionResponse(
                "unifiedscript.reload-transaction/v0.1",
                transactionId,
                "authoring",
                "rolledBack",
                fromGeneration,
                nextReload.Generation.Number,
                reload.Generation.Number,
                phases,
                exception.Message);
            if (exception is AuthoringProtocolException authoringException)
            {
                throw authoringException.WithRollback("restored", restoredTransaction);
            }
            throw new AuthoringProtocolException(
                AuthoringFailureKind.Conflict,
                "UAW1013",
                "The authoring commit failed; the previous source, artifacts, history, and runtime program were restored as a new generation.",
                rollbackState: "restored",
                reloadTransaction: restoredTransaction,
                innerException: exception);
        }

        phaseStart = Stopwatch.GetTimestamp();
        commit();
        preparedPreviews.Clear();
        if (artifactOutputs.HistoryPath is not null)
        {
            historyPersistenceState = "persisted";
            historyPersistenceMessage = null;
        }
        reload = nextReload;
        bindings = reload.Breakpoints.Bindings;
        Health = CreateHealth(reload.Generation);
        phases.Add(CompleteReloadPhase(
            "workbenchGenerationSwap",
            phaseStart,
            "succeeded",
            "Committed authoring history and exposed the candidate Workbench generation."));
        var transaction = new ReloadTransactionResponse(
            "unifiedscript.reload-transaction/v0.1",
            transactionId,
            "authoring",
            "published",
            fromGeneration,
            reload.Generation.Number,
            reload.Generation.Number,
            phases,
            null);
        Workspace = CreateWorkspace(authoring.Current, reload.Generation, transaction);
        return Workspace;
    }

    private AuthoringWorkspaceResponse AdoptNativePublication(
        NativePublicationReservation reservation,
        AuthoringTransactionContext transaction)
    {
        if (reservation.HistoryMutation is not null)
        {
            authoring.Commit(reservation.HistoryMutation, transaction.TransactionId);
        }
        else
        {
            authoring.Commit(
                new CSharpAuthoringMutation(
                    authoring.Current.Revision,
                    reservation.Candidate,
                    []),
                transaction.TransactionId,
                transaction.Description);
        }

        reservation.CandidateReload.Commit();
        reload = reservation.CandidateReload.Result;
        bindings = reload.Breakpoints.Bindings;
        preparedPreviews.Clear();
        if (artifactOutputs.HistoryPath is not null)
        {
            historyPersistenceState = "persisted";
            historyPersistenceMessage = null;
        }
        foreach (CSharpSourceDocument source in reservation.Candidate.Request.Sources)
            persistedSources[source.LogicalPath] = source.Source;
        IdentitySidecarDocument? sidecar = reservation.Candidate.Analysis.IdentitySidecars.SingleOrDefault(item =>
            string.Equals(item.SourcePath, sourceLogicalPath, StringComparison.Ordinal));
        persistedIdentity = sidecar is null ? persistedIdentity : IdentitySidecarJson.Serialize(sidecar) + "\n";
        foreach (NativePublicationOutput output in reservation.Prepare.Outputs)
        {
            persistedArtifactBaselines[Path.GetFullPath(output.Path)] = new FileBaseline(
                true,
                output.CandidateDigest);
        }
        CapturePersistedIdentityFiles();
        Health = CreateHealth(reload.Generation);
        var reloadTransaction = new ReloadTransactionResponse(
            "unifiedscript.reload-transaction/v0.1",
            transaction.TransactionId,
            "authoring",
            "published",
            reload.Generation.Number - 1,
            reload.Generation.Number,
            reload.Generation.Number,
            [new ReloadTransactionPhaseResponse(
                "nativePublicationAdopt",
                "succeeded",
                0,
                "Adopted the preloaded candidate after the native participant committed its journal.")],
            null);
        Workspace = CreateWorkspace(authoring.Current, reload.Generation, reloadTransaction);
        return Workspace;
    }

    private static void EnsureNativePublicationCandidateOutputs(
        IReadOnlyList<NativePublicationOutput> outputs)
    {
        foreach (NativePublicationOutput output in outputs)
        {
            if (!File.Exists(output.Path))
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1022",
                    $"Native publication did not write prepared output '{output.Path}'.");
            byte[] current = File.ReadAllBytes(output.Path);
            if (!string.Equals(ComputeByteDigest(current), output.CandidateDigest, StringComparison.Ordinal))
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1022",
                    $"Native publication output '{output.Path}' does not match the prepared candidate bytes.");
            }
        }
    }

    private static void EnsureNativePublicationCancellationBaseline(
        IReadOnlyList<NativePublicationOutput> outputs)
    {
        foreach (NativePublicationOutput output in outputs)
        {
            bool exists = File.Exists(output.Path);
            byte[] current = exists ? File.ReadAllBytes(output.Path) : [];
            string digest = ComputeByteDigest(current);
            if (exists != output.BaselineExists ||
                !string.Equals(digest, output.BaselineDigest, StringComparison.Ordinal))
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1023",
                    $"Native publication output '{output.Path}' is no longer at its prepared baseline; finish or recover the native transaction before cancelling.");
            }
        }
    }

    private static void EnsureNativeIdentityUsesNativePublication(
        CSharpAuthoringSnapshot baseline,
        CSharpAuthoringSnapshot candidate)
    {
        ScriptAssetDocument? beforeAssets = baseline.Analysis.ScriptAssets;
        ScriptAssetDocument? afterAssets = candidate.Analysis.ScriptAssets;
        if (beforeAssets is null || afterAssets is null ||
            BuildNativeBindingChanges(beforeAssets, afterAssets).Count == 0)
        {
            return;
        }

        throw new AuthoringProtocolException(
            AuthoringFailureKind.Conflict,
            "UAW1030",
            "This authoring operation changes a native Script Asset binding or identity. " +
            "Prepare native publication for the exact preview before committing source, declaration, history, group, or refresh changes.");
    }

    private static ReloadTransactionPhaseResponse CompleteReloadPhase(
        string name,
        long startedTimestamp,
        string state,
        string detail)
    {
        long elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
        long microseconds = Math.Max(
            0,
            (long)Math.Ceiling(elapsedTicks * 1_000_000d / Stopwatch.Frequency));
        return new ReloadTransactionPhaseResponse(
            name,
            state,
            microseconds,
            detail.Length <= 2048 ? detail : detail[..2048]);
    }

    private void EnsureFilesUnchanged()
    {
        foreach ((string logicalPath, string physicalPath) in sourceFiles)
        {
            string diskSource = File.ReadAllText(physicalPath);
            if (!string.Equals(diskSource, persistedSources[logicalPath], StringComparison.Ordinal))
            {
                string message =
                    $"Source '{logicalPath}' changed outside the workbench; reload before saving.";
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1002",
                    message,
                    [new UnifiedDiagnostic(
                        "UAW1002",
                        UnifiedDiagnosticSeverity.Error,
                        message,
                        SourceSpan.Unknown(logicalPath))]);
            }
        }
        foreach ((string logicalPath, string path) in identityFiles)
        {
            string? diskIdentity = File.Exists(path) ? File.ReadAllText(path) : null;
            if (!persistedIdentities.TryGetValue(logicalPath, out string? persisted) ||
                !string.Equals(diskIdentity, persisted, StringComparison.Ordinal))
            {
                const string message =
                    "The identity sidecar changed outside the workbench; reload before saving.";
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1002",
                    message,
                    [new UnifiedDiagnostic(
                        "UAW1002",
                        UnifiedDiagnosticSeverity.Error,
                        message,
                        SourceSpan.Unknown(logicalPath))]);
            }
        }
        foreach ((string path, FileBaseline baseline) in persistedArtifactBaselines)
        {
            bool exists = File.Exists(path);
            byte[] current = exists ? File.ReadAllBytes(path) : [];
            if (exists != baseline.Exists ||
                !string.Equals(ComputeByteDigest(current), baseline.Digest, StringComparison.Ordinal))
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1002",
                    $"Authoring artifact '{path}' changed outside the workbench; reload before saving.");
            }
        }
    }

    private IEnumerable<string> KnownArtifactPaths() =>
        new[]
        {
            artifactOutputs.DeclarationPath,
            artifactOutputs.SurfacePath,
            artifactOutputs.ManagedAssemblyPath,
            artifactOutputs.PortablePdbPath,
            artifactOutputs.HistoryPath,
            artifactOutputs.GraphLayoutPath,
            artifactOutputs.CompiledChunkPath,
            identityPath,
        }.OfType<string>().Concat(identityFiles.Values)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private void CaptureArtifactBaselines()
    {
        persistedArtifactBaselines.Clear();
        foreach (string path in KnownArtifactPaths())
        {
            if (IsGraphLayoutPath(path))
            {
                continue;
            }
            bool exists = File.Exists(path);
            // A missing optional artifact has no external bytes to protect yet.
            // It is captured once a prepared transaction publishes it; graph
            // layouts still carry their exact preview baseline through
            // PreparedGraphLayout, so a file created before preview is valid
            // while a file changed after preview is rejected.
            if (exists)
            {
                persistedArtifactBaselines[path] = new FileBaseline(
                    true,
                    ComputeByteDigest(File.ReadAllBytes(path)));
            }
        }
    }

    private void CaptureArtifactBaselines(IReadOnlyList<AuthoringOutput> outputs)
    {
        foreach (AuthoringOutput output in outputs)
        {
            string path = Path.GetFullPath(output.Path);
            if (IsGraphLayoutPath(path))
            {
                continue;
            }
            persistedArtifactBaselines[path] = new FileBaseline(
                true,
                ComputeByteDigest(output.Content));
        }
    }

    private bool IsGraphLayoutPath(string path) =>
        artifactOutputs.GraphLayoutPath is not null &&
        string.Equals(
            Path.GetFullPath(path),
            artifactOutputs.GraphLayoutPath,
            StringComparison.OrdinalIgnoreCase);

    private void ValidateEditableSources(CSharpAuthoringSnapshot snapshot)
    {
        foreach (CSharpSourceDocument source in snapshot.Request.Sources)
        {
            if (readOnlySources.Contains(source.LogicalPath) && source.Source != persistedSources[source.LogicalPath])
                throw new AuthoringProtocolException(AuthoringFailureKind.Conflict, "UAW1017",
                    $"'{source.LogicalPath}' is a project compilation input; open that source in its own authoring session before editing it.");
        }
    }

    private PreparedAuthoringOutputs BuildPreparedOutputs(
        CSharpAuthoringSnapshot snapshot,
        CSharpAuthoringHistoryDocument history,
        PreparedGraphLayout? graphLayout,
        ImportedProjectInput? imported = null)
    {
        if (imported is null) ValidateEditableSources(snapshot);
        IReadOnlyDictionary<string, string> files = imported?.Input.SourceFiles ?? sourceFiles;
        IReadOnlyDictionary<string, string> baseline = imported?.DiskSources ?? persistedSources;
        IReadOnlyDictionary<string, string> outputIdentityFiles =
            imported?.Input.IdentityFiles ?? identityFiles;
        var artifactPaths = new[] { artifactOutputs.DeclarationPath, artifactOutputs.SurfacePath,
            artifactOutputs.ManagedAssemblyPath, artifactOutputs.PortablePdbPath,
            artifactOutputs.HistoryPath, artifactOutputs.GraphLayoutPath, artifactOutputs.CompiledChunkPath }
            .OfType<string>()
            .Concat(outputIdentityFiles.Values);
        foreach (string path in artifactPaths)
            if (files.Values.Any(source => string.Equals(source, path, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Authoring artifact '{path}' cannot overwrite a compilation source.");
        var outputs = snapshot.Request.Sources
            .Where(source => source.Source != baseline[source.LogicalPath])
            .Select(source => new AuthoringOutput(
                files[source.LogicalPath],
                Encoding.UTF8.GetBytes(source.Source)))
            .ToList();
        string? nextIdentity = null;
        string primaryLogical = imported?.PrimaryLogical ?? sourceLogicalPath;
        IReadOnlySet<string> editableReadOnly = imported?.Input.ReadOnlySources ?? readOnlySources;
        foreach (IdentitySidecarDocument sidecar in snapshot.Analysis.IdentitySidecars)
        {
            if (!outputIdentityFiles.TryGetValue(sidecar.SourcePath, out string? sidecarPath) ||
                (editableReadOnly.Contains(sidecar.SourcePath) &&
                 !string.Equals(sidecar.SourcePath, primaryLogical, StringComparison.Ordinal)))
                continue;
            string sidecarText = IdentitySidecarJson.Serialize(sidecar) + "\n";
            if (string.Equals(sidecar.SourcePath, primaryLogical, StringComparison.Ordinal))
                nextIdentity = sidecarText;
            outputs.Add(new AuthoringOutput(sidecarPath, Encoding.UTF8.GetBytes(sidecarText)));
        }

        ScriptAssetDocument scriptAssets = snapshot.Analysis.ScriptAssets!;
        AuthoringSurfaceDocument surface = new AuthoringSurfaceProjector().Project(
            scriptAssets,
            snapshot.Graph,
            snapshot.Analysis.DeclarationOwners);
        if (artifactOutputs.DeclarationPath is not null)
        {
            outputs.Add(new AuthoringOutput(
                artifactOutputs.DeclarationPath,
                Encoding.UTF8.GetBytes(ScriptAssetJson.Serialize(scriptAssets) + "\n")));
        }
        if (artifactOutputs.SurfacePath is not null)
        {
            outputs.Add(new AuthoringOutput(
                artifactOutputs.SurfacePath,
                Encoding.UTF8.GetBytes(AuthoringSurfaceJson.Serialize(surface) + "\n")));
        }
        if (graphLayout is not null)
        {
            outputs.Add(new AuthoringOutput(
                graphLayout.Path,
                Encoding.UTF8.GetBytes(graphLayout.CandidateText)));
        }

        if (artifactOutputs.ManagedAssemblyPath is not null ||
            artifactOutputs.PortablePdbPath is not null || artifactOutputs.CompiledChunkPath is not null)
        {
            CSharpProgramEmitResult emitted = emitter.Emit(snapshot.Request);
            if (!emitted.Succeeded || emitted.Image is null)
            {
                throw new AuthoringProtocolException(
                    AuthoringFailureKind.Conflict,
                    "UAW1004",
                    "The candidate managed assembly could not be emitted; no authoring artifact was changed.",
                    emitted.Diagnostics);
            }
            if (artifactOutputs.ManagedAssemblyPath is not null)
                outputs.Add(new AuthoringOutput(artifactOutputs.ManagedAssemblyPath, emitted.Image.PortableExecutable));
            if (artifactOutputs.PortablePdbPath is not null)
                outputs.Add(new AuthoringOutput(artifactOutputs.PortablePdbPath, emitted.Image.PortablePdb));
            if (artifactOutputs.CompiledChunkPath is not null)
            {
                CSharpProgramChunkEmitResult chunk = new CSharpProgramChunkCompiler().Compile(
                    emitted.Image,
                    new CSharpProgramChunkOptions(RuntimeReferencePaths: snapshot.Request.RuntimeReferencePaths));
                outputs.Add(new AuthoringOutput(artifactOutputs.CompiledChunkPath, chunk.Content));
            }
        }
        if (artifactOutputs.HistoryPath is not null)
        {
            outputs.Add(new AuthoringOutput(
                artifactOutputs.HistoryPath,
                Encoding.UTF8.GetBytes(CSharpAuthoringHistoryJson.Serialize(history) + "\n")));
        }
        if (imported is not null)
        {
            if (InputIdentity(imported.Input) != InputIdentity(LoadRefreshInput()))
                throw new AuthoringProtocolException(AuthoringFailureKind.Conflict, "UAW1002",
                    "Project inputs changed while refreshing; retry after saving. No external source edit was overwritten.");
            if (imported.ArchivedHistory is not null) outputs.Add(imported.ArchivedHistory);
        }
        return new PreparedAuthoringOutputs(outputs, nextIdentity);
    }

    private void Persist(
        CSharpAuthoringSnapshot snapshot,
        CSharpAuthoringHistoryDocument history,
        PreparedGraphLayout? graphLayout,
        ImportedProjectInput? imported = null)
    {
        PreparedAuthoringOutputs prepared = BuildPreparedOutputs(snapshot, history, graphLayout, imported);
        WriteTransactionally(prepared.Outputs);
        if (imported is not null)
        {
            sourceFiles = imported.Input.SourceFiles;
            readOnlySources = imported.Input.ReadOnlySources;
            identityFiles = imported.Input.IdentityFiles;
            sourceLogicalPath = imported.PrimaryLogical;
            persistedSources.Clear();
        }
        foreach (CSharpSourceDocument source in snapshot.Request.Sources)
            persistedSources[source.LogicalPath] = source.Source;
        persistedIdentity = prepared.NextIdentity;
        CapturePersistedIdentityFiles();
        if (imported is null)
            CaptureArtifactBaselines(prepared.Outputs);
        else
            // A project refresh may replace or remove physical sidecars.
            // Rebuild the full baseline after adopting its identity map so
            // a retired sidecar is not treated as an external mutation on
            // the next transaction.
            CaptureArtifactBaselines();
    }

    private static DebugHealthResponse CreateHealth(CoreClrProgramGeneration generation) => new(
        "unifiedscript.debug-health/v0.1",
        "ready",
        generation.GenerationId,
        generation.SourceCompilationId,
        generation.SemanticConfigurationId);

    private AuthoringWorkspaceResponse CreateWorkspace(
        CSharpAuthoringSnapshot snapshot,
        CoreClrProgramGeneration generation,
        ReloadTransactionResponse? reloadTransaction = null)
    {
        ScriptAssetDocument scriptAssets = snapshot.Analysis.ScriptAssets!;
        AuthoringSurfaceDocument surface = new AuthoringSurfaceProjector().Project(
            scriptAssets,
            snapshot.Graph,
            snapshot.Analysis.DeclarationOwners);
        return new AuthoringWorkspaceResponse(
            "unifiedscript.authoring-workspace/v0.5",
            snapshot.Revision,
            sourceLogicalPath,
            snapshot.Request.Sources.Single(source =>
                string.Equals(source.LogicalPath, sourceLogicalPath, StringComparison.Ordinal)).Source,
            scriptAssets,
            snapshot.Graph,
            surface,
            snapshot.Analysis.Profile,
            CreateHistoryResponse(),
            generation,
            activeGroup is null
                ? null
                : new AuthoringGroupResponse(
                    activeGroup.Id,
                    activeGroup.Description,
                    activeGroup.BaselineRevision,
                    activeGroup.StagedOperationCount),
            reloadTransaction);
    }

    private AuthoringHistoryResponse CreateHistoryResponse()
    {
        CSharpAuthoringHistoryState history = authoring.History;
        return new AuthoringHistoryResponse(
            history.CanUndo,
            history.CanRedo,
            history.UndoDepth,
            history.RedoDepth,
            history.UndoTransactionId,
            history.UndoDescription,
            history.RedoTransactionId,
            history.RedoDescription,
            authoring.LastOperationId,
            historyPersistenceState,
            historyPersistenceMessage);
    }

    private static void WriteTransactionally(IReadOnlyList<AuthoringOutput> outputs)
    {
        string[] fullPaths = outputs.Select(output => Path.GetFullPath(output.Path)).ToArray();
        if (fullPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fullPaths.Length)
        {
            throw new ArgumentException("Authoring output paths must be distinct.", nameof(outputs));
        }

        var pending = new List<PendingAuthoringOutput>(outputs.Count);
        var moved = new List<PendingAuthoringOutput>(outputs.Count);
        bool committed = false;
        try
        {
            for (int index = 0; index < outputs.Count; index++)
            {
                string fullPath = fullPaths[index];
                string directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
                Directory.CreateDirectory(directory);
                string nonce = Guid.NewGuid().ToString("N");
                string temporary = Path.Combine(
                    directory,
                    $".{Path.GetFileName(fullPath)}.{nonce}.tmp");
                string backup = Path.Combine(
                    directory,
                    $".{Path.GetFileName(fullPath)}.{nonce}.bak");
                File.WriteAllBytes(temporary, outputs[index].Content);
                pending.Add(new PendingAuthoringOutput(fullPath, temporary, backup));
            }

            foreach (PendingAuthoringOutput output in pending)
            {
                if (File.Exists(output.Path))
                {
                    File.Move(output.Path, output.BackupPath);
                }

                File.Move(output.TemporaryPath, output.Path);
                moved.Add(output);
            }

            committed = true;
        }
        catch
        {
            foreach (PendingAuthoringOutput output in moved.AsEnumerable().Reverse())
            {
                TryDelete(output.Path);
            }

            foreach (PendingAuthoringOutput output in pending.AsEnumerable().Reverse())
            {
                if (File.Exists(output.BackupPath))
                {
                    File.Move(output.BackupPath, output.Path, overwrite: true);
                }
            }

            throw;
        }
        finally
        {
            foreach (PendingAuthoringOutput output in pending)
            {
                TryDelete(output.TemporaryPath);
                if (committed)
                {
                    TryDelete(output.BackupPath);
                }
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<DebugSessionResponse> WaitForNextStateAsync(
        CoreClrDebugExecution currentExecution,
        IReadOnlyList<SemanticBreakpointBinding> bindings,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        Task<CoreClrDebugPause> pauseTask = transport.WaitForPauseAsync(
            currentExecution.ExecutionId,
            timeout.Token);
        Task<ExecutionResult> completionTask = transport.WaitForCompletionAsync(
            currentExecution.ExecutionId,
            timeout.Token);
        try
        {
            Task completed = await Task.WhenAny(pauseTask, completionTask);
            timeout.Cancel();
            if (completed == pauseTask && pauseTask.IsCompletedSuccessfully)
            {
                CoreClrDebugPause nextPause = await pauseTask;
                IReadOnlyList<CoreClrDebugStackFrame> frames = transport.GetStackTrace(
                    reload.Generation.GenerationId,
                    nextPause.PauseId);
                DebugFrameLocalsResponse[] frameLocals = frames
                    .Where(static frame => frame.HasLocals)
                    .Select(frame => new DebugFrameLocalsResponse(
                        frame.FrameId,
                        transport.GetLocals(
                            reload.Generation.GenerationId,
                            nextPause.PauseId,
                            frame.FrameId)))
                    .ToArray();
                IReadOnlyList<CoreClrDebugValue> locals = frameLocals.FirstOrDefault()?.Locals ?? [];
                return DebugSessionResponse.Paused(
                    reload.Generation,
                    currentExecution.ExecutionId,
                    nextPause,
                    frames,
                    locals,
                    frameLocals,
                    bindings);
            }

            ExecutionResult result = await completionTask;
            return DebugSessionResponse.Completed(
                reload.Generation,
                currentExecution.ExecutionId,
                result,
                bindings);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DebugSessionResponse.Running(
                reload.Generation,
                currentExecution.ExecutionId,
                bindings);
        }
    }

    private sealed record AuthoringOutput(string Path, byte[] Content);

    private sealed record PreparedAuthoringOutputs(
        IReadOnlyList<AuthoringOutput> Outputs,
        string? NextIdentity);

    private sealed record FileBaseline(bool Exists, string Digest);

    private sealed record PendingAuthoringOutput(
        string Path,
        string TemporaryPath,
        string BackupPath);

    private sealed record ActiveAuthoringGroup(
        string Id,
        string Description,
        long BaselineRevision,
        CSharpAuthoringWorkspace Draft,
        int StagedOperationCount);

    private sealed record PreparedAuthoringPreview(
        long BaselineRevision,
        long? ClientBaselineRevision,
        string TransactionId,
        string Description,
        string? GroupId,
        SemanticId? BoundPayloadId,
        bool PublishesRuntime,
        CSharpAuthoringSnapshot Candidate,
        IReadOnlyList<UnifiedDiagnostic> Diagnostics,
        PreparedGraphLayout? GraphLayout,
        CSharpAuthoringHistoryMutation? HistoryMutation);

    private sealed record PreparedGraphLayout(
        string Path,
        string BaselineDigest,
        string BaselineText,
        string CandidateText,
        CSharpGraphRoundTripReport Report);

    private sealed record NativePublicationReservation(
        NativePublicationPrepareResponse Prepare,
        CSharpAuthoringSnapshot Candidate,
        CSharpAuthoringHistoryDocument History,
        PreparedGraphLayout? GraphLayout,
        CSharpAuthoringHistoryMutation? HistoryMutation,
        CoreClrDebugPreparedReload CandidateReload);
}
