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
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptCoreClrHost.h"

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
        TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll");
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
    const FString HostFxrPath = FUnifiedScriptCoreClrHost::Get().GetHostFxrPath();
    const bool bUsesPackagedRuntime =
        HostFxrPath.Contains(TEXT("UEUnifiedScript/Binaries/ThirdParty/DotNet/10.0"));
    const FString Failure = !bStarted
        ? Component->LastScriptError
        : !bTicked
            ? TEXT("Managed OnTick did not update the component state.")
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
            ? FString::Printf(TEXT("Managed Start/Tick/Stop succeeded; hostfxr=%s"), *HostFxrPath)
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
