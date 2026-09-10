namespace Finare.Core;

public sealed record IdentityAnchor(int Start, int Length);

public sealed record IdentityEntry(
    SemanticId Id,
    string Kind,
    string Owner,
    string Role,
    string Fingerprint,
    string ShapeFingerprint,
    string ParentFingerprint,
    IdentityAnchor Anchor,
    string ContextBefore,
    string ContextAfter,
    int Generation);

public sealed record IdentityTombstone(
    SemanticId Id,
    string Kind,
    string Owner,
    int Generation);

public sealed record IdentitySidecarDocument(
    string SchemaVersion,
    string SourcePath,
    string SourceDigest,
    int Generation,
    IReadOnlyList<IdentityEntry> Entries,
    IReadOnlyList<IdentityTombstone> Tombstones)
{
    public const string CurrentSchemaVersion = "unifiedscript.identity/v0.1";

    public static IdentitySidecarDocument CreateEmpty(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return new IdentitySidecarDocument(
            CurrentSchemaVersion,
            NormalizePath(sourcePath),
            $"sha256:{new string('0', 64)}",
            0,
            [],
            []);
    }

    internal static string NormalizePath(string path) => path.Replace('\\', '/');
}
