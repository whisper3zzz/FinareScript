#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptAsset.h"

#include "GameFramework/Actor.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"
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
    Asset->ApiNodeCatalogPath = TEXT("  {PluginDir}/Binaries/Managed/Demo/Demo.api-nodes.json  ");
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

    Asset->CompiledChunkPath =
        TEXT("  {PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.uschunk  ");
    TestTrue(
        TEXT("Compiled chunk takes precedence over loose assembly fallback"),
        Asset->ResolveConfiguration(AssemblyPath, TypeName, Error));
    TestEqual(
        TEXT("Compiled chunk path is normalized"),
        AssemblyPath,
        FString(TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.uschunk")));
    TestEqual(
        TEXT("Compiled chunk accessor is normalized"),
        Asset->ResolveCompiledChunkPath(),
        FString(TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.uschunk")));
    TestTrue(
        TEXT("Loose compatibility configuration remains explicitly selectable"),
        Asset->ResolveConfiguration(false, AssemblyPath, TypeName, Error));
    TestEqual(
        TEXT("Loose compatibility selection resolves the assembly"),
        AssemblyPath,
        FString(TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll")));
    TestTrue(
        TEXT("Runtime configuration expands deployment placeholders"),
        Asset->ResolveRuntimeConfiguration(true, AssemblyPath, TypeName, Error));
    TestFalse(TEXT("Runtime program path is absolute"), FPaths::IsRelative(AssemblyPath));
    TestFalse(
        TEXT("Runtime program path contains no plugin placeholder"),
        AssemblyPath.Contains(TEXT("{PluginDir}"), ESearchCase::IgnoreCase));
    TestTrue(
        TEXT("Runtime configuration keeps the selected compiled chunk"),
        AssemblyPath.EndsWith(
            TEXT("Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.uschunk"),
            ESearchCase::IgnoreCase));

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
        TEXT("API node catalog path is normalized"),
        Asset->ResolveApiNodeCatalogPath(),
        FString(TEXT("{PluginDir}/Binaries/Managed/Demo/Demo.api-nodes.json")));
    TestEqual(
        TEXT("Authoring server URL is normalized"),
        Asset->ResolveAuthoringServerUrl(),
        FString(TEXT("http://127.0.0.1:4173")));

    Asset->RequiredTargetClass = AActor::StaticClass();
    AActor* CompatibleTarget = NewObject<AActor>();
    TestTrue(
        TEXT("Compatible host target is accepted"),
        Asset->ValidateTarget(CompatibleTarget, Error));
    TestFalse(
        TEXT("Incompatible host target is rejected before managed startup"),
        Asset->ValidateTarget(Asset, Error));
    TestTrue(
        TEXT("Target mismatch identifies the required class"),
        Error.Contains(TEXT("Actor")));
    TestEqual(
        TEXT("Required target path is stable"),
        Asset->ResolveRequiredTargetClassPath(),
        AActor::StaticClass()->GetPathName());

    TestTrue(
        TEXT("Persistent class GUID parses"),
        FGuid::ParseExact(
            TEXT("0123456789abcdef0123456789abcdef"),
            EGuidFormats::Digits,
            Asset->ScriptClassId));
    TestTrue(
        TEXT("Exact declaration class identity is accepted"),
        Asset->ValidateDeclarationIdentity(
            Asset->ScriptClassId,
            TEXT("UnifiedScript.Unreal.Demo.DemoScript"),
            Error));
    FGuid StaleClassId;
    TestTrue(
        TEXT("Stale class GUID parses"),
        FGuid::ParseExact(
            TEXT("fedcba9876543210fedcba9876543210"),
            EGuidFormats::Digits,
            StaleClassId));
    TestFalse(
        TEXT("Stale declaration class identity is rejected"),
        Asset->ValidateDeclarationIdentity(
            StaleClassId,
            TEXT("UnifiedScript.Unreal.Demo.DemoScript"),
            Error));
    TestTrue(TEXT("Stale identity failure is explicit"), Error.Contains(TEXT("stale")));

    Asset->ScriptClassKind = EUnifiedScriptClassKind::Actor;
    Asset->GeneratedClassPath = FSoftClassPath(AActor::StaticClass());
    TestTrue(
        TEXT("Exact generated Actor class is accepted"),
        Asset->ValidateGeneratedClassIdentity(Asset, CompatibleTarget, Error));
    TestFalse(
        TEXT("A similarly configured non-Actor target is rejected"),
        Asset->ValidateGeneratedClassIdentity(Asset, Asset, Error));

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
