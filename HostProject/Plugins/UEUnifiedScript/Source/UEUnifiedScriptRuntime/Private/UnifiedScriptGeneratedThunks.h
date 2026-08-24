/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 17:20:32
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-24 18:57:47
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\Private\UnifiedScriptGeneratedThunks.h
 * @Description:
 */
#pragma once

#include "CoreMinimal.h"
#include "UnifiedScriptInvocationBridge.h"

namespace UE::UnifiedScript::Generated
{
/** Generated, member-ID keyed dispatch with direct native field/function access. */
bool ReadProperty(
    UObject* Target,
    uint64 MemberId,
    FUnifiedScriptValue& OutValue,
    FUnifiedScriptInvocationResult& OutResult);

bool WriteProperty(
    UObject* Target,
    uint64 MemberId,
    const FUnifiedScriptValue& Value,
    FUnifiedScriptInvocationResult& OutResult);

bool Invoke(
    UObject* Target,
    uint64 MemberId,
    TConstArrayView<FUnifiedScriptValue> Arguments,
    FUnifiedScriptValue& OutReturnValue,
    FUnifiedScriptInvocationResult& OutResult);
}
