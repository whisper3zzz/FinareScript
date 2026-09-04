#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "UnifiedScriptByteBuffer.h"
#include "UnifiedScriptNativeExtensionFixture.generated.h"

/** Deliberately lacks UPROPERTY/UFUNCTION metadata on every bound member. */
UCLASS(BlueprintType)
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptNativeExtensionFixture : public UObject
{
    GENERATED_BODY()

public:
    int32 Counter = 7;
    FUnifiedScriptByteBuffer Payload;
    int32 BinaryCalls = 0;

    void StoreBytes(const FUnifiedScriptByteBuffer& Value) { Payload = Value; ++BinaryCalls; }
    FUnifiedScriptByteBuffer ReadBytes() { ++BinaryCalls; return Payload; }
    FUnifiedScriptByteBuffer TransformBytes(const FUnifiedScriptByteBuffer& Input,
        FUnifiedScriptByteBuffer& InOut, int32& Calls)
    {
        ++BinaryCalls;
        TArray<uint8> Output;
        Output.SetNumZeroed(Input.Num() + 8192);
        const auto View = Input.GetView();
        for (int32 Index = 0; Index < View.Num(); ++Index) Output[Index] = View[Index] ^ 0xA5;
        InOut = FUnifiedScriptByteBuffer::TakeOwnership(MoveTemp(Output));
        Payload = InOut;
        Calls = BinaryCalls;
        return InOut;
    }

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
