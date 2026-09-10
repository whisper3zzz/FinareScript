#if WITH_DEV_AUTOMATION_TESTS

#include "FinareWorldSubsystem.h"

#include "EnhancedInputSubsystems.h"
#include "EnhancedPlayerInput.h"
#include "Engine/Engine.h"
#include "Engine/GameInstance.h"
#include "Engine/GameViewportClient.h"
#include "Engine/LocalPlayer.h"
#include "Engine/World.h"
#include "InputMappingContext.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"
#include "ReplaySubsystem.h"
#include "GameFramework/PlayerController.h"
#include "FinareCoreClrHost.h"
#include "FinareInteropFixture.h"
#include "FinareInvocationBridge.h"
#include "FinareObjectRegistry.h"
#include "WorldPartition/WorldPartitionSubsystem.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareSubsystemManagedTest,
    "Finare.Unreal.Subsystems.ManagedRoundTrip",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareSubsystemManagedTest::RunTest(const FString& Parameters)
{
    const int32 InitialWorldCount = FFinareCoreClrHost::Get().GetManagedWorldCount();
    UGameInstance* GameInstance = NewObject<UGameInstance>(GEngine);
    TestNotNull(TEXT("Subsystem test GameInstance exists"), GameInstance);
    if (GameInstance == nullptr)
    {
        return false;
    }
    GameInstance->AddToRoot();
    GameInstance->InitializeStandalone();
    UWorld* World = GameInstance->GetWorld();
    TestNotNull(TEXT("Subsystem test World exists"), World);
    if (World == nullptr)
    {
        GameInstance->Shutdown();
        GameInstance->RemoveFromRoot();
        return false;
    }
    World->AddToRoot();
    auto CleanupFixture = [&]()
    {
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        GameInstance->Shutdown();
        GEngine->DestroyWorldContext(World);
        GameInstance->RemoveFromRoot();
    };

    // Standalone commandlet worlds do not have a viewport. Supplying one keeps
    // CreateLocalPlayer on its normal non-dedicated path while leaving the
    // project input configuration untouched.
    UGameViewportClient* GameViewport = NewObject<UGameViewportClient>(GEngine);
    TestNotNull(TEXT("Subsystem test GameViewport exists"), GameViewport);
    if (GameViewport == nullptr)
    {
        CleanupFixture();
        return false;
    }
    GameViewport->Init(*GameInstance->GetWorldContext(), GameInstance, false);
    GameInstance->GetWorldContext()->GameViewport = GameViewport;

    FString PlayerError;
    ULocalPlayer* LocalPlayer = GameInstance->CreateLocalPlayer(0, PlayerError, false);
    TestNotNull(*FString::Printf(TEXT("Create LocalPlayer: %s"), *PlayerError), LocalPlayer);
    if (LocalPlayer == nullptr)
    {
        CleanupFixture();
        return false;
    }
    APlayerController* PlayerController = World->SpawnActor<APlayerController>();
    if (PlayerController != nullptr)
    {
        // Keep this fixture independent of DefaultInput.ini: install the concrete
        // Enhanced Input implementation before the controller initializes its input.
        PlayerController->PlayerInput = NewObject<UEnhancedPlayerInput>(PlayerController);
        PlayerController->SetPlayer(LocalPlayer);
        LocalPlayer->ReceivedPlayerController(PlayerController);
    }
    TestNotNull(TEXT("LocalPlayer has a PlayerController"), PlayerController);
    TestTrue(TEXT("PlayerController belongs to this World"),
        PlayerController != nullptr && PlayerController->GetWorld() == World);
    TestNotNull(TEXT("PlayerController owns EnhancedPlayerInput"),
        PlayerController != nullptr ? Cast<UEnhancedPlayerInput>(PlayerController->PlayerInput) : nullptr);
    if (PlayerController == nullptr)
    {
        CleanupFixture();
        return false;
    }

    World->InitializeActorsForPlay(FURL());
    TestTrue(TEXT("Subsystem test World initializes actors"), World->AreActorsInitialized());

    UEnhancedInputLocalPlayerSubsystem* InputSubsystem =
        LocalPlayer->GetSubsystem<UEnhancedInputLocalPlayerSubsystem>();
    TestNotNull(TEXT("LocalPlayer owns Enhanced Input subsystem"), InputSubsystem);
    UInputMappingContext* MappingContext = NewObject<UInputMappingContext>(World);
    UFinareInteropFixture* Fixture = NewObject<UFinareInteropFixture>(World);
    TestNotNull(TEXT("Subsystem mapping context exists"), MappingContext);
    TestNotNull(TEXT("Subsystem interop fixture exists"), Fixture);
    if (InputSubsystem == nullptr || MappingContext == nullptr || Fixture == nullptr)
    {
        CleanupFixture();
        return false;
    }
    Fixture->ObjectValue = MappingContext;

    // Exercise all selected SubsystemBlueprintLibrary entry points directly
    // before the managed sample, including class validation and registry scope.
    FFinareObjectRegistry AccessRegistry(World);
    FFinareInvocationBridge AccessBridge(AccessRegistry);
    FFinareObjectHandleResult AccessHandleResult;
    const FFinareObjectHandle AccessFixtureHandle =
        AccessRegistry.RegisterObject(Fixture, AccessHandleResult);
    const FFinareObjectHandle AccessPlayerHandle =
        AccessRegistry.RegisterObject(PlayerController, AccessHandleResult);
    const FFinareObjectHandle WorldPartitionClassHandle =
        AccessRegistry.RegisterObject(UWorldPartitionSubsystem::StaticClass(), AccessHandleResult);
    const FFinareObjectHandle ReplayClassHandle =
        AccessRegistry.RegisterObject(UReplaySubsystem::StaticClass(), AccessHandleResult);
    const FFinareObjectHandle InputClassHandle =
        AccessRegistry.RegisterObject(UEnhancedInputLocalPlayerSubsystem::StaticClass(), AccessHandleResult);
    const FFinareObjectHandle WrongClassHandle =
        AccessRegistry.RegisterObject(UObject::StaticClass(), AccessHandleResult);
    constexpr uint64 GetWorldSubsystemId = FinareMemberId(
        "/Script/Engine.SubsystemBlueprintLibrary:GetWorldSubsystem");
    constexpr uint64 GetGameInstanceSubsystemId = FinareMemberId(
        "/Script/Engine.SubsystemBlueprintLibrary:GetGameInstanceSubsystem");
    constexpr uint64 GetLocalPlayerSubsystemId = FinareMemberId(
        "/Script/Engine.SubsystemBlueprintLibrary:GetLocalPlayerSubsystem");
    constexpr uint64 GetLocalPlayerFromControllerId = FinareMemberId(
        "/Script/Engine.SubsystemBlueprintLibrary:GetLocalPlayerSubSystemFromPlayerController");

    auto ClassCell = [](FFinareObjectHandle Handle)
    {
        return FFinareValue::FromObjectHandle(Handle, true);
    };
    auto ObjectCell = [](FFinareObjectHandle Handle)
    {
        return FFinareValue::FromObjectHandle(Handle);
    };
    auto ResolveReturnedObject = [&](uint64 MemberId,
                                     FFinareObjectHandle ContextHandle,
                                     FFinareObjectHandle ClassHandle,
                                     const TCHAR* Label,
                                     UObject* Expected) -> bool
    {
        FFinareValue ReturnValue;
        FFinareInvocationResult InvocationResult;
        const TArray<FFinareValue> Arguments = {
            ObjectCell(ContextHandle),
            ClassCell(ClassHandle),
        };
        if (!AccessBridge.InvokeById(
                AccessFixtureHandle,
                MemberId,
                Arguments,
                ReturnValue,
                InvocationResult))
        {
            TestTrue(*FString::Printf(TEXT("%s invocation succeeds: %s"), Label, *InvocationResult.Message), false);
            return false;
        }
        FFinareObjectHandle ReturnedHandle;
        if (!ReturnValue.TryGetObjectHandle(ReturnedHandle))
        {
            TestTrue(*FString::Printf(TEXT("%s returns an object handle"), Label), false);
            return false;
        }
        FFinareObjectHandleResult ResolveResult;
        UObject* ReturnedObject = AccessRegistry.ResolveObject(ReturnedHandle, ResolveResult);
        TestTrue(*FString::Printf(TEXT("%s returns the expected subsystem identity"), Label),
            ResolveResult.IsSuccess() && ReturnedObject == Expected);
        return ResolveResult.IsSuccess() && ReturnedObject == Expected;
    };
    ResolveReturnedObject(
        GetWorldSubsystemId,
        AccessFixtureHandle,
        WorldPartitionClassHandle,
        TEXT("GetWorldSubsystem"),
        World->GetSubsystem<UWorldPartitionSubsystem>());
    ResolveReturnedObject(
        GetGameInstanceSubsystemId,
        AccessFixtureHandle,
        ReplayClassHandle,
        TEXT("GetGameInstanceSubsystem"),
        GameInstance->GetSubsystem<UReplaySubsystem>());
    ResolveReturnedObject(
        GetLocalPlayerSubsystemId,
        AccessPlayerHandle,
        InputClassHandle,
        TEXT("GetLocalPlayerSubsystem"),
        InputSubsystem);
    ResolveReturnedObject(
        GetLocalPlayerFromControllerId,
        AccessPlayerHandle,
        InputClassHandle,
        TEXT("GetLocalPlayerSubSystemFromPlayerController"),
        InputSubsystem);

    {
        FFinareValue ReturnValue;
        FFinareInvocationResult InvocationResult;
        const TArray<FFinareValue> Arguments = {
            ObjectCell(AccessFixtureHandle),
            ClassCell(WrongClassHandle),
        };
        TestFalse(
            TEXT("Subsystem getter rejects an incompatible class before native lookup"),
            AccessBridge.InvokeById(
                AccessFixtureHandle,
                GetWorldSubsystemId,
                Arguments,
                ReturnValue,
                InvocationResult));
    }
    {
        UWorld* ForeignWorld = UWorld::CreateWorld(EWorldType::Game, false);
        TestNotNull(TEXT("Foreign subsystem test World exists"), ForeignWorld);
        if (ForeignWorld != nullptr)
        {
            ForeignWorld->AddToRoot();
            FFinareObjectRegistry ForeignRegistry(ForeignWorld);
            const FFinareObjectHandle ForeignContextHandle =
                ForeignRegistry.RegisterObject(ForeignWorld, AccessHandleResult);
            FFinareValue ReturnValue;
            FFinareInvocationResult InvocationResult;
            const TArray<FFinareValue> Arguments = {
                ObjectCell(ForeignContextHandle),
                ClassCell(WorldPartitionClassHandle),
            };
            TestFalse(
                TEXT("Subsystem getter rejects a foreign World handle"),
                AccessBridge.InvokeById(
                    AccessFixtureHandle,
                    GetWorldSubsystemId,
                    Arguments,
                    ReturnValue,
                    InvocationResult));
            ForeignWorld->RemoveFromRoot();
            ForeignWorld->DestroyWorld(false);
        }
    }

    UFinareWorldSubsystem* Host = World->GetSubsystem<UFinareWorldSubsystem>();
    const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
    TestNotNull(TEXT("Subsystem test host exists"), Host);
    TestTrue(TEXT("Finare plugin is installed"), Plugin.IsValid());
    if (Host == nullptr || !Plugin.IsValid())
    {
        CleanupFixture();
        return false;
    }

    FFinareObjectHandleResult HandleResult;
    const FFinareObjectHandle FixtureHandle = Host->RegisterObject(Fixture, HandleResult);
    TestTrue(TEXT("Subsystem fixture handle is registered"), HandleResult.IsSuccess());
    const FFinareObjectHandle PlayerHandle = Host->RegisterObject(PlayerController, HandleResult);
    TestTrue(TEXT("PlayerController handle is registered"), HandleResult.IsSuccess());
    const FString Assembly = FPaths::Combine(
        Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo/Finare.Unreal.Demo.dll"));
    const FString Type = TEXT("Finare.Unreal.Demo.SubsystemGameplayDemoScript");
    FString Error;
    bool bStarted = false;
    if (!FixtureHandle.IsNull() && !PlayerHandle.IsNull())
    {
        bStarted = Host->CreateManagedScript(8901, FixtureHandle, PlayerHandle, Assembly, Type, Error);
    }
    TestTrue(*FString::Printf(TEXT("Managed subsystem sample starts: %s"), *Error), bStarted);
    if (bStarted)
    {
        int32 Priority = INDEX_NONE;
        TestTrue(TEXT("Managed startup registers mapping context"),
            InputSubsystem->HasMappingContext(MappingContext, Priority));
        TestEqual(TEXT("Managed startup preserves mapping priority"), Priority, 37);
        TestEqual(TEXT("Managed startup observes mapping priority"), Fixture->Int32Value, 37);

        Fixture->Int32Value = -8901;
        int32 Generation = 0;
        const bool bReloaded = Host->ReloadManagedScript(8901, Assembly, Type, Generation, Error);
        TestTrue(*FString::Printf(TEXT("Managed subsystem sample reloads: %s"), *Error), bReloaded);
        TestTrue(TEXT("Managed subsystem reload advances generation"), Generation > 0);
        Priority = INDEX_NONE;
        TestTrue(TEXT("Reload retains mapping context"),
            InputSubsystem->HasMappingContext(MappingContext, Priority));
        TestEqual(TEXT("Reload retains mapping priority"), Priority, 37);
        TestEqual(TEXT("Reload reruns generated subsystem calls"), Fixture->Int32Value, 37);

        TestTrue(TEXT("Managed subsystem sample stops"), Host->DestroyManagedScript(8901, Error));
        Priority = INDEX_NONE;
        TestFalse(TEXT("Managed OnStop removes mapping context"),
            InputSubsystem->HasMappingContext(MappingContext, Priority));

        // Leave one fresh managed script alive through World teardown so the
        // World cleanup path is exercised with an outstanding mapping.
        Error.Reset();
        const bool bSecondStarted = Host->CreateManagedScript(8902, FixtureHandle, PlayerHandle, Assembly, Type, Error);
        TestTrue(*FString::Printf(TEXT("Managed subsystem sample restarts: %s"), *Error), bSecondStarted);
        if (bSecondStarted)
        {
            Priority = INDEX_NONE;
            TestTrue(TEXT("Restarted managed sample keeps mapping live before World teardown"),
                InputSubsystem->HasMappingContext(MappingContext, Priority));
            TestEqual(TEXT("Restarted managed sample preserves mapping priority"), Priority, 37);
        }
    }

    // The live second script is intentionally not destroyed so World teardown
    // must retire it while the LocalPlayer and subsystem are still valid.
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    int32 PriorityAfterWorldCleanup = INDEX_NONE;
    TestFalse(
        TEXT("World cleanup removes the live managed mapping before GameInstance shutdown"),
        InputSubsystem->HasMappingContext(MappingContext, PriorityAfterWorldCleanup));
    GameInstance->Shutdown();
    GEngine->DestroyWorldContext(World);
    GameInstance->RemoveFromRoot();
    TestEqual(
        TEXT("Subsystem World cleanup releases the CoreCLR context"),
        FFinareCoreClrHost::Get().GetManagedWorldCount(),
        InitialWorldCount);
    return true;
}

#endif
