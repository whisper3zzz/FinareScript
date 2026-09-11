#include "Misc/AutomationTest.h"

#include "Editor.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformTime.h"
#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "GameFramework/Actor.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Tests/AutomationEditorCommon.h"
#include "FinareEditorModule.h"
#include "FinareProjectSetup.h"
#include "FinareReflectionExporter.h"
#include "EdGraphSchema_K2.h"
#include "UObject/Package.h"

#if WITH_DEV_AUTOMATION_TESTS

namespace UE::Finare::Editor::Tests
{
	class FDailyBuildPlayGateCommand final : public IAutomationLatentCommand
	{
	public:
		explicit FDailyBuildPlayGateCommand(FAutomationTestBase* InTest)
			: Test(InTest)
		{
			CompletionHandle = IFinareEditorModule::Get()
			                   .OnDailyBuildCompleted()
			                   .AddRaw(this, &FDailyBuildPlayGateCommand::OnDailyBuildCompleted);
			PostPIEStartedHandle = FEditorDelegates::PostPIEStarted.AddRaw(
				this,
				&FDailyBuildPlayGateCommand::OnPostPIEStarted);
		}

		virtual ~FDailyBuildPlayGateCommand() override
		{
			IFinareEditorModule::Get()
				.OnDailyBuildCompleted()
				.Remove(CompletionHandle);
			FEditorDelegates::PostPIEStarted.Remove(PostPIEStartedHandle);
		}

		virtual bool Update() override
		{
			if (!bRequested)
			{
				bRequested = true;
				RequestStartedAt = FPlatformTime::Seconds();
				if (GEditor == nullptr)
				{
					Test->AddError(TEXT("The Play gate smoke test requires GEditor."));
					return true;
				}

				FRequestPlaySessionParams Request;
				Request.bAllowOnlineSubsystem = false;
				Request.StartLocation = FVector::ZeroVector;
				Request.StartRotation = FRotator::ZeroRotator;
				GEditor->RequestPlaySession(Request);
				GEditor->StartQueuedPlaySessionRequest();
				return false;
			}

			if (bPIEStarted)
			{
				Test->TestEqual(
					TEXT("The Play gate publishes one shared daily-build receipt"),
					CompletionCount,
					1);
				Test->TestTrue(
					TEXT("The Play gate receipt is compatible"),
					CompletionResult.IsSuccess());
				Test->TestTrue(
					TEXT("The shared build records Play as its trigger"),
					(CompletionResult.Reasons &
						static_cast<uint8>(EFinareDailyBuildReason::Play)) != 0);
				Test->TestFalse(
					TEXT("The daily build is drained before PIE begins"),
					IFinareEditorModule::Get().IsDailyBuildRunning());
				return true;
			}

			if (CompletionCount > 0 && !CompletionResult.IsSuccess())
			{
				Test->AddError(FString::Printf(
					TEXT("Build & Reload did not produce a Play-compatible receipt; status=%s, summary=%s"),
					*CompletionResult.Status,
					*CompletionResult.Summary));
				return true;
			}

			constexpr double TimeoutSeconds = 30.0;
			if (FPlatformTime::Seconds() - RequestStartedAt < TimeoutSeconds)
			{
				return false;
			}

			Test->AddError(FString::Printf(
				TEXT(
					"Timed out waiting for Build & Reload to resume PIE after %.0f seconds; completions=%d, status=%s, summary=%s"),
				TimeoutSeconds,
				CompletionCount,
				*CompletionResult.Status,
				*CompletionResult.Summary));
			return true;
		}

	private:
		void OnDailyBuildCompleted(const FFinareDailyBuildResult& Result)
		{
			++CompletionCount;
			CompletionResult = Result;
		}

		void OnPostPIEStarted(bool bIsSimulating)
		{
			bPIEStarted = true;
		}

		FAutomationTestBase* Test = nullptr;
		FDelegateHandle CompletionHandle;
		FDelegateHandle PostPIEStartedHandle;
		FFinareDailyBuildResult CompletionResult;
		double RequestStartedAt = 0.0;
		int32 CompletionCount = 0;
		bool bRequested = false;
		bool bPIEStarted = false;
	};
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareProjectSetupCommandContractTest,
	"Finare.Unreal.ProjectSetup.CommandContract",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareProjectSetupCommandContractTest::RunTest(const FString& Parameters)
{
	const FString TestRoot = FPaths::Combine(
		FPaths::ProjectSavedDir(),
		TEXT("Automation/FinareProjectSetup"),
		FGuid::NewGuid().ToString(EGuidFormats::Digits));
	const FString ProjectFile = FPaths::Combine(TestRoot, TEXT("SetupGame.uproject"));
	const FString PluginRoot = FPaths::Combine(TestRoot, TEXT("Plugins/Finare"));
	const FString EngineRoot = FPaths::Combine(TestRoot, TEXT("EngineRoot"));
	const FString CliExecutable = FPaths::Combine(
		PluginRoot,
		TEXT("Tools/Finare.Cli/Finare.Cli.exe"));
	const FString DeferredScript = FPaths::Combine(
		PluginRoot,
		TEXT("Tools/run-finare-onboarding-after-process.ps1"));
	const FString SetupScript = FPaths::Combine(
		PluginRoot,
		TEXT("Tools/setup-unreal-project.ps1"));
	const FString AuthoringScript = FPaths::Combine(
		PluginRoot,
		TEXT("Tools/start-finare-authoring.ps1"));
	const FString ScriptClassSource = FPaths::Combine(
		TestRoot,
		TEXT("Managed/FirstPlayActor.cs"));

	IFileManager::Get().MakeDirectory(*FPaths::GetPath(CliExecutable), true);
	TestTrue(
		TEXT("Creates the project fixture"),
		FFileHelper::SaveStringToFile(TEXT("{}"), *ProjectFile));
	TestTrue(
		TEXT("Creates the project-local CLI fixture"),
		FFileHelper::SaveStringToFile(TEXT("fixture"), *CliExecutable));
	TestTrue(
		TEXT("Creates the attached setup fixture"),
		FFileHelper::SaveStringToFile(TEXT("# fixture"), *SetupScript));
	TestTrue(
		TEXT("Creates the deferred setup fixture"),
		FFileHelper::SaveStringToFile(TEXT("# fixture"), *DeferredScript));
	TestTrue(
		TEXT("Creates the authoring launcher fixture"),
		FFileHelper::SaveStringToFile(TEXT("# fixture"), *AuthoringScript));
	IFileManager::Get().MakeDirectory(*FPaths::GetPath(ScriptClassSource), true);
	TestTrue(
		TEXT("Creates the Script Class source fixture"),
		FFileHelper::SaveStringToFile(TEXT("public sealed class FirstPlayActor {}"), *ScriptClassSource));

	FFinareProjectSetupPaths Paths;
	FString Error;
	TestTrue(
		TEXT("Resolves project-local setup paths"),
		FFinareProjectSetup::ResolvePaths(
			ProjectFile,
			PluginRoot,
			EngineRoot,
			Paths,
			Error));
	TestTrue(TEXT("Path resolution has no error"), Error.IsEmpty());
	TestEqual(TEXT("CLI path"), Paths.CliExecutable, FPaths::ConvertRelativePathToFull(CliExecutable));
	TestEqual(
		TEXT("Setup script path"),
		Paths.SetupScript,
		FPaths::ConvertRelativePathToFull(SetupScript));
	TestEqual(
		TEXT("Deferred script path"),
		Paths.DeferredOnboardingScript,
		FPaths::ConvertRelativePathToFull(DeferredScript));
	TestEqual(
		TEXT("Authoring script path"),
		Paths.AuthoringScript,
		FPaths::ConvertRelativePathToFull(AuthoringScript));
	TestEqual(
		TEXT("First-Play request path"),
		Paths.FirstPlayRequest,
		FFinareProjectSetup::GetFirstPlayRequestPath(TestRoot));

	const FString SourceRepository = FPaths::Combine(TestRoot, TEXT("SourceRepository"));
	const FString SourcePluginRoot = FPaths::Combine(
		SourceRepository,
		TEXT("HostProject/Plugins/Finare"));
	const FString SourceProject = FPaths::Combine(
		SourceRepository,
		TEXT("HostProject/SourceHost.uproject"));
	const FString SourceSetup = FPaths::Combine(
		SourceRepository,
		TEXT("tools/setup-unreal-project.ps1"));
	IFileManager::Get().MakeDirectory(*FPaths::GetPath(SourceProject), true);
	IFileManager::Get().MakeDirectory(*FPaths::GetPath(SourceSetup), true);
	TestTrue(TEXT("Creates source repository marker"), FFileHelper::SaveStringToFile(
		         TEXT("<Solution />"),
		         *FPaths::Combine(SourceRepository, TEXT("Finare.slnx"))));
	TestTrue(TEXT("Creates source repository project"), FFileHelper::SaveStringToFile(
		         TEXT("{}"),
		         *SourceProject));
	TestTrue(TEXT("Creates source repository setup"), FFileHelper::SaveStringToFile(
		         TEXT("# fixture"),
		         *SourceSetup));
	FFinareProjectSetupPaths DailyPaths;
	TestTrue(
		TEXT("Daily build resolves the source-repository tool fallback"),
		FFinareProjectSetup::ResolveDailyBuildPaths(
			SourceProject,
			SourcePluginRoot,
			EngineRoot,
			DailyPaths,
			Error));
	TestEqual(TEXT("Daily build uses the repository setup script"),
	          DailyPaths.SetupScript, FPaths::ConvertRelativePathToFull(SourceSetup));

	FString FirstPlayPath;
	TestTrue(
		TEXT("Writes an exact Script Class first-Play request"),
		FFinareProjectSetup::WriteFirstPlayRequest(
			TestRoot,
			TEXT("us_0123456789abcdef0123456789abcdef"),
			TEXT("actor"),
			TEXT("/Game/FirstPlayActor.FirstPlayActor"),
			TEXT("/Game/SA_FirstPlayActor.SA_FirstPlayActor"),
			ScriptClassSource,
			TEXT("SetupGame.Finare.FirstPlayActor"),
			FirstPlayPath,
			Error));
	TestEqual(TEXT("Writes the resolved first-Play path"), FirstPlayPath, Paths.FirstPlayRequest);
	FFinareFirstPlayRequest FirstPlayRequest;
	TestTrue(
		TEXT("Reads the first-Play request"),
		FFinareProjectSetup::ReadFirstPlayRequest(
			FirstPlayPath,
			FirstPlayRequest,
			Error));
	TestEqual(
		TEXT("First-Play request begins at the build gate"),
		FirstPlayRequest.Status,
		FString(TEXT("buildRequired")));
	TestEqual(
		TEXT("First-Play request pins the Blueprint asset"),
		FirstPlayRequest.BlueprintAssetPath,
		FString(TEXT("/Game/FirstPlayActor.FirstPlayActor")));

	FFinareProjectSetupCommand Command;
	TestTrue(
		TEXT("Builds preview command"),
		FFinareProjectSetup::BuildCommand(
			EFinareProjectSetupOperation::PreviewAdoption,
			Paths,
			0,
			Command,
			Error));
	TestEqual(TEXT("Preview executable"), Command.Executable, Paths.CliExecutable);
	TestTrue(TEXT("Preview flag"), Command.Arguments.Contains(TEXT(" --preview ")));
	TestTrue(TEXT("Preview SDK root"), Command.Arguments.Contains(
		         FString::Printf(TEXT("--sdk-root \"%s\""), *Paths.PluginRoot)));
	TestEqual(TEXT("Preview report"), Command.ReportPath, Paths.PreviewReport);
	TestFalse(TEXT("Preview is attached"), Command.bDetached);

	TestTrue(
		TEXT("Builds repair command"),
		FFinareProjectSetup::BuildCommand(
			EFinareProjectSetupOperation::RepairAdoption,
			Paths,
			0,
			Command,
			Error));
	TestTrue(TEXT("Repair apply flag"), Command.Arguments.Contains(TEXT(" --apply ")));
	TestEqual(TEXT("Repair report"), Command.ReportPath, Paths.RepairReport);

	TestTrue(
		TEXT("Builds doctor command"),
		FFinareProjectSetup::BuildCommand(
			EFinareProjectSetupOperation::Doctor,
			Paths,
			0,
			Command,
			Error));
	TestTrue(TEXT("Doctor engine root"), Command.Arguments.Contains(
		         FString::Printf(TEXT("--engine-root \"%s\""), *Paths.EngineRoot)));
	TestEqual(TEXT("Doctor report"), Command.ReportPath, Paths.DoctorReport);

	TestTrue(
		TEXT("Builds support bundle command"),
		FFinareProjectSetup::BuildCommand(
			EFinareProjectSetupOperation::ExportDiagnostics,
			Paths,
			0,
			Command,
			Error));
	TestEqual(TEXT("Support bundle executable"), Command.Executable, Paths.CliExecutable);
	TestTrue(TEXT("Support bundle input"), Command.Arguments.Contains(
		         FString::Printf(TEXT("diagnostics-bundle \"%s\""), *Paths.DiagnosticsInputRoot)));
	TestTrue(TEXT("Support bundle output"), Command.Arguments.Contains(
		         FString::Printf(TEXT("--out \"%s\""), *Paths.DiagnosticsBundle)));
	TestEqual(TEXT("Support bundle path"), Command.ReportPath, Paths.DiagnosticsBundle);
	TestFalse(TEXT("Support bundle command is attached"), Command.bDetached);

	TestTrue(
		TEXT("Builds the editor-attached daily command"),
		FFinareProjectSetup::BuildCommand(
			EFinareProjectSetupOperation::DailyBuildAttached,
			Paths,
			0,
			Command,
			Error));
	TestEqual(TEXT("Daily build executable"), Command.Executable, FString(TEXT("powershell.exe")));
	TestTrue(TEXT("Daily build setup script"), Command.Arguments.Contains(
		         FString::Printf(TEXT("-File \"%s\""), *Paths.SetupScript)));
	TestTrue(TEXT("Daily build stays Editor-attached"), Command.Arguments.Contains(TEXT(" -EditorAttached")));
	TestEqual(TEXT("Daily build report"), Command.ReportPath, Paths.DailyBuildReport);
	TestFalse(TEXT("Daily build command is attached"), Command.bDetached);

	TestFalse(
		TEXT("Rejects deferred onboarding without an Editor process"),
		FFinareProjectSetup::BuildCommand(
			EFinareProjectSetupOperation::BuildAfterEditorExit,
			Paths,
			0,
			Command,
			Error));
	TestTrue(TEXT("Explains missing process ID"), Error.Contains(TEXT("process ID")));

	TestTrue(
		TEXT("Builds deferred onboarding command"),
		FFinareProjectSetup::BuildCommand(
			EFinareProjectSetupOperation::BuildAfterEditorExit,
			Paths,
			4242,
			Command,
			Error));
	TestEqual(TEXT("Deferred executable"), Command.Executable, FString(TEXT("powershell.exe")));
	TestTrue(TEXT("Deferred wait process"), Command.Arguments.Contains(TEXT("-WaitForProcessId 4242")));
	TestTrue(TEXT("Deferred script"), Command.Arguments.Contains(
		         FString::Printf(TEXT("-File \"%s\""), *Paths.DeferredOnboardingScript)));
	TestTrue(TEXT("Deferred first-Play request"), Command.Arguments.Contains(
		         FString::Printf(TEXT("-FirstPlayRequest \"%s\""), *Paths.FirstPlayRequest)));
	TestTrue(TEXT("Deferred Editor relaunch"), Command.Arguments.Contains(TEXT(" -RelaunchEditor")));
	TestEqual(TEXT("Onboarding report"), Command.ReportPath, Paths.OnboardingReport);
	TestTrue(TEXT("Deferred command is detached"), Command.bDetached);

	TestTrue(
		TEXT("Builds exact Script Class authoring command"),
		FFinareProjectSetup::BuildAuthoringCommand(
			Paths,
			ScriptClassSource,
			Command,
			Error));
	TestEqual(TEXT("Authoring executable"), Command.Executable, FString(TEXT("powershell.exe")));
	TestTrue(TEXT("Authoring launcher"), Command.Arguments.Contains(
		         FString::Printf(TEXT("-File \"%s\""), *Paths.AuthoringScript)));
	TestTrue(TEXT("Authoring source"), Command.Arguments.Contains(
		         FString::Printf(
			         TEXT("-Script \"%s\""),
			         *FPaths::ConvertRelativePathToFull(ScriptClassSource))));
	TestTrue(TEXT("Authoring command is detached"), Command.bDetached);
	TestTrue(TEXT("Authoring command has no report"), Command.ReportPath.IsEmpty());

	const FString DailyPassedJson = FString::Printf(
		TEXT("{\"schemaVersion\":\"unifiedscript.unreal-project-onboarding-report/v0.1\",")
		TEXT("\"status\":\"passed\",\"mode\":\"dailyEditor\",\"outputs\":{")
		TEXT("\"buildGraph\":{\"executed\":3,\"skipped\":8,\"deferred\":2}}}"));
	TestTrue(
		TEXT("Writes the daily success receipt fixture"),
		FFileHelper::SaveStringToFile(DailyPassedJson, *Paths.DailyBuildReport));
	FFinareDailyBuildReceipt DailyReceipt;
	TestTrue(
		TEXT("Reads a reload-ready daily receipt"),
		FFinareProjectSetup::ReadDailyBuildReceipt(
			Paths.DailyBuildReport,
			DailyReceipt,
			Error));
	TestTrue(TEXT("Daily receipt is reload-ready"), DailyReceipt.IsReadyToReload());
	TestEqual(TEXT("Daily executed stage count"), DailyReceipt.ExecutedStages, 3);
	TestEqual(TEXT("Daily deferred stage count"), DailyReceipt.DeferredStages, 2);

	const FString DailyRestartJson =
		TEXT("{\"schemaVersion\":\"unifiedscript.unreal-project-onboarding-report/v0.1\",")
		TEXT("\"status\":\"restartRequired\",\"mode\":\"dailyEditor\",")
		TEXT("\"error\":{\"code\":\"USONB3001\",\"message\":\"restart\"},")
		TEXT("\"outputs\":{\"requiredAction\":\"restartEditor\",")
		TEXT("\"restartRequiredStage\":\"native-final-build\",")
		TEXT("\"restartReason\":\"generated native thunks changed\",")
		TEXT("\"buildGraph\":{\"executed\":4,\"skipped\":3,\"deferred\":0}}}");
	TestTrue(
		TEXT("Writes the daily restart receipt fixture"),
		FFileHelper::SaveStringToFile(DailyRestartJson, *Paths.DailyBuildReport));
	TestTrue(
		TEXT("Reads an actionable daily restart receipt"),
		FFinareProjectSetup::ReadDailyBuildReceipt(
			Paths.DailyBuildReport,
			DailyReceipt,
			Error));
	TestTrue(TEXT("Daily receipt requires restart"), DailyReceipt.RequiresRestart());
	TestEqual(
		TEXT("Daily restart stage"),
		DailyReceipt.RestartRequiredStage,
		FString(TEXT("native-final-build")));

	FFinareDailyBuildQueue DailyQueue;
	DailyQueue.Enqueue(EFinareDailyBuildReason::GraphPublication, true);
	DailyQueue.Enqueue(EFinareDailyBuildReason::GraphPublication, false);
	bool bReloadComponents = false;
	TestEqual(
		TEXT("Graph requests coalesce into one active reason"),
		DailyQueue.Begin(bReloadComponents),
		static_cast<uint8>(EFinareDailyBuildReason::GraphPublication));
	TestTrue(TEXT("Coalesced Graph request retains reload intent"), bReloadComponents);
	DailyQueue.Enqueue(EFinareDailyBuildReason::Play, true);
	DailyQueue.Enqueue(EFinareDailyBuildReason::Play, false);
	TestTrue(
		TEXT("Requests arriving during a build schedule one follow-up"),
		DailyQueue.Complete());
	TestEqual(
		TEXT("The follow-up retains only the pending Play reason"),
		DailyQueue.Begin(bReloadComponents),
		static_cast<uint8>(EFinareDailyBuildReason::Play));
	TestTrue(TEXT("The coalesced Play gate retains reload intent"), bReloadComponents);
	TestFalse(TEXT("The second build drains the queue"), DailyQueue.Complete());
	TestEqual(TEXT("Three duplicate/overlapping requests were coalesced"),
	          DailyQueue.CoalescedRequestCount, 3);

	IFileManager::Get().DeleteDirectory(*TestRoot, false, true);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareDailyBuildPlayGateTest,
	"Finare.Integration.DailyBuildPlayGate",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareDailyBuildPlayGateTest::RunTest(const FString& Parameters)
{
	if (!TestNotNull(
		TEXT("Creates a transient Editor world for the Play gate"),
		FAutomationEditorCommonUtils::CreateNewMap()))
	{
		return false;
	}
	TestFalse(
		TEXT("The Play gate starts with no active daily build"),
		IFinareEditorModule::Get().IsDailyBuildRunning());
	ADD_LATENT_AUTOMATION_COMMAND(
		UE::Finare::Editor::Tests::FDailyBuildPlayGateCommand(this));
	ADD_LATENT_AUTOMATION_COMMAND(FEndPlayMapCommand);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareBlueprintReflectionDiscoveryTest,
	"Finare.Unreal.ProjectSetup.BlueprintReflectionDiscovery",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareBlueprintReflectionDiscoveryTest::RunTest(const FString& Parameters)
{
	const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
	const FString AssetName = TEXT("BP_FinareReflection_") + Suffix;
	const FString PackageName = TEXT("/Engine/Transient/") + AssetName;
	UPackage* Package = CreatePackage(*PackageName);
	UBlueprint* Blueprint = FKismetEditorUtilities::CreateBlueprint(
		AActor::StaticClass(),
		Package,
		FName(*AssetName),
		BPTYPE_Normal,
		UBlueprint::StaticClass(),
		UBlueprintGeneratedClass::StaticClass(),
		FName(TEXT("FinareBlueprintReflectionDiscovery")));
	if (!TestNotNull(TEXT("Creates transient Blueprint fixture"), Blueprint))
	{
		return false;
	}

	FEdGraphPinType BoolType;
	BoolType.PinCategory = UEdGraphSchema_K2::PC_Boolean;
	TestTrue(
		TEXT("Adds Blueprint-visible fixture member"),
		FBlueprintEditorUtils::AddMemberVariable(Blueprint, FName(TEXT("Open")), BoolType));
	FKismetEditorUtilities::CompileBlueprint(Blueprint);
	if (!TestNotNull(
		TEXT("Compiles Blueprint generated class"),
		Blueprint->GeneratedClass.Get()))
	{
		return false;
	}

	const FString OutputPath = FPaths::ConvertRelativePathToFull(FPaths::Combine(
		FPaths::ProjectSavedDir(),
		TEXT("Automation/FinareBlueprintReflection"),
		Suffix + TEXT(".json")));
	FFinareReflectionExportOptions Options;
	Options.OutputPath = OutputPath;
	Options.Modules.Add(TEXT("Engine"));
	Options.TypeNames.Add(Blueprint->GeneratedClass->GetName());
	Options.BlueprintAssetPaths.Add(Blueprint->GetPathName());
	FFinareReflectionExportResult Result;
	FText Error;
	const bool bExported = FFinareReflectionExporter::Export(Options, Result, Error);
	if (!TestTrue(TEXT("Exports exact Blueprint asset and generated class"), bExported))
	{
		AddError(FString::Printf(TEXT("Blueprint reflection export: %s"), *Error.ToString()));
		return false;
	}
	TestTrue(TEXT("Blueprint export has no error"), Error.IsEmpty());
	TestEqual(TEXT("One Blueprint generated class is present"), Result.DiscoveredBlueprintClassCount, 1);

	FString Json;
	TestTrue(TEXT("Reads Blueprint reflection manifest"), FFileHelper::LoadFileToString(Json, *OutputPath));
	TestTrue(TEXT("Uses reflection v0.5"), Json.Contains(
		         TEXT("unifiedscript.unreal-reflection/v0.5")));
	TestTrue(TEXT("Carries exact Blueprint asset-generated class path"), Json.Contains(
		         Blueprint->GeneratedClass->GetPathName()));
	TestTrue(TEXT("Carries Blueprint mount as module identity"), Json.Contains(
		         TEXT("\"module\": \"Engine\"")));
	TestTrue(TEXT("Carries Blueprint-defined member"), Json.Contains(TEXT("\"name\": \"Open\"")));

	IFileManager::Get().Delete(*OutputPath, false, true);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareReflectionExporterDelegateEnumTest,
	"Finare.Unreal.ProjectSetup.ReflectionExporterDelegateEnum",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareReflectionExporterDelegateEnumTest::RunTest(const FString& Parameters)
{
	const FString OutputPath = FPaths::ConvertRelativePathToFull(FPaths::Combine(
		FPaths::ProjectSavedDir(),
		TEXT("Automation/FinareReflectionDelegateEnum"),
		FGuid::NewGuid().ToString(EGuidFormats::Digits) + TEXT(".json")));
	ON_SCOPE_EXIT
	{
		IFileManager::Get().Delete(*OutputPath, false, true);
	};

	FFinareReflectionExportOptions Options;
	Options.OutputPath = OutputPath;
	Options.Modules.Add(TEXT("Engine"));
	Options.ClassPaths.Add(TEXT("/Script/Engine.Actor"));
	FFinareReflectionExportResult Result;
	FText Error;
	if (!TestTrue(
		TEXT("Exports the Actor delegate dependency manifest"),
		FFinareReflectionExporter::Export(Options, Result, Error)))
	{
		AddError(FString::Printf(TEXT("Actor delegate reflection export: %s"), *Error.ToString()));
		return false;
	}

	FString Json;
	if (!TestTrue(
		TEXT("Reads the Actor delegate dependency manifest"),
		FFileHelper::LoadFileToString(Json, *OutputPath)))
	{
		return false;
	}

	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
	if (!TestTrue(
		TEXT("Parses the Actor delegate dependency manifest"),
		FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid()))
	{
		return false;
	}

	const TArray<TSharedPtr<FJsonValue>>* Types = nullptr;
	if (!TestTrue(
		TEXT("Reflection manifest contains types"),
		Root->TryGetArrayField(TEXT("types"), Types) && Types != nullptr))
	{
		return false;
	}

	bool bFoundEndPlayReason = false;
	for (const TSharedPtr<FJsonValue>& Value : *Types)
	{
		const TSharedPtr<FJsonObject> Type = Value.IsValid() ? Value->AsObject() : nullptr;
		if (!Type.IsValid())
		{
			continue;
		}

		FString Path;
		if (!Type->TryGetStringField(TEXT("path"), Path) ||
			!Path.Equals(TEXT("/Script/Engine.EEndPlayReason"), ESearchCase::CaseSensitive))
		{
			continue;
		}

		bFoundEndPlayReason = true;
		FString Kind;
		TestTrue(TEXT("EEndPlayReason is exported as a reflected enum"),
		         Type->TryGetStringField(TEXT("kind"), Kind));
		TestEqual(TEXT("EEndPlayReason reflected kind"), Kind, FString(TEXT("enum")));
		const TArray<TSharedPtr<FJsonValue>>* EnumValues = nullptr;
		TestTrue(TEXT("EEndPlayReason carries enum values"),
		         Type->TryGetArrayField(TEXT("enumValues"), EnumValues) && EnumValues != nullptr);
		if (EnumValues != nullptr)
		{
			TestTrue(TEXT("EEndPlayReason includes Quit"), EnumValues->ContainsByPredicate(
				         [](const TSharedPtr<FJsonValue>& EnumValue)
				         {
					         const TSharedPtr<FJsonObject> EnumObject =
						         EnumValue.IsValid() ? EnumValue->AsObject() : nullptr;
					         FString Name;
					         return EnumObject.IsValid() &&
						         EnumObject->TryGetStringField(TEXT("name"), Name) &&
						         Name.Equals(TEXT("Quit"), ESearchCase::CaseSensitive);
				         }));
		}
		break;
	}

	TestTrue(TEXT("Actor.OnEndPlay exports its EEndPlayReason dependency"), bFoundEndPlayReason);
	return true;
}

#endif
