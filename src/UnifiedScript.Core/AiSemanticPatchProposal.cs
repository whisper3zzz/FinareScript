using System.Text.Json;

namespace UnifiedScript.Core;

public sealed record AiSemanticPatchProposalProducer(
    string Tool,
    string Version,
    string? Model = null);

public sealed record AiSemanticPatchProposalDraft(
    string SchemaVersion,
    string Title,
    string Intent,
    AiSemanticPatchProposalProducer Producer,
    SemanticPatchPlanDocument Plan)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.ai-semantic-patch-proposal-draft/v0.1";

    public static AiSemanticPatchProposalDraft Create(
        string title,
        string intent,
        AiSemanticPatchProposalProducer producer,
        SemanticPatchPlanDocument plan) =>
        AiSemanticPatchProposalJson.NormalizeAndValidateDraft(new(
            CurrentSchemaVersion,
            title,
            intent,
            producer,
            plan));
}

public sealed record AiSemanticPatchProposalDocument(
    string SchemaVersion,
    SemanticId ProposalId,
    string Title,
    string Intent,
    AiSemanticPatchProposalProducer Producer,
    SemanticPatchPlanDocument Plan)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.ai-semantic-patch-proposal/v0.1";

    public static AiSemanticPatchProposalDocument Create(
        AiSemanticPatchProposalDraft draft) => AiSemanticPatchProposalJson.Create(draft);
}

public static class AiSemanticPatchProposalJson
{
    private const int MaximumTitleLength = 160;
    private const int MaximumIntentLength = 2048;
    private const int MaximumProducerFieldLength = 160;

    public static string Serialize(
        AiSemanticPatchProposalDocument proposal,
        bool writeIndented = true) => JsonSerializer.Serialize(
            NormalizeAndValidate(proposal),
            CreateOptions(writeIndented));

    public static AiSemanticPatchProposalDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        AiSemanticPatchProposalDocument? proposal =
            JsonSerializer.Deserialize<AiSemanticPatchProposalDocument>(
                json,
                CreateOptions());
        return NormalizeAndValidate(proposal ??
            throw new JsonException("The AI semantic patch proposal was empty."));
    }

    public static string SerializeDraft(
        AiSemanticPatchProposalDraft draft,
        bool writeIndented = true) => JsonSerializer.Serialize(
            NormalizeAndValidateDraft(draft),
            CreateOptions(writeIndented));

    public static AiSemanticPatchProposalDraft DeserializeDraft(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        AiSemanticPatchProposalDraft? draft =
            JsonSerializer.Deserialize<AiSemanticPatchProposalDraft>(
                json,
                CreateOptions());
        return NormalizeAndValidateDraft(draft ??
            throw new JsonException("The AI semantic patch proposal draft was empty."));
    }

    public static AiSemanticPatchProposalDocument Create(
        AiSemanticPatchProposalDraft draft)
    {
        draft = NormalizeAndValidateDraft(draft);
        var proposal = new AiSemanticPatchProposalDocument(
            AiSemanticPatchProposalDocument.CurrentSchemaVersion,
            default,
            draft.Title,
            draft.Intent,
            draft.Producer,
            draft.Plan);
        return proposal with { ProposalId = ComputeProposalId(proposal) };
    }

    public static AiSemanticPatchProposalDraft NormalizeAndValidateDraft(
        AiSemanticPatchProposalDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.SchemaVersion != AiSemanticPatchProposalDraft.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported AI semantic proposal draft schema '{draft.SchemaVersion}'.");
        }
        ValidateText(draft.Title, MaximumTitleLength, "title");
        ValidateText(draft.Intent, MaximumIntentLength, "intent");
        ValidateProducer(draft.Producer);
        if (draft.Plan is null)
        {
            throw new JsonException("An AI semantic proposal requires a typed patch plan.");
        }
        SemanticPatchPlanJson.Validate(draft.Plan);
        return draft;
    }

    public static AiSemanticPatchProposalDocument NormalizeAndValidate(
        AiSemanticPatchProposalDocument proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.SchemaVersion != AiSemanticPatchProposalDocument.CurrentSchemaVersion ||
            proposal.ProposalId.IsEmpty)
        {
            throw new JsonException("The AI semantic patch proposal has invalid required fields.");
        }
        ValidateText(proposal.Title, MaximumTitleLength, "title");
        ValidateText(proposal.Intent, MaximumIntentLength, "intent");
        ValidateProducer(proposal.Producer);
        if (proposal.Plan is null)
        {
            throw new JsonException("An AI semantic proposal requires a typed patch plan.");
        }
        SemanticPatchPlanJson.Validate(proposal.Plan);
        SemanticId expectedId = ComputeProposalId(proposal);
        if (proposal.ProposalId != expectedId)
        {
            throw new JsonException(
                $"AI semantic proposal identity '{proposal.ProposalId}' does not match " +
                $"its deterministic content identity '{expectedId}'.");
        }
        return proposal;
    }

    public static string GetStepKind(SemanticPatchPlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        JsonElement json = JsonSerializer.SerializeToElement<SemanticPatchPlanStep>(
            step,
            UnifiedJson.CreateOptions(writeIndented: false));
        if (!json.TryGetProperty("step", out JsonElement kind) ||
            kind.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(
                $"AI semantic proposal step '{step.StepId}' has no type discriminator.");
        }
        return kind.GetString()!;
    }

    private static SemanticId ComputeProposalId(AiSemanticPatchProposalDocument proposal)
    {
        string plan = SemanticPatchPlanJson.Serialize(proposal.Plan, writeIndented: false);
        var key = new System.Text.StringBuilder();
        AppendIdentityPart(key, "ai-semantic-patch-proposal-identity/v0.1");
        AppendIdentityPart(key, proposal.Title);
        AppendIdentityPart(key, proposal.Intent);
        AppendIdentityPart(key, proposal.Producer.Tool);
        AppendIdentityPart(key, proposal.Producer.Version);
        AppendIdentityPart(key, proposal.Producer.Model ?? string.Empty);
        AppendIdentityPart(key, plan);
        return SemanticId.FromKey(key.ToString());
    }

    private static void AppendIdentityPart(System.Text.StringBuilder key, string value) =>
        key.Append(value.Length).Append(':').Append(value).Append(';');

    private static JsonSerializerOptions CreateOptions(bool writeIndented = true)
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented);
        options.UnmappedMemberHandling =
            System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
        return options;
    }

    private static void ValidateProducer(AiSemanticPatchProposalProducer producer)
    {
        if (producer is null)
        {
            throw new JsonException("An AI semantic proposal requires producer identity.");
        }
        ValidateText(producer.Tool, MaximumProducerFieldLength, "producer tool");
        ValidateText(producer.Version, MaximumProducerFieldLength, "producer version");
        if (producer.Model is not null)
        {
            ValidateText(producer.Model, MaximumProducerFieldLength, "producer model");
        }
    }

    private static void ValidateText(string value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(static character => char.IsControl(character) && character is not '\n' and not '\t'))
        {
            throw new JsonException(
                $"AI semantic proposal {field} must be trimmed, non-empty, bounded, and printable.");
        }
    }
}
