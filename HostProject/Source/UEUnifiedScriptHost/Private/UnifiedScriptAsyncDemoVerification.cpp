#include "UnifiedScriptAsyncDemoVerification.h"

#include "UnifiedScriptActor.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "Blueprint/UserWidget.h"
#include "Components/Button.h"
#include "Components/StaticMeshComponent.h"
#include "Components/TextBlock.h"
#include "Containers/Ticker.h"
#include "Engine/Engine.h"
#include "Engine/StaticMeshActor.h"
#include "EngineUtils.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "UObject/UObjectIterator.h"

namespace UE::UnifiedScript::AsyncDemoVerification
{
namespace
{
FTSTicker::FDelegateHandle Handle;
TWeakObjectPtr<AUnifiedScriptActor> Actor;
TWeakObjectPtr<UUserWidget> Hud;
TWeakObjectPtr<UUserWidget> OldHud;
int32 Phase = 0;
double Started = 0;

bool Finish(bool bSuccess, const FString& Message)
{
    UE_LOG(LogTemp, Display, TEXT("UNIFIEDSCRIPT_ASYNC_UI_VERIFY %s %s"),
        bSuccess ? TEXT("PASS") : TEXT("FAIL"), *Message);
    FPlatformMisc::RequestExitWithStatus(false, bSuccess ? 0 : 1, TEXT("UnifiedScriptAsyncUiVerify"));
    return false;
}

bool Tick(float)
{
    if (FPlatformTime::Seconds() - Started > 60)
        return Finish(false, FString::Printf(TEXT("Timed out at phase %d; state=%s"), Phase,
            Actor.IsValid() ? *Actor->GetUnifiedScriptComponent()->ManagedState : TEXT("no sample Actor")));
    if (Phase == 0)
    {
        if (!GEngine) return true;
        for (const FWorldContext& Context : GEngine->GetWorldContexts())
        {
            UWorld* World = Context.World();
            if (!World || World->WorldType != EWorldType::Game || !World->HasBegunPlay()) continue;
            for (TActorIterator<AUnifiedScriptActor> It(World); It; ++It)
                if (It->GetClass()->GetPathName() == TEXT("/Game/UnifiedScriptDemo/BP_AsyncUiDemo.BP_AsyncUiDemo_C")) Actor = *It;
        }
        if (!Actor.IsValid()) return true;
        Phase = 1;
    }
    if (!Actor.IsValid()) return Finish(false, TEXT("Sample Actor disappeared."));
    UWorld* World = Actor->GetWorld();
    auto* Component = Actor->GetUnifiedScriptComponent();
    auto* Host = World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    const FString State = Component->ManagedState;
    if (State.StartsWith(TEXT("UI:Error:"))) return Finish(false, State);
    if (Phase == 1 || Phase == 5)
    {
        if (State != TEXT("UI:Ready")) return true;
        for (TObjectIterator<UUserWidget> It; It; ++It)
            if (It->GetWorld() == World && It->IsInViewport() && It->GetWidgetFromName(TEXT("SpawnButton"))) Hud = *It;
        if (!Hud.IsValid() || Host->GetEventSubscriptionCount() != 2)
            return Finish(false, TEXT("Visible HUD and two subscriptions required."));
        auto* Button = Cast<UButton>(Hud->GetWidgetFromName(TEXT("SpawnButton")));
        auto* Cancel = Cast<UButton>(Hud->GetWidgetFromName(TEXT("CancelButton")));
        if (!Button || !Cancel) return Finish(false, TEXT("Sample controls have incorrect types."));
        if (Phase == 5)
        {
            if (OldHud.IsValid() && OldHud->IsInViewport()) return Finish(false, TEXT("Reload retained the old viewport view."));
            Phase = 6;
            Button->OnClicked.Broadcast();
            return true;
        }
        if (!World->GetFirstPlayerController() || !World->GetFirstPlayerController()->bShowMouseCursor)
            return Finish(false, TEXT("Demo local player/input policy is missing."));
        Button->OnClicked.Broadcast();
        Button->OnClicked.Broadcast();
        if (Host->GetPendingAsyncRequestCount() + Host->GetQueuedAsyncCompletionCount() != 1)
            return Finish(false, TEXT("Duplicate click started more than one load."));
        Cancel->OnClicked.Broadcast();
        Phase = 2;
        return true;
    }
    if (Phase == 2)
    {
        if (Host->GetQueuedAsyncCompletionCount() != 0) return true;
        if (State != TEXT("UI:Canceled") || Host->GetPendingAsyncRequestCount() != 0)
            return Finish(false, TEXT("Cancellation did not remain terminal after native completion."));
        CastChecked<UButton>(Hud->GetWidgetFromName(TEXT("SpawnButton")))->OnClicked.Broadcast();
        Phase = 3;
    }
    else if (Phase == 3 || Phase == 6)
    {
        if (State != TEXT("UI:Spawned:1")) return true;
        int32 Previews = 0;
        for (TActorIterator<AStaticMeshActor> It(World); It; ++It)
        {
            if (!It->GetStaticMeshComponent()->GetStaticMesh()) return Finish(false, TEXT("Spawned Actor has no loaded mesh."));
            ++Previews;
        }
        if (Previews != 1) return Finish(false, TEXT("Expected exactly one owned preview."));
        auto* Status = Cast<UTextBlock>(Hud->GetWidgetFromName(TEXT("StatusText")));
        if (!Status || !Status->GetText().ToString().Contains(TEXT("1")))
            return Finish(false, TEXT("HUD did not reflect managed success."));
        if (Phase == 3)
        {
            OldHud = Hud;
            Hud.Reset();
            if (!Component->ReloadScript()) return Finish(false, TEXT("Reload of the deployed compiled chunk failed."));
            Phase = 5;
        }
        else
        {
            Actor->Destroy();
            if (Host->GetEventSubscriptionCount() != 0 || Host->GetPendingAsyncRequestCount() != 0 ||
                Host->GetOwnedAsyncResourceCount() != 0 || (Hud.IsValid() && Hud->IsInViewport()))
                return Finish(false, TEXT("Actor deletion leaked UI, subscriptions or resources."));
            for (TActorIterator<AStaticMeshActor> It(World); It; ++It)
                return Finish(false, TEXT("Actor deletion retained the preview."));
            return Finish(true, TEXT("Saved scene, viewport, cancel, async mesh spawn, chunk reload and teardown."));
        }
    }
    return true;
}
}

void Start()
{
    if (!FParse::Param(FCommandLine::Get(), TEXT("UnifiedScriptAsyncUiVerify"))) return;
    Started = FPlatformTime::Seconds();
    Phase = 0;
    Handle = FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateStatic(&Tick));
}

void Stop()
{
    if (Handle.IsValid()) FTSTicker::GetCoreTicker().RemoveTicker(Handle);
    Handle.Reset();
    Actor.Reset();
    Hud.Reset();
    OldHud.Reset();
}
}
