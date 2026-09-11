using System.Reflection;
using System.Security.Cryptography;
using Finare.Core;
using Finare.CSharp;

namespace Finare.Runtime;

public sealed record CSharpProgramChunkOptions(
    FinareRuntimeBindingManifest? Bindings = null,
    IReadOnlyList<string>? RuntimeReferencePaths = null,
    bool IncludePortablePdb = true,
    IReadOnlyList<string>? EntryTypeNames = null);

public sealed record CSharpProgramChunkEmitResult(
    byte[] Content,
    FinareChunk Chunk,
    FinareRuntimeManifest RuntimeManifest,
    FinareRuntimeDeclarationDocument Declarations,
    FinareRuntimeBindingManifest Bindings,
    FinareRuntimeDependencyManifest Dependencies);

public sealed class CSharpProgramChunkCompiler
{
    public CSharpProgramChunkEmitResult Compile(
        CSharpProgramImage image,
        CSharpProgramChunkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= new CSharpProgramChunkOptions();
        ProgramImageKey imageKey = ProgramImageKey.FromImage(image);
        string[] availableTypes = image.Hir.Types
            .Select(static type => type.QualifiedName)
            .Concat(image.ScriptAssets.Assets.Select(static asset => asset.QualifiedName))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] entryTypes = (options.EntryTypeNames ?? availableTypes)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (entryTypes.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("Chunk entry type names cannot be blank.");
        }
        var runtimeManifest = new FinareRuntimeManifest(
            image.AssemblyName,
            image.Semantics.SourceCompilationId,
            image.Semantics.ConfigurationId,
            imageKey,
            entryTypes);
        FinareRuntimeDeclarationDocument declarations =
            FinareRuntimeDeclarationBinary.FromScriptAssets(image.ScriptAssets);
        FinareRuntimeBindingManifest bindings = options.Bindings ??
            FinareRuntimeBindingManifest.Empty;
        FinareRuntimeDependencyManifest dependencies = CreateDependencies(
            options.RuntimeReferencePaths ?? []);
        var sections = new List<FinareChunkSection>
        {
            RuntimeSection(
                FinareChunkSectionKind.RuntimeManifest,
                FinareRuntimeManifestBinary.Encode(runtimeManifest)),
            RuntimeSection(
                FinareChunkSectionKind.ManagedAssembly,
                image.PortableExecutable),
            RuntimeSection(
                FinareChunkSectionKind.DeclarationData,
                FinareRuntimeDeclarationBinary.Encode(declarations)),
            RuntimeSection(
                FinareChunkSectionKind.BindingManifest,
                FinareRuntimeBindingManifestBinary.Encode(bindings)),
            RuntimeSection(
                FinareChunkSectionKind.DependencyManifest,
                FinareRuntimeDependencyManifestBinary.Encode(dependencies)),
        };
        if (options.IncludePortablePdb)
        {
            sections.Add(new FinareChunkSection(
                FinareChunkSectionKind.PortablePdb,
                FinareChunkSectionFlags.Debug,
                image.PortablePdb));
        }

        byte[] content = FinareChunkCodec.Write(sections);
        FinareChunk chunk = FinareProgramChunk.Read(content);
        return new CSharpProgramChunkEmitResult(
            content,
            chunk,
            runtimeManifest,
            declarations,
            bindings,
            dependencies);
    }

    private static FinareChunkSection RuntimeSection(
        FinareChunkSectionKind kind,
        byte[] content) => new(
            kind,
            FinareChunkSectionFlags.RuntimeRequired,
            content);

    private static FinareRuntimeDependencyManifest CreateDependencies(
        IEnumerable<string> paths)
    {
        FinareRuntimeDependency[] dependencies = paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path =>
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("Runtime chunk dependency does not exist.", path);
                }
                AssemblyName assemblyName = AssemblyName.GetAssemblyName(path);
                using FileStream stream = File.OpenRead(path);
                string digest = "sha256:" +
                    Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                return new FinareRuntimeDependency(
                    FinareRuntimeDependencyKind.ManagedAssembly,
                    assemblyName.Name ?? Path.GetFileNameWithoutExtension(path),
                    assemblyName.Version?.ToString() ?? "0.0.0.0",
                    digest,
                    Optional: false);
            })
            .ToArray();
        return new FinareRuntimeDependencyManifest(dependencies);
    }
}
