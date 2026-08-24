/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 17:37:05
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-24 19:01:04
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\Public\UnifiedScriptDeclarationMaterializer.h
 * @Description:
 */
#pragma once

#include "CoreMinimal.h"
#include "StructUtils/PropertyBag.h"

/** Converts C# Script Asset Declaration IR into an Unreal editable state bag. */
class UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptDeclarationMaterializer
{
public:
    static bool MaterializeFile(
        const FString& DeclarationPath,
        const FString& RequestedAssetPath,
        const FInstancedPropertyBag& ExistingState,
        FInstancedPropertyBag& OutState,
        FString& OutAssetPath,
        FString& OutError);

    static bool MaterializeJson(
        const FString& DeclarationJson,
        const FString& RequestedAssetPath,
        const FInstancedPropertyBag& ExistingState,
        FInstancedPropertyBag& OutState,
        FString& OutAssetPath,
        FString& OutError);
};
