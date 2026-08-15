using System.Security.Cryptography;
using UnifiedScript.Semantics;
using UnifiedScript.Core;

namespace UnifiedScript.CSharp;

public sealed record CSharpSourceDocument
{
    public CSharpSourceDocument(
        string logicalPath,
        string source,
        IdentitySidecarDocument? identitySidecar = null,
        IEnumerable<CSharpIdentityPin>? identityPins = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalPath);
        ArgumentNullException.ThrowIfNull(source);
        LogicalPath = logicalPath.Replace('\\', '/');
        Source = source;
        if (identitySidecar is not null)
        {
            IdentitySidecarDocument normalized = IdentitySidecarJson.NormalizeAndValidate(identitySidecar);
            if (!string.Equals(normalized.SourcePath, LogicalPath, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Identity sidecar path '{normalized.SourcePath}' does not match source path " +
                    $"'{LogicalPath}'.",
                    nameof(identitySidecar));
            }

            IdentitySidecar = normalized;
        }

        CSharpIdentityPin[] materializedPins = (identityPins ?? []).ToArray();
        if (materializedPins.Length > 0 && IdentitySidecar is null)
        {
            throw new ArgumentException(
                "Identity pins require an identity sidecar on the same source document.",
                nameof(identityPins));
        }

        foreach (CSharpIdentityPin pin in materializedPins)
        {
            if (pin.Id.IsEmpty || string.IsNullOrWhiteSpace(pin.Kind))
            {
                throw new ArgumentException("Identity pins require a non-empty ID and kind.", nameof(identityPins));
            }

            if (!string.Equals(pin.CurrentSpan.FilePath, LogicalPath, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Identity pin path '{pin.CurrentSpan.FilePath}' does not match source path '{LogicalPath}'.",
                    nameof(identityPins));
            }
        }

        IdentityPins = materializedPins;
    }

    public string LogicalPath { get; }

    public string Source { get; }

    public IdentitySidecarDocument? IdentitySidecar { get; }

    public IReadOnlyList<CSharpIdentityPin> IdentityPins { get; }
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
        MethodEffectCatalog? effectCatalog = null,
        IdentitySidecarDocument? identitySidecar = null) =>
        new(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(filePath, source, identitySidecar)],
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
