using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedScript.Core;

namespace UnifiedScript.Unreal;

public enum UnifiedScriptUpgradeStrategy
{
    Preserve,
    Rewrite,
    Regenerate,
    Rebuild,
    Manual,
}

public enum UnifiedScriptUpgradeDisposition
{
    Current,
    Rewrite,
    Regenerate,
    Rebuild,
    Blocked,
}

public sealed record UnifiedScriptCompatibilityFamily(
    string Id,
    string Category,
    string CurrentVersion,
    IReadOnlyList<string> ReadableVersions,
    UnifiedScriptUpgradeStrategy UpgradeStrategy,
    string SourceOfTruth,
    string Producer,
    string Consumer)
{
    public IReadOnlyDictionary<string, int>? Components { get; init; }

    public string? Notes { get; init; }
}

public sealed record UnifiedScriptCompatibilityPolicy(
    string SchemaVersion,
    string ProductVersion,
    IReadOnlyList<UnifiedScriptCompatibilityFamily> Families)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.compatibility-policy/v0.1";
}

public sealed record UnifiedScriptUpgradePlanItem(
    string Path,
    string FamilyId,
    string DetectedVersion,
    string CurrentVersion,
    UnifiedScriptUpgradeDisposition Disposition,
    string Reason);

public sealed record UnifiedScriptUpgradePlanSummary(
    int Scanned,
    int Current,
    int Rewrite,
    int Regenerate,
    int Rebuild,
    int Blocked);

public sealed record UnifiedScriptUpgradePlan(
    string SchemaVersion,
    string PolicyVersion,
    string Input,
    IReadOnlyList<UnifiedScriptUpgradePlanItem> Items,
    UnifiedScriptUpgradePlanSummary Summary)
{
    public const string CurrentSchemaVersion = "unifiedscript.upgrade-plan/v0.1";
}

public static class UnifiedScriptCompatibilityJson
{
    public static UnifiedScriptCompatibilityPolicy DeserializePolicy(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        UnifiedScriptCompatibilityPolicy? policy =
            JsonSerializer.Deserialize<UnifiedScriptCompatibilityPolicy>(json, CreateOptions(true));
        return NormalizePolicy(policy ??
            throw new JsonException("The UnifiedScript compatibility policy was empty."));
    }

    public static string SerializePolicy(
        UnifiedScriptCompatibilityPolicy policy,
        bool writeIndented = true) =>
        JsonSerializer.Serialize(NormalizePolicy(policy), CreateOptions(writeIndented))
            .ReplaceLineEndings("\n") + "\n";

    public static string SerializePlan(
        UnifiedScriptUpgradePlan plan,
        bool writeIndented = true) =>
        JsonSerializer.Serialize(plan, CreateOptions(writeIndented))
            .ReplaceLineEndings("\n") + "\n";

    private static UnifiedScriptCompatibilityPolicy NormalizePolicy(
        UnifiedScriptCompatibilityPolicy policy)
    {
        if (policy.SchemaVersion != UnifiedScriptCompatibilityPolicy.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(policy.ProductVersion) ||
            policy.Families is null || policy.Families.Count == 0)
        {
            throw new JsonException("The UnifiedScript compatibility policy has invalid required fields.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var ownedVersions = new HashSet<string>(StringComparer.Ordinal);
        var families = new List<UnifiedScriptCompatibilityFamily>(policy.Families.Count);
        foreach (UnifiedScriptCompatibilityFamily? family in policy.Families)
        {
            if (family is null ||
                string.IsNullOrWhiteSpace(family.Id) ||
                string.IsNullOrWhiteSpace(family.Category) ||
                string.IsNullOrWhiteSpace(family.CurrentVersion) ||
                family.ReadableVersions is null ||
                !Enum.IsDefined(family.UpgradeStrategy) ||
                string.IsNullOrWhiteSpace(family.SourceOfTruth) ||
                string.IsNullOrWhiteSpace(family.Producer) ||
                string.IsNullOrWhiteSpace(family.Consumer) ||
                !ids.Add(family.Id.Trim()) ||
                !ownedVersions.Add(family.CurrentVersion.Trim()))
            {
                throw new JsonException("The UnifiedScript compatibility policy contains an invalid family.");
            }

            string[] readable = family.ReadableVersions
                .Select(static version => version?.Trim() ?? string.Empty)
                .ToArray();
            if (readable.Any(string.IsNullOrWhiteSpace) ||
                readable.Contains(family.CurrentVersion, StringComparer.Ordinal) ||
                readable.Distinct(StringComparer.Ordinal).Count() != readable.Length ||
                readable.Any(version => !ownedVersions.Add(version)))
            {
                throw new JsonException(
                    $"Compatibility family '{family.Id}' has invalid readable versions.");
            }

            IReadOnlyDictionary<string, int>? components = family.Components is null
                ? null
                : new SortedDictionary<string, int>(
                    family.Components.ToDictionary(
                        static component => component.Key,
                        static component => component.Value,
                        StringComparer.Ordinal),
                    StringComparer.Ordinal);
            if (components?.Any(static component =>
                    string.IsNullOrWhiteSpace(component.Key) || component.Value <= 0) == true)
            {
                throw new JsonException(
                    $"Compatibility family '{family.Id}' has invalid versioned components.");
            }

            families.Add(family with
            {
                Id = family.Id.Trim(),
                Category = family.Category.Trim(),
                CurrentVersion = family.CurrentVersion.Trim(),
                ReadableVersions = readable.Order(StringComparer.Ordinal).ToArray(),
                SourceOfTruth = family.SourceOfTruth.Trim(),
                Producer = family.Producer.Trim(),
                Consumer = family.Consumer.Trim(),
                Components = components,
                Notes = string.IsNullOrWhiteSpace(family.Notes) ? null : family.Notes.Trim(),
            });
        }

        return policy with
        {
            ProductVersion = policy.ProductVersion.Trim(),
            Families = families.OrderBy(static family => family.Id, StringComparer.Ordinal).ToArray(),
        };
    }

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
}

public static class UnifiedScriptUpgradePlanner
{
    private static ReadOnlySpan<byte> ChunkMagic => "USCHUNK\0"u8;

    public static UnifiedScriptUpgradePlan CreatePlan(
        UnifiedScriptCompatibilityPolicy policy,
        string inputPath)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        string fullInput = Path.GetFullPath(inputPath);
        bool isFile = File.Exists(fullInput);
        if (!isFile && !Directory.Exists(fullInput))
        {
            throw new FileNotFoundException($"Upgrade input does not exist: {inputPath}", inputPath);
        }

        IReadOnlyDictionary<string, UnifiedScriptCompatibilityFamily> exactVersions = policy.Families
            .SelectMany(static family => family.ReadableVersions
                .Append(family.CurrentVersion)
                .Select(version => new KeyValuePair<string, UnifiedScriptCompatibilityFamily>(
                    version,
                    family)))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        UnifiedScriptCompatibilityFamily[] schemaFamilies = policy.Families
            .Where(static family => family.CurrentVersion.StartsWith(
                "unifiedscript.",
                StringComparison.Ordinal))
            .ToArray();

        IEnumerable<string> files = isFile
            ? [fullInput]
            : Directory.EnumerateFiles(fullInput, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal);
        var items = new List<UnifiedScriptUpgradePlanItem>();
        foreach (string file in files)
        {
            string relativePath = isFile
                ? Path.GetFileName(fullInput)
                : Path.GetRelativePath(fullInput, file).Replace('\\', '/');
            string extension = Path.GetExtension(file);
            if (extension.Equals(".uschunk", StringComparison.OrdinalIgnoreCase))
            {
                TryAddChunk(file, relativePath, policy, items);
                continue;
            }
            if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".usgraph", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".usids", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? version = TryReadSchemaVersion(file);
            if (version is null)
            {
                continue;
            }
            if (exactVersions.TryGetValue(version, out UnifiedScriptCompatibilityFamily? family))
            {
                items.Add(CreateItem(relativePath, version, family));
                continue;
            }

            family = schemaFamilies.FirstOrDefault(candidate =>
                HasSameVersionFamily(version, candidate.CurrentVersion));
            if (family is not null)
            {
                UnifiedScriptUpgradeDisposition disposition = family.UpgradeStrategy switch
                {
                    UnifiedScriptUpgradeStrategy.Regenerate =>
                        UnifiedScriptUpgradeDisposition.Regenerate,
                    UnifiedScriptUpgradeStrategy.Rebuild =>
                        UnifiedScriptUpgradeDisposition.Rebuild,
                    _ => UnifiedScriptUpgradeDisposition.Blocked,
                };
                string reason = disposition switch
                {
                    UnifiedScriptUpgradeDisposition.Regenerate =>
                        $"The version is not readable; regenerate the derived artifact from {family.SourceOfTruth}.",
                    UnifiedScriptUpgradeDisposition.Rebuild =>
                        $"The version is not readable; rebuild the delivery artifact from {family.SourceOfTruth}.",
                    _ => "The artifact belongs to a known family but its version is not readable by this release.",
                };
                items.Add(new UnifiedScriptUpgradePlanItem(
                    relativePath,
                    family.Id,
                    version,
                    family.CurrentVersion,
                    disposition,
                    reason));
            }
        }

        UnifiedScriptUpgradePlanItem[] ordered = items
            .OrderBy(static item => item.Path, StringComparer.Ordinal)
            .ThenBy(static item => item.FamilyId, StringComparer.Ordinal)
            .ToArray();
        return new UnifiedScriptUpgradePlan(
            UnifiedScriptUpgradePlan.CurrentSchemaVersion,
            policy.ProductVersion,
            isFile ? Path.GetFileName(fullInput) : ".",
            ordered,
            new UnifiedScriptUpgradePlanSummary(
                ordered.Length,
                ordered.Count(static item => item.Disposition == UnifiedScriptUpgradeDisposition.Current),
                ordered.Count(static item => item.Disposition == UnifiedScriptUpgradeDisposition.Rewrite),
                ordered.Count(static item => item.Disposition == UnifiedScriptUpgradeDisposition.Regenerate),
                ordered.Count(static item => item.Disposition == UnifiedScriptUpgradeDisposition.Rebuild),
                ordered.Count(static item => item.Disposition == UnifiedScriptUpgradeDisposition.Blocked)));
    }

    public static string Rewrite(string familyId, string json) => familyId switch
    {
        "unreal-reflection" => UnrealReflectionJson.Serialize(
            UnrealReflectionJson.Deserialize(json)),
        "unreal-api-contract" => UnrealApiContractJson.Serialize(
            UnrealApiContractJson.Deserialize(json)),
        _ => throw new ArgumentException(
            $"Compatibility family '{familyId}' has no direct rewrite migration.",
            nameof(familyId)),
    };

    private static void TryAddChunk(
        string file,
        string relativePath,
        UnifiedScriptCompatibilityPolicy policy,
        ICollection<UnifiedScriptUpgradePlanItem> items)
    {
        using FileStream stream = File.OpenRead(file);
        Span<byte> header = stackalloc byte[12];
        int bytesRead = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (bytesRead != header.Length || !header[..8].SequenceEqual(ChunkMagic))
        {
            return;
        }

        string version = $"unifiedscript.chunk/v{BinaryPrimitives.ReadUInt16LittleEndian(header[8..])}." +
            BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        UnifiedScriptCompatibilityFamily? family = policy.Families.FirstOrDefault(
            static candidate => candidate.Id == "compiled-program-chunk");
        if (family is null)
        {
            return;
        }
        items.Add(version == family.CurrentVersion
            ? CreateItem(relativePath, version, family)
            : new UnifiedScriptUpgradePlanItem(
                relativePath,
                family.Id,
                version,
                family.CurrentVersion,
                UnifiedScriptUpgradeDisposition.Rebuild,
                "Compiled chunks are immutable delivery artifacts and must be rebuilt from source."));
    }

    private static UnifiedScriptUpgradePlanItem CreateItem(
        string path,
        string version,
        UnifiedScriptCompatibilityFamily family)
    {
        if (version == family.CurrentVersion)
        {
            return new UnifiedScriptUpgradePlanItem(
                path,
                family.Id,
                version,
                family.CurrentVersion,
                UnifiedScriptUpgradeDisposition.Current,
                "The artifact is already at the current version.");
        }

        UnifiedScriptUpgradeDisposition disposition = family.UpgradeStrategy switch
        {
            UnifiedScriptUpgradeStrategy.Rewrite => UnifiedScriptUpgradeDisposition.Rewrite,
            UnifiedScriptUpgradeStrategy.Regenerate => UnifiedScriptUpgradeDisposition.Regenerate,
            UnifiedScriptUpgradeStrategy.Rebuild => UnifiedScriptUpgradeDisposition.Rebuild,
            _ => UnifiedScriptUpgradeDisposition.Blocked,
        };
        string reason = disposition switch
        {
            UnifiedScriptUpgradeDisposition.Rewrite =>
                "A deterministic read-old/write-current migration is available.",
            UnifiedScriptUpgradeDisposition.Regenerate =>
                $"Regenerate this derived artifact from {family.SourceOfTruth}.",
            UnifiedScriptUpgradeDisposition.Rebuild =>
                $"Rebuild this delivery artifact from {family.SourceOfTruth}.",
            _ => "This version requires an explicit manual upgrade.",
        };
        return new UnifiedScriptUpgradePlanItem(
            path,
            family.Id,
            version,
            family.CurrentVersion,
            disposition,
            reason);
    }

    private static string? TryReadSchemaVersion(string path)
    {
        const long maximumJsonBytes = 64 * 1024 * 1024;
        if (new FileInfo(path).Length > maximumJsonBytes)
        {
            return null;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("schemaVersion", out JsonElement schema) &&
                schema.ValueKind == JsonValueKind.String
                    ? schema.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasSameVersionFamily(string candidate, string current)
    {
        int separator = current.LastIndexOf("/v", StringComparison.Ordinal);
        return separator > 0 && candidate.StartsWith(current[..(separator + 2)], StringComparison.Ordinal);
    }
}
