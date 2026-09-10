#pragma once

#include "CoreMinimal.h"
#include "FinareInvocationBridge.h"

struct FFinareNativeValueV3;

/** One generated member-ID dispatch table owned by an Unreal module. */
struct FINARERUNTIME_API FFinareGeneratedThunkTable
{
    using FDescribeReadResult = bool (*)(
        uint64,
        EFinareValueKind&,
        uint64&);
    using FDescribeInvokeResultCount = bool (*)(uint64, uint32&);
    using FDescribeInvokeResult = bool (*)(
        uint64,
        uint32,
        EFinareValueKind&,
        uint64&);
    using FReadProperty = bool (*)(
        UObject*,
        FFinareObjectRegistry&,
        uint64,
        FFinareValue&,
        FFinareInvocationResult&);
    using FWriteProperty = bool (*)(
        UObject*,
        FFinareObjectRegistry&,
        uint64,
        const FFinareValue&,
        FFinareInvocationResult&);
    using FInvoke = bool (*)(
        UObject*,
        FFinareObjectRegistry&,
        uint64,
        TConstArrayView<FFinareValue>,
        FFinareValue&,
        TArray<FFinareValue>&,
        FFinareInvocationResult&);
    using FInvokeInt32x2 = bool (*)(
        UObject*,
        uint64,
        int32,
        int32,
        int32&,
        FFinareInvocationResult&);
    using FInvokeFixed = bool (*)(
        UObject*,
        FFinareObjectRegistry&,
        uint64,
        TConstArrayView<FFinareNativeValueV3>,
        FFinareNativeValueV3*,
        FFinareInvocationResult&);

    FDescribeReadResult DescribeReadResult = nullptr;
    FDescribeInvokeResultCount DescribeInvokeResultCount = nullptr;
    FDescribeInvokeResult DescribeInvokeResult = nullptr;
    FReadProperty ReadProperty = nullptr;
    FWriteProperty WriteProperty = nullptr;
    FInvoke Invoke = nullptr;
    FInvokeInt32x2 InvokeInt32x2 = nullptr;
    FInvokeFixed InvokeFixed = nullptr;

    bool IsValid() const
    {
        return DescribeReadResult != nullptr &&
            DescribeInvokeResultCount != nullptr &&
            DescribeInvokeResult != nullptr &&
            ReadProperty != nullptr &&
            WriteProperty != nullptr &&
            Invoke != nullptr;
    }
};

/**
 * Composes generated ABI v3 fast-binding tables from the plugin and game modules.
 * Registration is module-lifetime scoped; member IDs must be globally unique.
 */
class FINARERUNTIME_API FFinareGeneratedThunkRegistry final
{
public:
    static bool RegisterTable(
        FName OwnerModule,
        const FFinareGeneratedThunkTable& Table);
    static void UnregisterTable(FName OwnerModule);

    static bool DescribeReadResult(
        uint64 MemberId,
        EFinareValueKind& OutKind,
        uint64& OutTypeId);
    static bool DescribeInvokeResultCount(
        uint64 MemberId,
        uint32& OutResultCount);
    static bool DescribeInvokeResult(
        uint64 MemberId,
        uint32 ResultIndex,
        EFinareValueKind& OutKind,
        uint64& OutTypeId);

    static bool ReadProperty(
        UObject* Target,
        FFinareObjectRegistry& Registry,
        uint64 MemberId,
        FFinareValue& OutValue,
        FFinareInvocationResult& OutResult);
    static bool WriteProperty(
        UObject* Target,
        FFinareObjectRegistry& Registry,
        uint64 MemberId,
        const FFinareValue& Value,
        FFinareInvocationResult& OutResult);
    static bool Invoke(
        UObject* Target,
        FFinareObjectRegistry& Registry,
        uint64 MemberId,
        TConstArrayView<FFinareValue> Arguments,
        FFinareValue& OutReturnValue,
        TArray<FFinareValue>& OutOutputValues,
        FFinareInvocationResult& OutResult);
    static bool InvokeInt32x2(
        UObject* Target,
        uint64 MemberId,
        int32 Argument0,
        int32 Argument1,
        int32& OutReturnValue,
        FFinareInvocationResult& OutResult);
    static bool InvokeFixed(
        UObject* Target,
        FFinareObjectRegistry& Registry,
        uint64 MemberId,
        TConstArrayView<FFinareNativeValueV3> Arguments,
        FFinareNativeValueV3* OutReturnValue,
        FFinareInvocationResult& OutResult);
};
