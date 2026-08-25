/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-25 17:47:14
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 17:55:11
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\Private\UnifiedScriptWorldLibrary.cpp
 * @Description:
 */
#include "UnifiedScriptWorldLibrary.h"

#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "UObject/UObjectGlobals.h"
#include "UnifiedScriptWorldSubsystem.h"

namespace
{
template <typename TObjectType>
TObjectType* RetainForWorld(UObject* WorldContextObject, TObjectType* Resource)
{
    UWorld* World = IsValid(WorldContextObject) ? WorldContextObject->GetWorld() : nullptr;
    if (World != nullptr && IsValid(Resource))
    {
        if (UUnifiedScriptWorldSubsystem* Subsystem =
                World->GetSubsystem<UUnifiedScriptWorldSubsystem>())
        {
            Subsystem->RetainLoadedResource(Resource);
        }
    }
    return Resource;
}
}

UObject* UUnifiedScriptWorldLibrary::LoadObjectByPath(
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

UClass* UUnifiedScriptWorldLibrary::LoadClassByPath(
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

AActor* UUnifiedScriptWorldLibrary::SpawnActor(
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

bool UUnifiedScriptWorldLibrary::LineTraceSingle(
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
        SCENE_QUERY_STAT(UnifiedScriptLineTraceSingle),
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
