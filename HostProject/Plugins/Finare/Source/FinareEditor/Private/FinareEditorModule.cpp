#include "FinareEditorModule.h"

#include "AssetToolsModule.h"
#include "Containers/Ticker.h"
#include "Dom/JsonObject.h"
#include "EdGraphUtilities.h"
#include "Editor/EditorEngine.h"
#include "Engine/Blueprint.h"
#include "Features/IModularFeatures.h"
#include "Graph/SFinareGraphNode.h"
#include "HAL/IConsoleManager.h"
#include "HAL/PlatformMisc.h"
#include "HAL/PlatformProcess.h"
#include "Interfaces/IPluginManager.h"
#include "IPIEAuthorizer.h"
#include "KismetCompiler.h"
#include "KismetCompilerModule.h"
#include "Kismet2/KismetReinstanceUtilities.h"
#include "KismetPins/SGraphPinExec.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "SGraphNodeKnot.h"
#include "Styling/AppStyle.h"
#include "ToolMenus.h"
#include "FinareEdGraph.h"
#include "FinareClassFactory.h"
#include "FinareClassAssetOperations.h"
#include "FinareAssetDefinition.h"
#include "FinareBlueprint.h"
#include "FinareEditorDiagnostics.h"
#include "FinareProjectSetup.h"
#include "FinareSourceWatcher.h"
#include "Widgets/Docking/SDockTab.h"
#include "Widgets/SFinareAuthoringSurface.h"
#include "Widgets/SFinareProjectSetup.h"
#include "WorkspaceMenuStructure.h"
#include "WorkspaceMenuStructureModule.h"

#define LOCTEXT_NAMESPACE "FinareEditorModule"

DEFINE_LOG_CATEGORY_STATIC(LogFinareEditor, Log, All);

void FFinareDailyBuildQueue::Enqueue(
	EFinareDailyBuildReason Reason,
	bool bReloadComponents)
{
	if (IsActive() || HasPending())
	{
		++CoalescedRequestCount;
	}
	PendingReasons |= static_cast<uint8>(Reason);
	bPendingReload |= bReloadComponents;
}

uint8 FFinareDailyBuildQueue::Begin(bool& bOutReloadComponents)
{
	check(!IsActive());
	ActiveReasons = PendingReasons;
	bActiveReload = bPendingReload;
	PendingReasons = 0;
	bPendingReload = false;
	bOutReloadComponents = bActiveReload;
	return ActiveReasons;
}

bool FFinareDailyBuildQueue::Complete()
{
	ActiveReasons = 0;
	bActiveReload = false;
	return HasPending();
}

void FFinareDailyBuildQueue::CancelPending()
{
	PendingReasons = 0;
	ActiveReasons = 0;
	bPendingReload = false;
	bActiveReload = false;
}

namespace UE::Finare::Editor::Private
{
	const FName AuthoringSurfaceTabName(TEXT("FinareAuthoringSurface"));
	const FName ProjectSetupTabName(TEXT("FinareProjectSetup"));

	class FFinareBlueprintCompilerContext final : public FKismetCompilerContext
	{
	public:
		FFinareBlueprintCompilerContext(
			UBlueprint* SourceBlueprint,
			FCompilerResultsLog& MessageLog,
			const FKismetCompilerOptions& CompilerOptions)
			: FKismetCompilerContext(SourceBlueprint, MessageLog, CompilerOptions)
		{
		}

	protected:
		virtual void CopyTermDefaultsToDefaultObject(UObject* DefaultObject) override
		{
			// Kismet may already have copied the old CDO over the newly constructed
			// one. Merge actual constructor defaults at the same point as terms.
			if (auto* Generated = Cast<UFinareBlueprintGeneratedClass>(DefaultObject->GetClass()))
			{
				if (!Generated->ManagedConstructionError.IsEmpty())
					MessageLog.Error(*FString::Printf(TEXT("Native construction: %s"), *Generated->ManagedConstructionError));
				for (const FBPVariableDescription& Variable : Blueprint->NewVariables)
				{
					const FName Key(TEXT("UnifiedScript.DeclarationDefault"));
					// Kismet consumes and clears Variable.DefaultValue during layout.
					// The pending term map retains the value being assigned this compile.
					const FString* Pending = DefaultPropertyValueMap.Find(Variable.VarName);
					if (Generated->ManagedConstructionProperties.Contains(Variable.VarName) &&
						Pending && Variable.HasMetaData(Key) && Variable.GetMetaData(Key) == *Pending)
					{
						if (const FString* Value = Generated->ManagedConstructionDefaults.Find(Variable.VarName))
							DefaultPropertyValueMap.Add(Variable.VarName, *Value);
					}
				}
			}
			FKismetCompilerContext::CopyTermDefaultsToDefaultObject(DefaultObject);
		}

		virtual void EnsureProperGeneratedClass(UClass*& InTargetClass) override
		{
			if (InTargetClass != nullptr &&
				Cast<UFinareBlueprintGeneratedClass>(InTargetClass) == nullptr)
			{
				FKismetCompilerUtilities::ConsignToOblivion(
					InTargetClass,
					Blueprint->bIsRegeneratingOnLoad);
				InTargetClass = nullptr;
			}
		}

		virtual void SpawnNewClass(const FString& NewClassName) override
		{
			ManagedClass = FindObject<UFinareBlueprintGeneratedClass>(
				Blueprint->GetOutermost(),
				*NewClassName);
			if (ManagedClass == nullptr)
			{
				ManagedClass = NewObject<UFinareBlueprintGeneratedClass>(
					Blueprint->GetOutermost(),
					FName(*NewClassName),
					RF_Public | RF_Transactional);
			}
			else
			{
				ManagedClass->ClassGeneratedBy = Blueprint;
				// Skeleton generation can run on the async loading thread.  The
				// managed binding table is runtime-owned and its recompile helper
				// deliberately requires the game thread; defer the actual bind
				// to the generated-class post-load/instance hooks in that case.
				if (IsInGameThread())
				{
					ManagedClass->PrepareManagedFunctionsForRecompile();
				}
				FBlueprintCompileReinstancer::Create(ManagedClass);
			}
			NewClass = ManagedClass;
		}

		virtual void OnNewClassSet(UBlueprintGeneratedClass* ClassToUse) override
		{
			ManagedClass = CastChecked<UFinareBlueprintGeneratedClass>(ClassToUse);
		}

		virtual void FinishCompilingClass(UClass* Class) override
		{
			if (auto* Generated = Cast<UFinareBlueprintGeneratedClass>(Class); Generated && IsInGameThread())
			{
				FString Error;
				auto* Unified = Cast<UFinareBlueprint>(Blueprint);
				if (!Generated->ConfigureManagedConstruction(Unified ? Unified->ScriptAsset.Get() : nullptr, Error))
					MessageLog.Error(*FString::Printf(TEXT("Native construction: %s"), *Error));
			}
			FKismetCompilerContext::FinishCompilingClass(Class);
			if (UFinareBlueprintGeneratedClass* GeneratedClass =
				Cast<UFinareBlueprintGeneratedClass>(Class))
			{
				// Direct Editor compiles must restore the same native contract as
				// declaration-driven materialization; users should not need a
				// second Build action after pressing Compile.
				// FinishCompilingClass is also reached by FastGenerateSkeletonClass
				// on the async loading thread.  BindManagedFunctions keeps its
				// game-thread check; the generated class's PostLoad/PostInitInstance
				// hooks perform the deferred rebind once the object is on the game
				// thread.
				if (IsInGameThread())
				{
					GeneratedClass->BindManagedFunctions();
				}
			}
		}

	private:
		UFinareBlueprintGeneratedClass* ManagedClass = nullptr;
	};

	TSharedPtr<FKismetCompilerContext> CreateFinareBlueprintCompiler(
		UBlueprint* Blueprint,
		FCompilerResultsLog& MessageLog,
		const FKismetCompilerOptions& CompilerOptions)
	{
		return MakeShared<FFinareBlueprintCompilerContext>(
			Blueprint,
			MessageLog,
			CompilerOptions);
	}

	class FFinareGraphPanelNodeFactory final : public FGraphPanelNodeFactory
	{
	public:
		virtual TSharedPtr<SGraphNode> CreateNode(UEdGraphNode* Node) const override
		{
			if (UFinareEdGraphNode* FinareNode = Cast<UFinareEdGraphNode>(Node))
			{
				return SNew(SFinareGraphNode, FinareNode);
			}
			if (UFinareEdGraphRerouteNode* Reroute =
				Cast<UFinareEdGraphRerouteNode>(Node))
			{
				return SNew(SGraphNodeKnot, Reroute);
			}
			return nullptr;
		}
	};

	class FFinareGraphPanelPinFactory final : public FGraphPanelPinFactory
	{
	public:
		virtual TSharedPtr<SGraphPin> CreatePin(UEdGraphPin* Pin) const override
		{
			if (Pin != nullptr && Cast<UFinareEdGraphNode>(Pin->GetOwningNode()) != nullptr &&
				Pin->PinType.PinCategory == TEXT("exec"))
			{
				return SNew(SGraphPinExec, Pin);
			}
			return nullptr;
		}
	};

	FString BuildDailyReloadTransaction(
		const FFinareDailyBuildReceipt& Receipt,
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
		if ((Reasons & static_cast<uint8>(EFinareDailyBuildReason::Manual)) != 0)
		{
			Labels.Add(TEXT("manual"));
		}
		if ((Reasons & static_cast<uint8>(EFinareDailyBuildReason::GraphPublication)) != 0)
		{
			Labels.Add(TEXT("graphPublication"));
		}
		if ((Reasons & static_cast<uint8>(EFinareDailyBuildReason::Play)) != 0)
		{
			Labels.Add(TEXT("play"));
		}
		if ((Reasons & static_cast<uint8>(EFinareDailyBuildReason::SourceSave)) != 0) Labels.Add(TEXT("sourceSave"));
		return FString::Join(Labels, TEXT(", "));
	}
}

class FFinareEditorModule final
	: public IFinareEditorModule
	  , public IPIEAuthorizer
{
public:
	virtual void StartupModule() override
	{
		using namespace UE::Finare::Editor::Private;
		IKismetCompilerInterface& KismetCompiler =
			FModuleManager::LoadModuleChecked<IKismetCompilerInterface>(
				KISMET_COMPILER_MODULENAME);
		KismetCompiler.OverrideBPGCTypeForBPType(
			UFinareBlueprint::StaticClass(),
			UFinareBlueprintGeneratedClass::StaticClass());
		FKismetCompilerContext::RegisterCompilerForBP(
			UFinareBlueprint::StaticClass(),
			&CreateFinareBlueprintCompiler);
		FFinareClassAssetRecoveryResult Recovery;
		if (!FFinareClassAssetOperations::RecoverPendingTransactions(Recovery))
		{
			UE_LOG(
				LogFinareEditor,
				Error,
				TEXT("Finare could not recover pending Script Class operations: %s"),
				*Recovery.Error);
		}
		else if (Recovery.RecoveredReceiptCount > 0)
		{
			UE_LOG(
				LogFinareEditor,
				Warning,
				TEXT("Finare recovered %d interrupted Script Class operation(s); archives: %s"),
				Recovery.RecoveredReceiptCount,
				*FString::Join(Recovery.ArchivePaths, TEXT(", ")));
		}
		FFinareEditorDiagnostics::Register();
		UE::Finare::Editor::RegisterFinareAssetOperationGuards();
		IModularFeatures::Get().RegisterModularFeature(
			IPIEAuthorizer::GetModularFeatureName(),
			this);
		GraphNodeFactory = MakeShared<FFinareGraphPanelNodeFactory>();
		FEdGraphUtilities::RegisterVisualNodeFactory(GraphNodeFactory);
		GraphPinFactory = MakeShared<FFinareGraphPanelPinFactory>();
		FEdGraphUtilities::RegisterVisualPinFactory(GraphPinFactory);

		FGlobalTabmanager::Get()->RegisterNomadTabSpawner(
			                        AuthoringSurfaceTabName,
			                        FOnSpawnTab::CreateRaw(this, &FFinareEditorModule::SpawnAuthoringSurfaceTab))
		                        .SetDisplayName(LOCTEXT("AuthoringSurfaceTab", "Finare Authoring"))
		                        .SetTooltipText(LOCTEXT("AuthoringSurfaceTooltip",
		                                                "Open the native Finare visual authoring workspace."))
		                        .SetGroup(WorkspaceMenu::GetMenuStructure().GetToolsCategory())
		                        .SetMenuType(ETabSpawnerMenuType::Hidden)
		                        .SetIcon(FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Code")));

		FGlobalTabmanager::Get()->RegisterNomadTabSpawner(
			                        ProjectSetupTabName,
			                        FOnSpawnTab::CreateRaw(this, &FFinareEditorModule::SpawnProjectSetupTab))
		                        .SetDisplayName(LOCTEXT("ProjectSetupTab", "Finare Project Setup"))
		                        .SetTooltipText(LOCTEXT("ProjectSetupTooltip",
		                                                "Adopt, diagnose, repair, and build this Finare project."))
		                        .SetGroup(WorkspaceMenu::GetMenuStructure().GetToolsCategory())
		                        .SetMenuType(ETabSpawnerMenuType::Hidden)
		                        .SetIcon(FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Settings")));

		OpenAuthoringConsoleCommand = IConsoleManager::Get().RegisterConsoleCommand(
			TEXT("Finare.OpenAuthoring"),
			TEXT("Open the Finare Authoring Surface, optionally for an exact Script Asset object path."),
			FConsoleCommandWithArgsDelegate::CreateRaw(
				this,
				&FFinareEditorModule::OpenAuthoringAssetCommand),
			ECVF_Default);

		OpenProjectSetupConsoleCommand = IConsoleManager::Get().RegisterConsoleCommand(
			TEXT("Finare.OpenProjectSetup"),
			TEXT("Open Finare Project Setup."),
			FConsoleCommandDelegate::CreateRaw(
				this,
				&FFinareEditorModule::OpenProjectSetup),
			ECVF_Default);

		BuildAndReloadConsoleCommand = IConsoleManager::Get().RegisterConsoleCommand(
			TEXT("Finare.BuildAndReload"),
			TEXT("Queue the shared editor-attached Finare Build & Reload action."),
			FConsoleCommandDelegate::CreateRaw(
				this,
				&FFinareEditorModule::RequestDailyBuildCommand),
			ECVF_Default);

		UToolMenus::RegisterStartupCallback(
			FSimpleMulticastDelegate::FDelegate::CreateRaw(
				this,
				&FFinareEditorModule::RegisterMenus));

		if (!IsRunningCommandlet())
			SourceWatcher = MakeUnique<FFinareSourceWatcher>(
				FPaths::Combine(FPaths::ProjectConfigDir(), TEXT("Finare.onboarding.json")),
				[this]() { RequestDailyBuild(EFinareDailyBuildReason::SourceSave, true); });

		if (FParse::Param(FCommandLine::Get(), TEXT("FinareFirstPlay")))
		{
			FirstPlayStartupHandle = FTSTicker::GetCoreTicker().AddTicker(
				FTickerDelegate::CreateRaw(
					this,
					&FFinareEditorModule::OpenFirstPlayOnStartup),
				0.5f);
		}
	}

	virtual void ShutdownModule() override
	{
		SourceWatcher.Reset();
		IModularFeatures::Get().UnregisterModularFeature(
			IPIEAuthorizer::GetModularFeatureName(),
			this);
		ShutdownDailyBuild();
		UE::Finare::Editor::UnregisterFinareAssetOperationGuards();
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
			UE::Finare::Editor::Private::AuthoringSurfaceTabName);
		FGlobalTabmanager::Get()->UnregisterNomadTabSpawner(
			UE::Finare::Editor::Private::ProjectSetupTabName);
		FFinareEditorDiagnostics::Unregister();
	}

	virtual void OpenAuthoringSurface(UFinareAsset* ScriptAsset) override
	{
		PendingAuthoringScriptAsset = ScriptAsset;
		FGlobalTabmanager::Get()->TryInvokeTab(
			UE::Finare::Editor::Private::AuthoringSurfaceTabName);
		if (AuthoringSurfaceWidget.IsValid() && IsValid(ScriptAsset))
		{
			AuthoringSurfaceWidget.Pin()->LoadScriptAsset(ScriptAsset);
			PendingAuthoringScriptAsset.Reset();
		}
	}

	virtual void OpenProjectSetup() override
	{
		FGlobalTabmanager::Get()->TryInvokeTab(
			UE::Finare::Editor::Private::ProjectSetupTabName);
	}

	virtual bool RequestDailyBuild(
		EFinareDailyBuildReason Reason,
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

	virtual const FFinareDailyBuildResult& GetLastDailyBuildResult() const override
	{
		return LastDailyBuildResult;
	}

	virtual FOnFinareDailyBuildCompleted& OnDailyBuildCompleted() override
	{
		return DailyBuildCompleted;
	}

#if WITH_DEV_AUTOMATION_TESTS
	virtual bool SetPrecompiledAutomationPlay(bool bEnabled) override
	{
		check(GIsAutomationTesting);
		const bool bPrevious = bPrecompiledAutomationPlay;
		bPrecompiledAutomationPlay = bEnabled;
		return bPrevious;
	}

	virtual bool SetDailyBuildCommandOverride(
		const FFinareDailyBuildCommandOverride& Override) override
	{
		if (!GIsAutomationTesting || IsDailyBuildRunning() ||
			Override.Executable.IsEmpty() || Override.Arguments.IsEmpty() ||
			Override.ReportPath.IsEmpty() || FPaths::IsRelative(Override.ReportPath))
		{
			return false;
		}
		DailyBuildCommandOverride = Override;
		return true;
	}

	virtual bool ClearDailyBuildCommandOverride() override
	{
		if (!GIsAutomationTesting)
		{
			return false;
		}
		if (DailyBuildCommandOverride.IsSet() && IsDailyBuildRunning())
		{
			// A timed-out Automation must stop its owned compiler before restoring
			// fixture files. Never cancel an ordinary project build through this hook.
			ShutdownDailyBuild();
			DailyBuildQueue = FFinareDailyBuildQueue();
		}
		DailyBuildCommandOverride.Reset();
		return true;
	}
#endif

	virtual bool OpenAuthoringMember(const FString& SemanticId) override
	{
		PendingAuthoringSemanticId = SemanticId;
		FGlobalTabmanager::Get()->TryInvokeTab(
			UE::Finare::Editor::Private::AuthoringSurfaceTabName);
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
	TUniquePtr<FFinareSourceWatcher> SourceWatcher;
	FTSTicker::FDelegateHandle FirstPlayStartupHandle;
	FTSTicker::FDelegateHandle CreatedClassSetupHandle;
	TWeakPtr<SFinareAuthoringSurface> AuthoringSurfaceWidget;
	TWeakObjectPtr<UFinareAsset> PendingAuthoringScriptAsset;
	FString PendingAuthoringSemanticId;
	FFinareDailyBuildQueue DailyBuildQueue;
	FFinareDailyBuildResult LastDailyBuildResult;
	FOnFinareDailyBuildCompleted DailyBuildCompleted;
	FProcHandle DailyBuildProcess;
	void* DailyBuildReadPipe = nullptr;
	void* DailyBuildWritePipe = nullptr;
	FTSTicker::FDelegateHandle DailyBuildTickerHandle;
	FTSTicker::FDelegateHandle ResumePlayTickerHandle;
	FString DailyBuildReportPath;
	FString DailyBuildOutput;
	bool bActiveDailyBuildReload = false;
	mutable bool bAllowNextPlay = false;
#if WITH_DEV_AUTOMATION_TESTS
	bool bPrecompiledAutomationPlay = false;
	TOptional<FFinareDailyBuildCommandOverride> DailyBuildCommandOverride;
#endif
	mutable TOptional<FRequestPlaySessionParams> DeferredPlayRequest;

	virtual TValueOrError<bool, FText> IsPIEAuthorizedInternal(
		bool bIsSimulateInEditor) const override
	{
		if (!bAllowNextPlay && LastDailyBuildResult.RequiresRestart())
		{
			return MakeError(FText::FromString(FString::Printf(
				TEXT("Finare requires an Editor restart at %s before Play: %s"),
				*LastDailyBuildResult.RestartRequiredStage,
				*LastDailyBuildResult.RestartReason)));
		}
		return MakeValue(true);
	}

	virtual TValueOrError<bool, FText> RequestPIEPermissionInternal(
		bool bIsSimulateInEditor) const override
	{
#if WITH_DEV_AUTOMATION_TESTS
		if (GIsAutomationTesting && bPrecompiledAutomationPlay)
		{
			return MakeValue(true);
		}
#endif
		FFinareEditorModule* MutableThis =
			const_cast<FFinareEditorModule*>(this);
		if (bAllowNextPlay)
		{
			bAllowNextPlay = false;
			return MakeValue(true);
		}
		if (GEditor == nullptr || !GEditor->GetPlaySessionRequest().IsSet())
		{
			return MakeError(FText::FromString(
				TEXT("Finare could not retain the pending Play request.")));
		}

		DeferredPlayRequest = GEditor->GetPlaySessionRequest().GetValue();
		if (!MutableThis->RequestDailyBuild(
			EFinareDailyBuildReason::Play,
			true))
		{
			DeferredPlayRequest.Reset();
			return MakeError(FText::FromString(
				TEXT("Finare could not start the daily Build & Reload gate.")));
		}
		return MakeError(FText::FromString(
			TEXT("Finare is running Build & Reload; Play will resume automatically after a compatible receipt.")));
	}

	bool StartNextDailyBuild()
	{
		if (DailyBuildQueue.IsActive() || !DailyBuildQueue.HasPending())
		{
			return false;
		}

		const uint8 Reasons = DailyBuildQueue.Begin(bActiveDailyBuildReload);
		DailyBuildOutput.Reset();
		LastDailyBuildResult = FFinareDailyBuildResult{};
		LastDailyBuildResult.Status = TEXT("running");
		LastDailyBuildResult.Reasons = Reasons;
		LastDailyBuildResult.CoalescedRequestCount =
			DailyBuildQueue.CoalescedRequestCount;

		const TSharedPtr<IPlugin> Plugin =
			IPluginManager::Get().FindPlugin(TEXT("Finare"));
		if (!Plugin.IsValid())
		{
			FinishDailyBuild(
				-1,
				TEXT("The Finare plugin descriptor could not be resolved."));
			return false;
		}

		FFinareProjectSetupPaths Paths;
		FString Error;
		const FString EngineRoot = FPaths::ConvertRelativePathToFull(
			FPaths::Combine(FPaths::EngineDir(), TEXT("..")));
		if (!FFinareProjectSetup::ResolveDailyBuildPaths(
			FPaths::GetProjectFilePath(),
			Plugin->GetBaseDir(),
			EngineRoot,
			Paths,
			Error))
		{
			FinishDailyBuild(-1, Error);
			return false;
		}

		FFinareProjectSetupCommand Command;
		if (!FFinareProjectSetup::BuildCommand(
			EFinareProjectSetupOperation::DailyBuildAttached,
			Paths,
			0,
			Command,
			Error))
		{
			FinishDailyBuild(-1, Error);
			return false;
		}
		#if WITH_DEV_AUTOMATION_TESTS
		if (DailyBuildCommandOverride.IsSet())
		{
			Command.Executable = DailyBuildCommandOverride->Executable;
			Command.Arguments = DailyBuildCommandOverride->Arguments;
			Command.ReportPath = DailyBuildCommandOverride->ReportPath;
		}
		#endif
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
			LogFinareEditor,
			Display,
			TEXT("Finare daily Build & Reload started for %s (process %u)."),
			*UE::Finare::Editor::Private::DescribeDailyBuildReasons(Reasons),
			ProcessId);
		DailyBuildTickerHandle = FTSTicker::GetCoreTicker().AddTicker(
			FTickerDelegate::CreateRaw(
				this,
				&FFinareEditorModule::PollDailyBuild),
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

		FFinareDailyBuildResult Result;
		Result.ReturnCode = ReturnCode;
		Result.ReportPath = DailyBuildReportPath;
		Result.Reasons = DailyBuildQueue.ActiveReasons;
		Result.CoalescedRequestCount = DailyBuildQueue.CoalescedRequestCount;

		FFinareDailyBuildReceipt Receipt;
		FString ReceiptError;
		if (!LaunchError.IsEmpty())
		{
			Result.Status = TEXT("failed");
			Result.Summary = LaunchError;
		}
		else if (!FFinareProjectSetup::ReadDailyBuildReceipt(
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
				FFinareReloadBatchResult Reload;
				if (bActiveDailyBuildReload)
				{
					const FString ReloadTransaction =
						UE::Finare::Editor::Private::BuildDailyReloadTransaction(
							Receipt,
							DailyBuildReportPath);
					Reload = SFinareAuthoringSurface::ReloadAllScriptClassComponents(
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
					TEXT(
						"Daily Build completed: %d stages ran, %d skipped, %d deferred; %d reload targets matched, %d reloaded, %d failed."),
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
			TEXT("FinareExitAfterDailyBuild")))
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

	void PublishDailyBuildResult(const FFinareDailyBuildResult& Result)
	{
		FFinareEditorDiagnostic Diagnostic;
		Diagnostic.Domain = TEXT("dailyBuild");
		Diagnostic.Stage = Result.RestartRequiredStage.IsEmpty()
			                   ? TEXT("editorAttached")
			                   : Result.RestartRequiredStage;
		Diagnostic.Code = Result.RequiresRestart()
			                  ? TEXT("USONB3001")
			                  : Result.IsSuccess()
			                  ? TEXT("USDAILY0000")
			                  : TEXT("USDAILY0001");
		Diagnostic.Severity = Result.RequiresRestart()
			                      ? TEXT("warning")
			                      : Result.IsSuccess()
			                      ? TEXT("info")
			                      : TEXT("error");
		Diagnostic.Summary = Result.Summary;
		Diagnostic.Detail = FString::Printf(
			TEXT("Reasons: %s; coalesced requests: %d"),
			*UE::Finare::Editor::Private::DescribeDailyBuildReasons(
				Result.Reasons),
			Result.CoalescedRequestCount);
		if (!DailyBuildOutput.IsEmpty())
		{
			Diagnostic.Detail += TEXT("\n");
			Diagnostic.Detail += DailyBuildOutput.Right(4096);
		}
		Diagnostic.RecoveryAction = Result.RequiresRestart()
			                            ? TEXT("restartEditor")
			                            : Result.IsSuccess()
			                            ? FString()
			                            : TEXT("retry");
		Diagnostic.EvidencePath = Result.ReportPath;
		FFinareEditorDiagnostics::Publish(
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
				&FFinareEditorModule::ResumeDeferredPlay),
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
		TSharedRef<SFinareAuthoringSurface> Surface =
			SNew(SFinareAuthoringSurface);
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
				SNew(SFinareProjectSetup)
			];
	}

	void OpenAuthoringSurfaceCommand()
	{
		OpenAuthoringSurface(nullptr);
	}

	void OpenAuthoringAssetCommand(const TArray<FString>& Args)
	{
		if (Args.IsEmpty())
		{
			OpenAuthoringSurfaceCommand();
			return;
		}
		UFinareAsset* Asset = Args.Num() == 1
			                      ? LoadObject<UFinareAsset>(nullptr, *Args[0])
			                      : nullptr;
		if (!Asset)
		{
			UE_LOG(LogTemp, Warning, TEXT("Finare.OpenAuthoring expects one valid Script Asset object path."));
			return;
		}
		OpenAuthoringSurface(Asset);
	}

	void RequestDailyBuildCommand()
	{
		RequestDailyBuild(EFinareDailyBuildReason::Manual, true);
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
				&FFinareEditorModule::OpenCreatedClassSetup),
			0.1f);
	}

	template <typename TFactory>
	void CreateScriptClass()
	{
		TFactory* Factory = NewObject<TFactory>(GetTransientPackage());
		UObject* CreatedAsset = FAssetToolsModule::GetModule().Get().CreateAssetWithDialog(
			UFinareBlueprint::StaticClass(),
			Factory,
			TEXT("CreateFinareClass"));
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
			TEXT("OpenFinareAuthoring"),
			LOCTEXT("OpenAuthoring", "Finare Authoring"),
			LOCTEXT("OpenAuthoringTooltip", "Open the Slate Authoring Surface for C# state and Visual behaviors."),
			FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Code")),
			FUIAction(FExecuteAction::CreateRaw(
				this,
				&FFinareEditorModule::OpenAuthoringSurfaceCommand)));
		Section.AddMenuEntry(
			TEXT("OpenFinareProjectSetup"),
			LOCTEXT("OpenProjectSetup", "Finare Project Setup"),
			LOCTEXT("OpenProjectSetupTooltip", "Preview, repair, diagnose, or build this Finare project."),
			FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Settings")),
			FUIAction(FExecuteAction::CreateRaw(
				this,
				&FFinareEditorModule::OpenProjectSetup)));
		Section.AddMenuEntry(
			TEXT("CreateFinareObjectClass"),
			LOCTEXT("CreateObjectClass", "Create Finare Class (Object)"),
			LOCTEXT("CreateObjectClassTooltip",
			        "Create an instantiable managed UObject class, C# source, Script Asset, and Blueprint proxy."),
			FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("ClassIcon.Object")),
			FUIAction(FExecuteAction::CreateRaw(
				this,
				&FFinareEditorModule::CreateScriptClass<UFinareObjectClassFactory>)));
		Section.AddMenuEntry(
			TEXT("CreateFinareActorClass"),
			LOCTEXT("CreateActorClass", "Create Finare Class (Actor)"),
			LOCTEXT("CreateActorClassTooltip",
			        "Create a placeable managed Actor class, C# source, Script Asset, and Blueprint proxy."),
			FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("ClassIcon.Actor")),
			FUIAction(FExecuteAction::CreateRaw(
				this,
				&FFinareEditorModule::CreateScriptClass<UFinareActorClassFactory>)));
		Section.AddMenuEntry(
			TEXT("CreateFinareActorComponentClass"),
			LOCTEXT("CreateActorComponentClass", "Create Finare Class (Actor Component)"),
			LOCTEXT("CreateActorComponentClassTooltip",
			        "Create an attachable managed ActorComponent class, C# source, Script Asset, and Blueprint proxy."),
			FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("ClassIcon.ActorComponent")),
			FUIAction(FExecuteAction::CreateRaw(
				this,
				&FFinareEditorModule::CreateScriptClass<UFinareActorComponentClassFactory>)));
		Section.AddMenuEntry(
			TEXT("CreateFinareBlueprintFunctionLibraryClass"),
			LOCTEXT("CreateBlueprintFunctionLibraryClass", "Create Finare Class (Blueprint Function Library)"),
			LOCTEXT("CreateBlueprintFunctionLibraryClassTooltip",
			        "Create a static managed Blueprint Function Library, C# source, Script Asset, and Blueprint proxy."),
			FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("ClassIcon.BlueprintFunctionLibrary")),
			FUIAction(FExecuteAction::CreateRaw(
				this,
				&FFinareEditorModule::CreateScriptClass<UFinareBlueprintFunctionLibraryClassFactory>)));
		UE::Finare::Editor::RegisterFinareAssetContextMenu();
	}
};

IMPLEMENT_MODULE(FFinareEditorModule, FinareEditor)

#undef LOCTEXT_NAMESPACE
