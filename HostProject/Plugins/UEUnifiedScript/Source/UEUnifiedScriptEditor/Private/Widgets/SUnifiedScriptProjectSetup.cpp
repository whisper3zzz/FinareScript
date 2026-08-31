#include "Widgets/SUnifiedScriptProjectSetup.h"

#include "Editor.h"
#include "Dom/JsonObject.h"
#include "FileHelpers.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformApplicationMisc.h"
#include "HAL/PlatformMisc.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/MessageDialog.h"
#include "Misc/Paths.h"
#include "Styling/AppStyle.h"
#include "UEUnifiedScriptEditorModule.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptEditorDiagnostics.h"
#include "Widgets/SUnifiedScriptAuthoringSurface.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Input/SMultiLineEditableTextBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"
#include "UObject/UObjectGlobals.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

#define LOCTEXT_NAMESPACE "SUnifiedScriptProjectSetup"

namespace UE::UnifiedScript::ProjectSetup::Private
{
FString BuildDailyReloadTransaction(
    const FUnifiedScriptDailyBuildReceipt& Receipt,
    const FString& ReportPath)
{
    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(
        TEXT("schemaVersion"),
        TEXT("unifiedscript.reload-transaction/v0.1"));
    Root->SetStringField(
        TEXT("transactionId"),
        FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower));
    Root->SetStringField(TEXT("source"), TEXT("dailyBuild"));
    Root->SetStringField(TEXT("buildReport"), ReportPath);

    TArray<TSharedPtr<FJsonValue>> Phases;
    const auto AddPhase = [&Phases](const TCHAR* Name, const FString& Detail)
    {
        const TSharedRef<FJsonObject> Phase = MakeShared<FJsonObject>();
        Phase->SetStringField(TEXT("name"), Name);
        Phase->SetStringField(TEXT("state"), TEXT("published"));
        Phase->SetNumberField(TEXT("durationMicroseconds"), 0.0);
        Phase->SetStringField(TEXT("detail"), Detail);
        Phases.Add(MakeShared<FJsonValueObject>(Phase));
    };
    AddPhase(
        TEXT("dependencyAwareBuild"),
        FString::Printf(
            TEXT("Daily graph executed %d, skipped %d, and deferred %d Editor-only stages."),
            Receipt.ExecutedStages,
            Receipt.SkippedStages,
            Receipt.DeferredStages));
    AddPhase(
        TEXT("artifactPublication"),
        TEXT("Managed assemblies and compiled chunks were published atomically before reload."));
    Root->SetArrayField(TEXT("phases"), MoveTemp(Phases));

    FString Json;
    const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Json);
    return FJsonSerializer::Serialize(Root, Writer) ? Json : FString();
}

TSharedRef<SWidget> MakeActionRow(
    const FText& Title,
    const FText& Description,
    const FText& ButtonLabel,
    const TAttribute<bool>& IsEnabled,
    const FOnClicked& OnClicked)
{
    return SNew(SBorder)
        .BorderImage(FAppStyle::GetBrush(TEXT("Brushes.Panel")))
        .Padding(FMargin(12.0f, 10.0f))
        [
            SNew(SHorizontalBox)
            + SHorizontalBox::Slot()
            .FillWidth(1.0f)
            .VAlign(VAlign_Center)
            [
                SNew(SVerticalBox)
                + SVerticalBox::Slot()
                .AutoHeight()
                [
                    SNew(STextBlock)
                    .Text(Title)
                    .TextStyle(FAppStyle::Get(), TEXT("DetailsView.CategoryTextStyle"))
                ]
                + SVerticalBox::Slot()
                .AutoHeight()
                .Padding(0.0f, 3.0f, 16.0f, 0.0f)
                [
                    SNew(STextBlock)
                    .Text(Description)
                    .AutoWrapText(true)
                    .ColorAndOpacity(FSlateColor::UseSubduedForeground())
                ]
            ]
            + SHorizontalBox::Slot()
            .AutoWidth()
            .VAlign(VAlign_Center)
            [
                SNew(SButton)
                .Text(ButtonLabel)
                .IsEnabled(IsEnabled)
                .OnClicked(OnClicked)
            ]
        ];
}

TSharedRef<SWidget> MakeDynamicActionRow(
    const FText& Title,
    const TAttribute<FText>& Description,
    const FText& ButtonLabel,
    const TAttribute<bool>& IsEnabled,
    const FOnClicked& OnClicked)
{
    return SNew(SBorder)
        .BorderImage(FAppStyle::GetBrush(TEXT("Brushes.Panel")))
        .Padding(FMargin(12.0f, 10.0f))
        [
            SNew(SHorizontalBox)
            + SHorizontalBox::Slot()
            .FillWidth(1.0f)
            .VAlign(VAlign_Center)
            [
                SNew(SVerticalBox)
                + SVerticalBox::Slot()
                .AutoHeight()
                [
                    SNew(STextBlock)
                    .Text(Title)
                    .TextStyle(FAppStyle::Get(), TEXT("DetailsView.CategoryTextStyle"))
                ]
                + SVerticalBox::Slot()
                .AutoHeight()
                .Padding(0.0f, 3.0f, 16.0f, 0.0f)
                [
                    SNew(STextBlock)
                    .Text(Description)
                    .AutoWrapText(true)
                    .ColorAndOpacity(FSlateColor::UseSubduedForeground())
                ]
            ]
            + SHorizontalBox::Slot()
            .AutoWidth()
            .VAlign(VAlign_Center)
            [
                SNew(SButton)
                .Text(ButtonLabel)
                .IsEnabled(IsEnabled)
                .OnClicked(OnClicked)
            ]
        ];
}
}

void SUnifiedScriptProjectSetup::Construct(const FArguments& InArgs)
{
    SetStatus(
        LOCTEXT("ReadyStatus", "Ready — inspect the project before applying changes."),
        FSlateColor::UseForeground());
    RefreshFirstPlayRequest();

    const TAttribute<bool> CanRun = TAttribute<bool>::CreateLambda(
        [this]() { return CanRunAttachedOperation(); });

    ChildSlot
    [
        SNew(SBorder)
        .BorderImage(FAppStyle::GetBrush(TEXT("Brushes.Recessed")))
        .Padding(18.0f)
        [
            SNew(SVerticalBox)
            + SVerticalBox::Slot()
            .AutoHeight()
            [
                SNew(STextBlock)
                .Text(LOCTEXT("Title", "UnifiedScript Project Setup"))
                .TextStyle(FAppStyle::Get(), TEXT("LargeText"))
            ]
            + SVerticalBox::Slot()
            .AutoHeight()
            .Padding(0.0f, 4.0f, 0.0f, 14.0f)
            [
                SNew(STextBlock)
                .Text(LOCTEXT(
                    "Intro",
                    "Install, inspect, repair, and build through the project-local UnifiedScript tools. "
                    "The Editor and command line share the same transactional configuration model."))
                .AutoWrapText(true)
                .ColorAndOpacity(FSlateColor::UseSubduedForeground())
            ]
            + SVerticalBox::Slot()
            .AutoHeight()
            .Padding(0.0f, 0.0f, 0.0f, 10.0f)
            [
                SNew(SBorder)
                .BorderImage(FAppStyle::GetBrush(TEXT("Brushes.Header")))
                .Padding(FMargin(10.0f, 7.0f))
                [
                    SNew(STextBlock)
                    .Text_Lambda([this]() { return StatusText; })
                    .ColorAndOpacity_Lambda([this]() { return StatusColor; })
                ]
            ]
            + SVerticalBox::Slot()
            .AutoHeight()
            .Padding(0.0f, 0.0f, 0.0f, 6.0f)
            [
                UE::UnifiedScript::ProjectSetup::Private::MakeActionRow(
                    LOCTEXT("PreviewTitle", "1. Preview adoption"),
                    LOCTEXT("PreviewDescription", "Show every owned file that would be created, updated, or left unchanged. No project files are modified."),
                    LOCTEXT("PreviewButton", "Preview"),
                    CanRun,
                    FOnClicked::CreateSP(this, &SUnifiedScriptProjectSetup::PreviewAdoption))
            ]
            + SVerticalBox::Slot()
            .AutoHeight()
            .Padding(0.0f, 0.0f, 0.0f, 6.0f)
            [
                UE::UnifiedScript::ProjectSetup::Private::MakeActionRow(
                    LOCTEXT("RepairTitle", "2. Repair project files"),
                    LOCTEXT("RepairDescription", "Apply the reviewed transactional plan. User-owned conflicts are reported and are never overwritten."),
                    LOCTEXT("RepairButton", "Repair"),
                    CanRun,
                    FOnClicked::CreateSP(this, &SUnifiedScriptProjectSetup::RepairProjectFiles))
            ]
            + SVerticalBox::Slot()
            .AutoHeight()
            .Padding(0.0f, 0.0f, 0.0f, 6.0f)
            [
                UE::UnifiedScript::ProjectSetup::Private::MakeActionRow(
                    LOCTEXT("DoctorTitle", "3. Diagnose the consumer"),
                    LOCTEXT("DoctorDescription", "Check engine compatibility, runtime closure, generated state, cook staging, and ownership drift."),
                    LOCTEXT("DoctorButton", "Run Doctor"),
                    CanRun,
                    FOnClicked::CreateSP(this, &SUnifiedScriptProjectSetup::RunDoctor))
            ]
            + SVerticalBox::Slot()
            .AutoHeight()
            .Padding(0.0f, 0.0f, 0.0f, 6.0f)
            [
                UE::UnifiedScript::ProjectSetup::Private::MakeActionRow(
                    LOCTEXT("DailyBuildTitle", "4. Build and reload"),
                    LOCTEXT("DailyBuildDescription", "Run only invalidated Editor-safe stages, then atomically reload every compatible Script Class instance. Native or schema changes return one explicit restart requirement."),
                    LOCTEXT("DailyBuildButton", "Build & Reload"),
                    CanRun,
                    FOnClicked::CreateSP(this, &SUnifiedScriptProjectSetup::BuildAndReload))
            ]
            + SVerticalBox::Slot()
            .AutoHeight()
            .Padding(0.0f, 0.0f, 0.0f, 12.0f)
            [
                UE::UnifiedScript::ProjectSetup::Private::MakeActionRow(
                    LOCTEXT("BuildTitle", "5. Build and restart"),
                    LOCTEXT("BuildDescription", "Save and close the Editor, build the complete native/managed dependency graph, then reopen this project automatically."),
                    LOCTEXT("BuildButton", "Build, Restart & Continue"),
                    CanRun,
                    FOnClicked::CreateSP(this, &SUnifiedScriptProjectSetup::BuildAndOnboard))
            ]
            + SVerticalBox::Slot()
            .AutoHeight()
            .Padding(0.0f, 0.0f, 0.0f, 12.0f)
            [
                UE::UnifiedScript::ProjectSetup::Private::MakeDynamicActionRow(
                    LOCTEXT("FirstPlayTitle", "6. Continue to first Play"),
                    TAttribute<FText>::CreateLambda(
                        [this]() { return GetFirstPlayDescription(); }),
                    LOCTEXT("FirstPlayButton", "Locate Generated Class"),
                    TAttribute<bool>::CreateLambda(
                        [this]() { return CanLocateFirstPlayClass(); }),
                    FOnClicked::CreateSP(this, &SUnifiedScriptProjectSetup::LocateFirstPlayClass))
            ]
            + SVerticalBox::Slot()
            .AutoHeight()
            .Padding(0.0f, 0.0f, 0.0f, 12.0f)
            [
                UE::UnifiedScript::ProjectSetup::Private::MakeDynamicActionRow(
                    LOCTEXT("AuthoringTitle", "7. Edit and reload"),
                    TAttribute<FText>::CreateLambda(
                        [this]() { return GetAuthoringDescription(); }),
                    LOCTEXT("AuthoringButton", "Open Authoring & Reload"),
                    TAttribute<bool>::CreateLambda(
                        [this]() { return CanAuthorFirstPlayClass(); }),
                    FOnClicked::CreateSP(
                        this,
                        &SUnifiedScriptProjectSetup::StartAuthoringAndOpenFirstPlayClass))
            ]
            + SVerticalBox::Slot()
            .AutoHeight()
            .Padding(0.0f, 0.0f, 0.0f, 12.0f)
            [
                UE::UnifiedScript::ProjectSetup::Private::MakeActionRow(
                    LOCTEXT("DiagnosticsTitle", "8. Diagnose and share"),
                    LOCTEXT(
                        "DiagnosticsDescription",
                        "Build one bounded, privacy-redacted support bundle from UnifiedScript reports and copy its path."),
                    LOCTEXT("DiagnosticsButton", "Export Support Bundle"),
                    CanRun,
                    FOnClicked::CreateSP(
                        this,
                        &SUnifiedScriptProjectSetup::ExportSupportBundle))
            ]
            + SVerticalBox::Slot()
            .FillHeight(1.0f)
            .MinHeight(160.0f)
            [
                SAssignNew(OutputTextBox, SMultiLineEditableTextBox)
                .IsReadOnly(true)
                .AlwaysShowScrollbars(true)
                .HintText(LOCTEXT("OutputHint", "Command output and report paths appear here."))
            ]
        ]
    ];
}

SUnifiedScriptProjectSetup::~SUnifiedScriptProjectSetup()
{
    CloseProcessResources();
}

FReply SUnifiedScriptProjectSetup::PreviewAdoption()
{
    return RunAttachedOperation(
        EUnifiedScriptProjectSetupOperation::PreviewAdoption,
        LOCTEXT("PreviewOperation", "Adoption preview"));
}

FReply SUnifiedScriptProjectSetup::RepairProjectFiles()
{
    return RunAttachedOperation(
        EUnifiedScriptProjectSetupOperation::RepairAdoption,
        LOCTEXT("RepairOperation", "Project repair"));
}

FReply SUnifiedScriptProjectSetup::RunDoctor()
{
    return RunAttachedOperation(
        EUnifiedScriptProjectSetupOperation::Doctor,
        LOCTEXT("DoctorOperation", "Project doctor"));
}

FReply SUnifiedScriptProjectSetup::BuildAndReload()
{
    FEditorFileUtils::SaveDirtyPackages(true, true, true);
    return RunAttachedOperation(
        EUnifiedScriptProjectSetupOperation::DailyBuildAttached,
        LOCTEXT("DailyBuildOperation", "daily Build & Reload"));
}

FReply SUnifiedScriptProjectSetup::LocateFirstPlayClass()
{
    if (!CanLocateFirstPlayClass())
    {
        return FReply::Handled();
    }

    UObject* BlueprintAsset = StaticLoadObject(
        UObject::StaticClass(),
        nullptr,
        *FirstPlayRequest.BlueprintAssetPath);
    if (BlueprintAsset == nullptr)
    {
        const FText ErrorText = FText::Format(
            LOCTEXT("FirstPlayAssetMissing", "The generated class could not be loaded: {0}"),
            FText::FromString(FirstPlayRequest.BlueprintAssetPath));
        AppendOutput(ErrorText.ToString());
        SetStatus(ErrorText, FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
        return FReply::Handled();
    }

    TArray<UObject*> Assets;
    Assets.Add(BlueprintAsset);
    GEditor->SyncBrowserToObjects(Assets);
    SetStatus(
        FText::Format(
            LOCTEXT(
                "FirstPlayLocated",
                "Located {0}. Add it to the level or an Actor, then press Play."),
            FText::FromString(FirstPlayRequest.ManagedTypeName)),
        FAppStyle::GetSlateColor(TEXT("Colors.AccentGreen")));
    return FReply::Handled();
}

FReply SUnifiedScriptProjectSetup::StartAuthoringAndOpenFirstPlayClass()
{
    if (!CanAuthorFirstPlayClass())
    {
        return FReply::Handled();
    }

    FUnifiedScriptProjectSetupPaths Paths;
    if (!ResolveProjectPaths(Paths))
    {
        return FReply::Handled();
    }
    FUnifiedScriptProjectSetupCommand Command;
    FString Error;
    if (!FUnifiedScriptProjectSetup::BuildAuthoringCommand(
            Paths,
            FirstPlayRequest.SourcePath,
            Command,
            Error))
    {
        AppendOutput(Error);
        SetStatus(FText::FromString(Error), FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
        return FReply::Handled();
    }

    UUnifiedScriptAsset* ScriptAsset = Cast<UUnifiedScriptAsset>(StaticLoadObject(
        UUnifiedScriptAsset::StaticClass(),
        nullptr,
        *FirstPlayRequest.ScriptAssetPath));
    if (ScriptAsset == nullptr)
    {
        const FText ErrorText = FText::Format(
            LOCTEXT("AuthoringAssetMissing", "The Script Asset could not be loaded: {0}"),
            FText::FromString(FirstPlayRequest.ScriptAssetPath));
        AppendOutput(ErrorText.ToString());
        SetStatus(ErrorText, FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
        return FReply::Handled();
    }

    uint32 ProcessId = 0;
    FProcHandle Process = FPlatformProcess::CreateProc(
        *Command.Executable,
        *Command.Arguments,
        Command.bDetached,
        false,
        false,
        &ProcessId,
        0,
        *Paths.ProjectRoot,
        nullptr,
        nullptr,
        nullptr);
    if (!Process.IsValid())
    {
        const FText ErrorText = LOCTEXT(
            "AuthoringLaunchFailure",
            "Could not start the project-local UnifiedScript authoring server.");
        AppendOutput(ErrorText.ToString());
        SetStatus(ErrorText, FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
        return FReply::Handled();
    }
    FPlatformProcess::CloseProc(Process);

    AppendOutput(FString::Printf(
        TEXT("Authoring server %u launched for %s. Close its terminal to stop the server."),
        ProcessId,
        *FirstPlayRequest.SourcePath));
    IUEUnifiedScriptEditorModule::Get().OpenAuthoringSurface(ScriptAsset);
    SetStatus(
        FText::Format(
            LOCTEXT(
                "AuthoringStarted",
                "Opening {0}. Wait for the authoring terminal to report its URL, then use Apply & Reload while the class is running."),
            FText::FromString(FirstPlayRequest.ManagedTypeName)),
        FAppStyle::GetSlateColor(TEXT("Colors.AccentGreen")));
    return FReply::Handled();
}

FReply SUnifiedScriptProjectSetup::ExportSupportBundle()
{
    return RunAttachedOperation(
        EUnifiedScriptProjectSetupOperation::ExportDiagnostics,
        LOCTEXT("ExportDiagnosticsOperation", "Support bundle export"));
}

FReply SUnifiedScriptProjectSetup::RunAttachedOperation(
    EUnifiedScriptProjectSetupOperation Operation,
    const FText& OperationLabel)
{
    if (!CanRunAttachedOperation())
    {
        return FReply::Handled();
    }

    FUnifiedScriptProjectSetupPaths Paths;
    if (!ResolveProjectPaths(Paths))
    {
        return FReply::Handled();
    }
    if (Operation == EUnifiedScriptProjectSetupOperation::ExportDiagnostics)
    {
        IFileManager::Get().MakeDirectory(*Paths.DiagnosticsInputRoot, true);
    }

    FUnifiedScriptProjectSetupCommand Command;
    FString Error;
    if (!FUnifiedScriptProjectSetup::BuildCommand(Operation, Paths, 0, Command, Error))
    {
        AppendOutput(Error);
        SetStatus(FText::FromString(Error), FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
        return FReply::Handled();
    }

    IFileManager::Get().MakeDirectory(*FPaths::GetPath(Command.ReportPath), true);
    if (!FPlatformProcess::CreatePipe(ReadPipe, WritePipe))
    {
        const FText ErrorText = LOCTEXT("PipeFailure", "Could not create a process output pipe.");
        AppendOutput(ErrorText.ToString());
        SetStatus(ErrorText, FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
        return FReply::Handled();
    }

    uint32 ProcessId = 0;
    ActiveProcess = FPlatformProcess::CreateProc(
        *Command.Executable,
        *Command.Arguments,
        false,
        true,
        true,
        &ProcessId,
        0,
        *Paths.ProjectRoot,
        WritePipe,
        nullptr,
        WritePipe);
    if (!ActiveProcess.IsValid())
    {
        CloseProcessResources();
        const FText ErrorText = FText::Format(
            LOCTEXT("LaunchFailure", "Could not start {0}."),
            OperationLabel);
        AppendOutput(ErrorText.ToString());
        SetStatus(ErrorText, FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
        return FReply::Handled();
    }

    ActiveOperationLabel = OperationLabel.ToString();
    ActiveReportPath = Command.ReportPath;
    ActiveOperation = Operation;
    AppendOutput(FString::Printf(
        TEXT("> %s %s"),
        *Command.Executable,
        *Command.Arguments));
    SetStatus(
        FText::Format(LOCTEXT("RunningStatus", "Running {0}…"), OperationLabel),
        FAppStyle::GetSlateColor(TEXT("Colors.AccentBlue")));
    RegisterActiveTimer(
        0.1f,
        FWidgetActiveTimerDelegate::CreateSP(
            this,
            &SUnifiedScriptProjectSetup::PollAttachedOperation));
    return FReply::Handled();
}

FReply SUnifiedScriptProjectSetup::BuildAndOnboard()
{
    if (!CanRunAttachedOperation())
    {
        return FReply::Handled();
    }

    FUnifiedScriptProjectSetupPaths Paths;
    if (!ResolveProjectPaths(Paths))
    {
        return FReply::Handled();
    }

    FUnifiedScriptProjectSetupCommand Command;
    FString Error;
    if (!FUnifiedScriptProjectSetup::BuildCommand(
            EUnifiedScriptProjectSetupOperation::BuildAfterEditorExit,
            Paths,
            FPlatformProcess::GetCurrentProcessId(),
            Command,
            Error))
    {
        AppendOutput(Error);
        SetStatus(FText::FromString(Error), FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
        return FReply::Handled();
    }

    const EAppReturnType::Type Answer = FMessageDialog::Open(
        EAppMsgType::OkCancel,
        LOCTEXT(
            "BuildConfirmation",
            "UnifiedScript will save dirty project packages and close the Editor. "
            "Native and managed onboarding will start after this Editor process exits, "
            "then the project will reopen at the first-Play step.\n\nContinue?"),
        LOCTEXT("BuildConfirmationTitle", "Build and Onboard UnifiedScript"));
    if (Answer != EAppReturnType::Ok)
    {
        return FReply::Handled();
    }

    FEditorFileUtils::SaveDirtyPackages(true, true, true);
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(Command.ReportPath), true);
    uint32 HelperProcessId = 0;
    FProcHandle HelperProcess = FPlatformProcess::CreateProc(
        *Command.Executable,
        *Command.Arguments,
        Command.bDetached,
        true,
        true,
        &HelperProcessId,
        0,
        *Paths.ProjectRoot,
        nullptr,
        nullptr,
        nullptr);
    if (!HelperProcess.IsValid())
    {
        const FText ErrorText = LOCTEXT(
            "DeferredLaunchFailure",
            "Could not start the deferred UnifiedScript onboarding helper. The Editor remains open.");
        AppendOutput(ErrorText.ToString());
        SetStatus(ErrorText, FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
        return FReply::Handled();
    }

    FPlatformProcess::CloseProc(HelperProcess);
    AppendOutput(FString::Printf(
        TEXT("Deferred onboarding helper %u is waiting for this Editor to exit. Report: %s"),
        HelperProcessId,
        *Command.ReportPath));
    SetStatus(
        LOCTEXT("DeferredStatus", "Project saved. Closing the Editor; onboarding will build and reopen this project."),
        FAppStyle::GetSlateColor(TEXT("Colors.AccentGreen")));
    FPlatformMisc::RequestExit(false);
    return FReply::Handled();
}

bool SUnifiedScriptProjectSetup::ResolveProjectPaths(
    FUnifiedScriptProjectSetupPaths& OutPaths)
{
    const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    if (!Plugin.IsValid())
    {
        const FText ErrorText = LOCTEXT("PluginMissing", "The UEUnifiedScript plugin descriptor could not be resolved.");
        AppendOutput(ErrorText.ToString());
        SetStatus(ErrorText, FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
        return false;
    }

    const FString EngineRoot = FPaths::ConvertRelativePathToFull(
        FPaths::Combine(FPaths::EngineDir(), TEXT("..")));
    FString Error;
    if (!FUnifiedScriptProjectSetup::ResolvePaths(
            FPaths::GetProjectFilePath(),
            Plugin->GetBaseDir(),
            EngineRoot,
            OutPaths,
            Error))
    {
        AppendOutput(Error);
        SetStatus(FText::FromString(Error), FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
        return false;
    }
    return true;
}

bool SUnifiedScriptProjectSetup::CanRunAttachedOperation() const
{
    return !ActiveProcess.IsValid();
}

bool SUnifiedScriptProjectSetup::CanLocateFirstPlayClass() const
{
    return !ActiveProcess.IsValid() && bHasFirstPlayRequest &&
        FirstPlayRequest.IsReady();
}

bool SUnifiedScriptProjectSetup::CanAuthorFirstPlayClass() const
{
    return !ActiveProcess.IsValid() && bHasFirstPlayRequest &&
        FirstPlayRequest.IsReady() && !FirstPlayRequest.SourcePath.IsEmpty() &&
        !FirstPlayRequest.ScriptAssetPath.IsEmpty();
}

void SUnifiedScriptProjectSetup::RefreshFirstPlayRequest()
{
    bHasFirstPlayRequest = false;
    FirstPlayRequest = FUnifiedScriptFirstPlayRequest();
    const FString RequestPath = FUnifiedScriptProjectSetup::GetFirstPlayRequestPath(
        FPaths::ProjectDir());
    if (!IFileManager::Get().FileExists(*RequestPath))
    {
        return;
    }

    FString Error;
    if (!FUnifiedScriptProjectSetup::ReadFirstPlayRequest(
            RequestPath,
            FirstPlayRequest,
            Error))
    {
        SetStatus(
            FText::FromString(Error),
            FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
        return;
    }
    bHasFirstPlayRequest = true;
    if (FirstPlayRequest.IsBuildRequired())
    {
        SetStatus(
            FText::Format(
                LOCTEXT(
                    "FirstPlayBuildRequired",
                    "{0} was created and requires Build, Restart & Continue before Play."),
                FText::FromString(FirstPlayRequest.ManagedTypeName)),
            FAppStyle::GetSlateColor(TEXT("Colors.Warning")));
    }
    else if (FirstPlayRequest.IsReady())
    {
        SetStatus(
            FText::Format(
                LOCTEXT(
                    "FirstPlayReady",
                    "{0} is built and ready for first Play."),
                FText::FromString(FirstPlayRequest.ManagedTypeName)),
            FAppStyle::GetSlateColor(TEXT("Colors.AccentGreen")));
    }
    else if (FirstPlayRequest.HasFailed())
    {
        SetStatus(
            FText::Format(
                LOCTEXT(
                    "FirstPlayFailed",
                    "The build for {0} failed with exit code {1}. Review the report and retry."),
                FText::FromString(FirstPlayRequest.ManagedTypeName),
                FText::AsNumber(FirstPlayRequest.ExitCode)),
            FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
    }
}

FText SUnifiedScriptProjectSetup::GetFirstPlayDescription() const
{
    if (!bHasFirstPlayRequest)
    {
        return LOCTEXT(
            "FirstPlayNoRequest",
            "Create a UnifiedScript Actor or Actor Component class. Its exact build and Play handoff will appear here.");
    }
    if (FirstPlayRequest.IsBuildRequired())
    {
        return FText::Format(
            LOCTEXT(
                "FirstPlayPendingDescription",
                "{0} is waiting for the complete Build/Onboard graph above."),
            FText::FromString(FirstPlayRequest.ManagedTypeName));
    }
    if (FirstPlayRequest.HasFailed())
    {
        return FText::Format(
            LOCTEXT(
                "FirstPlayFailureDescription",
                "Build failed. Report: {0}"),
            FText::FromString(FirstPlayRequest.OnboardingReport));
    }
    const FText Placement = FirstPlayRequest.ClassKind == TEXT("actor")
        ? LOCTEXT("FirstPlayActorPlacement", "Drag the Actor into the level")
        : LOCTEXT("FirstPlayComponentPlacement", "Add the Component class to an Actor Blueprint");
    return FText::Format(
        LOCTEXT(
            "FirstPlayReadyDescription",
            "{0} is ready. {1}, then use the normal Play button."),
        FText::FromString(FirstPlayRequest.ManagedTypeName),
        Placement);
}

FText SUnifiedScriptProjectSetup::GetAuthoringDescription() const
{
    if (!bHasFirstPlayRequest || !FirstPlayRequest.IsReady())
    {
        return LOCTEXT(
            "AuthoringNotReady",
            "Finish the first Build above before starting the state-preserving edit/reload loop.");
    }
    return FText::Format(
        LOCTEXT(
            "AuthoringReadyDescription",
            "Launch the project-local authoring server for {0}, open its exact visual surface, and reload matching live instances after an accepted edit."),
        FText::FromString(FirstPlayRequest.ManagedTypeName));
}

EActiveTimerReturnType SUnifiedScriptProjectSetup::PollAttachedOperation(
    double CurrentTime,
    float DeltaTime)
{
    const FString Output = ReadPipe != nullptr ? FPlatformProcess::ReadPipe(ReadPipe) : FString();
    if (!Output.IsEmpty())
    {
        AppendOutput(Output);
    }

    if (ActiveProcess.IsValid() && FPlatformProcess::IsProcRunning(ActiveProcess))
    {
        return EActiveTimerReturnType::Continue;
    }

    int32 ReturnCode = -1;
    if (!ActiveProcess.IsValid() ||
        !FPlatformProcess::GetProcReturnCode(ActiveProcess, &ReturnCode))
    {
        ReturnCode = -1;
    }
    FinishAttachedOperation(ReturnCode);
    return EActiveTimerReturnType::Stop;
}

void SUnifiedScriptProjectSetup::AppendOutput(const FString& Text)
{
    if (!OutputTextBox.IsValid() || Text.IsEmpty())
    {
        return;
    }
    FString Existing = OutputTextBox->GetText().ToString();
    if (!Existing.IsEmpty() && !Existing.EndsWith(TEXT("\n")))
    {
        Existing.AppendChar(TEXT('\n'));
    }
    Existing.Append(Text);
    OutputTextBox->SetText(FText::FromString(Existing));
    OutputTextBox->ScrollTo(ETextLocation::EndOfDocument);
}

void SUnifiedScriptProjectSetup::FinishAttachedOperation(int32 ReturnCode)
{
    const FString OperationLabel = MoveTemp(ActiveOperationLabel);
    const FString ReportPath = MoveTemp(ActiveReportPath);
    const TOptional<EUnifiedScriptProjectSetupOperation> CompletedOperation = ActiveOperation;
    ActiveOperation.Reset();
    CloseProcessResources();
    if (CompletedOperation.IsSet() &&
        CompletedOperation.GetValue() == EUnifiedScriptProjectSetupOperation::DailyBuildAttached)
    {
        FUnifiedScriptDailyBuildReceipt Receipt;
        FString ReceiptError;
        if (FUnifiedScriptProjectSetup::ReadDailyBuildReceipt(
                ReportPath,
                Receipt,
                ReceiptError))
        {
            if (Receipt.RequiresRestart())
            {
                const FString RestartMessage = FString::Printf(
                    TEXT("Restart required at %s: %s. Use Build, Restart & Continue; no live generation was changed."),
                    *Receipt.RestartRequiredStage,
                    *Receipt.RestartReason);
                SetStatus(
                    FText::FromString(RestartMessage),
                    FAppStyle::GetSlateColor(TEXT("Colors.Warning")));
                AppendOutput(RestartMessage);
                return;
            }
            if (ReturnCode == 0 && Receipt.IsReadyToReload())
            {
                const FString ReloadTransaction =
                    UE::UnifiedScript::ProjectSetup::Private::BuildDailyReloadTransaction(
                        Receipt,
                        ReportPath);
                const FUnifiedScriptReloadBatchResult Reload =
                    SUnifiedScriptAuthoringSurface::ReloadAllScriptClassComponents(
                        ReloadTransaction);
                const FString Completion = FString::Printf(
                    TEXT("Daily Build completed: %d stages ran, %d skipped, %d deferred; %d components matched, %d reloaded, %d failed."),
                    Receipt.ExecutedStages,
                    Receipt.SkippedStages,
                    Receipt.DeferredStages,
                    Reload.Matched,
                    Reload.Reloaded,
                    Reload.Failed);
                SetStatus(
                    FText::FromString(Completion),
                    Reload.Failed == 0
                        ? FAppStyle::GetSlateColor(TEXT("Colors.AccentGreen"))
                        : FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
                AppendOutput(FString::Printf(
                    TEXT("%s\nReport: %s"),
                    *Completion,
                    *ReportPath));
                return;
            }
        }
        else
        {
            AppendOutput(ReceiptError);
        }
    }
    if (ReturnCode == 0)
    {
        if (CompletedOperation.IsSet() &&
            CompletedOperation.GetValue() == EUnifiedScriptProjectSetupOperation::ExportDiagnostics)
        {
            FPlatformApplicationMisc::ClipboardCopy(*ReportPath);
            SetStatus(
                FText::Format(
                    LOCTEXT(
                        "SupportBundleSuccessStatus",
                        "Support bundle created and its path copied: {0}"),
                    FText::FromString(ReportPath)),
                FAppStyle::GetSlateColor(TEXT("Colors.AccentGreen")));
            AppendOutput(FString::Printf(
                TEXT("Support bundle: %s\nPath copied to clipboard."), *ReportPath));
            return;
        }
        SetStatus(
            FText::Format(
                LOCTEXT("SuccessStatus", "{0} completed. Report: {1}"),
                FText::FromString(OperationLabel),
                FText::FromString(ReportPath)),
            FAppStyle::GetSlateColor(TEXT("Colors.AccentGreen")));
        AppendOutput(FString::Printf(TEXT("Report: %s"), *ReportPath));
    }
    else
    {
        SetStatus(
            FText::Format(
                LOCTEXT("FailureStatus", "{0} failed with exit code {1}. See the output and report."),
                FText::FromString(OperationLabel),
                FText::AsNumber(ReturnCode)),
            FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
        AppendOutput(FString::Printf(
            TEXT("Exit code: %d\nReport: %s"),
            ReturnCode,
            *ReportPath));
        TArray<FUnifiedScriptEditorDiagnostic> Diagnostics =
            FUnifiedScriptEditorDiagnostics::ReadReport(ReportPath);
        Diagnostics.Insert(
            FUnifiedScriptEditorDiagnostics::FromSetupFailure(
                OperationLabel, ReturnCode, ReportPath),
            0);
        FUnifiedScriptEditorDiagnostics::Publish(
            LOCTEXT("SetupDiagnosticsPage", "Project Setup"),
            Diagnostics);
    }
}

void SUnifiedScriptProjectSetup::CloseProcessResources()
{
    if (ActiveProcess.IsValid())
    {
        FPlatformProcess::CloseProc(ActiveProcess);
        ActiveProcess = FProcHandle();
    }
    if (ReadPipe != nullptr || WritePipe != nullptr)
    {
        FPlatformProcess::ClosePipe(ReadPipe, WritePipe);
        ReadPipe = nullptr;
        WritePipe = nullptr;
    }
}

void SUnifiedScriptProjectSetup::SetStatus(
    const FText& InStatus,
    const FSlateColor& InColor)
{
    StatusText = InStatus;
    StatusColor = InColor;
}

#undef LOCTEXT_NAMESPACE
