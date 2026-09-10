/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 20:45:17
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 10:33:15
 * @FilePath: \Plugins\Finare\Source\FinareEditor\Private\Graph\FinareGraphLayoutStore.h
 * @Description:
 */
#pragma once

#include "CoreMinimal.h"

class UEdGraphNode;

struct FFinareGraphCommentLayout
{
    FString Id;
    FString Title;
    FVector2D Position = FVector2D::ZeroVector;
    FVector2D Size = FVector2D(400.0, 220.0);
    TArray<FString> NodeIds;
    bool bCollapsed = false;
};

struct FFinareGraphRoutingHintLayout
{
    FString EdgeId;
    TArray<FVector2D> ControlPoints;
};

struct FFinareGraphFunctionLayout
{
    TMap<FString, FVector2D> NodePositions;
    TArray<FFinareGraphCommentLayout> CommentRegions;
    TArray<FFinareGraphRoutingHintLayout> RoutingHints;
};

/** UE-side persistence adapter for the engine-neutral .usgraph v0.1 sidecar. */
class FFinareGraphLayoutStore
{
public:
    static FString GetLayoutPath(const FString& SurfacePath);

    static bool LoadFunction(
        const FString& LayoutPath,
        const FString& FunctionId,
        FFinareGraphFunctionLayout& OutLayout,
        FString& OutError);

    static bool SaveFunction(
        const FString& LayoutPath,
        const FString& FunctionId,
        const TArray<UEdGraphNode*>& Nodes,
        FString& OutError);
};
