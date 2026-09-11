#pragma once

#include "CoreMinimal.h"
#include "FinareInvocationBridge.h"
#include "FinareNativeTransport.h"

namespace UE::Finare::Generated
{
	bool DescribeReadResult(
		uint64 MemberId,
		EFinareValueKind& OutKind,
		uint64& OutTypeId);

	bool DescribeInvokeResult(
		uint64 MemberId,
		uint32 ResultIndex,
		EFinareValueKind& OutKind,
		uint64& OutTypeId);

	bool DescribeInvokeResultCount(uint64 MemberId, uint32& OutResultCount);

	/** Generated, member-ID keyed dispatch with direct native field/function access. */
	bool ReadProperty(
		UObject* Target,
		FFinareObjectRegistry& Registry,
		uint64 MemberId,
		FFinareValue& OutValue,
		FFinareInvocationResult& OutResult);

	bool WriteProperty(
		UObject* Target,
		FFinareObjectRegistry& Registry,
		uint64 MemberId,
		const FFinareValue& Value,
		FFinareInvocationResult& OutResult);

	bool Invoke(
		UObject* Target,
		FFinareObjectRegistry& Registry,
		uint64 MemberId,
		TConstArrayView<FFinareValue> Arguments,
		FFinareValue& OutReturnValue,
		TArray<FFinareValue>& OutOutputValues,
		FFinareInvocationResult& OutResult);

	bool InvokeInt32x2(
		UObject* Target,
		uint64 MemberId,
		int32 Argument0,
		int32 Argument1,
		int32& OutReturnValue,
		FFinareInvocationResult& OutResult);

	bool InvokeFixed(
		UObject* Target,
		FFinareObjectRegistry& Registry,
		uint64 MemberId,
		TConstArrayView<FFinareNativeValueV3> Arguments,
		FFinareNativeValueV3* OutReturnValue,
		FFinareInvocationResult& OutResult);
}
