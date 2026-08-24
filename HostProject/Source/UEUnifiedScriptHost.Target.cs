using UnrealBuildTool;
using System.Collections.Generic;

public class UEUnifiedScriptHostTarget : TargetRules
{
    public UEUnifiedScriptHostTarget(TargetInfo Target) : base(Target)
    {
        Type = TargetType.Game;
        DefaultBuildSettings = BuildSettingsVersion.Latest;
        IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
        ExtraModuleNames.Add("UEUnifiedScriptHost");
    }
}
