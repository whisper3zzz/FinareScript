#pragma once

#include "Editor/TransBuffer.h"

#include "UnifiedScriptAuthoringTransBuffer.generated.h"

/** Standard flip transaction that excludes reflected UStruct layout saves. */
class FAuthoringBlueprintTransaction final : public FTransaction
{
public:
    FAuthoringBlueprintTransaction(
        const TCHAR* InContext = nullptr,
        const FText& InTitle = FText(),
        bool /*bInFlip*/ = false)
        : FTransaction(InContext, InTitle, true)
    {
    }

    virtual void SaveObject(UObject* Object) override;
};

/** Private transaction buffer for Blueprint authoring state recovery. */
UCLASS(Transient)
class UUnifiedScriptAuthoringTransBuffer final : public UTransBuffer
{
    GENERATED_BODY()

public:
    virtual int32 Begin(const TCHAR* SessionContext, const FText& Description) override;
};
