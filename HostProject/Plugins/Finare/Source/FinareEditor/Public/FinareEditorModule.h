#pragma once

#include "CoreMinimal.h"
#include "Modules/ModuleInterface.h"
#include "Modules/ModuleManager.h"

class UFinareAsset;

enum class EFinareDailyBuildReason : uint8
{
	Manual = 1 << 0,
	GraphPublication = 1 << 1,
	Play = 1 << 2,
	SourceSave = 1 << 3,
};

ENUM_CLASS_FLAGS(EFinareDailyBuildReason);

/** Pure request coalescer shared by the manual, Graph, and Play entry points. */
struct FINAREEDITOR_API FFinareDailyBuildQueue
{
	uint8 PendingReasons = 0;
	uint8 ActiveReasons = 0;
	bool bPendingReload = false;
	bool bActiveReload = false;
	int32 CoalescedRequestCount = 0;

	void Enqueue(EFinareDailyBuildReason Reason, bool bReloadComponents);
	uint8 Begin(bool& bOutReloadComponents);
	bool Complete();
	void CancelPending();
	bool IsActive() const { return ActiveReasons != 0; }
	bool HasPending() const { return PendingReasons != 0; }
};

/** One shared Slate/diagnostic receipt from the daily build coordinator. */
struct FINAREEDITOR_API FFinareDailyBuildResult
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
	FOnFinareDailyBuildCompleted,
	const FFinareDailyBuildResult&);

/** Public editor navigation used by product workflows such as first Play. */
class FINAREEDITOR_API IFinareEditorModule : public IModuleInterface
{
public:
	static IFinareEditorModule& Get()
	{
		return FModuleManager::LoadModuleChecked<IFinareEditorModule>(
			TEXT("FinareEditor"));
	}

	virtual void OpenAuthoringSurface(UFinareAsset* ScriptAsset) = 0;
	virtual bool OpenAuthoringMember(const FString& SemanticId) = 0;
	virtual void OpenProjectSetup() = 0;
	virtual bool RequestDailyBuild(
		EFinareDailyBuildReason Reason,
		bool bReloadComponents = true) = 0;
	virtual bool IsDailyBuildRunning() const = 0;
	virtual const FFinareDailyBuildResult& GetLastDailyBuildResult() const = 0;
	virtual FOnFinareDailyBuildCompleted& OnDailyBuildCompleted() = 0;
#if WITH_DEV_AUTOMATION_TESTS
	/** Scoped runtime tests supply compiled inputs; do not rebuild their fixtures on PIE startup. Returns the previous state. */
	virtual bool SetPrecompiledAutomationPlay(bool bEnabled) = 0;
#endif
};
