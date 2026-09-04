#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "UnifiedScriptNativeExtensionFixture.generated.h"

/** Deliberately lacks UPROPERTY/UFUNCTION metadata on every bound member. */
UCLASS(BlueprintType)
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptNativeExtensionFixture : public UObject
{
    GENERATED_BODY()

public:
    int32 Counter = 7;

    void Reset() { Counter = 6; }

    int32 Scale(int32 Value) const { return Counter * Value; }
    double Scale(double Value) const { return Counter * Value; }

    static int32 Multiply(int32 Left, int32 Right) { return Left * Right; }

    bool Accumulate(const TArray<int32>& Values, int32& Total, FString& Label) const
    {
        for (int32 Value : Values) Total += Value;
        Label = FString::Printf(TEXT("total:%d"), Total);
        return Values.Num() > 0;
    }
};
