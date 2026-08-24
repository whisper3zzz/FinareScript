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
            "Engine",
            "Json",
            "UEUnifiedScriptRuntime"
        });
    }
}
