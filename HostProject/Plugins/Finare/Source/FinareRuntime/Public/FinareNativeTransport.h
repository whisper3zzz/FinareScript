#pragma once

#include "CoreMinimal.h"
#include "Containers/Queue.h"
#include "HAL/CriticalSection.h"
#include "Templates/Function.h"
#include "UObject/StrongObjectPtr.h"
#include "FinareAsyncService.h"
#include "FinareGameplayDataPlane.h"
#include "FinareInvocationBridge.h"
#include "FinareNativeObjectTransport.h"
#include "FinareCallbackTransport.h"
#include "FinareSharedDataPlane.h"

#include <atomic>
#include <type_traits>

/**
 * Stable, standard-layout value passed into the version-one native ABI.
 * StringData is a borrowed UTF-8 slice for text kinds and a compact,
 * kind-specific little-endian payload for 64-bit values, enums and core structs.
 * Small integers use Int32Value (UInt32 and Float32 use their bit patterns).
 * Enum payload: int64 value, uint64 type ID, optional UTF-8 path (max 4096 bytes).
 * Rotator is three binary64 cells; Transform is quaternion XYZW, translation XYZ,
 * scale XYZ. Existing encodings/layouts are unchanged; update SDK/native together.
 */
struct FFinareNativeValueV1
{
    uint8 Kind = 0;
    uint8 BooleanValue = 0;
    uint16 Reserved = 0;
    int32 Int32Value = 0;
    const uint8* StringData = nullptr;
    int32 StringLength = 0;
};

using FFinareNativeResultCallbackV1 = void (*)(
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
    int32 MessageLength);

using FFinareNativeReadPropertyV1 = void (*)(
    void* Context,
    uint64 Target,
    const uint8* PropertyPathData,
    int32 PropertyPathLength,
    FFinareNativeResultCallbackV1 Callback,
    void* UserData);

using FFinareNativeWritePropertyV1 = void (*)(
    void* Context,
    uint64 Target,
    const uint8* PropertyPathData,
    int32 PropertyPathLength,
    const FFinareNativeValueV1* Value,
    FFinareNativeResultCallbackV1 Callback,
    void* UserData);

using FFinareNativeInvokeV1 = void (*)(
    void* Context,
    uint64 Target,
    const uint8* FunctionPathData,
    int32 FunctionPathLength,
    const FFinareNativeValueV1* Arguments,
    int32 ArgumentCount,
    FFinareNativeResultCallbackV1 Callback,
    void* UserData);

using FFinareNativeReadPropertyByIdV2 = void (*)(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    FFinareNativeResultCallbackV1 Callback,
    void* UserData);

using FFinareNativeWritePropertyByIdV2 = void (*)(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    const FFinareNativeValueV1* Value,
    FFinareNativeResultCallbackV1 Callback,
    void* UserData);

using FFinareNativeInvokeByIdV2 = void (*)(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    const FFinareNativeValueV1* Arguments,
    int32 ArgumentCount,
    FFinareNativeResultCallbackV1 Callback,
    void* UserData);

using FFinareNativeRetainContextV1 = uint8 (*)(void* Context);
using FFinareNativeReleaseContextV1 = void (*)(void* Context);

struct FFinareNativeValueV3;

/** Borrowed, synchronous reverse-call frame for one reflected delegate broadcast. */
struct alignas(8) FFinareNativeEventFrameV4
{
    uint32 AbiVersion = 4;
    uint32 FrameSize = 0;
    uint64 SubscriptionId = 0;
    uint64 Source = 0;
    uint64 EventId = 0;
    const FFinareNativeValueV3* Arguments = nullptr;
    uint32 ArgumentCount = 0;
    uint32 Reserved = 0;
    const uint8* ByteBuffer = nullptr;
    uint32 ByteCount = 0;
    uint32 Reserved2 = 0;
};

using FFinareNativeEventCallbackV4 = void (*)(
    void* UserData,
    const FFinareNativeEventFrameV4* Frame);
using FFinareNativeSubscribeEventV4 = uint64 (*)(
    void* Context,
    uint64 Source,
    uint64 EventId,
    const uint8* EventPathData,
    int32 EventPathLength,
    FFinareNativeEventCallbackV4 Callback,
    void* UserData);
using FFinareNativeUnsubscribeEventV4 = uint8 (*)(
    void* Context,
    uint64 SubscriptionId);

struct alignas(8) FFinareNativeEventTransportV4
{
    uint32 AbiVersion = 4;
    uint32 TableSize = 0;
    uint32 FrameSize = 0;
    uint32 ValueSize = 0;
    FFinareNativeRetainContextV1 RetainContext = nullptr;
    FFinareNativeReleaseContextV1 ReleaseContext = nullptr;
    FFinareNativeSubscribeEventV4 Subscribe = nullptr;
    FFinareNativeUnsubscribeEventV4 Unsubscribe = nullptr;
};

/**
 * Ref-counted lifetime guard between a world host and managed transports.
 *
 * The world host owns the initial reference. Managed transports retain their
 * own reference through the ABI table. Detach prevents new bridge calls and
 * waits for any current call before the host destroys the bridge and registry.
 */
class FINARERUNTIME_API FFinareNativeBridgeContext final
{
public:
    static constexpr int32 MaximumPendingWorkerDispatchCount = 1024;
    static constexpr int32 MaximumCorrelatedResults = 64;
    static constexpr uint64 MaximumCorrelatedResultBytes = 64ull * 1024 * 1024;
    struct FPendingCallFrameResult
    {
        bool bValid = false;
        uint8 Operation = 0;
        uint64 Target = 0;
        uint64 MemberId = 0;
        uint64 RequestHash = 0;
        uint32 EncodedByteCount = 0;
        bool bInFlight = false;
        TArray<uint8> ParentResultCells;
        TArray<uint8> ParentArena;
        FFinareValue ReturnValue;
        TArray<FFinareValue> OutputValues;

        void Reset()
        {
            bValid = false;
            Operation = 0;
            Target = 0;
            MemberId = 0;
            RequestHash = 0;
            EncodedByteCount = 0;
            ParentResultCells.Reset();
            ParentArena.Reset();
            ReturnValue = {};
            OutputValues.Reset();
        }
    };

    explicit FFinareNativeBridgeContext(FFinareInvocationBridge& InBridge);

    /** Process-wide diagnostics used by lifecycle soak gates. */
    static int32 GetLiveContextCount();
    static int32 GetAttachedContextCount();
    static int32 GetGlobalPendingWorkerDispatchCount();
    static uint64 GetGlobalRejectedWorkerDispatchCount();

    int32 GetEventSubscriptionCount();

    void AttachSharedDataPlane(FFinareSharedDataPlane* InSharedDataPlane);
    void AttachGameplayDataPlane(FFinareGameplayDataPlane* InGameplayDataPlane);
    void AttachAsyncService(FFinareAsyncService* InAsyncService);
    bool TryRetain();
    void Release();
    void Detach();
    bool Execute(TUniqueFunction<void(FFinareInvocationBridge&)>&& Operation);
    bool ExecuteGameThreadFast(
        TUniqueFunction<void(FFinareInvocationBridge&)>&& Operation);
    bool ExecuteCallFrame(
        TUniqueFunction<void(
            FFinareInvocationBridge&,
            FPendingCallFrameResult&)>&& Operation,
        uint64 CorrelationId = 0);
    void ReleaseCorrelatedResult(uint64 CorrelationId);
    int32 GetCorrelatedResultCount() const { check(IsInGameThread()); return CorrelatedResults.Num(); }
    /** Executes worker-originated bridge calls without blocking the Game Thread. */
    void PumpGameThreadCalls();
    /** Rejects and releases worker calls before World teardown. */
    void SuspendWorkerDispatch();
    bool SubscribeEvent(
        FFinareObjectHandle Source,
        uint64 EventId,
        const FString& EventPath,
        FFinareNativeEventCallbackV4 Callback,
        void* UserData,
        uint64& OutSubscriptionId);
    bool UnsubscribeEvent(uint64 SubscriptionId);
    bool AcquireTransformSnapshot(FFinareSharedSnapshotViewV4& OutView);
    bool AcquireGeneratedSnapshot(
        uint64 SchemaId,
        uint32 EntrySize,
        FFinareGeneratedDataViewV1& OutView);
    bool GetSharedDataTelemetry(FFinareSharedDataTelemetryV1& OutTelemetry);
    bool ExecuteGameplayData(
        TFunctionRef<bool(FFinareGameplayDataPlane&)> Operation);
    bool ExecuteAsyncService(
        TFunctionRef<bool(FFinareAsyncService&)> Operation);
    /** Native objects are game-thread-only, including explicit disposal. */
    bool ExecuteNativeObjects(TFunctionRef<void(FFinareNativeObjectRegistry&)> Operation);
    EFinareCallbackStatus OpenCallbackScope(uint64& OutScope);
    int32 GetCallbackCount() const { check(IsInGameThread()); return CallbackProxies.Num(); }
    EFinareCallbackStatus CloseCallbackScope(uint64 Scope);
    EFinareCallbackStatus RegisterCallback(uint64 Scope, const FString& Signature,
        FFinareCallback Callback, void* UserData, uint64& OutToken);
    EFinareCallbackStatus ReleaseCallback(uint64 Scope, uint64 Token);

private:
    struct FPendingGameThreadCall;
    ~FFinareNativeBridgeContext();

    std::atomic<uint32> ReferenceCount{1};
    FCriticalSection BridgeLock;
    FFinareInvocationBridge* Bridge = nullptr;
    std::atomic<FFinareInvocationBridge*> FastBridge{nullptr};
    FFinareSharedDataPlane* SharedDataPlane = nullptr;
    FFinareGameplayDataPlane* GameplayDataPlane = nullptr;
    FFinareAsyncService* AsyncService = nullptr;
    FFinareNativeObjectRegistry NativeObjects;
    FPendingCallFrameResult PendingCallFrameResult;
    TMap<uint64, TSharedPtr<FPendingCallFrameResult>> CorrelatedResults;
    uint64 CorrelatedResultBytes = 0;
    TQueue<TSharedPtr<FPendingGameThreadCall>, EQueueMode::Mpsc> PendingGameThreadCalls;
    std::atomic<bool> bAcceptWorkerDispatch{true};
    uint64 NextEventSubscriptionId = 1;
    TMap<uint64, TStrongObjectPtr<UObject>> EventSubscriptions;
    TSet<uint64> CallbackScopes;
    TMap<uint64, TStrongObjectPtr<UObject>> CallbackProxies;
};

/**
 * Versioned synchronous function table consumed by the managed transport.
 *
 * Context must point to a live FFinareInvocationBridge owned by the
 * future world runtime host. Input slices remain caller-owned. Output slices
 * remain valid only for the duration of Callback and must be copied there.
 */
struct FFinareNativeTransportV1
{
    uint32 AbiVersion = 1;
    uint32 TableSize = 0;
    uint32 ValueSize = 0;
    uint32 Reserved = 0;
    FFinareNativeReadPropertyV1 ReadProperty = nullptr;
    FFinareNativeWritePropertyV1 WriteProperty = nullptr;
    FFinareNativeInvokeV1 Invoke = nullptr;
    FFinareNativeRetainContextV1 RetainContext = nullptr;
    FFinareNativeReleaseContextV1 ReleaseContext = nullptr;
};

struct FFinareNativeTransportV3;

/** ABI v2 preserves the complete v1 prefix and appends stable-ID fast thunks. */
struct FFinareNativeTransportV2
{
    uint32 AbiVersion = 2;
    uint32 TableSize = 0;
    uint32 ValueSize = 0;
    uint32 Reserved = 0;
    FFinareNativeReadPropertyV1 ReadProperty = nullptr;
    FFinareNativeWritePropertyV1 WriteProperty = nullptr;
    FFinareNativeInvokeV1 Invoke = nullptr;
    FFinareNativeRetainContextV1 RetainContext = nullptr;
    FFinareNativeReleaseContextV1 ReleaseContext = nullptr;
    FFinareNativeReadPropertyByIdV2 ReadPropertyById = nullptr;
    FFinareNativeWritePropertyByIdV2 WritePropertyById = nullptr;
    FFinareNativeInvokeByIdV2 InvokeById = nullptr;
    /** Optional independent ABI v3 table; older v2 clients ignore this suffix. */
    const FFinareNativeTransportV3* CallFrameTransport = nullptr;
    /** Optional reverse event ABI; older v2 clients ignore this suffix. */
    const FFinareNativeEventTransportV4* EventTransport = nullptr;
    /** Optional zero-copy World snapshot ABI; older v2 clients ignore this suffix. */
    const FFinareSharedDataTransportV4* SharedDataTransport = nullptr;
    /** Optional command/event/trace ABI; older v2 clients ignore this suffix. */
    const FFinareGameplayDataTransportV4* GameplayDataTransport = nullptr;
    /** Optional async/latent service ABI; older v2 clients ignore this suffix. */
    const FFinareAsyncTransportV5* AsyncTransport = nullptr;
    /** Optional non-UObject ownership/dispatch ABI; earlier v2 clients ignore it. */
    const FFinareNativeObjectTransportV1* NativeObjectTransport = nullptr;
    const FFinareCallbackTransport* CallbackTransport = nullptr;
};

/**
 * ABI v3 value kinds. The numeric values are part of the native/managed wire
 * contract and must only be extended by appending new entries.
 */
enum class EFinareNativeValueKindV3 : uint8
{
    Void = 0,
    Boolean = 1,
    Int8 = 2,
    UInt8 = 3,
    Int16 = 4,
    UInt16 = 5,
    Int32 = 6,
    UInt32 = 7,
    Int64 = 8,
    UInt64 = 9,
    Float32 = 10,
    Float64 = 11,
    Enum = 12,
    Name = 13,
    String = 14,
    Text = 15,
    Object = 16,
    Class = 17,
    Vector3 = 18,
    Rotator3 = 19,
    Transform = 20,
    Struct = 21,
    Array = 22,
    Set = 23,
    Map = 24,
    ByteBuffer = 25,
    // Valid only on the separate native-object transport, never a UObject cell.
    NativeObject = 26,
    Callback = 27,
};

enum class EFinareNativeValueFlagsV3 : uint8
{
    None = 0,
    Null = 1 << 0,
    ArenaBacked = 1 << 1,
};

/** Overall disposition of one synchronous ABI v3 call frame. */
enum class EFinareNativeCallStatusV3 : uint8
{
    Uninitialized = 0,
    Success = 1,
    InvocationFailure = 2,
    BufferTooSmall = 3,
    InvalidFrame = 4,
    TransportUnavailable = 5,
};

enum class EFinareNativeCapabilitiesV3 : uint64
{
    None = 0,
    FixedScalars = 1ull << 0,
    Enums = 1ull << 1,
    ObjectHandles = 1ull << 2,
    VectorAndRotator = 1ull << 3,
    Transform = 1ull << 4,
    Utf8Text = 1ull << 5, // Arena-backed UTF-8 arguments and property writes.
    Containers = 1ull << 6, // Arena-backed container inputs.
    OutputAndReference = 1ull << 7,
    Utf8Results = 1ull << 8, // Preflight-safe UTF-8 reads and return values.
    ContainerResults = 1ull << 9, // Preflight-safe container reads and returns.
    Structs = 1ull << 10, // Generated field-addressable UScriptStruct values.
    ByteBuffers = 1ull << 11,
    CallbackArguments = 1ull << 12,
    CorrelatedResults = 1ull << 13,
    BlueprintParentCalls = 1ull << 14,
};

enum class EFinareNativeCallFrameFlagsV3 : uint32
{
    None = 0,
    BlueprintParent = 1u << 0,
};

/**
 * Fixed-size ABI v3 value cell.
 *
 * Scalars, object handles, FVector, and FRotator fit inline. Variable-size
 * values reference the caller-owned byte arena by offset and length; no
 * allocator ownership crosses the ABI boundary.
 */
struct alignas(8) FFinareNativeValueV3
{
    EFinareNativeValueKindV3 Kind = EFinareNativeValueKindV3::Void;
    EFinareNativeValueFlagsV3 Flags = EFinareNativeValueFlagsV3::None;
    uint16 Reserved16 = 0;
    uint32 Auxiliary = 0;
    uint64 TypeId = 0;
    uint64 Payload0 = 0;
    uint64 Payload1 = 0;
    uint64 Payload2 = 0;
    uint64 Payload3 = 0;
};

/**
 * Caller-owned synchronous ABI v3 request/response frame.
 *
 * Arguments, results, and the byte arena remain borrowed for the duration of
 * a call. BufferTooSmall reports required capacities and produces no partial
 * result. Flags are zero for ordinary operations and BlueprintParent for the
 * correlated parent operation; every reserved field must remain zero.
 */
struct alignas(8) FFinareNativeCallFrameV3
{
    const FFinareNativeValueV3* Arguments = nullptr;
    uint32 ArgumentCount = 0;
    uint32 Flags = 0;
    FFinareNativeValueV3* Results = nullptr;
    uint32 ResultCapacity = 0;
    // In/out: expected result descriptors on entry, produced cells on success.
    uint32 ResultCount = 0;
    uint8* ByteBuffer = nullptr;
    uint32 ByteCapacity = 0;
    // In/out arena cursor: input bytes occupied on entry, total bytes used on success.
    uint32 ByteCount = 0;
    uint32 RequiredResultCapacity = 0;
    uint32 RequiredByteCapacity = 0;
    uint8 InvocationError = 0;
    uint8 HandleError = 0;
    EFinareNativeCallStatusV3 Status =
        EFinareNativeCallStatusV3::Uninitialized;
    uint8 Reserved8 = 0;
    uint32 ErrorMessageOffset = 0;
    uint32 ErrorMessageLength = 0;
    uint32 Reserved32 = 0;
};

using FFinareNativeExecuteByIdV3 = void (*)(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    FFinareNativeCallFrameV3* Frame);

using FFinareNativeInvokeCorrelatedByIdV3 = void (*)(
    void* Context, uint64 CorrelationId, uint64 Target, uint64 MemberId,
    FFinareNativeCallFrameV3* Frame);
using FFinareNativeReleaseCorrelatedResultV3 = void (*)(void* Context, uint64 CorrelationId);

/** Allocation-free scalar lane for generated int32(int32, int32) members. */
using FFinareNativeInvokeInt32x2ByIdV3 = uint8 (*)(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    int32 Argument0,
    int32 Argument1,
    int32* OutReturnValue,
    uint8* OutInvocationError,
    uint8* OutHandleError);

/** Allocation-free lane for 0-4 fixed int/float/bool/object/vector values. */
using FFinareNativeInvokeFixedByIdV3 = uint8 (*)(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    const FFinareNativeValueV3* Arguments,
    uint32 ArgumentCount,
    FFinareNativeValueV3* OutReturnValue,
    uint8* OutInvocationError,
    uint8* OutHandleError);

/**
 * ABI v3 is intentionally independent from v1/v2 because its caller-owned
 * call-frame semantics are not prefix-compatible with the callback contract.
 * The exported table is activated only after its marshaller is implemented.
 */
struct alignas(8) FFinareNativeTransportV3
{
    uint32 AbiVersion = 3;
    uint32 TableSize = 0;
    uint32 ValueSize = 0;
    uint32 CallFrameSize = 0;
    uint64 Capabilities = 0;
    FFinareNativeRetainContextV1 RetainContext = nullptr;
    FFinareNativeReleaseContextV1 ReleaseContext = nullptr;
    FFinareNativeExecuteByIdV3 ReadPropertyById = nullptr;
    FFinareNativeExecuteByIdV3 WritePropertyById = nullptr;
    FFinareNativeExecuteByIdV3 InvokeById = nullptr;
    /** Optional append-only scalar fast lane; older ABI v3 clients ignore it. */
    FFinareNativeInvokeInt32x2ByIdV3 InvokeInt32x2ById = nullptr;
    /** Optional append-only mixed fixed-value lane; nullptr result denotes void. */
    FFinareNativeInvokeFixedByIdV3 InvokeFixedById = nullptr;
    /** Optional logical invocation identity, independent of worker/thread identity. */
    FFinareNativeInvokeCorrelatedByIdV3 InvokeCorrelatedById = nullptr;
    FFinareNativeReleaseCorrelatedResultV3 ReleaseCorrelatedResult = nullptr;
};

static_assert(std::is_standard_layout_v<FFinareNativeValueV1>);
static_assert(std::is_standard_layout_v<FFinareNativeTransportV1>);
static_assert(std::is_standard_layout_v<FFinareNativeTransportV2>);
static_assert(std::is_standard_layout_v<FFinareNativeValueV3>);
static_assert(std::is_trivially_copyable_v<FFinareNativeValueV3>);
static_assert(sizeof(FFinareNativeValueV3) == 48);
static_assert(alignof(FFinareNativeValueV3) == 8);
static_assert(STRUCT_OFFSET(FFinareNativeValueV3, TypeId) == 8);
static_assert(STRUCT_OFFSET(FFinareNativeValueV3, Payload0) == 16);
static_assert(std::is_standard_layout_v<FFinareNativeCallFrameV3>);
static_assert(std::is_trivially_copyable_v<FFinareNativeCallFrameV3>);
static_assert(sizeof(FFinareNativeCallFrameV3) == 72);
static_assert(alignof(FFinareNativeCallFrameV3) == 8);
static_assert(STRUCT_OFFSET(FFinareNativeCallFrameV3, Results) == 16);
static_assert(STRUCT_OFFSET(FFinareNativeCallFrameV3, ByteBuffer) == 32);
static_assert(STRUCT_OFFSET(FFinareNativeCallFrameV3, Status) == 58);
static_assert(STRUCT_OFFSET(FFinareNativeCallFrameV3, ErrorMessageOffset) == 60);
static_assert(std::is_standard_layout_v<FFinareNativeTransportV3>);
static_assert(std::is_trivially_copyable_v<FFinareNativeTransportV3>);
static_assert(sizeof(FFinareNativeTransportV3) == 96);
static_assert(alignof(FFinareNativeTransportV3) == 8);
static_assert(STRUCT_OFFSET(FFinareNativeTransportV3, Capabilities) == 16);
static_assert(STRUCT_OFFSET(FFinareNativeTransportV3, RetainContext) == 24);
static_assert(STRUCT_OFFSET(FFinareNativeTransportV3, InvokeById) == 56);
static_assert(STRUCT_OFFSET(FFinareNativeTransportV3, InvokeInt32x2ById) == 64);
static_assert(STRUCT_OFFSET(FFinareNativeTransportV3, InvokeFixedById) == 72);
static_assert(STRUCT_OFFSET(FFinareNativeTransportV3, InvokeCorrelatedById) == 80);
static_assert(STRUCT_OFFSET(FFinareNativeTransportV3, ReleaseCorrelatedResult) == 88);
static_assert(std::is_standard_layout_v<FFinareNativeEventFrameV4>);
static_assert(std::is_trivially_copyable_v<FFinareNativeEventFrameV4>);
static_assert(sizeof(FFinareNativeEventFrameV4) == 64);
static_assert(alignof(FFinareNativeEventFrameV4) == 8);
static_assert(STRUCT_OFFSET(FFinareNativeEventFrameV4, Arguments) == 32);
static_assert(STRUCT_OFFSET(FFinareNativeEventFrameV4, ByteBuffer) == 48);
static_assert(std::is_standard_layout_v<FFinareNativeEventTransportV4>);
static_assert(std::is_trivially_copyable_v<FFinareNativeEventTransportV4>);
static_assert(sizeof(FFinareNativeEventTransportV4) == 48);
static_assert(
    STRUCT_OFFSET(FFinareNativeTransportV2, ReadPropertyById) ==
    sizeof(FFinareNativeTransportV1));
static_assert(
    STRUCT_OFFSET(FFinareNativeTransportV1, ReleaseContext) ==
    STRUCT_OFFSET(FFinareNativeTransportV2, ReleaseContext));
static_assert(
    STRUCT_OFFSET(FFinareNativeTransportV2, GameplayDataTransport) ==
    STRUCT_OFFSET(FFinareNativeTransportV2, SharedDataTransport) + sizeof(void*));
static_assert(
    STRUCT_OFFSET(FFinareNativeTransportV2, AsyncTransport) ==
    STRUCT_OFFSET(FFinareNativeTransportV2, GameplayDataTransport) + sizeof(void*));
static_assert(
    STRUCT_OFFSET(FFinareNativeTransportV2, NativeObjectTransport) ==
    STRUCT_OFFSET(FFinareNativeTransportV2, AsyncTransport) + sizeof(void*));

extern "C" FINARERUNTIME_API const FFinareNativeTransportV1*
Finare_GetNativeTransportV1();

extern "C" FINARERUNTIME_API const FFinareNativeTransportV2*
Finare_GetNativeTransportV2();

extern "C" FINARERUNTIME_API const FFinareNativeTransportV3*
Finare_GetNativeTransportV3();

extern "C" FINARERUNTIME_API const FFinareNativeEventTransportV4*
Finare_GetNativeEventTransportV4();

extern "C" FINARERUNTIME_API const FFinareGameplayDataTransportV4*
Finare_GetGameplayDataTransportV4();
