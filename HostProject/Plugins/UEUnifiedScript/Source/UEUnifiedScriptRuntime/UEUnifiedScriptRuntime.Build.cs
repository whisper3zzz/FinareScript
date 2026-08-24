using UnrealBuildTool;

public class UEUnifiedScriptRuntime : ModuleRules
{
    public UEUnifiedScriptRuntime(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PublicDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine" });
        PrivateDependencyModuleNames.AddRange(new[] { "Json", "Projects" });
    }
}
