#pragma once

#include "CoreMinimal.h"
#include "Commandlets/Commandlet.h"
#include "FinareExportCommandlet.generated.h"

UCLASS()
class FINAREEDITOR_API UFinareExportCommandlet : public UCommandlet
{
    GENERATED_BODY()

public:
    UFinareExportCommandlet();

    virtual int32 Main(const FString& Params) override;
};
