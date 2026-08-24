#pragma once

#include "Containers/ArrayView.h"
#include "CoreMinimal.h"
#include "UnifiedScriptObjectRegistry.h"

#include <atomic>

/** Stable 64-bit FNV-1a identity shared with generated managed wrappers. */
constexpr uint64 UnifiedScriptMemberId(const ANSICHAR* Path)
{
    uint64 Hash = 14695981039346656037ull;
    while (*Path != '\0')
    {
        Hash ^= static_cast<uint8>(*Path++);
        Hash *= 1099511628211ull;
    }
    return Hash;
}

/** The first native marshalling slice deliberately matches the live fixture. */
enum class EUnifiedScriptValueKind : uint8
{
    Void = 0,
    Boolean = 1,
    Int32 = 2,
    String = 3,
};

/** Owned value transport. No address into UObject or UFunction storage escapes. */
struct UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptValue
{
    EUnifiedScriptValueKind Kind = EUnifiedScriptValueKind::Void;
    bool BooleanValue = false;
    int32 Int32Value = 0;
    FString StringValue;

    static FUnifiedScriptValue FromBoolean(bool Value);
    static FUnifiedScriptValue FromInt32(int32 Value);
    static FUnifiedScriptValue FromString(FString Value);

    bool TryGetBoolean(bool& OutValue) const;
    bool TryGetInt32(int32& OutValue) const;
    bool TryGetString(FString& OutValue) const;
};

/** Stable failure categories for member resolution, marshalling, and invocation. */
enum class EUnifiedScriptInvocationError : uint8
{
    None = 0,
    ObjectHandleFailure = 1,
    InvalidMemberPath = 2,
    TargetTypeNotFound = 3,
    TargetTypeMismatch = 4,
    PropertyNotFound = 5,
    PropertyNotReadable = 6,
    PropertyNotWritable = 7,
    FunctionNotFound = 8,
    FunctionNotCallable = 9,
    UnsupportedValueType = 10,
    ValueTypeMismatch = 11,
    ArgumentCountMismatch = 12,
    UnsupportedParameter = 13,
    TransportUnavailable = 14,
};

struct UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptInvocationResult
{
    EUnifiedScriptInvocationError Error = EUnifiedScriptInvocationError::None;
    EUnifiedScriptObjectHandleError HandleError = EUnifiedScriptObjectHandleError::None;
    FString Message;

    bool IsSuccess() const
    {
        return Error == EUnifiedScriptInvocationError::None;
    }
};

/**
 * Game-thread-only native invocation boundary.
 *
 * Targets are resolved exclusively through the world-bound weak registry.
 * Member paths use the same /Script/Module.Type:Member identity emitted into
 * generated managed wrappers. This slice supports bool, int32, FString, and
 * void; unsupported reflected shapes fail before ProcessEvent is called.
 */
class UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptInvocationBridge
{
public:
    explicit FUnifiedScriptInvocationBridge(FUnifiedScriptObjectRegistry& InRegistry);

    bool ReadProperty(
        FUnifiedScriptObjectHandle Target,
        const FString& PropertyPath,
        FUnifiedScriptValue& OutValue,
        FUnifiedScriptInvocationResult& OutResult);

    bool WriteProperty(
        FUnifiedScriptObjectHandle Target,
        const FString& PropertyPath,
        const FUnifiedScriptValue& Value,
        FUnifiedScriptInvocationResult& OutResult);

    bool Invoke(
        FUnifiedScriptObjectHandle Target,
        const FString& FunctionPath,
        TConstArrayView<FUnifiedScriptValue> Arguments,
        FUnifiedScriptValue& OutReturnValue,
        FUnifiedScriptInvocationResult& OutResult);

    bool ReadPropertyById(
        FUnifiedScriptObjectHandle Target,
        uint64 MemberId,
        FUnifiedScriptValue& OutValue,
        FUnifiedScriptInvocationResult& OutResult);

    bool WritePropertyById(
        FUnifiedScriptObjectHandle Target,
        uint64 MemberId,
        const FUnifiedScriptValue& Value,
        FUnifiedScriptInvocationResult& OutResult);

    bool InvokeById(
        FUnifiedScriptObjectHandle Target,
        uint64 MemberId,
        TConstArrayView<FUnifiedScriptValue> Arguments,
        FUnifiedScriptValue& OutReturnValue,
        FUnifiedScriptInvocationResult& OutResult);

    uint64 GetFastInvocationCount() const;

private:
    FUnifiedScriptObjectRegistry& Registry;
    std::atomic<uint64> FastInvocationCount{0};
};
