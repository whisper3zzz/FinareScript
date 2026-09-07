#pragma once

#include "CoreMinimal.h"
#include "UnifiedScriptActor.h"
#include "UnifiedScriptEventExtensionFixture.generated.h"

/** Native parent-event surface used by materializer and later parent-call tests. */
UCLASS(BlueprintType)
class UEUNIFIEDSCRIPTRUNTIME_API AUnifiedScriptEventExtensionFixture : public AUnifiedScriptActor
{
    GENERATED_BODY()

public:
    UFUNCTION(BlueprintNativeEvent, BlueprintCallable, Category="UnifiedScript|Tests")
    int32 EvaluateInherited(
        int32 NativeInput,
        UPARAM(ref) int32& NativeAccumulator,
        int32& NativeOutput);

    UFUNCTION(BlueprintNativeEvent, BlueprintCallable, Category="UnifiedScript|Tests")
    int32 EvaluateInheritedAlternate(
        int32 NativeInput,
        UPARAM(ref) int32& NativeAccumulator,
        int32& NativeOutput);

    UFUNCTION(BlueprintImplementableEvent, Category="UnifiedScript|Tests")
    void ReceiveInherited(const FString& NativeText, AActor* NativeActor);

    UFUNCTION(BlueprintNativeEvent, BlueprintCallable, Category="UnifiedScript|Tests")
    TArray<int32> ExchangeInheritedArray(
        const TArray<int32>& NativeInput,
        UPARAM(ref) TArray<int32>& NativeState,
        TArray<int32>& NativeOutput);

protected:
    virtual int32 EvaluateInherited_Implementation(
        int32 NativeInput,
        int32& NativeAccumulator,
        int32& NativeOutput);

    virtual int32 EvaluateInheritedAlternate_Implementation(
        int32 NativeInput,
        int32& NativeAccumulator,
        int32& NativeOutput);

    virtual TArray<int32> ExchangeInheritedArray_Implementation(
        const TArray<int32>& NativeInput,
        TArray<int32>& NativeState,
        TArray<int32>& NativeOutput);
};
