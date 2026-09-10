#pragma once

#include "Editor/TransBuffer.h"
#include "UObject/ObjectKey.h"

#include "UnifiedScriptAuthoringTransBuffer.generated.h"

/**
 * Standard flip transaction for authoring recovery.  Its object whitelist is
 * opened only while FLocalBlueprintTransaction explicitly captures the
 * Blueprint authoring tree; compiler runtime objects cannot enter the record
 * through Modify(), arrays, snapshots, or custom changes afterwards.
 */
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

    void BeginExplicitAuthoringCapture();
    void EndExplicitAuthoringCapture();

    virtual void SaveObject(UObject* Object) override;
    virtual void SaveArray(
        UObject* Object,
        FScriptArray* Array,
        int32 Index,
        int32 Count,
        int32 Oper,
        int32 ElementSize,
        uint32 ElementAlignment,
        STRUCT_DC DefaultConstructor,
        STRUCT_AR Serializer,
        STRUCT_DTOR Destructor) override;
    virtual void StoreUndo(UObject* Object, TUniquePtr<FChange> UndoChange) override;
    virtual void SnapshotObject(UObject* Object, TArrayView<const FProperty*> Properties) override;

private:
    bool IsAuthoringObjectAllowed(UObject* Object) const;

    TSet<FObjectKey> ExplicitAuthoringObjects;
    bool bExplicitAuthoringCaptureOpen = false;
};

/** Private transaction buffer for Blueprint authoring state recovery. */
UCLASS(Transient)
class UUnifiedScriptAuthoringTransBuffer final : public UTransBuffer
{
    GENERATED_BODY()

public:
    virtual int32 Begin(const TCHAR* SessionContext, const FText& Description) override;
};
