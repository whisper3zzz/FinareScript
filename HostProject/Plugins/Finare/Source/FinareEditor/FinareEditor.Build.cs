using UnrealBuildTool;

public class FinareEditor : ModuleRules
{
    public FinareEditor(ReadOnlyTargetRules Target) : base(Target)
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
            "DirectoryWatcher",
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
            "TypedElementFramework",
            "UnrealEd",
            "UMG",
            "WorkspaceMenuStructure",
            "FinareRuntime"
        });
        AddEngineThirdPartyPrivateStaticDependencies(Target, "OpenSSL");
    }
}
