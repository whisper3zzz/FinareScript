#pragma once

#include "Commandlets/Commandlet.h"
#include "UnifiedScriptSyncClassesCommandlet.generated.h"

/**
 * Reconciles generated Script Class Blueprints with managed Declaration IR.
 * Deployment assets must already exist, so this command runs immediately after
 * UnifiedScriptSyncAssets in both installed workflows and CI.
 */
UCLASS()
class UEUNIFIEDSCRIPTEDITOR_API UUnifiedScriptSyncClassesCommandlet : public UCommandlet
{
    GENERATED_BODY()

public:
    UUnifiedScriptSyncClassesCommandlet();

    virtual int32 Main(const FString& Params) override;
};
