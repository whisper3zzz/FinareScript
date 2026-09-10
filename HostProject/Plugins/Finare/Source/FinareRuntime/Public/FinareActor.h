/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-25 12:41:41
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 14:22:40
 * @FilePath: \Plugins\Finare\Source\FinareRuntime\Public\FinareActor.h
 * @Description:
 */
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "FinareActor.generated.h"

class UFinareComponent;

/**
 * Placeable Actor whose gameplay behavior is supplied by a managed
 * UnrealActorScript. The native class remains the stable Blueprint/UObject
 * identity while managed generations can be reloaded independently.
 */
UCLASS(BlueprintType, Blueprintable)
class FINARERUNTIME_API AFinareActor : public AActor
{
    GENERATED_BODY()

public:
    AFinareActor();

    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="Finare")
    TObjectPtr<UFinareComponent> ScriptComponent;

    /** Blueprint-visible state controlled by the managed Actor behavior. */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|State")
    FString ManagedActorState;

    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Category="Finare|State")
    int32 ManagedTickCount = 0;

    UFUNCTION(BlueprintPure, Category="Finare")
    UFinareComponent* GetFinareComponent() const;

    UFUNCTION(BlueprintCallable, Category="Finare")
    bool StartManagedBehavior();

    UFUNCTION(BlueprintCallable, Category="Finare")
    void StopManagedBehavior();

    UFUNCTION(BlueprintCallable, Category="Finare")
    bool ReloadManagedBehavior();

    UFUNCTION(BlueprintCallable, Category="Finare|State")
    void SetManagedActorState(const FString& Value);

    UFUNCTION(BlueprintCallable, Category="Finare|State")
    void IncrementManagedTickCount();
};
