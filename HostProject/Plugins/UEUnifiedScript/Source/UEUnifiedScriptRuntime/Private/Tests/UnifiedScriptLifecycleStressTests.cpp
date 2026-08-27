#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptAsyncService.h"
#include "UnifiedScriptNativeTransport.h"
#include "UnifiedScriptWorldSubsystem.h"

#include "Containers/StringConv.h"
#include "Engine/World.h"
#include "HAL/PlatformProperties.h"
#include "Misc/AutomationTest.h"
#include "Misc/EngineVersion.h"
#include "UnifiedScriptActor.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptCoreClrHost.h"

namespace UE::UnifiedScript::LifecycleStress::Tests
{
struct FWorldFixture
{
    UWorld* World = nullptr;
    UUnifiedScriptWorldSubsystem* Host = nullptr;
    FUnifiedScriptNativeBridgeContext* Context = nullptr;
    const FUnifiedScriptAsyncTransportV5* Async = nullptr;
};

FWorldFixture CreateWorld(EWorldType::Type WorldType)
{
    FWorldFixture Fixture;
    Fixture.World = UWorld::CreateWorld(WorldType, false);
    if (Fixture.World == nullptr)
    {
        return Fixture;
    }
    Fixture.World->AddToRoot();
    Fixture.Host = Fixture.World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    if (Fixture.Host == nullptr)
    {
        return Fixture;
    }
    Fixture.Context = Fixture.Host->GetNativeInvocationContext();
    const FUnifiedScriptNativeTransportV2* Bootstrap = Fixture.Host->GetNativeTransport();
    Fixture.Async = Bootstrap != nullptr ? Bootstrap->AsyncTransport : nullptr;
    return Fixture;
}

void DestroyWorld(FWorldFixture& Fixture)
{
    if (Fixture.World != nullptr)
    {
        Fixture.World->RemoveFromRoot();
        Fixture.World->DestroyWorld(false);
    }
    Fixture = {};
}

bool IsReady(const FWorldFixture& Fixture)
{
    return Fixture.World != nullptr && Fixture.Host != nullptr &&
        Fixture.Context != nullptr && Fixture.Async != nullptr &&
        Fixture.Host->IsManagedWorldReady();
}

bool Submit(
    const FWorldFixture& Fixture,
    uint64 RequestId,
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration,
    EUnifiedScriptAsyncOperationV5 Operation,
    float DurationSeconds,
    const FString& Payload = FString())
{
    const FTCHARToUTF8 PayloadUtf8(*Payload);
    FUnifiedScriptAsyncRequestV5 Request;
    Request.RequestId = RequestId;
    Request.OwnerInstanceId = OwnerInstanceId;
    Request.OwnerGeneration = OwnerGeneration;
    Request.Operation = Operation;
    Request.DurationSeconds = DurationSeconds;
    Request.TimeoutSeconds = 30.0f;
    Request.PayloadData = reinterpret_cast<const uint8*>(PayloadUtf8.Get());
    Request.PayloadLength = static_cast<uint32>(PayloadUtf8.Length());
    return Fixture.Async->SubmitRequest(Fixture.Context, &Request) != 0;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptLifecycleStressTest,
    "UnifiedScript.Unreal.WorldHost.LifecycleStress",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptLifecycleStressTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::LifecycleStress::Tests;

    constexpr int32 TravelCycles = 8;
    constexpr int32 PieCycles = 8;
    constexpr int32 ConcurrentWorlds = 4;
    constexpr int32 CollidingRequestsPerWorld = 8;
    constexpr int32 GenerationRollovers = 16;
    constexpr uint64 OwnerInstanceId = 9001;
    constexpr uint32 OwnerGeneration = 17;

    FUnifiedScriptCoreClrHost& CoreClr = FUnifiedScriptCoreClrHost::Get();
    const int32 InitialManagedWorlds = CoreClr.GetManagedWorldCount();
    const int32 InitialLiveContexts =
        FUnifiedScriptNativeBridgeContext::GetLiveContextCount();
    const int32 InitialAttachedContexts =
        FUnifiedScriptNativeBridgeContext::GetAttachedContextCount();
    const int32 InitialLiveServices = FUnifiedScriptAsyncService::GetLiveServiceCount();
    const int32 InitialPending =
        FUnifiedScriptAsyncService::GetGlobalPendingRequestCount();
    const int32 InitialCompletions =
        FUnifiedScriptAsyncService::GetGlobalQueuedCompletionCount();
    const int32 InitialOwnedResources =
        FUnifiedScriptAsyncService::GetGlobalOwnedResourceCount();

    const FString ClassPath = UUnifiedScriptComponent::StaticClass()->GetPathName();
    uint64 PreviousTravelWorldId = 0;

    // Non-seamless travel destroys one World host and creates another while
    // pending timer and completed resource work still belong to the old World.
    for (int32 Cycle = 0; Cycle < TravelCycles; ++Cycle)
    {
        FWorldFixture Fixture = CreateWorld(EWorldType::Game);
        TestTrue(TEXT("Travel World fixture is ready"), IsReady(Fixture));
        if (!IsReady(Fixture))
        {
            DestroyWorld(Fixture);
            return false;
        }
        TestTrue(
            TEXT("Travel replacement receives a fresh World identity"),
            Fixture.Host->GetWorldHostId() != PreviousTravelWorldId);
        PreviousTravelWorldId = Fixture.Host->GetWorldHostId();
        TestTrue(
            TEXT("Travel timer request is accepted"),
            Submit(
                Fixture,
                1,
                OwnerInstanceId,
                OwnerGeneration,
                EUnifiedScriptAsyncOperationV5::Delay,
                600.0f));
        TestTrue(
            TEXT("Travel class-load request is accepted"),
            Submit(
                Fixture,
                2,
                OwnerInstanceId,
                OwnerGeneration,
                EUnifiedScriptAsyncOperationV5::LoadClass,
                0.0f,
                ClassPath));
        TestEqual(
            TEXT("Old travel World owns one pending timer"),
            Fixture.Host->GetPendingAsyncRequestCount(),
            1);
        TestEqual(
            TEXT("Old travel World owns one unpolled completion"),
            Fixture.Host->GetQueuedAsyncCompletionCount(),
            1);
        TestEqual(
            TEXT("Old travel generation owns one loaded resource"),
            Fixture.Host->GetOwnedAsyncResourceCount(),
            1);
        TestTrue(
            TEXT("Travel gate retains the old bridge context"),
            Fixture.Async->RetainContext(Fixture.Context) != 0);
        FUnifiedScriptNativeBridgeContext* RetainedContext = Fixture.Context;
        const FUnifiedScriptAsyncTransportV5* RetainedTransport = Fixture.Async;
        DestroyWorld(Fixture);

        FUnifiedScriptAsyncRequestV5 DetachedRequest;
        DetachedRequest.RequestId = 3;
        DetachedRequest.OwnerInstanceId = OwnerInstanceId;
        DetachedRequest.OwnerGeneration = OwnerGeneration;
        TestFalse(
            TEXT("Old travel context rejects callbacks after World teardown"),
            RetainedTransport->SubmitRequest(RetainedContext, &DetachedRequest) != 0);
        RetainedTransport->ReleaseContext(RetainedContext);
        TestEqual(
            TEXT("Travel teardown releases every pending request"),
            FUnifiedScriptAsyncService::GetGlobalPendingRequestCount(),
            InitialPending);
        TestEqual(
            TEXT("Travel teardown releases every queued completion"),
            FUnifiedScriptAsyncService::GetGlobalQueuedCompletionCount(),
            InitialCompletions);
        TestEqual(
            TEXT("Travel teardown releases generation-owned resources"),
            FUnifiedScriptAsyncService::GetGlobalOwnedResourceCount(),
            InitialOwnedResources);
        TestEqual(
            TEXT("Travel teardown releases the retained native context"),
            FUnifiedScriptNativeBridgeContext::GetLiveContextCount(),
            InitialLiveContexts);
    }

    // PIE create/stop/restart uses the same subsystem path but a distinct World
    // type. Repeating it catches delegate, context, and service retention.
    for (int32 Cycle = 0; Cycle < PieCycles; ++Cycle)
    {
        FWorldFixture Fixture = CreateWorld(EWorldType::PIE);
        TestTrue(TEXT("PIE restart World fixture is ready"), IsReady(Fixture));
        if (!IsReady(Fixture))
        {
            DestroyWorld(Fixture);
            return false;
        }
        TestTrue(
            TEXT("PIE timer request is accepted"),
            Submit(
                Fixture,
                1,
                OwnerInstanceId,
                OwnerGeneration,
                EUnifiedScriptAsyncOperationV5::Delay,
                600.0f));
        DestroyWorld(Fixture);
        TestEqual(
            TEXT("PIE restart restores managed World count"),
            CoreClr.GetManagedWorldCount(),
            InitialManagedWorlds);
        TestEqual(
            TEXT("PIE restart restores attached bridge count"),
            FUnifiedScriptNativeBridgeContext::GetAttachedContextCount(),
            InitialAttachedContexts);
        TestEqual(
            TEXT("PIE restart restores async service count"),
            FUnifiedScriptAsyncService::GetLiveServiceCount(),
            InitialLiveServices);
    }

    // Multiple Worlds deliberately reuse every owner and request identifier.
    // Completion/poll buffers must remain isolated by the native context.
    TArray<FWorldFixture> Fixtures;
    TSet<uint64> WorldIds;
    for (int32 WorldIndex = 0; WorldIndex < ConcurrentWorlds; ++WorldIndex)
    {
        FWorldFixture& Fixture = Fixtures.AddDefaulted_GetRef();
        Fixture = CreateWorld(EWorldType::Game);
        TestTrue(TEXT("Concurrent World fixture is ready"), IsReady(Fixture));
        if (!IsReady(Fixture))
        {
            for (FWorldFixture& Cleanup : Fixtures)
            {
                DestroyWorld(Cleanup);
            }
            return false;
        }
        WorldIds.Add(Fixture.Host->GetWorldHostId());
        for (int32 RequestIndex = 0;
             RequestIndex < CollidingRequestsPerWorld;
             ++RequestIndex)
        {
            TestTrue(
                TEXT("Colliding multi-World request is accepted"),
                Submit(
                    Fixture,
                    static_cast<uint64>(RequestIndex + 1),
                    OwnerInstanceId,
                    OwnerGeneration,
                    EUnifiedScriptAsyncOperationV5::Delay,
                    0.5f));
        }
    }
    TestEqual(
        TEXT("Concurrent Worlds publish distinct host identities"),
        WorldIds.Num(),
        ConcurrentWorlds);
    TestEqual(
        TEXT("Colliding requests remain independently pending"),
        FUnifiedScriptAsyncService::GetGlobalPendingRequestCount(),
        InitialPending + ConcurrentWorlds * CollidingRequestsPerWorld);

    FUnifiedScriptAsyncCompletionViewV5 CompletionView;
    TestTrue(
        TEXT("First concurrent World advances its owner"),
        Fixtures[0].Async->PumpOwner(
            Fixtures[0].Context,
            OwnerInstanceId,
            OwnerGeneration,
            0.5f) != 0);
    TestTrue(
        TEXT("First concurrent World polls its completions"),
        Fixtures[0].Async->PollCompletions(
            Fixtures[0].Context,
            OwnerInstanceId,
            OwnerGeneration,
            &CompletionView) != 0);
    TestEqual(
        TEXT("First concurrent World receives every local completion"),
        CompletionView.Count,
        static_cast<uint32>(CollidingRequestsPerWorld));
    TestTrue(
        TEXT("Second concurrent World polls independently"),
        Fixtures[1].Async->PollCompletions(
            Fixtures[1].Context,
            OwnerInstanceId,
            OwnerGeneration,
            &CompletionView) != 0);
    TestEqual(
        TEXT("No completion aliases into a second World"),
        CompletionView.Count,
        0u);

    for (int32 WorldIndex = 0; WorldIndex < Fixtures.Num(); ++WorldIndex)
    {
        const uint32 ExpectedCanceled = WorldIndex == 0
            ? 0u
            : static_cast<uint32>(CollidingRequestsPerWorld);
        TestEqual(
            TEXT("Concurrent World cancels only its own remaining requests"),
            Fixtures[WorldIndex].Async->CancelOwner(
                Fixtures[WorldIndex].Context,
                OwnerInstanceId,
                OwnerGeneration),
            ExpectedCanceled);
        DestroyWorld(Fixtures[WorldIndex]);
    }
    Fixtures.Reset();

    // A real managed Actor generation owns both async operations. Repeated
    // reload proves old completions/resources are removed before the next ALC.
    FWorldFixture ManagedFixture = CreateWorld(EWorldType::Game);
    TestTrue(TEXT("Managed rollover World fixture is ready"), IsReady(ManagedFixture));
    if (!IsReady(ManagedFixture))
    {
        DestroyWorld(ManagedFixture);
        return false;
    }
    AUnifiedScriptActor* Actor = ManagedFixture.World->SpawnActor<AUnifiedScriptActor>();
    TestNotNull(TEXT("Lifecycle stress Actor exists"), Actor);
    if (Actor == nullptr || Actor->ScriptComponent == nullptr)
    {
        DestroyWorld(ManagedFixture);
        return false;
    }
    UUnifiedScriptAsset* Asset = NewObject<UUnifiedScriptAsset>(ManagedFixture.World);
    Asset->ScriptAssemblyPath =
        TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll");
    Asset->ScriptTypeName = TEXT("UnifiedScript.Unreal.Demo.LifecycleStressScript");
    UUnifiedScriptComponent* Component = Actor->ScriptComponent;
    Component->ScriptAsset = Asset;
    Component->BeginPlay();
    TestTrue(
        *FString::Printf(
            TEXT("Lifecycle stress script starts: %s"),
            *Component->LastScriptError),
        Component->bScriptRunning);
    TestEqual(
        TEXT("Initial stress generation owns one timer"),
        ManagedFixture.Host->GetPendingAsyncRequestCount(),
        1);
    TestEqual(
        TEXT("Initial stress generation owns one class completion"),
        ManagedFixture.Host->GetQueuedAsyncCompletionCount(),
        1);
    TestEqual(
        TEXT("Initial stress generation owns one resource handle"),
        ManagedFixture.Host->GetOwnedAsyncResourceCount(),
        1);

    for (int32 Rollover = 0; Rollover <= GenerationRollovers; ++Rollover)
    {
        const int32 ExpectedGeneration = Rollover + 1;
        if (Rollover > 0)
        {
            TestTrue(
                TEXT("Managed lifecycle generation reload succeeds"),
                Component->ReloadScript());
        }
        TestEqual(
            TEXT("Generation rollover leaves one current timer"),
            ManagedFixture.Host->GetPendingAsyncRequestCount(),
            1);
        TestEqual(
            TEXT("Generation rollover leaves one current completion"),
            ManagedFixture.Host->GetQueuedAsyncCompletionCount(),
            1);
        TestEqual(
            TEXT("Generation rollover leaves one current resource ownership"),
            ManagedFixture.Host->GetOwnedAsyncResourceCount(),
            1);
        Component->TickComponent(0.0f, LEVELTICK_All, nullptr);
        TestEqual(
            TEXT("Only the current generation receives its class-load callback"),
            Component->ManagedState,
            FString::Printf(TEXT("StressLoad:%d"), ExpectedGeneration));
        TestEqual(
            TEXT("Current completion is consumed exactly once"),
            ManagedFixture.Host->GetQueuedAsyncCompletionCount(),
            0);
        TestEqual(
            TEXT("Collectible reload does not accumulate registered objects"),
            ManagedFixture.Host->NumRegisteredObjects(),
            3);
    }

    Component->EndPlay(EEndPlayReason::Destroyed);
    TestEqual(
        TEXT("Target deletion cancels its pending timer"),
        ManagedFixture.Host->GetPendingAsyncRequestCount(),
        0);
    TestEqual(
        TEXT("Target deletion leaves no stale completion"),
        ManagedFixture.Host->GetQueuedAsyncCompletionCount(),
        0);
    TestEqual(
        TEXT("Target deletion releases its async resource ownership"),
        ManagedFixture.Host->GetOwnedAsyncResourceCount(),
        0);
    TestEqual(
        TEXT("Target deletion removes the managed script entry"),
        ManagedFixture.Host->GetManagedScriptCount(),
        0);
    TestEqual(
        TEXT("Target deletion releases every registered object"),
        ManagedFixture.Host->NumRegisteredObjects(),
        0);
    TestEqual(
        TEXT("Managed EndPlay reports the final generation"),
        Component->ManagedState,
        FString::Printf(TEXT("StressEnd:%d"), GenerationRollovers + 1));
    TWeakObjectPtr<AUnifiedScriptActor> DeletedActor = Actor;
    Actor->Destroy();
    Actor = nullptr;
    TestFalse(TEXT("Deleted request target is no longer valid"), DeletedActor.IsValid());
    DestroyWorld(ManagedFixture);

    const int32 RetiredLoadContextsAlive =
        CoreClr.CollectRetiredManagedLoadContexts();
    TestEqual(
        TEXT("Every retired collectible script context is reclaimed"),
        RetiredLoadContextsAlive,
        0);
    TestEqual(
        TEXT("Lifecycle matrix restores managed World count"),
        CoreClr.GetManagedWorldCount(),
        InitialManagedWorlds);
    TestEqual(
        TEXT("Lifecycle matrix restores native context count"),
        FUnifiedScriptNativeBridgeContext::GetLiveContextCount(),
        InitialLiveContexts);
    TestEqual(
        TEXT("Lifecycle matrix restores attached context count"),
        FUnifiedScriptNativeBridgeContext::GetAttachedContextCount(),
        InitialAttachedContexts);
    TestEqual(
        TEXT("Lifecycle matrix restores async service count"),
        FUnifiedScriptAsyncService::GetLiveServiceCount(),
        InitialLiveServices);
    TestEqual(
        TEXT("Lifecycle matrix leaves no native pending request"),
        FUnifiedScriptAsyncService::GetGlobalPendingRequestCount(),
        InitialPending);
    TestEqual(
        TEXT("Lifecycle matrix leaves no native completion"),
        FUnifiedScriptAsyncService::GetGlobalQueuedCompletionCount(),
        InitialCompletions);
    TestEqual(
        TEXT("Lifecycle matrix leaves no generation-owned resource"),
        FUnifiedScriptAsyncService::GetGlobalOwnedResourceCount(),
        InitialOwnedResources);

    UE_LOG(
        LogTemp,
        Display,
        TEXT("UNIFIEDSCRIPT_LIFECYCLE_STRESS_JSON {\"schemaVersion\":\"unifiedscript.unreal-lifecycle-stress/v0.1\",\"platform\":\"%s\",\"engineMajor\":%d,\"engineMinor\":%d,\"enginePatch\":%d,\"travelCycles\":%d,\"pieCycles\":%d,\"concurrentWorlds\":%d,\"collidingRequestsPerWorld\":%d,\"generationRollovers\":%d,\"staleCompletions\":0,\"pendingRequestsDelta\":0,\"managedWorldsDelta\":0,\"nativeContextsDelta\":0,\"ownedResourcesDelta\":0,\"retiredLoadContextsAlive\":%d}"),
        ANSI_TO_TCHAR(FPlatformProperties::PlatformName()),
        FEngineVersion::Current().GetMajor(),
        FEngineVersion::Current().GetMinor(),
        FEngineVersion::Current().GetPatch(),
        TravelCycles,
        PieCycles,
        ConcurrentWorlds,
        CollidingRequestsPerWorld,
        GenerationRollovers,
        RetiredLoadContextsAlive);
    return true;
}

#endif
