#if WITH_DEV_AUTOMATION_TESTS

#include "FinareBlueprintClassMaterializer.h"

#include "Dom/JsonObject.h"
#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "GameFramework/Actor.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "FinareAsset.h"
#include "FinareActor.h"
#include "FinareBlueprint.h"
#include "UObject/Package.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareClassBindingMaterializerTest,
    "Finare.Unreal.ScriptClass.ClassBindingConsumer",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareClassBindingMaterializerTest::RunTest(const FString& Parameters)
{
    const FString FixtureRoot = FPaths::ConvertRelativePathToFull(FPaths::Combine(
        FPaths::ProjectDir(),
        TEXT("../artifacts/m13-3c-4-class-composition/class-binding")));
    const FString JsonPath = FPaths::Combine(FixtureRoot, TEXT("class-binding-fixture.asset.json"));
    FString Declaration;
    if (!TestTrue(TEXT("Loads actual emitted class binding JSON fixture"),
            FFileHelper::LoadFileToString(Declaration, *JsonPath)))
    {
        return false;
    }

    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Declaration);
    if (!TestTrue(TEXT("Emitted class binding JSON parses"),
            FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid()))
    {
        return false;
    }
    const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
    if (!TestTrue(TEXT("Emitted fixture contains assets"),
            Root->TryGetArrayField(TEXT("assets"), Assets) && Assets != nullptr && Assets->Num() == 1))
    {
        return false;
    }
    const TSharedPtr<FJsonObject> FixtureAsset = (*Assets)[0]->AsObject();
    FString AssetIdText;
    FString QualifiedName;
    FString AssetPath;
    if (!TestTrue(TEXT("Fixture asset carries its actual identity"),
            FixtureAsset.IsValid() &&
            FixtureAsset->TryGetStringField(TEXT("id"), AssetIdText) &&
            FixtureAsset->TryGetStringField(TEXT("qualifiedName"), QualifiedName) &&
            FixtureAsset->TryGetStringField(TEXT("assetPath"), AssetPath)))
    {
        return false;
    }

    const FString Name = TEXT("BP_ClassBinding_") +
        FGuid::NewGuid().ToString(EGuidFormats::Digits);
    UPackage* Package = CreatePackage(*(TEXT("/Engine/Transient/") + Name));
    UFinareBlueprint* Blueprint = Cast<UFinareBlueprint>(
        FKismetEditorUtilities::CreateBlueprint(
            AFinareActor::StaticClass(),
            Package,
            FName(*Name),
            BPTYPE_Normal,
            UFinareBlueprint::StaticClass(),
            UFinareBlueprintGeneratedClass::StaticClass(),
            FName(TEXT("FinareClassBindingConsumer"))));
    if (!TestNotNull(TEXT("Class binding test creates a managed Blueprint"), Blueprint) ||
        !TestNotNull(TEXT("Class binding test compiles a generated owner"), Blueprint->GeneratedClass.Get()))
    {
        return false;
    }

    UFinareAsset* ScriptAsset = NewObject<UFinareAsset>(GetTransientPackage());
    AssetIdText.RemoveFromStart(TEXT("us_"));
    if (!TestTrue(TEXT("Fixture class identity parses"),
            FGuid::ParseExact(AssetIdText, EGuidFormats::Digits, ScriptAsset->ScriptClassId)))
    {
        return false;
    }
    ScriptAsset->ScriptClassKind = EFinareClassKind::Actor;
    ScriptAsset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
    ScriptAsset->ScriptTypeName = QualifiedName;
    ScriptAsset->DeclarationAssetPath = AssetPath;
    Blueprint->ScriptAsset = ScriptAsset;

    FFinareBlueprintClassSyncResult Result;
    FString Error;
    TestTrue(
        *FString::Printf(TEXT("Editor accepts exact class binding: %s"), *Error),
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset, *Blueprint, Declaration, false, Result, Error));

    UPackage* ChildPackage = CreatePackage(*(TEXT("/Engine/Transient/") + Name + TEXT("_Child")));
    UBlueprint* ChildBlueprint = FKismetEditorUtilities::CreateBlueprint(
        Blueprint->GeneratedClass,
        ChildPackage,
        FName(*(Name + TEXT("_Child"))),
        BPTYPE_Normal,
        UBlueprint::StaticClass(),
        UBlueprintGeneratedClass::StaticClass(),
        FName(TEXT("FinareClassBindingConsumerChild")));
    if (!TestNotNull(TEXT("Creates an ordinary Blueprint child"), ChildBlueprint) ||
        !TestNotNull(
            TEXT("Ordinary child compiles a generated class"),
            ChildBlueprint != nullptr ? ChildBlueprint->GeneratedClass.Get() : nullptr))
    {
        return false;
    }
    if (ChildBlueprint != nullptr)
    {
        TestTrue(TEXT("Ordinary child has a distinct generated class"),
            ChildBlueprint->GeneratedClass != nullptr &&
            ChildBlueprint->GeneratedClass != Blueprint->GeneratedClass);
        TestTrue(TEXT("Ordinary child derives from the managed owner"),
            ChildBlueprint->GeneratedClass != nullptr &&
            ChildBlueprint->GeneratedClass->IsChildOf(Blueprint->GeneratedClass));
        TestFalse(TEXT("Declaration binding does not select the ordinary child"),
            ScriptAsset->GeneratedClassPath.ToString().Equals(
                ChildBlueprint->GeneratedClass->GetPathName(), ESearchCase::CaseSensitive));

        AActor* ChildDefault = ChildBlueprint->GeneratedClass->GetDefaultObject<AActor>();
        TestTrue(TEXT("Owner generated class accepts an ordinary child target"),
            ScriptAsset->ValidateGeneratedClassIdentity(Blueprint->GeneratedClass, ChildDefault, Error));
    }

    const FString OwnerPath = Blueprint->GeneratedClass->GetPathName();
    const FString BasePath = AFinareActor::StaticClass()->GetPathName();
    auto WithGeneratedClassPath = [&](const FString& Path) -> TOptional<FString>
    {
        FString Candidate = Declaration;
        const FString Marker = FString::Printf(TEXT("\"generatedClassPath\": \"%s\""), *Path);
        if (Candidate.Contains(TEXT("\"kind\": \"Actor\"")))
        {
            Candidate.ReplaceInline(
                TEXT("\"kind\": \"Actor\""),
                *FString::Printf(
                    TEXT("\"generatedClassPath\": \"%s\",\n        \"kind\": \"Actor\""),
                    *Path));
        }
    else if (Candidate.Contains(TEXT("\"kind\":\"Actor\"")))
        {
            Candidate.ReplaceInline(
                TEXT("\"kind\":\"Actor\""),
                *FString::Printf(
                    TEXT("\"generatedClassPath\": \"%s\",\n\"kind\":\"Actor\""),
                    *Path));
        }
        return Candidate.Contains(Marker) ? TOptional<FString>(MoveTemp(Candidate)) : TOptional<FString>();
    };
    const TOptional<FString> OwnerCandidate = WithGeneratedClassPath(OwnerPath);
    TestTrue(TEXT("Generated owner candidate is constructed from emitted JSON"), OwnerCandidate.IsSet());
    TestTrue(
        TEXT("Editor accepts an explicit generated owner path matching the loaded owner"),
        OwnerCandidate.IsSet() && FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *Blueprint,
            OwnerCandidate.GetValue(),
            false,
            Result,
            Error));
    const TOptional<FString> ChildCandidate =
        WithGeneratedClassPath(ChildBlueprint->GeneratedClass->GetPathName());
    TestTrue(TEXT("Child generated owner candidate is constructed"), ChildCandidate.IsSet());
    TestFalse(
        TEXT("Editor rejects a generated path naming the ordinary child"),
        ChildCandidate.IsSet() && FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *Blueprint,
            ChildCandidate.GetValue(),
            false,
            Result,
            Error));
    const TOptional<FString> LowercaseOwnerCandidate = WithGeneratedClassPath(OwnerPath.ToLower());
    TestTrue(TEXT("Case-variant generated owner candidate is constructed"), LowercaseOwnerCandidate.IsSet());
    TestFalse(
        TEXT("Editor rejects a generated owner path with case-only identity drift"),
        LowercaseOwnerCandidate.IsSet() && FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *Blueprint,
            LowercaseOwnerCandidate.GetValue(),
            false,
            Result,
            Error));

    FString LowercaseKind = Declaration;
    LowercaseKind.ReplaceInline(TEXT("\"kind\": \"Actor\""), TEXT("\"kind\": \"actor\""));
    LowercaseKind.ReplaceInline(TEXT("\"kind\":\"Actor\""), TEXT("\"kind\":\"actor\""));
    TestFalse(
        TEXT("Editor rejects producer-invalid lowercase class binding"),
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset, *Blueprint, LowercaseKind, false, Result, Error));
    TestTrue(TEXT("Editor reports class binding failure"), Error.Contains(TEXT("kind")));

    FString ForeignOwner = Declaration;
    ForeignOwner.ReplaceInline(
        *FString::Printf(TEXT("\"baseClassPath\": \"%s\""), *BasePath),
        *FString::Printf(
            TEXT("\"baseClassPath\": \"%s\""),
            *AActor::StaticClass()->GetPathName()));
    TestFalse(
        TEXT("Editor rejects a binding base that is not the selected Blueprint owner base"),
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset, *Blueprint, ForeignOwner, false, Result, Error));
    TestTrue(TEXT("Base mismatch failure is explicit"), Error.Contains(TEXT("class binding base")));

    FString NoExplicitBase = Declaration;
    NoExplicitBase.ReplaceInline(
        *FString::Printf(TEXT("\"baseClassPath\": \"%s\""), *BasePath),
        TEXT("\"baseClassPath\": null"));
    TestTrue(
        TEXT("Null base binding leaves the Blueprint parent unchanged"),
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset, *Blueprint, NoExplicitBase, false, Result, Error));
    TestTrue(
        TEXT("Null base binding preserves the owner parent"),
        Blueprint->ParentClass == AFinareActor::StaticClass());
    return true;
}

#endif
