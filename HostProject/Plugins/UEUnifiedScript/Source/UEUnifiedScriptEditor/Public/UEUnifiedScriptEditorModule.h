#pragma once

#include "CoreMinimal.h"
#include "Modules/ModuleInterface.h"
#include "Modules/ModuleManager.h"

class UUnifiedScriptAsset;

enum class EUnifiedScriptDailyBuildReason : uint8
{
    Manual = 1 << 0,
    GraphPublication = 1 << 1,
    Play = 1 << 2,
};
ENUM_CLASS_FLAGS(EUnifiedScriptDailyBuildReason);

/** Pure request coalescer shared by the manual, Graph, and Play entry points. */
struct UEUNIFIEDSCRIPTEDITOR_API FUnifiedScriptDailyBuildQueue
{
    uint8 PendingReasons = 0;
    uint8 ActiveReasons = 0;
    bool bPendingReload = false;
    bool bActiveReload = false;
    int32 CoalescedRequestCount = 0;

    void Enqueue(EUnifiedScriptDailyBuildReason Reason, bool bReloadComponents);
    uint8 Begin(bool& bOutReloadComponents);
    bool Complete();
    void CancelPending();
    bool IsActive() const { return ActiveReasons != 0; }
    bool HasPending() const { return PendingReasons != 0; }
};

/** One shared Slate/diagnostic receipt from the daily build coordinator. */
struct UEUNIFIEDSCRIPTEDITOR_API FUnifiedScriptDailyBuildResult
{
    FString Status;
    FString Summary;
    FString ReportPath;
    FString RequiredAction;
    FString RestartRequiredStage;
    FString RestartReason;
    int32 ReturnCode = -1;
    int32 ExecutedStages = 0;
    int32 SkippedStages = 0;
    int32 DeferredStages = 0;
    int32 MatchedComponents = 0;
    int32 ReloadedComponents = 0;
    int32 FailedComponents = 0;
    uint8 Reasons = 0;
    int32 CoalescedRequestCount = 0;

    bool IsSuccess() const { return Status == TEXT("passed"); }
    bool RequiresRestart() const
    {
        return Status == TEXT("restartRequired") &&
            RequiredAction == TEXT("restartEditor");
    }
};

DECLARE_MULTICAST_DELEGATE_OneParam(
    FOnUnifiedScriptDailyBuildCompleted,
    const FUnifiedScriptDailyBuildResult&);

/** Public editor navigation used by product workflows such as first Play. */
class UEUNIFIEDSCRIPTEDITOR_API IUEUnifiedScriptEditorModule : public IModuleInterface
{
public:
    static IUEUnifiedScriptEditorModule& Get()
    {
        return FModuleManager::LoadModuleChecked<IUEUnifiedScriptEditorModule>(
            TEXT("UEUnifiedScriptEditor"));
    }

    virtual void OpenAuthoringSurface(UUnifiedScriptAsset* ScriptAsset) = 0;
    virtual bool OpenAuthoringMember(const FString& SemanticId) = 0;
    virtual void OpenProjectSetup() = 0;
    virtual bool RequestDailyBuild(
        EUnifiedScriptDailyBuildReason Reason,
        bool bReloadComponents = true) = 0;
    virtual bool IsDailyBuildRunning() const = 0;
    virtual const FUnifiedScriptDailyBuildResult& GetLastDailyBuildResult() const = 0;
    virtual FOnUnifiedScriptDailyBuildCompleted& OnDailyBuildCompleted() = 0;
#if WITH_DEV_AUTOMATION_TESTS
    /** Scoped runtime tests supply compiled inputs; do not rebuild their fixtures on PIE startup. Returns the previous state. */
    virtual bool SetPrecompiledAutomationPlay(bool bEnabled) = 0;
#endif
};
