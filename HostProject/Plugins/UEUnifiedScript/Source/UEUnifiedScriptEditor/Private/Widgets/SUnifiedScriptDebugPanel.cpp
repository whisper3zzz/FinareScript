#include "Widgets/SUnifiedScriptDebugPanel.h"

#include "HttpModule.h"
#include "Interfaces/IHttpRequest.h"
#include "Interfaces/IHttpResponse.h"
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

FReply SUnifiedScriptDebugPanel::HandleFocusRuntimeFailure()
{
    if (CanFocusRuntimeFailure())
    {
        OnNodeFocused.ExecuteIfBound(RuntimeFailureMethodId, RuntimeFailureNodeId);
    }
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
        !bCommandPending && !bWaitPending && !GetServerUrl().IsEmpty();
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
    }
    else
    {
        LiveComponent = BestComponent;
        LiveContext = MoveTemp(BestContext);
        LastRuntimeDiagnosticSequence = LiveContext->LastDiagnostic.IsValid()
            ? LiveContext->LastDiagnostic.Sequence
            : 0;
        MapRuntimeDiagnosticToGraph();
    }
    if (PreviousComponent != LiveComponent.Get() ||
        PreviousSequence != LastRuntimeDiagnosticSequence)
    {
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
        !LiveContext->bPausedAtFrameBoundary;
}

bool SUnifiedScriptDebugPanel::CanStepLiveWorld() const
{
    return LiveComponent.IsValid() && LiveContext.IsSet() && LiveContext->bRunning &&
        LiveContext->bPausedAtFrameBoundary;
}

bool SUnifiedScriptDebugPanel::CanContinueLiveWorld() const
{
    return CanStepLiveWorld();
}

bool SUnifiedScriptDebugPanel::CanFocusRuntimeFailure() const
{
    return !RuntimeFailureMethodId.IsEmpty() && !RuntimeFailureNodeId.IsEmpty();
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
        LiveContext->bPausedAtFrameBoundary
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

#undef LOCTEXT_NAMESPACE
