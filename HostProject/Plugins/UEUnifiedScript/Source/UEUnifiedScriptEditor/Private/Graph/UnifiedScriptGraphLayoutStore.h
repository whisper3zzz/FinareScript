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

struct FUnifiedScriptGraphCommentLayout
{
    FString Id;
    FString Title;
    FVector2D Position = FVector2D::ZeroVector;
    FVector2D Size = FVector2D(400.0, 220.0);
    TArray<FString> NodeIds;
    bool bCollapsed = false;
};

struct FUnifiedScriptGraphRoutingHintLayout
{
    FString EdgeId;
    TArray<FVector2D> ControlPoints;
};

struct FUnifiedScriptGraphFunctionLayout
{
    TMap<FString, FVector2D> NodePositions;
    TArray<FUnifiedScriptGraphCommentLayout> CommentRegions;
    TArray<FUnifiedScriptGraphRoutingHintLayout> RoutingHints;
};

/** UE-side persistence adapter for the engine-neutral .usgraph v0.1 sidecar. */
class FUnifiedScriptGraphLayoutStore
{
public:
    static FString GetLayoutPath(const FString& SurfacePath);

    static bool LoadFunction(
        const FString& LayoutPath,
        const FString& FunctionId,
        FUnifiedScriptGraphFunctionLayout& OutLayout,
        FString& OutError);

    static bool SaveFunction(
        const FString& LayoutPath,
        const FString& FunctionId,
        const TArray<UEdGraphNode*>& Nodes,
        FString& OutError);
};
