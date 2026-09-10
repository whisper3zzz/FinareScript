#if WITH_DEV_AUTOMATION_TESTS

#include "FinareBlueprintClassMaterializer.h"

#include "Engine/Engine.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformFileManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "PackageTools.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "FinareAsset.h"
#include "FinareActor.h"
#include "FinareBlueprint.h"
#include "FinareComponent.h"
#include "FinareCoreClrHost.h"
#include "FinareWorldSubsystem.h"
#include "Interfaces/IPluginManager.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include "UObject/SoftObjectPtr.h"
#include "UObject/UnrealType.h"

namespace UE::Finare::CollectionStateGameplayTests
{
class FScenario final
{
public:
    FAutomationTestBase& Test;
    const FString PackageName = TEXT("/Game/UnifiedScriptDemo/BP_CollectionStateGameplay");
    const FString AssetName = TEXT("BP_CollectionStateGameplay");
    const FString BlueprintPath = PackageName + TEXT(".") + AssetName;
    UPackage* Package = nullptr;
    UFinareBlueprint* Blueprint = nullptr;
    UWorld* World = nullptr;
    FWorldContext* WorldContext = nullptr;
    AFinareActor* Actor = nullptr;
    AFinareActor* UnoverriddenActor = nullptr;
    UFinareWorldSubsystem* Host = nullptr;
    FString DeclarationJson;
    FString DeclarationPath;
    FString ChunkPath;
    FString AssemblyPath;
    FString PackageFilename;
    bool bHadOriginalPackage = false;
    bool bOriginalPackageReadOnly = false;
    bool bMayRestorePackage = false;
    bool bOwnsTemporaryPackage = false;
    TArray<uint8> OriginalPackageBytes;
    int32 InitialManagedWorldCount = 0;

    explicit FScenario(FAutomationTestBase& InTest)
        : Test(InTest)
    {
    }

    ~FScenario()
    {
        if (World != nullptr)
        {
            for (TActorIterator<AFinareActor> It(World); It; ++It)
            {
                AFinareActor* LiveActor = *It;
                if (LiveActor == nullptr || LiveActor->IsActorBeingDestroyed())
                {
                    continue;
                }
                if (UFinareComponent* Component = LiveActor->GetFinareComponent();
                    Component != nullptr && Component->bScriptRunning)
                {
                    Component->EndPlay(EEndPlayReason::Destroyed);
                }
                LiveActor->Destroy();
            }
            if (WorldContext != nullptr && GEngine != nullptr)
            {
                GEngine->DestroyWorldContext(World);
                WorldContext = nullptr;
            }
            World->RemoveFromRoot();
            World->DestroyWorld(false);
            World = nullptr;
        }
        Blueprint = nullptr;
        if (Package != nullptr)
        {
            Package->SetDirtyFlag(false);
            TArray<UPackage*> Packages{Package};
            UPackageTools::UnloadPackages(Packages);
            Package = nullptr;
        }

        IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
        if (bMayRestorePackage && !PackageFilename.IsEmpty() && bHadOriginalPackage)
        {
            PlatformFile.SetReadOnly(*PackageFilename, false);
            if (!FFileHelper::SaveArrayToFile(OriginalPackageBytes, *PackageFilename))
            {
                Test.AddError(TEXT("Collection state fixture failed to restore the existing Blueprint bytes."));
            }
            if (!PlatformFile.SetReadOnly(*PackageFilename, bOriginalPackageReadOnly))
            {
                Test.AddError(TEXT("Collection state fixture failed to restore the Blueprint read-only flag."));
            }
        }
        else if (bMayRestorePackage && bOwnsTemporaryPackage && !PackageFilename.IsEmpty() &&
            IFileManager::Get().FileExists(*PackageFilename) &&
            !IFileManager::Get().Delete(*PackageFilename, false, true, true))
        {
            Test.AddError(TEXT("Collection state fixture failed to remove its temporary Blueprint."));
        }
    }

    static bool SetSourceDefaults(
        const FString& Input,
        const TCHAR* QualifiedName,
        const TCHAR* MeshPath,
        const TCHAR* Choice,
        FString& Output)
    {
        TSharedPtr<FJsonObject> Root;
        const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Input);
        if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
        {
            return false;
        }
        const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
        if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr)
        {
            return false;
        }
        for (const TSharedPtr<FJsonValue>& AssetValue : *Assets)
        {
            const TSharedPtr<FJsonObject> Asset = AssetValue->AsObject();
            FString Name;
            if (!Asset.IsValid() || !Asset->TryGetStringField(TEXT("qualifiedName"), Name) ||
                Name != QualifiedName)
            {
                continue;
            }
            const TArray<TSharedPtr<FJsonValue>>* States = nullptr;
            if (!Asset->TryGetArrayField(TEXT("states"), States) || States == nullptr)
            {
                return false;
            }
            for (const TSharedPtr<FJsonValue>& StateValue : *States)
            {
                const TSharedPtr<FJsonObject> State = StateValue->AsObject();
                FString StateName;
                if (!State.IsValid() || !State->TryGetStringField(TEXT("name"), StateName))
                {
                    continue;
                }
                if (StateName == TEXT("Meshes"))
                {
                    TArray<TSharedPtr<FJsonValue>> Meshes;
                    Meshes.Add(MakeShared<FJsonValueString>(MeshPath));
                    State->SetArrayField(TEXT("defaultValue"), MoveTemp(Meshes));
                }
                else if (StateName == TEXT("EnabledChoices"))
                {
                    TArray<TSharedPtr<FJsonValue>> Choices;
                    Choices.Add(MakeShared<FJsonValueString>(Choice));
                    State->SetArrayField(TEXT("defaultValue"), MoveTemp(Choices));
                }
                else if (StateName == TEXT("ChoiceIndices"))
                {
                    TArray<TSharedPtr<FJsonValue>> Indices;
                    TSharedPtr<FJsonObject> Pair = MakeShared<FJsonObject>();
                    Pair->SetStringField(TEXT("key"), Choice);
                    Pair->SetNumberField(TEXT("value"), 0.0);
                    Indices.Add(MakeShared<FJsonValueObject>(Pair));
                    State->SetArrayField(TEXT("defaultValue"), MoveTemp(Indices));
                }
                else if (StateName == TEXT("SelectedChoice"))
                {
                    State->SetStringField(TEXT("defaultValue"), Choice);
                }
            }
            break;
        }
        const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Output);
        return FJsonSerializer::Serialize(Root.ToSharedRef(), Writer);
    }

    static void SetMeshes(const FArrayProperty* Property, UObject* Owner, TArrayView<const TCHAR*> Paths)
    {
        FScriptArrayHelper Helper(Property, Property->ContainerPtrToValuePtr<void>(Owner));
        Helper.EmptyValues();
        const FSoftObjectProperty* Element = CastFieldChecked<FSoftObjectProperty>(Property->Inner);
        for (const TCHAR* Path : Paths)
        {
            const int32 Index = Helper.AddValue();
            Element->SetPropertyValue(Helper.GetRawPtr(Index), FSoftObjectPtr(FSoftObjectPath(Path)));
        }
    }

    static void SetChoices(const FSetProperty* Property, UObject* Owner, TArrayView<const TCHAR*> Choices)
    {
        FScriptSetHelper Helper(Property, Property->ContainerPtrToValuePtr<void>(Owner));
        Helper.EmptyElements();
        const FNameProperty* Element = CastFieldChecked<FNameProperty>(Property->ElementProp);
        for (const TCHAR* Choice : Choices)
        {
            const int32 Index = Helper.AddDefaultValue_Invalid_NeedsRehash();
            Element->SetPropertyValue(Helper.GetElementPtr(Index), FName(Choice));
        }
        Helper.Rehash();
    }

    static void SetIndices(const FMapProperty* Property, UObject* Owner, TArrayView<const TPair<const TCHAR*, int32>> Values)
    {
        FScriptMapHelper Helper(Property, Property->ContainerPtrToValuePtr<void>(Owner));
        Helper.EmptyValues();
        const FNameProperty* Key = CastFieldChecked<FNameProperty>(Property->KeyProp);
        const FIntProperty* Value = CastFieldChecked<FIntProperty>(Property->ValueProp);
        for (const TPair<const TCHAR*, int32>& Pair : Values)
        {
            const int32 Index = Helper.AddDefaultValue_Invalid_NeedsRehash();
            Key->SetPropertyValue(Helper.GetKeyPtr(Index), FName(Pair.Key));
            Value->SetPropertyValue(Helper.GetValuePtr(Index), Pair.Value);
        }
        Helper.Rehash();
    }

    static FString FirstMeshPath(const FArrayProperty* Property, UObject* Owner)
    {
        FScriptArrayHelper Helper(Property, Property->ContainerPtrToValuePtr<void>(Owner));
        if (Helper.Num() == 0)
        {
            return FString();
        }
        return CastFieldChecked<FSoftObjectProperty>(Property->Inner)
            ->GetPropertyValue(Helper.GetRawPtr(0)).ToSoftObjectPath().ToString();
    }

    static bool HasChoice(const FSetProperty* Property, UObject* Owner, const TCHAR* Choice)
    {
        FScriptSetHelper Helper(Property, Property->ContainerPtrToValuePtr<void>(Owner));
        const FNameProperty* Element = CastFieldChecked<FNameProperty>(Property->ElementProp);
        for (int32 Index = 0; Index < Helper.GetMaxIndex(); ++Index)
        {
            if (Helper.IsValidIndex(Index) && Element->GetPropertyValue(Helper.GetElementPtr(Index)) == FName(Choice))
            {
                return true;
            }
        }
        return false;
    }

    static bool HasIndex(const FMapProperty* Property, UObject* Owner, const TCHAR* Choice, int32 Expected)
    {
        FScriptMapHelper Helper(Property, Property->ContainerPtrToValuePtr<void>(Owner));
        const FNameProperty* Key = CastFieldChecked<FNameProperty>(Property->KeyProp);
        const FIntProperty* Value = CastFieldChecked<FIntProperty>(Property->ValueProp);
        for (int32 Index = 0; Index < Helper.GetMaxIndex(); ++Index)
        {
            if (Helper.IsValidIndex(Index) && Key->GetPropertyValue(Helper.GetKeyPtr(Index)) == FName(Choice) &&
                Value->GetPropertyValue(Helper.GetValuePtr(Index)) == Expected)
            {
                return true;
            }
        }
        return false;
    }

    static FString DescribeIndices(const FMapProperty* Property, UObject* Owner)
    {
        FString Result;
        FScriptMapHelper Helper(Property, Property->ContainerPtrToValuePtr<void>(Owner));
        const FNameProperty* Key = CastFieldChecked<FNameProperty>(Property->KeyProp);
        const FIntProperty* Value = CastFieldChecked<FIntProperty>(Property->ValueProp);
        for (int32 Index = 0; Index < Helper.GetMaxIndex(); ++Index)
        {
            if (Helper.IsValidIndex(Index))
            {
                if (!Result.IsEmpty())
                {
                    Result += TEXT(", ");
                }
                Result += FString::Printf(TEXT("%s=%d"), *Key->GetPropertyValue(Helper.GetKeyPtr(Index)).ToString(),
                    Value->GetPropertyValue(Helper.GetValuePtr(Index)));
            }
        }
        return Result;
    }

    bool Build()
    {
        const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
        if (!Test.TestTrue(TEXT("Collection state plugin is installed"), Plugin.IsValid()))
        {
            return false;
        }
        const FString DemoDirectory = FPaths::Combine(Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo"));
        DeclarationPath = FPaths::Combine(DemoDirectory, TEXT("Finare.Unreal.Demo.asset.json"));
        ChunkPath = FPaths::Combine(DemoDirectory, TEXT("Finare.Unreal.Demo.uschunk"));
        AssemblyPath = FPaths::Combine(DemoDirectory, TEXT("Finare.Unreal.Demo.dll"));
        if (!Test.TestTrue(TEXT("Collection state declaration is staged"), FFileHelper::LoadFileToString(DeclarationJson, *DeclarationPath)) ||
            !Test.TestTrue(TEXT("Collection state chunk is staged"), IFileManager::Get().FileExists(*ChunkPath)) ||
            !Test.TestTrue(TEXT("Collection state assembly is staged"), IFileManager::Get().FileExists(*AssemblyPath)))
        {
            return false;
        }

        PackageFilename = FPackageName::LongPackageNameToFilename(PackageName, FPackageName::GetAssetPackageExtension());
        if (!Test.TestTrue(TEXT("Collection state fixture has a package filename"), !PackageFilename.IsEmpty()) ||
            !Test.TestTrue(TEXT("Collection state fixture has no loaded user Blueprint"), FindPackage(nullptr, *PackageName) == nullptr))
        {
            return false;
        }
        bHadOriginalPackage = IFileManager::Get().FileExists(*PackageFilename);
        IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
        bOriginalPackageReadOnly = bHadOriginalPackage && PlatformFile.IsReadOnly(*PackageFilename);
        if (bHadOriginalPackage &&
            !Test.TestTrue(TEXT("Collection state fixture can read the existing Blueprint bytes"),
                FFileHelper::LoadFileToArray(OriginalPackageBytes, *PackageFilename)))
        {
            return false;
        }
        if (bHadOriginalPackage && bOriginalPackageReadOnly &&
            !Test.TestTrue(TEXT("Collection state fixture can make the existing Blueprint writable"),
                PlatformFile.SetReadOnly(*PackageFilename, false)))
        {
            return false;
        }
        bMayRestorePackage = true;
        bOwnsTemporaryPackage = !bHadOriginalPackage;

        Package = CreatePackage(*PackageName);
        Blueprint = Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
            AFinareActor::StaticClass(), Package, FName(*AssetName), BPTYPE_Normal,
            UFinareBlueprint::StaticClass(), UFinareBlueprintGeneratedClass::StaticClass(),
            FName(TEXT("FinareCollectionStateGameplay"))));
        if (!Test.TestNotNull(TEXT("Collection state Blueprint is created"), Blueprint) ||
            !Test.TestNotNull(TEXT("Collection state Blueprint has a generated class"),
                Blueprint != nullptr ? Blueprint->GeneratedClass.Get() : nullptr))
        {
            return false;
        }

        UFinareAsset* ScriptAsset = NewObject<UFinareAsset>(Package);
        FGuid::ParseExact(TEXT("02aebfe361013d426bdf4b76921e821c"), EGuidFormats::Digits, ScriptAsset->ScriptClassId);
        ScriptAsset->ScriptClassKind = EFinareClassKind::Actor;
        ScriptAsset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
        ScriptAsset->CompiledChunkPath = ChunkPath;
        ScriptAsset->ScriptAssemblyPath = AssemblyPath;
        ScriptAsset->ScriptTypeName = TEXT("Finare.Unreal.Demo.CollectionStateGameplayDemoScript");
        ScriptAsset->DeclarationPath = DeclarationPath;
        ScriptAsset->DeclarationAssetPath = TEXT("Unreal/CollectionStateGameplay");
        Blueprint->ScriptAsset = ScriptAsset;
        Test.TestTrue(TEXT("Collection state Blueprint keeps the fixture sidecar"), Blueprint->ScriptAsset == ScriptAsset);
        Test.TestEqual(TEXT("Collection state fixture uses the staged semantic ID"),
            ScriptAsset->ScriptClassId.ToString(EGuidFormats::Digits).ToLower(),
            FString(TEXT("02aebfe361013d426bdf4b76921e821c")));

        FFinareBlueprintClassSyncResult Result;
        FString Error;
        if (!Test.TestTrue(*FString::Printf(TEXT("Collection state defaults materialize: %s"), *Error),
            FFinareBlueprintClassMaterializer::SynchronizeJson(*ScriptAsset, *Blueprint, DeclarationJson, false, Result, Error)))
        {
            return false;
        }

        const FArrayProperty* Meshes = FindFProperty<FArrayProperty>(Blueprint->GeneratedClass, TEXT("Meshes"));
        const FSetProperty* Choices = FindFProperty<FSetProperty>(Blueprint->GeneratedClass, TEXT("EnabledChoices"));
        const FMapProperty* Indices = FindFProperty<FMapProperty>(Blueprint->GeneratedClass, TEXT("ChoiceIndices"));
        const FNameProperty* Selected = FindFProperty<FNameProperty>(Blueprint->GeneratedClass, TEXT("SelectedChoice"));
        const FSoftClassProperty* ActorClass = FindFProperty<FSoftClassProperty>(Blueprint->GeneratedClass, TEXT("PreviewActorClass"));
        Test.TestTrue(TEXT("Collection state fields use native Array/Set/Map properties"),
            Meshes != nullptr && Choices != nullptr && Indices != nullptr && Selected != nullptr && ActorClass != nullptr);
        if (Meshes == nullptr || Choices == nullptr || Indices == nullptr || Selected == nullptr || ActorClass == nullptr)
        {
            return false;
        }
        UObject* Cdo = Blueprint->GeneratedClass->GetDefaultObject();
        Test.TestEqual(TEXT("Initial mesh default is Cube"), FirstMeshPath(Meshes, Cdo), FString(TEXT("/Engine/BasicShapes/Cube.Cube")));
        Test.TestTrue(TEXT("Initial enabled choices contain Sphere"), HasChoice(Choices, Cdo, TEXT("Sphere")));
        Test.TestTrue(TEXT("Initial choice index maps Sphere to one"), HasIndex(Indices, Cdo, TEXT("Sphere"), 1));
        const TCHAR* CdoChoices[] = {TEXT("Sphere")};
        SetChoices(Choices, Cdo, MakeArrayView(CdoChoices));
        Selected->SetPropertyValue_InContainer(Cdo, FName(TEXT("Sphere")));

        FString ChangedDeclaration;
        if (!Test.TestTrue(TEXT("Collection state source defaults can be updated"),
            SetSourceDefaults(DeclarationJson,
                TEXT("Finare.Unreal.Demo.CollectionStateGameplayDemoScript"),
                TEXT("/Engine/BasicShapes/Cube.Cube"), TEXT("Cube"), ChangedDeclaration)))
        {
            return false;
        }
        Error.Reset();
        if (!Test.TestTrue(*FString::Printf(TEXT("Collection state source update succeeds: %s"), *Error),
            FFinareBlueprintClassMaterializer::SynchronizeJson(*ScriptAsset, *Blueprint, ChangedDeclaration, false, Result, Error)))
        {
            return false;
        }
        Cdo = Blueprint->GeneratedClass->GetDefaultObject();
        Meshes = FindFProperty<FArrayProperty>(Blueprint->GeneratedClass, TEXT("Meshes"));
        Choices = FindFProperty<FSetProperty>(Blueprint->GeneratedClass, TEXT("EnabledChoices"));
        Indices = FindFProperty<FMapProperty>(Blueprint->GeneratedClass, TEXT("ChoiceIndices"));
        Selected = FindFProperty<FNameProperty>(Blueprint->GeneratedClass, TEXT("SelectedChoice"));
        FScriptArrayHelper SourceMeshesHelper(Meshes, Meshes->ContainerPtrToValuePtr<void>(Cdo));
        FScriptMapHelper SourceIndicesHelper(Indices, Indices->ContainerPtrToValuePtr<void>(Cdo));
        Test.TestEqual(TEXT("Source update propagates one mesh default"), SourceMeshesHelper.Num(), 1);
        Test.TestEqual(TEXT("Source update propagates the new mesh default"), FirstMeshPath(Meshes, Cdo), FString(TEXT("/Engine/BasicShapes/Cube.Cube")));
        FScriptSetHelper CdoChoicesHelper(Choices, Choices->ContainerPtrToValuePtr<void>(Cdo));
        Test.TestEqual(TEXT("CDO collection override contains one choice"), CdoChoicesHelper.Num(), 1);
        Test.TestTrue(TEXT("Explicit CDO collection override survives source update"), HasChoice(Choices, Cdo, TEXT("Sphere")));
        Test.TestEqual(TEXT("Source update propagates one map entry"), SourceIndicesHelper.Num(), 1);
        Test.TestTrue(TEXT("Source update propagates the new map index"), HasIndex(Indices, Cdo, TEXT("Cube"), 0));
        Test.TestEqual(TEXT("Explicit CDO SelectedChoice override survives source update"), Selected->GetPropertyValue_InContainer(Cdo), FName(TEXT("Sphere")));

        IFileManager::Get().MakeDirectory(*FPaths::GetPath(PackageFilename), true);
        FSavePackageArgs SaveArguments;
        SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
        SaveArguments.SaveFlags = SAVE_NoError;
        if (!Test.TestTrue(TEXT("Collection state Blueprint saves"), UPackage::SavePackage(Package, Blueprint, *PackageFilename, SaveArguments)))
        {
            return false;
        }

        InitialManagedWorldCount = FFinareCoreClrHost::Get().GetManagedWorldCount();
        World = UWorld::CreateWorld(EWorldType::Game, false);
        if (!Test.TestNotNull(TEXT("Collection state gameplay World is created"), World))
        {
            return false;
        }
        if (!Test.TestNotNull(TEXT("Collection state gameplay Engine exists"), GEngine))
        {
            return false;
        }
        WorldContext = &GEngine->CreateNewWorldContext(EWorldType::Game);
        WorldContext->SetCurrentWorld(World);
        World->AddToRoot();
        World->InitializeActorsForPlay(FURL());
        Actor = Cast<AFinareActor>(World->SpawnActor(Blueprint->GeneratedClass));
        AFinareActor* UnoverriddenCreated = Cast<AFinareActor>(
            World->SpawnActor(Blueprint->GeneratedClass));
        Host = World->GetSubsystem<UFinareWorldSubsystem>();
        if (!Test.TestNotNull(TEXT("Collection state gameplay Actor spawns"), Actor) ||
            !Test.TestNotNull(TEXT("Collection state unoverridden Actor spawns"), UnoverriddenCreated) ||
            !Test.TestNotNull(TEXT("Collection state gameplay host exists"), Host))
        {
            return false;
        }
        Meshes = FindFProperty<FArrayProperty>(Actor->GetClass(), TEXT("Meshes"));
        Choices = FindFProperty<FSetProperty>(Actor->GetClass(), TEXT("EnabledChoices"));
        Indices = FindFProperty<FMapProperty>(Actor->GetClass(), TEXT("ChoiceIndices"));
        Selected = FindFProperty<FNameProperty>(Actor->GetClass(), TEXT("SelectedChoice"));
        ActorClass = FindFProperty<FSoftClassProperty>(Actor->GetClass(), TEXT("PreviewActorClass"));
        Actor->Modify();
        const TCHAR* InitialMeshes[] = {
            TEXT("/Engine/BasicShapes/Cube.Cube"), TEXT("/Engine/BasicShapes/Sphere.Sphere")};
        const TCHAR* InitialChoices[] = {TEXT("Cube")};
        const TPair<const TCHAR*, int32> InitialIndices[] = {{TEXT("Cube"), 0}, {TEXT("Spare"), 1}};
        SetMeshes(Meshes, Actor, MakeArrayView(InitialMeshes));
        SetChoices(Choices, Actor, MakeArrayView(InitialChoices));
        SetIndices(Indices, Actor, MakeArrayView(InitialIndices));
        Selected->SetPropertyValue_InContainer(Actor, FName(TEXT("Cube")));
        ActorClass->SetPropertyValue_InContainer(Actor, FSoftObjectPtr(FSoftObjectPath(TEXT("/Script/Engine.StaticMeshActor"))));

        FString LiveChangedDeclaration;
        if (!Test.TestTrue(TEXT("Collection state source defaults update while an Actor exists"),
            SetSourceDefaults(ChangedDeclaration,
                TEXT("Finare.Unreal.Demo.CollectionStateGameplayDemoScript"),
                TEXT("/Engine/BasicShapes/Sphere.Sphere"), TEXT("Sphere"), LiveChangedDeclaration)))
        {
            return false;
        }
        Error.Reset();
        if (!Test.TestTrue(*FString::Printf(TEXT("Collection state live source update succeeds: %s"), *Error),
            FFinareBlueprintClassMaterializer::SynchronizeJson(*ScriptAsset, *Blueprint, LiveChangedDeclaration, false, Result, Error)))
        {
            return false;
        }
        Actor = nullptr;
        UnoverriddenActor = nullptr;
        for (TActorIterator<AFinareActor> It(World); It; ++It)
        {
            AFinareActor* Candidate = *It;
            const FArrayProperty* CandidateMeshes = FindFProperty<FArrayProperty>(
                Candidate->GetClass(), TEXT("Meshes"));
            const FMapProperty* CandidateIndices = FindFProperty<FMapProperty>(
                Candidate->GetClass(), TEXT("ChoiceIndices"));
            if (CandidateMeshes == nullptr || CandidateIndices == nullptr)
            {
                continue;
            }
            const FScriptArrayHelper CandidateMeshesHelper(
                CandidateMeshes,
                CandidateMeshes->ContainerPtrToValuePtr<void>(Candidate));
            const FScriptMapHelper CandidateIndicesHelper(
                CandidateIndices,
                CandidateIndices->ContainerPtrToValuePtr<void>(Candidate));
            if (CandidateMeshesHelper.Num() == 2 && CandidateIndicesHelper.Num() == 2)
            {
                Actor = Candidate;
            }
            else if (CandidateMeshesHelper.Num() == 1 && CandidateIndicesHelper.Num() == 1)
            {
                UnoverriddenActor = Candidate;
            }
        }
        Test.TestNotNull(TEXT("Collection state Actor survives live source update"), Actor);
        Test.TestNotNull(TEXT("Unmodified collection Actor inherits the new source defaults"), UnoverriddenActor);
        if (Actor == nullptr || UnoverriddenActor == nullptr)
        {
            return false;
        }
        Meshes = FindFProperty<FArrayProperty>(Actor->GetClass(), TEXT("Meshes"));
        Choices = FindFProperty<FSetProperty>(Actor->GetClass(), TEXT("EnabledChoices"));
        Indices = FindFProperty<FMapProperty>(Actor->GetClass(), TEXT("ChoiceIndices"));
        Selected = FindFProperty<FNameProperty>(Actor->GetClass(), TEXT("SelectedChoice"));
        FScriptArrayHelper LiveMeshesHelper(Meshes, Meshes->ContainerPtrToValuePtr<void>(Actor));
        FScriptSetHelper LiveChoicesHelper(Choices, Choices->ContainerPtrToValuePtr<void>(Actor));
        FScriptMapHelper LiveIndicesHelper(Indices, Indices->ContainerPtrToValuePtr<void>(Actor));
        Test.TestEqual(TEXT("Explicit live mesh override survives source update"), LiveMeshesHelper.Num(), 2);
        Test.TestEqual(TEXT("Explicit live mesh remains Cube first"), FirstMeshPath(Meshes, Actor), FString(TEXT("/Engine/BasicShapes/Cube.Cube")));
        Test.TestEqual(TEXT("Explicit live choice override survives source update"), LiveChoicesHelper.Num(), 1);
        Test.TestTrue(TEXT("Explicit live choice remains Cube"), HasChoice(Choices, Actor, TEXT("Cube")));
        Test.TestEqual(TEXT("Explicit live map override survives source update"), LiveIndicesHelper.Num(), 2);
        const bool bLiveCubeIndex = HasIndex(Indices, Actor, TEXT("Cube"), 0);
        Test.TestTrue(*FString::Printf(TEXT("Explicit live map remains Cube zero (actual=%s)"), *DescribeIndices(Indices, Actor)), bLiveCubeIndex);
        Test.TestTrue(TEXT("Explicit live map preserves its second entry"), HasIndex(Indices, Actor, TEXT("Spare"), 1));
        Test.TestEqual(TEXT("Explicit live scalar override survives source update"), Selected->GetPropertyValue_InContainer(Actor), FName(TEXT("Cube")));

        const FArrayProperty* UnoverriddenMeshes = FindFProperty<FArrayProperty>(
            UnoverriddenActor->GetClass(), TEXT("Meshes"));
        const FSetProperty* UnoverriddenChoices = FindFProperty<FSetProperty>(
            UnoverriddenActor->GetClass(), TEXT("EnabledChoices"));
        const FMapProperty* UnoverriddenIndices = FindFProperty<FMapProperty>(
            UnoverriddenActor->GetClass(), TEXT("ChoiceIndices"));
        Test.TestNotNull(TEXT("Unmodified collection Actor has the new mesh array"), UnoverriddenMeshes);
        Test.TestNotNull(TEXT("Unmodified collection Actor has the new choice set"), UnoverriddenChoices);
        Test.TestNotNull(TEXT("Unmodified collection Actor has the new choice map"), UnoverriddenIndices);
        if (UnoverriddenMeshes == nullptr || UnoverriddenChoices == nullptr || UnoverriddenIndices == nullptr)
        {
            return false;
        }
        Test.TestEqual(TEXT("Unmodified collection Actor inherits one mesh"),
            FScriptArrayHelper(
                UnoverriddenMeshes,
                UnoverriddenMeshes->ContainerPtrToValuePtr<void>(UnoverriddenActor)).Num(),
            1);
        Test.TestEqual(TEXT("Unmodified collection Actor inherits Sphere mesh"),
            FirstMeshPath(UnoverriddenMeshes, UnoverriddenActor),
            FString(TEXT("/Engine/BasicShapes/Sphere.Sphere")));
        Test.TestEqual(TEXT("Unmodified collection Actor inherits one choice"),
            FScriptSetHelper(
                UnoverriddenChoices,
                UnoverriddenChoices->ContainerPtrToValuePtr<void>(UnoverriddenActor)).Num(),
            1);
        Test.TestTrue(TEXT("Unmodified collection Actor inherits Sphere choice"),
            HasChoice(UnoverriddenChoices, UnoverriddenActor, TEXT("Sphere")));
        Test.TestEqual(TEXT("Unmodified collection Actor inherits one map entry"),
            FScriptMapHelper(
                UnoverriddenIndices,
                UnoverriddenIndices->ContainerPtrToValuePtr<void>(UnoverriddenActor)).Num(),
            1);
        Test.TestTrue(TEXT("Unmodified collection Actor inherits Sphere index"),
            HasIndex(UnoverriddenIndices, UnoverriddenActor, TEXT("Sphere"), 0));

        Actor->GetFinareComponent()->ScriptAsset = ScriptAsset;
        Actor->GetFinareComponent()->BeginPlay();
        Test.TestEqual(TEXT("Collection state managed World is registered"),
            FFinareCoreClrHost::Get().GetManagedWorldCount(), InitialManagedWorldCount + 1);
        return true;
    }

    void Pump()
    {
        if (Host != nullptr && Actor != nullptr && Actor->GetFinareComponent() != nullptr)
        {
            Host->BeginManagedFrame();
            Actor->GetFinareComponent()->TickComponent(0.016f, LEVELTICK_All, nullptr);
            Host->EndManagedFrame();
        }
    }

    int32 PreviewCount() const
    {
        int32 Count = 0;
        if (World != nullptr)
        {
            for (TActorIterator<AStaticMeshActor> It(World); It; ++It)
            {
                ++Count;
            }
        }
        return Count;
    }

    FString PreviewPath() const
    {
        if (World == nullptr)
        {
            return FString();
        }
        for (TActorIterator<AStaticMeshActor> It(World); It; ++It)
        {
            if (It->GetStaticMeshComponent()->GetStaticMesh() != nullptr)
            {
                return It->GetStaticMeshComponent()->GetStaticMesh()->GetPathName();
            }
        }
        return FString();
    }

    bool FinishWorld()
    {
        if (Actor != nullptr)
        {
            Actor->GetFinareComponent()->EndPlay(EEndPlayReason::Destroyed);
            Test.TestEqual(TEXT("Collection state EndPlay destroys the preview before World teardown"), PreviewCount(), 0);
            Actor->Destroy();
            Actor = nullptr;
        }
        World->RemoveFromRoot();
        if (WorldContext != nullptr && GEngine != nullptr)
        {
            GEngine->DestroyWorldContext(World);
            WorldContext = nullptr;
        }
        World->DestroyWorld(false);
        World = nullptr;
        Test.TestEqual(TEXT("Collection state World cleanup releases managed state"),
            FFinareCoreClrHost::Get().GetManagedWorldCount(), InitialManagedWorldCount);
        return !Test.HasAnyErrors();
    }
};

class FRun final : public IAutomationLatentCommand
{
    TSharedPtr<FScenario> Scenario;
    FAutomationTestBase& Test;
    double Started = FPlatformTime::Seconds();
    int32 Phase = 0;
    int32 ReloadGeneration = 0;

public:
    FRun(const TSharedPtr<FScenario>& InScenario, FAutomationTestBase& InTest)
        : Scenario(InScenario), Test(InTest)
    {
    }

    virtual bool Update() override
    {
        FScenario& S = *Scenario;
        if (FPlatformTime::Seconds() - Started > 60.0)
        {
            Test.AddError(FString::Printf(
                TEXT("Collection state gameplay timed out in phase %d (state=%s,error=%s)."),
                Phase,
                S.Actor != nullptr ? *S.Actor->GetFinareComponent()->ManagedState : TEXT("<none>"),
                S.Actor != nullptr ? *S.Actor->GetFinareComponent()->LastScriptError : TEXT("<none>")));
            Scenario.Reset();
            return true;
        }
        S.Pump();
        UFinareComponent* Component = S.Actor != nullptr ? S.Actor->GetFinareComponent() : nullptr;
        if (Component == nullptr)
        {
            Scenario.Reset();
            return true;
        }
        const FString State = Component->ManagedState;
        if (Phase == 0)
        {
            if (State != TEXT("CollectionState:Loaded:/Engine/BasicShapes/Cube.Cube"))
            {
                return false;
            }
            Test.TestEqual(TEXT("Initial live override selects Cube"), S.PreviewPath(), FString(TEXT("/Engine/BasicShapes/Cube.Cube")));
            Test.TestEqual(TEXT("Initial collection selection has one preview"), S.PreviewCount(), 1);
            FFinareManagedFunctionValueV1 Value;
            FString Error;
            Test.TestTrue(TEXT("Managed collection mutation is callable"),
                Component->InvokeManagedScriptFunction(TEXT("SelectSpherePreview"), {}, Value, Error));
            Phase = 1;
            return false;
        }
        if (Phase == 1)
        {
            if (State != TEXT("CollectionState:Loaded:/Engine/BasicShapes/Sphere.Sphere"))
            {
                return false;
            }
            Test.TestEqual(TEXT("Collection mutation switches the real preview to Sphere"), S.PreviewPath(), FString(TEXT("/Engine/BasicShapes/Sphere.Sphere")));
            Test.TestEqual(TEXT("Collection mutation leaves one preview actor"), S.PreviewCount(), 1);
            const FArrayProperty* Meshes = FindFProperty<FArrayProperty>(S.Actor->GetClass(), TEXT("Meshes"));
            const FSetProperty* Choices = FindFProperty<FSetProperty>(S.Actor->GetClass(), TEXT("EnabledChoices"));
            const FMapProperty* Indices = FindFProperty<FMapProperty>(S.Actor->GetClass(), TEXT("ChoiceIndices"));
            const FNameProperty* Selected = FindFProperty<FNameProperty>(S.Actor->GetClass(), TEXT("SelectedChoice"));
            FScriptArrayHelper MeshesHelper(Meshes, Meshes->ContainerPtrToValuePtr<void>(S.Actor));
            FScriptSetHelper ChoicesHelper(Choices, Choices->ContainerPtrToValuePtr<void>(S.Actor));
            FScriptMapHelper IndicesHelper(Indices, Indices->ContainerPtrToValuePtr<void>(S.Actor));
            Test.TestEqual(TEXT("Flushed mesh array contains exactly one item"), MeshesHelper.Num(), 1);
            Test.TestEqual(TEXT("Flushed enabled set contains exactly one item"), ChoicesHelper.Num(), 1);
            Test.TestEqual(TEXT("Flushed choice map contains exactly one item"), IndicesHelper.Num(), 1);
            Test.TestEqual(TEXT("Flushed collection has one Sphere mesh"), FScenario::FirstMeshPath(Meshes, S.Actor), FString(TEXT("/Engine/BasicShapes/Sphere.Sphere")));
            Test.TestTrue(TEXT("Flushed enabled set contains only Sphere"), FScenario::HasChoice(Choices, S.Actor, TEXT("Sphere")));
            Test.TestTrue(TEXT("Flushed map maps Sphere to zero"), FScenario::HasIndex(Indices, S.Actor, TEXT("Sphere"), 0));
            Test.TestEqual(TEXT("Flushed scalar selection is Sphere"), Selected->GetPropertyValue_InContainer(S.Actor), FName(TEXT("Sphere")));
            FFinareManagedFunctionValueV1 Value;
            FString Error;
            Test.TestTrue(TEXT("Managed invalid collection retry is callable"),
                Component->InvokeManagedScriptFunction(TEXT("RetryMissingPreview"), {}, Value, Error));
            Phase = 2;
            return false;
        }
        if (Phase == 2)
        {
            if (!State.StartsWith(TEXT("CollectionState:Error:")))
            {
                return false;
            }
            Test.TestEqual(TEXT("Failed collection load preserves the previous preview"), S.PreviewPath(), FString(TEXT("/Engine/BasicShapes/Sphere.Sphere")));
            Test.TestEqual(TEXT("Failed collection load leaves one preview actor"), S.PreviewCount(), 1);
            FFinareManagedFunctionValueV1 Value;
            FString Error;
            Test.TestTrue(TEXT("Managed collection retry restores the valid selection"),
                Component->InvokeManagedScriptFunction(TEXT("SelectSpherePreview"), {}, Value, Error));
            Phase = 3;
            return false;
        }
        if (Phase == 3)
        {
            if (State != TEXT("CollectionState:Loaded:/Engine/BasicShapes/Sphere.Sphere"))
            {
                return false;
            }
            Test.TestEqual(TEXT("Corrected collection retry loads Sphere"), S.PreviewPath(), FString(TEXT("/Engine/BasicShapes/Sphere.Sphere")));
            ReloadGeneration = FCString::Atoi(*Component->ActiveGeneration);
            Test.TestTrue(TEXT("Collection script reload is accepted"), Component->ReloadScript());
            Phase = 4;
            return false;
        }
        if (Phase == 4)
        {
            if (State != TEXT("CollectionState:Loaded:/Engine/BasicShapes/Sphere.Sphere") ||
                FCString::Atoi(*Component->ActiveGeneration) <= ReloadGeneration)
            {
                return false;
            }
            Test.TestEqual(TEXT("Reload keeps the collection-selected Sphere preview"), S.PreviewPath(), FString(TEXT("/Engine/BasicShapes/Sphere.Sphere")));
            Test.TestEqual(TEXT("Reload keeps one collection preview"), S.PreviewCount(), 1);
            Test.TestTrue(TEXT("Collection script World cleanup is observed"), S.FinishWorld());
            Scenario.Reset();
            return true;
        }
        return false;
    }
};
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareCollectionStateGameplayTest,
    "Finare.Unreal.ScriptClass.CollectionStateGameplay",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareCollectionStateGameplayTest::RunTest(const FString& Parameters)
{
    using namespace UE::Finare::CollectionStateGameplayTests;
    TSharedPtr<FScenario> Scenario = MakeShared<FScenario>(*this);
    if (!Scenario->Build())
    {
        return false;
    }
    ADD_LATENT_AUTOMATION_COMMAND(FRun(Scenario, *this));
    return true;
}

#endif
