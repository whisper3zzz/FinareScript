using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using UnifiedScript.Core;

namespace UnifiedScript.Runtime;

public sealed record UnifiedScriptManagedDependencyCacheSnapshot(
    int EntryCount,
    long CachedBytes,
    long HitCount,
    long MissCount,
    long EvictionCount);

internal sealed record UnifiedScriptVerifiedManagedDependency(
    string Name,
    string Version,
    string ContentDigest,
    Guid ModuleVersionId,
    byte[] Content);

internal sealed record UnifiedScriptManagedDependencyVerification(
    UnifiedScriptVerifiedManagedDependency Dependency,
    bool CacheHit);

public sealed class UnifiedScriptManagedDependencyCache
{
    public const int DefaultMaximumEntryCount = 128;

    public const long DefaultMaximumByteCount = 256L * 1024 * 1024;

    private const long MaximumDependencyByteCount = 512L * 1024 * 1024;

    private readonly object gate = new();
    private readonly int maximumEntryCount;
    private readonly long maximumByteCount;
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> entries =
        new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> leastRecentlyUsed = new();
    private readonly Dictionary<Assembly, string> sharedAssemblyDigests =
        new(ReferenceEqualityComparer.Instance);
    private long cachedBytes;
    private long hitCount;
    private long missCount;
    private long evictionCount;

    public UnifiedScriptManagedDependencyCache(
        int maximumEntryCount = DefaultMaximumEntryCount,
        long maximumByteCount = DefaultMaximumByteCount)
    {
        if (maximumEntryCount is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntryCount));
        }
        if (maximumByteCount is < 1 or > 1024L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumByteCount));
        }
        this.maximumEntryCount = maximumEntryCount;
        this.maximumByteCount = maximumByteCount;
    }

    public static UnifiedScriptManagedDependencyCache Shared { get; } = new();

    public UnifiedScriptManagedDependencyCacheSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return new UnifiedScriptManagedDependencyCacheSnapshot(
                entries.Count,
                cachedBytes,
                hitCount,
                missCount,
                evictionCount);
        }
    }

    public void ClearCachedContent()
    {
        lock (gate)
        {
            entries.Clear();
            leastRecentlyUsed.Clear();
            cachedBytes = 0;
        }
    }

    internal UnifiedScriptManagedDependencyVerification Verify(
        string path,
        UnifiedScriptRuntimeDependency dependency,
        Assembly? sharedAssembly = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(dependency);
        if (dependency.Kind != UnifiedScriptRuntimeDependencyKind.ManagedAssembly ||
            string.IsNullOrWhiteSpace(dependency.Name) ||
            string.IsNullOrWhiteSpace(dependency.Version) ||
            !ProgramImageKey.TryParse(dependency.ContentDigest, out _))
        {
            throw new InvalidDataException(
                "A managed dependency verification request is invalid.");
        }

        string fullPath = Path.GetFullPath(path);
        lock (gate)
        {
            ValidateSharedBinding(sharedAssembly, dependency);
            if (entries.TryGetValue(
                dependency.ContentDigest,
                out LinkedListNode<CacheEntry>? existing))
            {
                ValidateIdentity(existing.Value.Dependency, dependency, sharedAssembly);
                BindSharedAssembly(sharedAssembly, dependency.ContentDigest);
                leastRecentlyUsed.Remove(existing);
                leastRecentlyUsed.AddLast(existing);
                hitCount++;
                return new UnifiedScriptManagedDependencyVerification(
                    existing.Value.Dependency,
                    CacheHit: true);
            }

            missCount++;
            byte[] content = ReadDependencyContent(fullPath);
            string actualDigest = "sha256:" +
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            if (!string.Equals(
                actualDigest,
                dependency.ContentDigest,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Chunk dependency '{dependency.Name}' version or digest differs.");
            }
            ManagedAssemblyIdentity identity = ReadAssemblyIdentity(content);
            var verified = new UnifiedScriptVerifiedManagedDependency(
                identity.Name,
                identity.Version,
                actualDigest,
                identity.ModuleVersionId,
                content);
            ValidateIdentity(verified, dependency, sharedAssembly);
            BindSharedAssembly(sharedAssembly, dependency.ContentDigest);
            AddToCache(verified);
            return new UnifiedScriptManagedDependencyVerification(
                verified,
                CacheHit: false);
        }
    }

    internal UnifiedScriptManagedDependencyVerification Preload(
        string path,
        Assembly? sharedAssembly = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        byte[] content = ReadDependencyContent(fullPath);
        string actualDigest = "sha256:" +
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        ManagedAssemblyIdentity identity = ReadAssemblyIdentity(content);
        var dependency = new UnifiedScriptRuntimeDependency(
            UnifiedScriptRuntimeDependencyKind.ManagedAssembly,
            identity.Name,
            identity.Version,
            actualDigest,
            Optional: false);
        lock (gate)
        {
            ValidateSharedBinding(sharedAssembly, dependency);
            if (entries.TryGetValue(
                actualDigest,
                out LinkedListNode<CacheEntry>? existing))
            {
                ValidateIdentity(existing.Value.Dependency, dependency, sharedAssembly);
                BindSharedAssembly(sharedAssembly, actualDigest);
                leastRecentlyUsed.Remove(existing);
                leastRecentlyUsed.AddLast(existing);
                hitCount++;
                return new UnifiedScriptManagedDependencyVerification(
                    existing.Value.Dependency,
                    CacheHit: true);
            }

            missCount++;
            var verified = new UnifiedScriptVerifiedManagedDependency(
                identity.Name,
                identity.Version,
                actualDigest,
                identity.ModuleVersionId,
                content);
            ValidateIdentity(verified, dependency, sharedAssembly);
            BindSharedAssembly(sharedAssembly, actualDigest);
            AddToCache(verified);
            return new UnifiedScriptManagedDependencyVerification(
                verified,
                CacheHit: false);
        }
    }

    private static byte[] ReadDependencyContent(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumDependencyByteCount ||
            stream.Length > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Managed dependency '{Path.GetFileName(path)}' has an unsupported size.");
        }
        byte[] content = new byte[checked((int)stream.Length)];
        stream.ReadExactly(content);
        if (stream.ReadByte() >= 0)
        {
            throw new InvalidDataException(
                $"Managed dependency '{Path.GetFileName(path)}' changed while being read.");
        }
        return content;
    }

    private static ManagedAssemblyIdentity ReadAssemblyIdentity(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!peReader.HasMetadata)
        {
            throw new InvalidDataException("A managed dependency contains no CLR metadata.");
        }
        MetadataReader reader = peReader.GetMetadataReader();
        if (!reader.IsAssembly)
        {
            throw new InvalidDataException("A managed dependency is not an assembly image.");
        }
        AssemblyDefinition definition = reader.GetAssemblyDefinition();
        ModuleDefinition module = reader.GetModuleDefinition();
        return new ManagedAssemblyIdentity(
            reader.GetString(definition.Name),
            definition.Version.ToString(),
            reader.GetGuid(module.Mvid));
    }

    private void ValidateSharedBinding(
        Assembly? sharedAssembly,
        UnifiedScriptRuntimeDependency dependency)
    {
        if (sharedAssembly is not null &&
            sharedAssemblyDigests.TryGetValue(sharedAssembly, out string? boundDigest) &&
            !string.Equals(boundDigest, dependency.ContentDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Loaded shared dependency '{dependency.Name}' is already bound to a different content identity.");
        }
    }

    private static void ValidateIdentity(
        UnifiedScriptVerifiedManagedDependency actual,
        UnifiedScriptRuntimeDependency expected,
        Assembly? sharedAssembly)
    {
        if (!string.Equals(actual.Name, expected.Name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual.Version, expected.Version, StringComparison.Ordinal) ||
            !string.Equals(actual.ContentDigest, expected.ContentDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Chunk dependency '{expected.Name}' version or digest differs.");
        }
        if (sharedAssembly is null)
        {
            return;
        }

        AssemblyName sharedName = sharedAssembly.GetName();
        if (!string.Equals(sharedName.Name, expected.Name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                sharedName.Version?.ToString() ?? "0.0.0.0",
                expected.Version,
                StringComparison.Ordinal) ||
            sharedAssembly.ManifestModule.ModuleVersionId != actual.ModuleVersionId)
        {
            throw new InvalidDataException(
                $"Loaded shared dependency '{expected.Name}' does not match its verified image.");
        }
    }

    private void BindSharedAssembly(Assembly? sharedAssembly, string digest)
    {
        if (sharedAssembly is not null)
        {
            sharedAssemblyDigests[sharedAssembly] = digest;
        }
    }

    private void AddToCache(UnifiedScriptVerifiedManagedDependency dependency)
    {
        if (dependency.Content.LongLength > maximumByteCount)
        {
            return;
        }
        var entry = new CacheEntry(dependency);
        LinkedListNode<CacheEntry> node = leastRecentlyUsed.AddLast(entry);
        entries.Add(dependency.ContentDigest, node);
        cachedBytes = checked(cachedBytes + dependency.Content.LongLength);
        while (entries.Count > maximumEntryCount || cachedBytes > maximumByteCount)
        {
            LinkedListNode<CacheEntry> oldest = leastRecentlyUsed.First ??
                throw new InvalidOperationException("Dependency cache LRU state is inconsistent.");
            leastRecentlyUsed.RemoveFirst();
            entries.Remove(oldest.Value.Dependency.ContentDigest);
            cachedBytes -= oldest.Value.Dependency.Content.LongLength;
            evictionCount++;
        }
    }

    private sealed record CacheEntry(UnifiedScriptVerifiedManagedDependency Dependency);

    private sealed record ManagedAssemblyIdentity(
        string Name,
        string Version,
        Guid ModuleVersionId);
}
