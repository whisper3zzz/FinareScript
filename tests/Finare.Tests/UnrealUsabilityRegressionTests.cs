using System.Text.Json;
using Finare.Unreal;

namespace Finare.Tests;

public sealed class UnrealUsabilityRegressionTests
{
    [Fact]
    public void CheckedInBudgetsProduceDeterministicPassingWorkflowReport()
    {
        UnrealUsabilityBudgetManifest budgets = LoadBudgets();
        Assert.Equal(UnrealUsabilityBudgetManifest.CurrentSchemaVersion, budgets.SchemaVersion);
        UnrealUsabilityRegression.Validate(budgets);

        UnrealUsabilityScenarioBudget firstPlay = budgets.Scenarios.Single(
            static scenario => scenario.Id == UnrealUsabilityRegression.AdoptToFirstPlay);
        UnrealUsabilityScenarioBudget reload = budgets.Scenarios.Single(
            static scenario => scenario.Id == UnrealUsabilityRegression.EditToReload);
        Assert.Equal(8, firstPlay.MaximumDeliberateActions);
        Assert.Equal(1, firstPlay.MaximumRestartActions);
        Assert.Equal(720_000, firstPlay.MaximumWallClockMs);
        Assert.Equal(3, reload.MaximumDeliberateActions);
        Assert.Equal(0, reload.MaximumRestartActions);
        Assert.Equal(15_000, reload.MaximumWallClockMs);
        Assert.Equal(
            ["managed-gameplay-build", "compiled-chunks"],
            reload.RequiredExecutedStages);

        using Fixture fixture = Fixture.Create(budgets);
        UnrealUsabilityRegressionReport first = fixture.Evaluate(budgets);
        UnrealUsabilityRegressionReport second = fixture.Evaluate(budgets);

        Assert.Equal("passed", first.Status);
        Assert.Empty(first.Violations);
        Assert.Equal(
            UnrealUsabilityRegressionJson.SerializeReport(first),
            UnrealUsabilityRegressionJson.SerializeReport(second));
        UnrealUsabilityScenarioReport firstPlayReport = first.Scenarios.Single(
            static scenario => scenario.Id == UnrealUsabilityRegression.AdoptToFirstPlay);
        Assert.Equal(7, firstPlayReport.DeliberateActionCount);
        Assert.Equal(569_454, firstPlayReport.WallClockMs);
        Assert.Equal("Example.Game.FirstComponent", firstPlayReport.FinalIdentity.ManagedType);
        Assert.Equal("source-digest", firstPlayReport.FinalIdentity.SourceSha256);
        UnrealUsabilityScenarioReport reloadReport = first.Scenarios.Single(
            static scenario => scenario.Id == UnrealUsabilityRegression.EditToReload);
        Assert.Equal(6_366, reloadReport.WallClockMs);
        Assert.Equal(
            ["managed-gameplay-build", "compiled-chunks"],
            reloadReport.ExecutedStages);
        Assert.Equal("Example.Game.DemoScript", reloadReport.FinalIdentity.ManagedType);
        Assert.Contains(
            first.Evidence,
            static item => item.Role == "edit-to-reload.compiled-chunk" &&
                item.Status == "captured");
    }

    [Fact]
    public void RegressionsAreReportedAndFailingCliStillWritesReceipt()
    {
        UnrealUsabilityBudgetManifest budgets = LoadBudgets();
        UnrealUsabilityBudgetManifest regressedBudgets = budgets with
        {
            Scenarios = budgets.Scenarios.Select(scenario =>
                scenario.Id == UnrealUsabilityRegression.AdoptToFirstPlay
                    ? scenario with
                    {
                        MaximumDeliberateActions = 6,
                        MaximumWallClockMs = 100,
                    }
                    : scenario).ToArray(),
        };
        using Fixture fixture = Fixture.Create(
            regressedBudgets,
            includeUnexpectedStage: true,
            includeAutomationMarkers: false);

        UnrealUsabilityRegressionReport report = fixture.Evaluate(regressedBudgets);
        Assert.Equal("failed", report.Status);
        Assert.Contains(
            report.Violations,
            static violation => violation.Contains("contracted action count", StringComparison.Ordinal));
        Assert.Contains(
            report.Violations,
            static violation => violation.Contains("wall clock", StringComparison.Ordinal));
        Assert.Contains(
            report.Violations,
            static violation => violation.Contains("executed stages", StringComparison.Ordinal));
        Assert.Contains(
            report.Violations,
            static violation => violation.Contains("automation marker", StringComparison.Ordinal));

        string budgetPath = Path.Combine(fixture.Root, "regressed-budgets.json");
        string outputPath = Path.Combine(fixture.Root, "cli-report.json");
        File.WriteAllText(
            budgetPath,
            UnrealUsabilityRegressionJson.SerializeManifest(regressedBudgets));
        int exitCode = Finare.Cli.Program.Main([
            "ue-usability-budget",
            "--packaged-first-play", fixture.PackagedPath,
            "--daily-reload", fixture.DailyPath,
            "--automation-evidence", fixture.AutomationPath,
            "--budgets", budgetPath,
            "--out", outputPath,
            "--fail-on-regression",
        ]);

        Assert.Equal(65, exitCode);
        Assert.True(File.Exists(outputPath));
        UnrealUsabilityRegressionReport cliReport =
            UnrealUsabilityRegressionJson.DeserializeReport(File.ReadAllText(outputPath));
        Assert.Equal("failed", cliReport.Status);
        Assert.NotEmpty(cliReport.Violations);
    }

    [Fact]
    public void SdkCatalogShipsTheBudgetAndUsabilityGuide()
    {
        string repository = FindRepositoryRoot();
        string packager = File.ReadAllText(Path.Combine(
            repository,
            "tools",
            "package-finare-sdk.ps1"));
        string documentationIndex = File.ReadAllText(Path.Combine(
            repository,
            "docs",
            "DOCUMENTATION.md"));
        string usabilityGuide = File.ReadAllText(Path.Combine(
            repository,
            "docs",
            "UNREAL_USABILITY.md"));
        using JsonDocument release = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            repository,
            "release",
            "finare.release.json")));
        string[] releaseDocuments = release.RootElement
            .GetProperty("documentation")
            .EnumerateArray()
            .Select(static document => document.GetProperty("path").GetString()!)
            .ToArray();

        Assert.Contains("docs/UNREAL_USABILITY.md", releaseDocuments);
        Assert.Contains("usability-budgets.v0.1.json", packager, StringComparison.Ordinal);
        Assert.Contains("Documentation\\UNREAL_USABILITY.md", packager, StringComparison.Ordinal);
        Assert.Contains("UNREAL_USABILITY.md", documentationIndex, StringComparison.Ordinal);
        Assert.Contains("ue-usability-budget", usabilityGuide, StringComparison.Ordinal);
        Assert.Contains("--fail-on-regression", usabilityGuide, StringComparison.Ordinal);
        Assert.Contains(
            "does not require an interactive Editor session",
            usabilityGuide,
            StringComparison.Ordinal);
    }

    private static UnrealUsabilityBudgetManifest LoadBudgets()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "UnrealPerformance",
            "usability-budgets.v0.1.json");
        return UnrealUsabilityRegressionJson.DeserializeManifest(File.ReadAllText(path));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "Finare.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return directory.FullName;
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(
            string root,
            string packagedPath,
            string dailyPath,
            string automationPath)
        {
            Root = root;
            PackagedPath = packagedPath;
            DailyPath = dailyPath;
            AutomationPath = automationPath;
        }

        public string Root { get; }
        public string PackagedPath { get; }
        public string DailyPath { get; }
        public string AutomationPath { get; }

        public static Fixture Create(
            UnrealUsabilityBudgetManifest budgets,
            bool includeUnexpectedStage = false,
            bool includeAutomationMarkers = true)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "FinareUsabilityTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string packagedPath = Path.Combine(root, "packaged.json");
            string dailyPath = Path.Combine(root, "daily.json");
            string automationPath = Path.Combine(root, "automation.log");
            string deploymentPath = Path.Combine(root, "deployment.json");
            string chunkPath = Path.Combine(root, "Example.Game.uschunk");
            string sourcePath = Path.Combine(root, "FirstScript.cs");
            File.WriteAllText(sourcePath, "public sealed class FirstScript {}\n");
            File.WriteAllBytes(chunkPath, [0x55, 0x53, 0x43, 0x48]);
            File.WriteAllText(
                deploymentPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "unifiedscript.unreal-script-deployment/v0.1",
                    scripts = new[]
                    {
                        new
                        {
                            assetPath = "/Game/UnifiedScript/Demo",
                            assemblyPath = "{PluginDir}/Binaries/Managed/Example.Game.dll",
                            typeName = "Example.Game.DemoScript",
                            sourcePath = "{PluginDir}/Source/Managed/DemoScript.cs",
                        },
                    },
                }));
            File.WriteAllText(
                packagedPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "unifiedscript.packaged-native-onboarding-acceptance/v0.1",
                    status = "passed",
                    durationMs = 569_454,
                    project = Path.Combine(root, "Example.uproject"),
                    onboarding = new
                    {
                        applyDurationMs = 164_549,
                        generated = new
                        {
                            firstScriptSource = new
                            {
                                path = sourcePath,
                                sha256 = "source-digest",
                            },
                        },
                    },
                    scriptClassFirstPlay = new
                    {
                        durationMs = 229_464,
                        firstPlay = new
                        {
                            status = "ready",
                            managedTypeName = "Example.Game.FirstComponent",
                            blueprintAssetPath = "/Game/UnifiedScript/FirstComponent.FirstComponent",
                        },
                        lifecycle = new
                        {
                            actorComponent = "BeginPlay/Tick/EndPlay passed",
                        },
                        packagedSmoke = new
                        {
                            status = "passed",
                        },
                    },
                }));

            var stages = new List<object>
            {
                new { name = "managed-gameplay-build", status = "executed", durationMs = 1_853 },
                new { name = "compiled-chunks", status = "executed", durationMs = 2_769 },
                new { name = "deployment", status = "deferred", durationMs = 0 },
                new { name = "managed-startup", status = "deferred", durationMs = 0 },
            };
            if (includeUnexpectedStage)
            {
                stages.Add(new { name = "native-final-build", status = "executed", durationMs = 1 });
            }
            File.WriteAllText(
                dailyPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "unifiedscript.unreal-project-onboarding-report/v0.1",
                    status = "passed",
                    mode = "dailyEditor",
                    durationMs = 6_366,
                    project = Path.Combine(root, "Example.uproject"),
                    error = (object?)null,
                    outputs = new
                    {
                        deploymentManifest = deploymentPath,
                        compiledChunks = new[] { chunkPath },
                        buildGraph = new { stages },
                    },
                }));
            string automation = includeAutomationMarkers
                ? string.Join(
                    "\n",
                    budgets.Scenarios.Single(
                            static scenario => scenario.Id == UnrealUsabilityRegression.EditToReload)
                        .RequiredEvidenceMarkers)
                : "automation completed without the required proof markers";
            File.WriteAllText(automationPath, automation);
            return new Fixture(root, packagedPath, dailyPath, automationPath);
        }

        public UnrealUsabilityRegressionReport Evaluate(
            UnrealUsabilityBudgetManifest budgets) =>
            UnrealUsabilityRegression.Evaluate(
                budgets,
                PackagedPath,
                DailyPath,
                AutomationPath);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
