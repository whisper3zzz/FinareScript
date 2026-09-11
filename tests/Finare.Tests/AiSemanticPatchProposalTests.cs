using System.Text.Json;
using Finare.Core;

namespace Finare.Tests;

[Trait("Category", "Authoring")]
public sealed class AiSemanticPatchProposalTests
{
    [Fact]
    public void DraftCreatesDeterministicStrictProposal()
    {
        SemanticPatchPlanDocument plan = CreatePlan("2");
        AiSemanticPatchProposalDraft draft = AiSemanticPatchProposalDraft.Create(
            "Raise minimum damage",
            "Replace the selected literal while preserving its stable target and old value.",
            new AiSemanticPatchProposalProducer("test-agent", "1.0", "fixture-model"),
            plan);

        AiSemanticPatchProposalDocument first = AiSemanticPatchProposalDocument.Create(draft);
        AiSemanticPatchProposalDocument second = AiSemanticPatchProposalDocument.Create(draft);
        string json = AiSemanticPatchProposalJson.Serialize(first);
        AiSemanticPatchProposalDocument roundTrip =
            AiSemanticPatchProposalJson.Deserialize(json);

        Assert.Equal(first.ProposalId, second.ProposalId);
        Assert.Equal(first.ProposalId, roundTrip.ProposalId);
        Assert.Equal(json, AiSemanticPatchProposalJson.Serialize(roundTrip));
        Assert.Equal("replaceLiteral", AiSemanticPatchProposalJson.GetStepKind(plan.Steps[0]));
        Assert.Contains(first.ProposalId.Value, json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProposalIdentityRejectsMetadataOrPlanTampering()
    {
        AiSemanticPatchProposalDocument proposal = CreateProposal("2");

        Assert.Throws<JsonException>(() => AiSemanticPatchProposalJson.Serialize(
            proposal with { Intent = "A different intent." }));
        Assert.Throws<JsonException>(() => AiSemanticPatchProposalJson.Serialize(
            proposal with { Plan = CreatePlan("3") }));
    }

    [Fact]
    public void ProposalRejectsUnboundedOrUntrimmedHumanReadableFields()
    {
        SemanticPatchPlanDocument plan = CreatePlan("2");

        Assert.Throws<JsonException>(() => AiSemanticPatchProposalDraft.Create(
            " leading space",
            "Intent",
            new AiSemanticPatchProposalProducer("agent", "1"),
            plan));
        Assert.Throws<JsonException>(() => AiSemanticPatchProposalDraft.Create(
            "Title",
            new string('x', 2049),
            new AiSemanticPatchProposalProducer("agent", "1"),
            plan));
        Assert.Throws<JsonException>(() => AiSemanticPatchProposalJson.DeserializeDraft(
            AiSemanticPatchProposalJson.SerializeDraft(AiSemanticPatchProposalDraft.Create(
                "Title",
                "Intent",
                new AiSemanticPatchProposalProducer("agent", "1"),
                plan)).Replace("\"title\":", "\"unknown\":true,\"title\":", StringComparison.Ordinal)));
    }

    [Fact]
    public void CliPacksTheSameTypedPlanDeterministically()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"finare-ai-proposal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string planPath = Path.Combine(directory, "plan.json");
        string outputPath = Path.Combine(directory, "proposal.json");
        try
        {
            File.WriteAllText(planPath, SemanticPatchPlanJson.Serialize(CreatePlan("2")));
            string[] arguments =
            [
                "ai-proposal-pack", planPath,
                "--title", "Raise minimum damage",
                "--intent", "Apply a typed stable-ID literal replacement.",
                "--producer", "test-agent",
                "--producer-version", "1.0",
                "--model", "fixture-model",
                "--out", outputPath,
            ];

            Assert.Equal(0, Finare.Cli.Program.Main(arguments));
            byte[] first = File.ReadAllBytes(outputPath);
            AiSemanticPatchProposalDocument proposal =
                AiSemanticPatchProposalJson.Deserialize(File.ReadAllText(outputPath));
            Assert.Equal(0, Finare.Cli.Program.Main(arguments));

            Assert.Equal(first, File.ReadAllBytes(outputPath));
            Assert.Equal("test-agent", proposal.Producer.Tool);
            Assert.Single(proposal.Plan.Steps);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AiSemanticPatchProposalDocument CreateProposal(string value) =>
        AiSemanticPatchProposalDocument.Create(AiSemanticPatchProposalDraft.Create(
            "Raise minimum damage",
            "Apply a typed stable-ID literal replacement.",
            new AiSemanticPatchProposalProducer("test-agent", "1.0"),
            CreatePlan(value)));

    private static SemanticPatchPlanDocument CreatePlan(string value) =>
        SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("proposal-compilation"),
            SemanticId.FromKey("proposal-configuration"),
            [
                new ReplaceLiteralPatchPlanStep(
                    "replaceMinimum",
                    SemanticId.FromKey("minimum-literal"),
                    "1",
                    value),
            ]);
}
