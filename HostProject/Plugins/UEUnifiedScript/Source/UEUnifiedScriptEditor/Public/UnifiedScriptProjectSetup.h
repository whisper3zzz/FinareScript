#pragma once

#include "CoreMinimal.h"

enum class EUnifiedScriptProjectSetupOperation : uint8
{
    PreviewAdoption,
    RepairAdoption,
    Doctor,
    BuildAfterEditorExit,
};

struct FUnifiedScriptProjectSetupPaths
{
    FString ProjectFile;
    FString ProjectRoot;
    FString PluginRoot;
    FString EngineRoot;
    FString CliExecutable;
    FString DeferredOnboardingScript;
    FString AuthoringScript;
    FString PreviewReport;
    FString RepairReport;
    FString DoctorReport;
    FString OnboardingReport;
    FString FirstPlayRequest;
};

struct FUnifiedScriptProjectSetupCommand
{
    FString Executable;
    FString Arguments;
    FString ReportPath;
    bool bDetached = false;
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
};
