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
        var branchProducers = new Dictionary<string, BranchProducerState>(StringComparer.Ordinal);
        var branchPaths = new Dictionary<BranchPathKey, BranchPathState>();
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
                        RegisterBranchProducer(
                            insertion.StepId,
                            insertion.TargetNodeId,
                            null,
                            branchProducers,
                            branchPaths);
                    }

                    break;
                case InsertBranchStatementPatchPlanStep branchInsertion:
                    ValidateBranchInsertion(
                        document,
                        branchInsertion,
                        branchProducers,
                        branchPaths);
                    var sourcePath = new BranchPathKey(
                        branchInsertion.SourceStepId,
                        branchInsertion.Branch);
                    branchPaths[sourcePath].LastStatementStepId = branchInsertion.StepId;
                    if (branchInsertion.NewStatement is LocalLiteralDeclarationPatchStatement branchLocal)
                    {
                        producedTypes.Add(branchInsertion.StepId, branchLocal.ExpectedTypeId);
                    }
                    else if (branchInsertion.NewStatement is IfPatchStatement)
                    {
                        RegisterBranchProducer(
                            branchInsertion.StepId,
                            branchInsertion.TargetNodeId,
                            sourcePath,
                            branchProducers,
                            branchPaths);
                    }
                    else if (branchInsertion.NewStatement is ReturnPatchStatement)
                    {
                        MarkBranchTerminated(
                            sourcePath,
                            branchInsertion.StepId,
                            branchProducers,
                            branchPaths);
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
        IReadOnlyDictionary<string, BranchProducerState> branchProducers,
        IReadOnlyDictionary<BranchPathKey, BranchPathState> branchPaths)
    {
        if (string.IsNullOrWhiteSpace(insertion.SourceStepId) ||
            !branchProducers.TryGetValue(
                insertion.SourceStepId,
                out BranchProducerState? source))
        {
            throw new JsonException(
                $"Branch insertion step '{insertion.StepId}' must reference an earlier if-insertion step.");
        }

        if (source.OriginalTargetNodeId != insertion.TargetNodeId)
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

        var sourcePath = new BranchPathKey(insertion.SourceStepId, insertion.Branch);
        if (!branchPaths.TryGetValue(sourcePath, out BranchPathState? path))
        {
            throw new JsonException(
                $"Branch insertion step '{insertion.StepId}' could not resolve the " +
                $"'{insertion.Branch}' path of '{insertion.SourceStepId}'.");
        }

        if (path.IsTerminated)
        {
            throw new JsonException(
                $"Branch insertion step '{insertion.StepId}' would append an unreachable statement " +
                $"after a terminal path in the '{insertion.Branch}' branch of " +
                $"'{insertion.SourceStepId}'.");
        }

        if (insertion.NewStatement is not (
                LocalLiteralDeclarationPatchStatement or
                LocalAssignmentPatchStatement or
                IfPatchStatement or
                ReturnPatchStatement))
        {
            throw new JsonException(
                $"Branch insertion step '{insertion.StepId}' currently accepts only a " +
                "local-literal declaration, local assignment, nested if, or return statement.");
        }

        ValidateOperation(document, new InsertStatementPatchOperation(
            insertion.TargetNodeId,
            null,
            null,
            insertion.NewStatement));
    }

    private static void RegisterBranchProducer(
        string stepId,
        SemanticId originalTargetNodeId,
        BranchPathKey? parentPath,
        IDictionary<string, BranchProducerState> branchProducers,
        IDictionary<BranchPathKey, BranchPathState> branchPaths)
    {
        branchProducers.Add(
            stepId,
            new BranchProducerState(originalTargetNodeId, parentPath));
        branchPaths.Add(
            new BranchPathKey(stepId, IfBranchKind.Then),
            new BranchPathState());
        branchPaths.Add(
            new BranchPathKey(stepId, IfBranchKind.Else),
            new BranchPathState());
    }

    private static void MarkBranchTerminated(
        BranchPathKey pathKey,
        string terminalStepId,
        IReadOnlyDictionary<string, BranchProducerState> branchProducers,
        IReadOnlyDictionary<BranchPathKey, BranchPathState> branchPaths)
    {
        BranchPathKey currentPathKey = pathKey;
        while (true)
        {
            branchPaths[currentPathKey].IsTerminated = true;
            BranchProducerState producer = branchProducers[currentPathKey.SourceStepId];
            BranchPathState thenPath = branchPaths[
                new BranchPathKey(currentPathKey.SourceStepId, IfBranchKind.Then)];
            BranchPathState elsePath = branchPaths[
                new BranchPathKey(currentPathKey.SourceStepId, IfBranchKind.Else)];
            if (!thenPath.IsTerminated || !elsePath.IsTerminated ||
                producer.ParentPath is not BranchPathKey parentPathKey)
            {
                return;
            }

            BranchPathState parentPath = branchPaths[parentPathKey];
            if (!string.Equals(
                    parentPath.LastStatementStepId,
                    currentPathKey.SourceStepId,
                    StringComparison.Ordinal))
            {
                throw new JsonException(
                    $"Plan step '{terminalStepId}' would make statements appended after nested if " +
                    $"'{currentPathKey.SourceStepId}' unreachable in the " +
                    $"'{parentPathKey.Branch}' branch of '{parentPathKey.SourceStepId}'.");
            }

            currentPathKey = parentPathKey;
        }
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

    private readonly record struct BranchPathKey(
        string SourceStepId,
        IfBranchKind Branch);

    private sealed record BranchProducerState(
        SemanticId OriginalTargetNodeId,
        BranchPathKey? ParentPath);

    private sealed class BranchPathState
    {
        public string? LastStatementStepId { get; set; }

        public bool IsTerminated { get; set; }
    }
}
