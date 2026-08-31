#pragma once

#include "HAL/PlatformProcess.h"
#include "UnifiedScriptProjectSetup.h"
#include "Widgets/SCompoundWidget.h"

class SMultiLineEditableTextBox;

/** Project-local adopt, doctor, and deferred native onboarding surface. */
class SUnifiedScriptProjectSetup final : public SCompoundWidget
{
public:
    SLATE_BEGIN_ARGS(SUnifiedScriptProjectSetup)
    {
    }
    SLATE_END_ARGS()

    void Construct(const FArguments& InArgs);
    virtual ~SUnifiedScriptProjectSetup() override;

private:
    FReply PreviewAdoption();
    FReply RepairProjectFiles();
    FReply RunDoctor();
    FReply BuildAndOnboard();
    FReply LocateFirstPlayClass();
    FReply StartAuthoringAndOpenFirstPlayClass();
    FReply RunAttachedOperation(
        EUnifiedScriptProjectSetupOperation Operation,
        const FText& OperationLabel);

    bool ResolveProjectPaths(FUnifiedScriptProjectSetupPaths& OutPaths);
    bool CanRunAttachedOperation() const;
    bool CanLocateFirstPlayClass() const;
    bool CanAuthorFirstPlayClass() const;
    void RefreshFirstPlayRequest();
    FText GetFirstPlayDescription() const;
    FText GetAuthoringDescription() const;
    EActiveTimerReturnType PollAttachedOperation(double CurrentTime, float DeltaTime);
    void AppendOutput(const FString& Text);
    void FinishAttachedOperation(int32 ReturnCode);
    void CloseProcessResources();
    void SetStatus(const FText& InStatus, const FSlateColor& InColor);

    TSharedPtr<SMultiLineEditableTextBox> OutputTextBox;
    FProcHandle ActiveProcess;
    void* ReadPipe = nullptr;
    void* WritePipe = nullptr;
    FString ActiveOperationLabel;
    FString ActiveReportPath;
    FUnifiedScriptFirstPlayRequest FirstPlayRequest;
    bool bHasFirstPlayRequest = false;
    FText StatusText;
    FSlateColor StatusColor = FSlateColor::UseForeground();
};
