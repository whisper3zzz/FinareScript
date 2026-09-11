using System.Text.Json;

namespace Finare.Tests;

public sealed class UnrealOnboardingSurfaceTests
{
    [Fact]
    public void OnboardingTemplateAndSetupPublishSurfaceBesideChunkAndDeclaration()
    {
        string repository = FindRepositoryRoot();
        string template = File.ReadAllText(Path.Combine(
            repository,
            "templates",
            "UnrealProject",
            "Config",
            "Finare.onboarding.json.template"));
        using JsonDocument templateJson = JsonDocument.Parse(template.Replace(
            "__EDITOR_TARGET__", "FixtureEditor", StringComparison.Ordinal)
            .Replace("__GAME_TARGET__", "FixtureGame", StringComparison.Ordinal)
            .Replace("__PROJECT_NAME__", "Fixture", StringComparison.Ordinal)
            .Replace("__ASSEMBLY_NAME__", "Fixture.Game", StringComparison.Ordinal));
        JsonElement chunk = templateJson.RootElement
            .GetProperty("managed")
            .GetProperty("chunks")
            .EnumerateArray()
            .Single();
        string stagePath = chunk.GetProperty("stagePath").GetString()!;
        string normalizedStagePath = stagePath.Replace('\\', '/');
        Assert.EndsWith("Plugins/Finare/Binaries/Managed/Scripts/Fixture.Game.uschunk", normalizedStagePath, StringComparison.Ordinal);

        string deployment = File.ReadAllText(Path.Combine(
            repository,
            "templates",
            "UnrealProject",
            "Deployment",
            "Project.finare-deployment.json.template"));
        using JsonDocument deploymentJson = JsonDocument.Parse(deployment.Replace(
            "__PROJECT_NAME__", "Fixture", StringComparison.Ordinal)
            .Replace("__ASSEMBLY_NAME__", "Fixture.Game", StringComparison.Ordinal)
            .Replace("__MANAGED_NAMESPACE__", "Fixture.Gameplay", StringComparison.Ordinal)
            .Replace("__SCRIPT_NAME__", "FirstScript", StringComparison.Ordinal));
        JsonElement script = deploymentJson.RootElement
            .GetProperty("scripts")
            .EnumerateArray()
            .Single();
        string pluginRoot = "Plugins/Finare/";
        string stagedRelativePath = normalizedStagePath[(normalizedStagePath.IndexOf(pluginRoot, StringComparison.Ordinal) + pluginRoot.Length)..];
        Assert.Equal(
            Path.ChangeExtension(stagedRelativePath, ".asset.json"),
            script.GetProperty("declarationPath").GetString()!["{PluginDir}/".Length..]);
        Assert.Equal(
            Path.ChangeExtension(stagedRelativePath, ".surface.json"),
            script.GetProperty("authoringSurfacePath").GetString()!["{PluginDir}/".Length..]);

        string setup = File.ReadAllText(Path.Combine(repository, "tools", "setup-unreal-project.ps1"));
        Assert.Contains("Compiled managed authoring surface", setup, StringComparison.Ordinal);
        Assert.Contains("[IO.Path]::ChangeExtension($chunk.stagePath, '.surface.json')", setup, StringComparison.Ordinal);
        Assert.Contains("ValidateOnly prerequisite", setup, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "Finare.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return directory.FullName;
    }
}
