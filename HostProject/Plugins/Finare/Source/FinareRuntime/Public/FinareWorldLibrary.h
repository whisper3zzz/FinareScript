#pragma once

#include "CoreMinimal.h"
#include "Engine/HitResult.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include "FinareWorldSubsystem.h"
#include "FinareWorldLibrary.generated.h"

/**
 * Product-level World and resource primitives used by generated static bindings.
 * Gameplay policy remains managed; this library only exposes engine ownership
 * operations that do not have an instance UObject receiver.
 */
UCLASS()
class FINARERUNTIME_API UFinareWorldLibrary final
	: public UBlueprintFunctionLibrary
{
	GENERATED_BODY()

public:
	/** Shared Puerts/Finare arbitration point for one host lifecycle route. */
	UFUNCTION(BlueprintCallable, Category="Finare|Coexistence", meta=(WorldContext="WorldContextObject"))
	static bool ClaimGameplayLifecycle(
		UObject* WorldContextObject,
		UObject* Claimant,
		UObject* HostObject,
		FName LifecycleKey,
		EFinareRuntimeOwner RuntimeOwner,
		FString& OutError);

	UFUNCTION(BlueprintCallable, Category="Finare|Coexistence", meta=(WorldContext="WorldContextObject"))
	static bool ReleaseGameplayLifecycle(
		UObject* WorldContextObject,
		UObject* Claimant,
		UObject* HostObject,
		FName LifecycleKey,
		EFinareRuntimeOwner RuntimeOwner,
		FString& OutError);

	UFUNCTION(BlueprintPure, Category="Finare|Coexistence", meta=(WorldContext="WorldContextObject"))
	static FString GetGameplayLifecycleOwnershipReport(UObject* WorldContextObject);

	UFUNCTION(BlueprintCallable, Category="Finare|Resources", meta=(WorldContext="WorldContextObject"))
	static UObject* LoadObjectByPath(UObject* WorldContextObject, const FString& ObjectPath);

	UFUNCTION(BlueprintCallable, Category="Finare|Resources", meta=(WorldContext="WorldContextObject"))
	static UClass* LoadClassByPath(UObject* WorldContextObject, const FString& ClassPath);

	UFUNCTION(BlueprintCallable, Category="Finare|World", meta=(WorldContext="WorldContextObject"))
	static AActor* SpawnActor(
		UObject* WorldContextObject,
		UClass* ActorClass,
		const FTransform& Transform);

	UFUNCTION(BlueprintCallable, Category="Finare|World", meta=(WorldContext="WorldContextObject"))
	static bool DestroyActor(UObject* WorldContextObject, AActor* Actor);

	UFUNCTION(BlueprintPure, Category="Finare|Components", meta=(WorldContext="WorldContextObject"))
	static UActorComponent* FindComponentByClass(
		UObject* WorldContextObject,
		AActor* Actor,
		UClass* ComponentClass);

	UFUNCTION(BlueprintPure, Category="Finare|Input", meta=(WorldContext="WorldContextObject"))
	static bool IsInputKeyDown(
		UObject* WorldContextObject,
		APlayerController* PlayerController,
		FName KeyName);

	UFUNCTION(BlueprintPure, Category="Finare|World", meta=(WorldContext="WorldContextObject"))
	static float GetWorldTimeSeconds(UObject* WorldContextObject);

	UFUNCTION(BlueprintCallable, Category="Finare|World", meta=(WorldContext="WorldContextObject"))
	static bool LineTraceSingle(
		UObject* WorldContextObject,
		const FVector& Start,
		const FVector& End,
		AActor* IgnoreActor,
		FHitResult& OutHit,
		AActor*& OutHitActor);
};
