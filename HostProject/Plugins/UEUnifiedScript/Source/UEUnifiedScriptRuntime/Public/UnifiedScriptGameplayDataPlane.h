#pragma once

#include "CoreMinimal.h"
#include "UnifiedScriptSharedDataPlane.h"

#include <type_traits>

class UWorld;

namespace UE::UnifiedScript::GameplayData
{
inline constexpr uint32 AbiVersion = 4;
inline constexpr uint64 CommandSchemaId = 0x5553434D44423401ull;
inline constexpr uint64 EventSchemaId = 0x555345564E543401ull;
inline constexpr uint64 TraceRequestSchemaId = 0x5553545245513401ull;
inline constexpr uint64 TraceResultSchemaId = 0x5553545245533401ull;
inline constexpr uint32 MaximumCommandCount = 65536;
inline constexpr uint32 MaximumEventCount = 4096;
inline constexpr uint32 MaximumTraceCount = 65536;
}

enum class EUnifiedScriptGameplayDataPhaseV4 : uint8
{
    Idle = 0,
    NativePreActorTick = 1,
    ManagedTick = 2,
    NativePostActorTick = 3,
    ShuttingDown = 4,
};

enum class EUnifiedScriptCommandTypeV4 : uint16
{
    None = 0,
    SetActorTransform = 1,
    SetActorVelocity = 2,
    ApplyDamage = 3,
    SpawnActor = 4,
    DestroyActor = 5,
    SpawnEmitter = 6,
};

enum class EUnifiedScriptCommandStatusV4 : uint16
{
    Success = 0,
    InvalidCommand = 1,
    WorldMismatch = 2,
    StaleHandle = 3,
    TypeMismatch = 4,
    ExecutionFailed = 5,
    BufferOverflow = 6,
    WrongPhase = 7,
};

enum class EUnifiedScriptGameplayEventKindV4 : uint16
{
    None = 0,
    CommandCompleted = 1,
    ActorSpawned = 2,
    ActorDestroyed = 3,
    DamageApplied = 4,
    EffectSpawned = 5,
};

enum class EUnifiedScriptTraceShapeV4 : uint8
{
    Line = 0,
    Sphere = 1,
    Box = 2,
};

enum class EUnifiedScriptTraceStatusV4 : uint8
{
    Success = 0,
    InvalidRequest = 1,
    WorldMismatch = 2,
    StaleHandle = 3,
    ExecutionFailed = 4,
};

enum class EUnifiedScriptTraceResultFlagsV4 : uint8
{
    None = 0,
    BlockingHit = 1 << 0,
    StartPenetrating = 1 << 1,
};

enum class EUnifiedScriptGameplayDataCapabilitiesV4 : uint64
{
    None = 0,
    WorldPhaseScheduler = 1ull << 0,
    CommandBuffer = 1ull << 1,
    EventBuffer = 1ull << 2,
    BatchLineTrace = 1ull << 3,
    BatchSphereTrace = 1ull << 4,
    BatchBoxTrace = 1ull << 5,
};

/** Fixed command row. Type-specific constructors live on the managed side. */
struct alignas(8) FUnifiedScriptCommandV4
{
    EUnifiedScriptCommandTypeV4 Type = EUnifiedScriptCommandTypeV4::None;
    uint16 Flags = 0;
    uint32 Reserved = 0;
    uint64 SequenceId = 0;
    FUnifiedScriptSharedObjectHandleV4 Target;
    FUnifiedScriptSharedObjectHandleV4 Argument0;
    FUnifiedScriptSharedObjectHandleV4 Argument1;
    FUnifiedScriptSharedObjectHandleV4 Argument2;
    double Scalar0 = 0.0;
    double Scalar1 = 0.0;
    double Scalar2 = 0.0;
    double Scalar3 = 0.0;
    double Vector0X = 0.0;
    double Vector0Y = 0.0;
    double Vector0Z = 0.0;
    double Vector1X = 0.0;
    double Vector1Y = 0.0;
    double Vector1Z = 0.0;
    double RotationX = 0.0;
    double RotationY = 0.0;
    double RotationZ = 0.0;
    double RotationW = 1.0;
    double Vector2X = 1.0;
    double Vector2Y = 1.0;
    double Vector2Z = 1.0;
    uint64 Payload0 = 0;
};

struct alignas(8) FUnifiedScriptCommandBatchViewV4
{
    uint32 AbiVersion = UE::UnifiedScript::GameplayData::AbiVersion;
    uint32 ViewSize = 0;
    uint64 SchemaId = 0;
    uint64 WorldHostId = 0;
    uint64 FrameNumber = 0;
    uint64 BatchToken = 0;
    uint64 FirstSequenceId = 0;
    FUnifiedScriptCommandV4* Commands = nullptr;
    uint32 CommandCapacity = 0;
    uint32 CommandEntrySize = 0;
    uint32 Flags = 0;
    uint32 Reserved = 0;
};

/** Native-to-managed event row. Command events are published on the next frame. */
struct alignas(8) FUnifiedScriptGameplayEventV4
{
    uint64 SequenceId = 0;
    EUnifiedScriptGameplayEventKindV4 Kind =
        EUnifiedScriptGameplayEventKindV4::None;
    EUnifiedScriptCommandTypeV4 CommandType = EUnifiedScriptCommandTypeV4::None;
    EUnifiedScriptCommandStatusV4 Status = EUnifiedScriptCommandStatusV4::Success;
    uint16 Flags = 0;
    FUnifiedScriptSharedObjectHandleV4 Source;
    FUnifiedScriptSharedObjectHandleV4 Argument;
    double Value0 = 0.0;
    double VectorX = 0.0;
    double VectorY = 0.0;
    double VectorZ = 0.0;
    uint64 FrameNumber = 0;
};

struct alignas(8) FUnifiedScriptGameplayEventViewV4
{
    uint32 AbiVersion = UE::UnifiedScript::GameplayData::AbiVersion;
    uint32 ViewSize = 0;
    uint64 SchemaId = 0;
    uint64 WorldHostId = 0;
    uint64 FrameNumber = 0;
    const FUnifiedScriptGameplayEventV4* Events = nullptr;
    uint32 EventCount = 0;
    uint32 EventCapacity = 0;
    uint32 EventEntrySize = 0;
    uint32 Flags = 0;
    uint64 DroppedEventCount = 0;
};

/** One line/sphere/box trace request with up to four ignored Actors. */
struct alignas(8) FUnifiedScriptTraceRequestV4
{
    uint64 SequenceId = 0;
    EUnifiedScriptTraceShapeV4 Shape = EUnifiedScriptTraceShapeV4::Line;
    uint8 IgnoreActorCount = 0;
    uint16 Flags = 0;
    uint32 CollisionChannel = 0;
    FUnifiedScriptSharedObjectHandleV4 IgnoreActors[4];
    double StartX = 0.0;
    double StartY = 0.0;
    double StartZ = 0.0;
    double EndX = 0.0;
    double EndY = 0.0;
    double EndZ = 0.0;
    double ExtentX = 0.0;
    double ExtentY = 0.0;
    double ExtentZ = 0.0;
    double RotationX = 0.0;
    double RotationY = 0.0;
    double RotationZ = 0.0;
    double RotationW = 1.0;
};

struct alignas(8) FUnifiedScriptTraceResultV4
{
    uint64 SequenceId = 0;
    EUnifiedScriptTraceStatusV4 Status = EUnifiedScriptTraceStatusV4::Success;
    EUnifiedScriptTraceResultFlagsV4 Flags = EUnifiedScriptTraceResultFlagsV4::None;
    uint16 Reserved = 0;
    int32 FaceIndex = INDEX_NONE;
    FUnifiedScriptSharedObjectHandleV4 HitActor;
    FUnifiedScriptSharedObjectHandleV4 HitComponent;
    double Distance = 0.0;
    double Time = 1.0;
    double LocationX = 0.0;
    double LocationY = 0.0;
    double LocationZ = 0.0;
    double ImpactPointX = 0.0;
    double ImpactPointY = 0.0;
    double ImpactPointZ = 0.0;
    double NormalX = 0.0;
    double NormalY = 0.0;
    double NormalZ = 0.0;
    double ImpactNormalX = 0.0;
    double ImpactNormalY = 0.0;
    double ImpactNormalZ = 0.0;
    int32 Item = INDEX_NONE;
    int32 ElementIndex = INDEX_NONE;
};

struct alignas(8) FUnifiedScriptTraceRequestViewV4
{
    uint32 AbiVersion = UE::UnifiedScript::GameplayData::AbiVersion;
    uint32 ViewSize = 0;
    uint64 SchemaId = 0;
    uint64 WorldHostId = 0;
    uint64 FrameNumber = 0;
    uint64 BatchToken = 0;
    FUnifiedScriptTraceRequestV4* Requests = nullptr;
    uint32 RequestCapacity = 0;
    uint32 RequestEntrySize = 0;
    uint32 Flags = 0;
    uint32 Reserved = 0;
};

struct alignas(8) FUnifiedScriptTraceResultViewV4
{
    uint32 AbiVersion = UE::UnifiedScript::GameplayData::AbiVersion;
    uint32 ViewSize = 0;
    uint64 SchemaId = 0;
    uint64 WorldHostId = 0;
    uint64 FrameNumber = 0;
    uint64 BatchToken = 0;
    const FUnifiedScriptTraceResultV4* Results = nullptr;
    uint32 ResultCount = 0;
    uint32 ResultCapacity = 0;
    uint32 ResultEntrySize = 0;
    uint32 Flags = 0;
};

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

    EUnifiedScriptGameplayDataPhaseV4 GetPhase() const;
    uint64 GetFrameNumber() const;

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

    TWeakObjectPtr<UWorld> World;
    FUnifiedScriptObjectRegistry* ObjectRegistry = nullptr;
    uint64 WorldHostId = 0;
    uint64 FrameNumber = 0;
    uint64 NextBatchToken = 1;
    uint64 NextSequenceId = 1;
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
    bool bShutdown = false;
};

static_assert(sizeof(FUnifiedScriptCommandV4) == 224);
static_assert(sizeof(FUnifiedScriptCommandBatchViewV4) == 72);
static_assert(sizeof(FUnifiedScriptGameplayEventV4) == 88);
static_assert(sizeof(FUnifiedScriptGameplayEventViewV4) == 64);
static_assert(sizeof(FUnifiedScriptTraceRequestV4) == 184);
static_assert(sizeof(FUnifiedScriptTraceResultV4) == 168);
static_assert(sizeof(FUnifiedScriptTraceRequestViewV4) == 64);
static_assert(sizeof(FUnifiedScriptTraceResultViewV4) == 64);
static_assert(sizeof(FUnifiedScriptGameplayDataTransportV4) == 104);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptCommandV4>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptGameplayEventV4>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptTraceRequestV4>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptTraceResultV4>);

extern "C" UEUNIFIEDSCRIPTRUNTIME_API const FUnifiedScriptGameplayDataTransportV4*
UEUnifiedScript_GetGameplayDataTransportV4();
