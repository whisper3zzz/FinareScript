#if WITH_DEV_AUTOMATION_TESTS
#include "FinareAsset.h"
#include "FinareBlueprint.h"
#include "FinareBlueprintClassMaterializer.h"
#include "FinareDeclarationMaterializer.h"
#include "FinareInteropFixture.h"
#include "FinareWorldLibrary.h"
#include "Components/SceneComponent.h"
#include "GameFramework/Actor.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "UObject/UnrealType.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareObjectModelTest,
    "Finare.Unreal.ObjectModel.NativeTypesAndProperties",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareObjectModelTest::RunTest(const FString& Parameters)
{
    USceneComponent* Component = NewObject<USceneComponent>();
    const TArray<FString> Paths = UFinareWorldLibrary::GetObjectTypePaths(Component);
    TestTrue(TEXT("Native ancestry is most-specific first"), Paths.Num() >= 3 &&
        Paths[0] == USceneComponent::StaticClass()->GetPathName() && Paths.Last() == UObject::StaticClass()->GetPathName());
    TestTrue(TEXT("Native base is assignable"), UFinareWorldLibrary::IsObjectOfClass(Component, UActorComponent::StaticClass()->GetPathName()));
    TestFalse(TEXT("Unrelated native type is rejected"), UFinareWorldLibrary::IsObjectOfClass(Component, AActor::StaticClass()->GetPathName()));
    TestFalse(TEXT("Null has no runtime type"), UFinareWorldLibrary::IsObjectOfClass(nullptr, UObject::StaticClass()->GetPathName()));

    const FString Json = TEXT(R"JSON({"schemaVersion":"unifiedscript.script-asset/v0.1","assets":[{
      "id":"us_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","qualifiedName":"Tests.TypedObjects","assetPath":"Tests/TypedObjects",
      "states":[
        {"id":"us_11111111111111111111111111111111","name":"Target","typeMetadataName":"Tests.Actor","typeKind":"object",
         "unrealType":{"kind":"object","unrealTypePath":"/Script/Engine.Actor","unrealNativeName":"Actor","fields":[]},
         "defaultValue":null,"exposure":{"instanceEditable":true,"saveGame":false,"replicated":false}},
        {"id":"us_22222222222222222222222222222222","name":"Contract","typeMetadataName":"Tests.Contract","typeKind":"object",
         "unrealType":{"kind":"interface","unrealTypePath":"/Script/FinareRuntime.FinareInteropContract","unrealNativeName":"FinareInteropContract","fields":[]},
         "defaultValue":null,"exposure":{"instanceEditable":true,"saveGame":false,"replicated":false}}
      ],"functions":[],"lifecycleHooks":[]}]})JSON");
    FInstancedPropertyBag Bag;
    FString AssetPath, Error;
    if (!TestTrue(*FString::Printf(TEXT("Typed defaults materialize: %s"), *Error),
        FFinareDeclarationMaterializer::MaterializeJson(Json, TEXT("Tests/TypedObjects"), {}, Bag, AssetPath, Error))) return false;
    const FPropertyBagPropertyDesc* Desc = Bag.FindPropertyDescByName(TEXT("Target"));
    TestTrue(TEXT("Object default keeps its native class"), Desc != nullptr && Desc->ValueTypeObject == AActor::StaticClass());

    FString Name = TEXT("BP_TypedObjects_") + FGuid::NewGuid().ToString(EGuidFormats::Digits);
    UPackage* Package = CreatePackage(*(TEXT("/Engine/Transient/") + Name));
    UFinareBlueprint* Blueprint = Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(AActor::StaticClass(),
        Package, FName(*Name), BPTYPE_Normal, UFinareBlueprint::StaticClass(), UFinareBlueprintGeneratedClass::StaticClass()));
    if (!TestNotNull(TEXT("Creates generated class"), Blueprint)) return false;
    UFinareAsset* Asset = NewObject<UFinareAsset>();
    FGuid::ParseExact(TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), EGuidFormats::Digits, Asset->ScriptClassId);
    Asset->ScriptClassKind = EFinareClassKind::Actor;
    Asset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
    Asset->ScriptTypeName = TEXT("Tests.TypedObjects");
    Asset->DeclarationAssetPath = TEXT("Tests/TypedObjects");
    Blueprint->ScriptAsset = Asset;
    FFinareBlueprintClassSyncResult Sync;
    if (!TestTrue(*FString::Printf(TEXT("Typed Blueprint properties materialize: %s"), *Error),
        FFinareBlueprintClassMaterializer::SynchronizeJson(*Asset, *Blueprint, Json, false, Sync, Error))) return false;
    FObjectProperty* Target = FindFProperty<FObjectProperty>(Blueprint->GeneratedClass, TEXT("Target"));
    FInterfaceProperty* Contract = FindFProperty<FInterfaceProperty>(Blueprint->GeneratedClass, TEXT("Contract"));
    TestTrue(TEXT("UObject property has exact native type"), Target != nullptr && Target->PropertyClass == AActor::StaticClass());
    TestTrue(TEXT("Interface property retains interface identity"), Contract != nullptr && Contract->InterfaceClass == UFinareInteropContract::StaticClass());
    return true;
}
#endif
