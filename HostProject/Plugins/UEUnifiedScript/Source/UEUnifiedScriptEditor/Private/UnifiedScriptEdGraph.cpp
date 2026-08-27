/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 19:06:51
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 14:22:40
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptEditor\Private\UnifiedScriptEdGraph.cpp
 * @Description:
 */
#include "UnifiedScriptEdGraph.h"

#include "EdGraph/EdGraphSchema.h"

namespace UE::UnifiedScript::Editor::Graph::Private
{
class FCreateNodeAction final : public FEdGraphSchemaAction
{
public:
    FCreateNodeAction(const FUnifiedScriptGraphPaletteAction& Action)
        : FEdGraphSchemaAction(
            FText::FromString(Action.Category),
            FText::FromString(Action.Title),
            FText::FromString(Action.Tooltip),
            0,
            FText::FromString(Action.Keywords)),
          ActionId(Action.ActionId)
    {
    }

    virtual UEdGraphNode* PerformAction(
        UEdGraph* ParentGraph,
        UEdGraphPin* FromPin,
        const FVector2D Location,
        bool bSelectNewNode = true) override
    {
        UUnifiedScriptEdGraph* Graph = Cast<UUnifiedScriptEdGraph>(ParentGraph);
        if (Graph != nullptr && Graph->OnNodeCreateRequested.IsBound())
        {
            Graph->OnNodeCreateRequested.Execute(ActionId, Location);
        }
        return nullptr;
    }

private:
    FString ActionId;
};
}

#define LOCTEXT_NAMESPACE "UnifiedScriptEdGraph"

void UUnifiedScriptEdGraphNode::AllocateDefaultPins()
{
    // Pins are allocated from the loaded compiler-owned Surface v0.4 metadata.
}

FText UUnifiedScriptEdGraphNode::GetNodeTitle(ENodeTitleType::Type TitleType) const
{
    return FText::FromString(DisplayTitle);
}

FLinearColor UUnifiedScriptEdGraphNode::GetNodeTitleColor() const
{
    if (bDebugFaulted)
    {
        return FLinearColor(0.75f, 0.08f, 0.12f);
    }
    if (bDebugPaused)
    {
        return FLinearColor(1.0f, 0.55f, 0.08f);
    }
    if (bBreakpointEnabled)
    {
        return FLinearColor(0.72f, 0.08f, 0.08f);
    }
    if (NodeKind == TEXT("region"))
    {
        return FLinearColor(0.88f, 0.43f, 0.08f);
    }
    if (Category == TEXT("flow") || NodeKind == TEXT("branch"))
    {
        return FLinearColor(0.76f, 0.18f, 0.19f);
    }
    if (Category == TEXT("state") || NodeKind == TEXT("variable"))
    {
        return FLinearColor(0.38f, 0.23f, 0.70f);
    }
    if (NodeKind == TEXT("unrealEventEntry"))
    {
        return FLinearColor(0.78f, 0.12f, 0.18f);
    }
    if (NodeKind == TEXT("entry"))
    {
        return FLinearColor(0.10f, 0.55f, 0.42f);
    }
    if (NodeKind == TEXT("return"))
    {
        return FLinearColor(0.55f, 0.12f, 0.18f);
    }
    return FLinearColor(0.08f, 0.39f, 0.68f);
}

FText UUnifiedScriptEdGraphNode::GetTooltipText() const
{
    if (!AdvancedTooltip.IsEmpty())
    {
        return FText::FromString(AdvancedTooltip);
    }
    return FText::FromString(FString::Printf(
        TEXT("%s\n%s\n\nSemantic ID\n%s"),
        *Summary,
        *SourceDisplay,
        *SemanticId));
}

bool UUnifiedScriptEdGraphNode::CanDuplicateNode() const
{
    return false;
}

bool UUnifiedScriptEdGraphNode::CanUserDeleteNode() const
{
    return bCanDelete;
}

void UUnifiedScriptEdGraphNode::DestroyNode()
{
    UUnifiedScriptEdGraph* Graph = Cast<UUnifiedScriptEdGraph>(GetGraph());
    if (bCanDelete && Graph != nullptr && Graph->OnStatementDeleteRequested.IsBound())
    {
        Graph->OnStatementDeleteRequested.Execute(this);
    }
}

void UUnifiedScriptEdGraphRerouteNode::AllocateDefaultPins()
{
    FEdGraphPinType PinType;
    PinType.PinCategory = RoutedPinCategory;
    PinType.PinSubCategory = RoutedPinSubCategory;
    CreatePin(EGPD_Input, PinType, TEXT("in"));
    CreatePin(EGPD_Output, PinType, TEXT("out"));
}

bool UUnifiedScriptEdGraphRerouteNode::ShouldDrawNodeAsControlPointOnly(
    int32& OutInputPinIndex,
    int32& OutOutputPinIndex) const
{
    OutInputPinIndex = 0;
    OutOutputPinIndex = 1;
    return Pins.Num() == 2;
}

void UUnifiedScriptEdGraphRerouteNode::DestroyNode()
{
    UEdGraph* Graph = GetGraph();
    UEdGraphPin* Input = FindPin(TEXT("in"), EGPD_Input);
    UEdGraphPin* Output = FindPin(TEXT("out"), EGPD_Output);
    UEdGraphPin* Previous = Input != nullptr && Input->LinkedTo.Num() == 1
        ? Input->LinkedTo[0]
        : nullptr;
    UEdGraphPin* Next = Output != nullptr && Output->LinkedTo.Num() == 1
        ? Output->LinkedTo[0]
        : nullptr;
    if (Previous != nullptr && Next != nullptr)
    {
        Previous->MakeLinkTo(Next, false);
    }
    if (Graph != nullptr)
    {
        for (UEdGraphNode* Node : Graph->Nodes)
        {
            UUnifiedScriptEdGraphRerouteNode* Following =
                Cast<UUnifiedScriptEdGraphRerouteNode>(Node);
            if (Following != nullptr && Following != this &&
                Following->EdgeId == EdgeId && Following->PointIndex > PointIndex)
            {
                --Following->PointIndex;
            }
        }
    }
    Super::DestroyNode();
}

void UUnifiedScriptEdGraphSchema::GetGraphContextActions(
    FGraphContextMenuBuilder& ContextMenuBuilder) const
{
    const UUnifiedScriptEdGraph* Graph = Cast<UUnifiedScriptEdGraph>(ContextMenuBuilder.CurrentGraph);
    if (Graph == nullptr)
    {
        return;
    }
    for (const FUnifiedScriptGraphPaletteAction& Action : Graph->PaletteActions)
    {
        if (Action.bRequiresBlock && Graph->ActiveBlockRegionId.IsEmpty())
        {
            continue;
        }
        ContextMenuBuilder.AddAction(
            MakeShared<UE::UnifiedScript::Editor::Graph::Private::FCreateNodeAction>(Action));
    }
}

const FPinConnectionResponse UUnifiedScriptEdGraphSchema::CanCreateConnection(
    const UEdGraphPin* A,
    const UEdGraphPin* B) const
{
    if (A == nullptr || B == nullptr || A == B)
    {
        return FPinConnectionResponse(
            CONNECT_RESPONSE_DISALLOW,
            LOCTEXT("InvalidPins", "Choose two distinct graph pins."));
    }
    if (A->GetOwningNode() == B->GetOwningNode())
    {
        return FPinConnectionResponse(
            CONNECT_RESPONSE_DISALLOW,
            LOCTEXT("SelfConnection", "A node cannot connect to itself."));
    }
    if (A->Direction == B->Direction)
    {
        return FPinConnectionResponse(
            CONNECT_RESPONSE_DISALLOW,
            LOCTEXT("SameDirection", "Connect an output pin to an input pin."));
    }

    const UEdGraphPin* OutputPin = A->Direction == EGPD_Output ? A : B;
    const UEdGraphPin* InputPin = A->Direction == EGPD_Input ? A : B;
    const bool bData = OutputPin->PinType.PinCategory == TEXT("data") &&
        InputPin->PinType.PinCategory == TEXT("data");
    const bool bControl = OutputPin->PinType.PinCategory == TEXT("exec") &&
        InputPin->PinType.PinCategory == TEXT("exec");
    if (!bData && !bControl)
    {
        return FPinConnectionResponse(
            CONNECT_RESPONSE_DISALLOW,
            LOCTEXT("PinKindMismatch", "Connect data to data or control flow to control flow."));
    }
    if (bControl)
    {
        const UUnifiedScriptEdGraphNode* SourceNode =
            Cast<UUnifiedScriptEdGraphNode>(OutputPin->GetOwningNode());
        const UUnifiedScriptEdGraphNode* TargetNode =
            Cast<UUnifiedScriptEdGraphNode>(InputPin->GetOwningNode());
        int32 SourceControlOutputCount = 0;
        int32 TargetControlInputCount = 0;
        if (SourceNode != nullptr)
        {
            for (const UEdGraphPin* Pin : SourceNode->Pins)
            {
                SourceControlOutputCount += Pin != nullptr && Pin->Direction == EGPD_Output &&
                    Pin->PinType.PinCategory == TEXT("exec") ? 1 : 0;
            }
        }
        if (TargetNode != nullptr)
        {
            for (const UEdGraphPin* Pin : TargetNode->Pins)
            {
                TargetControlInputCount += Pin != nullptr && Pin->Direction == EGPD_Input &&
                    Pin->PinType.PinCategory == TEXT("exec") ? 1 : 0;
            }
        }
        const bool bSequentialEndpoints = SourceNode != nullptr && TargetNode != nullptr &&
            SourceNode->NodeKind != TEXT("entry") &&
            SourceNode->NodeKind != TEXT("switchCase") &&
            SourceControlOutputCount == 1 && TargetControlInputCount == 1;
        if (!bSequentialEndpoints)
        {
            return FPinConnectionResponse(
                CONNECT_RESPONSE_DISALLOW,
                LOCTEXT(
                    "StructuredControlOnly",
                    "Choose a single-path control output and a single-path control input."));
        }
        if (InputPin->LinkedTo.Num() != 1)
        {
            return FPinConnectionResponse(
                CONNECT_RESPONSE_DISALLOW,
                LOCTEXT(
                    "ExistingControlEdgeRequired",
                    "The target must have one compiler-owned control predecessor."));
        }
        if (InputPin->LinkedTo.Contains(OutputPin))
        {
            return FPinConnectionResponse(
                CONNECT_RESPONSE_DISALLOW,
                LOCTEXT("NoOpControlConnection", "This control edge is already connected."));
        }
        return FPinConnectionResponse(
            CONNECT_RESPONSE_MAKE,
            LOCTEXT(
                "ReconnectControlEdge",
                "Move this statement after the source through the Workbench."));
    }
    if (OutputPin->PinType.PinSubCategory != InputPin->PinType.PinSubCategory)
    {
        return FPinConnectionResponse(
            CONNECT_RESPONSE_DISALLOW,
            LOCTEXT("TypeMismatch", "Data pins must have the same semantic type."));
    }
    if (InputPin->LinkedTo.IsEmpty())
    {
        return FPinConnectionResponse(
            CONNECT_RESPONSE_DISALLOW,
            LOCTEXT("ExistingEdgeRequired", "Only existing compiler-owned data inputs can be rewired."));
    }
    if (InputPin->LinkedTo.Contains(OutputPin))
    {
        return FPinConnectionResponse(
            CONNECT_RESPONSE_DISALLOW,
            LOCTEXT("NoOpConnection", "This data edge is already connected."));
    }

    return FPinConnectionResponse(
        CONNECT_RESPONSE_MAKE,
        LOCTEXT("ReconnectDataEdge", "Submit a typed data-edge rewire to the Workbench."));
}

bool UUnifiedScriptEdGraphSchema::TryCreateConnection(UEdGraphPin* A, UEdGraphPin* B) const
{
    const FPinConnectionResponse Response = CanCreateConnection(A, B);
    if (Response.Response != CONNECT_RESPONSE_MAKE || A == nullptr || B == nullptr)
    {
        return false;
    }

    UUnifiedScriptEdGraph* Graph = Cast<UUnifiedScriptEdGraph>(A->GetOwningNode()->GetGraph());
    if (Graph == nullptr)
    {
        return false;
    }

    const UEdGraphPin* OutputPin = A->Direction == EGPD_Output ? A : B;
    const UEdGraphPin* InputPin = A->Direction == EGPD_Input ? A : B;
    if (OutputPin->PinType.PinCategory == TEXT("exec"))
    {
        if (!Graph->OnControlRewireRequested.IsBound())
        {
            return false;
        }
        Graph->OnControlRewireRequested.Execute(OutputPin, InputPin);
        return true;
    }
    if (!Graph->OnDataRewireRequested.IsBound())
    {
        return false;
    }
    Graph->OnDataRewireRequested.Execute(OutputPin, InputPin);
    return true;
}

void UUnifiedScriptEdGraphSchema::OnPinConnectionDoubleCicked(
    UEdGraphPin* PinA,
    UEdGraphPin* PinB,
    const FVector2f& GraphPosition) const
{
    UEdGraphNode* Node = PinA != nullptr ? PinA->GetOwningNode() : nullptr;
    UUnifiedScriptEdGraph* Graph = Node != nullptr
        ? Cast<UUnifiedScriptEdGraph>(Node->GetGraph())
        : nullptr;
    if (Graph != nullptr && Graph->OnRerouteRequested.IsBound())
    {
        Graph->OnRerouteRequested.Execute(PinA, PinB, FVector2D(GraphPosition));
    }
}

void UUnifiedScriptEdGraphSchema::BreakNodeLinks(UEdGraphNode& TargetNode) const
{
    // Surface links change only after a semantic patch commits on the Workbench.
}

void UUnifiedScriptEdGraphSchema::BreakPinLinks(
    UEdGraphPin& TargetPin,
    bool bSendsNodeNotification) const
{
    if (TargetPin.Direction != EGPD_Input || TargetPin.PinType.PinCategory != TEXT("data") ||
        TargetPin.LinkedTo.Num() != 1)
    {
        return;
    }
    UUnifiedScriptEdGraph* Graph = Cast<UUnifiedScriptEdGraph>(TargetPin.GetOwningNode()->GetGraph());
    if (Graph != nullptr && Graph->OnDataDisconnectRequested.IsBound())
    {
        Graph->OnDataDisconnectRequested.Execute(TargetPin.LinkedTo[0], &TargetPin);
    }
}

void UUnifiedScriptEdGraphSchema::BreakSinglePinLink(
    UEdGraphPin* SourcePin,
    UEdGraphPin* TargetPin) const
{
    if (SourcePin == nullptr || TargetPin == nullptr)
    {
        return;
    }
    const UEdGraphPin* OutputPin = SourcePin->Direction == EGPD_Output ? SourcePin : TargetPin;
    const UEdGraphPin* InputPin = SourcePin->Direction == EGPD_Input ? SourcePin : TargetPin;
    UUnifiedScriptEdGraph* Graph = Cast<UUnifiedScriptEdGraph>(InputPin->GetOwningNode()->GetGraph());
    if (OutputPin->PinType.PinCategory == TEXT("data") &&
        InputPin->PinType.PinCategory == TEXT("data") && Graph != nullptr &&
        Graph->OnDataDisconnectRequested.IsBound())
    {
        Graph->OnDataDisconnectRequested.Execute(OutputPin, InputPin);
    }
}

EGraphType UUnifiedScriptEdGraphSchema::GetGraphType(const UEdGraph* TestEdGraph) const
{
    return GT_Function;
}

FLinearColor UUnifiedScriptEdGraphSchema::GetPinTypeColor(
    const FEdGraphPinType& PinType) const
{
    if (PinType.PinCategory == TEXT("exec"))
    {
        return FLinearColor::White;
    }

    const FString TypeName = PinType.PinSubCategory.ToString().ToLower();
    if (TypeName.Contains(TEXT("bool")))
    {
        return FLinearColor(0.86f, 0.08f, 0.12f);
    }
    if (TypeName.Contains(TEXT("float")) || TypeName.Contains(TEXT("double")) ||
        TypeName.Contains(TEXT("decimal")))
    {
        return FLinearColor(0.20f, 0.82f, 0.38f);
    }
    if (TypeName.Contains(TEXT("int")) || TypeName.Contains(TEXT("byte")) ||
        TypeName.Contains(TEXT("short")) || TypeName.Contains(TEXT("long")))
    {
        return FLinearColor(0.10f, 0.72f, 0.78f);
    }
    if (TypeName.Contains(TEXT("string")) || TypeName.Contains(TEXT("char")))
    {
        return FLinearColor(0.88f, 0.18f, 0.72f);
    }
    return FLinearColor(0.26f, 0.52f, 0.92f);
}

bool UUnifiedScriptEdGraphSchema::ShouldHidePinDefaultValue(UEdGraphPin* Pin) const
{
    return true;
}

#undef LOCTEXT_NAMESPACE
