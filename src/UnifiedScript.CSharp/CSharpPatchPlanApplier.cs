using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Semantics;

namespace UnifiedScript.CSharp;

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
                    new ProducedIfBranches(insertedIf.Then.Id, insertedIf.Else.Id));
            }

            HirVariableDeclarationStatement? introduced = step switch
            {
                IntroduceTemporaryPatchPlanStep introduction => FindIntroducedDefinition(
                    stageResult.Analysis.Hir,
                    introduction),
                InsertStatementPatchPlanStep or InsertBranchStatementPatchPlanStep =>
                    insertedStatement as HirVariableDeclarationStatement,
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
            else
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
        out SemanticPatchOperation? operation,
        out UnifiedDiagnostic? diagnostic)
    {
        HirCompilationUnit compilation = current.Hir ??
            throw new ArgumentException("Plan-step rebasing requires HIR.", nameof(current));
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

    private static bool ProducesLocal(SemanticPatchPlanStep step) =>
        step is IntroduceTemporaryPatchPlanStep or
            InsertStatementPatchPlanStep { NewStatement: LocalLiteralDeclarationPatchStatement } or
            InsertBranchStatementPatchPlanStep
        {
            NewStatement: LocalLiteralDeclarationPatchStatement
        };

    private static bool ProducesBranches(SemanticPatchPlanStep step) =>
        step is InsertStatementPatchPlanStep { NewStatement: IfPatchStatement } or
            InsertBranchStatementPatchPlanStep { NewStatement: IfPatchStatement };

    private static bool TryGetStepMethod(
        SemanticPatchPlanStep step,
        HirNode target,
        HirPatchIndex patchIndex,
        out HirMethodDeclaration? method)
    {
        switch (step)
        {
            case InsertStatementPatchPlanStep or InsertBranchStatementPatchPlanStep
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

    private static SemanticPatchOperation GetInitialOperation(SemanticPatchPlanStep step) => step switch
    {
        ReplaceLiteralPatchPlanStep replace => replace.ToOperation(),
        ChangeOperatorPatchPlanStep changeOperator => changeOperator.ToOperation(),
        ReconnectArgumentPatchPlanStep reconnectArgument => reconnectArgument.ToOperation(),
        InsertStatementPatchPlanStep insertion => insertion.ToOperation(),
        InsertBranchStatementPatchPlanStep => throw new ArgumentException(
            "A symbolic branch insertion cannot become an initial standalone operation.",
            nameof(step)),
        DeleteStatementPatchPlanStep deletion => deletion.ToOperation(),
        IntroduceTemporaryPatchPlanStep introduction => introduction.ToOperation(),
        ReconnectValuePatchPlanStep => throw new ArgumentException(
            "A symbolic value reconnection cannot become an initial standalone operation.",
            nameof(step)),
        _ => throw new ArgumentOutOfRangeException(nameof(step), step.GetType().Name),
    };

    private readonly record struct ProducedIfBranches(
        SemanticId ThenBlockId,
        SemanticId ElseBlockId);

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
            patchedRequest.EffectCatalog);
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
