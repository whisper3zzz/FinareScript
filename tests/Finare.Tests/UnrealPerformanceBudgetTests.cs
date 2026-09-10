using Finare.Unreal;

namespace Finare.Tests;

public sealed class UnrealPerformanceBudgetTests
{
    [Fact]
    public void CheckedInM10Point3SuitePreservesFrozenWorkloadsAndReviewsBudgets()
    {
        UnrealPerformanceSuiteManifest frozen = LoadSuite("workload-suite.v0.1.json");
        UnrealPerformanceSuiteManifest current = LoadSuite("workload-suite.v1.1.0.json");
        UnrealPerformanceBudgetManifest budgets = LoadBudgets();

        Assert.Equal("1.0.0", frozen.SuiteVersion);
        Assert.Equal("1.1.0", current.SuiteVersion);
        Assert.Equal(frozen.SuiteId, current.SuiteId);
        Assert.Equal(frozen.Build, current.Build);
        Assert.Equal(frozen.Workloads.Count, current.Workloads.Count);
        foreach (UnrealPerformanceWorkload workload in current.Workloads)
        {
            UnrealPerformanceWorkload previous = frozen.Workloads.Single(
                candidate => candidate.Id == workload.Id);
            if (workload.Id is "struct-container-transfer" or "delegate-storm")
            {
                Assert.Equal("1.1.0", workload.Version);
                Assert.NotEqual(previous.InputDigest, workload.InputDigest);
            }
            else
            {
                Assert.Equal(previous.Version, workload.Version);
                Assert.Equal(previous.InputDigest, workload.InputDigest);
                Assert.Equal(previous.AssetDigest, workload.AssetDigest);
                Assert.Equal(previous.WarmupIterations, workload.WarmupIterations);
                Assert.Equal(previous.MeasuredIterations, workload.MeasuredIterations);
            }
        }

        UnrealPerformanceWorkload delegateWorkload = current.Workloads.Single(
            static workload => workload.Id == "delegate-storm");
        Assert.Equal(65_536, delegateWorkload.MeasuredIterations);
        UnrealPerformanceBudgets.Validate(current, budgets);
        Assert.Equal(0.95, budgets.MaximumWeightedRatio, 10);
        Assert.Equal(8, budgets.WeightedMetrics.Count);
        Assert.Equal("hot-reload", Assert.Single(budgets.ExcludedWorkloads).WorkloadId);
        Assert.Contains(
            budgets.RegressionGates,
            static gate => gate.Id == "generated-container-allocation" &&
                gate.Maximum == 0.0);
        Assert.Contains(
            budgets.RegressionGates,
            static gate => gate.Id == "generated-event-allocation" &&
                gate.Maximum == 0.0);
        Assert.Contains(
            budgets.RegressionGates,
            static gate => gate.Id == "blockbreaker-frame-p99");
    }

    [Fact]
    public void WeightedVerdictPublishesWinsAndLossesWithoutMovingThresholds()
    {
        UnrealPerformanceSuiteManifest manifest = LoadSuite("workload-suite.v1.1.0.json");
        UnrealPerformanceBudgetManifest budgets = LoadBudgets();

        UnrealPerformanceBudgetReport winning = UnrealPerformanceBudgets.Evaluate(
            manifest,
            CreateComparison(manifest, 0.90, generatedAllocation: 0.0),
            budgets);
        Assert.Equal("qualified", winning.Status);
        Assert.Equal(0.90, winning.WeightedRatio, 10);
        Assert.True(winning.BroadClaimQualified);
        Assert.True(winning.RegressionGatesPassed);
        Assert.Equal(0.92, winning.IncludedWeight, 10);
        string winningJson = UnrealPerformanceBudgetJson.SerializeReport(winning);
        Assert.Contains("\"unifiedScriptToPuertsRatio\"", winningJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"finareToPuertsRatio\"", winningJson, StringComparison.Ordinal);

        UnrealPerformanceBudgetReport losing = UnrealPerformanceBudgets.Evaluate(
            manifest,
            CreateComparison(manifest, 1.00, generatedAllocation: 0.0),
            budgets);
        Assert.Equal("not-qualified", losing.Status);
        Assert.Equal(1.00, losing.WeightedRatio, 10);
        Assert.False(losing.BroadClaimQualified);
        Assert.True(losing.RegressionGatesPassed);

        UnrealPerformanceBudgetReport allocating = UnrealPerformanceBudgets.Evaluate(
            manifest,
            CreateComparison(manifest, 0.90, generatedAllocation: 1.0),
            budgets);
        Assert.Equal("not-qualified", allocating.Status);
        Assert.True(allocating.BroadClaimQualified);
        Assert.False(allocating.RegressionGatesPassed);
        Assert.Contains(
            allocating.RegressionResults,
            static result => result.Id == "generated-container-allocation" &&
                !result.Passed);
    }

    [Fact]
    public void BudgetValidationRejectsUnaccountedWorkloadsAndUnreviewedRatio()
    {
        UnrealPerformanceSuiteManifest manifest = LoadSuite("workload-suite.v1.1.0.json");
        UnrealPerformanceBudgetManifest budgets = LoadBudgets();

        Assert.Throws<InvalidDataException>(() => UnrealPerformanceBudgets.Validate(
            manifest,
            budgets with
            {
                ExcludedWorkloads = [],
            }));
        Assert.Throws<InvalidDataException>(() => UnrealPerformanceBudgets.Validate(
            manifest,
            budgets with
            {
                MaximumWeightedRatio = 1.01,
            }));
    }

    private static UnrealPerformanceComparisonReport CreateComparison(
        UnrealPerformanceSuiteManifest manifest,
        double ratio,
        double generatedAllocation)
    {
        var workloads = new List<UnrealPerformanceWorkloadComparison>();
        foreach (UnrealPerformanceWorkload workload in manifest.Workloads)
        {
            var metrics = new Dictionary<string, UnrealPerformanceMetricComparison>(
                StringComparer.Ordinal);
            foreach (string metricName in UnrealPerformanceMetricName.All)
            {
                double unified = 0.001;
                double puerts = unified / ratio;
                double? metricRatio = ratio;
                if (metricName == UnrealPerformanceMetricName.AllocatedBytes)
                {
                    unified = workload.Id is "struct-container-transfer" or "delegate-storm"
                        ? generatedAllocation
                        : 0.0;
                    puerts = 1.0;
                    metricRatio = null;
                }
                metrics.Add(
                    metricName,
                    new UnrealPerformanceMetricComparison(unified, puerts, metricRatio));
            }
            workloads.Add(new UnrealPerformanceWorkloadComparison(
                workload.Id,
                workload.Category,
                workload.Weight,
                metrics));
        }
        return new UnrealPerformanceComparisonReport(
            UnrealPerformanceComparisonReport.CurrentSchemaVersion,
            UnrealPerformanceWorkloadSuite.ComputeManifestDigest(manifest),
            manifest.SuiteId,
            manifest.SuiteVersion,
            MatchedEnvironment: true,
            MatchedWorkloads: true,
            AlternatingSchedule: true,
            UnrealPerformanceWorkloadSuite.CreateAlternatingSchedule(manifest),
            workloads);
    }

    private static UnrealPerformanceSuiteManifest LoadSuite(string name)
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "UnrealPerformance",
            name);
        return UnrealPerformanceSuiteJson.DeserializeManifest(File.ReadAllText(path));
    }

    private static UnrealPerformanceBudgetManifest LoadBudgets()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "UnrealPerformance",
            "performance-budgets.v1.1.0.json");
        return UnrealPerformanceBudgetJson.DeserializeManifest(File.ReadAllText(path));
    }
}
