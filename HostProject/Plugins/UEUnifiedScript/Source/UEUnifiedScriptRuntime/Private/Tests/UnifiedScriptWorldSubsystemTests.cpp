#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptWorldSubsystem.h"

#include "Containers/StringConv.h"
#include "Engine/World.h"
#include "HAL/PlatformTime.h"
#include "Misc/AutomationTest.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UnifiedScriptAsset.h"
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
    TestTrue(
        TEXT("Instance state override is accepted"),
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
    TestEqual(
        TEXT("Managed OnStart reads materialized state through the generated wrapper"),
        FirstComponent->ManagedState,
        FString(TEXT("Started:37")));
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

    FirstComponent->TickComponent(0.016f, LEVELTICK_All, nullptr);
    TestEqual(
        TEXT("Managed OnTick executes through the component host"),
        FirstComponent->ManagedState,
        FString(TEXT("Ticked")));

    TestTrue(TEXT("Component reload creates a new script generation"), FirstComponent->ReloadScript());
    TestEqual(
        TEXT("Reloaded generation is visible"),
        FirstComponent->ActiveGeneration,
        FString(TEXT("2")));
    TestEqual(
        TEXT("Reload preserves compatible state override by semantic ID"),
        FirstComponent->ManagedState,
        FString(TEXT("Started:37")));

    UUnifiedScriptAsset* InvalidScriptAsset =
        NewObject<UUnifiedScriptAsset>(FirstWorld);
    InvalidScriptAsset->ScriptAssemblyPath = InitialScriptAsset->ScriptAssemblyPath;
    InvalidScriptAsset->ScriptTypeName = TEXT("UnifiedScript.Unreal.Demo.MissingScript");
    FirstComponent->ScriptAsset = InvalidScriptAsset;
    AddExpectedError(
        TEXT("UnifiedScript component: Managed script reload failed with status -5."),
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
    TestTrue(
        TEXT("Failed Script Asset reload exposes an error"),
        !FirstComponent->LastScriptError.IsEmpty());

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
        TEXT("UnifiedScript demo completed: DeclarationState=37, ScriptAsset Start/Tick/Reload/Stop, Generation=3, Add(20, 22)=42, FastCalls=%llu, CallFrameCalls=%llu, LegacyCalls=%llu"),
        static_cast<unsigned long long>(FirstHost->GetFastInvocationCount()),
        static_cast<unsigned long long>(ManagedCallFrameCalls),
        static_cast<unsigned long long>(ManagedLegacyCalls));

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
    FUnifiedScriptManagedInteropPerformanceTest,
    "UnifiedScript.Unreal.Performance.ManagedInterop",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptManagedInteropPerformanceTest::RunTest(const FString& Parameters)
{
    constexpr int32 Iterations = 100000;
    constexpr int32 WarmupIterations = 4096;

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
    double ManagedNanosecondsPerCall = 0.0;
    int64 AllocatedBytes = -1;
    int32 ManagedIterations = 0;
    const bool bParsed = Component->ManagedState.StartsWith(TEXT("Benchmark:v1;")) &&
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
        LexTryParseString(ManagedNanosecondsPerCall, *ManagedNanosecondsText) &&
        LexTryParseString(AllocatedBytes, *AllocatedBytesText) &&
        LexTryParseString(ManagedIterations, *ManagedIterationsText);
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
    const double NativeStarted = FPlatformTime::Seconds();
    for (int32 Index = 0; Index < Iterations; ++Index)
    {
        NativeChecksum += Component->Add(Index, 1);
    }
    const double NativeElapsedSeconds = FPlatformTime::Seconds() - NativeStarted;
    const double NativeNanosecondsPerCall =
        NativeElapsedSeconds * 1000000000.0 / Iterations;

    TestEqual(TEXT("Managed benchmark iteration count is stable"), ManagedIterations, Iterations);
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
        TEXT("UNIFIEDSCRIPT_BENCHMARK_JSON {\"schemaVersion\":\"unifiedscript.unreal-benchmark/v0.1\",\"scenario\":\"generated-int32-add\",\"configuration\":\"Development Editor\",\"iterations\":%d,\"cppDirectNsPerCall\":%.3f,\"unifiedScriptNsPerCall\":%.3f,\"unifiedScriptAllocatedBytes\":%lld,\"puertsNsPerCall\":null}"),
        Iterations,
        NativeNanosecondsPerCall,
        ManagedNanosecondsPerCall,
        static_cast<long long>(AllocatedBytes));

    Component->EndPlay(EEndPlayReason::Destroyed);
    Component->UnregisterComponent();
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}

#endif
