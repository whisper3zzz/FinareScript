using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed class UnrealPuertsMigrationPlannerTests
{
    [Fact]
    public void GeneratesDeterministicPlanCoverageRequestsStubsAndHarness()
    {
        using var fixture = new MigrationFixture();
        UnrealPuertsProjectScan scan = CreateScan(fixture);
        UnrealApiContract contract = CreateContract();
        var planner = new UnrealPuertsMigrationPlanner();

        UnrealPuertsMigrationPackage first = planner.Generate(
            scan,
            contract,
            "Example.Game.Migration");
        UnrealPuertsMigrationPackage second = planner.Generate(
            scan,
            contract,
            "Example.Game.Migration");

        Assert.False(first.Plan.Blocked);
        Assert.Equal(scan.Summary.FindingCount, first.Plan.Summary.WorkItemCount);
        Assert.Equal(5, first.Plan.Phases.Count);
        Assert.Equal(10, first.Files.Count);
        Assert.Equal(
            first.Files.Select(static file => (file.RelativePath, file.Content, file.Sha256)),
            second.Files.Select(static file => (file.RelativePath, file.Content, file.Sha256)));
        Assert.Equal(first.Plan.PlanId, second.Plan.PlanId);
        UnrealPuertsProjectScan changedScan = scan with
        {
            Findings = scan.Findings.Select((finding, index) => index == 0
                    ? finding with { MigrationAction = finding.MigrationAction + " changed" }
                    : finding)
                .ToArray(),
        };
        Assert.NotEqual(
            first.Plan.PlanId,
            planner.Generate(changedScan, contract, "Example.Game.Migration").Plan.PlanId);

        AssertCoverage(first.ApiCoverage, "UE.Actor.GetActorForwardVector", "covered");
        AssertCoverage(first.ApiCoverage, "UE.Character.AddMovementInput", "covered-owner");
        AssertCoverage(first.ApiCoverage, "UE.Vector", "type-covered");
        AssertCoverage(first.ApiCoverage, "UE.StaticMesh.Load", "covered-route");
        AssertCoverage(
            first.ApiCoverage,
            "UE.StaticMeshComponent.StaticMesh:set",
            "covered-adapter");
        AssertCoverage(first.ApiCoverage, "UE.GameplayStatics.ApplyDamage", "missing");
        Assert.All(first.CoexistenceManifest.Routes, static route =>
            Assert.Equal(UnrealPuertsRuntimeOwner.Puerts, route.Owner));
        Assert.Contains(
            first.Files,
            static file => file.RelativePath == "PuertsOwnership.generated.ts" &&
                file.Content.Contains("claimPuertsLifecycle", StringComparison.Ordinal));
        Assert.Contains(
            first.ContractRequests.Requests,
            static request => request.RequestKind == "runtime-service-route" &&
                request.SuggestedRoute == "data-plane" &&
                request.MemberName == "ApplyDamage");
        Assert.DoesNotContain(
            first.ContractRequests.Requests,
            static request => request.RequestKind == "runtime-service-route" &&
                request.SuggestedRoute == "resource-loader" &&
                request.SuggestedTypePath ==
                    "/Script/FinareRuntime.FinareWorldLibrary" &&
                request.MemberName == "LoadObjectByPath");

        UnrealPuertsMigrationGeneratedFile stubs = first.Files.Single(
            static file => file.RelativePath == "MigrationStubs.cs");
        Assert.Contains("PlayerMigrationStub", stubs.Content, StringComparison.Ordinal);
        Assert.Contains("Port_ReceiveBeginPlay", stubs.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(
            CSharpSyntaxTree.ParseText(stubs.Content).GetDiagnostics(),
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.All(first.VerificationHarness.Cases, static item =>
            Assert.NotEmpty(item.RequiredObservables));
        Assert.All(first.PuertsEvidenceTemplate.Cases, static item =>
            Assert.Equal("pending", item.Status));
        Assert.Equal(
            first.VerificationHarness.Cases.Count,
            first.PuertsEvidenceTemplate.Cases.Count);

        Assert.Equal(9, first.Manifest.Payloads.Count);
        foreach (UnrealPuertsMigrationPackagePayload payload in first.Manifest.Payloads)
        {
            UnrealPuertsMigrationGeneratedFile file = first.Files.Single(
                candidate => candidate.RelativePath == payload.RelativePath);
            Assert.Equal(Encoding.UTF8.GetByteCount(file.Content), payload.Size);
            Assert.Equal(Hash(file.Content), payload.Sha256);
            Assert.Equal(file.Sha256, payload.Sha256);
        }
    }

    [Fact]
    public void VerificationRequiresMatchedPassedEvidenceAndExactObservables()
    {
        using var fixture = new MigrationFixture();
        UnrealPuertsMigrationPackage package = new UnrealPuertsMigrationPlanner().Generate(
            CreateScan(fixture),
            CreateContract());
        UnrealPuertsVerificationEvidence puerts = Complete(
            package.PuertsEvidenceTemplate,
            package.VerificationHarness);
        UnrealPuertsVerificationEvidence unified = Complete(
            package.FinareEvidenceTemplate,
            package.VerificationHarness);

        UnrealPuertsVerificationReport passed = UnrealPuertsVerificationHarnessRunner.Compare(
            package.VerificationHarness,
            puerts,
            unified);

        Assert.True(passed.Passed);
        Assert.Equal(package.VerificationHarness.Cases.Count, passed.Summary.PassedCaseCount);
        UnrealPuertsVerificationCaseEvidence firstCase = unified.Cases[0];
        string firstObservable = firstCase.Observables.Keys.Order(StringComparer.Ordinal).First();
        var changedObservables = new Dictionary<string, string>(
            firstCase.Observables,
            StringComparer.Ordinal)
        {
            [firstObservable] = "different",
        };
        UnrealPuertsVerificationEvidence mismatched = unified with
        {
            Cases = [
                firstCase with { Observables = changedObservables },
                .. unified.Cases.Skip(1),
            ],
        };

        UnrealPuertsVerificationReport failed = UnrealPuertsVerificationHarnessRunner.Compare(
            package.VerificationHarness,
            puerts,
            mismatched);

        Assert.False(failed.Passed);
        Assert.Equal(1, failed.Summary.MismatchedCaseCount);
        Assert.Contains(
            failed.Issues,
            issue => issue.CaseId == firstCase.CaseId &&
                issue.Kind == "observable-mismatch" &&
                issue.Observable == firstObservable);
        string failedJson = UnrealPuertsVerificationReportJson.Serialize(failed);
        Assert.Contains("\"unifiedScriptValue\"", failedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"finareValue\"", failedJson, StringComparison.Ordinal);

        UnrealPuertsVerificationEvidence duplicated = unified with
        {
            Cases = [.. unified.Cases, unified.Cases[0]],
        };
        UnrealPuertsVerificationReport duplicateReport =
            UnrealPuertsVerificationHarnessRunner.Compare(
                package.VerificationHarness,
                puerts,
                duplicated);
        Assert.False(duplicateReport.Passed);
        Assert.Equal(
            package.VerificationHarness.Cases.Count - 1,
            duplicateReport.Summary.PassedCaseCount);
        Assert.Contains(
            duplicateReport.Issues,
            issue => issue.CaseId == firstCase.CaseId && issue.Kind == "duplicate-case");
    }

    [Fact]
    public void CliWritesPackageAndRunsSideBySideVerification()
    {
        using var fixture = new MigrationFixture();
        UnrealPuertsProjectScan scan = CreateScan(fixture);
        fixture.Write("scan.json", UnrealPuertsProjectScanJson.Serialize(scan));
        fixture.Write("contract.json", UnrealApiContractJson.Serialize(CreateContract()));
        string packageRoot = fixture.PathOf("package");

        int planExit = Finare.Cli.Program.Main(
        [
            "ue-puerts-plan",
            fixture.PathOf("scan.json"),
            "--contract",
            fixture.PathOf("contract.json"),
            "--namespace",
            "Example.Game.Migration",
            "--out",
            packageRoot,
            "--fail-on-blockers",
        ]);

        Assert.Equal(0, planExit);
        Assert.True(File.Exists(Path.Combine(packageRoot, "migration-package.json")));
        Assert.True(File.Exists(Path.Combine(packageRoot, "MigrationStubs.cs")));
        UnrealPuertsVerificationHarness harness =
            UnrealPuertsMigrationPackageJson.DeserializeHarness(File.ReadAllText(
                Path.Combine(packageRoot, "verification-harness.json")));
        UnrealPuertsVerificationEvidence puertsTemplate =
            UnrealPuertsMigrationPackageJson.DeserializeEvidence(File.ReadAllText(
                Path.Combine(packageRoot, "evidence", "puerts.template.json")));
        UnrealPuertsVerificationEvidence unifiedTemplate =
            UnrealPuertsMigrationPackageJson.DeserializeEvidence(File.ReadAllText(
                Path.Combine(packageRoot, "evidence", "finare.template.json")));
        fixture.Write(
            "puerts.json",
            UnrealPuertsMigrationPackageJson.Serialize(Complete(puertsTemplate, harness)));
        fixture.Write(
            "unified.json",
            UnrealPuertsMigrationPackageJson.Serialize(Complete(unifiedTemplate, harness)));
        string reportPath = fixture.PathOf("verification-report.json");

        int verifyExit = Finare.Cli.Program.Main(
        [
            "ue-puerts-verify",
            Path.Combine(packageRoot, "verification-harness.json"),
            "--puerts",
            fixture.PathOf("puerts.json"),
            "--unified",
            fixture.PathOf("unified.json"),
            "--out",
            reportPath,
            "--fail-on-mismatch",
        ]);

        Assert.Equal(0, verifyExit);
        Assert.Contains("\"passed\": true", File.ReadAllText(reportPath), StringComparison.Ordinal);
    }

    [Fact]
    public void BlockedScanStillWritesDiagnosablePackageBeforeFailingGate()
    {
        using var fixture = new MigrationFixture();
        fixture.Write(
            "TypeScript/Blocked.ts",
            "class Blocked extends UE.Actor { ReceiveBeginPlay() { eval(source); } }");
        fixture.Write(
            "TypeScript/Other.ts",
            "class Other extends UE.Actor { ReceiveBeginPlay() {} }");
        UnrealPuertsProjectScan scan = new UnrealPuertsProjectScanner().Scan(
            new UnrealPuertsProjectScanOptions(fixture.Root, MaximumFileCount: 1));
        fixture.Write("blocked-scan.json", UnrealPuertsProjectScanJson.Serialize(scan));
        string outputRoot = fixture.PathOf("blocked-package");

        int exitCode = Finare.Cli.Program.Main(
        [
            "ue-puerts-plan",
            fixture.PathOf("blocked-scan.json"),
            "--out",
            outputRoot,
            "--fail-on-blockers",
        ]);

        Assert.Equal(65, exitCode);
        UnrealPuertsMigrationPlan plan = UnrealPuertsMigrationPackageJson.DeserializePlan(
            File.ReadAllText(Path.Combine(outputRoot, "migration-plan.json")));
        Assert.True(plan.Blocked);
        Assert.Contains(plan.WorkItems, static item => item.Status == "blocked");
        UnrealPuertsVerificationHarness harness =
            UnrealPuertsMigrationPackageJson.DeserializeHarness(File.ReadAllText(
                Path.Combine(outputRoot, "verification-harness.json")));
        Assert.True(harness.PlanBlocked);
        Assert.All(
            plan.WorkItems.Where(static item => item.Status == "blocked"),
            item => Assert.Contains(
                harness.Cases,
                testCase => testCase.Status == "blocked" &&
                    testCase.WorkItemIds.Contains(item.Id, StringComparer.Ordinal)));
        UnrealPuertsVerificationEvidence puerts =
            UnrealPuertsMigrationPackageJson.DeserializeEvidence(File.ReadAllText(
                Path.Combine(outputRoot, "evidence", "puerts.template.json")));
        UnrealPuertsVerificationEvidence unified =
            UnrealPuertsMigrationPackageJson.DeserializeEvidence(File.ReadAllText(
                Path.Combine(outputRoot, "evidence", "finare.template.json")));
        UnrealPuertsVerificationReport report = UnrealPuertsVerificationHarnessRunner.Compare(
            harness,
            Complete(puerts, harness),
            Complete(unified, harness));
        Assert.False(report.Passed);
        Assert.Contains(report.Issues, static issue => issue.Kind == "blocked-plan");
    }

    private static UnrealPuertsProjectScan CreateScan(MigrationFixture fixture)
    {
        fixture.Write(
            "TypeScript/Player.ts",
            """
            import * as UE from 'ue';
            import {$ref} from 'puerts';

            class Player extends UE.Character {
                Mesh: UE.StaticMeshComponent;

                async ReceiveBeginPlay(): Promise<void> {
                    UE.Actor.GetActorForwardVector();
                    UE.Character.AddMovementInput();
                    UE.KismetSystemLibrary.LineTraceSingle(this);
                    const value = new UE.Vector(0, 0, 0);
                    UE.GameplayStatics.ApplyDamage(this, 1);
                    UE.StaticMesh.Load('/Game/Test/SM_Test');
                    this.Mesh.StaticMesh = UE.StaticMesh.Load('/Game/Test/SM_Test');
                    this.OnDamaged.Add(() => {});
                    await new Promise(resolve => setTimeout(resolve, 1));
                }
            }
            """);
        return new UnrealPuertsProjectScanner().Scan(
            new UnrealPuertsProjectScanOptions(fixture.Root));
    }

    private static UnrealApiContract CreateContract() => new(
        UnrealApiContract.CurrentSchemaVersion,
        "Example.Gameplay",
        "Example.Generated",
        "ExampleGeneratedThunks.h",
        "UE::Example::Generated",
        [
            new UnrealApiContractType(
                "/Script/Engine.Actor",
                ["GetActorForwardVector"]),
            new UnrealApiContractType(
                "/Script/Engine.Pawn",
                ["AddMovementInput"]),
            new UnrealApiContractType(
                "/Script/Engine.KismetSystemLibrary",
                ["LineTraceSingle"]),
            new UnrealApiContractType(
                "/Script/Engine.Vector",
                ["X"]),
            new UnrealApiContractType(
                "/Script/Engine.StaticMeshComponent",
                ["SetStaticMesh"]),
            new UnrealApiContractType(
                "/Script/FinareRuntime.FinareWorldLibrary",
                ["LoadObjectByPath"]),
        ]);

    private static UnrealPuertsVerificationEvidence Complete(
        UnrealPuertsVerificationEvidence template,
        UnrealPuertsVerificationHarness harness) =>
        template with
        {
            Cases = harness.Cases.Select(testCase => new UnrealPuertsVerificationCaseEvidence(
                testCase.Id,
                "passed",
                testCase.RequiredObservables.ToDictionary(
                    observable => observable,
                    observable => $"{testCase.Id}:{observable}:matched",
                    StringComparer.Ordinal))).ToArray(),
        };

    private static void AssertCoverage(
        UnrealPuertsApiCoverageReport report,
        string sourceApi,
        string status) =>
        Assert.Contains(
            report.Entries,
            entry => entry.SourceApi == sourceApi && entry.Status == status);

    private static string Hash(string content) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();

    private sealed class MigrationFixture : IDisposable
    {
        public MigrationFixture()
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "FinarePuertsPlan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string PathOf(string relativePath) =>
            System.IO.Path.Combine(
                Root,
                relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Write(string relativePath, string text)
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

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
    }
}
