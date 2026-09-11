#pragma once

#include "CoreMinimal.h"
#include "FinareAuthoringSurface.h"
#include "FinareClassBinding.h"

class UFinareAsset;
class UFinareBlueprint;

struct FFinareAuthoringHistoryResult
{
	bool bCanUndo = false;
	bool bCanRedo = false;
	int64 UndoDepth = 0;
	int64 RedoDepth = 0;
	FString UndoTransactionId;
	FString UndoDescription;
	FString RedoTransactionId;
	FString RedoDescription;
	FString LastOperationId;
	FString PersistenceState;
	FString PersistenceMessage;
};

struct FFinareAuthoringGroupResult
{
	bool bActive = false;
	FString Id;
	FString Description;
	int64 BaselineRevision = 0;
	int64 StagedOperationCount = 0;
};

struct FFinareVisualProfileResult
{
	FString SchemaVersion;
	FString DisplayName;
	FString LanguageVersion;
	int32 VisualMethodCount = 0;
	int32 OpaqueMethodCount = 0;
	int32 InvalidVisualMethodCount = 0;
	bool bOpaqueCallsProjectAsSingleNodes = false;
	bool bOpaqueBodiesGraphEditable = false;
};

struct FFinareAuthoringDiagnostic
{
	FString Code;
	FString Severity;
	FString Message;
	FFinareAuthoringSpan Span;
	FString SemanticId;
	FString GraphNodeId;

	FString ToDisplayString() const;
};

struct FFinareAuthoringQuickFix
{
	FString Id;
	FString Label;
	FString Kind;
};

struct FFinareAuthoringRollbackResult
{
	FString State;
	int64 Revision = 0;
	int64 Generation = 0;
	FString Message;

	bool IsConfirmed() const
	{
		return State == TEXT("unchanged") || State == TEXT("restored");
	}
};

struct FFinareReloadPhaseResult
{
	FString Name;
	FString State;
	int64 DurationMicroseconds = 0;
	FString Detail;
};

struct FFinareReloadTransactionResult
{
	FString TransactionId;
	FString Source;
	FString State;
	FString RequiredAction;
	int64 FromGeneration = 0;
	int64 CandidateGeneration = 0;
	int64 ActiveGeneration = 0;
	TArray<FFinareReloadPhaseResult> Phases;
	FString Failure;
	FString Json;

	bool IsValid() const { return !TransactionId.IsEmpty(); }
	FString ToDisplayString() const;
};

struct FFinareAuthoringErrorResult
{
	int32 StatusCode = 0;
	FString Code;
	FString Message;
	TArray<FFinareAuthoringDiagnostic> Diagnostics;
	TArray<FFinareAuthoringQuickFix> QuickFixes;
	FFinareAuthoringRollbackResult Rollback;
	FFinareReloadTransactionResult ReloadTransaction;

	FString ToDisplayString() const;
};

struct FFinareAuthoringPreviewFile
{
	FString Path;
	FString BeforeText;
	FString AfterText;
};

struct FFinareAuthoringSemanticChange
{
	FString FunctionName;
	FString EntityKind;
	FString Category;
	FString Facet;
	FString Summary;
};

struct FFinareAuthoringSemanticDiffSummary
{
	int64 AddedFunctions = 0;
	int64 RemovedFunctions = 0;
	int64 ModifiedFunctions = 0;
	int64 ChangeCount = 0;
};

struct FFinareAuthoringRoundTripResult
{
	int64 PreservedSemanticIds = 0;
	int64 IntroducedSemanticIds = 0;
	int64 RetiredSemanticIds = 0;
	int64 PreservedSourceComments = 0;
	int64 AddedSourceComments = 0;
	int64 RemovedSourceComments = 0;
	bool bLayoutChecked = false;
	int64 PreservedNodeLayouts = 0;
	int64 PreservedGraphComments = 0;
	int64 PreservedRoutingHints = 0;
	int64 NewUnplacedNodes = 0;
};

struct FFinareNativeBindingChange
{
	FString AssetPath;
	FString AssetId;
	TOptional<FFinareClassBindingDeclaration> Before;
	TOptional<FFinareClassBindingDeclaration> After;
};

struct FFinareAuthoringPreviewResult
{
	int64 BaselineRevision = 0;
	int64 CandidateRevision = 0;
	FString TransactionId;
	FString Description;
	FString ProposalId;
	FString ProposalTitle;
	bool bPublishesRuntime = false;
	FString PreviewToken;
	FFinareAuthoringSemanticDiffSummary SemanticSummary;
	FFinareAuthoringRoundTripResult RoundTrip;
	TArray<FFinareAuthoringSemanticChange> SemanticChanges;
	TArray<FFinareAuthoringPreviewFile> Files;
	TArray<FFinareAuthoringDiagnostic> Diagnostics;
	FString CandidateScriptAssetsJson;
	TArray<FFinareNativeBindingChange> NativeBindingChanges;
};

struct FFinareNativePublicationOutput
{
	FString Path;
	bool bBaselineExists = false;
	FString BaselineDigest;
	FString CandidateDigest;
	TArray<uint8> Content;
};

struct FFinareNativePublicationPrepareResult
{
	FString SchemaVersion;
	FString TransactionId;
	FString PreviewToken;
	int64 BaselineRevision = 0;
	int64 CandidateRevision = 0;
	bool bPublishesRuntime = false;
	FString HistoryEntryTransactionId;
	FString HistoryDirection;
	FString ScriptAssetsJson;
	TArray<FString> BindingAssetPaths;
	TArray<FString> BindingAssetIds;
	TArray<FFinareNativeBindingChange> BindingChanges;
	TArray<FFinareNativePublicationOutput> Outputs;
};

struct FFinareAuthoringWorkspaceResult
{
	int64 Revision = 0;
	int64 Generation = 0;
	FString SourcePath;
	FString SourceText;
	FString ScriptAssetsJson;
	FFinareAuthoringSurfaceDocument Surface;
	FFinareVisualProfileResult Profile;
	FFinareAuthoringHistoryResult History;
	FFinareAuthoringGroupResult Group;
	FString ReloadTransactionJson;
	FFinareReloadTransactionResult ReloadTransaction;
};

class FINAREEDITOR_API FFinareAuthoringWorkbench
{
public:
	struct FNativePublicationContext
	{
		TWeakObjectPtr<UFinareAsset> ScriptAsset;
		TWeakObjectPtr<UFinareBlueprint> Blueprint;
		TWeakObjectPtr<UClass> GeneratedClass;
		FString ScriptAssetObjectPath;
		FGuid ScriptClassId;
		FString ScriptTypeName;
		FString GeneratedClassPath;
		FString GeneratedOwnerPath;
		FString NativeParentClassPath;
		/** Accepted preview revisions captured at the Workbench boundary. */
		bool bHasExpectedPreviewRevisions = false;
		int64 ExpectedBaselineRevision = 0;
		int64 ExpectedCandidateRevision = 0;
		/** Editor Automation seam; normal Slate callers leave this disabled. */
		int32 FailAfterExternalPublicationWriteForTesting = INDEX_NONE;
	};

	static FString NormalizeServerUrl(const FString& ServerUrl);

	static FString BuildSemanticPatchUrl(const FString& ServerUrl);

	static FString BuildDeclarationPatchUrl(const FString& ServerUrl);

	static FString BuildSemanticPatchPlanUrl(const FString& ServerUrl);

	static FString BuildAiSemanticProposalUrl(const FString& ServerUrl);

	static FString BuildSourceTextUrl(const FString& ServerUrl);

	static FString BuildPreviewUrl(const FString& TransactionUrl);

	static FString BuildWorkspaceUrl(const FString& ServerUrl);

	static FString BuildRefreshUrl(const FString& ServerUrl);

	static FString BuildUndoUrl(const FString& ServerUrl);

	static FString BuildRedoUrl(const FString& ServerUrl);

	static FString BuildGroupBeginUrl(const FString& ServerUrl);

	static FString BuildGroupCommitUrl(const FString& ServerUrl);

	static FString BuildGroupCancelUrl(const FString& ServerUrl);

	static FString BuildNativePrepareUrl(const FString& ServerUrl);

	static FString BuildNativeFinishUrl(const FString& ServerUrl);

	static FString BuildNativeCancelUrl(const FString& ServerUrl);

	static bool BuildSourceTextPatch(
		const FString& SourcePath,
		const FString& BaselineSourceText,
		const FString& SourceText,
		FString& OutJson,
		FString& OutError);

	static bool ParseWorkspaceResponse(
		const FString& Json,
		FFinareAuthoringWorkspaceResult& OutWorkspace,
		FString& OutError);

	static bool ParsePreviewResponse(
		const FString& Json,
		FFinareAuthoringPreviewResult& OutPreview,
		FString& OutError);

	static bool ParseNativePublicationPrepareResponse(
		const FString& Json,
		FFinareNativePublicationPrepareResult& OutPrepare,
		FString& OutError);

	/**
	 * Applies a prepared native candidate and its UE/history sidecars as one
	 * materializer transaction. The surface supplies the exact selected owner
	 * context captured before the preview; this coordinator revalidates it.
	 */
	static bool ApplyNativePublication(
		const FNativePublicationContext& Context,
		const FFinareNativePublicationPrepareResult& Prepare,
		const FString& ExpectedPreviewToken,
		FString& OutError);

	/**
	 * Confirms that a failed local materialization returned to the prepared
	 * disk state and that no recovery receipt or committed marker remains.
	 * Only after this proof may the Workbench release the server reservation.
	 */
	static bool CanCancelNativePublication(
		const FFinareNativePublicationPrepareResult& Prepare,
		FString& OutError);

	/**
	 * Resolves the durable completion marker after the shared materializer
	 * journal has closed. A pending receipt always takes precedence over a
	 * marker, so an interrupted transaction cannot be adopted as committed.
	 */
	static bool ValidateNativeCompletionMarker(
		const FNativePublicationContext& Context,
		const FFinareNativePublicationPrepareResult& Prepare,
		FString& OutError);

	static bool ParseReloadTransaction(
		const FString& Json,
		FFinareReloadTransactionResult& OutTransaction,
		FString& OutError);

	static bool ParseErrorResponse(
		const FString& Json,
		int32 StatusCode,
		FFinareAuthoringErrorResult& OutFailure,
		FString& OutParseError);

	static FString ParseErrorResponse(const FString& Json, int32 StatusCode);
};
