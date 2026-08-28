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

/** Observable result of reconciling one instance state bag with a new declaration. */
struct UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptStateMigrationReport
{
    int32 DeclaredStateCount = 0;
    int32 PreservedOverrideCount = 0;
    int32 DefaultedStateCount = 0;
    int32 DroppedOverrideCount = 0;
};

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

    /**
     * Materializes declaration defaults and copies only explicitly overridden,
     * type-compatible properties from ExistingState. Stable descriptor IDs make
     * the override survive state renames without freezing untouched defaults.
     */
    static bool MaterializeFileWithOverrides(
        const FString& DeclarationPath,
        const FString& RequestedAssetPath,
        const FInstancedPropertyBag& ExistingState,
        TConstArrayView<FGuid> ExistingOverrideIds,
        FInstancedPropertyBag& OutState,
        TArray<FGuid>& OutOverrideIds,
        FUnifiedScriptStateMigrationReport& OutMigration,
        FString& OutAssetPath,
        FString& OutError);

    /** Materializes the binary DeclarationData section from a verified .uschunk. */
    static bool MaterializeChunkFileWithOverrides(
        const FString& ChunkPath,
        const FString& RequestedAssetPath,
        const FInstancedPropertyBag& ExistingState,
        TConstArrayView<FGuid> ExistingOverrideIds,
        FInstancedPropertyBag& OutState,
        TArray<FGuid>& OutOverrideIds,
        FUnifiedScriptStateMigrationReport& OutMigration,
        FString& OutAssetPath,
        FString& OutError);

    static bool MaterializeJson(
        const FString& DeclarationJson,
        const FString& RequestedAssetPath,
        const FInstancedPropertyBag& ExistingState,
        FInstancedPropertyBag& OutState,
        FString& OutAssetPath,
        FString& OutError);

    static bool MaterializeJsonWithOverrides(
        const FString& DeclarationJson,
        const FString& RequestedAssetPath,
        const FInstancedPropertyBag& ExistingState,
        TConstArrayView<FGuid> ExistingOverrideIds,
        FInstancedPropertyBag& OutState,
        TArray<FGuid>& OutOverrideIds,
        FUnifiedScriptStateMigrationReport& OutMigration,
        FString& OutAssetPath,
        FString& OutError);
};
