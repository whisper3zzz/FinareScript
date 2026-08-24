using UnrealBuildTool;
using System.Collections.Generic;

public class UEUnifiedScriptHostEditorTarget : TargetRules
{
    public UEUnifiedScriptHostEditorTarget(TargetInfo Target) : base(Target)
    {
        Type = TargetType.Editor;
        DefaultBuildSettings = BuildSettingsVersion.Latest;
        IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
        ExtraModuleNames.Add("UEUnifiedScriptHost");
    }
}
