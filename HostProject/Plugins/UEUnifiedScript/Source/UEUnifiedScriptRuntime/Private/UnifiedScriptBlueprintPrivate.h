#pragma once

#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptNativeTransport.h"

class UUnifiedScriptWorldSubsystem;

namespace UE::UnifiedScript::Blueprint::Private
{

bool PackValueV2(
    const FProperty& Property,
    const void* Container,
    UUnifiedScriptWorldSubsystem& Host,
    TArray<uint8, TInlineAllocator<512>>& Arena,
    const FUnifiedScriptManagedValueTypeBinding* TypeBinding,
    TConstArrayView<FName> StructPrefix,
    FUnifiedScriptNativeValueV3& OutValue,
    FString& OutError,
    bool bDirectValue = false);

bool UnpackReturnValueV2(
    const FProperty& Property,
    const FUnifiedScriptNativeValueV3& Value,
    TConstArrayView<uint8> Arena,
    uint32 ByteCount,
    UUnifiedScriptWorldSubsystem& Host,
    const FUnifiedScriptManagedValueTypeBinding* TypeBinding,
    TConstArrayView<FName> StructPrefix,
    void* Result,
    FString& OutError);

}
