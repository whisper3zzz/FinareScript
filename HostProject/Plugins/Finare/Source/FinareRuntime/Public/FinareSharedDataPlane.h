#pragma once

#include "CoreMinimal.h"
#include "HAL/CriticalSection.h"
#include "UObject/WeakObjectPtr.h"
#include "FinareObjectRegistry.h"

#include <type_traits>

class AActor;
class UWorld;

/** A generation-safe UObject handle scoped to one World host. */
struct alignas(8) FFinareSharedObjectHandleV4
{
    uint64 WorldHostId = 0;
    uint64 ObjectHandle = 0;
};

#include "FinareDataPlaneLayouts.gen.h"

using FFinareSharedRetainContextV4 = uint8 (*)(void* Context);
using FFinareSharedReleaseContextV4 = void (*)(void* Context);
using FFinareAcquireTransformSnapshotV4 = uint8 (*)(
    void* Context,
    FFinareSharedSnapshotViewV4* OutView);
using FFinareGetSharedDataTelemetryV1 = uint8 (*)(
    void* Context,
    FFinareSharedDataTelemetryV1* OutTelemetry,
    uint32 OutTelemetrySize);
using FFinareAcquireGeneratedSnapshotV1 = uint8 (*)(
    void* Context,
    uint64 SchemaId,
    uint32 EntrySize,
    FFinareGeneratedDataViewV1* OutView);

/** Independent shared-data ABI. It is discovered through the ABI v2 suffix. */
struct alignas(8) FFinareSharedDataTransportV4
{
    uint32 AbiVersion = UE::Finare::SharedData::AbiVersion;
    uint32 TableSize = 0;
    uint32 SnapshotViewSize = 0;
    uint32 TransformEntrySize = 0;
    uint64 TransformSchemaId = 0;
    uint64 Capabilities = 0;
    FFinareSharedRetainContextV4 RetainContext = nullptr;
    FFinareSharedReleaseContextV4 ReleaseContext = nullptr;
    FFinareAcquireTransformSnapshotV4 AcquireTransformSnapshot = nullptr;
    uint64 LayoutCompatibilityId = 0;
    uint32 LayoutRevision = 0;
    uint32 MaximumTransformSnapshotCount = 0;
    FFinareGetSharedDataTelemetryV1 GetTelemetry = nullptr;
    uint32 GeneratedChannelViewSize = 0;
    uint32 MaximumGeneratedSnapshotCount = 0;
    uint64 MaximumGeneratedChannelBytes = 0;
    FFinareAcquireGeneratedSnapshotV1 AcquireGeneratedSnapshot = nullptr;
};

/** Native-owned, per-World double buffer backing the shared Transform view. */
class FINARERUNTIME_API FFinareSharedDataPlane final
{
public:
    FFinareSharedDataPlane(
        UWorld& InWorld,
        FFinareObjectRegistry& InObjectRegistry,
        uint64 InWorldHostId);
    ~FFinareSharedDataPlane();

    FFinareSharedDataPlane(const FFinareSharedDataPlane&) = delete;
    FFinareSharedDataPlane& operator=(const FFinareSharedDataPlane&) = delete;

    /** Captures all live Actors into the inactive buffer, then atomically publishes it. */
    void PublishTransformSnapshot();

    /** Returns a view borrowed until the next PublishTransformSnapshot call. */
    bool AcquireTransformSnapshot(FFinareSharedSnapshotViewV4& OutView) const;

    /** Returns bounded-capacity and publication diagnostics for this World. */
    bool GetTelemetry(FFinareSharedDataTelemetryV1& OutTelemetry) const;

    /** Copies a generated row set into its World-owned inactive snapshot buffer. */
    bool PublishGeneratedSnapshot(
        uint64 SchemaId,
        uint32 EntrySize,
        const void* Entries,
        uint32 EntryCount);

    /** Returns a schema-checked view borrowed until this channel is republished. */
    bool AcquireGeneratedSnapshot(
        uint64 SchemaId,
        uint32 EntrySize,
        FFinareGeneratedDataViewV1& OutView) const;

    /** Releases snapshot-owned handle references before the World registry is invalidated. */
    void Shutdown();

private:
    using FSnapshotBuffer = TArray<FFinareTransformSnapshotEntryV4>;

    struct FGeneratedSnapshotChannel
    {
        uint32 EntrySize = 0;
        uint64 PublicationId = 0;
        uint64 DroppedEntryCount = 0;
        int32 ActiveBufferIndex = 0;
        TArray<uint8> Buffers[2];
        uint32 EntryCounts[2] = {0, 0};
    };

    mutable FRWLock SnapshotLock;
    TWeakObjectPtr<UWorld> World;
    FFinareObjectRegistry* ObjectRegistry = nullptr;
    uint64 WorldHostId = 0;
    uint64 PublishedGeneration = 0;
    uint64 TotalPublishedEntryCount = 0;
    uint64 DroppedSnapshotEntryCount = 0;
    int32 ActiveBufferIndex = 0;
    FSnapshotBuffer Buffers[2];
    TMap<TWeakObjectPtr<AActor>, FFinareObjectHandle> ActorHandles;
    TSet<TWeakObjectPtr<AActor>> SeenActors;
    TMap<uint64, FGeneratedSnapshotChannel> GeneratedSnapshotChannels;
    bool bShutdown = false;
};

static_assert(std::is_standard_layout_v<FFinareSharedObjectHandleV4>);
static_assert(std::is_trivially_copyable_v<FFinareSharedObjectHandleV4>);
static_assert(sizeof(FFinareSharedObjectHandleV4) == 16);
static_assert(std::is_standard_layout_v<FFinareSharedDataTransportV4>);
static_assert(std::is_trivially_copyable_v<FFinareSharedDataTransportV4>);
static_assert(sizeof(FFinareSharedDataTransportV4) == 104);

extern "C" FINARERUNTIME_API const FFinareSharedDataTransportV4*
Finare_GetSharedDataTransportV4();
