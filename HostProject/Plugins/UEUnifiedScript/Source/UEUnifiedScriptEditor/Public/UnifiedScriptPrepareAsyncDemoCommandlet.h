#pragma once

#include "Commandlets/Commandlet.h"
#include "UnifiedScriptPrepareAsyncDemoCommandlet.generated.h"

/** Repository sample bootstrap. Never rewrites an existing scene. */
UCLASS()
class UEUNIFIEDSCRIPTEDITOR_API UUnifiedScriptPrepareAsyncDemoCommandlet : public UCommandlet
{
    GENERATED_BODY()
public:
    UUnifiedScriptPrepareAsyncDemoCommandlet();
    virtual int32 Main(const FString& Params) override;
};
