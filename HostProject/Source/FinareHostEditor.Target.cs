using UnrealBuildTool;
using System.Collections.Generic;

public class FinareHostEditorTarget : TargetRules
{
    public FinareHostEditorTarget(TargetInfo Target) : base(Target)
    {
        Type = TargetType.Editor;
        DefaultBuildSettings = BuildSettingsVersion.Latest;
        IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
        ExtraModuleNames.Add("FinareHost");
    }
}
