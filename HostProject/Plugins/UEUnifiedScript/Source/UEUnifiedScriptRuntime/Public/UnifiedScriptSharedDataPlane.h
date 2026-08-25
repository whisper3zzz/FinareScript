#pragma once

#include "CoreMinimal.h"
#include "HAL/CriticalSection.h"
#include "UnifiedScriptObjectRegistry.h"

#include <type_traits>

class AActor;
class UWorld;

namespace UE::UnifiedScript::SharedData
{
/** Stable schema identity for the first World Transform snapshot. */
inline constexpr uint64 TransformSnapshotSchemaId = 0x55535452414E5334ull;
inline constexpr uint32 AbiVersion = 4;
}

/** A generation-safe UObject handle scoped to one World host. */
struct alignas(8) FUnifiedScriptSharedObjectHandleV4
{
    uint64 WorldHostId = 0;
    uint64 ObjectHandle = 0;
};

/**
 * Fixed row published by the Transform snapshot data plane.
 *
 * The layout intentionally mirrors UE5's double-precision FTransform without
 * exposing FTransform, UObject, or compiler-specific container memory.
 */
struct alignas(8) FUnifiedScriptTransformSnapshotEntryV4
{
    FUnifiedScriptSharedObjectHandleV4 Actor;
    double TranslationX = 0.0;
    double TranslationY = 0.0;
    double TranslationZ = 0.0;
    double RotationX = 0.0;
    double RotationY = 0.0;
    double RotationZ = 0.0;
    double RotationW = 1.0;
    double ScaleX = 1.0;
    double ScaleY = 1.0;
    double ScaleZ = 1.0;
};

enum class EUnifiedScriptSharedSnapshotFlagsV4 : uint32
{
    None = 0,
    /** Entries remain valid until the World publishes its next snapshot. */
    BorrowedUntilNextPublish = 1u << 0,
};

/** Borrowed view returned once per managed gameplay phase. */
struct alignas(8) FUnifiedScriptSharedSnapshotViewV4
{
    uint32 AbiVersion = UE::UnifiedScript::SharedData::AbiVersion;
    uint32 ViewSize = 0;
    uint64 SchemaId = 0;
    uint64 WorldHostId = 0;
    uint64 Generation = 0;
    const FUnifiedScriptTransformSnapshotEntryV4* Entries = nullptr;
    uint32 EntryCount = 0;
    uint32 EntryCapacity = 0;
    uint32 EntrySize = 0;
    EUnifiedScriptSharedSnapshotFlagsV4 Flags =
        EUnifiedScriptSharedSnapshotFlagsV4::None;
};

enum class EUnifiedScriptSharedDataCapabilitiesV4 : uint64
{
    None = 0,
    TransformSnapshot = 1ull << 0,
    DoubleBuffered = 1ull << 1,
    WorldScopedHandles = 1ull << 2,
};

using FUnifiedScriptSharedRetainContextV4 = uint8 (*)(void* Context);
using FUnifiedScriptSharedReleaseContextV4 = void (*)(void* Context);
using FUnifiedScriptAcquireTransformSnapshotV4 = uint8 (*)(
    void* Context,
    FUnifiedScriptSharedSnapshotViewV4* OutView);

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

    /** Releases snapshot-owned handle references before the World registry is invalidated. */
    void Shutdown();

private:
    using FSnapshotBuffer = TArray<FUnifiedScriptTransformSnapshotEntryV4>;

    mutable FRWLock SnapshotLock;
    TWeakObjectPtr<UWorld> World;
    FUnifiedScriptObjectRegistry* ObjectRegistry = nullptr;
    uint64 WorldHostId = 0;
    uint64 PublishedGeneration = 0;
    int32 ActiveBufferIndex = 0;
    FSnapshotBuffer Buffers[2];
    TMap<TWeakObjectPtr<AActor>, FUnifiedScriptObjectHandle> ActorHandles;
    TSet<TWeakObjectPtr<AActor>> SeenActors;
    bool bShutdown = false;
};

static_assert(std::is_standard_layout_v<FUnifiedScriptSharedObjectHandleV4>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptSharedObjectHandleV4>);
static_assert(sizeof(FUnifiedScriptSharedObjectHandleV4) == 16);
static_assert(std::is_standard_layout_v<FUnifiedScriptTransformSnapshotEntryV4>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptTransformSnapshotEntryV4>);
static_assert(sizeof(FUnifiedScriptTransformSnapshotEntryV4) == 96);
static_assert(STRUCT_OFFSET(FUnifiedScriptTransformSnapshotEntryV4, TranslationX) == 16);
static_assert(STRUCT_OFFSET(FUnifiedScriptTransformSnapshotEntryV4, RotationX) == 40);
static_assert(STRUCT_OFFSET(FUnifiedScriptTransformSnapshotEntryV4, ScaleX) == 72);
static_assert(std::is_standard_layout_v<FUnifiedScriptSharedSnapshotViewV4>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptSharedSnapshotViewV4>);
static_assert(sizeof(FUnifiedScriptSharedSnapshotViewV4) == 56);
static_assert(STRUCT_OFFSET(FUnifiedScriptSharedSnapshotViewV4, Entries) == 32);
static_assert(std::is_standard_layout_v<FUnifiedScriptSharedDataTransportV4>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptSharedDataTransportV4>);
static_assert(sizeof(FUnifiedScriptSharedDataTransportV4) == 56);

extern "C" UEUNIFIEDSCRIPTRUNTIME_API const FUnifiedScriptSharedDataTransportV4*
UEUnifiedScript_GetSharedDataTransportV4();
