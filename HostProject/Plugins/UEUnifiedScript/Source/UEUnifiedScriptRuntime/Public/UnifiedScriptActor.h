#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "UnifiedScriptActor.generated.h"

class UUnifiedScriptComponent;

/**
 * Placeable Actor whose gameplay behavior is supplied by a managed
 * UnrealActorScript. The native class remains the stable Blueprint/UObject
 * identity while managed generations can be reloaded independently.
 */
UCLASS(BlueprintType, Blueprintable)
class UEUNIFIEDSCRIPTRUNTIME_API AUnifiedScriptActor : public AActor
{
    GENERATED_BODY()

public:
    AUnifiedScriptActor();

    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="UnifiedScript")
    TObjectPtr<UUnifiedScriptComponent> ScriptComponent;

    /** Blueprint-visible state controlled by the managed Actor behavior. */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|State")
    FString ManagedActorState;

    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Category="UnifiedScript|State")
    int32 ManagedTickCount = 0;

    UFUNCTION(BlueprintPure, Category="UnifiedScript")
    UUnifiedScriptComponent* GetUnifiedScriptComponent() const;

    UFUNCTION(BlueprintCallable, Category="UnifiedScript")
    bool StartManagedBehavior();

    UFUNCTION(BlueprintCallable, Category="UnifiedScript")
    void StopManagedBehavior();

    UFUNCTION(BlueprintCallable, Category="UnifiedScript")
    bool ReloadManagedBehavior();

    UFUNCTION(BlueprintCallable, Category="UnifiedScript|State")
    void SetManagedActorState(const FString& Value);

    UFUNCTION(BlueprintCallable, Category="UnifiedScript|State")
    void IncrementManagedTickCount();
};
