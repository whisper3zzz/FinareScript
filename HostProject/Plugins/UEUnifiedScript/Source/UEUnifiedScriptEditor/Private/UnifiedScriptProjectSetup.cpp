#include "UnifiedScriptProjectSetup.h"

#include "HAL/FileManager.h"
#include "Misc/Paths.h"

namespace UE::UnifiedScript::ProjectSetup::Private
{
FString Quote(const FString& Value)
{
    FString Escaped = Value;
    Escaped.ReplaceInline(TEXT("\""), TEXT("\\\""), ESearchCase::CaseSensitive);
    return FString::Printf(TEXT("\"%s\""), *Escaped);
}

bool RequireFile(const FString& Path, const TCHAR* Label, FString& OutError)
{
    if (!IFileManager::Get().FileExists(*Path))
    {
        OutError = FString::Printf(TEXT("%s is missing: %s"), Label, *Path);
        return false;
    }
    return true;
}
}

bool FUnifiedScriptProjectSetup::ResolvePaths(
    const FString& ProjectFile,
    const FString& PluginRoot,
    const FString& EngineRoot,
    FUnifiedScriptProjectSetupPaths& OutPaths,
    FString& OutError)
{
    FUnifiedScriptProjectSetupPaths Candidate;
    Candidate.ProjectFile = FPaths::ConvertRelativePathToFull(ProjectFile);
    Candidate.ProjectRoot = FPaths::GetPath(Candidate.ProjectFile);
    Candidate.PluginRoot = FPaths::ConvertRelativePathToFull(PluginRoot);
    Candidate.EngineRoot = FPaths::ConvertRelativePathToFull(EngineRoot);
    FPaths::NormalizeDirectoryName(Candidate.ProjectRoot);
    FPaths::NormalizeDirectoryName(Candidate.PluginRoot);
    FPaths::NormalizeDirectoryName(Candidate.EngineRoot);

    Candidate.CliExecutable = FPaths::Combine(
        Candidate.PluginRoot,
        TEXT("Tools/UnifiedScript.Cli/UnifiedScript.Cli.exe"));
    Candidate.DeferredOnboardingScript = FPaths::Combine(
        Candidate.PluginRoot,
        TEXT("Tools/run-unifiedscript-onboarding-after-process.ps1"));
    const FString ReportRoot = FPaths::Combine(
        Candidate.ProjectRoot,
        TEXT("Saved/UnifiedScript/ProjectSetup"));
    Candidate.PreviewReport = FPaths::Combine(ReportRoot, TEXT("adoption-preview.json"));
    Candidate.RepairReport = FPaths::Combine(ReportRoot, TEXT("adoption-repair.json"));
    Candidate.DoctorReport = FPaths::Combine(ReportRoot, TEXT("doctor-report.json"));
    Candidate.OnboardingReport = FPaths::Combine(ReportRoot, TEXT("onboarding-report.json"));

    if (!UE::UnifiedScript::ProjectSetup::Private::RequireFile(
            Candidate.ProjectFile,
            TEXT("Unreal project"),
            OutError) ||
        !UE::UnifiedScript::ProjectSetup::Private::RequireFile(
            Candidate.CliExecutable,
            TEXT("Project-local UnifiedScript CLI"),
            OutError) ||
        !UE::UnifiedScript::ProjectSetup::Private::RequireFile(
            Candidate.DeferredOnboardingScript,
            TEXT("Deferred onboarding tool"),
            OutError))
    {
        return false;
    }
    OutPaths = MoveTemp(Candidate);
    OutError.Reset();
    return true;
}

bool FUnifiedScriptProjectSetup::BuildCommand(
    EUnifiedScriptProjectSetupOperation Operation,
    const FUnifiedScriptProjectSetupPaths& Paths,
    uint32 EditorProcessId,
    FUnifiedScriptProjectSetupCommand& OutCommand,
    FString& OutError)
{
    using namespace UE::UnifiedScript::ProjectSetup::Private;
    FUnifiedScriptProjectSetupCommand Candidate;
    switch (Operation)
    {
    case EUnifiedScriptProjectSetupOperation::PreviewAdoption:
        Candidate.Executable = Paths.CliExecutable;
        Candidate.ReportPath = Paths.PreviewReport;
        Candidate.Arguments = FString::Printf(
            TEXT("adopt %s --sdk-root %s --preview --report %s"),
            *Quote(Paths.ProjectFile),
            *Quote(Paths.PluginRoot),
            *Quote(Candidate.ReportPath));
        break;
    case EUnifiedScriptProjectSetupOperation::RepairAdoption:
        Candidate.Executable = Paths.CliExecutable;
        Candidate.ReportPath = Paths.RepairReport;
        Candidate.Arguments = FString::Printf(
            TEXT("adopt %s --sdk-root %s --apply --report %s"),
            *Quote(Paths.ProjectFile),
            *Quote(Paths.PluginRoot),
            *Quote(Candidate.ReportPath));
        break;
    case EUnifiedScriptProjectSetupOperation::Doctor:
        Candidate.Executable = Paths.CliExecutable;
        Candidate.ReportPath = Paths.DoctorReport;
        Candidate.Arguments = FString::Printf(
            TEXT("doctor %s --engine-root %s --report %s"),
            *Quote(Paths.ProjectFile),
            *Quote(Paths.EngineRoot),
            *Quote(Candidate.ReportPath));
        break;
    case EUnifiedScriptProjectSetupOperation::BuildAfterEditorExit:
        if (EditorProcessId == 0)
        {
            OutError = TEXT("Deferred onboarding requires the current Editor process ID.");
            return false;
        }
        Candidate.Executable = TEXT("powershell.exe");
        Candidate.ReportPath = Paths.OnboardingReport;
        Candidate.bDetached = true;
        Candidate.Arguments = FString::Printf(
            TEXT("-NoProfile -ExecutionPolicy Bypass -File %s -Project %s -EngineRoot %s ")
            TEXT("-WaitForProcessId %u -Report %s"),
            *Quote(Paths.DeferredOnboardingScript),
            *Quote(Paths.ProjectFile),
            *Quote(Paths.EngineRoot),
            EditorProcessId,
            *Quote(Candidate.ReportPath));
        break;
    default:
        OutError = TEXT("Unknown UnifiedScript project setup operation.");
        return false;
    }

    if (Candidate.Executable.IsEmpty() || Candidate.Arguments.IsEmpty())
    {
        OutError = TEXT("UnifiedScript project setup command is incomplete.");
        return false;
    }
    OutCommand = MoveTemp(Candidate);
    OutError.Reset();
    return true;
}
