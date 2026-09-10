using System.Text.Json;
using System.Text.Json.Serialization;
using Finare.Core;

namespace Finare.Unreal;

public static class UnrealPerformanceBudgetObservation
{
    public const string FinareMedian = "unifiedScriptMedian";
    public const string FinareToPuertsRatio = "unifiedScriptToPuertsRatio";

    public static IReadOnlyList<string> All { get; } =
    [
        FinareMedian,
        FinareToPuertsRatio,
    ];
}

public sealed record UnrealPerformanceWeightedMetric(
    string WorkloadId,
    string MetricName);

public sealed record UnrealPerformanceExcludedWorkload(
    string WorkloadId,
    string Reason);

public sealed record UnrealPerformanceRegressionGate(
    string Id,
    string WorkloadId,
    string MetricName,
    string Observation,
    double Maximum,
    string Rationale);

public sealed record UnrealPerformanceBudgetManifest(
    string SchemaVersion,
    string BudgetId,
    string SuiteId,
    string SuiteVersion,
    string ReferenceSuiteVersion,
    string Aggregation,
    double MaximumWeightedRatio,
    IReadOnlyList<UnrealPerformanceWeightedMetric> WeightedMetrics,
    IReadOnlyList<UnrealPerformanceExcludedWorkload> ExcludedWorkloads,
    IReadOnlyList<UnrealPerformanceRegressionGate> RegressionGates,
    IReadOnlyList<string> RevisionNotes)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.performance-budgets/v0.1";

    public const string WeightedArithmeticNormalized =
        "weighted-arithmetic-normalized";
}

public sealed record UnrealPerformanceWeightedResult(
    string WorkloadId,
    string MetricName,
    double Weight,
    [property: JsonPropertyName("unifiedScriptToPuertsRatio")]
    double FinareToPuertsRatio,
    double WeightedContribution);

public sealed record UnrealPerformanceRegressionResult(
    string Id,
    string WorkloadId,
    string MetricName,
    string Observation,
    double Actual,
    double Maximum,
    bool Passed,
    string Rationale);

public sealed record UnrealPerformanceBudgetReport(
    string SchemaVersion,
    string BudgetId,
    string ManifestDigest,
    string SuiteId,
    string SuiteVersion,
    string Status,
    double IncludedWeight,
    double WeightedRatio,
    double MaximumWeightedRatio,
    bool BroadClaimQualified,
    bool RegressionGatesPassed,
    IReadOnlyList<UnrealPerformanceWeightedResult> WeightedResults,
    IReadOnlyList<UnrealPerformanceExcludedWorkload> ExcludedWorkloads,
    IReadOnlyList<UnrealPerformanceRegressionResult> RegressionResults)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.performance-budget-report/v0.1";
}

public static class UnrealPerformanceBudgets
{
    public static UnrealPerformanceBudgetReport Evaluate(
        UnrealPerformanceSuiteManifest manifest,
        UnrealPerformanceComparisonReport comparison,
        UnrealPerformanceBudgetManifest budgets)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        Validate(manifest, budgets);
        string manifestDigest = UnrealPerformanceWorkloadSuite.ComputeManifestDigest(manifest);
        if (comparison.SchemaVersion != UnrealPerformanceComparisonReport.CurrentSchemaVersion ||
            comparison.ManifestDigest != manifestDigest ||
            comparison.SuiteId != manifest.SuiteId ||
            comparison.SuiteVersion != manifest.SuiteVersion ||
            !comparison.MatchedEnvironment || !comparison.MatchedWorkloads ||
            !comparison.AlternatingSchedule)
        {
            throw new InvalidDataException(
                "Performance budgets require one complete matched comparison for the exact suite.");
        }

        Dictionary<string, UnrealPerformanceWorkload> workloadById = manifest.Workloads
            .ToDictionary(static workload => workload.Id, StringComparer.Ordinal);
        Dictionary<string, UnrealPerformanceWorkloadComparison> comparisonById =
            comparison.Workloads.ToDictionary(
                static workload => workload.WorkloadId,
                StringComparer.Ordinal);
        var weightedResults = new List<UnrealPerformanceWeightedResult>(
            budgets.WeightedMetrics.Count);
        double includedWeight = 0.0;
        double weightedTotal = 0.0;
        foreach (UnrealPerformanceWeightedMetric weightedMetric in budgets.WeightedMetrics)
        {
            UnrealPerformanceWorkload workload = workloadById[weightedMetric.WorkloadId];
            UnrealPerformanceMetricComparison metric = comparisonById[weightedMetric.WorkloadId]
                .Metrics[weightedMetric.MetricName];
            double ratio = metric.FinareToPuertsRatio ??
                throw new InvalidDataException(
                    $"Weighted metric '{weightedMetric.WorkloadId}/{weightedMetric.MetricName}' " +
                    "has no comparable ratio.");
            double contribution = workload.Weight * ratio;
            includedWeight += workload.Weight;
            weightedTotal += contribution;
            weightedResults.Add(new UnrealPerformanceWeightedResult(
                workload.Id,
                weightedMetric.MetricName,
                workload.Weight,
                ratio,
                contribution));
        }
        if (includedWeight <= 0.0)
        {
            throw new InvalidDataException("Performance budget included weight must be positive.");
        }
        double weightedRatio = weightedTotal / includedWeight;

        var regressionResults = new List<UnrealPerformanceRegressionResult>(
            budgets.RegressionGates.Count);
        foreach (UnrealPerformanceRegressionGate gate in budgets.RegressionGates)
        {
            UnrealPerformanceMetricComparison metric =
                comparisonById[gate.WorkloadId].Metrics[gate.MetricName];
            double actual = gate.Observation switch
            {
                UnrealPerformanceBudgetObservation.FinareMedian =>
                    metric.FinareMedian,
                UnrealPerformanceBudgetObservation.FinareToPuertsRatio =>
                    metric.FinareToPuertsRatio,
                _ => null,
            } ?? throw new InvalidDataException(
                $"Regression gate '{gate.Id}' has no measured '{gate.Observation}' value.");
            regressionResults.Add(new UnrealPerformanceRegressionResult(
                gate.Id,
                gate.WorkloadId,
                gate.MetricName,
                gate.Observation,
                actual,
                gate.Maximum,
                actual <= gate.Maximum,
                gate.Rationale));
        }

        bool broadClaimQualified = weightedRatio <= budgets.MaximumWeightedRatio;
        bool regressionGatesPassed = regressionResults.All(static result => result.Passed);
        return new UnrealPerformanceBudgetReport(
            UnrealPerformanceBudgetReport.CurrentSchemaVersion,
            budgets.BudgetId,
            manifestDigest,
            manifest.SuiteId,
            manifest.SuiteVersion,
            broadClaimQualified && regressionGatesPassed
                ? "qualified"
                : "not-qualified",
            includedWeight,
            weightedRatio,
            budgets.MaximumWeightedRatio,
            broadClaimQualified,
            regressionGatesPassed,
            weightedResults,
            budgets.ExcludedWorkloads,
            regressionResults);
    }

    public static void Validate(
        UnrealPerformanceSuiteManifest manifest,
        UnrealPerformanceBudgetManifest budgets)
    {
        UnrealPerformanceWorkloadSuite.ValidateManifest(manifest);
        ArgumentNullException.ThrowIfNull(budgets);
        if (budgets.SchemaVersion != UnrealPerformanceBudgetManifest.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(budgets.BudgetId) ||
            budgets.SuiteId != manifest.SuiteId ||
            budgets.SuiteVersion != manifest.SuiteVersion ||
            string.IsNullOrWhiteSpace(budgets.ReferenceSuiteVersion) ||
            budgets.Aggregation !=
                UnrealPerformanceBudgetManifest.WeightedArithmeticNormalized ||
            !double.IsFinite(budgets.MaximumWeightedRatio) ||
            budgets.MaximumWeightedRatio <= 0.0 || budgets.MaximumWeightedRatio > 1.0)
        {
            throw new InvalidDataException(
                "Performance budgets must identify the exact suite and a finite winning ratio.");
        }
        if (budgets.WeightedMetrics is null || budgets.WeightedMetrics.Count == 0 ||
            budgets.ExcludedWorkloads is null || budgets.RegressionGates is null ||
            budgets.RevisionNotes is null || budgets.RevisionNotes.Count == 0 ||
            budgets.RevisionNotes.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException(
                "Performance budgets require weighted metrics, explicit exclusions, gates, and revision notes.");
        }

        Dictionary<string, UnrealPerformanceWorkload> workloads = manifest.Workloads
            .ToDictionary(static workload => workload.Id, StringComparer.Ordinal);
        var coveredWorkloads = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnrealPerformanceWeightedMetric metric in budgets.WeightedMetrics)
        {
            if (metric is null || !workloads.TryGetValue(metric.WorkloadId, out var workload) ||
                !UnrealPerformanceMetricName.All.Contains(metric.MetricName, StringComparer.Ordinal) ||
                !workload.RequiredMetrics.Contains(metric.MetricName, StringComparer.Ordinal) ||
                metric.MetricName == UnrealPerformanceMetricName.AllocatedBytes ||
                !coveredWorkloads.Add(metric.WorkloadId))
            {
                throw new InvalidDataException(
                    "Every weighted workload needs one unique, required, ratio-compatible metric.");
            }
        }
        foreach (UnrealPerformanceExcludedWorkload excluded in budgets.ExcludedWorkloads)
        {
            if (excluded is null || !workloads.ContainsKey(excluded.WorkloadId) ||
                string.IsNullOrWhiteSpace(excluded.Reason) ||
                !coveredWorkloads.Add(excluded.WorkloadId))
            {
                throw new InvalidDataException(
                    "Every excluded workload needs a unique ID and an explicit reason.");
            }
        }
        if (!coveredWorkloads.SetEquals(workloads.Keys))
        {
            throw new InvalidDataException(
                "Weighted metrics and explicit exclusions must cover the complete suite.");
        }

        var gateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnrealPerformanceRegressionGate gate in budgets.RegressionGates)
        {
            if (gate is null || string.IsNullOrWhiteSpace(gate.Id) ||
                !gateIds.Add(gate.Id) || !workloads.ContainsKey(gate.WorkloadId) ||
                !UnrealPerformanceMetricName.All.Contains(gate.MetricName, StringComparer.Ordinal) ||
                !UnrealPerformanceBudgetObservation.All.Contains(
                    gate.Observation,
                    StringComparer.Ordinal) ||
                !double.IsFinite(gate.Maximum) || gate.Maximum < 0.0 ||
                string.IsNullOrWhiteSpace(gate.Rationale))
            {
                throw new InvalidDataException(
                    "Performance regression gates require unique IDs, measured observations, and reviewed limits.");
            }
        }
    }
}

public static class UnrealPerformanceBudgetJson
{
    public static UnrealPerformanceBudgetManifest DeserializeManifest(string json) =>
        Deserialize<UnrealPerformanceBudgetManifest>(json, "budget manifest");

    public static string SerializeManifest(
        UnrealPerformanceBudgetManifest manifest,
        bool writeIndented = true) =>
        JsonSerializer.Serialize(manifest, UnifiedJson.CreateOptions(writeIndented));

    public static string SerializeReport(
        UnrealPerformanceBudgetReport report,
        bool writeIndented = true) =>
        JsonSerializer.Serialize(report, UnifiedJson.CreateOptions(writeIndented));

    public static UnrealPerformanceBudgetReport DeserializeReport(string json) =>
        Deserialize<UnrealPerformanceBudgetReport>(json, "budget report");

    private static T Deserialize<T>(string json, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<T>(json, UnifiedJson.CreateOptions()) ??
            throw new JsonException($"Performance {description} JSON was empty.");
    }
}
