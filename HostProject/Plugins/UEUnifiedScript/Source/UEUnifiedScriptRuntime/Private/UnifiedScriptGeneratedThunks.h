/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 17:20:32
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 14:22:40
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\Private\UnifiedScriptGeneratedThunks.h
 * @Description:
 */
#pragma once

#include "CoreMinimal.h"
#include "UnifiedScriptInvocationBridge.h"
#include "UnifiedScriptNativeTransport.h"

namespace UE::UnifiedScript::Generated
{
bool DescribeReadResult(
    uint64 MemberId,
    EUnifiedScriptValueKind& OutKind,
    uint64& OutTypeId);

bool DescribeInvokeResult(
    uint64 MemberId,
    uint32 ResultIndex,
    EUnifiedScriptValueKind& OutKind,
    uint64& OutTypeId);

bool DescribeInvokeResultCount(uint64 MemberId, uint32& OutResultCount);

/** Generated, member-ID keyed dispatch with direct native field/function access. */
bool ReadProperty(
    UObject* Target,
    FUnifiedScriptObjectRegistry& Registry,
    uint64 MemberId,
    FUnifiedScriptValue& OutValue,
    FUnifiedScriptInvocationResult& OutResult);

bool WriteProperty(
    UObject* Target,
    FUnifiedScriptObjectRegistry& Registry,
    uint64 MemberId,
    const FUnifiedScriptValue& Value,
    FUnifiedScriptInvocationResult& OutResult);

bool Invoke(
    UObject* Target,
    FUnifiedScriptObjectRegistry& Registry,
    uint64 MemberId,
    TConstArrayView<FUnifiedScriptValue> Arguments,
    FUnifiedScriptValue& OutReturnValue,
    TArray<FUnifiedScriptValue>& OutOutputValues,
    FUnifiedScriptInvocationResult& OutResult);

bool InvokeInt32x2(
    UObject* Target,
    uint64 MemberId,
    int32 Argument0,
    int32 Argument1,
    int32& OutReturnValue,
    FUnifiedScriptInvocationResult& OutResult);

bool InvokeFixed(
    UObject* Target,
    FUnifiedScriptObjectRegistry& Registry,
    uint64 MemberId,
    TConstArrayView<FUnifiedScriptNativeValueV3> Arguments,
    FUnifiedScriptNativeValueV3* OutReturnValue,
    FUnifiedScriptInvocationResult& OutResult);
}
