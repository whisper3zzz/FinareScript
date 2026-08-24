#pragma once

#include "CoreMinimal.h"
#include "HAL/CriticalSection.h"
#include "Templates/Function.h"
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
    explicit FUnifiedScriptNativeBridgeContext(FUnifiedScriptInvocationBridge& InBridge);

    bool TryRetain();
    void Release();
    void Detach();
    bool Execute(TFunctionRef<void(FUnifiedScriptInvocationBridge&)> Operation);

private:
    ~FUnifiedScriptNativeBridgeContext() = default;

    std::atomic<uint32> ReferenceCount{1};
    FCriticalSection BridgeLock;
    FUnifiedScriptInvocationBridge* Bridge = nullptr;
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
};

static_assert(std::is_standard_layout_v<FUnifiedScriptNativeValueV1>);
static_assert(std::is_standard_layout_v<FUnifiedScriptNativeTransportV1>);
static_assert(std::is_standard_layout_v<FUnifiedScriptNativeTransportV2>);
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
