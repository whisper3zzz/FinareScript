#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "FinareCallbackService.generated.h"

DECLARE_DYNAMIC_DELEGATE_RetVal_ThreeParams(bool, FFinareQueryCallback,
                                            int32, Value, UPARAM(ref) FString&, Text, FVector&, Position);

/** Public sample of a reflected API that invokes and retains a typed callback. */
UCLASS(BlueprintType)
class FINARERUNTIME_API UFinareCallbackService : public UObject
{
	GENERATED_BODY()

public:
	UPROPERTY(BlueprintReadWrite, Category = "Finare|Callbacks")
	int32 LastQueryValue = 0;

	// Native-side observation of exactly-once calls in the public sample.
	int32 EvaluationCount = 0;

	UFUNCTION(BlueprintCallable, Category = "Finare|Callbacks")
	FString FormatReply(const FString& Text) { return FString::ChrN(9000, TEXT('x')) + Text; }

	UFUNCTION(BlueprintCallable, Category = "Finare|Callbacks")
	static UFinareCallbackService* Create(UObject* Outer)
	{
		return Outer ? NewObject<UFinareCallbackService>(Outer) : nullptr;
	}

	UFUNCTION(BlueprintCallable, Category = "Finare|Callbacks")
	void StoreCallback(FFinareQueryCallback Callback) { SavedCallback = Callback; }

	UFUNCTION(BlueprintCallable, Category = "Finare|Callbacks")
	void ClearCallback() { SavedCallback.Unbind(); }

	UFUNCTION(BlueprintCallable, Category = "Finare|Callbacks")
	bool Evaluate(int32 Value, UPARAM(ref) FString& Text, FVector& Position)
	{
		++EvaluationCount;
		return SavedCallback.IsBound() ? SavedCallback.Execute(Value, Text, Position) : false;
	}

	UFUNCTION(BlueprintCallable, Category = "Finare|Callbacks")
	bool EvaluateNow(FFinareQueryCallback Callback, int32 Value,
	                 UPARAM(ref) FString& Text, FVector& Position)
	{
		++EvaluationCount;
		return Callback.IsBound() ? Callback.Execute(Value, Text, Position) : false;
	}

private:
	// The callback scope, not this weak delegate copy, owns its transient endpoint.
	FFinareQueryCallback SavedCallback;
};
