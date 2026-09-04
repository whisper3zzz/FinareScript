#include "UnifiedScriptAsyncDemoWidget.h"

#include "Blueprint/WidgetTree.h"
#include "Components/Button.h"
#include "Components/CanvasPanel.h"
#include "Components/CanvasPanelSlot.h"
#include "Components/TextBlock.h"
#include "Components/VerticalBox.h"

bool UUnifiedScriptAsyncDemoWidget::Initialize()
{
    if (!Super::Initialize()) return false;
    if (WidgetTree->RootWidget != nullptr) return true;

    UCanvasPanel* Canvas = WidgetTree->ConstructWidget<UCanvasPanel>(UCanvasPanel::StaticClass(), TEXT("Canvas"));
    WidgetTree->RootWidget = Canvas;
    UVerticalBox* Panel = WidgetTree->ConstructWidget<UVerticalBox>(UVerticalBox::StaticClass(), TEXT("Panel"));
    UCanvasPanelSlot* PanelSlot = Canvas->AddChildToCanvas(Panel);
    PanelSlot->SetPosition(FVector2D(32, 32));
    PanelSlot->SetSize(FVector2D(420, 200));

    UTextBlock* Title = WidgetTree->ConstructWidget<UTextBlock>(UTextBlock::StaticClass(), TEXT("Title"));
    Title->SetText(FText::FromString(TEXT("UnifiedScript | Async playground")));
    Panel->AddChildToVerticalBox(Title);
    UButton* Button = WidgetTree->ConstructWidget<UButton>(UButton::StaticClass(), TEXT("SpawnButton"));
    UTextBlock* Label = WidgetTree->ConstructWidget<UTextBlock>();
    Label->SetText(FText::FromString(TEXT("Load & spawn cube")));
    Button->AddChild(Label);
    Panel->AddChildToVerticalBox(Button);
    UButton* Cancel = WidgetTree->ConstructWidget<UButton>(UButton::StaticClass(), TEXT("CancelButton"));
    UTextBlock* CancelLabel = WidgetTree->ConstructWidget<UTextBlock>();
    CancelLabel->SetText(FText::FromString(TEXT("Cancel load")));
    Cancel->AddChild(CancelLabel);
    Panel->AddChildToVerticalBox(Cancel);
    UTextBlock* Status = WidgetTree->ConstructWidget<UTextBlock>(UTextBlock::StaticClass(), TEXT("StatusText"));
    Status->SetText(FText::FromString(TEXT("Starting...")));
    Status->SetAutoWrapText(true);
    Panel->AddChildToVerticalBox(Status);
    return true;
}
