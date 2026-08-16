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

        UnifiedDiagnostic? initialFailure = ValidateInitialContexts(request, original, plan);
        if (initialFailure is not null)
        {
            return Failure(initialFailure);
        }

        CSharpCompilationRequest currentRequest = request;
        var introducedDefinitions = new Dictionary<string, SemanticId>(StringComparer.Ordinal);
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
                    introducedDefinitions,
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

            if (step is IntroduceTemporaryPatchPlanStep introduction)
            {
                HirVariableDeclarationStatement? introduced = FindIntroducedDefinition(
                    stageResult.Analysis.Hir,
                    introduction);
                if (introduced is null)
                {
                    return Failure(new UnifiedDiagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        UnifiedDiagnosticSeverity.Error,
                        $"Plan step '{step.StepId}' succeeded but its introduced local could not be " +
                        "resolved for later steps.",
                        SourceSpan.Unknown("<patch-plan>"),
                        step.TargetNodeId));
                }

                introducedDefinitions.Add(step.StepId, introduced.Id);
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

    private static UnifiedDiagnostic? ValidateInitialContexts(
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
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> bindings = semantics.Nodes
            .ToDictionary(static binding => binding.NodeId);
        var patchIndex = new HirPatchIndex(compilation);
        var introductionMethods = new Dictionary<string, SemanticId>(StringComparer.Ordinal);
        foreach (SemanticPatchPlanStep step in plan.Steps)
        {
            if (!nodes.TryGetValue(step.TargetNodeId, out HirNode? target))
            {
                return Diagnostic(
                    SemanticPatchDiagnosticCodes.TargetNotFound,
                    $"Patch-plan target '{step.TargetNodeId}' was not found in the initial HIR.",
                    SourceSpan.Unknown("<patch-plan>"),
                    step.TargetNodeId);
            }

            if (target is not HirExpression expression ||
                !patchIndex.TryGetExpression(
                    expression.Id,
                    out HirPatchIndex.ExpressionLocation? expressionLocation))
            {
                return Diagnostic(
                    SemanticPatchDiagnosticCodes.TargetKindMismatch,
                    $"Patch-plan target '{step.TargetNodeId}' is not an indexed expression.",
                    target.Span,
                    target.Id);
            }

            SemanticId expectedStatementId = GetExpectedContainingStatementId(step);
            if (expressionLocation!.ContainingStatement.Id != expectedStatementId)
            {
                return Diagnostic(
                    SemanticPatchDiagnosticCodes.PreconditionFailed,
                    $"Plan step '{step.StepId}' expected containing statement '{expectedStatementId}', " +
                    $"but its target is inside '{expressionLocation.ContainingStatement.Id}'.",
                    expression.Span,
                    expression.Id);
            }

            if (!patchIndex.TryGetStatement(
                    expectedStatementId,
                    out HirPatchIndex.StatementLocation? statementLocation) ||
                statementLocation!.ParentBlock.Id != GetExpectedParentBlockId(step) ||
                !SiblingsMatch(
                    statementLocation,
                    GetExpectedPreviousSiblingId(step),
                    GetExpectedNextSiblingId(step)))
            {
                return Diagnostic(
                    SemanticPatchDiagnosticCodes.SiblingPreconditionFailed,
                    $"Initial parent-block or sibling preconditions failed for plan step '{step.StepId}'.",
                    expression.Span,
                    expression.Id);
            }

            SemanticId expectedTypeId = GetExpectedTypeId(step);
            if (!bindings.TryGetValue(expression.Id, out BoundNodeInfo? binding) ||
                binding.TypeId != expectedTypeId)
            {
                return Diagnostic(
                    SemanticPatchDiagnosticCodes.PreconditionFailed,
                    $"Plan step '{step.StepId}' expected target type '{expectedTypeId}', but the " +
                    $"current type is '{binding?.TypeId}'.",
                    expression.Span,
                    expression.Id);
            }

            CSharpSourceDocument source = request.Sources.Single(
                candidate => candidate.LogicalPath == expression.Span.FilePath);
            if (source.IdentitySidecar is null)
            {
                return Diagnostic(
                    SemanticPatchDiagnosticCodes.PlanRequiresPersistentIdentity,
                    "A multi-stage patch plan requires a persistent identity sidecar on every " +
                    "target document so later stages can resolve nodes after source spans change.",
                    expression.Span,
                    expression.Id);
            }

            switch (step)
            {
                case IntroduceTemporaryPatchPlanStep introduction:
                    introductionMethods.Add(introduction.StepId, expressionLocation.Method.Id);
                    break;
                case ReconnectValuePatchPlanStep reconnect
                    when introductionMethods[reconnect.SourceStepId] != expressionLocation.Method.Id:
                    return Diagnostic(
                        SemanticPatchDiagnosticCodes.InvalidPatchPlan,
                        $"Reconnect step '{reconnect.StepId}' and source step " +
                        $"'{reconnect.SourceStepId}' target different methods.",
                        expression.Span,
                        expression.Id);
            }
        }

        return null;
    }

    private static bool TryCreateCurrentOperation(
        SemanticPatchPlanStep step,
        CSharpAnalysisResult current,
        IReadOnlyDictionary<string, SemanticId> introducedDefinitions,
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

        if (target is not HirExpression expression ||
            !patchIndex.TryGetExpression(
                expression.Id,
                out HirPatchIndex.ExpressionLocation? expressionLocation) ||
            !patchIndex.TryGetStatement(
                expressionLocation!.ContainingStatement.Id,
                out HirPatchIndex.StatementLocation? statementLocation))
        {
            operation = null;
            diagnostic = Diagnostic(
                SemanticPatchDiagnosticCodes.TargetKindMismatch,
                $"Plan step '{step.StepId}' no longer resolves to an indexed expression.",
                target.Span,
                target.Id);
            return false;
        }

        HirPatchIndex.ExpressionLocation resolvedExpressionLocation = expressionLocation!;
        HirPatchIndex.StatementLocation resolvedStatementLocation = statementLocation!;
        GetSiblings(
            resolvedStatementLocation,
            out SemanticId? previousSiblingId,
            out SemanticId? nextSiblingId);
        operation = step switch
        {
            IntroduceTemporaryPatchPlanStep introduction => new IntroduceTemporaryPatchOperation(
                introduction.TargetNodeId,
                resolvedExpressionLocation.ContainingStatement.Id,
                resolvedStatementLocation.ParentBlock.Id,
                previousSiblingId,
                nextSiblingId,
                introduction.Name,
                introduction.TypeName,
                introduction.ExpectedTypeId),
            ReconnectValuePatchPlanStep reconnect when introducedDefinitions.TryGetValue(
                reconnect.SourceStepId,
                out SemanticId sourceDefinitionId) => new ReconnectValuePatchOperation(
                    reconnect.TargetNodeId,
                    sourceDefinitionId,
                    resolvedExpressionLocation.ContainingStatement.Id,
                    resolvedStatementLocation.ParentBlock.Id,
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

    private static SemanticId GetExpectedContainingStatementId(SemanticPatchPlanStep step) => step switch
    {
        IntroduceTemporaryPatchPlanStep introduction => introduction.ExpectedContainingStatementId,
        ReconnectValuePatchPlanStep reconnect => reconnect.ExpectedContainingStatementId,
        _ => throw new ArgumentOutOfRangeException(nameof(step), step.GetType().Name),
    };

    private static SemanticId GetExpectedParentBlockId(SemanticPatchPlanStep step) => step switch
    {
        IntroduceTemporaryPatchPlanStep introduction => introduction.ExpectedParentBlockId,
        ReconnectValuePatchPlanStep reconnect => reconnect.ExpectedParentBlockId,
        _ => throw new ArgumentOutOfRangeException(nameof(step), step.GetType().Name),
    };

    private static SemanticId? GetExpectedPreviousSiblingId(SemanticPatchPlanStep step) => step switch
    {
        IntroduceTemporaryPatchPlanStep introduction => introduction.ExpectedPreviousSiblingId,
        ReconnectValuePatchPlanStep reconnect => reconnect.ExpectedPreviousSiblingId,
        _ => throw new ArgumentOutOfRangeException(nameof(step), step.GetType().Name),
    };

    private static SemanticId? GetExpectedNextSiblingId(SemanticPatchPlanStep step) => step switch
    {
        IntroduceTemporaryPatchPlanStep introduction => introduction.ExpectedNextSiblingId,
        ReconnectValuePatchPlanStep reconnect => reconnect.ExpectedNextSiblingId,
        _ => throw new ArgumentOutOfRangeException(nameof(step), step.GetType().Name),
    };

    private static SemanticId GetExpectedTypeId(SemanticPatchPlanStep step) => step switch
    {
        IntroduceTemporaryPatchPlanStep introduction => introduction.ExpectedTypeId,
        ReconnectValuePatchPlanStep reconnect => reconnect.ExpectedTypeId,
        _ => throw new ArgumentOutOfRangeException(nameof(step), step.GetType().Name),
    };

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
