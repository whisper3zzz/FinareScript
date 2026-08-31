#include "Misc/AutomationTest.h"

#include "HAL/FileManager.h"
#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "GameFramework/Actor.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"
#include "UnifiedScriptProjectSetup.h"
#include "UnifiedScriptReflectionExporter.h"
#include "EdGraphSchema_K2.h"
#include "UObject/Package.h"

#if WITH_DEV_AUTOMATION_TESTS

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptProjectSetupCommandContractTest,
    "UnifiedScript.Unreal.ProjectSetup.CommandContract",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptProjectSetupCommandContractTest::RunTest(const FString& Parameters)
{
    const FString TestRoot = FPaths::Combine(
        FPaths::ProjectSavedDir(),
        TEXT("Automation/UnifiedScriptProjectSetup"),
        FGuid::NewGuid().ToString(EGuidFormats::Digits));
    const FString ProjectFile = FPaths::Combine(TestRoot, TEXT("SetupGame.uproject"));
    const FString PluginRoot = FPaths::Combine(TestRoot, TEXT("Plugins/UEUnifiedScript"));
    const FString EngineRoot = FPaths::Combine(TestRoot, TEXT("EngineRoot"));
    const FString CliExecutable = FPaths::Combine(
        PluginRoot,
        TEXT("Tools/UnifiedScript.Cli/UnifiedScript.Cli.exe"));
    const FString DeferredScript = FPaths::Combine(
        PluginRoot,
        TEXT("Tools/run-unifiedscript-onboarding-after-process.ps1"));
    const FString SetupScript = FPaths::Combine(
        PluginRoot,
        TEXT("Tools/setup-unreal-project.ps1"));
    const FString AuthoringScript = FPaths::Combine(
        PluginRoot,
        TEXT("Tools/start-unifiedscript-authoring.ps1"));
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

    FUnifiedScriptProjectSetupPaths Paths;
    FString Error;
    TestTrue(
        TEXT("Resolves project-local setup paths"),
        FUnifiedScriptProjectSetup::ResolvePaths(
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
        FUnifiedScriptProjectSetup::GetFirstPlayRequestPath(TestRoot));

    FString FirstPlayPath;
    TestTrue(
        TEXT("Writes an exact Script Class first-Play request"),
        FUnifiedScriptProjectSetup::WriteFirstPlayRequest(
            TestRoot,
            TEXT("us_0123456789abcdef0123456789abcdef"),
            TEXT("actor"),
            TEXT("/Game/FirstPlayActor.FirstPlayActor"),
            TEXT("/Game/SA_FirstPlayActor.SA_FirstPlayActor"),
            ScriptClassSource,
            TEXT("SetupGame.UnifiedScript.FirstPlayActor"),
            FirstPlayPath,
            Error));
    TestEqual(TEXT("Writes the resolved first-Play path"), FirstPlayPath, Paths.FirstPlayRequest);
    FUnifiedScriptFirstPlayRequest FirstPlayRequest;
    TestTrue(
        TEXT("Reads the first-Play request"),
        FUnifiedScriptProjectSetup::ReadFirstPlayRequest(
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

    FUnifiedScriptProjectSetupCommand Command;
    TestTrue(
        TEXT("Builds preview command"),
        FUnifiedScriptProjectSetup::BuildCommand(
            EUnifiedScriptProjectSetupOperation::PreviewAdoption,
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
        FUnifiedScriptProjectSetup::BuildCommand(
            EUnifiedScriptProjectSetupOperation::RepairAdoption,
            Paths,
            0,
            Command,
            Error));
    TestTrue(TEXT("Repair apply flag"), Command.Arguments.Contains(TEXT(" --apply ")));
    TestEqual(TEXT("Repair report"), Command.ReportPath, Paths.RepairReport);

    TestTrue(
        TEXT("Builds doctor command"),
        FUnifiedScriptProjectSetup::BuildCommand(
            EUnifiedScriptProjectSetupOperation::Doctor,
            Paths,
            0,
            Command,
            Error));
    TestTrue(TEXT("Doctor engine root"), Command.Arguments.Contains(
        FString::Printf(TEXT("--engine-root \"%s\""), *Paths.EngineRoot)));
    TestEqual(TEXT("Doctor report"), Command.ReportPath, Paths.DoctorReport);

    TestTrue(
        TEXT("Builds support bundle command"),
        FUnifiedScriptProjectSetup::BuildCommand(
            EUnifiedScriptProjectSetupOperation::ExportDiagnostics,
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
        FUnifiedScriptProjectSetup::BuildCommand(
            EUnifiedScriptProjectSetupOperation::DailyBuildAttached,
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
        FUnifiedScriptProjectSetup::BuildCommand(
            EUnifiedScriptProjectSetupOperation::BuildAfterEditorExit,
            Paths,
            0,
            Command,
            Error));
    TestTrue(TEXT("Explains missing process ID"), Error.Contains(TEXT("process ID")));

    TestTrue(
        TEXT("Builds deferred onboarding command"),
        FUnifiedScriptProjectSetup::BuildCommand(
            EUnifiedScriptProjectSetupOperation::BuildAfterEditorExit,
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
        FUnifiedScriptProjectSetup::BuildAuthoringCommand(
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
    FUnifiedScriptDailyBuildReceipt DailyReceipt;
    TestTrue(
        TEXT("Reads a reload-ready daily receipt"),
        FUnifiedScriptProjectSetup::ReadDailyBuildReceipt(
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
        FUnifiedScriptProjectSetup::ReadDailyBuildReceipt(
            Paths.DailyBuildReport,
            DailyReceipt,
            Error));
    TestTrue(TEXT("Daily receipt requires restart"), DailyReceipt.RequiresRestart());
    TestEqual(
        TEXT("Daily restart stage"),
        DailyReceipt.RestartRequiredStage,
        FString(TEXT("native-final-build")));

    IFileManager::Get().DeleteDirectory(*TestRoot, false, true);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptBlueprintReflectionDiscoveryTest,
    "UnifiedScript.Unreal.ProjectSetup.BlueprintReflectionDiscovery",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptBlueprintReflectionDiscoveryTest::RunTest(const FString& Parameters)
{
    const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
    const FString AssetName = TEXT("BP_UnifiedScriptReflection_") + Suffix;
    const FString PackageName = TEXT("/Engine/Transient/") + AssetName;
    UPackage* Package = CreatePackage(*PackageName);
    UBlueprint* Blueprint = FKismetEditorUtilities::CreateBlueprint(
        AActor::StaticClass(),
        Package,
        FName(*AssetName),
        BPTYPE_Normal,
        UBlueprint::StaticClass(),
        UBlueprintGeneratedClass::StaticClass(),
        FName(TEXT("UnifiedScriptBlueprintReflectionDiscovery")));
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
        TEXT("Automation/UnifiedScriptBlueprintReflection"),
        Suffix + TEXT(".json")));
    FUnifiedScriptReflectionExportOptions Options;
    Options.OutputPath = OutputPath;
    Options.Modules.Add(TEXT("Engine"));
    Options.TypeNames.Add(Blueprint->GeneratedClass->GetName());
    Options.BlueprintAssetPaths.Add(Blueprint->GetPathName());
    FUnifiedScriptReflectionExportResult Result;
    FText Error;
    const bool bExported = FUnifiedScriptReflectionExporter::Export(Options, Result, Error);
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

#endif
