#pragma once

#include "Commandlets/Commandlet.h"
#include "UnifiedScriptSyncAssetsCommandlet.generated.h"

/**
 * Validates and materializes versioned text deployment manifests as
 * UUnifiedScriptAsset data assets. All entries are preflighted before the
 * first package is changed, making the command suitable for CI and migration
 * workflows.
 */
UCLASS()
class UEUNIFIEDSCRIPTEDITOR_API UUnifiedScriptSyncAssetsCommandlet : public UCommandlet
{
    GENERATED_BODY()

public:
    UUnifiedScriptSyncAssetsCommandlet();

    virtual int32 Main(const FString& Params) override;
};
