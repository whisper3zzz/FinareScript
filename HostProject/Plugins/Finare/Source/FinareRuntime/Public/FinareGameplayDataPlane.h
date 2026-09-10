#pragma once

#include "CoreMinimal.h"
#include "FinareSharedDataPlane.h"

#include <type_traits>

class UWorld;

using FFinareBeginCommandBatchV4 = uint8 (*)(
    void*, uint32, FFinareCommandBatchViewV4*);
using FFinareCommitCommandBatchV4 = uint8 (*)(void*, uint64, uint32);
using FFinareAcquireGameplayEventsV4 = uint8 (*)(
    void*, FFinareGameplayEventViewV4*);
using FFinareBeginTraceBatchV4 = uint8 (*)(
    void*, uint32, FFinareTraceRequestViewV4*);
using FFinareExecuteTraceBatchV4 = uint8 (*)(
    void*, uint64, uint32, FFinareTraceResultViewV4*);
using FFinareCancelTraceBatchV4 = void (*)(void*, uint64);
using FFinareGetGameplayPhaseV4 = uint8 (*)(void*, uint8*, uint64*);
using FFinareGetGameplayDataTelemetryV1 = uint8 (*)(
    void*, FFinareGameplayDataTelemetryV1*, uint32);
using FFinareBeginOverlapBatchV1 = uint8 (*)(
    void*, uint32, FFinareOverlapRequestViewV1*);
using FFinareExecuteOverlapBatchV1 = uint8 (*)(
    void*, uint64, uint32, FFinareOverlapResultViewV1*);
using FFinareCancelOverlapBatchV1 = void (*)(void*, uint64);
using FFinareAcquireGeneratedEventsV1 = uint8 (*)(
    void*, uint64, uint32, FFinareGeneratedDataViewV1*);

struct alignas(8) FFinareGameplayDataTransportV4
{
    uint32 AbiVersion = UE::Finare::GameplayData::AbiVersion;
    uint32 TableSize = 0;
    uint32 CommandEntrySize = 0;
    uint32 EventEntrySize = 0;
    uint32 TraceRequestEntrySize = 0;
    uint32 TraceResultEntrySize = 0;
    uint64 Capabilities = 0;
    FFinareSharedRetainContextV4 RetainContext = nullptr;
    FFinareSharedReleaseContextV4 ReleaseContext = nullptr;
    FFinareBeginCommandBatchV4 BeginCommandBatch = nullptr;
    FFinareCommitCommandBatchV4 CommitCommandBatch = nullptr;
    FFinareAcquireGameplayEventsV4 AcquireEvents = nullptr;
    FFinareBeginTraceBatchV4 BeginTraceBatch = nullptr;
    FFinareExecuteTraceBatchV4 ExecuteTraceBatch = nullptr;
    FFinareCancelTraceBatchV4 CancelTraceBatch = nullptr;
    FFinareGetGameplayPhaseV4 GetPhase = nullptr;
    uint64 LayoutCompatibilityId = 0;
    uint32 LayoutRevision = 0;
    uint32 MaximumCommandCount = 0;
    uint32 MaximumEventCount = 0;
    uint32 MaximumTraceCount = 0;
    FFinareGetGameplayDataTelemetryV1 GetTelemetry = nullptr;
    uint32 OverlapRequestEntrySize = 0;
    uint32 OverlapResultEntrySize = 0;
    uint32 MaximumOverlapCount = 0;
    uint32 Reserved = 0;
    FFinareBeginOverlapBatchV1 BeginOverlapBatch = nullptr;
    FFinareExecuteOverlapBatchV1 ExecuteOverlapBatch = nullptr;
    FFinareCancelOverlapBatchV1 CancelOverlapBatch = nullptr;
    uint32 GeneratedChannelViewSize = 0;
    uint32 MaximumGeneratedEventCount = 0;
    uint64 MaximumGeneratedChannelBytes = 0;
    FFinareAcquireGeneratedEventsV1 AcquireGeneratedEvents = nullptr;
};

class FINARERUNTIME_API FFinareGameplayDataPlane final
{
public:
    FFinareGameplayDataPlane(
        UWorld& InWorld,
        FFinareObjectRegistry& InObjectRegistry,
        uint64 InWorldHostId);
    ~FFinareGameplayDataPlane();

    void BeginFrame();
    void EndFrame();
    void Shutdown();

    bool BeginCommandBatch(uint32 Capacity, FFinareCommandBatchViewV4& OutView);
    bool CommitCommandBatch(uint64 BatchToken, uint32 CommandCount);
    bool AcquireEvents(FFinareGameplayEventViewV4& OutView) const;
    bool QueueGeneratedEvents(
        uint64 SchemaId,
        uint32 EntrySize,
        const void* Entries,
        uint32 EntryCount);
    bool AcquireGeneratedEvents(
        uint64 SchemaId,
        uint32 EntrySize,
        FFinareGeneratedDataViewV1& OutView) const;
    bool BeginTraceBatch(uint32 Capacity, FFinareTraceRequestViewV4& OutView);
    bool ExecuteTraceBatch(
        uint64 BatchToken,
        uint32 RequestCount,
        FFinareTraceResultViewV4& OutView);
    void CancelTraceBatch(uint64 BatchToken);
    bool BeginOverlapBatch(
        uint32 Capacity,
        FFinareOverlapRequestViewV1& OutView);
    bool ExecuteOverlapBatch(
        uint64 BatchToken,
        uint32 RequestCount,
        FFinareOverlapResultViewV1& OutView);
    void CancelOverlapBatch(uint64 BatchToken);

    EFinareGameplayDataPhaseV4 GetPhase() const;
    uint64 GetFrameNumber() const;
    bool GetTelemetry(FFinareGameplayDataTelemetryV1& OutTelemetry) const;

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

    struct FGeneratedEventChannel
    {
        uint32 EntrySize = 0;
        uint64 PublicationId = 0;
        uint64 DroppedEntryCount = 0;
        int32 ActiveBufferIndex = 0;
        TArray<uint8> Buffers[2];
        uint32 EntryCounts[2] = {0, 0};
        TArray<uint8> PendingBytes;
        uint32 PendingEntryCount = 0;
    };

    bool IsManagedPhase() const;
    void PublishPendingEvents();
    void PublishPendingGeneratedEvents();
    void ExecuteCommittedCommands();
    FFinareGameplayEventV4 ExecuteCommand(FFinareCommandV4& Command);
    void QueueEvent(
        const FFinareGameplayEventV4& Event,
        FFinareObjectHandle OwnedArgument = {});
    EFinareCommandStatusV4 ResolveObject(
        const FFinareSharedObjectHandleV4& Handle,
        UClass* ExpectedClass,
        UObject*& OutObject,
        bool bAllowNull = false) const;
    FFinareSharedObjectHandleV4 RegisterEventObject(
        UObject* Object,
        FFinareObjectHandle& OutOwnedHandle);
    FFinareTraceResultV4 ExecuteTrace(
        const FFinareTraceRequestV4& Request);
    FFinareOverlapResultV1 ExecuteOverlap(
        const FFinareOverlapRequestV1& Request);

    TWeakObjectPtr<UWorld> World;
    FFinareObjectRegistry* ObjectRegistry = nullptr;
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
    uint64 NativePreActorTickCount = 0;
    uint64 NativePreActorTickNanoseconds = 0;
    uint64 ManagedTickCount = 0;
    uint64 ManagedTickNanoseconds = 0;
    uint64 NativePostActorTickCount = 0;
    uint64 NativePostActorTickNanoseconds = 0;
    uint64 ManagedTickStartedCycles = 0;
    EFinareGameplayDataPhaseV4 Phase =
        EFinareGameplayDataPhaseV4::Idle;

    TArray<FFinareCommandV4> Commands;
    TArray<FCommandReservation> CommandReservations;
    TMap<uint64, int32> ReservationIndices;
    uint64 ActiveCommandBatchToken = 0;

    TArray<FFinareGameplayEventV4> EventBuffers[2];
    TArray<FFinareObjectHandle> EventOwnedHandles[2];
    int32 ActiveEventBufferIndex = 0;
    TArray<FFinareGameplayEventV4> PendingEvents;
    TArray<FFinareObjectHandle> PendingEventOwnedHandles;
    uint64 DroppedEventCount = 0;
    TMap<uint64, FGeneratedEventChannel> GeneratedEventChannels;

    TArray<FFinareTraceRequestV4> TraceRequests;
    TArray<FFinareTraceResultV4> TraceResults;
    TArray<FFinareObjectHandle> TraceOwnedHandles;
    uint64 ActiveTraceBatchToken = 0;
    TArray<FFinareOverlapRequestV1> OverlapRequests;
    TArray<FFinareOverlapResultV1> OverlapResults;
    TArray<FFinareObjectHandle> OverlapOwnedHandles;
    uint64 ActiveOverlapBatchToken = 0;
    bool bShutdown = false;
};

static_assert(sizeof(FFinareGameplayDataTransportV4) == 200);

extern "C" FINARERUNTIME_API const FFinareGameplayDataTransportV4*
Finare_GetGameplayDataTransportV4();
