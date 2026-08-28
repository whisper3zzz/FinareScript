using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using UnifiedScript.Core;

namespace UnifiedScript.Runtime;

public sealed class CoreClrCompiledChunkHost
{
    public CoreClrCompiledChunkSession Load(
        ReadOnlySpan<byte> content,
        IEnumerable<string>? referencePaths = null) =>
        Load(UnifiedScriptProgramChunk.Read(content), referencePaths);

    public CoreClrCompiledChunkSession Load(
        Stream content,
        IEnumerable<string>? referencePaths = null) =>
        Load(UnifiedScriptProgramChunk.Read(content), referencePaths);

    public CoreClrCompiledChunkSession Load(
        UnifiedScriptChunk chunk,
        IEnumerable<string>? referencePaths = null)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        UnifiedScriptProgramChunk.Validate(chunk);
        UnifiedScriptRuntimeManifest manifest = UnifiedScriptRuntimeManifestBinary.Decode(
            chunk.GetRequiredSection(UnifiedScriptChunkSectionKind.RuntimeManifest).Data.Span);
        UnifiedScriptRuntimeDeclarationDocument declarations =
            UnifiedScriptRuntimeDeclarationBinary.Decode(chunk.GetRequiredSection(
                UnifiedScriptChunkSectionKind.DeclarationData).Data.Span);
        UnifiedScriptRuntimeBindingManifest bindings =
            UnifiedScriptRuntimeBindingManifestBinary.Decode(chunk.GetRequiredSection(
                UnifiedScriptChunkSectionKind.BindingManifest).Data.Span);
        UnifiedScriptRuntimeDependencyManifest dependencies =
            UnifiedScriptRuntimeDependencyManifestBinary.Decode(chunk.GetRequiredSection(
                UnifiedScriptChunkSectionKind.DependencyManifest).Data.Span);
        if (manifest.SourceCompilationId != declarations.SourceCompilationId ||
            manifest.SemanticConfigurationId != declarations.SemanticConfigurationId)
        {
            throw new InvalidDataException(
                "Compiled chunk runtime manifest and declarations have different identities.");
        }

        string[] references = (referencePaths ?? [])
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        ValidateDependencies(dependencies, references);
        var loadContext = new CoreClrProgramLoadContext(references);
        try
        {
            ReadOnlyMemory<byte> assemblyBytes = chunk.GetRequiredSection(
                UnifiedScriptChunkSectionKind.ManagedAssembly).Data;
            using var assemblyStream = new MemoryStream(
                assemblyBytes.ToArray(),
                writable: false);
            Assembly assembly;
            if (chunk.TryGetSection(
                UnifiedScriptChunkSectionKind.PortablePdb,
                out UnifiedScriptChunkSection? pdbSection) && pdbSection is not null)
            {
                using var pdbStream = new MemoryStream(pdbSection.Data.ToArray(), writable: false);
                assembly = loadContext.LoadFromStream(assemblyStream, pdbStream);
            }
            else
            {
                assembly = loadContext.LoadFromStream(assemblyStream);
            }

            if (!string.Equals(
                assembly.GetName().Name,
                manifest.AssemblyName,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Compiled chunk assembly '{assembly.GetName().Name}' does not match " +
                    $"manifest '{manifest.AssemblyName}'.");
            }

            foreach (string entryTypeName in manifest.EntryTypeNames)
            {
                if (assembly.GetType(entryTypeName, throwOnError: false) is null)
                {
                    throw new InvalidDataException(
                        $"Compiled chunk entry type '{entryTypeName}' is missing from the assembly.");
                }
            }

            return new CoreClrCompiledChunkSession(
                loadContext,
                assembly,
                chunk,
                manifest,
                declarations,
                bindings,
                dependencies);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    private static void ValidateDependencies(
        UnifiedScriptRuntimeDependencyManifest manifest,
        IReadOnlyList<string> referencePaths)
    {
        IReadOnlyDictionary<string, string> references = referencePaths.ToDictionary(
            static path => AssemblyName.GetAssemblyName(path).Name ??
                throw new InvalidDataException($"Runtime reference '{path}' has no assembly name."),
            static path => path,
            StringComparer.OrdinalIgnoreCase);
        foreach (UnifiedScriptRuntimeDependency dependency in manifest.Dependencies)
        {
            if (dependency.Kind != UnifiedScriptRuntimeDependencyKind.ManagedAssembly)
            {
                if (!dependency.Optional)
                {
                    throw new InvalidDataException(
                        $"Required native dependency '{dependency.Name}' needs a native resolver.");
                }
                continue;
            }

            if (!references.TryGetValue(dependency.Name, out string? path))
            {
                if (dependency.Optional)
                {
                    continue;
                }
                throw new FileNotFoundException(
                    $"Compiled chunk requires managed dependency '{dependency.Name}'.");
            }

            AssemblyName assemblyName = AssemblyName.GetAssemblyName(path);
            string actualVersion = assemblyName.Version?.ToString() ?? "0.0.0.0";
            using FileStream stream = File.OpenRead(path);
            string actualDigest = "sha256:" +
                Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actualVersion, dependency.Version, StringComparison.Ordinal) ||
                !string.Equals(actualDigest, dependency.ContentDigest, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Compiled chunk dependency '{dependency.Name}' version or digest differs.");
            }
        }
    }
}

public sealed class CoreClrCompiledChunkSession : IDisposable
{
    private readonly object gate = new();
    private AssemblyLoadContext? loadContext;
    private Assembly? assembly;

    internal CoreClrCompiledChunkSession(
        AssemblyLoadContext loadContext,
        Assembly assembly,
        UnifiedScriptChunk chunk,
        UnifiedScriptRuntimeManifest manifest,
        UnifiedScriptRuntimeDeclarationDocument declarations,
        UnifiedScriptRuntimeBindingManifest bindings,
        UnifiedScriptRuntimeDependencyManifest dependencies)
    {
        this.loadContext = loadContext;
        this.assembly = assembly;
        Chunk = chunk;
        Manifest = manifest;
        Declarations = declarations;
        Bindings = bindings;
        Dependencies = dependencies;
        LoadContextReference = new WeakReference(loadContext, trackResurrection: false);
    }

    public WeakReference LoadContextReference { get; }

    public UnifiedScriptChunk Chunk { get; }

    public UnifiedScriptRuntimeManifest Manifest { get; }

    public UnifiedScriptRuntimeDeclarationDocument Declarations { get; }

    public UnifiedScriptRuntimeBindingManifest Bindings { get; }

    public UnifiedScriptRuntimeDependencyManifest Dependencies { get; }

    public object CreateEntryInstance(string entryTypeName)
    {
        lock (gate)
        {
            Assembly loadedAssembly = assembly ??
                throw new ObjectDisposedException(GetType().FullName);
            if (!Manifest.EntryTypeNames.Contains(entryTypeName, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"Type '{entryTypeName}' is not a declared chunk entry type.",
                    nameof(entryTypeName));
            }

            Type runtimeType = loadedAssembly.GetType(entryTypeName, throwOnError: true)!;
            return Activator.CreateInstance(runtimeType) ??
                throw new InvalidOperationException(
                    $"Compiled chunk entry type '{entryTypeName}' could not be constructed.");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            AssemblyLoadContext? context = loadContext;
            loadContext = null;
            assembly = null;
            context?.Unload();
        }
    }
}

internal sealed class CoreClrProgramLoadContext : AssemblyLoadContext
{
    private readonly IReadOnlyDictionary<string, string> referencePaths;

    public CoreClrProgramLoadContext(IEnumerable<string> references)
        : base(isCollectible: true)
    {
        referencePaths = references
            .Select(Path.GetFullPath)
            .ToDictionary(
                static path => AssemblyName.GetAssemblyName(path).Name ??
                    throw new InvalidOperationException(
                        $"Reference '{path}' has no assembly name."),
                static path => path,
                StringComparer.OrdinalIgnoreCase);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        Assembly? shared = Default.Assemblies.FirstOrDefault(assembly =>
            AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
        if (shared is not null)
        {
            return shared;
        }

        return assemblyName.Name is not null &&
            referencePaths.TryGetValue(assemblyName.Name, out string? referencePath)
                ? LoadFromAssemblyPath(referencePath)
                : null;
    }
}
