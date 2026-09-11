using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed class UnrealApiContractProposalTests
{
    [Fact]
    public void ProposalIsDeterministicAndClassifiesEveryDependencyRequest()
    {
        UnrealReflectionManifest reflection = LoadReflection();
        UnrealApiContract contract = CreateContract();
        UnrealPuertsContractRequestSet requests = CreateRequests(
            Request("REQ-0008", "FixtureMode", null, "Active", "contract-member", "contract"),
            Request("REQ-0007", "FixtureDerived", null, "EchoOuter", "contract-member", "contract"),
            Request("REQ-0006", "OuterData", null, null, "type-construction", "contract"),
            Request("REQ-0002", "FixtureDerived", null, "Scale", "contract-member", "contract"),
            Request("REQ-0001", "FixtureDerived", null, "Spawn", "contract-member", "contract"),
            Request("REQ-0005", "FixtureDerived", null, "Spawn", "runtime-service-route", "data-plane"),
            Request("REQ-0004", "MissingType", null, "Run", "contract-member", "contract"),
            Request("REQ-0003", "FixtureDerived", null, "MissingMember", "contract-member", "contract"));
        var generator = new UnrealApiContractProposalGenerator();

        UnrealApiContractProposal first = generator.Generate(reflection, requests, contract);
        UnrealApiContractProposal second = generator.Generate(
            reflection,
            requests with { Requests = requests.Requests.Reverse().ToArray() },
            contract);

        Assert.Equal(
            UnrealApiContractProposalJson.Serialize(first),
            UnrealApiContractProposalJson.Serialize(second));
        Assert.True(first.ReviewRequired);
        Assert.Equal(8, first.Summary.RequestCount);
        Assert.Equal(1, first.Summary.SelectedCount);
        Assert.Equal(1, first.Summary.AlreadySelectedCount);
        Assert.Equal(1, first.Summary.AmbiguousCount);
        Assert.Equal(2, first.Summary.UnresolvedCount);
        Assert.Equal(3, first.Summary.ManualReviewCount);
        Assert.Equal(6, first.Summary.OpenDecisionCount);
        AssertDecision(first, "REQ-0001", "selected", "/Script/BindingMatrix.FixtureDerived", "Spawn");
        UnrealApiContractProposalDecision overload = AssertDecision(
            first,
            "REQ-0002",
            "ambiguous-member",
            "/Script/BindingMatrix.FixtureDerived",
            null);
        Assert.Equal(["Scale(float)", "Scale(int32)"], overload.Candidates);
        AssertDecision(first, "REQ-0003", "unresolved-member", "/Script/BindingMatrix.FixtureDerived", null);
        AssertDecision(first, "REQ-0004", "unresolved-type", null, null);
        AssertDecision(first, "REQ-0005", "manual-review", null, null);
        AssertDecision(first, "REQ-0006", "manual-review", "/Script/BindingMatrix.OuterData", null);
        AssertDecision(first, "REQ-0007", "already-selected", "/Script/BindingMatrix.FixtureDerived", "EchoOuter");
        AssertDecision(first, "REQ-0008", "manual-review", "/Script/BindingMatrix.FixtureMode", null);

        UnrealApiContractType proposedType = Assert.Single(
            first.ProposedContract.Types,
            static type => type.Path == "/Script/BindingMatrix.FixtureDerived");
        Assert.Equal(["EchoOuter", "Spawn"], proposedType.Members);
        _ = UnrealApiContractProjector.Project(reflection, first.ProposedContract);

        string serialized = UnrealApiContractProposalJson.Serialize(first);
        Assert.Equal(
            serialized,
            UnrealApiContractProposalJson.Serialize(
                UnrealApiContractProposalJson.Deserialize(serialized)));
        Assert.Throws<JsonException>(() => UnrealApiContractProposalJson.Deserialize(
            serialized.Replace(
                "\"projectName\"",
                "\"unknown\": true, \"projectName\"",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void ProposalRejectsRequestsAssessedAgainstAnotherContract()
    {
        UnrealPuertsContractRequestSet requests = CreateRequests(
            Request("REQ-0001", "FixtureDerived", null, "Spawn", "contract-member", "contract")) with
        {
            ContractFingerprint = new string('0', 64),
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new UnrealApiContractProposalGenerator().Generate(
                LoadReflection(),
                requests,
                CreateContract()));

        Assert.Contains("different API Contract", exception.Message, StringComparison.Ordinal);

        UnrealPuertsContractRequestSet malformed = CreateRequests(
            Request("REQ-0001", "FixtureDerived", null, "Spawn", "contract-member", "contract") with
            {
                SourceApis = [null!],
            });
        Assert.Throws<JsonException>(() => new UnrealApiContractProposalGenerator().Generate(
            LoadReflection(),
            malformed,
            CreateContract()));
    }

    [Fact]
    public void CliWritesReviewArtifactBeforeOpenDecisionGateFails()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "FinareContractProposalTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string reflectionPath = Path.Combine(root, "reflection.json");
            string requestsPath = Path.Combine(root, "requests.json");
            string contractPath = Path.Combine(root, "contract.json");
            string outputPath = Path.Combine(root, "proposal.json");
            File.WriteAllText(
                reflectionPath,
                UnrealReflectionJson.Serialize(LoadReflection()),
                Encoding.UTF8);
            File.WriteAllText(
                requestsPath,
                UnrealPuertsMigrationPackageJson.Serialize(CreateRequests(
                    Request("REQ-0001", "FixtureDerived", null, "Scale", "contract-member", "contract"))),
                Encoding.UTF8);
            File.WriteAllText(
                contractPath,
                UnrealApiContractJson.Serialize(CreateContract()),
                Encoding.UTF8);

            int exitCode = Finare.Cli.Program.Main(
            [
                "ue-contract-propose",
                reflectionPath,
                "--requests",
                requestsPath,
                "--base",
                contractPath,
                "--out",
                outputPath,
                "--fail-on-open-decisions",
            ]);

            Assert.Equal(65, exitCode);
            UnrealApiContractProposal proposal = UnrealApiContractProposalJson.Deserialize(
                File.ReadAllText(outputPath));
            Assert.Equal(1, proposal.Summary.AmbiguousCount);
            Assert.Equal(1, proposal.Summary.OpenDecisionCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static UnrealApiContractProposalDecision AssertDecision(
        UnrealApiContractProposal proposal,
        string requestId,
        string status,
        string? typePath,
        string? member)
    {
        UnrealApiContractProposalDecision decision = Assert.Single(
            proposal.Decisions,
            candidate => candidate.RequestId == requestId);
        Assert.Equal(status, decision.Status);
        Assert.Equal(typePath, decision.TypePath);
        Assert.Equal(member, decision.Member);
        return decision;
    }

    private static UnrealReflectionManifest LoadReflection() =>
        UnrealReflectionJson.Deserialize(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "UnrealBindingMatrix.reflection.json")));

    private static UnrealApiContract CreateContract() => new(
        UnrealApiContract.CurrentSchemaVersion,
        "Tests.Proposal",
        "Finare.Tests.Proposal",
        "ProposalThunks.h",
        "UE::Finare::Generated::Proposal",
        [new("/Script/BindingMatrix.FixtureDerived", ["EchoOuter"])]);

    private static UnrealPuertsContractRequestSet CreateRequests(
        params UnrealPuertsContractRequest[] requests) =>
        new(
            "unifiedscript.puerts-contract-requests/v0.1",
            "BindingMatrixPilot",
            Hash("binding-matrix-pilot"),
            null,
            requests);

    private static UnrealPuertsContractRequest Request(
        string id,
        string typeName,
        string? suggestedTypePath,
        string? memberName,
        string requestKind,
        string suggestedRoute) =>
        new(
            id,
            typeName,
            suggestedTypePath,
            memberName,
            requestKind,
            suggestedRoute,
            [$"UE.{typeName}{(memberName is null ? string.Empty : "." + memberName)}"],
            [new UnrealPuertsMigrationLocation("TypeScript/Pilot.ts", 1)]);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
