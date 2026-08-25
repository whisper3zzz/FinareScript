#pragma once

#include "Containers/ArrayView.h"
#include "CoreMinimal.h"
#include "UnifiedScriptObjectRegistry.h"

#include <atomic>

class FMulticastDelegateProperty;
struct FUnifiedScriptNativeValueV3;

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

/** Owned native value kinds. Existing v1 values retain their original numbers. */
enum class EUnifiedScriptValueKind : uint8
{
    Void = 0,
    Boolean = 1,
    Int32 = 2,
    String = 3,
    Int8 = 4,
    UInt8 = 5,
    Int16 = 6,
    UInt16 = 7,
    UInt32 = 8,
    Int64 = 9,
    UInt64 = 10,
    Float32 = 11,
    Float64 = 12,
    Name = 13,
    Text = 14,
    Object = 15,
    Class = 16,
    Enum = 17,
    Vector = 18,
    Rotator = 19,
    Transform = 20,
    Struct = 21,
    Array = 22,
    Set = 23,
    Map = 24,
};

/** Owned value transport. No address into UObject or UFunction storage escapes. */
struct UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptValue
{
    EUnifiedScriptValueKind Kind = EUnifiedScriptValueKind::Void;
    bool BooleanValue = false;
    int32 Int32Value = 0;
    int8 Int8Value = 0;
    uint8 UInt8Value = 0;
    int16 Int16Value = 0;
    uint16 UInt16Value = 0;
    uint32 UInt32Value = 0;
    int64 Int64Value = 0;
    uint64 UInt64Value = 0;
    float Float32Value = 0.0f;
    double Float64Value = 0.0;
    FString StringValue;
    FName NameValue;
    FText TextValue;
    FUnifiedScriptObjectHandle ObjectHandleValue;
    FName TypePath;
    uint64 TypeIdValue = 0;
    FVector VectorValue = FVector::ZeroVector;
    FRotator RotatorValue = FRotator::ZeroRotator;
    FTransform TransformValue = FTransform::Identity;
    // Array/Set elements, or flattened alternating Map key/value cells.
    TArray<FUnifiedScriptValue> ContainerValues;

    static FUnifiedScriptValue FromBoolean(bool Value);
    static FUnifiedScriptValue FromInt32(int32 Value);
    static FUnifiedScriptValue FromString(FString Value);
    static FUnifiedScriptValue FromInt8(int8 Value);
    static FUnifiedScriptValue FromUInt8(uint8 Value);
    static FUnifiedScriptValue FromInt16(int16 Value);
    static FUnifiedScriptValue FromUInt16(uint16 Value);
    static FUnifiedScriptValue FromUInt32(uint32 Value);
    static FUnifiedScriptValue FromInt64(int64 Value);
    static FUnifiedScriptValue FromUInt64(uint64 Value);
    static FUnifiedScriptValue FromFloat32(float Value);
    static FUnifiedScriptValue FromFloat64(double Value);
    static FUnifiedScriptValue FromName(FName Value);
    static FUnifiedScriptValue FromText(FText Value);
    static FUnifiedScriptValue FromObjectHandle(
        FUnifiedScriptObjectHandle Value,
        bool bClass = false);
    static FUnifiedScriptValue FromEnum(int64 Value, FName TypePath);
    static FUnifiedScriptValue FromEnumTypeId(int64 Value, uint64 TypeId);
    static FUnifiedScriptValue FromVector(FVector Value);
    static FUnifiedScriptValue FromRotator(FRotator Value);
    static FUnifiedScriptValue FromTransform(FTransform Value);
    static FUnifiedScriptValue FromStruct(
        TArray<FUnifiedScriptValue> Fields,
        uint64 TypeId);
    static FUnifiedScriptValue FromArray(
        TArray<FUnifiedScriptValue> Values,
        uint64 TypeId);
    static FUnifiedScriptValue FromSet(
        TArray<FUnifiedScriptValue> Values,
        uint64 TypeId);
    static FUnifiedScriptValue FromMap(
        TArray<FUnifiedScriptValue> FlattenedEntries,
        uint64 TypeId);

    bool TryGetBoolean(bool& OutValue) const;
    bool TryGetInt32(int32& OutValue) const;
    bool TryGetString(FString& OutValue) const;
    bool TryGetInt8(int8& OutValue) const;
    bool TryGetUInt8(uint8& OutValue) const;
    bool TryGetInt16(int16& OutValue) const;
    bool TryGetUInt16(uint16& OutValue) const;
    bool TryGetUInt32(uint32& OutValue) const;
    bool TryGetInt64(int64& OutValue) const;
    bool TryGetUInt64(uint64& OutValue) const;
    bool TryGetFloat32(float& OutValue) const;
    bool TryGetFloat64(double& OutValue) const;
    bool TryGetName(FName& OutValue) const;
    bool TryGetText(FText& OutValue) const;
    bool TryGetObjectHandle(FUnifiedScriptObjectHandle& OutValue, bool bClass = false) const;
    bool TryGetEnum(int64& OutValue, FName ExpectedTypePath) const;
    bool TryGetEnumById(int64& OutValue, uint64 ExpectedTypeId) const;
    bool TryGetVector(FVector& OutValue) const;
    bool TryGetRotator(FRotator& OutValue) const;
    bool TryGetTransform(FTransform& OutValue) const;
    bool TryGetStruct(
        uint64 ExpectedTypeId,
        TConstArrayView<FUnifiedScriptValue>& OutFields) const;
    bool TryGetArray(uint64 ExpectedTypeId, TConstArrayView<FUnifiedScriptValue>& OutValues) const;
    bool TryGetSet(uint64 ExpectedTypeId, TConstArrayView<FUnifiedScriptValue>& OutValues) const;
    bool TryGetMap(
        uint64 ExpectedTypeId,
        TConstArrayView<FUnifiedScriptValue>& OutFlattenedEntries) const;
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
 * generated managed wrappers. Scalar, object-handle, enum, and common UE core
 * struct values are owned across this boundary; unsupported reflected shapes
 * fail before ProcessEvent is called.
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

    bool InvokeById(
        FUnifiedScriptObjectHandle Target,
        uint64 MemberId,
        TConstArrayView<FUnifiedScriptValue> Arguments,
        FUnifiedScriptValue& OutReturnValue,
        TArray<FUnifiedScriptValue>& OutOutputValues,
        FUnifiedScriptInvocationResult& OutResult);

    bool InvokeInt32x2ById(
        FUnifiedScriptObjectHandle Target,
        uint64 MemberId,
        int32 Argument0,
        int32 Argument1,
        int32& OutReturnValue,
        FUnifiedScriptInvocationResult& OutResult);

    bool InvokeFixedById(
        FUnifiedScriptObjectHandle Target,
        uint64 MemberId,
        TConstArrayView<FUnifiedScriptNativeValueV3> Arguments,
        FUnifiedScriptNativeValueV3* OutReturnValue,
        FUnifiedScriptInvocationResult& OutResult);

    uint64 GetFastInvocationCount() const;

    bool ResolveMulticastDelegate(
        FUnifiedScriptObjectHandle Target,
        const FString& EventPath,
        UObject*& OutSource,
        const FMulticastDelegateProperty*& OutProperty,
        FUnifiedScriptInvocationResult& OutResult);

    bool ReadDelegateArguments(
        const UFunction* Signature,
        const void* ParameterMemory,
        TArray<FUnifiedScriptValue>& OutArguments,
        FUnifiedScriptInvocationResult& OutResult);

    void ReleaseEventArguments(TConstArrayView<FUnifiedScriptValue> Arguments);

private:
    FUnifiedScriptObjectRegistry& Registry;
    std::atomic<uint64> FastInvocationCount{0};
};
