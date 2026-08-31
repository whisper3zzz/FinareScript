#include "Misc/AutomationTest.h"

#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"
#include "UnifiedScriptProjectSetup.h"

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

#endif
