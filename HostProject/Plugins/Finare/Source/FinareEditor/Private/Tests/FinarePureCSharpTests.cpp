#if WITH_DEV_AUTOMATION_TESTS
#include "FinareAsset.h"
#include "FinareActor.h"
#include "FinareBlueprint.h"
#include "FinareBlueprintClassMaterializer.h"
#include "FinareComponent.h"
#include "FinareCoreClrHost.h"
#include "FinareWorldSubsystem.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "GameFramework/WorldSettings.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "HAL/FileManager.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UObject/UnrealType.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinarePureCSharpReloadTest,
    "Finare.Unreal.PureCSharp.SourceDrivenReload",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinarePureCSharpReloadTest::RunTest(const FString& Parameters)
{
    const FString Root = FPaths::ConvertRelativePathToFull(FPaths::Combine(
        FPaths::ProjectSavedDir(), TEXT("Finare/PureCSharp")));
    const FString FirstChunk = FPaths::Combine(Root, TEXT("v1/Finare.PureCSharp.uschunk"));
    const FString SecondChunk = FPaths::Combine(Root, TEXT("v2/Finare.PureCSharp.uschunk"));
    const FString DeclarationPath = FPaths::Combine(Root, TEXT("v1/Finare.PureCSharp.asset.json"));
    FString Declaration;
    if (!TestTrue(TEXT("Prepare both template revisions"), FPaths::FileExists(FirstChunk) &&
        FPaths::FileExists(SecondChunk) && FFileHelper::LoadFileToString(Declaration, *DeclarationPath))) return false;
    TSharedPtr<FJsonObject> Document;
    if (!TestTrue(TEXT("Read emitted template declaration"),
        FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(Declaration), Document) && Document.IsValid())) return false;
    const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
    if (!TestTrue(TEXT("Template emits exactly one asset"), Document->TryGetArrayField(TEXT("assets"), Assets) &&
        Assets->Num() == 1)) return false;
    FGuid ClassId;
    if (!TestTrue(TEXT("Template retains its declared identity"), FGuid::ParseExact(
        (*Assets)[0]->AsObject()->GetStringField(TEXT("id")).RightChop(3), EGuidFormats::Digits, ClassId))) return false;

    const FString PackageName = TEXT("/Game/UnifiedScript/FirstScript/BP_FirstScript");
    const FString Filename = FPackageName::LongPackageNameToFilename(PackageName, FPackageName::GetAssetPackageExtension());
    const bool bExisted = IFileManager::Get().FileExists(*Filename);
    TArray<uint8> PreviousBytes;
    if (bExisted && !TestTrue(TEXT("Read existing template fixture"),
        FFileHelper::LoadFileToArray(PreviousBytes, *Filename))) return false;
    ON_SCOPE_EXIT
    {
        if (bExisted) TestTrue(TEXT("Restore existing fixture"), FFileHelper::SaveArrayToFile(PreviousBytes, *Filename));
        else if (IFileManager::Get().FileExists(*Filename))
            TestTrue(TEXT("Remove generated fixture"), IFileManager::Get().Delete(*Filename));
    };
    if (bExisted && !TestTrue(TEXT("Isolate template fixture"), IFileManager::Get().Delete(*Filename))) return false;
    UPackage* Package = CreatePackage(*PackageName);
    UFinareBlueprint* Blueprint = Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
        AFinareActor::StaticClass(), Package, TEXT("BP_FirstScript"), BPTYPE_Normal,
        UFinareBlueprint::StaticClass(), UFinareBlueprintGeneratedClass::StaticClass()));
    if (!TestNotNull(TEXT("Create the template's native Script Class"), Blueprint)) return false;
    UFinareAsset* Asset = NewObject<UFinareAsset>(Package);
    Asset->ScriptClassId = ClassId;
    Asset->ScriptClassKind = EFinareClassKind::Actor;
    Asset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
    Asset->CompiledChunkPath = FirstChunk;
    Asset->ScriptTypeName = TEXT("Finare.PureCSharp.FirstScript");
    Asset->DeclarationAssetPath = TEXT("PureCSharp/FirstScript");
    Asset->DeclarationPath = DeclarationPath;
    Blueprint->ScriptAsset = Asset;
    FString Error;
    FFinareBlueprintClassSyncResult Sync;
    const bool bSynced = FFinareBlueprintClassMaterializer::SynchronizeJson(*Asset, *Blueprint, Declaration, false, Sync, Error);
    if (!TestTrue(*FString::Printf(TEXT("Materialize emitted template: %s"), *Error), bSynced)) return false;
    AFinareActor* Defaults = Cast<AFinareActor>(Blueprint->GeneratedClass->GetDefaultObject());
    if (!TestNotNull(TEXT("Generated Actor CDO exists"), Defaults) ||
        !TestNotNull(TEXT("Generated Actor CDO has its script component"),
            Defaults != nullptr ? Defaults->ScriptComponent.Get() : nullptr)) return false;
    Defaults->ScriptComponent->ScriptAsset = Asset;
    FIntProperty* StartValue = FindFProperty<FIntProperty>(Blueprint->GeneratedClass, TEXT("StartValue"));
    if (!TestNotNull(TEXT("Template state is a native property"), StartValue)) return false;
    const int32 InitialWorldCount = FFinareCoreClrHost::Get().GetManagedWorldCount();
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Template World exists"), World)) return false;
    World->AddToRoot();
    GEngine->CreateNewWorldContext(EWorldType::Game).SetCurrentWorld(World);
    ON_SCOPE_EXIT
    {
        if (World->HasBegunPlay()) World->EndPlay(EEndPlayReason::Quit);
        GEngine->DestroyWorldContext(World);
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        TestEqual(TEXT("Managed World lifetime is balanced"), FFinareCoreClrHost::Get().GetManagedWorldCount(), InitialWorldCount);
    };
    World->InitializeActorsForPlay(FURL());
    AActor* Actor = World->SpawnActor<AActor>(Blueprint->GeneratedClass);
    if (!TestNotNull(TEXT("Spawn generated template Actor"), Actor)) return false;
    TestEqual(TEXT("Source default initializes native state"), StartValue->GetPropertyValue_InContainer(Actor), 1);
    World->GetWorldSettings()->NotifyBeginPlay();
    World->Tick(LEVELTICK_All, 0.016f);
    UFinareWorldSubsystem* Host = World->GetSubsystem<UFinareWorldSubsystem>();
    UFinareComponent* Component = Host->ResolveManagedReceiver(Actor, nullptr, false, Error);
    if (!TestNotNull(TEXT("Template activates automatically"), Component)) return false;
    TestTrue(*FString::Printf(TEXT("Template runs: %s"), *Component->LastScriptError), Component->bScriptRunning);
    TestEqual(TEXT("C# writes native state immediately"), StartValue->GetPropertyValue_InContainer(Actor), 2);
    StartValue->SetPropertyValue_InContainer(Actor, 11);
    Asset->CompiledChunkPath = SecondChunk;
    const bool bReloaded = Component->ReloadScript();
    TestTrue(*FString::Printf(TEXT("C# body change reloads: %s"), *Component->LastScriptError), bReloaded);
    TestEqual(TEXT("New body adds two to the native edit"), StartValue->GetPropertyValue_InContainer(Actor), 13);
    TestEqual(TEXT("Reload publishes generation two"), Component->ActiveGeneration, FString(TEXT("2")));
    TestTrue(TEXT("No runtime error"), Component->LastScriptError.IsEmpty());
    Actor->Destroy();
    TestEqual(TEXT("Destruction releases the script"), Host->GetManagedScriptCount(), 0);
    return !HasAnyErrors();
}
#endif
