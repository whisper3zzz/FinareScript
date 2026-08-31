using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnifiedScript.Cli;

namespace UnifiedScript.Tests;

public sealed class UnrealProjectAdoptionTests
{
    [Fact]
    public void CliPreviewsAppliesDiagnosesAndRerunsWithoutProductDiff()
    {
        using var fixture = new AdoptionFixture(nativeProject: true);
        byte[] originalProject = File.ReadAllBytes(fixture.ProjectPath);
        string previewReport = Path.Combine(fixture.Root, "preview.json");

        int preview = Program.Main([
            "adopt",
            fixture.ProjectPath,
            "--sdk-root",
            fixture.SdkRoot,
            "--preview",
            "--report",
            previewReport,
        ]);

        Assert.Equal(0, preview);
        Assert.Equal(originalProject, File.ReadAllBytes(fixture.ProjectPath));
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectRoot, "Plugins", "UEUnifiedScript")));
        using (JsonDocument plan = JsonDocument.Parse(File.ReadAllText(previewReport)))
        {
            Assert.Equal(
                UnrealProjectAdoptionService.PlanSchemaVersion,
                plan.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal("ready", plan.RootElement.GetProperty("status").GetString());
            Assert.True(plan.RootElement.GetProperty("summary").GetProperty("changed").GetInt32() > 5);
            Assert.Equal(0, plan.RootElement.GetProperty("summary").GetProperty("conflicts").GetInt32());
        }

        int applied = Program.Main([
            "adopt",
            fixture.ProjectPath,
            "--sdk-root",
            fixture.SdkRoot,
            "--apply",
        ]);

        Assert.Equal(0, applied);
        JsonObject project = JsonNode.Parse(File.ReadAllText(fixture.ProjectPath))!.AsObject();
        Assert.Equal("preserved", project["FixtureField"]!.GetValue<string>());
        JsonObject plugin = Assert.Single(
            project["Plugins"]!.AsArray().OfType<JsonObject>(),
            candidate => candidate["Name"]!.GetValue<string>() == "UEUnifiedScript");
        Assert.True(plugin["Enabled"]!.GetValue<bool>());
        Assert.True(File.Exists(Path.Combine(
            fixture.ProjectRoot,
            "Managed",
            "AdoptGame.UnifiedScript",
            "FirstScript.cs")));
        Assert.True(File.Exists(Path.Combine(
            fixture.ProjectRoot,
            "Plugins",
            "UEUnifiedScript",
            "Binaries",
            "Managed",
            "UnifiedScript.Unreal.dll")));
        Assert.True(File.Exists(Path.Combine(
            fixture.ProjectRoot,
            "Plugins",
            "UEUnifiedScript",
            "Tools",
            "start-unifiedscript-authoring.ps1")));
        Assert.True(File.Exists(Path.Combine(
            fixture.ProjectRoot,
            "Plugins",
            "UEUnifiedScript",
            "Authoring",
            "Workbench",
            "index.html")));

        string doctorReport = Path.Combine(fixture.Root, "doctor.json");
        int doctor = Program.Main([
            "doctor",
            fixture.ProjectPath,
            "--engine-root",
            fixture.EngineRoot,
            "--report",
            doctorReport,
        ]);
        Assert.Equal(0, doctor);
        using (JsonDocument report = JsonDocument.Parse(File.ReadAllText(doctorReport)))
        {
            Assert.Equal("passed", report.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, report.RootElement.GetProperty("summary").GetProperty("failed").GetInt32());
            Assert.True(report.RootElement.GetProperty("summary").GetProperty("warnings").GetInt32() >= 1);
        }

        IReadOnlyDictionary<string, string> beforeRerun = SnapshotProductFiles(fixture.ProjectRoot);
        int rerun = Program.Main([
            "adopt",
            fixture.ProjectPath,
            "--sdk-root",
            Path.Combine(fixture.ProjectRoot, "Plugins", "UEUnifiedScript"),
        ]);
        Assert.Equal(0, rerun);
        Assert.Equal(beforeRerun, SnapshotProductFiles(fixture.ProjectRoot));
    }

    [Fact]
    public void UserEditBlocksUpgradeAndRollbackUntilItIsPreserved()
    {
        using var fixture = new AdoptionFixture(nativeProject: true);
        byte[] originalProject = File.ReadAllBytes(fixture.ProjectPath);
        UnrealProjectAdoptionPlan plan = fixture.CreatePlan();
        UnrealProjectAdoptionApplyResult apply = UnrealProjectAdoptionService.Apply(plan);
        Assert.Equal("applied", apply.Status);

        string managedSource = Path.Combine(
            fixture.ProjectRoot,
            "Managed",
            "AdoptGame.UnifiedScript",
            "FirstScript.cs");
        byte[] adoptedSource = File.ReadAllBytes(managedSource);
        File.AppendAllText(managedSource, "// user edit\n", Encoding.UTF8);

        int preview = Program.Main([
            "adopt",
            fixture.ProjectPath,
            "--sdk-root",
            fixture.SdkRoot,
            "--preview",
        ]);
        Assert.Equal(3, preview);
        int applyBlocked = Program.Main([
            "adopt",
            fixture.ProjectPath,
            "--sdk-root",
            fixture.SdkRoot,
        ]);
        Assert.Equal(65, applyBlocked);
        Assert.EndsWith("// user edit\n", File.ReadAllText(managedSource), StringComparison.Ordinal);

        int rollbackBlocked = Program.Main([
            "adopt",
            fixture.ProjectPath,
            "--rollback",
        ]);
        Assert.Equal(65, rollbackBlocked);
        Assert.True(File.Exists(managedSource));

        File.WriteAllBytes(managedSource, adoptedSource);
        int rolledBack = Program.Main([
            "adopt",
            fixture.ProjectPath,
            "--rollback",
        ]);
        Assert.Equal(0, rolledBack);
        Assert.Equal(originalProject, File.ReadAllBytes(fixture.ProjectPath));
        Assert.False(File.Exists(managedSource));
        Assert.False(File.Exists(Path.Combine(
            fixture.ProjectRoot,
            "Saved",
            "UnifiedScript",
            "Adoption",
            "current.json")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(fixture.ProjectRoot, "Config", "DefaultGame.ini")));
    }

    [Fact]
    public void InterruptedApplyRestoresEveryPreviouslyCommittedFile()
    {
        using var fixture = new AdoptionFixture(nativeProject: true);
        IReadOnlyDictionary<string, string> before = SnapshotProductFiles(fixture.ProjectRoot);
        UnrealProjectAdoptionPlan plan = fixture.CreatePlan();

        IOException exception = Assert.Throws<IOException>(() =>
            UnrealProjectAdoptionService.Apply(
                plan,
                index =>
                {
                    if (index == 2)
                    {
                        throw new IOException("Injected adoption interruption.");
                    }
                }));

        Assert.Contains("Injected", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, SnapshotProductFiles(fixture.ProjectRoot));
        Assert.False(File.Exists(Path.Combine(
            fixture.ProjectRoot,
            "Saved",
            "UnifiedScript",
            "Adoption",
            "current.json")));
    }

    [Fact]
    public void OwnedSdkPayloadUpgradesAndRollsBackOneGenerationAtATime()
    {
        using var fixture = new AdoptionFixture(nativeProject: true);
        UnrealProjectAdoptionApplyResult first = UnrealProjectAdoptionService.Apply(fixture.CreatePlan());
        string installedSource = Path.Combine(
            fixture.ProjectRoot,
            "Plugins",
            "UEUnifiedScript",
            "Source",
            "Fixture.txt");
        Assert.Equal("plugin-v1", File.ReadAllText(installedSource));

        File.WriteAllText(
            Path.Combine(fixture.SdkRoot, "Plugins", "UEUnifiedScript", "Source", "Fixture.txt"),
            "plugin-v2",
            new UTF8Encoding(false));
        UnrealProjectAdoptionPlan upgradePlan = fixture.CreatePlan();
        UnrealProjectAdoptionPlanEntry upgrade = Assert.Single(
            upgradePlan.Document.Files,
            file => file.Path == "Plugins/UEUnifiedScript/Source/Fixture.txt");
        Assert.Equal("update", upgrade.Action);
        UnrealProjectAdoptionApplyResult second = UnrealProjectAdoptionService.Apply(upgradePlan);
        Assert.Equal("plugin-v2", File.ReadAllText(installedSource));

        UnrealProjectAdoptionService.Rollback(fixture.ProjectPath, second.TransactionId);
        Assert.Equal("plugin-v1", File.ReadAllText(installedSource));
        UnrealProjectAdoptionApplyResult finalRollback = UnrealProjectAdoptionService.Rollback(
            fixture.ProjectPath,
            first.TransactionId);
        Assert.Equal("rolledBack", finalRollback.Status);
        Assert.False(File.Exists(installedSource));
    }

    [Fact]
    public void BlueprintOnlyProjectReceivesTransactionalNativeHostTargets()
    {
        using var fixture = new AdoptionFixture(nativeProject: false);
        byte[] originalProject = File.ReadAllBytes(fixture.ProjectPath);
        UnrealProjectAdoptionApplyResult result = UnrealProjectAdoptionService.Apply(
            fixture.CreatePlan());
        Assert.Equal("applied", result.Status);

        string config = File.ReadAllText(Path.Combine(
            fixture.ProjectRoot,
            "Config",
            "UnifiedScript.onboarding.json"));
        Assert.Contains("\"editorTarget\": \"AdoptGameEditor\"", config, StringComparison.Ordinal);
        Assert.Contains("\"gameTarget\": \"AdoptGame\"", config, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            fixture.ProjectRoot,
            "Source",
            "AdoptGame.Target.cs")));
        Assert.True(File.Exists(Path.Combine(
            fixture.ProjectRoot,
            "Source",
            "AdoptGameEditor.Target.cs")));
        Assert.True(File.Exists(Path.Combine(
            fixture.ProjectRoot,
            "Source",
            "AdoptGame",
            "AdoptGame.Build.cs")));
        Assert.True(File.Exists(Path.Combine(
            fixture.ProjectRoot,
            "Source",
            "AdoptGame",
            "AdoptGame.cpp")));
        Assert.Contains("\"Name\": \"AdoptGame\"", File.ReadAllText(fixture.ProjectPath));
        Assert.Contains("AdoptGame.UnifiedScript", File.ReadAllText(Path.Combine(
            fixture.ProjectRoot,
            "Managed",
            "AdoptGame.UnifiedScript",
            "AdoptGame.UnifiedScript.csproj")), StringComparison.Ordinal);

        string generatedSource = Path.Combine(
            fixture.ProjectRoot,
            "Plugins",
            "UEUnifiedScript",
            "Source",
            "Runtime",
            "Private",
            "Generated",
            "Fixture.generated.cpp");
        File.WriteAllText(generatedSource, "generated by project setup\n", new UTF8Encoding(false));

        UnrealProjectAdoptionPlan repair = fixture.CreatePlan();
        UnrealProjectAdoptionPlanEntry[] nativeHostFiles = repair.Document.Files
            .Where(static file => file.Kind == "native-host")
            .ToArray();
        Assert.Equal(4, nativeHostFiles.Length);
        Assert.All(nativeHostFiles, static file => Assert.Equal("unchanged", file.Action));
        UnrealProjectAdoptionPlanEntry generated = Assert.Single(
            repair.Document.Files,
            static file => file.Kind == "generated-source");
        Assert.Equal("unchanged", generated.Action);
        UnrealProjectDoctorReport doctor = UnrealProjectAdoptionService.Diagnose(
            fixture.ProjectPath,
            fixture.EngineRoot);
        Assert.Equal("passed", Check(doctor, "adoption-state").Status);
        Assert.Equal("unchanged", UnrealProjectAdoptionService.Apply(repair).Status);

        UnrealProjectAdoptionApplyResult rollback = UnrealProjectAdoptionService.Rollback(
            fixture.ProjectPath,
            result.TransactionId);
        Assert.Equal("rolledBack", rollback.Status);
        Assert.Equal(originalProject, File.ReadAllBytes(fixture.ProjectPath));
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectRoot, "Source")));
    }

    [Fact]
    public void DoctorSeparatesEngineDeploymentAndOwnedPayloadFailures()
    {
        using var fixture = new AdoptionFixture(nativeProject: true);
        UnrealProjectAdoptionService.Apply(fixture.CreatePlan());
        File.WriteAllText(
            Path.Combine(
                fixture.ProjectRoot,
                "Plugins",
                "UEUnifiedScript",
                "Source",
                "Fixture.txt"),
            "post-adoption drift",
            new UTF8Encoding(false));
        File.Delete(Path.Combine(
            fixture.ProjectRoot,
            "Deployment",
            "AdoptGame.unifiedscript-deployment.json"));
        File.WriteAllText(
            Path.Combine(fixture.EngineRoot, "Engine", "Build", "Build.version"),
            "{\"MajorVersion\":5,\"MinorVersion\":9,\"PatchVersion\":0}\n",
            new UTF8Encoding(false));

        UnrealProjectDoctorReport report = UnrealProjectAdoptionService.Diagnose(
            fixture.ProjectPath,
            fixture.EngineRoot);

        Assert.Equal("failed", report.Status);
        Assert.Equal("failed", Check(report, "engine").Status);
        Assert.Contains("outside the supported 5.8", Check(report, "engine").Message);
        Assert.Equal("failed", Check(report, "deployment-manifest").Status);
        Assert.Equal("failed", Check(report, "adoption-state").Status);
        Assert.Contains("drifted", Check(report, "adoption-state").Message);
    }

    private static IReadOnlyDictionary<string, string> SnapshotProductFiles(string projectRoot)
    {
        string savedRoot = Path.Combine(projectRoot, "Saved") + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(projectRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(savedRoot, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                path => Path.GetRelativePath(projectRoot, path).Replace('\\', '/'),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.OrdinalIgnoreCase);
    }

    private static UnrealProjectDoctorCheck Check(
        UnrealProjectDoctorReport report,
        string id) => Assert.Single(report.Checks, check => check.Id == id);

    private sealed class AdoptionFixture : IDisposable
    {
        public AdoptionFixture(bool nativeProject)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "UnifiedScriptAdoptionTests",
                Guid.NewGuid().ToString("N"));
            ProjectRoot = Path.Combine(Root, "Project");
            SdkRoot = Path.Combine(Root, "Sdk");
            EngineRoot = Path.Combine(Root, "Engine");
            Directory.CreateDirectory(ProjectRoot);
            Directory.CreateDirectory(SdkRoot);
            ProjectPath = Path.Combine(ProjectRoot, "AdoptGame.uproject");
            Write(ProjectPath, nativeProject ? NativeProjectJson : BlueprintProjectJson);
            Write(Path.Combine(ProjectRoot, "Config", "DefaultGame.ini"), "keep");
            if (nativeProject)
            {
                Write(
                    Path.Combine(ProjectRoot, "Source", "AdoptGameEditor.Target.cs"),
                    "public class AdoptGameEditorTarget {}\n");
                Write(
                    Path.Combine(ProjectRoot, "Source", "AdoptGame.Target.cs"),
                    "public class AdoptGameTarget {}\n");
            }
            CreateSdk();
            CreateEngine();
        }

        public string Root { get; }

        public string ProjectRoot { get; }

        public string ProjectPath { get; }

        public string SdkRoot { get; }

        public string EngineRoot { get; }

        public UnrealProjectAdoptionPlan CreatePlan() =>
            UnrealProjectAdoptionService.CreatePlan(new UnrealProjectAdoptionOptions(
                ProjectPath,
                SdkRoot,
                "minimal",
                "FirstScript",
                null));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private void CreateSdk()
        {
            string templates = Path.Combine(SdkRoot, "Templates", "UnrealProject");
            Write(
                Path.Combine(templates, "unifiedscript.project-templates.json"),
                """
                {
                  "schemaVersion": "unifiedscript.project-template-catalog/v0.1",
                  "productVersion": "test",
                  "defaultTemplate": "minimal",
                  "templates": [
                    {
                      "id": "minimal",
                      "title": "Minimal",
                      "description": "Fixture",
                      "scriptTemplate": "Managed/FirstScript.cs.template",
                      "capabilities": ["lifecycle"],
                      "nonClaims": ["package"]
                    }
                  ]
                }
                """);
            Write(
                Path.Combine(templates, "Config", "UnifiedScript.onboarding.json.template"),
                """
                {
                  "schemaVersion": "unifiedscript.unreal-project-onboarding/v0.1",
                  "editorTarget": "__EDITOR_TARGET__",
                  "gameTarget": "__GAME_TARGET__",
                  "projectModule": "__PROJECT_MODULE__"
                }
                """);
            Write(
                Path.Combine(templates, "Contracts", "Project.unreal-api-contract.json.template"),
                """
                {
                  "schemaVersion": "unifiedscript.unreal-api-contract/v0.1",
                  "contractName": "__PROJECT_NAME__.Gameplay",
                  "managedNamespace": "__MANAGED_NAMESPACE__.Generated",
                  "nativeHeader": "Fixture.h",
                  "nativeNamespace": "Fixture",
                  "types": []
                }
                """);
            Write(
                Path.Combine(templates, "Deployment", "Project.unifiedscript-deployment.json.template"),
                """
                {
                  "schemaVersion": "unifiedscript.unreal-script-deployment/v0.1",
                  "scripts": [{ "typeName": "__MANAGED_NAMESPACE__.__SCRIPT_NAME__" }]
                }
                """);
            Write(
                Path.Combine(templates, "Managed", "Project.UnifiedScript.csproj.template"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>__ASSEMBLY_NAME__</AssemblyName>
                    <RootNamespace>__MANAGED_NAMESPACE__</RootNamespace>
                  </PropertyGroup>
                </Project>
                """);
            Write(
                Path.Combine(templates, "Managed", "FirstScript.cs.template"),
                """
                namespace __MANAGED_NAMESPACE__;
                public sealed class __SCRIPT_NAME__ { }
                """);
            Write(
                Path.Combine(templates, "Source", "Project.Build.cs.template"),
                "public class __PROJECT_MODULE__ {}\n");
            Write(
                Path.Combine(templates, "Source", "Project.cpp.template"),
                "IMPLEMENT_PRIMARY_GAME_MODULE(__PROJECT_MODULE__)\n");
            Write(
                Path.Combine(templates, "Source", "Project.Target.cs.template"),
                "public class __PROJECT_NAME__Target {}\n");
            Write(
                Path.Combine(templates, "Source", "ProjectEditor.Target.cs.template"),
                "public class __PROJECT_NAME__EditorTarget {}\n");

            Write(
                Path.Combine(SdkRoot, "Plugins", "UEUnifiedScript", "UEUnifiedScript.uplugin"),
                "{\"FileVersion\":3,\"FriendlyName\":\"UnifiedScript\"}\n");
            Write(
                Path.Combine(SdkRoot, "Plugins", "UEUnifiedScript", "Source", "Fixture.txt"),
                "plugin-v1");
            Write(
                Path.Combine(
                    SdkRoot,
                    "Plugins",
                    "UEUnifiedScript",
                    "Source",
                    "Runtime",
                    "Private",
                    "Generated",
                    "Fixture.generated.cpp"),
                "sdk bootstrap generated source\n");
            Write(
                Path.Combine(SdkRoot, "Plugins", "UEUnifiedScript", "Resources", "Fixture.txt"),
                "resource-v1");
            foreach (string runtime in new[]
                     {
                         "UnifiedScript.Core.dll",
                         "UnifiedScript.Unreal.dll",
                         "UnifiedScript.Unreal.deps.json",
                     })
            {
                Write(Path.Combine(SdkRoot, "Managed", "Runtime", runtime), $"fixture:{runtime}");
            }
            Write(
                Path.Combine(SdkRoot, "Tools", "UnifiedScript.Cli", "UnifiedScript.Cli.dll"),
                "fixture-cli");
            Write(
                Path.Combine(SdkRoot, "Tools", "unifiedscript.cmd"),
                "@echo off\n");
            Write(
                Path.Combine(SdkRoot, "Tools", "setup-unreal-project.ps1"),
                "# fixture onboarding\n");
            Write(
                Path.Combine(SdkRoot, "Tools", "UnifiedScript.BuildGraph.psm1"),
                "# fixture build graph\n");
            Write(
                Path.Combine(SdkRoot, "Tools", "run-unifiedscript-onboarding-after-process.ps1"),
                "# fixture deferred onboarding\n");
            Write(
                Path.Combine(SdkRoot, "Tools", "start-unifiedscript-authoring.ps1"),
                "# fixture authoring launcher\n");
            Write(
                Path.Combine(SdkRoot, "Authoring", "Workbench", "index.html"),
                "<!doctype html><title>Fixture Workbench</title>\n");
        }

        private void CreateEngine()
        {
            Write(Path.Combine(EngineRoot, "Engine", "Build", "BatchFiles", "Build.bat"), "@echo off\n");
            Write(Path.Combine(EngineRoot, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe"), "fixture");
            Write(
                Path.Combine(EngineRoot, "Engine", "Build", "Build.version"),
                "{\"MajorVersion\":5,\"MinorVersion\":8,\"PatchVersion\":1}\n");
        }

        private static void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        private const string NativeProjectJson = """
            {
              "FileVersion": 3,
              "EngineAssociation": "5.8",
              "FixtureField": "preserved",
              "Modules": [
                { "Name": "AdoptGame", "Type": "Runtime" }
              ],
              "Plugins": [
                { "Name": "Puerts", "Enabled": true }
              ]
            }
            """;

        private const string BlueprintProjectJson = """
            {
              "FileVersion": 3,
              "EngineAssociation": "5.8",
              "FixtureField": "preserved"
            }
            """;
    }
}
