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
        var targetKinds = new Dictionary<SemanticId, bool>();
        var producedTypes = new Dictionary<string, SemanticId>(StringComparer.Ordinal);
        var branchProducers = new Dictionary<string, InsertStatementPatchPlanStep>(StringComparer.Ordinal);
        var terminatedBranches = new HashSet<(string SourceStepId, IfBranchKind Branch)>();
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

            if (step.TargetNodeId.IsEmpty)
            {
                throw new JsonException(
                    "Every semantic patch plan step must target a non-empty semantic node ID.");
            }

            bool isInsertion = step is InsertStatementPatchPlanStep or
                InsertBranchStatementPatchPlanStep;
            if (targetKinds.TryGetValue(step.TargetNodeId, out bool previousWasInsertion))
            {
                if (!isInsertion || !previousWasInsertion)
                {
                    throw new JsonException(
                        $"Semantic patch plan target '{step.TargetNodeId}' can be repeated only " +
                        "by statement-insertion steps.");
                }
            }
            else
            {
                targetKinds.Add(step.TargetNodeId, isInsertion);
            }

            switch (step)
            {
                case ReplaceLiteralPatchPlanStep replace:
                    ValidateOperation(document, replace.ToOperation());
                    break;
                case ChangeOperatorPatchPlanStep changeOperator:
                    ValidateOperation(document, changeOperator.ToOperation());
                    break;
                case ReconnectArgumentPatchPlanStep reconnectArgument:
                    ValidateOperation(document, reconnectArgument.ToOperation());
                    break;
                case InsertStatementPatchPlanStep insertion:
                    ValidateOperation(document, insertion.ToOperation());
                    if (insertion.NewStatement is LocalLiteralDeclarationPatchStatement insertedLocal)
                    {
                        producedTypes.Add(insertion.StepId, insertedLocal.ExpectedTypeId);
                    }
                    else if (insertion.NewStatement is IfPatchStatement)
                    {
                        branchProducers.Add(insertion.StepId, insertion);
                    }

                    break;
                case InsertBranchStatementPatchPlanStep branchInsertion:
                    ValidateBranchInsertion(
                        document,
                        branchInsertion,
                        branchProducers,
                        terminatedBranches);
                    if (branchInsertion.NewStatement is LocalLiteralDeclarationPatchStatement branchLocal)
                    {
                        producedTypes.Add(branchInsertion.StepId, branchLocal.ExpectedTypeId);
                    }
                    else if (branchInsertion.NewStatement is ReturnPatchStatement)
                    {
                        terminatedBranches.Add((branchInsertion.SourceStepId, branchInsertion.Branch));
                    }

                    break;
                case DeleteStatementPatchPlanStep deletion:
                    ValidateOperation(document, deletion.ToOperation());
                    break;
                case IntroduceTemporaryPatchPlanStep introduction:
                    ValidateIntroduction(document, introduction);
                    producedTypes.Add(introduction.StepId, introduction.ExpectedTypeId);
                    break;
                case ReconnectValuePatchPlanStep reconnect:
                    ValidateReconnection(reconnect, producedTypes);
                    break;
                default:
                    throw new JsonException(
                        $"Unsupported semantic patch plan step '{step.GetType().Name}'.");
            }
        }
    }

    private static void ValidateOperation(
        SemanticPatchPlanDocument document,
        SemanticPatchOperation operation)
    {
        SemanticPatchJson.Validate(new SemanticPatchDocument(
            SemanticPatchDocument.CurrentSchemaVersion,
            document.SourceCompilationId,
            document.SemanticConfigurationId,
            [operation]));
    }

    private static void ValidateIntroduction(
        SemanticPatchPlanDocument document,
        IntroduceTemporaryPatchPlanStep introduction)
    {
        ValidateOperation(document, introduction.ToOperation());
    }

    private static void ValidateBranchInsertion(
        SemanticPatchPlanDocument document,
        InsertBranchStatementPatchPlanStep insertion,
        IReadOnlyDictionary<string, InsertStatementPatchPlanStep> branchProducers,
        IReadOnlySet<(string SourceStepId, IfBranchKind Branch)> terminatedBranches)
    {
        if (string.IsNullOrWhiteSpace(insertion.SourceStepId) ||
            !branchProducers.TryGetValue(
                insertion.SourceStepId,
                out InsertStatementPatchPlanStep? source))
        {
            throw new JsonException(
                $"Branch insertion step '{insertion.StepId}' must reference an earlier if-insertion step.");
        }

        if (source.TargetNodeId != insertion.TargetNodeId)
        {
            throw new JsonException(
                $"Branch insertion step '{insertion.StepId}' and source step " +
                $"'{insertion.SourceStepId}' must use the same original parent-block target.");
        }

        if (!Enum.IsDefined(insertion.Branch))
        {
            throw new JsonException(
                $"Branch insertion step '{insertion.StepId}' has an unsupported branch selector.");
        }

        if (terminatedBranches.Contains((insertion.SourceStepId, insertion.Branch)))
        {
            throw new JsonException(
                $"Branch insertion step '{insertion.StepId}' would append an unreachable statement " +
                $"after a return in the '{insertion.Branch}' branch of '{insertion.SourceStepId}'.");
        }

        if (insertion.NewStatement is not (
                LocalLiteralDeclarationPatchStatement or
                LocalAssignmentPatchStatement or
                ReturnPatchStatement))
        {
            throw new JsonException(
                $"Branch insertion step '{insertion.StepId}' currently accepts only a " +
                "local-literal declaration, local assignment, or return statement.");
        }

        ValidateOperation(document, new InsertStatementPatchOperation(
            insertion.TargetNodeId,
            null,
            null,
            insertion.NewStatement));
    }

    private static void ValidateReconnection(
        ReconnectValuePatchPlanStep reconnect,
        IReadOnlyDictionary<string, SemanticId> producedTypes)
    {
        ValidateContext(
            reconnect.ExpectedContainingStatementId,
            reconnect.ExpectedParentBlockId,
            reconnect.ExpectedPreviousSiblingId,
            reconnect.ExpectedNextSiblingId,
            reconnect.ExpectedTypeId);
        if (string.IsNullOrWhiteSpace(reconnect.SourceStepId) ||
            !producedTypes.TryGetValue(reconnect.SourceStepId, out SemanticId sourceTypeId))
        {
            throw new JsonException(
                $"Reconnect step '{reconnect.StepId}' must reference an earlier local-producing step.");
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
