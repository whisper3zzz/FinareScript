#pragma once

#include "CoreMinimal.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include "FinareClassBases.generated.h"

/** Stable native UObject parent for generated managed UObject classes. */
UCLASS(BlueprintType, Blueprintable)
class FINARERUNTIME_API UFinareObject : public UObject
{
	GENERATED_BODY()
};

/** Stable native parent for generated managed Blueprint Function Libraries. */
UCLASS(Abstract, BlueprintType, Blueprintable)
class FINARERUNTIME_API UFinareBlueprintFunctionLibrary
	: public UBlueprintFunctionLibrary
{
	GENERATED_BODY()
};
