#pragma once

#include "CoreMinimal.h"
#include "Factories/BlueprintFactory.h"
#include "FinareAsset.h"
#include "FinareClassFactory.generated.h"

/** Deterministic creation failure seam used by headless transaction coverage. */
enum class EFinareClassCreationFailurePoint : uint8
{
	None,
	AfterExternalPublication,
};

/** Shared implementation for reviewed UObject, Actor, component, and function-library proxies. */
UCLASS(Abstract)
class FINAREEDITOR_API UFinareClassFactory : public UBlueprintFactory
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

	EFinareClassKind GetScriptClassKind() const { return ScriptClassKind; }

#if WITH_DEV_AUTOMATION_TESTS
	void SetFailurePointForTesting(
		EFinareClassCreationFailurePoint InFailurePoint)
	{
		FailurePointForTesting = InFailurePoint;
	}

	const FString& GetLastRecoveryReceiptPathForTesting() const
	{
		return LastRecoveryReceiptPathForTesting;
	}
#endif

protected:
	explicit UFinareClassFactory(const FObjectInitializer& ObjectInitializer);

	EFinareClassKind ScriptClassKind = EFinareClassKind::None;

#if WITH_DEV_AUTOMATION_TESTS
	EFinareClassCreationFailurePoint FailurePointForTesting =
		EFinareClassCreationFailurePoint::None;
	FString LastRecoveryReceiptPathForTesting;
#endif
};

/** Instantiable managed UObject class backed by UFinareObject. */
UCLASS()
class FINAREEDITOR_API UFinareObjectClassFactory final
	: public UFinareClassFactory
{
	GENERATED_BODY()

public:
	explicit UFinareObjectClassFactory(const FObjectInitializer& ObjectInitializer);

	virtual FText GetDisplayName() const override;
};

/** Placeable managed Actor class backed by AFinareActor. */
UCLASS()
class FINAREEDITOR_API UFinareActorClassFactory final
	: public UFinareClassFactory
{
	GENERATED_BODY()

public:
	explicit UFinareActorClassFactory(const FObjectInitializer& ObjectInitializer);

	virtual FText GetDisplayName() const override;
};

/** Attachable managed ActorComponent class backed by UFinareComponent. */
UCLASS()
class FINAREEDITOR_API UFinareActorComponentClassFactory final
	: public UFinareClassFactory
{
	GENERATED_BODY()

public:
	explicit UFinareActorComponentClassFactory(const FObjectInitializer& ObjectInitializer);

	virtual FText GetDisplayName() const override;
};

/** Static managed Blueprint-callable surface backed by a Blueprint Function Library. */
UCLASS()
class FINAREEDITOR_API UFinareBlueprintFunctionLibraryClassFactory final
	: public UFinareClassFactory
{
	GENERATED_BODY()

public:
	explicit UFinareBlueprintFunctionLibraryClassFactory(
		const FObjectInitializer& ObjectInitializer);

	virtual FText GetDisplayName() const override;
};
