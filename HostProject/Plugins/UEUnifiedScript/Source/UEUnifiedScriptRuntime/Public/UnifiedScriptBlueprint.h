#pragma once

#include "CoreMinimal.h"
#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "UnifiedScriptBlueprint.generated.h"

class UUnifiedScriptAsset;

USTRUCT()
struct FUnifiedScriptManagedStructFieldPath
{
    GENERATED_BODY()

    /** Native reflected property path from the top-level generated UStruct. */
    UPROPERTY()
    TArray<FName> Segments;
};

USTRUCT()
struct FUnifiedScriptManagedValueTypeBinding
{
    GENERATED_BODY()

    /** Exact reflected UEnum or UScriptStruct path; empty for built-in values. */
    UPROPERTY()
    FString UnrealTypePath;

    /** Stable generated field order, flattened depth-first for recursive structs. */
    UPROPERTY()
    TArray<FUnifiedScriptManagedStructFieldPath> StructFields;
};

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

    /** Versioned call contract. Zero and one retain the scalar v1 compatibility path. */
    UPROPERTY()
    int32 AbiVersion = 1;

    /** Static libraries resolve their managed program from the generated-class sidecar. */
    UPROPERTY()
    bool bStatic = false;

    /** One type contract per managed argument, excluding injected World context. */
    UPROPERTY()
    TArray<FUnifiedScriptManagedValueTypeBinding> ParameterTypes;

    /** Exact return value contract; empty for void and built-in values. */
    UPROPERTY()
    FUnifiedScriptManagedValueTypeBinding ReturnType;
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

    /** Cook-retained program identity used by generated static FunctionLibrary calls. */
    UPROPERTY()
    TObjectPtr<UUnifiedScriptAsset> ManagedScriptAsset;

    void BindManagedFunctions();
    bool IsManagedFunctionBound(FName FunctionName) const;

    virtual void PostLoad() override;
    virtual void PostLoadDefaultObject(UObject* Object) override;

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
#if WITH_EDITOR
    virtual UClass* GetBlueprintClass() const override;
#endif
};
