#pragma once

#include "GameFramework/GameModeBase.h"
#include "GameFramework/PlayerController.h"
#include "UnifiedScriptAsyncDemoGameMode.generated.h"

class UStaticMesh;

/** Presentation/input policy only; gameplay stays in AsyncUiDemoScript.cs. */
UCLASS()
class UEUNIFIEDSCRIPTHOST_API AUnifiedScriptAsyncDemoPlayerController : public APlayerController
{
    GENERATED_BODY()
public:
    virtual void BeginPlay() override;
};

UCLASS()
class UEUNIFIEDSCRIPTHOST_API AUnifiedScriptAsyncDemoGameMode : public AGameModeBase
{
    GENERATED_BODY()
public:
    AUnifiedScriptAsyncDemoGameMode();

private:
    // The sample's configurable string paths are not cook dependencies. Keep
    // explicit references for the two shipped choices, including failure/retry tests.
    UPROPERTY()
    TObjectPtr<UStaticMesh> Cube;
    UPROPERTY()
    TObjectPtr<UStaticMesh> Sphere;
};
