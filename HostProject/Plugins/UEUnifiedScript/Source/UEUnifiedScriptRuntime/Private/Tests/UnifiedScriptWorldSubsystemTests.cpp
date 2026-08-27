#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptWorldSubsystem.h"

#include "Containers/StringConv.h"
#include "Dom/JsonObject.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformProperties.h"
#include "HAL/PlatformTime.h"
#include "Misc/AutomationTest.h"
#include "Misc/EngineVersion.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptActor.h"
#include "UObject/UObjectGlobals.h"

namespace UE::UnifiedScript::WorldHost::Tests
{
struct FCapturedResult
{
    bool bCalled = false;
    bool bSuccess = false;
    EUnifiedScriptInvocationError Error = EUnifiedScriptInvocationError::None;
    EUnifiedScriptObjectHandleError HandleError = EUnifiedScriptObjectHandleError::None;
};

void Capture(
    void* UserData,
    uint8 bSuccess,
    uint8 ValueKind,
    uint8 BooleanValue,
    int32 Int32Value,
    const uint8* StringData,
    int32 StringLength,
    uint8 Error,
    uint8 HandleError,
    const uint8* MessageData,
    int32 MessageLength)
{
    FCapturedResult& Result = *static_cast<FCapturedResult*>(UserData);
    Result.bCalled = true;
    Result.bSuccess = bSuccess != 0;
    Result.Error = static_cast<EUnifiedScriptInvocationError>(Error);
    Result.HandleError = static_cast<EUnifiedScriptObjectHandleError>(HandleError);
}

struct FSemanticDebugSnapshot
{
    FString State;
    FString PauseId;
    FString NodeId;
    FString StepKind;
    TArray<int64> FrameIds;
    TMap<FString, FString> Locals;
};

bool ParseSemanticDebugSnapshot(
    const FString& Json,
    FSemanticDebugSnapshot& OutSnapshot)
{
    OutSnapshot = {};
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid() ||
        !Root->TryGetStringField(TEXT("state"), OutSnapshot.State))
    {
        return false;
    }

    const TSharedPtr<FJsonObject>* Pause = nullptr;
    if (Root->TryGetObjectField(TEXT("pause"), Pause) && Pause != nullptr)
    {
        (*Pause)->TryGetStringField(TEXT("pauseId"), OutSnapshot.PauseId);
        (*Pause)->TryGetStringField(TEXT("nodeId"), OutSnapshot.NodeId);
        (*Pause)->TryGetStringField(TEXT("stepKind"), OutSnapshot.StepKind);
    }

    const TArray<TSharedPtr<FJsonValue>>* Frames = nullptr;
    if (Root->TryGetArrayField(TEXT("frames"), Frames) && Frames != nullptr)
    {
        for (const TSharedPtr<FJsonValue>& FrameValue : *Frames)
        {
            const TSharedPtr<FJsonObject> Frame = FrameValue->AsObject();
            double FrameId = 0.0;
            if (Frame.IsValid() && Frame->TryGetNumberField(TEXT("frameId"), FrameId))
            {
                OutSnapshot.FrameIds.Add(static_cast<int64>(FrameId));
            }
        }
    }

    const TArray<TSharedPtr<FJsonValue>>* FrameLocals = nullptr;
    if (Root->TryGetArrayField(TEXT("frameLocals"), FrameLocals) &&
        FrameLocals != nullptr)
    {
        for (const TSharedPtr<FJsonValue>& FrameValue : *FrameLocals)
        {
            const TSharedPtr<FJsonObject> Frame = FrameValue->AsObject();
            const TArray<TSharedPtr<FJsonValue>>* Locals = nullptr;
            if (!Frame.IsValid() || !Frame->TryGetArrayField(TEXT("locals"), Locals) ||
                Locals == nullptr)
            {
                continue;
            }
            for (const TSharedPtr<FJsonValue>& LocalValue : *Locals)
            {
                const TSharedPtr<FJsonObject> Local = LocalValue->AsObject();
                FString Name;
                FString Display;
                if (Local.IsValid() && Local->TryGetStringField(TEXT("name"), Name) &&
                    Local->TryGetStringField(TEXT("display"), Display))
                {
                    OutSnapshot.Locals.Add(MoveTemp(Name), MoveTemp(Display));
                }
            }
        }
    }
    return true;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptWorldSubsystemLifecycleTest,
    "UnifiedScript.Unreal.WorldHost.Lifecycle",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptWorldSubsystemLifecycleTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::WorldHost::Tests;

    const int32 InitialManagedWorldCount =
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();

    UWorld* FirstWorld = UWorld::CreateWorld(EWorldType::Game, false);
    UWorld* SecondWorld = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("First world exists"), FirstWorld);
    TestNotNull(TEXT("Second world exists"), SecondWorld);
    if (FirstWorld == nullptr || SecondWorld == nullptr)
    {
        return false;
    }

    UUnifiedScriptWorldSubsystem* FirstHost =
        FirstWorld->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    UUnifiedScriptWorldSubsystem* SecondHost =
        SecondWorld->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    TestNotNull(TEXT("First world owns a UnifiedScript host"), FirstHost);
    TestNotNull(TEXT("Second world owns a UnifiedScript host"), SecondHost);
    if (FirstHost == nullptr || SecondHost == nullptr)
    {
        FirstWorld->RemoveFromRoot();
        SecondWorld->RemoveFromRoot();
        FirstWorld->DestroyWorld(false);
        SecondWorld->DestroyWorld(false);
        return false;
    }

    TestTrue(TEXT("First host is native-ready"), FirstHost->IsNativeRuntimeReady());
    TestTrue(TEXT("Second host is native-ready"), SecondHost->IsNativeRuntimeReady());
    TestTrue(TEXT("First managed World entry is ready"), FirstHost->IsManagedWorldReady());
    TestTrue(TEXT("Second managed World entry is ready"), SecondHost->IsManagedWorldReady());
    TestTrue(
        TEXT("World hosts have distinct managed identities"),
        FirstHost->GetWorldHostId() != SecondHost->GetWorldHostId());
    TestEqual(
        TEXT("Two collectible managed World entries were created"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
        InitialManagedWorldCount + 2);
    TestTrue(TEXT("Process CoreCLR host is ready"), FirstHost->IsCoreClrRuntimeReady());
    TestTrue(
        TEXT("Worlds share one process CoreCLR bootstrap"),
        SecondHost->IsCoreClrRuntimeReady());
    TestNotNull(
        TEXT("CoreCLR load-assembly delegate exists"),
        FUnifiedScriptCoreClrHost::Get().GetLoadAssemblyAndGetFunctionPointer());
    TestTrue(
        TEXT("CoreCLR was loaded through hostfxr"),
        FUnifiedScriptCoreClrHost::Get().GetHostFxrPath().EndsWith(TEXT("hostfxr.dll")));

    FUnifiedScriptObjectHandleResult HandleResult;
    UUnifiedScriptComponent* FirstComponent =
        NewObject<UUnifiedScriptComponent>(FirstWorld);
    UUnifiedScriptComponent* SecondComponent =
        NewObject<UUnifiedScriptComponent>(SecondWorld);
    UUnifiedScriptAsset* InitialScriptAsset =
        NewObject<UUnifiedScriptAsset>(FirstWorld);
    InitialScriptAsset->ScriptAssemblyPath =
        TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll");
    InitialScriptAsset->ScriptTypeName = TEXT("UnifiedScript.Unreal.Demo.DemoScript");
    InitialScriptAsset->DeclarationPath =
        TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.asset.json");
    InitialScriptAsset->DeclarationAssetPath = TEXT("Unreal/Demo");
    InitialScriptAsset->AuthoringSurfacePath =
        TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.surface.json");
    FirstComponent->ScriptAssetPath = InitialScriptAsset->GetPathName();
    TestNull(
        TEXT("Initial demo resolves only through ScriptAssetPath"),
        FirstComponent->ScriptAsset.Get());
    TestTrue(
        TEXT("Demo does not depend on legacy direct assembly configuration"),
        FirstComponent->ScriptAssemblyPath.IsEmpty() && FirstComponent->ScriptTypeName.IsEmpty());
    TestTrue(
        *FString::Printf(
            TEXT("Declaration IR materializes before play: %s"),
            *FirstComponent->LastScriptError),
        FirstComponent->RefreshScriptState());
    TestEqual(
        TEXT("C# [Expose] creates one editable Unreal state"),
        FirstComponent->MaterializedStateCount,
        1);
    TestEqual(
        TEXT("Declaration default reaches the component"),
        FirstComponent->GetScriptStateInt32(TEXT("InitialValue"), -1),
        10);
    TestEqual(
        TEXT("Fresh declaration values are inherited rather than overridden"),
        FirstComponent->MaterializedStateOverrideCount,
        0);
    TestTrue(
        TEXT("Default materialization is observable"),
        FirstComponent->LastStateMigration.Contains(TEXT("Defaults=1")));
    TestTrue(
        TEXT("Instance state override is accepted"),
        FirstComponent->SetScriptStateInt32(TEXT("InitialValue"), 37));
    TestTrue(
        TEXT("Instance state override is tracked by stable declaration ID"),
        FirstComponent->IsScriptStateOverridden(TEXT("InitialValue")));
    TestEqual(
        TEXT("One instance override is observable"),
        FirstComponent->MaterializedStateOverrideCount,
        1);
    TestTrue(
        TEXT("Reset restores the current declaration default"),
        FirstComponent->ResetScriptStateOverride(TEXT("InitialValue")));
    TestEqual(
        TEXT("Reset value inherits the C# default again"),
        FirstComponent->GetScriptStateInt32(TEXT("InitialValue"), -1),
        10);
    TestFalse(
        TEXT("Reset clears the stable override identity"),
        FirstComponent->IsScriptStateOverridden(TEXT("InitialValue")));
    TestTrue(
        TEXT("Demo override can be re-applied after reset"),
        FirstComponent->SetScriptStateInt32(TEXT("InitialValue"), 37));
    FirstComponent->RegisterComponentWithWorld(FirstWorld);
    TestTrue(TEXT("Lifecycle demo component is registered"), FirstComponent->IsRegistered());
    TestEqual(
        TEXT("Managed World initially has no script instances"),
        FirstHost->GetManagedScriptCount(),
        0);
    const uint64 FastCallsBeforeDemo = FirstHost->GetFastInvocationCount();
    FirstComponent->BeginPlay();
    const bool bScriptStarted = FirstComponent->bScriptRunning;
    TestTrue(
        *FString::Printf(
            TEXT("BeginPlay starts the configured managed script: %s"),
            *FirstComponent->LastScriptError),
        bScriptStarted);
    TestTrue(TEXT("Component reports a running script"), FirstComponent->bScriptRunning);
    TestEqual(
        TEXT("Managed World owns the component script instance"),
        FirstHost->GetManagedScriptCount(),
        1);
    FirstComponent->BroadcastManagedSignal(73, FirstComponent);
    TestEqual(
        TEXT("Attributed Event Entry receives a reflected UE multicast delegate"),
        FirstComponent->ManagedState,
        FString(TEXT("Started:37|Event:73")));
    TestEqual(
        TEXT("Component exposes the resolved Script Asset"),
        FirstComponent->ActiveScriptAssetPath,
        InitialScriptAsset->GetPathName());
    TestEqual(
        TEXT("Component exposes the selected declaration asset"),
        FirstComponent->ActiveDeclarationAssetPath,
        FString(TEXT("Unreal/Demo")));
    TestFalse(
        TEXT("Managed OnStart writes bStartAutomatically"),
        FirstComponent->bStartAutomatically);
    TestEqual(
        TEXT("Initial collectible script generation is visible"),
        FirstComponent->ActiveGeneration,
        FString(TEXT("1")));
    FUnifiedScriptRuntimeDebugContext LiveDebugContext;
    TestTrue(
        TEXT("Running component exposes a live UE World debug context"),
        FirstComponent->GetRuntimeDebugContext(LiveDebugContext));
    TestEqual(
        TEXT("Live debug context owns the current World"),
        LiveDebugContext.WorldHostId,
        FirstHost->GetWorldHostId());
    TestTrue(
        TEXT("Live debug context retains exact native handles"),
        LiveDebugContext.HostHandle != 0 && LiveDebugContext.TargetHandle != 0);
    TestEqual(TEXT("Live debug context starts at generation one"), LiveDebugContext.Generation, 1);
    TestTrue(
        TEXT("Generated managed wrapper selects native member-ID thunks"),
        FirstHost->GetFastInvocationCount() >= FastCallsBeforeDemo + 3);
    uint64 ManagedCallFrameCalls = 0;
    uint64 ManagedLegacyCalls = 0;
    TestTrue(
        TEXT("Managed World exposes invocation-path metrics"),
        FirstHost->GetManagedInvocationMetrics(
            ManagedCallFrameCalls,
            ManagedLegacyCalls));
    TestTrue(
        TEXT("Fixed generated calls cross the live ABI v3 call frame"),
        ManagedCallFrameCalls >= 2);
    TestTrue(
        TEXT("Generated UTF-8 inputs stay on the live ABI v3 call frame"),
        ManagedCallFrameCalls >= 4);
    TestEqual(
        TEXT("The current World lifecycle demo no longer needs ABI v2 fallback"),
        ManagedLegacyCalls,
        uint64{0});

    FString ProfilerError;
    TestTrue(
        *FString::Printf(
            TEXT("Live gameplay profiler enables without changing the default-off runtime: %s"),
            *ProfilerError),
        FirstComponent->SetGameplayProfilerEnabled(true, ProfilerError));
    FirstHost->EndManagedFrame();
    FirstHost->BeginManagedFrame();
    FirstComponent->TickComponent(0.016f, LEVELTICK_All, nullptr);
    TestEqual(
        TEXT("Managed OnTick executes through the component host"),
        FirstComponent->ManagedState,
        FString(TEXT("Ticked")));
    FString GameplayProfileJson;
    TestTrue(
        *FString::Printf(TEXT("Live component exports a portable gameplay trace: %s"), *ProfilerError),
        FirstComponent->CaptureGameplayProfileTrace(GameplayProfileJson, ProfilerError));
    TSharedPtr<FJsonObject> GameplayProfileRoot;
    const TSharedRef<TJsonReader<>> GameplayProfileReader =
        TJsonReaderFactory<>::Create(GameplayProfileJson);
    TestTrue(
        TEXT("Gameplay profile JSON is structurally readable"),
        FJsonSerializer::Deserialize(GameplayProfileReader, GameplayProfileRoot) &&
            GameplayProfileRoot.IsValid());
    FString GameplayProfileSchema;
    const TArray<TSharedPtr<FJsonValue>>* GameplayFrames = nullptr;
    const TSharedPtr<FJsonObject>* GameplayAggregate = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* GameplayBoundaries = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* GameplayWorldPhases = nullptr;
    TestTrue(
        TEXT("Gameplay profile binds schema, bounded frame timeline, and aggregate view"),
        GameplayProfileRoot.IsValid() &&
            GameplayProfileRoot->TryGetStringField(
                TEXT("schemaVersion"), GameplayProfileSchema) &&
            GameplayProfileSchema == TEXT("unifiedscript.gameplay-profile/v0.2") &&
            GameplayProfileRoot->TryGetArrayField(TEXT("frames"), GameplayFrames) &&
            GameplayFrames != nullptr && GameplayFrames->Num() == 1 &&
            GameplayProfileRoot->TryGetObjectField(TEXT("aggregate"), GameplayAggregate) &&
            GameplayAggregate != nullptr &&
            (*GameplayAggregate)->TryGetArrayField(
                TEXT("boundaries"), GameplayBoundaries) &&
            GameplayBoundaries != nullptr &&
            (*GameplayAggregate)->TryGetArrayField(
                TEXT("worldPhases"), GameplayWorldPhases) &&
            GameplayWorldPhases != nullptr && GameplayWorldPhases->Num() == 3);
    bool bCallFrameAttributed = false;
    if (GameplayBoundaries != nullptr)
    {
        for (const TSharedPtr<FJsonValue>& BoundaryValue : *GameplayBoundaries)
        {
            const TSharedPtr<FJsonObject> Boundary = BoundaryValue->AsObject();
            FString Name;
            double Count = 0.0;
            if (Boundary.IsValid() &&
                Boundary->TryGetStringField(TEXT("name"), Name) &&
                Name == TEXT("callFrame") &&
                Boundary->TryGetNumberField(TEXT("count"), Count) && Count >= 1.0)
            {
                bCallFrameAttributed = true;
                break;
            }
        }
    }
    TestTrue(
        TEXT("Live profiler attributes the generated FString write to CallFrame"),
        bCallFrameAttributed);
    bool bWorldPhasesAttributed =
        GameplayWorldPhases != nullptr && GameplayWorldPhases->Num() == 3;
    if (GameplayWorldPhases != nullptr)
    {
        for (const TSharedPtr<FJsonValue>& PhaseValue : *GameplayWorldPhases)
        {
            const TSharedPtr<FJsonObject> Phase = PhaseValue->AsObject();
            double Count = 0.0;
            double Nanoseconds = 0.0;
            bWorldPhasesAttributed = bWorldPhasesAttributed && Phase.IsValid() &&
                Phase->TryGetNumberField(TEXT("count"), Count) && Count >= 1.0 &&
                Phase->TryGetNumberField(TEXT("totalNanoseconds"), Nanoseconds) &&
                Nanoseconds > 0.0;
        }
    }
    TestTrue(
        TEXT("Live profiler attributes all three World scheduler phases"),
        bWorldPhasesAttributed);
    FirstComponent->ManagedState = TEXT("PausedAtWorldBoundary");
    TestTrue(
        TEXT("Live debugger pauses the managed component at a frame boundary"),
        FirstComponent->PauseAtDebugFrameBoundary());
    FirstComponent->TickComponent(0.016f, LEVELTICK_All, nullptr);
    TestEqual(
        TEXT("Paused frame boundary does not enter managed OnTick"),
        FirstComponent->ManagedState,
        FString(TEXT("PausedAtWorldBoundary")));
    TestTrue(
        TEXT("Live debugger schedules exactly one managed frame"),
        FirstComponent->StepOneDebugFrame());
    FirstComponent->TickComponent(0.016f, LEVELTICK_All, nullptr);
    TestEqual(
        TEXT("World frame step enters managed OnTick once"),
        FirstComponent->ManagedState,
        FString(TEXT("Ticked")));
    TestTrue(
        TEXT("World frame step remains paused after the managed tick"),
        FirstComponent->IsPausedAtDebugFrameBoundary());
    FirstComponent->bTickAutomatically = false;
    FirstComponent->ManagedState = TEXT("ManualFramePaused");
    TestTrue(
        TEXT("A manually ticked script can schedule one debugger frame"),
        FirstComponent->StepOneDebugFrame());
    FirstComponent->TickComponent(0.016f, LEVELTICK_All, nullptr);
    TestEqual(
        TEXT("World frame step enters managed OnTick when automatic tick is disabled"),
        FirstComponent->ManagedState,
        FString(TEXT("Ticked")));
    FirstComponent->bTickAutomatically = true;
    TestTrue(
        TEXT("Live debugger resumes automatic managed ticks"),
        FirstComponent->ContinueFromDebugFrameBoundary());

    TestTrue(TEXT("Component reload creates a new script generation"), FirstComponent->ReloadScript());
    TestEqual(
        TEXT("Reloaded generation is visible"),
        FirstComponent->ActiveGeneration,
        FString(TEXT("2")));
    TestEqual(
        TEXT("Reload retains exactly one compatible instance override"),
        FirstComponent->MaterializedStateOverrideCount,
        1);
    FirstComponent->BroadcastManagedSignal(73, FirstComponent);
    TestEqual(
        TEXT("Reload rebinds one Event Entry and preserves compatible state"),
        FirstComponent->ManagedState,
        FString(TEXT("Started:37|Event:73")));

    UUnifiedScriptAsset* InvalidScriptAsset =
        NewObject<UUnifiedScriptAsset>(FirstWorld);
    InvalidScriptAsset->ScriptAssemblyPath = InitialScriptAsset->ScriptAssemblyPath;
    InvalidScriptAsset->ScriptTypeName = TEXT("UnifiedScript.Unreal.Demo.MissingScript");
    FirstComponent->ScriptAsset = InvalidScriptAsset;
    AddExpectedError(
        TEXT("UnifiedScript component: managedException during ReloadScript:"),
        EAutomationExpectedErrorFlags::Contains,
        1);
    TestFalse(
        TEXT("Invalid Script Asset reload is rejected transactionally"),
        FirstComponent->ReloadScript());
    TestTrue(
        TEXT("Failed Script Asset reload keeps the previous instance running"),
        FirstComponent->bScriptRunning);
    TestEqual(
        TEXT("Failed reload does not advance the active generation"),
        FirstComponent->ActiveGeneration,
        FString(TEXT("2")));
    TestEqual(
        TEXT("Failed reload preserves the previous managed instance"),
        FirstHost->GetManagedScriptCount(),
        1);
    TestEqual(
        TEXT("Failed reload also preserves materialized state"),
        FirstComponent->GetScriptStateInt32(TEXT("InitialValue"), -1),
        37);
    TestEqual(
        TEXT("Failed reload preserves the previous override identity"),
        FirstComponent->MaterializedStateOverrideCount,
        1);
    TestTrue(
        TEXT("Failed Script Asset reload exposes the managed boundary and type"),
        FirstComponent->LastScriptError.Contains(TEXT("ReloadScript:")) &&
            FirstComponent->LastScriptError.Contains(TEXT("MissingScript")));
    FUnifiedScriptRuntimeDebugContext FailedDebugContext;
    TestTrue(
        TEXT("Failed live generation remains inspectable through its World context"),
        FirstComponent->GetRuntimeDebugContext(FailedDebugContext));
    TestTrue(
        TEXT("Managed boundary failure is parsed as runtime-diagnostic/v0.1"),
        FailedDebugContext.LastDiagnostic.IsValid());
    TestEqual(
        TEXT("Managed failure retains its operation"),
        FailedDebugContext.LastDiagnostic.Operation,
        FString(TEXT("ReloadScript")));
    TestEqual(
        TEXT("Managed failure retains the real World identity"),
        FailedDebugContext.LastDiagnostic.WorldHostId,
        FirstHost->GetWorldHostId());
    TestEqual(
        TEXT("Managed failure retains the active generation"),
        FailedDebugContext.LastDiagnostic.Generation,
        2);
    TestTrue(
        TEXT("Managed failure retains the script instance and UObject handles"),
        FailedDebugContext.LastDiagnostic.InstanceId != 0 &&
            FailedDebugContext.LastDiagnostic.HostHandle != 0 &&
            FailedDebugContext.LastDiagnostic.TargetHandle != 0);

    UUnifiedScriptAsset* ReloadScriptAsset =
        NewObject<UUnifiedScriptAsset>(FirstWorld);
    ReloadScriptAsset->ScriptAssemblyPath = InitialScriptAsset->ScriptAssemblyPath;
    ReloadScriptAsset->ScriptTypeName =
        TEXT("UnifiedScript.Unreal.Demo.AlternateDemoScript");
    FirstComponent->ScriptAsset = ReloadScriptAsset;
    FirstComponent->RequestReload();
    FirstComponent->TickComponent(0.016f, LEVELTICK_All, nullptr);
    TestEqual(
        TEXT("Queued reload advances the collectible generation"),
        FirstComponent->ActiveGeneration,
        FString(TEXT("3")));
    TestEqual(
        TEXT("Queued reload transaction changes the managed entry type"),
        FirstComponent->ManagedState,
        FString(TEXT("AlternateTicked")));
    TestEqual(
        TEXT("Reload transaction adopts the newly selected Script Asset"),
        FirstComponent->ActiveScriptAssetPath,
        ReloadScriptAsset->GetPathName());
    TestEqual(
        TEXT("A Script Asset without a declaration clears the old state layout"),
        FirstComponent->MaterializedStateCount,
        0);
    TestEqual(
        TEXT("Removed declaration drops stale instance override identities"),
        FirstComponent->MaterializedStateOverrideCount,
        0);
    TestTrue(
        TEXT("Dropped override is observable after asset replacement"),
        FirstComponent->LastStateMigration.Contains(TEXT("DroppedOverrides=1")));

    UUnifiedScriptAsset* FailureScriptAsset = NewObject<UUnifiedScriptAsset>(FirstWorld);
    FailureScriptAsset->ScriptAssemblyPath = InitialScriptAsset->ScriptAssemblyPath;
    FailureScriptAsset->ScriptTypeName =
        TEXT("UnifiedScript.Unreal.Demo.RuntimeFailureDemoScript");
    UUnifiedScriptComponent* FailureComponent =
        NewObject<UUnifiedScriptComponent>(FirstWorld);
    FailureComponent->ScriptAsset = FailureScriptAsset;
    FailureComponent->RegisterComponentWithWorld(FirstWorld);
    TestTrue(
        *FString::Printf(
            TEXT("Bridge-failure fixture starts in the live World: %s"),
            *FailureComponent->LastScriptError),
        FailureComponent->StartScript());
    AddExpectedError(
        TEXT("UnifiedScript component: nativeBridgeFailure during TickScript:"),
        EAutomationExpectedErrorFlags::Contains,
        1);
    FailureComponent->TickComponent(0.016f, LEVELTICK_All, nullptr);
    TestFalse(
        TEXT("A live native bridge failure stops only the failing component"),
        FailureComponent->bScriptRunning);
    FUnifiedScriptRuntimeDebugContext BridgeFailureContext;
    TestTrue(
        TEXT("Stopped bridge failure retains its live World debug identity"),
        FailureComponent->GetRuntimeDebugContext(BridgeFailureContext));
    TestEqual(
        TEXT("Live failure is classified at the native bridge boundary"),
        BridgeFailureContext.LastDiagnostic.Kind,
        FString(TEXT("nativeBridgeFailure")));
    TestEqual(
        TEXT("Live bridge failure retains the native invocation category"),
        BridgeFailureContext.LastDiagnostic.Bridge.Error,
        FString(TEXT("ObjectHandleFailure")));
    TestEqual(
        TEXT("Live bridge failure retains the stale UObject category"),
        BridgeFailureContext.LastDiagnostic.Bridge.HandleError,
        FString(TEXT("StaleHandle")));
    TestEqual(
        TEXT("Live bridge failure retains the generated member path"),
        BridgeFailureContext.LastDiagnostic.Bridge.MemberPath,
        FString(TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:ManagedState")));
    TestTrue(
        TEXT("Live bridge failure retains portable-PDB source frames"),
        BridgeFailureContext.LastDiagnostic.Source.IsValid() &&
            BridgeFailureContext.LastDiagnostic.Source.FilePath.EndsWith(
                TEXT("RuntimeFailureDemoScript.cs")) &&
            !BridgeFailureContext.LastDiagnostic.SourceFrames.IsEmpty());

    FirstComponent->EndPlay(EEndPlayReason::Destroyed);
    TestFalse(TEXT("Component reports a stopped script"), FirstComponent->bScriptRunning);
    TestEqual(
        TEXT("Managed OnStop executes before handle release"),
        FirstComponent->ManagedState,
        FString(TEXT("AlternateStopped")));
    TestTrue(
        TEXT("Stopped component clears its active Script Asset"),
        FirstComponent->ActiveScriptAssetPath.IsEmpty());
    TestEqual(
        TEXT("Managed script instance is removed on component stop"),
        FirstHost->GetManagedScriptCount(),
        0);
    FirstComponent->UnregisterComponent();
    TestTrue(
        TEXT("Final managed invocation metrics remain available after script stop"),
        FirstHost->GetManagedInvocationMetrics(
            ManagedCallFrameCalls,
            ManagedLegacyCalls));
    UE_LOG(
        LogTemp,
        Display,
        TEXT("UnifiedScript demo completed: DeclarationState=37, ReflectedEvent=73, ScriptAsset Start/Tick/Reload/Stop, Generation=3, Add(20, 22)=42, FastCalls=%llu, CallFrameCalls=%llu, LegacyCalls=%llu"),
        static_cast<unsigned long long>(FirstHost->GetFastInvocationCount()),
        static_cast<unsigned long long>(ManagedCallFrameCalls),
        static_cast<unsigned long long>(ManagedLegacyCalls));

    const int32 DataPlaneBorrowedHandleCount =
        FirstHost->NumRegisteredObjects();
    AUnifiedScriptActor* ScriptActor =
        FirstWorld->SpawnActor<AUnifiedScriptActor>();
    TestNotNull(TEXT("Placeable UnifiedScript Actor can be spawned"), ScriptActor);
    if (ScriptActor != nullptr)
    {
        UUnifiedScriptComponent* ActorComponent = ScriptActor->ScriptComponent;
        TestNotNull(TEXT("UnifiedScript Actor owns its script component"), ActorComponent);
        UUnifiedScriptAsset* ActorAsset = NewObject<UUnifiedScriptAsset>(FirstWorld);
        ActorAsset->ScriptAssemblyPath = InitialScriptAsset->ScriptAssemblyPath;
        ActorAsset->ScriptTypeName =
            TEXT("UnifiedScript.Unreal.Demo.ActorDemoScript");
        ActorComponent->ScriptAsset = ActorAsset;
        TestTrue(
            TEXT("Actor component resolves its owner as the managed target"),
            ActorComponent->GetScriptTarget() == ScriptActor);
        ActorComponent->BeginPlay();
        TestTrue(
            *FString::Printf(
                TEXT("Actor behavior begins: %s"),
                *ActorComponent->LastScriptError),
            ActorComponent->bScriptRunning);
        TestEqual(
            TEXT("Managed Actor BeginPlay reaches a Blueprint-visible property"),
            ScriptActor->ManagedActorState,
            FString(TEXT("ActorBegin:1")));
        TestTrue(
            TEXT("Managed context exposes distinct host and Actor identities"),
            ActorComponent->ManagedState.StartsWith(TEXT("ActorHost:")));

        ActorComponent->TickComponent(0.016f, LEVELTICK_All, nullptr);
        TestEqual(
            TEXT("Managed Actor Tick invokes native Actor behavior"),
            ScriptActor->ManagedTickCount,
            1);
        TestEqual(
            TEXT("Managed Actor Tick updates Blueprint-visible state"),
            ScriptActor->ManagedActorState,
            FString(TEXT("ActorTick")));
        TestEqual(
            TEXT("Managed latent delay resumes on the game-thread Tick"),
            ActorComponent->ManagedState,
            FString(TEXT("ActorLatent")));

        AActor* OtherActor = FirstWorld->SpawnActor<AActor>();
        TestNotNull(TEXT("Overlap event argument Actor can be spawned"), OtherActor);
        if (OtherActor != nullptr)
        {
            ScriptActor->OnActorBeginOverlap.Broadcast(ScriptActor, OtherActor);
            TestEqual(
                TEXT("World-safe BeginOverlap reaches managed Actor behavior"),
                ScriptActor->ManagedActorState,
                FString(TEXT("ActorBeginOverlap")));
            ScriptActor->OnActorEndOverlap.Broadcast(ScriptActor, OtherActor);
            TestEqual(
                TEXT("World-safe EndOverlap reaches managed Actor behavior"),
                ScriptActor->ManagedActorState,
                FString(TEXT("ActorEndOverlap")));
            TestEqual(
                TEXT("Temporary event argument releases back to the data-plane baseline"),
                FirstHost->NumRegisteredObjects(),
                DataPlaneBorrowedHandleCount + 2);
            OtherActor->Destroy();
        }

        TestTrue(
            TEXT("Actor behavior reloads as a new generation"),
            ActorComponent->ReloadScript());
        TestEqual(
            TEXT("Reloaded Actor behavior receives generation two"),
            ScriptActor->ManagedActorState,
            FString(TEXT("ActorBegin:2")));

        ActorComponent->EndPlay(EEndPlayReason::Destroyed);
        TestEqual(
            TEXT("Managed Actor EndPlay runs before target handle release"),
            ScriptActor->ManagedActorState,
            FString(TEXT("ActorEnd")));
        ScriptActor->OnActorBeginOverlap.Broadcast(ScriptActor, ScriptActor);
        TestEqual(
            TEXT("Actor delegates are unbound after managed EndPlay"),
            ScriptActor->ManagedActorState,
            FString(TEXT("ActorEnd")));
        TestEqual(
            TEXT("Actor teardown preserves only the data-plane borrowed handles"),
            FirstHost->NumRegisteredObjects(),
            DataPlaneBorrowedHandleCount);
        ScriptActor->Destroy();
    }

    const FUnifiedScriptObjectHandle FirstHandle =
        FirstHost->RegisterObject(FirstComponent, HandleResult);
    TestTrue(TEXT("First world registration succeeds"), HandleResult.IsSuccess());
    const FUnifiedScriptObjectHandle SecondHandle =
        SecondHost->RegisterObject(SecondComponent, HandleResult);
    TestTrue(TEXT("Second world registration succeeds"), HandleResult.IsSuccess());
    TestTrue(
        TEXT("Active handles are unique across world registries"),
        FirstHandle.Value != SecondHandle.Value);

    FUnifiedScriptNativeBridgeContext* FirstContext =
        FirstHost->GetNativeInvocationContext();
    FUnifiedScriptNativeBridgeContext* SecondContext =
        SecondHost->GetNativeInvocationContext();
    const FUnifiedScriptNativeTransportV2* Transport = FirstHost->GetNativeTransport();
    TestNotNull(TEXT("First world context exists"), FirstContext);
    TestNotNull(TEXT("Second world context exists"), SecondContext);
    TestTrue(TEXT("Worlds do not share bridge contexts"), FirstContext != SecondContext);
    TestNotNull(TEXT("World host exposes the native transport"), Transport);
    if (FirstContext == nullptr || SecondContext == nullptr || Transport == nullptr)
    {
        FirstWorld->RemoveFromRoot();
        SecondWorld->RemoveFromRoot();
        FirstWorld->DestroyWorld(false);
        SecondWorld->DestroyWorld(false);
        return false;
    }

    TestTrue(
        TEXT("Managed-side lifetime can retain the first context"),
        Transport->RetainContext(FirstContext) != 0);

    const FTCHARToUTF8 StartPath(
        TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:bStartAutomatically"));
    FCapturedResult Captured;
    Transport->ReadProperty(
        FirstContext,
        FirstHandle.Value,
        reinterpret_cast<const uint8*>(StartPath.Get()),
        StartPath.Length(),
        &Capture,
        &Captured);
    TestTrue(TEXT("Owning world context resolves its handle"), Captured.bSuccess);

    Captured = {};
    Transport->ReadProperty(
        SecondContext,
        FirstHandle.Value,
        reinterpret_cast<const uint8*>(StartPath.Get()),
        StartPath.Length(),
        &Capture,
        &Captured);
    TestTrue(
        TEXT("A handle cannot alias through another world context"),
        !Captured.bSuccess &&
            Captured.Error == EUnifiedScriptInvocationError::ObjectHandleFailure &&
            Captured.HandleError == EUnifiedScriptObjectHandleError::StaleHandle);

    FirstWorld->RemoveFromRoot();
    FirstWorld->DestroyWorld(false);
    FirstWorld = nullptr;
    TestEqual(
        TEXT("First World teardown removes its managed entry"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
        InitialManagedWorldCount + 1);

    Captured = {};
    Transport->ReadProperty(
        FirstContext,
        FirstHandle.Value,
        reinterpret_cast<const uint8*>(StartPath.Get()),
        StartPath.Length(),
        &Capture,
        &Captured);
    TestTrue(
        TEXT("Retained context rejects calls after world teardown"),
        !Captured.bSuccess &&
            Captured.Error == EUnifiedScriptInvocationError::TransportUnavailable);
    TestFalse(
        TEXT("Detached world context cannot be retained again"),
        Transport->RetainContext(FirstContext) != 0);
    Transport->ReleaseContext(FirstContext);

    SecondWorld->RemoveFromRoot();
    SecondWorld->DestroyWorld(false);
    TestEqual(
        TEXT("Second World teardown restores the managed entry count"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
        InitialManagedWorldCount);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptWorldSemanticDebugTest,
    "UnifiedScript.Unreal.WorldHost.SemanticDebug",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptWorldSemanticDebugTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::WorldHost::Tests;

    constexpr TCHAR OuterNodeId[] = TEXT("us_22222222222222222222222222222222");
    constexpr TCHAR ChildNodeId[] = TEXT("us_44444444444444444444444444444444");
    constexpr TCHAR AfterNodeId[] = TEXT("us_55555555555555555555555555555555");
    const int32 InitialManagedWorldCount =
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();

    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Semantic debugger World exists"), World);
    if (World == nullptr)
    {
        return false;
    }
    World->AddToRoot();
    UUnifiedScriptWorldSubsystem* Host =
        World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    TestNotNull(TEXT("Semantic debugger World host exists"), Host);
    if (Host == nullptr)
    {
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        return false;
    }

    UUnifiedScriptAsset* Asset = NewObject<UUnifiedScriptAsset>(World);
    Asset->ScriptAssemblyPath =
        TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll");
    Asset->ScriptTypeName =
        TEXT("UnifiedScript.Unreal.Demo.RuntimeSemanticDebugDemoScript");
    UUnifiedScriptComponent* Component =
        NewObject<UUnifiedScriptComponent>(World);
    Component->ScriptAsset = Asset;
    Component->RegisterComponentWithWorld(World);
    TestTrue(
        *FString::Printf(
            TEXT("Semantic debugger fixture starts: %s"),
            *Component->LastScriptError),
        Component->StartScript());
    TestEqual(
        TEXT("Semantic debugger fixture enters its ready state"),
        Component->ManagedState,
        FString(TEXT("SemanticDebugReady")));

    const auto BuildConfiguration = [](int32 RuntimeGeneration)
    {
        return FString::Printf(
            TEXT("{\"schemaVersion\":\"unifiedscript.unreal-debug-config/v0.1\","
                 "\"runtimeGeneration\":%d,\"generation\":{"
                 "\"domainId\":\"us_66666666666666666666666666666666\","
                 "\"number\":1,"
                 "\"generationId\":\"us_77777777777777777777777777777777\","
                 "\"imageKey\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\","
                 "\"sourceCompilationId\":\"us_88888888888888888888888888888888\","
                 "\"semanticConfigurationId\":\"us_99999999999999999999999999999999\"},"
                 "\"breakpointNodeIds\":[\"%s\"]}"),
            RuntimeGeneration,
            OuterNodeId);
    };
    const auto PumpUntil = [Component](
                               const FString& ExpectedState,
                               const FString& ExpectedNode,
                               FSemanticDebugSnapshot& OutSnapshot)
    {
        for (int32 Attempt = 0; Attempt < 2000; ++Attempt)
        {
            Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
            FSemanticDebugSnapshot Candidate;
            if (ParseSemanticDebugSnapshot(
                    Component->GetSemanticDebugStateJson(), Candidate) &&
                Candidate.State == ExpectedState &&
                (ExpectedNode.IsEmpty() || Candidate.NodeId == ExpectedNode))
            {
                OutSnapshot = MoveTemp(Candidate);
                return true;
            }
            FPlatformProcess::SleepNoStats(0.001f);
        }
        FSemanticDebugSnapshot LastSnapshot;
        const bool bParsedLastSnapshot = ParseSemanticDebugSnapshot(
            Component->GetSemanticDebugStateJson(), LastSnapshot);
        UE_LOG(
            LogTemp,
            Error,
            TEXT("Semantic debug wait timed out for state='%s' node='%s' "
                 "(parsed=%s actualState='%s' actualNode='%s'): %s"),
            *ExpectedState,
            *ExpectedNode,
            bParsedLastSnapshot ? TEXT("true") : TEXT("false"),
            *LastSnapshot.State,
            *LastSnapshot.NodeId,
            *Component->GetSemanticDebugStateJson());
        return false;
    };

    FString Error;
    TestTrue(
        *FString::Printf(TEXT("Live semantic debugger attaches: %s"), *Error),
        Component->ConfigureSemanticDebug(BuildConfiguration(1), Error));
    const double TickStart = FPlatformTime::Seconds();
    Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
    TestTrue(
        TEXT("Starting semantic execution never blocks the Unreal Game Thread"),
        FPlatformTime::Seconds() - TickStart < 0.25);

    FSemanticDebugSnapshot OuterPause;
    TestTrue(
        TEXT("The live World reaches the exact outer semantic breakpoint"),
        PumpUntil(TEXT("paused"), OuterNodeId, OuterPause));
    TestEqual(TEXT("Outer pause has one Visual frame"), OuterPause.FrameIds.Num(), 1);
    TestEqual(
        TEXT("Outer-frame locals retain the counter"),
        OuterPause.Locals.FindRef(TEXT("counter")),
        FString(TEXT("7")));
    FString StaleControlError;
    TestFalse(
        TEXT("A stale pause cannot control the live World execution"),
        Component->ControlSemanticDebug(
            EUnifiedScriptSemanticDebugControl::StepInto,
            TEXT("us_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            StaleControlError));
    TestTrue(
        TEXT("The stale-pause rejection is actionable"),
        StaleControlError.Contains(TEXT("stale pause")));

    Error.Reset();
    TestTrue(
        *FString::Printf(TEXT("Step Into resumes the exact outer pause: %s"), *Error),
        Component->ControlSemanticDebug(
            EUnifiedScriptSemanticDebugControl::StepInto,
            OuterPause.PauseId,
            Error));
    FSemanticDebugSnapshot ChildPause;
    TestTrue(
        TEXT("Step Into suspends inside the nested Visual frame"),
        PumpUntil(TEXT("paused"), ChildNodeId, ChildPause));
    TestEqual(TEXT("Nested pause exposes two Visual frames"), ChildPause.FrameIds.Num(), 2);
    TestEqual(
        TEXT("Deeper-frame locals retain the nested value"),
        ChildPause.Locals.FindRef(TEXT("nestedValue")),
        FString(TEXT("19")));
    TestTrue(
        TEXT("Frame IDs are unique across live pauses"),
        OuterPause.FrameIds.Num() == 1 && ChildPause.FrameIds.Num() == 2 &&
            !ChildPause.FrameIds.Contains(OuterPause.FrameIds[0]));

    Error.Reset();
    TestTrue(
        *FString::Printf(TEXT("Step Out resumes the nested pause: %s"), *Error),
        Component->ControlSemanticDebug(
            EUnifiedScriptSemanticDebugControl::StepOut,
            ChildPause.PauseId,
            Error));
    FSemanticDebugSnapshot AfterPause;
    TestTrue(
        TEXT("Step Out suspends at the caller statement"),
        PumpUntil(TEXT("paused"), AfterNodeId, AfterPause));
    TestEqual(
        TEXT("Step Out reports its completed control kind"),
        AfterPause.StepKind,
        FString(TEXT("out")));

    Component->bTickAutomatically = false;
    Error.Reset();
    TestTrue(
        *FString::Printf(TEXT("Continue resumes the caller pause: %s"), *Error),
        Component->ControlSemanticDebug(
            EUnifiedScriptSemanticDebugControl::Continue,
            AfterPause.PauseId,
            Error));
    FSemanticDebugSnapshot Completed;
    TestTrue(
        TEXT("Continued live World execution completes"),
        PumpUntil(TEXT("completed"), FString(), Completed));
    TestEqual(
        TEXT("Worker-originated generated bridge calls return through the Game Thread"),
        Component->ManagedState,
        FString(TEXT("SemanticDebugCompleted")));

    TestTrue(
        TEXT("Reload drains the completed semantic session and advances generation"),
        Component->ReloadScript());
    TestEqual(TEXT("Reloaded semantic fixture is generation two"), Component->ActiveGeneration, FString(TEXT("2")));
    FUnifiedScriptRuntimeDebugContext ReloadContext;
    TestTrue(
        TEXT("Reloaded semantic fixture retains its runtime identity"),
        Component->GetRuntimeDebugContext(ReloadContext));
    TestFalse(
        TEXT("Reload leaves no stale semantic debug session"),
        ReloadContext.bSemanticDebugEnabled || ReloadContext.bSemanticDebugActive ||
            ReloadContext.bSemanticDebugPaused);
    TestTrue(
        TEXT("Reload transaction exposes debug drain and old-context collection"),
        Component->LastReloadTransaction.Contains(TEXT("\"debugDrain\"")) &&
            Component->LastReloadTransaction.Contains(TEXT("\"oldContextCollection\"")) &&
            Component->LastReloadTransaction.Contains(
                TEXT("\"retiredLoadContextsAlive\":0")));

    Component->bTickAutomatically = true;
    Error.Reset();
    TestTrue(
        *FString::Printf(TEXT("The same breakpoint rebinds to generation two: %s"), *Error),
        Component->ConfigureSemanticDebug(BuildConfiguration(2), Error));
    FSemanticDebugSnapshot ReloadedPause;
    TestTrue(
        TEXT("Reloaded generation reaches the migrated semantic breakpoint"),
        PumpUntil(TEXT("paused"), OuterNodeId, ReloadedPause));
    Error.Reset();
    TestTrue(
        *FString::Printf(TEXT("Detach drains a suspended worker safely: %s"), *Error),
        Component->DisableSemanticDebug(Error));

    FUnifiedScriptRuntimeDebugContext DebugContext;
    TestTrue(
        TEXT("Detached component retains its live World identity"),
        Component->GetRuntimeDebugContext(DebugContext));
    TestFalse(TEXT("Detached component clears semantic enablement"), DebugContext.bSemanticDebugEnabled);
    TestFalse(TEXT("Detached component has no active worker"), DebugContext.bSemanticDebugActive);

    Component->StopScript();
    Component->UnregisterComponent();
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    TestEqual(
        TEXT("Semantic debugger teardown restores the managed World count"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
        InitialManagedWorldCount);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptManagedInteropPerformanceTest,
    "UnifiedScript.Unreal.Performance.ManagedInterop",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptManagedInteropPerformanceTest::RunTest(const FString& Parameters)
{
    constexpr int32 Iterations = 100000;
    constexpr int32 WarmupIterations = 4096;
    constexpr int32 SampleCount = 5;

    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Benchmark world exists"), World);
    if (World == nullptr)
    {
        return false;
    }
    World->AddToRoot();
    UUnifiedScriptWorldSubsystem* Host =
        World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    TestNotNull(TEXT("Benchmark World host exists"), Host);
    UUnifiedScriptComponent* Component =
        NewObject<UUnifiedScriptComponent>(World);
    UUnifiedScriptAsset* Asset = NewObject<UUnifiedScriptAsset>(World);
    Asset->ScriptAssemblyPath =
        TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll");
    Asset->ScriptTypeName =
        TEXT("UnifiedScript.Unreal.Demo.ManagedInteropBenchmarkScript");
    Component->ScriptAsset = Asset;
    Component->RegisterComponentWithWorld(World);
    Component->BeginPlay();
    TestTrue(
        *FString::Printf(
            TEXT("Managed benchmark starts: %s"),
            *Component->LastScriptError),
        Component->bScriptRunning);

    auto ReadField = [](const FString& State, const TCHAR* Name, FString& OutValue)
    {
        TArray<FString> Fields;
        State.ParseIntoArray(Fields, TEXT(";"), true);
        const FString Prefix = FString(Name) + TEXT("=");
        for (const FString& Field : Fields)
        {
            if (Field.StartsWith(Prefix))
            {
                OutValue = Field.Mid(Prefix.Len());
                return true;
            }
        }
        return false;
    };

    FString ManagedNanosecondsText;
    FString AllocatedBytesText;
    FString ManagedIterationsText;
    FString ManagedSampleCountText;
    double ManagedNanosecondsPerCall = 0.0;
    int64 AllocatedBytes = -1;
    int32 ManagedIterations = 0;
    int32 ManagedSampleCount = 0;
    const bool bParsed = Component->ManagedState.StartsWith(TEXT("Benchmark:v2;")) &&
        ReadField(
            Component->ManagedState,
            TEXT("NanosecondsPerCall"),
            ManagedNanosecondsText) &&
        ReadField(
            Component->ManagedState,
            TEXT("AllocatedBytes"),
            AllocatedBytesText) &&
        ReadField(
            Component->ManagedState,
            TEXT("Iterations"),
            ManagedIterationsText) &&
        ReadField(
            Component->ManagedState,
            TEXT("SampleCount"),
            ManagedSampleCountText) &&
        LexTryParseString(ManagedNanosecondsPerCall, *ManagedNanosecondsText) &&
        LexTryParseString(AllocatedBytes, *AllocatedBytesText) &&
        LexTryParseString(ManagedIterations, *ManagedIterationsText) &&
        LexTryParseString(ManagedSampleCount, *ManagedSampleCountText);
    TestTrue(
        *FString::Printf(
            TEXT("Managed benchmark result is parseable: %s"),
            *Component->ManagedState),
        bParsed);

    int64 NativeChecksum = 0;
    for (int32 Index = 0; Index < WarmupIterations; ++Index)
    {
        NativeChecksum += Component->Add(Index, 1);
    }
    TArray<double, TInlineAllocator<SampleCount>> NativeSamples;
    NativeSamples.SetNumUninitialized(SampleCount);
    for (int32 Sample = 0; Sample < SampleCount; ++Sample)
    {
        const double NativeStarted = FPlatformTime::Seconds();
        for (int32 Index = 0; Index < Iterations; ++Index)
        {
            NativeChecksum += Component->Add(Index, 1);
        }
        const double NativeElapsedSeconds = FPlatformTime::Seconds() - NativeStarted;
        NativeSamples[Sample] = NativeElapsedSeconds * 1000000000.0 / Iterations;
    }
    NativeSamples.Sort();
    const double NativeNanosecondsPerCall = NativeSamples[SampleCount / 2];

    TestEqual(TEXT("Managed benchmark iteration count is stable"), ManagedIterations, Iterations);
    TestEqual(TEXT("Managed benchmark sample count is stable"), ManagedSampleCount, SampleCount);
    TestEqual(TEXT("Managed fixed-call loop allocates no managed bytes"), AllocatedBytes, int64{0});
    TestTrue(
        TEXT("Managed result remains inside the initial regression ceiling"),
        ManagedNanosecondsPerCall > 0.0 && ManagedNanosecondsPerCall <= 20000.0);
    TestTrue(
        TEXT("Native benchmark is measurable"),
        NativeNanosecondsPerCall > 0.0 && NativeChecksum != 0);

    UE_LOG(
        LogTemp,
        Display,
        TEXT("UNIFIEDSCRIPT_BENCHMARK_JSON {\"schemaVersion\":\"unifiedscript.unreal-benchmark/v0.2\",\"scenario\":\"generated-int32-add\",\"configuration\":\"Development Editor\",\"platform\":\"%s\",\"engineMajor\":%d,\"engineMinor\":%d,\"enginePatch\":%d,\"warmupIterations\":%d,\"iterations\":%d,\"sampleCount\":%d,\"cppDirectNsPerCall\":%.3f,\"unifiedScriptNsPerCall\":%.3f,\"unifiedScriptAllocatedBytes\":%lld,\"puertsNsPerCall\":null}"),
        ANSI_TO_TCHAR(FPlatformProperties::PlatformName()),
        FEngineVersion::Current().GetMajor(),
        FEngineVersion::Current().GetMinor(),
        FEngineVersion::Current().GetPatch(),
        WarmupIterations,
        Iterations,
        SampleCount,
        NativeNanosecondsPerCall,
        ManagedNanosecondsPerCall,
        static_cast<long long>(AllocatedBytes));

    Component->EndPlay(EEndPlayReason::Destroyed);
    Component->UnregisterComponent();
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptDataPlaneWorkloadProfileTest,
    "UnifiedScript.Unreal.Performance.DataPlaneWorkloadProfile",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptDataPlaneWorkloadProfileTest::RunTest(
    const FString& Parameters)
{
    constexpr int32 Iterations = 1024;
    constexpr int32 SampleCount = 3;

    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Data-plane profile World exists"), World);
    if (World == nullptr)
    {
        return false;
    }
    World->AddToRoot();
    UUnifiedScriptWorldSubsystem* Host =
        World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    AActor* Actor = World->SpawnActor<AActor>();
    TestNotNull(TEXT("Data-plane profile World host exists"), Host);
    TestNotNull(TEXT("Data-plane profile Actor exists"), Actor);
    if (Host == nullptr || Actor == nullptr)
    {
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        return false;
    }

    UUnifiedScriptComponent* Component =
        NewObject<UUnifiedScriptComponent>(Actor);
    Actor->AddInstanceComponent(Component);
    UUnifiedScriptAsset* Asset = NewObject<UUnifiedScriptAsset>(World);
    Asset->ScriptAssemblyPath =
        TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll");
    Asset->ScriptTypeName =
        TEXT("UnifiedScript.Unreal.Demo.DataPlaneWorkloadProfileScript");
    Component->ScriptAsset = Asset;
    Component->RegisterComponentWithWorld(World);
    Component->BeginPlay();
    TestTrue(
        *FString::Printf(
            TEXT("Managed data-plane profile starts: %s"),
            *Component->LastScriptError),
        Component->bScriptRunning);

    auto ReadField = [](const FString& State, const TCHAR* Name, FString& OutValue)
    {
        TArray<FString> Fields;
        State.ParseIntoArray(Fields, TEXT(";"), true);
        const FString Prefix = FString(Name) + TEXT("=");
        for (const FString& Field : Fields)
        {
            if (Field.StartsWith(Prefix))
            {
                OutValue = Field.Mid(Prefix.Len());
                return true;
            }
        }
        return false;
    };

    FString IterationsText;
    FString SampleCountText;
    FString BindingScalarText;
    FString CommandSubmitText;
    FString BindingTraceText;
    FString BatchTraceText;
    FString AttributeReadText;
    FString AllocatedBytesText;
    FString MovementRoute;
    FString OverlapRoute;
    FString AttributeRoute;
    FString SpawnRoute;
    FString QueryRoute;
    int32 ManagedIterations = 0;
    int32 ManagedSampleCount = 0;
    double BindingScalarNs = 0.0;
    double CommandSubmitNs = 0.0;
    double BindingTraceNs = 0.0;
    double BatchTraceNs = 0.0;
    double AttributeReadNs = 0.0;
    int64 DataPlaneAllocatedBytes = -1;
    const bool bParsed =
        Component->ManagedState.StartsWith(TEXT("DataPlaneProfile:v1;")) &&
        ReadField(Component->ManagedState, TEXT("Iterations"), IterationsText) &&
        ReadField(Component->ManagedState, TEXT("SampleCount"), SampleCountText) &&
        ReadField(Component->ManagedState, TEXT("BindingScalarNs"), BindingScalarText) &&
        ReadField(Component->ManagedState, TEXT("CommandSubmitNs"), CommandSubmitText) &&
        ReadField(Component->ManagedState, TEXT("BindingTraceNs"), BindingTraceText) &&
        ReadField(Component->ManagedState, TEXT("BatchTraceNs"), BatchTraceText) &&
        ReadField(Component->ManagedState, TEXT("AttributeReadNs"), AttributeReadText) &&
        ReadField(Component->ManagedState, TEXT("DataPlaneAllocatedBytes"), AllocatedBytesText) &&
        ReadField(Component->ManagedState, TEXT("Movement"), MovementRoute) &&
        ReadField(Component->ManagedState, TEXT("Overlap"), OverlapRoute) &&
        ReadField(Component->ManagedState, TEXT("Attribute"), AttributeRoute) &&
        ReadField(Component->ManagedState, TEXT("Spawn"), SpawnRoute) &&
        ReadField(Component->ManagedState, TEXT("Query"), QueryRoute) &&
        LexTryParseString(ManagedIterations, *IterationsText) &&
        LexTryParseString(ManagedSampleCount, *SampleCountText) &&
        LexTryParseString(BindingScalarNs, *BindingScalarText) &&
        LexTryParseString(CommandSubmitNs, *CommandSubmitText) &&
        LexTryParseString(BindingTraceNs, *BindingTraceText) &&
        LexTryParseString(BatchTraceNs, *BatchTraceText) &&
        LexTryParseString(AttributeReadNs, *AttributeReadText) &&
        LexTryParseString(DataPlaneAllocatedBytes, *AllocatedBytesText);
    TestTrue(
        *FString::Printf(
            TEXT("Data-plane workload profile is parseable: %s"),
            *Component->ManagedState),
        bParsed);
    TestEqual(TEXT("Profile iteration count is stable"), ManagedIterations, Iterations);
    TestEqual(TEXT("Profile sample count is stable"), ManagedSampleCount, SampleCount);
    TestTrue(
        TEXT("Movement command submission beats scalar binding overhead"),
        CommandSubmitNs > 0.0 && CommandSubmitNs < BindingScalarNs);
    TestTrue(
        TEXT("Batch Trace beats the matched per-call binding workload"),
        BatchTraceNs > 0.0 && BatchTraceNs < BindingTraceNs);
    TestTrue(TEXT("Attribute binding remains measurable"), AttributeReadNs > 0.0);
    TestEqual(
        TEXT("Fixed-row data-plane loops allocate no managed memory"),
        DataPlaneAllocatedBytes,
        int64{0});
    TestEqual(TEXT("Movement route is evidence-backed"), MovementRoute, TEXT("data-plane"));
    TestEqual(TEXT("Overlap route is evidence-backed"), OverlapRoute, TEXT("data-plane"));
    TestEqual(TEXT("Attribute route remains generated binding"), AttributeRoute, TEXT("binding"));
    TestEqual(TEXT("Spawn route stays hybrid"), SpawnRoute, TEXT("hybrid"));
    TestEqual(TEXT("Query route uses the data plane"), QueryRoute, TEXT("data-plane"));

    UE_LOG(
        LogTemp,
        Display,
        TEXT("UNIFIEDSCRIPT_DATA_PLANE_PROFILE_JSON {\"schemaVersion\":\"unifiedscript.unreal-data-plane-profile/v0.1\",\"iterations\":%d,\"sampleCount\":%d,\"bindingScalarNs\":%.3f,\"commandSubmitNs\":%.3f,\"bindingTraceNs\":%.3f,\"batchTraceNs\":%.3f,\"attributeReadNs\":%.3f,\"dataPlaneAllocatedBytes\":%lld,\"movement\":\"%s\",\"overlap\":\"%s\",\"attribute\":\"%s\",\"spawn\":\"%s\",\"query\":\"%s\"}"),
        ManagedIterations,
        ManagedSampleCount,
        BindingScalarNs,
        CommandSubmitNs,
        BindingTraceNs,
        BatchTraceNs,
        AttributeReadNs,
        static_cast<long long>(DataPlaneAllocatedBytes),
        *MovementRoute,
        *OverlapRoute,
        *AttributeRoute,
        *SpawnRoute,
        *QueryRoute);

    Host->EndManagedFrame();
    Component->EndPlay(EEndPlayReason::Destroyed);
    Component->UnregisterComponent();
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}

#endif
