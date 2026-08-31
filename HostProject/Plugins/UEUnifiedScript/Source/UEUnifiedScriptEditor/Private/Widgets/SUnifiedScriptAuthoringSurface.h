#pragma once

#include "CoreMinimal.h"
#include "GraphEditor.h"
#include "Graph/UnifiedScriptGraphLayoutStore.h"
#include "Graph/UnifiedScriptNodePalette.h"
#include "UnifiedScriptAuthoringSurface.h"
#include "UnifiedScriptAuthoringWorkbench.h"
#include "UObject/StrongObjectPtr.h"
#include "Widgets/SCompoundWidget.h"

class SEditableTextBox;
class SGraphEditor;
class SUnifiedScriptCanvasMinimap;
class SUnifiedScriptDebugPanel;
class SSearchBox;
class STextBlock;
class SVerticalBox;
class FUICommandList;
class UEdGraphNode;
class UUnifiedScriptAsset;
class UUnifiedScriptEdGraph;
class UUnifiedScriptEdGraphNode;
template <typename ItemType> class STreeView;
template <typename ItemType> class SListView;

enum class EUnifiedScriptSurfaceTreeItemKind : uint8
{
    Asset,
    Group,
    State,
    Function,
    Region,
    Action,
};

struct FUnifiedScriptSurfaceTreeItem
{
    EUnifiedScriptSurfaceTreeItemKind Kind = EUnifiedScriptSurfaceTreeItemKind::Group;
    FString Label;
    FString Detail;
    FString SemanticId;
    FString Category;
    FString GraphKind;
    FUnifiedScriptAuthoringSpan Span;
    TArray<FUnifiedScriptAuthoringEdit> Edits;
    TArray<TSharedPtr<FUnifiedScriptSurfaceTreeItem>> Children;
};

struct FUnifiedScriptReflectedPaletteAction
{
    bool bInvocation = true;
    FUnifiedScriptAuthoringParameter Receiver;
    FString MemberName;
    FString ResultTypeName;
    FString ResultTypeId;
    TArray<FUnifiedScriptAuthoringParameter> Arguments;
    bool bEvent = false;
    FString EventPath;
    FString SourceTypeName;
    FString HandlerName;
    TArray<FUnifiedScriptAuthoringParameter> EventParameters;
};

struct FUnifiedScriptPendingAuthoringTransaction
{
    FString Payload;
    FString Url;
    FString Activity;
    FString TransactionId;
    FString GroupId;
    bool bPublishesRuntime = true;
};

using FUnifiedScriptSurfaceTreeItemPtr = TSharedPtr<FUnifiedScriptSurfaceTreeItem>;
using FUnifiedScriptNodePaletteItemPtr = TSharedPtr<FUnifiedScriptNodePaletteItem>;

class SUnifiedScriptAuthoringSurface : public SCompoundWidget
{
public:
    SLATE_BEGIN_ARGS(SUnifiedScriptAuthoringSurface) {}
        SLATE_ARGUMENT(FString, InitialSurfacePath)
    SLATE_END_ARGS()

    void Construct(const FArguments& InArgs);
    virtual ~SUnifiedScriptAuthoringSurface() override;
    virtual bool SupportsKeyboardFocus() const override { return true; }
    virtual FReply OnPreviewKeyDown(
        const FGeometry& MyGeometry,
        const FKeyEvent& InKeyEvent) override;

    bool LoadScriptAsset(UUnifiedScriptAsset* ScriptAsset);
    bool LoadSurface(const FString& ConfiguredPath);
    int32 GetVisibleGraphNodeCount() const;
    int32 GetVisibleGraphEdgeCount() const;
    FString GetActiveFunctionId() const;
    bool SelectFunction(const FString& FunctionId);
    bool HasNodePaletteAction(const FString& ActionId) const;
    bool GetNodePaletteAction(
        const FString& ActionId,
        FUnifiedScriptGraphPaletteAction& OutAction) const;
    bool ToggleDebugBreakpoint(const FString& ActionId);
    bool IsDebugBreakpointEnabled(const FString& ActionId) const;

private:
    friend class SUnifiedScriptCanvasMinimap;

    FReply HandleLoad();
    FReply HandleBrowse();
    FReply HandleLoadSelectedAsset();
    FReply HandleLoadInitialSurface();
    FReply HandleOpenSource(FUnifiedScriptAuthoringSpan Span);
    FReply HandleCopyId(FString SemanticId);
    FReply HandleCopyPatch(
        FUnifiedScriptAuthoringEdit Edit,
        TSharedPtr<SEditableTextBox> ValueEditor);
    FReply HandleApplyPatch(
        FUnifiedScriptAuthoringEdit Edit,
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
    bool ShowDiffPreview(const FUnifiedScriptAuthoringPreviewResult& Preview);
    void HandleAuthoringFailure(
        bool bRequestSucceeded,
        int32 StatusCode,
        const FString& ResponseJson,
        const FString& NetworkMessage);
    void RebuildDiagnosticsPanel();
    FReply HandleQuickFix(FString Kind);
    FReply HandleCopyDiagnostic(FUnifiedScriptAuthoringDiagnostic Diagnostic);
    void RequestWorkspaceRefresh();
    void HandleWorkspaceRefreshResponse(
        bool bRequestSucceeded,
        int32 StatusCode,
        FString ResponseJson);
    void AdoptWorkspace(FUnifiedScriptAuthoringWorkspaceResult Workspace);
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
        FUnifiedScriptSurfaceTreeItemPtr Item,
        const TSharedRef<STableViewBase>& OwnerTable) const;
    void GetTreeChildren(
        FUnifiedScriptSurfaceTreeItemPtr Item,
        TArray<FUnifiedScriptSurfaceTreeItemPtr>& OutChildren) const;
    void HandleSelectionChanged(
        FUnifiedScriptSurfaceTreeItemPtr Item,
        ESelectInfo::Type SelectInfo);
    void HandleTreeDoubleClick(FUnifiedScriptSurfaceTreeItemPtr Item);
    void HandleGraphSelectionChanged(const FGraphPanelSelectionSet& Selection);
    void HandleGraphNodeDoubleClicked(UEdGraphNode* Node);
    TSharedRef<ITableRow> GenerateNodePaletteRow(
        FUnifiedScriptNodePaletteItemPtr Item,
        const TSharedRef<STableViewBase>& OwnerTable);
    void HandleNodePaletteSearchChanged(const FText& Text);
    void HandleNodePaletteSearchCommitted(const FText& Text, ETextCommit::Type CommitType);
    void HandleNodePaletteDoubleClick(FUnifiedScriptNodePaletteItemPtr Item);
    FReply HandleInsertNodePaletteItem(FUnifiedScriptNodePaletteItemPtr Item);
    FReply HandleToggleNodePaletteFavorite(FUnifiedScriptNodePaletteItemPtr Item);

    void RebuildTree();
    void AddRegionTree(
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringRegion& Region,
        const FUnifiedScriptSurfaceTreeItemPtr& Parent);
    void RebuildGraph(const FUnifiedScriptSurfaceTreeItemPtr& FunctionItem);
    UUnifiedScriptEdGraphNode* AddGraphNode(
        const FUnifiedScriptSurfaceTreeItemPtr& Item,
        const FUnifiedScriptAuthoringAction& Action,
        const FUnifiedScriptAuthoringFunction& Function,
        int32 NodePosX,
        int32 NodePosY);
    const FUnifiedScriptAuthoringFunction* FindFunction(const FString& FunctionId) const;
    const FUnifiedScriptAuthoringAsset* FindAsset(const FString& AssetId) const;
    const FUnifiedScriptAuthoringAsset* FindOwningAsset(const FString& DeclarationId) const;
    const FUnifiedScriptAuthoringState* FindState(const FString& StateId) const;
    static bool IsLifecycleFunction(const FUnifiedScriptAuthoringFunction& Function);
    static bool IsEventFunction(const FUnifiedScriptAuthoringFunction& Function);
    static bool ParseFunctionParameters(
        const FString& Text,
        TArray<FUnifiedScriptAuthoringParameter>& OutParameters,
        FString& OutError);
    const FUnifiedScriptAuthoringRegion* FindRegion(
        const FUnifiedScriptAuthoringFunction& Function,
        const FString& RegionId) const;
    const FUnifiedScriptAuthoringRegion* ResolveActiveBlockRegion(
        const FUnifiedScriptAuthoringFunction& Function) const;
    const FUnifiedScriptAuthoringAction* GetSingleSelectedStatementAction() const;
    TArray<FString> GetSelectedStatementActionIds() const;
    void RebuildNodePalette(const FUnifiedScriptAuthoringFunction& Function);
    void RefreshNodePaletteItems();
    void LoadNodePalettePreferences();
    void SaveNodePalettePreferences() const;
    void LoadCanvasPreferences();
    void SaveCanvasPreferences() const;
    void RecordNodePaletteUsage(const FString& ActionId);
    FText GetNodePaletteContextText() const;
    void PersistCurrentLayout(bool bOnlyIfChanged);
    FString BuildCanvasLayoutFingerprint() const;
    void PersistCanvasView(bool bOnlyIfChanged);
    bool RestoreCanvasView();
    FString BuildCanvasViewConfigKey() const;
    FUnifiedScriptSurfaceTreeItemPtr FindSurfaceItem(const FString& SemanticId) const;
    FReply HandleFocusGraph();
    FReply HandleFocusSelection();
    TSharedRef<SWidget> BuildAdvancedNodeDetails(
        const FUnifiedScriptSurfaceTreeItemPtr& Item,
        const FUnifiedScriptAuthoringAction& Action);
    void RebuildInspector(const FUnifiedScriptSurfaceTreeItemPtr& Item);
    void SetStatus(FString Message, bool bError);
    FString ResolveSourcePath(const FString& SourcePath) const;
    FString BuildSourcePreview(const FUnifiedScriptAuthoringSpan& Span) const;
    FString BuildFunctionSignature(const FUnifiedScriptAuthoringFunction& Function) const;
    static FString GetKindLabel(EUnifiedScriptSurfaceTreeItemKind Kind);
    static FSlateColor GetKindColor(EUnifiedScriptSurfaceTreeItemKind Kind);

    FString InitialSurfacePath;
    FString LoadedSurfacePath;
    FString LoadedLayoutPath;
    FString WorkbenchSourcePath;
    FDateTime LoadedSurfaceTimestamp;
    bool bAutoReload = true;
    bool bReloadMatchingComponents = true;
    bool bPatchRequestPending = false;
    bool bCanUndoAuthoring = false;
    bool bCanRedoAuthoring = false;
    bool bPendingGraphFocus = false;
    bool bShowCanvasMinimap = true;
    bool bPendingRequestPublishesRuntime = true;
    bool bStatusError = false;
    bool bHasAuthoringFailure = false;
    FString StatusMessage;
    TOptional<FUnifiedScriptPendingAuthoringTransaction> PendingAuthoringTransaction;
    FUnifiedScriptAuthoringErrorResult LastAuthoringFailure;
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
    FUnifiedScriptAuthoringSurfaceDocument Document;
    TArray<FUnifiedScriptSurfaceTreeItemPtr> RootItems;
    TMap<FString, FUnifiedScriptSurfaceTreeItemPtr> SurfaceItemsById;
    TMap<FString, TWeakObjectPtr<UUnifiedScriptEdGraphNode>> GraphNodesBySemanticId;
    FString PersistedCanvasLayoutFingerprint;
    FString PersistedCanvasViewFingerprint;
    TMap<FString, FUnifiedScriptReflectedPaletteAction> ReflectedPaletteActions;
    TArray<FString> FavoriteNodePaletteActionIds;
    TArray<FString> RecentNodePaletteActionIds;
    TArray<FUnifiedScriptNodePaletteItemPtr> FilteredNodePaletteItems;
    FString NodePaletteSearchText;
    FString PendingNodePaletteActionId;
    FString ActiveFunctionId;
    FString ActiveBlockRegionId;
    FString ActiveFunctionSignature;
    TArray<FString> CopiedStatementIds;
    FString CopiedStatementFunctionId;
    int32 ActiveGraphActionCount = 0;

    TSharedPtr<SEditableTextBox> PathTextBox;
    TSharedPtr<SEditableTextBox> ServerUrlTextBox;
    TSharedPtr<SEditableTextBox> AuthoringGroupLabelTextBox;
    TSharedPtr<SEditableTextBox> ExtractionNameTextBox;
    TSharedPtr<SSearchBox> NodePaletteSearchBox;
    TSharedPtr<STreeView<FUnifiedScriptSurfaceTreeItemPtr>> TreeView;
    TSharedPtr<SListView<FUnifiedScriptNodePaletteItemPtr>> NodePaletteListView;
    TSharedPtr<SGraphEditor> GraphEditor;
    TSharedPtr<SUnifiedScriptDebugPanel> DebugPanel;
    TSharedPtr<FUICommandList> GraphCommands;
    TSharedPtr<SVerticalBox> InspectorBox;
    TSharedPtr<SVerticalBox> DiagnosticsBox;
    TSharedPtr<STextBlock> StatusText;
    TStrongObjectPtr<UUnifiedScriptEdGraph> AuthoringGraph;
    TWeakObjectPtr<UUnifiedScriptAsset> LoadedScriptAsset;
};
