using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Finare.Core;

namespace Finare.Unreal;

public sealed record FinareDiagnosticsPolicy(
    string SchemaVersion,
    string PolicyVersion,
    int MaximumReportCount,
    int MaximumReportBytes,
    int MaximumBundleBytes,
    IReadOnlyList<string> AllowedSchemas,
    IReadOnlyList<string> SensitivePropertyNames,
    IReadOnlyList<string> SensitivePropertySuffixes,
    IReadOnlyList<string> SafeStringPropertyNames)
{
    public const string CurrentSchemaVersion = "unifiedscript.diagnostics-policy/v0.1";
}

public sealed record FinareDiagnosticsBundleEntry(
    string Path,
    string OriginalSchemaVersion,
    int ByteLength,
    string Sha256,
    int RedactionCount);

public sealed record FinareDiagnosticsSkipSummary(
    int NonJson,
    int InvalidJson,
    int UnapprovedSchema,
    int Oversized,
    int ReparsePoint,
    int ReportLimit);

public sealed record FinareDiagnosticsBundleManifest(
    string SchemaVersion,
    string PolicyVersion,
    string PrivacyMode,
    int ScannedFileCount,
    int ReportCount,
    int ReportBytes,
    IReadOnlyList<FinareDiagnosticsBundleEntry> Reports,
    FinareDiagnosticsSkipSummary Skipped)
{
    public const string CurrentSchemaVersion = "unifiedscript.diagnostics-bundle/v0.1";
}

public sealed record FinareDiagnosticsBundleResult(
    byte[] Archive,
    string Sha256,
    FinareDiagnosticsBundleManifest Manifest);

public static class FinareDiagnosticsPolicyJson
{
    public static FinareDiagnosticsPolicy Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        FinareDiagnosticsPolicy? policy =
            JsonSerializer.Deserialize<FinareDiagnosticsPolicy>(json, CreateOptions(true));
        return Normalize(policy ??
            throw new JsonException("The Finare diagnostics policy was empty."));
    }

    public static string Serialize(
        FinareDiagnosticsPolicy policy,
        bool writeIndented = true) =>
        JsonSerializer.Serialize(Normalize(policy), CreateOptions(writeIndented))
            .ReplaceLineEndings("\n") + "\n";

    private static FinareDiagnosticsPolicy Normalize(
        FinareDiagnosticsPolicy policy)
    {
        if (policy.SchemaVersion != FinareDiagnosticsPolicy.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(policy.PolicyVersion) ||
            policy.MaximumReportCount is <= 0 or > 4096 ||
            policy.MaximumReportBytes is <= 0 or > 64 * 1024 * 1024 ||
            policy.MaximumBundleBytes < policy.MaximumReportBytes ||
            policy.MaximumBundleBytes > 256 * 1024 * 1024 ||
            policy.AllowedSchemas is null || policy.AllowedSchemas.Count == 0 ||
            policy.SensitivePropertyNames is null ||
            policy.SensitivePropertySuffixes is null ||
            policy.SafeStringPropertyNames is null ||
            policy.SafeStringPropertyNames.Count == 0)
        {
            throw new JsonException("The Finare diagnostics policy has invalid required fields.");
        }

        string[] schemas = NormalizeUnique(policy.AllowedSchemas, StringComparer.Ordinal);
        if (schemas.Any(static schema =>
                !schema.StartsWith("unifiedscript.", StringComparison.Ordinal) ||
                !schema.Contains("/v", StringComparison.Ordinal)))
        {
            throw new JsonException("Diagnostics allowed schemas must be versioned Finare schemas.");
        }
        string[] names = NormalizeUnique(
            policy.SensitivePropertyNames,
            StringComparer.OrdinalIgnoreCase);
        string[] suffixes = NormalizeUnique(
            policy.SensitivePropertySuffixes,
            StringComparer.OrdinalIgnoreCase);
        string[] safeStringNames = NormalizeUnique(
            policy.SafeStringPropertyNames,
            StringComparer.OrdinalIgnoreCase);
        if (names.Any(static name => name.Any(char.IsWhiteSpace)) ||
            suffixes.Any(static suffix => suffix.Any(char.IsWhiteSpace)) ||
            safeStringNames.Any(static name => name.Any(char.IsWhiteSpace)))
        {
            throw new JsonException("Diagnostics sensitive property selectors cannot contain whitespace.");
        }

        return policy with
        {
            PolicyVersion = policy.PolicyVersion.Trim(),
            AllowedSchemas = schemas,
            SensitivePropertyNames = names,
            SensitivePropertySuffixes = suffixes,
            SafeStringPropertyNames = safeStringNames,
        };
    }

    private static string[] NormalizeUnique(
        IEnumerable<string> values,
        StringComparer comparer)
    {
        string[] normalized = values
            .Select(static value => value?.Trim() ?? string.Empty)
            .ToArray();
        if (normalized.Any(string.IsNullOrWhiteSpace) ||
            normalized.Distinct(comparer).Count() != normalized.Length)
        {
            throw new JsonException("Diagnostics policy lists must contain unique nonblank values.");
        }
        return normalized.Order(comparer).ToArray();
    }

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
}

public sealed class FinareDiagnosticsBundleBuilder
{
    private static readonly DateTimeOffset ArchiveTimestamp =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Regex SensitiveMessagePattern = new(
        @"(?ix)(?:[a-z]:[\\/]|[\\/]{2}[^\\/]|/(?:home|users|root|private|var)/|" +
        @"\b(?:token|password|secret|authorization)\s*[:=]|" +
        @"\b[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}\b|" +
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public FinareDiagnosticsBundleResult Build(
        string inputDirectory,
        FinareDiagnosticsPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputDirectory);
        ArgumentNullException.ThrowIfNull(policy);
        policy = FinareDiagnosticsPolicyJson.Deserialize(
            FinareDiagnosticsPolicyJson.Serialize(policy, writeIndented: false));
        string root = Path.GetFullPath(inputDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Diagnostics input directory does not exist: {root}");
        }

        var allowedSchemas = policy.AllowedSchemas.ToHashSet(StringComparer.Ordinal);
        var sensitiveNames = policy.SensitivePropertyNames.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        string[] sensitiveSuffixes = policy.SensitivePropertySuffixes.ToArray();
        var safeStringNames = policy.SafeStringPropertyNames.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var envelopes = new List<BundleFile>();
        int scanned = 0;
        int reportBytes = 0;
        int nonJson = 0;
        int invalidJson = 0;
        int unapprovedSchema = 0;
        int oversized = 0;
        int reparsePoint = 0;
        int reportLimit = 0;

        string[] files = EnumerateFilesWithoutFollowingReparsePoints(
            root,
            out reparsePoint);
        scanned = checked(files.Length + reparsePoint);
        foreach (string file in files)
        {
            if (!Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                nonJson++;
                continue;
            }
            if (!TryReadBounded(file, policy.MaximumReportBytes, out byte[] bytes))
            {
                oversized++;
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(
                    bytes,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 64,
                    });
            }
            catch (JsonException)
            {
                invalidJson++;
                continue;
            }
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty(
                        "schemaVersion",
                        out JsonElement schemaElement) ||
                    schemaElement.ValueKind != JsonValueKind.String ||
                    schemaElement.GetString() is not string schemaVersion ||
                    !allowedSchemas.Contains(schemaVersion))
                {
                    unapprovedSchema++;
                    continue;
                }
                if (envelopes.Count >= policy.MaximumReportCount)
                {
                    reportLimit++;
                    continue;
                }

                int redactions = 0;
                byte[] content = CreateEnvelope(
                    schemaVersion,
                    document.RootElement,
                    sensitiveNames,
                    sensitiveSuffixes,
                    safeStringNames,
                    ref redactions);
                if (checked(reportBytes + content.Length) > policy.MaximumBundleBytes)
                {
                    oversized++;
                    continue;
                }
                reportBytes += content.Length;
                string path = $"reports/{envelopes.Count + 1:D4}-report.json";
                envelopes.Add(new BundleFile(
                    path,
                    schemaVersion,
                    content,
                    redactions));
            }
        }

        FinareDiagnosticsBundleEntry[] entries = envelopes.Select(static file =>
            new FinareDiagnosticsBundleEntry(
                file.Path,
                file.SchemaVersion,
                file.Content.Length,
                "sha256:" + Convert.ToHexString(SHA256.HashData(file.Content)).ToLowerInvariant(),
                file.RedactionCount)).ToArray();
        var manifest = new FinareDiagnosticsBundleManifest(
            FinareDiagnosticsBundleManifest.CurrentSchemaVersion,
            policy.PolicyVersion,
            "safe-default",
            scanned,
            entries.Length,
            reportBytes,
            entries,
            new FinareDiagnosticsSkipSummary(
                nonJson,
                invalidJson,
                unapprovedSchema,
                oversized,
                reparsePoint,
                reportLimit));
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            UnifiedJson.CreateOptions(writeIndented: true));
        manifestBytes = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(manifestBytes).ReplaceLineEndings("\n") + "\n");
        byte[] archive = CreateArchive(manifestBytes, envelopes);
        return new FinareDiagnosticsBundleResult(
            archive,
            "sha256:" + Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(),
            manifest);
    }

    private static byte[] CreateEnvelope(
        string schemaVersion,
        JsonElement document,
        ISet<string> sensitiveNames,
        IReadOnlyList<string> sensitiveSuffixes,
        ISet<string> safeStringNames,
        ref int redactions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "schemaVersion",
                "unifiedscript.diagnostics-report-envelope/v0.1");
            writer.WriteString("originalSchemaVersion", schemaVersion);
            writer.WritePropertyName("document");
            WriteSanitized(
                writer,
                document,
                sensitiveNames,
                sensitiveSuffixes,
                safeStringNames,
                propertyName: null,
                ref redactions);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings("\n") + "\n");
    }

    private static void WriteSanitized(
        Utf8JsonWriter writer,
        JsonElement element,
        ISet<string> sensitiveNames,
        IReadOnlyList<string> sensitiveSuffixes,
        ISet<string> safeStringNames,
        string? propertyName,
        ref int redactions)
    {
        if (propertyName is not null && IsSensitiveProperty(
                propertyName,
                sensitiveNames,
                sensitiveSuffixes))
        {
            writer.WriteStringValue("<redacted>");
            redactions++;
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject()
                    .OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteSanitized(
                        writer,
                        property.Value,
                        sensitiveNames,
                        sensitiveSuffixes,
                        safeStringNames,
                        property.Name,
                        ref redactions);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteSanitized(
                        writer,
                        item,
                        sensitiveNames,
                        sensitiveSuffixes,
                        safeStringNames,
                        propertyName,
                        ref redactions);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                string value = element.GetString() ?? string.Empty;
                bool looksSensitive = SensitiveMessagePattern.IsMatch(value);
                if (propertyName is null ||
                    !safeStringNames.Contains(propertyName) ||
                    looksSensitive)
                {
                    writer.WriteStringValue(looksSensitive
                        ? "<redacted-message>"
                        : "<redacted-string>");
                    redactions++;
                }
                else
                {
                    writer.WriteStringValue(value);
                }
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveProperty(
        string name,
        ISet<string> sensitiveNames,
        IReadOnlyList<string> sensitiveSuffixes) =>
        sensitiveNames.Contains(name) ||
        sensitiveSuffixes.Any(suffix => name.EndsWith(
            suffix,
            StringComparison.OrdinalIgnoreCase));

    private static string[] EnumerateFilesWithoutFollowingReparsePoints(
        string root,
        out int reparsePointCount)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "The diagnostics input root cannot be a filesystem reparse point.");
        }

        var pendingDirectories = new Stack<string>();
        var files = new List<string>();
        reparsePointCount = 0;
        pendingDirectories.Push(root);
        while (pendingDirectories.TryPop(out string? directory))
        {
            string[] entries = Directory.EnumerateFileSystemEntries(directory)
                .Order(StringComparer.Ordinal)
                .ToArray();
            foreach (string entry in entries)
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    reparsePointCount++;
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool TryReadBounded(
        string path,
        int maximumBytes,
        out byte[] content)
    {
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (input.Length > maximumBytes)
        {
            content = [];
            return false;
        }

        content = new byte[checked((int)input.Length)];
        input.ReadExactly(content);
        if (input.ReadByte() >= 0)
        {
            content = [];
            return false;
        }
        return true;
    }

    private static byte[] CreateArchive(
        byte[] manifest,
        IReadOnlyList<BundleFile> reports)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteArchiveEntry(archive, "manifest.json", manifest);
            foreach (BundleFile report in reports)
            {
                WriteArchiveEntry(archive, report.Path, report.Content);
            }
        }
        return stream.ToArray();
    }

    private static void WriteArchiveEntry(
        ZipArchive archive,
        string path,
        ReadOnlySpan<byte> content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = ArchiveTimestamp;
        using Stream output = entry.Open();
        output.Write(content);
    }

    private sealed record BundleFile(
        string Path,
        string SchemaVersion,
        byte[] Content,
        int RedactionCount);
}
