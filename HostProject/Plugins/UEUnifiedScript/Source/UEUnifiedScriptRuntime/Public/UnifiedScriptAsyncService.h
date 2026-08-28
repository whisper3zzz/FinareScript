#pragma once

#include "CoreMinimal.h"

#include <type_traits>

class FUnifiedScriptNativeBridgeContext;
class FUnifiedScriptObjectRegistry;
class UWorld;

namespace UE::UnifiedScript::AsyncService
{
inline constexpr uint32 AbiVersion = 5;
inline constexpr uint32 MaximumInFlightRequestCount = 4096;
inline constexpr uint32 MaximumGlobalInFlightRequestCount = 65536;
inline constexpr uint32 MaximumOwnedResourceCount = 4096;
inline constexpr uint32 MaximumGlobalOwnedResourceCount = 16384;
inline constexpr uint32 MaximumPayloadByteCount = 1024 * 1024;
}

enum class EUnifiedScriptAsyncOperationV5 : uint8
{
    Delay = 1,
    LoadObject = 2,
    LoadClass = 3,
};

enum class EUnifiedScriptAsyncCompletionStatusV5 : uint8
{
    Succeeded = 1,
    Canceled = 2,
    TimedOut = 3,
    GenerationEnded = 4,
    WorldTornDown = 5,
    Failed = 6,
};

enum class EUnifiedScriptAsyncCapabilitiesV5 : uint64
{
    None = 0,
    Delay = 1ull << 0,
    AsyncAssetLoad = 1ull << 1,
    Cancellation = 1ull << 2,
    Timeout = 1ull << 3,
    GenerationOwnership = 1ull << 4,
    WorldTeardown = 1ull << 5,
};

/** Caller-owned request borrowed only for SubmitRequest. */
struct alignas(8) FUnifiedScriptAsyncRequestV5
{
    uint32 AbiVersion = UE::UnifiedScript::AsyncService::AbiVersion;
    uint32 StructSize = sizeof(FUnifiedScriptAsyncRequestV5);
    uint64 RequestId = 0;
    uint64 OwnerInstanceId = 0;
    uint32 OwnerGeneration = 0;
    EUnifiedScriptAsyncOperationV5 Operation = EUnifiedScriptAsyncOperationV5::Delay;
    uint8 Reserved8 = 0;
    uint16 Flags = 0;
    float DurationSeconds = 0.0f;
    float TimeoutSeconds = 0.0f;
    const uint8* PayloadData = nullptr;
    uint32 PayloadLength = 0;
    uint32 Reserved32 = 0;
};

/** Fixed completion row owned by the native World service. */
struct alignas(8) FUnifiedScriptAsyncCompletionV5
{
    uint64 RequestId = 0;
    uint64 OwnerInstanceId = 0;
    uint32 OwnerGeneration = 0;
    EUnifiedScriptAsyncOperationV5 Operation = EUnifiedScriptAsyncOperationV5::Delay;
    EUnifiedScriptAsyncCompletionStatusV5 Status =
        EUnifiedScriptAsyncCompletionStatusV5::Failed;
    uint8 HandleError = 0;
    uint8 Reserved8 = 0;
    uint64 ResultHandle = 0;
    uint32 ErrorCode = 0;
    uint32 Reserved32 = 0;
};

/** Borrowed completion view valid until the next service operation. */
struct alignas(8) FUnifiedScriptAsyncCompletionViewV5
{
    uint32 AbiVersion = UE::UnifiedScript::AsyncService::AbiVersion;
    uint32 StructSize = sizeof(FUnifiedScriptAsyncCompletionViewV5);
    uint64 WorldHostId = 0;
    const FUnifiedScriptAsyncCompletionV5* Completions = nullptr;
    uint32 Count = 0;
    uint32 Reserved = 0;
    uint64 Sequence = 0;
};

using FUnifiedScriptAsyncSubmitV5 = uint8 (*)(
    void* Context,
    const FUnifiedScriptAsyncRequestV5* Request);
using FUnifiedScriptAsyncPumpOwnerV5 = uint8 (*)(
    void* Context,
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration,
    float DeltaSeconds);
using FUnifiedScriptAsyncCancelV5 = uint8 (*)(
    void* Context,
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration,
    uint64 RequestId);
using FUnifiedScriptAsyncPollV5 = uint8 (*)(
    void* Context,
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration,
    FUnifiedScriptAsyncCompletionViewV5* OutView);
using FUnifiedScriptAsyncCancelOwnerV5 = uint32 (*)(
    void* Context,
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration);

/** Append-only async/latent service function table. */
struct alignas(8) FUnifiedScriptAsyncTransportV5
{
    uint32 AbiVersion = UE::UnifiedScript::AsyncService::AbiVersion;
    uint32 TableSize = sizeof(FUnifiedScriptAsyncTransportV5);
    uint32 RequestSize = sizeof(FUnifiedScriptAsyncRequestV5);
    uint32 CompletionSize = sizeof(FUnifiedScriptAsyncCompletionV5);
    uint32 CompletionViewSize = sizeof(FUnifiedScriptAsyncCompletionViewV5);
    uint32 Reserved = 0;
    uint64 Capabilities = 0;
    uint8 (*RetainContext)(void* Context) = nullptr;
    void (*ReleaseContext)(void* Context) = nullptr;
    FUnifiedScriptAsyncSubmitV5 SubmitRequest = nullptr;
    FUnifiedScriptAsyncPumpOwnerV5 PumpOwner = nullptr;
    FUnifiedScriptAsyncCancelV5 CancelRequest = nullptr;
    FUnifiedScriptAsyncPollV5 PollCompletions = nullptr;
    FUnifiedScriptAsyncCancelOwnerV5 CancelOwner = nullptr;
};

/** Game-thread World-owned implementation behind the ABI table. */
class UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptAsyncService final
{
public:
    FUnifiedScriptAsyncService(
        UWorld& InWorld,
        FUnifiedScriptObjectRegistry& InRegistry,
        uint64 InWorldHostId);
    ~FUnifiedScriptAsyncService();

    bool Submit(const FUnifiedScriptAsyncRequestV5& Request);
    bool PumpOwner(uint64 OwnerInstanceId, uint32 OwnerGeneration, float DeltaSeconds);
    bool Cancel(
        uint64 OwnerInstanceId,
        uint32 OwnerGeneration,
        uint64 RequestId);
    bool Poll(
        uint64 OwnerInstanceId,
        uint32 OwnerGeneration,
        FUnifiedScriptAsyncCompletionViewV5& OutView);
    uint32 CancelOwner(uint64 OwnerInstanceId, uint32 OwnerGeneration);
    void Shutdown();
    int32 NumPending() const;
    int32 NumCompleted() const;
    int32 NumOwnedResources() const;

    /** Process-wide diagnostics used by World/PIE lifecycle soak gates. */
    static int32 GetLiveServiceCount();
    static int32 GetGlobalPendingRequestCount();
    static int32 GetGlobalQueuedCompletionCount();
    static int32 GetGlobalOwnedResourceCount();
    static uint64 GetGlobalCapacityRejectionCount();
    static uint64 GetGlobalPayloadRejectionCount();

private:
    struct FImpl;
    TUniquePtr<FImpl> Impl;
};

static_assert(std::is_standard_layout_v<FUnifiedScriptAsyncRequestV5>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptAsyncRequestV5>);
static_assert(sizeof(FUnifiedScriptAsyncRequestV5) == 56);
static_assert(STRUCT_OFFSET(FUnifiedScriptAsyncRequestV5, PayloadData) == 40);
static_assert(std::is_standard_layout_v<FUnifiedScriptAsyncCompletionV5>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptAsyncCompletionV5>);
static_assert(sizeof(FUnifiedScriptAsyncCompletionV5) == 40);
static_assert(STRUCT_OFFSET(FUnifiedScriptAsyncCompletionV5, ResultHandle) == 24);
static_assert(std::is_standard_layout_v<FUnifiedScriptAsyncCompletionViewV5>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptAsyncCompletionViewV5>);
static_assert(sizeof(FUnifiedScriptAsyncCompletionViewV5) == 40);
static_assert(STRUCT_OFFSET(FUnifiedScriptAsyncCompletionViewV5, Completions) == 16);
static_assert(std::is_standard_layout_v<FUnifiedScriptAsyncTransportV5>);
static_assert(std::is_trivially_copyable_v<FUnifiedScriptAsyncTransportV5>);
static_assert(sizeof(FUnifiedScriptAsyncTransportV5) == 88);
static_assert(STRUCT_OFFSET(FUnifiedScriptAsyncTransportV5, Capabilities) == 24);

extern "C" UEUNIFIEDSCRIPTRUNTIME_API const FUnifiedScriptAsyncTransportV5*
UEUnifiedScript_GetAsyncTransportV5();
