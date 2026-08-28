#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptAsyncService.h"
#include "UnifiedScriptNativeTransport.h"
#include "UnifiedScriptRuntimeDiagnostic.h"
#include "UnifiedScriptWorldSubsystem.h"

#include "Containers/StringConv.h"
#include "Components/SceneComponent.h"
#include "Engine/World.h"
#include "HAL/PlatformMemory.h"
#include "HAL/PlatformProperties.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/EngineVersion.h"
#include "Misc/Parse.h"
#include "UnifiedScriptActor.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UObject/GarbageCollection.h"

namespace UE::UnifiedScript::LifecycleStress::Tests
{
struct FWorldFixture
{
    UWorld* World = nullptr;
    UUnifiedScriptWorldSubsystem* Host = nullptr;
    FUnifiedScriptNativeBridgeContext* Context = nullptr;
    const FUnifiedScriptGameplayDataTransportV4* Gameplay = nullptr;
    const FUnifiedScriptAsyncTransportV5* Async = nullptr;
};

int32 GVerifiedShutdownSequenceCount = 0;
int32 GShutdownSequenceFailureCount = 0;

bool HasDeterministicShutdownSequence(uint64 WorldHostId)
{
    static const TArray<FString> ExpectedOperations = {
        TEXT("WorldShutdown.Begin"),
        TEXT("WorldShutdown.WorkerDispatchSuspended"),
        TEXT("WorldShutdown.ManagedWorldDestroyed"),
        TEXT("WorldShutdown.NativeContextDetached"),
        TEXT("WorldShutdown.ServicesStopped"),
        TEXT("WorldShutdown.HandlesInvalidated"),
        TEXT("WorldShutdown.Complete"),
    };
    const FUnifiedScriptRuntimeBreadcrumbSnapshot Snapshot =
        FUnifiedScriptRuntimeBreadcrumbs::Capture();
    int32 ExpectedIndex = 0;
    for (const FUnifiedScriptRuntimeBreadcrumb& Breadcrumb : Snapshot.Entries)
    {
        if (Breadcrumb.WorldHostId == WorldHostId &&
            ExpectedIndex < ExpectedOperations.Num() &&
            Breadcrumb.Operation == ExpectedOperations[ExpectedIndex])
        {
            ++ExpectedIndex;
        }
    }
    return ExpectedIndex == ExpectedOperations.Num();
}

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
    Fixture.Gameplay = Bootstrap != nullptr ? Bootstrap->GameplayDataTransport : nullptr;
    Fixture.Async = Bootstrap != nullptr ? Bootstrap->AsyncTransport : nullptr;
    return Fixture;
}

void DestroyWorld(FWorldFixture& Fixture)
{
    const uint64 WorldHostId =
        Fixture.Host != nullptr ? Fixture.Host->GetWorldHostId() : 0;
    if (Fixture.World != nullptr)
    {
        Fixture.World->RemoveFromRoot();
        Fixture.World->DestroyWorld(false);
    }
    Fixture = {};
    if (WorldHostId != 0)
    {
        ++GVerifiedShutdownSequenceCount;
        if (!HasDeterministicShutdownSequence(WorldHostId))
        {
            ++GShutdownSequenceFailureCount;
        }
    }
}

bool IsReady(const FWorldFixture& Fixture)
{
    return Fixture.World != nullptr && Fixture.Host != nullptr &&
        Fixture.Context != nullptr && Fixture.Gameplay != nullptr &&
        Fixture.Async != nullptr &&
        Fixture.Host->IsManagedWorldReady();
}

int32 ReadBoundedOption(
    const TCHAR* Key,
    int32 DefaultValue,
    int32 MinimumValue,
    int32 MaximumValue)
{
    int32 Value = DefaultValue;
    FParse::Value(FCommandLine::Get(), Key, Value);
    return FMath::Clamp(Value, MinimumValue, MaximumValue);
}

uint64 SampleUsedPhysicalBytes(uint64& InOutPeak)
{
    const uint64 UsedPhysical = FPlatformMemory::GetStats().UsedPhysical;
    InOutPeak = FMath::Max(InOutPeak, UsedPhysical);
    return UsedPhysical;
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
    EAutomationTestFlags::EditorContext |
        EAutomationTestFlags::ServerContext |
        EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptLifecycleStressTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::LifecycleStress::Tests;

    FUnifiedScriptRuntimeBreadcrumbs::ResetForTests();
    GVerifiedShutdownSequenceCount = 0;
    GShutdownSequenceFailureCount = 0;

    const int32 TravelCycles = ReadBoundedOption(
        TEXT("UnifiedScriptSoakTravelCycles="), 8, 1, 256);
    const int32 PieCycles = ReadBoundedOption(
        TEXT("UnifiedScriptSoakPieCycles="), 8, 1, 256);
    const int32 ConcurrentWorlds = ReadBoundedOption(
        TEXT("UnifiedScriptSoakConcurrentWorlds="), 4, 2, 32);
    const int32 CollidingRequestsPerWorld = ReadBoundedOption(
        TEXT("UnifiedScriptSoakRequestsPerWorld="), 8, 1, 1024);
    const int32 GenerationRollovers = ReadBoundedOption(
        TEXT("UnifiedScriptSoakGenerationRollovers="), 16, 1, 512);
    const int32 FailedReloadCycles = GenerationRollovers;
    const int32 ObjectChurnCycles = ReadBoundedOption(
        TEXT("UnifiedScriptSoakObjectChurnCycles="), 2, 1, 128);
    const int32 ObjectsPerChurnCycle = ReadBoundedOption(
        TEXT("UnifiedScriptSoakObjectsPerChurnCycle="), 128, 1, 4096);
    const int32 CommandSaturationCycles = ReadBoundedOption(
        TEXT("UnifiedScriptSoakCommandSaturationCycles="), 1, 1, 64);
    const int32 CommandsPerSaturationCycle = ReadBoundedOption(
        TEXT("UnifiedScriptSoakCommandsPerSaturationCycle="),
        static_cast<int32>(UE::UnifiedScript::GameplayData::MaximumEventCount + 1024),
        static_cast<int32>(UE::UnifiedScript::GameplayData::MaximumEventCount + 1),
        static_cast<int32>(UE::UnifiedScript::GameplayData::MaximumCommandCount));
    const int32 MemoryGrowthBudgetMegabytes = ReadBoundedOption(
        TEXT("UnifiedScriptSoakMemoryGrowthBudgetMB="), 512, 64, 4096);
    const uint64 MemoryGrowthBudgetBytes =
        static_cast<uint64>(MemoryGrowthBudgetMegabytes) * 1024ull * 1024ull;
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
    const uint64 InitialAsyncCapacityRejections =
        FUnifiedScriptAsyncService::GetGlobalCapacityRejectionCount();
    const uint64 InitialAsyncPayloadRejections =
        FUnifiedScriptAsyncService::GetGlobalPayloadRejectionCount();
    const int32 InitialPendingWorkerDispatch =
        FUnifiedScriptNativeBridgeContext::GetGlobalPendingWorkerDispatchCount();
    const uint64 InitialRejectedWorkerDispatch =
        FUnifiedScriptNativeBridgeContext::GetGlobalRejectedWorkerDispatchCount();
    uint64 PeakUsedPhysicalBytes = 0;
    const uint64 InitialUsedPhysicalBytes =
        SampleUsedPhysicalBytes(PeakUsedPhysicalBytes);
    int32 GarbageCollectionCycles = 0;

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
    SampleUsedPhysicalBytes(PeakUsedPhysicalBytes);

    // Repeated weak-handle registration and UE GC prove that registry slots do
    // not retain transient UObject graphs or revive a recycled generation.
    int32 ObjectRegistrationCount = 0;
    FWorldFixture ChurnFixture = CreateWorld(EWorldType::Game);
    TestTrue(TEXT("Object churn World fixture is ready"), IsReady(ChurnFixture));
    if (!IsReady(ChurnFixture))
    {
        DestroyWorld(ChurnFixture);
        return false;
    }
    for (int32 Cycle = 0; Cycle < ObjectChurnCycles; ++Cycle)
    {
        TArray<TWeakObjectPtr<USceneComponent>> WeakObjects;
        TArray<FUnifiedScriptObjectHandle> Handles;
        WeakObjects.Reserve(ObjectsPerChurnCycle);
        Handles.Reserve(ObjectsPerChurnCycle);
        bool bRegisteredEveryObject = true;
        for (int32 ObjectIndex = 0;
             ObjectIndex < ObjectsPerChurnCycle;
             ++ObjectIndex)
        {
            USceneComponent* Object = NewObject<USceneComponent>(ChurnFixture.World);
            FUnifiedScriptObjectHandleResult Result;
            const FUnifiedScriptObjectHandle Handle =
                ChurnFixture.Host->RegisterObject(Object, Result);
            bRegisteredEveryObject &= Result.IsSuccess() && !Handle.IsNull();
            WeakObjects.Add(Object);
            Handles.Add(Handle);
        }
        TestTrue(
            TEXT("Object churn registers every transient World object"),
            bRegisteredEveryObject);
        ObjectRegistrationCount += Handles.Num();

        bool bReleasedEveryObject = true;
        for (const FUnifiedScriptObjectHandle Handle : Handles)
        {
            FUnifiedScriptObjectHandleResult Result;
            bReleasedEveryObject &=
                ChurnFixture.Host->ReleaseObject(Handle, Result) &&
                Result.IsSuccess();
        }
        TestTrue(
            TEXT("Object churn releases every transient handle"),
            bReleasedEveryObject);
        TestEqual(
            TEXT("Object churn returns the registry to zero before GC"),
            ChurnFixture.Host->NumRegisteredObjects(),
            0);
        Handles.Reset();
        CollectGarbage(RF_NoFlags);
        ++GarbageCollectionCycles;
        TestFalse(
            TEXT("UE GC reclaims the first released churn object"),
            WeakObjects.IsEmpty() || WeakObjects[0].IsValid());
        SampleUsedPhysicalBytes(PeakUsedPhysicalBytes);
    }
    DestroyWorld(ChurnFixture);

    // The async service accepts exactly its declared in-flight capacity,
    // rejects one more request and an oversized payload, then drains without
    // leaving work owned by the World.
    FWorldFixture AsyncCapacityFixture = CreateWorld(EWorldType::Game);
    TestTrue(
        TEXT("Async saturation World fixture is ready"),
        IsReady(AsyncCapacityFixture));
    if (!IsReady(AsyncCapacityFixture))
    {
        DestroyWorld(AsyncCapacityFixture);
        return false;
    }
    bool bAcceptedAsyncCapacity = true;
    for (uint32 Index = 0;
         Index < UE::UnifiedScript::AsyncService::MaximumInFlightRequestCount;
         ++Index)
    {
        bAcceptedAsyncCapacity &= Submit(
            AsyncCapacityFixture,
            static_cast<uint64>(Index + 1),
            OwnerInstanceId,
            OwnerGeneration,
            EUnifiedScriptAsyncOperationV5::Delay,
            600.0f);
    }
    TestTrue(
        TEXT("Async saturation reaches the declared capacity"),
        bAcceptedAsyncCapacity);
    TestEqual(
        TEXT("Async pending storage remains at the declared capacity"),
        AsyncCapacityFixture.Host->GetPendingAsyncRequestCount(),
        static_cast<int32>(
            UE::UnifiedScript::AsyncService::MaximumInFlightRequestCount));
    TestFalse(
        TEXT("Async saturation rejects capacity plus one"),
        Submit(
            AsyncCapacityFixture,
            UE::UnifiedScript::AsyncService::MaximumInFlightRequestCount + 1ull,
            OwnerInstanceId,
            OwnerGeneration,
            EUnifiedScriptAsyncOperationV5::Delay,
            600.0f));
    TestEqual(
        TEXT("Async saturation drains deterministically"),
        AsyncCapacityFixture.Async->CancelOwner(
            AsyncCapacityFixture.Context,
            OwnerInstanceId,
            OwnerGeneration),
        UE::UnifiedScript::AsyncService::MaximumInFlightRequestCount);
    const uint8 OversizedPayloadSentinel = 'x';
    FUnifiedScriptAsyncRequestV5 OversizedPayloadRequest;
    OversizedPayloadRequest.RequestId = 1;
    OversizedPayloadRequest.OwnerInstanceId = OwnerInstanceId;
    OversizedPayloadRequest.OwnerGeneration = OwnerGeneration;
    OversizedPayloadRequest.Operation = EUnifiedScriptAsyncOperationV5::LoadObject;
    OversizedPayloadRequest.PayloadData = &OversizedPayloadSentinel;
    OversizedPayloadRequest.PayloadLength =
        UE::UnifiedScript::AsyncService::MaximumPayloadByteCount + 1;
    TestFalse(
        TEXT("Async saturation rejects an oversized payload"),
        AsyncCapacityFixture.Async->SubmitRequest(
            AsyncCapacityFixture.Context,
            &OversizedPayloadRequest) != 0);
    TestEqual(
        TEXT("Async saturation leaves no pending request"),
        AsyncCapacityFixture.Host->GetPendingAsyncRequestCount(),
        0);
    DestroyWorld(AsyncCapacityFixture);
    SampleUsedPhysicalBytes(PeakUsedPhysicalBytes);

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
    SampleUsedPhysicalBytes(PeakUsedPhysicalBytes);

    // Saturate command completion events and every oversized reservation path.
    // The frame scheduler must publish at most the declared event capacity and
    // remain usable on every following frame.
    FWorldFixture SaturationFixture = CreateWorld(EWorldType::Game);
    TestTrue(
        TEXT("Gameplay saturation World fixture is ready"),
        IsReady(SaturationFixture));
    if (!IsReady(SaturationFixture))
    {
        DestroyWorld(SaturationFixture);
        return false;
    }
    SaturationFixture.Host->BeginManagedFrame();
    FUnifiedScriptCommandBatchViewV4 OversizedCommandView;
    TestFalse(
        TEXT("Command buffer rejects capacity plus one"),
        SaturationFixture.Gameplay->BeginCommandBatch(
            SaturationFixture.Context,
            UE::UnifiedScript::GameplayData::MaximumCommandCount + 1,
            &OversizedCommandView) != 0);
    FUnifiedScriptTraceRequestViewV4 OversizedTraceView;
    TestFalse(
        TEXT("Trace buffer rejects capacity plus one"),
        SaturationFixture.Gameplay->BeginTraceBatch(
            SaturationFixture.Context,
            UE::UnifiedScript::GameplayData::MaximumTraceCount + 1,
            &OversizedTraceView) != 0);
    FUnifiedScriptOverlapRequestViewV1 OversizedOverlapView;
    TestFalse(
        TEXT("Overlap buffer rejects capacity plus one"),
        SaturationFixture.Gameplay->BeginOverlapBatch(
            SaturationFixture.Context,
            UE::UnifiedScript::GameplayData::MaximumOverlapCount + 1,
            &OversizedOverlapView) != 0);
    SaturationFixture.Host->EndManagedFrame();

    uint64 PublishedSaturationEventCount = 0;
    for (int32 Cycle = 0; Cycle < CommandSaturationCycles; ++Cycle)
    {
        SaturationFixture.Host->BeginManagedFrame();
        FUnifiedScriptCommandBatchViewV4 CommandView;
        TestTrue(
            TEXT("Saturation command batch reservation succeeds"),
            SaturationFixture.Gameplay->BeginCommandBatch(
                SaturationFixture.Context,
                static_cast<uint32>(CommandsPerSaturationCycle),
                &CommandView) != 0);
        TestTrue(
            TEXT("Saturation command batch commit succeeds"),
            SaturationFixture.Gameplay->CommitCommandBatch(
                SaturationFixture.Context,
                CommandView.BatchToken,
                static_cast<uint32>(CommandsPerSaturationCycle)) != 0);
        SaturationFixture.Host->EndManagedFrame();

        SaturationFixture.Host->BeginManagedFrame();
        FUnifiedScriptGameplayEventViewV4 EventView;
        TestTrue(
            TEXT("Saturation completion events remain readable"),
            SaturationFixture.Gameplay->AcquireEvents(
                SaturationFixture.Context,
                &EventView) != 0);
        TestEqual(
            TEXT("Saturation publishes exactly the bounded event capacity"),
            EventView.EventCount,
            UE::UnifiedScript::GameplayData::MaximumEventCount);
        TestTrue(
            TEXT("Published event storage never exceeds its declared capacity"),
            EventView.EventCount <= EventView.EventCapacity &&
                EventView.EventCapacity <=
                    UE::UnifiedScript::GameplayData::MaximumEventCount);
        PublishedSaturationEventCount += EventView.EventCount;
        SaturationFixture.Host->EndManagedFrame();
        SampleUsedPhysicalBytes(PeakUsedPhysicalBytes);
    }

    FUnifiedScriptGameplayDataTelemetryV1 SaturationTelemetry;
    TestTrue(
        TEXT("Gameplay saturation telemetry is readable"),
        SaturationFixture.Gameplay->GetTelemetry(
            SaturationFixture.Context,
            &SaturationTelemetry,
            sizeof(SaturationTelemetry)) != 0);
    const uint64 ExpectedDroppedEvents = static_cast<uint64>(
        CommandsPerSaturationCycle -
        static_cast<int32>(UE::UnifiedScript::GameplayData::MaximumEventCount)) *
        static_cast<uint64>(CommandSaturationCycles);
    TestEqual(
        TEXT("Command capacity rejection is counted exactly once"),
        SaturationTelemetry.CommandOverflowCount,
        1ull);
    TestEqual(
        TEXT("Trace capacity rejection is counted exactly once"),
        SaturationTelemetry.TraceOverflowCount,
        1ull);
    TestEqual(
        TEXT("Overlap capacity rejection is counted exactly once"),
        SaturationTelemetry.OverlapOverflowCount,
        1ull);
    TestEqual(
        TEXT("Every event beyond the bounded buffer is counted"),
        SaturationTelemetry.EventOverflowCount,
        ExpectedDroppedEvents);
    TestEqual(
        TEXT("Every saturated command is executed exactly once"),
        SaturationTelemetry.TotalExecutedCommandCount,
        static_cast<uint64>(CommandsPerSaturationCycle) *
            static_cast<uint64>(CommandSaturationCycles));
    DestroyWorld(SaturationFixture);

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
    TestEqual(
        TEXT("Initial stress generation owns one reflected event subscription"),
        ManagedFixture.Host->GetEventSubscriptionCount(),
        1);

    AddExpectedError(
        TEXT("UnifiedScript component: managedException during ReloadScript:"),
        EAutomationExpectedErrorFlags::Contains,
        FailedReloadCycles);

    for (int32 Rollover = 0; Rollover <= GenerationRollovers; ++Rollover)
    {
        const int32 ExpectedGeneration = Rollover + 1;
        if (Rollover > 0)
        {
            const FString PreviousState = Component->ManagedState;
            TestTrue(
                TEXT("A pending frame-boundary debug session can be drained by reload"),
                Component->PauseAtDebugFrameBoundary());
            Asset->ScriptTypeName = TEXT("UnifiedScript.Unreal.Demo.MissingScript");
            TestFalse(
                TEXT("Managed lifecycle rejected-candidate reload rolls back"),
                Component->ReloadScript());
            TestTrue(
                TEXT("Rejected reload leaves the previous generation running"),
                Component->bScriptRunning);
            TestEqual(
                TEXT("Rejected reload does not advance the active generation"),
                Component->ActiveGeneration,
                FString::FromInt(ExpectedGeneration - 1));
            TestEqual(
                TEXT("Rejected reload preserves the previous managed state"),
                Component->ManagedState,
                PreviousState);
            TestFalse(
                TEXT("Rejected reload does not retain a stale frame-boundary pause"),
                Component->IsPausedAtDebugFrameBoundary());
            FUnifiedScriptRuntimeDebugContext FailedReloadContext;
            TestTrue(
                TEXT("Rejected reload retains an inspectable runtime context"),
                Component->GetRuntimeDebugContext(FailedReloadContext));
            TestFalse(
                TEXT("Rejected reload leaves no stale semantic debug attachment"),
                FailedReloadContext.bSemanticDebugEnabled ||
                    FailedReloadContext.bSemanticDebugActive ||
                    FailedReloadContext.bSemanticDebugPaused);
            TestTrue(
                TEXT("Rejected reload reports one rolled-back cross-boundary transaction"),
                Component->LastReloadTransaction.Contains(TEXT("\"state\":\"rolledBack\"")) &&
                    Component->LastReloadTransaction.Contains(TEXT("\"generationLoad\"")) &&
                    Component->LastReloadTransaction.Contains(TEXT("\"stateRollback\"")) &&
                    Component->LastReloadTransaction.Contains(TEXT("\"oldContextCollection\"")) &&
                    Component->LastReloadTransaction.Contains(
                        TEXT("\"retiredLoadContextsAlive\":0")));
            TestEqual(
                TEXT("Rejected reload preserves the one live event subscription"),
                ManagedFixture.Host->GetEventSubscriptionCount(),
                1);
            TestEqual(
                TEXT("Rejected reload preserves the current timer"),
                ManagedFixture.Host->GetPendingAsyncRequestCount(),
                1);
            TestEqual(
                TEXT("Rejected reload preserves the current resource ownership"),
                ManagedFixture.Host->GetOwnedAsyncResourceCount(),
                1);

            Asset->ScriptTypeName =
                TEXT("UnifiedScript.Unreal.Demo.LifecycleStressScript");
            TestTrue(
                TEXT("Managed lifecycle generation reload succeeds"),
                Component->ReloadScript());
            TestTrue(
                TEXT("Successful reload reports every runtime transaction phase"),
                Component->LastReloadTransaction.Contains(TEXT("\"state\":\"committed\"")) &&
                    Component->LastReloadTransaction.Contains(TEXT("\"stateMigration\"")) &&
                    Component->LastReloadTransaction.Contains(TEXT("\"oldGenerationStop\"")) &&
                    Component->LastReloadTransaction.Contains(TEXT("\"candidateStart\"")) &&
                    Component->LastReloadTransaction.Contains(TEXT("\"eventRebinding\"")) &&
                    Component->LastReloadTransaction.Contains(TEXT("\"generationSwap\"")) &&
                    Component->LastReloadTransaction.Contains(TEXT("\"oldContextRetirement\"")) &&
                    Component->LastReloadTransaction.Contains(TEXT("\"oldContextCollection\"")) &&
                    Component->LastReloadTransaction.Contains(
                        TEXT("\"retiredLoadContextsAlive\":0")));
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
        TestEqual(
            TEXT("Generation rollover leaves one current event subscription"),
            ManagedFixture.Host->GetEventSubscriptionCount(),
            1);
        Component->BroadcastManagedSignal(ExpectedGeneration * 10, Actor);
        TestEqual(
            TEXT("Only the current generation receives the reflected event"),
            Component->ManagedState,
            FString::Printf(
                TEXT("StressEvent:%d:%d"),
                ExpectedGeneration,
                ExpectedGeneration * 10));
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
        if ((Rollover + 1) % 8 == 0)
        {
            SampleUsedPhysicalBytes(PeakUsedPhysicalBytes);
        }
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
        TEXT("Target deletion releases every reflected event subscription"),
        ManagedFixture.Host->GetEventSubscriptionCount(),
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
    CollectGarbage(RF_NoFlags);
    ++GarbageCollectionCycles;

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
    TestEqual(
        TEXT("Lifecycle matrix leaves no queued worker dispatch"),
        FUnifiedScriptNativeBridgeContext::GetGlobalPendingWorkerDispatchCount(),
        InitialPendingWorkerDispatch);
    TestEqual(
        TEXT("Lifecycle matrix does not overflow worker dispatch capacity"),
        FUnifiedScriptNativeBridgeContext::GetGlobalRejectedWorkerDispatchCount(),
        InitialRejectedWorkerDispatch);
    TestTrue(
        TEXT("Lifecycle matrix verifies at least one complete shutdown sequence"),
        GVerifiedShutdownSequenceCount > 0);
    TestEqual(
        TEXT("Every World follows the deterministic shutdown sequence"),
        GShutdownSequenceFailureCount,
        0);

    const FUnifiedScriptRuntimeBreadcrumbSnapshot BreadcrumbSnapshot =
        FUnifiedScriptRuntimeBreadcrumbs::Capture();
    TestEqual(
        TEXT("Lifecycle pressure keeps the process breadcrumb ring bounded"),
        BreadcrumbSnapshot.Entries.Num(),
        FUnifiedScriptRuntimeBreadcrumbs::MaximumEntryCount);
    TestTrue(
        TEXT("Lifecycle pressure counts overwritten crash breadcrumbs"),
        BreadcrumbSnapshot.DroppedCount > 0);

    const uint64 AsyncCapacityRejections =
        FUnifiedScriptAsyncService::GetGlobalCapacityRejectionCount() -
        InitialAsyncCapacityRejections;
    const uint64 AsyncPayloadRejections =
        FUnifiedScriptAsyncService::GetGlobalPayloadRejectionCount() -
        InitialAsyncPayloadRejections;
    TestEqual(
        TEXT("Soak observes exactly one async capacity rejection"),
        AsyncCapacityRejections,
        1ull);
    TestEqual(
        TEXT("Soak observes exactly one async payload rejection"),
        AsyncPayloadRejections,
        1ull);

    const uint64 FinalUsedPhysicalBytes =
        SampleUsedPhysicalBytes(PeakUsedPhysicalBytes);
    const uint64 MemoryGrowthBytes = FinalUsedPhysicalBytes > InitialUsedPhysicalBytes
        ? FinalUsedPhysicalBytes - InitialUsedPhysicalBytes
        : 0;
    TestTrue(
        *FString::Printf(
            TEXT("Final physical-memory growth stays within %d MiB"),
            MemoryGrowthBudgetMegabytes),
        MemoryGrowthBytes <= MemoryGrowthBudgetBytes);

    UE_LOG(
        LogTemp,
        Display,
        TEXT("UNIFIEDSCRIPT_LIFECYCLE_STRESS_JSON {\"schemaVersion\":\"unifiedscript.unreal-lifecycle-stress/v0.4\",\"platform\":\"%s\",\"dedicatedServer\":%s,\"engineMajor\":%d,\"engineMinor\":%d,\"enginePatch\":%d,\"travelCycles\":%d,\"pieCycles\":%d,\"concurrentWorlds\":%d,\"collidingRequestsPerWorld\":%d,\"generationRollovers\":%d,\"failedReloadCycles\":%d,\"objectChurnCycles\":%d,\"objectsPerChurnCycle\":%d,\"objectRegistrations\":%d,\"garbageCollectionCycles\":%d,\"commandSaturationCycles\":%d,\"commandsPerSaturationCycle\":%d,\"publishedSaturationEvents\":%llu,\"commandOverflowCount\":%llu,\"eventOverflowCount\":%llu,\"traceOverflowCount\":%llu,\"overlapOverflowCount\":%llu,\"asyncCapacityRejections\":%llu,\"asyncPayloadRejections\":%llu,\"maximumAsyncInFlightRequests\":%u,\"maximumAsyncPayloadBytes\":%u,\"shutdownSequencesVerified\":%d,\"shutdownSequenceFailures\":%d,\"breadcrumbCapacity\":%d,\"breadcrumbCount\":%d,\"breadcrumbDroppedCount\":%llu,\"memoryBeforeBytes\":%llu,\"memoryPeakBytes\":%llu,\"memoryAfterBytes\":%llu,\"memoryGrowthBytes\":%llu,\"memoryGrowthBudgetBytes\":%llu,\"staleCompletions\":0,\"pendingRequestsDelta\":0,\"managedWorldsDelta\":0,\"nativeContextsDelta\":0,\"pendingWorkerDispatchDelta\":0,\"rejectedWorkerDispatchDelta\":0,\"registeredObjectsDelta\":0,\"eventSubscriptionsDelta\":0,\"ownedResourcesDelta\":0,\"staleDebugSessions\":0,\"retiredLoadContextsAlive\":%d}"),
        ANSI_TO_TCHAR(FPlatformProperties::PlatformName()),
        IsRunningDedicatedServer() ? TEXT("true") : TEXT("false"),
        FEngineVersion::Current().GetMajor(),
        FEngineVersion::Current().GetMinor(),
        FEngineVersion::Current().GetPatch(),
        TravelCycles,
        PieCycles,
        ConcurrentWorlds,
        CollidingRequestsPerWorld,
        GenerationRollovers,
        FailedReloadCycles,
        ObjectChurnCycles,
        ObjectsPerChurnCycle,
        ObjectRegistrationCount,
        GarbageCollectionCycles,
        CommandSaturationCycles,
        CommandsPerSaturationCycle,
        static_cast<unsigned long long>(PublishedSaturationEventCount),
        static_cast<unsigned long long>(SaturationTelemetry.CommandOverflowCount),
        static_cast<unsigned long long>(SaturationTelemetry.EventOverflowCount),
        static_cast<unsigned long long>(SaturationTelemetry.TraceOverflowCount),
        static_cast<unsigned long long>(SaturationTelemetry.OverlapOverflowCount),
        static_cast<unsigned long long>(AsyncCapacityRejections),
        static_cast<unsigned long long>(AsyncPayloadRejections),
        UE::UnifiedScript::AsyncService::MaximumInFlightRequestCount,
        UE::UnifiedScript::AsyncService::MaximumPayloadByteCount,
        GVerifiedShutdownSequenceCount,
        GShutdownSequenceFailureCount,
        FUnifiedScriptRuntimeBreadcrumbs::MaximumEntryCount,
        BreadcrumbSnapshot.Entries.Num(),
        static_cast<unsigned long long>(BreadcrumbSnapshot.DroppedCount),
        static_cast<unsigned long long>(InitialUsedPhysicalBytes),
        static_cast<unsigned long long>(PeakUsedPhysicalBytes),
        static_cast<unsigned long long>(FinalUsedPhysicalBytes),
        static_cast<unsigned long long>(MemoryGrowthBytes),
        static_cast<unsigned long long>(MemoryGrowthBudgetBytes),
        RetiredLoadContextsAlive);
    return true;
}

#endif
