using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.Graph;

public enum SemanticGraphEntityKind
{
    Function,
    Node,
    Edge,
    Region,
}

public enum SemanticGraphChangeKind
{
    Added,
    Removed,
    Updated,
}

public enum SemanticGraphChangeCategory
{
    Signature,
    ControlFlow,
    DataFlow,
    Value,
    Binding,
    Structure,
}

public sealed record SemanticGraphChange(
    SemanticId Id,
    SemanticId FunctionId,
    string FunctionName,
    SemanticGraphEntityKind EntityKind,
    SemanticId EntityId,
    SemanticGraphChangeKind ChangeKind,
    SemanticGraphChangeCategory Category,
    string Facet,
    string? Before,
    string? After,
    string Summary);

public sealed record SemanticGraphFunctionDiff(
    SemanticId FunctionId,
    string QualifiedName,
    string? BaselineFingerprint,
    string? TargetFingerprint,
    IReadOnlyList<SemanticGraphChange> Changes);

public sealed record SemanticGraphDiffSummary(
    int AddedFunctions,
    int RemovedFunctions,
    int ModifiedFunctions,
    int ChangeCount);

public sealed record SemanticGraphDiffDocument(
    string SchemaVersion,
    string GraphSchemaVersion,
    SemanticId BaselineCompilationId,
    SemanticId TargetCompilationId,
    SemanticId? SemanticConfigurationId,
    SemanticGraphDiffSummary Summary,
    IReadOnlyList<SemanticGraphFunctionDiff> Functions)
{
    public const string CurrentSchemaVersion = "unifiedscript.semantic-graph-diff/v0.1";
}

public enum SemanticGraphMergeSource
{
    Ours,
    Theirs,
    Both,
    Removed,
}

public enum SemanticGraphMergeStatus
{
    Clean,
    Conflicted,
}

public sealed record SemanticGraphFunctionResolution(
    SemanticId FunctionId,
    string QualifiedName,
    SemanticGraphMergeSource Source,
    string? BaselineFingerprint,
    string? OursFingerprint,
    string? TheirsFingerprint,
    string? MergedFingerprint);

public sealed record SemanticGraphMergeConflict(
    SemanticId Id,
    SemanticId FunctionId,
    string QualifiedName,
    string Reason,
    SemanticGraphFunctionDiff Ours,
    SemanticGraphFunctionDiff Theirs);

public sealed record SemanticGraphMergeSummary(
    int OursResolutions,
    int TheirsResolutions,
    int IdenticalResolutions,
    int RemovedFunctions,
    int ConflictCount);

public sealed record SemanticGraphMergeDocument(
    string SchemaVersion,
    string GraphSchemaVersion,
    SemanticGraphMergeStatus Status,
    SemanticId BaselineCompilationId,
    SemanticId OursCompilationId,
    SemanticId TheirsCompilationId,
    SemanticId? SemanticConfigurationId,
    SemanticId? MergedCompilationId,
    SemanticGraphMergeSummary Summary,
    IReadOnlyList<SemanticGraphFunctionResolution> Resolutions,
    IReadOnlyList<SemanticGraphMergeConflict> Conflicts)
{
    public const string CurrentSchemaVersion = "unifiedscript.semantic-graph-merge/v0.1";
}

public sealed record SemanticGraphMergeResult(
    SemanticGraphMergeDocument Document,
    GraphDocument? MergedGraph)
{
    public bool Succeeded =>
        Document.Status == SemanticGraphMergeStatus.Clean &&
        MergedGraph is not null;
}

public static class SemanticGraphReviewJson
{
    public static string SerializeDiff(
        SemanticGraphDiffDocument document,
        bool writeIndented = true) => JsonSerializer.Serialize(
            NormalizeAndValidateDiff(document),
            UnifiedJson.CreateOptions(writeIndented));

    public static SemanticGraphDiffDocument DeserializeDiff(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        SemanticGraphDiffDocument? document = JsonSerializer.Deserialize<SemanticGraphDiffDocument>(
            json,
            UnifiedJson.CreateOptions());
        return NormalizeAndValidateDiff(document ??
            throw new JsonException("The semantic graph diff document was empty."));
    }

    public static string SerializeMerge(
        SemanticGraphMergeDocument document,
        bool writeIndented = true) => JsonSerializer.Serialize(
            NormalizeAndValidateMerge(document),
            UnifiedJson.CreateOptions(writeIndented));

    public static SemanticGraphMergeDocument DeserializeMerge(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        SemanticGraphMergeDocument? document = JsonSerializer.Deserialize<SemanticGraphMergeDocument>(
            json,
            UnifiedJson.CreateOptions());
        return NormalizeAndValidateMerge(document ??
            throw new JsonException("The semantic graph merge document was empty."));
    }

    public static SemanticGraphDiffDocument NormalizeAndValidateDiff(
        SemanticGraphDiffDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != SemanticGraphDiffDocument.CurrentSchemaVersion ||
            document.GraphSchemaVersion != GraphDocument.CurrentSchemaVersion ||
            document.BaselineCompilationId.IsEmpty ||
            document.TargetCompilationId.IsEmpty ||
            document.SemanticConfigurationId is SemanticId configurationId && configurationId.IsEmpty ||
            document.Summary is null || document.Functions is null)
        {
            throw new JsonException("The semantic graph diff has invalid required fields.");
        }

        SemanticGraphFunctionDiff[] functions = document.Functions
            .OrderBy(static function => function.QualifiedName, StringComparer.Ordinal)
            .ThenBy(static function => function.FunctionId.Value, StringComparer.Ordinal)
            .Select(NormalizeFunctionDiff)
            .ToArray();
        if (functions.Any(static function => function.FunctionId.IsEmpty ||
                string.IsNullOrWhiteSpace(function.QualifiedName) ||
                function.Changes.Count == 0) ||
            functions.Select(static function => function.FunctionId).Distinct().Count() !=
                functions.Length)
        {
            throw new JsonException("The semantic graph diff has an invalid function change.");
        }

        int added = functions.Count(static function =>
            function.BaselineFingerprint is null && function.TargetFingerprint is not null);
        int removed = functions.Count(static function =>
            function.BaselineFingerprint is not null && function.TargetFingerprint is null);
        int modified = functions.Length - added - removed;
        int changeCount = functions.Sum(static function => function.Changes.Count);
        if (document.Summary != new SemanticGraphDiffSummary(added, removed, modified, changeCount))
        {
            throw new JsonException("The semantic graph diff summary does not match its changes.");
        }

        return document with { Functions = functions };
    }

    public static SemanticGraphMergeDocument NormalizeAndValidateMerge(
        SemanticGraphMergeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != SemanticGraphMergeDocument.CurrentSchemaVersion ||
            document.GraphSchemaVersion != GraphDocument.CurrentSchemaVersion ||
            !Enum.IsDefined(document.Status) ||
            document.BaselineCompilationId.IsEmpty ||
            document.OursCompilationId.IsEmpty ||
            document.TheirsCompilationId.IsEmpty ||
            document.SemanticConfigurationId is SemanticId configurationId && configurationId.IsEmpty ||
            document.MergedCompilationId is SemanticId mergedId && mergedId.IsEmpty ||
            document.Summary is null || document.Resolutions is null || document.Conflicts is null)
        {
            throw new JsonException("The semantic graph merge has invalid required fields.");
        }

        SemanticGraphFunctionResolution[] resolutions = document.Resolutions
            .Select(NormalizeResolution)
            .OrderBy(static resolution => resolution.QualifiedName, StringComparer.Ordinal)
            .ThenBy(static resolution => resolution.FunctionId.Value, StringComparer.Ordinal)
            .ToArray();
        SemanticGraphMergeConflict[] conflicts = document.Conflicts
            .OrderBy(static conflict => conflict.QualifiedName, StringComparer.Ordinal)
            .ThenBy(static conflict => conflict.FunctionId.Value, StringComparer.Ordinal)
            .Select(static conflict => conflict with
            {
                Ours = NormalizeFunctionDiff(conflict.Ours),
                Theirs = NormalizeFunctionDiff(conflict.Theirs),
            })
            .ToArray();
        if (resolutions.Any(static resolution => resolution.FunctionId.IsEmpty ||
                string.IsNullOrWhiteSpace(resolution.QualifiedName) ||
                !Enum.IsDefined(resolution.Source)) ||
            resolutions.Select(static resolution => resolution.FunctionId).Distinct().Count() !=
                resolutions.Length ||
            conflicts.Any(static conflict => conflict.Id.IsEmpty || conflict.FunctionId.IsEmpty ||
                string.IsNullOrWhiteSpace(conflict.QualifiedName) ||
                string.IsNullOrWhiteSpace(conflict.Reason) ||
                conflict.Ours.FunctionId != conflict.FunctionId ||
                conflict.Theirs.FunctionId != conflict.FunctionId ||
                !string.Equals(
                    conflict.Ours.QualifiedName,
                    conflict.QualifiedName,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    conflict.Theirs.QualifiedName,
                    conflict.QualifiedName,
                    StringComparison.Ordinal) ||
                conflict.Ours.BaselineFingerprint != conflict.Theirs.BaselineFingerprint ||
                conflict.Ours.TargetFingerprint == conflict.Theirs.TargetFingerprint) ||
            conflicts.Select(static conflict => conflict.FunctionId).Distinct().Count() !=
                conflicts.Length ||
            resolutions.Select(static resolution => resolution.FunctionId)
                .Intersect(conflicts.Select(static conflict => conflict.FunctionId)).Any())
        {
            throw new JsonException("The semantic graph merge has an invalid resolution or conflict.");
        }

        bool clean = conflicts.Length == 0;
        if (clean != (document.Status == SemanticGraphMergeStatus.Clean) ||
            clean != (document.MergedCompilationId is not null))
        {
            throw new JsonException(
                "A clean semantic graph merge requires a merged compilation identity and no conflicts.");
        }

        SemanticGraphMergeSummary summary = CreateMergeSummary(resolutions, conflicts.Length);
        if (document.Summary != summary)
        {
            throw new JsonException("The semantic graph merge summary does not match its resolutions.");
        }

        return document with { Resolutions = resolutions, Conflicts = conflicts };
    }

    private static SemanticGraphFunctionResolution NormalizeResolution(
        SemanticGraphFunctionResolution resolution)
    {
        if (resolution is null)
        {
            throw new JsonException("The semantic graph merge contains an empty resolution.");
        }
        ValidateFingerprint(resolution.BaselineFingerprint);
        ValidateFingerprint(resolution.OursFingerprint);
        ValidateFingerprint(resolution.TheirsFingerprint);
        ValidateFingerprint(resolution.MergedFingerprint);
        bool validSource = resolution.Source switch
        {
            SemanticGraphMergeSource.Ours =>
                resolution.OursFingerprint is not null &&
                resolution.OursFingerprint != resolution.BaselineFingerprint &&
                resolution.TheirsFingerprint == resolution.BaselineFingerprint &&
                resolution.MergedFingerprint == resolution.OursFingerprint,
            SemanticGraphMergeSource.Theirs =>
                resolution.TheirsFingerprint is not null &&
                resolution.TheirsFingerprint != resolution.BaselineFingerprint &&
                resolution.OursFingerprint == resolution.BaselineFingerprint &&
                resolution.MergedFingerprint == resolution.TheirsFingerprint,
            SemanticGraphMergeSource.Both =>
                resolution.OursFingerprint is not null &&
                resolution.OursFingerprint == resolution.TheirsFingerprint &&
                resolution.OursFingerprint != resolution.BaselineFingerprint &&
                resolution.MergedFingerprint == resolution.OursFingerprint,
            SemanticGraphMergeSource.Removed =>
                resolution.BaselineFingerprint is not null &&
                resolution.MergedFingerprint is null &&
                ((resolution.OursFingerprint is null &&
                  (resolution.TheirsFingerprint is null ||
                   resolution.TheirsFingerprint == resolution.BaselineFingerprint)) ||
                 (resolution.TheirsFingerprint is null &&
                  resolution.OursFingerprint == resolution.BaselineFingerprint)),
            _ => false,
        };
        if (!validSource)
        {
            throw new JsonException(
                $"Semantic graph merge resolution '{resolution.FunctionId}' " +
                "does not match its selected source and fingerprints.");
        }
        return resolution;
    }

    private static SemanticGraphFunctionDiff NormalizeFunctionDiff(
        SemanticGraphFunctionDiff function)
    {
        if (function is null)
        {
            throw new JsonException("The semantic graph diff contains an empty function change.");
        }
        ValidateFingerprint(function.BaselineFingerprint);
        ValidateFingerprint(function.TargetFingerprint);
        if (function.BaselineFingerprint == function.TargetFingerprint)
        {
            throw new JsonException(
                $"Semantic graph function '{function.FunctionId}' does not contain a change.");
        }

        SemanticGraphChange[] changes = function.Changes
            .OrderBy(static change => change.EntityKind)
            .ThenBy(static change => change.EntityId.Value, StringComparer.Ordinal)
            .ThenBy(static change => change.Facet, StringComparer.Ordinal)
            .ThenBy(static change => change.ChangeKind)
            .ToArray();
        if (changes.Any(change => change.Id.IsEmpty ||
                change.FunctionId != function.FunctionId ||
                !string.Equals(change.FunctionName, function.QualifiedName, StringComparison.Ordinal) ||
                change.EntityId.IsEmpty ||
                !Enum.IsDefined(change.EntityKind) ||
                !Enum.IsDefined(change.ChangeKind) ||
                !Enum.IsDefined(change.Category) ||
                string.IsNullOrWhiteSpace(change.Facet) ||
                string.IsNullOrWhiteSpace(change.Summary)) ||
            changes.Select(static change => change.Id).Distinct().Count() != changes.Length)
        {
            throw new JsonException(
                $"Semantic graph function '{function.FunctionId}' has an invalid change.");
        }

        return function with { Changes = changes };
    }

    private static void ValidateFingerprint(string? fingerprint)
    {
        if (fingerprint is not null &&
            (fingerprint.Length != 64 || fingerprint.Any(static value => !Uri.IsHexDigit(value))))
        {
            throw new JsonException($"Invalid semantic graph fingerprint '{fingerprint}'.");
        }
    }

    internal static SemanticGraphMergeSummary CreateMergeSummary(
        IReadOnlyList<SemanticGraphFunctionResolution> resolutions,
        int conflicts) => new(
        resolutions.Count(static resolution => resolution.Source == SemanticGraphMergeSource.Ours),
        resolutions.Count(static resolution => resolution.Source == SemanticGraphMergeSource.Theirs),
        resolutions.Count(static resolution => resolution.Source == SemanticGraphMergeSource.Both),
        resolutions.Count(static resolution => resolution.Source == SemanticGraphMergeSource.Removed),
        conflicts);
}

public sealed class SemanticGraphDiffer
{
    public SemanticGraphDiffDocument Compare(GraphDocument baseline, GraphDocument target)
    {
        baseline = GraphJson.NormalizeAndValidate(baseline);
        target = GraphJson.NormalizeAndValidate(target);
        RequireCompatibleConfiguration(baseline, target, "diff");

        Dictionary<SemanticId, GraphFunction> baselineFunctions = baseline.Functions.ToDictionary(
            static function => function.Id);
        Dictionary<SemanticId, GraphFunction> targetFunctions = target.Functions.ToDictionary(
            static function => function.Id);
        SemanticGraphFunctionDiff[] functions = baselineFunctions.Keys
            .Concat(targetFunctions.Keys)
            .Distinct()
            .Select(id => CreateFunctionDiff(
                baselineFunctions.GetValueOrDefault(id),
                targetFunctions.GetValueOrDefault(id)))
            .Where(static function => function is not null)
            .Cast<SemanticGraphFunctionDiff>()
            .OrderBy(static function => function.QualifiedName, StringComparer.Ordinal)
            .ThenBy(static function => function.FunctionId.Value, StringComparer.Ordinal)
            .ToArray();
        var summary = new SemanticGraphDiffSummary(
            functions.Count(static function => function.BaselineFingerprint is null),
            functions.Count(static function => function.TargetFingerprint is null),
            functions.Count(static function =>
                function.BaselineFingerprint is not null && function.TargetFingerprint is not null),
            functions.Sum(static function => function.Changes.Count));
        return SemanticGraphReviewJson.NormalizeAndValidateDiff(new SemanticGraphDiffDocument(
            SemanticGraphDiffDocument.CurrentSchemaVersion,
            GraphDocument.CurrentSchemaVersion,
            baseline.SourceCompilationId,
            target.SourceCompilationId,
            baseline.SemanticConfigurationId,
            summary,
            functions));
    }

    internal static SemanticGraphFunctionDiff? CreateFunctionDiff(
        GraphFunction? baseline,
        GraphFunction? target)
    {
        if (baseline is null && target is null)
        {
            return null;
        }

        string qualifiedName = target?.QualifiedName ?? baseline!.QualifiedName;
        SemanticId functionId = target?.Id ?? baseline!.Id;
        string? baselineFingerprint = baseline is null ? null : Fingerprint(baseline);
        string? targetFingerprint = target is null ? null : Fingerprint(target);
        if (baselineFingerprint == targetFingerprint)
        {
            return null;
        }

        var changes = new List<SemanticGraphChange>();
        if (baseline is null || target is null)
        {
            AddChange(
                changes,
                functionId,
                qualifiedName,
                SemanticGraphEntityKind.Function,
                functionId,
                baseline is null ? SemanticGraphChangeKind.Added : SemanticGraphChangeKind.Removed,
                SemanticGraphChangeCategory.Structure,
                "presence",
                baseline?.QualifiedName,
                target?.QualifiedName,
                baseline is null
                    ? $"Added visual function {qualifiedName}."
                    : $"Removed visual function {qualifiedName}.");
        }
        else
        {
            CompareFunction(baseline, target, changes);
        }

        return new SemanticGraphFunctionDiff(
            functionId,
            qualifiedName,
            baselineFingerprint,
            targetFingerprint,
            changes);
    }

    internal static string Fingerprint(GraphFunction function)
    {
        CanonicalFunction canonical = Canonicalize(function);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            canonical,
            UnifiedJson.CreateOptions(writeIndented: false));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static void RequireCompatibleConfiguration(
        GraphDocument baseline,
        GraphDocument other,
        string operation)
    {
        if (baseline.SemanticConfigurationId != other.SemanticConfigurationId)
        {
            throw new InvalidDataException(
                $"Semantic graph {operation} requires the same semantic configuration identity; " +
                $"baseline is '{baseline.SemanticConfigurationId}' and input is " +
                $"'{other.SemanticConfigurationId}'.");
        }
    }

    private static void CompareFunction(
        GraphFunction baseline,
        GraphFunction target,
        ICollection<SemanticGraphChange> changes)
    {
        if (!string.Equals(baseline.QualifiedName, target.QualifiedName, StringComparison.Ordinal))
        {
            AddChange(
                changes,
                target.Id,
                target.QualifiedName,
                SemanticGraphEntityKind.Function,
                target.Id,
                SemanticGraphChangeKind.Updated,
                SemanticGraphChangeCategory.Signature,
                "qualifiedName",
                baseline.QualifiedName,
                target.QualifiedName,
                $"Renamed visual function {baseline.QualifiedName} to {target.QualifiedName}.");
        }

        Dictionary<SemanticId, GraphNode> baselineNodes = baseline.Nodes.ToDictionary(
            static node => node.Id);
        Dictionary<SemanticId, GraphNode> targetNodes = target.Nodes.ToDictionary(
            static node => node.Id);
        foreach (SemanticId nodeId in baselineNodes.Keys.Concat(targetNodes.Keys).Distinct())
        {
            GraphNode? before = baselineNodes.GetValueOrDefault(nodeId);
            GraphNode? after = targetNodes.GetValueOrDefault(nodeId);
            if (before is null || after is null)
            {
                AddNodePresenceChange(target, before, after, changes);
            }
            else
            {
                CompareNode(target, before, after, changes);
            }
        }

        Dictionary<SemanticId, GraphEdge> baselineEdges = baseline.Edges.ToDictionary(
            static edge => edge.Id);
        Dictionary<SemanticId, GraphEdge> targetEdges = target.Edges.ToDictionary(
            static edge => edge.Id);
        foreach (SemanticId edgeId in baselineEdges.Keys.Concat(targetEdges.Keys).Distinct())
        {
            GraphEdge? before = baselineEdges.GetValueOrDefault(edgeId);
            GraphEdge? after = targetEdges.GetValueOrDefault(edgeId);
            if (before is null || after is null)
            {
                GraphEdge edge = after ?? before!;
                AddChange(
                    changes,
                    target.Id,
                    target.QualifiedName,
                    SemanticGraphEntityKind.Edge,
                    edge.Id,
                    before is null ? SemanticGraphChangeKind.Added : SemanticGraphChangeKind.Removed,
                    edge.Kind == GraphEdgeKind.Control
                        ? SemanticGraphChangeCategory.ControlFlow
                        : SemanticGraphChangeCategory.DataFlow,
                    "connection",
                    before is null ? null : FormatEdge(before),
                    after is null ? null : FormatEdge(after),
                    before is null
                        ? $"Connected {FormatEdge(after!)}."
                        : $"Disconnected {FormatEdge(before)}.");
            }
            else if (before != after)
            {
                AddChange(
                    changes,
                    target.Id,
                    target.QualifiedName,
                    SemanticGraphEntityKind.Edge,
                    edgeId,
                    SemanticGraphChangeKind.Updated,
                    after.Kind == GraphEdgeKind.Control
                        ? SemanticGraphChangeCategory.ControlFlow
                        : SemanticGraphChangeCategory.DataFlow,
                    "connection",
                    FormatEdge(before),
                    FormatEdge(after),
                    $"Reconnected {FormatEdge(before)} as {FormatEdge(after)}.");
            }
        }

        Dictionary<SemanticId, GraphRegion> baselineRegions = baseline.Regions.ToDictionary(
            static region => region.Id);
        Dictionary<SemanticId, GraphRegion> targetRegions = target.Regions.ToDictionary(
            static region => region.Id);
        foreach (SemanticId regionId in baselineRegions.Keys.Concat(targetRegions.Keys).Distinct())
        {
            GraphRegion? before = baselineRegions.GetValueOrDefault(regionId);
            GraphRegion? after = targetRegions.GetValueOrDefault(regionId);
            if (before is null || after is null)
            {
                GraphRegion region = after ?? before!;
                AddChange(
                    changes,
                    target.Id,
                    target.QualifiedName,
                    SemanticGraphEntityKind.Region,
                    region.Id,
                    before is null ? SemanticGraphChangeKind.Added : SemanticGraphChangeKind.Removed,
                    SemanticGraphChangeCategory.ControlFlow,
                    "presence",
                    before?.Kind.ToString(),
                    after?.Kind.ToString(),
                    before is null
                        ? $"Added {after!.Kind} control region."
                        : $"Removed {before.Kind} control region.");
                continue;
            }

            CompareFacet(target, before.Id, SemanticGraphEntityKind.Region,
                SemanticGraphChangeCategory.ControlFlow, "kind", before.Kind.ToString(),
                after.Kind.ToString(), changes);
            CompareFacet(target, before.Id, SemanticGraphEntityKind.Region,
                SemanticGraphChangeCategory.ControlFlow, "parentRegionId",
                before.ParentRegionId?.Value, after.ParentRegionId?.Value, changes);
            CompareFacet(target, before.Id, SemanticGraphEntityKind.Region,
                SemanticGraphChangeCategory.ControlFlow, "sourceNodeId",
                before.SourceNodeId.Value, after.SourceNodeId.Value, changes);
            CompareFacet(target, before.Id, SemanticGraphEntityKind.Region,
                SemanticGraphChangeCategory.ControlFlow, "isImplicit",
                before.IsImplicit.ToString(), after.IsImplicit.ToString(), changes);
            CompareFacet(target, before.Id, SemanticGraphEntityKind.Region,
                SemanticGraphChangeCategory.ControlFlow, "nodeIds",
                FormatIds(before.NodeIds), FormatIds(after.NodeIds), changes);
            CompareFacet(target, before.Id, SemanticGraphEntityKind.Region,
                SemanticGraphChangeCategory.ControlFlow, "childRegionIds",
                FormatIds(before.ChildRegionIds), FormatIds(after.ChildRegionIds), changes);
        }
    }

    private static void CompareNode(
        GraphFunction function,
        GraphNode before,
        GraphNode after,
        ICollection<SemanticGraphChange> changes)
    {
        CompareFacet(function, before.Id, SemanticGraphEntityKind.Node,
            SemanticGraphChangeCategory.Structure, "kind", before.Kind, after.Kind, changes);
        CompareFacet(function, before.Id, SemanticGraphEntityKind.Node,
            SemanticGraphChangeCategory.Structure, "ports", FormatPorts(before.Ports),
            FormatPorts(after.Ports), changes);
        foreach (string property in before.Properties.Keys.Concat(after.Properties.Keys)
                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            string? beforeValue = before.Properties.GetValueOrDefault(property);
            string? afterValue = after.Properties.GetValueOrDefault(property);
            CompareFacet(
                function,
                before.Id,
                SemanticGraphEntityKind.Node,
                CategoryForProperty(property),
                $"property:{property}",
                beforeValue,
                afterValue,
                changes);
        }
    }

    private static void AddNodePresenceChange(
        GraphFunction function,
        GraphNode? before,
        GraphNode? after,
        ICollection<SemanticGraphChange> changes)
    {
        GraphNode node = after ?? before!;
        AddChange(
            changes,
            function.Id,
            function.QualifiedName,
            SemanticGraphEntityKind.Node,
            node.Id,
            before is null ? SemanticGraphChangeKind.Added : SemanticGraphChangeKind.Removed,
            node.Kind is "branch" or "return" or "break" or "continue"
                ? SemanticGraphChangeCategory.ControlFlow
                : SemanticGraphChangeCategory.Structure,
            "presence",
            before is null ? null : before.Kind,
            after is null ? null : after.Kind,
            before is null
                ? $"Added {after!.Kind} node {after.Label}."
                : $"Removed {before.Kind} node {before.Label}.");
    }

    private static void CompareFacet(
        GraphFunction function,
        SemanticId entityId,
        SemanticGraphEntityKind entityKind,
        SemanticGraphChangeCategory category,
        string facet,
        string? before,
        string? after,
        ICollection<SemanticGraphChange> changes)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return;
        }

        AddChange(
            changes,
            function.Id,
            function.QualifiedName,
            entityKind,
            entityId,
            SemanticGraphChangeKind.Updated,
            category,
            facet,
            before,
            after,
            $"Changed {entityKind.ToString().ToLowerInvariant()} {entityId} {facet} " +
            $"from '{before ?? "<missing>"}' to '{after ?? "<missing>"}'.");
    }

    private static void AddChange(
        ICollection<SemanticGraphChange> changes,
        SemanticId functionId,
        string functionName,
        SemanticGraphEntityKind entityKind,
        SemanticId entityId,
        SemanticGraphChangeKind changeKind,
        SemanticGraphChangeCategory category,
        string facet,
        string? before,
        string? after,
        string summary)
    {
        string key = string.Join(
            '|',
            "semantic-graph-change/v0.1",
            functionId.Value,
            entityKind,
            entityId.Value,
            changeKind,
            facet,
            before ?? "<null>",
            after ?? "<null>");
        changes.Add(new SemanticGraphChange(
            SemanticId.FromKey(key),
            functionId,
            functionName,
            entityKind,
            entityId,
            changeKind,
            category,
            facet,
            before,
            after,
            summary));
    }

    private static SemanticGraphChangeCategory CategoryForProperty(string property) =>
        property.Contains("value", StringComparison.OrdinalIgnoreCase) ||
        property.Contains("literal", StringComparison.OrdinalIgnoreCase) ||
        property.Contains("operator", StringComparison.OrdinalIgnoreCase)
            ? SemanticGraphChangeCategory.Value
            : property.Contains("method", StringComparison.OrdinalIgnoreCase) ||
              property.Contains("symbol", StringComparison.OrdinalIgnoreCase) ||
              property.Contains("type", StringComparison.OrdinalIgnoreCase) ||
              property.Contains("declaration", StringComparison.OrdinalIgnoreCase) ||
              property.Contains("nullFlow", StringComparison.OrdinalIgnoreCase)
                ? SemanticGraphChangeCategory.Binding
                : SemanticGraphChangeCategory.Structure;

    private static string FormatEdge(GraphEdge edge) =>
        $"{edge.FromNode}.{edge.FromPort} -> {edge.ToNode}.{edge.ToPort} ({edge.Kind})";

    private static string FormatIds(IReadOnlyList<SemanticId> ids) =>
        string.Join(',', ids.Select(static id => id.Value));

    private static string FormatPorts(IReadOnlyList<GraphPort> ports) => string.Join(
        '|',
        ports.Select(static port => $"{port.Direction}:{port.Kind}:{port.Name}"));

    private static CanonicalFunction Canonicalize(GraphFunction function) => new(
        function.Id.Value,
        function.QualifiedName,
        function.Nodes.OrderBy(static node => node.Id.Value, StringComparer.Ordinal)
            .Select(static node => new CanonicalNode(
                node.Id.Value,
                node.Kind,
                node.Ports.Select(static port =>
                    $"{port.Direction}:{port.Kind}:{port.Name}").ToArray(),
                node.Properties.OrderBy(static property => property.Key, StringComparer.Ordinal)
                    .Select(static property => $"{property.Key}={property.Value}").ToArray()))
            .ToArray(),
        function.Edges.OrderBy(static edge => edge.Id.Value, StringComparer.Ordinal)
            .Select(static edge => new CanonicalEdge(
                edge.Id.Value,
                edge.Kind.ToString(),
                edge.FromNode.Value,
                edge.FromPort,
                edge.ToNode.Value,
                edge.ToPort))
            .ToArray(),
        function.RootRegionId.Value,
        function.Regions.OrderBy(static region => region.Id.Value, StringComparer.Ordinal)
            .Select(static region => new CanonicalRegion(
                region.Id.Value,
                region.Kind.ToString(),
                region.ParentRegionId?.Value,
                region.SourceNodeId.Value,
                region.IsImplicit,
                region.NodeIds.Select(static id => id.Value).ToArray(),
                region.ChildRegionIds.Select(static id => id.Value).ToArray()))
            .ToArray());

    private sealed record CanonicalFunction(
        string Id,
        string QualifiedName,
        IReadOnlyList<CanonicalNode> Nodes,
        IReadOnlyList<CanonicalEdge> Edges,
        string RootRegionId,
        IReadOnlyList<CanonicalRegion> Regions);

    private sealed record CanonicalNode(
        string Id,
        string Kind,
        IReadOnlyList<string> Ports,
        IReadOnlyList<string> Properties);

    private sealed record CanonicalEdge(
        string Id,
        string Kind,
        string FromNode,
        string FromPort,
        string ToNode,
        string ToPort);

    private sealed record CanonicalRegion(
        string Id,
        string Kind,
        string? ParentRegionId,
        string SourceNodeId,
        bool IsImplicit,
        IReadOnlyList<string> NodeIds,
        IReadOnlyList<string> ChildRegionIds);

}

public sealed class SemanticGraphMerger
{
    public SemanticGraphMergeResult Merge(
        GraphDocument baseline,
        GraphDocument ours,
        GraphDocument theirs)
    {
        baseline = GraphJson.NormalizeAndValidate(baseline);
        ours = GraphJson.NormalizeAndValidate(ours);
        theirs = GraphJson.NormalizeAndValidate(theirs);
        SemanticGraphDiffer.RequireCompatibleConfiguration(baseline, ours, "merge");
        SemanticGraphDiffer.RequireCompatibleConfiguration(baseline, theirs, "merge");

        Dictionary<SemanticId, GraphFunction> baselineFunctions = baseline.Functions.ToDictionary(
            static function => function.Id);
        Dictionary<SemanticId, GraphFunction> oursFunctions = ours.Functions.ToDictionary(
            static function => function.Id);
        Dictionary<SemanticId, GraphFunction> theirsFunctions = theirs.Functions.ToDictionary(
            static function => function.Id);
        var selected = new Dictionary<SemanticId, GraphFunction>();
        var resolutions = new List<SemanticGraphFunctionResolution>();
        var conflicts = new List<SemanticGraphMergeConflict>();

        foreach (SemanticId functionId in baselineFunctions.Keys
                     .Concat(oursFunctions.Keys)
                     .Concat(theirsFunctions.Keys)
                     .Distinct()
                     .OrderBy(static id => id.Value, StringComparer.Ordinal))
        {
            GraphFunction? baseFunction = baselineFunctions.GetValueOrDefault(functionId);
            GraphFunction? oursFunction = oursFunctions.GetValueOrDefault(functionId);
            GraphFunction? theirsFunction = theirsFunctions.GetValueOrDefault(functionId);
            string? baseFingerprint = Fingerprint(baseFunction);
            string? oursFingerprint = Fingerprint(oursFunction);
            string? theirsFingerprint = Fingerprint(theirsFunction);
            if (oursFingerprint == baseFingerprint && theirsFingerprint == baseFingerprint)
            {
                if (baseFunction is not null)
                {
                    selected.Add(functionId, baseFunction);
                }
                continue;
            }

            string qualifiedName = oursFunction?.QualifiedName ??
                theirsFunction?.QualifiedName ?? baseFunction!.QualifiedName;
            GraphFunction? chosen;
            SemanticGraphMergeSource source;
            if (oursFingerprint == theirsFingerprint)
            {
                chosen = oursFunction;
                source = chosen is null ? SemanticGraphMergeSource.Removed :
                    SemanticGraphMergeSource.Both;
            }
            else if (oursFingerprint == baseFingerprint)
            {
                chosen = theirsFunction;
                source = chosen is null ? SemanticGraphMergeSource.Removed :
                    SemanticGraphMergeSource.Theirs;
            }
            else if (theirsFingerprint == baseFingerprint)
            {
                chosen = oursFunction;
                source = chosen is null ? SemanticGraphMergeSource.Removed :
                    SemanticGraphMergeSource.Ours;
            }
            else
            {
                SemanticGraphFunctionDiff oursDiff =
                    SemanticGraphDiffer.CreateFunctionDiff(baseFunction, oursFunction) ??
                    throw new InvalidOperationException("The ours semantic change was empty.");
                SemanticGraphFunctionDiff theirsDiff =
                    SemanticGraphDiffer.CreateFunctionDiff(baseFunction, theirsFunction) ??
                    throw new InvalidOperationException("The theirs semantic change was empty.");
                string reason =
                    "Both branches changed the same visual function to different semantic fingerprints; " +
                    "function-level auto-merge was refused.";
                conflicts.Add(new SemanticGraphMergeConflict(
                    SemanticId.FromKey(
                        $"semantic-graph-conflict/v0.1|{functionId}|{oursFingerprint}|{theirsFingerprint}"),
                    functionId,
                    qualifiedName,
                    reason,
                    oursDiff,
                    theirsDiff));
                continue;
            }

            if (chosen is not null)
            {
                selected.Add(functionId, chosen);
            }
            resolutions.Add(new SemanticGraphFunctionResolution(
                functionId,
                qualifiedName,
                source,
                baseFingerprint,
                oursFingerprint,
                theirsFingerprint,
                Fingerprint(chosen)));
        }

        GraphDocument? mergedGraph = null;
        SemanticId? mergedCompilationId = null;
        if (conflicts.Count == 0)
        {
            string[] mergeParts =
            [
                "semantic-graph-merge/v0.1",
                baseline.SourceCompilationId.Value,
                ours.SourceCompilationId.Value,
                theirs.SourceCompilationId.Value,
                .. selected.Values.OrderBy(static function => function.Id.Value, StringComparer.Ordinal)
                    .Select(function => $"{function.Id}:{SemanticGraphDiffer.Fingerprint(function)}"),
            ];
            string mergeKey = string.Join('|', mergeParts);
            mergedCompilationId = SemanticId.FromKey(mergeKey);
            mergedGraph = GraphJson.NormalizeAndValidate(new GraphDocument(
                GraphDocument.CurrentSchemaVersion,
                mergedCompilationId.Value,
                baseline.SemanticConfigurationId,
                selected.Values.ToArray()));
        }

        SemanticGraphMergeSummary summary = SemanticGraphReviewJson.CreateMergeSummary(
            resolutions,
            conflicts.Count);
        var document = new SemanticGraphMergeDocument(
            SemanticGraphMergeDocument.CurrentSchemaVersion,
            GraphDocument.CurrentSchemaVersion,
            conflicts.Count == 0
                ? SemanticGraphMergeStatus.Clean
                : SemanticGraphMergeStatus.Conflicted,
            baseline.SourceCompilationId,
            ours.SourceCompilationId,
            theirs.SourceCompilationId,
            baseline.SemanticConfigurationId,
            mergedCompilationId,
            summary,
            resolutions,
            conflicts);
        return new SemanticGraphMergeResult(
            SemanticGraphReviewJson.NormalizeAndValidateMerge(document),
            mergedGraph);
    }

    private static string? Fingerprint(GraphFunction? function) =>
        function is null ? null : SemanticGraphDiffer.Fingerprint(function);
}
