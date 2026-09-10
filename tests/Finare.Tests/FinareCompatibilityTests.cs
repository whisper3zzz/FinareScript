using System.Text.Json;
using Finare.Authoring;
using Finare.Core;
using Finare.Graph;
using Finare.Semantics;
using Finare.Unreal;

namespace Finare.Tests;

public sealed class FinareCompatibilityTests
{
    [Fact]
    public void PolicyCoversPersistentGeneratedBinaryAndAbiFamilies()
    {
        FinareCompatibilityPolicy policy = LoadPolicy();

        Assert.Equal(FinareCompatibilityPolicy.CurrentSchemaVersion, policy.SchemaVersion);
        Assert.Equal(19, policy.Families.Count);
        Assert.Equal(HirDocument.CurrentSchemaVersion, Family(policy, "hir").CurrentVersion);
        Assert.Equal(BoundCompilation.CurrentSchemaVersion, Family(policy, "semantics").CurrentVersion);
        Assert.Equal(GraphDocument.CurrentSchemaVersion, Family(policy, "graph").CurrentVersion);
        Assert.Equal(
            AuthoringSurfaceDocument.CurrentSchemaVersion,
            Family(policy, "authoring-surface").CurrentVersion);
        Assert.Equal(
            ScriptAssetDocument.CurrentSchemaVersion,
            Family(policy, "script-asset").CurrentVersion);
        Assert.Equal(
            GraphLayoutDocument.CurrentSchemaVersion,
            Family(policy, "graph-layout").CurrentVersion);
        Assert.Equal(
            UnrealReflectionManifest.CurrentSchemaVersion,
            Family(policy, "unreal-reflection").CurrentVersion);
        Assert.Equal(
            UnrealApiContract.CurrentSchemaVersion,
            Family(policy, "unreal-api-contract").CurrentVersion);
        Assert.Equal(
            UnrealApiGeneratedSurfaceLock.CurrentSchemaVersion,
            Family(policy, "unreal-api-generated-surface-lock").CurrentVersion);
        Assert.Equal(
            "unifiedscript.chunk/v1.0",
            Family(policy, "compiled-program-chunk").CurrentVersion);
        Assert.Equal(
            FinareUpgradeStrategy.Regenerate,
            Family(policy, "generated-bindings").UpgradeStrategy);
        Assert.Equal(
            UnrealDataPlaneExtensionSchema.CurrentSchemaVersion,
            Family(policy, "unreal-data-plane-extension").CurrentVersion);
        Assert.Equal(
            FinareUpgradeStrategy.Regenerate,
            Family(policy, "unreal-data-plane-extension").UpgradeStrategy);
        Assert.Equal(
            UnrealDataPlaneExtensionCatalog.CurrentSchemaVersion,
            Family(policy, "unreal-data-plane-extension-catalog").CurrentVersion);

        IReadOnlyDictionary<string, int> abi =
            Assert.IsAssignableFrom<IReadOnlyDictionary<string, int>>(
                Family(policy, "unreal-abi-set").Components);
        Assert.Equal(2, abi["bootstrap"]);
        Assert.Equal(2, abi["fastMember"]);
        Assert.Equal(3, abi["callFrame"]);
        Assert.Equal(4, abi["event"]);
        Assert.Equal(4, abi["sharedData"]);
        Assert.Equal(4, abi["gameplayData"]);
    }

    [Fact]
    public void PlanClassifiesCurrentLegacyDerivedChunkAndUnknownVersionsDeterministically()
    {
        using var fixture = new TemporaryDirectory();
        FinareCompatibilityPolicy policy = LoadPolicy();
        File.WriteAllText(
            Path.Combine(fixture.Path, "current-layout.usgraph"),
            "{\"schemaVersion\":\"unifiedscript.graph-layout/v0.1\"}");
        File.WriteAllText(
            Path.Combine(fixture.Path, "legacy-contract.json"),
            LegacyContractJson());
        File.WriteAllText(
            Path.Combine(fixture.Path, "old-derived.json"),
            "{\"schemaVersion\":\"unifiedscript.graph/v0.12\"}");
        File.WriteAllText(
            Path.Combine(fixture.Path, "future-reflection.json"),
            "{\"schemaVersion\":\"unifiedscript.unreal-reflection/v9.0\"}");
        File.WriteAllText(
            Path.Combine(fixture.Path, "unrelated.json"),
            "{\"schemaVersion\":\"thirdparty.example/v1\"}");
        File.WriteAllBytes(
            Path.Combine(fixture.Path, "program.uschunk"),
            FinareChunkCodec.Write([
                new FinareChunkSection(
                    FinareChunkSectionKind.RuntimeManifest,
                    FinareChunkSectionFlags.RuntimeRequired,
                    new byte[] { 1 }),
            ]));

        FinareUpgradePlan first = FinareUpgradePlanner.CreatePlan(policy, fixture.Path);
        FinareUpgradePlan second = FinareUpgradePlanner.CreatePlan(policy, fixture.Path);

        Assert.Equal(
            FinareCompatibilityJson.SerializePlan(first),
            FinareCompatibilityJson.SerializePlan(second));
        Assert.Equal(5, first.Summary.Scanned);
        Assert.Equal(2, first.Summary.Current);
        Assert.Equal(1, first.Summary.Rewrite);
        Assert.Equal(1, first.Summary.Regenerate);
        Assert.Equal(0, first.Summary.Rebuild);
        Assert.Equal(1, first.Summary.Blocked);
        Assert.Equal(
            FinareUpgradeDisposition.Rewrite,
            Item(first, "legacy-contract.json").Disposition);
        Assert.Equal(
            FinareUpgradeDisposition.Regenerate,
            Item(first, "old-derived.json").Disposition);
        Assert.DoesNotContain(first.Items, static item => item.Path == "unrelated.json");
    }

    [Fact]
    public void RewriteMigrationsReadLegacyAndEmitCanonicalCurrentDocuments()
    {
        string currentReflection = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "UnrealFixtures", "FinareRuntime.reflection.json"));
        string legacyReflection = currentReflection.Replace(
            UnrealReflectionManifest.CurrentSchemaVersion,
            UnrealReflectionManifest.PreviousSchemaVersion,
            StringComparison.Ordinal);

        string rewrittenReflection = FinareUpgradePlanner.Rewrite(
            "unreal-reflection",
            legacyReflection);
        string rewrittenContract = FinareUpgradePlanner.Rewrite(
            "unreal-api-contract",
            LegacyContractJson());

        Assert.Equal(
            UnrealReflectionManifest.CurrentSchemaVersion,
            ReadSchemaVersion(rewrittenReflection));
        Assert.Equal(
            UnrealApiContract.CurrentSchemaVersion,
            ReadSchemaVersion(rewrittenContract));
        Assert.Equal(
            rewrittenReflection,
            FinareUpgradePlanner.Rewrite("unreal-reflection", rewrittenReflection));
        Assert.Equal(
            rewrittenContract,
            FinareUpgradePlanner.Rewrite("unreal-api-contract", rewrittenContract));
    }

    [Fact]
    public void UpgradeApplyWritesLegacyRewritesAndReportWithoutChangingInputs()
    {
        using var fixture = new TemporaryDirectory();
        string inputDirectory = Path.Combine(fixture.Path, "input");
        string outputDirectory = Path.Combine(fixture.Path, "output");
        Directory.CreateDirectory(inputDirectory);
        string contractPath = Path.Combine(inputDirectory, "contract.json");
        File.WriteAllText(contractPath, LegacyContractJson());

        int exitCode = Finare.Cli.Program.Main([
            "upgrade-apply",
            inputDirectory,
            "--out",
            outputDirectory,
            "--policy",
            PolicyPath,
        ]);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            UnrealApiContract.LegacySchemaVersion,
            ReadSchemaVersion(File.ReadAllText(contractPath)));
        Assert.Equal(
            UnrealApiContract.CurrentSchemaVersion,
            ReadSchemaVersion(File.ReadAllText(Path.Combine(outputDirectory, "contract.json"))));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "upgrade-report.json")));
    }

    [Fact]
    public void UpgradeApplyIsAtomicWhenPlanRequiresRegenerationOrIsBlocked()
    {
        using var fixture = new TemporaryDirectory();
        string inputDirectory = Path.Combine(fixture.Path, "input");
        string outputDirectory = Path.Combine(fixture.Path, "output");
        Directory.CreateDirectory(inputDirectory);
        File.WriteAllText(Path.Combine(inputDirectory, "contract.json"), LegacyContractJson());
        File.WriteAllText(
            Path.Combine(inputDirectory, "future.json"),
            "{\"schemaVersion\":\"unifiedscript.unreal-reflection/v9.0\"}");

        int exitCode = Finare.Cli.Program.Main([
            "upgrade-apply",
            inputDirectory,
            "--out",
            outputDirectory,
            "--policy",
            PolicyPath,
        ]);

        Assert.Equal(65, exitCode);
        Assert.False(File.Exists(Path.Combine(outputDirectory, "contract.json")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "upgrade-report.json")));
    }

    private static string PolicyPath =>
        Path.Combine(AppContext.BaseDirectory, "finare.compatibility-policy.json");

    private static FinareCompatibilityPolicy LoadPolicy() =>
        FinareCompatibilityJson.DeserializePolicy(File.ReadAllText(PolicyPath));

    private static FinareCompatibilityFamily Family(
        FinareCompatibilityPolicy policy,
        string id) => Assert.Single(policy.Families, family => family.Id == id);

    private static FinareUpgradePlanItem Item(
        FinareUpgradePlan plan,
        string path) => Assert.Single(plan.Items, item => item.Path == path);

    private static string ReadSchemaVersion(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("schemaVersion").GetString()!;
    }

    private static string LegacyContractJson() => """
        {
          "schemaVersion": "unifiedscript.unreal-api-contract/v0.1",
          "contractName": "UpgradeFixture",
          "managedNamespace": "Finare.Generated",
          "nativeHeader": "UpgradeFixture.h",
          "nativeNamespace": "Finare::Generated",
          "types": [
            {
              "path": "/Script/Engine.Actor",
              "members": ["GetActorLocation"]
            }
          ]
        }
        """;

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"FinareCompatibility-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
