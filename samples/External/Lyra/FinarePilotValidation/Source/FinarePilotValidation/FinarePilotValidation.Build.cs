using UnrealBuildTool;

public class FinarePilotValidation : ModuleRules
{
    public FinarePilotValidation(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PrivateDependencyModuleNames.AddRange(new[]
        {
            "Core",
            "CoreUObject",
            "Engine",
            "UnrealEd",
        });
    }
}
