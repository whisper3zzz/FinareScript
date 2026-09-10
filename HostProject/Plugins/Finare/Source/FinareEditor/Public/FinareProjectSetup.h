#pragma once

#include "CoreMinimal.h"

enum class EFinareProjectSetupOperation : uint8
{
    PreviewAdoption,
    RepairAdoption,
    Doctor,
    ExportDiagnostics,
    DailyBuildAttached,
    BuildAfterEditorExit,
};

struct FFinareProjectSetupPaths
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

struct FFinareProjectSetupCommand
{
    FString Executable;
    FString Arguments;
    FString ReportPath;
    bool bDetached = false;
};

/** One editor-attached dependency-aware build decision. */
struct FFinareDailyBuildReceipt
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
struct FFinareFirstPlayRequest
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
class FINAREEDITOR_API FFinareProjectSetup
{
public:
    static bool ResolvePaths(
        const FString& ProjectFile,
        const FString& PluginRoot,
        const FString& EngineRoot,
        FFinareProjectSetupPaths& OutPaths,
        FString& OutError);

    /** Resolves only the daily build contract, including a source-repository fallback. */
    static bool ResolveDailyBuildPaths(
        const FString& ProjectFile,
        const FString& PluginRoot,
        const FString& EngineRoot,
        FFinareProjectSetupPaths& OutPaths,
        FString& OutError);

    static bool BuildCommand(
        EFinareProjectSetupOperation Operation,
        const FFinareProjectSetupPaths& Paths,
        uint32 EditorProcessId,
        FFinareProjectSetupCommand& OutCommand,
        FString& OutError);

    static bool BuildAuthoringCommand(
        const FFinareProjectSetupPaths& Paths,
        const FString& SourcePath,
        FFinareProjectSetupCommand& OutCommand,
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
        FFinareFirstPlayRequest& OutRequest,
        FString& OutError);

    static bool ReadDailyBuildReceipt(
        const FString& ReportPath,
        FFinareDailyBuildReceipt& OutReceipt,
        FString& OutError);
};
