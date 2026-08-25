#pragma once

#include "CoreMinimal.h"
#include "HAL/CriticalSection.h"
#include "Templates/Function.h"
#include "UObject/StrongObjectPtr.h"
#include "UnifiedScriptInvocationBridge.h"

#include <atomic>
#include <type_traits>

/** Stable, standard-layout value passed into the version-one native ABI. */
struct FUnifiedScriptNativeValueV1
{
    uint8 Kind = 0;
    uint8 BooleanValue = 0;
    uint16 Reserved = 0;
    int32 Int32Value = 0;
    const uint8* StringData = nullptr;
    int32 StringLength = 0;
};

using FUnifiedScriptNativeResultCallbackV1 = void (*)(
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

using FUnifiedScriptNativeReadPropertyV1 = void (*)(
    void* Context,
    uint64 Target,
    const uint8* PropertyPathData,
    int32 PropertyPathLength,
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData);

using FUnifiedScriptNativeWritePropertyV1 = void (*)(
    void* Context,
    uint64 Target,
    const uint8* PropertyPathData,
    int32 PropertyPathLength,
    const FUnifiedScriptNativeValueV1* Value,
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData);

using FUnifiedScriptNativeInvokeV1 = void (*)(
    void* Context,
    uint64 Target,
    const uint8* FunctionPathData,
    int32 FunctionPathLength,
    const FUnifiedScriptNativeValueV1* Arguments,
    int32 ArgumentCount,
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData);

using FUnifiedScriptNativeReadPropertyByIdV2 = void (*)(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData);

using FUnifiedScriptNativeWritePropertyByIdV2 = void (*)(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    const FUnifiedScriptNativeValueV1* Value,
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData);

using FUnifiedScriptNativeInvokeByIdV2 = void (*)(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    const FUnifiedScriptNativeValueV1* Arguments,
    int32 ArgumentCount,
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData);

using FUnifiedScriptNativeRetainContextV1 = uint8 (*)(void* Context);
using FUnifiedScriptNativeReleaseContextV1 = void (*)(void* Context);

struct FUnifiedScriptNativeValueV3;

/** Borrowed, synchronous reverse-call frame for one reflected delegate broadcast. */
struct alignas(8) FUnifiedScriptNativeEventFrameV4
{
    uint32 AbiVersion = 4;
    uint32 FrameSize = 0;
    uint64 SubscriptionId = 0;
    uint64 Source = 0;
    uint64 EventId = 0;
    const FUnifiedScriptNativeValueV3* Arguments = nullptr;
    uint32 ArgumentCount = 0;
    uint32 Reserved = 0;
    const uint8* ByteBuffer = nullptr;
    uint32 ByteCount = 0;
    uint32 Reserved2 = 0;
};

using FUnifiedScriptNativeEventCallbackV4 = void (*)(
    void* UserData,
    const FUnifiedScriptNativeEventFrameV4* Frame);
using FUnifiedScriptNativeSubscribeEventV4 = uint64 (*)(
    void* Context,
    uint64 Source,
    uint64 EventId,
    const uint8* EventPathData,
    int32 EventPathLength,
    FUnifiedScriptNativeEventCallbackV4 Callback,
    void* UserData);
using FUnifiedScriptNativeUnsubscribeEventV4 = uint8 (*)(
    void* Context,
    uint64 SubscriptionId);

struct alignas(8) FUnifiedScriptNativeEventTransportV4
{
    uint32 AbiVersion = 4;
    uint32 TableSize = 0;
    uint32 FrameSize = 0;
    uint32 ValueSize = 0;
    FUnifiedScriptNativeRetainContextV1 RetainContext = nullptr;
    FUnifiedScriptNativeReleaseContextV1 ReleaseContext = nullptr;
    FUnifiedScriptNativeSubscribeEventV4 Subscribe = nullptr;
    FUnifiedScriptNativeUnsubscribeEventV4 Unsubscribe = nullptr;
};

/**
 * Ref-counted lifetime guard between a world host and managed transports.
 *
 * The world host owns the initial reference. Managed transports retain their
 * own reference through the ABI table. Detach prevents new bridge calls and
 * waits for any current call before the host destroys the bridge and registry.
 */
class UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptNativeBridgeContext final
{
public:
    struct FPendingCallFrameResult
    {
        bool bValid = false;
        uint8 Operation = 0;
        uint64 Target = 0;
        uint64 MemberId = 0;
        uint64 RequestHash = 0;
        FUnifiedScriptValue ReturnValue;
        TArray<FUnifiedScriptValue> OutputValues;

        void Reset()
        {
            bValid = false;
            Operation = 0;
            Target = 0;
            MemberId = 0;
            RequestHash = 0;
            ReturnValue = {};
            OutputValues.Reset();
        }
    };

    explicit FUnifiedScriptNativeBridgeContext(FUnifiedScriptInvocationBridge& InBridge);

    bool TryRetain();
    void Release();
    void Detach();
    bool Execute(TFunctionRef<void(FUnifiedScriptInvocationBridge&)> Operation);
    bool ExecuteGameThreadFast(
        TFunctionRef<void(FUnifiedScriptInvocationBridge&)> Operation);
    bool ExecuteCallFrame(
        TFunctionRef<void(
            FUnifiedScriptInvocationBridge&,
            FPendingCallFrameResult&)> Operation);
    bool SubscribeEvent(
        FUnifiedScriptObjectHandle Source,
        uint64 EventId,
        const FString& EventPath,
        FUnifiedScriptNativeEventCallbackV4 Callback,
        void* UserData,
        uint64& OutSubscriptionId);
    bool UnsubscribeEvent(uint64 SubscriptionId);

private:
    ~FUnifiedScriptNativeBridgeContext() = default;

    std::atomic<uint32> ReferenceCount{1};
    FCriticalSection BridgeLock;
    FUnifiedScriptInvocationBridge* Bridge = nullptr;
    std::atomic<FUnifiedScriptInvocationBridge*> FastBridge{nullptr};
    FPendingCallFrameResult PendingCallFrameResult;
    uint64 NextEventSubscriptionId = 1;
    TMap<uint64, TStrongObjectPtr<UObject>> EventSubscriptions;
};

/**
 * Versioned synchronous function table consumed by the managed transport.
 *
 * Context must point to a live FUnifiedScriptInvocationBridge owned by the
 * future world runtime host. Input slices remain caller-owned. Output slices
 * remain valid only for the duration of Callback and must be copied there.
 */
struct FUnifiedScriptNativeTransportV1
{
    uint32 AbiVersion = 1;
    uint32 TableSize = 0;
    uint32 ValueSize = 0;
    uint32 Reserved = 0;
    FUnifiedScriptNativeReadPropertyV1 ReadProperty = nullptr;
    FUnifiedScriptNativeWritePropertyV1 WriteProperty = nullptr;
    FUnifiedScriptNativeInvokeV1 Invoke = nullptr;
    FUnifiedScriptNativeRetainContextV1 RetainContext = nullptr;
    FUnifiedScriptNativeReleaseContextV1 ReleaseContext = nullptr;
};

struct FUnifiedScriptNativeTransportV3;

/** ABI v2 preserves the complete v1 prefix and appends stable-ID fast thunks. */
struct FUnifiedScriptNativeTransportV2
{
    uint32 AbiVersion = 2;
    uint32 TableSize = 0;
    uint32 ValueSize = 0;
    uint32 Reserved = 0;
    FUnifiedScriptNativeReadPropertyV1 ReadProperty = nullptr;
    FUnifiedScriptNativeWritePropertyV1 WriteProperty = nullptr;
    FUnifiedScriptNativeInvokeV1 Invoke = nullptr;
    FUnifiedScriptNativeRetainContextV1 RetainContext = nullptr;
    FUnifiedScriptNativeReleaseContextV1 ReleaseContext = nullptr;
    FUnifiedScriptNativeReadPropertyByIdV2 ReadPropertyById = nullptr;
    FUnifiedScriptNativeWritePropertyByIdV2 WritePropertyById = nullptr;
    FUnifiedScriptNativeInvokeByIdV2 InvokeById = nullptr;
    /** Optional independent ABI v3 table; older v2 clients ignore this suffix. */
    const FUnifiedScriptNativeTransportV3* CallFrameTransport = nullptr;
    /** Optional reverse event ABI; older v2 clients ignore this suffix. */
    const FUnifiedScriptNativeEventTransportV4* EventTransport = nullptr;
};

/**
 * ABI v3 value kinds. The numeric values are part of the native/managed wire
 * contract and must only be extended by appending new entries.
 */
enum class EUnifiedScriptNativeValueKindV3 : uint8
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
};

enum class EUnifiedScriptNativeValueFlagsV3 : uint8
{
    None = 0,
    Null = 1 << 0,
    ArenaBacked = 1 << 1,
};

/** Overall disposition of one synchronous ABI v3 call frame. */
enum class EUnifiedScriptNativeCallStatusV3 : uint8
{
    Uninitialized = 0,
    Success = 1,
    InvocationFailure = 2,
    BufferTooSmall = 3,
    InvalidFrame = 4,
    TransportUnavailable = 5,
};

enum class EUnifiedScriptNativeCapabilitiesV3 : uint64
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
};

/**
 * Fixed-size ABI v3 value cell.
 *
 * Scalars, object handles, FVector, and FRotator fit inline. Variable-size
 * values reference the caller-owned byte arena by offset and length; no
 * allocator ownership crosses the ABI boundary.
 */
struct alignas(8) FUnifiedScriptNativeValueV3
{
    EUnifiedScriptNativeValueKindV3 Kind = EUnifiedScriptNativeValueKindV3::Void;
    EUnifiedScriptNativeValueFlagsV3 Flags = EUnifiedScriptNativeValueFlagsV3::None;
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
 * result. Flags and every reserved field must be zero for ABI v3.
 */
struct alignas(8) FUnifiedScriptNativeCallFrameV3
{
    const FUnifiedScriptNativeValueV3* Arguments = nullptr;
    uint32 ArgumentCount = 0;
    uint32 Flags = 0;
    FUnifiedScriptNativeValueV3* Results = nullptr;
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
    EUnifiedScriptNativeCallStatusV3 Status =
        EUnifiedScriptNativeCallStatusV3::Uninitialized;
    uint8 Reserved8 = 0;
    uint32 ErrorMessageOffset = 0;
    uint32 ErrorMessageLength = 0;
    uint32 Reserved32 = 0;
};

using FUnifiedScriptNativeExecuteByIdV3 = void (*)(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    FUnifiedScriptNativeCallFrameV3* Frame);

/** Allocation-free scalar lane for generated int32(int32, int32) members. */
using FUnifiedScriptNativeInvokeInt32x2ByIdV3 = uint8 (*)(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    int32 Argument0,
    int32 Argument1,
    int32* OutReturnValue,
    uint8* OutInvocationError,
    uint8* OutHandleError);

/**
 * ABI v3 is intentionally independent from v1/v2 because its caller-owned
 * call-frame semantics are not prefix-compatible with the callback contract.
 * The exported table is activated only after its marshaller is implemented.
 */
struct alignas(8) FUnifiedScriptNativeTransportV3
{
    uint32 AbiVersion = 3;
    uint32 TableSize = 0;
    uint32 ValueSize = 0;
    uint32 CallFrameSize = 0;
    uint64 Capabilities = 0;
    FUnifiedScriptNativeRetainContextV1 RetainContext = nullptr;
    FUnifiedScriptNativeReleaseContextV1 ReleaseContext = nullptr;
    FUnifiedScriptNativeExecuteByIdV3 ReadPropertyById = nullptr;
    FUnifiedScriptNativeExecuteByIdV3 WritePropertyById = nullptr;
    FUnifiedScriptNativeExecuteByIdV3 InvokeById = nullptr;
    /** Optional append-only scalar fast lane; older ABI v3 clients ignore it. */
    FUnifiedScriptNativeInvokeInt32x2ByIdV3 InvokeInt32x2ById = nullptr;
};

static_assert(std::is_standard_layout_v<FUnifiedScriptNativeValueV1>);
static_assert(std::is_standard_layout_v<FUnifiedScriptNativeTransportV1>);
static_assert(std::is_standard_layout_v<FUnifiedScriptNativeTransportV2>);
static_assert(std::is_standard_layout_v<FUnifiedScriptNativeValueV3>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptNativeValueV3>);
static_assert(sizeof(FUnifiedScriptNativeValueV3) == 48);
static_assert(alignof(FUnifiedScriptNativeValueV3) == 8);
static_assert(STRUCT_OFFSET(FUnifiedScriptNativeValueV3, TypeId) == 8);
static_assert(STRUCT_OFFSET(FUnifiedScriptNativeValueV3, Payload0) == 16);
static_assert(std::is_standard_layout_v<FUnifiedScriptNativeCallFrameV3>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptNativeCallFrameV3>);
static_assert(sizeof(FUnifiedScriptNativeCallFrameV3) == 72);
static_assert(alignof(FUnifiedScriptNativeCallFrameV3) == 8);
static_assert(STRUCT_OFFSET(FUnifiedScriptNativeCallFrameV3, Results) == 16);
static_assert(STRUCT_OFFSET(FUnifiedScriptNativeCallFrameV3, ByteBuffer) == 32);
static_assert(STRUCT_OFFSET(FUnifiedScriptNativeCallFrameV3, Status) == 58);
static_assert(STRUCT_OFFSET(FUnifiedScriptNativeCallFrameV3, ErrorMessageOffset) == 60);
static_assert(std::is_standard_layout_v<FUnifiedScriptNativeTransportV3>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptNativeTransportV3>);
static_assert(sizeof(FUnifiedScriptNativeTransportV3) == 72);
static_assert(alignof(FUnifiedScriptNativeTransportV3) == 8);
static_assert(STRUCT_OFFSET(FUnifiedScriptNativeTransportV3, Capabilities) == 16);
static_assert(STRUCT_OFFSET(FUnifiedScriptNativeTransportV3, RetainContext) == 24);
static_assert(STRUCT_OFFSET(FUnifiedScriptNativeTransportV3, InvokeById) == 56);
static_assert(STRUCT_OFFSET(FUnifiedScriptNativeTransportV3, InvokeInt32x2ById) == 64);
static_assert(std::is_standard_layout_v<FUnifiedScriptNativeEventFrameV4>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptNativeEventFrameV4>);
static_assert(sizeof(FUnifiedScriptNativeEventFrameV4) == 64);
static_assert(alignof(FUnifiedScriptNativeEventFrameV4) == 8);
static_assert(STRUCT_OFFSET(FUnifiedScriptNativeEventFrameV4, Arguments) == 32);
static_assert(STRUCT_OFFSET(FUnifiedScriptNativeEventFrameV4, ByteBuffer) == 48);
static_assert(std::is_standard_layout_v<FUnifiedScriptNativeEventTransportV4>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptNativeEventTransportV4>);
static_assert(sizeof(FUnifiedScriptNativeEventTransportV4) == 48);
static_assert(
    STRUCT_OFFSET(FUnifiedScriptNativeTransportV2, ReadPropertyById) ==
    sizeof(FUnifiedScriptNativeTransportV1));
static_assert(
    STRUCT_OFFSET(FUnifiedScriptNativeTransportV1, ReleaseContext) ==
    STRUCT_OFFSET(FUnifiedScriptNativeTransportV2, ReleaseContext));

extern "C" UEUNIFIEDSCRIPTRUNTIME_API const FUnifiedScriptNativeTransportV1*
UEUnifiedScript_GetNativeTransportV1();

extern "C" UEUNIFIEDSCRIPTRUNTIME_API const FUnifiedScriptNativeTransportV2*
UEUnifiedScript_GetNativeTransportV2();

extern "C" UEUNIFIEDSCRIPTRUNTIME_API const FUnifiedScriptNativeTransportV3*
UEUnifiedScript_GetNativeTransportV3();

extern "C" UEUNIFIEDSCRIPTRUNTIME_API const FUnifiedScriptNativeEventTransportV4*
UEUnifiedScript_GetNativeEventTransportV4();
