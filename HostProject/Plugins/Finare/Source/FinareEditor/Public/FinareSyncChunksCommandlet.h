#pragma once

#include "Commandlets/Commandlet.h"
#include "FinareSyncChunksCommandlet.generated.h"

/**
 * Verifies compiled program chunks and assigns them to existing Script Assets.
 * Authoring deployment remains owned by FinareSyncAssets; this command
 * publishes the independently generated runtime artifact transactionally.
 */
UCLASS()
class FINAREEDITOR_API UFinareSyncChunksCommandlet : public UCommandlet
{
    GENERATED_BODY()

public:
    UFinareSyncChunksCommandlet();

    virtual int32 Main(const FString& Params) override;
};
