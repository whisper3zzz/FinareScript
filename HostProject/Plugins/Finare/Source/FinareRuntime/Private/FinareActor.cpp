#include "FinareActor.h"

#include "FinareComponent.h"

AFinareActor::AFinareActor()
{
	PrimaryActorTick.bCanEverTick = false;
	ScriptComponent = CreateDefaultSubobject<UFinareComponent>(TEXT("UnifiedScript"));
}

UFinareComponent* AFinareActor::GetFinareComponent() const
{
	return ScriptComponent;
}

bool AFinareActor::StartManagedBehavior()
{
	return ScriptComponent != nullptr && ScriptComponent->StartScript();
}

void AFinareActor::StopManagedBehavior()
{
	if (ScriptComponent != nullptr)
	{
		ScriptComponent->StopScript();
	}
}

bool AFinareActor::ReloadManagedBehavior()
{
	return ScriptComponent != nullptr && ScriptComponent->ReloadScript();
}

void AFinareActor::SetManagedActorState(const FString& Value)
{
	ManagedActorState = Value;
}

void AFinareActor::IncrementManagedTickCount()
{
	++ManagedTickCount;
}
