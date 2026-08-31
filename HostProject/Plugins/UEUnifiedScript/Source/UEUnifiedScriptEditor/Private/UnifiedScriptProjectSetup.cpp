#include "UnifiedScriptProjectSetup.h"

#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

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

bool WriteTextAtomically(const FString& Path, const FString& Text, FString& OutError)
{
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(Path), true);
    const FString TemporaryPath = Path + TEXT(".tmp");
    if (!FFileHelper::SaveStringToFile(
            Text,
            *TemporaryPath,
            FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
    {
        OutError = FString::Printf(TEXT("Cannot write first-Play request '%s'."), *TemporaryPath);
        return false;
    }
    if (!IFileManager::Get().Move(*Path, *TemporaryPath, true, true, false, true))
    {
        IFileManager::Get().Delete(*TemporaryPath, false, true);
        OutError = FString::Printf(TEXT("Cannot publish first-Play request '%s'."), *Path);
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
    Candidate.SetupScript = FPaths::Combine(
        Candidate.PluginRoot,
        TEXT("Tools/setup-unreal-project.ps1"));
    Candidate.DeferredOnboardingScript = FPaths::Combine(
        Candidate.PluginRoot,
        TEXT("Tools/run-unifiedscript-onboarding-after-process.ps1"));
    Candidate.AuthoringScript = FPaths::Combine(
        Candidate.PluginRoot,
        TEXT("Tools/start-unifiedscript-authoring.ps1"));
    const FString ReportRoot = FPaths::Combine(
        Candidate.ProjectRoot,
        TEXT("Saved/UnifiedScript/ProjectSetup"));
    Candidate.PreviewReport = FPaths::Combine(ReportRoot, TEXT("adoption-preview.json"));
    Candidate.RepairReport = FPaths::Combine(ReportRoot, TEXT("adoption-repair.json"));
    Candidate.DoctorReport = FPaths::Combine(ReportRoot, TEXT("doctor-report.json"));
    Candidate.OnboardingReport = FPaths::Combine(ReportRoot, TEXT("onboarding-report.json"));
    Candidate.DailyBuildReport = FPaths::Combine(ReportRoot, TEXT("daily-build-report.json"));
    Candidate.DiagnosticsInputRoot = FPaths::Combine(
        Candidate.ProjectRoot,
        TEXT("Saved/UnifiedScript"));
    Candidate.DiagnosticsBundle = FPaths::Combine(
        Candidate.ProjectRoot,
        TEXT("Saved/UnifiedScriptSupport/UnifiedScriptSupport.zip"));
    Candidate.FirstPlayRequest = GetFirstPlayRequestPath(Candidate.ProjectRoot);

    if (!UE::UnifiedScript::ProjectSetup::Private::RequireFile(
            Candidate.ProjectFile,
            TEXT("Unreal project"),
            OutError) ||
        !UE::UnifiedScript::ProjectSetup::Private::RequireFile(
            Candidate.CliExecutable,
            TEXT("Project-local UnifiedScript CLI"),
            OutError) ||
        !UE::UnifiedScript::ProjectSetup::Private::RequireFile(
            Candidate.SetupScript,
            TEXT("Project-local onboarding tool"),
            OutError) ||
        !UE::UnifiedScript::ProjectSetup::Private::RequireFile(
            Candidate.DeferredOnboardingScript,
            TEXT("Deferred onboarding tool"),
            OutError) ||
        !UE::UnifiedScript::ProjectSetup::Private::RequireFile(
            Candidate.AuthoringScript,
            TEXT("Authoring launcher"),
            OutError))
    {
        return false;
    }
    OutPaths = MoveTemp(Candidate);
    OutError.Reset();
    return true;
}

bool FUnifiedScriptProjectSetup::ResolveDailyBuildPaths(
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

    Candidate.SetupScript = FPaths::Combine(
        Candidate.PluginRoot,
        TEXT("Tools/setup-unreal-project.ps1"));
    if (!IFileManager::Get().FileExists(*Candidate.SetupScript))
    {
        const FString RepositoryRoot = FPaths::ConvertRelativePathToFull(
            FPaths::Combine(Candidate.PluginRoot, TEXT("../../..")));
        const FString RepositoryMarker = FPaths::Combine(
            RepositoryRoot,
            TEXT("UnifiedScript.slnx"));
        const FString RepositorySetup = FPaths::Combine(
            RepositoryRoot,
            TEXT("tools/setup-unreal-project.ps1"));
        if (IFileManager::Get().FileExists(*RepositoryMarker) &&
            IFileManager::Get().FileExists(*RepositorySetup))
        {
            Candidate.SetupScript = RepositorySetup;
        }
    }
    Candidate.DailyBuildReport = FPaths::Combine(
        Candidate.ProjectRoot,
        TEXT("Saved/UnifiedScript/ProjectSetup/daily-build-report.json"));

    if (!UE::UnifiedScript::ProjectSetup::Private::RequireFile(
            Candidate.ProjectFile,
            TEXT("Unreal project"),
            OutError) ||
        !UE::UnifiedScript::ProjectSetup::Private::RequireFile(
            Candidate.SetupScript,
            TEXT("Daily onboarding tool"),
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
    case EUnifiedScriptProjectSetupOperation::ExportDiagnostics:
        Candidate.Executable = Paths.CliExecutable;
        Candidate.ReportPath = Paths.DiagnosticsBundle;
        Candidate.Arguments = FString::Printf(
            TEXT("diagnostics-bundle %s --out %s"),
            *Quote(Paths.DiagnosticsInputRoot),
            *Quote(Candidate.ReportPath));
        break;
    case EUnifiedScriptProjectSetupOperation::DailyBuildAttached:
        Candidate.Executable = TEXT("powershell.exe");
        Candidate.ReportPath = Paths.DailyBuildReport;
        Candidate.Arguments = FString::Printf(
            TEXT("-NoProfile -ExecutionPolicy Bypass -File %s -Project %s ")
            TEXT("-EngineRoot %s -Report %s -EditorAttached"),
            *Quote(Paths.SetupScript),
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
            TEXT("-WaitForProcessId %u -Report %s -FirstPlayRequest %s -RelaunchEditor"),
            *Quote(Paths.DeferredOnboardingScript),
            *Quote(Paths.ProjectFile),
            *Quote(Paths.EngineRoot),
            EditorProcessId,
            *Quote(Candidate.ReportPath),
            *Quote(Paths.FirstPlayRequest));
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

bool FUnifiedScriptProjectSetup::BuildAuthoringCommand(
    const FUnifiedScriptProjectSetupPaths& Paths,
    const FString& SourcePath,
    FUnifiedScriptProjectSetupCommand& OutCommand,
    FString& OutError)
{
    using namespace UE::UnifiedScript::ProjectSetup::Private;
    FString ResolvedSourcePath = FPaths::ConvertRelativePathToFull(SourcePath);
    FPaths::NormalizeFilename(ResolvedSourcePath);
    if (!RequireFile(ResolvedSourcePath, TEXT("Script Class source"), OutError) ||
        !RequireFile(Paths.AuthoringScript, TEXT("Authoring launcher"), OutError))
    {
        return false;
    }

    FUnifiedScriptProjectSetupCommand Candidate;
    Candidate.Executable = TEXT("powershell.exe");
    Candidate.bDetached = true;
    Candidate.Arguments = FString::Printf(
        TEXT("-NoProfile -ExecutionPolicy Bypass -File %s -Project %s -EngineRoot %s -Script %s"),
        *Quote(Paths.AuthoringScript),
        *Quote(Paths.ProjectFile),
        *Quote(Paths.EngineRoot),
        *Quote(ResolvedSourcePath));
    OutCommand = MoveTemp(Candidate);
    OutError.Reset();
    return true;
}

FString FUnifiedScriptProjectSetup::GetFirstPlayRequestPath(const FString& ProjectRoot)
{
    FString Root = FPaths::ConvertRelativePathToFull(ProjectRoot);
    FPaths::NormalizeDirectoryName(Root);
    return FPaths::Combine(
        Root,
        TEXT("Saved/UnifiedScript/ProjectSetup/first-play-request.json"));
}

bool FUnifiedScriptProjectSetup::WriteFirstPlayRequest(
    const FString& ProjectRoot,
    const FString& RequestId,
    const FString& ClassKind,
    const FString& BlueprintAssetPath,
    const FString& ScriptAssetPath,
    const FString& SourcePath,
    const FString& ManagedTypeName,
    FString& OutRequestPath,
    FString& OutError)
{
    if (RequestId.IsEmpty() ||
        (ClassKind != TEXT("actor") && ClassKind != TEXT("actorComponent")) ||
        BlueprintAssetPath.IsEmpty() || ScriptAssetPath.IsEmpty() ||
        SourcePath.IsEmpty() || ManagedTypeName.IsEmpty())
    {
        OutError = TEXT("First-Play request requires exact class, asset, source, and managed identities.");
        return false;
    }

    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(
        TEXT("schemaVersion"),
        TEXT("unifiedscript.first-play-request/v0.1"));
    Root->SetStringField(TEXT("requestId"), RequestId);
    Root->SetStringField(TEXT("status"), TEXT("buildRequired"));
    Root->SetStringField(TEXT("classKind"), ClassKind);
    Root->SetStringField(TEXT("blueprintAssetPath"), BlueprintAssetPath);
    Root->SetStringField(TEXT("scriptAssetPath"), ScriptAssetPath);
    Root->SetStringField(TEXT("sourcePath"), FPaths::ConvertRelativePathToFull(SourcePath));
    Root->SetStringField(TEXT("managedTypeName"), ManagedTypeName);

    FString Json;
    const TSharedRef<TJsonWriter<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>> Writer =
        TJsonWriterFactory<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>::Create(&Json);
    if (!FJsonSerializer::Serialize(Root, Writer))
    {
        OutError = TEXT("Cannot serialize the first-Play request.");
        return false;
    }
    Json.AppendChar(TEXT('\n'));

    OutRequestPath = GetFirstPlayRequestPath(ProjectRoot);
    if (!UE::UnifiedScript::ProjectSetup::Private::WriteTextAtomically(
            OutRequestPath,
            Json,
            OutError))
    {
        OutRequestPath.Reset();
        return false;
    }
    OutError.Reset();
    return true;
}

bool FUnifiedScriptProjectSetup::ReadFirstPlayRequest(
    const FString& RequestPath,
    FUnifiedScriptFirstPlayRequest& OutRequest,
    FString& OutError)
{
    FString Json;
    if (!FFileHelper::LoadFileToString(Json, *RequestPath))
    {
        OutError = FString::Printf(TEXT("Cannot read first-Play request '%s'."), *RequestPath);
        return false;
    }
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutError = FString::Printf(
            TEXT("Invalid first-Play request '%s': %s"),
            *RequestPath,
            *Reader->GetErrorMessage());
        return false;
    }

    FString Schema;
    FUnifiedScriptFirstPlayRequest Candidate;
    if (!Root->TryGetStringField(TEXT("schemaVersion"), Schema) ||
        Schema != TEXT("unifiedscript.first-play-request/v0.1") ||
        !Root->TryGetStringField(TEXT("requestId"), Candidate.RequestId) ||
        !Root->TryGetStringField(TEXT("status"), Candidate.Status) ||
        !Root->TryGetStringField(TEXT("classKind"), Candidate.ClassKind) ||
        !Root->TryGetStringField(TEXT("blueprintAssetPath"), Candidate.BlueprintAssetPath) ||
        !Root->TryGetStringField(TEXT("scriptAssetPath"), Candidate.ScriptAssetPath) ||
        !Root->TryGetStringField(TEXT("sourcePath"), Candidate.SourcePath) ||
        !Root->TryGetStringField(TEXT("managedTypeName"), Candidate.ManagedTypeName) ||
        (Candidate.Status != TEXT("buildRequired") &&
            Candidate.Status != TEXT("ready") &&
            Candidate.Status != TEXT("failed")))
    {
        OutError = TEXT("First-Play request schema, status, or class identity is invalid.");
        return false;
    }
    Root->TryGetStringField(TEXT("onboardingReport"), Candidate.OnboardingReport);
    double ExitCode = 0.0;
    if (Root->TryGetNumberField(TEXT("exitCode"), ExitCode))
    {
        Candidate.ExitCode = static_cast<int32>(ExitCode);
    }

    OutRequest = MoveTemp(Candidate);
    OutError.Reset();
    return true;
}

bool FUnifiedScriptProjectSetup::ReadDailyBuildReceipt(
    const FString& ReportPath,
    FUnifiedScriptDailyBuildReceipt& OutReceipt,
    FString& OutError)
{
    FString Json;
    if (!FFileHelper::LoadFileToString(Json, *ReportPath))
    {
        OutError = FString::Printf(TEXT("Cannot read daily build report '%s'."), *ReportPath);
        return false;
    }
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutError = FString::Printf(
            TEXT("Invalid daily build report '%s': %s"),
            *ReportPath,
            *Reader->GetErrorMessage());
        return false;
    }

    FString Schema;
    FString Mode;
    FUnifiedScriptDailyBuildReceipt Candidate;
    if (!Root->TryGetStringField(TEXT("schemaVersion"), Schema) ||
        Schema != TEXT("unifiedscript.unreal-project-onboarding-report/v0.1") ||
        !Root->TryGetStringField(TEXT("mode"), Mode) || Mode != TEXT("dailyEditor") ||
        !Root->TryGetStringField(TEXT("status"), Candidate.Status) ||
        (Candidate.Status != TEXT("passed") &&
            Candidate.Status != TEXT("restartRequired") &&
            Candidate.Status != TEXT("failed")))
    {
        OutError = TEXT("Daily build report schema, mode, or status is invalid.");
        return false;
    }

    const TSharedPtr<FJsonObject>* Outputs = nullptr;
    if (Root->TryGetObjectField(TEXT("outputs"), Outputs) &&
        Outputs != nullptr && Outputs->IsValid())
    {
        (*Outputs)->TryGetStringField(
            TEXT("requiredAction"), Candidate.RequiredAction);
        (*Outputs)->TryGetStringField(
            TEXT("restartRequiredStage"), Candidate.RestartRequiredStage);
        (*Outputs)->TryGetStringField(TEXT("restartReason"), Candidate.RestartReason);
        const TSharedPtr<FJsonObject>* BuildGraph = nullptr;
        if ((*Outputs)->TryGetObjectField(TEXT("buildGraph"), BuildGraph) &&
            BuildGraph != nullptr && BuildGraph->IsValid())
        {
            double Count = 0.0;
            if ((*BuildGraph)->TryGetNumberField(TEXT("executed"), Count))
            {
                Candidate.ExecutedStages = static_cast<int32>(Count);
            }
            if ((*BuildGraph)->TryGetNumberField(TEXT("skipped"), Count))
            {
                Candidate.SkippedStages = static_cast<int32>(Count);
            }
            if ((*BuildGraph)->TryGetNumberField(TEXT("deferred"), Count))
            {
                Candidate.DeferredStages = static_cast<int32>(Count);
            }
        }
    }
    const TSharedPtr<FJsonObject>* Error = nullptr;
    if (Root->TryGetObjectField(TEXT("error"), Error) &&
        Error != nullptr && Error->IsValid())
    {
        (*Error)->TryGetStringField(TEXT("code"), Candidate.ErrorCode);
        (*Error)->TryGetStringField(TEXT("message"), Candidate.ErrorMessage);
    }
    if (Candidate.Status == TEXT("restartRequired") && !Candidate.RequiresRestart())
    {
        OutError = TEXT("Restart-required daily build report has no restartEditor action.");
        return false;
    }

    OutReceipt = MoveTemp(Candidate);
    OutError.Reset();
    return true;
}
