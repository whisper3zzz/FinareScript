#pragma once

#include "Kismet/BlueprintFunctionLibrary.h"
#include "FinarePilotPlayLibrary.generated.h"

UCLASS()
class FINAREPILOTVALIDATION_API UFinarePilotPlayLibrary : public UBlueprintFunctionLibrary
{
    GENERATED_BODY()

public:
    UFUNCTION(BlueprintCallable, Category = "Finare|Pilot")
    static bool RequestPlay(bool bSimulate = false);
};
