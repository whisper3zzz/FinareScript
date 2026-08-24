/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 17:41:28
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-24 19:01:04
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\Private\Tests\UnifiedScriptDeclarationMaterializerTests.cpp
 * @Description:
 */
#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptDeclarationMaterializer.h"

#include "Misc/AutomationTest.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptDeclarationMaterializerTest,
    "UnifiedScript.Unreal.Declaration.Materialization",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptDeclarationMaterializerTest::RunTest(const FString& Parameters)
{
    const FString InitialJson = UTF8_TO_TCHAR(R"JSON(
{
  "schemaVersion": "unifiedscript.script-asset/v0.1",
  "assets": [{
    "assetPath": "Tests/State",
    "states": [
      {
        "id": "us_11111111111111111111111111111111",
        "name": "Damage",
        "typeMetadataName": "System.Int32",
        "typeKind": "integer",
        "defaultValue": 20,
        "exposure": {
          "instanceEditable": true,
          "saveGame": true,
          "replicated": false,
          "category": "Combat"
        }
      },
      {
        "id": "us_22222222222222222222222222222222",
        "name": "Scale",
        "typeMetadataName": "System.Single",
        "typeKind": "floatingPoint",
        "defaultValue": 2.5,
        "exposure": { "instanceEditable": true }
      },
      {
        "id": "us_33333333333333333333333333333333",
        "name": "Label",
        "typeMetadataName": "System.String",
        "typeKind": "string",
        "defaultValue": "Ready",
        "exposure": { "instanceEditable": false }
      }
    ]
  }]
}
)JSON");

    FInstancedPropertyBag InitialState;
    FString AssetPath;
    FString Error;
    const bool bInitialMaterialized =
        FUnifiedScriptDeclarationMaterializer::MaterializeJson(
            InitialJson,
            TEXT("Tests/State"),
            FInstancedPropertyBag(),
            InitialState,
            AssetPath,
            Error);
    TestTrue(
        *FString::Printf(TEXT("Initial declaration materializes: %s"), *Error),
        bInitialMaterialized);
    if (!bInitialMaterialized)
    {
        return false;
    }
    TestEqual(TEXT("Selected declaration asset is reported"), AssetPath, FString(TEXT("Tests/State")));
    TestEqual(TEXT("Three states are materialized"), InitialState.GetNumPropertiesInBag(), 3);

    const auto Damage = InitialState.GetValueInt32(TEXT("Damage"));
    const auto Scale = InitialState.GetValueFloat(TEXT("Scale"));
    const auto Label = InitialState.GetValueString(TEXT("Label"));
    TestTrue(TEXT("Int32 default is readable"), Damage.IsValid());
    TestTrue(TEXT("Float default is readable"), Scale.IsValid());
    TestTrue(TEXT("String default is readable"), Label.IsValid());
    if (Damage.IsValid())
    {
        TestEqual(TEXT("Int32 default is applied"), Damage.GetValue(), 20);
    }
    if (Scale.IsValid())
    {
        TestEqual(TEXT("Float default is applied"), Scale.GetValue(), 2.5f);
    }
    if (Label.IsValid())
    {
        TestEqual(TEXT("String default is applied"), Label.GetValue(), FString(TEXT("Ready")));
    }

    const FPropertyBagPropertyDesc* DamageDesc =
        InitialState.FindPropertyDescByName(TEXT("Damage"));
    const FPropertyBagPropertyDesc* LabelDesc =
        InitialState.FindPropertyDescByName(TEXT("Label"));
    TestNotNull(TEXT("Damage descriptor exists"), DamageDesc);
    TestNotNull(TEXT("Label descriptor exists"), LabelDesc);
    if (DamageDesc != nullptr)
    {
        TestTrue(TEXT("SaveGame exposure becomes a property flag"),
            (DamageDesc->PropertyFlags & CPF_SaveGame) != 0);
        TestFalse(TEXT("Editable state is not edit-const"),
            (DamageDesc->PropertyFlags & CPF_EditConst) != 0);
#if WITH_EDITOR
        TestEqual(TEXT("Category exposure becomes editor metadata"),
            DamageDesc->GetMetaData(TEXT("Category")), FString(TEXT("Combat")));
#endif
    }
    if (LabelDesc != nullptr)
    {
        TestTrue(TEXT("Non-editable state becomes edit-const"),
            (LabelDesc->PropertyFlags & CPF_EditConst) != 0);
    }

    TestEqual(
        TEXT("Instance override can be written"),
        InitialState.SetValueInt32(TEXT("Damage"), 77),
        EPropertyBagResult::Success);

    const FString ReloadedJson = UTF8_TO_TCHAR(R"JSON(
{
  "schemaVersion": "unifiedscript.script-asset/v0.1",
  "assets": [{
    "assetPath": "Tests/State",
    "states": [
      {
        "id": "us_11111111111111111111111111111111",
        "name": "RenamedDamage",
        "typeMetadataName": "System.Int32",
        "typeKind": "integer",
        "defaultValue": 99,
        "exposure": { "instanceEditable": true }
      },
      {
        "id": "us_44444444444444444444444444444444",
        "name": "Enabled",
        "typeMetadataName": "System.Boolean",
        "typeKind": "boolean",
        "defaultValue": true,
        "exposure": { "instanceEditable": true }
      }
    ]
  }]
}
)JSON");

    FInstancedPropertyBag ReloadedState;
    const bool bReloadedMaterialized =
        FUnifiedScriptDeclarationMaterializer::MaterializeJson(
            ReloadedJson,
            TEXT("Tests/State"),
            InitialState,
            ReloadedState,
            AssetPath,
            Error);
    TestTrue(
        *FString::Printf(TEXT("Reloaded declaration materializes: %s"), *Error),
        bReloadedMaterialized);
    if (!bReloadedMaterialized)
    {
        return false;
    }
    TestEqual(TEXT("Removed states leave the bag"), ReloadedState.GetNumPropertiesInBag(), 2);
    const auto RenamedDamage = ReloadedState.GetValueInt32(TEXT("RenamedDamage"));
    const auto Enabled = ReloadedState.GetValueBool(TEXT("Enabled"));
    TestTrue(TEXT("Renamed state remains readable"), RenamedDamage.IsValid());
    TestTrue(TEXT("New state receives its declaration default"), Enabled.IsValid());
    if (RenamedDamage.IsValid())
    {
        TestEqual(TEXT("Stable semantic ID preserves override across rename"), RenamedDamage.GetValue(), 77);
    }
    if (Enabled.IsValid())
    {
        TestTrue(TEXT("New boolean default is applied"), Enabled.GetValue());
    }
    TestFalse(TEXT("Old state name is removed"), ReloadedState.GetValueInt32(TEXT("Damage")).IsValid());

    FInstancedPropertyBag FailedOutput = ReloadedState;
    const int32 BeforeFailure = RenamedDamage.IsValid() ? RenamedDamage.GetValue() : 0;
    TestFalse(
        TEXT("Invalid declaration is rejected"),
        FUnifiedScriptDeclarationMaterializer::MaterializeJson(
            TEXT("{ not-json"),
            TEXT("Tests/State"),
            ReloadedState,
            FailedOutput,
            AssetPath,
            Error));
    const auto ValueAfterFailure = FailedOutput.GetValueInt32(TEXT("RenamedDamage"));
    TestTrue(TEXT("Failed output still contains the previous state"), ValueAfterFailure.IsValid());
    if (ValueAfterFailure.IsValid())
    {
        TestEqual(
            TEXT("Failure leaves the caller's output state unchanged"),
            ValueAfterFailure.GetValue(),
            BeforeFailure);
    }
    TestTrue(TEXT("Invalid JSON returns a useful error"), !Error.IsEmpty());
    return true;
}

#endif
