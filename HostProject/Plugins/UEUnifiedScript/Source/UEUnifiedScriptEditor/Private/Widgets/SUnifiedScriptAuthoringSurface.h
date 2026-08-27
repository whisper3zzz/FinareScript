#pragma once

#include "CoreMinimal.h"
#include "GraphEditor.h"
#include "UnifiedScriptAuthoringSurface.h"
#include "UObject/StrongObjectPtr.h"
#include "Widgets/SCompoundWidget.h"

class SEditableTextBox;
class SGraphEditor;
class STextBlock;
class SVerticalBox;
class FUICommandList;
class UEdGraphNode;
class UUnifiedScriptAsset;
class UUnifiedScriptEdGraph;
class UUnifiedScriptEdGraphNode;
template <typename ItemType> class STreeView;

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

using FUnifiedScriptSurfaceTreeItemPtr = TSharedPtr<FUnifiedScriptSurfaceTreeItem>;

class SUnifiedScriptAuthoringSurface : public SCompoundWidget
{
public:
    SLATE_BEGIN_ARGS(SUnifiedScriptAuthoringSurface) {}
        SLATE_ARGUMENT(FString, InitialSurfacePath)
    SLATE_END_ARGS()

    void Construct(const FArguments& InArgs);
    virtual ~SUnifiedScriptAuthoringSurface() override;

    bool LoadSurface(const FString& ConfiguredPath);
    int32 GetVisibleGraphNodeCount() const;
    int32 GetVisibleGraphEdgeCount() const;
    FString GetActiveFunctionId() const;
    bool HasNodePaletteAction(const FString& ActionId) const;

private:
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
    void HandleNodeCreateRequested(const FString& ActionId, const FVector2D& Location);
    void HandleStatementDeleteRequested(const UEdGraphNode* Node);
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
    void SubmitSemanticPatch(FString Patch, FString Activity);
    void SubmitAuthoringTransaction(
        FString Payload,
        FString Url,
        FString Activity,
        FString TransactionId = FString(),
        FString GroupId = FString(),
        bool bPublishesRuntime = true);
    void HandlePatchResponse(bool bRequestSucceeded, int32 StatusCode, FString ResponseJson);
    void ReloadMatchingComponents(int32& OutMatched, int32& OutReloaded, int32& OutFailed);
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
    const FUnifiedScriptAuthoringRegion* FindRegion(
        const FUnifiedScriptAuthoringFunction& Function,
        const FString& RegionId) const;
    const FUnifiedScriptAuthoringRegion* ResolveActiveBlockRegion(
        const FUnifiedScriptAuthoringFunction& Function) const;
    const FUnifiedScriptAuthoringAction* GetSingleSelectedStatementAction() const;
    TArray<FString> GetSelectedStatementActionIds() const;
    void RebuildNodePalette(const FUnifiedScriptAuthoringFunction& Function);
    void PersistCurrentLayout(bool bOnlyIfChanged);
    FUnifiedScriptSurfaceTreeItemPtr FindSurfaceItem(const FString& SemanticId) const;
    FReply HandleFocusGraph();
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
    FDateTime LoadedSurfaceTimestamp;
    bool bAutoReload = true;
    bool bReloadMatchingComponents = true;
    bool bPatchRequestPending = false;
    bool bCanUndoAuthoring = false;
    bool bCanRedoAuthoring = false;
    bool bPendingGraphFocus = false;
    bool bPendingRequestPublishesRuntime = true;
    bool bStatusError = false;
    FString StatusMessage;
    FString UndoAuthoringDescription;
    FString RedoAuthoringDescription;
    FString HistoryPersistenceState;
    FString HistoryPersistenceMessage;
    FString ActiveAuthoringGroupId;
    FString ActiveAuthoringGroupDescription;
    int64 ActiveAuthoringGroupStagedCount = 0;
    int64 WorkbenchRevision = 0;
    int64 UndoAuthoringDepth = 0;
    int64 RedoAuthoringDepth = 0;
    FUnifiedScriptAuthoringSurfaceDocument Document;
    TArray<FUnifiedScriptSurfaceTreeItemPtr> RootItems;
    TMap<FString, FUnifiedScriptSurfaceTreeItemPtr> SurfaceItemsById;
    TMap<FString, TWeakObjectPtr<UUnifiedScriptEdGraphNode>> GraphNodesBySemanticId;
    TMap<FString, FVector2D> PersistedNodePositions;
    TMap<FString, FUnifiedScriptReflectedPaletteAction> ReflectedPaletteActions;
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
    TSharedPtr<STreeView<FUnifiedScriptSurfaceTreeItemPtr>> TreeView;
    TSharedPtr<SGraphEditor> GraphEditor;
    TSharedPtr<FUICommandList> GraphCommands;
    TSharedPtr<SVerticalBox> InspectorBox;
    TSharedPtr<STextBlock> StatusText;
    TStrongObjectPtr<UUnifiedScriptEdGraph> AuthoringGraph;
    TWeakObjectPtr<UUnifiedScriptAsset> LoadedScriptAsset;
};
