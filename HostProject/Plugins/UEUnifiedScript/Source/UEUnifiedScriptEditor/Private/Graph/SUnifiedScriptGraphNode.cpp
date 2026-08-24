#include "Graph/SUnifiedScriptGraphNode.h"

#include "Styling/AppStyle.h"
#include "UnifiedScriptEdGraph.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Text/STextBlock.h"

void SUnifiedScriptGraphNode::Construct(
    const FArguments& InArgs,
    UUnifiedScriptEdGraphNode* InNode)
{
    GraphNode = InNode;
    SetCursor(EMouseCursor::CardinalCross);
    UpdateGraphNode();
}

TSharedRef<SWidget> SUnifiedScriptGraphNode::CreateNodeContentArea()
{
    const UUnifiedScriptEdGraphNode* Node = CastChecked<UUnifiedScriptEdGraphNode>(GraphNode);
    const bool bShowSummary = Node->bShowSummary && !Node->Summary.IsEmpty() &&
        Node->Summary != Node->DisplayTitle;

    TSharedRef<SWidget> SummaryWidget = SNew(SBox)
        .MinDesiredWidth(Node->bCompactPresentation ? 72.0f : 150.0f)
        .MaxDesiredWidth(Node->bCompactPresentation ? 150.0f : 260.0f)
        .Padding(bShowSummary
            ? FMargin(8.0f, 5.0f, 8.0f, 7.0f)
            : FMargin(4.0f, 1.0f))
        [
            SNew(STextBlock)
            .Text(FText::FromString(Node->Summary))
            .Visibility(bShowSummary ? EVisibility::Visible : EVisibility::Collapsed)
            .AutoWrapText(true)
            .Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
            .ColorAndOpacity(FLinearColor(0.68f, 0.72f, 0.78f))
        ];

    return SNew(SHorizontalBox)
        + SHorizontalBox::Slot()
        .AutoWidth()
        .VAlign(VAlign_Center)
        [
            SAssignNew(LeftNodeBox, SVerticalBox)
        ]
        + SHorizontalBox::Slot()
        .FillWidth(1.0f)
        [
            SummaryWidget
        ]
        + SHorizontalBox::Slot()
        .AutoWidth()
        .VAlign(VAlign_Center)
        [
            SAssignNew(RightNodeBox, SVerticalBox)
        ];
}
