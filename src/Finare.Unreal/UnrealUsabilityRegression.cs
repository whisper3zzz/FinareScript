using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Finare.Core;

namespace Finare.Unreal;

public sealed record UnrealUsabilityActionContract(
    string Id,
    string Label,
    bool RequiresEditorRestart);

public sealed record UnrealUsabilityScenarioBudget(
    string Id,
    string MeasurementScope,
    int MaximumDeliberateActions,
    int MaximumRestartActions,
    long MaximumWallClockMs,
    IReadOnlyList<UnrealUsabilityActionContract> DeliberateActions,
    IReadOnlyList<string> RequiredExecutedStages,
    IReadOnlyList<string> RequiredEvidenceMarkers,
    string Rationale);

public sealed record UnrealUsabilityBudgetManifest(
    string SchemaVersion,
    string BudgetId,
    IReadOnlyList<UnrealUsabilityScenarioBudget> Scenarios,
    IReadOnlyList<string> RevisionNotes)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.unreal-usability-budgets/v0.1";
}

public sealed record UnrealUsabilityEvidenceArtifact(
    string Role,
    string Path,
    string Sha256,
    long Bytes,
    string SchemaVersion,
    string Status);

public sealed record UnrealUsabilityFinalIdentity(
    string Project,
    string IdentityKind,
    string ManagedType,
    string Asset,
    string Source,
    string? SourceSha256);

public sealed record UnrealUsabilityScenarioReport(
    string Id,
    string Status,
    string MeasurementScope,
    int DeliberateActionCount,
    int MaximumDeliberateActions,
    int RestartActionCount,
    int MaximumRestartActions,
    long WallClockMs,
    long MaximumWallClockMs,
    string WallClockInterpretation,
    IReadOnlyList<UnrealUsabilityActionContract> DeliberateActions,
    IReadOnlyDictionary<string, long> PhaseDurationsMs,
    IReadOnlyList<string> ExecutedStages,
    IReadOnlyList<string> DeferredStages,
    UnrealUsabilityFinalIdentity FinalIdentity,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> RepairActions,
    IReadOnlyList<string> EvidenceRoles,
    IReadOnlyList<string> Violations);

public sealed record UnrealUsabilityRegressionReport(
    string SchemaVersion,
    string BudgetId,
    string BudgetDigest,
    string Status,
    IReadOnlyList<UnrealUsabilityScenarioReport> Scenarios,
    IReadOnlyList<UnrealUsabilityEvidenceArtifact> Evidence,
    IReadOnlyList<string> Violations)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.unreal-usability-regression/v0.1";
}

public static class UnrealUsabilityRegression
{
    public const string AdoptToFirstPlay = "adopt-to-first-play";
    public const string EditToReload = "edit-to-reload";

    private const string PackagedFirstPlaySchema =
        "unifiedscript.packaged-native-onboarding-acceptance/v0.1";
    private const string DailyReloadSchema =
        "unifiedscript.unreal-project-onboarding-report/v0.1";

    public static UnrealUsabilityRegressionReport Evaluate(
        UnrealUsabilityBudgetManifest budgets,
        string packagedFirstPlayPath,
        string dailyReloadPath,
        string automationEvidencePath)
    {
        Validate(budgets);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagedFirstPlayPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dailyReloadPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(automationEvidencePath);

        var evidence = new List<UnrealUsabilityEvidenceArtifact>();
        EvidenceFile packaged = ReadEvidence(
            "adopt-to-first-play.receipt",
            packagedFirstPlayPath,
            requireJson: true);
        EvidenceFile daily = ReadEvidence(
            "edit-to-reload.receipt",
            dailyReloadPath,
            requireJson: true);
        EvidenceFile automation = ReadEvidence(
            "edit-to-reload.runtime-automation",
            automationEvidencePath,
            requireJson: false);
        evidence.Add(packaged.Artifact);
        evidence.Add(daily.Artifact);
        evidence.Add(automation.Artifact);

        using JsonDocument packagedJson = JsonDocument.Parse(packaged.Bytes);
        using JsonDocument dailyJson = JsonDocument.Parse(daily.Bytes);
        RequireSchema(packagedJson.RootElement, PackagedFirstPlaySchema, packaged.Path);
        RequireSchema(dailyJson.RootElement, DailyReloadSchema, daily.Path);

        UnrealUsabilityScenarioReport firstPlay = EvaluateFirstPlay(
            budgets.Scenarios.Single(static scenario => scenario.Id == AdoptToFirstPlay),
            packagedJson.RootElement);
        UnrealUsabilityScenarioReport reload = EvaluateReload(
            budgets.Scenarios.Single(static scenario => scenario.Id == EditToReload),
            dailyJson.RootElement,
            Encoding.UTF8.GetString(automation.Bytes),
            evidence);
        UnrealUsabilityScenarioReport[] scenarios = [firstPlay, reload];
        string[] violations = scenarios
            .SelectMany(static scenario => scenario.Violations.Select(
                violation => $"{scenario.Id}: {violation}"))
            .ToArray();
        return new UnrealUsabilityRegressionReport(
            UnrealUsabilityRegressionReport.CurrentSchemaVersion,
            budgets.BudgetId,
            ComputeManifestDigest(budgets),
            violations.Length == 0 ? "passed" : "failed",
            scenarios,
            evidence,
            violations);
    }

    public static void Validate(UnrealUsabilityBudgetManifest budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        if (budgets.SchemaVersion != UnrealUsabilityBudgetManifest.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(budgets.BudgetId) ||
            budgets.Scenarios is null || budgets.Scenarios.Count != 2 ||
            budgets.RevisionNotes is null || budgets.RevisionNotes.Count == 0 ||
            budgets.RevisionNotes.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException(
                "Usability budgets require schema v0.1, one ID, two scenarios, and revision notes.");
        }

        var scenarioIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnrealUsabilityScenarioBudget scenario in budgets.Scenarios)
        {
            if (scenario is null ||
                !scenarioIds.Add(scenario.Id) ||
                scenario.Id is not (AdoptToFirstPlay or EditToReload) ||
                string.IsNullOrWhiteSpace(scenario.MeasurementScope) ||
                scenario.MaximumDeliberateActions <= 0 ||
                scenario.MaximumRestartActions < 0 ||
                scenario.MaximumWallClockMs <= 0 ||
                scenario.DeliberateActions is null ||
                scenario.DeliberateActions.Count == 0 ||
                scenario.RequiredExecutedStages is null ||
                scenario.RequiredEvidenceMarkers is null ||
                string.IsNullOrWhiteSpace(scenario.Rationale))
            {
                throw new InvalidDataException(
                    "Every usability scenario needs a unique supported ID, finite limits, actions, and rationale.");
            }

            var actionIds = new HashSet<string>(StringComparer.Ordinal);
            if (scenario.DeliberateActions.Any(action =>
                    action is null ||
                    string.IsNullOrWhiteSpace(action.Id) ||
                    string.IsNullOrWhiteSpace(action.Label) ||
                    !actionIds.Add(action.Id)) ||
                scenario.RequiredExecutedStages.Any(string.IsNullOrWhiteSpace) ||
                scenario.RequiredExecutedStages.Distinct(StringComparer.Ordinal).Count() !=
                    scenario.RequiredExecutedStages.Count ||
                scenario.RequiredEvidenceMarkers.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidDataException(
                    $"Usability scenario '{scenario.Id}' contains duplicate or empty contract entries.");
            }
        }
        if (!scenarioIds.SetEquals([AdoptToFirstPlay, EditToReload]))
        {
            throw new InvalidDataException(
                "Usability budgets must cover adopt-to-first-play and edit-to-reload exactly.");
        }
    }

    public static string ComputeManifestDigest(UnrealUsabilityBudgetManifest budgets)
    {
        Validate(budgets);
        byte[] bytes = Encoding.UTF8.GetBytes(
            UnrealUsabilityRegressionJson.SerializeManifest(budgets, writeIndented: false));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static UnrealUsabilityScenarioReport EvaluateFirstPlay(
        UnrealUsabilityScenarioBudget budget,
        JsonElement root)
    {
        var failures = new List<string>();
        var violations = ContractViolations(budget);
        string status = StringAt(root, "status");
        string firstPlayStatus = StringAt(
            root,
            "scriptClassFirstPlay",
            "firstPlay",
            "status");
        string packagedSmokeStatus = StringAt(
            root,
            "scriptClassFirstPlay",
            "packagedSmoke",
            "status");
        string lifecycle = StringAt(
            root,
            "scriptClassFirstPlay",
            "lifecycle",
            "actorComponent");
        if (status != "passed")
        {
            failures.Add($"packaged onboarding status was '{Printable(status)}'");
        }
        if (firstPlayStatus != "ready")
        {
            failures.Add($"first Play status was '{Printable(firstPlayStatus)}'");
        }
        if (packagedSmokeStatus != "passed")
        {
            failures.Add($"packaged runtime smoke status was '{Printable(packagedSmokeStatus)}'");
        }
        if (!lifecycle.Contains("passed", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("ActorComponent lifecycle proof was missing or failed");
        }

        long wallClockMs = Int64At(root, "durationMs");
        if (wallClockMs <= 0)
        {
            failures.Add("positive enclosing wall-clock duration was missing");
        }
        if (wallClockMs > budget.MaximumWallClockMs)
        {
            violations.Add(
                $"wall clock {wallClockMs} ms exceeded {budget.MaximumWallClockMs} ms");
        }
        violations.AddRange(failures);

        var identity = new UnrealUsabilityFinalIdentity(
            StringAt(root, "project"),
            "scriptClass",
            StringAt(root, "scriptClassFirstPlay", "firstPlay", "managedTypeName"),
            StringAt(root, "scriptClassFirstPlay", "firstPlay", "blueprintAssetPath"),
            StringAt(root, "onboarding", "generated", "firstScriptSource", "path"),
            NullIfWhiteSpace(StringAt(
                root,
                "onboarding",
                "generated",
                "firstScriptSource",
                "sha256")));
        if (new[]
            {
                identity.Project,
                identity.ManagedType,
                identity.Asset,
                identity.Source,
                identity.SourceSha256 ?? string.Empty,
            }.Any(string.IsNullOrWhiteSpace))
        {
            violations.Add("final Script Class project/type/asset/source identity was incomplete");
        }

        var phases = new SortedDictionary<string, long>(StringComparer.Ordinal)
        {
            ["adoptionApply"] = Int64At(root, "onboarding", "applyDurationMs"),
            ["scriptClassFirstPlay"] = Int64At(root, "scriptClassFirstPlay", "durationMs"),
        };
        return CreateScenarioReport(
            budget,
            wallClockMs,
            "conservative enclosing packaged onboarding, lifecycle, cook, package, and smoke run",
            phases,
            [],
            [],
            identity,
            failures,
            failures.Count == 0
                ? []
                : ["rerun packaged native onboarding and inspect the exact failed phase"],
            ["adopt-to-first-play.receipt"],
            violations);
    }

    private static UnrealUsabilityScenarioReport EvaluateReload(
        UnrealUsabilityScenarioBudget budget,
        JsonElement root,
        string automationText,
        List<UnrealUsabilityEvidenceArtifact> evidence)
    {
        var failures = new List<string>();
        var violations = ContractViolations(budget);
        string status = StringAt(root, "status");
        string mode = StringAt(root, "mode");
        if (status != "passed")
        {
            failures.Add($"daily reload status was '{Printable(status)}'");
        }
        if (mode != "dailyEditor")
        {
            failures.Add($"daily reload mode was '{Printable(mode)}'");
        }
        if (TryAt(root, "error") is JsonElement error && error.ValueKind != JsonValueKind.Null)
        {
            failures.Add("daily reload receipt contained an error");
        }

        long wallClockMs = Int64At(root, "durationMs");
        if (wallClockMs <= 0)
        {
            failures.Add("positive daily reload wall-clock duration was missing");
        }
        if (wallClockMs > budget.MaximumWallClockMs)
        {
            violations.Add(
                $"wall clock {wallClockMs} ms exceeded {budget.MaximumWallClockMs} ms");
        }

        JsonElement stagesElement = TryAt(root, "outputs", "buildGraph", "stages") ?? default;
        var phaseDurations = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var executedStages = new List<string>();
        var deferredStages = new List<string>();
        if (stagesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement stage in stagesElement.EnumerateArray())
            {
                string name = StringAt(stage, "name");
                string stageStatus = StringAt(stage, "status");
                if (name.Length == 0)
                {
                    continue;
                }
                phaseDurations[name] = Int64At(stage, "durationMs");
                if (stageStatus == "executed")
                {
                    executedStages.Add(name);
                }
                else if (stageStatus == "deferred")
                {
                    deferredStages.Add(name);
                }
            }
        }
        if (!executedStages.SequenceEqual(
                budget.RequiredExecutedStages,
                StringComparer.Ordinal))
        {
            violations.Add(
                "executed stages were [" + string.Join(", ", executedStages) +
                "]; expected [" + string.Join(", ", budget.RequiredExecutedStages) + "]");
        }
        string requiredAction = StringAt(root, "outputs", "requiredAction");
        if (status == "restartRequired" || requiredAction == "restartEditor")
        {
            violations.Add("compatible edit required an Editor restart");
        }
        foreach (string marker in budget.RequiredEvidenceMarkers)
        {
            if (!automationText.Contains(marker, StringComparison.Ordinal))
            {
                violations.Add($"runtime automation marker was missing: {marker}");
            }
        }

        UnrealUsabilityFinalIdentity identity = ResolveReloadIdentity(root, evidence, violations);
        violations.AddRange(failures);
        return CreateScenarioReport(
            budget,
            wallClockMs,
            "exact EditorAttached daily-build receipt",
            phaseDurations,
            executedStages,
            deferredStages,
            identity,
            failures,
            failures.Count == 0
                ? []
                : ["open the daily-build diagnostic receipt and use its bounded retry or restart action"],
            [
                "edit-to-reload.receipt",
                "edit-to-reload.runtime-automation",
                "edit-to-reload.deployment-manifest",
                "edit-to-reload.compiled-chunk",
            ],
            violations);
    }

    private static UnrealUsabilityFinalIdentity ResolveReloadIdentity(
        JsonElement root,
        List<UnrealUsabilityEvidenceArtifact> evidence,
        List<string> violations)
    {
        string project = StringAt(root, "project");
        string deploymentPath = StringAt(root, "outputs", "deploymentManifest");
        string chunkPath = ArrayStringAt(root, "outputs", "compiledChunks");
        string managedType = string.Empty;
        string asset = string.Empty;
        string source = string.Empty;
        string? sourceSha256 = null;

        if (!string.IsNullOrWhiteSpace(deploymentPath) && File.Exists(deploymentPath))
        {
            EvidenceFile deployment = ReadEvidence(
                "edit-to-reload.deployment-manifest",
                deploymentPath,
                requireJson: true);
            evidence.Add(deployment.Artifact);
            using JsonDocument deploymentJson = JsonDocument.Parse(deployment.Bytes);
            JsonElement scripts = TryAt(deploymentJson.RootElement, "scripts") ?? default;
            if (scripts.ValueKind == JsonValueKind.Array)
            {
                JsonElement selected = SelectDeploymentScript(scripts, chunkPath);
                if (selected.ValueKind == JsonValueKind.Object)
                {
                    managedType = StringAt(selected, "typeName");
                    asset = StringAt(selected, "assetPath");
                    source = StringAt(selected, "sourcePath");
                }
            }
        }
        else
        {
            violations.Add("daily reload deployment manifest was missing");
        }

        if (!string.IsNullOrWhiteSpace(chunkPath) && File.Exists(chunkPath))
        {
            EvidenceFile chunk = ReadEvidence(
                "edit-to-reload.compiled-chunk",
                chunkPath,
                requireJson: false);
            evidence.Add(chunk.Artifact with
            {
                SchemaVersion = "finare.compiled-program-chunk/opaque",
                Status = "captured",
            });
        }
        else
        {
            violations.Add("daily reload compiled chunk was missing");
        }

        if (new[] { project, managedType, asset, source }.Any(string.IsNullOrWhiteSpace))
        {
            violations.Add("final reload project/type/asset/source identity was incomplete");
        }
        return new UnrealUsabilityFinalIdentity(
            project,
            "deploymentScript",
            managedType,
            asset,
            source,
            sourceSha256);
    }

    private static JsonElement SelectDeploymentScript(JsonElement scripts, string chunkPath)
    {
        string chunkStem = Path.GetFileNameWithoutExtension(chunkPath);
        JsonElement first = default;
        foreach (JsonElement script in scripts.EnumerateArray())
        {
            if (first.ValueKind == JsonValueKind.Undefined)
            {
                first = script;
            }
            string assembly = StringAt(script, "assemblyPath");
            if (Path.GetFileNameWithoutExtension(assembly) == chunkStem)
            {
                return script;
            }
        }
        return first;
    }

    private static UnrealUsabilityScenarioReport CreateScenarioReport(
        UnrealUsabilityScenarioBudget budget,
        long wallClockMs,
        string wallClockInterpretation,
        IReadOnlyDictionary<string, long> phaseDurations,
        IReadOnlyList<string> executedStages,
        IReadOnlyList<string> deferredStages,
        UnrealUsabilityFinalIdentity identity,
        IReadOnlyList<string> failures,
        IReadOnlyList<string> repairActions,
        IReadOnlyList<string> evidenceRoles,
        IReadOnlyList<string> violations) =>
        new(
            budget.Id,
            violations.Count == 0 ? "passed" : "failed",
            budget.MeasurementScope,
            budget.DeliberateActions.Count,
            budget.MaximumDeliberateActions,
            budget.DeliberateActions.Count(static action => action.RequiresEditorRestart),
            budget.MaximumRestartActions,
            wallClockMs,
            budget.MaximumWallClockMs,
            wallClockInterpretation,
            budget.DeliberateActions,
            phaseDurations,
            executedStages,
            deferredStages,
            identity,
            failures,
            repairActions,
            evidenceRoles,
            violations);

    private static List<string> ContractViolations(UnrealUsabilityScenarioBudget budget)
    {
        var violations = new List<string>();
        if (budget.DeliberateActions.Count > budget.MaximumDeliberateActions)
        {
            violations.Add(
                $"contracted action count {budget.DeliberateActions.Count} exceeded " +
                budget.MaximumDeliberateActions);
        }
        int restartActions = budget.DeliberateActions.Count(
            static action => action.RequiresEditorRestart);
        if (restartActions > budget.MaximumRestartActions)
        {
            violations.Add(
                $"contracted restart count {restartActions} exceeded " +
                budget.MaximumRestartActions);
        }
        return violations;
    }

    private static EvidenceFile ReadEvidence(
        string role,
        string path,
        bool requireJson)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] bytes = File.ReadAllBytes(fullPath);
        string schema = requireJson ? ReadJsonString(bytes, "schemaVersion") : "opaque/log";
        string status = requireJson ? ReadJsonString(bytes, "status") : "captured";
        return new EvidenceFile(
            fullPath,
            bytes,
            new UnrealUsabilityEvidenceArtifact(
                role,
                fullPath,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.LongLength,
                schema,
                string.IsNullOrWhiteSpace(status) ? "unknown" : status));
    }

    private static string ReadJsonString(byte[] bytes, string property)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        return StringAt(document.RootElement, property);
    }

    private static void RequireSchema(JsonElement root, string expected, string path)
    {
        string actual = StringAt(root, "schemaVersion");
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"Usability evidence '{path}' uses unsupported schema '{Printable(actual)}'.");
        }
    }

    private static JsonElement? TryAt(JsonElement root, params string[] path)
    {
        JsonElement current = root;
        foreach (string segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }
        return current;
    }

    private static string StringAt(JsonElement root, params string[] path)
    {
        JsonElement? value = TryAt(root, path);
        return value is { ValueKind: JsonValueKind.String }
            ? value.Value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static long Int64At(JsonElement root, params string[] path)
    {
        JsonElement? value = TryAt(root, path);
        return value is { ValueKind: JsonValueKind.Number } && value.Value.TryGetInt64(out long result)
            ? result
            : 0;
    }

    private static string ArrayStringAt(JsonElement root, params string[] path)
    {
        JsonElement? value = TryAt(root, path);
        if (value is not { ValueKind: JsonValueKind.Array })
        {
            return string.Empty;
        }
        foreach (JsonElement item in value.Value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                return item.GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static string Printable(string value) =>
        string.IsNullOrWhiteSpace(value) ? "missing" : value;

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record EvidenceFile(
        string Path,
        byte[] Bytes,
        UnrealUsabilityEvidenceArtifact Artifact);
}

public static class UnrealUsabilityRegressionJson
{
    public static UnrealUsabilityBudgetManifest DeserializeManifest(string json) =>
        Deserialize<UnrealUsabilityBudgetManifest>(json, "budget manifest");

    public static string SerializeManifest(
        UnrealUsabilityBudgetManifest manifest,
        bool writeIndented = true) =>
        JsonSerializer.Serialize(manifest, UnifiedJson.CreateOptions(writeIndented));

    public static UnrealUsabilityRegressionReport DeserializeReport(string json) =>
        Deserialize<UnrealUsabilityRegressionReport>(json, "regression report");

    public static string SerializeReport(
        UnrealUsabilityRegressionReport report,
        bool writeIndented = true) =>
        JsonSerializer.Serialize(report, UnifiedJson.CreateOptions(writeIndented));

    private static T Deserialize<T>(string json, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<T>(json, UnifiedJson.CreateOptions()) ??
            throw new JsonException($"Usability {description} JSON was empty.");
    }
}
