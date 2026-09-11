using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed class UnrealPuertsCoexistenceTests
{
    [Fact]
    public void GeneratesDeterministicExplicitPuertsBaselineAndRuntimeGuard()
    {
        UnrealPuertsMigrationPlan plan = CreatePlan();
        var planner = new UnrealPuertsCoexistencePlanner();

        UnrealPuertsCoexistencePackage first = planner.Generate(plan);
        UnrealPuertsCoexistencePackage second = planner.Generate(plan);

        Assert.Equal(
            UnrealPuertsCoexistenceJson.Serialize(first.Manifest),
            UnrealPuertsCoexistenceJson.Serialize(second.Manifest));
        Assert.Equal(
            first.Files.Select(static file => (file.RelativePath, file.Content, file.Sha256)),
            second.Files.Select(static file => (file.RelativePath, file.Content, file.Sha256)));
        Assert.Equal(2, first.Manifest.Routes.Count);
        Assert.All(first.Manifest.Routes, static route =>
        {
            Assert.Equal(UnrealPuertsRuntimeOwner.Puerts, route.Owner);
            Assert.Null(route.ManagedTypeName);
            Assert.StartsWith("BlockBreaker.", route.LifecycleKey, StringComparison.Ordinal);
        });
        Assert.Equal(
            UnrealPuertsCoexistenceJson.Serialize(first.Manifest),
            UnrealPuertsCoexistenceJson.Serialize(
                UnrealPuertsCoexistenceJson.Deserialize(
                    UnrealPuertsCoexistenceJson.Serialize(first.Manifest))));
        UnrealPuertsCoexistenceGeneratedFile guard = Assert.Single(
            first.Files,
            static file => file.RelativePath == "PuertsOwnership.generated.ts");
        Assert.Contains("claimPuertsLifecycle", guard.Content, StringComparison.Ordinal);
        Assert.Contains(
            "UE.FinareWorldLibrary.ClaimGameplayLifecycle",
            guard.Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "UE.EFinareRuntimeOwner.Puerts",
            guard.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PromotesOnlyExplicitKnownTypesAndRejectsAmbiguousOwnership()
    {
        UnrealPuertsMigrationPlan plan = CreatePlan();
        var planner = new UnrealPuertsCoexistencePlanner();

        UnrealPuertsCoexistencePackage package = planner.Generate(
            plan,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TS_Rifle"] = "BlockBreaker.Managed.RifleScript",
            });

        UnrealPuertsCoexistenceRoute player = package.Manifest.Routes.Single(
            static route => route.SourceType == "TS_Player");
        UnrealPuertsCoexistenceRoute rifle = package.Manifest.Routes.Single(
            static route => route.SourceType == "TS_Rifle");
        Assert.Equal(UnrealPuertsRuntimeOwner.Puerts, player.Owner);
        Assert.Equal(UnrealPuertsRuntimeOwner.Finare, rifle.Owner);
        Assert.Equal("BlockBreaker.Managed.RifleScript", rifle.ManagedTypeName);
        Assert.Contains("ReceiveBeginPlay", rifle.LifecycleHooks);
        Assert.Throws<ArgumentException>(() => planner.Generate(
            plan,
            new Dictionary<string, string> { ["Missing"] = "Example.Missing" }));

        UnrealPuertsCoexistenceManifest duplicated = package.Manifest with
        {
            Routes = [.. package.Manifest.Routes, package.Manifest.Routes[0]],
        };
        Assert.Throws<ArgumentException>(() =>
            UnrealPuertsCoexistencePlanner.Validate(duplicated));
    }

    [Fact]
    public void CliWritesPromotedRoutesAndBlockedPlanStillFailsAfterOutput()
    {
        using var fixture = new CoexistenceFixture();
        UnrealPuertsMigrationPlan plan = CreatePlan();
        fixture.Write(
            "migration-plan.json",
            UnrealPuertsMigrationPackageJson.Serialize(plan));
        string outputRoot = fixture.PathOf("coexistence");

        int succeeded = Finare.Cli.Program.Main(
        [
            "ue-puerts-coexist",
            fixture.PathOf("migration-plan.json"),
            "--out",
            outputRoot,
            "--unified",
            "TS_Rifle=BlockBreaker.Managed.RifleScript",
            "--fail-on-blockers",
        ]);

        Assert.Equal(0, succeeded);
        UnrealPuertsCoexistenceManifest manifest = UnrealPuertsCoexistenceJson.Deserialize(
            File.ReadAllText(Path.Combine(outputRoot, "coexistence-manifest.json")));
        Assert.Single(
            manifest.Routes,
            static route => route.Owner == UnrealPuertsRuntimeOwner.Finare);
        Assert.True(File.Exists(Path.Combine(outputRoot, "PuertsOwnership.generated.ts")));

        UnrealPuertsMigrationPlan blocked = plan with { Blocked = true };
        fixture.Write(
            "blocked-plan.json",
            UnrealPuertsMigrationPackageJson.Serialize(blocked));
        string blockedRoot = fixture.PathOf("blocked");
        int blockedExit = Finare.Cli.Program.Main(
        [
            "ue-puerts-coexist",
            fixture.PathOf("blocked-plan.json"),
            "--out",
            blockedRoot,
            "--fail-on-blockers",
        ]);
        Assert.Equal(65, blockedExit);
        Assert.True(File.Exists(Path.Combine(blockedRoot, "coexistence-manifest.json")));
    }

    private static UnrealPuertsMigrationPlan CreatePlan()
    {
        UnrealPuertsMigrationWorkItem[] workItems =
        [
            Item(
                "MIG-0001",
                "managed-port",
                "scriptType",
                "TS_Player extends UE.Character",
                "planned",
                "TypeScript/TS_Player.ts",
                10),
            Item(
                "MIG-0002",
                "managed-port",
                "scriptType",
                "TS_Rifle extends TS_BaseGun",
                "planned",
                "TypeScript/TS_Rifle.ts",
                4),
            Item(
                "MIG-0003",
                "lifecycle",
                "lifecycleHook",
                "TS_Player.ReceiveBeginPlay",
                "review",
                "TypeScript/TS_Player.ts",
                41),
            Item(
                "MIG-0004",
                "lifecycle",
                "lifecycleHook",
                "TS_Rifle.ReceiveBeginPlay",
                "review",
                "TypeScript/TS_Rifle.ts",
                11),
        ];
        return new UnrealPuertsMigrationPlan(
            "unifiedscript.puerts-migration-plan/v0.1",
            "plan-0123456789abcdef",
            "BlockBreaker",
            "sha256:scan",
            null,
            true,
            false,
            [],
            workItems,
            new UnrealPuertsMigrationPlanSummary(5, 4, 2, 2, 0));
    }

    private static UnrealPuertsMigrationWorkItem Item(
        string id,
        string phase,
        string category,
        string sourceKey,
        string status,
        string sourcePath,
        int line) =>
        new(
            id,
            phase,
            category,
            sourceKey,
            status == "review" ? "review" : "info",
            status,
            "migrate",
            [],
            [new UnrealPuertsMigrationLocation(sourcePath, line)]);

    private sealed class CoexistenceFixture : IDisposable
    {
        public CoexistenceFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "FinarePuertsCoexistence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string PathOf(string relativePath) =>
            Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public void Write(string relativePath, string content)
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
