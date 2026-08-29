#pragma once

#include "CoreMinimal.h"
#include "UnifiedScriptAuthoringSurface.h"
#include "UnifiedScriptDebugWorkbench.h"
#include "UnifiedScriptRuntimeDiagnostic.h"
#include "Widgets/SCompoundWidget.h"

class SEditableTextBox;
class STextBlock;
template <typename ItemType> class SListView;

DECLARE_DELEGATE_TwoParams(
    FOnUnifiedScriptDebugNodeFocused,
    const FString&,
    const FString&);

class SUnifiedScriptDebugPanel final : public SCompoundWidget
{
public:
    SLATE_BEGIN_ARGS(SUnifiedScriptDebugPanel) {}
        SLATE_ATTRIBUTE(FString, ServerUrl)
        SLATE_EVENT(FOnUnifiedScriptDebugNodeFocused, OnNodeFocused)
        SLATE_EVENT(FSimpleDelegate, OnDebugStateChanged)
    SLATE_END_ARGS()

    void Construct(const FArguments& InArgs);

    void SetExecutionContext(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FString& FunctionId);

    bool ToggleBreakpoint(const FString& DebugTargetId);
    bool IsBreakpointEnabled(const FString& DebugTargetId) const;
    FString GetBreakpointState(const FString& DebugTargetId) const;
    bool IsPausedNode(const FString& DebugTargetId) const;
    bool IsRuntimeFailureNode(const FString& DebugTargetId) const;
    int32 GetBreakpointCount() const { return BreakpointNodeIds.Num(); }

private:
    enum class ERequestKind : uint8
    {
        Start,
        Wait,
        Pause,
        Resume,
        Evaluate,
        PublishLiveWorld,
    };

    using FDebugFrameItem = TSharedPtr<FUnifiedScriptDebugStackFrame>;
    using FDebugValueItem = TSharedPtr<FUnifiedScriptDebugValue>;
    using FProfileNodeItem = TSharedPtr<FUnifiedScriptGameplayProfileNode>;

    FReply HandleRun();
    FReply HandlePause();
    FReply HandleContinue();
    FReply HandleStepIn();
    FReply HandleStepOver();
    FReply HandleStepOut();
    FReply HandleEvaluate();
    FReply HandlePauseLiveWorld();
    FReply HandleStepLiveWorld();
    FReply HandleContinueLiveWorld();
    FReply HandleToggleGameplayProfiler();
    FReply HandleCaptureGameplayProfile();
    FReply HandleFocusHottestProfileNode();
    FReply HandleSaveGameplayProfile();
    FReply HandleFocusRuntimeFailure();
    FReply HandleAttachSemanticWorld();
    FReply HandleDetachSemanticWorld();
    void HandleFrameSelectionChanged(FDebugFrameItem Item, ESelectInfo::Type SelectInfo);
    TSharedRef<ITableRow> GenerateFrameRow(
        FDebugFrameItem Item,
        const TSharedRef<STableViewBase>& OwnerTable) const;
    TSharedRef<ITableRow> GenerateValueRow(
        FDebugValueItem Item,
        const TSharedRef<STableViewBase>& OwnerTable) const;
    TSharedRef<ITableRow> GenerateProfileNodeRow(
        FProfileNodeItem Item,
        const TSharedRef<STableViewBase>& OwnerTable) const;
    void HandleProfileNodeSelectionChanged(
        FProfileNodeItem Item,
        ESelectInfo::Type SelectInfo);

    void DispatchSessionRequest(ERequestKind Kind, const FString& Url, const FString& Json);
    void DispatchWait();
    void HandleSessionResponse(
        ERequestKind Kind,
        bool bSucceeded,
        int32 StatusCode,
        FString ResponseJson);
    void HandleEvaluationResponse(bool bSucceeded, int32 StatusCode, FString ResponseJson);
    void HandleLiveWorldPublishResponse(
        bool bSucceeded,
        int32 StatusCode,
        FString ResponseJson);
    bool ControlLiveSemantic(EUnifiedScriptSemanticDebugControl Control);
    bool EvaluateLiveSemanticWatch(const FString& Expression, FString& OutResult) const;
    void AdoptSession(FUnifiedScriptDebugSession NewSession);
    void SelectFrame(int64 FrameId);
    void RebuildFrameAndLocalViews();
    void RebuildProfileNodeView();
    void SetDebugStatus(FString Message, bool bError = false);
    FString GetServerUrl() const;
    bool HasActiveSession() const;
    bool CanRun() const;
    bool CanPause() const;
    bool CanResume() const;
    bool CanStepOut() const;
    bool CanEvaluate() const;
    FString DescribeHttpFailure(int32 StatusCode, const FString& ResponseJson) const;
    EActiveTimerReturnType RefreshLiveWorldTarget(double CurrentTime, float DeltaTime);
    void RefreshLiveWorldTargetNow();
    void MapRuntimeDiagnosticToGraph();
    bool CanPauseLiveWorld() const;
    bool CanStepLiveWorld() const;
    bool CanContinueLiveWorld() const;
    bool CanControlGameplayProfiler() const;
    bool CanCaptureGameplayProfile() const;
    bool CanFocusHottestProfileNode() const;
    bool CanSaveGameplayProfile() const;
    bool CanFocusRuntimeFailure() const;
    bool CanAttachSemanticWorld() const;
    bool CanDetachSemanticWorld() const;
    bool IsLiveSemanticSession() const;
    void AdoptLiveSemanticState();
    FString DescribeLiveWorldTarget() const;
    FString DescribeRuntimeDiagnostic() const;
    FString DescribeGameplayProfile() const;
    const FUnifiedScriptGameplayProfileNode* FindHottestProfileNode() const;

    TAttribute<FString> ServerUrlAttribute;
    FOnUnifiedScriptDebugNodeFocused OnNodeFocused;
    FSimpleDelegate OnDebugStateChanged;
    FString SourceCompilationId;
    FString SemanticConfigurationId;
    TOptional<FUnifiedScriptAuthoringFunction> ActiveFunction;
    TOptional<FUnifiedScriptAuthoringAsset> ActiveAsset;
    TWeakObjectPtr<class UUnifiedScriptComponent> LiveComponent;
    TOptional<FUnifiedScriptRuntimeDebugContext> LiveContext;
    FString RuntimeFailureMethodId;
    FString RuntimeFailureNodeId;
    int64 LastRuntimeDiagnosticSequence = 0;
    FString LiveRuntimeStatus = TEXT("No matching PIE/Game World script is running.");
    TOptional<FUnifiedScriptGameplayProfileResult> GameplayProfile;
    FString GameplayProfileJson;
    FString GameplayProfileStatus;
    TSet<FString> BreakpointNodeIds;
    TOptional<FUnifiedScriptDebugSession> Session;
    bool bSessionIsLiveWorld = false;
    FString LastSemanticDebugStateJson;
    int64 SelectedFrameId = 0;
    bool bCommandPending = false;
    bool bWaitPending = false;
    bool bDebugStatusError = false;
    FString DebugStatus = TEXT("Select a Visual function to start a managed debug session.");
    FString EvaluationText = TEXT("Read-only watches use local.field[0] syntax.");
    FString PendingResumeLabel;

    TArray<FDebugFrameItem> FrameItems;
    TArray<FDebugValueItem> LocalItems;
    TArray<FProfileNodeItem> ProfileNodeItems;
    TSharedPtr<SListView<FDebugFrameItem>> FrameListView;
    TSharedPtr<SListView<FDebugValueItem>> LocalListView;
    TSharedPtr<SListView<FProfileNodeItem>> ProfileNodeListView;
    TSharedPtr<SEditableTextBox> WatchTextBox;
    TSharedPtr<STextBlock> DebugStatusText;
    TSharedPtr<STextBlock> EvaluationTextBlock;
};
