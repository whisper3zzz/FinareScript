#pragma once

#include "CoreMinimal.h"

class UObject;
class UWorld;

/**
 * Opaque handle shared with managed bindings.
 *
 * The low 32 bits contain slot + 1 and the high 32 bits contain a non-zero
 * generation. A value of zero is always null. No UObject address crosses this
 * boundary.
 */
struct FINARERUNTIME_API FFinareObjectHandle
{
    uint64 Value = 0;

    bool IsNull() const
    {
        return Value == 0;
    }

    bool IsWellFormed() const
    {
        return GetEncodedSlot() != 0 && GetGeneration() != 0;
    }

    uint32 GetSlotIndex() const
    {
        check(IsWellFormed());
        return GetEncodedSlot() - 1;
    }

    uint32 GetGeneration() const
    {
        return static_cast<uint32>(Value >> 32);
    }

    static FFinareObjectHandle Make(uint32 SlotIndex, uint32 Generation)
    {
        check(SlotIndex != MAX_uint32);
        check(Generation != 0);
        return {static_cast<uint64>(Generation) << 32 | static_cast<uint64>(SlotIndex + 1)};
    }

    friend bool operator==(const FFinareObjectHandle&, const FFinareObjectHandle&) = default;

private:
    uint32 GetEncodedSlot() const
    {
        return static_cast<uint32>(Value);
    }
};

/** Stable failure categories returned before any future marshalling is attempted. */
enum class EFinareObjectHandleError : uint8
{
    None = 0,
    NotGameThread = 1,
    RegistryWorldUnavailable = 2,
    NullObject = 3,
    WorldMismatch = 4,
    InvalidHandle = 5,
    StaleHandle = 6,
};

struct FINARERUNTIME_API FFinareObjectHandleResult
{
    EFinareObjectHandleError Error = EFinareObjectHandleError::None;
    FString Message;

    bool IsSuccess() const
    {
        return Error == EFinareObjectHandleError::None;
    }
};

/**
 * Game-thread-only, world-bound weak UObject handle registry.
 *
 * The registry never owns registered objects. UObject deletion notifications
 * retire their slots immediately, while generation checks prevent a recycled
 * slot from reviving an old managed handle.
 */
class FINARERUNTIME_API FFinareObjectRegistry
{
public:
    explicit FFinareObjectRegistry(UWorld* InWorld);
    ~FFinareObjectRegistry();

    FFinareObjectRegistry(const FFinareObjectRegistry&) = delete;
    FFinareObjectRegistry& operator=(const FFinareObjectRegistry&) = delete;

    FFinareObjectHandle RegisterObject(
        UObject* Object,
        FFinareObjectHandleResult& OutResult);

    UObject* ResolveObject(
        FFinareObjectHandle Handle,
        FFinareObjectHandleResult& OutResult);

    bool ReleaseObject(
        FFinareObjectHandle Handle,
        FFinareObjectHandleResult& OutResult);

    bool InvalidateAll(FFinareObjectHandleResult& OutResult);

    int32 Num() const;

private:
    struct FImpl;
    TUniquePtr<FImpl> Impl;
};
