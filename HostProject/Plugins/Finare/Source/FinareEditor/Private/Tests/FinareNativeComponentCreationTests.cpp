#if WITH_DEV_AUTOMATION_TESTS
#include "FinareActor.h"
#include "FinareAsset.h"
#include "FinareBlueprint.h"
#include "FinareBlueprintClassMaterializer.h"
#include "FinareComponent.h"
#include "FinareCoreClrHost.h"
#include "FinareWorldSubsystem.h"
#include "Components/SceneComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "GameFramework/WorldSettings.h"
#include "HAL/FileManager.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareNativeComponentCreationTest,
    "Finare.Unreal.Components.GeneratedCreation",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativeComponentCreationTest::RunTest(const FString& Parameters)
{
    const FString Root = FPaths::ConvertRelativePathToFull(FPaths::ProjectSavedDir() / TEXT("Finare/NativeComponentCreation"));
    const FString DeclarationPath = Root / TEXT("NativeComponentCreation.asset.json");
    const FString ChunkPath = Root / TEXT("NativeComponentCreation.uschunk");
    FString Declaration;
    if (!TestTrue(TEXT("Prepare emitted C# component fixture"), FPaths::FileExists(ChunkPath) &&
        FFileHelper::LoadFileToString(Declaration, *DeclarationPath))) return false;
    TSharedPtr<FJsonObject> Document;
    if (!TestTrue(TEXT("Read emitted class declarations"),
        FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(Declaration), Document))) return false;

    // Isolate only the fixture's saved packages; user assets are restored byte-for-byte.
    TMap<FString, TArray<uint8>> PreviousFiles;
    TArray<FString> CreatedFiles;
    ON_SCOPE_EXIT
    {
        for (const auto& Pair : PreviousFiles)
            TestTrue(TEXT("Restore pre-existing fixture package"), FFileHelper::SaveArrayToFile(Pair.Value, *Pair.Key));
        for (const FString& Filename : CreatedFiles)
            if (IFileManager::Get().FileExists(*Filename))
                TestTrue(TEXT("Remove generated fixture package"), IFileManager::Get().Delete(*Filename));
    };
    UFinareBlueprint* ActorBlueprint = nullptr;
    UFinareBlueprint* ObserverBlueprint = nullptr;
    for (const auto& Value : Document->GetArrayField(TEXT("assets")))
    {
        const auto& Json = Value->AsObject();
        const bool bActor = Json->GetStringField(TEXT("qualifiedName")).EndsWith(TEXT(".ComponentCreator"));
        const FString ClassPath = Json->GetObjectField(TEXT("classBinding"))->GetStringField(TEXT("generatedClassPath"));
        const FString PackageName = FPackageName::ObjectPathToPackageName(ClassPath);
        const FString Filename = FPackageName::LongPackageNameToFilename(PackageName, FPackageName::GetAssetPackageExtension());
        if (IFileManager::Get().FileExists(*Filename))
        {
            TArray<uint8> Bytes;
            if (!TestTrue(TEXT("Read existing fixture package"), FFileHelper::LoadFileToArray(Bytes, *Filename))) return false;
            PreviousFiles.Add(Filename, MoveTemp(Bytes));
            if (!TestTrue(TEXT("Isolate fixture package"), IFileManager::Get().Delete(*Filename))) return false;
        }
        else CreatedFiles.Add(Filename);
        IFileManager::Get().MakeDirectory(*FPaths::GetPath(Filename), true);
        UPackage* Package = CreatePackage(*PackageName);
        // The normal Actor factory starts with AFinareActor; declaration materialization
        // changes it to the declared AActor parent and supplies its internal carrier.
        UFinareBlueprint* Blueprint = Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
            bActor ? AFinareActor::StaticClass() : USceneComponent::StaticClass(), Package,
            FName(*FPackageName::GetShortName(PackageName)), BPTYPE_Normal,
            UFinareBlueprint::StaticClass(), UFinareBlueprintGeneratedClass::StaticClass()));
        if (!TestNotNull(TEXT("Create Script Class proxy"), Blueprint)) return false;
        UFinareAsset* Asset = NewObject<UFinareAsset>(Package, NAME_None, RF_Public | RF_Standalone | RF_Transactional);
        if (!TestTrue(TEXT("Parse emitted identity"), FGuid::ParseExact(Json->GetStringField(TEXT("id")).Mid(3),
            EGuidFormats::Digits, Asset->ScriptClassId))) return false;
        Asset->ScriptClassKind = bActor ? EFinareClassKind::Actor : EFinareClassKind::ActorComponent;
        Asset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
        Asset->ScriptTypeName = Json->GetStringField(TEXT("qualifiedName"));
        Asset->DeclarationAssetPath = Json->GetStringField(TEXT("assetPath"));
        Asset->DeclarationPath = DeclarationPath;
        Asset->CompiledChunkPath = ChunkPath;
        Blueprint->ScriptAsset = Asset;
        if (bActor) CastChecked<AFinareActor>(Blueprint->GeneratedClass->GetDefaultObject())->ScriptComponent->ScriptAsset = Asset;
        FString Error;
        FFinareBlueprintClassSyncResult Sync;
        if (!FFinareBlueprintClassMaterializer::SynchronizeJson(*Asset, *Blueprint, Declaration, false, Sync, Error))
        { AddError(Error); return false; }
        if (bActor) ActorBlueprint = Blueprint; else ObserverBlueprint = Blueprint;
    }
    if (!TestNotNull(TEXT("Actor declaration exists"), ActorBlueprint) ||
        !TestNotNull(TEXT("Component declaration exists"), ObserverBlueprint)) return false;
    TestEqual(TEXT("Actor has ordinary native parent"), ActorBlueprint->GeneratedClass->GetSuperClass(), AActor::StaticClass());
    TestEqual(TEXT("Component has ordinary native parent"), ObserverBlueprint->GeneratedClass->GetSuperClass(), USceneComponent::StaticClass());

    const int32 InitialWorlds = FFinareCoreClrHost::Get().GetManagedWorldCount();
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Create Game World"), World)) return false;
    World->AddToRoot();
    GEngine->CreateNewWorldContext(EWorldType::Game).SetCurrentWorld(World);
    ON_SCOPE_EXIT
    {
        if (World->HasBegunPlay()) World->EndPlay(EEndPlayReason::Quit);
        GEngine->DestroyWorldContext(World);
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        TestEqual(TEXT("World host released"), FFinareCoreClrHost::Get().GetManagedWorldCount(), InitialWorlds);
    };
    World->InitializeActorsForPlay(FURL());
    World->GetWorldSettings()->NotifyBeginPlay();
    AActor* Actor = World->SpawnActor<AActor>(ActorBlueprint->GeneratedClass);
    if (!TestNotNull(TEXT("Spawn C# Actor"), Actor)) return false;
    UFinareWorldSubsystem* Host = World->GetSubsystem<UFinareWorldSubsystem>();
    FString Error;
    UFinareComponent* ActorHost = Host->ResolveManagedReceiver(Actor, nullptr, false, Error);
    if (!TestNotNull(*FString::Printf(TEXT("Actor automatically activates: %s"), *Error), ActorHost)) return false;

    auto FindTagged = [&](FName Tag) -> USceneComponent*
    {
        TInlineComponentArray<USceneComponent*> Components(Actor);
        for (USceneComponent* Component : Components)
            if (IsValid(Component) && Component->ComponentTags.Contains(Tag)) return Component;
        return nullptr;
    };
    auto CheckCreated = [&]() -> bool
    {
        USceneComponent* RootComponent = FindTagged(TEXT("CreatedRoot"));
        UStaticMeshComponent* Mesh = Cast<UStaticMeshComponent>(FindTagged(TEXT("CreatedMesh")));
        USceneComponent* First = FindTagged(TEXT("First"));
        USceneComponent* Second = FindTagged(TEXT("Second"));
        if (!TestNotNull(TEXT("C# created root"), RootComponent) || !TestNotNull(TEXT("C# created mesh"), Mesh) ||
            !TestNotNull(TEXT("C# created first receiver"), First) || !TestNotNull(TEXT("C# created second receiver"), Second)) return false;
        TestEqual(TEXT("C# explicitly attached mesh"), Mesh->GetAttachParent(), RootComponent);
        TestTrue(TEXT("C# assigned real mesh"), Mesh->GetStaticMesh() != nullptr);
        TestTrue(TEXT("UE registered all created components"), RootComponent->IsRegistered() && Mesh->IsRegistered() && First->IsRegistered() && Second->IsRegistered());
        TestTrue(TEXT("First BeginPlay sees deferred input"), First->ComponentTags.Contains(TEXT("Begin=41")));
        TestTrue(TEXT("Second BeginPlay sees independent input"), Second->ComponentTags.Contains(TEXT("Begin=73")));
        TestEqual(TEXT("One host per script receiver"), Host->GetManagedScriptCount(), 3);
        TInlineComponentArray<USceneComponent*> Components(Actor);
        TestEqual(TEXT("Exactly four C# scene components and the UE default root"), Components.Num(), 5);
        return true;
    };
    if (!CheckCreated()) return false;
    World->Tick(LEVELTICK_All, 0.016f);
    TestTrue(TEXT("First ticks once"), FindTagged(TEXT("First"))->ComponentTags.Contains(TEXT("Tick=1")));
    TestTrue(TEXT("Second ticks once"), FindTagged(TEXT("Second"))->ComponentTags.Contains(TEXT("Tick=1")));
    FFinareManagedFunctionValueV1 Result;
    if (!ActorHost->InvokeManagedScriptFunction(TEXT("RemoveFirst"), {}, Result, Error))
    { AddError(Error); return false; }
    TestNull(TEXT("C# destroyed first receiver"), FindTagged(TEXT("First")));
    TestTrue(TEXT("Destroyed handle cannot be read"), Actor->Tags.Contains(TEXT("DestroyedAccessRejected")));
    TestEqual(TEXT("Destroy released exactly one script host"), Host->GetManagedScriptCount(), 2);
    TestEqual(TEXT("Destroyed receiver stopped once"), Actor->Tags.FilterByPredicate([](FName Tag) { return Tag == TEXT("End=First"); }).Num(), 1);
    if (!TestTrue(*FString::Printf(TEXT("Actor reload: %s"), *ActorHost->LastScriptError), ActorHost->ReloadScript())) return false;
    if (!CheckCreated()) return false;
    TestEqual(TEXT("Reload generation advanced"), ActorHost->ActiveGeneration, FString(TEXT("2")));
    TestEqual(TEXT("Remaining old receiver stopped once"), Actor->Tags.FilterByPredicate([](FName Tag) { return Tag == TEXT("End=Second"); }).Num(), 1);
    Actor->Destroy();
    TestEqual(TEXT("Actor destruction releases component and Actor hosts"), Host->GetManagedScriptCount(), 0);
    return !HasAnyErrors();
}
#endif
