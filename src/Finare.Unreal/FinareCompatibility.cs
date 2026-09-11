using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Finare.Core;

namespace Finare.Unreal;

public enum FinareUpgradeStrategy
{
    Preserve,
    Rewrite,
    Regenerate,
    Rebuild,
    Manual,
}

public enum FinareUpgradeDisposition
{
    Current,
    Rewrite,
    Regenerate,
    Rebuild,
    Blocked,
}

public sealed record FinareCompatibilityFamily(
    string Id,
    string Category,
    string CurrentVersion,
    IReadOnlyList<string> ReadableVersions,
    FinareUpgradeStrategy UpgradeStrategy,
    string SourceOfTruth,
    string Producer,
    string Consumer)
{
    public IReadOnlyDictionary<string, int>? Components { get; init; }

    public string? Notes { get; init; }
}

public sealed record FinareCompatibilityPolicy(
    string SchemaVersion,
    string ProductVersion,
    IReadOnlyList<FinareCompatibilityFamily> Families)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.compatibility-policy/v0.1";
}

public sealed record FinareUpgradePlanItem(
    string Path,
    string FamilyId,
    string DetectedVersion,
    string CurrentVersion,
    FinareUpgradeDisposition Disposition,
    string Reason);

public sealed record FinareUpgradePlanSummary(
    int Scanned,
    int Current,
    int Rewrite,
    int Regenerate,
    int Rebuild,
    int Blocked);

public sealed record FinareUpgradePlan(
    string SchemaVersion,
    string PolicyVersion,
    string Input,
    IReadOnlyList<FinareUpgradePlanItem> Items,
    FinareUpgradePlanSummary Summary)
{
    public const string CurrentSchemaVersion = "unifiedscript.upgrade-plan/v0.1";
}

public static class FinareCompatibilityJson
{
    public static FinareCompatibilityPolicy DeserializePolicy(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        FinareCompatibilityPolicy? policy =
            JsonSerializer.Deserialize<FinareCompatibilityPolicy>(json, CreateOptions(true));
        return NormalizePolicy(policy ??
            throw new JsonException("The Finare compatibility policy was empty."));
    }

    public static string SerializePolicy(
        FinareCompatibilityPolicy policy,
        bool writeIndented = true) =>
        JsonSerializer.Serialize(NormalizePolicy(policy), CreateOptions(writeIndented))
            .ReplaceLineEndings("\n") + "\n";

    public static string SerializePlan(
        FinareUpgradePlan plan,
        bool writeIndented = true) =>
        JsonSerializer.Serialize(plan, CreateOptions(writeIndented))
            .ReplaceLineEndings("\n") + "\n";

    private static FinareCompatibilityPolicy NormalizePolicy(
        FinareCompatibilityPolicy policy)
    {
        if (policy.SchemaVersion != FinareCompatibilityPolicy.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(policy.ProductVersion) ||
            policy.Families is null || policy.Families.Count == 0)
        {
            throw new JsonException("The Finare compatibility policy has invalid required fields.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var ownedVersions = new HashSet<string>(StringComparer.Ordinal);
        var families = new List<FinareCompatibilityFamily>(policy.Families.Count);
        foreach (FinareCompatibilityFamily? family in policy.Families)
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
                throw new JsonException("The Finare compatibility policy contains an invalid family.");
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

public static class FinareUpgradePlanner
{
    private static ReadOnlySpan<byte> ChunkMagic => "USCHUNK\0"u8;

    public static FinareUpgradePlan CreatePlan(
        FinareCompatibilityPolicy policy,
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

        IReadOnlyDictionary<string, FinareCompatibilityFamily> exactVersions = policy.Families
            .SelectMany(static family => family.ReadableVersions
                .Append(family.CurrentVersion)
                .Select(version => new KeyValuePair<string, FinareCompatibilityFamily>(
                    version,
                    family)))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        FinareCompatibilityFamily[] schemaFamilies = policy.Families
            .Where(static family => family.CurrentVersion.StartsWith(
                "unifiedscript.",
                StringComparison.Ordinal))
            .ToArray();

        IEnumerable<string> files = isFile
            ? [fullInput]
            : Directory.EnumerateFiles(fullInput, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal);
        var items = new List<FinareUpgradePlanItem>();
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
            if (exactVersions.TryGetValue(version, out FinareCompatibilityFamily? family))
            {
                items.Add(CreateItem(relativePath, version, family));
                continue;
            }

            family = schemaFamilies.FirstOrDefault(candidate =>
                HasSameVersionFamily(version, candidate.CurrentVersion));
            if (family is not null)
            {
                FinareUpgradeDisposition disposition = family.UpgradeStrategy switch
                {
                    FinareUpgradeStrategy.Regenerate =>
                        FinareUpgradeDisposition.Regenerate,
                    FinareUpgradeStrategy.Rebuild =>
                        FinareUpgradeDisposition.Rebuild,
                    _ => FinareUpgradeDisposition.Blocked,
                };
                string reason = disposition switch
                {
                    FinareUpgradeDisposition.Regenerate =>
                        $"The version is not readable; regenerate the derived artifact from {family.SourceOfTruth}.",
                    FinareUpgradeDisposition.Rebuild =>
                        $"The version is not readable; rebuild the delivery artifact from {family.SourceOfTruth}.",
                    _ => "The artifact belongs to a known family but its version is not readable by this release.",
                };
                items.Add(new FinareUpgradePlanItem(
                    relativePath,
                    family.Id,
                    version,
                    family.CurrentVersion,
                    disposition,
                    reason));
            }
        }

        FinareUpgradePlanItem[] ordered = items
            .OrderBy(static item => item.Path, StringComparer.Ordinal)
            .ThenBy(static item => item.FamilyId, StringComparer.Ordinal)
            .ToArray();
        return new FinareUpgradePlan(
            FinareUpgradePlan.CurrentSchemaVersion,
            policy.ProductVersion,
            isFile ? Path.GetFileName(fullInput) : ".",
            ordered,
            new FinareUpgradePlanSummary(
                ordered.Length,
                ordered.Count(static item => item.Disposition == FinareUpgradeDisposition.Current),
                ordered.Count(static item => item.Disposition == FinareUpgradeDisposition.Rewrite),
                ordered.Count(static item => item.Disposition == FinareUpgradeDisposition.Regenerate),
                ordered.Count(static item => item.Disposition == FinareUpgradeDisposition.Rebuild),
                ordered.Count(static item => item.Disposition == FinareUpgradeDisposition.Blocked)));
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
        FinareCompatibilityPolicy policy,
        ICollection<FinareUpgradePlanItem> items)
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
        FinareCompatibilityFamily? family = policy.Families.FirstOrDefault(
            static candidate => candidate.Id == "compiled-program-chunk");
        if (family is null)
        {
            return;
        }
        items.Add(version == family.CurrentVersion
            ? CreateItem(relativePath, version, family)
            : new FinareUpgradePlanItem(
                relativePath,
                family.Id,
                version,
                family.CurrentVersion,
                FinareUpgradeDisposition.Rebuild,
                "Compiled chunks are immutable delivery artifacts and must be rebuilt from source."));
    }

    private static FinareUpgradePlanItem CreateItem(
        string path,
        string version,
        FinareCompatibilityFamily family)
    {
        if (version == family.CurrentVersion)
        {
            return new FinareUpgradePlanItem(
                path,
                family.Id,
                version,
                family.CurrentVersion,
                FinareUpgradeDisposition.Current,
                "The artifact is already at the current version.");
        }

        FinareUpgradeDisposition disposition = family.UpgradeStrategy switch
        {
            FinareUpgradeStrategy.Rewrite => FinareUpgradeDisposition.Rewrite,
            FinareUpgradeStrategy.Regenerate => FinareUpgradeDisposition.Regenerate,
            FinareUpgradeStrategy.Rebuild => FinareUpgradeDisposition.Rebuild,
            _ => FinareUpgradeDisposition.Blocked,
        };
        string reason = disposition switch
        {
            FinareUpgradeDisposition.Rewrite =>
                "A deterministic read-old/write-current migration is available.",
            FinareUpgradeDisposition.Regenerate =>
                $"Regenerate this derived artifact from {family.SourceOfTruth}.",
            FinareUpgradeDisposition.Rebuild =>
                $"Rebuild this delivery artifact from {family.SourceOfTruth}.",
            _ => "This version requires an explicit manual upgrade.",
        };
        return new FinareUpgradePlanItem(
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
