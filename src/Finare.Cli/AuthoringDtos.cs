using System.Text.Json;
using Finare.Authoring;
using Finare.Core;
using Finare.CSharp;
using Finare.Graph;
using Finare.Runtime;
using Finare.Unreal;

namespace Finare.Cli;

internal sealed record AuthoringArtifactOutputs(
    string? DeclarationPath = null,
    string? SurfacePath = null,
    string? ManagedAssemblyPath = null,
    string? PortablePdbPath = null,
    string? HistoryPath = null,
    string? GraphLayoutPath = null,
    string? CompiledChunkPath = null)
{
    public AuthoringArtifactOutputs Normalize() => new(
        NormalizePath(DeclarationPath),
        NormalizePath(SurfacePath),
        NormalizePath(ManagedAssemblyPath),
        NormalizePath(PortablePdbPath),
        NormalizePath(HistoryPath),
        NormalizePath(GraphLayoutPath),
        NormalizePath(CompiledChunkPath));

    private static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
}

internal sealed record AuthoringWorkspaceResponse(
    string SchemaVersion,
    long Revision,
    string SourcePath,
    string SourceText,
    ScriptAssetDocument ScriptAssets,
    GraphDocument Graph,
    AuthoringSurfaceDocument Surface,
    VisualProfileReport Profile,
    AuthoringHistoryResponse History,
    CoreClrProgramGeneration Generation,
    AuthoringGroupResponse? Group,
    ReloadTransactionResponse? ReloadTransaction);

internal sealed record ReloadTransactionPhaseResponse(
    string Name,
    string State,
    long DurationMicroseconds,
    string Detail);

internal sealed record ReloadTransactionResponse(
    string SchemaVersion,
    string TransactionId,
    string Source,
    string State,
    long FromGeneration,
    long CandidateGeneration,
    long ActiveGeneration,
    IReadOnlyList<ReloadTransactionPhaseResponse> Phases,
    string? Failure);

internal sealed record AuthoringPreviewResponse(
    string SchemaVersion,
    long BaselineRevision,
    long CandidateRevision,
    string TransactionId,
    string Description,
    bool PublishesRuntime,
    string PreviewToken,
    SemanticGraphDiffDocument SemanticDiff,
    CSharpGraphRoundTripReport RoundTrip,
    IReadOnlyList<AuthoringPreviewFile> Files,
    IReadOnlyList<UnifiedDiagnostic> Diagnostics,
    ScriptAssetDocument? ScriptAssets = null,
    IReadOnlyList<NativePublicationBindingChange>? NativeBindingChanges = null);

internal sealed record NativePublicationPrepareRequest(string PreviewToken);

internal sealed record NativePublicationFinishRequest(
    string PreviewToken,
    bool Succeeded,
    string? Error = null);

internal sealed record NativePublicationOutput(
    string Path,
    bool BaselineExists,
    string BaselineDigest,
    string CandidateDigest,
    byte[] Content);

internal sealed record NativePublicationBindingChange(
    string AssetPath,
    string AssetId,
    ScriptClassBindingDeclaration? Before,
    ScriptClassBindingDeclaration? After);

internal sealed record NativePublicationPrepareResponse(
    string SchemaVersion,
    string TransactionId,
    string PreviewToken,
    long BaselineRevision,
    long CandidateRevision,
    bool PublishesRuntime,
    string? HistoryEntryTransactionId,
    string? HistoryDirection,
    ScriptAssetDocument ScriptAssets,
    IReadOnlyList<NativePublicationBindingChange> BindingChanges,
    IReadOnlyList<NativePublicationOutput> Outputs);

internal sealed record NativePublicationFinishResponse(
    string SchemaVersion,
    string State,
    string TransactionId,
    NativePublicationPrepareResponse Prepare,
    AuthoringWorkspaceResponse? Workspace,
    string? Error);

internal sealed record AuthoringAiSemanticProposalPreviewResponse(
    string SchemaVersion,
    AiSemanticPatchProposalDocument Proposal,
    AuthoringPreviewResponse Preview);

internal sealed record AuthoringAiSemanticProposalCommitResponse(
    string SchemaVersion,
    SemanticId ProposalId,
    AuthoringWorkspaceResponse Workspace);

internal sealed record AuthoringPreviewFile(
    string Path,
    string BeforeText,
    string AfterText);

internal sealed record AuthoringGroupResponse(
    string Id,
    string Description,
    long BaselineRevision,
    int StagedOperationCount);

internal sealed record AuthoringHistoryResponse(
    bool CanUndo,
    bool CanRedo,
    int UndoDepth,
    int RedoDepth,
    string? UndoTransactionId,
    string? UndoDescription,
    string? RedoTransactionId,
    string? RedoDescription,
    string? LastOperationId,
    string PersistenceState,
    string? PersistenceMessage);


internal sealed record AuthoringQuickFixResponse(
    string Id,
    string Label,
    string Kind);

internal sealed record AuthoringRollbackResponse(
    string State,
    long Revision,
    long Generation,
    string Message);
