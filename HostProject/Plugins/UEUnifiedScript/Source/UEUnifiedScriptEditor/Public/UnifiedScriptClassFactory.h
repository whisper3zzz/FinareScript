#pragma once

#include "CoreMinimal.h"
#include "Factories/BlueprintFactory.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptClassFactory.generated.h"

/** Shared implementation for the Actor and ActorComponent Script Class assets. */
UCLASS(Abstract)
class UEUNIFIEDSCRIPTEDITOR_API UUnifiedScriptClassFactory : public UBlueprintFactory
{
    GENERATED_BODY()

public:
    virtual UObject* FactoryCreateNew(
        UClass* Class,
        UObject* InParent,
        FName Name,
        EObjectFlags Flags,
        UObject* Context,
        FFeedbackContext* Warn,
        FName CallingContext) override;

    EUnifiedScriptClassKind GetScriptClassKind() const { return ScriptClassKind; }

protected:
    explicit UUnifiedScriptClassFactory(const FObjectInitializer& ObjectInitializer);

    EUnifiedScriptClassKind ScriptClassKind = EUnifiedScriptClassKind::None;
};

/** Placeable managed Actor class backed by AUnifiedScriptActor. */
UCLASS()
class UEUNIFIEDSCRIPTEDITOR_API UUnifiedScriptActorClassFactory final
    : public UUnifiedScriptClassFactory
{
    GENERATED_BODY()

public:
    explicit UUnifiedScriptActorClassFactory(const FObjectInitializer& ObjectInitializer);

    virtual FText GetDisplayName() const override;
};

/** Attachable managed ActorComponent class backed by UUnifiedScriptComponent. */
UCLASS()
class UEUNIFIEDSCRIPTEDITOR_API UUnifiedScriptActorComponentClassFactory final
    : public UUnifiedScriptClassFactory
{
    GENERATED_BODY()

public:
    explicit UUnifiedScriptActorComponentClassFactory(const FObjectInitializer& ObjectInitializer);

    virtual FText GetDisplayName() const override;
};
