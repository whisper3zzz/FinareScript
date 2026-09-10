using System;
using System.IO;
using UnrealBuildTool;

public class FinareRuntime : ModuleRules
{
    public FinareRuntime(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PublicDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine", "EnhancedInput" });
        PrivateDependencyModuleNames.AddRange(new[] { "InputCore", "Json", "Projects", "NetCore" });
        AddEngineThirdPartyPrivateStaticDependencies(Target, "OpenSSL");

        RuntimeDependencies.Add("$(PluginDir)/Binaries/Managed/...", StagedFileType.NonUFS);
        RuntimeDependencies.Add("$(PluginDir)/Resources/Managed/...", StagedFileType.NonUFS);

        if (Target.Platform == UnrealTargetPlatform.Win64)
        {
            StageBundledDotNetDirectory("host/fxr");
            StageBundledDotNetDirectory("shared/Microsoft.NETCore.App");
        }
    }

    private void StageBundledDotNetDirectory(string RelativeDirectory)
    {
        string DotNetRoot = Path.Combine(
            EngineDirectory,
            "Binaries",
            "ThirdParty",
            "DotNet",
            "10.0",
            "win-x64");
        string SourceDirectory = Path.Combine(
            DotNetRoot,
            RelativeDirectory.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(SourceDirectory))
        {
            throw new BuildException(
                $"Finare requires the engine-bundled .NET 10 runtime: {SourceDirectory}");
        }

        foreach (string SourceFile in Directory.GetFiles(
                     SourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            string RelativeFile = SourceFile.Substring(DotNetRoot.Length + 1);
            string StagedFile = Path.Combine(
                "$(PluginDir)",
                "Binaries",
                "ThirdParty",
                "DotNet",
                "10.0",
                "win-x64",
                RelativeFile);
            RuntimeDependencies.Add(StagedFile, SourceFile, StagedFileType.NonUFS);
        }
    }
}
