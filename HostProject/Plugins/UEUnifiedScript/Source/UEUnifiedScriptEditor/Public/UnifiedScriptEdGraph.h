/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 19:06:51
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 10:33:15
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptEditor\Public\UnifiedScriptEdGraph.h
 * @Description:
 */
#pragma once

#include "CoreMinimal.h"
#include "EdGraph/EdGraph.h"
#include "EdGraph/EdGraphNode.h"
#include "EdGraph/EdGraphSchema.h"
#include "EdGraphNode_Comment.h"
#include "UnifiedScriptEdGraph.generated.h"

DECLARE_DELEGATE_TwoParams(
    FOnUnifiedScriptDataRewireRequested,
    const UEdGraphPin*,
    const UEdGraphPin*);
DECLARE_DELEGATE_TwoParams(
    FOnUnifiedScriptControlRewireRequested,
    const UEdGraphPin*,
    const UEdGraphPin*);
DECLARE_DELEGATE_TwoParams(
    FOnUnifiedScriptNodeCreateRequested,
    const FString&,
    const FVector2D&);
DECLARE_DELEGATE_OneParam(
    FOnUnifiedScriptStatementDeleteRequested,
    const UEdGraphNode*);
DECLARE_DELEGATE_TwoParams(
    FOnUnifiedScriptDataDisconnectRequested,
    const UEdGraphPin*,
    const UEdGraphPin*);
DECLARE_DELEGATE_ThreeParams(
    FOnUnifiedScriptRerouteRequested,
    UEdGraphPin*,
    UEdGraphPin*,
    const FVector2D&);

struct FUnifiedScriptGraphPaletteAction
{
    FString ActionId;
    FString Category;
    FString Title;
    FString Tooltip;
    FString Keywords;
    bool bRequiresBlock = true;
};

/** Transient graph that forwards authored data-wire changes to the Workbench transaction. */
UCLASS(Transient)
class UEUNIFIEDSCRIPTEDITOR_API UUnifiedScriptEdGraph final : public UEdGraph
{
    GENERATED_BODY()

public:
    FOnUnifiedScriptDataRewireRequested OnDataRewireRequested;
    FOnUnifiedScriptControlRewireRequested OnControlRewireRequested;
    FOnUnifiedScriptNodeCreateRequested OnNodeCreateRequested;
    FOnUnifiedScriptStatementDeleteRequested OnStatementDeleteRequested;
    FOnUnifiedScriptDataDisconnectRequested OnDataDisconnectRequested;
    FOnUnifiedScriptRerouteRequested OnRerouteRequested;
    TArray<FUnifiedScriptGraphPaletteAction> PaletteActions;
    FString ActiveBlockRegionId;
};

/** Layout-only comment persisted in .usgraph; it never owns program semantics. */
UCLASS(Transient)
class UEUNIFIEDSCRIPTEDITOR_API UUnifiedScriptEdGraphCommentNode final
    : public UEdGraphNode_Comment
{
    GENERATED_BODY()

public:
    UPROPERTY(Transient)
    FString LayoutId;

    UPROPERTY(Transient)
    bool bLayoutCollapsed = false;
};

/** Layout-only control point for one compiler-owned semantic edge. */
UCLASS(Transient)
class UEUNIFIEDSCRIPTEDITOR_API UUnifiedScriptEdGraphRerouteNode final
    : public UEdGraphNode
{
    GENERATED_BODY()

public:
    UPROPERTY(Transient)
    FString EdgeId;

    UPROPERTY(Transient)
    int32 PointIndex = 0;

    UPROPERTY(Transient)
    FName RoutedPinCategory;

    UPROPERTY(Transient)
    FName RoutedPinSubCategory;

    virtual void AllocateDefaultPins() override;
    virtual bool ShouldDrawNodeAsControlPointOnly(
        int32& OutInputPinIndex,
        int32& OutOutputPinIndex) const override;
    virtual bool CanDuplicateNode() const override { return false; }
    virtual bool CanUserDeleteNode() const override { return true; }
    virtual void DestroyNode() override;
};

/** Transient presentation node backed by an Authoring Surface semantic item. */
UCLASS(Transient)
class UEUNIFIEDSCRIPTEDITOR_API UUnifiedScriptEdGraphNode final : public UEdGraphNode
{
    GENERATED_BODY()

public:
    UPROPERTY(Transient)
    FString SemanticId;

    UPROPERTY(Transient)
    FString DisplayTitle;

    UPROPERTY(Transient)
    FString Summary;

    UPROPERTY(Transient)
    FString NodeKind;

    UPROPERTY(Transient)
    FString Category;

    UPROPERTY(Transient)
    FString SourceDisplay;

    UPROPERTY(Transient)
    bool bShowSummary = false;

    UPROPERTY(Transient)
    bool bCompactPresentation = false;

    UPROPERTY(Transient)
    bool bCanDelete = false;

    virtual void AllocateDefaultPins() override;
    virtual FText GetNodeTitle(ENodeTitleType::Type TitleType) const override;
    virtual FLinearColor GetNodeTitleColor() const override;
    virtual FText GetTooltipText() const override;
    virtual bool CanDuplicateNode() const override;
    virtual bool CanUserDeleteNode() const override;
    virtual void DestroyNode() override;
};

/** Compiler-owned semantic schema for Surface v0.4 pins and edges. */
UCLASS(Transient)
class UEUNIFIEDSCRIPTEDITOR_API UUnifiedScriptEdGraphSchema final : public UEdGraphSchema
{
    GENERATED_BODY()

public:
    virtual void GetGraphContextActions(FGraphContextMenuBuilder& ContextMenuBuilder) const override;
    virtual const FPinConnectionResponse CanCreateConnection(
        const UEdGraphPin* A,
        const UEdGraphPin* B) const override;
    virtual bool TryCreateConnection(UEdGraphPin* A, UEdGraphPin* B) const override;
    virtual void OnPinConnectionDoubleCicked(
        UEdGraphPin* PinA,
        UEdGraphPin* PinB,
        const FVector2f& GraphPosition) const override;
    virtual void BreakNodeLinks(UEdGraphNode& TargetNode) const override;
    virtual void BreakPinLinks(
        UEdGraphPin& TargetPin,
        bool bSendsNodeNotification) const override;
    virtual void BreakSinglePinLink(
        UEdGraphPin* SourcePin,
        UEdGraphPin* TargetPin) const override;
    virtual EGraphType GetGraphType(const UEdGraph* TestEdGraph) const override;
    virtual FLinearColor GetPinTypeColor(const FEdGraphPinType& PinType) const override;
    virtual bool ShouldHidePinDefaultValue(UEdGraphPin* Pin) const override;
};
