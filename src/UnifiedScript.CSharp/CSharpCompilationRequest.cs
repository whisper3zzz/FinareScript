using System.Security.Cryptography;
using UnifiedScript.Semantics;
using UnifiedScript.Core;

namespace UnifiedScript.CSharp;

public sealed record CSharpSourceDocument
{
    public CSharpSourceDocument(string logicalPath, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalPath);
        ArgumentNullException.ThrowIfNull(source);
        LogicalPath = logicalPath.Replace('\\', '/');
        Source = source;
    }

    public string LogicalPath { get; }

    public string Source { get; }
}

public sealed class CSharpCompilationRequest
{
    public CSharpCompilationRequest(
        string assemblyName,
        IEnumerable<CSharpSourceDocument> sources,
        IEnumerable<string>? referencePaths = null,
        MethodEffectCatalog? effectCatalog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        ArgumentNullException.ThrowIfNull(sources);
        CSharpSourceDocument[] orderedSources = sources
            .OrderBy(static source => source.LogicalPath, StringComparer.Ordinal)
            .ToArray();
        if (orderedSources.Length == 0)
        {
            throw new ArgumentException("At least one C# source document is required.", nameof(sources));
        }

        string? duplicatePath = orderedSources
            .GroupBy(static source => source.LogicalPath, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1)
            ?.Key;
        if (duplicatePath is not null)
        {
            throw new ArgumentException($"Duplicate logical source path '{duplicatePath}'.", nameof(sources));
        }

        AssemblyName = assemblyName;
        Sources = orderedSources;
        string[] orderedReferences = (referencePaths ?? [])
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (string referencePath in orderedReferences)
        {
            if (!File.Exists(referencePath))
            {
                throw new FileNotFoundException("Metadata reference does not exist.", referencePath);
            }
        }

        ReferencePaths = orderedReferences;
        EffectCatalog = effectCatalog ?? MethodEffectCatalog.CreateDefault();
        ConfigurationId = CreateConfigurationId();
    }

    public string AssemblyName { get; }

    public IReadOnlyList<CSharpSourceDocument> Sources { get; }

    public IReadOnlyList<string> ReferencePaths { get; }

    public MethodEffectCatalog EffectCatalog { get; }

    public SemanticId ConfigurationId { get; }

    public static CSharpCompilationRequest Single(
        string source,
        string filePath = "<memory>",
        MethodEffectCatalog? effectCatalog = null) =>
        new(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(filePath, source)],
            effectCatalog: effectCatalog);

    private SemanticId CreateConfigurationId()
    {
        var referenceFingerprints = new List<string>();
        foreach (string referencePath in ReferencePaths)
        {
            using FileStream stream = File.OpenRead(referencePath);
            string digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            referenceFingerprints.Add($"{Path.GetFileName(referencePath)}:{digest}");
        }

        referenceFingerprints.Sort(StringComparer.Ordinal);

        string key = string.Join(
            '|',
            "csharp-configuration/v0.1",
            AssemblyName,
            "net10.0",
            EffectCatalog.Id.Value,
            string.Join(',', referenceFingerprints));
        return SemanticId.FromKey(key);
    }
}
