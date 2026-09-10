using UnrealBuildTool;

public class FinareHost : ModuleRules
{
    public FinareHost(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PublicDependencyModuleNames.AddRange(new[]
        {
            "Core",
            "CoreUObject",
            "Engine",
            "EnhancedInput",
            "UMG",
            "FinareRuntime"
        });
        PrivateDependencyModuleNames.Add("Json");
    }
}
