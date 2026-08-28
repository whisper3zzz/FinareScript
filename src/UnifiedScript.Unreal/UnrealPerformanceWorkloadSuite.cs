using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.Unreal;

public static class UnrealPerformanceEngineId
{
    public const string UnifiedScript = "unifiedScript";
    public const string Puerts = "puerts";

    public static IReadOnlyList<string> All { get; } = [UnifiedScript, Puerts];
}

public static class UnrealPerformanceWorkloadCategory
{
    public const string ScalarPropertyCalls = "scalarPropertyCalls";
    public const string StructContainerTransfer = "structContainerTransfer";
    public const string DelegateStorm = "delegateStorm";
    public const string PerFrameActorLogic = "perFrameActorLogic";
    public const string MassEntities = "massEntities";
    public const string BatchQueries = "batchQueries";
    public const string ResourceSpawnFlow = "resourceSpawnFlow";
    public const string HotReload = "hotReload";
    public const string BlockBreaker = "blockBreaker";

    public static IReadOnlyList<string> All { get; } =
    [
        ScalarPropertyCalls,
        StructContainerTransfer,
        DelegateStorm,
        PerFrameActorLogic,
        MassEntities,
        BatchQueries,
        ResourceSpawnFlow,
        HotReload,
        BlockBreaker,
    ];
}

public static class UnrealPerformanceMetricName
{
    public const string FrameTimeMedianMilliseconds = "frameTimeMedianMilliseconds";
    public const string FrameTimeP95Milliseconds = "frameTimeP95Milliseconds";
    public const string FrameTimeP99Milliseconds = "frameTimeP99Milliseconds";
    public const string BridgeTimeMilliseconds = "bridgeTimeMilliseconds";
    public const string Calls = "calls";
    public const string BytesCopied = "bytesCopied";
    public const string AllocatedBytes = "allocatedBytes";
    public const string GcCollectionCount = "gcCollectionCount";
    public const string GcPauseMilliseconds = "gcPauseMilliseconds";
    public const string MemoryHighWaterBytes = "memoryHighWaterBytes";
    public const string StartupMilliseconds = "startupMilliseconds";
    public const string ReloadMilliseconds = "reloadMilliseconds";
    public const string PackageSizeBytes = "packageSizeBytes";

    public static IReadOnlyList<string> All { get; } =
    [
        FrameTimeMedianMilliseconds,
        FrameTimeP95Milliseconds,
        FrameTimeP99Milliseconds,
        BridgeTimeMilliseconds,
        Calls,
        BytesCopied,
        AllocatedBytes,
        GcCollectionCount,
        GcPauseMilliseconds,
        MemoryHighWaterBytes,
        StartupMilliseconds,
        ReloadMilliseconds,
        PackageSizeBytes,
    ];
}

public sealed record UnrealPerformanceBuildConfiguration(
    string EngineVersion,
    string Platform,
    string Configuration,
    string Rhi,
    double FixedTimestepFramesPerSecond,
    string BuildFingerprint);

public sealed record UnrealPerformanceWorkload(
    string Id,
    string Version,
    string Category,
    string Description,
    double Weight,
    long WarmupIterations,
    long MeasuredIterations,
    string InputDigest,
    string AssetDigest,
    IReadOnlyList<string> RequiredMetrics);

public sealed record UnrealPerformanceSuiteManifest(
    string SchemaVersion,
    string SuiteId,
    string SuiteVersion,
    int TrialCount,
    UnrealPerformanceBuildConfiguration Build,
    IReadOnlyList<UnrealPerformanceWorkload> Workloads)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.performance-workload-suite/v0.1";
}

public sealed record UnrealPerformanceEnvironment(
    string MachineFingerprint,
    string OperatingSystem,
    string CpuModel,
    int LogicalProcessorCount,
    long PhysicalMemoryBytes,
    string GpuModel,
    string EngineVersion,
    string EngineBuildId,
    string Platform,
    string Configuration,
    string Rhi,
    string BuildFingerprint);

public sealed record UnrealPerformanceMetrics(
    IReadOnlyDictionary<string, double?> Values);

public sealed record UnrealPerformanceTrial(
    string WorkloadId,
    int Trial,
    int ExecutionOrdinal,
    string Status,
    string InputDigest,
    string AssetDigest,
    UnrealPerformanceMetrics Metrics);

public sealed record UnrealPerformanceEngineResult(
    string SchemaVersion,
    string ManifestDigest,
    string SuiteId,
    string SuiteVersion,
    string EngineId,
    string RuntimeVersion,
    string RuntimeCommit,
    UnrealPerformanceEnvironment Environment,
    IReadOnlyList<UnrealPerformanceTrial> Trials)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.performance-engine-result/v0.1";
}

public sealed record UnrealPerformanceTrialCapture(
    string SchemaVersion,
    string ManifestDigest,
    string SuiteId,
    string SuiteVersion,
    string EngineId,
    string RuntimeVersion,
    string RuntimeCommit,
    UnrealPerformanceEnvironment Environment,
    UnrealPerformanceTrial Trial)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.performance-trial-capture/v0.1";
}

public sealed record UnrealPerformanceRawMeasurement(
    string SchemaVersion,
    string EngineId,
    string RuntimeVersion,
    string RuntimeCommit,
    string OperatingSystem,
    string CpuModel,
    int LogicalProcessorCount,
    long PhysicalMemoryBytes,
    string GpuModel,
    string EngineBuildId,
    string WorkloadId,
    int Trial,
    int ExecutionOrdinal,
    string Status,
    UnrealPerformanceMetrics Metrics)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.performance-raw-measurement/v0.1";
}

public sealed record UnrealPerformanceRunSlot(
    int ExecutionOrdinal,
    int Trial,
    string WorkloadId,
    string EngineId);

public sealed record UnrealPerformanceMetricComparison(
    double? UnifiedScriptMedian,
    double? PuertsMedian,
    double? UnifiedScriptToPuertsRatio);

public sealed record UnrealPerformanceWorkloadComparison(
    string WorkloadId,
    string Category,
    double Weight,
    IReadOnlyDictionary<string, UnrealPerformanceMetricComparison> Metrics);

public sealed record UnrealPerformanceComparisonReport(
    string SchemaVersion,
    string ManifestDigest,
    string SuiteId,
    string SuiteVersion,
    bool MatchedEnvironment,
    bool MatchedWorkloads,
    bool AlternatingSchedule,
    IReadOnlyList<UnrealPerformanceRunSlot> Schedule,
    IReadOnlyList<UnrealPerformanceWorkloadComparison> Workloads)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.performance-comparison/v0.1";
}

public static class UnrealPerformanceWorkloadSuite
{
    private const string PassedStatus = "passed";
    private const string Sha256Prefix = "sha256:";

    public static string ComputeManifestDigest(UnrealPerformanceSuiteManifest manifest)
    {
        ValidateManifest(manifest);
        return ComputeDigest(UnrealPerformanceSuiteJson.SerializeManifest(
            manifest,
            writeIndented: false));
    }

    public static string ComputeMachineFingerprint(
        string operatingSystem,
        string cpuModel,
        int logicalProcessorCount,
        long physicalMemoryBytes,
        string gpuModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatingSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(cpuModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(gpuModel);
        if (logicalProcessorCount <= 0 || physicalMemoryBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalProcessorCount),
                "Machine descriptors require positive processor and memory values.");
        }
        return ComputeDigest(string.Join(
            '\n',
            "unifiedscript.performance-machine/v0.1",
            operatingSystem.Trim(),
            cpuModel.Trim(),
            logicalProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            physicalMemoryBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            gpuModel.Trim()));
    }

    public static IReadOnlyList<UnrealPerformanceRunSlot> CreateAlternatingSchedule(
        UnrealPerformanceSuiteManifest manifest)
    {
        ValidateManifest(manifest);
        var slots = new List<UnrealPerformanceRunSlot>(
            checked(manifest.TrialCount * manifest.Workloads.Count * 2));
        int ordinal = 0;
        for (int trial = 1; trial <= manifest.TrialCount; ++trial)
        {
            IReadOnlyList<string> engines = trial % 2 == 1
                ? UnrealPerformanceEngineId.All
                : [UnrealPerformanceEngineId.Puerts, UnrealPerformanceEngineId.UnifiedScript];
            foreach (UnrealPerformanceWorkload workload in manifest.Workloads)
            {
                foreach (string engine in engines)
                {
                    slots.Add(new UnrealPerformanceRunSlot(
                        ++ordinal,
                        trial,
                        workload.Id,
                        engine));
                }
            }
        }
        return slots;
    }

    public static UnrealPerformanceComparisonReport Compare(
        UnrealPerformanceSuiteManifest manifest,
        UnrealPerformanceEngineResult unifiedScript,
        UnrealPerformanceEngineResult puerts)
    {
        ValidateManifest(manifest);
        string manifestDigest = ComputeManifestDigest(manifest);
        ValidateResult(manifest, manifestDigest, unifiedScript, UnrealPerformanceEngineId.UnifiedScript);
        ValidateResult(manifest, manifestDigest, puerts, UnrealPerformanceEngineId.Puerts);
        if (unifiedScript.Environment != puerts.Environment)
        {
            throw new InvalidDataException(
                "Performance comparison rejected: machine, Unreal engine, or build environment differs.");
        }

        IReadOnlyList<UnrealPerformanceRunSlot> schedule =
            CreateAlternatingSchedule(manifest);
        Dictionary<(string EngineId, string WorkloadId, int Trial), int> expected =
            schedule.ToDictionary(
                static slot => (slot.EngineId, slot.WorkloadId, slot.Trial),
                static slot => slot.ExecutionOrdinal);
        foreach (UnrealPerformanceEngineResult result in new[] { unifiedScript, puerts })
        {
            foreach (UnrealPerformanceTrial trial in result.Trials)
            {
                if (!expected.TryGetValue(
                        (result.EngineId, trial.WorkloadId, trial.Trial),
                        out int ordinal) || ordinal != trial.ExecutionOrdinal)
                {
                    throw new InvalidDataException(
                        "Performance comparison rejected: execution order does not match " +
                        "the alternating engine schedule.");
                }
            }
        }

        var comparisons = new List<UnrealPerformanceWorkloadComparison>(
            manifest.Workloads.Count);
        foreach (UnrealPerformanceWorkload workload in manifest.Workloads)
        {
            var metricComparisons = new Dictionary<string, UnrealPerformanceMetricComparison>(
                StringComparer.Ordinal);
            foreach (string metric in UnrealPerformanceMetricName.All)
            {
                double? unifiedMedian = Median(
                    unifiedScript.Trials
                        .Where(trial => trial.WorkloadId == workload.Id)
                        .Select(trial => trial.Metrics.Values[metric]));
                double? puertsMedian = Median(
                    puerts.Trials
                        .Where(trial => trial.WorkloadId == workload.Id)
                        .Select(trial => trial.Metrics.Values[metric]));
                metricComparisons.Add(
                    metric,
                    new UnrealPerformanceMetricComparison(
                        unifiedMedian,
                        puertsMedian,
                        metric == UnrealPerformanceMetricName.AllocatedBytes
                            ? null
                            : Ratio(unifiedMedian, puertsMedian)));
            }
            comparisons.Add(new UnrealPerformanceWorkloadComparison(
                workload.Id,
                workload.Category,
                workload.Weight,
                metricComparisons));
        }

        return new UnrealPerformanceComparisonReport(
            UnrealPerformanceComparisonReport.CurrentSchemaVersion,
            manifestDigest,
            manifest.SuiteId,
            manifest.SuiteVersion,
            MatchedEnvironment: true,
            MatchedWorkloads: true,
            AlternatingSchedule: true,
            schedule,
            comparisons);
    }

    public static UnrealPerformanceEngineResult AssembleResult(
        UnrealPerformanceSuiteManifest manifest,
        string engineId,
        IReadOnlyList<UnrealPerformanceTrialCapture> captures)
    {
        ValidateManifest(manifest);
        if (!UnrealPerformanceEngineId.All.Contains(engineId, StringComparer.Ordinal) ||
            captures is null || captures.Count == 0)
        {
            throw new InvalidDataException(
                "Performance assembly requires one supported engine and trial captures.");
        }
        string manifestDigest = ComputeManifestDigest(manifest);
        UnrealPerformanceTrialCapture first = captures[0] ??
            throw new InvalidDataException("Performance capture cannot be null.");
        foreach (UnrealPerformanceTrialCapture capture in captures)
        {
            if (capture is null ||
                capture.SchemaVersion != UnrealPerformanceTrialCapture.CurrentSchemaVersion ||
                capture.ManifestDigest != manifestDigest ||
                capture.SuiteId != manifest.SuiteId ||
                capture.SuiteVersion != manifest.SuiteVersion ||
                capture.EngineId != engineId ||
                capture.RuntimeVersion != first.RuntimeVersion ||
                capture.RuntimeCommit != first.RuntimeCommit ||
                capture.Environment != first.Environment)
            {
                throw new InvalidDataException(
                    "Performance captures mix schemas, suites, runtimes, or environments.");
            }
        }
        var result = new UnrealPerformanceEngineResult(
            UnrealPerformanceEngineResult.CurrentSchemaVersion,
            manifestDigest,
            manifest.SuiteId,
            manifest.SuiteVersion,
            engineId,
            first.RuntimeVersion,
            first.RuntimeCommit,
            first.Environment,
            captures.Select(static capture => capture.Trial).ToArray());
        ValidateEngineResult(manifest, result);
        return result;
    }

    public static UnrealPerformanceTrialCapture CreateCapture(
        UnrealPerformanceSuiteManifest manifest,
        UnrealPerformanceRawMeasurement measurement)
    {
        ValidateManifest(manifest);
        ArgumentNullException.ThrowIfNull(measurement);
        if (measurement.SchemaVersion !=
                UnrealPerformanceRawMeasurement.CurrentSchemaVersion ||
            !UnrealPerformanceEngineId.All.Contains(
                measurement.EngineId,
                StringComparer.Ordinal) ||
            string.IsNullOrWhiteSpace(measurement.RuntimeVersion) ||
            string.IsNullOrWhiteSpace(measurement.RuntimeCommit) ||
            string.IsNullOrWhiteSpace(measurement.EngineBuildId) ||
            measurement.Status != PassedStatus)
        {
            throw new InvalidDataException(
                "Raw performance measurement has an unsupported schema, runtime, or status.");
        }
        UnrealPerformanceWorkload workload = manifest.Workloads.SingleOrDefault(
            workload => workload.Id == measurement.WorkloadId) ??
            throw new InvalidDataException(
                "Raw performance measurement targets an unknown workload.");
        UnrealPerformanceRunSlot expectedSlot = CreateAlternatingSchedule(manifest)
            .SingleOrDefault(slot =>
                slot.EngineId == measurement.EngineId &&
                slot.WorkloadId == measurement.WorkloadId &&
                slot.Trial == measurement.Trial) ??
            throw new InvalidDataException(
                "Raw performance measurement targets an unknown trial slot.");
        if (measurement.ExecutionOrdinal != expectedSlot.ExecutionOrdinal)
        {
            throw new InvalidDataException(
                "Raw performance measurement does not match the alternating execution ordinal.");
        }
        ValidateMetrics(workload, measurement.Metrics);
        var environment = new UnrealPerformanceEnvironment(
            ComputeMachineFingerprint(
                measurement.OperatingSystem,
                measurement.CpuModel,
                measurement.LogicalProcessorCount,
                measurement.PhysicalMemoryBytes,
                measurement.GpuModel),
            measurement.OperatingSystem.Trim(),
            measurement.CpuModel.Trim(),
            measurement.LogicalProcessorCount,
            measurement.PhysicalMemoryBytes,
            measurement.GpuModel.Trim(),
            manifest.Build.EngineVersion,
            measurement.EngineBuildId.Trim(),
            manifest.Build.Platform,
            manifest.Build.Configuration,
            manifest.Build.Rhi,
            manifest.Build.BuildFingerprint);
        return new UnrealPerformanceTrialCapture(
            UnrealPerformanceTrialCapture.CurrentSchemaVersion,
            ComputeManifestDigest(manifest),
            manifest.SuiteId,
            manifest.SuiteVersion,
            measurement.EngineId,
            measurement.RuntimeVersion,
            measurement.RuntimeCommit,
            environment,
            new UnrealPerformanceTrial(
                workload.Id,
                measurement.Trial,
                measurement.ExecutionOrdinal,
                measurement.Status,
                workload.InputDigest,
                workload.AssetDigest,
                measurement.Metrics));
    }

    public static void ValidateEngineResult(
        UnrealPerformanceSuiteManifest manifest,
        UnrealPerformanceEngineResult result)
    {
        ValidateManifest(manifest);
        ArgumentNullException.ThrowIfNull(result);
        if (!UnrealPerformanceEngineId.All.Contains(
                result.EngineId,
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "Performance result has an unsupported engine ID.");
        }
        ValidateResult(
            manifest,
            ComputeManifestDigest(manifest),
            result,
            result.EngineId);
    }

    public static void ValidateManifest(UnrealPerformanceSuiteManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != UnrealPerformanceSuiteManifest.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(manifest.SuiteId) ||
            string.IsNullOrWhiteSpace(manifest.SuiteVersion) ||
            manifest.TrialCount is < 3 or > 9)
        {
            throw new InvalidDataException(
                "The workload manifest must use v0.1, stable IDs, and 3-9 trials.");
        }
        ValidateBuild(manifest.Build);
        if (manifest.Workloads is null ||
            manifest.Workloads.Count != UnrealPerformanceWorkloadCategory.All.Count ||
            manifest.Workloads.Select(static workload => workload.Id)
                .Distinct(StringComparer.Ordinal).Count() != manifest.Workloads.Count ||
            !manifest.Workloads.Select(static workload => workload.Category)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(
                    UnrealPerformanceWorkloadCategory.All.Order(StringComparer.Ordinal),
                    StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The workload manifest must contain one unique workload for every M10.1 category.");
        }
        var coveredMetrics = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnrealPerformanceWorkload workload in manifest.Workloads)
        {
            if (workload is null || string.IsNullOrWhiteSpace(workload.Id) ||
                string.IsNullOrWhiteSpace(workload.Version) ||
                string.IsNullOrWhiteSpace(workload.Description) ||
                !double.IsFinite(workload.Weight) || workload.Weight <= 0 ||
                workload.WarmupIterations < 0 || workload.MeasuredIterations <= 0 ||
                !IsDigest(workload.InputDigest) || !IsDigest(workload.AssetDigest) ||
                workload.RequiredMetrics is null || workload.RequiredMetrics.Count == 0 ||
                workload.RequiredMetrics.Distinct(StringComparer.Ordinal).Count() !=
                    workload.RequiredMetrics.Count ||
                workload.RequiredMetrics.Any(metric =>
                    !UnrealPerformanceMetricName.All.Contains(metric, StringComparer.Ordinal)))
            {
                throw new InvalidDataException(
                    $"Workload '{workload?.Id}' has an invalid version, recipe, digest, weight, or metric set.");
            }
            coveredMetrics.UnionWith(workload.RequiredMetrics);
        }
        if (!coveredMetrics.SetEquals(UnrealPerformanceMetricName.All))
        {
            throw new InvalidDataException(
                "The workload suite does not capture every required M10.1 metric.");
        }
    }

    private static void ValidateBuild(UnrealPerformanceBuildConfiguration? build)
    {
        if (build is null || string.IsNullOrWhiteSpace(build.EngineVersion) ||
            string.IsNullOrWhiteSpace(build.Platform) ||
            string.IsNullOrWhiteSpace(build.Configuration) ||
            string.IsNullOrWhiteSpace(build.Rhi) ||
            !double.IsFinite(build.FixedTimestepFramesPerSecond) ||
            build.FixedTimestepFramesPerSecond <= 0 ||
            !IsDigest(build.BuildFingerprint))
        {
            throw new InvalidDataException(
                "The workload manifest requires a frozen Unreal build configuration.");
        }
    }

    private static void ValidateResult(
        UnrealPerformanceSuiteManifest manifest,
        string manifestDigest,
        UnrealPerformanceEngineResult result,
        string expectedEngineId)
    {
        ArgumentNullException.ThrowIfNull(result);
        int expectedTrialCount = checked(manifest.TrialCount * manifest.Workloads.Count);
        if (result.SchemaVersion != UnrealPerformanceEngineResult.CurrentSchemaVersion ||
            result.ManifestDigest != manifestDigest ||
            result.SuiteId != manifest.SuiteId || result.SuiteVersion != manifest.SuiteVersion ||
            result.EngineId != expectedEngineId ||
            string.IsNullOrWhiteSpace(result.RuntimeVersion) ||
            string.IsNullOrWhiteSpace(result.RuntimeCommit) ||
            result.Trials is null || result.Trials.Count != expectedTrialCount)
        {
            throw new InvalidDataException(
                $"Performance result '{expectedEngineId}' does not match the versioned suite.");
        }
        ValidateEnvironment(manifest.Build, result.Environment);
        var workloads = manifest.Workloads.ToDictionary(
            static workload => workload.Id,
            StringComparer.Ordinal);
        if (result.Trials.GroupBy(static trial => (trial.WorkloadId, trial.Trial))
                .Any(static group => group.Count() != 1))
        {
            throw new InvalidDataException(
                $"Performance result '{expectedEngineId}' contains duplicate trials.");
        }
        foreach (UnrealPerformanceTrial trial in result.Trials)
        {
            if (trial is null || !workloads.TryGetValue(trial.WorkloadId, out var workload) ||
                trial.Trial < 1 || trial.Trial > manifest.TrialCount ||
                trial.ExecutionOrdinal < 1 ||
                trial.ExecutionOrdinal > manifest.TrialCount * manifest.Workloads.Count * 2 ||
                trial.Status != PassedStatus ||
                trial.InputDigest != workload.InputDigest ||
                trial.AssetDigest != workload.AssetDigest)
            {
                throw new InvalidDataException(
                    $"Performance result '{expectedEngineId}' contains an unmatched or failed trial.");
            }
            ValidateMetrics(workload, trial.Metrics);
        }
        foreach (UnrealPerformanceWorkload workload in manifest.Workloads)
        {
            if (result.Trials.Count(trial => trial.WorkloadId == workload.Id) !=
                manifest.TrialCount)
            {
                throw new InvalidDataException(
                    $"Performance result '{expectedEngineId}' is missing a workload trial.");
            }
        }
    }

    private static void ValidateEnvironment(
        UnrealPerformanceBuildConfiguration build,
        UnrealPerformanceEnvironment? environment)
    {
        if (environment is null ||
            environment.EngineVersion != build.EngineVersion ||
            environment.Platform != build.Platform ||
            environment.Configuration != build.Configuration ||
            environment.Rhi != build.Rhi ||
            environment.BuildFingerprint != build.BuildFingerprint ||
            string.IsNullOrWhiteSpace(environment.EngineBuildId) ||
            string.IsNullOrWhiteSpace(environment.OperatingSystem) ||
            string.IsNullOrWhiteSpace(environment.CpuModel) ||
            string.IsNullOrWhiteSpace(environment.GpuModel) ||
            environment.LogicalProcessorCount <= 0 || environment.PhysicalMemoryBytes <= 0 ||
            environment.MachineFingerprint != ComputeMachineFingerprint(
                environment.OperatingSystem,
                environment.CpuModel,
                environment.LogicalProcessorCount,
                environment.PhysicalMemoryBytes,
                environment.GpuModel))
        {
            throw new InvalidDataException(
                "Performance result has an unmatched or unverifiable environment fingerprint.");
        }
    }

    private static void ValidateMetrics(
        UnrealPerformanceWorkload workload,
        UnrealPerformanceMetrics? metrics)
    {
        if (metrics?.Values is null || metrics.Values.Count != UnrealPerformanceMetricName.All.Count ||
            !metrics.Values.Keys.ToHashSet(StringComparer.Ordinal)
                .SetEquals(UnrealPerformanceMetricName.All))
        {
            throw new InvalidDataException(
                $"Workload '{workload.Id}' must report every metric as measured or explicitly null.");
        }
        foreach (KeyValuePair<string, double?> metric in metrics.Values)
        {
            if (metric.Value is { } value && (!double.IsFinite(value) || value < 0) ||
                workload.RequiredMetrics.Contains(metric.Key, StringComparer.Ordinal) &&
                metric.Value is null)
            {
                throw new InvalidDataException(
                    $"Workload '{workload.Id}' has a missing or invalid required metric '{metric.Key}'.");
            }
        }
        double? median = metrics.Values[UnrealPerformanceMetricName.FrameTimeMedianMilliseconds];
        double? p95 = metrics.Values[UnrealPerformanceMetricName.FrameTimeP95Milliseconds];
        double? p99 = metrics.Values[UnrealPerformanceMetricName.FrameTimeP99Milliseconds];
        if (median is not null && p95 is not null && p99 is not null &&
            (median > p95 || p95 > p99))
        {
            throw new InvalidDataException(
                $"Workload '{workload.Id}' frame percentiles are not monotonic.");
        }
    }

    private static double? Median(IEnumerable<double?> values)
    {
        double[] measured = values.Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .Order()
            .ToArray();
        if (measured.Length == 0)
        {
            return null;
        }
        int middle = measured.Length / 2;
        return measured.Length % 2 == 1
            ? measured[middle]
            : (measured[middle - 1] + measured[middle]) / 2.0;
    }

    private static double? Ratio(double? unifiedScript, double? puerts)
    {
        if (unifiedScript is null || puerts is null)
        {
            return null;
        }
        if (puerts.Value == 0)
        {
            return unifiedScript.Value == 0 ? 1.0 : null;
        }
        return unifiedScript.Value / puerts.Value;
    }

    private static string ComputeDigest(string value)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Sha256Prefix + Convert.ToHexStringLower(digest);
    }

    private static bool IsDigest(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith(Sha256Prefix, StringComparison.Ordinal) &&
        value.AsSpan(Sha256Prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
}

public static class UnrealPerformanceSuiteJson
{
    public static string SerializeManifest(
        UnrealPerformanceSuiteManifest manifest,
        bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, UnifiedJson.CreateOptions(writeIndented));
    }

    public static string SerializeResult(
        UnrealPerformanceEngineResult result,
        bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.Serialize(result, UnifiedJson.CreateOptions(writeIndented));
    }

    public static string SerializeReport(
        UnrealPerformanceComparisonReport report,
        bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, UnifiedJson.CreateOptions(writeIndented));
    }

    public static string SerializeCapture(
        UnrealPerformanceTrialCapture capture,
        bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return JsonSerializer.Serialize(capture, UnifiedJson.CreateOptions(writeIndented));
    }

    public static string SerializeRawMeasurement(
        UnrealPerformanceRawMeasurement measurement,
        bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        return JsonSerializer.Serialize(measurement, UnifiedJson.CreateOptions(writeIndented));
    }

    public static UnrealPerformanceSuiteManifest DeserializeManifest(string json) =>
        Deserialize<UnrealPerformanceSuiteManifest>(json, "workload manifest");

    public static UnrealPerformanceEngineResult DeserializeResult(string json) =>
        Deserialize<UnrealPerformanceEngineResult>(json, "engine result");

    public static UnrealPerformanceComparisonReport DeserializeReport(string json) =>
        Deserialize<UnrealPerformanceComparisonReport>(json, "comparison report");

    public static UnrealPerformanceTrialCapture DeserializeCapture(string json) =>
        Deserialize<UnrealPerformanceTrialCapture>(json, "trial capture");

    public static UnrealPerformanceRawMeasurement DeserializeRawMeasurement(string json) =>
        Deserialize<UnrealPerformanceRawMeasurement>(json, "raw measurement");

    private static T Deserialize<T>(string json, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<T>(json, UnifiedJson.CreateOptions()) ??
            throw new JsonException($"Performance {description} JSON was empty.");
    }
}
