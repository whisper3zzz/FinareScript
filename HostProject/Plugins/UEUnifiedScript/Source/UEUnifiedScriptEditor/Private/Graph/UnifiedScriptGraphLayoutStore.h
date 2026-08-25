/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 20:45:17
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 10:33:15
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptEditor\Private\Graph\UnifiedScriptGraphLayoutStore.h
 * @Description:
 */
#pragma once

#include "CoreMinimal.h"

class UEdGraphNode;

/** UE-side persistence adapter for the engine-neutral .usgraph v0.1 sidecar. */
class FUnifiedScriptGraphLayoutStore
{
public:
    static FString GetLayoutPath(const FString& SurfacePath);

    static bool LoadFunction(
        const FString& LayoutPath,
        const FString& FunctionId,
        TMap<FString, FVector2D>& OutPositions,
        FString& OutError);

    static bool SaveFunction(
        const FString& LayoutPath,
        const FString& FunctionId,
        const TArray<UEdGraphNode*>& Nodes,
        FString& OutError);
};
