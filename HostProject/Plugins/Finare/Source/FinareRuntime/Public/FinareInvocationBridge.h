#pragma once

#include "Containers/ArrayView.h"
#include "CoreMinimal.h"
#include "FinareObjectRegistry.h"
#include "FinareByteBuffer.h"

#include <atomic>

class FMulticastDelegateProperty;
struct FFinareNativeValueV3;
struct FFinareNativeCallFrameV3;

/** Native parent result retained across a correlated BufferTooSmall retry. */
struct FINARERUNTIME_API FFinareParentCallResult
{
	TArray<uint8> ResultCells;
	TArray<uint8> Arena;
};

/** Stable 64-bit FNV-1a identity shared with generated managed wrappers. */
constexpr uint64 FinareMemberId(const ANSICHAR* Path)
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
enum class EFinareValueKind : uint8
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
	ByteBuffer = 25,
	Callback = 26,
	SoftObject = 27,
	SoftClass = 28,
};

/** Owned value transport. No address into UObject or UFunction storage escapes. */
struct FINARERUNTIME_API FFinareValue
{
	EFinareValueKind Kind = EFinareValueKind::Void;
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
	FFinareObjectHandle ObjectHandleValue;
	FName TypePath;
	uint64 TypeIdValue = 0;
	FVector VectorValue = FVector::ZeroVector;
	FRotator RotatorValue = FRotator::ZeroRotator;
	FTransform TransformValue = FTransform::Identity;
	// Array/Set elements, or flattened alternating Map key/value cells.
	TArray<FFinareValue> ContainerValues;
	FFinareByteBuffer ByteBufferValue;

	static FFinareValue FromCallbackToken(uint64 Token, uint64 Scope, uint64 SignatureId)
	{
		FFinareValue Result;
		Result.Kind = EFinareValueKind::Callback;
		Result.ObjectHandleValue = {Token};
		Result.UInt64Value = Scope;
		Result.TypeIdValue = SignatureId;
		return Result;
	}

	static FFinareValue FromByteBuffer(FFinareByteBuffer Value)
	{
		FFinareValue Result;
		Result.Kind = EFinareValueKind::ByteBuffer;
		Result.ByteBufferValue = MoveTemp(Value);
		return Result;
	}

	bool TryGetByteBuffer(FFinareByteBuffer& OutValue) const
	{
		if (Kind != EFinareValueKind::ByteBuffer)
		{
			return false;
		}
		OutValue = ByteBufferValue;
		return true;
	}

	static FFinareValue FromBoolean(bool Value);
	static FFinareValue FromInt32(int32 Value);
	static FFinareValue FromString(FString Value);
	static FFinareValue FromSoftObjectPath(FString Value);
	static FFinareValue FromSoftClassPath(FString Value);
	static FFinareValue FromInt8(int8 Value);
	static FFinareValue FromUInt8(uint8 Value);
	static FFinareValue FromInt16(int16 Value);
	static FFinareValue FromUInt16(uint16 Value);
	static FFinareValue FromUInt32(uint32 Value);
	static FFinareValue FromInt64(int64 Value);
	static FFinareValue FromUInt64(uint64 Value);
	static FFinareValue FromFloat32(float Value);
	static FFinareValue FromFloat64(double Value);
	static FFinareValue FromName(FName Value);
	static FFinareValue FromText(FText Value);
	static FFinareValue FromObjectHandle(
		FFinareObjectHandle Value,
		bool bClass = false);
	static FFinareValue FromEnum(int64 Value, FName TypePath);
	static FFinareValue FromEnumTypeId(int64 Value, uint64 TypeId);
	static FFinareValue FromVector(FVector Value);
	static FFinareValue FromRotator(FRotator Value);
	static FFinareValue FromTransform(FTransform Value);
	static FFinareValue FromStruct(
		TArray<FFinareValue> Fields,
		uint64 TypeId);
	static FFinareValue FromArray(
		TArray<FFinareValue> Values,
		uint64 TypeId);
	static FFinareValue FromSet(
		TArray<FFinareValue> Values,
		uint64 TypeId);
	static FFinareValue FromMap(
		TArray<FFinareValue> FlattenedEntries,
		uint64 TypeId);

	bool TryGetBoolean(bool& OutValue) const;
	bool TryGetInt32(int32& OutValue) const;
	bool TryGetString(FString& OutValue) const;
	bool TryGetSoftObjectPath(FString& OutValue) const;
	bool TryGetSoftClassPath(FString& OutValue) const;
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
	bool TryGetObjectHandle(FFinareObjectHandle& OutValue, bool bClass = false) const;
	bool TryGetEnum(int64& OutValue, FName ExpectedTypePath) const;
	bool TryGetEnumById(int64& OutValue, uint64 ExpectedTypeId) const;
	bool TryGetVector(FVector& OutValue) const;
	bool TryGetRotator(FRotator& OutValue) const;
	bool TryGetTransform(FTransform& OutValue) const;
	bool TryGetStruct(
		uint64 ExpectedTypeId,
		TConstArrayView<FFinareValue>& OutFields) const;
	bool TryGetArray(uint64 ExpectedTypeId, TConstArrayView<FFinareValue>& OutValues) const;
	bool TryGetSet(uint64 ExpectedTypeId, TConstArrayView<FFinareValue>& OutValues) const;
	bool TryGetMap(
		uint64 ExpectedTypeId,
		TConstArrayView<FFinareValue>& OutFlattenedEntries) const;
};

/** Stable failure categories for member resolution, marshalling, and invocation. */
enum class EFinareInvocationError : uint8
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

struct FINARERUNTIME_API FFinareInvocationResult
{
	EFinareInvocationError Error = EFinareInvocationError::None;
	EFinareObjectHandleError HandleError = EFinareObjectHandleError::None;
	FString Message;

	bool IsSuccess() const
	{
		return Error == EFinareInvocationError::None;
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
class FINARERUNTIME_API FFinareInvocationBridge
{
public:
	explicit FFinareInvocationBridge(FFinareObjectRegistry& InRegistry);

	bool ReadProperty(
		FFinareObjectHandle Target,
		const FString& PropertyPath,
		FFinareValue& OutValue,
		FFinareInvocationResult& OutResult);

	bool WriteProperty(
		FFinareObjectHandle Target,
		const FString& PropertyPath,
		const FFinareValue& Value,
		FFinareInvocationResult& OutResult);

	bool Invoke(
		FFinareObjectHandle Target,
		const FString& FunctionPath,
		TConstArrayView<FFinareValue> Arguments,
		FFinareValue& OutReturnValue,
		FFinareInvocationResult& OutResult);

	bool ReadPropertyById(
		FFinareObjectHandle Target,
		uint64 MemberId,
		FFinareValue& OutValue,
		FFinareInvocationResult& OutResult);

	/** Describe generic reflected results before executing any gameplay. */
	bool DescribeReflectedInvocation(
		FFinareObjectHandle Target, uint64 MemberId,
		TArray<FFinareValue>& OutDescriptors, FFinareInvocationResult& OutResult);

	bool Invoke(
		FFinareObjectHandle Target, const FString& FunctionPath,
		TConstArrayView<FFinareValue> Arguments, FFinareValue& OutReturnValue,
		TArray<FFinareValue>& OutOutputValues, FFinareInvocationResult& OutResult);

	bool WritePropertyById(
		FFinareObjectHandle Target,
		uint64 MemberId,
		const FFinareValue& Value,
		FFinareInvocationResult& OutResult);

	bool InvokeById(
		FFinareObjectHandle Target,
		uint64 MemberId,
		TConstArrayView<FFinareValue> Arguments,
		FFinareValue& OutReturnValue,
		FFinareInvocationResult& OutResult);

	bool InvokeById(
		FFinareObjectHandle Target,
		uint64 MemberId,
		TConstArrayView<FFinareValue> Arguments,
		FFinareValue& OutReturnValue,
		TArray<FFinareValue>& OutOutputValues,
		FFinareInvocationResult& OutResult);

	bool InvokeParentById(
		FFinareObjectHandle Host,
		uint64 MemberId,
		const FFinareNativeCallFrameV3& InputFrame,
		uint32 EntryByteCount,
		FFinareParentCallResult& OutParentResult,
		FFinareInvocationResult& OutResult);

	bool InvokeInt32x2ById(
		FFinareObjectHandle Target,
		uint64 MemberId,
		int32 Argument0,
		int32 Argument1,
		int32& OutReturnValue,
		FFinareInvocationResult& OutResult);

	bool InvokeFixedById(
		FFinareObjectHandle Target,
		uint64 MemberId,
		TConstArrayView<FFinareNativeValueV3> Arguments,
		FFinareNativeValueV3* OutReturnValue,
		FFinareInvocationResult& OutResult);

	uint64 GetFastInvocationCount() const;

	bool ResolveMulticastDelegate(
		FFinareObjectHandle Target,
		const FString& EventPath,
		UObject*& OutSource,
		const FMulticastDelegateProperty*& OutProperty,
		FFinareInvocationResult& OutResult);

	bool ReadDelegateArguments(
		const UFunction* Signature,
		const void* ParameterMemory,
		TArray<FFinareValue>& OutArguments,
		FFinareInvocationResult& OutResult);

	void ReleaseEventArguments(TConstArrayView<FFinareValue> Arguments);
	FFinareObjectRegistry& GetObjectRegistry() { return Registry; }
	bool WriteCallbackOutputs(const UFunction* Signature, void* Parameters,
	                          TConstArrayView<FFinareValue> Outputs, FFinareInvocationResult& OutResult);

private:
	FFinareObjectRegistry& Registry;
	std::atomic<uint64> FastInvocationCount{0};
};
