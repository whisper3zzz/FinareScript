#pragma once

#include "CoreMinimal.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include "UnifiedScriptClassBases.generated.h"

/** Stable native UObject parent for generated managed UObject classes. */
UCLASS(BlueprintType, Blueprintable)
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptObject : public UObject
{
    GENERATED_BODY()
};

/** Stable native parent for generated managed Blueprint Function Libraries. */
UCLASS(Abstract, BlueprintType, Blueprintable)
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptBlueprintFunctionLibrary
    : public UBlueprintFunctionLibrary
{
    GENERATED_BODY()
};
