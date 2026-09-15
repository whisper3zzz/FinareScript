using System.Security.Cryptography;
using Finare.Semantics;
using Finare.Core;

namespace Finare.CSharp;

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
        MethodEffectCatalog? effectCatalog = null,
        CSharpCompilationSettings? settings = null,
        IEnumerable<string>? runtimeReferencePaths = null,
        bool includeDefaultReferences = true,
        IEnumerable<string>? sourceGeneratorPaths = null)
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
        string[] orderedRuntimeReferences = (runtimeReferencePaths ?? orderedReferences)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (string referencePath in orderedRuntimeReferences)
        {
            if (!File.Exists(referencePath))
            {
                throw new FileNotFoundException("Runtime reference does not exist.", referencePath);
            }
        }

        RuntimeReferencePaths = orderedRuntimeReferences;
        EffectCatalog = effectCatalog ?? MethodEffectCatalog.CreateDefault();
        Settings = settings ?? CSharpCompilationSettings.Default;
        IncludeDefaultReferences = includeDefaultReferences;
        SourceGeneratorPaths = (sourceGeneratorPaths ?? [])
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal).ToArray();
        foreach (string path in SourceGeneratorPaths)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Source generator does not exist.", path);
        }
        ConfigurationId = CreateConfigurationId();
    }

    public string AssemblyName { get; }

    public IReadOnlyList<CSharpSourceDocument> Sources { get; }

    public IReadOnlyList<string> ReferencePaths { get; }

    public IReadOnlyList<string> RuntimeReferencePaths { get; }

    public MethodEffectCatalog EffectCatalog { get; }

    public CSharpCompilationSettings Settings { get; }

    public bool IncludeDefaultReferences { get; }

    public IReadOnlyList<string> SourceGeneratorPaths { get; }

    public SemanticId ConfigurationId { get; }

    public static CSharpCompilationRequest Single(
        string source,
        string filePath = "<memory>",
        MethodEffectCatalog? effectCatalog = null,
        IdentitySidecarDocument? identitySidecar = null) =>
        new(
            "Finare.Analysis",
            [new CSharpSourceDocument(filePath, source, identitySidecar)],
            effectCatalog: effectCatalog);

    private SemanticId CreateConfigurationId()
    {
        string[] referenceFingerprints = CreateReferenceFingerprints(ReferencePaths);
        string[] runtimeReferenceFingerprints = CreateReferenceFingerprints(RuntimeReferencePaths);

        bool legacyConfiguration =
            Settings.ConfigurationKey == CSharpCompilationSettings.Default.ConfigurationKey &&
            IncludeDefaultReferences && SourceGeneratorPaths.Count == 0 &&
            ReferencePaths.SequenceEqual(RuntimeReferencePaths, StringComparer.OrdinalIgnoreCase);
        if (legacyConfiguration)
        {
            return SemanticId.FromKey(string.Join(
                '|',
                "csharp-configuration/v0.1",
                AssemblyName,
                "net10.0",
                EffectCatalog.Id.Value,
                string.Join(',', referenceFingerprints)));
        }

        string key = string.Join(
            '|',
            "csharp-configuration/v0.2",
            AssemblyName,
            Settings.ConfigurationKey,
            IncludeDefaultReferences,
            EffectCatalog.Id.Value,
            string.Join(',', referenceFingerprints),
            string.Join(',', runtimeReferenceFingerprints));
        if (SourceGeneratorPaths.Count != 0)
            key = string.Join('|', "csharp-source-generators/v0.1", key,
                string.Join(',', CreateReferenceFingerprints(SourceGeneratorPaths)));
        return SemanticId.FromKey(key);
    }

    private static string[] CreateReferenceFingerprints(IEnumerable<string> paths) => paths
        .Select(static path =>
        {
            using FileStream stream = File.OpenRead(path);
            string digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return $"{Path.GetFileName(path)}:{digest}";
        })
        .Order(StringComparer.Ordinal)
        .ToArray();
}
