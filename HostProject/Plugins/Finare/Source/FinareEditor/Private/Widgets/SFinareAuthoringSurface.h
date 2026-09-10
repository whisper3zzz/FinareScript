#pragma once

#include "CoreMinimal.h"
#include "GraphEditor.h"
#include "Graph/FinareGraphLayoutStore.h"
#include "Graph/FinareNodePalette.h"
#include "FinareAuthoringSurface.h"
#include "FinareAuthoringWorkbench.h"
#include "UObject/StrongObjectPtr.h"
#include "Widgets/SCompoundWidget.h"

class SEditableTextBox;
class SGraphEditor;
class SFinareCanvasMinimap;

enum class EFinareAuthoringViewMode : uint8
{
    Graph,
    Code,
};
class SFinareDebugPanel;
class SSearchBox;
class STextBlock;
class SVerticalBox;
class FUICommandList;
class UEdGraphNode;
class UFinareAsset;
class UFinareEdGraph;
class UFinareEdGraphNode;
class FJsonObject;
struct FFinareDailyBuildResult;
template <typename ItemType> class STreeView;
template <typename ItemType> class SListView;

enum class EFinareSurfaceTreeItemKind : uint8
{
    Asset,
    Group,
    State,
    Function,
    Region,
    Action,
};

struct FFinareSurfaceTreeItem
{
    EFinareSurfaceTreeItemKind Kind = EFinareSurfaceTreeItemKind::Group;
    FString Label;
    FString Detail;
    FString SemanticId;
    FString Category;
    FString GraphKind;
    FFinareAuthoringSpan Span;
    TArray<FFinareAuthoringEdit> Edits;
    TArray<TSharedPtr<FFinareSurfaceTreeItem>> Children;
};

struct FFinareReflectedPaletteAction
{
    bool bInvocation = true;
    FFinareAuthoringParameter Receiver;
    FString MemberName;
    FString ResultTypeName;
    FString ResultTypeId;
    TArray<FFinareAuthoringParameter> Arguments;
    bool bEvent = false;
    FString EventPath;
    FString SourceTypeName;
    FString HandlerName;
    TArray<FFinareAuthoringParameter> EventParameters;
};

struct FFinarePendingAuthoringTransaction
{
    FString Payload;
    FString Url;
    FString Activity;
    FString TransactionId;
    FString GroupId;
    bool bPublishesRuntime = true;
};

using FFinareSurfaceTreeItemPtr = TSharedPtr<FFinareSurfaceTreeItem>;
using FFinareNodePaletteItemPtr = TSharedPtr<FFinareNodePaletteItem>;

struct FFinareReloadBatchResult
{
    int32 Matched = 0;
    int32 Reloaded = 0;
    int32 Failed = 0;
    int32 PreflightRejected = 0;
    int32 Prepared = 0;
    int32 PrepareRejected = 0;
    int32 Committed = 0;
    int32 CommitRejected = 0;
    int32 Aborted = 0;
    int32 FinalizeFailed = 0;
    FString ObservedReloadTransactionJson;
};

class SFinareAuthoringSurface : public SCompoundWidget
{
public:
    SLATE_BEGIN_ARGS(SFinareAuthoringSurface) {}
        SLATE_ARGUMENT(FString, InitialSurfacePath)
    SLATE_END_ARGS()

    void Construct(const FArguments& InArgs);
    virtual ~SFinareAuthoringSurface() override;
    virtual bool SupportsKeyboardFocus() const override { return true; }
    virtual FReply OnPreviewKeyDown(
        const FGeometry& MyGeometry,
        const FKeyEvent& InKeyEvent) override;

    bool LoadScriptAsset(UFinareAsset* ScriptAsset);
    bool LoadSurface(const FString& ConfiguredPath);
    bool NavigateToSemanticId(const FString& SemanticId);
    int32 GetVisibleGraphNodeCount() const;
    int32 GetVisibleGraphEdgeCount() const;
    FString GetActiveFunctionId() const;
    bool SelectFunction(const FString& FunctionId);
    bool HasNodePaletteAction(const FString& ActionId) const;
    bool GetNodePaletteAction(
        const FString& ActionId,
        FFinareGraphPaletteAction& OutAction) const;
    /**
     * Reports whether a reflected input may be omitted from an authoring call.
     * Generated node pins are authoritative when present; the reflected default
     * is retained only for legacy surfaces without generated node metadata.
     */
    static bool HasProjectableInputDefault(
        const TSharedPtr<FJsonObject>& GeneratedNode,
        const TSharedPtr<FJsonObject>& ReflectedParameter,
        const FString& ParameterName);
    bool ToggleDebugBreakpoint(const FString& ActionId);
    bool IsDebugBreakpointEnabled(const FString& ActionId) const;
    void SetViewMode(EFinareAuthoringViewMode InViewMode);
    EFinareAuthoringViewMode GetViewMode() const { return ViewMode; }
    bool HasCanonicalSourceSnapshot() const { return bHasCanonicalSourceSnapshot; }
    const FString& GetCanonicalSourceText() const { return WorkbenchSourceText; }
    const FString& GetCodeEditBuffer() const { return CodeEditBuffer; }
    bool IsCodeEditDirty() const { return bCodeEditDirty; }

    /** Executes one all-matching-instance preflight barrier before any reload. */
    static FFinareReloadBatchResult ReloadMatchingComponentsForAsset(
        const UFinareAsset* PreferredAsset,
        const FString& LoadedSurfacePath,
        const FString& ReloadTransactionJson);

    /** Reloads every exact Script Class asset currently represented by a World component. */
    static FFinareReloadBatchResult ReloadAllScriptClassComponents(
        const FString& ReloadTransactionJson);

private:
    friend class SFinareCanvasMinimap;
#if WITH_DEV_AUTOMATION_TESTS
    friend class FFinareProjectRefreshCommand;
    friend class FFinareInheritedAuthoringSurfaceTest;
#endif

    FReply HandleLoad();
    FReply HandleBrowse();
    FReply HandleLoadSelectedAsset();
    FReply HandleLoadInitialSurface();
    FReply HandleSetViewMode(EFinareAuthoringViewMode InViewMode);
    FReply HandleRefreshSourceSnapshot();
    void HandleCodeTextChanged(const FText& Text);
    FReply HandleApplyCodeSource();
    FReply HandleRevertCodeSource();
    FReply HandleOpenSource(FFinareAuthoringSpan Span);
    FReply HandleCopyId(FString SemanticId);
    FReply HandleCopyPatch(
        FFinareAuthoringEdit Edit,
        TSharedPtr<SEditableTextBox> ValueEditor);
    FReply HandleApplyPatch(
        FFinareAuthoringEdit Edit,
        TSharedPtr<SEditableTextBox> ValueEditor);
    void HandleDataRewireRequested(
        const UEdGraphPin* OutputPin,
        const UEdGraphPin* InputPin);
    void HandleControlRewireRequested(
        const UEdGraphPin* OutputPin,
        const UEdGraphPin* InputPin);
    void HandleDataDisconnectRequested(
        const UEdGraphPin* OutputPin,
        const UEdGraphPin* InputPin);
    void HandleRerouteRequested(
        UEdGraphPin* PinA,
        UEdGraphPin* PinB,
        const FVector2D& Location);
    FReply HandleAddComment();
    FReply HandleApplyDataInputValue(
        FString TargetActionId,
        FString TargetPort,
        TSharedPtr<SEditableTextBox> ValueEditor);
    FReply HandleResetDataInputValue(FString TargetActionId, FString TargetPort);
    FReply HandlePromoteDataInput(
        FString TargetActionId,
        FString TargetPort,
        TSharedPtr<SEditableTextBox> NameEditor);
    void HandleNodeCreateRequested(const FString& ActionId, const FVector2D& Location);
    void HandleStatementDeleteRequested(const UEdGraphNode* Node);
    void HandleBreakpointToggleRequested(const FString& DebugTargetId);
    void HandleDebugNodeFocused(const FString& MethodId, const FString& NodeId);
    void RefreshDebugNodeStates();
    void HandleCopySelectedStatement();
    bool CanCopySelectedStatement() const;
    void HandlePasteStatement();
    bool CanPasteStatement() const;
    void HandleUndoAuthoringCommand();
    bool CanUndoAuthoringCommand() const;
    void HandleRedoAuthoringCommand();
    bool CanRedoAuthoringCommand() const;
    FReply HandleExtractSelectedStatements();
    bool CanExtractSelectedStatements() const;
    FReply HandleCollapseSelectedStatements();
    bool CanCollapseSelectedStatements() const;
    FReply HandleExpandBlockRegion(FString RegionId);
    FReply HandleAddSwitchSection(
        FString SwitchActionId,
        TSharedPtr<SEditableTextBox> LabelEditor,
        bool bDefault);
    FReply HandleReplaceSwitchSection(
        FString CaseActionId,
        TSharedPtr<SEditableTextBox> LabelEditor);
    FReply HandleMoveSwitchSection(FString CaseActionId, int32 Direction);
    FReply HandleDeleteSwitchSection(FString CaseActionId);
    FReply HandleUndoAuthoringTransaction();
    FReply HandleRedoAuthoringTransaction();
    FReply HandleBeginAuthoringGroup();
    FReply HandleCommitAuthoringGroup();
    FReply HandleCancelAuthoringGroup();
    FReply HandleCreateScriptAsset();
    FReply HandleUpdateScriptAssetPath(
        FString AssetId,
        TSharedPtr<SEditableTextBox> PathEditor);
    FReply HandleRemoveScriptAsset(FString AssetId);
    FReply HandleAddState(FString AssetId);
    FReply HandleEditState(FString StateId);
    FReply HandleRemoveState(FString StateId);
    FReply HandleAddFunction(FString AssetId);
    FReply HandleEditFunction(FString FunctionId);
    FReply HandleRemoveFunction(FString FunctionId);
    FReply HandleAddLifecycleHook(FString AssetId, FString Hook);
    void SubmitDeclarationPatch(FString Patch, FString Activity);
    void SubmitSemanticPatch(FString Patch, FString Activity);
    void SubmitAuthoringTransaction(
        FString Payload,
        FString Url,
        FString Activity,
        FString TransactionId = FString(),
        FString GroupId = FString(),
        bool bPublishesRuntime = true);
    void DispatchAuthoringPreview();
    void DispatchAuthoringRequest(const FString& PreviewToken);
    void ConfigureAuthoringRequest(
        const TSharedRef<class IHttpRequest, ESPMode::ThreadSafe>& Request,
        const FString& Url,
        const FString& PreviewToken) const;
    void HandlePreviewResponse(bool bRequestSucceeded, int32 StatusCode, FString ResponseJson);
    void HandlePatchResponse(bool bRequestSucceeded, int32 StatusCode, FString ResponseJson);
    bool ShowDiffPreview(const FFinareAuthoringPreviewResult& Preview);
    void HandleAuthoringFailure(
        bool bRequestSucceeded,
        int32 StatusCode,
        const FString& ResponseJson,
        const FString& NetworkMessage);
    void RebuildDiagnosticsPanel();
    FReply HandleQuickFix(FString Kind);
    FReply HandleCopyDiagnostic(FFinareAuthoringDiagnostic Diagnostic);
    void RequestWorkspaceRefresh(bool bReadDisk = false, bool bHasFreshRevision = false);
    void HandleDailyBuildCompleted(const FFinareDailyBuildResult& Result);
    FDelegateHandle DailyBuildCompletedHandle;
    bool HandleWorkspaceRefreshResponse(
        bool bRequestSucceeded,
        int32 StatusCode,
        FString ResponseJson);
    void AdoptWorkspace(FFinareAuthoringWorkspaceResult Workspace);
    void ReloadMatchingComponents(
        const FString& ReloadTransactionJson,
        int32& OutMatched,
        int32& OutReloaded,
        int32& OutFailed,
        FString& OutObservedReloadTransactionJson);
    EActiveTimerReturnType HandleAutoReload(
        double CurrentTime,
        float DeltaTime);

    TSharedRef<ITableRow> GenerateTreeRow(
        FFinareSurfaceTreeItemPtr Item,
        const TSharedRef<STableViewBase>& OwnerTable) const;
    void GetTreeChildren(
        FFinareSurfaceTreeItemPtr Item,
        TArray<FFinareSurfaceTreeItemPtr>& OutChildren) const;
    void HandleSelectionChanged(
        FFinareSurfaceTreeItemPtr Item,
        ESelectInfo::Type SelectInfo);
    void HandleTreeDoubleClick(FFinareSurfaceTreeItemPtr Item);
    void HandleGraphSelectionChanged(const FGraphPanelSelectionSet& Selection);
    void HandleGraphNodeDoubleClicked(UEdGraphNode* Node);
    TSharedRef<ITableRow> GenerateNodePaletteRow(
        FFinareNodePaletteItemPtr Item,
        const TSharedRef<STableViewBase>& OwnerTable);
    void HandleNodePaletteSearchChanged(const FText& Text);
    void HandleNodePaletteSearchCommitted(const FText& Text, ETextCommit::Type CommitType);
    void HandleNodePaletteDoubleClick(FFinareNodePaletteItemPtr Item);
    FReply HandleInsertNodePaletteItem(FFinareNodePaletteItemPtr Item);
    FReply HandleToggleNodePaletteFavorite(FFinareNodePaletteItemPtr Item);

    void RebuildTree();
    void AddRegionTree(
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringRegion& Region,
        const FFinareSurfaceTreeItemPtr& Parent,
        bool bIndexItems = true);
    void RebuildGraph(const FFinareSurfaceTreeItemPtr& FunctionItem);
    UFinareEdGraphNode* AddGraphNode(
        const FFinareSurfaceTreeItemPtr& Item,
        const FFinareAuthoringAction& Action,
        const FFinareAuthoringFunction& Function,
        int32 NodePosX,
        int32 NodePosY);
    const FFinareAuthoringFunction* FindFunction(const FString& FunctionId) const;
    const FFinareAuthoringAsset* FindAsset(const FString& AssetId) const;
    const FFinareAuthoringAsset* FindOwningAsset(const FString& DeclarationId) const;
    const FFinareAuthoringState* FindState(const FString& StateId) const;
    static bool IsLifecycleFunction(const FFinareAuthoringFunction& Function);
    static bool IsEventFunction(const FFinareAuthoringFunction& Function);
    static bool ParseFunctionParameters(
        const FString& Text,
        TArray<FFinareAuthoringParameter>& OutParameters,
        FString& OutError);
    const FFinareAuthoringRegion* FindRegion(
        const FFinareAuthoringFunction& Function,
        const FString& RegionId) const;
    const FFinareAuthoringRegion* ResolveActiveBlockRegion(
        const FFinareAuthoringFunction& Function) const;
    const FFinareAuthoringAction* GetSingleSelectedStatementAction() const;
    TArray<FString> GetSelectedStatementActionIds() const;
    void RebuildNodePalette(const FFinareAuthoringFunction& Function);
    void RefreshNodePaletteItems();
    void LoadNodePalettePreferences();
    void SaveNodePalettePreferences() const;
    void LoadCanvasPreferences();
    void SaveCanvasPreferences() const;
    void RecordNodePaletteUsage(const FString& ActionId);
    FText GetNodePaletteContextText() const;
    bool PersistCurrentLayout(bool bOnlyIfChanged);
    FString BuildCanvasLayoutFingerprint() const;
    void PersistCanvasView(bool bOnlyIfChanged);
    bool RestoreCanvasView();
    FString BuildCanvasViewConfigKey() const;
    FFinareSurfaceTreeItemPtr FindSurfaceItem(const FString& SemanticId) const;
    FReply HandleFocusGraph();
    FReply HandleFocusSelection();
    TSharedRef<SWidget> BuildAdvancedNodeDetails(
        const FFinareSurfaceTreeItemPtr& Item,
        const FFinareAuthoringAction& Action);
    void RebuildInspector(const FFinareSurfaceTreeItemPtr& Item);
    void SetStatus(FString Message, bool bError);
    FString ResolveSourcePath(const FString& SourcePath) const;
    FString BuildSourcePreview(const FFinareAuthoringSpan& Span) const;
    FString BuildFunctionSignature(const FFinareAuthoringFunction& Function) const;
    static FString GetKindLabel(EFinareSurfaceTreeItemKind Kind);
    static FSlateColor GetKindColor(EFinareSurfaceTreeItemKind Kind);

    FString InitialSurfacePath;
    FString LoadedSurfacePath;
    FString LoadedLayoutPath;
    FString WorkbenchSourcePath;
    FString WorkbenchSourceText;
    FString CodeEditBuffer;
    FDateTime LoadedSurfaceTimestamp;
    bool bAutoReload = true;
    bool bReloadMatchingComponents = true;
    bool bPatchRequestPending = false;
    bool bCanUndoAuthoring = false;
    bool bCanRedoAuthoring = false;
    bool bPendingGraphFocus = false;
    bool bShowCanvasMinimap = true;
    bool bPendingRequestPublishesRuntime = true;
    bool bHasCanonicalSourceSnapshot = false;
    bool bCodeEditDirty = false;
    bool bStatusError = false;
    bool bHasAuthoringFailure = false;
    FString StatusMessage;
    TOptional<FFinarePendingAuthoringTransaction> PendingAuthoringTransaction;
    FFinareAuthoringErrorResult LastAuthoringFailure;
    FString UndoAuthoringDescription;
    FString RedoAuthoringDescription;
    FString HistoryPersistenceState;
    FString HistoryPersistenceMessage;
    FString ActiveAuthoringGroupId;
    FString ActiveAuthoringGroupDescription;
    FString VisualProfileDisplayName;
    int32 OpaqueMethodCount = 0;
    int64 ActiveAuthoringGroupStagedCount = 0;
    int64 WorkbenchRevision = 0;
    int64 UndoAuthoringDepth = 0;
    int64 RedoAuthoringDepth = 0;
    FFinareAuthoringSurfaceDocument Document;
    TArray<FFinareSurfaceTreeItemPtr> RootItems;
    TMap<FString, FFinareSurfaceTreeItemPtr> SurfaceItemsById;
    TMap<FString, TWeakObjectPtr<UFinareEdGraphNode>> GraphNodesBySemanticId;
    FString PersistedCanvasLayoutFingerprint;
    FString PersistedCanvasViewFingerprint;
    TMap<FString, FFinareReflectedPaletteAction> ReflectedPaletteActions;
    TArray<FString> FavoriteNodePaletteActionIds;
    TArray<FString> RecentNodePaletteActionIds;
    TArray<FFinareNodePaletteItemPtr> FilteredNodePaletteItems;
    FString NodePaletteSearchText;
    FString PendingNodePaletteActionId;
    FString ActiveFunctionId;
    FString ActiveBlockRegionId;
    FString ActiveFunctionSignature;
    TArray<FString> CopiedStatementIds;
    FString CopiedStatementFunctionId;
    int32 ActiveGraphActionCount = 0;
    EFinareAuthoringViewMode ViewMode = EFinareAuthoringViewMode::Graph;

    TSharedPtr<SEditableTextBox> PathTextBox;
    TSharedPtr<SEditableTextBox> ServerUrlTextBox;
    TSharedPtr<SEditableTextBox> AuthoringGroupLabelTextBox;
    TSharedPtr<SEditableTextBox> ExtractionNameTextBox;
    TSharedPtr<SSearchBox> NodePaletteSearchBox;
    TSharedPtr<STreeView<FFinareSurfaceTreeItemPtr>> TreeView;
    TSharedPtr<SListView<FFinareNodePaletteItemPtr>> NodePaletteListView;
    TSharedPtr<SGraphEditor> GraphEditor;
    TSharedPtr<SFinareDebugPanel> DebugPanel;
    TSharedPtr<FUICommandList> GraphCommands;
    TSharedPtr<SVerticalBox> InspectorBox;
    TSharedPtr<SVerticalBox> DiagnosticsBox;
    TSharedPtr<STextBlock> StatusText;
    TStrongObjectPtr<UFinareEdGraph> AuthoringGraph;
    TWeakObjectPtr<UFinareAsset> LoadedScriptAsset;
};
