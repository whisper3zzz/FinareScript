using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnifiedScript.Core;

public static partial class IdentitySidecarJson
{
    public static string Serialize(IdentitySidecarDocument document, bool writeIndented = true)
    {
        IdentitySidecarDocument normalized = NormalizeAndValidate(document);
        return JsonSerializer.Serialize(normalized, UnifiedJson.CreateOptions(writeIndented));
    }

    public static IdentitySidecarDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        IdentitySidecarDocument? document = JsonSerializer.Deserialize<IdentitySidecarDocument>(
            json,
            UnifiedJson.CreateOptions());
        if (document is null)
        {
            throw new JsonException("The identity sidecar document was empty.");
        }

        return NormalizeAndValidate(document);
    }

    public static IdentitySidecarDocument NormalizeAndValidate(IdentitySidecarDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != IdentitySidecarDocument.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported identity sidecar schema '{document.SchemaVersion}'. " +
                $"Expected '{IdentitySidecarDocument.CurrentSchemaVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(document.SourcePath))
        {
            throw new JsonException("An identity sidecar requires a source path.");
        }

        if (!Sha256DigestPattern().IsMatch(document.SourceDigest ?? string.Empty))
        {
            throw new JsonException(
                "An identity sidecar source digest must use 'sha256:' followed by 64 lowercase hex digits.");
        }

        if (document.Generation < 0)
        {
            throw new JsonException("An identity sidecar generation cannot be negative.");
        }

        if (document.Entries is null || document.Tombstones is null)
        {
            throw new JsonException("Identity sidecar entries and tombstones cannot be null.");
        }

        var ids = new HashSet<SemanticId>();
        foreach (IdentityEntry? entry in document.Entries)
        {
            ValidateEntry(entry, document.Generation);
            if (!ids.Add(entry!.Id))
            {
                throw new JsonException($"Duplicate live identity '{entry.Id}'.");
            }
        }

        foreach (IdentityTombstone? tombstone in document.Tombstones)
        {
            ValidateTombstone(tombstone, document.Generation);
            if (!ids.Add(tombstone!.Id))
            {
                throw new JsonException(
                    $"Identity '{tombstone.Id}' appears more than once across live entries and tombstones.");
            }
        }

        return document with
        {
            SourcePath = IdentitySidecarDocument.NormalizePath(document.SourcePath),
            Entries = document.Entries
                .OrderBy(static entry => entry.Id.Value, StringComparer.Ordinal)
                .ToArray(),
            Tombstones = document.Tombstones
                .OrderBy(static tombstone => tombstone.Id.Value, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static void ValidateEntry(IdentityEntry? entry, int documentGeneration)
    {
        if (entry is null)
        {
            throw new JsonException("Identity sidecar entries cannot contain null.");
        }

        if (entry.Id.IsEmpty ||
            string.IsNullOrWhiteSpace(entry.Kind) ||
            string.IsNullOrWhiteSpace(entry.Owner) ||
            string.IsNullOrWhiteSpace(entry.Role) ||
            !Sha256DigestPattern().IsMatch(entry.Fingerprint ?? string.Empty) ||
            !Sha256DigestPattern().IsMatch(entry.ShapeFingerprint ?? string.Empty) ||
            !Sha256DigestPattern().IsMatch(entry.ParentFingerprint ?? string.Empty) ||
            !Sha256DigestPattern().IsMatch(entry.ContextBefore ?? string.Empty) ||
            !Sha256DigestPattern().IsMatch(entry.ContextAfter ?? string.Empty))
        {
            throw new JsonException($"Identity entry '{entry.Id}' has invalid required fields.");
        }

        if (entry.Anchor is null || entry.Anchor.Start < 0 || entry.Anchor.Length < 0)
        {
            throw new JsonException($"Identity entry '{entry.Id}' has an invalid anchor.");
        }

        if (entry.Generation < 0 || entry.Generation > documentGeneration)
        {
            throw new JsonException($"Identity entry '{entry.Id}' has an invalid generation.");
        }
    }

    private static void ValidateTombstone(IdentityTombstone? tombstone, int documentGeneration)
    {
        if (tombstone is null ||
            tombstone.Id.IsEmpty ||
            string.IsNullOrWhiteSpace(tombstone.Kind) ||
            string.IsNullOrWhiteSpace(tombstone.Owner) ||
            tombstone.Generation < 0 ||
            tombstone.Generation > documentGeneration)
        {
            throw new JsonException("An identity tombstone has invalid required fields.");
        }
    }

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256DigestPattern();
}
