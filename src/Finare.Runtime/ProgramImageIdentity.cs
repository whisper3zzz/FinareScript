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

    private static ProgramImageKey FromHash(byte[] digest) =>
        new(Prefix + Convert.ToHexString(digest).ToLowerInvariant());

    private static void Append(IncrementalHash hash, string value) =>
        Append(hash, Encoding.UTF8.GetBytes(value));

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
