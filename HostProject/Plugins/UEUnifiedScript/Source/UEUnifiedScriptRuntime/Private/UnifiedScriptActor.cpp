#include "UnifiedScriptActor.h"

#include "UnifiedScriptComponent.h"

AUnifiedScriptActor::AUnifiedScriptActor()
{
    PrimaryActorTick.bCanEverTick = false;
    ScriptComponent = CreateDefaultSubobject<UUnifiedScriptComponent>(TEXT("UnifiedScript"));
}

UUnifiedScriptComponent* AUnifiedScriptActor::GetUnifiedScriptComponent() const
{
    return ScriptComponent;
}

bool AUnifiedScriptActor::StartManagedBehavior()
{
    return ScriptComponent != nullptr && ScriptComponent->StartScript();
}

void AUnifiedScriptActor::StopManagedBehavior()
{
    if (ScriptComponent != nullptr)
    {
        ScriptComponent->StopScript();
    }
}

bool AUnifiedScriptActor::ReloadManagedBehavior()
{
    return ScriptComponent != nullptr && ScriptComponent->ReloadScript();
}

void AUnifiedScriptActor::SetManagedActorState(const FString& Value)
{
    ManagedActorState = Value;
}

void AUnifiedScriptActor::IncrementManagedTickCount()
{
    ++ManagedTickCount;
}
