#pragma once
#include "CoreMinimal.h"
#include "FinareInteropFixture.h"
#include <functional>
#include <vector>
#include "FinareInteropParityFixture.generated.h"

USTRUCT(BlueprintType)
struct FFinareParityItem
{
    GENERATED_BODY()
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
    int32 Value = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
    TScriptInterface<IFinareInteropContract> Target;
};

USTRUCT(BlueprintType)
struct FFinareParityGroup
{
    GENERATED_BODY()
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
    TArray<FFinareParityItem> Items;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
    TMap<int32, FFinareParityItem> Lookup;
};

UCLASS(BlueprintType)
class FINARERUNTIME_API UFinareInteropParityFixture : public UObject
{
    GENERATED_BODY()
public:
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
    TArray<int32> Numbers;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
    TArray<uint8> Bytes;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
    TArray<TScriptInterface<IFinareInteropContract>> Targets;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
    TSet<int32> IntSet;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
    TMap<int32, TScriptInterface<IFinareInteropContract>> TargetMap;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
    FFinareParityGroup Group;
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category="Finare|Interop")
    TArray<int32> ReadOnlyNumbers;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
    int32 Result = 0;
};

namespace UE::Finare::Parity
{
    struct FPadding { virtual ~FPadding() = default; uint64 Padding[4]{}; };
    struct FBase { int32 BaseValue = 7; int32 Add(int32 Delta) { return BaseValue += Delta; } };
    struct FBuffer : FPadding, FBase
    {
        inline static int32 Live = 0;
        inline static std::function<int32(int32)> Saved;
        std::vector<int32> Samples{1, 2, 3};
        FBuffer() { ++Live; }
        ~FBuffer() override { --Live; }
        int32 Sum() const { int32 Result = 0; for (int32 Value : Samples) Result += Value; return Result; }
        void Resize(int32 Count) { if (Count >= 0 && Count <= 4096) Samples.resize(Count); }
        void Save(std::function<int32(int32)> Callback) { Saved = MoveTemp(Callback); }
        int32 Call(int32 Value) const { return Saved ? Saved(Value) : -1; }
    };
}
