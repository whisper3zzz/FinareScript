using System.Text.Json;
using UnifiedScript.Core;
using UnifiedScript.Runtime;

namespace UnifiedScript.Unreal;

public static class UnrealGameplayDiagnosisKind
{
    public const string GameplayDefect = "gameplayDefect";
    public const string PerformanceRegression = "performanceRegression";
}

public sealed record UnrealGameplayDebugObservation(
    string SchemaVersion,
    string MethodId,
    string NodeId,
    SourceSpan SourceSpan,
    string LocalName,
    string ExpectedDisplay,
    string ActualDisplay,
    string ActualType)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.gameplay-debug-observation/v0.1";

    public static UnrealGameplayDebugObservation Capture(
        CoreClrDebugPause pause,
        CoreClrDebugStackFrame frame,
        IReadOnlyList<CoreClrDebugValue> locals,
        string localName,
        string expectedDisplay)
    {
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(locals);
        ArgumentException.ThrowIfNullOrWhiteSpace(localName);
        ArgumentNullException.ThrowIfNull(expectedDisplay);
        if (frame.MethodId != pause.MethodId || frame.NodeId != pause.NodeId ||
            frame.SourceSpan is null)
        {
            throw new ArgumentException(
                "The debug frame must resolve the paused semantic method/node and source span.",
                nameof(frame));
        }
        CoreClrDebugValue actual = locals.Single(local => local.Name == localName);
        return new UnrealGameplayDebugObservation(
            CurrentSchemaVersion,
            pause.MethodId.Value,
            pause.NodeId.Value,
            frame.SourceSpan,
            localName,
            expectedDisplay,
            actual.Display,
            actual.TypeMetadataName);
    }
}

public sealed record UnrealGameplayNodeLocation(
    string MethodId,
    string NodeId,
    SourceSpan SourceSpan);

public sealed record UnrealGameplayDiagnosisOptions(
    double MinimumRegressionRatio = 1.25,
    long MinimumAverageIncreaseNanoseconds = 100_000,
    int MaximumPerformanceFindings = 64);

public sealed record UnrealGameplayDiagnosisEvidence(
    string SchemaVersion,
    UnrealGameplayDebugObservation DebugObservation,
    IReadOnlyList<UnrealGameplayNodeLocation> NodeLocations,
    UnrealGameplayDiagnosisOptions? Options)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.gameplay-diagnosis-evidence/v0.1";
}

public sealed record UnrealGameplayDiagnosisFinding(
    string Kind,
    string MethodId,
    string NodeId,
    SourceSpan SourceSpan,
    string RuntimeBoundary,
    string Summary,
    string? LocalName,
    string? ExpectedDisplay,
    string? ActualDisplay,
    long? BaselineAverageNanoseconds,
    long? CurrentAverageNanoseconds,
    double? Ratio);

public sealed record UnrealGameplayDiagnosisReport(
    string SchemaVersion,
    string BaselineTraceId,
    string CurrentTraceId,
    bool LocatedGameplayDefect,
    bool LocatedPerformanceRegression,
    bool AcceptanceSatisfied,
    IReadOnlyList<UnrealGameplayDiagnosisFinding> Findings)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.gameplay-diagnosis/v0.1";
}

public static class UnrealGameplayDiagnosis
{
    public const string ManagedCodeBoundary = "managedCode";

    public static UnrealGameplayDiagnosisReport Analyze(
        UnrealGameplayProfileTrace baseline,
        UnrealGameplayProfileTrace current,
        UnrealGameplayDebugObservation debugObservation,
        IReadOnlyList<UnrealGameplayNodeLocation> nodeLocations,
        UnrealGameplayDiagnosisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(debugObservation);
        ArgumentNullException.ThrowIfNull(nodeLocations);
        options ??= new UnrealGameplayDiagnosisOptions();
        ValidateTrace(baseline, nameof(baseline));
        ValidateTrace(current, nameof(current));
        ValidateOptions(options);
        if (debugObservation.SchemaVersion !=
                UnrealGameplayDebugObservation.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(debugObservation.MethodId) ||
            string.IsNullOrWhiteSpace(debugObservation.NodeId) ||
            string.IsNullOrWhiteSpace(debugObservation.LocalName) ||
            !IsValidSourceSpan(debugObservation.SourceSpan))
        {
            throw new InvalidDataException(
                "Gameplay diagnosis requires one versioned semantic debug observation.");
        }

        Dictionary<(string MethodId, string NodeId), SourceSpan> locations =
            nodeLocations
                .Select(static location =>
                {
                    if (location is null ||
                        string.IsNullOrWhiteSpace(location.MethodId) ||
                        string.IsNullOrWhiteSpace(location.NodeId) ||
                        !IsValidSourceSpan(location.SourceSpan))
                    {
                        throw new InvalidDataException(
                            "Gameplay node locations require semantic IDs and source spans.");
                    }
                    return location;
                })
                .GroupBy(static location => (location.MethodId, location.NodeId))
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Count() == 1
                        ? group.Single().SourceSpan
                        : throw new InvalidDataException(
                            "Gameplay node locations contain a duplicate semantic node."));
        var currentNodes = current.Aggregate.Nodes.ToDictionary(
            static node => (node.MethodId, node.NodeId));
        var baselineNodes = baseline.Aggregate.Nodes.ToDictionary(
            static node => (node.MethodId, node.NodeId));
        var findings = new List<UnrealGameplayDiagnosisFinding>();
        var debugKey = (debugObservation.MethodId, debugObservation.NodeId);
        if (!currentNodes.TryGetValue(debugKey, out UnrealGameplayProfileNode? debugNode))
        {
            throw new InvalidDataException(
                "The debug observation node is absent from the current gameplay profile.");
        }
        if (!string.Equals(
                debugObservation.ExpectedDisplay,
                debugObservation.ActualDisplay,
                StringComparison.Ordinal))
        {
            string boundary = FindDominantBoundary(debugNode);
            findings.Add(new UnrealGameplayDiagnosisFinding(
                UnrealGameplayDiagnosisKind.GameplayDefect,
                debugObservation.MethodId,
                debugObservation.NodeId,
                debugObservation.SourceSpan,
                boundary,
                $"Local '{debugObservation.LocalName}' is " +
                $"'{debugObservation.ActualDisplay}', expected " +
                $"'{debugObservation.ExpectedDisplay}'.",
                debugObservation.LocalName,
                debugObservation.ExpectedDisplay,
                debugObservation.ActualDisplay,
                null,
                null,
                null));
        }

        var performance = new List<UnrealGameplayDiagnosisFinding>();
        foreach (KeyValuePair<(string MethodId, string NodeId), UnrealGameplayProfileNode>
            pair in currentNodes)
        {
            if (!locations.TryGetValue(pair.Key, out SourceSpan? sourceSpan))
            {
                continue;
            }
            baselineNodes.TryGetValue(pair.Key, out UnrealGameplayProfileNode? baselineNode);
            Dictionary<string, long> baselineCosts = GetBoundaryCosts(baselineNode);
            foreach (KeyValuePair<string, long> boundary in GetBoundaryCosts(pair.Value))
            {
                baselineCosts.TryGetValue(boundary.Key, out long baselineTotal);
                long baselineAverage = DivideRounded(
                    baselineTotal,
                    baseline.Aggregate.FrameCount);
                long currentAverage = DivideRounded(
                    boundary.Value,
                    current.Aggregate.FrameCount);
                long increase = Math.Max(0, currentAverage - baselineAverage);
                double? ratio = baselineAverage > 0
                    ? currentAverage / (double)baselineAverage
                    : null;
                if (increase < options.MinimumAverageIncreaseNanoseconds ||
                    ratio is not null && ratio < options.MinimumRegressionRatio)
                {
                    continue;
                }
                performance.Add(new UnrealGameplayDiagnosisFinding(
                    UnrealGameplayDiagnosisKind.PerformanceRegression,
                    pair.Key.MethodId,
                    pair.Key.NodeId,
                    sourceSpan,
                    boundary.Key,
                    ratio is null
                        ? $"Boundary '{boundary.Key}' newly costs " +
                          $"{currentAverage} ns/frame."
                        : $"Boundary '{boundary.Key}' increased from " +
                          $"{baselineAverage} to {currentAverage} ns/frame " +
                          $"({ratio.Value:F2}x).",
                    null,
                    null,
                    null,
                    baselineAverage,
                    currentAverage,
                    ratio));
            }
        }
        findings.AddRange(performance
            .OrderByDescending(static finding =>
                finding.CurrentAverageNanoseconds - finding.BaselineAverageNanoseconds)
            .ThenByDescending(static finding => finding.Ratio)
            .ThenBy(static finding => finding.MethodId, StringComparer.Ordinal)
            .ThenBy(static finding => finding.NodeId, StringComparer.Ordinal)
            .ThenBy(static finding => finding.RuntimeBoundary, StringComparer.Ordinal)
            .Take(options.MaximumPerformanceFindings));
        bool locatedDefect = findings.Any(static finding =>
            finding.Kind == UnrealGameplayDiagnosisKind.GameplayDefect);
        bool locatedRegression = findings.Any(static finding =>
            finding.Kind == UnrealGameplayDiagnosisKind.PerformanceRegression);
        return new UnrealGameplayDiagnosisReport(
            UnrealGameplayDiagnosisReport.CurrentSchemaVersion,
            baseline.TraceId,
            current.TraceId,
            locatedDefect,
            locatedRegression,
            locatedDefect && locatedRegression,
            findings);
    }

    private static Dictionary<string, long> GetBoundaryCosts(
        UnrealGameplayProfileNode? node)
    {
        if (node is null)
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }
        var result = node.Boundaries.ToDictionary(
            static boundary => boundary.Name,
            static boundary => boundary.TotalNanoseconds,
            StringComparer.Ordinal);
        long attributed = node.Boundaries.Aggregate(
            0L,
            static (total, boundary) => SaturatingAdd(
                total,
                boundary.TotalNanoseconds));
        result[ManagedCodeBoundary] = Math.Max(0, node.TotalNanoseconds - attributed);
        return result;
    }

    private static string FindDominantBoundary(UnrealGameplayProfileNode node) =>
        node.Boundaries
            .Where(static boundary => boundary.Count > 0 || boundary.TotalNanoseconds > 0)
            .OrderByDescending(static boundary => boundary.TotalNanoseconds)
            .ThenByDescending(static boundary => boundary.Count)
            .ThenBy(static boundary => boundary.Name, StringComparer.Ordinal)
            .Select(static boundary => boundary.Name)
            .FirstOrDefault() ?? ManagedCodeBoundary;

    private static void ValidateTrace(UnrealGameplayProfileTrace trace, string parameterName)
    {
        if (trace.SchemaVersion != UnrealGameplayProfileTrace.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(trace.TraceId) ||
            trace.FrameCapacity is < 1 or > 4096 ||
            trace.Frames is null || trace.Frames.Count > trace.FrameCapacity ||
            trace.Aggregate is null || trace.Aggregate.FrameCount != trace.Frames.Count ||
            trace.Aggregate.FrameCount <= 0 || trace.Aggregate.Nodes.Count > 4096 ||
            trace.Aggregate.Nodes.Any(static node =>
                string.IsNullOrWhiteSpace(node.MethodId) ||
                string.IsNullOrWhiteSpace(node.NodeId) ||
                node.HitCount < 0 || node.TotalNanoseconds < 0 ||
                node.Boundaries is null || node.Boundaries.Count > 32 ||
                node.Boundaries.Select(static boundary => boundary.Name)
                    .Distinct(StringComparer.Ordinal).Count() != node.Boundaries.Count ||
                node.Boundaries.Any(static boundary =>
                    string.IsNullOrWhiteSpace(boundary.Name) ||
                    boundary.Count < 0 || boundary.TotalNanoseconds < 0)))
        {
            throw new InvalidDataException(
                $"Parameter '{parameterName}' is not a bounded " +
                $"{UnrealGameplayProfileTrace.CurrentSchemaVersion} trace.");
        }
    }

    private static void ValidateOptions(UnrealGameplayDiagnosisOptions options)
    {
        if (!double.IsFinite(options.MinimumRegressionRatio) ||
            options.MinimumRegressionRatio <= 1.0 ||
            options.MinimumAverageIncreaseNanoseconds < 0 ||
            options.MaximumPerformanceFindings is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Diagnosis options require ratio > 1, non-negative cost, and 1-4096 findings.");
        }
    }

    private static long DivideRounded(long value, long divisor) =>
        divisor <= 0 ? 0 : (long)Math.Round(value / (double)divisor);

    private static bool IsValidSourceSpan(SourceSpan? span) =>
        span is not null && !string.IsNullOrWhiteSpace(span.FilePath) &&
        span.Start is not null && span.End is not null &&
        span.Start.Line >= 0 && span.Start.Column >= 0 &&
        span.End.Line >= span.Start.Line && span.End.Column >= 0;

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
}

public static class UnrealGameplayDiagnosisJson
{
    public static string Serialize(
        UnrealGameplayDiagnosisReport report,
        bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(
            report,
            UnifiedJson.CreateOptions(writeIndented));
    }

    public static UnrealGameplayProfileTrace DeserializeTrace(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<UnrealGameplayProfileTrace>(
            json,
            UnifiedJson.CreateOptions()) ?? throw new JsonException(
                "Gameplay profile trace JSON was empty.");
    }

    public static UnrealGameplayDiagnosisEvidence DeserializeEvidence(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        UnrealGameplayDiagnosisEvidence evidence =
            JsonSerializer.Deserialize<UnrealGameplayDiagnosisEvidence>(
                json,
                UnifiedJson.CreateOptions()) ?? throw new JsonException(
                    "Gameplay diagnosis evidence JSON was empty.");
        if (evidence.SchemaVersion !=
            UnrealGameplayDiagnosisEvidence.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported gameplay diagnosis evidence schema " +
                $"'{evidence.SchemaVersion}'.");
        }
        return evidence;
    }
}
