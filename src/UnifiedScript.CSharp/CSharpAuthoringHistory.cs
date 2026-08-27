using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.CSharp;

public sealed record CSharpAuthoringHistorySourceDocument(
    string LogicalPath,
    string Source,
    IdentitySidecarDocument? IdentitySidecar,
    IReadOnlyList<CSharpIdentityPin> IdentityPins);

public sealed record CSharpAuthoringHistorySnapshotDocument(
    string SnapshotId,
    SemanticId SourceCompilationId,
    IReadOnlyList<CSharpAuthoringHistorySourceDocument> Sources);

public sealed record CSharpAuthoringHistoryEntryDocument(
    string TransactionId,
    string Description,
    string BeforeSnapshotId,
    string AfterSnapshotId);

public sealed record CSharpAuthoringHistoryDocument(
    string SchemaVersion,
    long Revision,
    SemanticId SemanticConfigurationId,
    string CurrentSnapshotId,
    IReadOnlyList<CSharpAuthoringHistorySnapshotDocument> Snapshots,
    IReadOnlyList<CSharpAuthoringHistoryEntryDocument> UndoHistory,
    IReadOnlyList<CSharpAuthoringHistoryEntryDocument> RedoHistory,
    IReadOnlyList<string> CommittedOperationIds,
    string? LastOperationId)
{
    public const string CurrentSchemaVersion = "unifiedscript.authoring-history/v0.1";
}

public static class CSharpAuthoringHistoryJson
{
    private const int MaximumHistoryEntries = 100;
    private const int MaximumCommittedOperationIds = 100_000;

    public static string Serialize(
        CSharpAuthoringHistoryDocument document,
        bool writeIndented = true) => JsonSerializer.Serialize(
            NormalizeAndValidate(document),
            UnifiedJson.CreateOptions(writeIndented));

    public static CSharpAuthoringHistoryDocument Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        CSharpAuthoringHistoryDocument? document = JsonSerializer.Deserialize<CSharpAuthoringHistoryDocument>(
            json,
            UnifiedJson.CreateOptions());
        return NormalizeAndValidate(document ??
            throw new JsonException("The authoring history document was empty."));
    }

    public static CSharpAuthoringHistoryDocument NormalizeAndValidate(
        CSharpAuthoringHistoryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(
                document.SchemaVersion,
                CSharpAuthoringHistoryDocument.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new JsonException(
                $"Unsupported authoring history schema '{document.SchemaVersion}'.");
        }
        if (document.Revision < 1 || document.SemanticConfigurationId.IsEmpty)
        {
            throw new JsonException(
                "Authoring history requires a positive revision and semantic configuration ID.");
        }
        if (string.IsNullOrWhiteSpace(document.CurrentSnapshotId))
        {
            throw new JsonException("Authoring history requires a current snapshot ID.");
        }
        if (document.Snapshots is null || document.UndoHistory is null ||
            document.RedoHistory is null || document.CommittedOperationIds is null)
        {
            throw new JsonException(
                "Authoring history requires snapshot, Undo, Redo, and operation-ID collections.");
        }

        CSharpAuthoringHistorySnapshotDocument[] snapshots = document.Snapshots
            .Select(NormalizeSnapshot)
            .OrderBy(static snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
            .ToArray();
        if (snapshots.Length == 0)
        {
            throw new JsonException("Authoring history requires at least the current snapshot.");
        }
        Dictionary<string, CSharpAuthoringHistorySnapshotDocument> snapshotsById = snapshots
            .ToDictionary(static snapshot => snapshot.SnapshotId, StringComparer.Ordinal);
        if (!snapshotsById.ContainsKey(document.CurrentSnapshotId))
        {
            throw new JsonException(
                $"Current authoring snapshot '{document.CurrentSnapshotId}' is missing.");
        }

        CSharpAuthoringHistoryEntryDocument[] undo = document.UndoHistory
            .Select(entry => NormalizeEntry(entry, snapshotsById))
            .ToArray();
        CSharpAuthoringHistoryEntryDocument[] redo = document.RedoHistory
            .Select(entry => NormalizeEntry(entry, snapshotsById))
            .ToArray();
        if (undo.Length + redo.Length > MaximumHistoryEntries)
        {
            throw new JsonException(
                $"Authoring history cannot retain more than {MaximumHistoryEntries} transactions.");
        }
        string[] historyTransactionIds = undo.Concat(redo)
            .Select(static entry => entry.TransactionId)
            .ToArray();
        if (historyTransactionIds.Distinct(StringComparer.Ordinal).Count() !=
            historyTransactionIds.Length)
        {
            throw new JsonException("Authoring history transaction IDs must be unique.");
        }
        ValidateHistoryChain(document.CurrentSnapshotId, undo, redo);

        HashSet<string> referencedSnapshotIds =
        [
            document.CurrentSnapshotId,
            .. undo.SelectMany(static entry => new[]
            {
                entry.BeforeSnapshotId,
                entry.AfterSnapshotId,
            }),
            .. redo.SelectMany(static entry => new[]
            {
                entry.BeforeSnapshotId,
                entry.AfterSnapshotId,
            }),
        ];
        if (referencedSnapshotIds.Count != snapshots.Length)
        {
            throw new JsonException("Authoring history contains an unreferenced snapshot.");
        }

        string[] committedIds = document.CommittedOperationIds
            .Select(NormalizeOperationId)
            .ToArray();
        if (committedIds.Length > MaximumCommittedOperationIds ||
            committedIds.Distinct(StringComparer.Ordinal).Count() != committedIds.Length)
        {
            throw new JsonException(
                "Committed authoring operation IDs must be unique and within the journal limit.");
        }
        HashSet<string> committed = committedIds.ToHashSet(StringComparer.Ordinal);
        foreach (CSharpAuthoringHistoryEntryDocument entry in undo.Concat(redo))
        {
            if (!committed.Contains(entry.TransactionId))
            {
                throw new JsonException(
                    $"History transaction '{entry.TransactionId}' is not reserved by the journal.");
            }
        }

        string? lastOperationId = document.LastOperationId is null
            ? null
            : NormalizeOperationId(document.LastOperationId);
        if (lastOperationId is not null && !committed.Contains(lastOperationId))
        {
            throw new JsonException(
                $"Last operation '{lastOperationId}' is not reserved by the journal.");
        }

        return new CSharpAuthoringHistoryDocument(
            CSharpAuthoringHistoryDocument.CurrentSchemaVersion,
            document.Revision,
            document.SemanticConfigurationId,
            document.CurrentSnapshotId,
            snapshots,
            undo,
            redo,
            committedIds,
            lastOperationId);
    }

    public static string CreateSnapshotId(
        IEnumerable<CSharpAuthoringHistorySourceDocument> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (CSharpAuthoringHistorySourceDocument source in sources
                     .OrderBy(static source => source.LogicalPath, StringComparer.Ordinal))
        {
            Append(hash, source.LogicalPath.Replace('\\', '/'));
            Append(hash, source.Source);
            Append(
                hash,
                source.IdentitySidecar is null
                    ? string.Empty
                    : IdentitySidecarJson.Serialize(source.IdentitySidecar, writeIndented: false));
            Append(
                hash,
                JsonSerializer.Serialize(
                    source.IdentityPins.OrderBy(static pin => pin.Id.Value, StringComparer.Ordinal),
                    UnifiedJson.CreateOptions(writeIndented: false)));
        }
        return $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private static CSharpAuthoringHistorySnapshotDocument NormalizeSnapshot(
        CSharpAuthoringHistorySnapshotDocument snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SourceCompilationId.IsEmpty)
        {
            throw new JsonException("An authoring history snapshot has no compilation ID.");
        }
        if (snapshot.Sources is null)
        {
            throw new JsonException("An authoring history snapshot has no source collection.");
        }
        CSharpAuthoringHistorySourceDocument[] sources = snapshot.Sources
            .Select(NormalizeSource)
            .OrderBy(static source => source.LogicalPath, StringComparer.Ordinal)
            .ToArray();
        if (sources.Length == 0 ||
            sources.Select(static source => source.LogicalPath)
                .Distinct(StringComparer.Ordinal).Count() != sources.Length)
        {
            throw new JsonException(
                "An authoring history snapshot requires unique logical source paths.");
        }
        string expectedId = CreateSnapshotId(sources);
        if (!string.Equals(snapshot.SnapshotId, expectedId, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"Authoring history snapshot digest '{snapshot.SnapshotId}' is invalid.");
        }
        return new CSharpAuthoringHistorySnapshotDocument(
            expectedId,
            snapshot.SourceCompilationId,
            sources);
    }

    private static CSharpAuthoringHistorySourceDocument NormalizeSource(
        CSharpAuthoringHistorySourceDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.LogicalPath);
        ArgumentNullException.ThrowIfNull(source.Source);
        string logicalPath = source.LogicalPath.Replace('\\', '/');
        IdentitySidecarDocument? identity = source.IdentitySidecar is null
            ? null
            : IdentitySidecarJson.NormalizeAndValidate(source.IdentitySidecar);
        if (identity is not null &&
            !string.Equals(identity.SourcePath, logicalPath, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"History identity path '{identity.SourcePath}' does not match '{logicalPath}'.");
        }
        CSharpIdentityPin[] pins = (source.IdentityPins ?? [])
            .OrderBy(static pin => pin.Id.Value, StringComparer.Ordinal)
            .ToArray();
        _ = new CSharpSourceDocument(logicalPath, source.Source, identity, pins);
        return new CSharpAuthoringHistorySourceDocument(logicalPath, source.Source, identity, pins);
    }

    private static CSharpAuthoringHistoryEntryDocument NormalizeEntry(
        CSharpAuthoringHistoryEntryDocument entry,
        IReadOnlyDictionary<string, CSharpAuthoringHistorySnapshotDocument> snapshots)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string transactionId = NormalizeOperationId(entry.TransactionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Description);
        if (entry.Description.Length > 256)
        {
            throw new JsonException("Authoring history descriptions must be at most 256 characters.");
        }
        if (!snapshots.ContainsKey(entry.BeforeSnapshotId) ||
            !snapshots.ContainsKey(entry.AfterSnapshotId) ||
            string.Equals(entry.BeforeSnapshotId, entry.AfterSnapshotId, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"History transaction '{transactionId}' has invalid snapshot references.");
        }
        return new CSharpAuthoringHistoryEntryDocument(
            transactionId,
            entry.Description,
            entry.BeforeSnapshotId,
            entry.AfterSnapshotId);
    }

    private static void ValidateHistoryChain(
        string currentSnapshotId,
        IReadOnlyList<CSharpAuthoringHistoryEntryDocument> undo,
        IReadOnlyList<CSharpAuthoringHistoryEntryDocument> redo)
    {
        for (int index = 1; index < undo.Count; index++)
        {
            if (!string.Equals(
                    undo[index - 1].AfterSnapshotId,
                    undo[index].BeforeSnapshotId,
                    StringComparison.Ordinal))
            {
                throw new JsonException("The persisted undo history is not one continuous chain.");
            }
        }
        if (undo.Count > 0 &&
            !string.Equals(undo[^1].AfterSnapshotId, currentSnapshotId, StringComparison.Ordinal))
        {
            throw new JsonException("The persisted undo history does not end at the current snapshot.");
        }

        string redoCursor = currentSnapshotId;
        for (int index = redo.Count - 1; index >= 0; index--)
        {
            CSharpAuthoringHistoryEntryDocument entry = redo[index];
            if (!string.Equals(entry.BeforeSnapshotId, redoCursor, StringComparison.Ordinal))
            {
                throw new JsonException("The persisted redo history is not one continuous chain.");
            }
            redoCursor = entry.AfterSnapshotId;
        }
    }

    private static string NormalizeOperationId(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (operationId.Length > 128)
        {
            throw new JsonException("Authoring operation IDs must be at most 128 characters.");
        }
        return operationId;
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
