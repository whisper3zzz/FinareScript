using System.Text.Json;

namespace Finare.Cli;

internal sealed record ProductGuidanceDocument(
    string SchemaVersion,
    string Topic,
    string Title,
    string Path,
    string DocumentationRoot)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.product-guidance/v0.1";
}

internal static class ProductGuidanceCatalog
{
    private static readonly IReadOnlyDictionary<string, (string Title, string FileName)> Topics =
        new Dictionary<string, (string Title, string FileName)>(StringComparer.Ordinal)
        {
            ["index"] = ("Finare documentation", "DOCUMENTATION.md"),
            ["first-project"] = ("First project golden path", "UNREAL_FIRST_PROJECT.md"),
            ["recipes"] = ("Gameplay task recipes", "UNREAL_GAMEPLAY_RECIPES.md"),
        };

    public static ProductGuidanceDocument Resolve(
        string topic,
        string? sdkRoot = null,
        string? executableDirectory = null)
    {
        if (!Topics.TryGetValue(topic, out (string Title, string FileName) entry))
        {
            throw new ArgumentException(
                $"Unknown guide topic '{topic}'. Expected index, first-project, or recipes.",
                nameof(topic));
        }

        string documentationRoot = ResolveDocumentationRoot(
            sdkRoot,
            executableDirectory ?? AppContext.BaseDirectory);
        string path = Path.Combine(documentationRoot, entry.FileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Finare guide '{topic}' is missing from '{documentationRoot}'.",
                path);
        }

        return new ProductGuidanceDocument(
            ProductGuidanceDocument.CurrentSchemaVersion,
            topic,
            entry.Title,
            Path.GetFullPath(path),
            documentationRoot);
    }

    public static string Serialize(ProductGuidanceDocument document) =>
        JsonSerializer.Serialize(
            document,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            });

    private static string ResolveDocumentationRoot(
        string? sdkRoot,
        string executableDirectory)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(sdkRoot))
        {
            string root = Path.GetFullPath(sdkRoot);
            candidates.Add(Path.Combine(root, "Documentation"));
            candidates.Add(Path.Combine(
                root,
                "Plugins",
                "Finare",
                "Tools",
                "Documentation"));
        }
        else
        {
            DirectoryInfo? directory = new(Path.GetFullPath(executableDirectory));
            for (int depth = 0;
                directory is not null && depth < 8;
                depth++, directory = directory.Parent)
            {
                candidates.Add(Path.Combine(directory.FullName, "Documentation"));
                candidates.Add(Path.Combine(directory.FullName, "docs"));
            }
        }

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(candidate, "DOCUMENTATION.md")))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException(
            "Finare documentation was not found. Run this command from an extracted SDK or " +
            "an adopted project's Plugins/Finare/Tools directory, or pass --sdk-root.");
    }
}
