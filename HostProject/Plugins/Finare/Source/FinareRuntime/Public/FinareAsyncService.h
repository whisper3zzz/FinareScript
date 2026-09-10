#pragma once

#include "CoreMinimal.h"

#include <type_traits>

class FFinareNativeBridgeContext;
class FFinareObjectRegistry;
class UWorld;

namespace UE::Finare::AsyncService
{
inline constexpr uint32 AbiVersion = 5;
inline constexpr uint32 MaximumInFlightRequestCount = 4096;
inline constexpr uint32 MaximumGlobalInFlightRequestCount = 65536;
inline constexpr uint32 MaximumOwnedResourceCount = 4096;
inline constexpr uint32 MaximumGlobalOwnedResourceCount = 16384;
inline constexpr uint32 MaximumPayloadByteCount = 1024 * 1024;
}

enum class EFinareAsyncOperationV5 : uint8
{
    Delay = 1,
    LoadObject = 2,
    LoadClass = 3,
};

enum class EFinareAsyncCompletionStatusV5 : uint8
{
    Succeeded = 1,
    Canceled = 2,
    TimedOut = 3,
    GenerationEnded = 4,
    WorldTornDown = 5,
    Failed = 6,
};

enum class EFinareAsyncCapabilitiesV5 : uint64
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
struct alignas(8) FFinareAsyncRequestV5
{
    uint32 AbiVersion = UE::Finare::AsyncService::AbiVersion;
    uint32 StructSize = sizeof(FFinareAsyncRequestV5);
    uint64 RequestId = 0;
    uint64 OwnerInstanceId = 0;
    uint32 OwnerGeneration = 0;
    EFinareAsyncOperationV5 Operation = EFinareAsyncOperationV5::Delay;
    uint8 Reserved8 = 0;
    uint16 Flags = 0;
    float DurationSeconds = 0.0f;
    float TimeoutSeconds = 0.0f;
    const uint8* PayloadData = nullptr;
    uint32 PayloadLength = 0;
    uint32 Reserved32 = 0;
};

/** Fixed completion row owned by the native World service. */
struct alignas(8) FFinareAsyncCompletionV5
{
    uint64 RequestId = 0;
    uint64 OwnerInstanceId = 0;
    uint32 OwnerGeneration = 0;
    EFinareAsyncOperationV5 Operation = EFinareAsyncOperationV5::Delay;
    EFinareAsyncCompletionStatusV5 Status =
        EFinareAsyncCompletionStatusV5::Failed;
    uint8 HandleError = 0;
    uint8 Reserved8 = 0;
    uint64 ResultHandle = 0;
    uint32 ErrorCode = 0;
    uint32 Reserved32 = 0;
};

/** Borrowed completion view valid until the next service operation. */
struct alignas(8) FFinareAsyncCompletionViewV5
{
    uint32 AbiVersion = UE::Finare::AsyncService::AbiVersion;
    uint32 StructSize = sizeof(FFinareAsyncCompletionViewV5);
    uint64 WorldHostId = 0;
    const FFinareAsyncCompletionV5* Completions = nullptr;
    uint32 Count = 0;
    uint32 Reserved = 0;
    uint64 Sequence = 0;
};

using FFinareAsyncSubmitV5 = uint8 (*)(
    void* Context,
    const FFinareAsyncRequestV5* Request);
using FFinareAsyncPumpOwnerV5 = uint8 (*)(
    void* Context,
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration,
    float DeltaSeconds);
using FFinareAsyncCancelV5 = uint8 (*)(
    void* Context,
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration,
    uint64 RequestId);
using FFinareAsyncPollV5 = uint8 (*)(
    void* Context,
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration,
    FFinareAsyncCompletionViewV5* OutView);
using FFinareAsyncCancelOwnerV5 = uint32 (*)(
    void* Context,
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration);

/** Append-only async/latent service function table. */
struct alignas(8) FFinareAsyncTransportV5
{
    uint32 AbiVersion = UE::Finare::AsyncService::AbiVersion;
    uint32 TableSize = sizeof(FFinareAsyncTransportV5);
    uint32 RequestSize = sizeof(FFinareAsyncRequestV5);
    uint32 CompletionSize = sizeof(FFinareAsyncCompletionV5);
    uint32 CompletionViewSize = sizeof(FFinareAsyncCompletionViewV5);
    uint32 Reserved = 0;
    uint64 Capabilities = 0;
    uint8 (*RetainContext)(void* Context) = nullptr;
    void (*ReleaseContext)(void* Context) = nullptr;
    FFinareAsyncSubmitV5 SubmitRequest = nullptr;
    FFinareAsyncPumpOwnerV5 PumpOwner = nullptr;
    FFinareAsyncCancelV5 CancelRequest = nullptr;
    FFinareAsyncPollV5 PollCompletions = nullptr;
    FFinareAsyncCancelOwnerV5 CancelOwner = nullptr;
};

/** Game-thread World-owned implementation behind the ABI table. */
class FINARERUNTIME_API FFinareAsyncService final
{
public:
    FFinareAsyncService(
        UWorld& InWorld,
        FFinareObjectRegistry& InRegistry,
        uint64 InWorldHostId);
    ~FFinareAsyncService();

    bool Submit(const FFinareAsyncRequestV5& Request);
    bool PumpOwner(uint64 OwnerInstanceId, uint32 OwnerGeneration, float DeltaSeconds);
    bool Cancel(
        uint64 OwnerInstanceId,
        uint32 OwnerGeneration,
        uint64 RequestId);
    bool Poll(
        uint64 OwnerInstanceId,
        uint32 OwnerGeneration,
        FFinareAsyncCompletionViewV5& OutView);
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

static_assert(std::is_standard_layout_v<FFinareAsyncRequestV5>);
static_assert(std::is_trivially_copyable_v<FFinareAsyncRequestV5>);
static_assert(sizeof(FFinareAsyncRequestV5) == 56);
static_assert(STRUCT_OFFSET(FFinareAsyncRequestV5, PayloadData) == 40);
static_assert(std::is_standard_layout_v<FFinareAsyncCompletionV5>);
static_assert(std::is_trivially_copyable_v<FFinareAsyncCompletionV5>);
static_assert(sizeof(FFinareAsyncCompletionV5) == 40);
static_assert(STRUCT_OFFSET(FFinareAsyncCompletionV5, ResultHandle) == 24);
static_assert(std::is_standard_layout_v<FFinareAsyncCompletionViewV5>);
static_assert(std::is_trivially_copyable_v<FFinareAsyncCompletionViewV5>);
static_assert(sizeof(FFinareAsyncCompletionViewV5) == 40);
static_assert(STRUCT_OFFSET(FFinareAsyncCompletionViewV5, Completions) == 16);
static_assert(std::is_standard_layout_v<FFinareAsyncTransportV5>);
static_assert(std::is_trivially_copyable_v<FFinareAsyncTransportV5>);
static_assert(sizeof(FFinareAsyncTransportV5) == 88);
static_assert(STRUCT_OFFSET(FFinareAsyncTransportV5, Capabilities) == 24);

extern "C" FINARERUNTIME_API const FFinareAsyncTransportV5*
Finare_GetAsyncTransportV5();
