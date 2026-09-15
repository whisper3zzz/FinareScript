#if WITH_DEV_AUTOMATION_TESTS
#include "FinareAsset.h"
#include "FinareBlueprint.h"
#include "FinareBlueprintClassMaterializer.h"
#include "FinareComponent.h"
#include "FinareWorldSubsystem.h"
#include "Components/SceneComponent.h"
#include "Engine/World.h"
#include "Engine/Engine.h"
#include "GameFramework/Actor.h"
#include "GameFramework/WorldSettings.h"
#include "Interfaces/IPluginManager.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "HAL/FileManager.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareNativeReceiverLifecycleTest,
    "Finare.Unreal.ScriptClass.NativeReceiverLifecycle",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativeReceiverLifecycleTest::RunTest(const FString& Parameters)
{
    const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
    const FString Name = TEXT("BP_NativeReceiver_") + Suffix;
    UPackage* Package = CreatePackage(*(TEXT("/Engine/Transient/") + Name));
    UFinareBlueprint* Blueprint = Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
        USceneComponent::StaticClass(), Package, FName(*Name), BPTYPE_Normal,
        UFinareBlueprint::StaticClass(), UFinareBlueprintGeneratedClass::StaticClass()));
    if (!TestNotNull(TEXT("Creates a SceneComponent Script Class"), Blueprint)) return false;
    const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
    if (!Plugin.IsValid()) return false;
    UFinareAsset* Asset = NewObject<UFinareAsset>(Package);
    Asset->ScriptClassId = FGuid::NewGuid();
    Asset->ScriptClassKind = EFinareClassKind::ActorComponent;
    Asset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
    Asset->ScriptTypeName = TEXT("Finare.Unreal.Demo.NativeReceiverDemoScript");
    Asset->ScriptAssemblyPath = FPaths::Combine(Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo/Finare.Unreal.Demo.dll"));
    Asset->DeclarationAssetPath = TEXT("Tests/NativeReceiver");
    Asset->DeclarationPath = FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("Automation/Finare"), Suffix + TEXT(".json"));
    Blueprint->ScriptAsset = Asset;
    // No declared lifecycle hook is necessary to own the receiver's lifetime.
    const FString Json = FString::Printf(TEXT(R"JSON({"schemaVersion":"unifiedscript.script-asset/v0.3","assets":[{
        "id":"us_%s","qualifiedName":"Finare.Unreal.Demo.NativeReceiverDemoScript","assetPath":"Tests/NativeReceiver",
        "states":[],"functions":[],"lifecycleHooks":[],
        "classBinding":{"kind":"ActorComponent","baseClassPath":"/Script/Engine.SceneComponent","generatedClassPath":"%s"}
    }]})JSON"), *Asset->ScriptClassId.ToString(EGuidFormats::Digits), *Asset->GeneratedClassPath.ToString());
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(Asset->DeclarationPath), true);
    ON_SCOPE_EXIT { IFileManager::Get().Delete(*Asset->DeclarationPath); };
    if (!TestTrue(TEXT("Writes runtime declaration"), FFileHelper::SaveStringToFile(Json, *Asset->DeclarationPath))) return false;
    FFinareBlueprintClassSyncResult Sync;
    FString Error;
    const bool bSynchronized = FFinareBlueprintClassMaterializer::SynchronizeJson(*Asset, *Blueprint, Json, false, Sync, Error);
    if (!TestTrue(*FString::Printf(TEXT("Materializes SceneComponent parent: %s"), *Error), bSynchronized)) return false;
    TestEqual(TEXT("Preserves native parent"), Blueprint->GeneratedClass->GetSuperClass(), USceneComponent::StaticClass());
    TestFalse(TEXT("Receiver has no FinareComponent base"), Blueprint->GeneratedClass->IsChildOf(UFinareComponent::StaticClass()));
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Creates a Game World"), World)) return false;
    World->AddToRoot();
    GEngine->CreateNewWorldContext(EWorldType::Game).SetCurrentWorld(World);
    ON_SCOPE_EXIT
    {
        if (World->HasBegunPlay()) World->EndPlay(EEndPlayReason::Quit);
        GEngine->DestroyWorldContext(World);
        World->RemoveFromRoot();
        World->DestroyWorld(false);
    };
    World->InitializeActorsForPlay(FURL());
    AActor* Owner = World->SpawnActor<AActor>();
    if (!TestNotNull(TEXT("Creates native owner"), Owner)) return false;
    UFinareWorldSubsystem* Host = World->GetSubsystem<UFinareWorldSubsystem>();
    if (!TestNotNull(TEXT("Creates World host"), Host) || !TestTrue(TEXT("Managed World ready"), Host->IsManagedWorldReady())) return false;
    auto MakeReceiver = [&](const TCHAR* ReceiverName)
    {
        USceneComponent* Receiver = NewObject<USceneComponent>(Owner, Blueprint->GeneratedClass, FName(ReceiverName));
        Owner->AddInstanceComponent(Receiver);
        Receiver->RegisterComponentWithWorld(World);
        return Receiver;
    };
    USceneComponent* A = MakeReceiver(TEXT("ReceiverA"));
    USceneComponent* B = MakeReceiver(TEXT("ReceiverB"));
    TestEqual(TEXT("Construction does not start managed code"), Host->GetManagedScriptCount(), 0);
    TestTrue(TEXT("Receiver supports native Tick scheduling"), A->PrimaryComponentTick.bCanEverTick);
    // GameState's engine BeginPlay route, without an unrelated game mode fixture.
    World->GetWorldSettings()->NotifyBeginPlay();
    TestEqual(TEXT("One generation per component"), Host->GetManagedScriptCount(), 2);
    TestTrue(TEXT("A observes one BeginPlay"), A->ComponentTags.Contains(TEXT("Begin=1")));
    TestTrue(TEXT("B observes one BeginPlay"), B->ComponentTags.Contains(TEXT("Begin=1")));
    UFinareComponent* HostA = Host->ResolveManagedReceiver(A, nullptr, false, Error);
    UFinareComponent* HostB = Host->ResolveManagedReceiver(B, nullptr, false, Error);
    if (!TestNotNull(TEXT("Resolves A"), HostA) || !TestNotNull(TEXT("Resolves B"), HostB)) return false;
    TestTrue(TEXT("Components have different hosts"), HostA != HostB);
    TestFalse(TEXT("Internal host does not schedule extra Tick"), HostA->IsComponentTickEnabled());
    World->Tick(LEVELTICK_All, 0.016f);
    TestTrue(TEXT("Exactly one A Tick"), A->ComponentTags.Contains(TEXT("Tick=1")));
    TestTrue(TEXT("Exactly one B Tick"), B->ComponentTags.Contains(TEXT("Tick=1")));
    A->UnregisterComponent();
    World->Tick(LEVELTICK_All, 0.016f);
    TestFalse(TEXT("Unregister retires A"), HostA->bScriptRunning);
    TestTrue(TEXT("Unregister tears down A once"), A->ComponentTags.Contains(TEXT("End=1")));
    TestTrue(TEXT("B remains active"), HostB->bScriptRunning);
    TestEqual(TEXT("Only B remains running"), Host->GetManagedScriptCount(), 1);
    A->RegisterComponentWithWorld(World);
    World->Tick(LEVELTICK_All, 0.016f);
    TestTrue(TEXT("Re-register restores A"), HostA->bScriptRunning);
    TestEqual(TEXT("Two generations restored"), Host->GetManagedScriptCount(), 2);
    B->DestroyComponent();
    TestFalse(TEXT("Destruction synchronously stops B"), HostB->bScriptRunning);
    TestTrue(TEXT("Destruction tears down B once"), B->ComponentTags.Contains(TEXT("End=1")));
    Owner->Destroy();
    World->Tick(LEVELTICK_All, 0.016f);
    TestEqual(TEXT("Actor destruction releases all generations"), Host->GetManagedScriptCount(), 0);
    return !HasAnyErrors();
}
#endif
