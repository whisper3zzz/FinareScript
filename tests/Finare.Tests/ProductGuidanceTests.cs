using System.Text.Json;
using System.Text.RegularExpressions;
using Finare.Cli;

namespace Finare.Tests;

[Trait("Category", "Delivery")]
public sealed partial class ProductGuidanceTests
{
    [Fact]
    public void CatalogResolvesPackagedAndProjectLocalDocumentation()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "FinareGuidanceTests",
            Guid.NewGuid().ToString("N"));
        string documentation = Path.Combine(root, "Documentation");
        string cliDirectory = Path.Combine(root, "Tools", "Finare.Cli");
        Directory.CreateDirectory(documentation);
        Directory.CreateDirectory(cliDirectory);
        try
        {
            foreach (string fileName in new[]
                     {
                         "DOCUMENTATION.md",
                         "UNREAL_FIRST_PROJECT.md",
                         "UNREAL_GAMEPLAY_RECIPES.md",
                     })
            {
                File.WriteAllText(Path.Combine(documentation, fileName), $"# {fileName}\n");
            }

            ProductGuidanceDocument explicitSdk = ProductGuidanceCatalog.Resolve(
                "first-project",
                root,
                Path.GetTempPath());
            ProductGuidanceDocument discovered = ProductGuidanceCatalog.Resolve(
                "recipes",
                executableDirectory: cliDirectory);

            Assert.Equal(
                ProductGuidanceDocument.CurrentSchemaVersion,
                explicitSdk.SchemaVersion);
            Assert.Equal("first-project", explicitSdk.Topic);
            Assert.Equal(
                Path.Combine(documentation, "UNREAL_FIRST_PROJECT.md"),
                explicitSdk.Path);
            Assert.Equal("recipes", discovered.Topic);
            Assert.Equal(
                Path.Combine(documentation, "UNREAL_GAMEPLAY_RECIPES.md"),
                discovered.Path);

            using JsonDocument json = JsonDocument.Parse(
                ProductGuidanceCatalog.Serialize(discovered));
            Assert.Equal(
                ProductGuidanceDocument.CurrentSchemaVersion,
                json.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal(discovered.Path, json.RootElement.GetProperty("path").GetString());
            Assert.Throws<ArgumentException>(() => ProductGuidanceCatalog.Resolve(
                "unknown",
                root,
                cliDirectory));
            Assert.Throws<FileNotFoundException>(() => ProductGuidanceCatalog.Resolve(
                "index",
                Path.Combine(root, "missing-sdk"),
                cliDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PackagedGuidanceOwnsTheGoldenPathAndEveryGameplayRecipe()
    {
        string repository = FindRepositoryRoot();
        string firstProjectPath = Path.Combine(
            repository,
            "docs",
            "UNREAL_FIRST_PROJECT.md");
        string recipesPath = Path.Combine(
            repository,
            "docs",
            "UNREAL_GAMEPLAY_RECIPES.md");
        string firstProject = File.ReadAllText(firstProjectPath);
        string recipes = File.ReadAllText(recipesPath);
        string documentationIndex = File.ReadAllText(Path.Combine(
            repository,
            "docs",
            "DOCUMENTATION.md"));
        string sdkPackager = File.ReadAllText(Path.Combine(
            repository,
            "tools",
            "package-finare-sdk.ps1"));
        using JsonDocument release = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            repository,
            "release",
            "finare.release.json")));

        string[] expectedReleaseDocuments =
        [
            "docs/UNREAL_FIRST_PROJECT.md",
            "docs/UNREAL_GAMEPLAY_RECIPES.md",
        ];
        string[] releaseDocuments = release.RootElement
            .GetProperty("documentation")
            .EnumerateArray()
            .Select(static document => document.GetProperty("path").GetString()!)
            .ToArray();
        foreach (string path in expectedReleaseDocuments)
        {
            Assert.Contains(path, releaseDocuments);
            Assert.Contains(Path.GetFileName(path), sdkPackager, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(path), documentationIndex, StringComparison.Ordinal);
        }

        foreach (string token in new[]
                 {
                     "guide first-project",
                     "adopt $project --sdk-root $sdk --preview",
                     "doctor $project --engine-root $engine",
                     "setup-unreal-project.ps1",
                     "start-finare-authoring.ps1",
                     "Build & Reload",
                 })
        {
            Assert.Contains(token, firstProject, StringComparison.Ordinal);
        }

        foreach (string heading in new[]
                 {
                     "## Actor behavior",
                     "## Component behavior",
                     "## Blueprint event",
                     "## Asset load",
                     "## Spawn Actor",
                     "## Trace and FHitResult",
                     "## UI bridge",
                     "## Async work",
                     "## Packaging",
                 })
        {
            Assert.Contains(heading, recipes, StringComparison.Ordinal);
        }
        Assert.Contains("Unreal API Browser", recipes, StringComparison.Ordinal);
        Assert.Contains("Build, Restart & Continue", recipes, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet run --project", firstProject, StringComparison.Ordinal);
        Assert.DoesNotContain("HostProject", firstProject, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet run --project", recipes, StringComparison.Ordinal);
        Assert.DoesNotContain("HostProject", recipes, StringComparison.Ordinal);

        AssertMarkdownLinksResolve(firstProjectPath, firstProject);
        AssertMarkdownLinksResolve(recipesPath, recipes);
    }

    private static void AssertMarkdownLinksResolve(string sourcePath, string markdown)
    {
        string sourceDirectory = Path.GetDirectoryName(sourcePath)!;
        foreach (Match match in MarkdownLink().Matches(markdown))
        {
            string target = match.Groups[1].Value.Split('#', 2)[0];
            if (target.Length == 0 || Uri.TryCreate(target, UriKind.Absolute, out _))
            {
                continue;
            }
            Assert.True(
                File.Exists(Path.GetFullPath(Path.Combine(sourceDirectory, target))),
                $"Broken Markdown link '{target}' in '{sourcePath}'.");
        }
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

    [GeneratedRegex(@"\[[^\]]+\]\(([^)]+\.md(?:#[^)]+)?)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLink();
}
