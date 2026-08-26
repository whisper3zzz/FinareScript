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
    void HandleDataDisconnectRequested(
        const UEdGraphPin* OutputPin,
        const UEdGraphPin* InputPin);
    void HandleNodeCreateRequested(const FString& ActionId, const FVector2D& Location);
    void HandleStatementDeleteRequested(const UEdGraphNode* Node);
    void SubmitSemanticPatch(FString Patch, FString Activity);
    void SubmitAuthoringTransaction(FString Payload, FString Url, FString Activity);
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
    bool bPendingGraphFocus = false;
    bool bStatusError = false;
    FString StatusMessage;
    FUnifiedScriptAuthoringSurfaceDocument Document;
    TArray<FUnifiedScriptSurfaceTreeItemPtr> RootItems;
    TMap<FString, FUnifiedScriptSurfaceTreeItemPtr> SurfaceItemsById;
    TMap<FString, TWeakObjectPtr<UUnifiedScriptEdGraphNode>> GraphNodesBySemanticId;
    TMap<FString, FVector2D> PersistedNodePositions;
    TMap<FString, FUnifiedScriptReflectedPaletteAction> ReflectedPaletteActions;
    FString ActiveFunctionId;
    FString ActiveBlockRegionId;
    FString ActiveFunctionSignature;
    int32 ActiveGraphActionCount = 0;

    TSharedPtr<SEditableTextBox> PathTextBox;
    TSharedPtr<SEditableTextBox> ServerUrlTextBox;
    TSharedPtr<STreeView<FUnifiedScriptSurfaceTreeItemPtr>> TreeView;
    TSharedPtr<SGraphEditor> GraphEditor;
    TSharedPtr<SVerticalBox> InspectorBox;
    TSharedPtr<STextBlock> StatusText;
    TStrongObjectPtr<UUnifiedScriptEdGraph> AuthoringGraph;
    TWeakObjectPtr<UUnifiedScriptAsset> LoadedScriptAsset;
};
