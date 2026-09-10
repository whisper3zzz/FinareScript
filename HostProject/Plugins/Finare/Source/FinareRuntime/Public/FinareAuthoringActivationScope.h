#pragma once

#include "CoreMinimal.h"

#if WITH_EDITOR

#include "UObject/ObjectKey.h"
#include "UObject/StrongObjectPtr.h"

class UActorComponent;

/**
 * Temporarily suppresses automatic managed activation while an editor
 * authoring operation reinstantiates Blueprint objects.  The owner key is
 * matched against both the component and actor class hierarchies so the
 * transient replacement cannot dispatch BeginPlay into the old generation
 * or erase a deliberately stopped runtime state.
 */
class FINARERUNTIME_API FFinareAuthoringActivationScope final
{
public:
    explicit FFinareAuthoringActivationScope(UObject* InOwner);
    ~FFinareAuthoringActivationScope();

    FFinareAuthoringActivationScope(const FFinareAuthoringActivationScope&) = delete;
    FFinareAuthoringActivationScope& operator=(const FFinareAuthoringActivationScope&) = delete;

    static bool IsSuppressedForComponent(const UActorComponent& Component);

private:
    FObjectKey OwnerKey;
    TStrongObjectPtr<UObject> Owner;
    bool bRegistered = false;
};

#endif // WITH_EDITOR
