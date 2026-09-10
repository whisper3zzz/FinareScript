using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Finare.Core;

namespace Finare.CSharp;

public sealed record CSharpSourceTextPatchDocument(
    string SchemaVersion,
    string SourcePath,
    string BaselineContentDigest,
    string SourceText)
{
    public const string CurrentSchemaVersion = "unifiedscript.csharp-source-text-patch/v0.1";

    public static CSharpSourceTextPatchDocument Create(
        string sourcePath,
        string baselineSourceText,
        string sourceText) => new(
            CurrentSchemaVersion,
            sourcePath,
            CSharpSourceTextPatchJson.ComputeContentDigest(baselineSourceText),
            sourceText);
}

public static class CSharpSourceTextPatchDiagnosticCodes
{
    public const string SourceNotFound = "UCS1002";
    public const string BaselineMismatch = "UCS1003";
    public const string NoChange = "UCS1004";
}

public static class CSharpSourceTextPatchJson
{
    public const int MaximumSourceCharacters = 512 * 1024;
    private const string DigestPrefix = "sha256:";

    public static string Serialize(
        CSharpSourceTextPatchDocument document,
        bool writeIndented = true) => JsonSerializer.Serialize(
            NormalizeAndValidate(document),
            CreateOptions(writeIndented));

    public static CSharpSourceTextPatchDocument Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        CSharpSourceTextPatchDocument? document =
            JsonSerializer.Deserialize<CSharpSourceTextPatchDocument>(json, CreateOptions());
        return NormalizeAndValidate(document ??
            throw new JsonException("The C# source-text patch document was empty."));
    }

    public static CSharpSourceTextPatchDocument NormalizeAndValidate(
        CSharpSourceTextPatchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(
                document.SchemaVersion,
                CSharpSourceTextPatchDocument.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new JsonException(
                $"Unsupported C# source-text patch schema '{document.SchemaVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(document.SourcePath))
        {
            throw new JsonException("A C# source-text patch requires one logical source path.");
        }
        string sourcePath = document.SourcePath.Replace('\\', '/');
        if (sourcePath.Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new JsonException(
                "A C# source-text patch path cannot traverse outside its logical source path.");
        }

        if (!IsContentDigest(document.BaselineContentDigest))
        {
            throw new JsonException(
                "A C# source-text patch requires a lowercase 'sha256:' baseline content digest.");
        }
        if (document.SourceText is null)
        {
            throw new JsonException("A C# source-text patch requires sourceText.");
        }
        if (document.SourceText.Length > MaximumSourceCharacters)
        {
            throw new JsonException(
                $"A C# source-text patch cannot exceed {MaximumSourceCharacters} characters.");
        }

        return document with { SourcePath = sourcePath };
    }

    public static string ComputeContentDigest(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        return DigestPrefix + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(sourceText))).ToLowerInvariant();
    }

    private static bool IsContentDigest(string? value) =>
        value is not null &&
        value.Length == DigestPrefix.Length + 64 &&
        value.StartsWith(DigestPrefix, StringComparison.Ordinal) &&
        value.AsSpan(DigestPrefix.Length).ToString().All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static JsonSerializerOptions CreateOptions(bool writeIndented = true)
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
}
