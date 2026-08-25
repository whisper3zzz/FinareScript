/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-25 12:41:41
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 14:22:40
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\Private\UnifiedScriptActor.cpp
 * @Description:
 */
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
