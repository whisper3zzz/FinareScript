#pragma once

#include "CoreMinimal.h"
#include "UnifiedScriptSharedDataPlane.h"

#include <type_traits>

class UWorld;

using FUnifiedScriptBeginCommandBatchV4 = uint8 (*)(
    void*, uint32, FUnifiedScriptCommandBatchViewV4*);
using FUnifiedScriptCommitCommandBatchV4 = uint8 (*)(void*, uint64, uint32);
using FUnifiedScriptAcquireGameplayEventsV4 = uint8 (*)(
    void*, FUnifiedScriptGameplayEventViewV4*);
using FUnifiedScriptBeginTraceBatchV4 = uint8 (*)(
    void*, uint32, FUnifiedScriptTraceRequestViewV4*);
using FUnifiedScriptExecuteTraceBatchV4 = uint8 (*)(
    void*, uint64, uint32, FUnifiedScriptTraceResultViewV4*);
using FUnifiedScriptCancelTraceBatchV4 = void (*)(void*, uint64);
using FUnifiedScriptGetGameplayPhaseV4 = uint8 (*)(void*, uint8*, uint64*);
using FUnifiedScriptGetGameplayDataTelemetryV1 = uint8 (*)(
    void*, FUnifiedScriptGameplayDataTelemetryV1*, uint32);
using FUnifiedScriptBeginOverlapBatchV1 = uint8 (*)(
    void*, uint32, FUnifiedScriptOverlapRequestViewV1*);
using FUnifiedScriptExecuteOverlapBatchV1 = uint8 (*)(
    void*, uint64, uint32, FUnifiedScriptOverlapResultViewV1*);
using FUnifiedScriptCancelOverlapBatchV1 = void (*)(void*, uint64);

struct alignas(8) FUnifiedScriptGameplayDataTransportV4
{
    uint32 AbiVersion = UE::UnifiedScript::GameplayData::AbiVersion;
    uint32 TableSize = 0;
    uint32 CommandEntrySize = 0;
    uint32 EventEntrySize = 0;
    uint32 TraceRequestEntrySize = 0;
    uint32 TraceResultEntrySize = 0;
    uint64 Capabilities = 0;
    FUnifiedScriptSharedRetainContextV4 RetainContext = nullptr;
    FUnifiedScriptSharedReleaseContextV4 ReleaseContext = nullptr;
    FUnifiedScriptBeginCommandBatchV4 BeginCommandBatch = nullptr;
    FUnifiedScriptCommitCommandBatchV4 CommitCommandBatch = nullptr;
    FUnifiedScriptAcquireGameplayEventsV4 AcquireEvents = nullptr;
    FUnifiedScriptBeginTraceBatchV4 BeginTraceBatch = nullptr;
    FUnifiedScriptExecuteTraceBatchV4 ExecuteTraceBatch = nullptr;
    FUnifiedScriptCancelTraceBatchV4 CancelTraceBatch = nullptr;
    FUnifiedScriptGetGameplayPhaseV4 GetPhase = nullptr;
    uint64 LayoutCompatibilityId = 0;
    uint32 LayoutRevision = 0;
    uint32 MaximumCommandCount = 0;
    uint32 MaximumEventCount = 0;
    uint32 MaximumTraceCount = 0;
    FUnifiedScriptGetGameplayDataTelemetryV1 GetTelemetry = nullptr;
    uint32 OverlapRequestEntrySize = 0;
    uint32 OverlapResultEntrySize = 0;
    uint32 MaximumOverlapCount = 0;
    uint32 Reserved = 0;
    FUnifiedScriptBeginOverlapBatchV1 BeginOverlapBatch = nullptr;
    FUnifiedScriptExecuteOverlapBatchV1 ExecuteOverlapBatch = nullptr;
    FUnifiedScriptCancelOverlapBatchV1 CancelOverlapBatch = nullptr;
};

class UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptGameplayDataPlane final
{
public:
    FUnifiedScriptGameplayDataPlane(
        UWorld& InWorld,
        FUnifiedScriptObjectRegistry& InObjectRegistry,
        uint64 InWorldHostId);
    ~FUnifiedScriptGameplayDataPlane();

    void BeginFrame();
    void EndFrame();
    void Shutdown();

    bool BeginCommandBatch(uint32 Capacity, FUnifiedScriptCommandBatchViewV4& OutView);
    bool CommitCommandBatch(uint64 BatchToken, uint32 CommandCount);
    bool AcquireEvents(FUnifiedScriptGameplayEventViewV4& OutView) const;
    bool BeginTraceBatch(uint32 Capacity, FUnifiedScriptTraceRequestViewV4& OutView);
    bool ExecuteTraceBatch(
        uint64 BatchToken,
        uint32 RequestCount,
        FUnifiedScriptTraceResultViewV4& OutView);
    void CancelTraceBatch(uint64 BatchToken);
    bool BeginOverlapBatch(
        uint32 Capacity,
        FUnifiedScriptOverlapRequestViewV1& OutView);
    bool ExecuteOverlapBatch(
        uint64 BatchToken,
        uint32 RequestCount,
        FUnifiedScriptOverlapResultViewV1& OutView);
    void CancelOverlapBatch(uint64 BatchToken);

    EUnifiedScriptGameplayDataPhaseV4 GetPhase() const;
    uint64 GetFrameNumber() const;
    bool GetTelemetry(FUnifiedScriptGameplayDataTelemetryV1& OutTelemetry) const;

private:
    struct FCommandReservation
    {
        uint64 Token = 0;
        uint64 FirstSequenceId = 0;
        uint32 Offset = 0;
        uint32 Capacity = 0;
        uint32 Count = 0;
        bool bCommitted = false;
    };

    bool IsManagedPhase() const;
    void PublishPendingEvents();
    void ExecuteCommittedCommands();
    FUnifiedScriptGameplayEventV4 ExecuteCommand(FUnifiedScriptCommandV4& Command);
    void QueueEvent(
        const FUnifiedScriptGameplayEventV4& Event,
        FUnifiedScriptObjectHandle OwnedArgument = {});
    EUnifiedScriptCommandStatusV4 ResolveObject(
        const FUnifiedScriptSharedObjectHandleV4& Handle,
        UClass* ExpectedClass,
        UObject*& OutObject,
        bool bAllowNull = false) const;
    FUnifiedScriptSharedObjectHandleV4 RegisterEventObject(
        UObject* Object,
        FUnifiedScriptObjectHandle& OutOwnedHandle);
    FUnifiedScriptTraceResultV4 ExecuteTrace(
        const FUnifiedScriptTraceRequestV4& Request);
    FUnifiedScriptOverlapResultV1 ExecuteOverlap(
        const FUnifiedScriptOverlapRequestV1& Request);

    TWeakObjectPtr<UWorld> World;
    FUnifiedScriptObjectRegistry* ObjectRegistry = nullptr;
    uint64 WorldHostId = 0;
    uint64 FrameNumber = 0;
    uint64 NextBatchToken = 1;
    uint64 NextSequenceId = 1;
    uint64 CommandOverflowCount = 0;
    uint64 TraceOverflowCount = 0;
    uint64 OverlapOverflowCount = 0;
    uint64 TotalCommittedCommandCount = 0;
    uint64 TotalExecutedCommandCount = 0;
    uint64 TotalExecutedTraceCount = 0;
    uint64 TotalExecutedOverlapCount = 0;
    uint64 LastCommittedSequenceId = 0;
    uint64 LastExecutedSequenceId = 0;
    uint64 LastPublishedEventFrame = 0;
    EUnifiedScriptGameplayDataPhaseV4 Phase =
        EUnifiedScriptGameplayDataPhaseV4::Idle;

    TArray<FUnifiedScriptCommandV4> Commands;
    TArray<FCommandReservation> CommandReservations;
    TMap<uint64, int32> ReservationIndices;
    uint64 ActiveCommandBatchToken = 0;

    TArray<FUnifiedScriptGameplayEventV4> EventBuffers[2];
    TArray<FUnifiedScriptObjectHandle> EventOwnedHandles[2];
    int32 ActiveEventBufferIndex = 0;
    TArray<FUnifiedScriptGameplayEventV4> PendingEvents;
    TArray<FUnifiedScriptObjectHandle> PendingEventOwnedHandles;
    uint64 DroppedEventCount = 0;

    TArray<FUnifiedScriptTraceRequestV4> TraceRequests;
    TArray<FUnifiedScriptTraceResultV4> TraceResults;
    TArray<FUnifiedScriptObjectHandle> TraceOwnedHandles;
    uint64 ActiveTraceBatchToken = 0;
    TArray<FUnifiedScriptOverlapRequestV1> OverlapRequests;
    TArray<FUnifiedScriptOverlapResultV1> OverlapResults;
    TArray<FUnifiedScriptObjectHandle> OverlapOwnedHandles;
    uint64 ActiveOverlapBatchToken = 0;
    bool bShutdown = false;
};

static_assert(sizeof(FUnifiedScriptGameplayDataTransportV4) == 176);

extern "C" UEUNIFIEDSCRIPTRUNTIME_API const FUnifiedScriptGameplayDataTransportV4*
UEUnifiedScript_GetGameplayDataTransportV4();
