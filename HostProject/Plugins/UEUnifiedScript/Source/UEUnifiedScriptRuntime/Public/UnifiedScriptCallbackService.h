#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "UnifiedScriptCallbackService.generated.h"

DECLARE_DYNAMIC_DELEGATE_RetVal_ThreeParams(bool, FUnifiedScriptQueryCallback,
    int32, Value, UPARAM(ref) FString&, Text, FVector&, Position);

/** Public sample of a reflected API that invokes and retains a typed callback. */
UCLASS(BlueprintType)
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptCallbackService : public UObject
{
    GENERATED_BODY()
public:
    UPROPERTY(BlueprintReadWrite, Category = "UnifiedScript|Callbacks")
    int32 LastQueryValue = 0;

    // Native-side observation of exactly-once calls in the public sample.
    int32 EvaluationCount = 0;

    UFUNCTION(BlueprintCallable, Category = "UnifiedScript|Callbacks")
    FString FormatReply(const FString& Text) { return FString::ChrN(9000, TEXT('x')) + Text; }

    UFUNCTION(BlueprintCallable, Category = "UnifiedScript|Callbacks")
    static UUnifiedScriptCallbackService* Create(UObject* Outer)
    {
        return Outer ? NewObject<UUnifiedScriptCallbackService>(Outer) : nullptr;
    }

    UFUNCTION(BlueprintCallable, Category = "UnifiedScript|Callbacks")
    void StoreCallback(FUnifiedScriptQueryCallback Callback) { SavedCallback = Callback; }

    UFUNCTION(BlueprintCallable, Category = "UnifiedScript|Callbacks")
    void ClearCallback() { SavedCallback.Unbind(); }

    UFUNCTION(BlueprintCallable, Category = "UnifiedScript|Callbacks")
    bool Evaluate(int32 Value, UPARAM(ref) FString& Text, FVector& Position)
    {
        ++EvaluationCount;
        return SavedCallback.IsBound() ? SavedCallback.Execute(Value, Text, Position) : false;
    }

    UFUNCTION(BlueprintCallable, Category = "UnifiedScript|Callbacks")
    bool EvaluateNow(FUnifiedScriptQueryCallback Callback, int32 Value,
        UPARAM(ref) FString& Text, FVector& Position)
    {
        ++EvaluationCount;
        return Callback.IsBound() ? Callback.Execute(Value, Text, Position) : false;
    }

private:
    // The callback scope, not this weak delegate copy, owns its transient endpoint.
    FUnifiedScriptQueryCallback SavedCallback;
};
