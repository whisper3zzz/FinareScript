using UnrealBuildTool;

public class UEUnifiedScriptHost : ModuleRules
{
    public UEUnifiedScriptHost(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PublicDependencyModuleNames.AddRange(new[]
        {
            "Core",
            "CoreUObject",
            "Engine",
            "UEUnifiedScriptRuntime"
        });
    }
}
