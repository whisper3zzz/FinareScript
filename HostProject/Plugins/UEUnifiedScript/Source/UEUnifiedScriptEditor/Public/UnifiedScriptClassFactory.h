#pragma once

#include "CoreMinimal.h"
#include "Factories/BlueprintFactory.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptClassFactory.generated.h"

/** Deterministic creation failure seam used by headless transaction coverage. */
enum class EUnifiedScriptClassCreationFailurePoint : uint8
{
    None,
    AfterExternalPublication,
};

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

    /**
     * Script Class creation needs the asset name before it writes C# and
     * deployment identities. Keep it on the named Tools workflow instead of
     * the Content Browser's create-then-rename menu.
     */
    virtual bool ShouldShowInNewMenu() const override { return false; }

    EUnifiedScriptClassKind GetScriptClassKind() const { return ScriptClassKind; }

#if WITH_DEV_AUTOMATION_TESTS
    void SetFailurePointForTesting(
        EUnifiedScriptClassCreationFailurePoint InFailurePoint)
    {
        FailurePointForTesting = InFailurePoint;
    }

    const FString& GetLastRecoveryReceiptPathForTesting() const
    {
        return LastRecoveryReceiptPathForTesting;
    }
#endif

protected:
    explicit UUnifiedScriptClassFactory(const FObjectInitializer& ObjectInitializer);

    EUnifiedScriptClassKind ScriptClassKind = EUnifiedScriptClassKind::None;

#if WITH_DEV_AUTOMATION_TESTS
    EUnifiedScriptClassCreationFailurePoint FailurePointForTesting =
        EUnifiedScriptClassCreationFailurePoint::None;
    FString LastRecoveryReceiptPathForTesting;
#endif
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
