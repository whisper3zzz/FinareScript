#pragma once

#include "FinareComponent.h"
#include "FinareApiDiscoveryComponent.generated.h"

/** Ordinary reflected API fixture: deliberately has no Blueprint exposure or generated thunks. */
UCLASS()
class FINARERUNTIME_API UFinareApiDiscoveryComponent : public UFinareComponent
{
	GENERATED_BODY()

public:
	UFUNCTION()
	int32 DiscoveryAdd(int32 Left, int32 Right) { return Left + Right; }

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
