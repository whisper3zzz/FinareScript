using System.Text.Json.Nodes;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealPerformanceWorkloadSuiteTests
{
    [Fact]
    public void CheckedInManifestCoversM10WorkloadsMetricsAndAlternatingOrder()
    {
        UnrealPerformanceSuiteManifest manifest = LoadManifest();

        UnrealPerformanceWorkloadSuite.ValidateManifest(manifest);
        Assert.Equal(9, manifest.Workloads.Count);
        Assert.Equal(
            UnrealPerformanceWorkloadCategory.All.Order(StringComparer.Ordinal),
            manifest.Workloads.Select(static workload => workload.Category)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            UnrealPerformanceMetricName.All.Order(StringComparer.Ordinal),
            manifest.Workloads.SelectMany(static workload => workload.RequiredMetrics)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));

        IReadOnlyList<UnrealPerformanceRunSlot> schedule =
            UnrealPerformanceWorkloadSuite.CreateAlternatingSchedule(manifest);
        Assert.Equal(54, schedule.Count);
        Assert.Equal(Enumerable.Range(1, schedule.Count),
            schedule.Select(static slot => slot.ExecutionOrdinal));
        foreach (int trial in Enumerable.Range(1, manifest.TrialCount))
        {
            foreach (UnrealPerformanceWorkload workload in manifest.Workloads)
            {
                string[] engines = schedule
                    .Where(slot => slot.Trial == trial && slot.WorkloadId == workload.Id)
                    .Select(static slot => slot.EngineId)
                    .ToArray();
                Assert.Equal(
                    trial % 2 == 1
                        ? [UnrealPerformanceEngineId.UnifiedScript, UnrealPerformanceEngineId.Puerts]
                        : [UnrealPerformanceEngineId.Puerts, UnrealPerformanceEngineId.UnifiedScript],
                    engines);
            }
        }
        string firstDigest = UnrealPerformanceWorkloadSuite.ComputeManifestDigest(manifest);
        string secondDigest = UnrealPerformanceWorkloadSuite.ComputeManifestDigest(
            UnrealPerformanceSuiteJson.DeserializeManifest(
                UnrealPerformanceSuiteJson.SerializeManifest(manifest)));
        Assert.Equal(firstDigest, secondDigest);
        Assert.StartsWith("sha256:", firstDigest, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparisonPublishesNeutralOrLosingMetricsWithoutMovingTheGate()
    {
        UnrealPerformanceSuiteManifest manifest = LoadManifest();
        UnrealPerformanceEngineResult unified = CreateResult(
            manifest,
            UnrealPerformanceEngineId.UnifiedScript,
            scale: 1.05);
        UnrealPerformanceEngineResult puerts = CreateResult(
            manifest,
            UnrealPerformanceEngineId.Puerts,
            scale: 1.0);

        UnrealPerformanceComparisonReport report =
            UnrealPerformanceWorkloadSuite.Compare(manifest, unified, puerts);

        Assert.True(report.MatchedEnvironment);
        Assert.True(report.MatchedWorkloads);
        Assert.True(report.AlternatingSchedule);
        Assert.Equal(9, report.Workloads.Count);
        UnrealPerformanceMetricComparison blockBreakerFrame = report.Workloads
            .Single(static workload => workload.Category ==
                UnrealPerformanceWorkloadCategory.BlockBreaker)
            .Metrics[UnrealPerformanceMetricName.FrameTimeMedianMilliseconds];
        Assert.Equal(1.05, blockBreakerFrame.UnifiedScriptToPuertsRatio!.Value, 10);
        Assert.True(blockBreakerFrame.UnifiedScriptMedian!.Value >
            blockBreakerFrame.PuertsMedian!.Value);

        JsonNode json = JsonNode.Parse(
            UnrealPerformanceSuiteJson.SerializeReport(report))!;
        Assert.Equal(
            UnrealPerformanceComparisonReport.CurrentSchemaVersion,
            json["schemaVersion"]!.GetValue<string>());
        Assert.Equal(1.05, json["workloads"]![8]!["metrics"]!
            [UnrealPerformanceMetricName.FrameTimeMedianMilliseconds]!
            ["unifiedScriptToPuertsRatio"]!.GetValue<double>(), 10);
    }

    [Fact]
    public void ComparisonRejectsAValidButDifferentMachineFingerprint()
    {
        UnrealPerformanceSuiteManifest manifest = LoadManifest();
        UnrealPerformanceEngineResult unified = CreateResult(
            manifest,
            UnrealPerformanceEngineId.UnifiedScript,
            scale: 1.0);
        UnrealPerformanceEngineResult puerts = CreateResult(
            manifest,
            UnrealPerformanceEngineId.Puerts,
            scale: 1.0);
        UnrealPerformanceEnvironment otherEnvironment = puerts.Environment with
        {
            CpuModel = "Fixture CPU B",
            MachineFingerprint = UnrealPerformanceWorkloadSuite.ComputeMachineFingerprint(
                puerts.Environment.OperatingSystem,
                "Fixture CPU B",
                puerts.Environment.LogicalProcessorCount,
                puerts.Environment.PhysicalMemoryBytes,
                puerts.Environment.GpuModel),
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            UnrealPerformanceWorkloadSuite.Compare(
                manifest,
                unified,
                puerts with { Environment = otherEnvironment }));

        Assert.Contains("environment differs", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparisonRejectsMissingRequiredMetricAndNonAlternatingExecution()
    {
        UnrealPerformanceSuiteManifest manifest = LoadManifest();
        UnrealPerformanceEngineResult unified = CreateResult(
            manifest,
            UnrealPerformanceEngineId.UnifiedScript,
            scale: 1.0);
        UnrealPerformanceEngineResult puerts = CreateResult(
            manifest,
            UnrealPerformanceEngineId.Puerts,
            scale: 1.0);
        UnrealPerformanceTrial first = unified.Trials[0];
        var missingValues = first.Metrics.Values.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        missingValues[UnrealPerformanceMetricName.BridgeTimeMilliseconds] = null;
        var missingTrials = unified.Trials.ToArray();
        missingTrials[0] = first with
        {
            Metrics = new UnrealPerformanceMetrics(missingValues),
        };
        Assert.Throws<InvalidDataException>(() =>
            UnrealPerformanceWorkloadSuite.Compare(
                manifest,
                unified with { Trials = missingTrials },
                puerts));

        var reorderedTrials = unified.Trials.ToArray();
        reorderedTrials[0] = first with
        {
            ExecutionOrdinal = first.ExecutionOrdinal + 1,
        };
        InvalidDataException orderException = Assert.Throws<InvalidDataException>(() =>
            UnrealPerformanceWorkloadSuite.Compare(
                manifest,
                unified with { Trials = reorderedTrials },
                puerts));
        Assert.Contains("alternating engine schedule", orderException.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResultJsonRoundTripsExplicitNullMetrics()
    {
        UnrealPerformanceSuiteManifest manifest = LoadManifest();
        UnrealPerformanceEngineResult result = CreateResult(
            manifest,
            UnrealPerformanceEngineId.UnifiedScript,
            scale: 1.0,
            includeOnlyRequiredMetrics: true);

        UnrealPerformanceEngineResult roundTripped =
            UnrealPerformanceSuiteJson.DeserializeResult(
                UnrealPerformanceSuiteJson.SerializeResult(result));
        UnrealPerformanceComparisonReport report = UnrealPerformanceWorkloadSuite.Compare(
            manifest,
            roundTripped,
            CreateResult(
                manifest,
                UnrealPerformanceEngineId.Puerts,
                scale: 1.0,
                includeOnlyRequiredMetrics: true));

        UnrealPerformanceMetricComparison reload = report.Workloads
            .Single(static workload => workload.Category ==
                UnrealPerformanceWorkloadCategory.ScalarPropertyCalls)
            .Metrics[UnrealPerformanceMetricName.ReloadMilliseconds];
        Assert.Null(reload.UnifiedScriptMedian);
        Assert.Null(reload.PuertsMedian);
        Assert.Null(reload.UnifiedScriptToPuertsRatio);
    }

    [Fact]
    public void CliWritesMatchedReportAndRejectsAnUnmatchedResult()
    {
        UnrealPerformanceSuiteManifest manifest = LoadManifest();
        UnrealPerformanceEngineResult unified = CreateResult(
            manifest,
            UnrealPerformanceEngineId.UnifiedScript,
            scale: 1.0);
        UnrealPerformanceEngineResult puerts = CreateResult(
            manifest,
            UnrealPerformanceEngineId.Puerts,
            scale: 1.0);
        string directory = Directory.CreateTempSubdirectory(
            "unifiedscript-performance-").FullName;
        try
        {
            string manifestPath = System.IO.Path.Combine(directory, "manifest.json");
            string unifiedPath = System.IO.Path.Combine(directory, "unified.json");
            string puertsPath = System.IO.Path.Combine(directory, "puerts.json");
            string reportPath = System.IO.Path.Combine(directory, "report.json");
            File.WriteAllText(
                manifestPath,
                UnrealPerformanceSuiteJson.SerializeManifest(manifest));
            File.WriteAllText(
                unifiedPath,
                UnrealPerformanceSuiteJson.SerializeResult(unified));
            File.WriteAllText(
                puertsPath,
                UnrealPerformanceSuiteJson.SerializeResult(puerts));

            int exitCode = UnifiedScript.Cli.Program.Main(
            [
                "ue-workload-compare",
                "--manifest", manifestPath,
                "--unified", unifiedPath,
                "--puerts", puertsPath,
                "--out", reportPath,
            ]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(reportPath));
            Assert.Contains(
                UnrealPerformanceComparisonReport.CurrentSchemaVersion,
                File.ReadAllText(reportPath),
                StringComparison.Ordinal);

            UnrealPerformanceTrial first = puerts.Trials[0];
            File.WriteAllText(
                puertsPath,
                UnrealPerformanceSuiteJson.SerializeResult(puerts with
                {
                    Trials = [first with { InputDigest = new string('0', 71) },
                        .. puerts.Trials.Skip(1)],
                }));
            Assert.Equal(65, UnifiedScript.Cli.Program.Main(
            [
                "ue-workload-compare",
                "--manifest", manifestPath,
                "--unified", unifiedPath,
                "--puerts", puertsPath,
                "--out", reportPath,
            ]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static UnrealPerformanceSuiteManifest LoadManifest()
    {
        string path = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "UnrealPerformance",
            "workload-suite.v0.1.json");
        return UnrealPerformanceSuiteJson.DeserializeManifest(File.ReadAllText(path));
    }

    private static UnrealPerformanceEngineResult CreateResult(
        UnrealPerformanceSuiteManifest manifest,
        string engineId,
        double scale,
        bool includeOnlyRequiredMetrics = false)
    {
        const string operatingSystem = "Windows fixture 10.0";
        const string cpuModel = "Fixture CPU A";
        const int logicalProcessors = 16;
        const long physicalMemoryBytes = 32L * 1024 * 1024 * 1024;
        const string gpuModel = "Fixture GPU";
        var environment = new UnrealPerformanceEnvironment(
            UnrealPerformanceWorkloadSuite.ComputeMachineFingerprint(
                operatingSystem,
                cpuModel,
                logicalProcessors,
                physicalMemoryBytes,
                gpuModel),
            operatingSystem,
            cpuModel,
            logicalProcessors,
            physicalMemoryBytes,
            gpuModel,
            manifest.Build.EngineVersion,
            "fixture-engine-build",
            manifest.Build.Platform,
            manifest.Build.Configuration,
            manifest.Build.Rhi,
            manifest.Build.BuildFingerprint);
        Dictionary<string, UnrealPerformanceWorkload> workloads = manifest.Workloads
            .ToDictionary(static workload => workload.Id, StringComparer.Ordinal);
        UnrealPerformanceTrial[] trials = UnrealPerformanceWorkloadSuite
            .CreateAlternatingSchedule(manifest)
            .Where(slot => slot.EngineId == engineId)
            .Select(slot =>
            {
                UnrealPerformanceWorkload workload = workloads[slot.WorkloadId];
                var values = new Dictionary<string, double?>(StringComparer.Ordinal);
                foreach (string metric in UnrealPerformanceMetricName.All)
                {
                    bool required = workload.RequiredMetrics.Contains(
                        metric,
                        StringComparer.Ordinal);
                    double baseValue = 100.0 + slot.Trial;
                    double? value = includeOnlyRequiredMetrics && !required
                        ? null
                        : baseValue * scale;
                    values.Add(metric, value);
                }
                if (values[UnrealPerformanceMetricName.FrameTimeMedianMilliseconds]
                    is { } median)
                {
                    values[UnrealPerformanceMetricName.FrameTimeP95Milliseconds] =
                        median + 10.0 * scale;
                    values[UnrealPerformanceMetricName.FrameTimeP99Milliseconds] =
                        median + 20.0 * scale;
                }
                return new UnrealPerformanceTrial(
                    workload.Id,
                    slot.Trial,
                    slot.ExecutionOrdinal,
                    "passed",
                    workload.InputDigest,
                    workload.AssetDigest,
                    new UnrealPerformanceMetrics(values));
            })
            .ToArray();
        return new UnrealPerformanceEngineResult(
            UnrealPerformanceEngineResult.CurrentSchemaVersion,
            UnrealPerformanceWorkloadSuite.ComputeManifestDigest(manifest),
            manifest.SuiteId,
            manifest.SuiteVersion,
            engineId,
            $"{engineId}-fixture",
            $"{engineId}-commit",
            environment,
            trials);
    }
}
