using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using UnifiedScript.Core;

namespace UnifiedScript.Runtime;

public sealed record CoreClrCompiledChunkLoadProfile(
    UnifiedScriptChunkReadProfile ChunkRead,
    long ProgramValidationStopwatchTicks,
    long RuntimeManifestDecodeStopwatchTicks,
    long DeclarationDecodeStopwatchTicks,
    long BindingDecodeStopwatchTicks,
    long DependencyDecodeStopwatchTicks,
    long IdentityValidationStopwatchTicks,
    long DependencyValidationStopwatchTicks,
    int DependencyCacheHitCount,
    int DependencyCacheMissCount,
    long DependencySnapshotCopyBytes,
    long LoadContextCreationStopwatchTicks,
    long ManagedAssemblyCopyBytes,
    long ManagedAssemblyMaterializationStopwatchTicks,
    long PortablePdbCopyBytes,
    long PortablePdbMaterializationStopwatchTicks,
    long AssemblyLoadStopwatchTicks,
    long EntryTypeValidationStopwatchTicks,
    long TotalStopwatchTicks,
    long CurrentThreadAllocatedBytes)
{
    public long TotalCopyBytes => checked(
        ChunkRead.WholeFileCopyBytes +
        DependencySnapshotCopyBytes +
        ManagedAssemblyCopyBytes +
        PortablePdbCopyBytes);
}

public sealed record CoreClrCompiledChunkLoadResult(
    CoreClrCompiledChunkSession Session,
    CoreClrCompiledChunkLoadProfile Profile);

public sealed record CoreClrCompiledChunkPrewarmProfile(
    int PreparedMethodCount,
    int SkippedMethodCount,
    long StopwatchTicks,
    long CurrentThreadAllocatedBytes);

public sealed class CoreClrCompiledChunkHost
{
    private readonly UnifiedScriptManagedDependencyCache dependencyCache;

    public CoreClrCompiledChunkHost(
        UnifiedScriptManagedDependencyCache? dependencyCache = null)
    {
        this.dependencyCache = dependencyCache ??
            UnifiedScriptManagedDependencyCache.Shared;
    }

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
        IEnumerable<string>? referencePaths = null) =>
        LoadCore(chunk, referencePaths, profile: null);

    public CoreClrCompiledChunkLoadResult LoadProfiled(
        ReadOnlySpan<byte> content,
        IEnumerable<string>? referencePaths = null)
    {
        long started = Stopwatch.GetTimestamp();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        UnifiedScriptChunkReadResult read = UnifiedScriptChunkCodec.ReadProfiled(content);
        var profile = new CompiledChunkLoadProfileBuilder(read.Profile);
        CoreClrCompiledChunkSession session = LoadCore(read.Chunk, referencePaths, profile);
        profile.TotalStopwatchTicks = ElapsedStopwatchTicks(started);
        profile.CurrentThreadAllocatedBytes = Math.Max(
            0,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        return new CoreClrCompiledChunkLoadResult(session, profile.Build());
    }

    public CoreClrCompiledChunkLoadResult LoadProfiled(
        Stream content,
        IEnumerable<string>? referencePaths = null)
    {
        long started = Stopwatch.GetTimestamp();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        UnifiedScriptChunkReadResult read = UnifiedScriptChunkCodec.ReadProfiled(content);
        var profile = new CompiledChunkLoadProfileBuilder(read.Profile);
        CoreClrCompiledChunkSession session = LoadCore(read.Chunk, referencePaths, profile);
        profile.TotalStopwatchTicks = ElapsedStopwatchTicks(started);
        profile.CurrentThreadAllocatedBytes = Math.Max(
            0,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        return new CoreClrCompiledChunkLoadResult(session, profile.Build());
    }

    private CoreClrCompiledChunkSession LoadCore(
        UnifiedScriptChunk chunk,
        IEnumerable<string>? referencePaths,
        CompiledChunkLoadProfileBuilder? profile)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        long phaseStarted = StartProfilePhase(profile);
        UnifiedScriptProgramChunk.Validate(chunk);
        profile?.SetProgramValidation(ElapsedStopwatchTicks(phaseStarted));
        phaseStarted = StartProfilePhase(profile);
        UnifiedScriptRuntimeManifest manifest = UnifiedScriptRuntimeManifestBinary.Decode(
            chunk.GetRequiredSection(UnifiedScriptChunkSectionKind.RuntimeManifest).Data.Span);
        profile?.SetRuntimeManifestDecode(ElapsedStopwatchTicks(phaseStarted));
        phaseStarted = StartProfilePhase(profile);
        UnifiedScriptRuntimeDeclarationDocument declarations =
            UnifiedScriptRuntimeDeclarationBinary.Decode(chunk.GetRequiredSection(
                UnifiedScriptChunkSectionKind.DeclarationData).Data.Span);
        profile?.SetDeclarationDecode(ElapsedStopwatchTicks(phaseStarted));
        phaseStarted = StartProfilePhase(profile);
        UnifiedScriptRuntimeBindingManifest bindings =
            UnifiedScriptRuntimeBindingManifestBinary.Decode(chunk.GetRequiredSection(
                UnifiedScriptChunkSectionKind.BindingManifest).Data.Span);
        profile?.SetBindingDecode(ElapsedStopwatchTicks(phaseStarted));
        phaseStarted = StartProfilePhase(profile);
        UnifiedScriptRuntimeDependencyManifest dependencies =
            UnifiedScriptRuntimeDependencyManifestBinary.Decode(chunk.GetRequiredSection(
                UnifiedScriptChunkSectionKind.DependencyManifest).Data.Span);
        profile?.SetDependencyDecode(ElapsedStopwatchTicks(phaseStarted));
        phaseStarted = StartProfilePhase(profile);
        if (manifest.SourceCompilationId != declarations.SourceCompilationId ||
            manifest.SemanticConfigurationId != declarations.SemanticConfigurationId)
        {
            throw new InvalidDataException(
                "Compiled chunk runtime manifest and declarations have different identities.");
        }
        profile?.SetIdentityValidation(ElapsedStopwatchTicks(phaseStarted));

        phaseStarted = StartProfilePhase(profile);
        string[] references = (referencePaths ?? [])
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        VerifiedDependencySet verifiedDependencies =
            ValidateDependencies(dependencies, references);
        profile?.SetDependencyValidation(ElapsedStopwatchTicks(phaseStarted));
        profile?.SetDependencyCache(
            verifiedDependencies.CacheHitCount,
            verifiedDependencies.CacheMissCount,
            verifiedDependencies.SnapshotCopyBytes);
        phaseStarted = StartProfilePhase(profile);
        var loadContext = new CoreClrProgramLoadContext(
            verifiedDependencies.Dependencies);
        profile?.SetLoadContextCreation(ElapsedStopwatchTicks(phaseStarted));
        try
        {
            ReadOnlyMemory<byte> assemblyBytes = chunk.GetRequiredSection(
                UnifiedScriptChunkSectionKind.ManagedAssembly).Data;
            phaseStarted = StartProfilePhase(profile);
            byte[] assemblyContent = assemblyBytes.ToArray();
            if (profile is not null)
            {
                profile.ManagedAssemblyCopyBytes = assemblyContent.Length;
                profile.ManagedAssemblyMaterializationStopwatchTicks =
                    ElapsedStopwatchTicks(phaseStarted);
            }
            using var assemblyStream = new MemoryStream(
                assemblyContent,
                writable: false);
            Assembly assembly;
            if (chunk.TryGetSection(
                UnifiedScriptChunkSectionKind.PortablePdb,
                out UnifiedScriptChunkSection? pdbSection) && pdbSection is not null)
            {
                phaseStarted = StartProfilePhase(profile);
                byte[] pdbContent = pdbSection.Data.ToArray();
                if (profile is not null)
                {
                    profile.PortablePdbCopyBytes = pdbContent.Length;
                    profile.PortablePdbMaterializationStopwatchTicks =
                        ElapsedStopwatchTicks(phaseStarted);
                }
                using var pdbStream = new MemoryStream(pdbContent, writable: false);
                phaseStarted = StartProfilePhase(profile);
                assembly = loadContext.LoadFromStream(assemblyStream, pdbStream);
                profile?.SetAssemblyLoad(ElapsedStopwatchTicks(phaseStarted));
            }
            else
            {
                phaseStarted = StartProfilePhase(profile);
                assembly = loadContext.LoadFromStream(assemblyStream);
                profile?.SetAssemblyLoad(ElapsedStopwatchTicks(phaseStarted));
            }

            phaseStarted = StartProfilePhase(profile);
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
            profile?.SetEntryTypeValidation(ElapsedStopwatchTicks(phaseStarted));

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

    private VerifiedDependencySet ValidateDependencies(
        UnifiedScriptRuntimeDependencyManifest manifest,
        IReadOnlyList<string> referencePaths)
    {
        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string referencePath in referencePaths)
        {
            string fileName = Path.GetFileNameWithoutExtension(referencePath);
            if (!references.TryAdd(fileName, referencePath))
            {
                throw new InvalidDataException(
                    $"Runtime references contain duplicate file name '{fileName}'.");
            }
        }
        var verifiedDependencies =
            new Dictionary<string, UnifiedScriptVerifiedManagedDependency>(
                StringComparer.OrdinalIgnoreCase);
        int cacheHits = 0;
        int cacheMisses = 0;
        long snapshotCopyBytes = 0;
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
                path = referencePaths.FirstOrDefault(candidate => string.Equals(
                    AssemblyName.GetAssemblyName(candidate).Name,
                    dependency.Name,
                    StringComparison.OrdinalIgnoreCase));
                if (path is null && dependency.Optional)
                {
                    continue;
                }
                if (path is null)
                {
                    throw new FileNotFoundException(
                        $"Compiled chunk requires managed dependency '{dependency.Name}'.");
                }
            }

            Assembly? sharedAssembly = AssemblyLoadContext.Default.Assemblies
                .FirstOrDefault(candidate => string.Equals(
                    candidate.GetName().Name,
                    dependency.Name,
                    StringComparison.OrdinalIgnoreCase));
            UnifiedScriptManagedDependencyVerification verification =
                dependencyCache.Verify(path, dependency, sharedAssembly);
            if (!verifiedDependencies.TryAdd(
                dependency.Name,
                verification.Dependency))
            {
                throw new InvalidDataException(
                    $"Compiled chunk dependency '{dependency.Name}' is duplicated.");
            }
            cacheHits += verification.CacheHit ? 1 : 0;
            cacheMisses += verification.CacheHit ? 0 : 1;
            if (!verification.CacheHit)
            {
                snapshotCopyBytes = checked(
                    snapshotCopyBytes + verification.Dependency.Content.LongLength);
            }
        }
        return new VerifiedDependencySet(
            verifiedDependencies,
            cacheHits,
            cacheMisses,
            snapshotCopyBytes);
    }

    private static long StartProfilePhase(CompiledChunkLoadProfileBuilder? profile) =>
        profile is null ? 0 : Stopwatch.GetTimestamp();

    private static long ElapsedStopwatchTicks(long started) =>
        Math.Max(0, Stopwatch.GetTimestamp() - started);

    private sealed record VerifiedDependencySet(
        IReadOnlyDictionary<string, UnifiedScriptVerifiedManagedDependency> Dependencies,
        int CacheHitCount,
        int CacheMissCount,
        long SnapshotCopyBytes);

    private sealed class CompiledChunkLoadProfileBuilder
    {
        public CompiledChunkLoadProfileBuilder(UnifiedScriptChunkReadProfile chunkRead)
        {
            ChunkRead = chunkRead;
        }

        public UnifiedScriptChunkReadProfile ChunkRead { get; }

        public long ProgramValidationStopwatchTicks { get; private set; }

        public long RuntimeManifestDecodeStopwatchTicks { get; private set; }

        public long DeclarationDecodeStopwatchTicks { get; private set; }

        public long BindingDecodeStopwatchTicks { get; private set; }

        public long DependencyDecodeStopwatchTicks { get; private set; }

        public long IdentityValidationStopwatchTicks { get; private set; }

        public long DependencyValidationStopwatchTicks { get; private set; }

        public int DependencyCacheHitCount { get; private set; }

        public int DependencyCacheMissCount { get; private set; }

        public long DependencySnapshotCopyBytes { get; private set; }

        public long LoadContextCreationStopwatchTicks { get; private set; }

        public long ManagedAssemblyCopyBytes { get; set; }

        public long ManagedAssemblyMaterializationStopwatchTicks { get; set; }

        public long PortablePdbCopyBytes { get; set; }

        public long PortablePdbMaterializationStopwatchTicks { get; set; }

        public long AssemblyLoadStopwatchTicks { get; private set; }

        public long EntryTypeValidationStopwatchTicks { get; private set; }

        public long TotalStopwatchTicks { get; set; }

        public long CurrentThreadAllocatedBytes { get; set; }

        public void SetProgramValidation(long ticks) =>
            ProgramValidationStopwatchTicks = ticks;

        public void SetRuntimeManifestDecode(long ticks) =>
            RuntimeManifestDecodeStopwatchTicks = ticks;

        public void SetDeclarationDecode(long ticks) =>
            DeclarationDecodeStopwatchTicks = ticks;

        public void SetBindingDecode(long ticks) =>
            BindingDecodeStopwatchTicks = ticks;

        public void SetDependencyDecode(long ticks) =>
            DependencyDecodeStopwatchTicks = ticks;

        public void SetIdentityValidation(long ticks) =>
            IdentityValidationStopwatchTicks = ticks;

        public void SetDependencyValidation(long ticks) =>
            DependencyValidationStopwatchTicks = ticks;

        public void SetDependencyCache(
            int hitCount,
            int missCount,
            long snapshotCopyBytes)
        {
            DependencyCacheHitCount = hitCount;
            DependencyCacheMissCount = missCount;
            DependencySnapshotCopyBytes = snapshotCopyBytes;
        }

        public void SetLoadContextCreation(long ticks) =>
            LoadContextCreationStopwatchTicks = ticks;

        public void SetAssemblyLoad(long ticks) => AssemblyLoadStopwatchTicks = ticks;

        public void SetEntryTypeValidation(long ticks) =>
            EntryTypeValidationStopwatchTicks = ticks;

        public CoreClrCompiledChunkLoadProfile Build() => new(
            ChunkRead,
            ProgramValidationStopwatchTicks,
            RuntimeManifestDecodeStopwatchTicks,
            DeclarationDecodeStopwatchTicks,
            BindingDecodeStopwatchTicks,
            DependencyDecodeStopwatchTicks,
            IdentityValidationStopwatchTicks,
            DependencyValidationStopwatchTicks,
            DependencyCacheHitCount,
            DependencyCacheMissCount,
            DependencySnapshotCopyBytes,
            LoadContextCreationStopwatchTicks,
            ManagedAssemblyCopyBytes,
            ManagedAssemblyMaterializationStopwatchTicks,
            PortablePdbCopyBytes,
            PortablePdbMaterializationStopwatchTicks,
            AssemblyLoadStopwatchTicks,
            EntryTypeValidationStopwatchTicks,
            TotalStopwatchTicks,
            CurrentThreadAllocatedBytes);
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

    public CoreClrCompiledChunkPrewarmProfile PrewarmEntryMethods()
    {
        lock (gate)
        {
            Assembly loadedAssembly = assembly ??
                throw new ObjectDisposedException(GetType().FullName);
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            int prepared = 0;
            int skipped = 0;
            foreach (string entryTypeName in Manifest.EntryTypeNames)
            {
                Type entryType = loadedAssembly.GetType(entryTypeName, throwOnError: true)!;
                foreach (MethodInfo method in entryType.GetMethods(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly).OrderBy(static method => method.MetadataToken))
                {
                    if (method.IsAbstract ||
                        method.ContainsGenericParameters ||
                        method.GetMethodBody() is null)
                    {
                        skipped++;
                        continue;
                    }
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    prepared++;
                }
            }

            return new CoreClrCompiledChunkPrewarmProfile(
                prepared,
                skipped,
                Math.Max(0, Stopwatch.GetTimestamp() - started),
                Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore));
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
    private readonly IReadOnlyDictionary<string, UnifiedScriptVerifiedManagedDependency>
        dependencies;

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
        dependencies =
            new Dictionary<string, UnifiedScriptVerifiedManagedDependency>(
                StringComparer.OrdinalIgnoreCase);
    }

    public CoreClrProgramLoadContext(
        IReadOnlyDictionary<string, UnifiedScriptVerifiedManagedDependency> dependencies)
        : base(isCollectible: true)
    {
        this.dependencies = dependencies;
        referencePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        Assembly? shared = Default.Assemblies.FirstOrDefault(assembly =>
            AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
        if (shared is not null)
        {
            return shared;
        }

        if (assemblyName.Name is null ||
            !dependencies.TryGetValue(assemblyName.Name, out var dependency))
        {
            return assemblyName.Name is not null &&
                referencePaths.TryGetValue(assemblyName.Name, out string? referencePath)
                    ? LoadFromAssemblyPath(referencePath)
                    : null;
        }
        if (assemblyName.Version is not null &&
            !string.Equals(
                assemblyName.Version.ToString(),
                dependency.Version,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Requested dependency '{assemblyName.Name}' version differs from the verified snapshot.");
        }
        using var stream = new MemoryStream(dependency.Content, writable: false);
        return LoadFromStream(stream);
    }
}
