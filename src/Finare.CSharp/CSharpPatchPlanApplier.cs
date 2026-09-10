using Finare.Core;
using Finare.Core.Hir;
using Finare.Semantics;

namespace Finare.CSharp;

public sealed class CSharpPatchPlanApplier
{
    private readonly CSharpSemanticPipeline pipeline = new();
    private readonly CSharpSourcePatchApplier stageApplier = new();

    public CSharpPatchResult Apply(
        CSharpCompilationRequest request,
        SemanticPatchPlanDocument plan)
    {
        ArgumentNullException.ThrowIfNull(request);
        SemanticPatchPlanJson.Validate(plan);

        CSharpAnalysisResult original = pipeline.Analyze(request);
        if (!original.Succeeded || original.Hir is null || original.Semantics is null)
        {
            return Failure(original.Diagnostics);
        }

        if (plan.SourceCompilationId != original.Hir.Id)
        {
            return Failure(new UnifiedDiagnostic(
                SemanticPatchDiagnosticCodes.BaselineMismatch,
                UnifiedDiagnosticSeverity.Error,
                $"Patch-plan baseline '{plan.SourceCompilationId}' does not match current source " +
                $"compilation '{original.Hir.Id}'. Re-analyze before applying the plan.",
                SourceSpan.Unknown("<patch-plan>")));
        }

        if (plan.SemanticConfigurationId != original.Semantics.ConfigurationId)
        {
            return Failure(new UnifiedDiagnostic(
                SemanticPatchDiagnosticCodes.ConfigurationMismatch,
                UnifiedDiagnosticSeverity.Error,
                $"Patch-plan semantic configuration '{plan.SemanticConfigurationId}' does not match " +
                $"current configuration '{original.Semantics.ConfigurationId}'.",
                SourceSpan.Unknown("<patch-plan>")));
        }

        IReadOnlyList<UnifiedDiagnostic>? initialFailures = ValidateInitialContexts(
            request,
            original,
            plan);
        if (initialFailures is not null)
        {
            return Failure(initialFailures);
        }

        CSharpCompilationRequest currentRequest = request;
        var producedDefinitions = new Dictionary<string, SemanticId>(StringComparer.Ordinal);
        var producedBranches = new Dictionary<string, ProducedIfBranches>(StringComparer.Ordinal);
        var producedLoops = new Dictionary<string, ProducedLoop>(StringComparer.Ordinal);
        var producedReturns = new Dictionary<string, SemanticId>(StringComparer.Ordinal);
        var producedExpressions = new Dictionary<string, SemanticId>(StringComparer.Ordinal);
        CSharpPatchResult? lastResult = null;
        foreach (SemanticPatchPlanStep step in plan.Steps)
        {
            CSharpAnalysisResult current = pipeline.Analyze(currentRequest);
            if (!current.Succeeded || current.Hir is null || current.Semantics is null)
            {
                return Failure(current.Diagnostics);
            }

            if (!TryCreateCurrentOperation(
                    step,
                    current,
                    producedDefinitions,
                    producedBranches,
                    producedLoops,
                    producedReturns,
                    producedExpressions,
                    out SemanticPatchOperation? operation,
                    out UnifiedDiagnostic? operationFailure))
            {
                return Failure(operationFailure!);
            }

            SemanticPatchDocument stagePatch = SemanticPatchDocument.Create(
                current.Hir.Id,
                current.Semantics.ConfigurationId,
                [operation!]);
            CSharpPatchResult stageResult = stageApplier.Apply(currentRequest, stagePatch);
            if (!stageResult.Succeeded ||
                stageResult.PatchedRequest is null ||
                stageResult.Analysis?.Hir is null)
            {
                return Failure(stageResult.Diagnostics);
            }

            HirStatement? insertedStatement = operation is InsertStatementPatchOperation insertionOperation
                ? FindInsertedStatement(stageResult.Analysis.Hir, insertionOperation)
                : null;
            if (ProducesBranches(step))
            {
                if (insertedStatement is not HirIfStatement { Else: not null } insertedIf)
                {
                    return Failure(new UnifiedDiagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        UnifiedDiagnosticSeverity.Error,
                        $"Plan step '{step.StepId}' succeeded but its produced branch blocks could " +
                        "not be resolved for later steps.",
                        SourceSpan.Unknown("<patch-plan>"),
                        step.TargetNodeId));
                }

                producedBranches.Add(
                    step.StepId,
                    new ProducedIfBranches(insertedIf.Id, insertedIf.Then.Id, insertedIf.Else.Id));
            }

            if (ProducesLoop(step))
            {
                ProducedLoop? producedLoop = insertedStatement switch
                {
                    HirWhileStatement insertedWhile => new ProducedLoop(
                        insertedWhile.Id,
                        insertedWhile.Body.Id),
                    HirDoWhileStatement insertedDoWhile => new ProducedLoop(
                        insertedDoWhile.Id,
                        insertedDoWhile.Body.Id),
                    HirForStatement insertedFor => new ProducedLoop(
                        insertedFor.Id,
                        insertedFor.Body.Id),
                    HirForEachStatement insertedForEach => new ProducedLoop(
                        insertedForEach.Id,
                        insertedForEach.Body.Id),
                    _ => null,
                };
                if (producedLoop is null)
                {
                    return Failure(new UnifiedDiagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        UnifiedDiagnosticSeverity.Error,
                        $"Plan step '{step.StepId}' succeeded but its produced loop body could " +
                        "not be resolved for later steps.",
                        SourceSpan.Unknown("<patch-plan>"),
                        step.TargetNodeId));
                }

                producedLoops.Add(step.StepId, producedLoop.Value);
            }

            if (ProducesReturnValue(step))
            {
                if (insertedStatement is not HirReturnStatement { Expression: not null } insertedReturn)
                {
                    return Failure(new UnifiedDiagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        UnifiedDiagnosticSeverity.Error,
                        $"Plan step '{step.StepId}' succeeded but its produced return value could " +
                        "not be resolved for later steps.",
                        SourceSpan.Unknown("<patch-plan>"),
                        step.TargetNodeId));
                }

                producedReturns.Add(step.StepId, insertedReturn.Id);
            }

            HirNode? introduced = step switch
            {
                IntroduceTemporaryPatchPlanStep introduction => FindIntroducedDefinition(
                    stageResult.Analysis.Hir,
                    introduction),
                InsertStatementPatchPlanStep or
                    InsertBranchStatementPatchPlanStep or
                    InsertLoopBodyStatementPatchPlanStep =>
                    GetInsertedDefinition(insertedStatement),
                _ => null,
            };
            if (ProducesLocal(step))
            {
                if (introduced is null)
                {
                    return Failure(new UnifiedDiagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        UnifiedDiagnosticSeverity.Error,
                        $"Plan step '{step.StepId}' succeeded but its produced local could not be " +
                        "resolved for later steps.",
                        SourceSpan.Unknown("<patch-plan>"),
                        step.TargetNodeId));
                }

                producedDefinitions.Add(step.StepId, introduced.Id);
            }

            if (SemanticPatchPlanExpressions.GetAuthoredExpression(step) is not null)
            {
                HirExpression? producedExpression = FindProducedExpression(
                    stageResult.Analysis.Hir,
                    operation!,
                    insertedStatement);
                if (producedExpression is null)
                {
                    return Failure(new UnifiedDiagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        UnifiedDiagnosticSeverity.Error,
                        $"Plan step '{step.StepId}' succeeded but its produced expression root " +
                        "could not be resolved for later steps.",
                        SourceSpan.Unknown("<patch-plan>"),
                        step.TargetNodeId));
                }

                producedExpressions.Add(step.StepId, producedExpression.Id);
            }

            currentRequest = AdvanceRequest(
                stageResult.PatchedRequest,
                stageResult.Analysis);
            lastResult = new CSharpPatchResult(
                currentRequest,
                stageResult.Analysis,
                stageResult.Diagnostics);
        }

        return lastResult ?? throw new InvalidOperationException(
            "A validated semantic patch plan completed without executing any steps.");
    }

    private IReadOnlyList<UnifiedDiagnostic>? ValidateInitialContexts(
        CSharpCompilationRequest request,
        CSharpAnalysisResult analysis,
        SemanticPatchPlanDocument plan)
    {
        HirCompilationUnit compilation = analysis.Hir ??
            throw new ArgumentException("Initial plan validation requires HIR.", nameof(analysis));
        BoundCompilation semantics = analysis.Semantics ??
            throw new ArgumentException("Initial plan validation requires bound semantics.", nameof(analysis));
        IReadOnlyDictionary<SemanticId, HirNode> nodes = compilation
            .DescendantsAndSelf()
            .ToDictionary(static node => node.Id);
        var patchIndex = new HirPatchIndex(compilation);
        var producerMethods = new Dictionary<string, SemanticId>(StringComparer.Ordinal);
        var branchProducerMethods = new Dictionary<string, SemanticId>(StringComparer.Ordinal);
        var loopProducerMethods = new Dictionary<string, SemanticId>(StringComparer.Ordinal);
        var localInitializerProducerMethods = new Dictionary<string, SemanticId>(StringComparer.Ordinal);
        var returnProducerMethods = new Dictionary<string, SemanticId>(StringComparer.Ordinal);
        var expressionProducerMethods = new Dictionary<string, SemanticId>(StringComparer.Ordinal);
        foreach (SemanticPatchPlanStep step in plan.Steps)
        {
            if (!nodes.TryGetValue(step.TargetNodeId, out HirNode? target))
            {
                return [Diagnostic(
                    SemanticPatchDiagnosticCodes.TargetNotFound,
                    $"Patch-plan target '{step.TargetNodeId}' was not found in the initial HIR.",
                    SourceSpan.Unknown("<patch-plan>"),
                    step.TargetNodeId)];
            }

            if (!TryGetStepMethod(step, target, patchIndex, out HirMethodDeclaration? method))
            {
                return [Diagnostic(
                    SemanticPatchDiagnosticCodes.TargetKindMismatch,
                    $"Patch-plan target '{step.TargetNodeId}' does not have the structure required " +
                    $"by step '{step.StepId}'.",
                    target.Span,
                    target.Id)];
            }

            CSharpSourceDocument source = request.Sources.Single(
                candidate => candidate.LogicalPath == target.Span.FilePath);
            if (source.IdentitySidecar is null)
            {
                return [Diagnostic(
                    SemanticPatchDiagnosticCodes.PlanRequiresPersistentIdentity,
                    "A multi-stage patch plan requires a persistent identity sidecar on every " +
                    "target document so later stages can resolve nodes after source spans change.",
                    target.Span,
                    target.Id)];
            }

            IReadOnlyList<ProducedLocalReferencePatchExpression> producedReferences =
                SemanticPatchPlanExpressions.FindProducedLocalReferences(step);
            foreach (ProducedLocalReferencePatchExpression reference in producedReferences)
            {
                if (!producerMethods.TryGetValue(
                        reference.SourceStepId,
                        out SemanticId valueProducerMethodId) ||
                    valueProducerMethodId != method!.Id)
                {
                    return [Diagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        $"Plan step '{step.StepId}' and produced-local source step " +
                        $"'{reference.SourceStepId}' target different methods.",
                        target.Span,
                        target.Id)];
                }
            }

            if (step is ReconnectValuePatchPlanStep reconnect)
            {
                UnifiedDiagnostic? reconnectFailure = ValidateInitialReconnection(
                    reconnect,
                    target,
                    semantics,
                    patchIndex,
                    producerMethods,
                    method!);
                if (reconnectFailure is not null)
                {
                    return [reconnectFailure];
                }
            }
            else if (step is InsertBranchStatementPatchPlanStep branchInsertion)
            {
                if (!branchProducerMethods.TryGetValue(
                        branchInsertion.SourceStepId,
                        out SemanticId branchProducerMethodId) ||
                    branchProducerMethodId != method!.Id)
                {
                    return [Diagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        $"Branch insertion step '{branchInsertion.StepId}' and source step " +
                        $"'{branchInsertion.SourceStepId}' target different methods.",
                        target.Span,
                        target.Id)];
                }
            }
            else if (step is InsertLoopBodyStatementPatchPlanStep loopInsertion)
            {
                if (!loopProducerMethods.TryGetValue(
                        loopInsertion.SourceStepId,
                        out SemanticId loopProducerMethodId) ||
                    loopProducerMethodId != method!.Id)
                {
                    return [Diagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        $"Loop-body insertion step '{loopInsertion.StepId}' and source step " +
                        $"'{loopInsertion.SourceStepId}' target different methods.",
                        target.Span,
                        target.Id)];
                }
            }
            else if (step is ReplaceProducedWhileConditionPatchPlanStep producedCondition)
            {
                if (!loopProducerMethods.TryGetValue(
                        producedCondition.SourceStepId,
                        out SemanticId loopProducerMethodId) ||
                    loopProducerMethodId != method!.Id)
                {
                    return [Diagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        $"Produced-while condition step '{producedCondition.StepId}' and source " +
                        $"step '{producedCondition.SourceStepId}' target different methods.",
                        target.Span,
                        target.Id)];
                }
            }
            else if (step is ReplaceProducedDoWhileConditionPatchPlanStep producedDoCondition)
            {
                if (!loopProducerMethods.TryGetValue(
                        producedDoCondition.SourceStepId,
                        out SemanticId loopProducerMethodId) ||
                    loopProducerMethodId != method!.Id)
                {
                    return [Diagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        $"Produced-do-while condition step '{producedDoCondition.StepId}' and " +
                        $"source step '{producedDoCondition.SourceStepId}' target different methods.",
                        target.Span,
                        target.Id)];
                }
            }
            else if (step is ReplaceProducedForConditionPatchPlanStep producedForCondition)
            {
                if (!loopProducerMethods.TryGetValue(
                        producedForCondition.SourceStepId,
                        out SemanticId loopProducerMethodId) ||
                    loopProducerMethodId != method!.Id)
                {
                    return [Diagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        $"Produced-for condition step '{producedForCondition.StepId}' and source " +
                        $"step '{producedForCondition.SourceStepId}' target different methods.",
                        target.Span,
                        target.Id)];
                }
            }
            else if (step is ReplaceProducedForEachCollectionPatchPlanStep producedForEachCollection)
            {
                if (!loopProducerMethods.TryGetValue(
                        producedForEachCollection.SourceStepId,
                        out SemanticId loopProducerMethodId) ||
                    loopProducerMethodId != method!.Id)
                {
                    return [Diagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        $"Produced-foreach collection step '{producedForEachCollection.StepId}' " +
                        $"and source step '{producedForEachCollection.SourceStepId}' target " +
                        "different methods.",
                        target.Span,
                        target.Id)];
                }
            }
            else if (step is ReplaceProducedIfConditionPatchPlanStep producedIfCondition)
            {
                if (!branchProducerMethods.TryGetValue(
                        producedIfCondition.SourceStepId,
                        out SemanticId branchProducerMethodId) ||
                    branchProducerMethodId != method!.Id)
                {
                    return [Diagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        $"Produced-if condition step '{producedIfCondition.StepId}' and source " +
                        $"step '{producedIfCondition.SourceStepId}' target different methods.",
                        target.Span,
                        target.Id)];
                }
            }
            else if (step is ReplaceProducedLocalInitializerPatchPlanStep producedInitializer)
            {
                if (!localInitializerProducerMethods.TryGetValue(
                        producedInitializer.SourceStepId,
                        out SemanticId localProducerMethodId) ||
                    localProducerMethodId != method!.Id)
                {
                    return [Diagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        $"Produced-local initializer step '{producedInitializer.StepId}' and " +
                        $"source step '{producedInitializer.SourceStepId}' target different methods.",
                        target.Span,
                        target.Id)];
                }
            }
            else if (step is ReplaceProducedReturnValuePatchPlanStep producedReturn)
            {
                if (!returnProducerMethods.TryGetValue(
                        producedReturn.SourceStepId,
                        out SemanticId returnProducerMethodId) ||
                    returnProducerMethodId != method!.Id)
                {
                    return [Diagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        $"Produced-return value step '{producedReturn.StepId}' and source step " +
                        $"'{producedReturn.SourceStepId}' target different methods.",
                        target.Span,
                        target.Id)];
                }
            }
            else if (step is ReplaceProducedExpressionInputPatchPlanStep producedExpressionInput)
            {
                if (!expressionProducerMethods.TryGetValue(
                        producedExpressionInput.SourceStepId,
                        out SemanticId expressionProducerMethodId) ||
                    expressionProducerMethodId != method!.Id)
                {
                    return [Diagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        $"Produced-expression input step '{producedExpressionInput.StepId}' and " +
                        $"source step '{producedExpressionInput.SourceStepId}' target different methods.",
                        target.Span,
                        target.Id)];
                }
            }
            // A produced value does not exist in the external baseline. Its method relationship
            // was checked above; the complete operation is validated against the reanalyzed
            // in-memory source when that stage executes.
            else if (producedReferences.Count == 0)
            {
                SemanticPatchOperation initialOperation = GetInitialOperation(step);
                CSharpPatchResult validationResult = stageApplier.Apply(
                    request,
                    SemanticPatchDocument.Create(
                        compilation.Id,
                        semantics.ConfigurationId,
                        [initialOperation]));
                if (!validationResult.Succeeded)
                {
                    return validationResult.Diagnostics;
                }
            }

            if (ProducesLocal(step))
            {
                producerMethods.Add(step.StepId, method!.Id);
            }

            if (ProducesBranches(step))
            {
                branchProducerMethods.Add(step.StepId, method!.Id);
            }

            if (ProducesLoop(step))
            {
                loopProducerMethods.Add(step.StepId, method!.Id);
            }

            if (ProducesLocalInitializer(step))
            {
                localInitializerProducerMethods.Add(step.StepId, method!.Id);
            }

            if (ProducesReturnValue(step))
            {
                returnProducerMethods.Add(step.StepId, method!.Id);
            }

            if (SemanticPatchPlanExpressions.GetAuthoredExpression(step) is not null)
            {
                expressionProducerMethods.Add(step.StepId, method!.Id);
            }
        }

        return null;
    }

    private static UnifiedDiagnostic? ValidateInitialReconnection(
        ReconnectValuePatchPlanStep reconnect,
        HirNode target,
        BoundCompilation semantics,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<string, SemanticId> producerMethods,
        HirMethodDeclaration method)
    {
        if (target is not HirExpression expression ||
            !patchIndex.TryGetExpression(
                expression.Id,
                out HirPatchIndex.ExpressionLocation? expressionLocation))
        {
            return Diagnostic(
                SemanticPatchDiagnosticCodes.TargetKindMismatch,
                $"Patch-plan target '{reconnect.TargetNodeId}' is not an indexed expression.",
                target.Span,
                target.Id);
        }

        if (expressionLocation!.ContainingStatement.Id != reconnect.ExpectedContainingStatementId)
        {
            return Diagnostic(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Plan step '{reconnect.StepId}' expected containing statement " +
                $"'{reconnect.ExpectedContainingStatementId}', but its target is inside " +
                $"'{expressionLocation.ContainingStatement.Id}'.",
                expression.Span,
                expression.Id);
        }

        if (!patchIndex.TryGetStatement(
                reconnect.ExpectedContainingStatementId,
                out HirPatchIndex.StatementLocation? statementLocation) ||
            statementLocation!.ParentBlock.Id != reconnect.ExpectedParentBlockId ||
            !SiblingsMatch(
                statementLocation,
                reconnect.ExpectedPreviousSiblingId,
                reconnect.ExpectedNextSiblingId))
        {
            return Diagnostic(
                SemanticPatchDiagnosticCodes.SiblingPreconditionFailed,
                $"Initial parent-block or sibling preconditions failed for plan step " +
                $"'{reconnect.StepId}'.",
                expression.Span,
                expression.Id);
        }

        BoundNodeInfo? binding = semantics.Nodes.SingleOrDefault(
            candidate => candidate.NodeId == expression.Id);
        if (binding?.TypeId != reconnect.ExpectedTypeId)
        {
            return Diagnostic(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Plan step '{reconnect.StepId}' expected target type " +
                $"'{reconnect.ExpectedTypeId}', but the current type is '{binding?.TypeId}'.",
                expression.Span,
                expression.Id);
        }

        if (!producerMethods.TryGetValue(reconnect.SourceStepId, out SemanticId producerMethodId) ||
            producerMethodId != method.Id)
        {
            return Diagnostic(
                SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                $"Reconnect step '{reconnect.StepId}' and source step " +
                $"'{reconnect.SourceStepId}' target different methods.",
                expression.Span,
                expression.Id);
        }

        return null;
    }

    private static bool TryCreateCurrentOperation(
        SemanticPatchPlanStep step,
        CSharpAnalysisResult current,
        IReadOnlyDictionary<string, SemanticId> producedDefinitions,
        IReadOnlyDictionary<string, ProducedIfBranches> producedBranches,
        IReadOnlyDictionary<string, ProducedLoop> producedLoops,
        IReadOnlyDictionary<string, SemanticId> producedReturns,
        IReadOnlyDictionary<string, SemanticId> producedExpressions,
        out SemanticPatchOperation? operation,
        out UnifiedDiagnostic? diagnostic)
    {
        HirCompilationUnit compilation = current.Hir ??
            throw new ArgumentException("Plan-step rebasing requires HIR.", nameof(current));
        if (!TryResolveProducedLocalReferences(
                step,
                producedDefinitions,
                out SemanticPatchPlanStep? resolvedStep,
                out diagnostic))
        {
            operation = null;
            return false;
        }

        step = resolvedStep!;
        IReadOnlyDictionary<SemanticId, HirNode> nodes = compilation
            .DescendantsAndSelf()
            .ToDictionary(static node => node.Id);
        var patchIndex = new HirPatchIndex(compilation);
        if (!nodes.TryGetValue(step.TargetNodeId, out HirNode? target))
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.TargetNotFound,
                $"Plan step '{step.StepId}' target '{step.TargetNodeId}' did not survive an earlier step.",
                SourceSpan.Unknown("<patch-plan>"),
                step.TargetNodeId);
            return false;
        }

        switch (step)
        {
            case ReplaceLiteralPatchPlanStep replace when target is HirLiteralExpression:
                operation = replace.ToOperation();
                diagnostic = null;
                return true;
            case ChangeOperatorPatchPlanStep changeOperator
                when target is HirUnaryExpression or HirBinaryExpression:
                operation = changeOperator.ToOperation();
                diagnostic = null;
                return true;
            case ReconnectArgumentPatchPlanStep reconnectArgument
                when target is HirInvocationExpression invocation:
                return TryRebaseArgumentReconnection(
                    reconnectArgument,
                    invocation,
                    out operation,
                    out diagnostic);
            case ReplaceExpressionInputPatchPlanStep expressionInput
                when target is HirExpression:
                operation = expressionInput.ToOperation();
                diagnostic = null;
                return true;
            case ReplaceProducedExpressionInputPatchPlanStep producedExpressionInput:
                return TryCreateProducedExpressionInputReplacement(
                    producedExpressionInput,
                    nodes,
                    producedExpressions,
                    out operation,
                    out diagnostic);
            case ReplaceAssignmentValuePatchPlanStep assignmentValue
                when target is HirAssignmentExpression:
                operation = assignmentValue.ToOperation();
                diagnostic = null;
                return true;
            case ReplaceWhileConditionPatchPlanStep whileCondition
                when target is HirWhileStatement:
                operation = whileCondition.ToOperation();
                diagnostic = null;
                return true;
            case ReplaceProducedWhileConditionPatchPlanStep producedCondition:
                return TryCreateProducedWhileConditionReplacement(
                    producedCondition,
                    nodes,
                    producedLoops,
                    out operation,
                    out diagnostic);
            case ReplaceDoWhileConditionPatchPlanStep doWhileCondition
                when target is HirDoWhileStatement:
                operation = doWhileCondition.ToOperation();
                diagnostic = null;
                return true;
            case ReplaceProducedDoWhileConditionPatchPlanStep producedDoWhileCondition:
                return TryCreateProducedDoWhileConditionReplacement(
                    producedDoWhileCondition,
                    nodes,
                    producedLoops,
                    out operation,
                    out diagnostic);
            case ReplaceForConditionPatchPlanStep forCondition
                when target is HirForStatement:
                operation = forCondition.ToOperation();
                diagnostic = null;
                return true;
            case ReplaceProducedForConditionPatchPlanStep producedForCondition:
                return TryCreateProducedForConditionReplacement(
                    producedForCondition,
                    nodes,
                    producedLoops,
                    out operation,
                    out diagnostic);
            case ReplaceForEachCollectionPatchPlanStep forEachCollection
                when target is HirForEachStatement:
                operation = forEachCollection.ToOperation();
                diagnostic = null;
                return true;
            case ReplaceProducedForEachCollectionPatchPlanStep producedForEachCollection:
                return TryCreateProducedForEachCollectionReplacement(
                    producedForEachCollection,
                    nodes,
                    producedLoops,
                    out operation,
                    out diagnostic);
            case ReplaceForDeclarationInitializerPatchPlanStep forDeclaration
                when target is HirForVariableDeclarator:
                operation = forDeclaration.ToOperation();
                diagnostic = null;
                return true;
            case ReplaceForAssignmentInitializerValuePatchPlanStep forAssignment
                when target is HirAssignmentExpression:
                operation = forAssignment.ToOperation();
                diagnostic = null;
                return true;
            case ReplaceForIncrementorPatchPlanStep forIncrementor
                when target is HirUnaryExpression:
                operation = forIncrementor.ToOperation();
                diagnostic = null;
                return true;
            case ReplaceIfConditionPatchPlanStep ifCondition
                when target is HirIfStatement:
                operation = ifCondition.ToOperation();
                diagnostic = null;
                return true;
            case ReplaceProducedIfConditionPatchPlanStep producedIfCondition:
                return TryCreateProducedIfConditionReplacement(
                    producedIfCondition,
                    nodes,
                    producedBranches,
                    out operation,
                    out diagnostic);
            case ReplaceLocalInitializerPatchPlanStep localInitializer
                when target is HirVariableDeclarationStatement:
                operation = localInitializer.ToOperation();
                diagnostic = null;
                return true;
            case ReplaceProducedLocalInitializerPatchPlanStep producedInitializer:
                return TryCreateProducedLocalInitializerReplacement(
                    producedInitializer,
                    nodes,
                    producedDefinitions,
                    out operation,
                    out diagnostic);
            case ReplaceReturnValuePatchPlanStep returnValue
                when target is HirReturnStatement:
                operation = returnValue.ToOperation();
                diagnostic = null;
                return true;
            case ReplaceProducedReturnValuePatchPlanStep producedReturn:
                return TryCreateProducedReturnValueReplacement(
                    producedReturn,
                    nodes,
                    producedReturns,
                    out operation,
                    out diagnostic);
            case InsertStatementPatchPlanStep insertion
                when patchIndex.TryGetBlock(
                    insertion.TargetNodeId,
                    out HirPatchIndex.BlockLocation? blockLocation):
                return TryRebaseInsertion(
                    insertion,
                    blockLocation!.Block,
                    out operation,
                    out diagnostic);
            case InsertBranchStatementPatchPlanStep branchInsertion:
                return TryCreateBranchInsertion(
                    branchInsertion,
                    patchIndex,
                    producedBranches,
                    out operation,
                    out diagnostic);
            case InsertLoopBodyStatementPatchPlanStep loopInsertion:
                return TryCreateLoopBodyInsertion(
                    loopInsertion,
                    patchIndex,
                    producedLoops,
                    out operation,
                    out diagnostic);
            case DeleteStatementPatchPlanStep deletion
                when patchIndex.TryGetStatement(
                    deletion.TargetNodeId,
                    out HirPatchIndex.StatementLocation? statementLocation):
                GetSiblings(
                    statementLocation!,
                    out SemanticId? deletionPreviousSiblingId,
                    out SemanticId? deletionNextSiblingId);
                operation = new DeleteStatementPatchOperation(
                    deletion.TargetNodeId,
                    statementLocation!.ParentBlock.Id,
                    deletionPreviousSiblingId,
                    deletionNextSiblingId);
                diagnostic = null;
                return true;
        }

        if (step is not (IntroduceTemporaryPatchPlanStep or ReconnectValuePatchPlanStep) ||
            target is not HirExpression expression ||
            !patchIndex.TryGetExpression(
                expression.Id,
                out HirPatchIndex.ExpressionLocation? expressionLocation) ||
            !patchIndex.TryGetStatement(
                expressionLocation!.ContainingStatement.Id,
                out HirPatchIndex.StatementLocation? expressionStatementLocation))
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.TargetKindMismatch,
                $"Plan step '{step.StepId}' no longer resolves to the required HIR shape.",
                target.Span,
                target.Id);
            return false;
        }

        GetSiblings(
            expressionStatementLocation!,
            out SemanticId? previousSiblingId,
            out SemanticId? nextSiblingId);
        operation = step switch
        {
            IntroduceTemporaryPatchPlanStep introduction => new IntroduceTemporaryPatchOperation(
                introduction.TargetNodeId,
                expressionLocation!.ContainingStatement.Id,
                expressionStatementLocation!.ParentBlock.Id,
                previousSiblingId,
                nextSiblingId,
                introduction.Name,
                introduction.TypeName,
                introduction.ExpectedTypeId),
            ReconnectValuePatchPlanStep reconnect when producedDefinitions.TryGetValue(
                reconnect.SourceStepId,
                out SemanticId sourceDefinitionId) => new ReconnectValuePatchOperation(
                    reconnect.TargetNodeId,
                    sourceDefinitionId,
                    expressionLocation!.ContainingStatement.Id,
                    expressionStatementLocation!.ParentBlock.Id,
                    previousSiblingId,
                    nextSiblingId,
                    reconnect.ExpectedTypeId,
                    reconnect.ExpectedTypeId),
            ReconnectValuePatchPlanStep => null,
            _ => throw new ArgumentOutOfRangeException(nameof(step), step.GetType().Name),
        };
        if (operation is not null)
        {
            diagnostic = null;
            return true;
        }

        var unresolved = (ReconnectValuePatchPlanStep)step;
        diagnostic = Diagnostic(
            SemanticPatchDiagnosticCodes.InvalidPatchPlan,
            $"Reconnect step '{unresolved.StepId}' could not resolve source step " +
            $"'{unresolved.SourceStepId}'.",
            expression.Span,
            expression.Id);
        return false;
    }

    private static bool TryResolveProducedLocalReferences(
        SemanticPatchPlanStep step,
        IReadOnlyDictionary<string, SemanticId> producedDefinitions,
        out SemanticPatchPlanStep? resolvedStep,
        out UnifiedDiagnostic? diagnostic)
    {
        IReadOnlyList<ProducedLocalReferencePatchExpression> references =
            SemanticPatchPlanExpressions.FindProducedLocalReferences(step);
        foreach (ProducedLocalReferencePatchExpression reference in references)
        {
            if (!producedDefinitions.ContainsKey(reference.SourceStepId))
            {
                resolvedStep = null;
                diagnostic = Diagnostic(
                    SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                    $"Plan step '{step.StepId}' could not resolve produced-local source step " +
                    $"'{reference.SourceStepId}'.",
                    SourceSpan.Unknown("<patch-plan>"),
                    step.TargetNodeId);
                return false;
            }
        }

        resolvedStep = SemanticPatchPlanExpressions.ResolveProducedLocalReferences(
            step,
            reference => new LocalReferencePatchExpression(
                producedDefinitions[reference.SourceStepId],
                reference.ExpectedName,
                reference.ExpectedTypeId));
        diagnostic = null;
        return true;
    }

    private static bool TryRebaseArgumentReconnection(
        ReconnectArgumentPatchPlanStep step,
        HirInvocationExpression invocation,
        out SemanticPatchOperation? operation,
        out UnifiedDiagnostic? diagnostic)
    {
        int argumentIndex = FindArgumentIndex(invocation.Arguments, step.ExpectedArgumentNodeId);
        int sourceArgumentIndex = FindArgumentIndex(
            invocation.Arguments,
            step.ExpectedSourceArgumentNodeId);
        if (argumentIndex < 0 || sourceArgumentIndex < 0 || argumentIndex == sourceArgumentIndex)
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Plan step '{step.StepId}' could not relocate both original invocation endpoints " +
                "after earlier stages.",
                invocation.Span,
                invocation.Id);
            return false;
        }

        operation = new ReconnectArgumentPatchOperation(
            step.TargetNodeId,
            argumentIndex,
            step.ExpectedArgumentNodeId,
            sourceArgumentIndex,
            step.ExpectedSourceArgumentNodeId,
            step.ExpectedArgumentTypeId,
            step.ExpectedSourceArgumentTypeId);
        diagnostic = null;
        return true;
    }

    private static int FindArgumentIndex(
        IReadOnlyList<HirExpression> arguments,
        SemanticId nodeId)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Id == nodeId)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryRebaseInsertion(
        InsertStatementPatchPlanStep step,
        HirBlockStatement block,
        out SemanticPatchOperation? operation,
        out UnifiedDiagnostic? diagnostic)
    {
        if (!TryResolveInsertionGap(
                block,
                step.ExpectedPreviousSiblingId,
                step.ExpectedNextSiblingId,
                out SemanticId? previousSiblingId,
                out SemanticId? nextSiblingId))
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.SiblingPreconditionFailed,
                $"Plan step '{step.StepId}' could not rebase its original insertion gap after " +
                "earlier stages.",
                block.Span,
                block.Id);
            return false;
        }

        operation = new InsertStatementPatchOperation(
            step.TargetNodeId,
            previousSiblingId,
            nextSiblingId,
            step.NewStatement);
        diagnostic = null;
        return true;
    }

    private static bool TryCreateBranchInsertion(
        InsertBranchStatementPatchPlanStep step,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<string, ProducedIfBranches> producedBranches,
        out SemanticPatchOperation? operation,
        out UnifiedDiagnostic? diagnostic)
    {
        if (!producedBranches.TryGetValue(step.SourceStepId, out ProducedIfBranches branches))
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                $"Branch insertion step '{step.StepId}' could not resolve source step " +
                $"'{step.SourceStepId}'.",
                SourceSpan.Unknown("<patch-plan>"),
                step.TargetNodeId);
            return false;
        }

        SemanticId branchBlockId = step.Branch switch
        {
            IfBranchKind.Then => branches.ThenBlockId,
            IfBranchKind.Else => branches.ElseBlockId,
            _ => throw new ArgumentOutOfRangeException(nameof(step), step.Branch, null),
        };
        if (!patchIndex.TryGetBlock(
                branchBlockId,
                out HirPatchIndex.BlockLocation? branchLocation))
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.TargetNotFound,
                $"Branch insertion step '{step.StepId}' could not resolve the produced " +
                $"'{step.Branch}' block from source step '{step.SourceStepId}'.",
                SourceSpan.Unknown("<patch-plan>"),
                branchBlockId);
            return false;
        }

        SemanticId? previousSiblingId = branchLocation!.Block.Statements.Count == 0
            ? null
            : branchLocation.Block.Statements[^1].Id;
        operation = new InsertStatementPatchOperation(
            branchBlockId,
            previousSiblingId,
            null,
            step.NewStatement);
        diagnostic = null;
        return true;
    }

    private static bool TryCreateLoopBodyInsertion(
        InsertLoopBodyStatementPatchPlanStep step,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<string, ProducedLoop> producedLoops,
        out SemanticPatchOperation? operation,
        out UnifiedDiagnostic? diagnostic)
    {
        if (!producedLoops.TryGetValue(step.SourceStepId, out ProducedLoop loop))
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                $"Loop-body insertion step '{step.StepId}' could not resolve source step " +
                $"'{step.SourceStepId}'.",
                SourceSpan.Unknown("<patch-plan>"),
                step.TargetNodeId);
            return false;
        }

        if (!patchIndex.TryGetBlock(
                loop.BodyBlockId,
                out HirPatchIndex.BlockLocation? bodyLocation) ||
            !IsMatchingProducedLoop(bodyLocation!.EntryStatement, loop))
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.TargetNotFound,
                $"Loop-body insertion step '{step.StepId}' could not resolve the produced body " +
                $"from source step '{step.SourceStepId}'.",
                SourceSpan.Unknown("<patch-plan>"),
                loop.BodyBlockId);
            return false;
        }

        SemanticId? previousSiblingId = bodyLocation.Block.Statements.Count == 0
            ? null
            : bodyLocation.Block.Statements[^1].Id;
        operation = new InsertStatementPatchOperation(
            loop.BodyBlockId,
            previousSiblingId,
            null,
            step.NewStatement);
        diagnostic = null;
        return true;
    }

    private static bool TryCreateProducedWhileConditionReplacement(
        ReplaceProducedWhileConditionPatchPlanStep step,
        IReadOnlyDictionary<SemanticId, HirNode> nodes,
        IReadOnlyDictionary<string, ProducedLoop> producedLoops,
        out SemanticPatchOperation? operation,
        out UnifiedDiagnostic? diagnostic)
    {
        if (!producedLoops.TryGetValue(step.SourceStepId, out ProducedLoop producedLoop) ||
            !nodes.TryGetValue(producedLoop.LoopId, out HirNode? node) ||
            node is not HirWhileStatement currentLoop ||
            currentLoop.Body.Id != producedLoop.BodyBlockId)
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.TargetNotFound,
                $"Produced-while condition step '{step.StepId}' could not resolve loop source " +
                $"'{step.SourceStepId}'.",
                SourceSpan.Unknown("<patch-plan>"),
                step.TargetNodeId);
            return false;
        }

        operation = new ReplaceWhileConditionPatchOperation(
            currentLoop.Id,
            currentLoop.Condition.Id,
            step.NewCondition.ExpectedTypeId,
            step.NewCondition);
        diagnostic = null;
        return true;
    }

    private static bool TryCreateProducedForConditionReplacement(
        ReplaceProducedForConditionPatchPlanStep step,
        IReadOnlyDictionary<SemanticId, HirNode> nodes,
        IReadOnlyDictionary<string, ProducedLoop> producedLoops,
        out SemanticPatchOperation? operation,
        out UnifiedDiagnostic? diagnostic)
    {
        if (!producedLoops.TryGetValue(step.SourceStepId, out ProducedLoop producedLoop) ||
            !nodes.TryGetValue(producedLoop.LoopId, out HirNode? node) ||
            node is not HirForStatement { Condition: not null } currentLoop ||
            currentLoop.Body.Id != producedLoop.BodyBlockId)
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.TargetNotFound,
                $"Produced-for condition step '{step.StepId}' could not resolve an explicit " +
                $"condition from loop source '{step.SourceStepId}'.",
                SourceSpan.Unknown("<patch-plan>"),
                step.TargetNodeId);
            return false;
        }

        operation = new ReplaceForConditionPatchOperation(
            currentLoop.Id,
            currentLoop.Condition.Id,
            step.NewCondition.ExpectedTypeId,
            step.NewCondition);
        diagnostic = null;
        return true;
    }

    private static bool TryCreateProducedDoWhileConditionReplacement(
        ReplaceProducedDoWhileConditionPatchPlanStep step,
        IReadOnlyDictionary<SemanticId, HirNode> nodes,
        IReadOnlyDictionary<string, ProducedLoop> producedLoops,
        out SemanticPatchOperation? operation,
        out UnifiedDiagnostic? diagnostic)
    {
        if (!producedLoops.TryGetValue(step.SourceStepId, out ProducedLoop producedLoop) ||
            !nodes.TryGetValue(producedLoop.LoopId, out HirNode? node) ||
            node is not HirDoWhileStatement currentLoop ||
            currentLoop.Body.Id != producedLoop.BodyBlockId)
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.TargetNotFound,
                $"Produced-do-while condition step '{step.StepId}' could not resolve loop " +
                $"source '{step.SourceStepId}'.",
                SourceSpan.Unknown("<patch-plan>"),
                step.TargetNodeId);
            return false;
        }

        operation = new ReplaceDoWhileConditionPatchOperation(
            currentLoop.Id,
            currentLoop.Condition.Id,
            step.NewCondition.ExpectedTypeId,
            step.NewCondition);
        diagnostic = null;
        return true;
    }

    private static bool TryCreateProducedForEachCollectionReplacement(
        ReplaceProducedForEachCollectionPatchPlanStep step,
        IReadOnlyDictionary<SemanticId, HirNode> nodes,
        IReadOnlyDictionary<string, ProducedLoop> producedLoops,
        out SemanticPatchOperation? operation,
        out UnifiedDiagnostic? diagnostic)
    {
        if (!producedLoops.TryGetValue(step.SourceStepId, out ProducedLoop producedLoop) ||
            !nodes.TryGetValue(producedLoop.LoopId, out HirNode? node) ||
            node is not HirForEachStatement currentLoop ||
            currentLoop.Body.Id != producedLoop.BodyBlockId)
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.TargetNotFound,
                $"Produced-foreach collection step '{step.StepId}' could not resolve loop " +
                $"source '{step.SourceStepId}'.",
                SourceSpan.Unknown("<patch-plan>"),
                step.TargetNodeId);
            return false;
        }

        operation = new ReplaceForEachCollectionPatchOperation(
            currentLoop.Id,
            currentLoop.Collection.Id,
            step.NewCollection.ExpectedTypeId,
            step.NewCollection);
        diagnostic = null;
        return true;
    }

    private static bool TryCreateProducedExpressionInputReplacement(
        ReplaceProducedExpressionInputPatchPlanStep step,
        IReadOnlyDictionary<SemanticId, HirNode> nodes,
        IReadOnlyDictionary<string, SemanticId> producedExpressions,
        out SemanticPatchOperation? operation,
        out UnifiedDiagnostic? diagnostic)
    {
        if (!producedExpressions.TryGetValue(step.SourceStepId, out SemanticId rootId) ||
            !nodes.TryGetValue(rootId, out HirNode? rootNode) ||
            rootNode is not HirExpression target)
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.TargetNotFound,
                $"Produced-expression input step '{step.StepId}' could not resolve expression " +
                $"source step '{step.SourceStepId}'.",
                SourceSpan.Unknown("<patch-plan>"),
                step.TargetNodeId);
            return false;
        }

        foreach (string pathPort in step.TargetPath)
        {
            if (!TryGetHirExpressionInput(target, pathPort, out HirExpression? child))
            {
                operation = null;
                diagnostic = Diagnostic(
                    SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                    $"Produced-expression input step '{step.StepId}' could not follow runtime " +
                    $"target-path port '{pathPort}' from source step '{step.SourceStepId}'.",
                    target.Span,
                    target.Id);
                return false;
            }

            target = child!;
        }

        if (!TryGetHirExpressionInput(target, step.InputPort, out HirExpression? input))
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.InvalidExpressionInputPort,
                $"Produced-expression input step '{step.StepId}' could not resolve runtime input " +
                $"port '{step.InputPort}'.",
                target.Span,
                target.Id);
            return false;
        }

        operation = new ReplaceExpressionInputPatchOperation(
            target.Id,
            step.InputPort,
            input!.Id,
            step.ExpectedInputTypeId,
            step.ExpectedTargetTypeId,
            step.NewInput);
        diagnostic = null;
        return true;
    }

    private static bool TryGetHirExpressionInput(
        HirExpression target,
        string inputPort,
        out HirExpression? input)
    {
        input = (target, inputPort) switch
        {
            (HirBinaryExpression binary, "left") => binary.Left,
            (HirBinaryExpression binary, "right") => binary.Right,
            (HirUnaryExpression unary, "operand") => unary.Operand,
            (HirCastExpression cast, "operand") => cast.Operand,
            (HirConditionalExpression conditional, "condition") => conditional.Condition,
            (HirConditionalExpression conditional, "whenTrue") => conditional.WhenTrue,
            (HirConditionalExpression conditional, "whenFalse") => conditional.WhenFalse,
            (HirConditionalAccessExpression conditionalAccess, "receiver") =>
                conditionalAccess.Receiver,
            _ => null,
        };
        if (input is not null)
        {
            return true;
        }

        if (target is not HirInvocationExpression invocation ||
            !inputPort.StartsWith("arg", StringComparison.Ordinal) ||
            !int.TryParse(inputPort.AsSpan(3), out int argumentIndex) ||
            argumentIndex < 0 ||
            argumentIndex >= invocation.Arguments.Count ||
            !string.Equals(inputPort, $"arg{argumentIndex}", StringComparison.Ordinal))
        {
            return false;
        }

        input = invocation.Arguments[argumentIndex];
        return true;
    }

    private static bool TryCreateProducedIfConditionReplacement(
        ReplaceProducedIfConditionPatchPlanStep step,
        IReadOnlyDictionary<SemanticId, HirNode> nodes,
        IReadOnlyDictionary<string, ProducedIfBranches> producedBranches,
        out SemanticPatchOperation? operation,
        out UnifiedDiagnostic? diagnostic)
    {
        if (!producedBranches.TryGetValue(step.SourceStepId, out ProducedIfBranches producedIf) ||
            !nodes.TryGetValue(producedIf.IfId, out HirNode? node) ||
            node is not HirIfStatement currentIf ||
            currentIf.Then.Id != producedIf.ThenBlockId ||
            currentIf.Else?.Id != producedIf.ElseBlockId)
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.TargetNotFound,
                $"Produced-if condition step '{step.StepId}' could not resolve source step " +
                $"'{step.SourceStepId}'.",
                SourceSpan.Unknown("<patch-plan>"),
                step.TargetNodeId);
            return false;
        }

        operation = new ReplaceIfConditionPatchOperation(
            currentIf.Id,
            currentIf.Condition.Id,
            step.NewCondition.ExpectedTypeId,
            step.NewCondition);
        diagnostic = null;
        return true;
    }

    private static bool TryCreateProducedLocalInitializerReplacement(
        ReplaceProducedLocalInitializerPatchPlanStep step,
        IReadOnlyDictionary<SemanticId, HirNode> nodes,
        IReadOnlyDictionary<string, SemanticId> producedDefinitions,
        out SemanticPatchOperation? operation,
        out UnifiedDiagnostic? diagnostic)
    {
        if (!producedDefinitions.TryGetValue(step.SourceStepId, out SemanticId definitionId) ||
            !nodes.TryGetValue(definitionId, out HirNode? node) ||
            node is not HirVariableDeclarationStatement { Initializer: not null } variable)
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.TargetNotFound,
                $"Produced-local initializer step '{step.StepId}' could not resolve source step " +
                $"'{step.SourceStepId}'.",
                SourceSpan.Unknown("<patch-plan>"),
                step.TargetNodeId);
            return false;
        }

        operation = new ReplaceLocalInitializerPatchOperation(
            variable.Id,
            variable.Name,
            variable.Initializer.Id,
            step.NewInitializer.ExpectedTypeId,
            step.NewInitializer);
        diagnostic = null;
        return true;
    }

    private static bool TryCreateProducedReturnValueReplacement(
        ReplaceProducedReturnValuePatchPlanStep step,
        IReadOnlyDictionary<SemanticId, HirNode> nodes,
        IReadOnlyDictionary<string, SemanticId> producedReturns,
        out SemanticPatchOperation? operation,
        out UnifiedDiagnostic? diagnostic)
    {
        if (!producedReturns.TryGetValue(step.SourceStepId, out SemanticId returnId) ||
            !nodes.TryGetValue(returnId, out HirNode? node) ||
            node is not HirReturnStatement { Expression: not null } returnStatement)
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.TargetNotFound,
                $"Produced-return value step '{step.StepId}' could not resolve source step " +
                $"'{step.SourceStepId}'.",
                SourceSpan.Unknown("<patch-plan>"),
                step.TargetNodeId);
            return false;
        }

        operation = new ReplaceReturnValuePatchOperation(
            returnStatement.Id,
            returnStatement.Expression.Id,
            step.NewValue.ExpectedTypeId,
            step.NewValue);
        diagnostic = null;
        return true;
    }

    private static bool TryResolveInsertionGap(
        HirBlockStatement block,
        SemanticId? expectedPreviousSiblingId,
        SemanticId? expectedNextSiblingId,
        out SemanticId? previousSiblingId,
        out SemanticId? nextSiblingId)
    {
        IReadOnlyList<HirStatement> statements = block.Statements;
        int previousIndex = expectedPreviousSiblingId is SemanticId expectedPrevious
            ? FindStatementIndex(statements, expectedPrevious)
            : -1;
        int nextIndex = expectedNextSiblingId is SemanticId expectedNext
            ? FindStatementIndex(statements, expectedNext)
            : -1;

        if (expectedPreviousSiblingId is null && expectedNextSiblingId is null)
        {
            previousSiblingId = statements.Count == 0 ? null : statements[^1].Id;
            nextSiblingId = null;
            return true;
        }

        if (expectedPreviousSiblingId is null)
        {
            if (nextIndex < 0)
            {
                previousSiblingId = null;
                nextSiblingId = null;
                return false;
            }

            previousSiblingId = nextIndex == 0 ? null : statements[nextIndex - 1].Id;
            nextSiblingId = statements[nextIndex].Id;
            return true;
        }

        if (expectedNextSiblingId is null)
        {
            if (previousIndex < 0)
            {
                previousSiblingId = null;
                nextSiblingId = null;
                return false;
            }

            previousSiblingId = statements[^1].Id;
            nextSiblingId = null;
            return true;
        }

        if (previousIndex >= 0 && nextIndex >= 0 && previousIndex >= nextIndex)
        {
            previousSiblingId = null;
            nextSiblingId = null;
            return false;
        }

        if (nextIndex >= 0)
        {
            previousSiblingId = nextIndex == 0 ? null : statements[nextIndex - 1].Id;
            nextSiblingId = statements[nextIndex].Id;
            return true;
        }

        if (previousIndex >= 0)
        {
            previousSiblingId = statements[previousIndex].Id;
            nextSiblingId = previousIndex == statements.Count - 1
                ? null
                : statements[previousIndex + 1].Id;
            return true;
        }

        previousSiblingId = null;
        nextSiblingId = null;
        return false;
    }

    private static int FindStatementIndex(
        IReadOnlyList<HirStatement> statements,
        SemanticId statementId)
    {
        for (int index = 0; index < statements.Count; index++)
        {
            if (statements[index].Id == statementId)
            {
                return index;
            }
        }

        return -1;
    }

    private static HirVariableDeclarationStatement? FindIntroducedDefinition(
        HirCompilationUnit compilation,
        IntroduceTemporaryPatchPlanStep step)
    {
        HirVariableDeclarationStatement[] candidates = compilation
            .DescendantsAndSelf()
            .OfType<HirVariableDeclarationStatement>()
            .Where(variable => variable.Name == step.Name &&
                variable.Initializer?.DescendantsAndSelf().Any(
                    node => node.Id == step.TargetNodeId) == true)
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static HirStatement? FindInsertedStatement(
        HirCompilationUnit compilation,
        InsertStatementPatchOperation operation)
    {
        if (!new HirPatchIndex(compilation).TryGetBlock(
                operation.TargetNodeId,
                out HirPatchIndex.BlockLocation? blockLocation))
        {
            return null;
        }

        IReadOnlyList<HirStatement> statements = blockLocation!.Block.Statements;
        int insertionIndex = 0;
        if (operation.ExpectedPreviousSiblingId is SemanticId previousSiblingId)
        {
            int previousIndex = FindStatementIndex(statements, previousSiblingId);
            if (previousIndex < 0)
            {
                return null;
            }

            insertionIndex = previousIndex + 1;
        }

        if (insertionIndex < 0 || insertionIndex >= statements.Count)
        {
            return null;
        }

        if (operation.ExpectedNextSiblingId is SemanticId nextSiblingId)
        {
            if (insertionIndex + 1 >= statements.Count ||
                statements[insertionIndex + 1].Id != nextSiblingId)
            {
                return null;
            }
        }
        else if (insertionIndex != statements.Count - 1)
        {
            return null;
        }

        return statements[insertionIndex];
    }

    private static HirExpression? FindProducedExpression(
        HirCompilationUnit compilation,
        SemanticPatchOperation operation,
        HirStatement? insertedStatement)
    {
        IReadOnlyDictionary<SemanticId, HirNode> nodes = compilation
            .DescendantsAndSelf()
            .ToDictionary(static node => node.Id);
        return operation switch
        {
            ReplaceExpressionInputPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? node) &&
                node is HirExpression target &&
                TryGetHirExpressionInput(target, replacement.InputPort, out HirExpression? input) =>
                    input,
            ReplaceAssignmentValuePatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? node) &&
                node is HirAssignmentExpression assignment => assignment.Value,
            ReplaceWhileConditionPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? node) &&
                node is HirWhileStatement loop => loop.Condition,
            ReplaceDoWhileConditionPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? node) &&
                node is HirDoWhileStatement loop => loop.Condition,
            ReplaceForConditionPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? node) &&
                node is HirForStatement loop => loop.Condition,
            ReplaceForEachCollectionPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? node) &&
                node is HirForEachStatement loop => loop.Collection,
            ReplaceForDeclarationInitializerPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? node) &&
                node is HirForVariableDeclarator variable => variable.Initializer,
            ReplaceForAssignmentInitializerValuePatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? node) &&
                node is HirAssignmentExpression assignment => assignment.Value,
            ReplaceForIncrementorPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? node) &&
                node is HirUnaryExpression incrementor => incrementor.Operand,
            ReplaceIfConditionPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? node) &&
                node is HirIfStatement branch => branch.Condition,
            ReplaceLocalInitializerPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? node) &&
                node is HirVariableDeclarationStatement variable => variable.Initializer,
            ReplaceReturnValuePatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? node) &&
                node is HirReturnStatement valueReturn => valueReturn.Expression,
            InsertStatementPatchOperation => GetInsertedExpression(insertedStatement),
            _ => null,
        };
    }

    private static HirExpression? GetInsertedExpression(HirStatement? statement) => statement switch
    {
        HirExpressionStatement { Expression: HirAssignmentExpression assignment } => assignment.Value,
        HirIfStatement branch => branch.Condition,
        HirWhileStatement loop => loop.Condition,
        HirDoWhileStatement loop => loop.Condition,
        HirForEachStatement loop => loop.Collection,
        HirReturnStatement valueReturn => valueReturn.Expression,
        _ => null,
    };

    private static HirNode? GetInsertedDefinition(HirStatement? statement) => statement switch
    {
        HirVariableDeclarationStatement variable => variable,
        HirForStatement { Declaration.Variables.Count: 1 } forStatement =>
            forStatement.Declaration.Variables[0],
        HirForEachStatement forEachStatement => forEachStatement.Variable,
        _ => null,
    };

    private static bool IsMatchingProducedLoop(
        HirStatement? statement,
        ProducedLoop producedLoop) => statement switch
        {
            HirWhileStatement loop =>
                loop.Id == producedLoop.LoopId && loop.Body.Id == producedLoop.BodyBlockId,
            HirDoWhileStatement loop =>
                loop.Id == producedLoop.LoopId && loop.Body.Id == producedLoop.BodyBlockId,
            HirForStatement loop =>
                loop.Id == producedLoop.LoopId && loop.Body.Id == producedLoop.BodyBlockId,
            HirForEachStatement loop =>
                loop.Id == producedLoop.LoopId && loop.Body.Id == producedLoop.BodyBlockId,
            _ => false,
        };

    private static bool ProducesLocal(SemanticPatchPlanStep step) =>
        step is IntroduceTemporaryPatchPlanStep or
            InsertStatementPatchPlanStep { NewStatement: LocalLiteralDeclarationPatchStatement } or
            InsertStatementPatchPlanStep
        {
            NewStatement: ForPatchStatement { Declaration: not null },
        } or
            InsertStatementPatchPlanStep
        {
            NewStatement: ForEachPatchStatement,
        } or
            InsertBranchStatementPatchPlanStep
        {
            NewStatement: LocalLiteralDeclarationPatchStatement
        } or
            InsertBranchStatementPatchPlanStep
        {
            NewStatement: ForPatchStatement { Declaration: not null },
        } or
            InsertBranchStatementPatchPlanStep
        {
            NewStatement: ForEachPatchStatement,
        } or
            InsertLoopBodyStatementPatchPlanStep
        {
            NewStatement: LocalLiteralDeclarationPatchStatement
        } or
            InsertLoopBodyStatementPatchPlanStep
        {
            NewStatement: ForPatchStatement { Declaration: not null },
        } or
            InsertLoopBodyStatementPatchPlanStep
        {
            NewStatement: ForEachPatchStatement,
        };

    private static bool ProducesBranches(SemanticPatchPlanStep step) =>
        step is InsertStatementPatchPlanStep { NewStatement: IfPatchStatement } or
            InsertBranchStatementPatchPlanStep { NewStatement: IfPatchStatement } or
            InsertLoopBodyStatementPatchPlanStep { NewStatement: IfPatchStatement };

    private static bool ProducesLoop(SemanticPatchPlanStep step) =>
        step is InsertStatementPatchPlanStep
        {
            NewStatement: WhilePatchStatement or DoWhilePatchStatement or ForPatchStatement or
                ForEachPatchStatement,
        } or
            InsertBranchStatementPatchPlanStep
        {
            NewStatement: WhilePatchStatement or DoWhilePatchStatement or ForPatchStatement or
                ForEachPatchStatement,
        } or
            InsertLoopBodyStatementPatchPlanStep
        {
            NewStatement: WhilePatchStatement or DoWhilePatchStatement or ForPatchStatement or
                ForEachPatchStatement,
        };

    private static bool ProducesLocalInitializer(SemanticPatchPlanStep step) =>
        step is InsertStatementPatchPlanStep
        {
            NewStatement: LocalLiteralDeclarationPatchStatement,
        } or
            InsertBranchStatementPatchPlanStep
        {
            NewStatement: LocalLiteralDeclarationPatchStatement,
        } or
            InsertLoopBodyStatementPatchPlanStep
        {
            NewStatement: LocalLiteralDeclarationPatchStatement,
        };

    private static bool ProducesReturnValue(SemanticPatchPlanStep step) =>
        step is InsertStatementPatchPlanStep
        {
            NewStatement: ReturnPatchStatement { Expression: not null },
        } or
            InsertBranchStatementPatchPlanStep
        {
            NewStatement: ReturnPatchStatement { Expression: not null },
        } or
            InsertLoopBodyStatementPatchPlanStep
        {
            NewStatement: ReturnPatchStatement { Expression: not null },
        };

    private static bool TryGetStepMethod(
        SemanticPatchPlanStep step,
        HirNode target,
        HirPatchIndex patchIndex,
        out HirMethodDeclaration? method)
    {
        if (step is ReplaceProducedExpressionInputPatchPlanStep)
        {
            return TryGetContainingMethod(target, patchIndex, out method);
        }

        switch (step)
        {
            case InsertStatementPatchPlanStep or
                InsertBranchStatementPatchPlanStep or
                InsertLoopBodyStatementPatchPlanStep or
                ReplaceProducedWhileConditionPatchPlanStep or
                ReplaceProducedDoWhileConditionPatchPlanStep or
                ReplaceProducedForConditionPatchPlanStep or
                ReplaceProducedForEachCollectionPatchPlanStep or
                ReplaceProducedIfConditionPatchPlanStep or
                ReplaceProducedLocalInitializerPatchPlanStep or
                ReplaceProducedReturnValuePatchPlanStep
                when target is HirBlockStatement block &&
                patchIndex.TryGetBlock(block.Id, out HirPatchIndex.BlockLocation? blockLocation):
                method = blockLocation!.Method;
                return true;
            case DeleteStatementPatchPlanStep when target is HirStatement statement &&
                patchIndex.TryGetStatement(
                    statement.Id,
                    out HirPatchIndex.StatementLocation? statementLocation):
                method = statementLocation!.Method;
                return true;
            case ReplaceWhileConditionPatchPlanStep when target is HirWhileStatement loop &&
                patchIndex.TryGetStatement(
                    loop.Id,
                    out HirPatchIndex.StatementLocation? loopLocation):
                method = loopLocation!.Method;
                return true;
            case ReplaceDoWhileConditionPatchPlanStep when target is HirDoWhileStatement loop &&
                patchIndex.TryGetStatement(
                    loop.Id,
                    out HirPatchIndex.StatementLocation? loopLocation):
                method = loopLocation!.Method;
                return true;
            case ReplaceForConditionPatchPlanStep when target is HirForStatement loop &&
                patchIndex.TryGetStatement(
                    loop.Id,
                    out HirPatchIndex.StatementLocation? loopLocation):
                method = loopLocation!.Method;
                return true;
            case ReplaceForEachCollectionPatchPlanStep when target is HirForEachStatement loop &&
                patchIndex.TryGetStatement(
                    loop.Id,
                    out HirPatchIndex.StatementLocation? loopLocation):
                method = loopLocation!.Method;
                return true;
            case ReplaceForDeclarationInitializerPatchPlanStep
                when target is HirForVariableDeclarator variable &&
                patchIndex.TryGetDeclaringFor(variable.Id, out HirForStatement? declaringLoop) &&
                patchIndex.TryGetStatement(
                    declaringLoop!.Id,
                    out HirPatchIndex.StatementLocation? declarationLocation):
                method = declarationLocation!.Method;
                return true;
            case ReplaceIfConditionPatchPlanStep or
                ReplaceLocalInitializerPatchPlanStep or
                ReplaceReturnValuePatchPlanStep
                when target is HirStatement editableStatement &&
                patchIndex.TryGetStatement(
                    editableStatement.Id,
                    out HirPatchIndex.StatementLocation? editableLocation):
                method = editableLocation!.Method;
                return true;
            default:
                if (target is HirExpression expression &&
                    patchIndex.TryGetExpression(
                        expression.Id,
                        out HirPatchIndex.ExpressionLocation? expressionLocation))
                {
                    method = expressionLocation!.Method;
                    return true;
                }

                method = null;
                return false;
        }
    }

    private static bool TryGetContainingMethod(
        HirNode target,
        HirPatchIndex patchIndex,
        out HirMethodDeclaration? method)
    {
        if (target is HirBlockStatement block &&
            patchIndex.TryGetBlock(block.Id, out HirPatchIndex.BlockLocation? blockLocation))
        {
            method = blockLocation!.Method;
            return true;
        }

        if (target is HirStatement statement &&
            patchIndex.TryGetStatement(
                statement.Id,
                out HirPatchIndex.StatementLocation? statementLocation))
        {
            method = statementLocation!.Method;
            return true;
        }

        if (target is HirExpression expression &&
            patchIndex.TryGetExpression(
                expression.Id,
                out HirPatchIndex.ExpressionLocation? expressionLocation))
        {
            method = expressionLocation!.Method;
            return true;
        }

        method = null;
        return false;
    }

    private static SemanticPatchOperation GetInitialOperation(SemanticPatchPlanStep step) => step switch
    {
        ReplaceLiteralPatchPlanStep replace => replace.ToOperation(),
        ChangeOperatorPatchPlanStep changeOperator => changeOperator.ToOperation(),
        ReconnectArgumentPatchPlanStep reconnectArgument => reconnectArgument.ToOperation(),
        ReplaceExpressionInputPatchPlanStep expressionInput => expressionInput.ToOperation(),
        ReplaceProducedExpressionInputPatchPlanStep => throw new ArgumentException(
            "A symbolic produced-expression input edit cannot become an initial standalone operation.",
            nameof(step)),
        ReplaceAssignmentValuePatchPlanStep assignmentValue => assignmentValue.ToOperation(),
        ReplaceWhileConditionPatchPlanStep whileCondition => whileCondition.ToOperation(),
        ReplaceProducedWhileConditionPatchPlanStep => throw new ArgumentException(
            "A symbolic produced-while condition edit cannot become an initial standalone operation.",
            nameof(step)),
        ReplaceDoWhileConditionPatchPlanStep doWhileCondition => doWhileCondition.ToOperation(),
        ReplaceProducedDoWhileConditionPatchPlanStep => throw new ArgumentException(
            "A symbolic produced-do-while condition edit cannot become an initial standalone operation.",
            nameof(step)),
        ReplaceForConditionPatchPlanStep forCondition => forCondition.ToOperation(),
        ReplaceProducedForConditionPatchPlanStep => throw new ArgumentException(
            "A symbolic produced-for condition edit cannot become an initial standalone operation.",
            nameof(step)),
        ReplaceForEachCollectionPatchPlanStep forEachCollection =>
            forEachCollection.ToOperation(),
        ReplaceProducedForEachCollectionPatchPlanStep => throw new ArgumentException(
            "A symbolic produced-foreach collection edit cannot become an initial standalone operation.",
            nameof(step)),
        ReplaceForDeclarationInitializerPatchPlanStep forDeclaration =>
            forDeclaration.ToOperation(),
        ReplaceForAssignmentInitializerValuePatchPlanStep forAssignment =>
            forAssignment.ToOperation(),
        ReplaceForIncrementorPatchPlanStep forIncrementor => forIncrementor.ToOperation(),
        ReplaceIfConditionPatchPlanStep ifCondition => ifCondition.ToOperation(),
        ReplaceProducedIfConditionPatchPlanStep => throw new ArgumentException(
            "A symbolic produced-if condition edit cannot become an initial standalone operation.",
            nameof(step)),
        ReplaceLocalInitializerPatchPlanStep initializer => initializer.ToOperation(),
        ReplaceProducedLocalInitializerPatchPlanStep => throw new ArgumentException(
            "A symbolic produced-local initializer edit cannot become an initial standalone operation.",
            nameof(step)),
        ReplaceReturnValuePatchPlanStep returnValue => returnValue.ToOperation(),
        ReplaceProducedReturnValuePatchPlanStep => throw new ArgumentException(
            "A symbolic produced-return value edit cannot become an initial standalone operation.",
            nameof(step)),
        InsertStatementPatchPlanStep insertion => insertion.ToOperation(),
        InsertBranchStatementPatchPlanStep => throw new ArgumentException(
            "A symbolic branch insertion cannot become an initial standalone operation.",
            nameof(step)),
        InsertLoopBodyStatementPatchPlanStep => throw new ArgumentException(
            "A symbolic loop-body insertion cannot become an initial standalone operation.",
            nameof(step)),
        DeleteStatementPatchPlanStep deletion => deletion.ToOperation(),
        IntroduceTemporaryPatchPlanStep introduction => introduction.ToOperation(),
        ReconnectValuePatchPlanStep => throw new ArgumentException(
            "A symbolic value reconnection cannot become an initial standalone operation.",
            nameof(step)),
        _ => throw new ArgumentOutOfRangeException(nameof(step), step.GetType().Name),
    };

    private readonly record struct ProducedIfBranches(
        SemanticId IfId,
        SemanticId ThenBlockId,
        SemanticId ElseBlockId);

    private readonly record struct ProducedLoop(
        SemanticId LoopId,
        SemanticId BodyBlockId);

    private static CSharpCompilationRequest AdvanceRequest(
        CSharpCompilationRequest patchedRequest,
        CSharpAnalysisResult analysis)
    {
        IReadOnlyDictionary<string, IdentitySidecarDocument> sidecars = analysis.IdentitySidecars
            .ToDictionary(static sidecar => sidecar.SourcePath, StringComparer.Ordinal);
        CSharpSourceDocument[] sources = patchedRequest.Sources
            .Select(source => new CSharpSourceDocument(
                source.LogicalPath,
                source.Source,
                sidecars.GetValueOrDefault(source.LogicalPath) ?? source.IdentitySidecar))
            .ToArray();
        return new CSharpCompilationRequest(
            patchedRequest.AssemblyName,
            sources,
            patchedRequest.ReferencePaths,
            patchedRequest.EffectCatalog,
            patchedRequest.Settings,
            patchedRequest.RuntimeReferencePaths,
            patchedRequest.IncludeDefaultReferences);
    }

    private static bool SiblingsMatch(
        HirPatchIndex.StatementLocation location,
        SemanticId? expectedPreviousSiblingId,
        SemanticId? expectedNextSiblingId)
    {
        GetSiblings(location, out SemanticId? previousSiblingId, out SemanticId? nextSiblingId);
        return previousSiblingId == expectedPreviousSiblingId && nextSiblingId == expectedNextSiblingId;
    }

    private static void GetSiblings(
        HirPatchIndex.StatementLocation location,
        out SemanticId? previousSiblingId,
        out SemanticId? nextSiblingId)
    {
        previousSiblingId = location.Index == 0
            ? null
            : location.ParentBlock.Statements[location.Index - 1].Id;
        nextSiblingId = location.Index == location.ParentBlock.Statements.Count - 1
            ? null
            : location.ParentBlock.Statements[location.Index + 1].Id;
    }

    private static UnifiedDiagnostic Diagnostic(
        string code,
        string message,
        SourceSpan span,
        SemanticId? nodeId = null) => new(
        code,
        UnifiedDiagnosticSeverity.Error,
        message,
        span,
        nodeId);

    private static CSharpPatchResult Failure(params UnifiedDiagnostic[] diagnostics) =>
        new(null, null, diagnostics);

    private static CSharpPatchResult Failure(IReadOnlyList<UnifiedDiagnostic> diagnostics) =>
        new(null, null, diagnostics);
}
