#pragma once

#include "CoreMinimal.h"
#include "Components/ActorComponent.h"
#include "UnifiedScriptComponent.generated.h"

UCLASS(BlueprintType, Blueprintable, ClassGroup=(UnifiedScript), meta=(BlueprintSpawnableComponent))
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptComponent : public UActorComponent
{
    GENERATED_BODY()

public:
    UUnifiedScriptComponent();

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript")
    FString ScriptAssetPath;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript")
    bool bStartAutomatically = true;

    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript")
    FString ActiveGeneration;

    UFUNCTION(BlueprintCallable, Category="UnifiedScript")
    void RequestReload();

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Verification")
    int32 Add(int32 Left, int32 Right) const;

    bool ConsumeReloadRequest();

private:
    bool bReloadRequested = false;
};
