using UnrealBuildTool;
using System.Collections.Generic;

public class FinareHostTarget : TargetRules
{
    public FinareHostTarget(TargetInfo Target) : base(Target)
    {
        Type = TargetType.Game;
        DefaultBuildSettings = BuildSettingsVersion.Latest;
        IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
        ExtraModuleNames.Add("FinareHost");
    }
}
