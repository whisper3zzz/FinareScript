using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Finare.Core;
using Finare.CSharp;

namespace Finare.Runtime;

[JsonConverter(typeof(ProgramImageKeyJsonConverter))]
public readonly partial record struct ProgramImageKey
{
    private const string Prefix = "sha256:";

    public ProgramImageKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!ImageKeyPattern().IsMatch(value))
        {
            throw new ArgumentException(
                "A program image key must use 'sha256:' followed by 64 lowercase hex digits.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public static ProgramImageKey FromImage(CSharpProgramImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "unifiedscript.program-image/v0.1");
        Append(hash, image.AssemblyName);
        Append(hash, image.PortableExecutable);
        Append(hash, image.PortablePdb);
        Append(hash, CSharpProgramSourceMapJson.Serialize(image.SourceMap, writeIndented: false));
        return FromHash(hash.GetHashAndReset());
    }

    public static bool TryParse(string? value, out ProgramImageKey key)
    {
        if (value is not null && ImageKeyPattern().IsMatch(value))
        {
            key = new ProgramImageKey(value);
            return true;
        }

        key = default;
        return false;
    }

    public override string ToString() => Value ?? string.Empty;

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

    private static ProgramImageKey FromHash(byte[] digest) =>
        new(Prefix + Convert.ToHexString(digest).ToLowerInvariant());

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

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageKeyPattern();
}

public sealed class ProgramImageKeyJsonConverter : JsonConverter<ProgramImageKey>
{
    public override ProgramImageKey Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        string? value = reader.GetString();
        return ProgramImageKey.TryParse(value, out ProgramImageKey key)
            ? key
            : throw new JsonException($"Invalid program image key '{value}'.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        ProgramImageKey value,
        JsonSerializerOptions options)
    {
        if (value.IsEmpty)
        {
            throw new JsonException("A default program image key cannot be serialized.");
        }

        writer.WriteStringValue(value.Value);
    }
}

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
        string requestKey = ProgramImageKey.CreateRequestKey(request);
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

    private sealed record CacheEntry(
        ProgramImageKey ImageKey,
        CSharpProgramImage Image,
        LinkedListNode<string> RecencyNode);
}
