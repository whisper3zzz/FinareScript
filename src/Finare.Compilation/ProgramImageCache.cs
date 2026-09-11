using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Finare.Core;
using Finare.CSharp;

namespace Finare.Runtime;

public sealed record ProgramImageCacheResult(
    ProgramImageKey ImageKey,
    CSharpProgramImage Image,
    bool CacheHit);

public sealed class CoreClrProgramImageCache
{
    private readonly object gate = new();
    private readonly int capacity;
    private readonly CSharpProgramEmitter emitter;
    private readonly Dictionary<string, CacheEntry> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> recency = new();
    private long hitCount;
    private long missCount;

    public CoreClrProgramImageCache(int capacity = 32)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Cache capacity must be positive.");
        }

        this.capacity = capacity;
        emitter = new CSharpProgramEmitter();
    }

    public int Capacity => capacity;

    public int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    public long HitCount
    {
        get
        {
            lock (gate)
            {
                return hitCount;
            }
        }
    }

    public long MissCount
    {
        get
        {
            lock (gate)
            {
                return missCount;
            }
        }
    }

    public ProgramImageCacheResult GetOrAdd(CSharpCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string requestKey = CreateRequestKey(request);
        lock (gate)
        {
            if (entries.TryGetValue(requestKey, out CacheEntry? cached))
            {
                hitCount++;
                Touch(cached);
                return new ProgramImageCacheResult(cached.ImageKey, cached.Image, CacheHit: true);
            }

            missCount++;
            CSharpProgramEmitResult emitted = emitter.Emit(request);
            if (!emitted.Succeeded || emitted.Image is null)
            {
                throw new ExecutionCompilationException(emitted.Diagnostics);
            }

            ProgramImageKey imageKey = ProgramImageKey.FromImage(emitted.Image);
            LinkedListNode<string> node = recency.AddLast(requestKey);
            entries.Add(requestKey, new CacheEntry(imageKey, emitted.Image, node));
            if (entries.Count > capacity)
            {
                string oldestKey = recency.First!.Value;
                recency.RemoveFirst();
                _ = entries.Remove(oldestKey);
            }

            return new ProgramImageCacheResult(imageKey, emitted.Image, CacheHit: false);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            recency.Clear();
        }
    }

    private void Touch(CacheEntry entry)
    {
        recency.Remove(entry.RecencyNode);
        recency.AddLast(entry.RecencyNode);
    }

    internal static string CreateRequestKey(CSharpCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "unifiedscript.program-request/v0.1");
        Append(hash, request.AssemblyName);
        Append(hash, request.ConfigurationId.Value);
        foreach (CSharpSourceDocument source in request.Sources)
        {
            Append(hash, source.LogicalPath);
            Append(hash, source.Source);
            Append(
                hash,
                source.IdentitySidecar is null
                    ? "<no-identity-sidecar>"
                    : IdentitySidecarJson.Serialize(source.IdentitySidecar, writeIndented: false));
            foreach (CSharpIdentityPin pin in source.IdentityPins
                         .OrderBy(static pin => pin.Id.Value, StringComparer.Ordinal)
                         .ThenBy(static pin => pin.Kind, StringComparer.Ordinal))
            {
                Append(hash, pin.Id.Value);
                Append(hash, pin.Kind);
                Append(hash, pin.CurrentSpan.FilePath);
                Append(hash, pin.CurrentSpan.Start.Line);
                Append(hash, pin.CurrentSpan.Start.Column);
                Append(hash, pin.CurrentSpan.End.Line);
                Append(hash, pin.CurrentSpan.End.Column);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value) =>
        Append(hash, Encoding.UTF8.GetBytes(value));

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        Append(hash, bytes);
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private sealed record CacheEntry(
        ProgramImageKey ImageKey,
        CSharpProgramImage Image,
        LinkedListNode<string> RecencyNode);
}
