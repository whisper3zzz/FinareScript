#pragma once

#include "CoreMinimal.h"
#include "Engine/Blueprint.h"
#include "UnifiedScriptBlueprint.generated.h"

/**
 * Product-owned Blueprint proxy for one complete UnifiedScript Class.
 *
 * The distinct asset class lets Content Browser route rename and duplicate
 * through the complete source/sidecar/deployment transaction instead of the
 * generic Blueprint-only operations.
 */
UCLASS(BlueprintType)
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptBlueprint : public UBlueprint
{
    GENERATED_BODY()
};
