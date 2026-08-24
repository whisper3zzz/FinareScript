using UnrealBuildTool;
using System.Collections.Generic;

public class UEUnifiedScriptHostEditorTarget : TargetRules
{
    public UEUnifiedScriptHostEditorTarget(TargetInfo Target) : base(Target)
    {
        Type = TargetType.Editor;
        DefaultBuildSettings = BuildSettingsVersion.V6;
        IncludeOrderVersion = EngineIncludeOrderVersion.Unreal5_7;
        ExtraModuleNames.Add("UEUnifiedScriptHost");
    }
}
