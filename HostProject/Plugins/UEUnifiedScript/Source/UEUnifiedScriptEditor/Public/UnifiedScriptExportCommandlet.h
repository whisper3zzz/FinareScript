#pragma once

#include "CoreMinimal.h"
#include "Commandlets/Commandlet.h"
#include "UnifiedScriptExportCommandlet.generated.h"

UCLASS()
class UEUNIFIEDSCRIPTEDITOR_API UUnifiedScriptExportCommandlet : public UCommandlet
{
    GENERATED_BODY()

public:
    UUnifiedScriptExportCommandlet();

    virtual int32 Main(const FString& Params) override;
};
