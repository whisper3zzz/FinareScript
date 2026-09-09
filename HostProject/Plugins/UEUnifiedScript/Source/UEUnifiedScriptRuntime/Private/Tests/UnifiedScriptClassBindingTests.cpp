#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptClassBinding.h"

#include "Dom/JsonObject.h"
#include "GameFramework/Actor.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptDeclarationMaterializer.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptClassBindingDeclarationTest,
    "UnifiedScript.Unreal.ScriptAsset.ClassBindingDeclaration",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptClassBindingDeclarationTest::RunTest(const FString& Parameters)
{
    const TSharedPtr<FJsonObject> Asset = MakeShared<FJsonObject>();
    const TSharedPtr<FJsonObject> BindingObject = MakeShared<FJsonObject>();
    BindingObject->SetStringField(TEXT("kind"), TEXT("Actor"));
    BindingObject->SetStringField(
        TEXT("baseClassPath"), TEXT("/Script/Engine.Actor"));
    BindingObject->SetStringField(
        TEXT("generatedClassPath"), TEXT("/Game/Test/BP_Test.BP_Test_C"));
    Asset->SetObjectField(TEXT("classBinding"), BindingObject);

    TOptional<FUnifiedScriptClassBindingDeclaration> Binding;
    FString Error;
    TestTrue(
        TEXT("Exact Actor class binding is accepted"),
        FUnifiedScriptClassBindingDeclaration::ReadJson(Asset, Binding, Error));
    TestTrue(TEXT("Class binding is present"), Binding.IsSet());
    if (Binding.IsSet())
    {
        TestEqual(TEXT("Binding kind is preserved case-sensitively"), Binding->Kind, FString(TEXT("Actor")));
        TestEqual(
            TEXT("Binding base path is preserved"),
            Binding->BaseClassPath.GetValue(),
            FString(TEXT("/Script/Engine.Actor")));
    }

    TArray<TSharedPtr<FJsonValue>> Assets;
    Assets.Add(MakeShared<FJsonValueObject>(Asset));
    TestTrue(
        TEXT("v0.3 validates every asset binding"),
        ValidateUnifiedScriptClassBindingsJson(
            Assets,
            TEXT("unifiedscript.script-asset/v0.3"),
            Error));

    BindingObject->SetStringField(TEXT("kind"), TEXT("actor"));
    TestFalse(
        TEXT("Lowercase binding kind is rejected"),
        FUnifiedScriptClassBindingDeclaration::ReadJson(Asset, Binding, Error));
    TestTrue(TEXT("Lowercase kind failure is explicit"), Error.Contains(TEXT("unknown")));

    BindingObject->SetStringField(TEXT("kind"), TEXT("Actor"));
    BindingObject->SetStringField(TEXT("generatedClassPath"), TEXT("/Game/Test/BP_Test.BP_Test"));
    TestFalse(
        TEXT("Blueprint class path without _C is rejected"),
        FUnifiedScriptClassBindingDeclaration::ReadJson(Asset, Binding, Error));

    BindingObject->SetStringField(TEXT("generatedClassPath"), TEXT("/Script/Engine.Actor.Other"));
    TestFalse(
        TEXT("Native class path with a module/object shape mismatch is rejected"),
        FUnifiedScriptClassBindingDeclaration::ReadJson(Asset, Binding, Error));

    BindingObject->SetStringField(TEXT("generatedClassPath"), TEXT("/Game/Test/BP_Test.BP_Test_C"));
    TestTrue(
        TEXT("v0.1 rejects class binding metadata"),
        !ValidateUnifiedScriptClassBindingsJson(
            Assets,
            TEXT("unifiedscript.script-asset/v0.1"),
            Error));
    TestTrue(
        TEXT("v0.2 rejects class binding metadata"),
        !ValidateUnifiedScriptClassBindingsJson(
            Assets,
            TEXT("unifiedscript.script-asset/v0.2"),
            Error));

    Asset->SetField(TEXT("classBinding"), MakeShared<FJsonValueNull>());
    TestTrue(
        TEXT("Explicit null binding remains legacy-compatible"),
        FUnifiedScriptClassBindingDeclaration::ReadJson(Asset, Binding, Error));
    TestFalse(TEXT("Explicit null does not produce a binding"), Binding.IsSet());

    UUnifiedScriptAsset* ScriptAsset = NewObject<UUnifiedScriptAsset>(GetTransientPackage());
    ScriptAsset->ScriptClassId = FGuid(1, 2, 3, 4);
    ScriptAsset->ScriptClassKind = EUnifiedScriptClassKind::Actor;
    ScriptAsset->GeneratedClassPath = FSoftClassPath(AActor::StaticClass());
    FUnifiedScriptClassBindingDeclaration OwnerBinding;
    OwnerBinding.Kind = TEXT("Actor");
    TOptional<FUnifiedScriptClassBindingDeclaration> OwnerBindingOptional = OwnerBinding;
    TestTrue(
        TEXT("Persistent Actor binding validates against its generated owner"),
        ScriptAsset->ValidateDeclarationClassBinding(OwnerBindingOptional, Error));
    ScriptAsset->GeneratedClassPath = FSoftClassPath();
    TestFalse(
        TEXT("Persistent binding without a generated owner is rejected"),
        ScriptAsset->ValidateDeclarationClassBinding(OwnerBindingOptional, Error));
    ScriptAsset->GeneratedClassPath = FSoftClassPath(AActor::StaticClass());
    ScriptAsset->ScriptClassKind = EUnifiedScriptClassKind::Object;
    OwnerBinding.Kind = TEXT("Object");
    OwnerBindingOptional = OwnerBinding;
    TestFalse(
        TEXT("Object binding cannot use an Actor owner"),
        ScriptAsset->ValidateDeclarationClassBinding(OwnerBindingOptional, Error));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptClassBindingFixtureReaderTest,
    "UnifiedScript.Unreal.ScriptAsset.ClassBindingFixtureReader",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptClassBindingFixtureReaderTest::RunTest(const FString& Parameters)
{
    const FString FixtureRoot = FPaths::ConvertRelativePathToFull(FPaths::Combine(
        FPaths::ProjectDir(),
        TEXT("../artifacts/m13-3c-4-class-composition/class-binding")));
    const FString JsonPath = FPaths::Combine(FixtureRoot, TEXT("class-binding-fixture.asset.json"));
    const FString ChunkPath = FPaths::Combine(FixtureRoot, TEXT("class-binding-fixture.uschunk"));
    FString Json;
    TArray<uint8> Chunk;
    if (!TestTrue(TEXT("Loads actual emitted class binding JSON"),
            FFileHelper::LoadFileToString(Json, *JsonPath)) ||
        !TestTrue(TEXT("Loads actual emitted class binding chunk"),
            FFileHelper::LoadFileToArray(Chunk, *ChunkPath)))
    {
        return false;
    }

    FInstancedPropertyBag JsonState;
    TArray<FGuid> JsonOverrides;
    FUnifiedScriptStateMigrationReport JsonMigration;
    FString JsonAssetPath;
    FString Error;
    const bool bJsonMaterialized =
        FUnifiedScriptDeclarationMaterializer::MaterializeJsonWithOverrides(
            Json,
            TEXT("Game/ClassBindingFixture"),
            FInstancedPropertyBag(),
            {},
            JsonState,
            JsonOverrides,
            JsonMigration,
            JsonAssetPath,
            Error);
    TestTrue(*FString::Printf(TEXT("Actual JSON materializes: %s"), *Error), bJsonMaterialized);
    TestTrue(TEXT("JSON report includes class binding"), JsonMigration.ClassBinding.IsSet());

    FString InvalidJson = Json;
    InvalidJson.ReplaceInline(TEXT("\"kind\": \"Actor\""), TEXT("\"kind\": \"actor\""));
    FUnifiedScriptStateMigrationReport PreservedMigration;
    PreservedMigration.AssetQualifiedName = TEXT("sentinel");
    FString PreservedAssetPath = TEXT("sentinel");
    FString InvalidError;
    TestFalse(
        TEXT("Invalid class binding is rejected before JSON state publication"),
        FUnifiedScriptDeclarationMaterializer::MaterializeJsonWithOverrides(
            InvalidJson,
            TEXT("Game/ClassBindingFixture"),
            FInstancedPropertyBag(),
            {},
            JsonState,
            JsonOverrides,
            PreservedMigration,
            PreservedAssetPath,
            InvalidError));
    TestEqual(TEXT("Invalid class binding preserves output asset path"),
        PreservedAssetPath, FString(TEXT("sentinel")));

    FString DowngradedJson = Json;
    DowngradedJson.ReplaceInline(
        TEXT("unifiedscript.script-asset/v0.3"),
        TEXT("unifiedscript.script-asset/v0.2"));
    PreservedAssetPath = TEXT("sentinel-downgrade");
    InvalidError.Reset();
    TestFalse(
        TEXT("Binding metadata cannot be carried by the v0.2 JSON schema"),
        FUnifiedScriptDeclarationMaterializer::MaterializeJsonWithOverrides(
            DowngradedJson,
            TEXT("Game/ClassBindingFixture"),
            FInstancedPropertyBag(),
            {},
            JsonState,
            JsonOverrides,
            PreservedMigration,
            PreservedAssetPath,
            InvalidError));
    TestEqual(TEXT("Schema downgrade preserves output asset path"),
        PreservedAssetPath,
        FString(TEXT("sentinel-downgrade")));

    FInstancedPropertyBag ChunkState;
    TArray<FGuid> ChunkOverrides;
    FUnifiedScriptStateMigrationReport ChunkMigration;
    FString ChunkAssetPath;
    Error.Reset();
    const bool bChunkMaterialized =
        FUnifiedScriptDeclarationMaterializer::MaterializeChunkFileWithOverrides(
            ChunkPath,
            TEXT("Game/ClassBindingFixture"),
            FInstancedPropertyBag(),
            {},
            ChunkState,
            ChunkOverrides,
            ChunkMigration,
            ChunkAssetPath,
            Error);
    TestTrue(*FString::Printf(TEXT("Actual chunk materializes: %s"), *Error), bChunkMaterialized);
    TestTrue(TEXT("Chunk report includes class binding"), ChunkMigration.ClassBinding.IsSet());
    if (JsonMigration.ClassBinding.IsSet() && ChunkMigration.ClassBinding.IsSet())
    {
        TestEqual(TEXT("JSON and chunk binding kinds agree"),
            JsonMigration.ClassBinding->Kind,
            ChunkMigration.ClassBinding->Kind);
        TestEqual(TEXT("JSON and chunk binding base paths agree"),
            JsonMigration.ClassBinding->BaseClassPath.GetValue(),
            ChunkMigration.ClassBinding->BaseClassPath.GetValue());
        TestEqual(TEXT("JSON and chunk selected asset paths agree"), JsonAssetPath, ChunkAssetPath);
        TestEqual(TEXT("JSON and chunk asset identities agree"), JsonMigration.AssetId, ChunkMigration.AssetId);
        TestEqual(
            TEXT("JSON and chunk qualified names agree"),
            JsonMigration.AssetQualifiedName,
            ChunkMigration.AssetQualifiedName);
        TestEqual(TEXT("JSON and chunk state layouts agree"),
            JsonState.GetNumPropertiesInBag(),
            ChunkState.GetNumPropertiesInBag());
    }
    return true;
}

#endif
