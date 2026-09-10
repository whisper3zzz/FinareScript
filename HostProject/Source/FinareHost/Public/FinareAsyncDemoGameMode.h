#pragma once

#include "GameFramework/GameModeBase.h"
#include "GameFramework/PlayerController.h"
#include "FinareAsyncDemoGameMode.generated.h"

class UStaticMesh;

/** Presentation/input policy only; gameplay stays in AsyncUiDemoScript.cs. */
UCLASS()
class FINAREHOST_API AFinareAsyncDemoPlayerController : public APlayerController
{
    GENERATED_BODY()
public:
    virtual void BeginPlay() override;
};

UCLASS()
class FINAREHOST_API AFinareAsyncDemoGameMode : public AGameModeBase
{
    GENERATED_BODY()
public:
    AFinareAsyncDemoGameMode();

private:
    // The sample's configurable string paths are not cook dependencies. Keep
    // explicit references for the two shipped choices, including failure/retry tests.
    UPROPERTY()
    TObjectPtr<UStaticMesh> Cube;
    UPROPERTY()
    TObjectPtr<UStaticMesh> Sphere;
};
