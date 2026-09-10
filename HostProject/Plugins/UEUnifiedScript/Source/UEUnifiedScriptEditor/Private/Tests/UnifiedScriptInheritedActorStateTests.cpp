#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptBlueprintClassMaterializer.h"

#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/Engine.h"
#include "EngineUtils.h"
#include "Engine/World.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "GameFramework/Actor.h"
#include "HAL/FileManager.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "PackageTools.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UnifiedScriptActor.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptAuthoringActivationScope.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "UObject/Package.h"
#include "UObject/ObjectKey.h"
#include "UObject/SavePackage.h"
#include "UObject/StrongObjectPtr.h"
#include "UObject/UnrealType.h"

namespace UE::UnifiedScript::InheritedActorStateTests
{
struct FFixtureConfig
{
    FString FixtureId;
    FString PackageDirectory;
    FString ActorClassPath;
    FString ScriptClassId;
    FString ScriptTypeName;
    FString DeclarationAssetPath;
    FString DeclarationPath;
    FString ChunkPath;
    FString AssemblyPath;
};

struct FState
{
    int32 Seed = 0;
    TArray<int32> Values;
    TSet<int32> Enabled;
    TMap<int32, int32> Weights;
    int32 TickCount = 0;
};

struct FWorldFixture
{
    UWorld* World = nullptr;
    FWorldContext* Context = nullptr;
    AUnifiedScriptActor* Actor = nullptr;
    UUnifiedScriptComponent* Component = nullptr;
    UUnifiedScriptWorldSubsystem* Host = nullptr;
    int32 Generation = 0;
};

bool ReadConfig(const FString& Path, FFixtureConfig& Out, FString& OutError)
{
    FString Json;
    if (!FFileHelper::LoadFileToString(Json, *Path))
    {
        OutError = FString::Printf(TEXT("Could not read fixture config '%s'."), *Path);
        return false;
    }
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutError = TEXT("Inherited-state fixture config is invalid JSON.");
        return false;
    }
    FString Schema;
    if (!Root->TryGetStringField(TEXT("schemaVersion"), Schema) ||
        Schema != TEXT("unifiedscript.blueprint-inherited-state-fixture/v0.1") ||
        !Root->TryGetStringField(TEXT("fixtureId"), Out.FixtureId) ||
        !Root->TryGetStringField(TEXT("packageDirectory"), Out.PackageDirectory) ||
        !Root->TryGetStringField(TEXT("actorClassPath"), Out.ActorClassPath) ||
        !Root->TryGetStringField(TEXT("scriptClassId"), Out.ScriptClassId) ||
        !Root->TryGetStringField(TEXT("scriptTypeName"), Out.ScriptTypeName) ||
        !Root->TryGetStringField(TEXT("declarationAssetPath"), Out.DeclarationAssetPath) ||
        !Root->TryGetStringField(TEXT("declarationPath"), Out.DeclarationPath) ||
        !Root->TryGetStringField(TEXT("chunkPath"), Out.ChunkPath) ||
        !Root->TryGetStringField(TEXT("assemblyPath"), Out.AssemblyPath))
    {
        OutError = TEXT("Inherited-state fixture config is missing a required field.");
        return false;
    }
    FGuid FixtureGuid;
    const FString ExpectedPackageDirectory =
        FString::Printf(TEXT("/Game/UnifiedScriptInheritedState_%s"), *Out.FixtureId);
    const FString ExpectedActorClassPath = ExpectedPackageDirectory +
        TEXT("/InheritedStateBase.InheritedStateBase_C");
    if (Out.FixtureId.Len() != 32 ||
        !FGuid::ParseExact(Out.FixtureId, EGuidFormats::Digits, FixtureGuid) ||
        !Out.FixtureId.Equals(Out.FixtureId.ToUpper(), ESearchCase::CaseSensitive) ||
        !Out.PackageDirectory.Equals(ExpectedPackageDirectory, ESearchCase::CaseSensitive) ||
        !Out.ActorClassPath.Equals(ExpectedActorClassPath, ESearchCase::CaseSensitive) ||
        !Out.ScriptClassId.StartsWith(TEXT("us_")) ||
        !FPaths::FileExists(Out.DeclarationPath) ||
        !FPaths::FileExists(Out.ChunkPath) ||
        !FPaths::FileExists(Out.AssemblyPath))
    {
        OutError = TEXT("Inherited-state fixture config has invalid identity or missing program files.");
        return false;
    }
    return true;
}

bool ParseScriptClassId(const FString& Value, FGuid& OutId)
{
    return Value.StartsWith(TEXT("us_")) &&
        FGuid::ParseExact(Value.Mid(3), EGuidFormats::Digits, OutId);
}

UUnifiedScriptBlueprint* CreateScriptBlueprint(
    UClass* Parent,
    const FString& PackageName,
    const FString& AssetName,
    UPackage*& OutPackage)
{
    OutPackage = CreatePackage(*PackageName);
    return Cast<UUnifiedScriptBlueprint>(FKismetEditorUtilities::CreateBlueprint(
        Parent,
        OutPackage,
        FName(*AssetName),
        BPTYPE_Normal,
        UUnifiedScriptBlueprint::StaticClass(),
        UUnifiedScriptBlueprintGeneratedClass::StaticClass(),
        FName(TEXT("UnifiedScriptInheritedActorState"))));
}

UBlueprint* CreateOrdinaryBlueprint(
    UClass* Parent,
    const FString& PackageName,
    const FString& AssetName,
    UPackage*& OutPackage)
{
    OutPackage = CreatePackage(*PackageName);
    return FKismetEditorUtilities::CreateBlueprint(
        Parent,
        OutPackage,
        FName(*AssetName),
        BPTYPE_Normal,
        UBlueprint::StaticClass(),
        UBlueprintGeneratedClass::StaticClass(),
        FName(TEXT("UnifiedScriptInheritedActorState")));
}

void SetArray(const FArrayProperty& Property, UObject& Object, TConstArrayView<int32> Values)
{
    FScriptArrayHelper Helper(&Property, Property.ContainerPtrToValuePtr<void>(&Object));
    Helper.EmptyValues();
    const FIntProperty* Inner = CastFieldChecked<FIntProperty>(Property.Inner);
    for (const int32 Value : Values)
    {
        const int32 Index = Helper.AddValue();
        Inner->SetPropertyValue(Helper.GetRawPtr(Index), Value);
    }
}

TArray<int32> GetArray(const FArrayProperty& Property, const UObject& Object)
{
    TArray<int32> Result;
    FScriptArrayHelper Helper(
        &Property,
        Property.ContainerPtrToValuePtr<void>(const_cast<UObject*>(&Object)));
    const FIntProperty* Inner = CastFieldChecked<FIntProperty>(Property.Inner);
    for (int32 Index = 0; Index < Helper.Num(); ++Index)
    {
        Result.Add(Inner->GetPropertyValue(Helper.GetRawPtr(Index)));
    }
    return Result;
}

void SetSet(const FSetProperty& Property, UObject& Object, std::initializer_list<int32> Values)
{
    FScriptSetHelper Helper(&Property, Property.ContainerPtrToValuePtr<void>(&Object));
    Helper.EmptyElements();
    const FIntProperty* Element = CastFieldChecked<FIntProperty>(Property.ElementProp);
    for (const int32 Value : Values)
    {
        const int32 Index = Helper.AddDefaultValue_Invalid_NeedsRehash();
        Element->SetPropertyValue(Helper.GetElementPtr(Index), Value);
    }
    Helper.Rehash();
}

TSet<int32> GetSet(const FSetProperty& Property, const UObject& Object)
{
    TSet<int32> Result;
    FScriptSetHelper Helper(
        &Property,
        Property.ContainerPtrToValuePtr<void>(const_cast<UObject*>(&Object)));
    const FIntProperty* Element = CastFieldChecked<FIntProperty>(Property.ElementProp);
    for (int32 Index = 0; Index < Helper.GetMaxIndex(); ++Index)
    {
        if (Helper.IsValidIndex(Index))
        {
            Result.Add(Element->GetPropertyValue(Helper.GetElementPtr(Index)));
        }
    }
    return Result;
}

void SetMap(const FMapProperty& Property, UObject& Object, std::initializer_list<TPair<int32, int32>> Values)
{
    FScriptMapHelper Helper(&Property, Property.ContainerPtrToValuePtr<void>(&Object));
    Helper.EmptyValues();
    const FIntProperty* Key = CastFieldChecked<FIntProperty>(Property.KeyProp);
    const FIntProperty* Value = CastFieldChecked<FIntProperty>(Property.ValueProp);
    for (const TPair<int32, int32>& Pair : Values)
    {
        const int32 Index = Helper.AddDefaultValue_Invalid_NeedsRehash();
        Key->SetPropertyValue(Helper.GetKeyPtr(Index), Pair.Key);
        Value->SetPropertyValue(Helper.GetValuePtr(Index), Pair.Value);
    }
    Helper.Rehash();
}

TMap<int32, int32> GetMap(const FMapProperty& Property, const UObject& Object)
{
    TMap<int32, int32> Result;
    FScriptMapHelper Helper(
        &Property,
        Property.ContainerPtrToValuePtr<void>(const_cast<UObject*>(&Object)));
    const FIntProperty* Key = CastFieldChecked<FIntProperty>(Property.KeyProp);
    const FIntProperty* Value = CastFieldChecked<FIntProperty>(Property.ValueProp);
    for (int32 Index = 0; Index < Helper.GetMaxIndex(); ++Index)
    {
        if (Helper.IsValidIndex(Index))
        {
            Result.Add(
                Key->GetPropertyValue(Helper.GetKeyPtr(Index)),
                Value->GetPropertyValue(Helper.GetValuePtr(Index)));
        }
    }
    return Result;
}

bool SameArray(const TArray<int32>& Actual, TConstArrayView<int32> Expected)
{
    if (Actual.Num() != Expected.Num())
    {
        return false;
    }
    for (int32 Index = 0; Index < Actual.Num(); ++Index)
    {
        if (Actual[Index] != Expected[Index])
        {
            return false;
        }
    }
    return true;
}

bool SameFilename(const FString& Left, const FString& Right)
{
    FString NormalizedLeft = Left;
    FString NormalizedRight = Right;
    FPaths::NormalizeFilename(NormalizedLeft);
    FPaths::NormalizeFilename(NormalizedRight);
    return NormalizedLeft.Equals(NormalizedRight, ESearchCase::CaseSensitive);
}

int32 Signature(const FState& State)
{
    int32 Result = State.Seed * 17;
    for (int32 Index = 0; Index < State.Values.Num(); ++Index)
    {
        Result += State.Values[Index] * (Index + 1);
    }
    for (int32 Value : State.Enabled)
    {
        Result += Value * 3;
    }
    for (const TPair<int32, int32>& Pair : State.Weights)
    {
        Result += Pair.Key * 5 + Pair.Value * 7 + Pair.Key * Pair.Value * 11;
    }
    return Result;
}

bool ReadState(UObject& Object, FState& Out)
{
    const UClass* Class = Object.GetClass();
    const FIntProperty* Seed = FindFProperty<FIntProperty>(Class, TEXT("Seed"));
    const FArrayProperty* Values = FindFProperty<FArrayProperty>(Class, TEXT("Values"));
    const FSetProperty* Enabled = FindFProperty<FSetProperty>(Class, TEXT("Enabled"));
    const FMapProperty* Weights = FindFProperty<FMapProperty>(Class, TEXT("Weights"));
    const FIntProperty* TickCount = FindFProperty<FIntProperty>(Class, TEXT("TickCount"));
    if (Seed == nullptr || Values == nullptr || Enabled == nullptr || Weights == nullptr || TickCount == nullptr)
    {
        return false;
    }
    Out.Seed = Seed->GetPropertyValue_InContainer(&Object);
    Out.Values = GetArray(*Values, Object);
    Out.Enabled = GetSet(*Enabled, Object);
    Out.Weights = GetMap(*Weights, Object);
    Out.TickCount = TickCount->GetPropertyValue_InContainer(&Object);
    return true;
}

bool CheckState(
    UObject& Object,
    const FState& Expected,
    FAutomationTestBase& Test,
    const TCHAR* Label)
{
    FState Actual;
    if (!Test.TestTrue(
            *FString::Printf(TEXT("%s exposes inherited state properties"), Label),
            ReadState(Object, Actual)))
    {
        return false;
    }
    const bool bValues = Actual.Values == Expected.Values;
    const bool bEnabled = Actual.Enabled.Num() == Expected.Enabled.Num() &&
        Actual.Enabled.Includes(Expected.Enabled);
    bool bWeights = Actual.Weights.Num() == Expected.Weights.Num();
    for (const TPair<int32, int32>& Pair : Expected.Weights)
    {
        bWeights &= Actual.Weights.FindRef(Pair.Key) == Pair.Value;
    }
    Test.TestEqual(*FString::Printf(TEXT("%s Seed"), Label), Actual.Seed, Expected.Seed);
    Test.TestTrue(*FString::Printf(TEXT("%s Values"), Label), bValues);
    Test.TestTrue(*FString::Printf(TEXT("%s Enabled"), Label), bEnabled);
    Test.TestTrue(*FString::Printf(TEXT("%s Weights"), Label), bWeights);
    Test.TestEqual(*FString::Printf(TEXT("%s TickCount"), Label), Actual.TickCount, Expected.TickCount);
    return Actual.Seed == Expected.Seed && bValues && bEnabled && bWeights && Actual.TickCount == Expected.TickCount;
}

bool ConfigureAndCheckCdo(
    UClass& Class,
    UUnifiedScriptAsset& ScriptAsset,
    UObject* ExpectedImmediateArchetype,
    const FState& Expected,
    FAutomationTestBase& Test,
    const TCHAR* Label)
{
    AUnifiedScriptActor* Cdo = Cast<AUnifiedScriptActor>(Class.GetDefaultObject());
    if (!Test.TestNotNull(*FString::Printf(TEXT("%s CDO exists"), Label), Cdo))
    {
        return false;
    }
    UUnifiedScriptComponent* Component = Cdo->GetUnifiedScriptComponent();
    if (!Test.TestNotNull(*FString::Printf(TEXT("%s has UnifiedScript component"), Label), Component))
    {
        return false;
    }
    bool bValid = Test.TestEqual(
        *FString::Printf(TEXT("%s component has the stable name"), Label),
        Component->GetFName(),
        FName(TEXT("UnifiedScript")));
    bValid &= Test.TestTrue(*FString::Printf(TEXT("%s component is owned by its CDO"), Label), Component->GetOuter() == Cdo);
    const bool bImmediateArchetype = Component->GetArchetype() == ExpectedImmediateArchetype;
    if (!bImmediateArchetype)
    {
        Test.AddError(FString::Printf(
            TEXT("%s immediate archetype mismatch: actual=%s expected=%s"),
            Label,
            Component->GetArchetype() != nullptr ? *Component->GetArchetype()->GetPathName() : TEXT("<null>"),
            ExpectedImmediateArchetype != nullptr ? *ExpectedImmediateArchetype->GetPathName() : TEXT("<null>")));
    }
    bValid &= Test.TestTrue(
        *FString::Printf(TEXT("%s component has its immediate archetype"), Label),
        bImmediateArchetype);
    bValid &= Test.TestNull(
        *FString::Printf(TEXT("%s excludes the unexposed helper property"), Label),
        FindFProperty<FProperty>(&Class, TEXT("UnexposedHelper")));
    bValid &= Test.TestTrue(*FString::Printf(TEXT("%s ScriptAsset is inherited"), Label), Component->ScriptAsset == &ScriptAsset);
    bValid &= Test.TestTrue(*FString::Printf(TEXT("%s starts automatically"), Label), Component->bStartAutomatically);
    bValid &= Test.TestTrue(*FString::Printf(TEXT("%s ticks automatically"), Label), Component->bTickAutomatically);
    return bValid && CheckState(*Cdo, Expected, Test, Label);
}

bool CheckInstanceComponent(
    AUnifiedScriptActor& Actor,
    UUnifiedScriptAsset& ScriptAsset,
    UObject* ExpectedImmediateArchetype,
    FAutomationTestBase& Test,
    const TCHAR* Label)
{
    UUnifiedScriptComponent* Component = Actor.GetUnifiedScriptComponent();
    if (!Test.TestNotNull(*FString::Printf(TEXT("%s instance has UnifiedScript component"), Label), Component))
    {
        return false;
    }
    bool bValid = Test.TestEqual(
        *FString::Printf(TEXT("%s instance component has the stable name"), Label),
        Component->GetFName(),
        FName(TEXT("UnifiedScript")));
    bValid &= Test.TestTrue(
        *FString::Printf(TEXT("%s instance component has the immediate archetype"), Label),
        Component->GetArchetype() == ExpectedImmediateArchetype);
    bValid &= Test.TestTrue(
        *FString::Printf(TEXT("%s instance keeps exact ScriptAsset"), Label),
        Component->ScriptAsset == &ScriptAsset);
    bValid &= Test.TestTrue(
        *FString::Printf(TEXT("%s instance starts automatically"), Label),
        Component->bStartAutomatically);
    bValid &= Test.TestTrue(
        *FString::Printf(TEXT("%s instance ticks automatically"), Label),
        Component->bTickAutomatically);
    return bValid;
}

bool MakeWorld(FWorldFixture& Out, FString& OutError)
{
    Out.World = UWorld::CreateWorld(EWorldType::Game, false);
    if (Out.World == nullptr || GEngine == nullptr)
    {
        OutError = TEXT("Could not create inherited-state World or engine context.");
        return false;
    }
    Out.World->AddToRoot();
    Out.Context = &GEngine->CreateNewWorldContext(EWorldType::Game);
    Out.Context->SetCurrentWorld(Out.World);
    Out.World->InitializeActorsForPlay(FURL());
    Out.Host = Out.World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    if (Out.Host == nullptr)
    {
        OutError = TEXT("Inherited-state World did not create its host subsystem.");
        return false;
    }
    return true;
}

void DestroyActor(FWorldFixture& Fixture)
{
    if (Fixture.Actor != nullptr && !Fixture.Actor->IsActorBeingDestroyed())
    {
        if (Fixture.Actor->HasActorBegunPlay())
        {
            Fixture.Actor->RouteEndPlay(EEndPlayReason::Destroyed);
        }
        Fixture.Actor->Destroy();
    }
    Fixture.Component = nullptr;
    Fixture.Actor = nullptr;
}

void DestroyWorld(FWorldFixture& Fixture)
{
    DestroyActor(Fixture);
    Fixture.Host = nullptr;
    if (Fixture.Context != nullptr && GEngine != nullptr)
    {
        GEngine->DestroyWorldContext(Fixture.World);
        Fixture.Context = nullptr;
    }
    if (Fixture.World != nullptr)
    {
        Fixture.World->RemoveFromRoot();
        Fixture.World->DestroyWorld(false);
        Fixture.World = nullptr;
    }
}

bool SavePackage(UObject& TopLevelObject, const FString& Filename, FString& OutError)
{
    FSavePackageArgs Args;
    Args.TopLevelFlags = RF_Public | RF_Standalone;
    Args.SaveFlags = SAVE_NoError;
    if (!UPackage::SavePackage(TopLevelObject.GetOutermost(), &TopLevelObject, *Filename, Args))
    {
        OutError = FString::Printf(TEXT("Failed to save inherited-state package '%s'."), *Filename);
        return false;
    }
    return true;
}

bool UnloadByName(const TArray<FString>& PackageNames)
{
    TArray<UPackage*> Packages;
    for (const FString& Name : PackageNames)
    {
        if (UPackage* Package = FindPackage(nullptr, *Name))
        {
            Package->SetDirtyFlag(false);
            Packages.Add(Package);
        }
    }
    if (Packages.Num() > 0)
    {
        return UPackageTools::UnloadPackages(Packages);
    }
    return true;
}

FString CookStageDirectory(const FFixtureConfig& Config)
{
    return FPaths::ConvertRelativePathToFull(FPaths::Combine(
        FPaths::ProjectDir(),
        TEXT("Plugins/UEUnifiedScript/Binaries/Managed/InheritedState"),
        Config.FixtureId));
}

FString CookStagePath(const FFixtureConfig& Config, const TCHAR* LeafName)
{
    return FPaths::Combine(CookStageDirectory(Config), LeafName);
}

FString CookRuntimePath(const FFixtureConfig& Config, const TCHAR* LeafName)
{
    return FString::Printf(
        TEXT("{PluginDir}/Binaries/Managed/InheritedState/%s/%s"),
        *Config.FixtureId,
        LeafName);
}

bool WriteCookReceipt(
    const FString& ReceiptPath,
    const FFixtureConfig& Config,
    const TArray<FString>& PackageNames,
    const TArray<FString>& PackageFilenames,
    const FString& BaseClassPath,
    const FString& ChildClassPath,
    const FString& GrandchildClassPath,
    const FString& ScriptAssetPath,
    FString& OutError)
{
    if (PackageNames.Num() != 4 || PackageFilenames.Num() != 4)
    {
        OutError = TEXT("Cook receipt requires exactly four inherited-state packages.");
        return false;
    }
    TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("schemaVersion"), TEXT("unifiedscript.inherited-state-cook-fixture/v0.1"));
    Root->SetStringField(TEXT("fixtureId"), Config.FixtureId);
    Root->SetStringField(TEXT("packageDirectory"), Config.PackageDirectory);
    Root->SetStringField(TEXT("scriptClassId"), Config.ScriptClassId);
    Root->SetStringField(TEXT("scriptTypeName"), Config.ScriptTypeName);
    Root->SetStringField(TEXT("stageDirectory"),
        FString::Printf(TEXT("{PluginDir}/Binaries/Managed/InheritedState/%s"), *Config.FixtureId));
    Root->SetStringField(TEXT("baseClassPath"), BaseClassPath);
    Root->SetStringField(TEXT("childClassPath"), ChildClassPath);
    Root->SetStringField(TEXT("grandchildClassPath"), GrandchildClassPath);
    Root->SetStringField(TEXT("scriptAssetPath"), ScriptAssetPath);
    TArray<TSharedPtr<FJsonValue>> Packages;
    for (int32 Index = 0; Index < PackageNames.Num(); ++Index)
    {
        TSharedRef<FJsonObject> Package = MakeShared<FJsonObject>();
        Package->SetStringField(TEXT("packageName"), PackageNames[Index]);
        Package->SetStringField(TEXT("packageFilename"), FPaths::ConvertRelativePathToFull(PackageFilenames[Index]));
        Packages.Add(MakeShared<FJsonValueObject>(Package));
    }
    Root->SetArrayField(TEXT("packages"), Packages);
    FString Json;
    const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Json);
    if (!FJsonSerializer::Serialize(Root, Writer) ||
        !FFileHelper::SaveStringToFile(
            Json + TEXT("\n"),
            *ReceiptPath,
            FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
    {
        OutError = FString::Printf(TEXT("Could not write inherited-state cook receipt '%s'."), *ReceiptPath);
        return false;
    }
    return true;
}

FString WithSelectedAssetBaseClassPath(
    const FString& Source,
    const FString& SelectedAssetPath,
    const FString& ParentPath)
{
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Source);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        return FString();
    }
    const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
    if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr)
    {
        return FString();
    }
    TSharedPtr<FJsonObject> SelectedAsset;
    for (const TSharedPtr<FJsonValue>& AssetValue : *Assets)
    {
        const TSharedPtr<FJsonObject> Asset = AssetValue.IsValid() ? AssetValue->AsObject() : nullptr;
        FString AssetPath;
        if (Asset.IsValid() && Asset->TryGetStringField(TEXT("assetPath"), AssetPath) &&
            AssetPath.Equals(SelectedAssetPath, ESearchCase::CaseSensitive))
        {
            SelectedAsset = Asset;
            break;
        }
    }
    if (!SelectedAsset.IsValid())
    {
        return FString();
    }
    const TSharedPtr<FJsonObject> Binding = SelectedAsset->GetObjectField(TEXT("classBinding"));
    if (!Binding.IsValid())
    {
        return FString();
    }
    Binding->SetStringField(TEXT("baseClassPath"), ParentPath);
    FString Result;
    const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Result);
    return FJsonSerializer::Serialize(Root.ToSharedRef(), Writer) ? Result : FString();
}
} // namespace UE::UnifiedScript::InheritedActorStateTests

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptInheritedActorStateRuntimeTest,
    "UnifiedScript.Unreal.ScriptClass.InheritedActorStateRuntime",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptInheritedActorStateRuntimeTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::InheritedActorStateTests;
    FAutomationTestBase& Test = *this;

    FString ConfigPath;
    if (!FParse::Value(FCommandLine::Get(), TEXT("-UnifiedScriptInheritedStateFixture="), ConfigPath))
    {
        ConfigPath = FPaths::Combine(
            FPaths::ProjectDir(),
            TEXT("../artifacts/m13-3c-3-inherited-runtime/fixture/BlueprintInheritedStateFixture.config.json"));
    }
    ConfigPath = FPaths::ConvertRelativePathToFull(ConfigPath);

    FString CookReceiptPath;
    const bool bCookFixture = FParse::Value(
        FCommandLine::Get(),
        TEXT("-UnifiedScriptInheritedStateCookReceipt="),
        CookReceiptPath);
    CookReceiptPath.TrimQuotesInline();
    if (bCookFixture)
    {
        CookReceiptPath = FPaths::ConvertRelativePathToFull(CookReceiptPath);
    }
    bool bKeepCookFixture = false;

    FFixtureConfig Config;
    FString Error;
    if (!TestTrue(TEXT("Inherited-state fixture config is valid"), ReadConfig(ConfigPath, Config, Error)))
    {
        AddError(Error);
        return false;
    }
    FString Declaration;
    if (!TestTrue(TEXT("Inherited-state declaration is readable"),
            FFileHelper::LoadFileToString(Declaration, *Config.DeclarationPath)))
    {
        return false;
    }
    FGuid ClassId;
    if (!TestTrue(TEXT("Inherited-state ScriptClassId is valid"), ParseScriptClassId(Config.ScriptClassId, ClassId)))
    {
        return false;
    }

    const FString PackagePrefix = Config.PackageDirectory;
    const FString BasePackageName = PackagePrefix + TEXT("/InheritedStateBase");
    const FString ChildPackageName = PackagePrefix + TEXT("/InheritedStateChild");
    const FString GrandchildPackageName = PackagePrefix + TEXT("/InheritedStateGrandchild");
    const FString ScriptAssetPackageName = PackagePrefix + TEXT("/InheritedStateScriptAsset");
    const FString BaseName = TEXT("InheritedStateBase");
    const FString ChildName = TEXT("InheritedStateChild");
    const FString GrandchildName = TEXT("InheritedStateGrandchild");
    const FString ScriptAssetName = TEXT("InheritedStateScriptAsset");
    const FString BaseFilename = FPackageName::LongPackageNameToFilename(
        BasePackageName, FPackageName::GetAssetPackageExtension());
    const FString ChildFilename = FPackageName::LongPackageNameToFilename(
        ChildPackageName, FPackageName::GetAssetPackageExtension());
    const FString GrandchildFilename = FPackageName::LongPackageNameToFilename(
        GrandchildPackageName, FPackageName::GetAssetPackageExtension());
    const FString ScriptAssetFilename = FPackageName::LongPackageNameToFilename(
        ScriptAssetPackageName, FPackageName::GetAssetPackageExtension());
    const TArray<FString> PackageNames{
        BasePackageName,
        ChildPackageName,
        GrandchildPackageName,
        ScriptAssetPackageName};
    const TArray<FString> PackageFilenames{
        BaseFilename,
        ChildFilename,
        GrandchildFilename,
        ScriptAssetFilename};

    UPackage* BasePackage = nullptr;
    UPackage* ChildPackage = nullptr;
    UPackage* GrandchildPackage = nullptr;
    UPackage* ScriptAssetPackage = nullptr;
    UUnifiedScriptBlueprint* BaseBlueprint = nullptr;
    UBlueprint* ChildBlueprint = nullptr;
    UBlueprint* GrandchildBlueprint = nullptr;
    UUnifiedScriptAsset* ScriptAsset = nullptr;
    FWorldFixture First;
    FWorldFixture Second;
    const int32 InitialWorldCount = FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();
    const FString CookChunkPath = CookStagePath(Config, TEXT("BlueprintInheritedStateFixture.uschunk"));
    const FString CookAssemblyPath = CookStagePath(Config, TEXT("BlueprintInheritedStateFixture.dll"));
    const FString CookDeclarationPath = CookStagePath(Config, TEXT("BlueprintInheritedStateFixture.asset.json"));
    const FString RuntimeChunkPath = bCookFixture ? CookRuntimePath(Config, TEXT("BlueprintInheritedStateFixture.uschunk")) : Config.ChunkPath;
    const FString RuntimeAssemblyPath = bCookFixture ? CookRuntimePath(Config, TEXT("BlueprintInheritedStateFixture.dll")) : Config.AssemblyPath;
    const FString RuntimeDeclarationPath = bCookFixture ? CookRuntimePath(Config, TEXT("BlueprintInheritedStateFixture.asset.json")) : Config.DeclarationPath;
    if (bCookFixture &&
        (!FPaths::FileExists(CookChunkPath) ||
         !FPaths::FileExists(CookAssemblyPath) ||
         !FPaths::FileExists(CookDeclarationPath)))
    {
        AddError(FString::Printf(TEXT("Cook fixture stage is missing one or more files below '%s'."), *CookStageDirectory(Config)));
        return false;
    }

    ON_SCOPE_EXIT
    {
        DestroyWorld(First);
        DestroyWorld(Second);
        CollectGarbage(RF_NoFlags);
        BaseBlueprint = nullptr;
        ChildBlueprint = nullptr;
        GrandchildBlueprint = nullptr;
        ScriptAsset = nullptr;
        if (!bKeepCookFixture)
        {
            Test.TestTrue(TEXT("Unloads all inherited-state packages during cleanup"), UnloadByName(PackageNames));
            CollectGarbage(RF_NoFlags);
            for (const FString& Filename : PackageFilenames)
            {
                if (IFileManager::Get().FileExists(*Filename))
                {
                    Test.TestTrue(
                        *FString::Printf(TEXT("Deletes inherited-state package %s"), *Filename),
                        IFileManager::Get().Delete(*Filename, false, true, true));
                }
                Test.TestFalse(
                    *FString::Printf(TEXT("Inherited-state package file is absent %s"), *Filename),
                    IFileManager::Get().FileExists(*Filename));
            }
            const FString FixtureDirectory = FPackageName::LongPackageNameToFilename(
                PackagePrefix, TEXT(""));
            IFileManager::Get().DeleteDirectory(*FixtureDirectory, false, false);
        }
        Test.TestEqual(
            TEXT("Inherited-state World hosts return to baseline"),
            FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
            InitialWorldCount);
    };

    BaseBlueprint = CreateScriptBlueprint(
        AUnifiedScriptActor::StaticClass(),
        BasePackageName,
        BaseName,
        BasePackage);
    if (!Test.TestNotNull(TEXT("Creates inherited-state base Blueprint"), BaseBlueprint))
    {
        return false;
    }
    if (!Test.TestTrue(TEXT("Base generated class uses the configured exact path"),
            FSoftClassPath(BaseBlueprint->GeneratedClass).ToString().Equals(
                Config.ActorClassPath,
                ESearchCase::CaseSensitive)))
    {
        return false;
    }
    ScriptAssetPackage = CreatePackage(*ScriptAssetPackageName);
    ScriptAsset = NewObject<UUnifiedScriptAsset>(
        ScriptAssetPackage,
        *ScriptAssetName,
        RF_Public | RF_Standalone);
    ScriptAsset->ScriptClassId = ClassId;
    ScriptAsset->ScriptClassKind = EUnifiedScriptClassKind::Actor;
    ScriptAsset->GeneratedClassPath = FSoftClassPath(BaseBlueprint->GeneratedClass);
    ScriptAsset->CompiledChunkPath = RuntimeChunkPath;
    ScriptAsset->ScriptAssemblyPath = RuntimeAssemblyPath;
    ScriptAsset->ScriptTypeName = Config.ScriptTypeName;
    ScriptAsset->DeclarationPath = RuntimeDeclarationPath;
    ScriptAsset->DeclarationAssetPath = Config.DeclarationAssetPath;
    BaseBlueprint->ScriptAsset = ScriptAsset;
    AUnifiedScriptActor* BaseCdo = Cast<AUnifiedScriptActor>(BaseBlueprint->GeneratedClass->GetDefaultObject());
    UUnifiedScriptComponent* BaseComponent = BaseCdo != nullptr
        ? BaseCdo->GetUnifiedScriptComponent()
        : nullptr;
    if (!Test.TestNotNull(TEXT("Base CDO has the native UnifiedScript component"), BaseComponent))
    {
        return false;
    }
    FUnifiedScriptBlueprintClassSyncResult SyncResult;
    if (!Test.TestTrue(*FString::Printf(TEXT("Inherited-state declaration materializes: %s"), *Error),
            FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
                *ScriptAsset, *BaseBlueprint, Declaration, false, SyncResult, Error)))
    {
        AddError(Error);
        return false;
    }
    FKismetEditorUtilities::CompileBlueprint(BaseBlueprint);
    if (!Test.TestEqual(TEXT("Base Blueprint compiles"), BaseBlueprint->Status, BS_UpToDate))
    {
        return false;
    }

    // Configuration belongs to the post-materialization generated CDO.  This
    // is the same authoring path used by the class factory and updates the
    // generated-class default cache before ordinary child blueprints exist.
    BaseCdo = Cast<AUnifiedScriptActor>(BaseBlueprint->GeneratedClass->GetDefaultObject());
    BaseComponent = BaseCdo != nullptr ? BaseCdo->GetUnifiedScriptComponent() : nullptr;
    if (!Test.TestNotNull(TEXT("Re-resolved base CDO has the native UnifiedScript component"), BaseComponent))
    {
        return false;
    }
    BaseComponent->ScriptAsset = ScriptAsset;
    BaseBlueprint->ScriptAsset = ScriptAsset;
    BaseComponent->bStartAutomatically = true;
    BaseComponent->bTickAutomatically = true;
    BaseBlueprint->MarkPackageDirty();
    FBlueprintEditorUtils::MarkBlueprintAsModified(BaseBlueprint);

    FState BaseState;
    BaseState.Seed = 3;
    BaseState.Values = {2, 4};
    BaseState.Enabled = {5, 7};
    BaseState.Weights = {{11, 13}, {17, 19}};
    AUnifiedScriptActor* NativeCdo = Cast<AUnifiedScriptActor>(
        AUnifiedScriptActor::StaticClass()->GetDefaultObject());
    UUnifiedScriptComponent* NativeComponent = NativeCdo != nullptr
        ? NativeCdo->GetUnifiedScriptComponent()
        : nullptr;
    if (!Test.TestTrue(TEXT("Base CDO keeps configured component defaults"),
            NativeComponent != nullptr &&
                ConfigureAndCheckCdo(
                    *BaseBlueprint->GeneratedClass.Get(),
                    *ScriptAsset,
                    NativeComponent,
                    BaseState,
                    *this,
                    TEXT("Base CDO"))))
    {
        return false;
    }

    ChildBlueprint = CreateOrdinaryBlueprint(
        BaseBlueprint->GeneratedClass,
        ChildPackageName,
        ChildName,
        ChildPackage);
    GrandchildBlueprint = nullptr;
    if (!Test.TestNotNull(TEXT("Creates inherited-state child Blueprint"), ChildBlueprint))
    {
        return false;
    }
    FKismetEditorUtilities::CompileBlueprint(ChildBlueprint);
    UClass* ChildClass = ChildBlueprint->GeneratedClass;
    if (!Test.TestNotNull(TEXT("Child generated class exists"), ChildClass))
    {
        return false;
    }
    // Ordinary child compilation can reinstance the parent generated class.
    // Resolve the current base CDO/component before using it as the child's
    // immediate archetype.
    BaseCdo = Cast<AUnifiedScriptActor>(BaseBlueprint->GeneratedClass->GetDefaultObject());
    BaseComponent = BaseCdo != nullptr ? BaseCdo->GetUnifiedScriptComponent() : nullptr;
    if (!Test.TestNotNull(TEXT("Re-resolved base CDO after child compile"), BaseComponent))
    {
        return false;
    }
    const FIntProperty* ChildSeed = FindFProperty<FIntProperty>(ChildClass, TEXT("Seed"));
    const FArrayProperty* ChildValues = FindFProperty<FArrayProperty>(ChildClass, TEXT("Values"));
    const FSetProperty* ChildEnabled = FindFProperty<FSetProperty>(ChildClass, TEXT("Enabled"));
    const FMapProperty* ChildWeights = FindFProperty<FMapProperty>(ChildClass, TEXT("Weights"));
    if (!Test.TestNotNull(TEXT("Child inherits Seed"), ChildSeed) ||
        !Test.TestNotNull(TEXT("Child inherits Values"), ChildValues) ||
        !Test.TestNotNull(TEXT("Child inherits Enabled"), ChildEnabled) ||
        !Test.TestNotNull(TEXT("Child inherits Weights"), ChildWeights))
    {
        return false;
    }
    FState ChildState;
    ChildState.Seed = 9;
    ChildState.Values = {3, 8, 13};
    ChildState.Enabled = {4, 10};
    ChildState.Weights = {{2, 5}, {7, 11}};
    ChildSeed->SetPropertyValue_InContainer(ChildClass->GetDefaultObject(), ChildState.Seed);
    SetArray(*ChildValues, *ChildClass->GetDefaultObject(), ChildState.Values);
    SetSet(*ChildEnabled, *ChildClass->GetDefaultObject(), {4, 10});
    SetMap(*ChildWeights, *ChildClass->GetDefaultObject(), {{2, 5}, {7, 11}});
    ChildPackage->SetDirtyFlag(true);

    if (!Test.TestTrue(TEXT("Child CDO keeps its complete inherited component"),
            ConfigureAndCheckCdo(
                *ChildClass,
                *ScriptAsset,
                BaseComponent,
                ChildState,
                *this,
                TEXT("Child CDO"))))
    {
        return false;
    }

    GrandchildBlueprint = CreateOrdinaryBlueprint(
        ChildBlueprint->GeneratedClass,
        GrandchildPackageName,
        GrandchildName,
        GrandchildPackage);
    if (!Test.TestNotNull(TEXT("Creates inherited-state grandchild Blueprint"), GrandchildBlueprint))
    {
        return false;
    }
    FKismetEditorUtilities::CompileBlueprint(GrandchildBlueprint);
    if (!Test.TestTrue(TEXT("Grandchild CDO follows its immediate child"),
            ConfigureAndCheckCdo(
                *GrandchildBlueprint->GeneratedClass.Get(),
                *ScriptAsset,
                CastChecked<AUnifiedScriptActor>(ChildBlueprint->GeneratedClass->GetDefaultObject())
                    ->GetUnifiedScriptComponent(),
                ChildState,
                *this,
                TEXT("Grandchild CDO"))))
    {
        return false;
    }

    BaseBlueprint->MarkPackageDirty();
    ChildBlueprint->MarkPackageDirty();
    GrandchildBlueprint->MarkPackageDirty();
    if (!Test.TestTrue(TEXT("Saves inherited-state base package"), SavePackage(*BaseBlueprint, BaseFilename, Error)) ||
        !Test.TestTrue(TEXT("Saves inherited-state child package"), SavePackage(*ChildBlueprint, ChildFilename, Error)) ||
        !Test.TestTrue(TEXT("Saves inherited-state grandchild package"), SavePackage(*GrandchildBlueprint, GrandchildFilename, Error)) ||
        !Test.TestTrue(TEXT("Saves inherited-state ScriptAsset package"), SavePackage(*ScriptAsset, ScriptAssetFilename, Error)))
    {
        AddError(Error);
        return false;
    }
    TWeakObjectPtr<UBlueprint> WeakBase = BaseBlueprint;
    TWeakObjectPtr<UBlueprint> WeakChild = ChildBlueprint;
    TWeakObjectPtr<UBlueprint> WeakGrandchild = GrandchildBlueprint;
    TWeakObjectPtr<UUnifiedScriptAsset> WeakAsset = ScriptAsset;
    BaseBlueprint = nullptr;
    ChildBlueprint = nullptr;
    GrandchildBlueprint = nullptr;
    ScriptAsset = nullptr;
    if (!Test.TestTrue(TEXT("Unloads inherited-state packages before reload"), UnloadByName(PackageNames)))
    {
        return false;
    }
    CollectGarbage(RF_NoFlags);
    Test.TestFalse(TEXT("Saved base package unloads"), WeakBase.IsValid());
    Test.TestFalse(TEXT("Saved child package unloads"), WeakChild.IsValid());
    Test.TestFalse(TEXT("Saved grandchild package unloads"), WeakGrandchild.IsValid());
    Test.TestFalse(TEXT("Saved ScriptAsset unloads"), WeakAsset.IsValid());

    BaseBlueprint = LoadObject<UUnifiedScriptBlueprint>(
        nullptr, *(BasePackageName + TEXT(".") + BaseName));
    ChildBlueprint = LoadObject<UBlueprint>(nullptr, *(ChildPackageName + TEXT(".") + ChildName));
    GrandchildBlueprint = LoadObject<UBlueprint>(nullptr, *(GrandchildPackageName + TEXT(".") + GrandchildName));
    ScriptAsset = LoadObject<UUnifiedScriptAsset>(
        nullptr, *(ScriptAssetPackageName + TEXT(".") + ScriptAssetName));
    if (!Test.TestNotNull(TEXT("Reloaded base Blueprint"), BaseBlueprint) ||
        !Test.TestNotNull(TEXT("Reloaded child Blueprint"), ChildBlueprint) ||
        !Test.TestNotNull(TEXT("Reloaded grandchild Blueprint"), GrandchildBlueprint) ||
        !Test.TestNotNull(TEXT("Reloaded ScriptAsset"), ScriptAsset))
    {
        return false;
    }
    if (!Test.TestTrue(TEXT("Reloaded ScriptAsset keeps exact generated class"),
            ScriptAsset->GeneratedClassPath.ToString().Equals(
                Config.ActorClassPath,
                ESearchCase::CaseSensitive)))
    {
        return false;
    }
    Test.TestTrue(
        TEXT("Reloaded ScriptAsset is a distinct public sidecar package"),
        ScriptAsset->GetOutermost()->GetName().Equals(ScriptAssetPackageName, ESearchCase::CaseSensitive) &&
            ScriptAsset->HasAnyFlags(RF_Public | RF_Standalone));
    Test.TestTrue(
        TEXT("Reloaded base Blueprint keeps exact sidecar ScriptAsset"),
        BaseBlueprint->ScriptAsset == ScriptAsset);
    BaseCdo = Cast<AUnifiedScriptActor>(BaseBlueprint->GeneratedClass->GetDefaultObject());
    BaseComponent = BaseCdo != nullptr ? BaseCdo->GetUnifiedScriptComponent() : nullptr;
    TestTrue(TEXT("Reloaded base CDO keeps ScriptAsset"), BaseComponent != nullptr && BaseComponent->ScriptAsset == ScriptAsset);
    ChildClass = ChildBlueprint->GeneratedClass;
    UClass* GrandchildClass = GrandchildBlueprint->GeneratedClass;
    if (!Test.TestTrue(TEXT("Reloaded ScriptAsset keeps its stable identity"),
            ScriptAsset->ScriptClassId == ClassId &&
                ScriptAsset->ScriptTypeName.Equals(Config.ScriptTypeName, ESearchCase::CaseSensitive)) ||
        !Test.TestTrue(TEXT("Reloaded base CDO keeps complete defaults"),
            ConfigureAndCheckCdo(
                *BaseBlueprint->GeneratedClass.Get(),
                *ScriptAsset,
                NativeComponent,
                BaseState,
                *this,
                TEXT("Reloaded base CDO"))) ||
        !Test.TestTrue(TEXT("Reloaded child keeps complete override"),
            ConfigureAndCheckCdo(
                *ChildClass,
                *ScriptAsset,
                BaseComponent,
                ChildState,
                *this,
                TEXT("Reloaded child CDO"))) ||
        !Test.TestTrue(TEXT("Reloaded grandchild follows child"),
            ConfigureAndCheckCdo(
                *GrandchildClass,
                *ScriptAsset,
                CastChecked<AUnifiedScriptActor>(ChildClass->GetDefaultObject())->GetUnifiedScriptComponent(),
                ChildState,
                *this,
                TEXT("Reloaded grandchild CDO"))))
    {
        return false;
    }
    if (!Test.TestTrue(TEXT("Reloaded base ScriptAsset configures automatic startup"),
            BaseComponent != nullptr && BaseComponent->bStartAutomatically && BaseComponent->bTickAutomatically))
    {
        return false;
    }

    FString WorldError;
    if (!Test.TestTrue(TEXT("Creates first inherited-state World"), MakeWorld(First, WorldError)) ||
        !Test.TestTrue(TEXT("Creates second inherited-state World"), MakeWorld(Second, WorldError)))
    {
        AddError(WorldError);
        return false;
    }
    Test.TestEqual(
        TEXT("Two inherited-state worlds are registered"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
        InitialWorldCount + 2);
    First.Actor = First.World->SpawnActor<AUnifiedScriptActor>(ChildClass);
    Second.Actor = Second.World->SpawnActor<AUnifiedScriptActor>(GrandchildClass);
    if (!Test.TestNotNull(TEXT("Spawns child instance in first World"), First.Actor) ||
        !Test.TestNotNull(TEXT("Spawns grandchild instance in second World"), Second.Actor))
    {
        return false;
    }
    FState FirstState;
    FirstState.Seed = 12;
    FirstState.Values = {1, 6};
    FirstState.Enabled = {8, 9};
    FirstState.Weights = {{3, 10}, {14, 20}};
    const FIntProperty* FirstSeed = FindFProperty<FIntProperty>(ChildClass, TEXT("Seed"));
    const FArrayProperty* FirstValues = FindFProperty<FArrayProperty>(ChildClass, TEXT("Values"));
    const FSetProperty* FirstEnabled = FindFProperty<FSetProperty>(ChildClass, TEXT("Enabled"));
    const FMapProperty* FirstWeights = FindFProperty<FMapProperty>(ChildClass, TEXT("Weights"));
    FirstSeed->SetPropertyValue_InContainer(First.Actor, FirstState.Seed);
    SetArray(*FirstValues, *First.Actor, FirstState.Values);
    SetSet(*FirstEnabled, *First.Actor, {8, 9});
    SetMap(*FirstWeights, *First.Actor, {{3, 10}, {14, 20}});
    if (!Test.TestTrue(TEXT("First instance keeps all explicit collection overrides"),
            CheckState(*First.Actor, FirstState, *this, TEXT("First instance"))) ||
        !Test.TestTrue(TEXT("Second instance inherits child complete state"),
            CheckState(*Second.Actor, ChildState, *this, TEXT("Second instance"))))
    {
        return false;
    }
    First.Component = First.Actor->GetUnifiedScriptComponent();
    Second.Component = Second.Actor->GetUnifiedScriptComponent();
    First.Host = First.World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    Second.Host = Second.World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    if (!CheckInstanceComponent(
            *First.Actor,
            *ScriptAsset,
            CastChecked<AUnifiedScriptActor>(ChildClass->GetDefaultObject())->GetUnifiedScriptComponent(),
            *this,
            TEXT("First inherited child")) ||
        !CheckInstanceComponent(
            *Second.Actor,
            *ScriptAsset,
            CastChecked<AUnifiedScriptActor>(GrandchildClass->GetDefaultObject())->GetUnifiedScriptComponent(),
            *this,
            TEXT("Second inherited grandchild")))
    {
        return false;
    }
    const int32 BaselineManagedCount = First.Host->GetManagedScriptCount();
    First.Actor->DispatchBeginPlay();
    Second.Actor->DispatchBeginPlay();
    if (!Test.TestTrue(TEXT("First inherited actor begins managed script"),
            First.Component != nullptr && First.Component->bScriptRunning) ||
        !Test.TestTrue(TEXT("Second inherited actor begins managed script"),
            Second.Component != nullptr && Second.Component->bScriptRunning) ||
        !Test.TestEqual(TEXT("First World owns one managed script"), First.Host->GetManagedScriptCount(), BaselineManagedCount + 1) ||
        !Test.TestEqual(TEXT("Second World owns one managed script"), Second.Host->GetManagedScriptCount(), BaselineManagedCount + 1))
    {
        return false;
    }
    const int32 FirstInitialClaims = First.Host->GetGameplayLifecycleClaimCount();
    const int32 SecondInitialClaims = Second.Host->GetGameplayLifecycleClaimCount();
    Test.TestEqual(TEXT("First World claims one inherited actor lifecycle"), FirstInitialClaims, 1);
    Test.TestEqual(TEXT("Second World claims one inherited actor lifecycle"), SecondInitialClaims, 1);
    const FString ExpectedChunkPath = bCookFixture ? CookChunkPath : Config.ChunkPath;
    if (!SameFilename(First.Component->GetActiveProgramPath(), ExpectedChunkPath))
    {
        AddError(FString::Printf(TEXT("First chunk path mismatch: actual=%s expected=%s"), *First.Component->GetActiveProgramPath(), *ExpectedChunkPath));
    }
    if (!SameFilename(Second.Component->GetActiveProgramPath(), ExpectedChunkPath))
    {
        AddError(FString::Printf(TEXT("Second chunk path mismatch: actual=%s expected=%s"), *Second.Component->GetActiveProgramPath(), *ExpectedChunkPath));
    }
    const FString FirstMarker = FString::Printf(TEXT("InheritedState:%d:Ticks:0"), Signature(FirstState));
    const FString SecondMarker = FString::Printf(TEXT("InheritedState:%d:Ticks:0"), Signature(ChildState));
    Test.TestEqual(TEXT("First managed state consumes all inherited collections"), First.Component->ManagedState, FirstMarker);
    Test.TestEqual(TEXT("Second managed state consumes inherited child collections"), Second.Component->ManagedState, SecondMarker);
    Test.TestTrue(
        TEXT("First managed signature drives native actor rotation"),
        FMath::IsNearlyEqual(
            First.Actor->GetActorRotation().Yaw,
            FRotator::NormalizeAxis(static_cast<float>(Signature(FirstState) % 360)),
            0.01f));
    Test.TestTrue(
        TEXT("Second managed signature drives native actor rotation"),
        FMath::IsNearlyEqual(
            Second.Actor->GetActorRotation().Yaw,
            FRotator::NormalizeAxis(static_cast<float>(Signature(ChildState) % 360)),
            0.01f));

    FUnifiedScriptRuntimeDebugContext FirstContextBeforeReload;
    FUnifiedScriptRuntimeDebugContext SecondContextBeforeReload;
    if (!Test.TestTrue(TEXT("First runtime debug identity is available"),
            First.Component->GetRuntimeDebugContext(FirstContextBeforeReload)) ||
        !Test.TestTrue(TEXT("Second runtime debug identity is available"),
            Second.Component->GetRuntimeDebugContext(SecondContextBeforeReload)))
    {
        return false;
    }
    Test.TestEqual(TEXT("First debug identity uses first World"), FirstContextBeforeReload.WorldHostId, First.Host->GetWorldHostId());
    Test.TestEqual(TEXT("Second debug identity uses second World"), SecondContextBeforeReload.WorldHostId, Second.Host->GetWorldHostId());
    Test.TestTrue(TEXT("World instances have distinct runtime identities"),
        FirstContextBeforeReload.InstanceId != SecondContextBeforeReload.InstanceId &&
            FirstContextBeforeReload.HostHandle != SecondContextBeforeReload.HostHandle &&
            FirstContextBeforeReload.TargetHandle != SecondContextBeforeReload.TargetHandle);

    First.Host->BeginManagedFrame();
    First.Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
    First.Host->EndManagedFrame();
    FState FirstTick = FirstState;
    FirstTick.TickCount = 1;
    CheckState(*First.Actor, FirstTick, *this, TEXT("First tick"));
    FState SecondTick = ChildState;
    Test.TestTrue(TEXT("Second instance remains at tick zero while First ticks"),
        CheckState(*Second.Actor, SecondTick, *this, TEXT("Second remains at tick zero")));
    Test.TestEqual(
        TEXT("First managed marker includes signature and tick count"),
        First.Component->ManagedState,
        FString::Printf(TEXT("InheritedState:%d:Ticks:1"), Signature(FirstState)));

    Second.Host->BeginManagedFrame();
    Second.Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
    Second.Host->EndManagedFrame();
    SecondTick.TickCount = 1;
    Test.TestTrue(TEXT("Second instance advances independently on its own tick"),
        CheckState(*Second.Actor, SecondTick, *this, TEXT("Second first tick")));

    const FString FirstGeneration = First.Component->ActiveGeneration;
    const FString SecondGeneration = Second.Component->ActiveGeneration;
    if (!Test.TestTrue(TEXT("Reloads first inherited-state instance"), First.Component->ReloadScript()))
    {
        AddError(First.Component->LastScriptError);
        return false;
    }
    Test.TestTrue(TEXT("First reload changes generation"), First.Component->ActiveGeneration != FirstGeneration);
    Test.TestEqual(TEXT("Second generation is isolated"), Second.Component->ActiveGeneration, SecondGeneration);
    CheckState(*First.Actor, FirstTick, *this, TEXT("First after reload"));
    CheckState(*Second.Actor, SecondTick, *this, TEXT("Second after first reload"));
    First.Host->BeginManagedFrame();
    First.Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
    First.Host->EndManagedFrame();
    FState FirstAfterReloadTick = FirstTick;
    FirstAfterReloadTick.TickCount = 2;
    CheckState(*First.Actor, FirstAfterReloadTick, *this, TEXT("First after isolated reload tick"));
    Test.TestEqual(
        TEXT("First reload tick updates managed marker once"),
        First.Component->ManagedState,
        FString::Printf(TEXT("InheritedState:%d:Ticks:2"), Signature(FirstState)));
    Second.Host->BeginManagedFrame();
    Second.Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
    Second.Host->EndManagedFrame();
    FState SecondAfterTick = SecondTick;
    SecondAfterTick.TickCount = 2;
    CheckState(*Second.Actor, SecondAfterTick, *this, TEXT("Second isolated tick"));
    FUnifiedScriptRuntimeDebugContext FirstContextAfterReload;
    FUnifiedScriptRuntimeDebugContext SecondContextAfterFirstReload;
    Test.TestTrue(TEXT("First debug identity remains readable after reload"),
        First.Component->GetRuntimeDebugContext(FirstContextAfterReload));
    Test.TestTrue(TEXT("Second debug identity remains readable after first reload"),
        Second.Component->GetRuntimeDebugContext(SecondContextAfterFirstReload));
    Test.TestEqual(TEXT("First reload changes debug generation"), FirstContextAfterReload.Generation, FirstContextBeforeReload.Generation + 1);
    Test.TestEqual(TEXT("Second debug generation remains isolated"), SecondContextAfterFirstReload.Generation, SecondContextBeforeReload.Generation);
    Test.TestEqual(TEXT("First reload preserves World identity"), FirstContextAfterReload.WorldHostId, FirstContextBeforeReload.WorldHostId);
    Test.TestEqual(TEXT("First reload preserves instance identity"), FirstContextAfterReload.InstanceId, FirstContextBeforeReload.InstanceId);
    Test.TestEqual(TEXT("First reload preserves Host handle"), FirstContextAfterReload.HostHandle, FirstContextBeforeReload.HostHandle);
    Test.TestEqual(TEXT("First reload preserves Target handle"), FirstContextAfterReload.TargetHandle, FirstContextBeforeReload.TargetHandle);
    Test.TestEqual(TEXT("Second remains on its original World identity"), SecondContextAfterFirstReload.WorldHostId, SecondContextBeforeReload.WorldHostId);
    Test.TestEqual(TEXT("Second remains on its original instance identity"), SecondContextAfterFirstReload.InstanceId, SecondContextBeforeReload.InstanceId);
    Test.TestEqual(TEXT("Second retains its Host handle after First reload"), SecondContextAfterFirstReload.HostHandle, SecondContextBeforeReload.HostHandle);
    Test.TestEqual(TEXT("Second retains its Target handle after First reload"), SecondContextAfterFirstReload.TargetHandle, SecondContextBeforeReload.TargetHandle);

    const int32 SecondManagedCountBeforeFirstDestroy = Second.Host->GetManagedScriptCount();
    const int32 SecondClaimsBeforeFirstDestroy = Second.Host->GetGameplayLifecycleClaimCount();
    UUnifiedScriptWorldSubsystem* FirstHost = First.Host;
    DestroyActor(First);
    Test.TestEqual(TEXT("First World releases managed script before World teardown"), FirstHost->GetManagedScriptCount(), 0);
    Test.TestEqual(TEXT("First World releases lifecycle claim before World teardown"), FirstHost->GetGameplayLifecycleClaimCount(), 0);
    Test.TestEqual(
        TEXT("First World teardown leaves the other managed World registered"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
        InitialWorldCount + 2);
    DestroyWorld(First);
    Test.TestEqual(
        TEXT("First World destruction leaves the second managed World registered"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
        InitialWorldCount + 1);
    Test.TestEqual(TEXT("Second script remains after first World teardown"), Second.Host->GetManagedScriptCount(), SecondManagedCountBeforeFirstDestroy);
    Test.TestEqual(TEXT("Second lifecycle claim remains after first World teardown"), Second.Host->GetGameplayLifecycleClaimCount(), SecondClaimsBeforeFirstDestroy);
    Second.Host->BeginManagedFrame();
    Second.Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
    Second.Host->EndManagedFrame();
    FState SecondAfterFirstTeardownTick = SecondAfterTick;
    SecondAfterFirstTeardownTick.TickCount = 3;
    CheckState(*Second.Actor, SecondAfterFirstTeardownTick, *this, TEXT("Second after first World teardown"));
    const FString SecondGenerationBeforeReload = Second.Component->ActiveGeneration;
    if (!Test.TestTrue(TEXT("Reloads second inherited-state instance"), Second.Component->ReloadScript()))
    {
        AddError(Second.Component->LastScriptError);
        return false;
    }
    Test.TestTrue(TEXT("Second reload changes its own generation"), Second.Component->ActiveGeneration != SecondGenerationBeforeReload);
    CheckState(*Second.Actor, SecondAfterFirstTeardownTick, *this, TEXT("Second after own reload"));
    Second.Host->BeginManagedFrame();
    Second.Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
    Second.Host->EndManagedFrame();
    FState SecondFinal = SecondAfterFirstTeardownTick;
    SecondFinal.TickCount = 4;
    CheckState(*Second.Actor, SecondFinal, *this, TEXT("Second after final reload tick"));
    Test.TestEqual(
        TEXT("Second final marker includes all isolated ticks"),
        Second.Component->ManagedState,
        FString::Printf(TEXT("InheritedState:%d:Ticks:4"), Signature(ChildState)));
    UUnifiedScriptWorldSubsystem* SecondHost = Second.Host;
    DestroyActor(Second);
    Test.TestEqual(TEXT("Second World releases managed script"), SecondHost->GetManagedScriptCount(), 0);
    Test.TestEqual(TEXT("Second World releases lifecycle claim"), SecondHost->GetGameplayLifecycleClaimCount(), 0);
    DestroyWorld(Second);
    Test.TestEqual(
        TEXT("Second World destruction restores managed World count"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
        InitialWorldCount);
    if (bCookFixture && !HasAnyErrors())
    {
        const FString BaseClassPath = FSoftClassPath(BaseBlueprint->GeneratedClass).ToString();
        const FString ChildClassPath = FSoftClassPath(ChildBlueprint->GeneratedClass).ToString();
        const FString GrandchildClassPath = FSoftClassPath(GrandchildBlueprint->GeneratedClass).ToString();
        const FString ScriptAssetPath = ScriptAsset->GetPathName();
        FString ReceiptError;
        BaseBlueprint = nullptr;
        ChildBlueprint = nullptr;
        GrandchildBlueprint = nullptr;
        ScriptAsset = nullptr;
        if (!Test.TestTrue(TEXT("Unloads inherited-state packages before publishing cook receipt"), UnloadByName(PackageNames)))
        {
            AddError(TEXT("Could not unload inherited-state packages before publishing cook receipt."));
            return false;
        }
        CollectGarbage(RF_NoFlags);
        if (!HasAnyErrors() &&
            !WriteCookReceipt(
                CookReceiptPath,
                Config,
                PackageNames,
                PackageFilenames,
                BaseClassPath,
                ChildClassPath,
                GrandchildClassPath,
                ScriptAssetPath,
                ReceiptError))
        {
            AddError(ReceiptError);
        }
        if (!HasAnyErrors())
        {
            bKeepCookFixture = true;
        }
    }
    return !HasAnyErrors();
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptInheritedActorStateParentChangeActiveWorldsTest,
    "UnifiedScript.Unreal.ScriptClass.ParentChange.ActiveWorlds",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptInheritedActorStateParentChangeActiveWorldsTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::InheritedActorStateTests;
    FAutomationTestBase& Test = *this;

    FString ConfigPath;
    if (!FParse::Value(FCommandLine::Get(), TEXT("-UnifiedScriptParentChangeFixture="), ConfigPath))
    {
        ConfigPath = FPaths::Combine(
            FPaths::ProjectDir(),
            TEXT("../artifacts/m13-3c-4-class-composition/parent-change/runtime-fixture/BlueprintInheritedStateFixture.config.json"));
    }
    ConfigPath = FPaths::ConvertRelativePathToFull(ConfigPath);

    FFixtureConfig Config;
    FString Error;
    if (!Test.TestTrue(TEXT("Parent-change runtime fixture config is valid"), ReadConfig(ConfigPath, Config, Error)))
    {
        AddError(Error);
        return false;
    }

    FString Declaration;
    if (!Test.TestTrue(TEXT("Parent-change runtime declaration is readable"),
            FFileHelper::LoadFileToString(Declaration, *Config.DeclarationPath)))
    {
        return false;
    }
    FGuid ClassId;
    if (!Test.TestTrue(TEXT("Parent-change runtime ScriptClassId is valid"),
            ParseScriptClassId(Config.ScriptClassId, ClassId)))
    {
        return false;
    }

    const FString PackagePrefix = Config.PackageDirectory;
    const FString BasePackageName = PackagePrefix + TEXT("/InheritedStateBase");
    const FString ChildPackageName = PackagePrefix + TEXT("/InheritedStateChild");
    const FString GrandchildPackageName = PackagePrefix + TEXT("/InheritedStateGrandchild");
    const FString ScriptAssetPackageName = PackagePrefix + TEXT("/InheritedStateScriptAsset");
    const FString ParentPackageName = PackagePrefix + TEXT("/ParentChangeOrdinaryParent");
    const FString BaseName = TEXT("InheritedStateBase");
    const FString ChildName = TEXT("InheritedStateChild");
    const FString GrandchildName = TEXT("InheritedStateGrandchild");
    const FString ScriptAssetName = TEXT("InheritedStateScriptAsset");
    const FString ParentName = TEXT("ParentChangeOrdinaryParent");
    const TArray<FString> PackageNames{
        BasePackageName,
        ChildPackageName,
        GrandchildPackageName,
        ScriptAssetPackageName,
        ParentPackageName};
    TArray<FString> PackageFilenames;
    for (const FString& PackageName : PackageNames)
    {
        PackageFilenames.Add(FPackageName::LongPackageNameToFilename(
            PackageName,
            FPackageName::GetAssetPackageExtension()));
    }

    // This test owns these exact package paths.  Refuse to mutate if a prior
    // run or another test already has either an object or a file at any path.
    for (int32 Index = 0; Index < PackageNames.Num(); ++Index)
    {
        if (FindPackage(nullptr, *PackageNames[Index]) != nullptr ||
            IFileManager::Get().FileExists(*PackageFilenames[Index]))
        {
            AddError(FString::Printf(
                TEXT("Parent-change ActiveWorlds fixture path is already occupied: %s."),
                *PackageNames[Index]));
            return false;
        }
    }

    UPackage* BasePackage = nullptr;
    UPackage* ChildPackage = nullptr;
    UPackage* GrandchildPackage = nullptr;
    UPackage* ScriptAssetPackage = nullptr;
    UPackage* ParentPackage = nullptr;
    UUnifiedScriptBlueprint* BaseBlueprint = nullptr;
    UBlueprint* ChildBlueprint = nullptr;
    UBlueprint* GrandchildBlueprint = nullptr;
    UBlueprint* ParentBlueprint = nullptr;
    UUnifiedScriptAsset* ScriptAsset = nullptr;
    FWorldFixture First;
    FWorldFixture Second;
    const int32 InitialWorldCount = FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();
    bool bOwnPackagePaths = true;

    ON_SCOPE_EXIT
    {
        DestroyWorld(First);
        DestroyWorld(Second);
        BaseBlueprint = nullptr;
        ChildBlueprint = nullptr;
        GrandchildBlueprint = nullptr;
        ParentBlueprint = nullptr;
        ScriptAsset = nullptr;
        CollectGarbage(RF_NoFlags);
        Test.TestTrue(
            TEXT("Unloads all ActiveWorlds parent-change packages during cleanup"),
            UnloadByName(PackageNames));
        CollectGarbage(RF_NoFlags);
        if (bOwnPackagePaths)
        {
            for (const FString& Filename : PackageFilenames)
            {
                if (IFileManager::Get().FileExists(*Filename))
                {
                    Test.TestTrue(
                        *FString::Printf(TEXT("Deletes ActiveWorlds fixture package %s"), *Filename),
                        IFileManager::Get().Delete(*Filename, false, true, true));
                }
                Test.TestFalse(
                    *FString::Printf(TEXT("ActiveWorlds fixture package is absent %s"), *Filename),
                    IFileManager::Get().FileExists(*Filename));
            }
            IFileManager::Get().DeleteDirectory(
                *FPackageName::LongPackageNameToFilename(PackagePrefix, TEXT("")),
                false,
                false);
        }
        Test.TestEqual(
            TEXT("ActiveWorlds cleanup restores managed World count"),
            FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
            InitialWorldCount);
    };

    BaseBlueprint = CreateScriptBlueprint(
        AUnifiedScriptActor::StaticClass(),
        BasePackageName,
        BaseName,
        BasePackage);
    ParentBlueprint = CreateOrdinaryBlueprint(
        AUnifiedScriptActor::StaticClass(),
        ParentPackageName,
        ParentName,
        ParentPackage);
    if (!Test.TestNotNull(TEXT("Creates parent-change managed owner Blueprint"), BaseBlueprint) ||
        !Test.TestNotNull(TEXT("Creates parent-change ordinary parent Blueprint"), ParentBlueprint))
    {
        return false;
    }
    if (!Test.TestTrue(
            TEXT("Parent-change owner uses the configured exact generated class path"),
            FSoftClassPath(BaseBlueprint->GeneratedClass).ToString().Equals(
                Config.ActorClassPath,
                ESearchCase::CaseSensitive)))
    {
        return false;
    }
    FKismetEditorUtilities::CompileBlueprint(ParentBlueprint);
    if (!Test.TestEqual(
            TEXT("Parent-change ordinary parent Blueprint compiles"),
            ParentBlueprint->Status,
            BS_UpToDate))
    {
        return false;
    }

    ScriptAssetPackage = CreatePackage(*ScriptAssetPackageName);
    ScriptAsset = NewObject<UUnifiedScriptAsset>(
        ScriptAssetPackage,
        *ScriptAssetName,
        RF_Public | RF_Standalone);
    if (!Test.TestNotNull(TEXT("Creates parent-change ScriptAsset"), ScriptAsset))
    {
        return false;
    }
    ScriptAsset->ScriptClassId = ClassId;
    ScriptAsset->ScriptClassKind = EUnifiedScriptClassKind::Actor;
    ScriptAsset->GeneratedClassPath = FSoftClassPath(BaseBlueprint->GeneratedClass);
    ScriptAsset->CompiledChunkPath = Config.ChunkPath;
    ScriptAsset->ScriptAssemblyPath = Config.AssemblyPath;
    ScriptAsset->ScriptTypeName = Config.ScriptTypeName;
    ScriptAsset->DeclarationPath = Config.DeclarationPath;
    ScriptAsset->DeclarationAssetPath = Config.DeclarationAssetPath;
    BaseBlueprint->ScriptAsset = ScriptAsset;

    FUnifiedScriptBlueprintClassSyncResult SyncResult;
    if (!Test.TestTrue(
            TEXT("Parent-change runtime declaration materializes"),
            FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
                *ScriptAsset,
                *BaseBlueprint,
                Declaration,
                false,
                SyncResult,
                Error)))
    {
        AddError(Error);
        return false;
    }
    FKismetEditorUtilities::CompileBlueprint(BaseBlueprint);
    if (!Test.TestEqual(TEXT("Parent-change managed owner Blueprint compiles"), BaseBlueprint->Status, BS_UpToDate))
    {
        return false;
    }

    AUnifiedScriptActor* BaseCdo = Cast<AUnifiedScriptActor>(BaseBlueprint->GeneratedClass->GetDefaultObject());
    UUnifiedScriptComponent* BaseComponent = BaseCdo != nullptr
        ? BaseCdo->GetUnifiedScriptComponent()
        : nullptr;
    if (!Test.TestNotNull(TEXT("Parent-change owner CDO has its native component"), BaseComponent))
    {
        return false;
    }
    BaseComponent->ScriptAsset = ScriptAsset;
    BaseBlueprint->ScriptAsset = ScriptAsset;
    BaseComponent->bStartAutomatically = true;
    BaseComponent->bTickAutomatically = true;
    FState BaseState;
    BaseState.Seed = 3;
    BaseState.Values = {2, 4};
    BaseState.Enabled = {5, 7};
    BaseState.Weights = {{11, 13}, {17, 19}};
    if (!Test.TestTrue(
            TEXT("Parent-change owner CDO keeps fixture defaults"),
            ConfigureAndCheckCdo(
                *BaseBlueprint->GeneratedClass.Get(),
                *ScriptAsset,
                Cast<AUnifiedScriptActor>(AUnifiedScriptActor::StaticClass()->GetDefaultObject())
                    ->GetUnifiedScriptComponent(),
                BaseState,
                *this,
                TEXT("Parent-change owner CDO"))))
    {
        return false;
    }

    ChildBlueprint = CreateOrdinaryBlueprint(
        BaseBlueprint->GeneratedClass,
        ChildPackageName,
        ChildName,
        ChildPackage);
    if (!Test.TestNotNull(TEXT("Creates parent-change ordinary child Blueprint"), ChildBlueprint))
    {
        return false;
    }
    FKismetEditorUtilities::CompileBlueprint(ChildBlueprint);
    if (!Test.TestEqual(TEXT("Parent-change ordinary child Blueprint compiles"), ChildBlueprint->Status, BS_UpToDate))
    {
        return false;
    }
    BaseCdo = Cast<AUnifiedScriptActor>(BaseBlueprint->GeneratedClass->GetDefaultObject());
    BaseComponent = BaseCdo != nullptr ? BaseCdo->GetUnifiedScriptComponent() : nullptr;
    AUnifiedScriptActor* ChildCdo = Cast<AUnifiedScriptActor>(ChildBlueprint->GeneratedClass->GetDefaultObject());
    if (!Test.TestNotNull(TEXT("Parent-change child CDO exists"), ChildCdo) ||
        !Test.TestNotNull(TEXT("Parent-change child component exists"), ChildCdo->GetUnifiedScriptComponent()))
    {
        return false;
    }
    FState ChildState;
    ChildState.Seed = 9;
    ChildState.Values = {3, 8, 13};
    ChildState.Enabled = {4, 10};
    ChildState.Weights = {{2, 5}, {7, 11}};
    const FIntProperty* ChildSeed = FindFProperty<FIntProperty>(ChildBlueprint->GeneratedClass, TEXT("Seed"));
    const FArrayProperty* ChildValues = FindFProperty<FArrayProperty>(ChildBlueprint->GeneratedClass, TEXT("Values"));
    const FSetProperty* ChildEnabled = FindFProperty<FSetProperty>(ChildBlueprint->GeneratedClass, TEXT("Enabled"));
    const FMapProperty* ChildWeights = FindFProperty<FMapProperty>(ChildBlueprint->GeneratedClass, TEXT("Weights"));
    if (!Test.TestNotNull(TEXT("Parent-change child Seed property exists"), ChildSeed) ||
        !Test.TestNotNull(TEXT("Parent-change child Values property exists"), ChildValues) ||
        !Test.TestNotNull(TEXT("Parent-change child Enabled property exists"), ChildEnabled) ||
        !Test.TestNotNull(TEXT("Parent-change child Weights property exists"), ChildWeights))
    {
        return false;
    }
    ChildSeed->SetPropertyValue_InContainer(ChildCdo, ChildState.Seed);
    SetArray(*ChildValues, *ChildCdo, ChildState.Values);
    SetSet(*ChildEnabled, *ChildCdo, {4, 10});
    SetMap(*ChildWeights, *ChildCdo, {{2, 5}, {7, 11}});
    if (!Test.TestTrue(
            TEXT("Parent-change child CDO keeps its complete state"),
            CheckState(*ChildCdo, ChildState, *this, TEXT("Parent-change child CDO"))))
    {
        return false;
    }

    GrandchildBlueprint = CreateOrdinaryBlueprint(
        ChildBlueprint->GeneratedClass,
        GrandchildPackageName,
        GrandchildName,
        GrandchildPackage);
    if (!Test.TestNotNull(TEXT("Creates parent-change ordinary grandchild Blueprint"), GrandchildBlueprint))
    {
        return false;
    }
    FKismetEditorUtilities::CompileBlueprint(GrandchildBlueprint);
    if (!Test.TestEqual(
            TEXT("Parent-change ordinary grandchild Blueprint compiles"),
            GrandchildBlueprint->Status,
            BS_UpToDate))
    {
        return false;
    }
    AUnifiedScriptActor* GrandchildCdo = Cast<AUnifiedScriptActor>(GrandchildBlueprint->GeneratedClass->GetDefaultObject());
    if (!Test.TestNotNull(TEXT("Parent-change grandchild CDO exists"), GrandchildCdo) ||
        !Test.TestNotNull(TEXT("Parent-change grandchild component exists"), GrandchildCdo->GetUnifiedScriptComponent()) ||
        !Test.TestTrue(
            TEXT("Parent-change grandchild CDO follows its configured child"),
            CheckState(*GrandchildCdo, ChildState, *this, TEXT("Parent-change grandchild CDO"))))
    {
        return false;
    }

    AUnifiedScriptActor* ParentCdo = Cast<AUnifiedScriptActor>(ParentBlueprint->GeneratedClass->GetDefaultObject());
    if (!Test.TestNotNull(TEXT("Parent-change ordinary parent CDO exists"), ParentCdo) ||
        !Test.TestNotNull(TEXT("Parent-change ordinary parent component exists"), ParentCdo->GetUnifiedScriptComponent()))
    {
        return false;
    }
    ParentCdo->GetUnifiedScriptComponent()->bStartAutomatically = true;
    ParentCdo->GetUnifiedScriptComponent()->bTickAutomatically = true;

    for (int32 Index = 0; Index < PackageNames.Num(); ++Index)
    {
        UObject* ObjectToSave = nullptr;
        switch (Index)
        {
        case 0: ObjectToSave = BaseBlueprint; break;
        case 1: ObjectToSave = ChildBlueprint; break;
        case 2: ObjectToSave = GrandchildBlueprint; break;
        case 3: ObjectToSave = ScriptAsset; break;
        case 4: ObjectToSave = ParentBlueprint; break;
        default: break;
        }
        if (!Test.TestNotNull(*FString::Printf(TEXT("Parent-change package %d top-level object"), Index), ObjectToSave) ||
            !Test.TestTrue(
                *FString::Printf(TEXT("Saves parent-change package %s"), *PackageNames[Index]),
                SavePackage(*ObjectToSave, PackageFilenames[Index], Error)))
        {
            AddError(Error);
            return false;
        }
    }

    TArray<uint8> BaseBytesBefore;
    TArray<uint8> ChildBytesBefore;
    TArray<uint8> GrandchildBytesBefore;
    if (!Test.TestTrue(TEXT("Reads owner package bytes before active parent rejection"),
            FFileHelper::LoadFileToArray(BaseBytesBefore, *PackageFilenames[0])) ||
        !Test.TestTrue(TEXT("Reads child package bytes before active parent rejection"),
            FFileHelper::LoadFileToArray(ChildBytesBefore, *PackageFilenames[1])) ||
        !Test.TestTrue(TEXT("Reads grandchild package bytes before active parent rejection"),
            FFileHelper::LoadFileToArray(GrandchildBytesBefore, *PackageFilenames[2])))
    {
        return false;
    }

    const FString ParentClassPath = FSoftClassPath(ParentBlueprint->GeneratedClass).ToString();
    const FString ReparentedDeclaration = WithSelectedAssetBaseClassPath(
        Declaration,
        Config.DeclarationAssetPath,
        ParentClassPath);
    if (!Test.TestTrue(TEXT("Builds the selected parent-change declaration"), !ReparentedDeclaration.IsEmpty()))
    {
        return false;
    }

    FString WorldError;
    if (!Test.TestTrue(TEXT("Creates first ActiveWorlds World"), MakeWorld(First, WorldError)) ||
        !Test.TestTrue(TEXT("Creates second ActiveWorlds World"), MakeWorld(Second, WorldError)))
    {
        AddError(WorldError);
        return false;
    }
    First.Actor = First.World->SpawnActor<AUnifiedScriptActor>(ChildBlueprint->GeneratedClass);
    Second.Actor = Second.World->SpawnActor<AUnifiedScriptActor>(GrandchildBlueprint->GeneratedClass);
    if (!Test.TestNotNull(TEXT("Spawns child actor in first ActiveWorlds World"), First.Actor) ||
        !Test.TestNotNull(TEXT("Spawns grandchild actor in second ActiveWorlds World"), Second.Actor))
    {
        return false;
    }
    First.Component = First.Actor->GetUnifiedScriptComponent();
    Second.Component = Second.Actor->GetUnifiedScriptComponent();
    First.Host = First.World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    Second.Host = Second.World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    if (!Test.TestNotNull(TEXT("First ActiveWorlds actor component exists"), First.Component) ||
        !Test.TestNotNull(TEXT("Second ActiveWorlds actor component exists"), Second.Component) ||
        !Test.TestNotNull(TEXT("First ActiveWorlds host exists"), First.Host) ||
        !Test.TestNotNull(TEXT("Second ActiveWorlds host exists"), Second.Host))
    {
        return false;
    }

    const auto CheckManagedConsumer =
        [&](const FWorldFixture& Fixture, const FState& Expected, const TCHAR* Label) -> bool
    {
        if (!Test.TestNotNull(*FString::Printf(TEXT("%s actor for managed consumer check"), Label), Fixture.Actor) ||
            !Test.TestNotNull(*FString::Printf(TEXT("%s component for managed consumer check"), Label), Fixture.Component))
        {
            return false;
        }
        bool bValid = Test.TestEqual(
            *FString::Printf(TEXT("%s ManagedState consumes the complete state"), Label),
            Fixture.Component->ManagedState,
            FString::Printf(TEXT("InheritedState:%d:Ticks:%d"), Signature(Expected), Expected.TickCount));
        bValid &= Test.TestTrue(
            *FString::Printf(TEXT("%s managed consumer updates Actor yaw"), Label),
            FMath::IsNearlyEqual(
                Fixture.Actor->GetActorRotation().Yaw,
                FRotator::NormalizeAxis(static_cast<float>(Signature(Expected) % 360)),
                0.01f));
        return bValid;
    };

    FState FirstState;
    FirstState.Seed = 23;
    FirstState.Values = {101, 102};
    FirstState.Enabled = {103, 104};
    FirstState.Weights = {{105, 106}, {107, 108}};
    FState SecondState;
    SecondState.Seed = 31;
    SecondState.Values = {201, 202, 203};
    SecondState.Enabled = {204, 205};
    SecondState.Weights = {{206, 207}, {208, 209}};
    const FIntProperty* FirstSeed = FindFProperty<FIntProperty>(First.Actor->GetClass(), TEXT("Seed"));
    const FArrayProperty* FirstValues = FindFProperty<FArrayProperty>(First.Actor->GetClass(), TEXT("Values"));
    const FSetProperty* FirstEnabled = FindFProperty<FSetProperty>(First.Actor->GetClass(), TEXT("Enabled"));
    const FMapProperty* FirstWeights = FindFProperty<FMapProperty>(First.Actor->GetClass(), TEXT("Weights"));
    const FIntProperty* SecondSeed = FindFProperty<FIntProperty>(Second.Actor->GetClass(), TEXT("Seed"));
    const FArrayProperty* SecondValues = FindFProperty<FArrayProperty>(Second.Actor->GetClass(), TEXT("Values"));
    const FSetProperty* SecondEnabled = FindFProperty<FSetProperty>(Second.Actor->GetClass(), TEXT("Enabled"));
    const FMapProperty* SecondWeights = FindFProperty<FMapProperty>(Second.Actor->GetClass(), TEXT("Weights"));
    if (!Test.TestNotNull(TEXT("First ActiveWorlds Seed property exists"), FirstSeed) ||
        !Test.TestNotNull(TEXT("First ActiveWorlds Values property exists"), FirstValues) ||
        !Test.TestNotNull(TEXT("First ActiveWorlds Enabled property exists"), FirstEnabled) ||
        !Test.TestNotNull(TEXT("First ActiveWorlds Weights property exists"), FirstWeights) ||
        !Test.TestNotNull(TEXT("Second ActiveWorlds Seed property exists"), SecondSeed) ||
        !Test.TestNotNull(TEXT("Second ActiveWorlds Values property exists"), SecondValues) ||
        !Test.TestNotNull(TEXT("Second ActiveWorlds Enabled property exists"), SecondEnabled) ||
        !Test.TestNotNull(TEXT("Second ActiveWorlds Weights property exists"), SecondWeights))
    {
        return false;
    }
    FirstSeed->SetPropertyValue_InContainer(First.Actor, FirstState.Seed);
    SetArray(*FirstValues, *First.Actor, FirstState.Values);
    SetSet(*FirstEnabled, *First.Actor, {103, 104});
    SetMap(*FirstWeights, *First.Actor, {{105, 106}, {107, 108}});
    SecondSeed->SetPropertyValue_InContainer(Second.Actor, SecondState.Seed);
    SetArray(*SecondValues, *Second.Actor, SecondState.Values);
    SetSet(*SecondEnabled, *Second.Actor, {204, 205});
    SetMap(*SecondWeights, *Second.Actor, {{206, 207}, {208, 209}});
    if (!Test.TestTrue(TEXT("First ActiveWorlds actor has distinct complete state"),
            CheckState(*First.Actor, FirstState, *this, TEXT("First ActiveWorlds before BeginPlay"))) ||
        !Test.TestTrue(TEXT("Second ActiveWorlds actor has distinct complete state"),
            CheckState(*Second.Actor, SecondState, *this, TEXT("Second ActiveWorlds before BeginPlay"))))
    {
        return false;
    }

    First.Actor->DispatchBeginPlay();
    Second.Actor->DispatchBeginPlay();
    if (!Test.TestTrue(TEXT("First ActiveWorlds actor starts the real managed fixture"), First.Component->bScriptRunning) ||
        !Test.TestTrue(TEXT("Second ActiveWorlds actor starts the real managed fixture"), Second.Component->bScriptRunning) ||
        !Test.TestEqual(TEXT("First ActiveWorlds host has one managed script"), First.Host->GetManagedScriptCount(), 1) ||
        !Test.TestEqual(TEXT("Second ActiveWorlds host has one managed script"), Second.Host->GetManagedScriptCount(), 1) ||
        !Test.TestEqual(TEXT("First ActiveWorlds host has one lifecycle claim"), First.Host->GetGameplayLifecycleClaimCount(), 1) ||
        !Test.TestEqual(TEXT("Second ActiveWorlds host has one lifecycle claim"), Second.Host->GetGameplayLifecycleClaimCount(), 1))
    {
        return false;
    }
    First.Host->BeginManagedFrame();
    First.Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
    First.Host->EndManagedFrame();
    ++FirstState.TickCount;
    Second.Host->BeginManagedFrame();
    Second.Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
    Second.Host->EndManagedFrame();
    ++SecondState.TickCount;
    if (!Test.TestTrue(TEXT("First ActiveWorlds managed tick runs"), CheckState(*First.Actor, FirstState, *this, TEXT("First ActiveWorlds after tick"))) ||
        !Test.TestTrue(TEXT("Second ActiveWorlds managed tick runs"), CheckState(*Second.Actor, SecondState, *this, TEXT("Second ActiveWorlds after tick"))) ||
        !CheckManagedConsumer(First, FirstState, TEXT("First ActiveWorlds after tick")) ||
        !CheckManagedConsumer(Second, SecondState, TEXT("Second ActiveWorlds after tick")))
    {
        return false;
    }

    const FObjectKey OriginalFirstActorKey(First.Actor);
    const FObjectKey OriginalSecondActorKey(Second.Actor);
    FUnifiedScriptRuntimeDebugContext FirstContextBeforeReject;
    FUnifiedScriptRuntimeDebugContext SecondContextBeforeReject;
    if (!Test.TestTrue(TEXT("First ActiveWorlds actor exposes runtime context"),
            First.Component->GetRuntimeDebugContext(FirstContextBeforeReject)) ||
        !Test.TestTrue(TEXT("Second ActiveWorlds actor exposes runtime context"),
            Second.Component->GetRuntimeDebugContext(SecondContextBeforeReject)))
    {
        return false;
    }
    const UClass* OriginalParentClassBeforeReject = BaseBlueprint->ParentClass.Get();
    UClass* OriginalGeneratedClassBeforeReject = BaseBlueprint->GeneratedClass;
    UObject* OriginalOwnerCdoBeforeReject = BaseBlueprint->GeneratedClass->GetDefaultObject();
    const TArray<uint8> BaseBytesBeforeReject = BaseBytesBefore;
    const TArray<uint8> ChildBytesBeforeReject = ChildBytesBefore;
    const TArray<uint8> GrandchildBytesBeforeReject = GrandchildBytesBefore;

    auto CheckRejectedParentChange =
        [&](const TCHAR* Label, bool bChangeResult, const FString& ChangeError, bool bFirstActive) -> bool
    {
        if (!Test.TestFalse(*FString::Printf(TEXT("%s rejects active parent change"), Label), bChangeResult))
        {
            return false;
        }
        if (!Test.TestTrue(
                *FString::Printf(TEXT("%s explains that a managed instance is active"), Label),
                ChangeError.Contains(TEXT("while a managed instance is active"))))
        {
            return false;
        }
        if (!ChangeError.IsEmpty())
        {
            Test.AddInfo(FString::Printf(TEXT("%s complete rejection error: %s"), Label, *ChangeError));
        }
        UClass* CurrentGeneratedClass = BaseBlueprint->GeneratedClass;
        AUnifiedScriptActor* CurrentOwnerCdo = CurrentGeneratedClass != nullptr
            ? Cast<AUnifiedScriptActor>(CurrentGeneratedClass->GetDefaultObject())
            : nullptr;
        UUnifiedScriptComponent* CurrentOwnerComponent = CurrentOwnerCdo != nullptr
            ? CurrentOwnerCdo->GetUnifiedScriptComponent()
            : nullptr;
        bool bStable =
            BaseBlueprint->ParentClass.Get() == OriginalParentClassBeforeReject &&
            BaseBlueprint->GeneratedClass == OriginalGeneratedClassBeforeReject &&
            CurrentOwnerCdo == OriginalOwnerCdoBeforeReject &&
            CurrentOwnerComponent != nullptr &&
            CurrentOwnerComponent->ScriptAsset == ScriptAsset &&
            BaseBlueprint->ScriptAsset == ScriptAsset;
        TArray<uint8> CurrentBytes;
        bStable &= FFileHelper::LoadFileToArray(CurrentBytes, *PackageFilenames[0]) && CurrentBytes == BaseBytesBeforeReject;
        bStable &= FFileHelper::LoadFileToArray(CurrentBytes, *PackageFilenames[1]) && CurrentBytes == ChildBytesBeforeReject;
        bStable &= FFileHelper::LoadFileToArray(CurrentBytes, *PackageFilenames[2]) && CurrentBytes == GrandchildBytesBeforeReject;
        Test.TestTrue(*FString::Printf(TEXT("%s keeps parent, generated CDO, and package bytes"), Label), bStable);
        if (!Test.TestNotNull(*FString::Printf(TEXT("%s keeps first actor"), Label), First.Actor) ||
            !Test.TestNotNull(*FString::Printf(TEXT("%s keeps second actor"), Label), Second.Actor) ||
            !Test.TestNotNull(*FString::Printf(TEXT("%s keeps first component"), Label), First.Component) ||
            !Test.TestNotNull(*FString::Printf(TEXT("%s keeps second component"), Label), Second.Component))
        {
            return false;
        }
        Test.TestTrue(*FString::Printf(TEXT("%s keeps first state"), Label), CheckState(*First.Actor, FirstState, *this, TEXT("First after rejection")));
        Test.TestTrue(*FString::Printf(TEXT("%s keeps second state"), Label), CheckState(*Second.Actor, SecondState, *this, TEXT("Second after rejection")));
        Test.TestEqual(*FString::Printf(TEXT("%s keeps first managed count"), Label), First.Host->GetManagedScriptCount(), bFirstActive ? 1 : 0);
        Test.TestEqual(*FString::Printf(TEXT("%s keeps second managed count"), Label), Second.Host->GetManagedScriptCount(), 1);
        Test.TestEqual(*FString::Printf(TEXT("%s keeps first claim count"), Label), First.Host->GetGameplayLifecycleClaimCount(), bFirstActive ? 1 : 0);
        Test.TestEqual(*FString::Printf(TEXT("%s keeps second claim count"), Label), Second.Host->GetGameplayLifecycleClaimCount(), 1);
        if (bFirstActive)
        {
            FUnifiedScriptRuntimeDebugContext CurrentFirst;
            if (!Test.TestTrue(*FString::Printf(TEXT("%s keeps first runtime context"), Label),
                    First.Component->GetRuntimeDebugContext(CurrentFirst)))
            {
                return false;
            }
            Test.TestEqual(*FString::Printf(TEXT("%s keeps first WorldHostId"), Label), CurrentFirst.WorldHostId, FirstContextBeforeReject.WorldHostId);
            Test.TestEqual(*FString::Printf(TEXT("%s keeps first Generation"), Label), CurrentFirst.Generation, FirstContextBeforeReject.Generation);
            Test.TestEqual(*FString::Printf(TEXT("%s keeps first InstanceId"), Label), CurrentFirst.InstanceId, FirstContextBeforeReject.InstanceId);
            Test.TestEqual(*FString::Printf(TEXT("%s keeps first HostHandle"), Label), CurrentFirst.HostHandle, FirstContextBeforeReject.HostHandle);
            Test.TestEqual(*FString::Printf(TEXT("%s keeps first TargetHandle"), Label), CurrentFirst.TargetHandle, FirstContextBeforeReject.TargetHandle);
        }
        FUnifiedScriptRuntimeDebugContext CurrentSecond;
        if (!Test.TestTrue(*FString::Printf(TEXT("%s keeps second runtime context"), Label),
                Second.Component->GetRuntimeDebugContext(CurrentSecond)))
        {
            return false;
        }
        Test.TestEqual(*FString::Printf(TEXT("%s keeps second WorldHostId"), Label), CurrentSecond.WorldHostId, SecondContextBeforeReject.WorldHostId);
        Test.TestEqual(*FString::Printf(TEXT("%s keeps second Generation"), Label), CurrentSecond.Generation, SecondContextBeforeReject.Generation);
        Test.TestEqual(*FString::Printf(TEXT("%s keeps second InstanceId"), Label), CurrentSecond.InstanceId, SecondContextBeforeReject.InstanceId);
        Test.TestEqual(*FString::Printf(TEXT("%s keeps second HostHandle"), Label), CurrentSecond.HostHandle, SecondContextBeforeReject.HostHandle);
        Test.TestEqual(*FString::Printf(TEXT("%s keeps second TargetHandle"), Label), CurrentSecond.TargetHandle, SecondContextBeforeReject.TargetHandle);
        return bStable;
    };

    FString ValidateError;
    const bool bValidateWhileBothActive = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *BaseBlueprint,
        ReparentedDeclaration,
        true,
        SyncResult,
        ValidateError);
    if (!CheckRejectedParentChange(TEXT("Validate-only with two active Worlds"), bValidateWhileBothActive, ValidateError, true))
    {
        return false;
    }
    FString ApplyError;
    const bool bApplyWhileBothActive = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *BaseBlueprint,
        ReparentedDeclaration,
        false,
        SyncResult,
        ApplyError);
    if (!CheckRejectedParentChange(TEXT("Apply with two active Worlds"), bApplyWhileBothActive, ApplyError, true))
    {
        return false;
    }
    First.Component->StopScript();
    if (!Test.TestFalse(TEXT("Stopping first ActiveWorlds actor releases its script"), First.Component->bScriptRunning) ||
        !Test.TestEqual(TEXT("Stopping first ActiveWorlds actor releases its managed script"), First.Host->GetManagedScriptCount(), 0) ||
        !Test.TestEqual(TEXT("Stopping first ActiveWorlds actor releases its claim"), First.Host->GetGameplayLifecycleClaimCount(), 0))
    {
        return false;
    }
    Second.Host->BeginManagedFrame();
    Second.Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
    Second.Host->EndManagedFrame();
    ++SecondState.TickCount;
    if (!Test.TestTrue(TEXT("Second ActiveWorlds actor continues ticking after first stops"), CheckState(*Second.Actor, SecondState, *this, TEXT("Second after first stop"))))
    {
        return false;
    }

    FString ApplyOneStillActiveError;
    const bool bApplyWithSecondStillActive = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *BaseBlueprint,
        ReparentedDeclaration,
        false,
        SyncResult,
        ApplyOneStillActiveError);
    if (!CheckRejectedParentChange(TEXT("Apply with second World still active"), bApplyWithSecondStillActive, ApplyOneStillActiveError, false))
    {
        return false;
    }
    Second.Component->StopScript();
    if (!Test.TestFalse(TEXT("Stopping second ActiveWorlds actor releases its script"), Second.Component->bScriptRunning) ||
        !Test.TestEqual(TEXT("Stopping second ActiveWorlds actor releases its managed script"), Second.Host->GetManagedScriptCount(), 0) ||
        !Test.TestEqual(TEXT("Stopping second ActiveWorlds actor releases its claim"), Second.Host->GetGameplayLifecycleClaimCount(), 0))
    {
        return false;
    }

    {
        FUnifiedScriptAuthoringActivationScope OuterAuthoringScope(BaseBlueprint);
        if (!Test.TestTrue(TEXT("Authoring scope retains its owner while nested activation is suppressed"), IsValid(BaseBlueprint)) ||
            !Test.TestTrue(TEXT("Authoring scope suppresses the first derived actor component"),
                FUnifiedScriptAuthoringActivationScope::IsSuppressedForComponent(*First.Component)) ||
            !Test.TestTrue(TEXT("Authoring scope suppresses the second derived actor component"),
                FUnifiedScriptAuthoringActivationScope::IsSuppressedForComponent(*Second.Component)) ||
            !Test.TestFalse(TEXT("Authoring scope does not suppress the ordinary parent component"),
                FUnifiedScriptAuthoringActivationScope::IsSuppressedForComponent(
                    *ParentCdo->GetUnifiedScriptComponent())))
        {
            return false;
        }
        {
            FUnifiedScriptAuthoringActivationScope InnerAuthoringScope(BaseBlueprint);
            if (!Test.TestTrue(TEXT("Nested authoring scope suppresses the first component"),
                    FUnifiedScriptAuthoringActivationScope::IsSuppressedForComponent(*First.Component)))
            {
                return false;
            }
        }
        if (!Test.TestTrue(TEXT("Outer authoring scope remains after nested scope exits"),
                FUnifiedScriptAuthoringActivationScope::IsSuppressedForComponent(*First.Component)) ||
            !Test.TestFalse(TEXT("Explicit Start is rejected inside authoring scope"), First.Component->StartScript()) ||
            !Test.TestEqual(TEXT("Authoring scope Start rejection creates no first managed script"), First.Host->GetManagedScriptCount(), 0) ||
            !Test.TestEqual(TEXT("Authoring scope Start rejection creates no first claim"), First.Host->GetGameplayLifecycleClaimCount(), 0) ||
            !Test.TestFalse(TEXT("Authoring scope Start rejection leaves first stopped"), First.Component->bScriptRunning) ||
            !Test.TestFalse(TEXT("Authoring scope leaves second stopped"), Second.Component->bScriptRunning))
        {
            return false;
        }
    }
    if (!Test.TestFalse(TEXT("First component is unsuppressed after authoring scope exits"),
            FUnifiedScriptAuthoringActivationScope::IsSuppressedForComponent(*First.Component)) ||
        !Test.TestFalse(TEXT("Second component is unsuppressed after authoring scope exits"),
            FUnifiedScriptAuthoringActivationScope::IsSuppressedForComponent(*Second.Component)))
    {
        return false;
    }

    TMap<FObjectKey, FObjectKey> ReinstancedKeys;
    TMap<FObjectKey, TStrongObjectPtr<UObject>> ReinstancedTargets;
    const FDelegateHandle ReinstancedHandle = FCoreUObjectDelegates::OnObjectsReinstanced.AddLambda(
        [&ReinstancedKeys, &ReinstancedTargets](const FCoreUObjectDelegates::FReplacementObjectMap& ReplacementMap)
        {
            for (const TPair<UObject*, UObject*>& Pair : ReplacementMap)
            {
                if (Pair.Key == nullptr)
                {
                    continue;
                }
                const FObjectKey OldKey(Pair.Key);
                ReinstancedKeys.Add(OldKey, Pair.Value != nullptr ? FObjectKey(Pair.Value) : FObjectKey());
                if (Pair.Value != nullptr)
                {
                    ReinstancedTargets.Add(OldKey, TStrongObjectPtr<UObject>(Pair.Value));
                }
            }
        });
    ON_SCOPE_EXIT
    {
        FCoreUObjectDelegates::OnObjectsReinstanced.Remove(ReinstancedHandle);
    };

    auto ResolveReinstancedObject =
        [&](const FObjectKey& OriginalKey) -> UObject*
    {
        TSet<FObjectKey> Visited;
        FObjectKey CurrentKey = OriginalKey;
        while (CurrentKey != FObjectKey() && !Visited.Contains(CurrentKey))
        {
            Visited.Add(CurrentKey);
            const FObjectKey* NextKey = ReinstancedKeys.Find(CurrentKey);
            if (NextKey == nullptr)
            {
                UObject* TerminalObject = CurrentKey.ResolveObjectPtr();
                return IsValid(TerminalObject) ? TerminalObject : nullptr;
            }
            if (*NextKey == FObjectKey())
            {
                return nullptr;
            }
            CurrentKey = *NextKey;
        }
        return nullptr;
    };

    FUnifiedScriptBlueprintClassMaterializerOptions RollbackOptions;
    RollbackOptions.FailurePoint =
        EUnifiedScriptBlueprintClassMaterializerFailurePoint::AfterPackageSave;
    FString RollbackError;
    const bool bRollbackSucceeded = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *BaseBlueprint,
        ReparentedDeclaration,
        false,
        SyncResult,
        RollbackError,
        RollbackOptions);
    First.Actor = Cast<AUnifiedScriptActor>(ResolveReinstancedObject(OriginalFirstActorKey));
    Second.Actor = Cast<AUnifiedScriptActor>(ResolveReinstancedObject(OriginalSecondActorKey));
    First.Component = First.Actor != nullptr ? First.Actor->GetUnifiedScriptComponent() : nullptr;
    Second.Component = Second.Actor != nullptr ? Second.Actor->GetUnifiedScriptComponent() : nullptr;
    if (!Test.TestFalse(TEXT("AfterPackageSave parent change rolls back"), bRollbackSucceeded) ||
        !Test.TestEqual(
            TEXT("AfterPackageSave reports the exact injected publication failure"),
            RollbackError,
            FString(TEXT("Injected package publication failure for UnifiedScript Blueprint transaction test."))))
    {
        return false;
    }
    if (!Test.TestNotNull(TEXT("AfterPackageSave rollback resolves the first exact actor"), First.Actor) ||
        !Test.TestNotNull(TEXT("AfterPackageSave rollback resolves the second exact actor"), Second.Actor) ||
        !Test.TestNotNull(TEXT("AfterPackageSave rollback resolves the first component"), First.Component) ||
        !Test.TestNotNull(TEXT("AfterPackageSave rollback resolves the second component"), Second.Component))
    {
        return false;
    }
    if (!Test.TestNotNull(TEXT("AfterPackageSave rollback keeps owner generated class"), BaseBlueprint->GeneratedClass.Get()) ||
        !Test.TestNotNull(TEXT("AfterPackageSave rollback keeps child generated class"), ChildBlueprint->GeneratedClass.Get()) ||
        !Test.TestNotNull(TEXT("AfterPackageSave rollback keeps grandchild generated class"), GrandchildBlueprint->GeneratedClass.Get()))
    {
        return false;
    }
    BaseCdo = Cast<AUnifiedScriptActor>(BaseBlueprint->GeneratedClass->GetDefaultObject());
    ChildCdo = Cast<AUnifiedScriptActor>(ChildBlueprint->GeneratedClass->GetDefaultObject());
    GrandchildCdo = Cast<AUnifiedScriptActor>(GrandchildBlueprint->GeneratedClass->GetDefaultObject());
    TArray<uint8> RollbackBytes;
    if (!Test.TestTrue(
            TEXT("AfterPackageSave rollback restores the native parent"),
            BaseBlueprint->ParentClass.Get() == AUnifiedScriptActor::StaticClass()) ||
        !Test.TestTrue(
            TEXT("AfterPackageSave rollback restores the generated native super"),
            BaseBlueprint->GeneratedClass->GetSuperClass() == AUnifiedScriptActor::StaticClass()) ||
        !Test.TestTrue(TEXT("AfterPackageSave rollback restores owner package bytes"),
            FFileHelper::LoadFileToArray(RollbackBytes, *PackageFilenames[0]) && RollbackBytes == BaseBytesBeforeReject) ||
        !Test.TestTrue(TEXT("AfterPackageSave rollback restores child package bytes"),
            FFileHelper::LoadFileToArray(RollbackBytes, *PackageFilenames[1]) && RollbackBytes == ChildBytesBeforeReject) ||
        !Test.TestTrue(TEXT("AfterPackageSave rollback restores grandchild package bytes"),
            FFileHelper::LoadFileToArray(RollbackBytes, *PackageFilenames[2]) && RollbackBytes == GrandchildBytesBeforeReject) ||
        !Test.TestNotNull(TEXT("AfterPackageSave rollback resolves owner CDO"), BaseCdo) ||
        !Test.TestNotNull(TEXT("AfterPackageSave rollback resolves child CDO"), ChildCdo) ||
        !Test.TestNotNull(TEXT("AfterPackageSave rollback resolves grandchild CDO"), GrandchildCdo))
    {
        return false;
    }
    Test.TestTrue(TEXT("AfterPackageSave rollback preserves first complete state"), CheckState(*First.Actor, FirstState, *this, TEXT("First after rollback")));
    Test.TestTrue(TEXT("AfterPackageSave rollback preserves second complete state"), CheckState(*Second.Actor, SecondState, *this, TEXT("Second after rollback")));
    Test.TestTrue(TEXT("AfterPackageSave rollback preserves first component ScriptAsset"), First.Component->ScriptAsset == ScriptAsset);
    Test.TestTrue(TEXT("AfterPackageSave rollback preserves second component ScriptAsset"), Second.Component->ScriptAsset == ScriptAsset);
    Test.TestTrue(TEXT("AfterPackageSave rollback preserves first component Outer"), First.Component->GetOuter() == First.Actor);
    Test.TestTrue(TEXT("AfterPackageSave rollback preserves second component Outer"), Second.Component->GetOuter() == Second.Actor);
    Test.TestTrue(TEXT("AfterPackageSave rollback keeps first actor in its World"), First.Actor->GetWorld() == First.World);
    Test.TestTrue(TEXT("AfterPackageSave rollback keeps second actor in its World"), Second.Actor->GetWorld() == Second.World);
    Test.TestTrue(TEXT("AfterPackageSave rollback preserves first current CDO archetype"), First.Component->GetArchetype() == ChildCdo->GetUnifiedScriptComponent());
    Test.TestTrue(TEXT("AfterPackageSave rollback preserves second current CDO archetype"), Second.Component->GetArchetype() == GrandchildCdo->GetUnifiedScriptComponent());
    Test.TestTrue(TEXT("AfterPackageSave rollback preserves first automatic-start setting"), First.Component->bStartAutomatically);
    Test.TestTrue(TEXT("AfterPackageSave rollback preserves second automatic-start setting"), Second.Component->bStartAutomatically);
    Test.TestFalse(TEXT("AfterPackageSave rollback leaves first actor stopped"), First.Component->bScriptRunning);
    Test.TestFalse(TEXT("AfterPackageSave rollback leaves second actor stopped"), Second.Component->bScriptRunning);
    Test.TestEqual(TEXT("AfterPackageSave rollback leaves first managed scripts stopped"), First.Host->GetManagedScriptCount(), 0);
    Test.TestEqual(TEXT("AfterPackageSave rollback leaves second managed scripts stopped"), Second.Host->GetManagedScriptCount(), 0);
    Test.TestEqual(TEXT("AfterPackageSave rollback leaves first claims stopped"), First.Host->GetGameplayLifecycleClaimCount(), 0);
    Test.TestEqual(TEXT("AfterPackageSave rollback leaves second claims stopped"), Second.Host->GetGameplayLifecycleClaimCount(), 0);
    if (!Test.TestTrue(TEXT("First explicitly starts after rollback scope exits"), First.Component->StartScript()) ||
        !Test.TestTrue(TEXT("First runs after rollback explicit Start"), First.Component->bScriptRunning) ||
        !Test.TestFalse(TEXT("Second remains stopped after rollback explicit Start"), Second.Component->bScriptRunning) ||
        !Test.TestEqual(TEXT("Rollback explicit Start creates one first managed script"), First.Host->GetManagedScriptCount(), 1) ||
        !Test.TestEqual(TEXT("Rollback explicit Start leaves second managed scripts empty"), Second.Host->GetManagedScriptCount(), 0))
    {
        return false;
    }
    First.Component->StopScript();
    if (!Test.TestFalse(TEXT("First stops again before successful parent change"), First.Component->bScriptRunning) ||
        !Test.TestEqual(TEXT("First stop after rollback releases its script"), First.Host->GetManagedScriptCount(), 0) ||
        !Test.TestEqual(TEXT("First stop after rollback releases its claim"), First.Host->GetGameplayLifecycleClaimCount(), 0))
    {
        return false;
    }

    FString ReparentError;
    const bool bReparented = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *BaseBlueprint,
        ReparentedDeclaration,
        false,
        SyncResult,
        ReparentError);
    First.Actor = Cast<AUnifiedScriptActor>(ResolveReinstancedObject(OriginalFirstActorKey));
    Second.Actor = Cast<AUnifiedScriptActor>(ResolveReinstancedObject(OriginalSecondActorKey));
    First.Component = First.Actor != nullptr ? First.Actor->GetUnifiedScriptComponent() : nullptr;
    Second.Component = Second.Actor != nullptr ? Second.Actor->GetUnifiedScriptComponent() : nullptr;
    if (!Test.TestTrue(TEXT("Parent-change succeeds after both Worlds stop"), bReparented))
    {
        AddError(ReparentError);
        return false;
    }
    if (!Test.TestTrue(TEXT("Parent-change captures the first exact actor reinstance"), ReinstancedKeys.Contains(OriginalFirstActorKey)) ||
        !Test.TestTrue(TEXT("Parent-change captures the second exact actor reinstance"), ReinstancedKeys.Contains(OriginalSecondActorKey)))
    {
        return false;
    }

    if (!Test.TestNotNull(TEXT("Parent-change keeps owner generated class"), BaseBlueprint->GeneratedClass.Get()) ||
        !Test.TestNotNull(TEXT("Parent-change keeps child generated class"), ChildBlueprint->GeneratedClass.Get()) ||
        !Test.TestNotNull(TEXT("Parent-change keeps grandchild generated class"), GrandchildBlueprint->GeneratedClass.Get()))
    {
        return false;
    }
    BaseCdo = Cast<AUnifiedScriptActor>(BaseBlueprint->GeneratedClass->GetDefaultObject());
    ChildCdo = Cast<AUnifiedScriptActor>(ChildBlueprint->GeneratedClass->GetDefaultObject());
    GrandchildCdo = Cast<AUnifiedScriptActor>(GrandchildBlueprint->GeneratedClass->GetDefaultObject());
    const bool bCurrentParent = BaseBlueprint->GeneratedClass != nullptr &&
        BaseBlueprint->GeneratedClass->GetSuperClass() == ParentBlueprint->GeneratedClass;
    if (!Test.TestTrue(TEXT("Parent-change generated class uses the requested ordinary parent"), bCurrentParent) ||
        !Test.TestNotNull(TEXT("Parent-change resolves the first exact replacement actor"), First.Actor) ||
        !Test.TestNotNull(TEXT("Parent-change resolves the second exact replacement actor"), Second.Actor) ||
        !Test.TestNotNull(TEXT("Parent-change resolves the first replacement component"), First.Component) ||
        !Test.TestNotNull(TEXT("Parent-change resolves the second replacement component"), Second.Component) ||
        !Test.TestNotNull(TEXT("Parent-change resolves the current owner CDO"), BaseCdo) ||
        !Test.TestNotNull(TEXT("Parent-change resolves the current child CDO"), ChildCdo) ||
        !Test.TestNotNull(TEXT("Parent-change resolves the current grandchild CDO"), GrandchildCdo))
    {
        return false;
    }
    Test.TestTrue(TEXT("Parent-change preserves the exact owner ScriptAsset"), BaseBlueprint->ScriptAsset == ScriptAsset);
    Test.TestTrue(TEXT("Parent-change preserves the exact first ScriptAsset"), First.Component->ScriptAsset == ScriptAsset);
    Test.TestTrue(TEXT("Parent-change preserves the exact second ScriptAsset"), Second.Component->ScriptAsset == ScriptAsset);
    Test.TestTrue(TEXT("Parent-change replacement keeps first actor in its World"), First.Actor->GetWorld() == First.World);
    Test.TestTrue(TEXT("Parent-change replacement keeps second actor in its World"), Second.Actor->GetWorld() == Second.World);
    Test.TestTrue(TEXT("Parent-change first component keeps exact actor Outer"), First.Component->GetOuter() == First.Actor);
    Test.TestTrue(TEXT("Parent-change second component keeps exact actor Outer"), Second.Component->GetOuter() == Second.Actor);
    Test.TestTrue(TEXT("Parent-change first component uses current child CDO archetype"), First.Component->GetArchetype() == ChildCdo->GetUnifiedScriptComponent());
    Test.TestTrue(TEXT("Parent-change second component uses current grandchild CDO archetype"), Second.Component->GetArchetype() == GrandchildCdo->GetUnifiedScriptComponent());
    Test.TestTrue(TEXT("Parent-change first replacement keeps complete state"), CheckState(*First.Actor, FirstState, *this, TEXT("First after parent change")));
    Test.TestTrue(TEXT("Parent-change second replacement keeps complete state"), CheckState(*Second.Actor, SecondState, *this, TEXT("Second after parent change")));
    Test.TestFalse(TEXT("Parent-change first replacement is stopped before explicit Start"), First.Component->bScriptRunning);
    Test.TestFalse(TEXT("Parent-change second replacement is stopped before explicit Start"), Second.Component->bScriptRunning);
    Test.TestEqual(TEXT("Parent-change first host has no script before explicit Start"), First.Host->GetManagedScriptCount(), 0);
    Test.TestEqual(TEXT("Parent-change second host has no script before explicit Start"), Second.Host->GetManagedScriptCount(), 0);
    Test.TestEqual(TEXT("Parent-change first host has no claim before explicit Start"), First.Host->GetGameplayLifecycleClaimCount(), 0);
    Test.TestEqual(TEXT("Parent-change second host has no claim before explicit Start"), Second.Host->GetGameplayLifecycleClaimCount(), 0);

    FUnifiedScriptRuntimeDebugContext FirstContextAfterStart;
    FUnifiedScriptRuntimeDebugContext SecondContextAfterStart;
    if (!Test.TestTrue(TEXT("Explicitly starts the first replacement actor"), First.Component->StartScript()) ||
        !Test.TestTrue(TEXT("First replacement actor is running after explicit Start"), First.Component->bScriptRunning) ||
        !Test.TestFalse(TEXT("Second replacement actor remains stopped"), Second.Component->bScriptRunning) ||
        !Test.TestEqual(TEXT("First host has one script after explicit Start"), First.Host->GetManagedScriptCount(), 1) ||
        !Test.TestEqual(TEXT("Second host remains empty after first explicit Start"), Second.Host->GetManagedScriptCount(), 0) ||
        !Test.TestTrue(TEXT("First replacement exposes a new runtime context"), First.Component->GetRuntimeDebugContext(FirstContextAfterStart)))
    {
        return false;
    }
    Test.TestEqual(TEXT("First replacement keeps its World host identity"), FirstContextAfterStart.WorldHostId, FirstContextBeforeReject.WorldHostId);
    Test.TestNotEqual(TEXT("First replacement does not reuse the old InstanceId"), FirstContextAfterStart.InstanceId, FirstContextBeforeReject.InstanceId);
    Test.TestNotEqual(TEXT("First replacement does not reuse the old HostHandle"), FirstContextAfterStart.HostHandle, FirstContextBeforeReject.HostHandle);
    Test.TestNotEqual(TEXT("First replacement does not reuse the old TargetHandle"), FirstContextAfterStart.TargetHandle, FirstContextBeforeReject.TargetHandle);
    First.Host->BeginManagedFrame();
    First.Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
    First.Host->EndManagedFrame();
    ++FirstState.TickCount;
    Test.TestTrue(TEXT("First replacement ticks after explicit Start"), CheckState(*First.Actor, FirstState, *this, TEXT("First after explicit Start and tick")));
    Test.TestTrue(TEXT("First replacement managed consumer uses exact state after explicit Start"),
        CheckManagedConsumer(First, FirstState, TEXT("First replacement after explicit Start and tick")));

    if (!Test.TestTrue(TEXT("Explicitly starts the second replacement actor"), Second.Component->StartScript()) ||
        !Test.TestTrue(TEXT("Second replacement actor is running after explicit Start"), Second.Component->bScriptRunning) ||
        !Test.TestEqual(TEXT("Second host has one script after explicit Start"), Second.Host->GetManagedScriptCount(), 1) ||
        !Test.TestEqual(TEXT("Second host has one claim after explicit Start"), Second.Host->GetGameplayLifecycleClaimCount(), 1) ||
        !Test.TestTrue(TEXT("Second replacement exposes a new runtime context"), Second.Component->GetRuntimeDebugContext(SecondContextAfterStart)))
    {
        return false;
    }
    Test.TestEqual(TEXT("Second replacement keeps its World host identity"), SecondContextAfterStart.WorldHostId, SecondContextBeforeReject.WorldHostId);
    Test.TestNotEqual(TEXT("Second replacement does not reuse the old InstanceId"), SecondContextAfterStart.InstanceId, SecondContextBeforeReject.InstanceId);
    Test.TestNotEqual(TEXT("Second replacement does not reuse the old HostHandle"), SecondContextAfterStart.HostHandle, SecondContextBeforeReject.HostHandle);
    Test.TestNotEqual(TEXT("Second replacement does not reuse the old TargetHandle"), SecondContextAfterStart.TargetHandle, SecondContextBeforeReject.TargetHandle);
    Test.TestTrue(TEXT("Replacement Worlds keep isolated managed identities"),
        FirstContextAfterStart.WorldHostId != SecondContextAfterStart.WorldHostId &&
        FirstContextAfterStart.InstanceId != SecondContextAfterStart.InstanceId &&
        FirstContextAfterStart.HostHandle != SecondContextAfterStart.HostHandle &&
        FirstContextAfterStart.TargetHandle != SecondContextAfterStart.TargetHandle);
    Second.Host->BeginManagedFrame();
    Second.Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
    Second.Host->EndManagedFrame();
    ++SecondState.TickCount;
    Test.TestTrue(TEXT("Second replacement ticks after explicit Start"), CheckState(*Second.Actor, SecondState, *this, TEXT("Second after explicit Start and tick")));
    Test.TestTrue(TEXT("Second replacement managed consumer uses exact state after explicit Start"),
        CheckManagedConsumer(Second, SecondState, TEXT("Second replacement after explicit Start and tick")));

    DestroyActor(First);
    DestroyActor(Second);
    Test.TestEqual(TEXT("ActiveWorlds first host releases managed script after actor teardown"), First.Host->GetManagedScriptCount(), 0);
    Test.TestEqual(TEXT("ActiveWorlds second host releases managed script after actor teardown"), Second.Host->GetManagedScriptCount(), 0);
    Test.TestEqual(TEXT("ActiveWorlds first host releases claims after actor teardown"), First.Host->GetGameplayLifecycleClaimCount(), 0);
    Test.TestEqual(TEXT("ActiveWorlds second host releases claims after actor teardown"), Second.Host->GetGameplayLifecycleClaimCount(), 0);
    DestroyWorld(First);
    DestroyWorld(Second);
    return !HasAnyErrors();
}

#endif
