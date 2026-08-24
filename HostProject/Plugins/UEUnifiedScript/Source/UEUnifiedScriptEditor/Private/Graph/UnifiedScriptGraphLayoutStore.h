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
