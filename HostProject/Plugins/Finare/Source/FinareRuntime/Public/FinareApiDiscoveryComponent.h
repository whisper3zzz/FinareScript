#pragma once

#include "FinareComponent.h"
#include "FinareApiDiscoveryComponent.generated.h"

UENUM()
enum class EFinareDiscoveryWideMode : uint64
{
	Large = 1152921504606846977ULL
};

USTRUCT()
struct FFinareDiscoveryData
{
	GENERATED_BODY()
	UPROPERTY() int32 Value = 0;
	UPROPERTY() FString Label;
	UPROPERTY() TArray<int32> Numbers;
	UPROPERTY() int32 Fixed[2] = {7, 9};
};

/** Ordinary reflected API fixture: deliberately has no Blueprint exposure or generated thunks. */
UCLASS()
class FINARERUNTIME_API UFinareApiDiscoveryComponent : public UFinareComponent
{
	GENERATED_BODY()

public:
	UFUNCTION()
	int32 DiscoveryAdd(int32 Left, int32 Right) { return Left + Right; }

	UFUNCTION()
	int32 DiscoveryOffset(int32 Value) { return Value + 10; }

	UFUNCTION()
	bool DiscoveryOutputs(int32 Delta, UPARAM(ref) int32& Total, FString& Label)
	{
		++DiscoveryCalls;
		Total += Delta;
		Label = FString::Printf(TEXT("total=%d"), Total);
		return true;
	}

	UPROPERTY()
	int32 DiscoveryCalls = 0;

	UPROPERTY()
	FFinareDiscoveryData DiscoveryData;

	UFUNCTION()
	bool DiscoveryEchoData(FFinareDiscoveryData Input, FFinareDiscoveryData& Output)
	{
		Output = Input;
		++Output.Value;
		return true;
	}

	UPROPERTY()
	int32 DiscoveryResult = 0;

	UPROPERTY(VisibleAnywhere, Category="Discovery")
	int32 DiscoveryReadOnly = 7;

private:
	UFUNCTION()
	int32 DiscoveryPrivate() { return 99; }

	UPROPERTY()
	int32 DiscoveryPrivateValue = 99;
};
