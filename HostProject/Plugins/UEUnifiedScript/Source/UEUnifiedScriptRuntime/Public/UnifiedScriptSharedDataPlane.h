#pragma once

#include "CoreMinimal.h"
#include "HAL/CriticalSection.h"
#include "UObject/WeakObjectPtr.h"
#include "UnifiedScriptObjectRegistry.h"

#include <type_traits>

class AActor;
class UWorld;

/** A generation-safe UObject handle scoped to one World host. */
struct alignas(8) FUnifiedScriptSharedObjectHandleV4
{
    uint64 WorldHostId = 0;
    uint64 ObjectHandle = 0;
};

#include "UnifiedScriptDataPlaneLayouts.gen.h"

using FUnifiedScriptSharedRetainContextV4 = uint8 (*)(void* Context);
using FUnifiedScriptSharedReleaseContextV4 = void (*)(void* Context);
using FUnifiedScriptAcquireTransformSnapshotV4 = uint8 (*)(
    void* Context,
    FUnifiedScriptSharedSnapshotViewV4* OutView);
using FUnifiedScriptGetSharedDataTelemetryV1 = uint8 (*)(
    void* Context,
    FUnifiedScriptSharedDataTelemetryV1* OutTelemetry,
    uint32 OutTelemetrySize);
using FUnifiedScriptAcquireGeneratedSnapshotV1 = uint8 (*)(
    void* Context,
    uint64 SchemaId,
    uint32 EntrySize,
    FUnifiedScriptGeneratedDataViewV1* OutView);

/** Independent shared-data ABI. It is discovered through the ABI v2 suffix. */
struct alignas(8) FUnifiedScriptSharedDataTransportV4
{
    uint32 AbiVersion = UE::UnifiedScript::SharedData::AbiVersion;
    uint32 TableSize = 0;
    uint32 SnapshotViewSize = 0;
    uint32 TransformEntrySize = 0;
    uint64 TransformSchemaId = 0;
    uint64 Capabilities = 0;
    FUnifiedScriptSharedRetainContextV4 RetainContext = nullptr;
    FUnifiedScriptSharedReleaseContextV4 ReleaseContext = nullptr;
    FUnifiedScriptAcquireTransformSnapshotV4 AcquireTransformSnapshot = nullptr;
    uint64 LayoutCompatibilityId = 0;
    uint32 LayoutRevision = 0;
    uint32 MaximumTransformSnapshotCount = 0;
    FUnifiedScriptGetSharedDataTelemetryV1 GetTelemetry = nullptr;
    uint32 GeneratedChannelViewSize = 0;
    uint32 MaximumGeneratedSnapshotCount = 0;
    uint64 MaximumGeneratedChannelBytes = 0;
    FUnifiedScriptAcquireGeneratedSnapshotV1 AcquireGeneratedSnapshot = nullptr;
};

/** Native-owned, per-World double buffer backing the shared Transform view. */
class UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptSharedDataPlane final
{
public:
    FUnifiedScriptSharedDataPlane(
        UWorld& InWorld,
        FUnifiedScriptObjectRegistry& InObjectRegistry,
        uint64 InWorldHostId);
    ~FUnifiedScriptSharedDataPlane();

    FUnifiedScriptSharedDataPlane(const FUnifiedScriptSharedDataPlane&) = delete;
    FUnifiedScriptSharedDataPlane& operator=(const FUnifiedScriptSharedDataPlane&) = delete;

    /** Captures all live Actors into the inactive buffer, then atomically publishes it. */
    void PublishTransformSnapshot();

    /** Returns a view borrowed until the next PublishTransformSnapshot call. */
    bool AcquireTransformSnapshot(FUnifiedScriptSharedSnapshotViewV4& OutView) const;

    /** Returns bounded-capacity and publication diagnostics for this World. */
    bool GetTelemetry(FUnifiedScriptSharedDataTelemetryV1& OutTelemetry) const;

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
        FUnifiedScriptGeneratedDataViewV1& OutView) const;

    /** Releases snapshot-owned handle references before the World registry is invalidated. */
    void Shutdown();

private:
    using FSnapshotBuffer = TArray<FUnifiedScriptTransformSnapshotEntryV4>;

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
    FUnifiedScriptObjectRegistry* ObjectRegistry = nullptr;
    uint64 WorldHostId = 0;
    uint64 PublishedGeneration = 0;
    uint64 TotalPublishedEntryCount = 0;
    uint64 DroppedSnapshotEntryCount = 0;
    int32 ActiveBufferIndex = 0;
    FSnapshotBuffer Buffers[2];
    TMap<TWeakObjectPtr<AActor>, FUnifiedScriptObjectHandle> ActorHandles;
    TSet<TWeakObjectPtr<AActor>> SeenActors;
    TMap<uint64, FGeneratedSnapshotChannel> GeneratedSnapshotChannels;
    bool bShutdown = false;
};

static_assert(std::is_standard_layout_v<FUnifiedScriptSharedObjectHandleV4>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptSharedObjectHandleV4>);
static_assert(sizeof(FUnifiedScriptSharedObjectHandleV4) == 16);
static_assert(std::is_standard_layout_v<FUnifiedScriptSharedDataTransportV4>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptSharedDataTransportV4>);
static_assert(sizeof(FUnifiedScriptSharedDataTransportV4) == 104);

extern "C" UEUNIFIEDSCRIPTRUNTIME_API const FUnifiedScriptSharedDataTransportV4*
UEUnifiedScript_GetSharedDataTransportV4();
