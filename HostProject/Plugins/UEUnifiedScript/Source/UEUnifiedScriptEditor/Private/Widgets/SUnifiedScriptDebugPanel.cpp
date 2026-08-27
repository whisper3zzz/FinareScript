#include "Widgets/SUnifiedScriptDebugPanel.h"

#include "HAL/FileManager.h"
#include "HttpModule.h"
#include "Interfaces/IHttpRequest.h"
#include "Interfaces/IHttpResponse.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Styling/AppStyle.h"
#include "UnifiedScriptAuthoringWorkbench.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptComponent.h"
#include "UObject/UObjectIterator.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SSeparator.h"
#include "Widgets/Layout/SSplitter.h"
#include "Widgets/Views/SListView.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "UnifiedScriptDebugPanel"

void SUnifiedScriptDebugPanel::Construct(const FArguments& InArgs)
{
    ServerUrlAttribute = InArgs._ServerUrl;
    OnNodeFocused = InArgs._OnNodeFocused;
    OnDebugStateChanged = InArgs._OnDebugStateChanged;
    RegisterActiveTimer(
        0.25f,
        FWidgetActiveTimerDelegate::CreateSP(
            this,
            &SUnifiedScriptDebugPanel::RefreshLiveWorldTarget));

    ChildSlot
    [
        SNew(SBorder)
        .BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
        .Padding(8.0f)
        [
            SNew(SVerticalBox)
            + SVerticalBox::Slot().AutoHeight()
            [
                SNew(SHorizontalBox)
                + SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0.0f, 0.0f, 10.0f, 0.0f)
                [
                    SNew(STextBlock)
                    .Text(LOCTEXT("DebuggerTitle", "CORECLR DEBUGGER"))
                    .Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
                    .ColorAndOpacity(FLinearColor(0.95f, 0.48f, 0.22f))
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 4.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("AttachSemanticWorld", "Attach Semantic"))
                    .ToolTipText(LOCTEXT(
                        "AttachSemanticWorldTip",
                        "Publish an instrumented image, reload this World instance, and attach statement breakpoints."))
                    .IsEnabled(this, &SUnifiedScriptDebugPanel::CanAttachSemanticWorld)
                    .OnClicked(this, &SUnifiedScriptDebugPanel::HandleAttachSemanticWorld)
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 4.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("DetachSemanticWorld", "Detach Semantic"))
                    .IsEnabled(this, &SUnifiedScriptDebugPanel::CanDetachSemanticWorld)
                    .OnClicked(this, &SUnifiedScriptDebugPanel::HandleDetachSemanticWorld)
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 4.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("Run", "Run"))
                    .ButtonStyle(FAppStyle::Get(), TEXT("PrimaryButton"))
                    .IsEnabled(this, &SUnifiedScriptDebugPanel::CanRun)
                    .OnClicked(this, &SUnifiedScriptDebugPanel::HandleRun)
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 4.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("Pause", "Pause"))
                    .IsEnabled(this, &SUnifiedScriptDebugPanel::CanPause)
                    .OnClicked(this, &SUnifiedScriptDebugPanel::HandlePause)
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 4.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("Continue", "Continue"))
                    .IsEnabled(this, &SUnifiedScriptDebugPanel::CanResume)
                    .OnClicked(this, &SUnifiedScriptDebugPanel::HandleContinue)
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 4.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("StepIn", "Step In"))
                    .IsEnabled(this, &SUnifiedScriptDebugPanel::CanResume)
                    .OnClicked(this, &SUnifiedScriptDebugPanel::HandleStepIn)
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 4.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("StepOver", "Step Over"))
                    .IsEnabled(this, &SUnifiedScriptDebugPanel::CanResume)
                    .OnClicked(this, &SUnifiedScriptDebugPanel::HandleStepOver)
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 10.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("StepOut", "Step Out"))
                    .IsEnabled(this, &SUnifiedScriptDebugPanel::CanStepOut)
                    .OnClicked(this, &SUnifiedScriptDebugPanel::HandleStepOut)
                ]
                + SHorizontalBox::Slot().FillWidth(1.0f).VAlign(VAlign_Center)
                [
                    SAssignNew(DebugStatusText, STextBlock)
                    .Text_Lambda([this]() { return FText::FromString(DebugStatus); })
                    .ColorAndOpacity_Lambda([this]()
                    {
                        return bDebugStatusError
                            ? FSlateColor(FLinearColor(0.95f, 0.28f, 0.22f))
                            : FSlateColor(FLinearColor(0.58f, 0.64f, 0.72f));
                    })
                    .Clipping(EWidgetClipping::ClipToBounds)
                ]
            ]
            + SVerticalBox::Slot().AutoHeight().Padding(0.0f, 6.0f, 0.0f, 5.0f)
            [
                SNew(SSeparator)
            ]
            + SVerticalBox::Slot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 5.0f)
            [
                SNew(SHorizontalBox)
                + SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0.0f, 0.0f, 8.0f, 0.0f)
                [
                    SNew(STextBlock)
                    .Text(LOCTEXT("LiveWorld", "LIVE UE WORLD"))
                    .Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
                    .ColorAndOpacity(FLinearColor(0.30f, 0.72f, 0.95f))
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 4.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("PauseWorld", "Pause World Script"))
                    .ToolTipText(LOCTEXT(
                        "PauseWorldTip",
                        "Pause managed ticks at the next UE frame boundary without blocking the Game Thread."))
                    .IsEnabled(this, &SUnifiedScriptDebugPanel::CanPauseLiveWorld)
                    .OnClicked(this, &SUnifiedScriptDebugPanel::HandlePauseLiveWorld)
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 4.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("StepWorld", "Step Frame"))
                    .IsEnabled(this, &SUnifiedScriptDebugPanel::CanStepLiveWorld)
                    .OnClicked(this, &SUnifiedScriptDebugPanel::HandleStepLiveWorld)
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 8.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("ContinueWorld", "Continue World"))
                    .IsEnabled(this, &SUnifiedScriptDebugPanel::CanContinueLiveWorld)
                    .OnClicked(this, &SUnifiedScriptDebugPanel::HandleContinueLiveWorld)
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 4.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text_Lambda([this]()
                    {
                        return LiveComponent.IsValid() &&
                            LiveComponent->bGameplayProfilerEnabled
                            ? LOCTEXT("StopProfile", "Stop Profile")
                            : LOCTEXT("StartProfile", "Start Profile");
                    })
                    .ToolTipText(LOCTEXT(
                        "ToggleProfileTip",
                        "Opt in to a bounded 120-frame gameplay, allocation, semantic-node, and bridge trace."))
                    .IsEnabled(this, &SUnifiedScriptDebugPanel::CanControlGameplayProfiler)
                    .OnClicked(this, &SUnifiedScriptDebugPanel::HandleToggleGameplayProfiler)
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 8.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("CaptureProfile", "Capture Profile"))
                    .IsEnabled(this, &SUnifiedScriptDebugPanel::CanCaptureGameplayProfile)
                    .OnClicked(this, &SUnifiedScriptDebugPanel::HandleCaptureGameplayProfile)
                ]
                + SHorizontalBox::Slot().FillWidth(1.0f).VAlign(VAlign_Center)
                [
                    SNew(STextBlock)
                    .Text_Lambda([this]() { return FText::FromString(DescribeLiveWorldTarget()); })
                    .Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
                    .ColorAndOpacity(FLinearColor(0.58f, 0.68f, 0.76f))
                    .Clipping(EWidgetClipping::ClipToBounds)
                ]
            ]
            + SVerticalBox::Slot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 5.0f)
            [
                SNew(SHorizontalBox)
                .Visibility_Lambda([this]()
                {
                    return GameplayProfile.IsSet() || !GameplayProfileStatus.IsEmpty()
                        ? EVisibility::Visible
                        : EVisibility::Collapsed;
                })
                + SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0.0f, 0.0f, 8.0f, 0.0f)
                [
                    SNew(STextBlock)
                    .Text(LOCTEXT("GameplayProfiler", "GAMEPLAY / BRIDGE PROFILE"))
                    .Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
                    .ColorAndOpacity(FLinearColor(0.50f, 0.82f, 0.42f))
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 6.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("FocusHottestProfileNode", "Focus Hot Node"))
                    .ToolTipText(LOCTEXT(
                        "FocusHottestProfileNodeTip",
                        "Select and frame the highest-cost semantic node in the Visual graph."))
                    .IsEnabled(this, &SUnifiedScriptDebugPanel::CanFocusHottestProfileNode)
                    .OnClicked(this, &SUnifiedScriptDebugPanel::HandleFocusHottestProfileNode)
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 8.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("SaveGameplayProfile", "Save JSON"))
                    .ToolTipText(LOCTEXT(
                        "SaveGameplayProfileTip",
                        "Write the portable bounded trace under Saved/UnifiedScript/Profiles."))
                    .IsEnabled(this, &SUnifiedScriptDebugPanel::CanSaveGameplayProfile)
                    .OnClicked(this, &SUnifiedScriptDebugPanel::HandleSaveGameplayProfile)
                ]
                + SHorizontalBox::Slot().FillWidth(1.0f).VAlign(VAlign_Center)
                [
                    SNew(STextBlock)
                    .Text_Lambda([this]() { return FText::FromString(DescribeGameplayProfile()); })
                    .Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
                    .ColorAndOpacity(FLinearColor(0.62f, 0.74f, 0.60f))
                    .ToolTipText_Lambda([this]()
                    {
                        if (!GameplayProfile.IsSet() || GameplayProfile->Frames.IsEmpty())
                        {
                            return FText::FromString(GameplayProfileStatus);
                        }
                        const FUnifiedScriptGameplayProfileFrame& Last =
                            GameplayProfile->Frames.Last();
                        return FText::FromString(FString::Printf(
                            TEXT("Trace %s · World frame %s · generation %d · %.3f ms · %lld B"),
                            *GameplayProfile->TraceId,
                            *Last.WorldFrameNumber,
                            Last.Generation,
                            static_cast<double>(Last.DurationNanoseconds) / 1000000.0,
                            static_cast<long long>(Last.AllocatedBytes)));
                    })
                    .Clipping(EWidgetClipping::ClipToBounds)
                ]
            ]
            + SVerticalBox::Slot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 5.0f)
            [
                SNew(SHorizontalBox)
                .Visibility_Lambda([this]()
                {
                    return LiveContext.IsSet() && LiveContext->LastDiagnostic.IsValid()
                        ? EVisibility::Visible
                        : EVisibility::Collapsed;
                })
                + SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0.0f, 0.0f, 6.0f, 0.0f)
                [
                    SNew(STextBlock)
                    .Text(LOCTEXT("RuntimeFailure", "RUNTIME FAILURE"))
                    .Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
                    .ColorAndOpacity(FLinearColor(0.95f, 0.24f, 0.22f))
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 8.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("FocusRuntimeFailure", "Focus Node"))
                    .IsEnabled(this, &SUnifiedScriptDebugPanel::CanFocusRuntimeFailure)
                    .OnClicked(this, &SUnifiedScriptDebugPanel::HandleFocusRuntimeFailure)
                ]
                + SHorizontalBox::Slot().FillWidth(1.0f).VAlign(VAlign_Center)
                [
                    SNew(STextBlock)
                    .Text_Lambda([this]() { return FText::FromString(DescribeRuntimeDiagnostic()); })
                    .Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
                    .ColorAndOpacity(FLinearColor(0.90f, 0.46f, 0.42f))
                    .ToolTipText_Lambda([this]()
                    {
                        return FText::FromString(
                            LiveContext.IsSet()
                                ? LiveContext->LastDiagnostic.StackTrace
                                : FString());
                    })
                    .Clipping(EWidgetClipping::ClipToBounds)
                ]
            ]
            + SVerticalBox::Slot().AutoHeight()
            [
                SNew(SBox)
                .HeightOverride(142.0f)
                [
                    SNew(SSplitter)
                    + SSplitter::Slot().Value(0.36f)
                    [
                        SNew(SVerticalBox)
                        + SVerticalBox::Slot().AutoHeight().Padding(3.0f)
                        [
                            SNew(STextBlock)
                            .Text(LOCTEXT("CallStack", "CALL STACK"))
                            .Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
                        ]
                        + SVerticalBox::Slot().FillHeight(1.0f)
                        [
                            SAssignNew(FrameListView, SListView<FDebugFrameItem>)
                            .ListItemsSource(&FrameItems)
                            .OnGenerateRow(this, &SUnifiedScriptDebugPanel::GenerateFrameRow)
                            .OnSelectionChanged(this, &SUnifiedScriptDebugPanel::HandleFrameSelectionChanged)
                            .SelectionMode(ESelectionMode::Single)
                        ]
                    ]
                    + SSplitter::Slot().Value(0.34f)
                    [
                        SNew(SVerticalBox)
                        + SVerticalBox::Slot().AutoHeight().Padding(6.0f, 3.0f)
                        [
                            SNew(STextBlock)
                            .Text(LOCTEXT("Locals", "LOCALS / PARAMETERS"))
                            .Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
                        ]
                        + SVerticalBox::Slot().FillHeight(1.0f)
                        [
                            SAssignNew(LocalListView, SListView<FDebugValueItem>)
                            .ListItemsSource(&LocalItems)
                            .OnGenerateRow(this, &SUnifiedScriptDebugPanel::GenerateValueRow)
                            .SelectionMode(ESelectionMode::None)
                        ]
                    ]
                    + SSplitter::Slot().Value(0.30f)
                    [
                        SNew(SVerticalBox)
                        + SVerticalBox::Slot().AutoHeight().Padding(6.0f, 3.0f)
                        [
                            SNew(STextBlock)
                            .Text(LOCTEXT("Watch", "READ-ONLY WATCH"))
                            .Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
                        ]
                        + SVerticalBox::Slot().AutoHeight().Padding(5.0f, 2.0f)
                        [
                            SNew(SHorizontalBox)
                            + SHorizontalBox::Slot().FillWidth(1.0f)
                            [
                                SAssignNew(WatchTextBox, SEditableTextBox)
                                .HintText(LOCTEXT("WatchHint", "local.field[0]"))
                                .IsEnabled_Lambda([this]()
                                {
                                    return Session.IsSet() && Session->IsPaused() &&
                                        SelectedFrameId > 0 && !bCommandPending && !bWaitPending;
                                })
                                .OnTextCommitted_Lambda([this](const FText&, ETextCommit::Type CommitType)
                                {
                                    if (CommitType == ETextCommit::OnEnter)
                                    {
                                        HandleEvaluate();
                                    }
                                })
                            ]
                            + SHorizontalBox::Slot().AutoWidth().Padding(4.0f, 0.0f, 0.0f, 0.0f)
                            [
                                SNew(SButton)
                                .Text(LOCTEXT("Evaluate", "Evaluate"))
                                .IsEnabled(this, &SUnifiedScriptDebugPanel::CanEvaluate)
                                .OnClicked(this, &SUnifiedScriptDebugPanel::HandleEvaluate)
                            ]
                        ]
                        + SVerticalBox::Slot().FillHeight(1.0f).Padding(6.0f, 4.0f)
                        [
                            SNew(SScrollBox)
                            + SScrollBox::Slot()
                            [
                                SAssignNew(EvaluationTextBlock, STextBlock)
                                .Text_Lambda([this]() { return FText::FromString(EvaluationText); })
                                .AutoWrapText(true)
                                .ColorAndOpacity(FLinearColor(0.66f, 0.70f, 0.76f))
                            ]
                        ]
                    ]
                ]
            ]
        ]
    ];
}

void SUnifiedScriptDebugPanel::SetExecutionContext(
    const FUnifiedScriptAuthoringSurfaceDocument& Document,
    const FString& FunctionId)
{
    SourceCompilationId = Document.SourceCompilationId;
    SemanticConfigurationId = Document.SemanticConfigurationId;
    ActiveFunction.Reset();
    ActiveAsset.Reset();
    for (const FUnifiedScriptAuthoringAsset& Asset : Document.Assets)
    {
        if (const FUnifiedScriptAuthoringFunction* Function = Asset.Functions.FindByPredicate(
                [&FunctionId](const FUnifiedScriptAuthoringFunction& Candidate)
                {
                    return Candidate.Id == FunctionId;
                }))
        {
            ActiveFunction = *Function;
            ActiveAsset = Asset;
            break;
        }
    }
    if (!HasActiveSession())
    {
        SetDebugStatus(
            ActiveFunction.IsSet()
                ? FString::Printf(
                    TEXT("Ready: %s · %d semantic breakpoint%s"),
                    *ActiveFunction->QualifiedName,
                    BreakpointNodeIds.Num(),
                    BreakpointNodeIds.Num() == 1 ? TEXT("") : TEXT("s"))
                : TEXT("Select a Visual function to start a managed debug session."));
    }
    RefreshLiveWorldTargetNow();
}

bool SUnifiedScriptDebugPanel::ToggleBreakpoint(const FString& DebugTargetId)
{
    if (DebugTargetId.IsEmpty())
    {
        return false;
    }
    if (HasActiveSession())
    {
        SetDebugStatus(
            TEXT("Breakpoint changes apply between runs; finish the current execution first."),
            true);
        return false;
    }
    if (BreakpointNodeIds.Contains(DebugTargetId))
    {
        BreakpointNodeIds.Remove(DebugTargetId);
    }
    else
    {
        BreakpointNodeIds.Add(DebugTargetId);
    }
    SetDebugStatus(FString::Printf(
        TEXT("%d semantic breakpoint%s ready for the current managed generation."),
        BreakpointNodeIds.Num(),
        BreakpointNodeIds.Num() == 1 ? TEXT("") : TEXT("s")));
    OnDebugStateChanged.ExecuteIfBound();
    return true;
}

bool SUnifiedScriptDebugPanel::IsBreakpointEnabled(const FString& DebugTargetId) const
{
    return BreakpointNodeIds.Contains(DebugTargetId);
}

FString SUnifiedScriptDebugPanel::GetBreakpointState(const FString& DebugTargetId) const
{
    if (!BreakpointNodeIds.Contains(DebugTargetId))
    {
        return TEXT("disabled");
    }
    if (Session.IsSet())
    {
        if (const FUnifiedScriptDebugBreakpointBinding* Binding =
                Session->Breakpoints.FindByPredicate(
                    [&DebugTargetId](const FUnifiedScriptDebugBreakpointBinding& Candidate)
                    {
                        return Candidate.NodeId == DebugTargetId;
                    }))
        {
            return Binding->State;
        }
    }
    return TEXT("enabled");
}

bool SUnifiedScriptDebugPanel::IsPausedNode(const FString& DebugTargetId) const
{
    if (!Session.IsSet() || !Session->IsPaused())
    {
        return false;
    }
    const FUnifiedScriptDebugStackFrame* SelectedFrame = Session->Frames.FindByPredicate(
        [this](const FUnifiedScriptDebugStackFrame& Candidate)
        {
            return Candidate.FrameId == SelectedFrameId;
        });
    return SelectedFrame != nullptr && SelectedFrame->NodeId == DebugTargetId;
}

bool SUnifiedScriptDebugPanel::IsRuntimeFailureNode(const FString& DebugTargetId) const
{
    return !DebugTargetId.IsEmpty() && RuntimeFailureNodeId == DebugTargetId;
}

FReply SUnifiedScriptDebugPanel::HandlePauseLiveWorld()
{
    if (CanPauseLiveWorld() && LiveComponent->PauseAtDebugFrameBoundary())
    {
        LiveRuntimeStatus = TEXT("Managed script paused at the UE frame boundary.");
        RefreshLiveWorldTargetNow();
    }
    return FReply::Handled();
}

FReply SUnifiedScriptDebugPanel::HandleStepLiveWorld()
{
    if (CanStepLiveWorld() && LiveComponent->StepOneDebugFrame())
    {
        LiveRuntimeStatus = TEXT("One managed World frame is scheduled; the script remains paused.");
        RefreshLiveWorldTargetNow();
    }
    return FReply::Handled();
}

FReply SUnifiedScriptDebugPanel::HandleContinueLiveWorld()
{
    if (CanContinueLiveWorld() && LiveComponent->ContinueFromDebugFrameBoundary())
    {
        LiveRuntimeStatus = TEXT("Managed World execution resumed.");
        RefreshLiveWorldTargetNow();
    }
    return FReply::Handled();
}

FReply SUnifiedScriptDebugPanel::HandleToggleGameplayProfiler()
{
    if (!CanControlGameplayProfiler())
    {
        return FReply::Handled();
    }
    UUnifiedScriptComponent* Component = LiveComponent.Get();
    check(Component != nullptr);
    FString Error;
    if (Component->bGameplayProfilerEnabled)
    {
        FString Json;
        if (!Component->CaptureGameplayProfileTrace(Json, Error))
        {
            GameplayProfileStatus = MoveTemp(Error);
            return FReply::Handled();
        }
        FUnifiedScriptGameplayProfileResult Captured;
        if (!FUnifiedScriptDebugWorkbench::ParseGameplayProfileTrace(
                Json,
                Captured,
                Error))
        {
            GameplayProfileStatus = MoveTemp(Error);
            return FReply::Handled();
        }
        GameplayProfile = MoveTemp(Captured);
        GameplayProfileJson = MoveTemp(Json);
        if (!Component->SetGameplayProfilerEnabled(false, Error))
        {
            GameplayProfileStatus = MoveTemp(Error);
            return FReply::Handled();
        }
        GameplayProfileStatus = TEXT("Profile stopped; the bounded capture remains available.");
    }
    else
    {
        if (!Component->SetGameplayProfilerEnabled(true, Error))
        {
            GameplayProfileStatus = MoveTemp(Error);
            return FReply::Handled();
        }
        GameplayProfile.Reset();
        GameplayProfileJson.Reset();
        GameplayProfileStatus = TEXT("Collecting up to 120 live World frames ...");
    }
    RefreshLiveWorldTargetNow();
    return FReply::Handled();
}

FReply SUnifiedScriptDebugPanel::HandleCaptureGameplayProfile()
{
    if (!CanCaptureGameplayProfile())
    {
        return FReply::Handled();
    }
    FString Json;
    FString Error;
    if (!LiveComponent->CaptureGameplayProfileTrace(Json, Error))
    {
        GameplayProfileStatus = MoveTemp(Error);
        return FReply::Handled();
    }
    FUnifiedScriptGameplayProfileResult Captured;
    if (!FUnifiedScriptDebugWorkbench::ParseGameplayProfileTrace(
            Json,
            Captured,
            Error))
    {
        GameplayProfileStatus = MoveTemp(Error);
        return FReply::Handled();
    }
    GameplayProfile = MoveTemp(Captured);
    GameplayProfileJson = MoveTemp(Json);
    GameplayProfileStatus = TEXT("Captured the current bounded timeline without stopping collection.");
    return FReply::Handled();
}

FReply SUnifiedScriptDebugPanel::HandleFocusHottestProfileNode()
{
    if (const FUnifiedScriptGameplayProfileNode* Node = FindHottestProfileNode())
    {
        OnNodeFocused.ExecuteIfBound(Node->MethodId, Node->NodeId);
    }
    return FReply::Handled();
}

FReply SUnifiedScriptDebugPanel::HandleSaveGameplayProfile()
{
    if (!CanSaveGameplayProfile())
    {
        return FReply::Handled();
    }
    const FString Directory = FPaths::Combine(
        FPaths::ProjectSavedDir(),
        TEXT("UnifiedScript"),
        TEXT("Profiles"));
    if (!IFileManager::Get().MakeDirectory(*Directory, true))
    {
        GameplayProfileStatus = FString::Printf(
            TEXT("Could not create profile directory: %s"),
            *Directory);
        return FReply::Handled();
    }
    const FString TraceName = FPaths::MakeValidFileName(GameplayProfile->TraceId);
    const FString Timestamp = FDateTime::UtcNow().ToString(TEXT("%Y%m%d-%H%M%S"));
    const FString Path = FPaths::Combine(
        Directory,
        FString::Printf(TEXT("%s-%s.json"), *TraceName, *Timestamp));
    if (!FFileHelper::SaveStringToFile(
            GameplayProfileJson,
            *Path,
            FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
    {
        GameplayProfileStatus = FString::Printf(
            TEXT("Could not write gameplay profile: %s"),
            *Path);
        return FReply::Handled();
    }
    GameplayProfileStatus = FString::Printf(TEXT("Saved portable trace: %s"), *Path);
    return FReply::Handled();
}

FReply SUnifiedScriptDebugPanel::HandleFocusRuntimeFailure()
{
    if (CanFocusRuntimeFailure())
    {
        OnNodeFocused.ExecuteIfBound(RuntimeFailureMethodId, RuntimeFailureNodeId);
    }
    return FReply::Handled();
}

FReply SUnifiedScriptDebugPanel::HandleAttachSemanticWorld()
{
    if (!CanAttachSemanticWorld())
    {
        return FReply::Handled();
    }
    FString Json;
    FString Error;
    if (!FUnifiedScriptDebugWorkbench::BuildLiveWorldPublishRequest(
            SourceCompilationId,
            SemanticConfigurationId,
            Json,
            Error))
    {
        SetDebugStatus(MoveTemp(Error), true);
        return FReply::Handled();
    }
    bCommandPending = true;
    SetDebugStatus(TEXT("Publishing the instrumented live World generation ..."));
    DispatchSessionRequest(
        ERequestKind::PublishLiveWorld,
        FUnifiedScriptDebugWorkbench::BuildLiveWorldPublishUrl(GetServerUrl()),
        Json);
    return FReply::Handled();
}

FReply SUnifiedScriptDebugPanel::HandleDetachSemanticWorld()
{
    if (!CanDetachSemanticWorld())
    {
        return FReply::Handled();
    }
    FString Error;
    if (!LiveComponent->DisableSemanticDebug(Error))
    {
        SetDebugStatus(MoveTemp(Error), true);
        return FReply::Handled();
    }
    bSessionIsLiveWorld = false;
    Session.Reset();
    SelectedFrameId = 0;
    RebuildFrameAndLocalViews();
    SetDebugStatus(TEXT("Detached from the live World semantic generation."));
    RefreshLiveWorldTargetNow();
    OnDebugStateChanged.ExecuteIfBound();
    return FReply::Handled();
}

FReply SUnifiedScriptDebugPanel::HandleRun()
{
    if (!CanRun())
    {
        return FReply::Handled();
    }
    FString Json;
    FString Error;
    if (!FUnifiedScriptDebugWorkbench::BuildStartRequest(
            SourceCompilationId,
            SemanticConfigurationId,
            ActiveFunction.GetValue(),
            ActiveAsset.GetValue(),
            BreakpointNodeIds,
            Json,
            Error))
    {
        SetDebugStatus(MoveTemp(Error), true);
        return FReply::Handled();
    }
    bCommandPending = true;
    SetDebugStatus(TEXT("Starting the current Visual function on CoreCLR ..."));
    DispatchSessionRequest(
        ERequestKind::Start,
        FUnifiedScriptDebugWorkbench::BuildStartUrl(GetServerUrl()),
        Json);
    return FReply::Handled();
}

FReply SUnifiedScriptDebugPanel::HandlePause()
{
    if (IsLiveSemanticSession())
    {
        ControlLiveSemantic(EUnifiedScriptSemanticDebugControl::Pause);
        return FReply::Handled();
    }
    if (!CanPause())
    {
        return FReply::Handled();
    }
    FString Json;
    FString Error;
    if (!FUnifiedScriptDebugWorkbench::BuildExecutionRequest(
            Session.GetValue(), Json, Error))
    {
        SetDebugStatus(MoveTemp(Error), true);
        return FReply::Handled();
    }
    bCommandPending = true;
    SetDebugStatus(TEXT("Pause requested; waiting for the next managed safe point ..."));
    DispatchSessionRequest(
        ERequestKind::Pause,
        FUnifiedScriptDebugWorkbench::BuildPauseUrl(GetServerUrl()),
        Json);
    return FReply::Handled();
}

FReply SUnifiedScriptDebugPanel::HandleContinue()
{
    PendingResumeLabel = TEXT("Continue");
    if (IsLiveSemanticSession())
    {
        ControlLiveSemantic(EUnifiedScriptSemanticDebugControl::Continue);
        return FReply::Handled();
    }
    FString Json;
    FString Error;
    if (!CanResume() ||
        !FUnifiedScriptDebugWorkbench::BuildResumeRequest(Session.GetValue(), Json, Error))
    {
        if (!Error.IsEmpty()) SetDebugStatus(MoveTemp(Error), true);
        return FReply::Handled();
    }
    bCommandPending = true;
    DispatchSessionRequest(
        ERequestKind::Resume,
        FUnifiedScriptDebugWorkbench::BuildContinueUrl(GetServerUrl()),
        Json);
    return FReply::Handled();
}

FReply SUnifiedScriptDebugPanel::HandleStepIn()
{
    PendingResumeLabel = TEXT("Step In");
    if (IsLiveSemanticSession())
    {
        ControlLiveSemantic(EUnifiedScriptSemanticDebugControl::StepInto);
        return FReply::Handled();
    }
    FString Json;
    FString Error;
    if (!CanResume() ||
        !FUnifiedScriptDebugWorkbench::BuildResumeRequest(Session.GetValue(), Json, Error))
    {
        if (!Error.IsEmpty()) SetDebugStatus(MoveTemp(Error), true);
        return FReply::Handled();
    }
    bCommandPending = true;
    DispatchSessionRequest(
        ERequestKind::Resume,
        FUnifiedScriptDebugWorkbench::BuildStepInUrl(GetServerUrl()),
        Json);
    return FReply::Handled();
}

FReply SUnifiedScriptDebugPanel::HandleStepOver()
{
    PendingResumeLabel = TEXT("Step Over");
    if (IsLiveSemanticSession())
    {
        ControlLiveSemantic(EUnifiedScriptSemanticDebugControl::StepOver);
        return FReply::Handled();
    }
    FString Json;
    FString Error;
    if (!CanResume() ||
        !FUnifiedScriptDebugWorkbench::BuildResumeRequest(Session.GetValue(), Json, Error))
    {
        if (!Error.IsEmpty()) SetDebugStatus(MoveTemp(Error), true);
        return FReply::Handled();
    }
    bCommandPending = true;
    DispatchSessionRequest(
        ERequestKind::Resume,
        FUnifiedScriptDebugWorkbench::BuildStepOverUrl(GetServerUrl()),
        Json);
    return FReply::Handled();
}

FReply SUnifiedScriptDebugPanel::HandleStepOut()
{
    PendingResumeLabel = TEXT("Step Out");
    if (IsLiveSemanticSession())
    {
        ControlLiveSemantic(EUnifiedScriptSemanticDebugControl::StepOut);
        return FReply::Handled();
    }
    FString Json;
    FString Error;
    if (!CanStepOut() ||
        !FUnifiedScriptDebugWorkbench::BuildResumeRequest(Session.GetValue(), Json, Error))
    {
        if (!Error.IsEmpty()) SetDebugStatus(MoveTemp(Error), true);
        return FReply::Handled();
    }
    bCommandPending = true;
    DispatchSessionRequest(
        ERequestKind::Resume,
        FUnifiedScriptDebugWorkbench::BuildStepOutUrl(GetServerUrl()),
        Json);
    return FReply::Handled();
}

FReply SUnifiedScriptDebugPanel::HandleEvaluate()
{
    if (!CanEvaluate())
    {
        return FReply::Handled();
    }
    if (IsLiveSemanticSession())
    {
        FString Result;
        if (EvaluateLiveSemanticWatch(WatchTextBox->GetText().ToString(), Result))
        {
            EvaluationText = MoveTemp(Result);
        }
        else
        {
            EvaluationText = MoveTemp(Result);
        }
        return FReply::Handled();
    }
    FString Json;
    FString Error;
    if (!FUnifiedScriptDebugWorkbench::BuildEvaluateRequest(
            Session.GetValue(),
            SelectedFrameId,
            WatchTextBox->GetText().ToString(),
            Json,
            Error))
    {
        EvaluationText = MoveTemp(Error);
        return FReply::Handled();
    }
    bCommandPending = true;
    EvaluationText = TEXT("Evaluating detached snapshot ...");
    const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request =
        FHttpModule::Get().CreateRequest();
    Request->SetURL(FUnifiedScriptDebugWorkbench::BuildEvaluateUrl(GetServerUrl()));
    Request->SetVerb(TEXT("POST"));
    Request->SetHeader(TEXT("Content-Type"), TEXT("application/json; charset=utf-8"));
    Request->SetContentAsString(Json);
    const TWeakPtr<SUnifiedScriptDebugPanel> WeakThis = SharedThis(this);
    Request->OnProcessRequestComplete().BindLambda(
        [WeakThis](FHttpRequestPtr, FHttpResponsePtr Response, bool bSucceeded)
        {
            if (const TSharedPtr<SUnifiedScriptDebugPanel> Pinned = WeakThis.Pin())
            {
                Pinned->HandleEvaluationResponse(
                    bSucceeded && Response.IsValid(),
                    Response.IsValid() ? Response->GetResponseCode() : 0,
                    Response.IsValid() ? Response->GetContentAsString() : FString());
            }
        });
    if (!Request->ProcessRequest())
    {
        bCommandPending = false;
        EvaluationText = TEXT("The watch request could not be started.");
    }
    return FReply::Handled();
}

void SUnifiedScriptDebugPanel::DispatchSessionRequest(
    ERequestKind Kind,
    const FString& Url,
    const FString& Json)
{
    if (Url.IsEmpty())
    {
        bCommandPending = false;
        bWaitPending = false;
        SetDebugStatus(TEXT("Enter a valid Workbench URL before debugging."), true);
        return;
    }
    const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request =
        FHttpModule::Get().CreateRequest();
    Request->SetURL(Url);
    Request->SetVerb(TEXT("POST"));
    Request->SetHeader(TEXT("Content-Type"), TEXT("application/json; charset=utf-8"));
    Request->SetContentAsString(Json);
    const TWeakPtr<SUnifiedScriptDebugPanel> WeakThis = SharedThis(this);
    Request->OnProcessRequestComplete().BindLambda(
        [WeakThis, Kind](FHttpRequestPtr, FHttpResponsePtr Response, bool bSucceeded)
        {
            if (const TSharedPtr<SUnifiedScriptDebugPanel> Pinned = WeakThis.Pin())
            {
                Pinned->HandleSessionResponse(
                    Kind,
                    bSucceeded && Response.IsValid(),
                    Response.IsValid() ? Response->GetResponseCode() : 0,
                    Response.IsValid() ? Response->GetContentAsString() : FString());
            }
        });
    if (!Request->ProcessRequest())
    {
        if (Kind == ERequestKind::Wait)
        {
            bWaitPending = false;
        }
        else
        {
            bCommandPending = false;
        }
        SetDebugStatus(TEXT("The CoreCLR debug request could not be started."), true);
    }
}

void SUnifiedScriptDebugPanel::DispatchWait()
{
    if (!Session.IsSet() || !Session->IsRunning() || bWaitPending)
    {
        return;
    }
    FString Json;
    FString Error;
    if (!FUnifiedScriptDebugWorkbench::BuildExecutionRequest(Session.GetValue(), Json, Error))
    {
        SetDebugStatus(MoveTemp(Error), true);
        return;
    }
    bWaitPending = true;
    DispatchSessionRequest(
        ERequestKind::Wait,
        FUnifiedScriptDebugWorkbench::BuildWaitUrl(GetServerUrl()),
        Json);
}

void SUnifiedScriptDebugPanel::HandleSessionResponse(
    ERequestKind Kind,
    bool bSucceeded,
    int32 StatusCode,
    FString ResponseJson)
{
    if (Kind == ERequestKind::PublishLiveWorld)
    {
        bCommandPending = false;
        HandleLiveWorldPublishResponse(
            bSucceeded,
            StatusCode,
            MoveTemp(ResponseJson));
        return;
    }
    if (Kind == ERequestKind::Wait)
    {
        bWaitPending = false;
    }
    else
    {
        bCommandPending = false;
    }
    if (!bSucceeded || StatusCode < 200 || StatusCode >= 300)
    {
        SetDebugStatus(DescribeHttpFailure(StatusCode, ResponseJson), true);
        return;
    }
    FUnifiedScriptDebugSession Response;
    FString Error;
    if (!FUnifiedScriptDebugWorkbench::ParseSessionResponse(ResponseJson, Response, Error))
    {
        SetDebugStatus(MoveTemp(Error), true);
        return;
    }
    if (!SourceCompilationId.IsEmpty() &&
        (Response.Generation.SourceCompilationId != SourceCompilationId ||
         Response.Generation.SemanticConfigurationId != SemanticConfigurationId))
    {
        SetDebugStatus(
            TEXT("The Workbench debug generation does not match the loaded Slate Surface."),
            true);
        return;
    }
    if (Kind == ERequestKind::Pause)
    {
        if (!Session.IsSet() || !Session->IsPaused())
        {
            SetDebugStatus(TEXT("Pause requested; waiting for the next managed safe point ..."));
        }
        if (!bWaitPending)
        {
            DispatchWait();
        }
        return;
    }
    bSessionIsLiveWorld = false;
    AdoptSession(MoveTemp(Response));
    if (Session.IsSet() && Session->IsRunning())
    {
        DispatchWait();
    }
}

void SUnifiedScriptDebugPanel::HandleEvaluationResponse(
    bool bSucceeded,
    int32 StatusCode,
    FString ResponseJson)
{
    bCommandPending = false;
    if (!bSucceeded || StatusCode < 200 || StatusCode >= 300)
    {
        EvaluationText = DescribeHttpFailure(StatusCode, ResponseJson);
        return;
    }
    FUnifiedScriptDebugEvaluation Evaluation;
    FString Error;
    if (!FUnifiedScriptDebugWorkbench::ParseEvaluationResponse(
            ResponseJson, Evaluation, Error))
    {
        EvaluationText = MoveTemp(Error);
        return;
    }
    if (!Session.IsSet() || !Session->IsPaused() || !Session->Pause.IsSet() ||
        Evaluation.Generation.GenerationId != Session->Generation.GenerationId ||
        Evaluation.PauseId != Session->Pause->PauseId ||
        Evaluation.FrameId != SelectedFrameId)
    {
        EvaluationText = TEXT("The watch response belongs to a stale pause or frame.");
        return;
    }
    EvaluationText = FString::Printf(
        TEXT("%s = %s\n%s"),
        *Evaluation.Expression,
        *Evaluation.Value.Display,
        *Evaluation.Value.TypeMetadataName);
}

void SUnifiedScriptDebugPanel::HandleLiveWorldPublishResponse(
    bool bSucceeded,
    int32 StatusCode,
    FString ResponseJson)
{
    if (!bSucceeded || StatusCode < 200 || StatusCode >= 300)
    {
        SetDebugStatus(DescribeHttpFailure(StatusCode, ResponseJson), true);
        return;
    }
    if (!LiveComponent.IsValid() || !LiveContext.IsSet())
    {
        SetDebugStatus(TEXT("The selected live World target disappeared during publish."), true);
        return;
    }

    FUnifiedScriptLiveWorldDebugPublish Publish;
    FString Error;
    if (!FUnifiedScriptDebugWorkbench::ParseLiveWorldPublishResponse(
            ResponseJson,
            Publish,
            Error))
    {
        SetDebugStatus(MoveTemp(Error), true);
        return;
    }
    if (Publish.Generation.SourceCompilationId != SourceCompilationId ||
        Publish.Generation.SemanticConfigurationId != SemanticConfigurationId ||
        !FPaths::IsSamePath(Publish.AssemblyPath, LiveContext->AssemblyPath))
    {
        SetDebugStatus(
            TEXT("The published debug image does not match the selected Surface or live component assembly."),
            true);
        return;
    }
    if (!LiveComponent->ReloadScriptWithTransaction(Publish.ReloadTransactionJson))
    {
        FString Status = LiveComponent->LastScriptError.IsEmpty()
            ? TEXT("The live component rejected the instrumented generation.")
            : LiveComponent->LastScriptError;
        FUnifiedScriptReloadTransactionResult FailedTransaction;
        FString TransactionError;
        if (FUnifiedScriptAuthoringWorkbench::ParseReloadTransaction(
                LiveComponent->LastReloadTransaction,
                FailedTransaction,
                TransactionError))
        {
            Status += TEXT("\n");
            Status += FailedTransaction.ToDisplayString();
        }
        SetDebugStatus(MoveTemp(Status), true);
        return;
    }

    FUnifiedScriptRuntimeDebugContext ReloadedContext;
    if (!LiveComponent->GetRuntimeDebugContext(ReloadedContext))
    {
        SetDebugStatus(TEXT("The reloaded component has no live runtime identity."), true);
        return;
    }
    FString ConfigurationJson;
    if (!FUnifiedScriptDebugWorkbench::BuildLiveWorldConfiguration(
            Publish,
            ReloadedContext.Generation,
            BreakpointNodeIds,
            ConfigurationJson,
            Error) ||
        !LiveComponent->ConfigureSemanticDebug(ConfigurationJson, Error))
    {
        SetDebugStatus(MoveTemp(Error), true);
        return;
    }

    bSessionIsLiveWorld = true;
    Session.Reset();
    SelectedFrameId = 0;
    RebuildFrameAndLocalViews();
    FString Status = FString::Printf(
        TEXT("Attached live World instance %llu generation %d · %d semantic breakpoint%s"),
        ReloadedContext.InstanceId,
        ReloadedContext.Generation,
        BreakpointNodeIds.Num(),
        BreakpointNodeIds.Num() == 1 ? TEXT("") : TEXT("s"));
    FUnifiedScriptReloadTransactionResult ObservedTransaction;
    FString TransactionError;
    if (FUnifiedScriptAuthoringWorkbench::ParseReloadTransaction(
            LiveComponent->LastReloadTransaction,
            ObservedTransaction,
            TransactionError))
    {
        Status += TEXT("\n");
        Status += ObservedTransaction.ToDisplayString();
    }
    SetDebugStatus(MoveTemp(Status));
    RefreshLiveWorldTargetNow();
}

bool SUnifiedScriptDebugPanel::ControlLiveSemantic(
    EUnifiedScriptSemanticDebugControl Control)
{
    if (!LiveComponent.IsValid() || !Session.IsSet())
    {
        return false;
    }
    const FString PauseId = Session->Pause.IsSet()
        ? Session->Pause->PauseId
        : FString();
    FString Error;
    if (!LiveComponent->ControlSemanticDebug(Control, PauseId, Error))
    {
        SetDebugStatus(MoveTemp(Error), true);
        return false;
    }
    const TCHAR* Label = Control == EUnifiedScriptSemanticDebugControl::Pause
        ? TEXT("Pause requested at the next live semantic probe")
        : Control == EUnifiedScriptSemanticDebugControl::Continue
            ? TEXT("Continuing the suspended live World CLR stack")
            : Control == EUnifiedScriptSemanticDebugControl::StepInto
                ? TEXT("Stepping into the next live semantic probe")
                : Control == EUnifiedScriptSemanticDebugControl::StepOver
                    ? TEXT("Stepping over on the suspended live CLR stack")
                    : TEXT("Stepping out on the suspended live CLR stack");
    SetDebugStatus(Label);
    RefreshLiveWorldTargetNow();
    return true;
}

bool SUnifiedScriptDebugPanel::EvaluateLiveSemanticWatch(
    const FString& Expression,
    FString& OutResult) const
{
    const FString Trimmed = Expression.TrimStartAndEnd();
    if (!Session.IsSet() || !Session->IsPaused() || SelectedFrameId < 1 ||
        Trimmed.IsEmpty() || Trimmed.Len() > 256)
    {
        OutResult = TEXT("Live watch requires a paused frame and 1-256 characters.");
        return false;
    }
    const FUnifiedScriptDebugFrameLocals* Frame = Session->FrameLocals.FindByPredicate(
        [this](const FUnifiedScriptDebugFrameLocals& Candidate)
        {
            return Candidate.FrameId == SelectedFrameId;
        });
    if (Frame == nullptr)
    {
        OutResult = TEXT("The selected live frame has no detached locals.");
        return false;
    }

    int32 Cursor = 0;
    while (Cursor < Trimmed.Len() && Trimmed[Cursor] != TEXT('.') &&
        Trimmed[Cursor] != TEXT('['))
    {
        ++Cursor;
    }
    const FString RootName = Trimmed.Left(Cursor);
    const auto IsIdentifier = [](const FString& Candidate)
    {
        if (Candidate.IsEmpty() ||
            !(FChar::IsAlpha(Candidate[0]) || Candidate[0] == TEXT('_')))
        {
            return false;
        }
        for (int32 Index = 1; Index < Candidate.Len(); ++Index)
        {
            if (!(FChar::IsAlnum(Candidate[Index]) || Candidate[Index] == TEXT('_')))
            {
                return false;
            }
        }
        return true;
    };
    if (!IsIdentifier(RootName))
    {
        OutResult = TEXT("Live watch must begin with a local or parameter name.");
        return false;
    }
    const FUnifiedScriptDebugValue* Value = Frame->Locals.FindByPredicate(
        [&RootName](const FUnifiedScriptDebugValue& Candidate)
        {
            return Candidate.Name == RootName;
        });
    int32 SegmentCount = 0;
    while (Value != nullptr && Cursor < Trimmed.Len())
    {
        if (++SegmentCount > 16)
        {
            OutResult = TEXT("Live watch paths are limited to 16 detached segments.");
            return false;
        }
        FString ChildName;
        if (Trimmed[Cursor] == TEXT('.'))
        {
            const int32 Start = ++Cursor;
            while (Cursor < Trimmed.Len() && Trimmed[Cursor] != TEXT('.') &&
                Trimmed[Cursor] != TEXT('['))
            {
                ++Cursor;
            }
            ChildName = Trimmed.Mid(Start, Cursor - Start);
            if (!IsIdentifier(ChildName))
            {
                Value = nullptr;
                break;
            }
        }
        else if (Trimmed[Cursor] == TEXT('['))
        {
            const int32 Close = Trimmed.Find(TEXT("]"), ESearchCase::CaseSensitive,
                ESearchDir::FromStart, Cursor + 1);
            if (Close == INDEX_NONE)
            {
                Value = nullptr;
                break;
            }
            const FString IndexText = Trimmed.Mid(Cursor + 1, Close - Cursor - 1);
            if (IndexText.IsEmpty() ||
                !IndexText.Equals(FString::FromInt(FCString::Atoi(*IndexText))) ||
                FCString::Atoi(*IndexText) < 0)
            {
                Value = nullptr;
                break;
            }
            ChildName = FString::Printf(TEXT("[%d]"), FCString::Atoi(*IndexText));
            Cursor = Close + 1;
        }
        else
        {
            Value = nullptr;
            break;
        }
        if (ChildName.IsEmpty())
        {
            Value = nullptr;
            break;
        }
        Value = Value->Children.FindByPredicate(
            [&ChildName](const FUnifiedScriptDebugValue& Candidate)
            {
                return Candidate.Name == ChildName;
            });
    }
    if (Value == nullptr)
    {
        OutResult = FString::Printf(
            TEXT("'%s' is not available in the detached live frame snapshot."),
            *Trimmed);
        return false;
    }
    OutResult = FString::Printf(
        TEXT("%s = %s\n%s · detached read-only live snapshot"),
        *Trimmed,
        *Value->Display,
        *Value->TypeMetadataName);
    return true;
}

void SUnifiedScriptDebugPanel::AdoptSession(FUnifiedScriptDebugSession NewSession)
{
    Session = MoveTemp(NewSession);
    SelectedFrameId = 0;
    RebuildFrameAndLocalViews();
    if (Session->IsRunning())
    {
        const FString Action = PendingResumeLabel.IsEmpty() ? TEXT("Running") : PendingResumeLabel;
        PendingResumeLabel.Reset();
        int32 BoundCount = 0;
        int32 PendingCount = 0;
        for (const FUnifiedScriptDebugBreakpointBinding& Binding : Session->Breakpoints)
        {
            BoundCount += Binding.State == TEXT("bound") ? 1 : 0;
            PendingCount += Binding.State == TEXT("pending") ? 1 : 0;
        }
        SetDebugStatus(FString::Printf(
            TEXT("%s · execution %lld · generation %lld · %d bound / %d pending"),
            *Action,
            Session->ExecutionId,
            Session->Generation.Number,
            BoundCount,
            PendingCount));
    }
    else if (Session->IsPaused() && Session->Pause.IsSet())
    {
        SetDebugStatus(FString::Printf(
            TEXT("Paused: %s%s · generation %lld · %d frame%s"),
            *Session->Pause->Reason,
            Session->Pause->StepKind.IsEmpty()
                ? TEXT("")
                : *FString::Printf(TEXT("/%s"), *Session->Pause->StepKind),
            Session->Generation.Number,
            Session->Frames.Num(),
            Session->Frames.Num() == 1 ? TEXT("") : TEXT("s")));
        if (!Session->Frames.IsEmpty())
        {
            SelectFrame(Session->Frames[0].FrameId);
        }
    }
    else if (Session->IsCompleted() && Session->Result.IsSet())
    {
        const FUnifiedScriptDebugResult& Result = Session->Result.GetValue();
        SetDebugStatus(
            Result.Outcome == TEXT("returned")
                ? FString::Printf(
                    TEXT("Completed generation %lld · returned %s"),
                    Session->Generation.Number,
                    Result.ReturnJson.IsEmpty() ? TEXT("<void>") : *Result.ReturnJson)
                : FString::Printf(
                    TEXT("Faulted generation %lld · %s: %s"),
                    Session->Generation.Number,
                    *Result.FaultTypeMetadataName,
                    *Result.FaultMessage),
            Result.Outcome != TEXT("returned"));
    }
    OnDebugStateChanged.ExecuteIfBound();
}

void SUnifiedScriptDebugPanel::SelectFrame(int64 FrameId)
{
    if (!Session.IsSet() || !Session->IsPaused())
    {
        return;
    }
    const FUnifiedScriptDebugStackFrame* Frame = Session->Frames.FindByPredicate(
        [FrameId](const FUnifiedScriptDebugStackFrame& Candidate)
        {
            return Candidate.FrameId == FrameId;
        });
    if (Frame == nullptr)
    {
        return;
    }
    SelectedFrameId = FrameId;
    LocalItems.Reset();
    if (const FUnifiedScriptDebugFrameLocals* Locals = Session->FrameLocals.FindByPredicate(
            [FrameId](const FUnifiedScriptDebugFrameLocals& Candidate)
            {
                return Candidate.FrameId == FrameId;
            }))
    {
        for (const FUnifiedScriptDebugValue& Value : Locals->Locals)
        {
            LocalItems.Add(MakeShared<FUnifiedScriptDebugValue>(Value));
        }
    }
    if (LocalListView.IsValid())
    {
        LocalListView->RequestListRefresh();
    }
    OnNodeFocused.ExecuteIfBound(Frame->MethodId, Frame->NodeId);
}

void SUnifiedScriptDebugPanel::RebuildFrameAndLocalViews()
{
    FrameItems.Reset();
    LocalItems.Reset();
    if (Session.IsSet() && Session->IsPaused())
    {
        for (const FUnifiedScriptDebugStackFrame& Frame : Session->Frames)
        {
            FrameItems.Add(MakeShared<FUnifiedScriptDebugStackFrame>(Frame));
        }
    }
    if (FrameListView.IsValid())
    {
        FrameListView->RequestListRefresh();
        FrameListView->ClearSelection();
    }
    if (LocalListView.IsValid())
    {
        LocalListView->RequestListRefresh();
    }
}

void SUnifiedScriptDebugPanel::HandleFrameSelectionChanged(
    FDebugFrameItem Item,
    ESelectInfo::Type SelectInfo)
{
    if (Item.IsValid())
    {
        SelectFrame(Item->FrameId);
    }
}

TSharedRef<ITableRow> SUnifiedScriptDebugPanel::GenerateFrameRow(
    FDebugFrameItem Item,
    const TSharedRef<STableViewBase>& OwnerTable) const
{
    const FString Location = Item.IsValid() && Item->SourceSpan.IsSet()
        ? FString::Printf(TEXT("L%d"), Item->SourceSpan->StartLine)
        : Item.IsValid() ? FString::Printf(TEXT("IL %d"), Item->IlOffset) : FString();
    return SNew(STableRow<FDebugFrameItem>, OwnerTable)
    [
        SNew(STextBlock)
        .Text(FText::FromString(Item.IsValid()
            ? FString::Printf(
                TEXT("#%d  %s  ·  %s%s"),
                Item->Index,
                *Item->MethodMetadataName,
                *Location,
                Item->bHasLocals ? TEXT("  ◆") : TEXT(""))
            : FString()))
        .Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
        .ToolTipText(FText::FromString(
            Item.IsValid() && Item->SourceSpan.IsSet()
                ? Item->SourceSpan->ToDisplayString()
                : FString()))
    ];
}

TSharedRef<ITableRow> SUnifiedScriptDebugPanel::GenerateValueRow(
    FDebugValueItem Item,
    const TSharedRef<STableViewBase>& OwnerTable) const
{
    return SNew(STableRow<FDebugValueItem>, OwnerTable)
    [
        SNew(STextBlock)
        .Text(FText::FromString(Item.IsValid()
            ? FString::Printf(TEXT("%s = %s"), *Item->Name, *Item->Display)
            : FString()))
        .Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
        .ToolTipText(FText::FromString(Item.IsValid() ? Item->TypeMetadataName : FString()))
    ];
}

void SUnifiedScriptDebugPanel::SetDebugStatus(FString Message, bool bError)
{
    DebugStatus = MoveTemp(Message);
    bDebugStatusError = bError;
}

FString SUnifiedScriptDebugPanel::GetServerUrl() const
{
    return ServerUrlAttribute.IsSet() ? ServerUrlAttribute.Get() : FString();
}

bool SUnifiedScriptDebugPanel::HasActiveSession() const
{
    return Session.IsSet() && !Session->IsCompleted();
}

bool SUnifiedScriptDebugPanel::CanRun() const
{
    return ActiveFunction.IsSet() && ActiveAsset.IsSet() && !HasActiveSession() &&
        !IsLiveSemanticSession() && !bCommandPending && !bWaitPending &&
        !GetServerUrl().IsEmpty();
}

bool SUnifiedScriptDebugPanel::CanPause() const
{
    return Session.IsSet() && Session->IsRunning() && !bCommandPending;
}

bool SUnifiedScriptDebugPanel::CanResume() const
{
    return Session.IsSet() && Session->IsPaused() && !bCommandPending && !bWaitPending;
}

bool SUnifiedScriptDebugPanel::CanStepOut() const
{
    return CanResume() && Session->Frames.Num() > 1;
}

bool SUnifiedScriptDebugPanel::CanEvaluate() const
{
    return CanResume() && SelectedFrameId > 0 && WatchTextBox.IsValid() &&
        !WatchTextBox->GetText().IsEmpty();
}

FString SUnifiedScriptDebugPanel::DescribeHttpFailure(
    int32 StatusCode,
    const FString& ResponseJson) const
{
    if (StatusCode > 0 && !ResponseJson.IsEmpty())
    {
        return FUnifiedScriptAuthoringWorkbench::ParseErrorResponse(ResponseJson, StatusCode);
    }
    return TEXT("The CoreCLR debug bridge is offline or did not return a response.");
}

EActiveTimerReturnType SUnifiedScriptDebugPanel::RefreshLiveWorldTarget(
    double CurrentTime,
    float DeltaTime)
{
    RefreshLiveWorldTargetNow();
    return EActiveTimerReturnType::Continue;
}

void SUnifiedScriptDebugPanel::RefreshLiveWorldTargetNow()
{
    UUnifiedScriptComponent* BestComponent = nullptr;
    FUnifiedScriptRuntimeDebugContext BestContext;
    int32 BestScore = MIN_int32;
    if (ActiveAsset.IsSet())
    {
        for (TObjectIterator<UUnifiedScriptComponent> It; It; ++It)
        {
            UUnifiedScriptComponent* Component = *It;
            if (!IsValid(Component) || Component->IsTemplate() || Component->GetWorld() == nullptr)
            {
                continue;
            }
            FUnifiedScriptRuntimeDebugContext Candidate;
            if (!Component->GetRuntimeDebugContext(Candidate))
            {
                continue;
            }
            const UUnifiedScriptAsset* ScriptAsset = Component->ScriptAsset;
            const FString ConfiguredType = ScriptAsset != nullptr
                ? ScriptAsset->ScriptTypeName
                : Component->ScriptTypeName;
            if (Candidate.TypeName != ActiveAsset->QualifiedName &&
                ConfiguredType != ActiveAsset->QualifiedName)
            {
                continue;
            }
            int32 Score = Candidate.WorldType == TEXT("PIE") ? 100 : 0;
            Score += Candidate.bRunning ? 20 : 0;
            Score += Candidate.bPausedAtFrameBoundary ? 5 : 0;
            Score += Candidate.LastDiagnostic.IsValid() ? 1 : 0;
            if (BestComponent == nullptr || Score > BestScore ||
                (Score == BestScore && Candidate.WorldHostId < BestContext.WorldHostId))
            {
                BestComponent = Component;
                BestContext = MoveTemp(Candidate);
                BestScore = Score;
            }
        }
    }

    const UUnifiedScriptComponent* PreviousComponent = LiveComponent.Get();
    const int64 PreviousSequence = LastRuntimeDiagnosticSequence;
    if (BestComponent == nullptr)
    {
        LiveComponent.Reset();
        LiveContext.Reset();
        LastRuntimeDiagnosticSequence = 0;
        RuntimeFailureMethodId.Reset();
        RuntimeFailureNodeId.Reset();
        LiveRuntimeStatus = TEXT("No matching PIE/Game World script is running.");
        LastSemanticDebugStateJson.Reset();
        if (bSessionIsLiveWorld)
        {
            bSessionIsLiveWorld = false;
            Session.Reset();
            RebuildFrameAndLocalViews();
        }
    }
    else
    {
        LiveComponent = BestComponent;
        LiveContext = MoveTemp(BestContext);
        LastRuntimeDiagnosticSequence = LiveContext->LastDiagnostic.IsValid()
            ? LiveContext->LastDiagnostic.Sequence
            : 0;
        MapRuntimeDiagnosticToGraph();
        AdoptLiveSemanticState();
    }
    if (PreviousComponent != LiveComponent.Get() ||
        PreviousSequence != LastRuntimeDiagnosticSequence)
    {
        if (PreviousComponent != LiveComponent.Get())
        {
            GameplayProfile.Reset();
            GameplayProfileJson.Reset();
            GameplayProfileStatus.Reset();
        }
        OnDebugStateChanged.ExecuteIfBound();
    }
}

void SUnifiedScriptDebugPanel::MapRuntimeDiagnosticToGraph()
{
    RuntimeFailureMethodId.Reset();
    RuntimeFailureNodeId.Reset();
    if (!LiveContext.IsSet() || !ActiveAsset.IsSet() ||
        (!LiveContext->LastDiagnostic.Source.IsValid() &&
         LiveContext->LastDiagnostic.SourceFrames.IsEmpty()))
    {
        return;
    }
    TArray<FUnifiedScriptRuntimeSourceLocation> Sources =
        LiveContext->LastDiagnostic.SourceFrames;
    if (Sources.IsEmpty())
    {
        Sources.Add(LiveContext->LastDiagnostic.Source);
    }
    FUnifiedScriptDebugWorkbench::ResolveRuntimeDiagnosticNode(
        ActiveAsset.GetValue(),
        Sources,
        RuntimeFailureMethodId,
        RuntimeFailureNodeId);
}

bool SUnifiedScriptDebugPanel::CanPauseLiveWorld() const
{
    return LiveComponent.IsValid() && LiveContext.IsSet() && LiveContext->bRunning &&
        !LiveContext->bSemanticDebugEnabled && !LiveContext->bPausedAtFrameBoundary;
}

bool SUnifiedScriptDebugPanel::CanStepLiveWorld() const
{
    return LiveComponent.IsValid() && LiveContext.IsSet() && LiveContext->bRunning &&
        !LiveContext->bSemanticDebugEnabled && LiveContext->bPausedAtFrameBoundary;
}

bool SUnifiedScriptDebugPanel::CanContinueLiveWorld() const
{
    return CanStepLiveWorld();
}

bool SUnifiedScriptDebugPanel::CanControlGameplayProfiler() const
{
    return LiveComponent.IsValid() && LiveContext.IsSet() &&
        LiveContext->bRunning && !bCommandPending;
}

bool SUnifiedScriptDebugPanel::CanCaptureGameplayProfile() const
{
    return CanControlGameplayProfiler() &&
        LiveComponent->bGameplayProfilerEnabled;
}

bool SUnifiedScriptDebugPanel::CanFocusHottestProfileNode() const
{
    return FindHottestProfileNode() != nullptr && OnNodeFocused.IsBound();
}

bool SUnifiedScriptDebugPanel::CanSaveGameplayProfile() const
{
    return GameplayProfile.IsSet() && !GameplayProfileJson.IsEmpty();
}

bool SUnifiedScriptDebugPanel::CanFocusRuntimeFailure() const
{
    return !RuntimeFailureMethodId.IsEmpty() && !RuntimeFailureNodeId.IsEmpty();
}

bool SUnifiedScriptDebugPanel::CanAttachSemanticWorld() const
{
    return LiveComponent.IsValid() && LiveContext.IsSet() && LiveContext->bRunning &&
        !LiveContext->bSemanticDebugEnabled && ActiveAsset.IsSet() &&
        !SourceCompilationId.IsEmpty() && !SemanticConfigurationId.IsEmpty() &&
        !HasActiveSession() && !bCommandPending && !bWaitPending &&
        !GetServerUrl().IsEmpty();
}

bool SUnifiedScriptDebugPanel::CanDetachSemanticWorld() const
{
    return LiveComponent.IsValid() && LiveContext.IsSet() &&
        LiveContext->bSemanticDebugEnabled && !bCommandPending;
}

bool SUnifiedScriptDebugPanel::IsLiveSemanticSession() const
{
    return bSessionIsLiveWorld && LiveContext.IsSet() &&
        LiveContext->bSemanticDebugEnabled;
}

void SUnifiedScriptDebugPanel::AdoptLiveSemanticState()
{
    if (!LiveContext.IsSet() || !LiveContext->bSemanticDebugEnabled)
    {
        if (bSessionIsLiveWorld)
        {
            bSessionIsLiveWorld = false;
            LastSemanticDebugStateJson.Reset();
            Session.Reset();
            RebuildFrameAndLocalViews();
        }
        return;
    }
    if (LiveContext->SemanticDebugStateJson.IsEmpty() ||
        LiveContext->SemanticDebugStateJson == LastSemanticDebugStateJson)
    {
        return;
    }

    FUnifiedScriptDebugSession LiveSession;
    FString Error;
    if (!FUnifiedScriptDebugWorkbench::ParseSessionResponse(
            LiveContext->SemanticDebugStateJson,
            LiveSession,
            Error))
    {
        SetDebugStatus(MoveTemp(Error), true);
        return;
    }
    if (LiveSession.Generation.SourceCompilationId != SourceCompilationId ||
        LiveSession.Generation.SemanticConfigurationId != SemanticConfigurationId)
    {
        SetDebugStatus(
            TEXT("The live World semantic generation no longer matches the Slate Surface."),
            true);
        return;
    }
    LastSemanticDebugStateJson = LiveContext->SemanticDebugStateJson;
    bSessionIsLiveWorld = true;
    AdoptSession(MoveTemp(LiveSession));
}

FString SUnifiedScriptDebugPanel::DescribeLiveWorldTarget() const
{
    if (!LiveContext.IsSet())
    {
        return LiveRuntimeStatus;
    }
    return FString::Printf(
        TEXT("%s %s · World %llu · Instance %llu · Gen %d · %s · Target 0x%016llX"),
        *LiveContext->WorldType,
        *LiveContext->WorldName,
        LiveContext->WorldHostId,
        LiveContext->InstanceId,
        LiveContext->Generation,
        LiveContext->bSemanticDebugPaused
            ? TEXT("SEMANTIC-PAUSED")
            : LiveContext->bSemanticDebugActive
                ? TEXT("SEMANTIC-RUNNING")
                : LiveContext->bSemanticDebugEnabled
                    ? TEXT("SEMANTIC-ATTACHED")
                    : LiveContext->bPausedAtFrameBoundary
                        ? TEXT("FRAME-PAUSED")
            : LiveContext->bRunning ? TEXT("RUNNING") : TEXT("STOPPED"),
        LiveContext->TargetHandle);
}

FString SUnifiedScriptDebugPanel::DescribeRuntimeDiagnostic() const
{
    if (!LiveContext.IsSet() || !LiveContext->LastDiagnostic.IsValid())
    {
        return FString();
    }
    const FUnifiedScriptRuntimeDiagnostic& Diagnostic = LiveContext->LastDiagnostic;
    const FString Source = Diagnostic.Source.IsValid()
        ? Diagnostic.Source.ToDisplayString()
        : TEXT("no portable-PDB source");
    const FString Bridge = Diagnostic.Bridge.IsSet()
        ? FString::Printf(
            TEXT(" · Bridge %s/%s · %s"),
            *Diagnostic.Bridge.Error,
            *Diagnostic.Bridge.HandleError,
            Diagnostic.Bridge.MemberPath.IsEmpty()
                ? TEXT("unknown member")
                : *Diagnostic.Bridge.MemberPath)
        : FString();
    return FString::Printf(
        TEXT("%s · %s · %s · %s%s"),
        *Diagnostic.Operation,
        *Diagnostic.ExceptionType,
        *Diagnostic.Message,
        *Source,
        *Bridge);
}

FString SUnifiedScriptDebugPanel::DescribeGameplayProfile() const
{
    if (!GameplayProfile.IsSet())
    {
        return GameplayProfileStatus;
    }
    FString Result = GameplayProfile->ToDisplayString();
    if (GameplayProfile->DroppedFrameCount > 0)
    {
        Result += FString::Printf(
            TEXT(" · dropped %lld old frame%s"),
            static_cast<long long>(GameplayProfile->DroppedFrameCount),
            GameplayProfile->DroppedFrameCount == 1 ? TEXT("") : TEXT("s"));
    }
    if (const FUnifiedScriptGameplayProfileNode* Node = FindHottestProfileNode())
    {
        FString NodeLabel = Node->NodeId;
        if (ActiveAsset.IsSet())
        {
            for (const FUnifiedScriptAuthoringFunction& Function : ActiveAsset->Functions)
            {
                if (Function.Id != Node->MethodId)
                {
                    continue;
                }
                if (const FUnifiedScriptAuthoringAction* Action =
                        Function.Actions.FindByPredicate(
                            [Node](const FUnifiedScriptAuthoringAction& Candidate)
                            {
                                return Candidate.DebugTargetId == Node->NodeId;
                            }))
                {
                    NodeLabel = Action->Title;
                }
                break;
            }
        }
        Result += FString::Printf(
            TEXT(" · Hot %s %.3f ms/%lld hit%s"),
            *NodeLabel,
            static_cast<double>(Node->TotalNanoseconds) / 1000000.0,
            static_cast<long long>(Node->HitCount),
            Node->HitCount == 1 ? TEXT("") : TEXT("s"));
    }
    if (!GameplayProfileStatus.IsEmpty())
    {
        Result += TEXT(" · ") + GameplayProfileStatus;
    }
    return Result;
}

const FUnifiedScriptGameplayProfileNode*
SUnifiedScriptDebugPanel::FindHottestProfileNode() const
{
    if (!GameplayProfile.IsSet() || !ActiveAsset.IsSet())
    {
        return nullptr;
    }
    const FUnifiedScriptGameplayProfileNode* Hottest = nullptr;
    for (const FUnifiedScriptGameplayProfileNode& Node : GameplayProfile->Nodes)
    {
        bool bResolvable = false;
        for (const FUnifiedScriptAuthoringFunction& Function : ActiveAsset->Functions)
        {
            if (Function.Id == Node.MethodId &&
                Function.Actions.ContainsByPredicate(
                    [&Node](const FUnifiedScriptAuthoringAction& Candidate)
                    {
                        return Candidate.DebugTargetId == Node.NodeId;
                    }))
            {
                bResolvable = true;
                break;
            }
        }
        if (bResolvable &&
            (Hottest == nullptr ||
             Node.TotalNanoseconds > Hottest->TotalNanoseconds ||
             (Node.TotalNanoseconds == Hottest->TotalNanoseconds &&
              Node.HitCount > Hottest->HitCount)))
        {
            Hottest = &Node;
        }
    }
    return Hottest;
}

#undef LOCTEXT_NAMESPACE
