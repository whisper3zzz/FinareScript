#pragma once

#include "CoreMinimal.h"

enum class EUnifiedScriptProjectSetupOperation : uint8
{
    PreviewAdoption,
    RepairAdoption,
    Doctor,
    ExportDiagnostics,
    DailyBuildAttached,
    BuildAfterEditorExit,
};

struct FUnifiedScriptProjectSetupPaths
{
    FString ProjectFile;
    FString ProjectRoot;
    FString PluginRoot;
    FString EngineRoot;
    FString CliExecutable;
    FString SetupScript;
    FString DeferredOnboardingScript;
    FString AuthoringScript;
    FString PreviewReport;
    FString RepairReport;
    FString DoctorReport;
    FString OnboardingReport;
    FString DailyBuildReport;
    FString DiagnosticsInputRoot;
    FString DiagnosticsBundle;
    FString FirstPlayRequest;
};

struct FUnifiedScriptProjectSetupCommand
{
    FString Executable;
    FString Arguments;
    FString ReportPath;
    bool bDetached = false;
};

/** One editor-attached dependency-aware build decision. */
struct FUnifiedScriptDailyBuildReceipt
{
    FString Status;
    FString RequiredAction;
    FString RestartRequiredStage;
    FString RestartReason;
    FString ErrorCode;
    FString ErrorMessage;
    int32 ExecutedStages = 0;
    int32 SkippedStages = 0;
    int32 DeferredStages = 0;

    bool IsReadyToReload() const { return Status == TEXT("passed"); }
    bool RequiresRestart() const
    {
        return Status == TEXT("restartRequired") &&
            RequiredAction == TEXT("restartEditor");
    }
};

/** Persistent handoff from Script Class creation through deferred build and Editor restart. */
struct FUnifiedScriptFirstPlayRequest
{
    FString RequestId;
    FString Status;
    FString ClassKind;
    FString BlueprintAssetPath;
    FString ScriptAssetPath;
    FString SourcePath;
    FString ManagedTypeName;
    FString OnboardingReport;
    int32 ExitCode = 0;

    bool IsBuildRequired() const { return Status == TEXT("buildRequired"); }
    bool IsReady() const { return Status == TEXT("ready"); }
    bool HasFailed() const { return Status == TEXT("failed"); }
};

/** Resolves and builds the project-local M13.1A setup/doctor command contract. */
class UEUNIFIEDSCRIPTEDITOR_API FUnifiedScriptProjectSetup
{
public:
    static bool ResolvePaths(
        const FString& ProjectFile,
        const FString& PluginRoot,
        const FString& EngineRoot,
        FUnifiedScriptProjectSetupPaths& OutPaths,
        FString& OutError);

    /** Resolves only the daily build contract, including a source-repository fallback. */
    static bool ResolveDailyBuildPaths(
        const FString& ProjectFile,
        const FString& PluginRoot,
        const FString& EngineRoot,
        FUnifiedScriptProjectSetupPaths& OutPaths,
        FString& OutError);

    static bool BuildCommand(
        EUnifiedScriptProjectSetupOperation Operation,
        const FUnifiedScriptProjectSetupPaths& Paths,
        uint32 EditorProcessId,
        FUnifiedScriptProjectSetupCommand& OutCommand,
        FString& OutError);

    static bool BuildAuthoringCommand(
        const FUnifiedScriptProjectSetupPaths& Paths,
        const FString& SourcePath,
        FUnifiedScriptProjectSetupCommand& OutCommand,
        FString& OutError);

    static FString GetFirstPlayRequestPath(const FString& ProjectRoot);

    static bool WriteFirstPlayRequest(
        const FString& ProjectRoot,
        const FString& RequestId,
        const FString& ClassKind,
        const FString& BlueprintAssetPath,
        const FString& ScriptAssetPath,
        const FString& SourcePath,
        const FString& ManagedTypeName,
        FString& OutRequestPath,
        FString& OutError);

    static bool ReadFirstPlayRequest(
        const FString& RequestPath,
        FUnifiedScriptFirstPlayRequest& OutRequest,
        FString& OutError);

    static bool ReadDailyBuildReceipt(
        const FString& ReportPath,
        FUnifiedScriptDailyBuildReceipt& OutReceipt,
        FString& OutError);
};
