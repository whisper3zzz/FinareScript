/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-25 17:47:14
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 17:55:12
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\Public\UnifiedScriptWorldLibrary.h
 * @Description:
 */
#pragma once

#include "CoreMinimal.h"
#include "Engine/HitResult.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include "UnifiedScriptWorldLibrary.generated.h"

/**
 * Product-level World and resource primitives used by generated static bindings.
 * Gameplay policy remains managed; this library only exposes engine ownership
 * operations that do not have an instance UObject receiver.
 */
UCLASS()
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptWorldLibrary final
    : public UBlueprintFunctionLibrary
{
    GENERATED_BODY()

public:
    UFUNCTION(BlueprintCallable, Category="UnifiedScript|Resources", meta=(WorldContext="WorldContextObject"))
    static UObject* LoadObjectByPath(UObject* WorldContextObject, const FString& ObjectPath);

    UFUNCTION(BlueprintCallable, Category="UnifiedScript|Resources", meta=(WorldContext="WorldContextObject"))
    static UClass* LoadClassByPath(UObject* WorldContextObject, const FString& ClassPath);

    UFUNCTION(BlueprintCallable, Category="UnifiedScript|World", meta=(WorldContext="WorldContextObject"))
    static AActor* SpawnActor(
        UObject* WorldContextObject,
        UClass* ActorClass,
        const FTransform& Transform);

    UFUNCTION(BlueprintCallable, Category="UnifiedScript|World", meta=(WorldContext="WorldContextObject"))
    static bool LineTraceSingle(
        UObject* WorldContextObject,
        const FVector& Start,
        const FVector& End,
        AActor* IgnoreActor,
        FHitResult& OutHit,
        AActor*& OutHitActor);
};
