#include "Widgets/SFinareProjectSetup.h"

#include "Editor.h"
#include "FileHelpers.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformApplicationMisc.h"
#include "HAL/PlatformMisc.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/MessageDialog.h"
#include "Misc/Paths.h"
#include "Styling/AppStyle.h"
#include "FinareEditorModule.h"
#include "FinareAsset.h"
#include "FinareEditorDiagnostics.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Input/SMultiLineEditableTextBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"
#include "UObject/UObjectGlobals.h"

#define LOCTEXT_NAMESPACE "SFinareProjectSetup"

namespace UE::Finare::ProjectSetup::Private
{
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

void SFinareProjectSetup::Construct(const FArguments& InArgs)
{
	DailyBuildCompletedHandle =
		IFinareEditorModule::Get().OnDailyBuildCompleted().AddSP(
			this,
			&SFinareProjectSetup::HandleDailyBuildCompleted);
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
				.Text(LOCTEXT("Title", "Finare Project Setup"))
				.TextStyle(FAppStyle::Get(), TEXT("LargeText"))
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.0f, 4.0f, 0.0f, 14.0f)
			[
				SNew(STextBlock)
				.Text(LOCTEXT(
					"Intro",
					"Install, inspect, repair, and build through the project-local Finare tools. "
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
				UE::Finare::ProjectSetup::Private::MakeActionRow(
					LOCTEXT("PreviewTitle", "1. Preview adoption"),
					LOCTEXT("PreviewDescription",
					        "Show every owned file that would be created, updated, or left unchanged. No project files are modified."),
					LOCTEXT("PreviewButton", "Preview"),
					CanRun,
					FOnClicked::CreateSP(this, &SFinareProjectSetup::PreviewAdoption))
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.0f, 0.0f, 0.0f, 6.0f)
			[
				UE::Finare::ProjectSetup::Private::MakeActionRow(
					LOCTEXT("RepairTitle", "2. Repair project files"),
					LOCTEXT("RepairDescription",
					        "Apply the reviewed transactional plan. User-owned conflicts are reported and are never overwritten."),
					LOCTEXT("RepairButton", "Repair"),
					CanRun,
					FOnClicked::CreateSP(this, &SFinareProjectSetup::RepairProjectFiles))
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.0f, 0.0f, 0.0f, 6.0f)
			[
				UE::Finare::ProjectSetup::Private::MakeActionRow(
					LOCTEXT("DoctorTitle", "3. Diagnose the consumer"),
					LOCTEXT("DoctorDescription",
					        "Check engine compatibility, runtime closure, generated state, cook staging, and ownership drift."),
					LOCTEXT("DoctorButton", "Run Doctor"),
					CanRun,
					FOnClicked::CreateSP(this, &SFinareProjectSetup::RunDoctor))
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.0f, 0.0f, 0.0f, 6.0f)
			[
				UE::Finare::ProjectSetup::Private::MakeActionRow(
					LOCTEXT("DailyBuildTitle", "4. Build and reload"),
					LOCTEXT("DailyBuildDescription",
					        "Run only invalidated Editor-safe stages, then atomically reload every compatible Script Class instance. Native or schema changes return one explicit restart requirement."),
					LOCTEXT("DailyBuildButton", "Build & Reload"),
					CanRun,
					FOnClicked::CreateSP(this, &SFinareProjectSetup::BuildAndReload))
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.0f, 0.0f, 0.0f, 12.0f)
			[
				UE::Finare::ProjectSetup::Private::MakeActionRow(
					LOCTEXT("BuildTitle", "5. Build and restart"),
					LOCTEXT("BuildDescription",
					        "Save and close the Editor, build the complete native/managed dependency graph, then reopen this project automatically."),
					LOCTEXT("BuildButton", "Build, Restart & Continue"),
					CanRun,
					FOnClicked::CreateSP(this, &SFinareProjectSetup::BuildAndOnboard))
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.0f, 0.0f, 0.0f, 12.0f)
			[
				UE::Finare::ProjectSetup::Private::MakeDynamicActionRow(
					LOCTEXT("FirstPlayTitle", "6. Continue to first Play"),
					TAttribute<FText>::CreateLambda(
						[this]() { return GetFirstPlayDescription(); }),
					LOCTEXT("FirstPlayButton", "Locate Generated Class"),
					TAttribute<bool>::CreateLambda(
						[this]() { return CanLocateFirstPlayClass(); }),
					FOnClicked::CreateSP(this, &SFinareProjectSetup::LocateFirstPlayClass))
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.0f, 0.0f, 0.0f, 12.0f)
			[
				UE::Finare::ProjectSetup::Private::MakeDynamicActionRow(
					LOCTEXT("AuthoringTitle", "7. Edit and reload"),
					TAttribute<FText>::CreateLambda(
						[this]() { return GetAuthoringDescription(); }),
					LOCTEXT("AuthoringButton", "Open Authoring & Reload"),
					TAttribute<bool>::CreateLambda(
						[this]() { return CanAuthorFirstPlayClass(); }),
					FOnClicked::CreateSP(
						this,
						&SFinareProjectSetup::StartAuthoringAndOpenFirstPlayClass))
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.0f, 0.0f, 0.0f, 12.0f)
			[
				UE::Finare::ProjectSetup::Private::MakeActionRow(
					LOCTEXT("DiagnosticsTitle", "8. Diagnose and share"),
					LOCTEXT(
						"DiagnosticsDescription",
						"Build one bounded, privacy-redacted support bundle from Finare reports and copy its path."),
					LOCTEXT("DiagnosticsButton", "Export Support Bundle"),
					CanRun,
					FOnClicked::CreateSP(
						this,
						&SFinareProjectSetup::ExportSupportBundle))
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

SFinareProjectSetup::~SFinareProjectSetup()
{
	if (DailyBuildCompletedHandle.IsValid() &&
		FModuleManager::Get().IsModuleLoaded(TEXT("FinareEditor")))
	{
		IFinareEditorModule::Get().OnDailyBuildCompleted().Remove(
			DailyBuildCompletedHandle);
	}
	CloseProcessResources();
}

FReply SFinareProjectSetup::PreviewAdoption()
{
	return RunAttachedOperation(
		EFinareProjectSetupOperation::PreviewAdoption,
		LOCTEXT("PreviewOperation", "Adoption preview"));
}

FReply SFinareProjectSetup::RepairProjectFiles()
{
	return RunAttachedOperation(
		EFinareProjectSetupOperation::RepairAdoption,
		LOCTEXT("RepairOperation", "Project repair"));
}

FReply SFinareProjectSetup::RunDoctor()
{
	return RunAttachedOperation(
		EFinareProjectSetupOperation::Doctor,
		LOCTEXT("DoctorOperation", "Project doctor"));
}

FReply SFinareProjectSetup::BuildAndReload()
{
	if (IFinareEditorModule::Get().IsDailyBuildRunning())
	{
		SetStatus(
			LOCTEXT("DailyBuildAlreadyRunning", "Build & Reload is already running; this request was coalesced."),
			FAppStyle::GetSlateColor(TEXT("Colors.AccentBlue")));
	}
	FEditorFileUtils::SaveDirtyPackages(true, true, true);
	const bool bQueued = IFinareEditorModule::Get().RequestDailyBuild(
		EFinareDailyBuildReason::Manual,
		true);
	if (bQueued)
	{
		AppendOutput(TEXT("Queued the shared daily Build & Reload action (manual)."));
		SetStatus(
			LOCTEXT("DailyBuildQueued", "Running the shared daily Build & Reload action…"),
			FAppStyle::GetSlateColor(TEXT("Colors.AccentBlue")));
	}
	return FReply::Handled();
}

FReply SFinareProjectSetup::LocateFirstPlayClass()
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

FReply SFinareProjectSetup::StartAuthoringAndOpenFirstPlayClass()
{
	if (!CanAuthorFirstPlayClass())
	{
		return FReply::Handled();
	}

	FFinareProjectSetupPaths Paths;
	if (!ResolveProjectPaths(Paths))
	{
		return FReply::Handled();
	}
	FFinareProjectSetupCommand Command;
	FString Error;
	if (!FFinareProjectSetup::BuildAuthoringCommand(
		Paths,
		FirstPlayRequest.SourcePath,
		Command,
		Error))
	{
		AppendOutput(Error);
		SetStatus(FText::FromString(Error), FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
		return FReply::Handled();
	}

	UFinareAsset* ScriptAsset = Cast<UFinareAsset>(StaticLoadObject(
		UFinareAsset::StaticClass(),
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
			"Could not start the project-local Finare authoring server.");
		AppendOutput(ErrorText.ToString());
		SetStatus(ErrorText, FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
		return FReply::Handled();
	}
	FPlatformProcess::CloseProc(Process);

	AppendOutput(FString::Printf(
		TEXT("Authoring server %u launched for %s. Close its terminal to stop the server."),
		ProcessId,
		*FirstPlayRequest.SourcePath));
	IFinareEditorModule::Get().OpenAuthoringSurface(ScriptAsset);
	SetStatus(
		FText::Format(
			LOCTEXT(
				"AuthoringStarted",
				"Opening {0}. Wait for the authoring terminal to report its URL, then use Apply & Reload while the class is running."),
			FText::FromString(FirstPlayRequest.ManagedTypeName)),
		FAppStyle::GetSlateColor(TEXT("Colors.AccentGreen")));
	return FReply::Handled();
}

FReply SFinareProjectSetup::ExportSupportBundle()
{
	return RunAttachedOperation(
		EFinareProjectSetupOperation::ExportDiagnostics,
		LOCTEXT("ExportDiagnosticsOperation", "Support bundle export"));
}

FReply SFinareProjectSetup::RunAttachedOperation(
	EFinareProjectSetupOperation Operation,
	const FText& OperationLabel)
{
	if (!CanRunAttachedOperation())
	{
		return FReply::Handled();
	}

	FFinareProjectSetupPaths Paths;
	if (!ResolveProjectPaths(Paths))
	{
		return FReply::Handled();
	}
	if (Operation == EFinareProjectSetupOperation::ExportDiagnostics)
	{
		IFileManager::Get().MakeDirectory(*Paths.DiagnosticsInputRoot, true);
	}

	FFinareProjectSetupCommand Command;
	FString Error;
	if (!FFinareProjectSetup::BuildCommand(Operation, Paths, 0, Command, Error))
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
			&SFinareProjectSetup::PollAttachedOperation));
	return FReply::Handled();
}

FReply SFinareProjectSetup::BuildAndOnboard()
{
	if (!CanRunAttachedOperation())
	{
		return FReply::Handled();
	}

	FFinareProjectSetupPaths Paths;
	if (!ResolveProjectPaths(Paths))
	{
		return FReply::Handled();
	}

	FFinareProjectSetupCommand Command;
	FString Error;
	if (!FFinareProjectSetup::BuildCommand(
		EFinareProjectSetupOperation::BuildAfterEditorExit,
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
			"Finare will save dirty project packages and close the Editor. "
			"Native and managed onboarding will start after this Editor process exits, "
			"then the project will reopen at the first-Play step.\n\nContinue?"),
		LOCTEXT("BuildConfirmationTitle", "Build and Onboard Finare"));
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
			"Could not start the deferred Finare onboarding helper. The Editor remains open.");
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

bool SFinareProjectSetup::ResolveProjectPaths(
	FFinareProjectSetupPaths& OutPaths)
{
	const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
	if (!Plugin.IsValid())
	{
		const FText ErrorText = LOCTEXT("PluginMissing", "The Finare plugin descriptor could not be resolved.");
		AppendOutput(ErrorText.ToString());
		SetStatus(ErrorText, FAppStyle::GetSlateColor(TEXT("Colors.AccentRed")));
		return false;
	}

	const FString EngineRoot = FPaths::ConvertRelativePathToFull(
		FPaths::Combine(FPaths::EngineDir(), TEXT("..")));
	FString Error;
	if (!FFinareProjectSetup::ResolvePaths(
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

bool SFinareProjectSetup::CanRunAttachedOperation() const
{
	return !ActiveProcess.IsValid() &&
		!IFinareEditorModule::Get().IsDailyBuildRunning();
}

bool SFinareProjectSetup::CanLocateFirstPlayClass() const
{
	return !ActiveProcess.IsValid() && bHasFirstPlayRequest &&
		FirstPlayRequest.IsReady();
}

bool SFinareProjectSetup::CanAuthorFirstPlayClass() const
{
	return !ActiveProcess.IsValid() && bHasFirstPlayRequest &&
		FirstPlayRequest.IsReady() && !FirstPlayRequest.SourcePath.IsEmpty() &&
		!FirstPlayRequest.ScriptAssetPath.IsEmpty();
}

void SFinareProjectSetup::RefreshFirstPlayRequest()
{
	bHasFirstPlayRequest = false;
	FirstPlayRequest = FFinareFirstPlayRequest();
	const FString RequestPath = FFinareProjectSetup::GetFirstPlayRequestPath(
		FPaths::ProjectDir());
	if (!IFileManager::Get().FileExists(*RequestPath))
	{
		return;
	}

	FString Error;
	if (!FFinareProjectSetup::ReadFirstPlayRequest(
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

FText SFinareProjectSetup::GetFirstPlayDescription() const
{
	if (!bHasFirstPlayRequest)
	{
		return LOCTEXT(
			"FirstPlayNoRequest",
			"Create a Finare Actor or Actor Component class. Its exact build and Play handoff will appear here.");
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
		                        : LOCTEXT("FirstPlayComponentPlacement",
		                                  "Add the Component class to an Actor Blueprint");
	return FText::Format(
		LOCTEXT(
			"FirstPlayReadyDescription",
			"{0} is ready. {1}, then use the normal Play button."),
		FText::FromString(FirstPlayRequest.ManagedTypeName),
		Placement);
}

FText SFinareProjectSetup::GetAuthoringDescription() const
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

EActiveTimerReturnType SFinareProjectSetup::PollAttachedOperation(
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

void SFinareProjectSetup::AppendOutput(const FString& Text)
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

void SFinareProjectSetup::HandleDailyBuildCompleted(
	const FFinareDailyBuildResult& Result)
{
	AppendOutput(FString::Printf(
		TEXT("%s\nReport: %s"),
		*Result.Summary,
		*Result.ReportPath));
	const FSlateColor Color = Result.RequiresRestart()
		                          ? FAppStyle::GetSlateColor(TEXT("Colors.Warning"))
		                          : Result.IsSuccess()
		                          ? FAppStyle::GetSlateColor(TEXT("Colors.AccentGreen"))
		                          : FAppStyle::GetSlateColor(TEXT("Colors.AccentRed"));
	SetStatus(FText::FromString(Result.Summary), Color);
}

void SFinareProjectSetup::FinishAttachedOperation(int32 ReturnCode)
{
	const FString OperationLabel = MoveTemp(ActiveOperationLabel);
	const FString ReportPath = MoveTemp(ActiveReportPath);
	const TOptional<EFinareProjectSetupOperation> CompletedOperation = ActiveOperation;
	ActiveOperation.Reset();
	CloseProcessResources();
	if (ReturnCode == 0)
	{
		if (CompletedOperation.IsSet() &&
			CompletedOperation.GetValue() == EFinareProjectSetupOperation::ExportDiagnostics)
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
		TArray<FFinareEditorDiagnostic> Diagnostics =
			FFinareEditorDiagnostics::ReadReport(ReportPath);
		Diagnostics.Insert(
			FFinareEditorDiagnostics::FromSetupFailure(
				OperationLabel, ReturnCode, ReportPath),
			0);
		FFinareEditorDiagnostics::Publish(
			LOCTEXT("SetupDiagnosticsPage", "Project Setup"),
			Diagnostics);
	}
}

void SFinareProjectSetup::CloseProcessResources()
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

void SFinareProjectSetup::SetStatus(
	const FText& InStatus,
	const FSlateColor& InColor)
{
	StatusText = InStatus;
	StatusColor = InColor;
}

#undef LOCTEXT_NAMESPACE
