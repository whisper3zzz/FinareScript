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
    FString PreviewReport;
    FString RepairReport;
    FString DoctorReport;
    FString OnboardingReport;
};

struct FUnifiedScriptProjectSetupCommand
{
    FString Executable;
    FString Arguments;
    FString ReportPath;
    bool bDetached = false;
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
};
