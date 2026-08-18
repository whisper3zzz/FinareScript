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
        var producedLocals = new Dictionary<string, LocalProducerState>(StringComparer.Ordinal);
        var branchProducers = new Dictionary<string, BranchProducerState>(StringComparer.Ordinal);
        var loopProducers = new Dictionary<string, LoopProducerState>(StringComparer.Ordinal);
        var localInitializerProducers = new Dictionary<string, LocalInitializerProducerState>(
            StringComparer.Ordinal);
        var returnProducers = new Dictionary<string, ReturnProducerState>(StringComparer.Ordinal);
        var expressionProducers = new Dictionary<string, ExpressionProducerState>(
            StringComparer.Ordinal);
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

            bool isRepeatableTarget = step is InsertStatementPatchPlanStep or
                InsertBranchStatementPatchPlanStep or
                InsertLoopBodyStatementPatchPlanStep or
                ReplaceProducedWhileConditionPatchPlanStep or
                ReplaceProducedIfConditionPatchPlanStep or
                ReplaceProducedLocalInitializerPatchPlanStep or
                ReplaceProducedReturnValuePatchPlanStep;
            bool isProducedExpressionTarget =
                step is ReplaceProducedExpressionInputPatchPlanStep producedExpressionInput &&
                expressionProducers.TryGetValue(
                    producedExpressionInput.SourceStepId,
                    out ExpressionProducerState? expressionSource) &&
                expressionSource.OriginalTargetNodeId == step.TargetNodeId;
            if (targetKinds.TryGetValue(step.TargetNodeId, out bool previousWasRepeatable))
            {
                if ((!isRepeatableTarget || !previousWasRepeatable) &&
                    !isProducedExpressionTarget)
                {
                    throw new JsonException(
                        $"Semantic patch plan target '{step.TargetNodeId}' can be repeated only " +
                        "by structural insertion/producer-handle steps.");
                }
            }
            else
            {
                targetKinds.Add(step.TargetNodeId, isRepeatableTarget);
            }

            SemanticPatchPlanStep resolvedStep = ResolveProducedLocalReferences(
                step,
                producedLocals);
            switch (resolvedStep)
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
                case ReplaceExpressionInputPatchPlanStep replaceExpressionInput:
                    ValidateOperation(document, replaceExpressionInput.ToOperation());
                    break;
                case ReplaceProducedExpressionInputPatchPlanStep producedInputReplacement:
                    ValidateProducedExpressionInput(
                        document,
                        producedInputReplacement,
                        expressionProducers);
                    break;
                case ReplaceAssignmentValuePatchPlanStep replaceAssignmentValue:
                    ValidateOperation(document, replaceAssignmentValue.ToOperation());
                    break;
                case ReplaceWhileConditionPatchPlanStep replaceWhileCondition:
                    ValidateOperation(document, replaceWhileCondition.ToOperation());
                    break;
                case ReplaceProducedWhileConditionPatchPlanStep producedCondition:
                    ValidateProducedWhileCondition(
                        document,
                        producedCondition,
                        loopProducers);
                    break;
                case ReplaceIfConditionPatchPlanStep replaceIfCondition:
                    ValidateOperation(document, replaceIfCondition.ToOperation());
                    break;
                case ReplaceProducedIfConditionPatchPlanStep producedIfCondition:
                    ValidateProducedIfCondition(
                        document,
                        producedIfCondition,
                        branchProducers);
                    break;
                case ReplaceLocalInitializerPatchPlanStep replaceLocalInitializer:
                    ValidateOperation(document, replaceLocalInitializer.ToOperation());
                    break;
                case ReplaceProducedLocalInitializerPatchPlanStep producedInitializer:
                    ValidateProducedLocalInitializer(
                        document,
                        producedInitializer,
                        localInitializerProducers);
                    break;
                case ReplaceReturnValuePatchPlanStep replaceReturnValue:
                    ValidateOperation(document, replaceReturnValue.ToOperation());
                    break;
                case ReplaceProducedReturnValuePatchPlanStep producedReturn:
                    ValidateProducedReturnValue(
                        document,
                        producedReturn,
                        returnProducers);
                    break;
                case InsertStatementPatchPlanStep insertion:
                    ValidateOperation(document, insertion.ToOperation());
                    if (insertion.NewStatement is LocalLiteralDeclarationPatchStatement insertedLocal)
                    {
                        producedLocals.Add(
                            insertion.StepId,
                            new LocalProducerState(insertedLocal.Name, insertedLocal.ExpectedTypeId));
                        RegisterLocalInitializerProducer(
                            insertion.StepId,
                            insertion.TargetNodeId,
                            insertedLocal,
                            localInitializerProducers);
                    }
                    else if (insertion.NewStatement is IfPatchStatement insertedIf)
                    {
                        RegisterBranchProducer(
                            insertion.StepId,
                            insertion.TargetNodeId,
                            insertedIf.Condition.ExpectedTypeId,
                            null,
                            branchProducers,
                            paths);
                    }
                    else if (insertion.NewStatement is WhilePatchStatement insertedLoop)
                    {
                        RegisterLoopProducer(
                            insertion.StepId,
                            insertion.TargetNodeId,
                            insertedLoop.Condition.ExpectedTypeId,
                            loopProducers,
                            paths);
                    }
                    else if (insertion.NewStatement is ReturnPatchStatement
                    {
                        Expression: not null,
                    } insertedReturn)
                    {
                        RegisterReturnProducer(
                            insertion.StepId,
                            insertion.TargetNodeId,
                            insertedReturn,
                            returnProducers);
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
                        producedLocals.Add(
                            branchInsertion.StepId,
                            new LocalProducerState(branchLocal.Name, branchLocal.ExpectedTypeId));
                        RegisterLocalInitializerProducer(
                            branchInsertion.StepId,
                            branchInsertion.TargetNodeId,
                            branchLocal,
                            localInitializerProducers);
                    }
                    else if (branchInsertion.NewStatement is IfPatchStatement insertedBranchIf)
                    {
                        RegisterBranchProducer(
                            branchInsertion.StepId,
                            branchInsertion.TargetNodeId,
                            insertedBranchIf.Condition.ExpectedTypeId,
                            sourcePath,
                            branchProducers,
                            paths);
                    }
                    else if (branchInsertion.NewStatement is WhilePatchStatement insertedBranchLoop)
                    {
                        RegisterLoopProducer(
                            branchInsertion.StepId,
                            branchInsertion.TargetNodeId,
                            insertedBranchLoop.Condition.ExpectedTypeId,
                            loopProducers,
                            paths);
                    }
                    else if (branchInsertion.NewStatement is ReturnPatchStatement
                    {
                        Expression: not null,
                    } branchReturn)
                    {
                        RegisterReturnProducer(
                            branchInsertion.StepId,
                            branchInsertion.TargetNodeId,
                            branchReturn,
                            returnProducers);
                        MarkPathTerminated(
                            sourcePath,
                            branchInsertion.StepId,
                            branchProducers,
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
                        producedLocals.Add(
                            loopInsertion.StepId,
                            new LocalProducerState(loopLocal.Name, loopLocal.ExpectedTypeId));
                        RegisterLocalInitializerProducer(
                            loopInsertion.StepId,
                            loopInsertion.TargetNodeId,
                            loopLocal,
                            localInitializerProducers);
                    }
                    else if (loopInsertion.NewStatement is IfPatchStatement insertedLoopIf)
                    {
                        RegisterBranchProducer(
                            loopInsertion.StepId,
                            loopInsertion.TargetNodeId,
                            insertedLoopIf.Condition.ExpectedTypeId,
                            loopPath,
                            branchProducers,
                            paths);
                    }
                    else if (loopInsertion.NewStatement is WhilePatchStatement insertedNestedLoop)
                    {
                        RegisterLoopProducer(
                            loopInsertion.StepId,
                            loopInsertion.TargetNodeId,
                            insertedNestedLoop.Condition.ExpectedTypeId,
                            loopProducers,
                            paths);
                    }
                    else if (loopInsertion.NewStatement is ReturnPatchStatement
                    {
                        Expression: not null,
                    } loopReturn)
                    {
                        RegisterReturnProducer(
                            loopInsertion.StepId,
                            loopInsertion.TargetNodeId,
                            loopReturn,
                            returnProducers);
                        MarkPathTerminated(
                            loopPath,
                            loopInsertion.StepId,
                            branchProducers,
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
                    producedLocals.Add(
                        introduction.StepId,
                        new LocalProducerState(introduction.Name, introduction.ExpectedTypeId));
                    break;
                case ReconnectValuePatchPlanStep reconnect:
                    ValidateReconnection(reconnect, producedLocals);
                    break;
                default:
                    throw new JsonException(
                        $"Unsupported semantic patch plan step '{step.GetType().Name}'.");
            }

            PatchExpression? authoredExpression =
                SemanticPatchPlanExpressions.GetAuthoredExpression(resolvedStep);
            if (authoredExpression is not null)
            {
                expressionProducers.Add(
                    step.StepId,
                    new ExpressionProducerState(step.TargetNodeId, authoredExpression));
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

    private static void ValidateProducedExpressionInput(
        SemanticPatchPlanDocument document,
        ReplaceProducedExpressionInputPatchPlanStep replacement,
        IReadOnlyDictionary<string, ExpressionProducerState> producers)
    {
        if (string.IsNullOrWhiteSpace(replacement.SourceStepId) ||
            !producers.TryGetValue(replacement.SourceStepId, out ExpressionProducerState? source))
        {
            throw new JsonException(
                $"Produced-expression input step '{replacement.StepId}' must reference an " +
                "earlier expression-producing step.");
        }

        if (source.OriginalTargetNodeId != replacement.TargetNodeId)
        {
            throw new JsonException(
                $"Produced-expression input step '{replacement.StepId}' and source step " +
                $"'{replacement.SourceStepId}' must use the same original structural target.");
        }

        if (replacement.TargetPath is null)
        {
            throw new JsonException(
                $"Produced-expression input step '{replacement.StepId}' requires a target path.");
        }

        PatchExpression target = source.Root;
        foreach (string? pathPort in replacement.TargetPath)
        {
            if (!TryGetPatchExpressionInput(target, pathPort, out PatchExpression? child))
            {
                throw new JsonException(
                    $"Produced-expression input step '{replacement.StepId}' cannot follow " +
                    $"target-path port '{pathPort ?? "<null>"}' through the authored " +
                    $"'{target.GetType().Name}' expression.");
            }

            target = child!;
        }

        if (!TryGetPatchExpressionInput(target, replacement.InputPort, out PatchExpression? input))
        {
            throw new JsonException(
                $"Produced-expression input step '{replacement.StepId}' cannot resolve input " +
                $"port '{replacement.InputPort}' on the selected authored expression.");
        }

        if (replacement.ExpectedTargetTypeId != target.ExpectedTypeId ||
            replacement.ExpectedInputTypeId != input!.ExpectedTypeId)
        {
            throw new JsonException(
                $"Produced-expression input step '{replacement.StepId}' must exactly match the " +
                "selected authored target and input semantic types.");
        }

        ValidateOperation(document, new ReplaceExpressionInputPatchOperation(
            SemanticId.FromKey($"patch-plan-produced-expression-target:{replacement.StepId}"),
            replacement.InputPort,
            SemanticId.FromKey($"patch-plan-produced-expression-input:{replacement.StepId}"),
            replacement.ExpectedInputTypeId,
            replacement.ExpectedTargetTypeId,
            replacement.NewInput));
    }

    private static bool TryGetPatchExpressionInput(
        PatchExpression target,
        string? inputPort,
        out PatchExpression? input)
    {
        input = (target, inputPort) switch
        {
            (BinaryPatchExpression binary, "left") => binary.Left,
            (BinaryPatchExpression binary, "right") => binary.Right,
            (UnaryPatchExpression unary, "operand") => unary.Operand,
            (CastPatchExpression cast, "operand") => cast.Operand,
            _ => null,
        };
        return input is not null;
    }

    private static SemanticPatchPlanStep ResolveProducedLocalReferences(
        SemanticPatchPlanStep step,
        IReadOnlyDictionary<string, LocalProducerState> producedLocals)
    {
        IReadOnlyList<ProducedLocalReferencePatchExpression> references =
            SemanticPatchPlanExpressions.FindProducedLocalReferences(step);
        foreach (ProducedLocalReferencePatchExpression reference in references)
        {
            if (string.IsNullOrWhiteSpace(reference.SourceStepId) ||
                !producedLocals.TryGetValue(reference.SourceStepId, out LocalProducerState? source))
            {
                throw new JsonException(
                    $"Plan step '{step.StepId}' contains a produced-local reference that must " +
                    "name an earlier local-producing step.");
            }

            if (!string.Equals(reference.ExpectedName, source.Name, StringComparison.Ordinal) ||
                reference.ExpectedTypeId != source.TypeId)
            {
                throw new JsonException(
                    $"Plan step '{step.StepId}' and local source step " +
                    $"'{reference.SourceStepId}' must declare the same name and semantic type ID.");
            }
        }

        return SemanticPatchPlanExpressions.ResolveProducedLocalReferences(
            step,
            reference =>
            {
                LocalProducerState source = producedLocals[reference.SourceStepId];
                return new LocalReferencePatchExpression(
                    SemanticId.FromKey($"patch-plan-produced-local:{reference.SourceStepId}"),
                    source.Name,
                    source.TypeId);
            });
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

    private static void ValidateProducedWhileCondition(
        SemanticPatchPlanDocument document,
        ReplaceProducedWhileConditionPatchPlanStep replacement,
        IReadOnlyDictionary<string, LoopProducerState> loopProducers)
    {
        if (string.IsNullOrWhiteSpace(replacement.SourceStepId) ||
            !loopProducers.TryGetValue(replacement.SourceStepId, out LoopProducerState? source))
        {
            throw new JsonException(
                $"Produced-while condition step '{replacement.StepId}' must reference an earlier " +
                "while-insertion step.");
        }

        if (source.OriginalTargetNodeId != replacement.TargetNodeId)
        {
            throw new JsonException(
                $"Produced-while condition step '{replacement.StepId}' and source step " +
                $"'{replacement.SourceStepId}' must use the same original parent-block target.");
        }

        if (replacement.NewCondition is null ||
            replacement.NewCondition.ExpectedTypeId != source.ConditionTypeId)
        {
            throw new JsonException(
                $"Produced-while condition step '{replacement.StepId}' must preserve the source " +
                "while condition's declared semantic type.");
        }

        ValidateOperation(document, new InsertStatementPatchOperation(
            replacement.TargetNodeId,
            null,
            null,
            new WhilePatchStatement(replacement.NewCondition)));
    }

    private static void ValidateProducedIfCondition(
        SemanticPatchPlanDocument document,
        ReplaceProducedIfConditionPatchPlanStep replacement,
        IReadOnlyDictionary<string, BranchProducerState> branchProducers)
    {
        if (string.IsNullOrWhiteSpace(replacement.SourceStepId) ||
            !branchProducers.TryGetValue(replacement.SourceStepId, out BranchProducerState? source))
        {
            throw new JsonException(
                $"Produced-if condition step '{replacement.StepId}' must reference an earlier " +
                "if-insertion step.");
        }

        if (source.OriginalTargetNodeId != replacement.TargetNodeId)
        {
            throw new JsonException(
                $"Produced-if condition step '{replacement.StepId}' and source step " +
                $"'{replacement.SourceStepId}' must use the same original parent-block target.");
        }

        if (replacement.NewCondition is null ||
            replacement.NewCondition.ExpectedTypeId != source.ConditionTypeId)
        {
            throw new JsonException(
                $"Produced-if condition step '{replacement.StepId}' must preserve the source " +
                "if condition's declared semantic type.");
        }

        ValidateOperation(document, new ReplaceIfConditionPatchOperation(
            replacement.TargetNodeId,
            SemanticId.FromKey("produced-if-condition-endpoint"),
            source.ConditionTypeId,
            replacement.NewCondition));
    }

    private static void ValidateProducedLocalInitializer(
        SemanticPatchPlanDocument document,
        ReplaceProducedLocalInitializerPatchPlanStep replacement,
        IReadOnlyDictionary<string, LocalInitializerProducerState> producers)
    {
        if (string.IsNullOrWhiteSpace(replacement.SourceStepId) ||
            !producers.TryGetValue(
                replacement.SourceStepId,
                out LocalInitializerProducerState? source))
        {
            throw new JsonException(
                $"Produced-local initializer step '{replacement.StepId}' must reference an earlier " +
                "local-literal insertion step.");
        }

        if (source.OriginalTargetNodeId != replacement.TargetNodeId)
        {
            throw new JsonException(
                $"Produced-local initializer step '{replacement.StepId}' and source step " +
                $"'{replacement.SourceStepId}' must use the same original structural target.");
        }

        if (replacement.NewInitializer is null ||
            replacement.NewInitializer.ExpectedTypeId != source.TypeId)
        {
            throw new JsonException(
                $"Produced-local initializer step '{replacement.StepId}' must preserve the source " +
                "local's declared semantic type.");
        }

        ValidateOperation(document, new ReplaceLocalInitializerPatchOperation(
            replacement.TargetNodeId,
            source.Name,
            SemanticId.FromKey("produced-local-initializer-endpoint"),
            source.TypeId,
            replacement.NewInitializer));
    }

    private static void ValidateProducedReturnValue(
        SemanticPatchPlanDocument document,
        ReplaceProducedReturnValuePatchPlanStep replacement,
        IReadOnlyDictionary<string, ReturnProducerState> producers)
    {
        if (string.IsNullOrWhiteSpace(replacement.SourceStepId) ||
            !producers.TryGetValue(replacement.SourceStepId, out ReturnProducerState? source))
        {
            throw new JsonException(
                $"Produced-return value step '{replacement.StepId}' must reference an earlier " +
                "value-return insertion step.");
        }

        if (source.OriginalTargetNodeId != replacement.TargetNodeId)
        {
            throw new JsonException(
                $"Produced-return value step '{replacement.StepId}' and source step " +
                $"'{replacement.SourceStepId}' must use the same original structural target.");
        }

        if (replacement.NewValue is null ||
            replacement.NewValue.ExpectedTypeId != source.ReturnTypeId)
        {
            throw new JsonException(
                $"Produced-return value step '{replacement.StepId}' must preserve the source " +
                "return's declared semantic type.");
        }

        ValidateOperation(document, new ReplaceReturnValuePatchOperation(
            replacement.TargetNodeId,
            SemanticId.FromKey("produced-return-value-endpoint"),
            source.ReturnTypeId,
            replacement.NewValue));
    }

    private static void RegisterBranchProducer(
        string stepId,
        SemanticId originalTargetNodeId,
        SemanticId conditionTypeId,
        PlanPathKey? parentPath,
        IDictionary<string, BranchProducerState> branchProducers,
        IDictionary<PlanPathKey, PlanPathState> paths)
    {
        branchProducers.Add(
            stepId,
            new BranchProducerState(originalTargetNodeId, conditionTypeId, parentPath));
        paths.Add(
            new PlanPathKey(stepId, PlanPathKind.Then),
            new PlanPathState());
        paths.Add(
            new PlanPathKey(stepId, PlanPathKind.Else),
            new PlanPathState());
    }

    private static void RegisterLocalInitializerProducer(
        string stepId,
        SemanticId originalTargetNodeId,
        LocalLiteralDeclarationPatchStatement local,
        IDictionary<string, LocalInitializerProducerState> producers) => producers.Add(
        stepId,
        new LocalInitializerProducerState(
            originalTargetNodeId,
            local.Name,
            local.ExpectedTypeId));

    private static void RegisterReturnProducer(
        string stepId,
        SemanticId originalTargetNodeId,
        ReturnPatchStatement returnStatement,
        IDictionary<string, ReturnProducerState> producers) => producers.Add(
        stepId,
        new ReturnProducerState(
            originalTargetNodeId,
            returnStatement.ExpectedReturnTypeId));

    private static void RegisterLoopProducer(
        string stepId,
        SemanticId originalTargetNodeId,
        SemanticId conditionTypeId,
        IDictionary<string, LoopProducerState> loopProducers,
        IDictionary<PlanPathKey, PlanPathState> paths)
    {
        loopProducers.Add(
            stepId,
            new LoopProducerState(originalTargetNodeId, conditionTypeId));
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
        IReadOnlyDictionary<string, LocalProducerState> producedLocals)
    {
        ValidateContext(
            reconnect.ExpectedContainingStatementId,
            reconnect.ExpectedParentBlockId,
            reconnect.ExpectedPreviousSiblingId,
            reconnect.ExpectedNextSiblingId,
            reconnect.ExpectedTypeId);
        if (string.IsNullOrWhiteSpace(reconnect.SourceStepId) ||
            !producedLocals.TryGetValue(reconnect.SourceStepId, out LocalProducerState? source))
        {
            throw new JsonException(
                $"Reconnect step '{reconnect.StepId}' must reference an earlier local-producing step.");
        }

        if (source.TypeId != reconnect.ExpectedTypeId)
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
        SemanticId ConditionTypeId,
        PlanPathKey? ParentPath);

    private sealed record LoopProducerState(
        SemanticId OriginalTargetNodeId,
        SemanticId ConditionTypeId);

    private sealed record LocalInitializerProducerState(
        SemanticId OriginalTargetNodeId,
        string Name,
        SemanticId TypeId);

    private sealed record ReturnProducerState(
        SemanticId OriginalTargetNodeId,
        SemanticId ReturnTypeId);

    private sealed record LocalProducerState(
        string Name,
        SemanticId TypeId);

    private sealed record ExpressionProducerState(
        SemanticId OriginalTargetNodeId,
        PatchExpression Root);

    private sealed class PlanPathState
    {
        public string? LastStatementStepId { get; set; }

        public bool IsTerminated { get; set; }
    }
}
