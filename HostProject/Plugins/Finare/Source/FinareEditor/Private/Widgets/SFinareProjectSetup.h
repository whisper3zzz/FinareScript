#pragma once

#include "HAL/PlatformProcess.h"
#include "FinareProjectSetup.h"
#include "Widgets/SCompoundWidget.h"

class SMultiLineEditableTextBox;
struct FFinareDailyBuildResult;

/** Project-local adopt, doctor, and deferred native onboarding surface. */
class SFinareProjectSetup final : public SCompoundWidget
{
public:
    SLATE_BEGIN_ARGS(SFinareProjectSetup)
    {
    }
    SLATE_END_ARGS()

    void Construct(const FArguments& InArgs);
    virtual ~SFinareProjectSetup() override;

private:
    FReply PreviewAdoption();
    FReply RepairProjectFiles();
    FReply RunDoctor();
    FReply BuildAndReload();
    FReply BuildAndOnboard();
    FReply LocateFirstPlayClass();
    FReply StartAuthoringAndOpenFirstPlayClass();
    FReply ExportSupportBundle();
    FReply RunAttachedOperation(
        EFinareProjectSetupOperation Operation,
        const FText& OperationLabel);

    bool ResolveProjectPaths(FFinareProjectSetupPaths& OutPaths);
    bool CanRunAttachedOperation() const;
    bool CanLocateFirstPlayClass() const;
    bool CanAuthorFirstPlayClass() const;
    void RefreshFirstPlayRequest();
    FText GetFirstPlayDescription() const;
    FText GetAuthoringDescription() const;
    EActiveTimerReturnType PollAttachedOperation(double CurrentTime, float DeltaTime);
    void AppendOutput(const FString& Text);
    void HandleDailyBuildCompleted(const FFinareDailyBuildResult& Result);
    void FinishAttachedOperation(int32 ReturnCode);
    void CloseProcessResources();
    void SetStatus(const FText& InStatus, const FSlateColor& InColor);

    TSharedPtr<SMultiLineEditableTextBox> OutputTextBox;
    FProcHandle ActiveProcess;
    void* ReadPipe = nullptr;
    void* WritePipe = nullptr;
    FString ActiveOperationLabel;
    FString ActiveReportPath;
    TOptional<EFinareProjectSetupOperation> ActiveOperation;
    FDelegateHandle DailyBuildCompletedHandle;
    FFinareFirstPlayRequest FirstPlayRequest;
    bool bHasFirstPlayRequest = false;
    FText StatusText;
    FSlateColor StatusColor = FSlateColor::UseForeground();
};
