#include "Graph/SFinareGraphNode.h"

#include "Styling/AppStyle.h"
#include "FinareEdGraph.h"
#include "Widgets/Input/SCheckBox.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Text/STextBlock.h"

void SFinareGraphNode::Construct(
	const FArguments& InArgs,
	UFinareEdGraphNode* InNode)
{
	GraphNode = InNode;
	SetCursor(EMouseCursor::CardinalCross);
	UpdateGraphNode();
}

TSharedRef<SWidget> SFinareGraphNode::CreateNodeContentArea()
{
	UFinareEdGraphNode* Node = CastChecked<UFinareEdGraphNode>(GraphNode);
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
			SNew(SVerticalBox)
			+ SVerticalBox::Slot()
			.AutoHeight()
			.HAlign(HAlign_Center)
			.Padding(2.0f, 0.0f, 2.0f, 3.0f)
			[
				SNew(SCheckBox)
				.Visibility(Node->DebugTargetId.IsEmpty()
					            ? EVisibility::Collapsed
					            : EVisibility::Visible)
				.IsChecked_Lambda([Node]()
				{
					return Node->bBreakpointEnabled
						       ? ECheckBoxState::Checked
						       : ECheckBoxState::Unchecked;
				})
				.ToolTipText_Lambda([Node]()
				{
					return FText::FromString(FString::Printf(
						TEXT("Semantic breakpoint · %s\n%s"),
						Node->DebugBreakpointState.IsEmpty()
							? TEXT("disabled")
							: *Node->DebugBreakpointState,
						*Node->DebugTargetId));
				})
				.OnCheckStateChanged_Lambda([Node](ECheckBoxState)
				{
					UFinareEdGraph* Graph = Cast<UFinareEdGraph>(Node->GetGraph());
					if (Graph != nullptr && Graph->OnBreakpointToggleRequested.IsBound())
					{
						Graph->OnBreakpointToggleRequested.Execute(Node->DebugTargetId);
					}
				})
				[
					SNew(STextBlock)
					.Text(FText::FromString(TEXT("●")))
					.ColorAndOpacity_Lambda([Node]()
					{
						return Node->DebugBreakpointState == TEXT("pending")
							       ? FSlateColor(FLinearColor(0.95f, 0.66f, 0.08f))
							       : Node->bBreakpointEnabled
							       ? FSlateColor(FLinearColor(0.95f, 0.10f, 0.08f))
							       : FSlateColor(FLinearColor(0.28f, 0.30f, 0.34f));
					})
				]
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			[
				SAssignNew(LeftNodeBox, SVerticalBox)
			]
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
