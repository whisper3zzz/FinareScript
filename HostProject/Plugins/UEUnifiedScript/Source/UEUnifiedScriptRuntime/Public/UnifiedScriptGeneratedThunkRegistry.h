#pragma once

#include "CoreMinimal.h"
#include "UnifiedScriptInvocationBridge.h"

/** One generated member-ID dispatch table owned by an Unreal module. */
struct UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptGeneratedThunkTable
{
    using FDescribeReadResult = bool (*)(
        uint64,
        EUnifiedScriptValueKind&,
        uint64&);
    using FDescribeInvokeResultCount = bool (*)(uint64, uint32&);
    using FDescribeInvokeResult = bool (*)(
        uint64,
        uint32,
        EUnifiedScriptValueKind&,
        uint64&);
    using FReadProperty = bool (*)(
        UObject*,
        FUnifiedScriptObjectRegistry&,
        uint64,
        FUnifiedScriptValue&,
        FUnifiedScriptInvocationResult&);
    using FWriteProperty = bool (*)(
        UObject*,
        FUnifiedScriptObjectRegistry&,
        uint64,
        const FUnifiedScriptValue&,
        FUnifiedScriptInvocationResult&);
    using FInvoke = bool (*)(
        UObject*,
        FUnifiedScriptObjectRegistry&,
        uint64,
        TConstArrayView<FUnifiedScriptValue>,
        FUnifiedScriptValue&,
        TArray<FUnifiedScriptValue>&,
        FUnifiedScriptInvocationResult&);
    using FInvokeInt32x2 = bool (*)(
        UObject*,
        uint64,
        int32,
        int32,
        int32&,
        FUnifiedScriptInvocationResult&);

    FDescribeReadResult DescribeReadResult = nullptr;
    FDescribeInvokeResultCount DescribeInvokeResultCount = nullptr;
    FDescribeInvokeResult DescribeInvokeResult = nullptr;
    FReadProperty ReadProperty = nullptr;
    FWriteProperty WriteProperty = nullptr;
    FInvoke Invoke = nullptr;
    FInvokeInt32x2 InvokeInt32x2 = nullptr;

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
class UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptGeneratedThunkRegistry final
{
public:
    static bool RegisterTable(
        FName OwnerModule,
        const FUnifiedScriptGeneratedThunkTable& Table);
    static void UnregisterTable(FName OwnerModule);

    static bool DescribeReadResult(
        uint64 MemberId,
        EUnifiedScriptValueKind& OutKind,
        uint64& OutTypeId);
    static bool DescribeInvokeResultCount(
        uint64 MemberId,
        uint32& OutResultCount);
    static bool DescribeInvokeResult(
        uint64 MemberId,
        uint32 ResultIndex,
        EUnifiedScriptValueKind& OutKind,
        uint64& OutTypeId);

    static bool ReadProperty(
        UObject* Target,
        FUnifiedScriptObjectRegistry& Registry,
        uint64 MemberId,
        FUnifiedScriptValue& OutValue,
        FUnifiedScriptInvocationResult& OutResult);
    static bool WriteProperty(
        UObject* Target,
        FUnifiedScriptObjectRegistry& Registry,
        uint64 MemberId,
        const FUnifiedScriptValue& Value,
        FUnifiedScriptInvocationResult& OutResult);
    static bool Invoke(
        UObject* Target,
        FUnifiedScriptObjectRegistry& Registry,
        uint64 MemberId,
        TConstArrayView<FUnifiedScriptValue> Arguments,
        FUnifiedScriptValue& OutReturnValue,
        TArray<FUnifiedScriptValue>& OutOutputValues,
        FUnifiedScriptInvocationResult& OutResult);
    static bool InvokeInt32x2(
        UObject* Target,
        uint64 MemberId,
        int32 Argument0,
        int32 Argument1,
        int32& OutReturnValue,
        FUnifiedScriptInvocationResult& OutResult);
};
