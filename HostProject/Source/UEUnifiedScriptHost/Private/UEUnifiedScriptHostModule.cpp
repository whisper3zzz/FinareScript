#include "Containers/Ticker.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "HAL/PlatformMemory.h"
#include "HAL/PlatformTime.h"
#include "Misc/CommandLine.h"
#include "Misc/CoreDelegates.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Modules/ModuleManager.h"
#include "UnifiedScriptActor.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "UObject/StructOnScope.h"
#include "UObject/UnrealType.h"

namespace UE::UnifiedScript::PackagedScriptClassSmoke::Private
{
bool ReadArgument(const TCHAR* Name, FString& OutValue)
{
    if (!FParse::Value(FCommandLine::Get(), Name, OutValue))
    {
        return false;
    }
    OutValue.TrimQuotesInline();
    return !OutValue.IsEmpty();
}

void Finish(bool bSuccess, const FString& Detail)
{
    FString ResultPath;
    ReadArgument(TEXT("UnifiedScriptScriptClassSmokeResult="), ResultPath);
    const FString Result = FString::Printf(
        TEXT("%s\n%s\n"),
        bSuccess ? TEXT("PASS") : TEXT("FAIL"),
        *Detail);
    if (!ResultPath.IsEmpty())
    {
        FFileHelper::SaveStringToFile(
            Result,
            *ResultPath,
            FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM);
    }
    UE_LOG(
        LogTemp,
        Display,
        TEXT("UNIFIEDSCRIPT_SCRIPT_CLASS_PACKAGED_SMOKE %s %s"),
        bSuccess ? TEXT("PASS") : TEXT("FAIL"),
        *Detail.ReplaceCharWithEscapedChar());
    FPlatformMisc::RequestExitWithStatus(
        false,
        bSuccess ? 0 : 1,
        TEXT("UnifiedScriptScriptClassPackagedSmoke"));
}

bool InvokeManagedBlueprintEvent(
    UObject& Target,
    UClass& GeneratedClass,
    int32 Amount,
    int32 ExpectedState,
    FString& OutError)
{
    const UUnifiedScriptBlueprintGeneratedClass* ManagedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(&GeneratedClass);
    if (ManagedClass == nullptr || ManagedClass->ManagedLifecycleBindings.Num() != 3)
    {
        OutError = TEXT("GeneratedClass did not retain all lifecycle identities.");
        return false;
    }

    FName EventName = NAME_None;
    for (const TPair<FName, FUnifiedScriptManagedFunctionBinding>& Pair :
         ManagedClass->ManagedFunctionBindings)
    {
        if (Pair.Value.ManagedName == TEXT("OnManagedActivated"))
        {
            if (!Pair.Value.bBlueprintEvent ||
                Pair.Value.BlueprintEventSuperMode !=
                    EUnifiedScriptBlueprintEventSuperMode::Explicit)
            {
                OutError = TEXT("Packaged managed Event lost its explicit Super contract.");
                return false;
            }
            EventName = Pair.Key;
            break;
        }
    }
    UFunction* EventFunction = EventName.IsNone()
        ? nullptr
        : GeneratedClass.FindFunctionByName(
            EventName,
            EIncludeSuperFlag::ExcludeSuper);
    FIntProperty* AmountProperty = EventFunction != nullptr
        ? FindFProperty<FIntProperty>(EventFunction, TEXT("amount"))
        : nullptr;
    FIntProperty* StateProperty = FindFProperty<FIntProperty>(
        &GeneratedClass,
        TEXT("StartValue"));
    if (EventFunction == nullptr || AmountProperty == nullptr ||
        StateProperty == nullptr ||
        !EventFunction->HasAllFunctionFlags(FUNC_Event | FUNC_BlueprintEvent))
    {
        OutError = TEXT("Packaged managed Event reflection contract is incomplete.");
        return false;
    }
    if (!ManagedClass->IsManagedFunctionBound(EventName))
    {
        OutError = TEXT("Packaged managed Event lost its native dispatcher.");
        return false;
    }
#if UE_BLUEPRINT_EVENTGRAPH_FASTCALLS
    if (EventFunction->EventGraphFunction != nullptr)
    {
        OutError = TEXT("Packaged managed Event still targets an empty fast-call graph.");
        return false;
    }
#endif
#if !WITH_EDITOR
    if (!EventFunction->HasAnyFunctionFlags(FUNC_Native))
    {
        OutError = TEXT("Packaged managed Event lost its native dispatch flag.");
        return false;
    }
#endif
    const int32 EventCallspace = Target.GetFunctionCallspace(EventFunction, nullptr);
    if ((EventCallspace & FunctionCallspace::Local) == 0)
    {
        OutError = FString::Printf(
            TEXT("Packaged managed Event is not locally callable (callspace=%d flags=0x%08X)."),
            EventCallspace,
            static_cast<uint32>(EventFunction->FunctionFlags));
        return false;
    }

    FStructOnScope Parameters(EventFunction);
    AmountProperty->SetPropertyValue_InContainer(
        Parameters.GetStructMemory(),
        Amount);
    Target.ProcessEvent(EventFunction, Parameters.GetStructMemory());
    const int32 State = StateProperty->GetPropertyValue_InContainer(&Target);
    if (State != ExpectedState)
    {
        OutError = FString::Printf(
            TEXT("Packaged managed Event returned state %d instead of %d (callspace=%d flags=0x%08X scriptBytes=%d)."),
            State,
            ExpectedState,
            EventCallspace,
            static_cast<uint32>(EventFunction->FunctionFlags),
            EventFunction->Script.Num());
        return false;
    }
    return true;
}

void Run()
{
    FString ActorClassPath;
    FString ComponentClassPath;
    if (!ReadArgument(TEXT("UnifiedScriptActorClass="), ActorClassPath) ||
        !ReadArgument(TEXT("UnifiedScriptComponentClass="), ComponentClassPath))
    {
        Finish(false, TEXT("Required Actor or Component class path is missing."));
        return;
    }

    UClass* ActorClass = LoadClass<AUnifiedScriptActor>(nullptr, *ActorClassPath);
    UClass* ComponentClass = LoadClass<UUnifiedScriptComponent>(
        nullptr,
        *ComponentClassPath);
    if (ActorClass == nullptr || ComponentClass == nullptr)
    {
        Finish(
            false,
            FString::Printf(
                TEXT("Packaged class load failed; actor=%s; component=%s"),
                ActorClass != nullptr ? TEXT("loaded") : TEXT("missing"),
                ComponentClass != nullptr ? TEXT("loaded") : TEXT("missing")));
        return;
    }

    const int32 InitialManagedWorldCount =
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (World == nullptr)
    {
        Finish(false, TEXT("Unable to create the packaged smoke World."));
        return;
    }
    World->AddToRoot();

    UUnifiedScriptWorldSubsystem* Host =
        World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    AUnifiedScriptActor* ScriptActor = World->SpawnActor<AUnifiedScriptActor>(
        ActorClass);
    AActor* ComponentOwner = World->SpawnActor<AActor>();
    UUnifiedScriptComponent* AttachedComponent = ComponentOwner != nullptr
        ? NewObject<UUnifiedScriptComponent>(ComponentOwner, ComponentClass)
        : nullptr;
    if (ComponentOwner != nullptr && AttachedComponent != nullptr)
    {
        ComponentOwner->AddInstanceComponent(AttachedComponent);
        AttachedComponent->RegisterComponentWithWorld(World);
    }
    // AActor::ProcessEvent intentionally ignores gameplay events until its
    // World has routed actor initialization. Keep this packaged smoke on the
    // same execution path as a real game World instead of calling components
    // manually inside an uninitialized test shell.
    World->InitializeActorsForPlay(FURL());

    UUnifiedScriptComponent* ActorComponent = ScriptActor != nullptr
        ? ScriptActor->ScriptComponent
        : nullptr;
    FString Failure;
    FString ActorIdentityError;
    FString ComponentIdentityError;
    const bool bObjectsReady = World->AreActorsInitialized() &&
        Host != nullptr && ScriptActor != nullptr &&
        ComponentOwner != nullptr && AttachedComponent != nullptr &&
        ActorComponent != nullptr && ActorComponent->ScriptAsset != nullptr &&
        AttachedComponent->ScriptAsset != nullptr;
    if (!bObjectsReady)
    {
        Failure = TEXT(
            "Packaged Actor/Component instances or Script Assets are unavailable.");
    }

    const bool bActorIdentity = bObjectsReady &&
        ActorComponent->ScriptAsset->ValidateGeneratedClassIdentity(
            ActorComponent,
            ScriptActor,
            ActorIdentityError);
    const bool bComponentIdentity = bObjectsReady &&
        AttachedComponent->ScriptAsset->ValidateGeneratedClassIdentity(
            AttachedComponent,
            ComponentOwner,
            ComponentIdentityError);
    if (Failure.IsEmpty() && (!bActorIdentity || !bComponentIdentity))
    {
        Failure = FString::Printf(
            TEXT("Generated-class identity failed; actor=%s; component=%s"),
            *ActorIdentityError,
            *ComponentIdentityError);
    }

    if (Failure.IsEmpty())
    {
        ActorComponent->BeginPlay();
        AttachedComponent->BeginPlay();
    }
    const bool bActorStarted = ActorComponent != nullptr &&
        ActorComponent->bScriptRunning && ActorComponent->LastScriptError.IsEmpty();
    const bool bComponentStarted = AttachedComponent != nullptr &&
        AttachedComponent->bScriptRunning &&
        AttachedComponent->LastScriptError.IsEmpty();
    const int32 LiveManagedScriptCount = Host != nullptr
        ? Host->GetManagedScriptCount()
        : -1;
    const int32 LiveLifecycleClaimCount = Host != nullptr
        ? Host->GetGameplayLifecycleClaimCount()
        : -1;
    if (Failure.IsEmpty() &&
        (!bActorStarted || !bComponentStarted ||
         LiveManagedScriptCount != 2 || LiveLifecycleClaimCount != 2))
    {
        Failure = FString::Printf(
            TEXT("Managed BeginPlay failed; actor=%s (%s); component=%s (%s); scripts=%d; claims=%d"),
            bActorStarted ? TEXT("true") : TEXT("false"),
            ActorComponent != nullptr
                ? *ActorComponent->LastScriptError
                : TEXT("missing"),
            bComponentStarted ? TEXT("true") : TEXT("false"),
            AttachedComponent != nullptr
                ? *AttachedComponent->LastScriptError
                : TEXT("missing"),
            LiveManagedScriptCount,
            LiveLifecycleClaimCount);
    }

    if (Failure.IsEmpty())
    {
        Host->EndManagedFrame();
        Host->BeginManagedFrame();
        ActorComponent->TickComponent(0.016f, LEVELTICK_All, nullptr);
        AttachedComponent->TickComponent(0.016f, LEVELTICK_All, nullptr);
    }
    const bool bActorTicked = bActorStarted && ActorComponent != nullptr &&
        ActorComponent->bScriptRunning && ActorComponent->LastScriptError.IsEmpty();
    const bool bComponentTicked = bComponentStarted &&
        AttachedComponent != nullptr && AttachedComponent->bScriptRunning &&
        AttachedComponent->LastScriptError.IsEmpty();
    if (Failure.IsEmpty() && (!bActorTicked || !bComponentTicked))
    {
        Failure = TEXT("Managed Actor or Component Tick failed.");
    }

    bool bActorEvent = false;
    bool bComponentEvent = false;
    if (Failure.IsEmpty())
    {
        FString EventError;
        bActorEvent = InvokeManagedBlueprintEvent(
            *ScriptActor,
            *ActorClass,
            3,
            5,
            EventError);
        if (!bActorEvent)
        {
            Failure = TEXT("Actor ") + EventError;
        }
        else
        {
            bComponentEvent = InvokeManagedBlueprintEvent(
                *AttachedComponent,
                *ComponentClass,
                4,
                6,
                EventError);
            if (!bComponentEvent)
            {
                Failure = TEXT("ActorComponent ") + EventError;
            }
        }
    }

    const FString ActorProgram = ActorComponent != nullptr
        ? ActorComponent->GetActiveProgramPath()
        : FString();
    const FString ComponentProgram = AttachedComponent != nullptr
        ? AttachedComponent->GetActiveProgramPath()
        : FString();
    const FString HostFxrPath = FUnifiedScriptCoreClrHost::Get().GetHostFxrPath();
    const bool bCompiledChunks = ActorProgram.EndsWith(TEXT(".uschunk")) &&
        ComponentProgram.EndsWith(TEXT(".uschunk"));
    const bool bPluginLocalRuntime = HostFxrPath.Contains(
        TEXT("UEUnifiedScript/Binaries/ThirdParty/DotNet/10.0"));
    if (Failure.IsEmpty() && (!bCompiledChunks || !bPluginLocalRuntime))
    {
        Failure = FString::Printf(
            TEXT("Packaged runtime closure failed; actorProgram=%s; componentProgram=%s; hostfxr=%s"),
            *ActorProgram,
            *ComponentProgram,
            *HostFxrPath);
    }

    if (ActorComponent != nullptr && ActorComponent->bScriptRunning)
    {
        ActorComponent->EndPlay(EEndPlayReason::Quit);
    }
    if (AttachedComponent != nullptr && AttachedComponent->bScriptRunning)
    {
        AttachedComponent->EndPlay(EEndPlayReason::Quit);
    }
    const int32 FinalManagedScriptCount = Host != nullptr
        ? Host->GetManagedScriptCount()
        : -1;
    const int32 FinalLifecycleClaimCount = Host != nullptr
        ? Host->GetGameplayLifecycleClaimCount()
        : -1;
    if (AttachedComponent != nullptr)
    {
        AttachedComponent->UnregisterComponent();
    }
    if (ScriptActor != nullptr)
    {
        ScriptActor->Destroy();
    }
    if (ComponentOwner != nullptr)
    {
        ComponentOwner->Destroy();
    }
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    const int32 FinalManagedWorldCount =
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();
    if (Failure.IsEmpty() &&
        (FinalManagedScriptCount != 0 || FinalLifecycleClaimCount != 0 ||
         FinalManagedWorldCount != InitialManagedWorldCount))
    {
        Failure = FString::Printf(
            TEXT("Packaged teardown leaked state; scripts=%d; claims=%d; worldsBefore=%d; worldsAfter=%d"),
            FinalManagedScriptCount,
            FinalLifecycleClaimCount,
            InitialManagedWorldCount,
            FinalManagedWorldCount);
    }

    Finish(
        Failure.IsEmpty(),
        Failure.IsEmpty()
            ? FString::Printf(
                TEXT("actorClass=%s; componentClass=%s; actorStarted=true; componentStarted=true; actorTicked=true; componentTicked=true; actorEvent=true; componentEvent=true; managedScriptCount=2; lifecycleClaimCount=2; finalManagedScriptCount=0; finalLifecycleClaimCount=0; managedWorldCountBefore=%d; managedWorldCountAfter=%d; actorProgram=%s; componentProgram=%s; hostfxr=%s"),
                *ActorClassPath,
                *ComponentClassPath,
                InitialManagedWorldCount,
                FinalManagedWorldCount,
                *ActorProgram,
                *ComponentProgram,
                *HostFxrPath)
            : Failure);
}
}

namespace UE::UnifiedScript::HostFixture::Private
{
void FinishSmoke(bool bSuccess, const FString& Detail)
{
    FString ResultPath;
    if (FParse::Value(
            FCommandLine::Get(),
            TEXT("UnifiedScriptSmokeResult="),
            ResultPath))
    {
        ResultPath.TrimQuotesInline();
        FFileHelper::SaveStringToFile(
            FString::Printf(
                TEXT("%s\n%s\n"),
                bSuccess ? TEXT("PASS") : TEXT("FAIL"),
                *Detail),
            *ResultPath,
            FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM);
    }
    UE_LOG(
        LogTemp,
        Display,
        TEXT("UNIFIEDSCRIPT_PACKAGED_SMOKE %s detail=\"%s\""),
        bSuccess ? TEXT("PASS") : TEXT("FAIL"),
        *Detail.ReplaceCharWithEscapedChar());
    FPlatformMisc::RequestExitWithStatus(
        false,
        bSuccess ? 0 : 1,
        TEXT("UnifiedScriptPackagedSmoke"));
}

void RunSmoke()
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (World == nullptr)
    {
        FinishSmoke(false, TEXT("Unable to create the smoke-test World."));
        return;
    }

    UUnifiedScriptComponent* Component = NewObject<UUnifiedScriptComponent>(World);
    Component->ScriptAssemblyPath =
        TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.uschunk");
    Component->ScriptTypeName = TEXT("UnifiedScript.Unreal.Demo.DemoScript");
    Component->RegisterComponentWithWorld(World);
    Component->BeginPlay();

    const bool bStarted =
        Component->bScriptRunning && Component->ManagedState == TEXT("Started:10");
    if (bStarted)
    {
        Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
    }
    const bool bTicked = bStarted && Component->ManagedState == TEXT("Ticked");
    const FString ProgramPath = Component->GetActiveProgramPath();
    const double StartupMilliseconds = Component->LastStartMilliseconds;
    const bool bUsesCompiledChunk =
        ProgramPath.EndsWith(TEXT(".uschunk"));
    const FString HostFxrPath = FUnifiedScriptCoreClrHost::Get().GetHostFxrPath();
    const bool bUsesPackagedRuntime =
        HostFxrPath.Contains(TEXT("UEUnifiedScript/Binaries/ThirdParty/DotNet/10.0"));
    const FString Failure = !bStarted
            ? Component->LastScriptError
        : !bTicked
            ? TEXT("Managed OnTick did not update the component state.")
            : !bUsesCompiledChunk
                ? TEXT("Packaged smoke did not start from a compiled chunk.")
            : !bUsesPackagedRuntime
                ? FString::Printf(TEXT("CoreCLR used a non-packaged hostfxr: %s"), *HostFxrPath)
                : FString();

    Component->EndPlay(EEndPlayReason::Quit);
    Component->UnregisterComponent();
    World->RemoveFromRoot();
    World->DestroyWorld(false);

    FinishSmoke(
        Failure.IsEmpty(),
        Failure.IsEmpty()
            ? FString::Printf(
                TEXT("Managed Start/Tick/Stop succeeded; program=%s; startupMs=%.6f; hostfxr=%s"),
                *ProgramPath,
                StartupMilliseconds,
                *HostFxrPath)
            : Failure);
}
}

class FUEUnifiedScriptHostModule final : public FDefaultGameModuleImpl
{
public:
    virtual void StartupModule() override
    {
        FDefaultGameModuleImpl::StartupModule();
        if (FParse::Param(FCommandLine::Get(), TEXT("UnifiedScriptSmoke")))
        {
            SmokeDelegateHandle = FCoreDelegates::OnFEngineLoopInitComplete.AddStatic(
                &UE::UnifiedScript::HostFixture::Private::RunSmoke);
        }
        if (FParse::Param(
                FCommandLine::Get(),
                TEXT("UnifiedScriptScriptClassSmoke")))
        {
            ScriptClassSmokeDelegateHandle =
                FCoreDelegates::OnFEngineLoopInitComplete.AddStatic(
                    &UE::UnifiedScript::PackagedScriptClassSmoke::Private::Run);
        }
        if (FParse::Param(
                FCommandLine::Get(),
                TEXT("UnifiedScriptFrameWorkload")))
        {
            FrameBenchmarkInitHandle =
                FCoreDelegates::OnFEngineLoopInitComplete.AddRaw(
                    this,
                    &FUEUnifiedScriptHostModule::QueueFrameBenchmark);
        }
    }

    virtual void ShutdownModule() override
    {
        if (SmokeDelegateHandle.IsValid())
        {
            FCoreDelegates::OnFEngineLoopInitComplete.Remove(SmokeDelegateHandle);
            SmokeDelegateHandle.Reset();
        }
        if (ScriptClassSmokeDelegateHandle.IsValid())
        {
            FCoreDelegates::OnFEngineLoopInitComplete.Remove(
                ScriptClassSmokeDelegateHandle);
            ScriptClassSmokeDelegateHandle.Reset();
        }
        if (FrameBenchmarkInitHandle.IsValid())
        {
            FCoreDelegates::OnFEngineLoopInitComplete.Remove(
                FrameBenchmarkInitHandle);
            FrameBenchmarkInitHandle.Reset();
        }
        if (FrameBenchmarkTickerHandle.IsValid())
        {
            FTSTicker::GetCoreTicker().RemoveTicker(
                FrameBenchmarkTickerHandle);
            FrameBenchmarkTickerHandle.Reset();
        }
        if (FrameBenchmarkStartTickerHandle.IsValid())
        {
            FTSTicker::GetCoreTicker().RemoveTicker(
                FrameBenchmarkStartTickerHandle);
            FrameBenchmarkStartTickerHandle.Reset();
        }
        CleanupFrameBenchmark();
        FDefaultGameModuleImpl::ShutdownModule();
    }

private:
    static bool ReadField(
        const FString& State,
        const TCHAR* Name,
        FString& OutValue)
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
    }

    static double Median(const TArray<double>& Sorted)
    {
        const int32 Middle = Sorted.Num() / 2;
        return (Sorted.Num() & 1) != 0
            ? Sorted[Middle]
            : (Sorted[Middle - 1] + Sorted[Middle]) * 0.5;
    }

    static double Percentile(const TArray<double>& Sorted, double P)
    {
        const int32 Index = FMath::Clamp(
            FMath::CeilToInt(P * static_cast<double>(Sorted.Num())) - 1,
            0,
            Sorted.Num() - 1);
        return Sorted[Index];
    }

    void QueueFrameBenchmark()
    {
        if (FrameBenchmarkInitHandle.IsValid())
        {
            FCoreDelegates::OnFEngineLoopInitComplete.Remove(
                FrameBenchmarkInitHandle);
            FrameBenchmarkInitHandle.Reset();
        }
        FrameBenchmarkStartTickerHandle = FTSTicker::GetCoreTicker().AddTicker(
            FTickerDelegate::CreateRaw(
                this,
                &FUEUnifiedScriptHostModule::StartFrameBenchmarkNextTick));
    }

    bool StartFrameBenchmarkNextTick(float)
    {
        FrameBenchmarkStartTickerHandle.Reset();
        StartFrameBenchmark();
        return false;
    }

    void StartFrameBenchmark()
    {
        FParse::Value(
            FCommandLine::Get(),
            TEXT("UnifiedScriptRepresentativeWorkload="),
            FrameWorkloadId);
        FParse::Value(
            FCommandLine::Get(),
            TEXT("UnifiedScriptRepresentativeResult="),
            FrameResultPath);
        FrameWorkloadId.TrimQuotesInline();
        FrameResultPath.TrimQuotesInline();
        const bool bPerFrame =
            FrameWorkloadId == TEXT("per-frame-actor-logic");
        const bool bMass = FrameWorkloadId == TEXT("mass-entities");
        if ((!bPerFrame && !bMass) || FrameResultPath.IsEmpty())
        {
            FinishFrameBenchmark(
                false,
                TEXT("Frame workload id or result path is invalid."));
            return;
        }
        FrameWarmupIterations = bPerFrame ? 300 : 120;
        FrameMeasuredIterations = bPerFrame ? 10000 : 1000;
        FrameExpectedCalls = bPerFrame ? 20000ll : 1001000ll;
        FrameExpectedBytes = bPerFrame ? 440000ll : 72004000ll;
        FrameExpectedChecksum = bPerFrame ? 100015000ll : 1501500000ll;

        FrameWorld = UWorld::CreateWorld(EWorldType::Game, false);
        if (FrameWorld == nullptr)
        {
            FinishFrameBenchmark(false, TEXT("Unable to create benchmark World."));
            return;
        }
        FrameWorld->AddToRoot();
        FrameActor = FrameWorld->SpawnActor<AActor>();
        if (FrameActor == nullptr)
        {
            FinishFrameBenchmark(false, TEXT("Unable to spawn benchmark Actor."));
            return;
        }
        FrameComponent = NewObject<UUnifiedScriptComponent>(FrameActor);
        FrameActor->AddInstanceComponent(FrameComponent);
        FrameComponent->ScriptAssemblyPath =
            TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll");
        FrameComponent->ScriptTypeName =
            TEXT("UnifiedScript.Unreal.Demo.RepresentativeWorkloadBenchmarkScript");
        FrameComponent->ManagedState =
            FString::Printf(TEXT("Run=%s"), *FrameWorkloadId);
        FrameComponent->RegisterComponentWithWorld(FrameWorld);
        const double StartupStarted = FPlatformTime::Seconds();
        FrameComponent->BeginPlay();
        FrameStartupMilliseconds = FMath::Max(
            0.001,
            (FPlatformTime::Seconds() - StartupStarted) * 1000.0);
        if (!FrameComponent->bScriptRunning ||
            !FrameComponent->ManagedState.Contains(TEXT("Status=ready")))
        {
            FinishFrameBenchmark(
                false,
                FString::Printf(
                    TEXT("Managed frame workload failed to start: %s state=%s"),
                    *FrameComponent->LastScriptError,
                    *FrameComponent->ManagedState));
            return;
        }

        FrameSamples.Reserve(FrameMeasuredIterations);
        FrameBenchmarkTickerHandle = FTSTicker::GetCoreTicker().AddTicker(
            FTickerDelegate::CreateRaw(
                this,
                &FUEUnifiedScriptHostModule::TickFrameBenchmark));
    }

    bool TickFrameBenchmark(float)
    {
        if (FrameComponent == nullptr || !FrameComponent->bScriptRunning)
        {
            FinishFrameBenchmark(false, TEXT("Managed frame workload stopped early."));
            return false;
        }
        const int32 MeasuredEnd =
            FrameWarmupIterations + FrameMeasuredIterations;
        if (FrameTickIndex < MeasuredEnd)
        {
            const bool bMeasured = FrameTickIndex >= FrameWarmupIterations;
            const uint64 StartedCycles = bMeasured
                ? FPlatformTime::Cycles64()
                : 0;
            FrameComponent->TickComponent(
                1.0f / 60.0f,
                LEVELTICK_All,
                nullptr);
            if (bMeasured)
            {
                const double Milliseconds =
                    FPlatformTime::ToSeconds64(
                        FPlatformTime::Cycles64() - StartedCycles) * 1000.0;
                FrameSamples.Add(Milliseconds);
                FrameBridgeMilliseconds += Milliseconds;
            }
            ++FrameTickIndex;
            return true;
        }

        // The publication tick is deliberately outside the measured window.
        FrameComponent->TickComponent(
            1.0f / 60.0f,
            LEVELTICK_All,
            nullptr);
        FinishFrameBenchmark(true, FString());
        return false;
    }

    void FinishFrameBenchmark(bool bCandidateSuccess, const FString& Detail)
    {
        bool bSuccess = bCandidateSuccess;
        FString Failure = Detail;
        if (bCandidateSuccess)
        {
            FString ActualWorkload;
            FString Status;
            FString WarmupText;
            FString IterationsText;
            FString CallsText;
            FString BytesText;
            FString AllocatedText;
            FString GcCountText;
            FString GcPauseText;
            FString ChecksumText;
            int32 Warmup = 0;
            int32 Iterations = 0;
            int64 Calls = 0;
            int64 Bytes = 0;
            int64 Allocated = -1;
            int32 GcCount = -1;
            double GcPause = -1.0;
            int64 Checksum = 0;
            const FString State = FrameComponent != nullptr
                ? FrameComponent->ManagedState
                : FString();
            const bool bParsed =
                State.StartsWith(TEXT("RepresentativeFrame:v1;")) &&
                ReadField(State, TEXT("Workload"), ActualWorkload) &&
                ReadField(State, TEXT("Status"), Status) &&
                ReadField(State, TEXT("WarmupIterations"), WarmupText) &&
                ReadField(State, TEXT("Iterations"), IterationsText) &&
                ReadField(State, TEXT("Calls"), CallsText) &&
                ReadField(State, TEXT("BytesCopied"), BytesText) &&
                ReadField(State, TEXT("AllocatedBytes"), AllocatedText) &&
                ReadField(State, TEXT("GcCollectionCount"), GcCountText) &&
                ReadField(State, TEXT("GcPauseMilliseconds"), GcPauseText) &&
                ReadField(State, TEXT("Checksum"), ChecksumText) &&
                LexTryParseString(Warmup, *WarmupText) &&
                LexTryParseString(Iterations, *IterationsText) &&
                LexTryParseString(Calls, *CallsText) &&
                LexTryParseString(Bytes, *BytesText) &&
                LexTryParseString(Allocated, *AllocatedText) &&
                LexTryParseString(GcCount, *GcCountText) &&
                LexTryParseString(GcPause, *GcPauseText) &&
                LexTryParseString(Checksum, *ChecksumText);
            bSuccess = bParsed && ActualWorkload == FrameWorkloadId &&
                Status == TEXT("complete") &&
                Warmup == FrameWarmupIterations &&
                Iterations == FrameMeasuredIterations &&
                Calls == FrameExpectedCalls && Bytes == FrameExpectedBytes &&
                Checksum == FrameExpectedChecksum && Allocated >= 0 &&
                GcCount >= 0 && GcPause >= 0.0 &&
                FrameSamples.Num() == FrameMeasuredIterations &&
                FrameBridgeMilliseconds > 0.0;
            if (!bSuccess)
            {
                Failure = FString::Printf(
                    TEXT("Frame workload contract failed: %s"),
                    *State);
            }
            else
            {
                FrameSamples.Sort();
                const double FrameMedian = Median(FrameSamples);
                const double FrameP95 = Percentile(FrameSamples, 0.95);
                const double FrameP99 = Percentile(FrameSamples, 0.99);
                const FString Json = FString::Printf(
                    TEXT("{\n")
                    TEXT("  \"schemaVersion\": \"unifiedscript.representative-workload/v0.1\",\n")
                    TEXT("  \"workloadId\": \"%s\",\n")
                    TEXT("  \"warmupIterations\": %d,\n")
                    TEXT("  \"iterations\": %d,\n")
                    TEXT("  \"frameTimeMedianMilliseconds\": %.9f,\n")
                    TEXT("  \"frameTimeP95Milliseconds\": %.9f,\n")
                    TEXT("  \"frameTimeP99Milliseconds\": %.9f,\n")
                    TEXT("  \"bridgeTimeMilliseconds\": %.6f,\n")
                    TEXT("  \"calls\": %lld,\n")
                    TEXT("  \"bytesCopied\": %lld,\n")
                    TEXT("  \"allocatedBytes\": %lld,\n")
                    TEXT("  \"gcCollectionCount\": %d,\n")
                    TEXT("  \"gcPauseMilliseconds\": %.6f,\n")
                    TEXT("  \"memoryHighWaterBytes\": %llu,\n")
                    TEXT("  \"startupMilliseconds\": %.6f,\n")
                    TEXT("  \"checksum\": %lld\n")
                    TEXT("}\n"),
                    *FrameWorkloadId,
                    Warmup,
                    Iterations,
                    FrameMedian,
                    FrameP95,
                    FrameP99,
                    FrameBridgeMilliseconds,
                    static_cast<long long>(Calls),
                    static_cast<long long>(Bytes),
                    static_cast<long long>(Allocated),
                    GcCount,
                    GcPause,
                    static_cast<unsigned long long>(
                        FPlatformMemory::GetStats().PeakUsedPhysical),
                    FrameStartupMilliseconds,
                    static_cast<long long>(Checksum));
                bSuccess = FFileHelper::SaveStringToFile(
                    Json,
                    *FrameResultPath,
                    FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM);
                if (!bSuccess)
                {
                    Failure = FString::Printf(
                        TEXT("Cannot write frame workload result: %s"),
                        *FrameResultPath);
                }
                else
                {
                    FString Condensed = Json;
                    Condensed.ReplaceInline(TEXT("\r"), TEXT(""));
                    Condensed.ReplaceInline(TEXT("\n"), TEXT(""));
                    UE_LOG(
                        LogTemp,
                        Display,
                        TEXT("UNIFIEDSCRIPT_REPRESENTATIVE_WORKLOAD_JSON %s"),
                        *Condensed);
                }
            }
        }

        FrameBenchmarkTickerHandle.Reset();
        CleanupFrameBenchmark();
        if (!bSuccess)
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("UNIFIEDSCRIPT_REPRESENTATIVE_WORKLOAD_FAILED %s"),
                *Failure);
        }
        FPlatformMisc::RequestExitWithStatus(
            false,
            bSuccess ? 0 : 1,
            TEXT("UnifiedScriptRepresentativeWorkload"));
    }

    void CleanupFrameBenchmark()
    {
        if (FrameComponent != nullptr)
        {
            if (FrameComponent->bScriptRunning)
            {
                FrameComponent->EndPlay(EEndPlayReason::Quit);
            }
            FrameComponent->UnregisterComponent();
            FrameComponent = nullptr;
        }
        FrameActor = nullptr;
        if (FrameWorld != nullptr)
        {
            FrameWorld->RemoveFromRoot();
            FrameWorld->DestroyWorld(false);
            FrameWorld = nullptr;
        }
    }

    FDelegateHandle SmokeDelegateHandle;
    FDelegateHandle ScriptClassSmokeDelegateHandle;
    FDelegateHandle FrameBenchmarkInitHandle;
    FTSTicker::FDelegateHandle FrameBenchmarkStartTickerHandle;
    FTSTicker::FDelegateHandle FrameBenchmarkTickerHandle;
    UWorld* FrameWorld = nullptr;
    AActor* FrameActor = nullptr;
    UUnifiedScriptComponent* FrameComponent = nullptr;
    FString FrameWorkloadId;
    FString FrameResultPath;
    TArray<double> FrameSamples;
    int32 FrameWarmupIterations = 0;
    int32 FrameMeasuredIterations = 0;
    int32 FrameTickIndex = 0;
    int64 FrameExpectedCalls = 0;
    int64 FrameExpectedBytes = 0;
    int64 FrameExpectedChecksum = 0;
    double FrameBridgeMilliseconds = 0.0;
    double FrameStartupMilliseconds = 0.0;
};

IMPLEMENT_PRIMARY_GAME_MODULE(
    FUEUnifiedScriptHostModule,
    UEUnifiedScriptHost,
    "UEUnifiedScriptHost");
