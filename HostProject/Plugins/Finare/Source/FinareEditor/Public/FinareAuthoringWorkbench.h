/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 18:41:46
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 14:22:40
 * @FilePath: \Plugins\Finare\Source\FinareEditor\Public\FinareAuthoringWorkbench.h
 * @Description:
 */
#pragma once

#include "CoreMinimal.h"
#include "FinareAuthoringSurface.h"

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
};

struct FFinareAuthoringWorkspaceResult
{
    int64 Revision = 0;
    int64 Generation = 0;
    FString SourcePath;
    FString SourceText;
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
