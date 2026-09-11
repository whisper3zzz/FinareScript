#if WITH_DEV_AUTOMATION_TESTS

#include "FinareDeclarationMaterializer.h"

#include "Misc/AutomationTest.h"
#include "UObject/UnrealType.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareDeclarationMaterializerTest,
	"Finare.Unreal.Declaration.Materialization",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareDeclarationMaterializerTest::RunTest(const FString& Parameters)
{
	const FString InitialJson = UTF8_TO_TCHAR(R"JSON(
{
  "schemaVersion": "unifiedscript.script-asset/v0.1",
  "assets": [{
    "id": "us_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "qualifiedName": "Tests.State",
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
      },
      {
        "id": "us_44444444444444444444444444444444",
        "name": "RowName",
        "typeMetadataName": "Finare.Unreal.UnrealName",
        "typeKind": "name",
        "defaultValue": "Player",
        "exposure": { "instanceEditable": true }
      },
      {
        "id": "us_55555555555555555555555555555555",
        "name": "DisplayText",
        "typeMetadataName": "Finare.Unreal.UnrealText",
        "typeKind": "text",
        "defaultValue": "Loaded",
        "exposure": { "instanceEditable": true }
      },
      {
        "id": "us_66666666666666666666666666666666",
        "name": "Target",
        "typeMetadataName": "Finare.Unreal.UnrealObjectHandle",
        "typeKind": "object",
        "defaultValue": null,
        "exposure": { "instanceEditable": true }
      },
      {
        "id": "us_77777777777777777777777777777777",
        "name": "ActorClass",
        "typeMetadataName": "Finare.Unreal.UnrealClassHandle",
        "typeKind": "class",
        "defaultValue": null,
        "exposure": { "instanceEditable": true }
      },
      {
        "id": "us_88888888888888888888888888888888",
        "name": "Offset",
        "typeMetadataName": "Finare.Unreal.UnrealVector",
        "typeKind": "struct",
        "unrealType": {
          "kind": "struct",
          "unrealTypePath": "/Script/CoreUObject.Vector",
          "unrealNativeName": "Vector",
          "fields": []
        },
        "defaultValue": { "x": 1.5, "y": 2.5, "z": 3.5 },
        "exposure": { "instanceEditable": true }
      }
    ]
  }]
}
)JSON");

	FInstancedPropertyBag InitialState;
	TArray<FGuid> InitialOverrideIds;
	FFinareStateMigrationReport InitialMigration;
	FString AssetPath;
	FString Error;
	const bool bInitialMaterialized =
		FFinareDeclarationMaterializer::MaterializeJsonWithOverrides(
			InitialJson,
			TEXT("Tests/State"),
			FInstancedPropertyBag(),
			{},
			InitialState,
			InitialOverrideIds,
			InitialMigration,
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
	FGuid ExpectedAssetId;
	TestTrue(
		TEXT("Expected declaration class ID parses"),
		FGuid::ParseExact(
			TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
			EGuidFormats::Digits,
			ExpectedAssetId));
	TestEqual(TEXT("Selected declaration class ID is reported"), InitialMigration.AssetId, ExpectedAssetId);
	TestEqual(
		TEXT("Selected managed type is reported"),
		InitialMigration.AssetQualifiedName,
		FString(TEXT("Tests.State")));
	TestEqual(TEXT("Eight states are materialized"), InitialState.GetNumPropertiesInBag(), 8);
	TestEqual(TEXT("Initial values inherit declaration defaults"), InitialMigration.DefaultedStateCount, 8);
	TestEqual(TEXT("Initial materialization has no overrides"), InitialOverrideIds.Num(), 0);

	const auto Damage = InitialState.GetValueInt32(TEXT("Damage"));
	const auto Scale = InitialState.GetValueFloat(TEXT("Scale"));
	const auto Label = InitialState.GetValueString(TEXT("Label"));
	const auto RowName = InitialState.GetValueName(TEXT("RowName"));
	const auto DisplayText = InitialState.GetValueText(TEXT("DisplayText"));
	const auto Offset = InitialState.GetValueStruct<FVector>(TEXT("Offset"));
	TestTrue(TEXT("Int32 default is readable"), Damage.IsValid());
	TestTrue(TEXT("Float default is readable"), Scale.IsValid());
	TestTrue(TEXT("String default is readable"), Label.IsValid());
	TestTrue(TEXT("FName default is readable"), RowName.IsValid());
	TestTrue(TEXT("FText default is readable"), DisplayText.IsValid());
	TestTrue(TEXT("FVector default is readable"), Offset.IsValid());
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
	if (RowName.IsValid())
	{
		TestEqual(TEXT("FName default is applied"), RowName.GetValue(), FName(TEXT("Player")));
	}
	if (DisplayText.IsValid())
	{
		TestEqual(TEXT("FText default is applied"), DisplayText.GetValue().ToString(), FString(TEXT("Loaded")));
	}
	if (Offset.IsValid())
	{
		TestEqual(TEXT("FVector default is applied"), *Offset.GetValue(), FVector(1.5, 2.5, 3.5));
	}

	const FPropertyBagPropertyDesc* TargetDesc =
		InitialState.FindPropertyDescByName(TEXT("Target"));
	const FPropertyBagPropertyDesc* ClassDesc =
		InitialState.FindPropertyDescByName(TEXT("ActorClass"));
	TestTrue(TEXT("Object handle becomes an UObject property"),
	         TargetDesc != nullptr && TargetDesc->ValueType == EPropertyBagPropertyType::Object &&
	         TargetDesc->ValueTypeObject == UObject::StaticClass());
	TestTrue(TEXT("Class handle becomes a UClass property"),
	         ClassDesc != nullptr && ClassDesc->ValueType == EPropertyBagPropertyType::Class &&
	         ClassDesc->ValueTypeObject == UObject::StaticClass());

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
	TestNotNull(TEXT("Damage can be tracked as an explicit override"), DamageDesc);
	if (DamageDesc == nullptr)
	{
		return false;
	}
	InitialOverrideIds.Add(DamageDesc->ID);

	const FString ReloadedJson = UTF8_TO_TCHAR(R"JSON(
{
  "schemaVersion": "unifiedscript.script-asset/v0.1",
  "assets": [{
    "id": "us_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "qualifiedName": "Tests.State",
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
	TArray<FGuid> ReloadedOverrideIds;
	FFinareStateMigrationReport ReloadedMigration;
	const bool bReloadedMaterialized =
		FFinareDeclarationMaterializer::MaterializeJsonWithOverrides(
			ReloadedJson,
			TEXT("Tests/State"),
			InitialState,
			InitialOverrideIds,
			ReloadedState,
			ReloadedOverrideIds,
			ReloadedMigration,
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
	TestEqual(TEXT("One explicit override survives"), ReloadedMigration.PreservedOverrideCount, 1);
	TestEqual(TEXT("New state inherits its default"), ReloadedMigration.DefaultedStateCount, 1);
	TestEqual(TEXT("Removed non-overrides need no drop"), ReloadedMigration.DroppedOverrideCount, 0);
	TestEqual(TEXT("Stable override identity remains explicit"), ReloadedOverrideIds.Num(), 1);
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

	const FString ChangedDefaultsJson = UTF8_TO_TCHAR(R"JSON(
{
  "schemaVersion": "unifiedscript.script-asset/v0.1",
  "assets": [{
    "id": "us_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "qualifiedName": "Tests.State",
    "assetPath": "Tests/State",
    "states": [
      {
        "id": "us_11111111111111111111111111111111",
        "name": "RenamedDamage",
        "typeMetadataName": "System.Int32",
        "typeKind": "integer",
        "defaultValue": 123,
        "exposure": { "instanceEditable": true }
      },
      {
        "id": "us_44444444444444444444444444444444",
        "name": "Enabled",
        "typeMetadataName": "System.Boolean",
        "typeKind": "boolean",
        "defaultValue": false,
        "exposure": { "instanceEditable": true }
      }
    ]
  }]
}
)JSON");
	FInstancedPropertyBag ChangedDefaultsState;
	TArray<FGuid> ChangedDefaultsOverrideIds;
	FFinareStateMigrationReport ChangedDefaultsMigration;
	TestTrue(
		TEXT("Changed declaration defaults rematerialize"),
		FFinareDeclarationMaterializer::MaterializeJsonWithOverrides(
			ChangedDefaultsJson,
			TEXT("Tests/State"),
			ReloadedState,
			ReloadedOverrideIds,
			ChangedDefaultsState,
			ChangedDefaultsOverrideIds,
			ChangedDefaultsMigration,
			AssetPath,
			Error));
	const auto PreservedDamage = ChangedDefaultsState.GetValueInt32(TEXT("RenamedDamage"));
	const auto ChangedEnabled = ChangedDefaultsState.GetValueBool(TEXT("Enabled"));
	TestTrue(TEXT("Explicit override remains readable after a default edit"), PreservedDamage.IsValid());
	TestTrue(TEXT("Inherited value remains readable after a default edit"), ChangedEnabled.IsValid());
	if (PreservedDamage.IsValid())
	{
		TestEqual(TEXT("Explicit override ignores the new default"), PreservedDamage.GetValue(), 77);
	}
	if (ChangedEnabled.IsValid())
	{
		TestFalse(TEXT("Untouched instance adopts the new C# default"), ChangedEnabled.GetValue());
	}

	const FString IncompatibleTypeJson = UTF8_TO_TCHAR(R"JSON(
{
  "schemaVersion": "unifiedscript.script-asset/v0.1",
  "assets": [{
    "id": "us_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "qualifiedName": "Tests.State",
    "assetPath": "Tests/State",
    "states": [{
      "id": "us_11111111111111111111111111111111",
      "name": "DamageAsFloat",
      "typeMetadataName": "System.Single",
      "typeKind": "floatingPoint",
      "defaultValue": 1.25,
      "exposure": { "instanceEditable": true }
    }]
  }]
}
)JSON");
	FInstancedPropertyBag IncompatibleState;
	TArray<FGuid> IncompatibleOverrideIds;
	FFinareStateMigrationReport IncompatibleMigration;
	TestTrue(
		TEXT("Incompatible declaration type rematerializes safely"),
		FFinareDeclarationMaterializer::MaterializeJsonWithOverrides(
			IncompatibleTypeJson,
			TEXT("Tests/State"),
			ChangedDefaultsState,
			ChangedDefaultsOverrideIds,
			IncompatibleState,
			IncompatibleOverrideIds,
			IncompatibleMigration,
			AssetPath,
			Error));
	const auto DamageAsFloat = IncompatibleState.GetValueFloat(TEXT("DamageAsFloat"));
	TestTrue(TEXT("Changed type receives its new declaration value"), DamageAsFloat.IsValid());
	if (DamageAsFloat.IsValid())
	{
		TestEqual(TEXT("Incompatible override falls back to the new default"), DamageAsFloat.GetValue(), 1.25f);
	}
	TestEqual(TEXT("Incompatible override is explicitly dropped"), IncompatibleMigration.DroppedOverrideCount, 1);
	TestEqual(TEXT("Dropped override no longer remains active"), IncompatibleOverrideIds.Num(), 0);

	FInstancedPropertyBag FailedOutput = ReloadedState;
	TArray<FGuid> FailedOverrideIds = ReloadedOverrideIds;
	FFinareStateMigrationReport FailedMigration = ReloadedMigration;
	const int32 BeforeFailure = RenamedDamage.IsValid() ? RenamedDamage.GetValue() : 0;
	TestFalse(
		TEXT("Invalid declaration is rejected"),
		FFinareDeclarationMaterializer::MaterializeJsonWithOverrides(
			TEXT("{ not-json"),
			TEXT("Tests/State"),
			ReloadedState,
			ReloadedOverrideIds,
			FailedOutput,
			FailedOverrideIds,
			FailedMigration,
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
	TestEqual(TEXT("Failure keeps active override identities"), FailedOverrideIds.Num(), 1);
	TestEqual(TEXT("Failure keeps the previous migration report"), FailedMigration.PreservedOverrideCount, 1);
	TestTrue(TEXT("Invalid JSON returns a useful error"), !Error.IsEmpty());
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareCollectionDeclarationMaterializerTest,
	"Finare.Unreal.Declaration.CollectionMaterialization",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareCollectionDeclarationMaterializerTest::RunTest(const FString& Parameters)
{
	const FString CollectionJson = UTF8_TO_TCHAR(R"JSON(
{
  "schemaVersion": "unifiedscript.script-asset/v0.1",
  "assets": [{
    "id": "us_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "qualifiedName": "Tests.Collections",
    "assetPath": "Tests/Collections",
    "states": [
      {"id":"us_11111111111111111111111111111111","name":"ByteValue","typeMetadataName":"System.Byte","typeKind":"integer","defaultValue":255,"exposure":{"instanceEditable":true}},
      {"id":"us_22222222222222222222222222222222","name":"LongMax","typeMetadataName":"System.Int64","typeKind":"integer","defaultValue":9223372036854775807,"exposure":{"instanceEditable":true}},
      {"id":"us_33333333333333333333333333333333","name":"LongMin","typeMetadataName":"System.Int64","typeKind":"integer","defaultValue":-9223372036854775808,"exposure":{"instanceEditable":true}},
      {
        "id":"us_44444444444444444444444444444444","name":"Vectors","typeMetadataName":"System.Collections.Generic.IReadOnlyList<Finare.Unreal.UnrealVector>","typeKind":"array",
        "unrealType":{"kind":"array","unrealTypePath":"","unrealNativeName":"TArray","fields":[],"typeArguments":[{"typeMetadataName":"Finare.Unreal.UnrealVector","unrealType":{"kind":"struct","unrealTypePath":"/Script/CoreUObject.Vector","unrealNativeName":"Vector","fields":[{"index":0,"nativeName":"X","typeMetadataName":"System.Double"},{"index":1,"nativeName":"Y","typeMetadataName":"System.Double"},{"index":2,"nativeName":"Z","typeMetadataName":"System.Double"}]}}]},
        "defaultValue":[{"x":1,"y":2,"z":3},{"x":4,"y":5,"z":6}],"exposure":{"instanceEditable":true}
      },
      {
        "id":"us_55555555555555555555555555555555","name":"Names","typeMetadataName":"System.Collections.Generic.IReadOnlySet<Finare.Unreal.UnrealName>","typeKind":"set",
        "unrealType":{"kind":"set","unrealTypePath":"","unrealNativeName":"TSet","fields":[],"typeArguments":[{"typeMetadataName":"Finare.Unreal.UnrealName"}]},
        "defaultValue":["Alpha","Beta"],"exposure":{"instanceEditable":true}
      },
      {
        "id":"us_66666666666666666666666666666666","name":"LargeMap","typeMetadataName":"System.Collections.Generic.IReadOnlyDictionary<System.Int64, System.Int64>","typeKind":"map",
        "unrealType":{"kind":"map","unrealTypePath":"","unrealNativeName":"TMap","fields":[],"typeArguments":[{"typeMetadataName":"System.Int64"},{"typeMetadataName":"System.Int64"}]},
        "defaultValue":[{"key":9007199254740992,"value":11},{"key":9007199254740993,"value":22}],"exposure":{"instanceEditable":true}
      }
    ]
  }]
}
)JSON");

	FInstancedPropertyBag State;
	FString AssetPath;
	FString Error;
	TestTrue(
		*FString::Printf(TEXT("Collection declaration materializes: %s"), *Error),
		FFinareDeclarationMaterializer::MaterializeJson(
			CollectionJson, TEXT("Tests/Collections"), FInstancedPropertyBag(), State, AssetPath, Error));
	if (State.GetNumPropertiesInBag() != 6)
	{
		return false;
	}
	TestEqual(TEXT("Byte default is preserved exactly"), State.GetValueByte(TEXT("ByteValue")).GetValue(), uint8(255));
	TestEqual(TEXT("Int64 maximum is lossless"), State.GetValueInt64(TEXT("LongMax")).GetValue(), MAX_int64);
	TestEqual(TEXT("Int64 minimum is lossless"), State.GetValueInt64(TEXT("LongMin")).GetValue(), MIN_int64);
	const FPropertyBagPropertyDesc* ArrayDesc = State.FindPropertyDescByName(TEXT("Vectors"));
	const FPropertyBagPropertyDesc* SetDesc = State.FindPropertyDescByName(TEXT("Names"));
	const FPropertyBagPropertyDesc* MapDesc = State.FindPropertyDescByName(TEXT("LargeMap"));
	TestTrue(TEXT("Vector state is a PropertyBag array"), ArrayDesc != nullptr &&
	         ArrayDesc->ContainerTypes.GetFirstContainerType() == EPropertyBagContainerType::Array);
	TestTrue(TEXT("Name state is a PropertyBag set"), SetDesc != nullptr &&
	         SetDesc->ContainerTypes.GetFirstContainerType() == EPropertyBagContainerType::Set);
	TestTrue(TEXT("Large integer state is a PropertyBag map"), MapDesc != nullptr &&
	         MapDesc->ContainerTypes.GetFirstContainerType() == EPropertyBagContainerType::Map);
	if (ArrayDesc != nullptr && ArrayDesc->CachedProperty != nullptr)
	{
		const FArrayProperty* ArrayProperty = CastField<FArrayProperty>(ArrayDesc->CachedProperty);
		const FStructProperty* VectorProperty = ArrayProperty != nullptr
			                                        ? CastField<FStructProperty>(ArrayProperty->Inner)
			                                        : nullptr;
		FScriptArrayHelper ArrayHelper(
			ArrayProperty,
			ArrayProperty != nullptr
				? ArrayProperty->ContainerPtrToValuePtr<void>(State.GetMutableValue().GetMemory())
				: nullptr);
		TestEqual(TEXT("Vector array contains both native entries"), ArrayHelper.Num(), 2);
		if (VectorProperty != nullptr && ArrayHelper.Num() == 2)
		{
			const FVector* First = reinterpret_cast<const FVector*>(ArrayHelper.GetRawPtr(0));
			const FVector* Second = reinterpret_cast<const FVector*>(ArrayHelper.GetRawPtr(1));
			TestTrue(TEXT("First vector components are materialized"), First != nullptr &&
			         First->Equals(FVector(1.0, 2.0, 3.0), KINDA_SMALL_NUMBER));
			TestTrue(TEXT("Second vector components are materialized"), Second != nullptr &&
			         Second->Equals(FVector(4.0, 5.0, 6.0), KINDA_SMALL_NUMBER));
		}
	}
	if (MapDesc != nullptr && MapDesc->CachedProperty != nullptr)
	{
		const FMapProperty* MapProperty = CastField<FMapProperty>(MapDesc->CachedProperty);
		const FInt64Property* KeyProperty = MapProperty != nullptr
			                                    ? CastField<FInt64Property>(MapProperty->KeyProp)
			                                    : nullptr;
		const FInt64Property* ValueProperty = MapProperty != nullptr
			                                      ? CastField<FInt64Property>(MapProperty->ValueProp)
			                                      : nullptr;
		FScriptMapHelper MapHelper(
			MapProperty,
			MapProperty != nullptr
				? MapProperty->ContainerPtrToValuePtr<void>(State.GetMutableValue().GetMemory())
				: nullptr);
		TestEqual(TEXT("Large integer map contains both native pairs"), MapHelper.Num(), 2);
		TSet<int64> SeenKeys;
		for (int32 PairIndex = 0; PairIndex < MapHelper.GetMaxIndex(); ++PairIndex)
		{
			if (!MapHelper.IsValidIndex(PairIndex) || KeyProperty == nullptr || ValueProperty == nullptr)
			{
				continue;
			}
			const int64 Key = KeyProperty->GetPropertyValue(MapHelper.GetKeyPtr(PairIndex));
			const int64 Value = ValueProperty->GetPropertyValue(MapHelper.GetValuePtr(PairIndex));
			SeenKeys.Add(Key);
			TestTrue(
				TEXT("Large integer map values retain their exact key/value relationship"),
				(Key == 9007199254740992LL && Value == 11) ||
				(Key == 9007199254740993LL && Value == 22));
		}
		TestTrue(TEXT("Both adjacent large integer keys are present"),
		         SeenKeys.Contains(9007199254740992LL) && SeenKeys.Contains(9007199254740993LL));
	}
	const auto VectorText = State.GetValueSerializedString(TEXT("Vectors"));
	const auto NamesText = State.GetValueSerializedString(TEXT("Names"));
	const auto MapText = State.GetValueSerializedString(TEXT("LargeMap"));
	TestTrue(TEXT("Vector collection has both distinct entries"), VectorText.IsValid() &&
	         VectorText.GetValue().Contains(TEXT("1.000000")) && VectorText.GetValue().Contains(TEXT("4.000000")));
	TestTrue(TEXT("Set collection has both entries"), NamesText.IsValid() &&
	         NamesText.GetValue().Contains(TEXT("Alpha")) && NamesText.GetValue().Contains(TEXT("Beta")));
	TestTrue(TEXT("Map keeps adjacent integers above double precision"), MapText.IsValid() &&
	         MapText.GetValue().Contains(TEXT("9007199254740992")) && MapText.GetValue().Contains(
		         TEXT("9007199254740993")));

	const FString DuplicateMapJson = CollectionJson.Replace(
		TEXT("9007199254740993"), TEXT("9007199254740992"));
	FInstancedPropertyBag FailedOutput = State;
	FString FailedAssetPath;
	FString FailedError;
	TestFalse(
		TEXT("Duplicate native map key is rejected"),
		FFinareDeclarationMaterializer::MaterializeJson(
			DuplicateMapJson, TEXT("Tests/Collections"), State, FailedOutput, FailedAssetPath, FailedError));
	TestEqual(TEXT("Duplicate map failure keeps the previous scalar value"),
	          FailedOutput.GetValueInt64(TEXT("LongMax")).GetValue(), MAX_int64);

	const FString FractionalLongJson = CollectionJson.Replace(
		TEXT("9223372036854775807"), TEXT("1.5"));
	FInstancedPropertyBag FractionalOutput = State;
	FString FractionalAssetPath;
	FString FractionalError;
	TestFalse(
		TEXT("Fractional Int64 default is rejected"),
		FFinareDeclarationMaterializer::MaterializeJson(
			FractionalLongJson, TEXT("Tests/Collections"), State, FractionalOutput, FractionalAssetPath,
			FractionalError));
	TestEqual(TEXT("Fractional failure keeps the previous map"),
	          FractionalOutput.GetValueSerializedString(TEXT("LargeMap")).GetValue(),
	          MapText.GetValue());

	const FString NullCollectionJson = CollectionJson.Replace(
		TEXT("\"defaultValue\":[{\"x\":1,\"y\":2,\"z\":3},{\"x\":4,\"y\":5,\"z\":6}]"),
		TEXT("\"defaultValue\":null"));
	FInstancedPropertyBag NullCollectionOutput = State;
	FString NullCollectionAssetPath;
	FString NullCollectionError;
	TestFalse(
		TEXT("Null collection default is rejected"),
		FFinareDeclarationMaterializer::MaterializeJson(
			NullCollectionJson,
			TEXT("Tests/Collections"),
			State,
			NullCollectionOutput,
			NullCollectionAssetPath,
			NullCollectionError));
	TestTrue(
		TEXT("Null collection failure keeps the previous vector array"),
		NullCollectionOutput.GetValueSerializedString(TEXT("Vectors")).IsValid() &&
		NullCollectionOutput.GetValueSerializedString(TEXT("Vectors")).GetValue() == VectorText.GetValue());

	const FString OverflowJson = CollectionJson.Replace(
		TEXT("9223372036854775807"), TEXT("9223372036854775808"));
	FInstancedPropertyBag OverflowOutput = State;
	FString OverflowAssetPath;
	FString OverflowError;
	TestFalse(
		TEXT("Out of range Int64 default is rejected"),
		FFinareDeclarationMaterializer::MaterializeJson(
			OverflowJson,
			TEXT("Tests/Collections"),
			State,
			OverflowOutput,
			OverflowAssetPath,
			OverflowError));
	TestEqual(
		TEXT("Overflow failure keeps the previous Int64 maximum"),
		OverflowOutput.GetValueInt64(TEXT("LongMax")).GetValue(),
		MAX_int64);

	const FString EmptyUnhashableSetJson = CollectionJson
	                                       .Replace(
		                                       TEXT(
			                                       "System.Collections.Generic.IReadOnlySet<Finare.Unreal.UnrealName>"),
		                                       TEXT(
			                                       "System.Collections.Generic.IReadOnlySet<Finare.Unreal.UnrealText>"))
	                                       .Replace(
		                                       TEXT("\"typeMetadataName\":\"Finare.Unreal.UnrealName\""),
		                                       TEXT("\"typeMetadataName\":\"Finare.Unreal.UnrealText\""))
	                                       .Replace(
		                                       TEXT("\"defaultValue\":[\"Alpha\",\"Beta\"]"),
		                                       TEXT("\"defaultValue\":[]"));
	FInstancedPropertyBag EmptyUnhashableSetOutput = State;
	FString EmptyUnhashableSetAssetPath;
	FString EmptyUnhashableSetError;
	TestFalse(
		TEXT("Empty unhashable Set leaf is rejected"),
		FFinareDeclarationMaterializer::MaterializeJson(
			EmptyUnhashableSetJson,
			TEXT("Tests/Collections"),
			State,
			EmptyUnhashableSetOutput,
			EmptyUnhashableSetAssetPath,
			EmptyUnhashableSetError));

	const FString MalformedLeafJson = CollectionJson.Replace(
		TEXT("{\"index\":0,\"nativeName\":\"X\",\"typeMetadataName\":\"System.Double\"}"),
		TEXT("{\"index\":0,\"nativeName\":\"X\",\"typeMetadataName\":\"System.Single\"}"));
	FInstancedPropertyBag MalformedLeafOutput = State;
	FString MalformedLeafAssetPath;
	FString MalformedLeafError;
	TestFalse(
		TEXT("Malformed Vector leaf descriptor is rejected"),
		FFinareDeclarationMaterializer::MaterializeJson(
			MalformedLeafJson,
			TEXT("Tests/Collections"),
			State,
			MalformedLeafOutput,
			MalformedLeafAssetPath,
			MalformedLeafError));
	return true;
}

#endif
