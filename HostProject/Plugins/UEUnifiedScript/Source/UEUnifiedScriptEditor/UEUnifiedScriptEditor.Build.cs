using UnrealBuildTool;

public class UEUnifiedScriptEditor : ModuleRules
{
    public UEUnifiedScriptEditor(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PrivateDependencyModuleNames.AddRange(new[]
        {
            "Core",
            "CoreUObject",
            "ApplicationCore",
            "AssetDefinition",
            "AssetTools",
            "AssetRegistry",
            "BlueprintGraph",
            "ContentBrowser",
            "DesktopPlatform",
            "Engine",
            "EngineAssetDefinitions",
            "EnhancedInput",
            "GraphEditor",
            "HTTP",
            "Json",
            "KismetCompiler",
            "MessageLog",
            "InputCore",
            "Projects",
            "Slate",
            "SlateCore",
            "ToolMenus",
            "UnrealEd",
            "UMG",
            "WorkspaceMenuStructure",
            "UEUnifiedScriptRuntime"
        });
        AddEngineThirdPartyPrivateStaticDependencies(Target, "OpenSSL");
    }
}
