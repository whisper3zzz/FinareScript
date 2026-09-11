#include "FinareWorldLibrary.h"

#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "GameFramework/PlayerController.h"
#include "InputCoreTypes.h"
#include "UObject/UObjectGlobals.h"
#include "FinareWorldSubsystem.h"

namespace
{
	template <typename TObjectType>
	TObjectType* RetainForWorld(UObject* WorldContextObject, TObjectType* Resource)
	{
		UWorld* World = IsValid(WorldContextObject) ? WorldContextObject->GetWorld() : nullptr;
		if (World != nullptr && IsValid(Resource))
		{
			if (UFinareWorldSubsystem* Subsystem =
				World->GetSubsystem<UFinareWorldSubsystem>())
			{
				Subsystem->RetainLoadedResource(Resource);
			}
		}
		return Resource;
	}
}

bool UFinareWorldLibrary::ClaimGameplayLifecycle(
	UObject* WorldContextObject,
	UObject* Claimant,
	UObject* HostObject,
	FName LifecycleKey,
	EFinareRuntimeOwner RuntimeOwner,
	FString& OutError)
{
	UWorld* World = IsValid(WorldContextObject) ? WorldContextObject->GetWorld() : nullptr;
	UFinareWorldSubsystem* Subsystem =
		World != nullptr ? World->GetSubsystem<UFinareWorldSubsystem>() : nullptr;
	if (Subsystem == nullptr)
	{
		OutError = TEXT("The Finare World ownership arbiter is unavailable.");
		return false;
	}
	return Subsystem->ClaimGameplayLifecycle(
		Claimant,
		HostObject,
		LifecycleKey,
		RuntimeOwner,
		OutError);
}

bool UFinareWorldLibrary::ReleaseGameplayLifecycle(
	UObject* WorldContextObject,
	UObject* Claimant,
	UObject* HostObject,
	FName LifecycleKey,
	EFinareRuntimeOwner RuntimeOwner,
	FString& OutError)
{
	UWorld* World = IsValid(WorldContextObject) ? WorldContextObject->GetWorld() : nullptr;
	UFinareWorldSubsystem* Subsystem =
		World != nullptr ? World->GetSubsystem<UFinareWorldSubsystem>() : nullptr;
	if (Subsystem == nullptr)
	{
		OutError = TEXT("The Finare World ownership arbiter is unavailable.");
		return false;
	}
	return Subsystem->ReleaseGameplayLifecycle(
		Claimant,
		HostObject,
		LifecycleKey,
		RuntimeOwner,
		OutError);
}

FString UFinareWorldLibrary::GetGameplayLifecycleOwnershipReport(
	UObject* WorldContextObject)
{
	UWorld* World = IsValid(WorldContextObject) ? WorldContextObject->GetWorld() : nullptr;
	const UFinareWorldSubsystem* Subsystem =
		World != nullptr ? World->GetSubsystem<UFinareWorldSubsystem>() : nullptr;
	return Subsystem != nullptr
		       ? Subsystem->BuildGameplayLifecycleOwnershipReport()
		       : FString();
}

UObject* UFinareWorldLibrary::LoadObjectByPath(
	UObject* WorldContextObject,
	const FString& ObjectPath)
{
	if (!IsValid(WorldContextObject) || ObjectPath.IsEmpty())
	{
		return nullptr;
	}
	return RetainForWorld(
		WorldContextObject,
		StaticLoadObject(UObject::StaticClass(), nullptr, *ObjectPath));
}

UClass* UFinareWorldLibrary::LoadClassByPath(
	UObject* WorldContextObject,
	const FString& ClassPath)
{
	if (!IsValid(WorldContextObject) || ClassPath.IsEmpty())
	{
		return nullptr;
	}
	return RetainForWorld(
		WorldContextObject,
		StaticLoadClass(UObject::StaticClass(), nullptr, *ClassPath));
}

AActor* UFinareWorldLibrary::SpawnActor(
	UObject* WorldContextObject,
	UClass* ActorClass,
	const FTransform& Transform)
{
	UWorld* World = IsValid(WorldContextObject) ? WorldContextObject->GetWorld() : nullptr;
	if (World == nullptr || ActorClass == nullptr || !ActorClass->IsChildOf(AActor::StaticClass()))
	{
		return nullptr;
	}
	return World->SpawnActor<AActor>(ActorClass, Transform);
}

bool UFinareWorldLibrary::DestroyActor(
	UObject* WorldContextObject,
	AActor* Actor)
{
	UWorld* World = IsValid(WorldContextObject) ? WorldContextObject->GetWorld() : nullptr;
	return World != nullptr && IsValid(Actor) && Actor->GetWorld() == World &&
		Actor->Destroy();
}

UActorComponent* UFinareWorldLibrary::FindComponentByClass(
	UObject* WorldContextObject,
	AActor* Actor,
	UClass* ComponentClass)
{
	UWorld* World = IsValid(WorldContextObject) ? WorldContextObject->GetWorld() : nullptr;
	if (World == nullptr || !IsValid(Actor) || Actor->GetWorld() != World ||
		ComponentClass == nullptr ||
		!ComponentClass->IsChildOf(UActorComponent::StaticClass()))
	{
		return nullptr;
	}
	return Actor->GetComponentByClass(ComponentClass);
}

bool UFinareWorldLibrary::IsInputKeyDown(
	UObject* WorldContextObject,
	APlayerController* PlayerController,
	FName KeyName)
{
	UWorld* World = IsValid(WorldContextObject) ? WorldContextObject->GetWorld() : nullptr;
	return World != nullptr && IsValid(PlayerController) &&
		PlayerController->GetWorld() == World && !KeyName.IsNone() &&
		PlayerController->IsInputKeyDown(FKey(KeyName));
}

float UFinareWorldLibrary::GetWorldTimeSeconds(UObject* WorldContextObject)
{
	const UWorld* World =
		IsValid(WorldContextObject) ? WorldContextObject->GetWorld() : nullptr;
	return World != nullptr ? World->GetTimeSeconds() : 0.0f;
}

bool UFinareWorldLibrary::LineTraceSingle(
	UObject* WorldContextObject,
	const FVector& Start,
	const FVector& End,
	AActor* IgnoreActor,
	FHitResult& OutHit,
	AActor*& OutHitActor)
{
	OutHit = {};
	OutHitActor = nullptr;
	UWorld* World = IsValid(WorldContextObject) ? WorldContextObject->GetWorld() : nullptr;
	if (World == nullptr)
	{
		return false;
	}
	const FCollisionQueryParams QueryParams(
		SCENE_QUERY_STAT(FinareLineTraceSingle),
		true,
		IgnoreActor);
	const bool bHit = World->LineTraceSingleByChannel(
		OutHit,
		Start,
		End,
		ECC_Visibility,
		QueryParams);
	OutHitActor = bHit ? OutHit.GetActor() : nullptr;
	return bHit;
}
