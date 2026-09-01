#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptBlueprintClassMaterializer.h"

#include "EdGraphSchema_K2.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "GameFramework/Actor.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptBlueprint.h"
#include "UObject/Package.h"
#include "UObject/UnrealType.h"

namespace UE::UnifiedScript::BlueprintClass::Tests
{
FString SemanticId(const TCHAR* Digits)
{
    return FString::Printf(TEXT("us_%s"), Digits);
}

FGuid Guid(const TCHAR* Digits)
{
    FGuid Result;
    ensureAlways(FGuid::ParseExact(Digits, EGuidFormats::Digits, Result));
    return Result;
}

FString State(
    const TCHAR* Id,
    const TCHAR* Name,
    const TCHAR* DisplayType,
    const TCHAR* MetadataType,
    const TCHAR* TypeKind,
    const TCHAR* DefaultJson,
    bool bInstanceEditable = true,
    bool bSaveGame = false,
    bool bReplicated = false,
    const TCHAR* Category = TEXT("State"))
{
    return FString::Printf(
        TEXT(R"JSON(
      {
        "id": "us_%s",
        "name": "%s",
        "typeDisplayName": "%s",
        "typeMetadataName": "%s",
        "typeKind": "%s",
        "defaultValue": %s,
        "span": {
          "filePath": "Managed/StateActor.cs",
          "start": { "line": 1, "column": 1 },
          "end": { "line": 1, "column": 2 }
        },
        "exposure": {
          "instanceEditable": %s,
          "saveGame": %s,
          "replicated": %s,
          "category": "%s"
        }
      })JSON"),
        Id,
        Name,
        DisplayType,
        MetadataType,
        TypeKind,
        DefaultJson,
        bInstanceEditable ? TEXT("true") : TEXT("false"),
        bSaveGame ? TEXT("true") : TEXT("false"),
        bReplicated ? TEXT("true") : TEXT("false"),
        Category);
}

FString Document(const FString& States)
{
    return FString::Printf(
        TEXT(R"JSON(
{
  "schemaVersion": "unifiedscript.script-asset/v0.1",
  "sourceCompilationId": "us_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  "semanticConfigurationId": "us_cccccccccccccccccccccccccccccccc",
  "assets": [{
    "id": "us_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "qualifiedName": "Tests.StateActor",
    "assetPath": "Tests/StateActor",
    "span": {
      "filePath": "Managed/StateActor.cs",
      "start": { "line": 1, "column": 1 },
      "end": { "line": 50, "column": 2 }
    },
    "states": [%s
    ],
    "functions": []
  }]
}
)JSON"),
        *States);
}

FString InitialStates()
{
    TArray<FString> States = {
        State(
            TEXT("11111111111111111111111111111111"),
            TEXT("Enabled"),
            TEXT("bool"),
            TEXT("System.Boolean"),
            TEXT("boolean"),
            TEXT("true")),
        State(
            TEXT("22222222222222222222222222222222"),
            TEXT("ByteValue"),
            TEXT("byte"),
            TEXT("System.Byte"),
            TEXT("integer"),
            TEXT("7")),
        State(
            TEXT("33333333333333333333333333333333"),
            TEXT("Count"),
            TEXT("int"),
            TEXT("System.Int32"),
            TEXT("integer"),
            TEXT("3"),
            true,
            true,
            true,
            TEXT("Stats")),
        State(
            TEXT("44444444444444444444444444444444"),
            TEXT("Large"),
            TEXT("long"),
            TEXT("System.Int64"),
            TEXT("integer"),
            TEXT("9001")),
        State(
            TEXT("55555555555555555555555555555555"),
            TEXT("Ratio"),
            TEXT("float"),
            TEXT("System.Single"),
            TEXT("floatingPoint"),
            TEXT("1.25")),
        State(
            TEXT("66666666666666666666666666666666"),
            TEXT("Precision"),
            TEXT("double"),
            TEXT("System.Double"),
            TEXT("floatingPoint"),
            TEXT("2.5")),
        State(
            TEXT("77777777777777777777777777777777"),
            TEXT("Label"),
            TEXT("string"),
            TEXT("System.String"),
            TEXT("string"),
            TEXT("\"Ready\""),
            false),
    };
    return FString::Join(States, TEXT(","));
}

FString ReloadedStates()
{
    TArray<FString> States = {
        State(
            TEXT("22222222222222222222222222222222"),
            TEXT("ByteValue"),
            TEXT("byte"),
            TEXT("System.Byte"),
            TEXT("integer"),
            TEXT("7")),
        State(
            TEXT("33333333333333333333333333333333"),
            TEXT("Score"),
            TEXT("int"),
            TEXT("System.Int32"),
            TEXT("integer"),
            TEXT("9"),
            true,
            true,
            true,
            TEXT("Stats")),
        State(
            TEXT("44444444444444444444444444444444"),
            TEXT("Large"),
            TEXT("double"),
            TEXT("System.Double"),
            TEXT("floatingPoint"),
            TEXT("42.5")),
        State(
            TEXT("55555555555555555555555555555555"),
            TEXT("Ratio"),
            TEXT("float"),
            TEXT("System.Single"),
            TEXT("floatingPoint"),
            TEXT("1.25")),
        State(
            TEXT("66666666666666666666666666666666"),
            TEXT("Precision"),
            TEXT("double"),
            TEXT("System.Double"),
            TEXT("floatingPoint"),
            TEXT("2.5")),
        State(
            TEXT("77777777777777777777777777777777"),
            TEXT("Label"),
            TEXT("string"),
            TEXT("System.String"),
            TEXT("string"),
            TEXT("\"Ready\""),
            false),
    };
    return FString::Join(States, TEXT(","));
}
} // namespace UE::UnifiedScript::BlueprintClass::Tests

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptBlueprintClassMaterializerTest,
    "UnifiedScript.Unreal.ScriptClass.BlueprintStateMaterialization",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptBlueprintClassMaterializerTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::BlueprintClass::Tests;

    const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
    const FString AssetName = TEXT("BP_UnifiedScriptState_") + Suffix;
    UPackage* Package = CreatePackage(*(TEXT("/Engine/Transient/") + AssetName));
    UUnifiedScriptBlueprint* Blueprint = Cast<UUnifiedScriptBlueprint>(
        FKismetEditorUtilities::CreateBlueprint(
            AActor::StaticClass(),
            Package,
            FName(*AssetName),
            BPTYPE_Normal,
            UUnifiedScriptBlueprint::StaticClass(),
            UBlueprintGeneratedClass::StaticClass(),
            FName(TEXT("UnifiedScriptBlueprintStateMaterialization"))));
    if (!TestNotNull(TEXT("Creates a transient UnifiedScript Blueprint"), Blueprint) ||
        !TestNotNull(TEXT("Blueprint compiles an initial generated class"), Blueprint->GeneratedClass.Get()))
    {
        return false;
    }

    UUnifiedScriptAsset* ScriptAsset = NewObject<UUnifiedScriptAsset>(GetTransientPackage());
    ScriptAsset->ScriptClassId = Guid(TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
    ScriptAsset->ScriptClassKind = EUnifiedScriptClassKind::Actor;
    ScriptAsset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
    ScriptAsset->ScriptTypeName = TEXT("Tests.StateActor");
    ScriptAsset->DeclarationAssetPath = TEXT("Tests/StateActor");
    Blueprint->ScriptAsset = ScriptAsset;

    FUnifiedScriptBlueprintClassSyncResult Result;
    FString Error;
    const bool bInitial = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *Blueprint,
        Document(InitialStates()),
        false,
        Result,
        Error);
    TestTrue(*FString::Printf(TEXT("Materializes reviewed state: %s"), *Error), bInitial);
    if (!bInitial)
    {
        return false;
    }
    TestTrue(TEXT("Initial materialization changes the proxy"), Result.bChanged);
    TestEqual(TEXT("Seven reviewed types become properties"), Result.AddedPropertyCount, 7);
    TestEqual(TEXT("Seven states are declared"), Result.DeclaredStateCount, 7);
    TestEqual(TEXT("Blueprint remains compiled"), Blueprint->Status, BS_UpToDate);

    UClass* GeneratedClass = Blueprint->GeneratedClass;
    const FBoolProperty* Enabled = FindFProperty<FBoolProperty>(GeneratedClass, TEXT("Enabled"));
    const FByteProperty* ByteValue = FindFProperty<FByteProperty>(GeneratedClass, TEXT("ByteValue"));
    const FIntProperty* Count = FindFProperty<FIntProperty>(GeneratedClass, TEXT("Count"));
    const FInt64Property* Large = FindFProperty<FInt64Property>(GeneratedClass, TEXT("Large"));
    const FFloatProperty* Ratio = FindFProperty<FFloatProperty>(GeneratedClass, TEXT("Ratio"));
    const FDoubleProperty* Precision = FindFProperty<FDoubleProperty>(GeneratedClass, TEXT("Precision"));
    const FStrProperty* Label = FindFProperty<FStrProperty>(GeneratedClass, TEXT("Label"));
    TestNotNull(TEXT("bool becomes FBoolProperty"), Enabled);
    TestNotNull(TEXT("byte becomes FByteProperty"), ByteValue);
    TestNotNull(TEXT("int becomes FIntProperty"), Count);
    TestNotNull(TEXT("long becomes FInt64Property"), Large);
    TestNotNull(TEXT("float becomes FFloatProperty"), Ratio);
    TestNotNull(TEXT("double becomes FDoubleProperty"), Precision);
    TestNotNull(TEXT("string becomes FStrProperty"), Label);
    if (Count == nullptr || Label == nullptr)
    {
        return false;
    }
    TestTrue(TEXT("SaveGame exposure reaches reflected flags"), Count->HasAnyPropertyFlags(CPF_SaveGame));
    TestTrue(TEXT("Replicated exposure reaches reflected flags"), Count->HasAnyPropertyFlags(CPF_Net));
    TestEqual(TEXT("Category reaches reflection metadata"), Count->GetMetaData(TEXT("Category")), FString(TEXT("Stats")));
    TestEqual(
        TEXT("Stable state ID reaches reflection metadata"),
        Count->GetMetaData(TEXT("UnifiedScript.StateId")),
        SemanticId(TEXT("33333333333333333333333333333333")));
    TestEqual(
        TEXT("String default reaches the generated CDO"),
        Label->GetPropertyValue_InContainer(GeneratedClass->GetDefaultObject()),
        FString(TEXT("Ready")));
    TestEqual(
        TEXT("State ID becomes the Blueprint variable GUID"),
        FBlueprintEditorUtils::FindMemberVariableGuidByName(Blueprint, TEXT("Count")),
        Guid(TEXT("33333333333333333333333333333333")));

    FEdGraphPinType UserType;
    UserType.PinCategory = UEdGraphSchema_K2::PC_Boolean;
    TestTrue(
        TEXT("Adds a user-owned control variable"),
        FBlueprintEditorUtils::AddMemberVariable(Blueprint, TEXT("UserOwned"), UserType, TEXT("false")));
    FKismetEditorUtilities::CompileBlueprint(Blueprint);
    Count = FindFProperty<FIntProperty>(Blueprint->GeneratedClass, TEXT("Count"));
    if (!TestNotNull(TEXT("Count survives the user variable compile"), Count))
    {
        return false;
    }
    UObject* Cdo = Blueprint->GeneratedClass->GetDefaultObject();
    Cdo->Modify();
    Count->SetPropertyValue_InContainer(Cdo, 77);

    const bool bReloaded = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *Blueprint,
        Document(ReloadedStates()),
        false,
        Result,
        Error);
    TestTrue(*FString::Printf(TEXT("Reloaded state materializes: %s"), *Error), bReloaded);
    if (!bReloaded)
    {
        return false;
    }
    TestEqual(TEXT("One stable state is renamed"), Result.RenamedPropertyCount, 1);
    TestEqual(TEXT("One stale generated state is removed"), Result.RemovedPropertyCount, 1);
    TestEqual(TEXT("One Blueprint default override is preserved"), Result.PreservedDefaultOverrideCount, 1);
    TestNull(TEXT("Old generated name leaves reflection"), FindFProperty<FProperty>(Blueprint->GeneratedClass, TEXT("Count")));
    TestNull(TEXT("Removed generated state leaves reflection"), FindFProperty<FProperty>(Blueprint->GeneratedClass, TEXT("Enabled")));
    TestNotNull(TEXT("User-owned variable survives reconciliation"), FindFProperty<FProperty>(Blueprint->GeneratedClass, TEXT("UserOwned")));
    const FDoubleProperty* RetypedLarge =
        FindFProperty<FDoubleProperty>(Blueprint->GeneratedClass, TEXT("Large"));
    if (TestNotNull(TEXT("Unmodified declaration default permits a type change"), RetypedLarge))
    {
        TestEqual(
            TEXT("Accepted type change applies its new class default"),
            RetypedLarge->GetPropertyValue_InContainer(
                Blueprint->GeneratedClass->GetDefaultObject()),
            42.5);
    }

    const FIntProperty* Score = FindFProperty<FIntProperty>(Blueprint->GeneratedClass, TEXT("Score"));
    if (!TestNotNull(TEXT("Renamed state remains an int property"), Score))
    {
        return false;
    }
    TestEqual(
        TEXT("Blueprint override survives source rename/default change"),
        Score->GetPropertyValue_InContainer(Blueprint->GeneratedClass->GetDefaultObject()),
        77);
    TestEqual(
        TEXT("Renamed property retains stable GUID"),
        FBlueprintEditorUtils::FindMemberVariableGuidByName(Blueprint, TEXT("Score")),
        Guid(TEXT("33333333333333333333333333333333")));

    const bool bNoChange = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *Blueprint,
        Document(ReloadedStates()),
        true,
        Result,
        Error);
    TestTrue(*FString::Printf(TEXT("ValidateOnly accepts synchronized state: %s"), *Error), bNoChange);
    TestFalse(TEXT("Synchronized state is a no-change replay"), Result.bChanged);

    const FString TypeChange = State(
        TEXT("33333333333333333333333333333333"),
        TEXT("Score"),
        TEXT("double"),
        TEXT("System.Double"),
        TEXT("floatingPoint"),
        TEXT("9.0"));
    TestFalse(
        TEXT("Type change with a Blueprint override is rejected"),
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *Blueprint,
            Document(TypeChange),
            false,
            Result,
            Error));
    TestTrue(TEXT("Type-change rejection is actionable"), Error.Contains(TEXT("overridden")));
    TestNotNull(TEXT("Rejected change leaves the int property intact"), FindFProperty<FIntProperty>(Blueprint->GeneratedClass, TEXT("Score")));

    const FString Collision = State(
        TEXT("88888888888888888888888888888888"),
        TEXT("UserOwned"),
        TEXT("bool"),
        TEXT("System.Boolean"),
        TEXT("boolean"),
        TEXT("false"));
    TestFalse(
        TEXT("A managed state cannot claim a user-owned Blueprint name"),
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *Blueprint,
            Document(Collision),
            false,
            Result,
            Error));
    TestTrue(TEXT("Collision rejection identifies ownership"), Error.Contains(TEXT("different identity")));

    const FString Unsupported = State(
        TEXT("99999999999999999999999999999999"),
        TEXT("Mode"),
        TEXT("Mode"),
        TEXT("Tests.Mode"),
        TEXT("enum"),
        TEXT("\"Idle\""));
    TestFalse(
        TEXT("Unsupported enum fails before Blueprint mutation"),
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *Blueprint,
            Document(Unsupported),
            false,
            Result,
            Error));
    TestTrue(TEXT("Unsupported-type rejection lists reviewed types"), Error.Contains(TEXT("Reviewed types")));
    TestEqual(TEXT("Rejected plans leave six generated plus one user property"), Blueprint->NewVariables.Num(), 7);
    return true;
}

#endif
