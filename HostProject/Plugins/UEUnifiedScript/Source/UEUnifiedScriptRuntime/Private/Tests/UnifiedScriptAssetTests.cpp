#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptAsset.h"

#include "Misc/AutomationTest.h"
#include "UObject/UObjectGlobals.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptAssetConfigurationTest,
    "UnifiedScript.Unreal.ScriptAsset.Configuration",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptAssetConfigurationTest::RunTest(const FString& Parameters)
{
    UUnifiedScriptAsset* Asset = NewObject<UUnifiedScriptAsset>();
    TestNotNull(TEXT("Transient Script Asset can be created"), Asset);
    if (Asset == nullptr)
    {
        return false;
    }

    FString AssemblyPath;
    FString TypeName;
    FString Error;
    TestFalse(
        TEXT("Incomplete Script Asset is rejected"),
        Asset->ResolveConfiguration(AssemblyPath, TypeName, Error));
    TestTrue(TEXT("Incomplete asset reports a useful error"), !Error.IsEmpty());

    Asset->ScriptAssemblyPath =
        TEXT("  {PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll  ");
    Asset->ScriptTypeName = TEXT("  UnifiedScript.Unreal.Demo.DemoScript  ");
    Asset->DeclarationPath = TEXT("  {PluginDir}/Binaries/Managed/Demo/Demo.asset.json  ");
    Asset->DeclarationAssetPath = TEXT("  Unreal/Demo  ");
    Asset->AuthoringSurfacePath = TEXT("  {PluginDir}/Binaries/Managed/Demo/Demo.surface.json  ");
    Asset->AuthoringServerUrl = TEXT("  http://127.0.0.1:4173///  ");
    TestTrue(
        TEXT("Configured Script Asset resolves"),
        Asset->ResolveConfiguration(AssemblyPath, TypeName, Error));
    TestEqual(
        TEXT("Asset assembly path is normalized"),
        AssemblyPath,
        FString(TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll")));
    TestEqual(
        TEXT("Asset type name is normalized"),
        TypeName,
        FString(TEXT("UnifiedScript.Unreal.Demo.DemoScript")));

    FString DeclarationPath;
    FString DeclarationAssetPath;
    Asset->ResolveDeclarationConfiguration(DeclarationPath, DeclarationAssetPath);
    TestEqual(
        TEXT("Declaration path is normalized"),
        DeclarationPath,
        FString(TEXT("{PluginDir}/Binaries/Managed/Demo/Demo.asset.json")));
    TestEqual(
        TEXT("Declaration asset key is normalized"),
        DeclarationAssetPath,
        FString(TEXT("Unreal/Demo")));
    TestEqual(
        TEXT("Authoring Surface path is normalized"),
        Asset->ResolveAuthoringSurfacePath(),
        FString(TEXT("{PluginDir}/Binaries/Managed/Demo/Demo.surface.json")));
    TestEqual(
        TEXT("Authoring server URL is normalized"),
        Asset->ResolveAuthoringServerUrl(),
        FString(TEXT("http://127.0.0.1:4173")));

    Asset->SchemaVersion = 2;
    TestFalse(
        TEXT("Unknown Script Asset schema is rejected"),
        Asset->ResolveConfiguration(AssemblyPath, TypeName, Error));
    TestTrue(
        TEXT("Schema failure is explicit"),
        Error.Contains(TEXT("schema version 2")));
    return true;
}

#endif
