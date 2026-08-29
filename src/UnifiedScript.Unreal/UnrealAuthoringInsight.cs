using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.Unreal;

public sealed record UnrealAuthoringInsightRequest(
    string SchemaVersion,
    UnrealGameplayProfileTrace Trace,
    string? FocusedMethodId,
    string? FocusedNodeId)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.authoring-insight-request/v0.1";
}

public sealed record UnrealAuthoringInsightSummary(
    long FrameCount,
    long TotalNanoseconds,
    long AverageFrameNanoseconds,
    long AllocatedBytes,
    long DroppedFrameCount,
    long BufferOverflowCount,
    int ResolvedNodeCount,
    int UnresolvedNodeCount);

public sealed record UnrealAuthoringInsightLocation(
    string MethodId,
    string NodeId,
    string ActionId,
    string Title,
    string Summary,
    string Category,
    SourceSpan SourceSpan);

public sealed record UnrealAuthoringInsightApiRoute(
    string RuntimeBoundary,
    string ContractRoute,
    int CatalogMemberCount,
    IReadOnlyList<string> CatalogExamples);

public sealed record UnrealAuthoringInsightBoundary(
    string Name,
    string ContractRoute,
    long Count,
    long TotalNanoseconds,
    double PercentOfOwner,
    int CatalogMemberCount);

public sealed record UnrealAuthoringInsightNode(
    int Rank,
    string MethodId,
    string NodeId,
    string? ActionId,
    string Title,
    string Summary,
    string Category,
    SourceSpan? SourceSpan,
    long HitCount,
    long TotalNanoseconds,
    long AverageHitNanoseconds,
    double PercentOfTrace,
    bool Focused,
    IReadOnlyList<UnrealAuthoringInsightBoundary> Boundaries);

public sealed record UnrealAuthoringInsightSnapshot(
    string SchemaVersion,
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId,
    string TraceId,
    DateTimeOffset CapturedAtUtc,
    string WorldHostId,
    string InstanceId,
    UnrealAuthoringInsightSummary Summary,
    IReadOnlyList<UnrealGameplayProfileBoundary> WorldPhases,
    IReadOnlyList<UnrealAuthoringInsightBoundary> Boundaries,
    IReadOnlyList<UnrealAuthoringInsightApiRoute> ApiRoutes,
    IReadOnlyList<UnrealAuthoringInsightNode> Nodes)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.authoring-insight/v0.1";
}

/// <summary>
/// Joins the live gameplay profiler to the canonical authoring Surface and the
/// generated Unreal API catalog. Runtime boundary routes are exact profiler
/// attribution; catalog examples describe APIs that use that route and are not
/// presented as a per-call trace.
/// </summary>
public static class UnrealAuthoringInsight
{
    private const string ManagedCodeBoundary = "managedCode";
    private const string ManagedCodeRoute = "managedCode";
    private const string UnknownRoute = "unknown";

    public static UnrealAuthoringInsightSnapshot Create(
        SemanticId sourceCompilationId,
        SemanticId semanticConfigurationId,
        IReadOnlyList<UnrealAuthoringInsightLocation> locations,
        UnrealAuthoringInsightRequest request,
        UnrealApiBrowserDocument? apiBrowser = null)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(request);
        if (sourceCompilationId.IsEmpty || semanticConfigurationId.IsEmpty)
        {
            throw new ArgumentException(
                "Authoring insight requires source and semantic configuration identities.");
        }
        ValidateLocations(locations);
        ValidateRequest(request);
        ValidateTrace(request.Trace);
        ValidateApiBrowser(apiBrowser);

        Dictionary<(string MethodId, string NodeId), UnrealAuthoringInsightLocation> actions =
            locations.ToDictionary(
                static location => (location.MethodId, location.NodeId));
        Dictionary<string, UnrealAuthoringInsightApiRoute> routes = BuildApiRoutes(apiBrowser);
        UnrealGameplayProfileTrace trace = request.Trace;
        UnrealAuthoringInsightBoundary[] boundaries = BuildBoundaries(
            trace.Aggregate.Boundaries,
            trace.Aggregate.TotalNanoseconds,
            routes,
            includeManagedCode: true);
        UnrealAuthoringInsightNode[] nodes = trace.Aggregate.Nodes
            .OrderByDescending(static node => node.TotalNanoseconds)
            .ThenByDescending(static node => node.HitCount)
            .ThenBy(static node => node.MethodId, StringComparer.Ordinal)
            .ThenBy(static node => node.NodeId, StringComparer.Ordinal)
            .Select((node, index) =>
            {
                actions.TryGetValue(
                    (node.MethodId, node.NodeId),
                    out UnrealAuthoringInsightLocation? action);
                return new UnrealAuthoringInsightNode(
                    index + 1,
                    node.MethodId,
                    node.NodeId,
                    action?.ActionId,
                    action?.Title ?? "Unresolved semantic node",
                    action?.Summary ?? "The captured semantic ID is not present in this authoring generation.",
                    action?.Category ?? "Unresolved",
                    action?.SourceSpan,
                    node.HitCount,
                    node.TotalNanoseconds,
                    DivideRounded(node.TotalNanoseconds, node.HitCount),
                    Percent(node.TotalNanoseconds, trace.Aggregate.TotalNanoseconds),
                    request.FocusedMethodId == node.MethodId &&
                        request.FocusedNodeId == node.NodeId,
                    BuildBoundaries(
                        node.Boundaries,
                        node.TotalNanoseconds,
                        routes,
                        includeManagedCode: true));
            })
            .ToArray();
        int resolved = nodes.Count(static node => node.ActionId is not null);
        UnrealGameplayProfileBufferPressure pressure = trace.Aggregate.BufferPressure;
        return new UnrealAuthoringInsightSnapshot(
            UnrealAuthoringInsightSnapshot.CurrentSchemaVersion,
            sourceCompilationId,
            semanticConfigurationId,
            trace.TraceId,
            trace.CapturedAtUtc,
            trace.WorldHostId,
            trace.InstanceId,
            new UnrealAuthoringInsightSummary(
                trace.Aggregate.FrameCount,
                trace.Aggregate.TotalNanoseconds,
                DivideRounded(trace.Aggregate.TotalNanoseconds, trace.Aggregate.FrameCount),
                trace.Aggregate.AllocatedBytes,
                trace.DroppedFrameCount,
                SaturatingSum(
                    pressure.CommandOverflowCount,
                    pressure.EventOverflowCount,
                    pressure.TraceOverflowCount,
                    pressure.OverlapOverflowCount),
                resolved,
                nodes.Length - resolved),
            trace.Aggregate.WorldPhases
                .OrderBy(static phase => phase.Name, StringComparer.Ordinal)
                .ToArray(),
            boundaries,
            routes.Values
                .OrderBy(static route => route.RuntimeBoundary, StringComparer.Ordinal)
                .ToArray(),
            nodes);
    }

    private static UnrealAuthoringInsightBoundary[] BuildBoundaries(
        IReadOnlyList<UnrealGameplayProfileBoundary> source,
        long ownerNanoseconds,
        IReadOnlyDictionary<string, UnrealAuthoringInsightApiRoute> routes,
        bool includeManagedCode)
    {
        IEnumerable<UnrealGameplayProfileBoundary> values = source;
        if (includeManagedCode)
        {
            long attributed = source.Aggregate(
                0L,
                static (total, boundary) => SaturatingAdd(total, boundary.TotalNanoseconds));
            values = values.Append(new UnrealGameplayProfileBoundary(
                ManagedCodeBoundary,
                0,
                Math.Max(0, ownerNanoseconds - attributed)));
        }
        return values
            .Where(static boundary => boundary.Count > 0 || boundary.TotalNanoseconds > 0)
            .OrderByDescending(static boundary => boundary.TotalNanoseconds)
            .ThenBy(static boundary => boundary.Name, StringComparer.Ordinal)
            .Select(boundary =>
            {
                routes.TryGetValue(boundary.Name, out UnrealAuthoringInsightApiRoute? route);
                return new UnrealAuthoringInsightBoundary(
                    boundary.Name,
                    route?.ContractRoute ??
                        (boundary.Name == ManagedCodeBoundary ? ManagedCodeRoute : UnknownRoute),
                    boundary.Count,
                    boundary.TotalNanoseconds,
                    Percent(boundary.TotalNanoseconds, ownerNanoseconds),
                    route?.CatalogMemberCount ?? 0);
            })
            .ToArray();
    }

    private static Dictionary<string, UnrealAuthoringInsightApiRoute> BuildApiRoutes(
        UnrealApiBrowserDocument? browser)
    {
        UnrealApiBrowserMember[] members = browser?.Types
            .SelectMany(static type => type.Members)
            .ToArray() ?? [];
        return RuntimeRoutes().ToDictionary(
            static pair => pair.Boundary,
            pair =>
            {
                string route = pair.Disposition.ToString();
                UnrealApiBrowserMember[] matches = members
                    .Where(member => member.Disposition == pair.Disposition)
                    .OrderBy(static member => member.MemberPath, StringComparer.Ordinal)
                    .ToArray();
                return new UnrealAuthoringInsightApiRoute(
                    pair.Boundary,
                    route,
                    matches.Length,
                    matches.Take(8).Select(static member => member.MemberPath).ToArray());
            },
            StringComparer.Ordinal);
    }

    private static IEnumerable<(string Boundary, UnrealBindingDisposition Disposition)>
        RuntimeRoutes()
    {
        yield return ("directThunk", UnrealBindingDisposition.DirectThunk);
        yield return ("callFrame", UnrealBindingDisposition.CallFrame);
        yield return ("reflectionFallback", UnrealBindingDisposition.ReflectionFallback);
        yield return ("commands", UnrealBindingDisposition.DataPlaneOperation);
        yield return ("events", UnrealBindingDisposition.DataPlaneOperation);
        yield return ("batchTrace", UnrealBindingDisposition.DataPlaneOperation);
        yield return ("batchOverlap", UnrealBindingDisposition.DataPlaneOperation);
    }

    private static void ValidateRequest(UnrealAuthoringInsightRequest request)
    {
        bool hasMethod = !string.IsNullOrWhiteSpace(request.FocusedMethodId);
        bool hasNode = !string.IsNullOrWhiteSpace(request.FocusedNodeId);
        if (request.SchemaVersion != UnrealAuthoringInsightRequest.CurrentSchemaVersion ||
            request.Trace is null || hasMethod != hasNode)
        {
            throw new InvalidDataException(
                $"Authoring insight input must be a valid " +
                $"{UnrealAuthoringInsightRequest.CurrentSchemaVersion} request.");
        }
    }

    private static void ValidateLocations(
        IReadOnlyList<UnrealAuthoringInsightLocation> locations)
    {
        if (locations.Count > 16384 || locations.Any(static location =>
                location is null || string.IsNullOrWhiteSpace(location.MethodId) ||
                string.IsNullOrWhiteSpace(location.NodeId) ||
                string.IsNullOrWhiteSpace(location.ActionId) ||
                string.IsNullOrWhiteSpace(location.Title) || location.Summary is null ||
                string.IsNullOrWhiteSpace(location.Category) ||
                location.SourceSpan is null ||
                string.IsNullOrWhiteSpace(location.SourceSpan.FilePath) ||
                location.SourceSpan.Start is null || location.SourceSpan.End is null ||
                location.SourceSpan.Start.Line < 0 ||
                location.SourceSpan.Start.Column < 0 ||
                location.SourceSpan.End.Line < location.SourceSpan.Start.Line ||
                location.SourceSpan.End.Column < 0 ||
                location.SourceSpan.End.Line == location.SourceSpan.Start.Line &&
                location.SourceSpan.End.Column < location.SourceSpan.Start.Column) ||
            locations.Select(static location => (location.MethodId, location.NodeId))
                .Distinct().Count() != locations.Count)
        {
            throw new InvalidDataException(
                "Authoring insight locations require unique semantic nodes and source spans.");
        }
    }

    private static void ValidateTrace(UnrealGameplayProfileTrace trace)
    {
        if (trace.SchemaVersion != UnrealGameplayProfileTrace.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(trace.TraceId) ||
            string.IsNullOrWhiteSpace(trace.WorldHostId) ||
            string.IsNullOrWhiteSpace(trace.InstanceId) ||
            trace.FrameCapacity is < 1 or > 4096 || trace.DroppedFrameCount < 0 ||
            trace.Frames is null || trace.Frames.Count > trace.FrameCapacity ||
            trace.Aggregate is null || trace.Aggregate.FrameCount != trace.Frames.Count ||
            trace.Aggregate.FrameCount <= 0 || trace.Aggregate.TotalNanoseconds < 0 ||
            trace.Aggregate.AllocatedBytes < 0 || trace.Aggregate.Nodes is null ||
            trace.Aggregate.Nodes.Count > 4096 || trace.Aggregate.Boundaries is null ||
            trace.Aggregate.WorldPhases is null || trace.Aggregate.BufferPressure is null ||
            !ValidBoundaries(trace.Aggregate.Boundaries) ||
            !ValidBoundaries(trace.Aggregate.WorldPhases) ||
            trace.Aggregate.Nodes.Any(static node =>
                node is null || string.IsNullOrWhiteSpace(node.MethodId) ||
                string.IsNullOrWhiteSpace(node.NodeId) || node.HitCount < 0 ||
                node.TotalNanoseconds < 0 || node.Boundaries is null ||
                !ValidBoundaries(node.Boundaries)) ||
            trace.Aggregate.Nodes
                .Select(static node => (node.MethodId, node.NodeId))
                .Distinct().Count() != trace.Aggregate.Nodes.Count ||
            !ValidPressure(trace.Aggregate.BufferPressure))
        {
            throw new InvalidDataException(
                $"Authoring insight requires one bounded " +
                $"{UnrealGameplayProfileTrace.CurrentSchemaVersion} trace.");
        }
    }

    private static bool ValidBoundaries(IReadOnlyList<UnrealGameplayProfileBoundary> values) =>
        values.Count <= 64 &&
        values.Select(static value => value.Name).Distinct(StringComparer.Ordinal).Count() ==
            values.Count &&
        values.All(static value => value is not null &&
            !string.IsNullOrWhiteSpace(value.Name) && value.Count >= 0 &&
            value.TotalNanoseconds >= 0);

    private static bool ValidPressure(UnrealGameplayProfileBufferPressure pressure) =>
        pressure.CommandOverflowCount >= 0 && pressure.EventOverflowCount >= 0 &&
        pressure.TraceOverflowCount >= 0 && pressure.OverlapOverflowCount >= 0;

    private static void ValidateApiBrowser(UnrealApiBrowserDocument? browser)
    {
        if (browser is not null &&
            (browser.SchemaVersion != UnrealApiBrowserDocument.CurrentSchemaVersion ||
             browser.Types is null || browser.Types.Any(static type => type?.Members is null)))
        {
            throw new InvalidDataException("The Unreal API browser is malformed or unsupported.");
        }
    }

    private static double Percent(long value, long total) =>
        total <= 0 ? 0 : value * 100.0 / total;

    private static long DivideRounded(long value, long divisor) =>
        divisor <= 0 ? 0 : (long)Math.Round(value / (double)divisor);

    private static long SaturatingSum(params long[] values) =>
        values.Aggregate(0L, static (total, value) => SaturatingAdd(total, value));

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
}

public static class UnrealAuthoringInsightJson
{
    public static string Serialize(
        UnrealAuthoringInsightSnapshot snapshot,
        bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != UnrealAuthoringInsightSnapshot.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported authoring insight schema '{snapshot.SchemaVersion}'.");
        }
        return JsonSerializer.Serialize(snapshot, UnifiedJson.CreateOptions(writeIndented));
    }

    public static UnrealAuthoringInsightSnapshot Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        UnrealAuthoringInsightSnapshot snapshot =
            JsonSerializer.Deserialize<UnrealAuthoringInsightSnapshot>(
                json,
                UnifiedJson.CreateOptions()) ?? throw new JsonException(
                "The authoring insight snapshot was empty.");
        if (snapshot.SchemaVersion != UnrealAuthoringInsightSnapshot.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported authoring insight schema '{snapshot.SchemaVersion}'.");
        }
        return snapshot;
    }
}
