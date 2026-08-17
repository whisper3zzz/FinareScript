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
        var loopProducers = new Dictionary<string, LoopProducerState>(StringComparer.Ordinal);
        var paths = new Dictionary<PlanPathKey, PlanPathState>();
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
                InsertBranchStatementPatchPlanStep or
                InsertLoopBodyStatementPatchPlanStep;
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
                            paths);
                    }
                    else if (insertion.NewStatement is WhilePatchStatement)
                    {
                        RegisterLoopProducer(
                            insertion.StepId,
                            insertion.TargetNodeId,
                            loopProducers,
                            paths);
                    }

                    break;
                case InsertBranchStatementPatchPlanStep branchInsertion:
                    ValidateBranchInsertion(
                        document,
                        branchInsertion,
                        branchProducers,
                        paths);
                    var sourcePath = new PlanPathKey(
                        branchInsertion.SourceStepId,
                        branchInsertion.Branch == IfBranchKind.Then
                            ? PlanPathKind.Then
                            : PlanPathKind.Else);
                    paths[sourcePath].LastStatementStepId = branchInsertion.StepId;
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
                            paths);
                    }
                    else if (branchInsertion.NewStatement is WhilePatchStatement)
                    {
                        RegisterLoopProducer(
                            branchInsertion.StepId,
                            branchInsertion.TargetNodeId,
                            loopProducers,
                            paths);
                    }
                    else if (IsTerminalStatement(branchInsertion.NewStatement))
                    {
                        MarkPathTerminated(
                            sourcePath,
                            branchInsertion.StepId,
                            branchProducers,
                            paths);
                    }

                    break;
                case InsertLoopBodyStatementPatchPlanStep loopInsertion:
                    ValidateLoopBodyInsertion(
                        document,
                        loopInsertion,
                        loopProducers,
                        paths);
                    var loopPath = new PlanPathKey(
                        loopInsertion.SourceStepId,
                        PlanPathKind.LoopBody);
                    paths[loopPath].LastStatementStepId = loopInsertion.StepId;
                    if (loopInsertion.NewStatement is LocalLiteralDeclarationPatchStatement loopLocal)
                    {
                        producedTypes.Add(loopInsertion.StepId, loopLocal.ExpectedTypeId);
                    }
                    else if (loopInsertion.NewStatement is IfPatchStatement)
                    {
                        RegisterBranchProducer(
                            loopInsertion.StepId,
                            loopInsertion.TargetNodeId,
                            loopPath,
                            branchProducers,
                            paths);
                    }
                    else if (loopInsertion.NewStatement is WhilePatchStatement)
                    {
                        RegisterLoopProducer(
                            loopInsertion.StepId,
                            loopInsertion.TargetNodeId,
                            loopProducers,
                            paths);
                    }
                    else if (IsTerminalStatement(loopInsertion.NewStatement))
                    {
                        MarkPathTerminated(
                            loopPath,
                            loopInsertion.StepId,
                            branchProducers,
                            paths);
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
        IReadOnlyDictionary<PlanPathKey, PlanPathState> paths)
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

        var sourcePath = new PlanPathKey(
            insertion.SourceStepId,
            insertion.Branch == IfBranchKind.Then ? PlanPathKind.Then : PlanPathKind.Else);
        if (!paths.TryGetValue(sourcePath, out PlanPathState? path))
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
                WhilePatchStatement or
                BreakPatchStatement or
                ContinuePatchStatement or
                ReturnPatchStatement))
        {
            throw new JsonException(
                $"Branch insertion step '{insertion.StepId}' currently accepts only a " +
                "local-literal declaration, local assignment, nested if/while, loop transfer, " +
                "or return statement.");
        }

        ValidateOperation(document, new InsertStatementPatchOperation(
            insertion.TargetNodeId,
            null,
            null,
            insertion.NewStatement));
    }

    private static void ValidateLoopBodyInsertion(
        SemanticPatchPlanDocument document,
        InsertLoopBodyStatementPatchPlanStep insertion,
        IReadOnlyDictionary<string, LoopProducerState> loopProducers,
        IReadOnlyDictionary<PlanPathKey, PlanPathState> paths)
    {
        if (string.IsNullOrWhiteSpace(insertion.SourceStepId) ||
            !loopProducers.TryGetValue(insertion.SourceStepId, out LoopProducerState? source))
        {
            throw new JsonException(
                $"Loop-body insertion step '{insertion.StepId}' must reference an earlier " +
                "while-insertion step.");
        }

        if (source.OriginalTargetNodeId != insertion.TargetNodeId)
        {
            throw new JsonException(
                $"Loop-body insertion step '{insertion.StepId}' and source step " +
                $"'{insertion.SourceStepId}' must use the same original parent-block target.");
        }

        var sourcePath = new PlanPathKey(insertion.SourceStepId, PlanPathKind.LoopBody);
        if (!paths.TryGetValue(sourcePath, out PlanPathState? path))
        {
            throw new JsonException(
                $"Loop-body insertion step '{insertion.StepId}' could not resolve the body of " +
                $"'{insertion.SourceStepId}'.");
        }

        if (path.IsTerminated)
        {
            throw new JsonException(
                $"Loop-body insertion step '{insertion.StepId}' would append an unreachable " +
                $"statement after a terminal body path in '{insertion.SourceStepId}'.");
        }

        if (insertion.NewStatement is not (
                LocalLiteralDeclarationPatchStatement or
                LocalAssignmentPatchStatement or
                IfPatchStatement or
                WhilePatchStatement or
                BreakPatchStatement or
                ContinuePatchStatement or
                ReturnPatchStatement))
        {
            throw new JsonException(
                $"Loop-body insertion step '{insertion.StepId}' has an unsupported statement payload.");
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
        PlanPathKey? parentPath,
        IDictionary<string, BranchProducerState> branchProducers,
        IDictionary<PlanPathKey, PlanPathState> paths)
    {
        branchProducers.Add(
            stepId,
            new BranchProducerState(originalTargetNodeId, parentPath));
        paths.Add(
            new PlanPathKey(stepId, PlanPathKind.Then),
            new PlanPathState());
        paths.Add(
            new PlanPathKey(stepId, PlanPathKind.Else),
            new PlanPathState());
    }

    private static void RegisterLoopProducer(
        string stepId,
        SemanticId originalTargetNodeId,
        IDictionary<string, LoopProducerState> loopProducers,
        IDictionary<PlanPathKey, PlanPathState> paths)
    {
        loopProducers.Add(stepId, new LoopProducerState(originalTargetNodeId));
        paths.Add(
            new PlanPathKey(stepId, PlanPathKind.LoopBody),
            new PlanPathState());
    }

    private static bool IsTerminalStatement(InsertPatchStatement statement) =>
        statement is ReturnPatchStatement or BreakPatchStatement or ContinuePatchStatement;

    private static void MarkPathTerminated(
        PlanPathKey pathKey,
        string terminalStepId,
        IReadOnlyDictionary<string, BranchProducerState> branchProducers,
        IReadOnlyDictionary<PlanPathKey, PlanPathState> paths)
    {
        PlanPathKey currentPathKey = pathKey;
        while (true)
        {
            paths[currentPathKey].IsTerminated = true;
            if (currentPathKey.Kind == PlanPathKind.LoopBody)
            {
                return;
            }

            BranchProducerState producer = branchProducers[currentPathKey.SourceStepId];
            PlanPathState thenPath = paths[
                new PlanPathKey(currentPathKey.SourceStepId, PlanPathKind.Then)];
            PlanPathState elsePath = paths[
                new PlanPathKey(currentPathKey.SourceStepId, PlanPathKind.Else)];
            if (!thenPath.IsTerminated || !elsePath.IsTerminated ||
                producer.ParentPath is not PlanPathKey parentPathKey)
            {
                return;
            }

            PlanPathState parentPath = paths[parentPathKey];
            if (!string.Equals(
                    parentPath.LastStatementStepId,
                    currentPathKey.SourceStepId,
                    StringComparison.Ordinal))
            {
                throw new JsonException(
                    $"Plan step '{terminalStepId}' would make statements appended after nested if " +
                    $"'{currentPathKey.SourceStepId}' unreachable in the " +
                    $"'{parentPathKey.Kind}' path of '{parentPathKey.SourceStepId}'.");
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

    private enum PlanPathKind
    {
        Then,
        Else,
        LoopBody,
    }

    private readonly record struct PlanPathKey(
        string SourceStepId,
        PlanPathKind Kind);

    private sealed record BranchProducerState(
        SemanticId OriginalTargetNodeId,
        PlanPathKey? ParentPath);

    private sealed record LoopProducerState(
        SemanticId OriginalTargetNodeId);

    private sealed class PlanPathState
    {
        public string? LastStatementStepId { get; set; }

        public bool IsTerminated { get; set; }
    }
}
