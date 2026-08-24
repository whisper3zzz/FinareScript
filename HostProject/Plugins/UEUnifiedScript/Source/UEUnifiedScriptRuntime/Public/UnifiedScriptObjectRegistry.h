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
struct UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptObjectHandle
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

    static FUnifiedScriptObjectHandle Make(uint32 SlotIndex, uint32 Generation)
    {
        check(SlotIndex != MAX_uint32);
        check(Generation != 0);
        return {static_cast<uint64>(Generation) << 32 | static_cast<uint64>(SlotIndex + 1)};
    }

    friend bool operator==(const FUnifiedScriptObjectHandle&, const FUnifiedScriptObjectHandle&) = default;

private:
    uint32 GetEncodedSlot() const
    {
        return static_cast<uint32>(Value);
    }
};

/** Stable failure categories returned before any future marshalling is attempted. */
enum class EUnifiedScriptObjectHandleError : uint8
{
    None = 0,
    NotGameThread = 1,
    RegistryWorldUnavailable = 2,
    NullObject = 3,
    WorldMismatch = 4,
    InvalidHandle = 5,
    StaleHandle = 6,
};

struct UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptObjectHandleResult
{
    EUnifiedScriptObjectHandleError Error = EUnifiedScriptObjectHandleError::None;
    FString Message;

    bool IsSuccess() const
    {
        return Error == EUnifiedScriptObjectHandleError::None;
    }
};

/**
 * Game-thread-only, world-bound weak UObject handle registry.
 *
 * The registry never owns registered objects. UObject deletion notifications
 * retire their slots immediately, while generation checks prevent a recycled
 * slot from reviving an old managed handle.
 */
class UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptObjectRegistry
{
public:
    explicit FUnifiedScriptObjectRegistry(UWorld* InWorld);
    ~FUnifiedScriptObjectRegistry();

    FUnifiedScriptObjectRegistry(const FUnifiedScriptObjectRegistry&) = delete;
    FUnifiedScriptObjectRegistry& operator=(const FUnifiedScriptObjectRegistry&) = delete;

    FUnifiedScriptObjectHandle RegisterObject(
        UObject* Object,
        FUnifiedScriptObjectHandleResult& OutResult);

    UObject* ResolveObject(
        FUnifiedScriptObjectHandle Handle,
        FUnifiedScriptObjectHandleResult& OutResult);

    bool ReleaseObject(
        FUnifiedScriptObjectHandle Handle,
        FUnifiedScriptObjectHandleResult& OutResult);

    bool InvalidateAll(FUnifiedScriptObjectHandleResult& OutResult);

    int32 Num() const;

private:
    struct FImpl;
    TUniquePtr<FImpl> Impl;
};
