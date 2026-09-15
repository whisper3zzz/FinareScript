#include "FinareWorldLibrary.h"

#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "GameFramework/PlayerController.h"
#include "InputCoreTypes.h"
#include "UObject/UObjectGlobals.h"
#include "FinareWorldSubsystem.h"
#include "FinareWorldContext.h"
#include "FinareBlueprint.h"
#include "FinareComponent.h"
#include "FinareAsset.h"
#include "Kismet/BlueprintFunctionLibrary.h"

namespace
{
	template <typename TObjectType>
	TObjectType* RetainForWorld(UObject* WorldContextObject, TObjectType* Resource)
	{
		UWorld* World = UE::Finare::WorldContext::Resolve(WorldContextObject);
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

namespace
{
	UWorld* ResolveRuntimeWorld(UObject* Context)
	{
		UWorld* World = UE::Finare::WorldContext::Resolve(Context);
		return World != nullptr && World->IsGameWorld() ? World : nullptr;
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
	UWorld* World = UE::Finare::WorldContext::Resolve(WorldContextObject);
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
	UWorld* World = UE::Finare::WorldContext::Resolve(WorldContextObject);
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
	UWorld* World = UE::Finare::WorldContext::Resolve(WorldContextObject);
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
	UWorld* World = UE::Finare::WorldContext::Resolve(WorldContextObject);
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
	UWorld* World = UE::Finare::WorldContext::Resolve(WorldContextObject);
	return World != nullptr && IsValid(Actor) && Actor->GetWorld() == World &&
		Actor->Destroy();
}

UActorComponent* UFinareWorldLibrary::FindComponentByClass(
	UObject* WorldContextObject,
	AActor* Actor,
	UClass* ComponentClass)
{
	UWorld* World = UE::Finare::WorldContext::Resolve(WorldContextObject);
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
	UWorld* World = UE::Finare::WorldContext::Resolve(WorldContextObject);
	return World != nullptr && IsValid(PlayerController) &&
		PlayerController->GetWorld() == World && !KeyName.IsNone() &&
		PlayerController->IsInputKeyDown(FKey(KeyName));
}

float UFinareWorldLibrary::GetWorldTimeSeconds(UObject* WorldContextObject)
{
	const UWorld* World = UE::Finare::WorldContext::Resolve(WorldContextObject);
	return World != nullptr ? World->GetTimeSeconds() : 0.0f;
}

TArray<FString> UFinareWorldLibrary::GetObjectTypePaths(UObject* Object)
{
	TArray<FString> Paths;
	if (!IsValid(Object))
	{
		return Paths;
	}

	for (const UClass* Class = Object->GetClass(); Class != nullptr; Class = Class->GetSuperClass())
	{
		Paths.Add(Class->GetPathName());
	}
	return Paths;
}

bool UFinareWorldLibrary::IsObjectOfClass(UObject* Object, const FString& ClassPath)
{
	if (!IsValid(Object) || ClassPath.IsEmpty())
	{
		return false;
	}

	const UClass* Candidate = FindObject<UClass>(nullptr, *ClassPath, EFindObjectFlags::None);
	if (Candidate == nullptr)
	{
		return false;
	}
	return Candidate->HasAnyClassFlags(CLASS_Interface)
		? Object->GetClass()->ImplementsInterface(Candidate)
		: Object->IsA(Candidate);
}

UObject* UFinareWorldLibrary::CreateObject(
	UObject* WorldContextObject,
	UClass* ObjectClass,
	UObject* Outer)
{
	UWorld* World = ResolveRuntimeWorld(WorldContextObject);
	if (World == nullptr || ObjectClass == nullptr ||
		ObjectClass->HasAnyClassFlags(CLASS_Abstract))
	{
		return nullptr;
	}
	if (ObjectClass->IsChildOf(AActor::StaticClass()) ||
		ObjectClass->IsChildOf(UActorComponent::StaticClass()) ||
		ObjectClass->IsChildOf(UBlueprintFunctionLibrary::StaticClass()) ||
		ObjectClass->IsChildOf(UClass::StaticClass()))
	{
		return nullptr;
	}
	const UFinareBlueprintGeneratedClass* GeneratedClass =
		Cast<UFinareBlueprintGeneratedClass>(ObjectClass);
	if (GeneratedClass != nullptr &&
		(GeneratedClass->ManagedScriptAsset == nullptr ||
		 GeneratedClass->ManagedScriptAsset->ScriptClassKind != EFinareClassKind::Object))
	{
		return nullptr;
	}
	if (Outer == nullptr)
	{
		Outer = World;
	}
	if (!IsValid(Outer) || Outer->IsTemplate() ||
		UE::Finare::WorldContext::Resolve(Outer) != World)
	{
		return nullptr;
	}
	if (ObjectClass->ClassWithin != nullptr &&
		!Outer->IsA(ObjectClass->ClassWithin))
	{
		return nullptr;
	}

	UObject* Instance = NewObject<UObject>(Outer, ObjectClass);
	if (Instance == nullptr)
	{
		return nullptr;
	}
	if (GeneratedClass == nullptr)
	{
		return Instance;
	}
	FString Error;
	UFinareComponent* ExecutionHost = UFinareComponent::CreateExecutionHost(
		Instance,
		GeneratedClass->ManagedScriptAsset,
		Error);
	if (ExecutionHost == nullptr || !ExecutionHost->StartScript())
	{
		if (ExecutionHost != nullptr)
		{
			ExecutionHost->StopScript();
		}
		Instance->MarkAsGarbage();
		return nullptr;
	}
	return Instance;
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
	UWorld* World = UE::Finare::WorldContext::Resolve(WorldContextObject);
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
