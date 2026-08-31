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

    IFileManager::Get().MakeDirectory(*FPaths::GetPath(CliExecutable), true);
    TestTrue(
        TEXT("Creates the project fixture"),
        FFileHelper::SaveStringToFile(TEXT("{}"), *ProjectFile));
    TestTrue(
        TEXT("Creates the project-local CLI fixture"),
        FFileHelper::SaveStringToFile(TEXT("fixture"), *CliExecutable));
    TestTrue(
        TEXT("Creates the deferred setup fixture"),
        FFileHelper::SaveStringToFile(TEXT("# fixture"), *DeferredScript));

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
        TEXT("Deferred script path"),
        Paths.DeferredOnboardingScript,
        FPaths::ConvertRelativePathToFull(DeferredScript));

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
    TestEqual(TEXT("Onboarding report"), Command.ReportPath, Paths.OnboardingReport);
    TestTrue(TEXT("Deferred command is detached"), Command.bDetached);

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
