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
            "GraphEditor",
            "HTTP",
            "Json",
            "MessageLog",
            "InputCore",
            "Projects",
            "Slate",
            "SlateCore",
            "ToolMenus",
            "UnrealEd",
            "WorkspaceMenuStructure",
            "UEUnifiedScriptRuntime"
        });
    }
}
