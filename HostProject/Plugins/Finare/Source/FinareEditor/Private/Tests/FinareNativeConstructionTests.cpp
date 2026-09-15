#if WITH_DEV_AUTOMATION_TESTS
#include "FinareActor.h"
#include "FinareAsset.h"
#include "FinareBlueprint.h"
#include "FinareBlueprintClassMaterializer.h"
#include "FinareComponent.h"
#include "FinareCoreClrHost.h"
#include "FinareWorldSubsystem.h"
#include "Camera/CameraComponent.h"
#include "Components/CapsuleComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "GameFramework/Character.h"
#include "GameFramework/WorldSettings.h"
#include "HAL/FileManager.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "PackageTools.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UObject/SavePackage.h"
#include "UObject/UnrealType.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareNativeConstructionTest,
    "Finare.Unreal.Construction.CharacterDefaults",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativeConstructionTest::RunTest(const FString& Parameters)
{
    const FString Root = FPaths::ConvertRelativePathToFull(FPaths::ProjectSavedDir() / TEXT("Finare/NativeConstruction"));
    const FString DeclarationPath = Root / TEXT("NativeConstruction.asset.json");
    const FString ChunkPath = Root / TEXT("NativeConstruction.uschunk");
    FString Declaration;
    if (!TestTrue(TEXT("Prepare emitted Character fixture"), FPaths::FileExists(ChunkPath) &&
        FFileHelper::LoadFileToString(Declaration, *DeclarationPath))) return false;
    TSharedPtr<FJsonObject> Document;
    if (!TestTrue(TEXT("Read class declarations"), FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(Declaration), Document))) return false;
    const auto Json = Document->GetArrayField(TEXT("assets"))[0]->AsObject();
    const FString ClassPath = Json->GetObjectField(TEXT("classBinding"))->GetStringField(TEXT("generatedClassPath"));
    const FString PackageName = FPackageName::ObjectPathToPackageName(ClassPath);
    const FString Filename = FPackageName::LongPackageNameToFilename(PackageName, FPackageName::GetAssetPackageExtension());
    const bool bExisted = IFileManager::Get().FileExists(*Filename);
    TArray<uint8> Previous;
    if (bExisted && !TestTrue(TEXT("Read pre-existing fixture"), FFileHelper::LoadFileToArray(Previous, *Filename))) return false;
    ON_SCOPE_EXIT
    {
        if (UPackage* Loaded = FindPackage(nullptr, *PackageName)) ResetLoaders(Loaded);
        if (bExisted) TestTrue(TEXT("Restore fixture"), FFileHelper::SaveArrayToFile(Previous, *Filename));
        else if (IFileManager::Get().FileExists(*Filename)) TestTrue(TEXT("Remove fixture"), IFileManager::Get().Delete(*Filename));
    };
    if (bExisted) IFileManager::Get().Delete(*Filename);
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(Filename), true);
    UPackage* Package = CreatePackage(*PackageName);
    UFinareBlueprint* Blueprint = Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
        AFinareActor::StaticClass(), Package, FName(*FPackageName::GetShortName(PackageName)), BPTYPE_Normal,
        UFinareBlueprint::StaticClass(), UFinareBlueprintGeneratedClass::StaticClass()));
    if (!TestNotNull(TEXT("Create Script Class"), Blueprint)) return false;
    auto* Asset = NewObject<UFinareAsset>(Package, NAME_None, RF_Public | RF_Standalone | RF_Transactional);
    FGuid::ParseExact(Json->GetStringField(TEXT("id")).Mid(3), EGuidFormats::Digits, Asset->ScriptClassId);
    Asset->ScriptClassKind = EFinareClassKind::Actor;
    Asset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
    Asset->ScriptTypeName = Json->GetStringField(TEXT("qualifiedName"));
    Asset->DeclarationAssetPath = Json->GetStringField(TEXT("assetPath"));
    Asset->DeclarationPath = DeclarationPath;
    Asset->CompiledChunkPath = ChunkPath;
    Blueprint->ScriptAsset = Asset;
    CastChecked<AFinareActor>(Blueprint->GeneratedClass->GetDefaultObject())->ScriptComponent->ScriptAsset = Asset;
    FString Error;
    FFinareBlueprintClassSyncResult Sync;
    if (!FFinareBlueprintClassMaterializer::SynchronizeJson(*Asset, *Blueprint, Declaration, false, Sync, Error))
    { AddError(Error); return false; }
    auto* Class = CastChecked<UFinareBlueprintGeneratedClass>(Blueprint->GeneratedClass);
    TestTrue(TEXT("Compiled hook is retained"), Class->bHasManagedConstruction);
    if (!TestTrue(*Class->ManagedConstructionError, Class->ManagedConstructionError.IsEmpty())) return false;
    auto Check = [&](ACharacter* Character, bool bCDO) -> UCameraComponent*
    {
        auto* Camera = Cast<UCameraComponent>(Character->GetDefaultSubobjectByName(TEXT("PlayerCamera")));
        auto* Mesh = Cast<UStaticMeshComponent>(Character->GetDefaultSubobjectByName(TEXT("WeaponMesh")));
        if (!TestNotNull(TEXT("C# created native default camera"), Camera) || !TestNotNull(TEXT("C# created native default mesh"), Mesh)) return nullptr;
        TestTrue(TEXT("RF_DefaultSubObject"), Camera->HasAnyFlags(RF_DefaultSubObject) && Mesh->HasAnyFlags(RF_DefaultSubObject));
        TestEqual(TEXT("Camera attaches to native Character capsule"), Camera->GetAttachParent(), static_cast<USceneComponent*>(Character->GetCapsuleComponent()));
        TestEqual(TEXT("Mesh attaches to C# camera"), Mesh->GetAttachParent(), static_cast<USceneComponent*>(Camera));
        auto* Reference = FindFProperty<FObjectPropertyBase>(Character->GetClass(), TEXT("Camera"));
        if (TestNotNull(TEXT("Reflected camera reference"), Reference))
            TestEqual(*FString::Printf(TEXT("Reference on %s resolves to its camera (actual %s)"), *Character->GetPathName(),
                *GetPathNameSafe(Reference->GetObjectPropertyValue_InContainer(Character))), Reference->GetObjectPropertyValue_InContainer(Character), static_cast<UObject*>(Camera));
        auto* Value = FindFProperty<FIntProperty>(Character->GetClass(), TEXT("ConstructionValue"));
        if (TestNotNull(TEXT("Reflected constructor scalar"), Value)) TestEqual(TEXT("Constructor scalar survives defaults merge"), Value->GetPropertyValue_InContainer(Character), 37);
        TestEqual(TEXT("No gameplay from CDO construction"), Character->Tags.Contains(TEXT("ConstructedBeginPlay")), !bCDO);
        return Camera;
    };
    auto* CDO = CastChecked<ACharacter>(Class->GetDefaultObject());
    UCameraComponent* Camera = Check(CDO, true);
    if (!Camera) return false;
    TestEqual(TEXT("C# constructor default"), Camera->FieldOfView, 93.0f);
    // This is an editor default override; native test never creates components.
    Camera->FieldOfView = 105.0f;
    FSavePackageArgs Save;
    Save.TopLevelFlags = RF_Public | RF_Standalone;
    Save.SaveFlags = SAVE_NoError;
    if (!TestTrue(TEXT("Save class and component defaults"), UPackage::SavePackage(Package, Blueprint, *Filename, Save))) return false;
    Blueprint = nullptr; Asset = nullptr; Class = nullptr; CDO = nullptr; Camera = nullptr;
    TArray<UPackage*> Packages{Package};
    if (!TestTrue(TEXT("Unload saved class"), UPackageTools::UnloadPackages(Packages))) return false;
    Packages.Reset(); Package = nullptr;
    Class = LoadObject<UFinareBlueprintGeneratedClass>(nullptr, *ClassPath);
    if (!TestNotNull(TEXT("Reopen saved class"), Class)) return false;
    CDO = CastChecked<ACharacter>(Class->GetDefaultObject());
    Camera = Check(CDO, true);
    if (!Camera) return false;
    TestEqual(TEXT("Saved FOV override survives reopening"), Camera->FieldOfView, 105.0f);

    const int32 InitialWorlds = FFinareCoreClrHost::Get().GetManagedWorldCount();
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    World->AddToRoot(); GEngine->CreateNewWorldContext(EWorldType::Game).SetCurrentWorld(World);
    ON_SCOPE_EXIT
    {
        if (World->HasBegunPlay()) World->EndPlay(EEndPlayReason::Quit);
        GEngine->DestroyWorldContext(World); World->RemoveFromRoot(); World->DestroyWorld(false);
        TestEqual(TEXT("World teardown"), FFinareCoreClrHost::Get().GetManagedWorldCount(), InitialWorlds);
    };
    World->InitializeActorsForPlay(FURL()); World->GetWorldSettings()->NotifyBeginPlay();
    ACharacter* First = World->SpawnActor<ACharacter>(Class);
    ACharacter* Second = World->SpawnActor<ACharacter>(Class);
    if (!TestNotNull(TEXT("Spawn first Character"), First) || !TestNotNull(TEXT("Spawn second Character"), Second)) return false;
    auto* FirstCamera = Check(First, false); auto* SecondCamera = Check(Second, false);
    if (!FirstCamera || !SecondCamera) return false;
    TestTrue(TEXT("Independent component instances"), FirstCamera != SecondCamera && FirstCamera != Camera);
    TestEqual(TEXT("Instance uses edited default"), FirstCamera->FieldOfView, 105.0f);
    TestTrue(TEXT("UE registers default components"), FirstCamera->IsRegistered() && SecondCamera->IsRegistered());
    auto* Host = World->GetSubsystem<UFinareWorldSubsystem>();
    auto* FirstHost = Host->ResolveManagedReceiver(First, nullptr, false, Error);
    if (!TestNotNull(TEXT("Character has ordinary managed behavior"), FirstHost)) return false;
    TestTrue(TEXT("Same-program gameplay reload"), FirstHost->ReloadScript());
    TestEqual(TEXT("Reload does not reconstruct components"), First->GetDefaultSubobjectByName(TEXT("PlayerCamera")), static_cast<UObject*>(FirstCamera));
    const FString Hash = Class->ManagedConstructionProgramHash;
    Class->ManagedConstructionProgramHash = TEXT("different-program");
    TestFalse(TEXT("Changed constructor program cannot reload gameplay in place"), Class->ValidateManagedConstruction(ChunkPath, Error));
    auto* ReopenedBlueprint = CastChecked<UFinareBlueprint>(Class->ClassGeneratedBy);
    FFinareBlueprintClassSyncResult RejectedSync;
    TestFalse(TEXT("Active publication rejects constructor version mismatch before mutation"),
        FFinareBlueprintClassMaterializer::SynchronizeJson(*ReopenedBlueprint->ScriptAsset, *ReopenedBlueprint,
            Declaration, false, RejectedSync, Error));
    TestEqual(TEXT("Rejected publication retains native class"), ReopenedBlueprint->GeneratedClass.Get(), static_cast<UClass*>(Class));
    TestTrue(TEXT("Rejected publication retains gameplay"), FirstHost->bScriptRunning);
    Class->ManagedConstructionProgramHash = Hash;
    First->Destroy(); Second->Destroy();
    TestEqual(TEXT("Receivers stop cleanly"), Host->GetManagedScriptCount(), 0);
    return !HasAnyErrors();
}
#endif
