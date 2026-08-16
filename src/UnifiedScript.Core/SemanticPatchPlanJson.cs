using System.Text.Json;

namespace UnifiedScript.Core;

public static class SemanticPatchPlanJson
{
    public static string Serialize(SemanticPatchPlanDocument document, bool writeIndented = true)
    {
        Validate(document);
        return JsonSerializer.Serialize(document, UnifiedJson.CreateOptions(writeIndented));
    }

    public static SemanticPatchPlanDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        SemanticPatchPlanDocument? document = JsonSerializer.Deserialize<SemanticPatchPlanDocument>(
            json,
            UnifiedJson.CreateOptions());
        if (document is null)
        {
            throw new JsonException("The semantic patch plan document was empty.");
        }

        Validate(document);
        return document;
    }

    public static void Validate(SemanticPatchPlanDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != SemanticPatchPlanDocument.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported semantic patch plan schema '{document.SchemaVersion}'. " +
                $"Expected '{SemanticPatchPlanDocument.CurrentSchemaVersion}'.");
        }

        if (document.SourceCompilationId.IsEmpty || document.SemanticConfigurationId.IsEmpty)
        {
            throw new JsonException(
                "A semantic patch plan requires non-empty source compilation and configuration IDs.");
        }

        if (document.Steps is null || document.Steps.Count == 0)
        {
            throw new JsonException("A semantic patch plan requires at least one step.");
        }

        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        var targetIds = new HashSet<SemanticId>();
        var introductionTypes = new Dictionary<string, SemanticId>(StringComparer.Ordinal);
        foreach (SemanticPatchPlanStep? step in document.Steps)
        {
            if (step is null)
            {
                throw new JsonException("Semantic patch plan steps cannot be null.");
            }

            if (string.IsNullOrWhiteSpace(step.StepId) || !stepIds.Add(step.StepId))
            {
                throw new JsonException(
                    "Every semantic patch plan step requires a unique, non-empty step ID.");
            }

            if (step.TargetNodeId.IsEmpty || !targetIds.Add(step.TargetNodeId))
            {
                throw new JsonException(
                    "Every semantic patch plan step must target one unique, non-empty semantic node ID.");
            }

            switch (step)
            {
                case IntroduceTemporaryPatchPlanStep introduction:
                    ValidateIntroduction(document, introduction);
                    introductionTypes.Add(introduction.StepId, introduction.ExpectedTypeId);
                    break;
                case ReconnectValuePatchPlanStep reconnect:
                    ValidateReconnection(reconnect, introductionTypes);
                    break;
                default:
                    throw new JsonException(
                        $"Unsupported semantic patch plan step '{step.GetType().Name}'.");
            }
        }
    }

    private static void ValidateIntroduction(
        SemanticPatchPlanDocument document,
        IntroduceTemporaryPatchPlanStep introduction)
    {
        SemanticPatchJson.Validate(new SemanticPatchDocument(
            SemanticPatchDocument.CurrentSchemaVersion,
            document.SourceCompilationId,
            document.SemanticConfigurationId,
            [introduction.ToOperation()]));
    }

    private static void ValidateReconnection(
        ReconnectValuePatchPlanStep reconnect,
        IReadOnlyDictionary<string, SemanticId> introductionTypes)
    {
        ValidateContext(
            reconnect.ExpectedContainingStatementId,
            reconnect.ExpectedParentBlockId,
            reconnect.ExpectedPreviousSiblingId,
            reconnect.ExpectedNextSiblingId,
            reconnect.ExpectedTypeId);
        if (string.IsNullOrWhiteSpace(reconnect.SourceStepId) ||
            !introductionTypes.TryGetValue(reconnect.SourceStepId, out SemanticId sourceTypeId))
        {
            throw new JsonException(
                $"Reconnect step '{reconnect.StepId}' must reference an earlier temporary-introduction step.");
        }

        if (sourceTypeId != reconnect.ExpectedTypeId)
        {
            throw new JsonException(
                $"Reconnect step '{reconnect.StepId}' and source step '{reconnect.SourceStepId}' " +
                "must declare the same semantic type ID.");
        }
    }

    private static void ValidateContext(
        SemanticId containingStatementId,
        SemanticId parentBlockId,
        SemanticId? previousSiblingId,
        SemanticId? nextSiblingId,
        SemanticId typeId)
    {
        if (containingStatementId.IsEmpty || parentBlockId.IsEmpty || typeId.IsEmpty)
        {
            throw new JsonException(
                "A reconnect plan step requires non-empty statement, parent block, and type IDs.");
        }

        if ((previousSiblingId is SemanticId previous && previous.IsEmpty) ||
            (nextSiblingId is SemanticId next && next.IsEmpty) ||
            previousSiblingId is not null && previousSiblingId == nextSiblingId)
        {
            throw new JsonException("Reconnect plan sibling preconditions are invalid.");
        }

        if (containingStatementId == previousSiblingId || containingStatementId == nextSiblingId)
        {
            throw new JsonException(
                "A reconnect plan containing statement cannot also be one of its sibling preconditions.");
        }
    }
}
