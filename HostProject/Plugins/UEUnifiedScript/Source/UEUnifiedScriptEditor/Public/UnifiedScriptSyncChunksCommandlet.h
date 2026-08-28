#pragma once

#include "Commandlets/Commandlet.h"
#include "UnifiedScriptSyncChunksCommandlet.generated.h"

/**
 * Verifies compiled program chunks and assigns them to existing Script Assets.
 * Authoring deployment remains owned by UnifiedScriptSyncAssets; this command
 * publishes the independently generated runtime artifact transactionally.
 */
UCLASS()
class UEUNIFIEDSCRIPTEDITOR_API UUnifiedScriptSyncChunksCommandlet : public UCommandlet
{
    GENERATED_BODY()

public:
    UUnifiedScriptSyncChunksCommandlet();

    virtual int32 Main(const FString& Params) override;
};
