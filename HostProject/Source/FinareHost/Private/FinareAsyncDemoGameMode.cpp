#include "FinareAsyncDemoGameMode.h"

#include "Camera/CameraActor.h"
#include "Engine/StaticMesh.h"
#include "EngineUtils.h"
#include "UObject/ConstructorHelpers.h"

AFinareAsyncDemoGameMode::AFinareAsyncDemoGameMode()
{
    PlayerControllerClass = AFinareAsyncDemoPlayerController::StaticClass();
    DefaultPawnClass = nullptr;
    static ConstructorHelpers::FObjectFinder<UStaticMesh> CubeAsset(TEXT("/Engine/BasicShapes/Cube.Cube"));
    static ConstructorHelpers::FObjectFinder<UStaticMesh> SphereAsset(TEXT("/Engine/BasicShapes/Sphere.Sphere"));
    Cube = CubeAsset.Object;
    Sphere = SphereAsset.Object;
}

void AFinareAsyncDemoPlayerController::BeginPlay()
{
    Super::BeginPlay();
    if (!IsLocalController()) return;
    bAutoManageActiveCameraTarget = false;
    for (TActorIterator<ACameraActor> It(GetWorld()); It; ++It)
    {
        if (!It->ActorHasTag(TEXT("UnifiedScript.AsyncUiCamera"))) continue;
        SetViewTarget(*It);
        break;
    }
    bShowMouseCursor = true;
    FInputModeGameAndUI Mode;
    Mode.SetHideCursorDuringCapture(false);
    Mode.SetLockMouseToViewportBehavior(EMouseLockMode::DoNotLock);
    SetInputMode(Mode);
}
