#pragma once

#include "Commandlets/Commandlet.h"
#include "FinareSyncAssetsCommandlet.generated.h"

/**
 * Validates and materializes versioned text deployment manifests as
 * UFinareAsset data assets. All entries are preflighted before the
 * first package is changed, making the command suitable for CI and migration
 * workflows.
 */
UCLASS()
class FINAREEDITOR_API UFinareSyncAssetsCommandlet : public UCommandlet
{
    GENERATED_BODY()

public:
    UFinareSyncAssetsCommandlet();

    virtual int32 Main(const FString& Params) override;
};
