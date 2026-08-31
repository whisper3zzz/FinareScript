#include "UEUnifiedScriptEditorModule.h"

#include "AssetToolsModule.h"
#include "Containers/Ticker.h"
#include "Dom/JsonObject.h"
#include "EdGraphUtilities.h"
#include "Editor/EditorEngine.h"
#include "Engine/Blueprint.h"
#include "Features/IModularFeatures.h"
#include "Graph/SUnifiedScriptGraphNode.h"
#include "HAL/IConsoleManager.h"
#include "HAL/PlatformMisc.h"
#include "HAL/PlatformProcess.h"
#include "Interfaces/IPluginManager.h"
#include "IPIEAuthorizer.h"
#include "KismetPins/SGraphPinExec.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "SGraphNodeKnot.h"
#include "Styling/AppStyle.h"
#include "ToolMenus.h"
#include "UnifiedScriptEdGraph.h"
#include "UnifiedScriptClassFactory.h"
#include "UnifiedScriptClassAssetOperations.h"
#include "UnifiedScriptAssetDefinition.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptEditorDiagnostics.h"
#include "UnifiedScriptProjectSetup.h"
#include "Widgets/Docking/SDockTab.h"
#include "Widgets/SUnifiedScriptAuthoringSurface.h"
#include "Widgets/SUnifiedScriptProjectSetup.h"
#include "WorkspaceMenuStructure.h"
#include "WorkspaceMenuStructureModule.h"

#define LOCTEXT_NAMESPACE "UEUnifiedScriptEditorModule"

DEFINE_LOG_CATEGORY_STATIC(LogUnifiedScriptEditor, Log, All);

void FUnifiedScriptDailyBuildQueue::Enqueue(
    EUnifiedScriptDailyBuildReason Reason,
    bool bReloadComponents)
{
    if (IsActive() || HasPending())
    {
        ++CoalescedRequestCount;
    }
    PendingReasons |= static_cast<uint8>(Reason);
    bPendingReload |= bReloadComponents;
}

uint8 FUnifiedScriptDailyBuildQueue::Begin(bool& bOutReloadComponents)
{
    check(!IsActive());
    ActiveReasons = PendingReasons;
    bActiveReload = bPendingReload;
    PendingReasons = 0;
    bPendingReload = false;
    bOutReloadComponents = bActiveReload;
    return ActiveReasons;
}

bool FUnifiedScriptDailyBuildQueue::Complete()
{
    ActiveReasons = 0;
    bActiveReload = false;
    return HasPending();
}

void FUnifiedScriptDailyBuildQueue::CancelPending()
{
    PendingReasons = 0;
    ActiveReasons = 0;
    bPendingReload = false;
    bActiveReload = false;
}

namespace UE::UnifiedScript::Editor::Private
{
const FName AuthoringSurfaceTabName(TEXT("UnifiedScriptAuthoringSurface"));
const FName ProjectSetupTabName(TEXT("UnifiedScriptProjectSetup"));

class FUnifiedScriptGraphPanelNodeFactory final : public FGraphPanelNodeFactory
{
public:
    virtual TSharedPtr<SGraphNode> CreateNode(UEdGraphNode* Node) const override
    {
        if (UUnifiedScriptEdGraphNode* UnifiedScriptNode = Cast<UUnifiedScriptEdGraphNode>(Node))
        {
            return SNew(SUnifiedScriptGraphNode, UnifiedScriptNode);
        }
        if (UUnifiedScriptEdGraphRerouteNode* Reroute =
                Cast<UUnifiedScriptEdGraphRerouteNode>(Node))
        {
            return SNew(SGraphNodeKnot, Reroute);
        }
        return nullptr;
    }
};

class FUnifiedScriptGraphPanelPinFactory final : public FGraphPanelPinFactory
{
public:
    virtual TSharedPtr<SGraphPin> CreatePin(UEdGraphPin* Pin) const override
    {
        if (Pin != nullptr && Cast<UUnifiedScriptEdGraphNode>(Pin->GetOwningNode()) != nullptr &&
            Pin->PinType.PinCategory == TEXT("exec"))
        {
            return SNew(SGraphPinExec, Pin);
        }
        return nullptr;
    }
};

FString BuildDailyReloadTransaction(
    const FUnifiedScriptDailyBuildReceipt& Receipt,
    const FString& ReportPath)
{
    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(
        TEXT("schemaVersion"),
        TEXT("unifiedscript.reload-transaction/v0.1"));
    Root->SetStringField(
        TEXT("transactionId"),
        FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower));
    Root->SetStringField(TEXT("source"), TEXT("dailyBuild"));
    Root->SetStringField(TEXT("buildReport"), ReportPath);

    TArray<TSharedPtr<FJsonValue>> Phases;
    const auto AddPhase = [&Phases](const TCHAR* Name, const FString& Detail)
    {
        const TSharedRef<FJsonObject> Phase = MakeShared<FJsonObject>();
        Phase->SetStringField(TEXT("name"), Name);
        Phase->SetStringField(TEXT("state"), TEXT("published"));
        Phase->SetNumberField(TEXT("durationMicroseconds"), 0.0);
        Phase->SetStringField(TEXT("detail"), Detail);
        Phases.Add(MakeShared<FJsonValueObject>(Phase));
    };
    AddPhase(
        TEXT("dependencyAwareBuild"),
        FString::Printf(
            TEXT("Daily graph executed %d, skipped %d, and deferred %d Editor-only stages."),
            Receipt.ExecutedStages,
            Receipt.SkippedStages,
            Receipt.DeferredStages));
    AddPhase(
        TEXT("artifactPublication"),
        TEXT("Managed assemblies and compiled chunks were published atomically before reload."));
    Root->SetArrayField(TEXT("phases"), MoveTemp(Phases));

    FString Json;
    const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Json);
    return FJsonSerializer::Serialize(Root, Writer) ? Json : FString();
}

FString DescribeDailyBuildReasons(uint8 Reasons)
{
    TArray<FString> Labels;
    if ((Reasons & static_cast<uint8>(EUnifiedScriptDailyBuildReason::Manual)) != 0)
    {
        Labels.Add(TEXT("manual"));
    }
    if ((Reasons & static_cast<uint8>(EUnifiedScriptDailyBuildReason::GraphPublication)) != 0)
    {
        Labels.Add(TEXT("graphPublication"));
    }
    if ((Reasons & static_cast<uint8>(EUnifiedScriptDailyBuildReason::Play)) != 0)
    {
        Labels.Add(TEXT("play"));
    }
    return FString::Join(Labels, TEXT(", "));
}
}

class FUEUnifiedScriptEditorModule final
    : public IUEUnifiedScriptEditorModule
    , public IPIEAuthorizer
{
public:
    virtual void StartupModule() override
    {
        using namespace UE::UnifiedScript::Editor::Private;
        FUnifiedScriptClassAssetRecoveryResult Recovery;
        if (!FUnifiedScriptClassAssetOperations::RecoverPendingTransactions(Recovery))
        {
            UE_LOG(
                LogUnifiedScriptEditor,
                Error,
                TEXT("UnifiedScript could not recover pending Script Class operations: %s"),
                *Recovery.Error);
        }
        else if (Recovery.RecoveredReceiptCount > 0)
        {
            UE_LOG(
                LogUnifiedScriptEditor,
                Warning,
                TEXT("UnifiedScript recovered %d interrupted Script Class operation(s); archives: %s"),
                Recovery.RecoveredReceiptCount,
                *FString::Join(Recovery.ArchivePaths, TEXT(", ")));
        }
        FUnifiedScriptEditorDiagnostics::Register();
        UE::UnifiedScript::Editor::RegisterUnifiedScriptAssetOperationGuards();
        IModularFeatures::Get().RegisterModularFeature(
            IPIEAuthorizer::GetModularFeatureName(),
            this);
        GraphNodeFactory = MakeShared<FUnifiedScriptGraphPanelNodeFactory>();
        FEdGraphUtilities::RegisterVisualNodeFactory(GraphNodeFactory);
        GraphPinFactory = MakeShared<FUnifiedScriptGraphPanelPinFactory>();
        FEdGraphUtilities::RegisterVisualPinFactory(GraphPinFactory);

        FGlobalTabmanager::Get()->RegisterNomadTabSpawner(
            AuthoringSurfaceTabName,
            FOnSpawnTab::CreateRaw(this, &FUEUnifiedScriptEditorModule::SpawnAuthoringSurfaceTab))
            .SetDisplayName(LOCTEXT("AuthoringSurfaceTab", "UnifiedScript Authoring"))
            .SetTooltipText(LOCTEXT("AuthoringSurfaceTooltip", "Open the native UnifiedScript visual authoring workspace."))
            .SetGroup(WorkspaceMenu::GetMenuStructure().GetToolsCategory())
            .SetMenuType(ETabSpawnerMenuType::Hidden)
            .SetIcon(FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Code")));

        FGlobalTabmanager::Get()->RegisterNomadTabSpawner(
            ProjectSetupTabName,
            FOnSpawnTab::CreateRaw(this, &FUEUnifiedScriptEditorModule::SpawnProjectSetupTab))
            .SetDisplayName(LOCTEXT("ProjectSetupTab", "UnifiedScript Project Setup"))
            .SetTooltipText(LOCTEXT("ProjectSetupTooltip", "Adopt, diagnose, repair, and build this UnifiedScript project."))
            .SetGroup(WorkspaceMenu::GetMenuStructure().GetToolsCategory())
            .SetMenuType(ETabSpawnerMenuType::Hidden)
            .SetIcon(FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Settings")));

        OpenAuthoringConsoleCommand = IConsoleManager::Get().RegisterConsoleCommand(
            TEXT("UnifiedScript.OpenAuthoring"),
            TEXT("Open the UnifiedScript Authoring Surface."),
            FConsoleCommandDelegate::CreateRaw(
                this,
                &FUEUnifiedScriptEditorModule::OpenAuthoringSurfaceCommand),
            ECVF_Default);

        OpenProjectSetupConsoleCommand = IConsoleManager::Get().RegisterConsoleCommand(
            TEXT("UnifiedScript.OpenProjectSetup"),
            TEXT("Open UnifiedScript Project Setup."),
            FConsoleCommandDelegate::CreateRaw(
                this,
                &FUEUnifiedScriptEditorModule::OpenProjectSetup),
            ECVF_Default);

        BuildAndReloadConsoleCommand = IConsoleManager::Get().RegisterConsoleCommand(
            TEXT("UnifiedScript.BuildAndReload"),
            TEXT("Queue the shared editor-attached UnifiedScript Build & Reload action."),
            FConsoleCommandDelegate::CreateRaw(
                this,
                &FUEUnifiedScriptEditorModule::RequestDailyBuildCommand),
            ECVF_Default);

        UToolMenus::RegisterStartupCallback(
            FSimpleMulticastDelegate::FDelegate::CreateRaw(
                this,
                &FUEUnifiedScriptEditorModule::RegisterMenus));

        if (FParse::Param(FCommandLine::Get(), TEXT("UnifiedScriptFirstPlay")))
        {
            FirstPlayStartupHandle = FTSTicker::GetCoreTicker().AddTicker(
                FTickerDelegate::CreateRaw(
                    this,
                    &FUEUnifiedScriptEditorModule::OpenFirstPlayOnStartup),
                0.5f);
        }
    }

    virtual void ShutdownModule() override
    {
        IModularFeatures::Get().UnregisterModularFeature(
            IPIEAuthorizer::GetModularFeatureName(),
            this);
        ShutdownDailyBuild();
        UE::UnifiedScript::Editor::UnregisterUnifiedScriptAssetOperationGuards();
        if (GraphNodeFactory.IsValid())
        {
            FEdGraphUtilities::UnregisterVisualNodeFactory(GraphNodeFactory);
            GraphNodeFactory.Reset();
        }
        if (GraphPinFactory.IsValid())
        {
            FEdGraphUtilities::UnregisterVisualPinFactory(GraphPinFactory);
            GraphPinFactory.Reset();
        }
        UToolMenus::UnRegisterStartupCallback(this);
        UToolMenus::UnregisterOwner(this);
        if (FirstPlayStartupHandle.IsValid())
        {
            FTSTicker::GetCoreTicker().RemoveTicker(FirstPlayStartupHandle);
            FirstPlayStartupHandle.Reset();
        }
        if (CreatedClassSetupHandle.IsValid())
        {
            FTSTicker::GetCoreTicker().RemoveTicker(CreatedClassSetupHandle);
            CreatedClassSetupHandle.Reset();
        }
        if (OpenAuthoringConsoleCommand != nullptr)
        {
            IConsoleManager::Get().UnregisterConsoleObject(OpenAuthoringConsoleCommand);
            OpenAuthoringConsoleCommand = nullptr;
        }
        if (OpenProjectSetupConsoleCommand != nullptr)
        {
            IConsoleManager::Get().UnregisterConsoleObject(OpenProjectSetupConsoleCommand);
            OpenProjectSetupConsoleCommand = nullptr;
        }
        if (BuildAndReloadConsoleCommand != nullptr)
        {
            IConsoleManager::Get().UnregisterConsoleObject(
                BuildAndReloadConsoleCommand);
            BuildAndReloadConsoleCommand = nullptr;
        }
        FGlobalTabmanager::Get()->UnregisterNomadTabSpawner(
            UE::UnifiedScript::Editor::Private::AuthoringSurfaceTabName);
        FGlobalTabmanager::Get()->UnregisterNomadTabSpawner(
            UE::UnifiedScript::Editor::Private::ProjectSetupTabName);
        FUnifiedScriptEditorDiagnostics::Unregister();
    }

    virtual void OpenAuthoringSurface(UUnifiedScriptAsset* ScriptAsset) override
    {
        PendingAuthoringScriptAsset = ScriptAsset;
        FGlobalTabmanager::Get()->TryInvokeTab(
            UE::UnifiedScript::Editor::Private::AuthoringSurfaceTabName);
        if (AuthoringSurfaceWidget.IsValid() && IsValid(ScriptAsset))
        {
            AuthoringSurfaceWidget.Pin()->LoadScriptAsset(ScriptAsset);
            PendingAuthoringScriptAsset.Reset();
        }
    }

    virtual void OpenProjectSetup() override
    {
        FGlobalTabmanager::Get()->TryInvokeTab(
            UE::UnifiedScript::Editor::Private::ProjectSetupTabName);
    }

    virtual bool RequestDailyBuild(
        EUnifiedScriptDailyBuildReason Reason,
        bool bReloadComponents) override
    {
        DailyBuildQueue.Enqueue(Reason, bReloadComponents);
        if (DailyBuildQueue.IsActive())
        {
            return true;
        }
        return StartNextDailyBuild();
    }

    virtual bool IsDailyBuildRunning() const override
    {
        return DailyBuildQueue.IsActive() || DailyBuildProcess.IsValid();
    }

    virtual const FUnifiedScriptDailyBuildResult& GetLastDailyBuildResult() const override
    {
        return LastDailyBuildResult;
    }

    virtual FOnUnifiedScriptDailyBuildCompleted& OnDailyBuildCompleted() override
    {
        return DailyBuildCompleted;
    }

    virtual bool OpenAuthoringMember(const FString& SemanticId) override
    {
        PendingAuthoringSemanticId = SemanticId;
        FGlobalTabmanager::Get()->TryInvokeTab(
            UE::UnifiedScript::Editor::Private::AuthoringSurfaceTabName);
        if (AuthoringSurfaceWidget.IsValid())
        {
            const bool bNavigated =
                AuthoringSurfaceWidget.Pin()->NavigateToSemanticId(SemanticId);
            if (bNavigated)
            {
                PendingAuthoringSemanticId.Reset();
            }
            return bNavigated;
        }
        return false;
    }

private:
    TSharedPtr<FGraphPanelNodeFactory> GraphNodeFactory;
    TSharedPtr<FGraphPanelPinFactory> GraphPinFactory;
    IConsoleObject* OpenAuthoringConsoleCommand = nullptr;
    IConsoleObject* OpenProjectSetupConsoleCommand = nullptr;
    IConsoleObject* BuildAndReloadConsoleCommand = nullptr;
    FTSTicker::FDelegateHandle FirstPlayStartupHandle;
    FTSTicker::FDelegateHandle CreatedClassSetupHandle;
    TWeakPtr<SUnifiedScriptAuthoringSurface> AuthoringSurfaceWidget;
    TWeakObjectPtr<UUnifiedScriptAsset> PendingAuthoringScriptAsset;
    FString PendingAuthoringSemanticId;
    FUnifiedScriptDailyBuildQueue DailyBuildQueue;
    FUnifiedScriptDailyBuildResult LastDailyBuildResult;
    FOnUnifiedScriptDailyBuildCompleted DailyBuildCompleted;
    FProcHandle DailyBuildProcess;
    void* DailyBuildReadPipe = nullptr;
    void* DailyBuildWritePipe = nullptr;
    FTSTicker::FDelegateHandle DailyBuildTickerHandle;
    FTSTicker::FDelegateHandle ResumePlayTickerHandle;
    FString DailyBuildReportPath;
    FString DailyBuildOutput;
    bool bActiveDailyBuildReload = false;
    mutable bool bAllowNextPlay = false;
    mutable TOptional<FRequestPlaySessionParams> DeferredPlayRequest;

    virtual TValueOrError<bool, FText> IsPIEAuthorizedInternal(
        bool bIsSimulateInEditor) const override
    {
        if (!bAllowNextPlay && LastDailyBuildResult.RequiresRestart())
        {
            return MakeError(FText::FromString(FString::Printf(
                TEXT("UnifiedScript requires an Editor restart at %s before Play: %s"),
                *LastDailyBuildResult.RestartRequiredStage,
                *LastDailyBuildResult.RestartReason)));
        }
        return MakeValue(true);
    }

    virtual TValueOrError<bool, FText> RequestPIEPermissionInternal(
        bool bIsSimulateInEditor) const override
    {
        FUEUnifiedScriptEditorModule* MutableThis =
            const_cast<FUEUnifiedScriptEditorModule*>(this);
        if (bAllowNextPlay)
        {
            bAllowNextPlay = false;
            return MakeValue(true);
        }
        if (GEditor == nullptr || !GEditor->GetPlaySessionRequest().IsSet())
        {
            return MakeError(FText::FromString(
                TEXT("UnifiedScript could not retain the pending Play request.")));
        }

        DeferredPlayRequest = GEditor->GetPlaySessionRequest().GetValue();
        if (!MutableThis->RequestDailyBuild(
                EUnifiedScriptDailyBuildReason::Play,
                true))
        {
            DeferredPlayRequest.Reset();
            return MakeError(FText::FromString(
                TEXT("UnifiedScript could not start the daily Build & Reload gate.")));
        }
        return MakeError(FText::FromString(
            TEXT("UnifiedScript is running Build & Reload; Play will resume automatically after a compatible receipt.")));
    }

    bool StartNextDailyBuild()
    {
        if (DailyBuildQueue.IsActive() || !DailyBuildQueue.HasPending())
        {
            return false;
        }

        const uint8 Reasons = DailyBuildQueue.Begin(bActiveDailyBuildReload);
        DailyBuildOutput.Reset();
        LastDailyBuildResult = FUnifiedScriptDailyBuildResult{};
        LastDailyBuildResult.Status = TEXT("running");
        LastDailyBuildResult.Reasons = Reasons;
        LastDailyBuildResult.CoalescedRequestCount =
            DailyBuildQueue.CoalescedRequestCount;

        const TSharedPtr<IPlugin> Plugin =
            IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
        if (!Plugin.IsValid())
        {
            FinishDailyBuild(
                -1,
                TEXT("The UEUnifiedScript plugin descriptor could not be resolved."));
            return false;
        }

        FUnifiedScriptProjectSetupPaths Paths;
        FString Error;
        const FString EngineRoot = FPaths::ConvertRelativePathToFull(
            FPaths::Combine(FPaths::EngineDir(), TEXT("..")));
        if (!FUnifiedScriptProjectSetup::ResolveDailyBuildPaths(
                FPaths::GetProjectFilePath(),
                Plugin->GetBaseDir(),
                EngineRoot,
                Paths,
                Error))
        {
            FinishDailyBuild(-1, Error);
            return false;
        }

        FUnifiedScriptProjectSetupCommand Command;
        if (!FUnifiedScriptProjectSetup::BuildCommand(
                EUnifiedScriptProjectSetupOperation::DailyBuildAttached,
                Paths,
                0,
                Command,
                Error))
        {
            FinishDailyBuild(-1, Error);
            return false;
        }
        DailyBuildReportPath = Command.ReportPath;
        IFileManager::Get().MakeDirectory(
            *FPaths::GetPath(DailyBuildReportPath),
            true);
        IFileManager::Get().Delete(*DailyBuildReportPath, false, true, true);
        if (!FPlatformProcess::CreatePipe(
                DailyBuildReadPipe,
                DailyBuildWritePipe))
        {
            FinishDailyBuild(-1, TEXT("Could not create the daily build output pipe."));
            return false;
        }

        uint32 ProcessId = 0;
        DailyBuildProcess = FPlatformProcess::CreateProc(
            *Command.Executable,
            *Command.Arguments,
            false,
            true,
            true,
            &ProcessId,
            0,
            *Paths.ProjectRoot,
            DailyBuildWritePipe,
            nullptr,
            DailyBuildWritePipe);
        if (!DailyBuildProcess.IsValid())
        {
            FinishDailyBuild(-1, TEXT("Could not start the daily Build & Reload process."));
            return false;
        }

        UE_LOG(
            LogUnifiedScriptEditor,
            Display,
            TEXT("UnifiedScript daily Build & Reload started for %s (process %u)."),
            *UE::UnifiedScript::Editor::Private::DescribeDailyBuildReasons(Reasons),
            ProcessId);
        DailyBuildTickerHandle = FTSTicker::GetCoreTicker().AddTicker(
            FTickerDelegate::CreateRaw(
                this,
                &FUEUnifiedScriptEditorModule::PollDailyBuild),
            0.1f);
        return true;
    }

    bool PollDailyBuild(float DeltaTime)
    {
        if (DailyBuildReadPipe != nullptr)
        {
            DailyBuildOutput += FPlatformProcess::ReadPipe(DailyBuildReadPipe);
        }
        if (DailyBuildProcess.IsValid() &&
            FPlatformProcess::IsProcRunning(DailyBuildProcess))
        {
            return true;
        }

        int32 ReturnCode = -1;
        if (!DailyBuildProcess.IsValid() ||
            !FPlatformProcess::GetProcReturnCode(DailyBuildProcess, &ReturnCode))
        {
            ReturnCode = -1;
        }
        DailyBuildTickerHandle.Reset();
        FinishDailyBuild(ReturnCode, FString());
        return false;
    }

    void FinishDailyBuild(int32 ReturnCode, const FString& LaunchError)
    {
        if (DailyBuildReadPipe != nullptr)
        {
            DailyBuildOutput += FPlatformProcess::ReadPipe(DailyBuildReadPipe);
        }
        CloseDailyBuildProcess();

        FUnifiedScriptDailyBuildResult Result;
        Result.ReturnCode = ReturnCode;
        Result.ReportPath = DailyBuildReportPath;
        Result.Reasons = DailyBuildQueue.ActiveReasons;
        Result.CoalescedRequestCount = DailyBuildQueue.CoalescedRequestCount;

        FUnifiedScriptDailyBuildReceipt Receipt;
        FString ReceiptError;
        if (!LaunchError.IsEmpty())
        {
            Result.Status = TEXT("failed");
            Result.Summary = LaunchError;
        }
        else if (!FUnifiedScriptProjectSetup::ReadDailyBuildReceipt(
                     DailyBuildReportPath,
                     Receipt,
                     ReceiptError))
        {
            Result.Status = TEXT("failed");
            Result.Summary = MoveTemp(ReceiptError);
        }
        else
        {
            Result.Status = Receipt.Status;
            Result.RequiredAction = Receipt.RequiredAction;
            Result.RestartRequiredStage = Receipt.RestartRequiredStage;
            Result.RestartReason = Receipt.RestartReason;
            Result.ExecutedStages = Receipt.ExecutedStages;
            Result.SkippedStages = Receipt.SkippedStages;
            Result.DeferredStages = Receipt.DeferredStages;
            if (Receipt.RequiresRestart())
            {
                Result.Summary = FString::Printf(
                    TEXT("Restart required at %s: %s. No live generation changed."),
                    *Receipt.RestartRequiredStage,
                    *Receipt.RestartReason);
            }
            else if (ReturnCode == 0 && Receipt.IsReadyToReload())
            {
                FUnifiedScriptReloadBatchResult Reload;
                if (bActiveDailyBuildReload)
                {
                    const FString ReloadTransaction =
                        UE::UnifiedScript::Editor::Private::BuildDailyReloadTransaction(
                            Receipt,
                            DailyBuildReportPath);
                    Reload = SUnifiedScriptAuthoringSurface::ReloadAllScriptClassComponents(
                        ReloadTransaction);
                }
                Result.MatchedComponents = Reload.Matched;
                Result.ReloadedComponents = Reload.Reloaded;
                Result.FailedComponents = Reload.Failed;
                if (Reload.Failed > 0)
                {
                    Result.Status = TEXT("failed");
                }
                Result.Summary = FString::Printf(
                    TEXT("Daily Build completed: %d stages ran, %d skipped, %d deferred; %d components matched, %d reloaded, %d failed."),
                    Result.ExecutedStages,
                    Result.SkippedStages,
                    Result.DeferredStages,
                    Result.MatchedComponents,
                    Result.ReloadedComponents,
                    Result.FailedComponents);
            }
            else
            {
                Result.Status = TEXT("failed");
                Result.Summary = Receipt.ErrorMessage.IsEmpty()
                    ? FString::Printf(
                        TEXT("Daily Build & Reload failed with exit code %d."),
                        ReturnCode)
                    : Receipt.ErrorMessage;
            }
        }

        const bool bHasPending = DailyBuildQueue.Complete();
        LastDailyBuildResult = Result;
        PublishDailyBuildResult(Result);
        DailyBuildCompleted.Broadcast(LastDailyBuildResult);

        if (Result.IsSuccess() && bHasPending)
        {
            StartNextDailyBuild();
            return;
        }
        if (FParse::Param(
                FCommandLine::Get(),
                TEXT("UnifiedScriptExitAfterDailyBuild")))
        {
            FPlatformMisc::RequestExitWithStatus(
                false,
                Result.IsSuccess() ? 0 : 1);
        }
        if (!Result.IsSuccess())
        {
            DailyBuildQueue.CancelPending();
            DeferredPlayRequest.Reset();
        }
        else
        {
            ScheduleDeferredPlay();
        }
        DailyBuildQueue.CoalescedRequestCount = 0;
    }

    void PublishDailyBuildResult(const FUnifiedScriptDailyBuildResult& Result)
    {
        FUnifiedScriptEditorDiagnostic Diagnostic;
        Diagnostic.Domain = TEXT("dailyBuild");
        Diagnostic.Stage = Result.RestartRequiredStage.IsEmpty()
            ? TEXT("editorAttached")
            : Result.RestartRequiredStage;
        Diagnostic.Code = Result.RequiresRestart()
            ? TEXT("USONB3001")
            : Result.IsSuccess() ? TEXT("USDAILY0000") : TEXT("USDAILY0001");
        Diagnostic.Severity = Result.RequiresRestart()
            ? TEXT("warning")
            : Result.IsSuccess() ? TEXT("info") : TEXT("error");
        Diagnostic.Summary = Result.Summary;
        Diagnostic.Detail = FString::Printf(
            TEXT("Reasons: %s; coalesced requests: %d"),
            *UE::UnifiedScript::Editor::Private::DescribeDailyBuildReasons(
                Result.Reasons),
            Result.CoalescedRequestCount);
        if (!DailyBuildOutput.IsEmpty())
        {
            Diagnostic.Detail += TEXT("\n");
            Diagnostic.Detail += DailyBuildOutput.Right(4096);
        }
        Diagnostic.RecoveryAction = Result.RequiresRestart()
            ? TEXT("restartEditor")
            : Result.IsSuccess() ? FString() : TEXT("retry");
        Diagnostic.EvidencePath = Result.ReportPath;
        FUnifiedScriptEditorDiagnostics::Publish(
            LOCTEXT("DailyBuildDiagnosticsPage", "Daily Build & Reload"),
            {Diagnostic},
            !Result.IsSuccess());
    }

    void ScheduleDeferredPlay()
    {
        if (!DeferredPlayRequest.IsSet() || ResumePlayTickerHandle.IsValid())
        {
            return;
        }
        ResumePlayTickerHandle = FTSTicker::GetCoreTicker().AddTicker(
            FTickerDelegate::CreateRaw(
                this,
                &FUEUnifiedScriptEditorModule::ResumeDeferredPlay),
            0.0f);
    }

    bool ResumeDeferredPlay(float DeltaTime)
    {
        ResumePlayTickerHandle.Reset();
        if (GEditor != nullptr && DeferredPlayRequest.IsSet())
        {
            const FRequestPlaySessionParams Request =
                DeferredPlayRequest.GetValue();
            DeferredPlayRequest.Reset();
            bAllowNextPlay = true;
            GEditor->RequestPlaySession(Request);
        }
        return false;
    }

    void CloseDailyBuildProcess()
    {
        if (DailyBuildProcess.IsValid())
        {
            FPlatformProcess::CloseProc(DailyBuildProcess);
            DailyBuildProcess = FProcHandle();
        }
        if (DailyBuildReadPipe != nullptr || DailyBuildWritePipe != nullptr)
        {
            FPlatformProcess::ClosePipe(
                DailyBuildReadPipe,
                DailyBuildWritePipe);
            DailyBuildReadPipe = nullptr;
            DailyBuildWritePipe = nullptr;
        }
    }

    void ShutdownDailyBuild()
    {
        if (DailyBuildTickerHandle.IsValid())
        {
            FTSTicker::GetCoreTicker().RemoveTicker(DailyBuildTickerHandle);
            DailyBuildTickerHandle.Reset();
        }
        if (ResumePlayTickerHandle.IsValid())
        {
            FTSTicker::GetCoreTicker().RemoveTicker(ResumePlayTickerHandle);
            ResumePlayTickerHandle.Reset();
        }
        if (DailyBuildProcess.IsValid() &&
            FPlatformProcess::IsProcRunning(DailyBuildProcess))
        {
            FPlatformProcess::TerminateProc(DailyBuildProcess, true);
        }
        CloseDailyBuildProcess();
        DailyBuildQueue.CancelPending();
        DeferredPlayRequest.Reset();
    }

    TSharedRef<SDockTab> SpawnAuthoringSurfaceTab(const FSpawnTabArgs& Args)
    {
        TSharedRef<SUnifiedScriptAuthoringSurface> Surface =
            SNew(SUnifiedScriptAuthoringSurface);
        AuthoringSurfaceWidget = Surface;
        if (PendingAuthoringScriptAsset.IsValid())
        {
            Surface->LoadScriptAsset(PendingAuthoringScriptAsset.Get());
            PendingAuthoringScriptAsset.Reset();
        }
        if (!PendingAuthoringSemanticId.IsEmpty() &&
            Surface->NavigateToSemanticId(PendingAuthoringSemanticId))
        {
            PendingAuthoringSemanticId.Reset();
        }
        return SNew(SDockTab)
            .TabRole(ETabRole::NomadTab)
            [
                Surface
            ];
    }

    TSharedRef<SDockTab> SpawnProjectSetupTab(const FSpawnTabArgs& Args)
    {
        return SNew(SDockTab)
            .TabRole(ETabRole::NomadTab)
            [
                SNew(SUnifiedScriptProjectSetup)
            ];
    }

    void OpenAuthoringSurfaceCommand()
    {
        OpenAuthoringSurface(nullptr);
    }

    void RequestDailyBuildCommand()
    {
        RequestDailyBuild(EUnifiedScriptDailyBuildReason::Manual, true);
    }

    bool OpenFirstPlayOnStartup(float DeltaTime)
    {
        FirstPlayStartupHandle.Reset();
        OpenProjectSetup();
        return false;
    }

    bool OpenCreatedClassSetup(float DeltaTime)
    {
        CreatedClassSetupHandle.Reset();
        OpenProjectSetup();
        return false;
    }

    void ScheduleCreatedClassSetup()
    {
        if (CreatedClassSetupHandle.IsValid())
        {
            FTSTicker::GetCoreTicker().RemoveTicker(CreatedClassSetupHandle);
        }
        CreatedClassSetupHandle = FTSTicker::GetCoreTicker().AddTicker(
            FTickerDelegate::CreateRaw(
                this,
                &FUEUnifiedScriptEditorModule::OpenCreatedClassSetup),
            0.1f);
    }

    template <typename TFactory>
    void CreateScriptClass()
    {
        TFactory* Factory = NewObject<TFactory>(GetTransientPackage());
        UObject* CreatedAsset = FAssetToolsModule::GetModule().Get().CreateAssetWithDialog(
            UUnifiedScriptBlueprint::StaticClass(),
            Factory,
            TEXT("CreateUnifiedScriptClass"));
        if (CreatedAsset != nullptr)
        {
            // AssetTools may foreground the new Blueprint after this call.
            // Defer once so Project Setup remains the visible next action.
            ScheduleCreatedClassSetup();
        }
    }

    void RegisterMenus()
    {
        FToolMenuOwnerScoped OwnerScoped(this);
        UToolMenu* Menu = UToolMenus::Get()->ExtendMenu(TEXT("LevelEditor.MainMenu.Tools"));
        FToolMenuSection& Section = Menu->FindOrAddSection(TEXT("Programming"));
        Section.AddMenuEntry(
            TEXT("OpenUnifiedScriptAuthoring"),
            LOCTEXT("OpenAuthoring", "UnifiedScript Authoring"),
            LOCTEXT("OpenAuthoringTooltip", "Open the Slate Authoring Surface for C# state and Visual behaviors."),
            FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Code")),
            FUIAction(FExecuteAction::CreateRaw(
                this,
                &FUEUnifiedScriptEditorModule::OpenAuthoringSurfaceCommand)));
        Section.AddMenuEntry(
            TEXT("OpenUnifiedScriptProjectSetup"),
            LOCTEXT("OpenProjectSetup", "UnifiedScript Project Setup"),
            LOCTEXT("OpenProjectSetupTooltip", "Preview, repair, diagnose, or build this UnifiedScript project."),
            FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Settings")),
            FUIAction(FExecuteAction::CreateRaw(
                this,
                &FUEUnifiedScriptEditorModule::OpenProjectSetup)));
        Section.AddMenuEntry(
            TEXT("CreateUnifiedScriptActorClass"),
            LOCTEXT("CreateActorClass", "Create UnifiedScript Class (Actor)"),
            LOCTEXT("CreateActorClassTooltip", "Create a placeable managed Actor class, C# source, Script Asset, and Blueprint proxy."),
            FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("ClassIcon.Actor")),
            FUIAction(FExecuteAction::CreateRaw(
                this,
                &FUEUnifiedScriptEditorModule::CreateScriptClass<UUnifiedScriptActorClassFactory>)));
        Section.AddMenuEntry(
            TEXT("CreateUnifiedScriptActorComponentClass"),
            LOCTEXT("CreateActorComponentClass", "Create UnifiedScript Class (Actor Component)"),
            LOCTEXT("CreateActorComponentClassTooltip", "Create an attachable managed ActorComponent class, C# source, Script Asset, and Blueprint proxy."),
            FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("ClassIcon.ActorComponent")),
            FUIAction(FExecuteAction::CreateRaw(
                this,
                &FUEUnifiedScriptEditorModule::CreateScriptClass<UUnifiedScriptActorComponentClassFactory>)));
        UE::UnifiedScript::Editor::RegisterUnifiedScriptAssetContextMenu();
    }
};

IMPLEMENT_MODULE(FUEUnifiedScriptEditorModule, UEUnifiedScriptEditor)

#undef LOCTEXT_NAMESPACE
