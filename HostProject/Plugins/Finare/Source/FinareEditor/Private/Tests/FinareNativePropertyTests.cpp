#if WITH_DEV_AUTOMATION_TESTS
#include "FinareAsset.h"
#include "FinareBlueprint.h"
#include "FinareBlueprintClassMaterializer.h"
#include "FinareComponent.h"
#include "FinareWorldSubsystem.h"
#include "Components/SceneComponent.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "GameFramework/WorldSettings.h"
#include "Interfaces/IPluginManager.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Misc/PackageName.h"
#include "Misc/ScopeExit.h"
#include "HAL/FileManager.h"
#include "UObject/UnrealType.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareNativePropertyTest,
    "Finare.Unreal.ObjectModel.LiveProperties",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativePropertyTest::RunTest(const FString& Parameters)
{
    // The managed class attribute requires a stable Game path. Isolate its
    // saved fixture so repeating this test does not create a partially loaded
    // package over an earlier run, and preserve any pre-existing file.
    const FString FixtureFilename = FPackageName::LongPackageNameToFilename(
        TEXT("/Game/FinareAutomation/BP_NativeProperties"), FPackageName::GetAssetPackageExtension());
    const bool bHadFixture = IFileManager::Get().FileExists(*FixtureFilename);
    TArray<uint8> PreviousFixture;
    if (bHadFixture && !TestTrue(TEXT("Reads existing property fixture"),
        FFileHelper::LoadFileToArray(PreviousFixture, *FixtureFilename))) return false;
    ON_SCOPE_EXIT
    {
        if (bHadFixture)
            TestTrue(TEXT("Restores existing property fixture"), FFileHelper::SaveArrayToFile(PreviousFixture, *FixtureFilename));
        else if (IFileManager::Get().FileExists(*FixtureFilename))
            TestTrue(TEXT("Removes generated property fixture"), IFileManager::Get().Delete(*FixtureFilename));
    };
    if (bHadFixture && !TestTrue(TEXT("Isolates the saved property fixture"),
        IFileManager::Get().Delete(*FixtureFilename))) return false;
    UPackage* Package = CreatePackage(TEXT("/Game/FinareAutomation/BP_NativeProperties"));
    UFinareBlueprint* Blueprint = Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
        USceneComponent::StaticClass(), Package, TEXT("BP_NativeProperties"), BPTYPE_Normal,
        UFinareBlueprint::StaticClass(), UFinareBlueprintGeneratedClass::StaticClass()));
    if (!TestNotNull(TEXT("Creates native property Script Class"), Blueprint)) return false;
    UFinareAsset* Asset = NewObject<UFinareAsset>(Package);
    FGuid::ParseExact(TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab"), EGuidFormats::Digits, Asset->ScriptClassId);
    Asset->ScriptClassKind = EFinareClassKind::ActorComponent;
    Asset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
    Asset->ScriptTypeName = TEXT("Finare.Unreal.Demo.NativePropertyDemoScript");
    Asset->ScriptAssemblyPath = FPaths::Combine(IPluginManager::Get().FindPlugin(TEXT("Finare"))->GetBaseDir(),
        TEXT("Binaries/Managed/Demo/Finare.Unreal.Demo.dll"));
    Asset->DeclarationAssetPath = TEXT("Tests/NativeProperties");
    Asset->DeclarationPath = FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("Automation/Finare"),
        FGuid::NewGuid().ToString(EGuidFormats::Digits) + TEXT(".json"));
    Blueprint->ScriptAsset = Asset;
    const FString Json = TEXT(R"JSON({"schemaVersion":"unifiedscript.script-asset/v0.3","assets":[{
      "id":"us_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab","qualifiedName":"Finare.Unreal.Demo.NativePropertyDemoScript","assetPath":"Tests/NativeProperties",
      "states":[
        {"id":"us_11111111111111111111111111111111","name":"Health","typeMetadataName":"System.Int32","typeKind":"integer",
         "defaultValue":100,"exposure":{"instanceEditable":true,"saveGame":false,"replicated":false}},
        {"id":"us_22222222222222222222222222222222","name":"Scores","typeMetadataName":"System.Collections.Generic.IReadOnlyList<System.Int32>","typeKind":"array",
         "unrealType":{"kind":"array","unrealTypePath":"","unrealNativeName":"TArray","fields":[],"typeArguments":[{"typeMetadataName":"System.Int32"}]},
         "defaultValue":[],"exposure":{"instanceEditable":true,"saveGame":false,"replicated":false}}
      ],"functions":[],"lifecycleHooks":[],
      "classBinding":{"kind":"ActorComponent","baseClassPath":"/Script/Engine.SceneComponent",
        "generatedClassPath":"/Game/FinareAutomation/BP_NativeProperties.BP_NativeProperties_C"}
    }]})JSON");
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(Asset->DeclarationPath), true);
    ON_SCOPE_EXIT { IFileManager::Get().Delete(*Asset->DeclarationPath); };
    if (!TestTrue(TEXT("Writes native property declaration"), FFileHelper::SaveStringToFile(Json, *Asset->DeclarationPath))) return false;
    FString Error;
    FFinareBlueprintClassSyncResult Sync;
    const bool bSynced = FFinareBlueprintClassMaterializer::SynchronizeJson(*Asset, *Blueprint, Json, false, Sync, Error);
    if (!TestTrue(*FString::Printf(TEXT("Materializes live properties: %s"), *Error), bSynced)) return false;
    FIntProperty* Health = FindFProperty<FIntProperty>(Blueprint->GeneratedClass, TEXT("Health"));
    FArrayProperty* Scores = FindFProperty<FArrayProperty>(Blueprint->GeneratedClass, TEXT("Scores"));
    if (!TestNotNull(TEXT("Health property"), Health) || !TestNotNull(TEXT("Scores property"), Scores)) return false;
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
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
    USceneComponent* Receiver = NewObject<USceneComponent>(Owner, Blueprint->GeneratedClass);
    Owner->AddInstanceComponent(Receiver);
    Receiver->RegisterComponentWithWorld(World);
    TestEqual(TEXT("C# initializer becomes native class default"), Health->GetPropertyValue_InContainer(Receiver), 100);
    Health->SetPropertyValue_InContainer(Receiver, 200);
    World->GetWorldSettings()->NotifyBeginPlay();
    TestEqual(TEXT("C# reads native value at BeginPlay"), Health->GetPropertyValue_InContainer(Receiver), 201);
    FScriptArrayHelper InitialScores(Scores, Scores->ContainerPtrToValuePtr<void>(Receiver));
    TestEqual(TEXT("C# setter replaces native array"), InitialScores.Num(), 2);
    Health->SetPropertyValue_InContainer(Receiver, 500);
    World->Tick(LEVELTICK_All, 0.016f);
    TestEqual(TEXT("C# observes subsequent native edits"), Health->GetPropertyValue_InContainer(Receiver), 501);
    FScriptArrayHelper UpdatedScores(Scores, Scores->ContainerPtrToValuePtr<void>(Receiver));
    TestTrue(TEXT("Snapshot mutation is detached; setter publishes replacement"), UpdatedScores.Num() == 2 &&
        CastFieldChecked<FIntProperty>(Scores->Inner)->GetPropertyValue(UpdatedScores.GetRawPtr(0)) == 3);
    UFinareWorldSubsystem* Host = World->GetSubsystem<UFinareWorldSubsystem>();
    UFinareComponent* Execution = Host->ResolveManagedReceiver(Receiver, nullptr, false, Error);
    if (!TestNotNull(TEXT("Live property generation remains active"), Execution)) return false;
    if (!TestTrue(TEXT("Reloads native-backed property generation"), Execution->ReloadScript())) return false;
    TestEqual(TEXT("Reload preserves native state then invokes BeginPlay"), Health->GetPropertyValue_InContainer(Receiver), 502);
    Owner->Destroy();
    TestEqual(TEXT("Destroy releases property script"), Host->GetManagedScriptCount(), 0);
    return !HasAnyErrors();
}
#endif
