#pragma once

#include "CoreMinimal.h"
#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "UnifiedScriptBlueprint.generated.h"

class UUnifiedScriptAsset;

USTRUCT()
struct FUnifiedScriptManagedFunctionBinding
{
    GENERATED_BODY()

    /** Exact managed method name owned by the stable Blueprint function identity. */
    UPROPERTY()
    FString ManagedName;

    /** Stable declaration identity retained across source-level method renames. */
    UPROPERTY()
    FGuid FunctionId;
};

/**
 * Generated-class carrier for persistent managed function bindings.
 *
 * Blueprint graphs own the durable typed signature and call-node identity;
 * this class rebinds their implementation to one generic native dispatcher
 * after load, so cooked assets do not require one handwritten thunk per method.
 */
UCLASS()
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptBlueprintGeneratedClass
    : public UBlueprintGeneratedClass
{
    GENERATED_BODY()

public:
    UPROPERTY()
    TMap<FName, FUnifiedScriptManagedFunctionBinding> ManagedFunctionBindings;

    void BindManagedFunctions();
    bool IsManagedFunctionBound(FName FunctionName) const;

    virtual void PostLoad() override;

private:
    DECLARE_FUNCTION(execDispatchManagedFunction);
};

/**
 * Product-owned Blueprint proxy for one complete UnifiedScript Class.
 *
 * The distinct asset class lets Content Browser route rename and duplicate
 * through the complete source/sidecar/deployment transaction instead of the
 * generic Blueprint-only operations.
 */
UCLASS(BlueprintType)
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptBlueprint : public UBlueprint
{
    GENERATED_BODY()

public:
    /** Exact sidecar owned by this generated proxy, independent of its native parent kind. */
    UPROPERTY(VisibleAnywhere, Category="UnifiedScript|Class")
    TObjectPtr<UUnifiedScriptAsset> ScriptAsset;

    /** Keep the product-owned binding carrier for every Blueprint parent kind. */
    virtual UClass* GetBlueprintClass() const override;
};
