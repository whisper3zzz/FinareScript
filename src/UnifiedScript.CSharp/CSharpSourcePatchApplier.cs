using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Graph;
using UnifiedScript.Semantics;

namespace UnifiedScript.CSharp;

public sealed class CSharpSourcePatchApplier
{
    private const string IdentityAnnotationKind = "UnifiedScript.IdentityPin";
    private const string PatchLocatorAnnotationKind = "UnifiedScript.PatchLocator";
    private static readonly SymbolDisplayFormat ExactPatchTypeMetadataFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
    private static readonly IReadOnlyDictionary<string, string> BinaryOperatorFamilies =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["+"] = "arithmetic",
            ["-"] = "arithmetic",
            ["*"] = "arithmetic",
            ["/"] = "arithmetic",
            ["%"] = "arithmetic",
            ["<<"] = "shift",
            [">>"] = "shift",
            ["<"] = "relational",
            [">"] = "relational",
            ["<="] = "relational",
            [">="] = "relational",
            ["=="] = "equality",
            ["!="] = "equality",
            ["&"] = "bitwise",
            ["^"] = "bitwise",
            ["|"] = "bitwise",
            ["&&"] = "conditionalLogical",
            ["||"] = "conditionalLogical",
        };
    private static readonly IReadOnlyDictionary<string, string> UnaryOperatorFamilies =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["+"] = "numericSign",
            ["-"] = "numericSign",
            ["!"] = "logicalNot",
            ["~"] = "bitwiseNot",
        };
    private static readonly IReadOnlySet<string> StableTemporaryBinaryOperators =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "+",
            "-",
            "*",
            "<<",
            ">>",
            "<",
            ">",
            "<=",
            ">=",
            "==",
            "!=",
            "&",
            "^",
            "|",
        };
    private static readonly IReadOnlySet<string> GraphExpressionBinaryOperators =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "+",
            "-",
            "*",
            "<<",
            ">>",
            "<",
            ">",
            "<=",
            ">=",
            "==",
            "!=",
            "&",
            "^",
            "|",
            "&&",
            "||",
            "??",
        };
    private static readonly IReadOnlySet<string> GraphExpressionUnaryOperators =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "+",
            "-",
            "!",
            "~",
        };

    private readonly CSharpSemanticPipeline pipeline = new();

    public CSharpPatchResult Apply(
        CSharpCompilationRequest request,
        SemanticPatchDocument patch)
    {
        ArgumentNullException.ThrowIfNull(request);
        SemanticPatchJson.Validate(patch);

        CSharpAnalysisResult original = pipeline.Analyze(request);
        if (!original.Succeeded || original.Hir is null)
        {
            return Failure(original.Diagnostics);
        }

        if (patch.SourceCompilationId != original.Hir.Id)
        {
            return Failure(new UnifiedDiagnostic(
                SemanticPatchDiagnosticCodes.BaselineMismatch,
                UnifiedDiagnosticSeverity.Error,
                $"Patch baseline '{patch.SourceCompilationId}' does not match current source " +
                $"compilation '{original.Hir.Id}'. Re-analyze the source before applying the patch.",
                SourceSpan.Unknown("<patch>")));
        }

        if (original.Semantics is null ||
            patch.SemanticConfigurationId != original.Semantics.ConfigurationId)
        {
            return Failure(new UnifiedDiagnostic(
                SemanticPatchDiagnosticCodes.ConfigurationMismatch,
                UnifiedDiagnosticSeverity.Error,
                $"Patch semantic configuration '{patch.SemanticConfigurationId}' does not match current " +
                $"configuration '{original.Semantics?.ConfigurationId}'. Re-analyze the graph before " +
                "applying the patch.",
                SourceSpan.Unknown("<patch>")));
        }

        IReadOnlyDictionary<SemanticId, HirNode> hirNodes = original.Hir
            .DescendantsAndSelf()
            .ToDictionary(static node => node.Id);
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes = original.Semantics.Nodes
            .ToDictionary(static node => node.NodeId);
        IReadOnlyDictionary<SemanticId, MethodDescriptor> methods = original.Semantics.Methods
            .ToDictionary(static method => method.Id);
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types = original.Semantics.Types
            .ToDictionary(static type => type.Id);
        var patchIndex = new HirPatchIndex(original.Hir);
        var graphSafetyIndex = new GraphPatchSafetyIndex(
            original.Hir,
            original.Semantics,
            patchIndex);
        var useDefinitionIndex = new HirUseDefinitionIndex(original.Hir, boundNodes);
        GraphDocument graph = new HirGraphProjector().Project(
            original.Hir,
            original.Semantics,
            original.DeclarationReferences);
        var documents = request.Sources.ToDictionary(
            static source => source.LogicalPath,
            source => new ParsedDocument(
                source,
                CSharpSyntaxTree.ParseText(
                    source.Source,
                    request.Settings.ParseOptions,
                    source.LogicalPath)));
        var replacements = new Dictionary<string, List<PendingReplacement>>(StringComparer.Ordinal);

        foreach (SemanticPatchOperation operation in patch.Operations
                     .OrderBy(static operation => operation.TargetNodeId.Value, StringComparer.Ordinal))
        {
            OperationPlan plan = operation switch
            {
                ReplaceLiteralPatchOperation replace => PlanLiteralReplacement(
                    replace,
                    hirNodes,
                    documents),
                ChangeOperatorPatchOperation changeOperator => PlanOperatorChange(
                    changeOperator,
                    hirNodes,
                    boundNodes,
                    documents),
                ReconnectArgumentPatchOperation reconnectArgument => PlanArgumentReconnection(
                    reconnectArgument,
                    hirNodes,
                    boundNodes,
                    methods,
                    documents),
                ReplaceExpressionInputPatchOperation expressionInput =>
                    PlanExpressionInputReplacement(
                        expressionInput,
                        hirNodes,
                        patchIndex,
                        useDefinitionIndex,
                        boundNodes,
                        methods,
                        types,
                        graphSafetyIndex,
                        documents),
                ReplaceAssignmentValuePatchOperation assignmentValue =>
                    PlanAssignmentValueReplacement(
                        assignmentValue,
                        hirNodes,
                        patchIndex,
                        boundNodes,
                        types,
                        graphSafetyIndex,
                        documents),
                ReplaceWhileConditionPatchOperation whileCondition =>
                    PlanWhileConditionReplacement(
                        whileCondition,
                        hirNodes,
                        patchIndex,
                        boundNodes,
                        types,
                        graphSafetyIndex,
                        documents),
                ReplaceDoWhileConditionPatchOperation doWhileCondition =>
                    PlanDoWhileConditionReplacement(
                        doWhileCondition,
                        hirNodes,
                        patchIndex,
                        boundNodes,
                        types,
                        graphSafetyIndex,
                        documents),
                ReplaceForConditionPatchOperation forCondition =>
                    PlanForConditionReplacement(
                        forCondition,
                        hirNodes,
                        patchIndex,
                        boundNodes,
                        types,
                        graphSafetyIndex,
                        documents),
                ReplaceForEachCollectionPatchOperation forEachCollection =>
                    PlanForEachCollectionReplacement(
                        forEachCollection,
                        hirNodes,
                        patchIndex,
                        boundNodes,
                        types,
                        graphSafetyIndex,
                        documents),
                ReplaceForDeclarationInitializerPatchOperation forDeclaration =>
                    PlanForDeclarationInitializerReplacement(
                        forDeclaration,
                        hirNodes,
                        patchIndex,
                        boundNodes,
                        types,
                        graphSafetyIndex,
                        documents),
                ReplaceForAssignmentInitializerValuePatchOperation forAssignment =>
                    PlanForAssignmentInitializerValueReplacement(
                        forAssignment,
                        hirNodes,
                        patchIndex,
                        boundNodes,
                        types,
                        graphSafetyIndex,
                        documents),
                ReplaceForIncrementorPatchOperation forIncrementor =>
                    PlanForIncrementorReplacement(
                        forIncrementor,
                        hirNodes,
                        patchIndex,
                        boundNodes,
                        types,
                        graphSafetyIndex,
                        documents),
                ReplaceIfConditionPatchOperation ifCondition =>
                    PlanIfConditionReplacement(
                        ifCondition,
                        hirNodes,
                        patchIndex,
                        boundNodes,
                        types,
                        graphSafetyIndex,
                        documents),
                ReplaceLocalInitializerPatchOperation localInitializer =>
                    PlanLocalInitializerReplacement(
                        localInitializer,
                        hirNodes,
                        patchIndex,
                        boundNodes,
                        types,
                        graphSafetyIndex,
                        documents),
                ReplaceReturnValuePatchOperation returnValue =>
                    PlanReturnValueReplacement(
                        returnValue,
                        hirNodes,
                        patchIndex,
                        boundNodes,
                        methods,
                        types,
                        graphSafetyIndex,
                        documents),
                InsertStatementPatchOperation insertStatement => PlanStatementInsertion(
                    insertStatement,
                    hirNodes,
                    patchIndex,
                    boundNodes,
                    methods,
                    types,
                    graphSafetyIndex,
                    documents),
                DeleteStatementPatchOperation deleteStatement => PlanStatementDeletion(
                    deleteStatement,
                    hirNodes,
                    patchIndex,
                    useDefinitionIndex,
                    boundNodes,
                    methods,
                    documents),
                MoveStatementPatchOperation moveStatement => PlanStatementMove(
                    moveStatement,
                    hirNodes,
                    patchIndex,
                    boundNodes,
                    documents),
                InsertSwitchSectionPatchOperation insertSwitchSection =>
                    PlanSwitchSectionInsertion(
                        insertSwitchSection,
                        hirNodes,
                        patchIndex,
                        boundNodes,
                        types,
                        graphSafetyIndex,
                        documents),
                ReplaceSwitchSectionPatchOperation replaceSwitchSection =>
                    PlanSwitchSectionReplacement(
                        replaceSwitchSection,
                        hirNodes,
                        patchIndex,
                        boundNodes,
                        types,
                        graphSafetyIndex,
                        documents),
                MoveSwitchSectionPatchOperation moveSwitchSection => PlanSwitchSectionMove(
                    moveSwitchSection,
                    hirNodes,
                    documents),
                DeleteSwitchSectionPatchOperation deleteSwitchSection =>
                    PlanSwitchSectionDeletion(
                        deleteSwitchSection,
                        hirNodes,
                        documents),
                IntroduceTemporaryPatchOperation introduceTemporary => PlanTemporaryIntroduction(
                    introduceTemporary,
                    hirNodes,
                    patchIndex,
                    useDefinitionIndex,
                    boundNodes,
                    methods,
                    types,
                    documents),
                ReconnectValuePatchOperation reconnectValue => PlanValueReconnection(
                    reconnectValue,
                    hirNodes,
                    patchIndex,
                    useDefinitionIndex,
                    boundNodes,
                    methods,
                    types,
                    graphSafetyIndex,
                    documents),
                ReconnectDataEdgePatchOperation reconnectDataEdge => PlanDataEdgeReconnection(
                    reconnectDataEdge,
                    graph,
                    hirNodes,
                    patchIndex,
                    useDefinitionIndex,
                    boundNodes,
                    methods,
                    types,
                    graphSafetyIndex,
                    documents),
                DisconnectDataEdgePatchOperation disconnectDataEdge => PlanDataEdgeDisconnection(
                    disconnectDataEdge,
                    graph,
                    hirNodes,
                    patchIndex,
                    useDefinitionIndex,
                    boundNodes,
                    methods,
                    types,
                    graphSafetyIndex,
                    documents),
                _ => throw new InvalidOperationException(
                    $"Validated patch operation '{operation.GetType().Name}' was not handled."),
            };
            if (plan.Diagnostic is not null)
            {
                return Failure(plan.Diagnostic);
            }

            PendingReplacement replacement = plan.Replacement ??
                throw new InvalidOperationException("A successful patch plan did not contain a replacement.");
            if (!TryAddReplacement(replacements, replacement, out UnifiedDiagnostic? overlapDiagnostic))
            {
                return Failure(overlapDiagnostic!);
            }
        }

        IReadOnlyDictionary<string, IdentitySidecarDocument> effectiveSidecars = original.IdentitySidecars
            .ToDictionary(static sidecar => sidecar.SourcePath, StringComparer.Ordinal);
        CSharpSourceDocument[] patchedSources = request.Sources
            .Select(source => ReplaceDocument(
                source,
                documents[source.LogicalPath],
                replacements,
                effectiveSidecars.GetValueOrDefault(source.LogicalPath),
                hirNodes))
            .ToArray();
        var patchedRequest = new CSharpCompilationRequest(
            request.AssemblyName,
            patchedSources,
            request.ReferencePaths,
            request.EffectCatalog,
            request.Settings,
            request.RuntimeReferencePaths,
            request.IncludeDefaultReferences);
        CSharpAnalysisResult patchedAnalysis = pipeline.Analyze(patchedRequest);
        if (!patchedAnalysis.Succeeded)
        {
            return Failure(patchedAnalysis.Diagnostics);
        }

        SemanticPatchOperation? controlFlowOperation = patch.Operations.FirstOrDefault(
            static operation => operation is MoveStatementPatchOperation or
                InsertSwitchSectionPatchOperation or ReplaceSwitchSectionPatchOperation or
                MoveSwitchSectionPatchOperation or DeleteSwitchSectionPatchOperation ||
                operation is InsertStatementPatchOperation
                {
                    NewStatement: SwitchPatchStatement,
                });
        if (controlFlowOperation is not null)
        {
            try
            {
                GraphDocument patchedGraph = new HirGraphProjector().Project(
                    patchedAnalysis.Hir!,
                    patchedAnalysis.Semantics!,
                    patchedAnalysis.DeclarationReferences);
                _ = new GraphControlFlowAnalyzer().AnalyzeAndValidate(patchedGraph);
            }
            catch (InvalidOperationException exception)
            {
                HirNode? target = hirNodes.GetValueOrDefault(controlFlowOperation.TargetNodeId);
                return Failure(new UnifiedDiagnostic(
                    controlFlowOperation is MoveStatementPatchOperation
                        ? SemanticPatchDiagnosticCodes.InvalidControlFlowReconnection
                        : SemanticPatchDiagnosticCodes.InvalidSwitchSection,
                    UnifiedDiagnosticSeverity.Error,
                    "The structured control edit was rejected because its projected CFG is " +
                    $"invalid or irreducible: {exception.Message}",
                    target?.Span ?? SourceSpan.Unknown("<patch>"),
                    controlFlowOperation.TargetNodeId));
            }
        }

        UnifiedDiagnostic? authoredExpressionFailure =
            ValidateConditionalMemberAccessResults(patch, patchedRequest, patchedAnalysis);
        if (authoredExpressionFailure is not null)
        {
            return Failure(authoredExpressionFailure);
        }

        return new CSharpPatchResult(patchedRequest, patchedAnalysis, patchedAnalysis.Diagnostics);
    }

    private static UnifiedDiagnostic? ValidateConditionalMemberAccessResults(
        SemanticPatchDocument patch,
        CSharpCompilationRequest request,
        CSharpAnalysisResult analysis)
    {
        HirCompilationUnit hir = analysis.Hir ??
            throw new ArgumentException("Patched expression validation requires HIR.", nameof(analysis));
        BoundCompilation semantics = analysis.Semantics ??
            throw new ArgumentException(
                "Patched expression validation requires bound semantics.",
                nameof(analysis));
        IReadOnlyDictionary<SemanticId, HirNode> nodes = hir
            .DescendantsAndSelf()
            .ToDictionary(static node => node.Id);
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes = semantics.Nodes
            .ToDictionary(static node => node.NodeId);
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types = semantics.Types
            .ToDictionary(static type => type.Id);
        SyntaxTree[] syntaxTrees = request.Sources
            .Select(source => CSharpSyntaxTree.ParseText(
                source.Source,
                request.Settings.ParseOptions,
                source.LogicalPath))
            .ToArray();
        CSharpCompilation compilation = new CSharpCompilationHost().Create(request, syntaxTrees);
        IReadOnlyDictionary<string, ExactBindingContext> exactContexts = syntaxTrees
            .ToDictionary(
                static tree => tree.FilePath,
                tree => new ExactBindingContext(
                    compilation.GetSemanticModel(tree, ignoreAccessibility: false),
                    tree.GetRoot()),
                StringComparer.Ordinal);

        foreach (SemanticPatchOperation operation in patch.Operations)
        {
            PatchExpression? authored = GetAuthoredPatchExpression(operation);
            if (authored is null || !ContainsConditionalMemberAccess(authored))
            {
                continue;
            }

            HirExpression? actual = FindPatchedAuthoredExpression(operation, nodes);
            if (actual is null)
            {
                return new UnifiedDiagnostic(
                    SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                    UnifiedDiagnosticSeverity.Error,
                    "The emitted conditional member access could not be correlated with its " +
                    "post-patch HIR root.",
                    SourceSpan.Unknown("<patch>"),
                    operation.TargetNodeId);
            }

            if (!TryValidateAuthoredExpression(
                    authored,
                    actual,
                    boundNodes,
                    types,
                    exactContexts,
                    out string? failure))
            {
                return new UnifiedDiagnostic(
                    SemanticPatchDiagnosticCodes.TypeMismatch,
                    UnifiedDiagnosticSeverity.Error,
                    failure ?? "The emitted conditional member access changed semantic shape or type.",
                    actual.Span,
                    actual.Id);
            }
        }

        return null;
    }

    private static PatchExpression? GetAuthoredPatchExpression(
        SemanticPatchOperation operation) => operation switch
        {
            ReplaceExpressionInputPatchOperation replacement => replacement.NewInput,
            ReplaceAssignmentValuePatchOperation replacement => replacement.NewValue,
            ReplaceWhileConditionPatchOperation replacement => replacement.NewCondition,
            ReplaceDoWhileConditionPatchOperation replacement => replacement.NewCondition,
            ReplaceForConditionPatchOperation replacement => replacement.NewCondition,
            ReplaceForEachCollectionPatchOperation replacement => replacement.NewCollection,
            ReplaceForDeclarationInitializerPatchOperation replacement =>
                replacement.NewInitializer,
            ReplaceForAssignmentInitializerValuePatchOperation replacement => replacement.NewValue,
            ReplaceForIncrementorPatchOperation replacement => replacement.NewOperand,
            ReplaceIfConditionPatchOperation replacement => replacement.NewCondition,
            ReplaceLocalInitializerPatchOperation replacement => replacement.NewInitializer,
            ReplaceReturnValuePatchOperation replacement => replacement.NewValue,
            InsertStatementPatchOperation
            {
                NewStatement: LocalAssignmentPatchStatement assignment,
            } => assignment.Value,
            InsertStatementPatchOperation
            {
                NewStatement: LocalDeclarationPatchStatement local,
            } => local.Initializer,
            InsertStatementPatchOperation
            {
                NewStatement: IfPatchStatement branch,
            } => branch.Condition,
            InsertStatementPatchOperation
            {
                NewStatement: WhilePatchStatement loop,
            } => loop.Condition,
            InsertStatementPatchOperation
            {
                NewStatement: DoWhilePatchStatement loop,
            } => loop.Condition,
            InsertStatementPatchOperation
            {
                NewStatement: ForEachPatchStatement loop,
            } => loop.Collection,
            InsertStatementPatchOperation
            {
                NewStatement: ReturnPatchStatement { Expression: not null } valueReturn,
            } => valueReturn.Expression,
            _ => null,
        };

    private static bool ContainsConditionalMemberAccess(PatchExpression expression) => expression switch
    {
        ConditionalMemberAccessPatchExpression => true,
        UnaryPatchExpression unary => ContainsConditionalMemberAccess(unary.Operand),
        BinaryPatchExpression binary =>
            ContainsConditionalMemberAccess(binary.Left) ||
            ContainsConditionalMemberAccess(binary.Right),
        CastPatchExpression cast => ContainsConditionalMemberAccess(cast.Operand),
        ConditionalPatchExpression conditional =>
            ContainsConditionalMemberAccess(conditional.Condition) ||
            ContainsConditionalMemberAccess(conditional.WhenTrue) ||
            ContainsConditionalMemberAccess(conditional.WhenFalse),
        MemberAccessPatchExpression member => ContainsConditionalMemberAccess(member.Receiver),
        MemberInvocationPatchExpression invocation =>
            ContainsConditionalMemberAccess(invocation.Receiver) ||
            invocation.Arguments.Any(ContainsConditionalMemberAccess),
        _ => false,
    };

    private static HirExpression? FindPatchedAuthoredExpression(
        SemanticPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> nodes)
    {
        switch (operation)
        {
            case ReplaceExpressionInputPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? targetNode) &&
                targetNode is HirExpression target &&
                TryGetExpressionInput(
                    target,
                    replacement.InputPort,
                    out HirExpression? input,
                    out _):
                return input;
            case ReplaceAssignmentValuePatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? targetNode) &&
                targetNode is HirAssignmentExpression assignment:
                return assignment.Value;
            case ReplaceWhileConditionPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? targetNode) &&
                targetNode is HirWhileStatement loop:
                return loop.Condition;
            case ReplaceDoWhileConditionPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? targetNode) &&
                targetNode is HirDoWhileStatement loop:
                return loop.Condition;
            case ReplaceForConditionPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? targetNode) &&
                targetNode is HirForStatement loop:
                return loop.Condition;
            case ReplaceForEachCollectionPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? targetNode) &&
                targetNode is HirForEachStatement loop:
                return loop.Collection;
            case ReplaceForDeclarationInitializerPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? targetNode) &&
                targetNode is HirForVariableDeclarator variable:
                return variable.Initializer;
            case ReplaceForAssignmentInitializerValuePatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? targetNode) &&
                targetNode is HirAssignmentExpression assignment:
                return assignment.Value;
            case ReplaceForIncrementorPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? targetNode) &&
                targetNode is HirUnaryExpression incrementor:
                return incrementor.Operand;
            case ReplaceIfConditionPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? targetNode) &&
                targetNode is HirIfStatement branch:
                return branch.Condition;
            case ReplaceLocalInitializerPatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? targetNode) &&
                targetNode is HirVariableDeclarationStatement local:
                return local.Initializer;
            case ReplaceReturnValuePatchOperation replacement
                when nodes.TryGetValue(replacement.TargetNodeId, out HirNode? targetNode) &&
                targetNode is HirReturnStatement valueReturn:
                return valueReturn.Expression;
            case InsertStatementPatchOperation insertion
                when nodes.TryGetValue(insertion.TargetNodeId, out HirNode? targetNode) &&
                targetNode is HirBlockStatement block:
                return GetInsertedAuthoredExpression(FindInsertedStatement(block, insertion));
            default:
                return null;
        }
    }

    private static HirStatement? FindInsertedStatement(
        HirBlockStatement block,
        InsertStatementPatchOperation insertion)
    {
        int previousIndex = insertion.ExpectedPreviousSiblingId is SemanticId previousId
            ? block.Statements.ToList().FindIndex(statement => statement.Id == previousId)
            : -1;
        int nextIndex = insertion.ExpectedNextSiblingId is SemanticId nextId
            ? block.Statements.ToList().FindIndex(statement => statement.Id == nextId)
            : -1;
        int insertedIndex = previousIndex >= 0
            ? previousIndex + 1
            : nextIndex >= 0
                ? nextIndex - 1
                : block.Statements.Count == 1
                    ? 0
                    : -1;
        if (insertedIndex < 0 || insertedIndex >= block.Statements.Count)
        {
            return null;
        }

        if (nextIndex >= 0 && insertedIndex + 1 != nextIndex)
        {
            return null;
        }

        return block.Statements[insertedIndex];
    }

    private static HirExpression? GetInsertedAuthoredExpression(HirStatement? statement) => statement switch
    {
        HirExpressionStatement { Expression: HirAssignmentExpression assignment } => assignment.Value,
        HirIfStatement branch => branch.Condition,
        HirWhileStatement loop => loop.Condition,
        HirDoWhileStatement loop => loop.Condition,
        HirForEachStatement loop => loop.Collection,
        HirReturnStatement valueReturn => valueReturn.Expression,
        _ => null,
    };

    private static bool TryValidateAuthoredExpression(
        PatchExpression expected,
        HirExpression actual,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        IReadOnlyDictionary<string, ExactBindingContext> exactContexts,
        out string? failure)
    {
        boundNodes.TryGetValue(actual.Id, out BoundNodeInfo? binding);
        types.TryGetValue(expected.ExpectedTypeId, out TypeDescriptor? expectedType);
        TryGetExactExpressionType(actual, exactContexts, out ITypeSymbol? exactType);
        if (binding is null ||
            binding.TypeId != expected.ExpectedTypeId ||
            expectedType is null ||
            exactType is null ||
            !string.Equals(
                exactType.ToDisplayString(ExactPatchTypeMetadataFormat),
                expectedType.MetadataName,
                StringComparison.Ordinal))
        {
            string exactTypeName = exactType?.ToDisplayString(ExactPatchTypeMetadataFormat) ?? "<none>";
            failure = $"Graph-authored expression '{actual.Id}' has exact Roslyn type " +
                $"'{exactTypeName}' and converted semantic type '{binding?.TypeId}', not its " +
                $"declared exact type '{expectedType?.MetadataName}' " +
                $"('{expected.ExpectedTypeId}'). Hidden C# conversions are not permitted in the " +
                "shared-VM expression subset.";
            return false;
        }

        bool valid = (expected, actual) switch
        {
            (LiteralPatchExpression literal, HirLiteralExpression hirLiteral) =>
                string.Equals(literal.ValueText, hirLiteral.ValueText, StringComparison.Ordinal),
            (LocalReferencePatchExpression local, HirIdentifierExpression identifier) =>
                string.Equals(local.ExpectedName, identifier.Name, StringComparison.Ordinal) &&
                binding.ReferencedSymbolId == local.SourceDefinitionId,
            (UnaryPatchExpression unary, HirUnaryExpression hirUnary) =>
                !hirUnary.IsPostfix &&
                string.Equals(unary.Operator, hirUnary.Operator, StringComparison.Ordinal) &&
                TryValidateAuthoredExpression(
                    unary.Operand,
                    hirUnary.Operand,
                    boundNodes,
                    types,
                    exactContexts,
                    out _),
            (BinaryPatchExpression binary, HirBinaryExpression hirBinary) =>
                string.Equals(binary.Operator, hirBinary.Operator, StringComparison.Ordinal) &&
                TryValidateAuthoredExpression(
                    binary.Left,
                    hirBinary.Left,
                    boundNodes,
                    types,
                    exactContexts,
                    out _) &&
                TryValidateAuthoredExpression(
                    binary.Right,
                    hirBinary.Right,
                    boundNodes,
                    types,
                    exactContexts,
                    out _),
            (CastPatchExpression cast, HirCastExpression hirCast) =>
                string.Equals(cast.TypeName, hirCast.TypeName, StringComparison.Ordinal) &&
                TryValidateAuthoredExpression(
                    cast.Operand,
                    hirCast.Operand,
                    boundNodes,
                    types,
                    exactContexts,
                    out _),
            (ConditionalPatchExpression conditional, HirConditionalExpression hirConditional) =>
                TryValidateAuthoredExpression(
                    conditional.Condition,
                    hirConditional.Condition,
                    boundNodes,
                    types,
                    exactContexts,
                    out _) &&
                TryValidateAuthoredExpression(
                    conditional.WhenTrue,
                    hirConditional.WhenTrue,
                    boundNodes,
                    types,
                    exactContexts,
                    out _) &&
                TryValidateAuthoredExpression(
                    conditional.WhenFalse,
                    hirConditional.WhenFalse,
                    boundNodes,
                    types,
                    exactContexts,
                    out _),
            (
                ConditionalMemberAccessPatchExpression conditionalAccess,
                HirConditionalAccessExpression
                {
                    WhenNotNull: HirConditionalMemberBindingExpression memberBinding,
                } hirConditionalAccess) =>
                string.Equals(
                    conditionalAccess.MemberName,
                    memberBinding.MemberName,
                    StringComparison.Ordinal) &&
                TryValidateAuthoredExpression(
                    conditionalAccess.Receiver,
                    hirConditionalAccess.Receiver,
                    boundNodes,
                    types,
                    exactContexts,
                    out _),
            _ => false,
        };
        failure = valid
            ? null
            : $"Graph-authored expression '{actual.Id}' did not retain the declared closed " +
                $"'{expected.GetType().Name}' structure after Roslyn reanalysis.";
        return valid;
    }

    private static bool TryGetExactExpressionType(
        HirExpression expression,
        IReadOnlyDictionary<string, ExactBindingContext> contexts,
        out ITypeSymbol? type)
    {
        type = null;
        if (!contexts.TryGetValue(expression.Span.FilePath, out ExactBindingContext? context))
        {
            return false;
        }

        ExpressionSyntax[] matches = context.Root
            .DescendantNodesAndSelf()
            .OfType<ExpressionSyntax>()
            .Where(candidate => RoslynSourceSpan.FromNode(
                candidate,
                expression.Span.FilePath) == expression.Span)
            .ToArray();
        if (matches.Length != 1)
        {
            return false;
        }

        type = context.SemanticModel.GetTypeInfo(matches[0]).Type;
        return type is not null;
    }

    private sealed record ExactBindingContext(SemanticModel SemanticModel, SyntaxNode Root);

    private static OperationPlan PlanLiteralReplacement(
        ReplaceLiteralPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirLiteralExpression literal)
        {
            return KindMismatch(target, operation.TargetNodeId, "a literal");
        }

        if (!string.Equals(literal.ValueText, operation.ExpectedValueText, StringComparison.Ordinal))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Literal precondition failed: expected '{operation.ExpectedValueText}', " +
                $"but the current value is '{literal.ValueText}'.",
                literal);
        }

        if (!TryFindSyntax(documents, literal, out ParsedDocument? document, out LiteralExpressionSyntax? syntax))
        {
            return MissingSyntax(literal, "literal");
        }

        if (!TryParseLiteral(operation.NewValueText, out LiteralExpressionSyntax? parsed))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidReplacement,
                $"Replacement '{operation.NewValueText}' is not one complete C# literal expression.",
                literal);
        }

        var annotation = new SyntaxAnnotation(IdentityAnnotationKind, literal.Id.Value);
        LiteralExpressionSyntax replacement = parsed!
            .WithTriviaFrom(syntax!)
            .WithAdditionalAnnotations(annotation);
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            syntax!,
            replacement,
            [new PendingPin(literal.Id, "literal", annotation)]));
    }

    private static OperationPlan PlanOperatorChange(
        ChangeOperatorPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        return target switch
        {
            HirBinaryExpression binary => PlanBinaryOperatorChange(
                operation,
                binary,
                boundNodes,
                documents),
            HirUnaryExpression unary => PlanUnaryOperatorChange(
                operation,
                unary,
                boundNodes,
                documents),
            _ => KindMismatch(target, operation.TargetNodeId, "a unary or binary expression"),
        };
    }

    private static OperationPlan PlanBinaryOperatorChange(
        ChangeOperatorPatchOperation operation,
        HirBinaryExpression binary,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        OperationPlan? resultTypeFailure = RequireType(
            binary,
            operation.ExpectedResultTypeId,
            "operator result",
            boundNodes);
        if (resultTypeFailure is not null)
        {
            return resultTypeFailure;
        }

        if (!string.Equals(binary.Operator, operation.ExpectedOperator, StringComparison.Ordinal))
        {
            return OperatorPreconditionFailure(operation, binary.Operator, binary);
        }

        if (operation.ExpectedRightOperandTypeId is not SemanticId expectedRightTypeId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                "A binary operator patch requires an expected right-operand type ID.",
                binary);
        }

        OperationPlan? leftTypeFailure = RequireType(
            binary.Left,
            operation.ExpectedOperandTypeId,
            "left operand",
            boundNodes);
        if (leftTypeFailure is not null)
        {
            return leftTypeFailure;
        }

        OperationPlan? rightTypeFailure = RequireType(
            binary.Right,
            expectedRightTypeId,
            "right operand",
            boundNodes);
        if (rightTypeFailure is not null)
        {
            return rightTypeFailure;
        }

        if (!AreOperatorsCompatible(
                BinaryOperatorFamilies,
                operation.ExpectedOperator,
                operation.NewOperator))
        {
            return InvalidOperatorFamily(operation, "binary", binary);
        }

        if (!TryFindSyntax(documents, binary, out ParsedDocument? document, out BinaryExpressionSyntax? syntax))
        {
            return MissingSyntax(binary, "binary expression");
        }

        if (!TryParseBinaryOperator(operation.NewOperator, out BinaryExpressionSyntax? parsed))
        {
            return InvalidOperatorSyntax(operation, binary);
        }

        var annotation = new SyntaxAnnotation(IdentityAnnotationKind, binary.Id.Value);
        SyntaxToken operatorToken = SyntaxFactory.Token(
            syntax!.OperatorToken.LeadingTrivia,
            parsed!.OperatorToken.Kind(),
            operation.NewOperator,
            operation.NewOperator,
            syntax.OperatorToken.TrailingTrivia);
        BinaryExpressionSyntax replacement = SyntaxFactory.BinaryExpression(
                parsed.Kind(),
                syntax.Left,
                operatorToken,
                syntax.Right)
            .WithAdditionalAnnotations(annotation);
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            syntax,
            replacement,
            [new PendingPin(binary.Id, "binary", annotation)]));
    }

    private static OperationPlan PlanUnaryOperatorChange(
        ChangeOperatorPatchOperation operation,
        HirUnaryExpression unary,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        OperationPlan? resultTypeFailure = RequireType(
            unary,
            operation.ExpectedResultTypeId,
            "operator result",
            boundNodes);
        if (resultTypeFailure is not null)
        {
            return resultTypeFailure;
        }

        if (!string.Equals(unary.Operator, operation.ExpectedOperator, StringComparison.Ordinal))
        {
            return OperatorPreconditionFailure(operation, unary.Operator, unary);
        }

        if (operation.ExpectedRightOperandTypeId is not null)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                "A unary operator patch cannot specify a right-operand type ID.",
                unary);
        }

        OperationPlan? operandTypeFailure = RequireType(
            unary.Operand,
            operation.ExpectedOperandTypeId,
            "operand",
            boundNodes);
        if (operandTypeFailure is not null)
        {
            return operandTypeFailure;
        }

        if (!AreOperatorsCompatible(
                UnaryOperatorFamilies,
                operation.ExpectedOperator,
                operation.NewOperator))
        {
            return InvalidOperatorFamily(operation, "unary", unary);
        }

        if (!TryFindSyntax(documents, unary, out ParsedDocument? document, out PrefixUnaryExpressionSyntax? syntax))
        {
            return MissingSyntax(unary, "unary expression");
        }

        if (!TryParseUnaryOperator(operation.NewOperator, out PrefixUnaryExpressionSyntax? parsed))
        {
            return InvalidOperatorSyntax(operation, unary);
        }

        var annotation = new SyntaxAnnotation(IdentityAnnotationKind, unary.Id.Value);
        SyntaxToken operatorToken = SyntaxFactory.Token(
            syntax!.OperatorToken.LeadingTrivia,
            parsed!.OperatorToken.Kind(),
            operation.NewOperator,
            operation.NewOperator,
            syntax.OperatorToken.TrailingTrivia);
        PrefixUnaryExpressionSyntax replacement = SyntaxFactory.PrefixUnaryExpression(
                parsed.Kind(),
                operatorToken,
                syntax.Operand)
            .WithAdditionalAnnotations(annotation);
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            syntax,
            replacement,
            [new PendingPin(unary.Id, "unary", annotation)]));
    }

    private static OperationPlan PlanArgumentReconnection(
        ReconnectArgumentPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, MethodDescriptor> methods,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirInvocationExpression invocation)
        {
            return KindMismatch(target, operation.TargetNodeId, "an invocation");
        }

        if (operation.ArgumentIndex >= invocation.Arguments.Count ||
            operation.SourceArgumentIndex >= invocation.Arguments.Count)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.ArgumentIndexOutOfRange,
                $"Invocation '{invocation.Id}' has {invocation.Arguments.Count} arguments, but the patch " +
                $"requested indices {operation.ArgumentIndex} and {operation.SourceArgumentIndex}.",
                invocation);
        }

        HirExpression argument = invocation.Arguments[operation.ArgumentIndex];
        HirExpression sourceArgument = invocation.Arguments[operation.SourceArgumentIndex];
        if (argument.Id != operation.ExpectedArgumentNodeId ||
            sourceArgument.Id != operation.ExpectedSourceArgumentNodeId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Invocation argument precondition failed. Current endpoints are '{argument.Id}' and " +
                $"'{sourceArgument.Id}', not '{operation.ExpectedArgumentNodeId}' and " +
                $"'{operation.ExpectedSourceArgumentNodeId}'.",
                invocation);
        }

        if (!boundNodes.TryGetValue(invocation.Id, out BoundNodeInfo? invocationBinding) ||
            invocationBinding.InvokedMethodId is not SemanticId methodId ||
            !methods.TryGetValue(methodId, out MethodDescriptor? method) ||
            method.Parameters.Count != invocation.Arguments.Count)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvocationShapeNotSupported,
                "Argument reconnection currently requires a resolved, non-expanded positional invocation " +
                "whose argument count equals its parameter count.",
                invocation);
        }

        SemanticId argumentParameterType = method.Parameters[operation.ArgumentIndex].TypeId;
        SemanticId sourceParameterType = method.Parameters[operation.SourceArgumentIndex].TypeId;
        if (argumentParameterType != operation.ExpectedArgumentTypeId ||
            sourceParameterType != operation.ExpectedSourceArgumentTypeId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Invocation parameter type precondition failed. Current port types are " +
                $"'{argumentParameterType}' and '{sourceParameterType}'.",
                invocation);
        }

        OperationPlan? argumentTypeFailure = RequireType(
            argument,
            operation.ExpectedArgumentTypeId,
            $"argument {operation.ArgumentIndex}",
            boundNodes);
        if (argumentTypeFailure is not null)
        {
            return argumentTypeFailure;
        }

        OperationPlan? sourceTypeFailure = RequireType(
            sourceArgument,
            operation.ExpectedSourceArgumentTypeId,
            $"argument {operation.SourceArgumentIndex}",
            boundNodes);
        if (sourceTypeFailure is not null)
        {
            return sourceTypeFailure;
        }

        if (operation.ExpectedArgumentTypeId != operation.ExpectedSourceArgumentTypeId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"Cannot exchange invocation ports with types '{operation.ExpectedArgumentTypeId}' and " +
                $"'{operation.ExpectedSourceArgumentTypeId}'. This patch slice permits only exact type matches.",
                invocation);
        }

        if (HasEffects(argument, boundNodes) || HasEffects(sourceArgument, boundNodes))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.EffectOrderViolation,
                "Cannot reconnect effectful arguments because exchanging their positions would change " +
                "C# left-to-right evaluation order.",
                invocation);
        }

        if (!TryFindSyntax(
                documents,
                invocation,
                out ParsedDocument? document,
                out InvocationExpressionSyntax? syntax))
        {
            return MissingSyntax(invocation, "invocation");
        }

        SeparatedSyntaxList<ArgumentSyntax> arguments = syntax!.ArgumentList.Arguments;
        if (arguments.Count != invocation.Arguments.Count)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvocationShapeNotSupported,
                "Roslyn argument syntax no longer matches the analyzed invocation shape.",
                invocation);
        }

        var invocationAnnotation = new SyntaxAnnotation(IdentityAnnotationKind, invocation.Id.Value);
        var argumentAnnotation = new SyntaxAnnotation(IdentityAnnotationKind, argument.Id.Value);
        var sourceAnnotation = new SyntaxAnnotation(IdentityAnnotationKind, sourceArgument.Id.Value);
        ArgumentSyntax[] updatedArguments = arguments.ToArray();
        ExpressionSyntax targetExpression = arguments[operation.ArgumentIndex].Expression;
        ExpressionSyntax sourceExpression = arguments[operation.SourceArgumentIndex].Expression;
        updatedArguments[operation.ArgumentIndex] = arguments[operation.ArgumentIndex].WithExpression(
            sourceExpression
                .WithTriviaFrom(targetExpression)
                .WithAdditionalAnnotations(sourceAnnotation));
        updatedArguments[operation.SourceArgumentIndex] = arguments[operation.SourceArgumentIndex].WithExpression(
            targetExpression
                .WithTriviaFrom(sourceExpression)
                .WithAdditionalAnnotations(argumentAnnotation));
        SeparatedSyntaxList<ArgumentSyntax> updatedList = SyntaxFactory.SeparatedList(
            updatedArguments,
            arguments.GetSeparators());
        InvocationExpressionSyntax replacement = syntax
            .WithArgumentList(syntax.ArgumentList.WithArguments(updatedList))
            .WithAdditionalAnnotations(invocationAnnotation);
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            syntax,
            replacement,
            [
                new PendingPin(invocation.Id, "invocation", invocationAnnotation),
                new PendingPin(argument.Id, GetIdentityKind(argument), argumentAnnotation),
                new PendingPin(sourceArgument.Id, GetIdentityKind(sourceArgument), sourceAnnotation),
            ]));
    }

    private static OperationPlan PlanDataEdgeReconnection(
        ReconnectDataEdgePatchOperation operation,
        GraphDocument graph,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        HirUseDefinitionIndex useDefinitionIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, MethodDescriptor> methods,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        GraphFunction? function = graph.Functions.SingleOrDefault(candidate =>
            candidate.Nodes.Any(node => node.Id == operation.TargetNodeId));
        GraphNode? targetGraphNode = function?.Nodes.SingleOrDefault(
            node => node.Id == operation.TargetNodeId);
        GraphNode? sourceGraphNode = function?.Nodes.SingleOrDefault(
            node => node.Id == operation.NewSourceNodeId);
        GraphEdge? expectedEdge = function?.Edges.SingleOrDefault(
            edge => edge.Id == operation.ExpectedEdgeId);
        GraphPort? targetPort = targetGraphNode?.Ports.SingleOrDefault(port =>
            port.Direction == GraphPortDirection.Input && port.Kind == GraphPortKind.Data &&
            string.Equals(port.Name, operation.TargetPort, StringComparison.Ordinal));
        GraphPort? sourcePort = sourceGraphNode?.Ports.SingleOrDefault(port =>
            port.Direction == GraphPortDirection.Output && port.Kind == GraphPortKind.Data &&
            string.Equals(port.Name, operation.NewSourcePort, StringComparison.Ordinal));
        if (function is null || targetGraphNode is null || sourceGraphNode is null ||
            targetPort is null || sourcePort is null || expectedEdge is null ||
            expectedEdge.Kind != GraphEdgeKind.Data ||
            expectedEdge.FromNode != operation.ExpectedSourceNodeId ||
            !string.Equals(
                expectedEdge.FromPort,
                operation.ExpectedSourcePort,
                StringComparison.Ordinal) ||
            expectedEdge.ToNode != operation.TargetNodeId ||
            !string.Equals(expectedEdge.ToPort, operation.TargetPort, StringComparison.Ordinal))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Data-edge precondition '{operation.ExpectedEdgeId}' no longer matches " +
                $"'{operation.ExpectedSourceNodeId}.{operation.ExpectedSourcePort} -> " +
                $"{operation.TargetNodeId}.{operation.TargetPort}'.",
                target);
        }

        if (!hirNodes.TryGetValue(operation.NewSourceNodeId, out HirNode? sourceNode) ||
            sourceNode is not HirExpression sourceExpression)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidDataEdgeReconnection,
                "Data-edge reconnection requires HIR-backed target and expression source nodes.",
                target);
        }

        if (sourceExpression.DescendantsAndSelf().Any(node => node.Id == target.Id))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidDataEdgeReconnection,
                "A data edge cannot reconnect an expression into itself or one of its descendants.",
                target);
        }

        if (!boundNodes.TryGetValue(sourceExpression.Id, out BoundNodeInfo? sourceBinding) ||
            sourceBinding.TypeId != operation.ExpectedTypeId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"Graph source '{sourceExpression.Id}' no longer has expected type " +
                $"'{operation.ExpectedTypeId}'.",
                sourceExpression);
        }

        if (!IsStablePureExpression(
                sourceExpression,
                useDefinitionIndex,
                boundNodes,
                methods,
                types))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.EffectOrderViolation,
                "The selected source is not a stable pure expression and cannot be copied " +
                "without changing evaluation count or effect order.",
                sourceExpression);
        }

        if (!TryProjectRewireExpression(
                sourceExpression,
                hirNodes,
                boundNodes,
                out PatchExpression? newInput,
                out string? projectionFailure))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                projectionFailure ?? "The selected graph source cannot be authored as C#.",
                sourceExpression);
        }

        SemanticPatchOperation? replacement = target switch
        {
            HirExpression expression when TryGetExpressionInput(
                expression,
                operation.TargetPort,
                out HirExpression? currentInput,
                out _) && boundNodes.TryGetValue(expression.Id, out BoundNodeInfo? targetBinding) &&
                targetBinding.TypeId is SemanticId targetTypeId =>
                new ReplaceExpressionInputPatchOperation(
                    expression.Id,
                    operation.TargetPort,
                    currentInput!.Id,
                    operation.ExpectedTypeId,
                    targetTypeId,
                    newInput!),
            HirVariableDeclarationStatement { Initializer: not null } variable
                when operation.TargetPort == "value" => new ReplaceLocalInitializerPatchOperation(
                    variable.Id,
                    variable.Name,
                    variable.Initializer.Id,
                    operation.ExpectedTypeId,
                    newInput!),
            HirAssignmentExpression assignment
                when operation.TargetPort == "assignedValue" &&
                assignment.Target is HirIdentifierExpression identifier &&
                boundNodes.TryGetValue(identifier.Id, out BoundNodeInfo? identifierBinding) &&
                identifierBinding.ReferencedSymbolId is SemanticId definitionId =>
                new ReplaceAssignmentValuePatchOperation(
                    assignment.Id,
                    assignment.Operator,
                    identifier.Id,
                    definitionId,
                    identifier.Name,
                    assignment.Value.Id,
                    operation.ExpectedTypeId,
                    newInput!),
            HirIfStatement branch when operation.TargetPort == "condition" =>
                new ReplaceIfConditionPatchOperation(
                    branch.Id,
                    branch.Condition.Id,
                    operation.ExpectedTypeId,
                    newInput!),
            HirWhileStatement loop when operation.TargetPort == "condition" =>
                new ReplaceWhileConditionPatchOperation(
                    loop.Id,
                    loop.Condition.Id,
                    operation.ExpectedTypeId,
                    newInput!),
            HirDoWhileStatement loop when operation.TargetPort == "condition" =>
                new ReplaceDoWhileConditionPatchOperation(
                    loop.Id,
                    loop.Condition.Id,
                    operation.ExpectedTypeId,
                    newInput!),
            HirForStatement { Condition: not null } loop when operation.TargetPort == "condition" =>
                new ReplaceForConditionPatchOperation(
                    loop.Id,
                    loop.Condition.Id,
                    operation.ExpectedTypeId,
                    newInput!),
            HirForEachStatement loop when operation.TargetPort == "collection" =>
                new ReplaceForEachCollectionPatchOperation(
                    loop.Id,
                    loop.Collection.Id,
                    operation.ExpectedTypeId,
                    newInput!),
            HirReturnStatement { Expression: not null } valueReturn
                when operation.TargetPort == "value" => new ReplaceReturnValuePatchOperation(
                    valueReturn.Id,
                    valueReturn.Expression.Id,
                    operation.ExpectedTypeId,
                    newInput!),
            _ => null,
        };
        if (replacement is null)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidExpressionInputPort,
                $"Graph target '{operation.TargetNodeId}' does not expose reconnectable data " +
                $"input '{operation.TargetPort}'.",
                target);
        }

        return replacement switch
        {
            ReplaceExpressionInputPatchOperation input => PlanExpressionInputReplacement(
                input,
                hirNodes,
                patchIndex,
                useDefinitionIndex,
                boundNodes,
                methods,
                types,
                graphSafetyIndex,
                documents),
            ReplaceAssignmentValuePatchOperation assignment => PlanAssignmentValueReplacement(
                assignment,
                hirNodes,
                patchIndex,
                boundNodes,
                types,
                graphSafetyIndex,
                documents),
            ReplaceIfConditionPatchOperation branch => PlanIfConditionReplacement(
                branch,
                hirNodes,
                patchIndex,
                boundNodes,
                types,
                graphSafetyIndex,
                documents),
            ReplaceWhileConditionPatchOperation loop => PlanWhileConditionReplacement(
                loop,
                hirNodes,
                patchIndex,
                boundNodes,
                types,
                graphSafetyIndex,
                documents),
            ReplaceDoWhileConditionPatchOperation loop => PlanDoWhileConditionReplacement(
                loop,
                hirNodes,
                patchIndex,
                boundNodes,
                types,
                graphSafetyIndex,
                documents),
            ReplaceForConditionPatchOperation loop => PlanForConditionReplacement(
                loop,
                hirNodes,
                patchIndex,
                boundNodes,
                types,
                graphSafetyIndex,
                documents),
            ReplaceForEachCollectionPatchOperation loop => PlanForEachCollectionReplacement(
                loop,
                hirNodes,
                patchIndex,
                boundNodes,
                types,
                graphSafetyIndex,
                documents),
            ReplaceLocalInitializerPatchOperation local => PlanLocalInitializerReplacement(
                local,
                hirNodes,
                patchIndex,
                boundNodes,
                types,
                graphSafetyIndex,
                documents),
            ReplaceReturnValuePatchOperation valueReturn => PlanReturnValueReplacement(
                valueReturn,
                hirNodes,
                patchIndex,
                boundNodes,
                methods,
                types,
                graphSafetyIndex,
                documents),
            _ => throw new InvalidOperationException(
                $"Data-edge replacement '{replacement.GetType().Name}' was not handled."),
        };
    }

    private static OperationPlan PlanDataEdgeDisconnection(
        DisconnectDataEdgePatchOperation operation,
        GraphDocument graph,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        HirUseDefinitionIndex useDefinitionIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, MethodDescriptor> methods,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        GraphFunction? function = graph.Functions.SingleOrDefault(candidate =>
            candidate.Nodes.Any(node => node.Id == operation.TargetNodeId));
        GraphNode? targetGraphNode = function?.Nodes.SingleOrDefault(
            node => node.Id == operation.TargetNodeId);
        GraphEdge? expectedEdge = function?.Edges.SingleOrDefault(
            edge => edge.Id == operation.ExpectedEdgeId);
        GraphPort? targetPort = targetGraphNode?.Ports.SingleOrDefault(port =>
            port.Direction == GraphPortDirection.Input && port.Kind == GraphPortKind.Data &&
            string.Equals(port.Name, operation.TargetPort, StringComparison.Ordinal));
        if (function is null || targetGraphNode is null || targetPort is null ||
            expectedEdge is null || expectedEdge.Kind != GraphEdgeKind.Data ||
            expectedEdge.FromNode != operation.ExpectedSourceNodeId ||
            !string.Equals(
                expectedEdge.FromPort,
                operation.ExpectedSourcePort,
                StringComparison.Ordinal) ||
            expectedEdge.ToNode != operation.TargetNodeId ||
            !string.Equals(expectedEdge.ToPort, operation.TargetPort, StringComparison.Ordinal))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Data-edge precondition '{operation.ExpectedEdgeId}' no longer matches " +
                $"'{operation.ExpectedSourceNodeId}.{operation.ExpectedSourcePort} -> " +
                $"{operation.TargetNodeId}.{operation.TargetPort}'.",
                target);
        }

        if (!types.TryGetValue(operation.ExpectedTypeId, out TypeDescriptor? expectedType) ||
            !expectedType.IsGraphSafe)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"Data-edge input no longer has graph-safe type '{operation.ExpectedTypeId}'.",
                target);
        }

        var newInput = new DefaultPatchExpression(expectedType.DisplayName, expectedType.Id);
        SemanticPatchOperation? replacement = target switch
        {
            HirExpression expression when TryGetExpressionInput(
                expression,
                operation.TargetPort,
                out HirExpression? currentInput,
                out _) && boundNodes.TryGetValue(expression.Id, out BoundNodeInfo? targetBinding) &&
                targetBinding.TypeId is SemanticId targetTypeId =>
                new ReplaceExpressionInputPatchOperation(
                    expression.Id,
                    operation.TargetPort,
                    currentInput!.Id,
                    operation.ExpectedTypeId,
                    targetTypeId,
                    newInput),
            HirVariableDeclarationStatement { Initializer: not null } variable
                when operation.TargetPort == "value" => new ReplaceLocalInitializerPatchOperation(
                    variable.Id,
                    variable.Name,
                    variable.Initializer.Id,
                    operation.ExpectedTypeId,
                    newInput),
            HirAssignmentExpression assignment
                when operation.TargetPort == "assignedValue" &&
                assignment.Target is HirIdentifierExpression identifier &&
                boundNodes.TryGetValue(identifier.Id, out BoundNodeInfo? identifierBinding) &&
                identifierBinding.ReferencedSymbolId is SemanticId definitionId =>
                new ReplaceAssignmentValuePatchOperation(
                    assignment.Id,
                    assignment.Operator,
                    identifier.Id,
                    definitionId,
                    identifier.Name,
                    assignment.Value.Id,
                    operation.ExpectedTypeId,
                    newInput),
            HirIfStatement branch when operation.TargetPort == "condition" =>
                new ReplaceIfConditionPatchOperation(
                    branch.Id,
                    branch.Condition.Id,
                    operation.ExpectedTypeId,
                    newInput),
            HirWhileStatement loop when operation.TargetPort == "condition" =>
                new ReplaceWhileConditionPatchOperation(
                    loop.Id,
                    loop.Condition.Id,
                    operation.ExpectedTypeId,
                    newInput),
            HirDoWhileStatement loop when operation.TargetPort == "condition" =>
                new ReplaceDoWhileConditionPatchOperation(
                    loop.Id,
                    loop.Condition.Id,
                    operation.ExpectedTypeId,
                    newInput),
            HirForStatement { Condition: not null } loop when operation.TargetPort == "condition" =>
                new ReplaceForConditionPatchOperation(
                    loop.Id,
                    loop.Condition.Id,
                    operation.ExpectedTypeId,
                    newInput),
            HirForEachStatement loop when operation.TargetPort == "collection" =>
                new ReplaceForEachCollectionPatchOperation(
                    loop.Id,
                    loop.Collection.Id,
                    operation.ExpectedTypeId,
                    newInput),
            HirReturnStatement { Expression: not null } valueReturn
                when operation.TargetPort == "value" => new ReplaceReturnValuePatchOperation(
                    valueReturn.Id,
                    valueReturn.Expression.Id,
                    operation.ExpectedTypeId,
                    newInput),
            _ => null,
        };
        if (replacement is null)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidExpressionInputPort,
                $"Graph target '{operation.TargetNodeId}' does not expose disconnectable data " +
                $"input '{operation.TargetPort}'.",
                target);
        }

        return replacement switch
        {
            ReplaceExpressionInputPatchOperation input => PlanExpressionInputReplacement(
                input, hirNodes, patchIndex, useDefinitionIndex, boundNodes, methods, types,
                graphSafetyIndex, documents),
            ReplaceAssignmentValuePatchOperation assignment => PlanAssignmentValueReplacement(
                assignment, hirNodes, patchIndex, boundNodes, types, graphSafetyIndex, documents),
            ReplaceIfConditionPatchOperation branch => PlanIfConditionReplacement(
                branch, hirNodes, patchIndex, boundNodes, types, graphSafetyIndex, documents),
            ReplaceWhileConditionPatchOperation loop => PlanWhileConditionReplacement(
                loop, hirNodes, patchIndex, boundNodes, types, graphSafetyIndex, documents),
            ReplaceDoWhileConditionPatchOperation loop => PlanDoWhileConditionReplacement(
                loop, hirNodes, patchIndex, boundNodes, types, graphSafetyIndex, documents),
            ReplaceForConditionPatchOperation loop => PlanForConditionReplacement(
                loop, hirNodes, patchIndex, boundNodes, types, graphSafetyIndex, documents),
            ReplaceForEachCollectionPatchOperation loop => PlanForEachCollectionReplacement(
                loop, hirNodes, patchIndex, boundNodes, types, graphSafetyIndex, documents),
            ReplaceLocalInitializerPatchOperation local => PlanLocalInitializerReplacement(
                local, hirNodes, patchIndex, boundNodes, types, graphSafetyIndex, documents),
            ReplaceReturnValuePatchOperation valueReturn => PlanReturnValueReplacement(
                valueReturn, hirNodes, patchIndex, boundNodes, methods, types, graphSafetyIndex,
                documents),
            _ => throw new InvalidOperationException(
                $"Data-edge disconnection replacement '{replacement.GetType().Name}' was not handled."),
        };
    }

    private static bool TryProjectRewireExpression(
        HirExpression source,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        out PatchExpression? expression,
        out string? failure)
    {
        expression = null;
        failure = null;
        if (!boundNodes.TryGetValue(source.Id, out BoundNodeInfo? binding) ||
            binding.TypeId is not SemanticId typeId)
        {
            failure = $"Graph source '{source.Id}' has no bound semantic type.";
            return false;
        }

        switch (source)
        {
            case HirLiteralExpression literal:
                expression = new LiteralPatchExpression(literal.ValueText, typeId);
                return true;
            case HirIdentifierExpression identifier
                when binding.ReferencedSymbolId is SemanticId definitionId &&
                hirNodes.TryGetValue(definitionId, out HirNode? definition) &&
                definition is HirParameter or HirVariableDeclarationStatement or
                    HirForVariableDeclarator or HirForEachVariable:
                expression = new LocalReferencePatchExpression(
                    definitionId,
                    identifier.Name,
                    typeId);
                return true;
            case HirUnaryExpression { IsPostfix: false } unary
                when TryProjectRewireExpression(
                    unary.Operand,
                    hirNodes,
                    boundNodes,
                    out PatchExpression? operand,
                    out failure):
                expression = new UnaryPatchExpression(unary.Operator, operand!, typeId);
                return true;
            case HirBinaryExpression binary
                when TryProjectRewireExpression(
                    binary.Left,
                    hirNodes,
                    boundNodes,
                    out PatchExpression? left,
                    out failure) && TryProjectRewireExpression(
                    binary.Right,
                    hirNodes,
                    boundNodes,
                    out PatchExpression? right,
                    out failure):
                expression = new BinaryPatchExpression(binary.Operator, left!, right!, typeId);
                return true;
            case HirCastExpression cast
                when TryProjectRewireExpression(
                    cast.Operand,
                    hirNodes,
                    boundNodes,
                    out PatchExpression? castOperand,
                    out failure):
                expression = new CastPatchExpression(cast.TypeName, castOperand!, typeId);
                return true;
            case HirConditionalExpression conditional
                when TryProjectRewireExpression(
                    conditional.Condition,
                    hirNodes,
                    boundNodes,
                    out PatchExpression? condition,
                    out failure) && TryProjectRewireExpression(
                    conditional.WhenTrue,
                    hirNodes,
                    boundNodes,
                    out PatchExpression? whenTrue,
                    out failure) && TryProjectRewireExpression(
                    conditional.WhenFalse,
                    hirNodes,
                    boundNodes,
                    out PatchExpression? whenFalse,
                    out failure):
                expression = new ConditionalPatchExpression(
                    condition!,
                    whenTrue!,
                    whenFalse!,
                    typeId);
                return true;
            default:
                failure ??= $"Graph source '{source.Id}' of kind " +
                    $"'{source.GetType().Name}' is not a supported pure reconnect expression.";
                return false;
        }
    }

    private static OperationPlan PlanExpressionInputReplacement(
        ReplaceExpressionInputPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        HirUseDefinitionIndex useDefinitionIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, MethodDescriptor> methods,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirExpression targetExpression ||
            !patchIndex.TryGetExpression(
                targetExpression.Id,
                out HirPatchIndex.ExpressionLocation? expressionLocation) ||
            !patchIndex.TryGetStatement(
                expressionLocation!.ContainingStatement.Id,
                out HirPatchIndex.StatementLocation? statementLocation) ||
            !patchIndex.TryGetBlock(
                statementLocation!.ParentBlock.Id,
                out HirPatchIndex.BlockLocation? blockLocation))
        {
            return KindMismatch(
                target,
                operation.TargetNodeId,
                "an indexed expression with an editable Graph input port");
        }

        if (!TryGetExpressionInput(
                targetExpression,
                operation.InputPort,
                out HirExpression? currentInput,
                out int? invocationArgumentIndex))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidExpressionInputPort,
                $"Expression '{targetExpression.Id}' of kind " +
                $"'{targetExpression.GetType().Name}' does not expose supported Graph input " +
                $"port '{operation.InputPort}'.",
                targetExpression);
        }

        if (currentInput!.Id != operation.ExpectedInputNodeId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Expression input precondition failed for '{targetExpression.Id}." +
                $"{operation.InputPort}': current endpoint is '{currentInput.Id}', not " +
                $"'{operation.ExpectedInputNodeId}'.",
                targetExpression);
        }

        OperationPlan? targetTypeFailure = RequireType(
            targetExpression,
            operation.ExpectedTargetTypeId,
            "expression-input target",
            boundNodes);
        OperationPlan? inputTypeFailure = RequireType(
            currentInput,
            operation.ExpectedInputTypeId,
            $"expression input '{operation.InputPort}'",
            boundNodes);
        if (targetTypeFailure is not null || inputTypeFailure is not null)
        {
            return targetTypeFailure ?? inputTypeFailure!;
        }

        if (targetExpression is HirInvocationExpression invocation)
        {
            if (invocationArgumentIndex is not int argumentIndex ||
                !boundNodes.TryGetValue(invocation.Id, out BoundNodeInfo? invocationBinding) ||
                invocationBinding.InvokedMethodId is not SemanticId methodId ||
                !methods.TryGetValue(methodId, out MethodDescriptor? method) ||
                method.Parameters.Count != invocation.Arguments.Count)
            {
                return DiagnosticPlan(
                    SemanticPatchDiagnosticCodes.InvocationShapeNotSupported,
                    "Invocation input editing requires a resolved, non-expanded positional " +
                    "invocation whose argument count equals its parameter count.",
                    invocation);
            }

            if (method.Parameters[argumentIndex].TypeId != operation.ExpectedInputTypeId)
            {
                return DiagnosticPlan(
                    SemanticPatchDiagnosticCodes.PreconditionFailed,
                    $"Invocation port '{operation.InputPort}' currently declares parameter type " +
                    $"'{method.Parameters[argumentIndex].TypeId}', not " +
                    $"'{operation.ExpectedInputTypeId}'.",
                    invocation);
            }
        }

        if (!graphSafetyIndex.IsInsertionReachable(blockLocation!, statementLocation.Index))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.UnreachableInsertion,
                "An expression input can be edited only when its containing statement is " +
                "CFG-reachable.",
                expressionLocation.ContainingStatement);
        }

        if (!IsStablePureExpression(
                currentInput,
                useDefinitionIndex,
                boundNodes,
                methods,
                types))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.EffectOrderViolation,
                $"Expression input '{targetExpression.Id}.{operation.InputPort}' is not a stable, " +
                "effect-free subtree. This bounded replacement cannot remove or reorder state " +
                "reads, mutation, throws, short-circuit evaluation, or unknown effects.",
                currentInput);
        }

        ExpressionBuildResult newInput = BuildPatchExpression(
            operation.NewInput,
            blockLocation!,
            statementLocation.Index,
            hirNodes,
            patchIndex,
            graphSafetyIndex,
            boundNodes,
            types);
        if (newInput.Syntax is null || newInput.Type?.Id != operation.ExpectedInputTypeId)
        {
            return DiagnosticPlan(
                newInput.DiagnosticCode ?? SemanticPatchDiagnosticCodes.TypeMismatch,
                newInput.DiagnosticMessage ??
                $"The replacement for input port '{operation.InputPort}' is invalid.",
                targetExpression);
        }

        if (!TryFindSyntax(
                documents,
                expressionLocation.ContainingStatement,
                out ParsedDocument? document,
                out StatementSyntax? statementSyntax))
        {
            return MissingSyntax(
                expressionLocation.ContainingStatement,
                "expression-input containing statement");
        }

        HashSet<SemanticId> removedIds = currentInput
            .DescendantsAndSelf()
            .Select(static node => node.Id)
            .ToHashSet();
        if (!TryAnnotateExpressions(
                document!,
                statementSyntax!,
                expressionLocation.ContainingStatement,
                removedIds,
                out StatementSyntax? annotatedStatement,
                out IReadOnlyDictionary<SemanticId, SyntaxAnnotation>? expressionAnnotations,
                out IReadOnlyList<PendingPin>? expressionPins) ||
            !expressionAnnotations!.TryGetValue(
                currentInput.Id,
                out SyntaxAnnotation? inputAnnotation))
        {
            return MissingSyntax(currentInput, $"expression input '{operation.InputPort}'");
        }

        ExpressionSyntax annotatedInput = annotatedStatement!
            .GetAnnotatedNodes(inputAnnotation)
            .OfType<ExpressionSyntax>()
            .Single();
        if (annotatedInput.DescendantTrivia(descendIntoTrivia: true).Any(IsMeaningfulTrivia))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidReplacement,
                $"Expression input '{targetExpression.Id}.{operation.InputPort}' contains comments " +
                "or directives that cannot be discarded safely.",
                currentInput);
        }

        ExpressionSyntax formattedInput = FormatReplacementExpression(
            ParenthesizeCompositeExpression(newInput.Syntax),
            annotatedInput);
        StatementSyntax updatedStatement = annotatedStatement.ReplaceNode(
            annotatedInput,
            formattedInput);
        var statementAnnotation = new SyntaxAnnotation(
            IdentityAnnotationKind,
            expressionLocation.ContainingStatement.Id.Value);
        updatedStatement = updatedStatement.WithAdditionalAnnotations(statementAnnotation);
        var pins = new List<PendingPin>(expressionPins!)
        {
            new(
                expressionLocation.ContainingStatement.Id,
                GetIdentityKind(expressionLocation.ContainingStatement),
                statementAnnotation),
        };
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            statementSyntax!,
            updatedStatement,
            pins));
    }

    private static bool TryGetExpressionInput(
        HirExpression target,
        string inputPort,
        out HirExpression? input,
        out int? invocationArgumentIndex)
    {
        invocationArgumentIndex = null;
        input = (target, inputPort) switch
        {
            (HirBinaryExpression binary, "left") => binary.Left,
            (HirBinaryExpression binary, "right") => binary.Right,
            (HirUnaryExpression unary, "operand")
                when GraphExpressionUnaryOperators.Contains(unary.Operator) => unary.Operand,
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

        invocationArgumentIndex = argumentIndex;
        input = invocation.Arguments[argumentIndex];
        return true;
    }

    private static OperationPlan PlanStatementInsertion(
        InsertStatementPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, MethodDescriptor> methods,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirBlockStatement block ||
            !patchIndex.TryGetBlock(block.Id, out HirPatchIndex.BlockLocation? blockLocation))
        {
            return KindMismatch(target, operation.TargetNodeId, "a block statement");
        }

        if (!TryGetInsertionIndex(
                block,
                operation.ExpectedPreviousSiblingId,
                operation.ExpectedNextSiblingId,
                out int insertionIndex))
        {
            return SiblingFailure(
                block,
                operation.ExpectedPreviousSiblingId,
                operation.ExpectedNextSiblingId);
        }

        if (block.Statements.Take(insertionIndex).Any(static statement =>
                statement is HirReturnStatement or HirBreakStatement or HirContinueStatement))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                "A statement cannot be inserted after terminal control flow because it would be unreachable.",
                block);
        }

        if (operation.NewStatement is ReturnPatchStatement or BreakPatchStatement or ContinuePatchStatement &&
            insertionIndex != block.Statements.Count)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                "A terminal control-transfer statement can be inserted only at the end of an explicit block.",
                block);
        }

        if (operation.NewStatement is LocalAssignmentPatchStatement &&
            !graphSafetyIndex.IsInsertionReachable(blockLocation!, insertionIndex))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.UnreachableInsertion,
                "A local assignment requires a CFG-reachable insertion gap.",
                block);
        }

        StatementBuildResult statementResult = operation.NewStatement switch
        {
            LocalLiteralDeclarationPatchStatement local => BuildLocalLiteralStatement(
                local,
                blockLocation!.Method,
                types),
            LocalDeclarationPatchStatement local => BuildLocalStatement(
                local,
                blockLocation!,
                insertionIndex,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                types),
            LocalAssignmentPatchStatement assignment => BuildLocalAssignmentStatement(
                assignment,
                blockLocation!,
                insertionIndex,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                types),
            IfPatchStatement ifStatement => BuildIfStatement(
                ifStatement,
                blockLocation!,
                insertionIndex,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                types),
            WhilePatchStatement whileStatement => BuildWhileStatement(
                whileStatement,
                blockLocation!,
                insertionIndex,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                types),
            DoWhilePatchStatement doWhileStatement => BuildDoWhileStatement(
                doWhileStatement,
                blockLocation!,
                insertionIndex,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                types),
            ForPatchStatement forStatement => BuildForStatement(
                forStatement,
                blockLocation!,
                insertionIndex,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                types),
            ForEachPatchStatement forEachStatement => BuildForEachStatement(
                forEachStatement,
                blockLocation!,
                insertionIndex,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                types),
            SwitchPatchStatement switchStatement => BuildSwitchStatement(
                switchStatement,
                blockLocation!,
                insertionIndex,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                types),
            BreakPatchStatement => BuildControlTransferStatement(
                isContinue: false,
                blockLocation!,
                insertionIndex,
                patchIndex,
                graphSafetyIndex),
            ContinuePatchStatement => BuildControlTransferStatement(
                isContinue: true,
                blockLocation!,
                insertionIndex,
                patchIndex,
                graphSafetyIndex),
            ReturnPatchStatement returnStatement => BuildReturnStatement(
                returnStatement,
                blockLocation!,
                insertionIndex,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                methods,
                types),
            _ => StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                $"Inserted statement payload '{operation.NewStatement.GetType().Name}' is not supported."),
        };
        if (statementResult.Syntax is null)
        {
            return DiagnosticPlan(
                statementResult.DiagnosticCode ?? SemanticPatchDiagnosticCodes.InvalidStatement,
                statementResult.DiagnosticMessage ?? "The inserted statement payload is invalid.",
                block);
        }

        if (!TryFindEditableStatementContainer(
                documents,
                block,
                out ParsedDocument? document,
                out EditableStatementContainer? container) ||
            container!.Statements.Count != block.Statements.Count)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                "Statement insertion requires an explicit C# block or switch-section body whose " +
                "statement list matches the analyzed HIR block.",
                block);
        }

        (List<StatementSyntax> annotatedStatements, List<PendingPin> pins) =
            AnnotateStatementTrees(document!, container.Statements, block.Statements);
        InsertionFormatting formatting = GetInsertionFormatting(
            document!,
            container,
            insertionIndex);
        StatementSyntax newStatement = FormatInsertedStatement(statementResult.Syntax, formatting);
        annotatedStatements.Insert(insertionIndex, newStatement);
        var blockAnnotation = new SyntaxAnnotation(IdentityAnnotationKind, block.Id.Value);
        SyntaxNode replacement = container.WithStatements(
                SyntaxFactory.List(annotatedStatements))
            .WithAdditionalAnnotations(blockAnnotation);
        pins.Add(new PendingPin(block.Id, "block", blockAnnotation));
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            container.Syntax,
            replacement,
            pins));
    }

    private static OperationPlan PlanAssignmentValueReplacement(
        ReplaceAssignmentValuePatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirAssignmentExpression assignment ||
            !patchIndex.TryGetExpression(
                assignment.Id,
                out HirPatchIndex.ExpressionLocation? expressionLocation) ||
            expressionLocation!.ParentExpression is not null ||
            expressionLocation.ContainingStatement is not HirExpressionStatement expressionStatement ||
            expressionStatement.Expression.Id != assignment.Id ||
            !patchIndex.TryGetStatement(
                expressionStatement.Id,
                out HirPatchIndex.StatementLocation? statementLocation) ||
            !patchIndex.TryGetBlock(
                statementLocation!.ParentBlock.Id,
                out HirPatchIndex.BlockLocation? blockLocation))
        {
            return KindMismatch(
                target,
                operation.TargetNodeId,
                "a root simple-assignment expression statement");
        }

        if (!string.Equals(assignment.Operator, operation.ExpectedOperator, StringComparison.Ordinal) ||
            !string.Equals(assignment.Operator, "=", StringComparison.Ordinal) ||
            assignment.Target.Id != operation.ExpectedTargetNodeId ||
            assignment.Value.Id != operation.ExpectedValueNodeId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Assignment shape precondition failed for '{assignment.Id}'. Current operator/target/value " +
                $"are '{assignment.Operator}', '{assignment.Target.Id}', and '{assignment.Value.Id}'.",
                assignment);
        }

        if (assignment.Target is not HirIdentifierExpression targetIdentifier ||
            !boundNodes.TryGetValue(targetIdentifier.Id, out BoundNodeInfo? targetBinding) ||
            targetBinding.ReferencedSymbolId != operation.ExpectedTargetDefinitionId ||
            !hirNodes.TryGetValue(
                operation.ExpectedTargetDefinitionId,
                out HirNode? targetDefinition) ||
            targetDefinition is not HirVariableDeclarationStatement variable ||
            !string.Equals(variable.Name, operation.ExpectedTargetName, StringComparison.Ordinal) ||
            !string.Equals(targetIdentifier.Name, operation.ExpectedTargetName, StringComparison.Ordinal))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                "Assignment target no longer resolves to the expected named local definition.",
                assignment);
        }

        OperationPlan? assignmentTypeFailure = RequireType(
            assignment,
            operation.ExpectedTypeId,
            "assignment",
            boundNodes);
        OperationPlan? targetTypeFailure = RequireType(
            assignment.Target,
            operation.ExpectedTypeId,
            "assignment target",
            boundNodes);
        OperationPlan? valueTypeFailure = RequireType(
            assignment.Value,
            operation.ExpectedTypeId,
            "assignment value",
            boundNodes);
        OperationPlan? definitionTypeFailure = RequireType(
            variable,
            operation.ExpectedTypeId,
            "assignment target definition",
            boundNodes);
        if (assignmentTypeFailure is not null || targetTypeFailure is not null ||
            valueTypeFailure is not null || definitionTypeFailure is not null)
        {
            return assignmentTypeFailure ?? targetTypeFailure ??
                valueTypeFailure ?? definitionTypeFailure!;
        }

        if (!graphSafetyIndex.IsInsertionReachable(blockLocation!, statementLocation.Index))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.UnreachableInsertion,
                "An assignment value can be edited only when its statement is CFG-reachable.",
                assignment);
        }

        ExpressionBuildResult newValue = BuildPatchExpression(
            operation.NewValue,
            blockLocation!,
            statementLocation.Index,
            hirNodes,
            patchIndex,
            graphSafetyIndex,
            boundNodes,
            types);
        if (newValue.Syntax is null || newValue.Type?.Id != operation.ExpectedTypeId)
        {
            return DiagnosticPlan(
                newValue.DiagnosticCode ?? SemanticPatchDiagnosticCodes.TypeMismatch,
                newValue.DiagnosticMessage ?? "The replacement assignment value is invalid.",
                assignment);
        }

        if (!TryFindSyntax(
                documents,
                expressionStatement,
                out ParsedDocument? document,
                out ExpressionStatementSyntax? statementSyntax) ||
            statementSyntax!.Expression is not AssignmentExpressionSyntax)
        {
            return MissingSyntax(expressionStatement, "assignment expression statement");
        }

        HashSet<SemanticId> removedIds = assignment.Value
            .DescendantsAndSelf()
            .Select(static node => node.Id)
            .ToHashSet();
        if (!TryAnnotateExpressions(
                document!,
                statementSyntax,
                expressionStatement,
                removedIds,
                out StatementSyntax? annotatedStatement,
                out IReadOnlyDictionary<SemanticId, SyntaxAnnotation>? expressionAnnotations,
                out IReadOnlyList<PendingPin>? expressionPins) ||
            !expressionAnnotations!.TryGetValue(
                assignment.Id,
                out SyntaxAnnotation? assignmentAnnotation))
        {
            return MissingSyntax(assignment, "assignment expression");
        }

        AssignmentExpressionSyntax annotatedAssignment = annotatedStatement!
            .GetAnnotatedNodes(assignmentAnnotation)
            .OfType<AssignmentExpressionSyntax>()
            .Single();
        if (annotatedAssignment.Right
            .DescendantTrivia(descendIntoTrivia: true)
            .Any(IsMeaningfulTrivia))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidReplacement,
                "The existing assignment value contains comments or directives that cannot be " +
                "discarded safely.",
                assignment.Value);
        }

        ExpressionSyntax formattedValue = FormatReplacementExpression(
            newValue.Syntax,
            annotatedAssignment.Right);
        AssignmentExpressionSyntax updatedAssignment = annotatedAssignment.WithRight(formattedValue);
        StatementSyntax updatedStatement = annotatedStatement.ReplaceNode(
            annotatedAssignment,
            updatedAssignment);
        var statementAnnotation = new SyntaxAnnotation(
            IdentityAnnotationKind,
            expressionStatement.Id.Value);
        updatedStatement = updatedStatement.WithAdditionalAnnotations(statementAnnotation);
        var pins = new List<PendingPin>(expressionPins!)
        {
            new(expressionStatement.Id, GetIdentityKind(expressionStatement), statementAnnotation),
        };
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            statementSyntax,
            updatedStatement,
            pins));
    }

    private static OperationPlan PlanForConditionReplacement(
        ReplaceForConditionPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirForStatement { Condition: not null } loop ||
            !patchIndex.TryGetStatement(
                loop.Id,
                out HirPatchIndex.StatementLocation? statementLocation) ||
            !patchIndex.TryGetBlock(
                statementLocation!.ParentBlock.Id,
                out HirPatchIndex.BlockLocation? blockLocation))
        {
            return KindMismatch(target, operation.TargetNodeId, "a for statement with a condition");
        }

        if (loop.Condition.Id != operation.ExpectedConditionNodeId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"For condition precondition failed: current endpoint is '{loop.Condition.Id}', " +
                $"not '{operation.ExpectedConditionNodeId}'.",
                loop);
        }

        return PlanStatementExpressionReplacement(
            "for condition",
            loop,
            loop.Condition,
            operation.ExpectedConditionTypeId,
            operation.NewCondition,
            requireBoolean: true,
            blockLocation!,
            statementLocation!,
            hirNodes,
            patchIndex,
            boundNodes,
            types,
            graphSafetyIndex,
            documents,
            loop.Id);
    }

    private static OperationPlan PlanForDeclarationInitializerReplacement(
        ReplaceForDeclarationInitializerPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirForVariableDeclarator { Initializer: not null } variable ||
            !patchIndex.TryGetDeclaringFor(variable.Id, out HirForStatement? loop) ||
            loop!.Id != operation.ExpectedForStatementId ||
            !patchIndex.TryGetStatement(
                loop.Id,
                out HirPatchIndex.StatementLocation? statementLocation) ||
            !patchIndex.TryGetBlock(
                statementLocation!.ParentBlock.Id,
                out HirPatchIndex.BlockLocation? blockLocation))
        {
            return KindMismatch(
                target,
                operation.TargetNodeId,
                "an initialized variable declared by the expected for statement");
        }

        if (!string.Equals(variable.Name, operation.ExpectedName, StringComparison.Ordinal) ||
            variable.Initializer.Id != operation.ExpectedInitializerNodeId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                "For declaration initializer no longer has the expected name or value endpoint.",
                variable);
        }

        OperationPlan? variableTypeFailure = RequireType(
            variable,
            operation.ExpectedTypeId,
            "for variable",
            boundNodes);
        if (variableTypeFailure is not null)
        {
            return variableTypeFailure;
        }

        return PlanStatementExpressionReplacement(
            "for declaration initializer",
            loop,
            variable.Initializer,
            operation.ExpectedTypeId,
            operation.NewInitializer,
            requireBoolean: false,
            blockLocation!,
            statementLocation!,
            hirNodes,
            patchIndex,
            boundNodes,
            types,
            graphSafetyIndex,
            documents);
    }

    private static OperationPlan PlanForAssignmentInitializerValueReplacement(
        ReplaceForAssignmentInitializerValuePatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirAssignmentExpression assignment ||
            !patchIndex.TryGetExpression(
                assignment.Id,
                out HirPatchIndex.ExpressionLocation? expressionLocation) ||
            expressionLocation!.ParentExpression is not null ||
            expressionLocation.ContainingStatement is not HirForStatement loop ||
            loop.Id != operation.ExpectedForStatementId ||
            operation.InitializerIndex >= loop.Initializers.Count ||
            loop.Initializers[operation.InitializerIndex].Id != assignment.Id ||
            !patchIndex.TryGetStatement(
                loop.Id,
                out HirPatchIndex.StatementLocation? statementLocation) ||
            !patchIndex.TryGetBlock(
                statementLocation!.ParentBlock.Id,
                out HirPatchIndex.BlockLocation? blockLocation))
        {
            return KindMismatch(
                target,
                operation.TargetNodeId,
                "the indexed assignment initializer of the expected for statement");
        }

        if (!string.Equals(assignment.Operator, operation.ExpectedOperator, StringComparison.Ordinal) ||
            assignment.Target.Id != operation.ExpectedTargetNodeId ||
            assignment.Value.Id != operation.ExpectedValueNodeId ||
            assignment.Target is not HirIdentifierExpression targetIdentifier ||
            !boundNodes.TryGetValue(targetIdentifier.Id, out BoundNodeInfo? targetBinding) ||
            targetBinding.ReferencedSymbolId != operation.ExpectedTargetDefinitionId ||
            !hirNodes.TryGetValue(operation.ExpectedTargetDefinitionId, out HirNode? definition) ||
            definition is not HirVariableDeclarationStatement targetVariable ||
            !string.Equals(targetIdentifier.Name, operation.ExpectedTargetName, StringComparison.Ordinal) ||
            !string.Equals(targetVariable.Name, operation.ExpectedTargetName, StringComparison.Ordinal))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                "For assignment initializer no longer has the expected operator, target, or value.",
                assignment);
        }

        OperationPlan? assignmentTypeFailure = RequireType(
            assignment,
            operation.ExpectedTypeId,
            "for assignment initializer",
            boundNodes);
        OperationPlan? targetTypeFailure = RequireType(
            assignment.Target,
            operation.ExpectedTypeId,
            "for assignment initializer target",
            boundNodes);
        OperationPlan? valueTypeFailure = RequireType(
            assignment.Value,
            operation.ExpectedTypeId,
            "for assignment initializer value",
            boundNodes);
        OperationPlan? definitionTypeFailure = RequireType(
            targetVariable,
            operation.ExpectedTypeId,
            "for assignment initializer target definition",
            boundNodes);
        if (assignmentTypeFailure is not null || targetTypeFailure is not null ||
            valueTypeFailure is not null || definitionTypeFailure is not null)
        {
            return assignmentTypeFailure ?? targetTypeFailure ?? valueTypeFailure ??
                definitionTypeFailure!;
        }

        return PlanStatementExpressionReplacement(
            "for assignment initializer value",
            loop,
            assignment.Value,
            operation.ExpectedTypeId,
            operation.NewValue,
            requireBoolean: false,
            blockLocation!,
            statementLocation!,
            hirNodes,
            patchIndex,
            boundNodes,
            types,
            graphSafetyIndex,
            documents);
    }

    private static OperationPlan PlanForIncrementorReplacement(
        ReplaceForIncrementorPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirUnaryExpression incrementor ||
            !patchIndex.TryGetExpression(
                incrementor.Id,
                out HirPatchIndex.ExpressionLocation? expressionLocation) ||
            expressionLocation!.ParentExpression is not null ||
            expressionLocation.ContainingStatement is not HirForStatement loop ||
            loop.Id != operation.ExpectedForStatementId ||
            operation.IncrementorIndex >= loop.Incrementors.Count ||
            loop.Incrementors[operation.IncrementorIndex].Id != incrementor.Id ||
            !patchIndex.TryGetStatement(
                loop.Id,
                out HirPatchIndex.StatementLocation? statementLocation) ||
            !patchIndex.TryGetBlock(
                statementLocation!.ParentBlock.Id,
                out HirPatchIndex.BlockLocation? blockLocation))
        {
            return KindMismatch(
                target,
                operation.TargetNodeId,
                "the indexed mutation incrementor of the expected for statement");
        }

        if (incrementor.Operator != operation.ExpectedOperator ||
            incrementor.IsPostfix != operation.ExpectedIsPostfix ||
            incrementor.Operand.Id != operation.ExpectedOperandNodeId ||
            incrementor.Operand is not HirIdentifierExpression operandIdentifier ||
            !boundNodes.TryGetValue(operandIdentifier.Id, out BoundNodeInfo? operandBinding) ||
            operandBinding.ReferencedSymbolId != operation.ExpectedOperandDefinitionId ||
            !string.Equals(
                operandIdentifier.Name,
                operation.ExpectedOperandName,
                StringComparison.Ordinal) ||
            !hirNodes.TryGetValue(operation.ExpectedOperandDefinitionId, out HirNode? operandDefinition))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                "For incrementor no longer has the expected mutation or bound operand.",
                incrementor);
        }

        OperationPlan? incrementorTypeFailure = RequireType(
            incrementor,
            operation.ExpectedTypeId,
            "for incrementor",
            boundNodes);
        OperationPlan? operandTypeFailure = RequireType(
            incrementor.Operand,
            operation.ExpectedTypeId,
            "for incrementor operand",
            boundNodes);
        OperationPlan? definitionTypeFailure = RequireType(
            operandDefinition,
            operation.ExpectedTypeId,
            "for incrementor operand definition",
            boundNodes);
        if (incrementorTypeFailure is not null || operandTypeFailure is not null ||
            definitionTypeFailure is not null)
        {
            return incrementorTypeFailure ?? operandTypeFailure ?? definitionTypeFailure!;
        }

        if (!types.TryGetValue(operation.ExpectedTypeId, out TypeDescriptor? mutationType) ||
            (!IsArithmeticOperatorType(mutationType) && mutationType.Kind != UnifiedTypeKind.Enum))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                "The for incrementor type no longer supports ++ or --.",
                incrementor);
        }

        if (!graphSafetyIndex.IsInsertionReachable(blockLocation!, statementLocation!.Index))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.UnreachableInsertion,
                "A for incrementor can be edited only when its loop is CFG-reachable.",
                incrementor);
        }

        ExpressionBuildResult newOperand = BuildPatchExpression(
            operation.NewOperand,
            blockLocation!,
            statementLocation.Index,
            hirNodes,
            patchIndex,
            graphSafetyIndex,
            boundNodes,
            types,
            authoredForLocals: null,
            loop.Id);
        if (newOperand.Syntax is not IdentifierNameSyntax ||
            newOperand.Type?.Id != operation.ExpectedTypeId)
        {
            return DiagnosticPlan(
                newOperand.DiagnosticCode ?? SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                newOperand.DiagnosticMessage ??
                    "A replacement for incrementor operand must be one same-typed writable local.",
                incrementor);
        }

        if (!TryFindSyntax(
                documents,
                incrementor,
                out ParsedDocument? document,
                out ExpressionSyntax? syntax) ||
            syntax is not PrefixUnaryExpressionSyntax and not PostfixUnaryExpressionSyntax)
        {
            return MissingSyntax(incrementor, "for incrementor");
        }

        if (syntax.DescendantTrivia(descendIntoTrivia: true).Any(IsMeaningfulTrivia))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidReplacement,
                "The existing for incrementor contains comments or directives that cannot be " +
                "discarded safely.",
                incrementor);
        }

        SyntaxKind kind = (operation.NewOperator, operation.NewIsPostfix) switch
        {
            ("++", false) => SyntaxKind.PreIncrementExpression,
            ("--", false) => SyntaxKind.PreDecrementExpression,
            ("++", true) => SyntaxKind.PostIncrementExpression,
            ("--", true) => SyntaxKind.PostDecrementExpression,
            _ => SyntaxKind.None,
        };
        if (kind == SyntaxKind.None)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                "The replacement for incrementor operator is unsupported.",
                incrementor);
        }

        var pins = new List<PendingPin>();
        ExpressionSyntax operandSyntax = newOperand.Syntax;
        LocalReferencePatchExpression authoredOperand =
            (LocalReferencePatchExpression)operation.NewOperand;
        if (authoredOperand.SourceDefinitionId == operation.ExpectedOperandDefinitionId &&
            string.Equals(
                authoredOperand.ExpectedName,
                operation.ExpectedOperandName,
                StringComparison.Ordinal))
        {
            var operandAnnotation = new SyntaxAnnotation(
                IdentityAnnotationKind,
                incrementor.Operand.Id.Value);
            operandSyntax = operandSyntax.WithAdditionalAnnotations(operandAnnotation);
            pins.Add(new PendingPin(
                incrementor.Operand.Id,
                GetIdentityKind(incrementor.Operand),
                operandAnnotation));
        }

        ExpressionSyntax replacement = operation.NewIsPostfix
            ? SyntaxFactory.PostfixUnaryExpression(kind, operandSyntax)
            : SyntaxFactory.PrefixUnaryExpression(kind, operandSyntax);
        var incrementorAnnotation = new SyntaxAnnotation(
            IdentityAnnotationKind,
            incrementor.Id.Value);
        replacement = FormatReplacementExpression(replacement, syntax)
            .WithAdditionalAnnotations(incrementorAnnotation);
        pins.Add(new PendingPin(incrementor.Id, GetIdentityKind(incrementor), incrementorAnnotation));
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            syntax,
            replacement,
            pins));
    }

    private static OperationPlan PlanWhileConditionReplacement(
        ReplaceWhileConditionPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirWhileStatement loop ||
            !patchIndex.TryGetStatement(
                loop.Id,
                out HirPatchIndex.StatementLocation? statementLocation) ||
            !patchIndex.TryGetBlock(
                statementLocation!.ParentBlock.Id,
                out HirPatchIndex.BlockLocation? blockLocation))
        {
            return KindMismatch(target, operation.TargetNodeId, "a while statement");
        }

        if (loop.Condition.Id != operation.ExpectedConditionNodeId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"While condition precondition failed: current endpoint is '{loop.Condition.Id}', " +
                $"not '{operation.ExpectedConditionNodeId}'.",
                loop);
        }

        return PlanStatementExpressionReplacement(
            "while condition",
            loop,
            loop.Condition,
            operation.ExpectedConditionTypeId,
            operation.NewCondition,
            requireBoolean: true,
            blockLocation!,
            statementLocation!,
            hirNodes,
            patchIndex,
            boundNodes,
            types,
            graphSafetyIndex,
            documents);
    }

    private static OperationPlan PlanIfConditionReplacement(
        ReplaceIfConditionPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirIfStatement branch ||
            !patchIndex.TryGetStatement(
                branch.Id,
                out HirPatchIndex.StatementLocation? statementLocation) ||
            !patchIndex.TryGetBlock(
                statementLocation!.ParentBlock.Id,
                out HirPatchIndex.BlockLocation? blockLocation))
        {
            return KindMismatch(target, operation.TargetNodeId, "an if statement");
        }

        if (branch.Condition.Id != operation.ExpectedConditionNodeId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"If condition precondition failed: current endpoint is '{branch.Condition.Id}', " +
                $"not '{operation.ExpectedConditionNodeId}'.",
                branch);
        }

        return PlanStatementExpressionReplacement(
            "if condition",
            branch,
            branch.Condition,
            operation.ExpectedConditionTypeId,
            operation.NewCondition,
            requireBoolean: true,
            blockLocation!,
            statementLocation!,
            hirNodes,
            patchIndex,
            boundNodes,
            types,
            graphSafetyIndex,
            documents);
    }

    private static OperationPlan PlanDoWhileConditionReplacement(
        ReplaceDoWhileConditionPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirDoWhileStatement loop ||
            !patchIndex.TryGetStatement(
                loop.Id,
                out HirPatchIndex.StatementLocation? statementLocation) ||
            !patchIndex.TryGetBlock(
                statementLocation!.ParentBlock.Id,
                out HirPatchIndex.BlockLocation? blockLocation))
        {
            return KindMismatch(target, operation.TargetNodeId, "a do-while statement");
        }

        if (loop.Condition.Id != operation.ExpectedConditionNodeId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Do-while condition precondition failed: current endpoint is " +
                $"'{loop.Condition.Id}', not '{operation.ExpectedConditionNodeId}'.",
                loop);
        }

        return PlanStatementExpressionReplacement(
            "do-while condition",
            loop,
            loop.Condition,
            operation.ExpectedConditionTypeId,
            operation.NewCondition,
            requireBoolean: true,
            blockLocation!,
            statementLocation!,
            hirNodes,
            patchIndex,
            boundNodes,
            types,
            graphSafetyIndex,
            documents);
    }

    private static OperationPlan PlanForEachCollectionReplacement(
        ReplaceForEachCollectionPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirForEachStatement loop ||
            !patchIndex.TryGetStatement(
                loop.Id,
                out HirPatchIndex.StatementLocation? statementLocation) ||
            !patchIndex.TryGetBlock(
                statementLocation!.ParentBlock.Id,
                out HirPatchIndex.BlockLocation? blockLocation))
        {
            return KindMismatch(target, operation.TargetNodeId, "a foreach statement");
        }

        if (loop.Collection.Id != operation.ExpectedCollectionNodeId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Foreach collection precondition failed: current endpoint is " +
                $"'{loop.Collection.Id}', not '{operation.ExpectedCollectionNodeId}'.",
                loop);
        }

        return PlanStatementExpressionReplacement(
            "foreach collection",
            loop,
            loop.Collection,
            operation.ExpectedCollectionTypeId,
            operation.NewCollection,
            requireBoolean: false,
            blockLocation!,
            statementLocation!,
            hirNodes,
            patchIndex,
            boundNodes,
            types,
            graphSafetyIndex,
            documents,
            requireForEachCollection: true);
    }

    private static OperationPlan PlanLocalInitializerReplacement(
        ReplaceLocalInitializerPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirVariableDeclarationStatement { Initializer: not null } variable ||
            !patchIndex.TryGetStatement(
                variable.Id,
                out HirPatchIndex.StatementLocation? statementLocation) ||
            !patchIndex.TryGetBlock(
                statementLocation!.ParentBlock.Id,
                out HirPatchIndex.BlockLocation? blockLocation))
        {
            return KindMismatch(
                target,
                operation.TargetNodeId,
                "a local declaration with an initializer");
        }

        if (!string.Equals(variable.Name, operation.ExpectedName, StringComparison.Ordinal) ||
            variable.Initializer.Id != operation.ExpectedInitializerNodeId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Local initializer precondition failed for '{variable.Id}'. Current name/endpoint " +
                $"are '{variable.Name}' and '{variable.Initializer.Id}'.",
                variable);
        }

        OperationPlan? variableTypeFailure = RequireType(
            variable,
            operation.ExpectedTypeId,
            "local declaration",
            boundNodes);
        if (variableTypeFailure is not null)
        {
            return variableTypeFailure;
        }

        return PlanStatementExpressionReplacement(
            "local initializer",
            variable,
            variable.Initializer,
            operation.ExpectedTypeId,
            operation.NewInitializer,
            requireBoolean: false,
            blockLocation!,
            statementLocation!,
            hirNodes,
            patchIndex,
            boundNodes,
            types,
            graphSafetyIndex,
            documents);
    }

    private static OperationPlan PlanReturnValueReplacement(
        ReplaceReturnValuePatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, MethodDescriptor> methods,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirReturnStatement { Expression: not null } returnStatement ||
            !patchIndex.TryGetStatement(
                returnStatement.Id,
                out HirPatchIndex.StatementLocation? statementLocation) ||
            !patchIndex.TryGetBlock(
                statementLocation!.ParentBlock.Id,
                out HirPatchIndex.BlockLocation? blockLocation))
        {
            return KindMismatch(
                target,
                operation.TargetNodeId,
                "a value-return statement");
        }

        if (returnStatement.Expression.Id != operation.ExpectedValueNodeId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Return value precondition failed: current endpoint is " +
                $"'{returnStatement.Expression.Id}', not '{operation.ExpectedValueNodeId}'.",
                returnStatement);
        }

        if (!methods.TryGetValue(blockLocation!.Method.Id, out MethodDescriptor? method) ||
            method.ReturnTypeId != operation.ExpectedReturnTypeId ||
            blockLocation.Method.IsAsync ||
            !types.TryGetValue(operation.ExpectedReturnTypeId, out TypeDescriptor? returnType) ||
            !returnType.IsGraphSafe ||
            returnType.Kind is UnifiedTypeKind.Void or UnifiedTypeKind.TaskLike)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"Return replacement type '{operation.ExpectedReturnTypeId}' does not match a " +
                $"supported graph-safe value return of method '{blockLocation.Method.Name}'.",
                returnStatement);
        }

        return PlanStatementExpressionReplacement(
            "return value",
            returnStatement,
            returnStatement.Expression,
            operation.ExpectedReturnTypeId,
            operation.NewValue,
            requireBoolean: false,
            blockLocation,
            statementLocation!,
            hirNodes,
            patchIndex,
            boundNodes,
            types,
            graphSafetyIndex,
            documents);
    }

    private static OperationPlan PlanStatementExpressionReplacement(
        string role,
        HirStatement containingStatement,
        HirExpression currentExpression,
        SemanticId expectedTypeId,
        PatchExpression newExpression,
        bool requireBoolean,
        HirPatchIndex.BlockLocation blockLocation,
        HirPatchIndex.StatementLocation statementLocation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents,
        SemanticId? forHeaderLoopId = null,
        bool requireForEachCollection = false)
    {
        OperationPlan? typeFailure = RequireType(
            currentExpression,
            expectedTypeId,
            role,
            boundNodes);
        if (typeFailure is not null)
        {
            return typeFailure;
        }

        if (!graphSafetyIndex.IsInsertionReachable(blockLocation, statementLocation.Index))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.UnreachableInsertion,
                $"A {role} can be edited only when its containing statement is CFG-reachable.",
                containingStatement);
        }

        ExpressionBuildResult replacement = requireForEachCollection
            ? BuildForEachCollectionExpression(
                newExpression,
                blockLocation,
                statementLocation.Index,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                types)
            : BuildPatchExpression(
                newExpression,
                blockLocation,
                statementLocation.Index,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                types,
                authoredForLocals: null,
                forHeaderLoopId);
        if (replacement.Syntax is null || replacement.Type is null)
        {
            return DiagnosticPlan(
                replacement.DiagnosticCode ?? SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                replacement.DiagnosticMessage ?? $"The replacement {role} is invalid.",
                containingStatement);
        }

        if (replacement.Type.Id != expectedTypeId ||
            requireBoolean &&
            (replacement.Type.Kind != UnifiedTypeKind.Boolean ||
                !string.Equals(
                    replacement.Type.MetadataName,
                    "System.Boolean",
                    StringComparison.Ordinal)))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                requireBoolean
                    ? $"A replacement {role} must have semantic type 'System.Boolean'."
                    : $"A replacement {role} must preserve semantic type '{expectedTypeId}'.",
                containingStatement);
        }

        if (!TryFindSyntax(
                documents,
                currentExpression,
                out ParsedDocument? document,
                out ExpressionSyntax? syntax))
        {
            return MissingSyntax(currentExpression, role);
        }

        if (syntax!.DescendantTrivia(descendIntoTrivia: true).Any(IsMeaningfulTrivia))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidReplacement,
                $"The existing {role} contains comments or directives that cannot be discarded safely.",
                currentExpression);
        }

        ExpressionSyntax formatted = FormatReplacementExpression(replacement.Syntax, syntax);
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            syntax,
            formatted,
            []));
    }

    private static OperationPlan PlanStatementDeletion(
        DeleteStatementPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        HirUseDefinitionIndex useDefinitionIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, MethodDescriptor> methods,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirStatement statement ||
            !patchIndex.TryGetStatement(statement.Id, out HirPatchIndex.StatementLocation? location))
        {
            return KindMismatch(target, operation.TargetNodeId, "a statement");
        }

        if (location!.ParentBlock.Id != operation.ExpectedParentBlockId ||
            !StatementSiblingsMatch(
                location,
                operation.ExpectedPreviousSiblingId,
                operation.ExpectedNextSiblingId))
        {
            return SiblingFailure(
                statement,
                operation.ExpectedPreviousSiblingId,
                operation.ExpectedNextSiblingId,
                operation.ExpectedParentBlockId);
        }

        OperationPlan? safetyFailure = ValidateDeletionSafety(
            location,
            useDefinitionIndex,
            boundNodes,
            methods);
        if (safetyFailure is not null)
        {
            return safetyFailure;
        }

        HirBlockStatement parentBlock = location.ParentBlock;
        if (!TryFindEditableStatementContainer(
                documents,
                parentBlock,
                out ParsedDocument? document,
                out EditableStatementContainer? container) ||
            container!.Statements.Count != parentBlock.Statements.Count ||
            RoslynSourceSpan.FromNode(
                container.Statements[location.Index],
                document!.Source.LogicalPath) != statement.Span)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                "Statement deletion requires an explicit C# block or switch-section body whose " +
                "statement list matches the analyzed HIR block.",
                statement);
        }

        SyntaxNode removed = RemoveStatementPreservingComments(
            document!,
            container,
            location.Index);
        SyntaxList<StatementSyntax> removedStatements = GetStatements(removed);
        HirStatement[] survivingHir = parentBlock.Statements
            .Where((_, index) => index != location.Index)
            .ToArray();
        (List<StatementSyntax> annotatedStatements, List<PendingPin> pins) =
            AnnotateStatements(removedStatements, survivingHir);
        var blockAnnotation = new SyntaxAnnotation(IdentityAnnotationKind, parentBlock.Id.Value);
        SyntaxNode replacement = WithStatements(
                removed,
                SyntaxFactory.List(annotatedStatements))
            .WithAdditionalAnnotations(blockAnnotation);
        pins.Add(new PendingPin(parentBlock.Id, "block", blockAnnotation));
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            container.Syntax,
            replacement,
            pins));
    }

    private static OperationPlan PlanTemporaryIntroduction(
        IntroduceTemporaryPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        HirUseDefinitionIndex useDefinitionIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, MethodDescriptor> methods,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirExpression expression ||
            !patchIndex.TryGetExpression(
                expression.Id,
                out HirPatchIndex.ExpressionLocation? expressionLocation))
        {
            return KindMismatch(target, operation.TargetNodeId, "an expression");
        }

        if (expressionLocation!.ContainingStatement.Id != operation.ExpectedContainingStatementId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Temporary introduction expected containing statement " +
                $"'{operation.ExpectedContainingStatementId}', but expression '{expression.Id}' is currently " +
                $"inside '{expressionLocation.ContainingStatement.Id}'.",
                expression);
        }

        if (!patchIndex.TryGetStatement(
                expressionLocation.ContainingStatement.Id,
                out HirPatchIndex.StatementLocation? statementLocation) ||
            statementLocation!.ParentBlock.Id != operation.ExpectedParentBlockId ||
            !StatementSiblingsMatch(
                statementLocation,
                operation.ExpectedPreviousSiblingId,
                operation.ExpectedNextSiblingId))
        {
            return SiblingFailure(
                expressionLocation.ContainingStatement,
                operation.ExpectedPreviousSiblingId,
                operation.ExpectedNextSiblingId,
                operation.ExpectedParentBlockId);
        }

        HirExpression? containingExpression = expressionLocation.ContainingStatement switch
        {
            HirReturnStatement { Expression: not null } returnStatement => returnStatement.Expression,
            HirVariableDeclarationStatement { Initializer: not null } variable => variable.Initializer,
            _ => null,
        };
        if (containingExpression is null)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                "Temporary introduction currently requires a return value or local initializer " +
                "inside an explicit block.",
                expressionLocation.ContainingStatement);
        }

        if (!types.TryGetValue(operation.ExpectedTypeId, out TypeDescriptor? expectedType) ||
            !expectedType.IsGraphSafe ||
            !TypeNameMatches(operation.TypeName, expectedType))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"Temporary type '{operation.TypeName}' does not match graph-safe semantic type " +
                $"'{operation.ExpectedTypeId}'.",
                expression);
        }

        OperationPlan? typeFailure = RequireType(
            expression,
            operation.ExpectedTypeId,
            "temporary expression",
            boundNodes);
        if (typeFailure is not null)
        {
            return typeFailure;
        }

        if (HasNameConflict(expressionLocation.Method, operation.Name))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.NameConflict,
                $"A parameter or local named '{operation.Name}' already exists in method " +
                $"'{expressionLocation.Method.Name}'.",
                expression);
        }

        if (!IsStablePureExpression(
                containingExpression,
                useDefinitionIndex,
                boundNodes,
                methods,
                types))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.UnsafeTemporaryIntroduction,
                "Temporary introduction would move evaluation within a statement that is not a " +
                "stable, effect-free value expression.",
                expression);
        }

        HirBlockStatement parentBlock = statementLocation.ParentBlock;
        if (!TryFindSyntax(
                documents,
                parentBlock,
                out ParsedDocument? document,
                out BlockSyntax? blockSyntax) ||
            blockSyntax!.Statements.Count != parentBlock.Statements.Count)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                "Temporary introduction currently requires an explicit C# block whose statement list " +
                "matches the analyzed HIR block.",
                parentBlock);
        }

        StatementSyntax containingSyntax = blockSyntax.Statements[statementLocation.Index];
        if (RoslynSourceSpan.FromNode(
                containingSyntax,
                document!.Source.LogicalPath) != expressionLocation.ContainingStatement.Span)
        {
            return MissingSyntax(expressionLocation.ContainingStatement, "statement");
        }

        (List<StatementSyntax> annotatedStatements, List<PendingPin> pins) =
            AnnotateStatements(blockSyntax, parentBlock.Statements);
        if (!TryAnnotateExpressions(
                document!,
                containingSyntax,
                expressionLocation.ContainingStatement,
                null,
                out StatementSyntax? annotatedContaining,
                out IReadOnlyDictionary<SemanticId, SyntaxAnnotation>? expressionAnnotations,
                out IReadOnlyList<PendingPin>? expressionPins) ||
            !expressionAnnotations!.TryGetValue(expression.Id, out SyntaxAnnotation? targetAnnotation))
        {
            return MissingSyntax(expression, "expression");
        }

        SyntaxAnnotation statementAnnotation = pins
            .Single(pin => pin.Id == expressionLocation.ContainingStatement.Id)
            .Annotation;
        annotatedContaining = annotatedContaining!.WithAdditionalAnnotations(statementAnnotation);

        ExpressionSyntax annotatedTarget = annotatedContaining
            .GetAnnotatedNodes(targetAnnotation)
            .OfType<ExpressionSyntax>()
            .Single();
        if (!TryCreateTemporaryStatement(
                operation.Name,
                operation.TypeName,
                annotatedTarget,
                out LocalDeclarationStatementSyntax? temporaryStatement))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                "The temporary name, type, or initializer could not be represented as one C# " +
                "local declaration.",
                expression);
        }

        IdentifierNameSyntax replacementIdentifier = SyntaxFactory.IdentifierName(operation.Name)
            .WithTriviaFrom(annotatedTarget);
        StatementSyntax updatedContaining = annotatedContaining.ReplaceNode(
            annotatedTarget,
            replacementIdentifier);
        annotatedStatements[statementLocation.Index] = updatedContaining;
        InsertionFormatting formatting = GetInsertionFormatting(
            document!,
            blockSyntax,
            statementLocation.Index);
        temporaryStatement = temporaryStatement!
            .WithLeadingTrivia(SyntaxFactory.Whitespace(formatting.Indentation))
            .WithTrailingTrivia(SyntaxFactory.EndOfLine(formatting.EndOfLine));
        annotatedStatements.Insert(statementLocation.Index, temporaryStatement);

        pins.AddRange(expressionPins!);
        var blockAnnotation = new SyntaxAnnotation(IdentityAnnotationKind, parentBlock.Id.Value);
        BlockSyntax replacement = blockSyntax
            .WithStatements(SyntaxFactory.List(annotatedStatements))
            .WithAdditionalAnnotations(blockAnnotation);
        pins.Add(new PendingPin(parentBlock.Id, "block", blockAnnotation));
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            blockSyntax,
            replacement,
            pins));
    }

    private static OperationPlan PlanStatementMove(
        MoveStatementPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirStatement statement ||
            !patchIndex.TryGetStatement(statement.Id, out HirPatchIndex.StatementLocation? location))
        {
            return KindMismatch(target, operation.TargetNodeId, "a movable statement");
        }

        if (location!.ParentBlock.Id != operation.ExpectedSourceParentBlockId ||
            !StatementSiblingsMatch(
                location,
                operation.ExpectedSourcePreviousSiblingId,
                operation.ExpectedSourceNextSiblingId))
        {
            return SiblingFailure(
                statement,
                operation.ExpectedSourcePreviousSiblingId,
                operation.ExpectedSourceNextSiblingId,
                operation.ExpectedSourceParentBlockId);
        }

        if (!patchIndex.TryGetBlock(
                operation.DestinationParentBlockId,
                out HirPatchIndex.BlockLocation? destinationLocation))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.SiblingPreconditionFailed,
                $"The destination block '{operation.DestinationParentBlockId}' no longer exists. " +
                "Refresh the graph before reconnecting the wire.",
                statement);
        }

        HirBlockStatement sourceBlock = location.ParentBlock;
        HirBlockStatement destinationBlock = destinationLocation!.Block;
        bool crossesRegion = destinationBlock.Id != sourceBlock.Id;
        if (destinationLocation.Method.Id != location.Method.Id)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidControlFlowReconnection,
                "A control wire cannot move a statement between functions. Select a destination " +
                "inside the same Visual method.",
                statement);
        }

        if (crossesRegion && statement is not (
                HirVariableDeclarationStatement or HirExpressionStatement))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidControlFlowReconnection,
                "Cross-region control reconnection currently moves only a variable declaration or " +
                "expression statement. Use the explicit branch, loop, switch, or terminal-statement " +
                "transform for this node.",
                statement);
        }

        HirStatement[] sourceRemaining = sourceBlock.Statements
            .Where(candidate => candidate.Id != statement.Id)
            .ToArray();
        IReadOnlyList<HirStatement> destinationCandidates = crossesRegion
            ? destinationBlock.Statements
            : sourceRemaining;
        if (!TryGetInsertionIndex(
                destinationCandidates,
                operation.ExpectedDestinationPreviousSiblingId,
                operation.ExpectedDestinationNextSiblingId,
                out int destinationIndex))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.SiblingPreconditionFailed,
                "The control-flow destination gap changed after the Surface was generated. " +
                "Refresh the graph before reconnecting the wire.",
                statement);
        }

        var destinationStatements = destinationCandidates.ToList();
        destinationStatements.Insert(destinationIndex, statement);
        if (!crossesRegion && destinationStatements.Select(static candidate => candidate.Id).SequenceEqual(
                sourceBlock.Statements.Select(static candidate => candidate.Id)))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidControlFlowReconnection,
                "The selected control output already precedes this statement; the reconnection " +
                "would not change structured control flow.",
                statement);
        }

        if (HasNonFinalTerminal(sourceRemaining) || HasNonFinalTerminal(destinationStatements))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidControlFlowReconnection,
                "A return, break, or continue must remain the final statement of its structured block.",
                statement);
        }

        string? dependencyFailure = FindLocalOrderingFailure(sourceRemaining, boundNodes) ??
            FindLocalOrderingFailure(destinationStatements, boundNodes);
        if (dependencyFailure is not null)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidControlFlowReconnection,
                dependencyFailure,
                statement);
        }

        if (!TryFindEditableStatementContainer(
                documents,
                sourceBlock,
                out ParsedDocument? sourceDocument,
                out EditableStatementContainer? sourceContainer) ||
            sourceContainer!.Statements.Count != sourceBlock.Statements.Count ||
            !TryFindEditableStatementContainer(
                documents,
                destinationBlock,
                out ParsedDocument? destinationDocument,
                out EditableStatementContainer? destinationContainer) ||
            destinationContainer!.Statements.Count != destinationBlock.Statements.Count ||
            sourceDocument!.Source.LogicalPath != destinationDocument!.Source.LogicalPath)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidControlFlowReconnection,
                "Statement movement requires an explicit C# block or switch-section body whose " +
                "statement order still matches the analyzed HIR.",
                statement);
        }

        if (sourceDocument.Source.IdentitySidecar is null)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PlanRequiresPersistentIdentity,
                "Control-flow reconnection requires a .usids identity sidecar so every moved " +
                "statement and nested expression keeps its semantic identity.",
                statement);
        }

        if (!crossesRegion)
        {
            (List<StatementSyntax> annotatedStatements, List<PendingPin> pins) =
                AnnotateStatementTrees(
                    sourceDocument,
                    sourceContainer.Statements,
                    sourceBlock.Statements);
            StatementSyntax movedSyntax = annotatedStatements[location.Index];
            annotatedStatements.RemoveAt(location.Index);
            annotatedStatements.Insert(destinationIndex, movedSyntax);
            var blockAnnotation = new SyntaxAnnotation(IdentityAnnotationKind, sourceBlock.Id.Value);
            SyntaxNode replacement = sourceContainer.WithStatements(
                    SyntaxFactory.List(annotatedStatements))
                .WithAdditionalAnnotations(blockAnnotation);
            pins.Add(new PendingPin(sourceBlock.Id, "block", blockAnnotation));
            return SuccessPlan(sourceDocument, new PendingReplacement(
                sourceDocument.Source.LogicalPath,
                sourceContainer.Syntax,
                replacement,
                pins));
        }

        if (!TryPrepareMethodMutation(
                documents,
                location.Method,
                out ParsedDocument? document,
                out MethodDeclarationSyntax? originalMethod,
                out MethodDeclarationSyntax? annotatedMethod,
                out IReadOnlyDictionary<SemanticId, SyntaxAnnotation>? identityAnnotations,
                out List<PendingPin>? methodPins,
                out string? identityFailure) ||
            !identityAnnotations!.TryGetValue(sourceBlock.Id, out SyntaxAnnotation? sourceAnnotation) ||
            !identityAnnotations.TryGetValue(destinationBlock.Id, out SyntaxAnnotation? destinationAnnotation) ||
            !identityAnnotations.TryGetValue(statement.Id, out SyntaxAnnotation? statementAnnotation))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PlanRequiresPersistentIdentity,
                identityFailure ??
                    "Cross-region control reconnection could not pin the source block, destination " +
                    "block, and moved statement in the persistent identity sidecar.",
                statement);
        }

        SyntaxNode annotatedSourceSyntax = annotatedMethod!
            .GetAnnotatedNodes(sourceAnnotation)
            .Single();
        SyntaxNode annotatedDestinationSyntax = annotatedMethod
            .GetAnnotatedNodes(destinationAnnotation)
            .Single();
        StatementSyntax movedStatement = annotatedMethod
            .GetAnnotatedNodes(statementAnnotation)
            .OfType<StatementSyntax>()
            .Single();
        if (!TryCreateEditableStatementContainer(
                annotatedSourceSyntax,
                out EditableStatementContainer? annotatedSource) ||
            !TryCreateEditableStatementContainer(
                annotatedDestinationSyntax,
                out EditableStatementContainer? annotatedDestination) ||
            annotatedDestinationSyntax.Ancestors().Contains(movedStatement))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidControlFlowReconnection,
                "The destination is not an independent explicit block or switch-section body. " +
                "Move the enclosing structure with its dedicated transform instead.",
                statement);
        }

        InsertionFormatting destinationFormatting = GetInsertionFormatting(
            document!,
            destinationContainer,
            destinationIndex);
        movedStatement = ReindentMovedStatement(movedStatement, destinationFormatting.Indentation);
        SyntaxNode[] changedContainers = [annotatedSource!.Syntax, annotatedDestination!.Syntax];
        SyntaxNode replacementMethod = annotatedMethod.ReplaceNodes(
            changedContainers,
            (original, rewritten) =>
            {
                if (!TryCreateEditableStatementContainer(
                        rewritten,
                        out EditableStatementContainer? rewrittenContainer))
                {
                    throw new InvalidOperationException(
                        "A tracked statement container changed syntax kind during movement.");
                }

                var statements = rewrittenContainer!.Statements.ToList();
                if (original == annotatedSource.Syntax)
                {
                    int sourceIndex = statements.FindIndex(candidate =>
                        candidate.HasAnnotation(statementAnnotation));
                    if (sourceIndex < 0)
                    {
                        throw new InvalidOperationException(
                            "The moved statement disappeared from its tracked source block.");
                    }
                    statements.RemoveAt(sourceIndex);
                }
                else
                {
                    statements.Insert(destinationIndex, movedStatement);
                }
                return rewrittenContainer.WithStatements(SyntaxFactory.List(statements));
            });
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            originalMethod!,
            replacementMethod,
            FilterPinsForSyntax(replacementMethod, methodPins!)));
    }

    private static OperationPlan PlanSwitchSectionInsertion(
        InsertSwitchSectionPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirSwitchStatement switchStatement ||
            !patchIndex.TryGetStatement(
                switchStatement.Id,
                out HirPatchIndex.StatementLocation? location) ||
            !patchIndex.TryGetBlock(
                location!.ParentBlock.Id,
                out HirPatchIndex.BlockLocation? blockLocation))
        {
            return KindMismatch(target, operation.TargetNodeId, "a switch statement");
        }

        if (!TryGetSwitchSectionInsertionIndex(
                switchStatement.Sections,
                operation.ExpectedPreviousSectionId,
                operation.ExpectedNextSectionId,
                out int sectionIndex))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.SiblingPreconditionFailed,
                "The switch-section insertion gap changed. Refresh the Surface before editing.",
                switchStatement);
        }

        if (!TryGetSwitchGoverningType(
                switchStatement,
                boundNodes,
                types,
                out TypeDescriptor? governingType))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                "The switch governing expression no longer has one graph-safe semantic type.",
                switchStatement);
        }

        SwitchSectionBuildResult built = BuildSwitchSection(
            operation.NewSection,
            governingType!,
            blockLocation!,
            location.Index,
            hirNodes,
            patchIndex,
            graphSafetyIndex,
            boundNodes,
            types);
        if (built.Syntax is null)
        {
            return DiagnosticPlan(
                built.DiagnosticCode ?? SemanticPatchDiagnosticCodes.InvalidSwitchSection,
                built.DiagnosticMessage ?? "The new switch section is invalid.",
                switchStatement);
        }

        if (operation.NewSection.Labels.Any(static label => label is DefaultSwitchLabelPatch) &&
            switchStatement.Sections.SelectMany(static section => section.Labels)
                .Any(static label => label is HirDefaultSwitchLabel))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidSwitchSection,
                "A switch cannot contain more than one default section.",
                switchStatement);
        }

        if (!TryPrepareSwitchMutation(
                documents,
                switchStatement,
                hirNodes,
                out ParsedDocument? document,
                out SwitchStatementSyntax? originalSyntax,
                out SwitchStatementSyntax? annotatedSyntax,
                out List<PendingPin>? pins,
                out string? identityFailure))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PlanRequiresPersistentIdentity,
                identityFailure!,
                switchStatement);
        }

        SwitchSectionSyntax formatted = FormatInsertedSwitchSection(
            document!,
            annotatedSyntax!,
            sectionIndex,
            built.Syntax);
        SwitchStatementSyntax replacement = annotatedSyntax!.WithSections(
            annotatedSyntax.Sections.Insert(sectionIndex, formatted));
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            originalSyntax!,
            replacement,
            FilterPinsForSyntax(replacement, pins!)));
    }

    private static OperationPlan PlanSwitchSectionReplacement(
        ReplaceSwitchSectionPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!TryResolveSwitchSection(
                hirNodes,
                operation.TargetNodeId,
                operation.ExpectedSwitchStatementId,
                out HirSwitchStatement? switchStatement,
                out HirSwitchSection? section,
                out int sectionIndex))
        {
            HirNode? target = hirNodes.GetValueOrDefault(operation.TargetNodeId);
            return target is null
                ? MissingTarget(operation.TargetNodeId)
                : KindMismatch(target, operation.TargetNodeId, "a section of the expected switch");
        }

        if (!section!.Labels.Select(static label => label.Id)
            .SequenceEqual(operation.ExpectedLabelIds))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                "The switch-section labels changed. Refresh the Surface before relabeling.",
                section);
        }

        int existingDefaultsOutsideSection = switchStatement!.Sections
            .Where(candidate => candidate.Id != section.Id)
            .SelectMany(static candidate => candidate.Labels)
            .Count(static label => label is HirDefaultSwitchLabel);
        if (existingDefaultsOutsideSection > 0 &&
            operation.NewLabels.Any(static label => label is DefaultSwitchLabelPatch))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidSwitchSection,
                "A switch cannot contain more than one default label.",
                section);
        }

        if (!patchIndex.TryGetStatement(
                switchStatement.Id,
                out HirPatchIndex.StatementLocation? location) ||
            !patchIndex.TryGetBlock(
                location!.ParentBlock.Id,
                out HirPatchIndex.BlockLocation? blockLocation) ||
            !TryGetSwitchGoverningType(
                switchStatement,
                boundNodes,
                types,
                out TypeDescriptor? governingType))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                "The switch governing expression no longer has one graph-safe semantic type.",
                switchStatement);
        }

        SwitchSectionBuildResult built = BuildSwitchSection(
            new SwitchSectionPatch(operation.NewLabels),
            governingType!,
            blockLocation!,
            location.Index,
            hirNodes,
            patchIndex,
            graphSafetyIndex,
            boundNodes,
            types);
        if (built.Syntax is null)
        {
            return DiagnosticPlan(
                built.DiagnosticCode ?? SemanticPatchDiagnosticCodes.InvalidSwitchSection,
                built.DiagnosticMessage ?? "The replacement switch labels are invalid.",
                section);
        }

        if (!TryPrepareSwitchMutation(
                documents, switchStatement, hirNodes, out ParsedDocument? document,
                out SwitchStatementSyntax? originalSyntax,
                out SwitchStatementSyntax? annotatedSyntax,
                out List<PendingPin>? pins, out string? identityFailure))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PlanRequiresPersistentIdentity,
                identityFailure!,
                switchStatement);
        }

        SwitchSectionSyntax current = annotatedSyntax!.Sections[sectionIndex];
        SwitchSectionSyntax labelsOnly = FormatReplacementSwitchLabels(
            document!,
            current,
            built.Syntax.Labels);
        SwitchSectionSyntax changed = current.WithLabels(labelsOnly.Labels);
        SwitchStatementSyntax replacement = annotatedSyntax!.WithSections(
            annotatedSyntax.Sections.Replace(current, changed));
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            originalSyntax!,
            replacement,
            FilterPinsForSyntax(replacement, pins!)));
    }

    private static OperationPlan PlanSwitchSectionMove(
        MoveSwitchSectionPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!TryResolveSwitchSection(
                hirNodes, operation.TargetNodeId, operation.ExpectedSwitchStatementId,
                out HirSwitchStatement? switchStatement, out HirSwitchSection? section,
                out int sectionIndex))
        {
            HirNode? target = hirNodes.GetValueOrDefault(operation.TargetNodeId);
            return target is null
                ? MissingTarget(operation.TargetNodeId)
                : KindMismatch(target, operation.TargetNodeId, "a section of the expected switch");
        }

        if (!SwitchSectionSiblingsMatch(
                switchStatement!,
                sectionIndex,
                operation.ExpectedSourcePreviousSectionId,
                operation.ExpectedSourceNextSectionId))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.SiblingPreconditionFailed,
                "The source switch-section order changed. Refresh the Surface before moving it.",
                section!);
        }

        HirSwitchSection[] remaining = switchStatement!.Sections
            .Where(candidate => candidate.Id != section!.Id)
            .ToArray();
        if (!TryGetSwitchSectionInsertionIndex(
                remaining,
                operation.ExpectedDestinationPreviousSectionId,
                operation.ExpectedDestinationNextSectionId,
                out int destinationIndex))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.SiblingPreconditionFailed,
                "The destination switch-section gap changed. Refresh the Surface before moving it.",
                section!);
        }

        var reorderedIds = remaining.Select(static candidate => candidate.Id).ToList();
        reorderedIds.Insert(destinationIndex, section!.Id);
        if (reorderedIds.SequenceEqual(
                switchStatement.Sections.Select(static candidate => candidate.Id)))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidSwitchSection,
                "The switch section already occupies the requested position.",
                section);
        }

        if (!TryPrepareSwitchMutation(
                documents, switchStatement, hirNodes, out ParsedDocument? document,
                out SwitchStatementSyntax? originalSyntax,
                out SwitchStatementSyntax? annotatedSyntax,
                out List<PendingPin>? pins, out string? identityFailure))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PlanRequiresPersistentIdentity,
                identityFailure!,
                switchStatement);
        }

        var syntaxSections = annotatedSyntax!.Sections.ToList();
        SwitchSectionSyntax moved = syntaxSections[sectionIndex];
        syntaxSections.RemoveAt(sectionIndex);
        syntaxSections.Insert(destinationIndex, moved);
        SwitchStatementSyntax replacement = annotatedSyntax!.WithSections(
            SyntaxFactory.List(syntaxSections));
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            originalSyntax!,
            replacement,
            FilterPinsForSyntax(replacement, pins!)));
    }

    private static OperationPlan PlanSwitchSectionDeletion(
        DeleteSwitchSectionPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!TryResolveSwitchSection(
                hirNodes, operation.TargetNodeId, operation.ExpectedSwitchStatementId,
                out HirSwitchStatement? switchStatement, out HirSwitchSection? section,
                out int sectionIndex))
        {
            HirNode? target = hirNodes.GetValueOrDefault(operation.TargetNodeId);
            return target is null
                ? MissingTarget(operation.TargetNodeId)
                : KindMismatch(target, operation.TargetNodeId, "a section of the expected switch");
        }

        if (!SwitchSectionSiblingsMatch(
                switchStatement!,
                sectionIndex,
                operation.ExpectedPreviousSectionId,
                operation.ExpectedNextSectionId) ||
            !section!.Labels.Select(static label => label.Id)
                .SequenceEqual(operation.ExpectedLabelIds))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.SiblingPreconditionFailed,
                "The switch section or its labels changed. Refresh the Surface before deleting it.",
                section!);
        }

        if (section.Body.Statements.Count > 1 ||
            section.Body.Statements.Count == 1 &&
            section.Body.Statements[0] is not HirBreakStatement)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.UnsafeSwitchSectionDeletion,
                "Only an empty switch section or a generated break-only section can be deleted. " +
                "Delete authored body statements explicitly first.",
                section);
        }

        if (!TryPrepareSwitchMutation(
                documents, switchStatement!, hirNodes, out ParsedDocument? document,
                out SwitchStatementSyntax? originalSyntax,
                out SwitchStatementSyntax? annotatedSyntax,
                out List<PendingPin>? pins, out string? identityFailure))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PlanRequiresPersistentIdentity,
                identityFailure!,
                switchStatement!);
        }

        SwitchSectionSyntax syntaxSection = originalSyntax!.Sections[sectionIndex];
        if (syntaxSection.DescendantTrivia(descendIntoTrivia: true).Any(IsMeaningfulTrivia))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.UnsafeSwitchSectionDeletion,
                "The switch section contains comments or directives that cannot be discarded safely.",
                section);
        }

        SwitchStatementSyntax replacement = annotatedSyntax!.WithSections(
            annotatedSyntax.Sections.RemoveAt(sectionIndex));
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            originalSyntax,
            replacement,
            FilterPinsForSyntax(replacement, pins!)));
    }

    private static string? FindLocalOrderingFailure(
        IReadOnlyList<HirStatement> statements,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes)
    {
        IReadOnlyDictionary<SemanticId, int> declarationIndices = statements
            .Select((statement, index) => (statement, index))
            .Where(static item => item.statement is HirVariableDeclarationStatement)
            .ToDictionary(static item => item.statement.Id, static item => item.index);
        for (int statementIndex = 0; statementIndex < statements.Count; statementIndex++)
        {
            foreach (HirExpression expression in statements[statementIndex]
                         .DescendantsAndSelf()
                         .OfType<HirExpression>())
            {
                if (!boundNodes.TryGetValue(expression.Id, out BoundNodeInfo? binding) ||
                    binding.ReferencedSymbolId is not SemanticId definitionId ||
                    !declarationIndices.TryGetValue(definitionId, out int declarationIndex) ||
                    declarationIndex < statementIndex)
                {
                    continue;
                }

                HirVariableDeclarationStatement declaration =
                    (HirVariableDeclarationStatement)statements[declarationIndex];
                return $"Moving this statement would place use '{expression.Id}' before local " +
                    $"'{declaration.Name}' is declared.";
            }
        }

        return null;
    }

    private static bool HasNonFinalTerminal(IReadOnlyList<HirStatement> statements)
    {
        int terminalIndex = statements
            .Select((candidate, index) => (candidate, index))
            .Where(static item => item.candidate is
                HirReturnStatement or HirBreakStatement or HirContinueStatement)
            .Select(static item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        return terminalIndex >= 0 && terminalIndex != statements.Count - 1;
    }

    private static bool TryResolveSwitchSection(
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        SemanticId sectionId,
        SemanticId expectedSwitchId,
        out HirSwitchStatement? switchStatement,
        out HirSwitchSection? section,
        out int sectionIndex)
    {
        switchStatement = hirNodes.Values
            .OfType<HirSwitchStatement>()
            .SingleOrDefault(candidate => candidate.Id == expectedSwitchId &&
                candidate.Sections.Any(item => item.Id == sectionId));
        sectionIndex = switchStatement?.Sections
            .Select((item, index) => (item, index))
            .Where(item => item.item.Id == sectionId)
            .Select(static item => item.index)
            .SingleOrDefault() ?? -1;
        section = sectionIndex >= 0 ? switchStatement!.Sections[sectionIndex] : null;
        return section is not null;
    }

    private static bool TryGetSwitchGoverningType(
        HirSwitchStatement switchStatement,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        out TypeDescriptor? type)
    {
        type = null;
        return boundNodes.TryGetValue(
                switchStatement.GoverningExpression.Id,
                out BoundNodeInfo? binding) &&
            binding.TypeId is SemanticId typeId &&
            types.TryGetValue(typeId, out type) &&
            type.IsGraphSafe;
    }

    private static bool TryGetSwitchSectionInsertionIndex(
        IReadOnlyList<HirSwitchSection> sections,
        SemanticId? expectedPreviousSectionId,
        SemanticId? expectedNextSectionId,
        out int index)
    {
        if (expectedPreviousSectionId is null && expectedNextSectionId is null)
        {
            index = 0;
            return sections.Count == 0;
        }

        if (expectedPreviousSectionId is null)
        {
            index = 0;
            return sections.Count > 0 && sections[0].Id == expectedNextSectionId;
        }

        if (expectedNextSectionId is null)
        {
            index = sections.Count;
            return sections.Count > 0 && sections[^1].Id == expectedPreviousSectionId;
        }

        int previousIndex = sections
            .Select((section, sectionIndex) => (section, sectionIndex))
            .Where(item => item.section.Id == expectedPreviousSectionId)
            .Select(static item => item.sectionIndex)
            .DefaultIfEmpty(-1)
            .Single();
        index = previousIndex + 1;
        return previousIndex >= 0 && index < sections.Count &&
            sections[index].Id == expectedNextSectionId;
    }

    private static bool SwitchSectionSiblingsMatch(
        HirSwitchStatement switchStatement,
        int sectionIndex,
        SemanticId? expectedPreviousSectionId,
        SemanticId? expectedNextSectionId)
    {
        SemanticId? previous = sectionIndex == 0
            ? null
            : switchStatement.Sections[sectionIndex - 1].Id;
        SemanticId? next = sectionIndex == switchStatement.Sections.Count - 1
            ? null
            : switchStatement.Sections[sectionIndex + 1].Id;
        return previous == expectedPreviousSectionId && next == expectedNextSectionId;
    }

    private static bool TryPrepareSwitchMutation(
        IReadOnlyDictionary<string, ParsedDocument> documents,
        HirSwitchStatement switchStatement,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        out ParsedDocument? document,
        out SwitchStatementSyntax? originalSyntax,
        out SwitchStatementSyntax? annotatedSyntax,
        out List<PendingPin>? pins,
        out string? failure)
    {
        if (!TryFindSyntax(
                documents,
                switchStatement,
                out document,
                out originalSyntax) ||
            originalSyntax!.Sections.Count != switchStatement.Sections.Count)
        {
            annotatedSyntax = null;
            pins = null;
            failure = "The switch syntax no longer matches the analyzed HIR.";
            return false;
        }

        if (document!.Source.IdentitySidecar is null)
        {
            annotatedSyntax = null;
            pins = null;
            failure = "Switch-section authoring requires a .usids sidecar so unaffected " +
                "sections, labels, patterns, and body statements retain semantic identity.";
            return false;
        }

        IReadOnlyDictionary<SemanticId, IdentityEntry> entries = document.Source.IdentitySidecar
            .Entries.ToDictionary(static entry => entry.Id);
        SwitchStatementSyntax sourceSyntax = originalSyntax!;
        var annotations = new Dictionary<SyntaxNode, List<SyntaxAnnotation>>();
        pins = [];
        foreach (HirNode node in switchStatement.DescendantsAndSelf())
        {
            if (!entries.TryGetValue(node.Id, out IdentityEntry? entry) ||
                !TryFindIdentitySyntax(document, node, out SyntaxNode? syntax) ||
                !sourceSyntax.FullSpan.Contains(syntax!.FullSpan))
            {
                continue;
            }

            var annotation = new SyntaxAnnotation(IdentityAnnotationKind, node.Id.Value);
            if (!annotations.TryGetValue(syntax, out List<SyntaxAnnotation>? nodeAnnotations))
            {
                nodeAnnotations = [];
                annotations.Add(syntax, nodeAnnotations);
            }
            nodeAnnotations.Add(annotation);
            pins.Add(new PendingPin(node.Id, entry.Kind, annotation));
        }

        SyntaxNode[] descendants = annotations.Keys
            .Where(candidate => candidate != sourceSyntax)
            .ToArray();
        annotatedSyntax = sourceSyntax.ReplaceNodes(
            descendants,
            (original, rewritten) => rewritten.WithAdditionalAnnotations(
                annotations[original].ToArray()));
        if (annotations.TryGetValue(sourceSyntax, out List<SyntaxAnnotation>? rootAnnotations))
        {
            annotatedSyntax = annotatedSyntax.WithAdditionalAnnotations(rootAnnotations.ToArray());
        }

        failure = null;
        return true;
    }

    private static bool TryPrepareMethodMutation(
        IReadOnlyDictionary<string, ParsedDocument> documents,
        HirMethodDeclaration method,
        out ParsedDocument? document,
        out MethodDeclarationSyntax? originalSyntax,
        out MethodDeclarationSyntax? annotatedSyntax,
        out IReadOnlyDictionary<SemanticId, SyntaxAnnotation>? identityAnnotations,
        out List<PendingPin>? pins,
        out string? failure)
    {
        if (!TryFindSyntax(documents, method, out document, out originalSyntax))
        {
            annotatedSyntax = null;
            identityAnnotations = null;
            pins = null;
            failure = "The containing method syntax no longer matches the analyzed HIR.";
            return false;
        }

        if (document!.Source.IdentitySidecar is null)
        {
            annotatedSyntax = null;
            identityAnnotations = null;
            pins = null;
            failure = "Cross-region control reconnection requires a .usids sidecar so all " +
                "identities in the containing method survive the structural move.";
            return false;
        }

        IReadOnlyDictionary<SemanticId, IdentityEntry> entries = document.Source.IdentitySidecar
            .Entries.ToDictionary(static entry => entry.Id);
        MethodDeclarationSyntax sourceSyntax = originalSyntax!;
        var annotations = new Dictionary<SyntaxNode, List<SyntaxAnnotation>>();
        var annotationsById = new Dictionary<SemanticId, SyntaxAnnotation>();
        pins = [];
        foreach (HirNode node in method.DescendantsAndSelf())
        {
            if (!entries.TryGetValue(node.Id, out IdentityEntry? entry) ||
                !TryFindIdentitySyntax(document, node, out SyntaxNode? syntax) ||
                !sourceSyntax.FullSpan.Contains(syntax!.FullSpan))
            {
                continue;
            }

            var annotation = new SyntaxAnnotation(IdentityAnnotationKind, node.Id.Value);
            if (!annotations.TryGetValue(syntax, out List<SyntaxAnnotation>? nodeAnnotations))
            {
                nodeAnnotations = [];
                annotations.Add(syntax, nodeAnnotations);
            }
            nodeAnnotations.Add(annotation);
            annotationsById.Add(node.Id, annotation);
            pins.Add(new PendingPin(node.Id, entry.Kind, annotation));
        }

        SyntaxNode[] descendants = annotations.Keys
            .Where(candidate => candidate != sourceSyntax)
            .ToArray();
        annotatedSyntax = (MethodDeclarationSyntax)sourceSyntax.ReplaceNodes(
            descendants,
            (original, rewritten) => rewritten.WithAdditionalAnnotations(
                annotations[original].ToArray()));
        if (annotations.TryGetValue(sourceSyntax, out List<SyntaxAnnotation>? rootAnnotations))
        {
            annotatedSyntax = annotatedSyntax.WithAdditionalAnnotations(rootAnnotations.ToArray());
        }

        identityAnnotations = annotationsById;
        failure = null;
        return true;
    }

    private static List<PendingPin> FilterPinsForSyntax(
        SyntaxNode syntax,
        IEnumerable<PendingPin> pins) => pins
        .Where(pin => syntax.GetAnnotatedNodes(pin.Annotation).Any())
        .ToList();

    private static SwitchSectionSyntax FormatInsertedSwitchSection(
        ParsedDocument document,
        SwitchStatementSyntax switchSyntax,
        int insertionIndex,
        SwitchSectionSyntax section)
    {
        string sectionIndent;
        if (switchSyntax.Sections.Count > 0)
        {
            SwitchSectionSyntax reference = insertionIndex < switchSyntax.Sections.Count
                ? switchSyntax.Sections[insertionIndex]
                : switchSyntax.Sections[^1];
            sectionIndent = GetLineIndentation(document, reference.Labels[0]);
        }
        else
        {
            int braceColumn = switchSyntax.OpenBraceToken.GetLocation()
                .GetLineSpan().StartLinePosition.Character;
            sectionIndent = new string(' ', braceColumn + 4);
        }

        string endOfLine = GetEndOfLine(document);
        string statementIndent = sectionIndent + "    ";
        SyntaxList<SwitchLabelSyntax> labels = SyntaxFactory.List(section.Labels.Select(label =>
            label.WithLeadingTrivia(SyntaxFactory.Whitespace(sectionIndent))
                .WithColonToken(label.ColonToken.WithTrailingTrivia(
                    SyntaxFactory.EndOfLine(endOfLine)))));
        SyntaxList<StatementSyntax> statements = SyntaxFactory.List(section.Statements.Select(
            statement => statement
                .WithLeadingTrivia(SyntaxFactory.Whitespace(statementIndent))
                .WithTrailingTrivia(SyntaxFactory.EndOfLine(endOfLine))));
        return section.WithLabels(labels).WithStatements(statements);
    }

    private static SwitchSectionSyntax FormatReplacementSwitchLabels(
        ParsedDocument document,
        SwitchSectionSyntax current,
        SyntaxList<SwitchLabelSyntax> labels)
    {
        string indentation = GetLineIndentation(document, current.Labels[0]);
        string endOfLine = GetEndOfLine(document);
        SyntaxList<SwitchLabelSyntax> formatted = SyntaxFactory.List(labels.Select(label =>
            label.WithLeadingTrivia(SyntaxFactory.Whitespace(indentation))
                .WithColonToken(label.ColonToken.WithTrailingTrivia(
                    SyntaxFactory.EndOfLine(endOfLine)))));
        return current.WithLabels(formatted);
    }

    private static OperationPlan PlanValueReconnection(
        ReconnectValuePatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        HirUseDefinitionIndex useDefinitionIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, MethodDescriptor> methods,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<string, ParsedDocument> documents)
    {
        if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
        {
            return MissingTarget(operation.TargetNodeId);
        }

        if (target is not HirExpression expression ||
            !patchIndex.TryGetExpression(
                expression.Id,
                out HirPatchIndex.ExpressionLocation? expressionLocation))
        {
            return KindMismatch(target, operation.TargetNodeId, "an expression");
        }

        if (!hirNodes.TryGetValue(operation.SourceDefinitionId, out HirNode? sourceNode) ||
            sourceNode is not HirVariableDeclarationStatement sourceDefinition ||
            !patchIndex.TryGetStatement(
                operation.SourceDefinitionId,
                out HirPatchIndex.StatementLocation? sourceLocation))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.ValueSourceNotAvailable,
                $"Value source '{operation.SourceDefinitionId}' is not a local definition in the current HIR.",
                expression);
        }

        if (expressionLocation!.ContainingStatement.Id != operation.ExpectedContainingStatementId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Value reconnection expected containing statement " +
                $"'{operation.ExpectedContainingStatementId}', but expression '{expression.Id}' is currently " +
                $"inside '{expressionLocation.ContainingStatement.Id}'.",
                expression);
        }

        if (!patchIndex.TryGetStatement(
                expressionLocation.ContainingStatement.Id,
                out HirPatchIndex.StatementLocation? consumerLocation) ||
            consumerLocation!.ParentBlock.Id != operation.ExpectedParentBlockId ||
            !StatementSiblingsMatch(
                consumerLocation,
                operation.ExpectedPreviousSiblingId,
                operation.ExpectedNextSiblingId))
        {
            return SiblingFailure(
                expressionLocation.ContainingStatement,
                operation.ExpectedPreviousSiblingId,
                operation.ExpectedNextSiblingId,
                operation.ExpectedParentBlockId);
        }

        if (sourceLocation!.Method.Id != expressionLocation.Method.Id ||
            !patchIndex.DoesStatementDominate(
                sourceDefinition.Id,
                expressionLocation.ContainingStatement.Id) ||
            !graphSafetyIndex.DoesDefinitionDominateStatement(
                sourceDefinition.Id,
                consumerLocation!))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.ValueSourceNotAvailable,
                $"Local '{sourceDefinition.Name}' does not lexically and CFG-dominate reachable " +
                $"expression '{expression.Id}' in the same method.",
                expression);
        }

        if (expressionLocation.ContainingStatement
            .DescendantsAndSelf()
            .OfType<HirAssignmentExpression>()
            .Any(assignment => assignment.Target
                .DescendantsAndSelf()
                .Any(node => node.Id == expression.Id)))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.UnsafeValueReconnection,
                "Value reconnection cannot retarget the writable side of an assignment.",
                expression);
        }

        if (HasNameConflict(
                expressionLocation.Method,
                sourceDefinition.Name,
                sourceDefinition.Id))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.ValueSourceNotAvailable,
                $"Local '{sourceDefinition.Name}' is shadowed by another parameter or local in method " +
                $"'{expressionLocation.Method.Name}'.",
                expression);
        }

        if (useDefinitionIndex.References(expression, sourceDefinition.Id))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Expression '{expression.Id}' already references local '{sourceDefinition.Name}'.",
                expression);
        }

        if (operation.ExpectedTargetTypeId != operation.ExpectedSourceTypeId ||
            !types.TryGetValue(operation.ExpectedSourceTypeId, out TypeDescriptor? sourceType) ||
            !sourceType.IsGraphSafe)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                "Value reconnection requires the same graph-safe semantic type on source and target.",
                expression);
        }

        OperationPlan? targetTypeFailure = RequireType(
            expression,
            operation.ExpectedTargetTypeId,
            "consumer expression",
            boundNodes);
        if (targetTypeFailure is not null)
        {
            return targetTypeFailure;
        }

        OperationPlan? sourceTypeFailure = RequireType(
            sourceDefinition,
            operation.ExpectedSourceTypeId,
            "source local",
            boundNodes);
        if (sourceTypeFailure is not null)
        {
            return sourceTypeFailure;
        }

        if (!IsStablePureExpression(
                expression,
                useDefinitionIndex,
                boundNodes,
                methods,
                types))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.UnsafeValueReconnection,
                "The consumer expression cannot be removed because it may read state, mutate, throw, " +
                "allocate, or change conditional evaluation.",
                expression);
        }

        HirBlockStatement parentBlock = consumerLocation.ParentBlock;
        if (!TryFindSyntax(
                documents,
                parentBlock,
                out ParsedDocument? document,
                out BlockSyntax? blockSyntax) ||
            blockSyntax!.Statements.Count != parentBlock.Statements.Count)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                "Value reconnection currently requires an explicit C# block whose statement list " +
                "matches the analyzed HIR block.",
                parentBlock);
        }

        StatementSyntax containingSyntax = blockSyntax.Statements[consumerLocation.Index];
        if (RoslynSourceSpan.FromNode(
                containingSyntax,
                document!.Source.LogicalPath) != expressionLocation.ContainingStatement.Span)
        {
            return MissingSyntax(expressionLocation.ContainingStatement, "statement");
        }

        HashSet<SemanticId> removedIds = expression
            .DescendantsAndSelf()
            .Select(static node => node.Id)
            .ToHashSet();
        (List<StatementSyntax> annotatedStatements, List<PendingPin> pins) =
            AnnotateStatements(blockSyntax, parentBlock.Statements);
        if (!TryAnnotateExpressions(
                document!,
                containingSyntax,
                expressionLocation.ContainingStatement,
                removedIds,
                out StatementSyntax? annotatedContaining,
                out IReadOnlyDictionary<SemanticId, SyntaxAnnotation>? expressionAnnotations,
                out IReadOnlyList<PendingPin>? expressionPins) ||
            !expressionAnnotations!.TryGetValue(expression.Id, out SyntaxAnnotation? targetAnnotation))
        {
            return MissingSyntax(expression, "expression");
        }

        SyntaxAnnotation statementAnnotation = pins
            .Single(pin => pin.Id == expressionLocation.ContainingStatement.Id)
            .Annotation;
        annotatedContaining = annotatedContaining!.WithAdditionalAnnotations(statementAnnotation);
        ExpressionSyntax annotatedTarget = annotatedContaining
            .GetAnnotatedNodes(targetAnnotation)
            .OfType<ExpressionSyntax>()
            .Single();
        if (annotatedTarget
            .DescendantTrivia(descendIntoTrivia: true)
            .Any(IsMeaningfulTrivia))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.UnsafeValueReconnection,
                "The consumer expression contains comments or directives that cannot be discarded safely.",
                expression);
        }

        IdentifierNameSyntax replacementIdentifier = SyntaxFactory.IdentifierName(sourceDefinition.Name)
            .WithTriviaFrom(annotatedTarget);
        StatementSyntax updatedContaining = annotatedContaining.ReplaceNode(
            annotatedTarget,
            replacementIdentifier);
        annotatedStatements[consumerLocation.Index] = updatedContaining;
        pins.AddRange(expressionPins!);

        var blockAnnotation = new SyntaxAnnotation(IdentityAnnotationKind, parentBlock.Id.Value);
        BlockSyntax replacement = blockSyntax
            .WithStatements(SyntaxFactory.List(annotatedStatements))
            .WithAdditionalAnnotations(blockAnnotation);
        pins.Add(new PendingPin(parentBlock.Id, "block", blockAnnotation));
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            blockSyntax,
            replacement,
            pins));
    }

    private static SyntaxNode RemoveStatementPreservingComments(
        ParsedDocument document,
        EditableStatementContainer container,
        int statementIndex)
    {
        StatementSyntax target = container.Statements[statementIndex];
        SyntaxTriviaList preservedTrivia = GetPreservedDeletionTrivia(document, target);
        SyntaxList<StatementSyntax> statements = container.Statements.RemoveAt(statementIndex);
        if (preservedTrivia.Count == 0)
        {
            return container.WithStatements(statements);
        }

        if (statementIndex < statements.Count)
        {
            StatementSyntax next = statements[statementIndex];
            statements = statements.Replace(
                next,
                next.WithLeadingTrivia(preservedTrivia.AddRange(next.GetLeadingTrivia())));
            return container.WithStatements(statements);
        }

        if (container.Syntax is BlockSyntax block)
        {
            return block
                .WithStatements(statements)
                .WithCloseBraceToken(block.CloseBraceToken.WithLeadingTrivia(
                    preservedTrivia.AddRange(block.CloseBraceToken.LeadingTrivia)));
        }

        SwitchSectionSyntax section = (SwitchSectionSyntax)container.Syntax;
        SwitchLabelSyntax firstLabel = section.Labels[0];
        return section
            .WithStatements(statements)
            .WithLabels(section.Labels.Replace(
                firstLabel,
                firstLabel.WithLeadingTrivia(
                    preservedTrivia.AddRange(firstLabel.GetLeadingTrivia()))));
    }

    private static SyntaxList<StatementSyntax> GetStatements(SyntaxNode container) => container switch
    {
        BlockSyntax block => block.Statements,
        SwitchSectionSyntax section => section.Statements,
        _ => throw new InvalidOperationException(
            $"Unsupported editable statement container '{container.GetType().Name}'."),
    };

    private static SyntaxNode WithStatements(
        SyntaxNode container,
        SyntaxList<StatementSyntax> statements) => container switch
        {
            BlockSyntax block => block.WithStatements(statements),
            SwitchSectionSyntax section => section.WithStatements(statements),
            _ => throw new InvalidOperationException(
                $"Unsupported editable statement container '{container.GetType().Name}'."),
        };

    private static SyntaxTriviaList GetPreservedDeletionTrivia(
        ParsedDocument document,
        StatementSyntax statement)
    {
        var preserved = new List<SyntaxTrivia>();
        SyntaxTriviaList leading = statement.GetLeadingTrivia();
        if (leading.Any(IsMeaningfulTrivia))
        {
            preserved.AddRange(leading);
            while (preserved.Count > 0 &&
                preserved[^1].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                preserved.RemoveAt(preserved.Count - 1);
            }

            EnsureEndsWithNewLine(preserved, document);
        }

        SyntaxTrivia[] trailing = statement.GetTrailingTrivia()
            .SkipWhile(static trivia =>
                trivia.IsKind(SyntaxKind.WhitespaceTrivia) ||
                trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            .ToArray();
        if (trailing.Any(IsMeaningfulTrivia))
        {
            preserved.Add(SyntaxFactory.Whitespace(GetLineIndentation(document, statement)));
            preserved.AddRange(trailing);
            EnsureEndsWithNewLine(preserved, document);
        }

        return SyntaxFactory.TriviaList(preserved);
    }

    private static bool IsMeaningfulTrivia(SyntaxTrivia trivia) =>
        !trivia.IsKind(SyntaxKind.WhitespaceTrivia) &&
        !trivia.IsKind(SyntaxKind.EndOfLineTrivia);

    private static void EnsureEndsWithNewLine(
        List<SyntaxTrivia> trivia,
        ParsedDocument document)
    {
        if (trivia.Count == 0 || !trivia[^1].IsKind(SyntaxKind.EndOfLineTrivia))
        {
            trivia.Add(SyntaxFactory.EndOfLine(GetEndOfLine(document)));
        }
    }

    private static OperationPlan? ValidateDeletionSafety(
        HirPatchIndex.StatementLocation location,
        HirUseDefinitionIndex useDefinitionIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, MethodDescriptor> methods)
    {
        switch (location.Statement)
        {
            case HirVariableDeclarationStatement variable:
                if (useDefinitionIndex.HasUses(variable.Id))
                {
                    return DiagnosticPlan(
                        SemanticPatchDiagnosticCodes.SymbolStillReferenced,
                        $"Local '{variable.Name}' cannot be deleted while another HIR node references it.",
                        variable);
                }

                if (variable.Initializer is not null and not HirLiteralExpression)
                {
                    return DiagnosticPlan(
                        SemanticPatchDiagnosticCodes.UnsafeDeletion,
                        "This slice deletes a local declaration only when it has no initializer or one " +
                        "literal initializer; broader expressions may read, throw, or have effects.",
                        variable);
                }

                return null;
            case HirExpressionStatement { Expression: HirInvocationExpression invocation } expression:
                if (!boundNodes.TryGetValue(invocation.Id, out BoundNodeInfo? invocationBinding) ||
                    invocationBinding.InvokedMethodId is not SemanticId methodId ||
                    !methods.TryGetValue(methodId, out MethodDescriptor? method) ||
                    !method.IsStatic ||
                    method.Effects != MethodEffects.None ||
                    invocation.Arguments.Any(argument =>
                        argument is not (
                            HirLiteralExpression or HirIdentifierExpression or HirThisExpression) ||
                        HasEffects(argument, boundNodes)))
                {
                    return DiagnosticPlan(
                        SemanticPatchDiagnosticCodes.UnsafeDeletion,
                        "An expression statement can be deleted only when it is a resolved pure static call " +
                        "with literal, identifier, or this arguments.",
                        expression);
                }

                return null;
            case HirExpressionStatement expression:
                return DiagnosticPlan(
                    SemanticPatchDiagnosticCodes.UnsafeDeletion,
                    "Assignment, await, unary mutation, and unresolved expression statements cannot be deleted.",
                    expression);
            default:
                return DiagnosticPlan(
                    SemanticPatchDiagnosticCodes.InvalidStatement,
                    $"Deleting '{location.Statement.GetType().Name}' is outside this patch slice.",
                    location.Statement);
        }
    }

    private static (List<StatementSyntax> Statements, List<PendingPin> Pins) AnnotateStatements(
        BlockSyntax block,
        IReadOnlyList<HirStatement> hirStatements) => AnnotateStatements(
            block.Statements,
            hirStatements);

    private static (List<StatementSyntax> Statements, List<PendingPin> Pins) AnnotateStatements(
        SyntaxList<StatementSyntax> syntaxStatements,
        IReadOnlyList<HirStatement> hirStatements)
    {
        if (syntaxStatements.Count != hirStatements.Count)
        {
            throw new ArgumentException("Roslyn and HIR statement counts must match.", nameof(hirStatements));
        }

        var statements = new List<StatementSyntax>(hirStatements.Count);
        var pins = new List<PendingPin>(hirStatements.Count + 1);
        for (int index = 0; index < hirStatements.Count; index++)
        {
            HirStatement hirStatement = hirStatements[index];
            var annotation = new SyntaxAnnotation(IdentityAnnotationKind, hirStatement.Id.Value);
            statements.Add(syntaxStatements[index].WithAdditionalAnnotations(annotation));
            pins.Add(new PendingPin(
                hirStatement.Id,
                GetIdentityKind(hirStatement),
                annotation));
        }

        return (statements, pins);
    }

    private static (List<StatementSyntax> Statements, List<PendingPin> Pins) AnnotateStatementTrees(
        ParsedDocument document,
        BlockSyntax block,
        IReadOnlyList<HirStatement> hirStatements) => AnnotateStatementTrees(
            document,
            block.Statements,
            hirStatements);

    private static (List<StatementSyntax> Statements, List<PendingPin> Pins) AnnotateStatementTrees(
        ParsedDocument document,
        SyntaxList<StatementSyntax> syntaxStatements,
        IReadOnlyList<HirStatement> hirStatements)
    {
        if (syntaxStatements.Count != hirStatements.Count)
        {
            throw new ArgumentException("Roslyn and HIR statement counts must match.", nameof(hirStatements));
        }

        var statements = new List<StatementSyntax>(hirStatements.Count);
        var pins = new List<PendingPin>();
        for (int index = 0; index < hirStatements.Count; index++)
        {
            HirStatement hirStatement = hirStatements[index];
            if (!TryAnnotateExpressions(
                    document,
                    syntaxStatements[index],
                    hirStatement,
                    null,
                    out StatementSyntax? annotated,
                    out _,
                    out IReadOnlyList<PendingPin>? expressionPins))
            {
                throw new InvalidOperationException(
                    $"Could not pin expressions in existing statement '{hirStatement.Id}'.");
            }

            var statementAnnotation = new SyntaxAnnotation(IdentityAnnotationKind, hirStatement.Id.Value);
            statements.Add(annotated!.WithAdditionalAnnotations(statementAnnotation));
            pins.Add(new PendingPin(
                hirStatement.Id,
                GetIdentityKind(hirStatement),
                statementAnnotation));
            pins.AddRange(expressionPins!);
        }

        return (statements, pins);
    }

    private static bool TryAnnotateExpressions(
        ParsedDocument document,
        StatementSyntax syntax,
        HirStatement statement,
        IReadOnlySet<SemanticId>? unpinnedExpressionIds,
        out StatementSyntax? annotated,
        out IReadOnlyDictionary<SemanticId, SyntaxAnnotation>? annotations,
        out IReadOnlyList<PendingPin>? pins)
    {
        ExpressionSyntax[] syntaxExpressions = syntax
            .DescendantNodes()
            .OfType<ExpressionSyntax>()
            .ToArray();
        var annotationBySyntax = new Dictionary<ExpressionSyntax, SyntaxAnnotation>();
        var annotationById = new Dictionary<SemanticId, SyntaxAnnotation>();
        var pendingPins = new List<PendingPin>();
        foreach (HirExpression expression in statement
                     .DescendantsAndSelf()
                     .OfType<HirExpression>())
        {
            ExpressionSyntax[] matches = syntaxExpressions
                .Where(candidate => RoslynSourceSpan.FromNode(
                    candidate,
                    document.Source.LogicalPath) == expression.Span)
                .ToArray();
            if (matches.Length != 1)
            {
                annotated = null;
                annotations = null;
                pins = null;
                return false;
            }

            bool shouldPin = unpinnedExpressionIds?.Contains(expression.Id) != true;
            var annotation = new SyntaxAnnotation(
                shouldPin ? IdentityAnnotationKind : PatchLocatorAnnotationKind,
                expression.Id.Value);
            annotationBySyntax.Add(matches[0], annotation);
            annotationById.Add(expression.Id, annotation);
            if (shouldPin)
            {
                pendingPins.Add(new PendingPin(
                    expression.Id,
                    GetIdentityKind(expression),
                    annotation));
            }
        }

        annotated = syntax.ReplaceNodes(
            annotationBySyntax.Keys,
            (original, rewritten) => rewritten.WithAdditionalAnnotations(annotationBySyntax[original]));
        annotations = annotationById;
        pins = pendingPins;
        return true;
    }

    private static bool HasNameConflict(
        HirMethodDeclaration method,
        string name,
        SemanticId? exceptDefinitionId = null) =>
        method.Parameters.Any(parameter =>
            parameter.Name == name && parameter.Id != exceptDefinitionId) ||
        method.DescendantsAndSelf()
            .OfType<HirVariableDeclarationStatement>()
            .Any(variable => variable.Name == name && variable.Id != exceptDefinitionId) ||
        method.DescendantsAndSelf()
            .OfType<HirForVariableDeclarator>()
            .Any(variable => variable.Name == name && variable.Id != exceptDefinitionId) ||
        method.DescendantsAndSelf()
            .OfType<HirForEachVariable>()
            .Any(variable => variable.Name == name && variable.Id != exceptDefinitionId);

    private static bool IsStablePureExpression(
        HirExpression expression,
        HirUseDefinitionIndex useDefinitionIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, MethodDescriptor> methods,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types) => expression switch
        {
            HirLiteralExpression => true,
            HirIdentifierExpression identifier => useDefinitionIndex.IsValueReference(identifier),
            HirBinaryExpression binary =>
                StableTemporaryBinaryOperators.Contains(binary.Operator) &&
                HasStableOperatorType(binary.Left, boundNodes, types) &&
                HasStableOperatorType(binary.Right, boundNodes, types) &&
                IsStablePureExpression(binary.Left, useDefinitionIndex, boundNodes, methods, types) &&
                IsStablePureExpression(binary.Right, useDefinitionIndex, boundNodes, methods, types),
            HirUnaryExpression { Operator: not ("++" or "--") } unary =>
                HasStableOperatorType(unary.Operand, boundNodes, types) &&
                IsStablePureExpression(unary.Operand, useDefinitionIndex, boundNodes, methods, types),
            HirCastExpression cast =>
                HasStableOperatorType(cast, boundNodes, types) &&
                HasStableOperatorType(cast.Operand, boundNodes, types) &&
                IsStablePureExpression(cast.Operand, useDefinitionIndex, boundNodes, methods, types),
            HirConditionalExpression conditional =>
                HasStableOperatorType(conditional.Condition, boundNodes, types) &&
                HasStableOperatorType(conditional.WhenTrue, boundNodes, types) &&
                HasStableOperatorType(conditional.WhenFalse, boundNodes, types) &&
                IsStablePureExpression(
                    conditional.Condition,
                    useDefinitionIndex,
                    boundNodes,
                    methods,
                    types) &&
                IsStablePureExpression(
                    conditional.WhenTrue,
                    useDefinitionIndex,
                    boundNodes,
                    methods,
                    types) &&
                IsStablePureExpression(
                    conditional.WhenFalse,
                    useDefinitionIndex,
                    boundNodes,
                    methods,
                    types),
            HirConditionalAccessExpression conditionalAccess =>
                IsStablePureExpression(
                    conditionalAccess.Receiver,
                    useDefinitionIndex,
                    boundNodes,
                    methods,
                    types) &&
                IsStablePureExpression(
                    conditionalAccess.WhenNotNull,
                    useDefinitionIndex,
                    boundNodes,
                    methods,
                    types),
            HirConditionalMemberBindingExpression => true,
            HirInvocationExpression invocation =>
                boundNodes.TryGetValue(invocation.Id, out BoundNodeInfo? invocationBinding) &&
                invocationBinding.InvokedMethodId is SemanticId methodId &&
                methods.TryGetValue(methodId, out MethodDescriptor? method) &&
                method.IsStatic &&
                method.Effects == MethodEffects.None &&
                invocationBinding.TypeId is SemanticId invocationTypeId &&
                types.TryGetValue(invocationTypeId, out TypeDescriptor? invocationType) &&
                invocationType.IsGraphSafe &&
                invocation.Arguments.All(argument =>
                    IsStablePureExpression(argument, useDefinitionIndex, boundNodes, methods, types)),
            _ => false,
        };

    private static bool HasStableOperatorType(
        HirExpression expression,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types) =>
        boundNodes.TryGetValue(expression.Id, out BoundNodeInfo? binding) &&
        binding.TypeId is SemanticId typeId &&
        types.TryGetValue(typeId, out TypeDescriptor? type) &&
        (type.Kind is UnifiedTypeKind.Boolean or
            UnifiedTypeKind.Integer or
            UnifiedTypeKind.FloatingPoint or
            UnifiedTypeKind.Enum) &&
        !string.Equals(type.MetadataName, "System.Decimal", StringComparison.Ordinal);

    private static bool TryGetInsertionIndex(
        HirBlockStatement block,
        SemanticId? expectedPreviousSiblingId,
        SemanticId? expectedNextSiblingId,
        out int index) => TryGetInsertionIndex(
            block.Statements,
            expectedPreviousSiblingId,
            expectedNextSiblingId,
            out index);

    private static bool TryGetInsertionIndex(
        IReadOnlyList<HirStatement> statements,
        SemanticId? expectedPreviousSiblingId,
        SemanticId? expectedNextSiblingId,
        out int index)
    {
        if (expectedPreviousSiblingId is null && expectedNextSiblingId is null)
        {
            index = 0;
            return statements.Count == 0;
        }

        if (expectedPreviousSiblingId is null)
        {
            index = 0;
            return statements.Count > 0 && statements[0].Id == expectedNextSiblingId;
        }

        if (expectedNextSiblingId is null)
        {
            index = statements.Count;
            return statements.Count > 0 && statements[^1].Id == expectedPreviousSiblingId;
        }

        int previousIndex = statements
            .Select((statement, statementIndex) => (statement, statementIndex))
            .Where(item => item.statement.Id == expectedPreviousSiblingId)
            .Select(static item => item.statementIndex)
            .DefaultIfEmpty(-1)
            .Single();
        index = previousIndex + 1;
        return previousIndex >= 0 &&
            index < statements.Count &&
            statements[index].Id == expectedNextSiblingId;
    }

    private static bool StatementSiblingsMatch(
        HirPatchIndex.StatementLocation location,
        SemanticId? expectedPreviousSiblingId,
        SemanticId? expectedNextSiblingId)
    {
        SemanticId? currentPrevious = location.Index == 0
            ? null
            : location.ParentBlock.Statements[location.Index - 1].Id;
        SemanticId? currentNext = location.Index == location.ParentBlock.Statements.Count - 1
            ? null
            : location.ParentBlock.Statements[location.Index + 1].Id;
        return currentPrevious == expectedPreviousSiblingId && currentNext == expectedNextSiblingId;
    }

    private static StatementBuildResult BuildLocalLiteralStatement(
        LocalLiteralDeclarationPatchStatement local,
        HirMethodDeclaration method,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types)
    {
        if (!types.TryGetValue(local.ExpectedTypeId, out TypeDescriptor? expectedType) ||
            !expectedType.IsGraphSafe ||
            !TypeNameMatches(local.TypeName, expectedType))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"Inserted local type '{local.TypeName}' does not match graph-safe semantic type " +
                $"'{local.ExpectedTypeId}'. The type must already exist in the bound compilation.");
        }

        if (HasNameConflict(method, local.Name))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.NameConflict,
                $"A parameter or local named '{local.Name}' already exists in method '{method.Name}'.");
        }

        if (!TryCreateLocalLiteralStatement(local, out LocalDeclarationStatementSyntax? statement))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                "The local-literal payload could not be represented as one C# local declaration.");
        }

        return StatementBuildResult.Success(statement!);
    }

    private static StatementBuildResult BuildLocalStatement(
        LocalDeclarationPatchStatement local,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types)
    {
        if (!types.TryGetValue(local.ExpectedTypeId, out TypeDescriptor? expectedType) ||
            !expectedType.IsGraphSafe ||
            !TypeNameMatches(local.TypeName, expectedType))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"Inserted local type '{local.TypeName}' does not match graph-safe semantic type " +
                $"'{local.ExpectedTypeId}'.");
        }

        if (HasNameConflict(blockLocation.Method, local.Name))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.NameConflict,
                $"A parameter or local named '{local.Name}' already exists in method " +
                $"'{blockLocation.Method.Name}'.");
        }

        ExpressionBuildResult initializer = BuildPatchExpression(
            local.Initializer,
            blockLocation,
            insertionIndex,
            hirNodes,
            patchIndex,
            graphSafetyIndex,
            boundNodes,
            types);
        if (initializer.Syntax is null || initializer.Type is null ||
            !IsSameType(initializer.Type, expectedType))
        {
            return StatementBuildResult.Failure(
                initializer.DiagnosticCode ?? SemanticPatchDiagnosticCodes.TypeMismatch,
                initializer.DiagnosticMessage ??
                    "The local initializer does not match its declared semantic type.");
        }

        TypeSyntax typeSyntax = SyntaxFactory.ParseTypeName(
            local.TypeName,
            options: CSharpToHirLowerer.ParseOptions,
            consumeFullText: true);
        if (typeSyntax.ContainsDiagnostics || typeSyntax.Span.Length != local.TypeName.Length ||
            !SyntaxFacts.IsValidIdentifier(local.Name) ||
            SyntaxFacts.GetKeywordKind(local.Name) != SyntaxKind.None)
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                "The local name or explicit type cannot be represented as one C# declaration.");
        }

        LocalDeclarationStatementSyntax statement = SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(typeSyntax.WithoutTrivia())
                    .AddVariables(SyntaxFactory.VariableDeclarator(local.Name)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(initializer.Syntax))))
            .NormalizeWhitespace()
            .WithoutLeadingTrivia()
            .WithoutTrailingTrivia();
        return StatementBuildResult.Success(statement);
    }

    private static StatementBuildResult BuildLocalAssignmentStatement(
        LocalAssignmentPatchStatement assignment,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types)
    {
        if (!hirNodes.TryGetValue(
                assignment.TargetDefinitionId,
                out HirNode? targetNode) ||
            targetNode is not HirVariableDeclarationStatement target ||
            !patchIndex.TryGetStatement(
                target.Id,
                out HirPatchIndex.StatementLocation? targetLocation) ||
            targetLocation!.Method.Id != blockLocation.Method.Id)
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.AssignmentTargetNotAvailable,
                $"Assignment target '{assignment.TargetDefinitionId}' is not a local in the target method.");
        }

        if (!string.Equals(target.Name, assignment.ExpectedName, StringComparison.Ordinal) ||
            HasNameConflict(blockLocation.Method, target.Name, target.Id))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.AssignmentTargetNotAvailable,
                $"Assignment target '{assignment.TargetDefinitionId}' is no longer the unshadowed local " +
                $"named '{assignment.ExpectedName}'.");
        }

        if (!patchIndex.DoesStatementDominateInsertion(
                target.Id,
                blockLocation.Block.Id,
                insertionIndex) ||
            !graphSafetyIndex.DoesDefinitionDominateInsertion(
                target.Id,
                blockLocation,
                insertionIndex))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.AssignmentTargetNotAvailable,
                $"Local '{target.Name}' is not in scope on every reachable CFG path to the insertion gap.");
        }

        if (!types.TryGetValue(assignment.ExpectedTypeId, out TypeDescriptor? targetType) ||
            !targetType.IsGraphSafe ||
            !boundNodes.TryGetValue(target.Id, out BoundNodeInfo? targetBinding) ||
            targetBinding.TypeId != assignment.ExpectedTypeId)
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"Local '{target.Name}' no longer has graph-safe semantic type " +
                $"'{assignment.ExpectedTypeId}'.");
        }

        ExpressionBuildResult value = BuildPatchExpression(
            assignment.Value,
            blockLocation,
            insertionIndex,
            hirNodes,
            patchIndex,
            graphSafetyIndex,
            boundNodes,
            types);
        if (value.Syntax is null || value.Type?.Id != assignment.ExpectedTypeId)
        {
            return StatementBuildResult.Failure(
                value.DiagnosticCode ?? SemanticPatchDiagnosticCodes.TypeMismatch,
                value.DiagnosticMessage ?? "The local assignment value is invalid.");
        }

        AssignmentExpressionSyntax assignmentSyntax = SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.IdentifierName(target.Name).WithTrailingTrivia(SyntaxFactory.Space),
            value.Syntax)
            .WithOperatorToken(SyntaxFactory.Token(SyntaxKind.EqualsToken)
                .WithTrailingTrivia(SyntaxFactory.Space));
        StatementSyntax statement = SyntaxFactory.ExpressionStatement(assignmentSyntax)
            .NormalizeWhitespace()
            .WithoutLeadingTrivia()
            .WithoutTrailingTrivia();
        return StatementBuildResult.Success(statement);
    }

    private static StatementBuildResult BuildIfStatement(
        IfPatchStatement ifStatement,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types)
    {
        ExpressionBuildResult condition = BuildPatchExpression(
            ifStatement.Condition,
            blockLocation,
            insertionIndex,
            hirNodes,
            patchIndex,
            graphSafetyIndex,
            boundNodes,
            types);
        if (condition.Syntax is null || condition.Type is null)
        {
            return StatementBuildResult.Failure(
                condition.DiagnosticCode ?? SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                condition.DiagnosticMessage ?? "The graph-authored if condition is invalid.");
        }

        if (condition.Type.Kind != UnifiedTypeKind.Boolean ||
            !string.Equals(condition.Type.MetadataName, "System.Boolean", StringComparison.Ordinal))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"An if condition must have semantic type 'System.Boolean', not " +
                $"'{condition.Type.MetadataName}' ('{ifStatement.Condition.ExpectedTypeId}').");
        }

        IfStatementSyntax syntax = SyntaxFactory
            .IfStatement(condition.Syntax, SyntaxFactory.Block())
            .WithElse(SyntaxFactory.ElseClause(SyntaxFactory.Block()));
        return StatementBuildResult.Success(syntax);
    }

    private static StatementBuildResult BuildWhileStatement(
        WhilePatchStatement whileStatement,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types)
    {
        if (!graphSafetyIndex.IsInsertionReachable(blockLocation, insertionIndex))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.UnreachableInsertion,
                "A graph-authored while requires a CFG-reachable insertion gap.");
        }

        ExpressionBuildResult condition = BuildPatchExpression(
            whileStatement.Condition,
            blockLocation,
            insertionIndex,
            hirNodes,
            patchIndex,
            graphSafetyIndex,
            boundNodes,
            types);
        if (condition.Syntax is null || condition.Type is null)
        {
            return StatementBuildResult.Failure(
                condition.DiagnosticCode ?? SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                condition.DiagnosticMessage ?? "The graph-authored while condition is invalid.");
        }

        if (condition.Type.Kind != UnifiedTypeKind.Boolean ||
            !string.Equals(condition.Type.MetadataName, "System.Boolean", StringComparison.Ordinal))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"A while condition must have semantic type 'System.Boolean', not " +
                $"'{condition.Type.MetadataName}' ('{whileStatement.Condition.ExpectedTypeId}').");
        }

        WhileStatementSyntax syntax = SyntaxFactory.WhileStatement(
            condition.Syntax,
            SyntaxFactory.Block());
        return StatementBuildResult.Success(syntax);
    }

    private static StatementBuildResult BuildForStatement(
        ForPatchStatement forStatement,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types)
    {
        if (!graphSafetyIndex.IsInsertionReachable(blockLocation, insertionIndex))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.UnreachableInsertion,
                "A graph-authored for requires a CFG-reachable insertion gap.");
        }

        var authoredLocals = new Dictionary<int, AuthoredForLocal>();
        VariableDeclarationSyntax? declarationSyntax = null;
        if (forStatement.Declaration is not null)
        {
            if (forStatement.Declaration.Variables.Count != 1)
            {
                return StatementBuildResult.Failure(
                    SemanticPatchDiagnosticCodes.InvalidStatement,
                    "The bounded for constructor requires exactly one declaration variable.");
            }

            ForVariableDeclaratorPatch variable = forStatement.Declaration.Variables[0];
            if (!types.TryGetValue(variable.ExpectedTypeId, out TypeDescriptor? variableType) ||
                !IsStableGraphExpressionType(variableType) ||
                !TypeNameMatches(forStatement.Declaration.TypeName, variableType))
            {
                return StatementBuildResult.Failure(
                    SemanticPatchDiagnosticCodes.TypeMismatch,
                    $"For variable type '{forStatement.Declaration.TypeName}' does not match " +
                    $"graph-safe semantic type '{variable.ExpectedTypeId}'.");
            }

            if (!SyntaxFacts.IsValidIdentifier(variable.Name) ||
                SyntaxFacts.GetKeywordKind(variable.Name) != SyntaxKind.None)
            {
                return StatementBuildResult.Failure(
                    SemanticPatchDiagnosticCodes.InvalidStatement,
                    $"For variable name '{variable.Name}' is not one unescaped C# identifier.");
            }

            if (HasNameConflict(blockLocation.Method, variable.Name))
            {
                return StatementBuildResult.Failure(
                    SemanticPatchDiagnosticCodes.NameConflict,
                    $"A parameter or local named '{variable.Name}' already exists in method " +
                    $"'{blockLocation.Method.Name}'.");
            }

            ExpressionBuildResult initializer = BuildPatchExpression(
                variable.Initializer,
                blockLocation,
                insertionIndex,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                types,
                authoredLocals);
            if (initializer.Syntax is null || initializer.Type?.Id != variable.ExpectedTypeId)
            {
                return StatementBuildResult.Failure(
                    initializer.DiagnosticCode ?? SemanticPatchDiagnosticCodes.TypeMismatch,
                    initializer.DiagnosticMessage ?? "The for declaration initializer is invalid.");
            }

            TypeSyntax typeSyntax = SyntaxFactory.ParseTypeName(
                forStatement.Declaration.TypeName,
                options: CSharpToHirLowerer.ParseOptions,
                consumeFullText: true);
            if (typeSyntax.ContainsDiagnostics ||
                typeSyntax.SpanStart != 0 ||
                typeSyntax.Span.Length != forStatement.Declaration.TypeName.Length)
            {
                return StatementBuildResult.Failure(
                    SemanticPatchDiagnosticCodes.InvalidStatement,
                    $"For variable type '{forStatement.Declaration.TypeName}' is not one C# type name.");
            }

            VariableDeclaratorSyntax variableSyntax = SyntaxFactory.VariableDeclarator(
                    SyntaxFactory.Identifier(variable.Name))
                .WithInitializer(SyntaxFactory.EqualsValueClause(initializer.Syntax));
            declarationSyntax = SyntaxFactory.VariableDeclaration(
                typeSyntax.WithoutTrivia(),
                SyntaxFactory.SingletonSeparatedList(variableSyntax));
            authoredLocals.Add(0, new AuthoredForLocal(variable.Name, variableType));
        }

        var initializerSyntaxes = new List<ExpressionSyntax>(forStatement.Initializers.Count);
        foreach (ForAssignmentPatchExpression initializer in forStatement.Initializers)
        {
            ExpressionBuildResult builtInitializer = BuildForAssignmentExpression(
                initializer,
                blockLocation,
                insertionIndex,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                types);
            if (builtInitializer.Syntax is null)
            {
                return StatementBuildResult.Failure(
                    builtInitializer.DiagnosticCode ?? SemanticPatchDiagnosticCodes.InvalidStatement,
                    builtInitializer.DiagnosticMessage ?? "A for assignment initializer is invalid.");
            }

            initializerSyntaxes.Add(builtInitializer.Syntax);
        }

        ExpressionSyntax? conditionSyntax = null;
        if (forStatement.Condition is not null)
        {
            ExpressionBuildResult condition = BuildPatchExpression(
                forStatement.Condition,
                blockLocation,
                insertionIndex,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                types,
                authoredLocals);
            if (condition.Syntax is null || condition.Type is null)
            {
                return StatementBuildResult.Failure(
                    condition.DiagnosticCode ?? SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                    condition.DiagnosticMessage ?? "The graph-authored for condition is invalid.");
            }

            if (!IsBoolean(condition.Type))
            {
                return StatementBuildResult.Failure(
                    SemanticPatchDiagnosticCodes.TypeMismatch,
                    $"A for condition must have semantic type 'System.Boolean', not " +
                    $"'{condition.Type.MetadataName}' ('{forStatement.Condition.ExpectedTypeId}').");
            }

            conditionSyntax = condition.Syntax;
        }

        var incrementorSyntaxes = new List<ExpressionSyntax>(forStatement.Incrementors.Count);
        foreach (ForMutationPatchExpression incrementor in forStatement.Incrementors)
        {
            ExpressionBuildResult builtIncrementor = BuildForMutationExpression(
                incrementor,
                blockLocation,
                insertionIndex,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                types,
                authoredLocals);
            if (builtIncrementor.Syntax is null)
            {
                return StatementBuildResult.Failure(
                    builtIncrementor.DiagnosticCode ?? SemanticPatchDiagnosticCodes.InvalidStatement,
                    builtIncrementor.DiagnosticMessage ?? "A for mutation incrementor is invalid.");
            }

            incrementorSyntaxes.Add(builtIncrementor.Syntax);
        }

        ForStatementSyntax syntax = SyntaxFactory.ForStatement(SyntaxFactory.Block())
            .WithDeclaration(declarationSyntax)
            .WithInitializers(SyntaxFactory.SeparatedList(initializerSyntaxes))
            .WithCondition(conditionSyntax)
            .WithIncrementors(SyntaxFactory.SeparatedList(incrementorSyntaxes));
        return StatementBuildResult.Success(syntax);
    }

    private static StatementBuildResult BuildDoWhileStatement(
        DoWhilePatchStatement doWhileStatement,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types)
    {
        if (!graphSafetyIndex.IsInsertionReachable(blockLocation, insertionIndex))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.UnreachableInsertion,
                "A graph-authored do-while requires a CFG-reachable insertion gap.");
        }

        ExpressionBuildResult condition = BuildPatchExpression(
            doWhileStatement.Condition,
            blockLocation,
            insertionIndex,
            hirNodes,
            patchIndex,
            graphSafetyIndex,
            boundNodes,
            types);
        if (condition.Syntax is null || condition.Type is null)
        {
            return StatementBuildResult.Failure(
                condition.DiagnosticCode ?? SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                condition.DiagnosticMessage ?? "The graph-authored do-while condition is invalid.");
        }

        if (condition.Type.Kind != UnifiedTypeKind.Boolean ||
            !string.Equals(condition.Type.MetadataName, "System.Boolean", StringComparison.Ordinal))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"A do-while condition must have semantic type 'System.Boolean', not " +
                $"'{condition.Type.MetadataName}' " +
                $"('{doWhileStatement.Condition.ExpectedTypeId}').");
        }

        DoStatementSyntax syntax = SyntaxFactory.DoStatement(
            SyntaxFactory.Token(SyntaxKind.DoKeyword),
            SyntaxFactory.Block(),
            SyntaxFactory.Token(SyntaxKind.WhileKeyword),
            SyntaxFactory.Token(SyntaxKind.OpenParenToken),
            condition.Syntax,
            SyntaxFactory.Token(SyntaxKind.CloseParenToken),
            SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        return StatementBuildResult.Success(syntax);
    }

    private static StatementBuildResult BuildForEachStatement(
        ForEachPatchStatement forEachStatement,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types)
    {
        if (!graphSafetyIndex.IsInsertionReachable(blockLocation, insertionIndex))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.UnreachableInsertion,
                "A graph-authored foreach requires a CFG-reachable insertion gap.");
        }

        if (!types.TryGetValue(
                forEachStatement.ExpectedElementTypeId,
                out TypeDescriptor? elementType) ||
            !IsStableGraphExpressionType(elementType) ||
            !TypeNameMatches(forEachStatement.TypeName, elementType))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"Foreach element type '{forEachStatement.TypeName}' does not match graph-safe " +
                $"semantic type '{forEachStatement.ExpectedElementTypeId}'.");
        }

        if (!SyntaxFacts.IsValidIdentifier(forEachStatement.VariableName) ||
            SyntaxFacts.GetKeywordKind(forEachStatement.VariableName) != SyntaxKind.None)
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                $"Foreach variable name '{forEachStatement.VariableName}' is not one unescaped " +
                "C# identifier.");
        }

        if (HasNameConflict(blockLocation.Method, forEachStatement.VariableName))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.NameConflict,
                $"A parameter or local named '{forEachStatement.VariableName}' already exists in " +
                $"method '{blockLocation.Method.Name}'.");
        }

        ExpressionBuildResult collection = BuildForEachCollectionExpression(
            forEachStatement.Collection,
            blockLocation,
            insertionIndex,
            hirNodes,
            patchIndex,
            graphSafetyIndex,
            boundNodes,
            types);
        if (collection.Syntax is null || collection.Type is null)
        {
            return StatementBuildResult.Failure(
                collection.DiagnosticCode ?? SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                collection.DiagnosticMessage ?? "The graph-authored foreach collection is invalid.");
        }

        if (!ForEachCollectionElementMatches(collection.Type, elementType))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"Foreach collection type '{collection.Type.MetadataName}' does not yield exact " +
                $"element type '{elementType.MetadataName}'.");
        }

        TypeSyntax typeSyntax = SyntaxFactory.ParseTypeName(
            forEachStatement.TypeName,
            options: CSharpToHirLowerer.ParseOptions,
            consumeFullText: true);
        if (typeSyntax.ContainsDiagnostics ||
            typeSyntax.SpanStart != 0 ||
            typeSyntax.Span.Length != forEachStatement.TypeName.Length)
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                $"Foreach element type '{forEachStatement.TypeName}' is not one C# type name.");
        }

        ForEachStatementSyntax syntax = SyntaxFactory.ForEachStatement(
            default,
            default,
            SyntaxFactory.Token(SyntaxKind.ForEachKeyword),
            SyntaxFactory.Token(SyntaxKind.OpenParenToken),
            typeSyntax.WithoutTrivia(),
            SyntaxFactory.Identifier(forEachStatement.VariableName),
            SyntaxFactory.Token(SyntaxKind.InKeyword),
            collection.Syntax,
            SyntaxFactory.Token(SyntaxKind.CloseParenToken),
            SyntaxFactory.Block());
        return StatementBuildResult.Success(syntax);
    }

    private static StatementBuildResult BuildSwitchStatement(
        SwitchPatchStatement switchStatement,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types)
    {
        if (!graphSafetyIndex.IsInsertionReachable(blockLocation, insertionIndex))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.UnreachableInsertion,
                "A graph-authored switch requires a CFG-reachable insertion gap.");
        }

        ExpressionBuildResult governing = BuildPatchExpression(
            switchStatement.GoverningExpression,
            blockLocation,
            insertionIndex,
            hirNodes,
            patchIndex,
            graphSafetyIndex,
            boundNodes,
            types);
        if (governing.Syntax is null || governing.Type is null ||
            governing.Type.Id != switchStatement.ExpectedGoverningTypeId)
        {
            return StatementBuildResult.Failure(
                governing.DiagnosticCode ?? SemanticPatchDiagnosticCodes.TypeMismatch,
                governing.DiagnosticMessage ??
                    "The switch governing expression no longer matches its expected type.");
        }

        var sections = new List<SwitchSectionSyntax>(switchStatement.Sections.Count);
        foreach (SwitchSectionPatch section in switchStatement.Sections)
        {
            SwitchSectionBuildResult built = BuildSwitchSection(
                section,
                governing.Type,
                blockLocation,
                insertionIndex,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                types);
            if (built.Syntax is null)
            {
                return StatementBuildResult.Failure(
                    built.DiagnosticCode ?? SemanticPatchDiagnosticCodes.InvalidSwitchSection,
                    built.DiagnosticMessage ?? "The graph-authored switch section is invalid.");
            }

            sections.Add(built.Syntax);
        }

        SwitchStatementSyntax syntax = SyntaxFactory.SwitchStatement(governing.Syntax)
            .WithSections(SyntaxFactory.List(sections));
        return StatementBuildResult.Success(syntax);
    }

    private static SwitchSectionBuildResult BuildSwitchSection(
        SwitchSectionPatch section,
        TypeDescriptor governingType,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types)
    {
        var labels = new List<SwitchLabelSyntax>(section.Labels.Count);
        var patternNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (SwitchLabelPatch label in section.Labels)
        {
            if (label is DefaultSwitchLabelPatch)
            {
                labels.Add(SyntaxFactory.DefaultSwitchLabel());
                continue;
            }

            PatternSwitchLabelPatch patternLabel = (PatternSwitchLabelPatch)label;
            SwitchPatternBuildResult pattern = BuildSwitchPatternText(
                patternLabel.Pattern,
                governingType,
                blockLocation,
                insertionIndex,
                hirNodes,
                patchIndex,
                graphSafetyIndex,
                boundNodes,
                types,
                patternNames);
            if (pattern.Text is null)
            {
                return SwitchSectionBuildResult.Failure(
                    pattern.DiagnosticCode ?? SemanticPatchDiagnosticCodes.InvalidSwitchSection,
                    pattern.DiagnosticMessage ?? "The graph-authored switch pattern is invalid.");
            }

            string guardText = string.Empty;
            if (patternLabel.Guard is not null)
            {
                ExpressionBuildResult guard = BuildPatchExpression(
                    patternLabel.Guard,
                    blockLocation,
                    insertionIndex,
                    hirNodes,
                    patchIndex,
                    graphSafetyIndex,
                    boundNodes,
                    types);
                if (guard.Syntax is null || guard.Type is null ||
                    guard.Type.Kind != UnifiedTypeKind.Boolean ||
                    !string.Equals(
                        guard.Type.MetadataName,
                        "System.Boolean",
                        StringComparison.Ordinal))
                {
                    return SwitchSectionBuildResult.Failure(
                        guard.DiagnosticCode ?? SemanticPatchDiagnosticCodes.TypeMismatch,
                        guard.DiagnosticMessage ??
                            "A graph-authored switch guard must have semantic type System.Boolean.");
                }

                guardText = $" when {guard.Syntax.WithoutTrivia()}";
            }

            StatementSyntax parsed = SyntaxFactory.ParseStatement(
                $"switch (__unifiedValue) {{ case {pattern.Text}{guardText}: break; }}",
                options: CSharpToHirLowerer.ParseOptions,
                consumeFullText: true);
            if (parsed is not SwitchStatementSyntax parsedSwitch ||
                parsed.ContainsDiagnostics ||
                parsedSwitch.Sections.Count != 1 ||
                parsedSwitch.Sections[0].Labels.Count != 1)
            {
                return SwitchSectionBuildResult.Failure(
                    SemanticPatchDiagnosticCodes.InvalidSwitchSection,
                    $"Switch pattern '{pattern.Text}' could not be represented as one C# case label.");
            }

            labels.Add(parsedSwitch.Sections[0].Labels[0].WithoutTrivia());
        }

        return SwitchSectionBuildResult.Success(SyntaxFactory.SwitchSection(
            SyntaxFactory.List(labels),
            SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.BreakStatement())));
    }

    private static SwitchPatternBuildResult BuildSwitchPatternText(
        SwitchPatternPatch pattern,
        TypeDescriptor governingType,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        ISet<string> patternNames)
    {
        switch (pattern)
        {
            case ConstantSwitchPatternPatch constant:
                {
                    ExpressionBuildResult value = BuildPatchExpression(
                        constant.Value,
                        blockLocation,
                        insertionIndex,
                        hirNodes,
                        patchIndex,
                        graphSafetyIndex,
                        boundNodes,
                        types);
                    if (value.Syntax is null || value.Type?.Id != governingType.Id)
                    {
                        return SwitchPatternBuildResult.Failure(
                            value.DiagnosticCode ?? SemanticPatchDiagnosticCodes.TypeMismatch,
                            value.DiagnosticMessage ??
                                "A constant switch pattern must exactly match the governing type.");
                    }

                    return SwitchPatternBuildResult.Success(value.Syntax.WithoutTrivia().ToString());
                }
            case DeclarationSwitchPatternPatch declaration:
                {
                    if (!TryResolvePatternType(
                            declaration.TypeName,
                            declaration.ExpectedTypeId,
                            types,
                            out TypeSyntax? typeSyntax,
                            out string? typeFailure))
                    {
                        return SwitchPatternBuildResult.Failure(
                            SemanticPatchDiagnosticCodes.TypeMismatch,
                            typeFailure!);
                    }

                    string designation = declaration.IsDiscard ? "_" : declaration.VariableName;
                    if (!declaration.IsDiscard && !ValidatePatternVariable(
                            designation,
                            blockLocation.Method,
                            patternNames,
                            out string? variableFailure))
                    {
                        return SwitchPatternBuildResult.Failure(
                            SemanticPatchDiagnosticCodes.NameConflict,
                            variableFailure!);
                    }

                    return SwitchPatternBuildResult.Success($"{typeSyntax} {designation}");
                }
            case TypeSwitchPatternPatch typePattern:
                return TryResolvePatternType(
                        typePattern.TypeName,
                        typePattern.ExpectedTypeId,
                        types,
                        out TypeSyntax? matchedTypeSyntax,
                        out string? matchedTypeFailure)
                    ? SwitchPatternBuildResult.Success(matchedTypeSyntax!.ToString())
                    : SwitchPatternBuildResult.Failure(
                        SemanticPatchDiagnosticCodes.TypeMismatch,
                        matchedTypeFailure!);
            case VarSwitchPatternPatch varPattern:
                {
                    string designation = varPattern.IsDiscard ? "_" : varPattern.VariableName;
                    if (!varPattern.IsDiscard && !ValidatePatternVariable(
                            designation,
                            blockLocation.Method,
                            patternNames,
                            out string? variableFailure))
                    {
                        return SwitchPatternBuildResult.Failure(
                            SemanticPatchDiagnosticCodes.NameConflict,
                            variableFailure!);
                    }

                    return SwitchPatternBuildResult.Success($"var {designation}");
                }
            case DiscardSwitchPatternPatch:
                return SwitchPatternBuildResult.Success("_");
            case RelationalSwitchPatternPatch relational:
                {
                    ExpressionBuildResult value = BuildPatchExpression(
                        relational.Value,
                        blockLocation,
                        insertionIndex,
                        hirNodes,
                        patchIndex,
                        graphSafetyIndex,
                        boundNodes,
                        types);
                    if (value.Syntax is null || value.Type?.Id != governingType.Id)
                    {
                        return SwitchPatternBuildResult.Failure(
                            value.DiagnosticCode ?? SemanticPatchDiagnosticCodes.TypeMismatch,
                            value.DiagnosticMessage ??
                                "A relational switch pattern must exactly match the governing type.");
                    }

                    return SwitchPatternBuildResult.Success(
                        $"{relational.Operator} {value.Syntax.WithoutTrivia()}");
                }
            case LogicalSwitchPatternPatch logical:
                {
                    SwitchPatternBuildResult left = BuildSwitchPatternText(
                        logical.Left, governingType, blockLocation, insertionIndex, hirNodes,
                        patchIndex, graphSafetyIndex, boundNodes, types, patternNames);
                    if (left.Text is null)
                    {
                        return left;
                    }
                    SwitchPatternBuildResult right = BuildSwitchPatternText(
                        logical.Right, governingType, blockLocation, insertionIndex, hirNodes,
                        patchIndex, graphSafetyIndex, boundNodes, types, patternNames);
                    return right.Text is null
                        ? right
                        : SwitchPatternBuildResult.Success(
                            $"{left.Text} {logical.Operator} {right.Text}");
                }
            case NotSwitchPatternPatch notPattern:
                {
                    SwitchPatternBuildResult operand = BuildSwitchPatternText(
                        notPattern.Operand, governingType, blockLocation, insertionIndex, hirNodes,
                        patchIndex, graphSafetyIndex, boundNodes, types, patternNames);
                    return operand.Text is null
                        ? operand
                        : SwitchPatternBuildResult.Success($"not {operand.Text}");
                }
            case ParenthesizedSwitchPatternPatch parenthesized:
                {
                    SwitchPatternBuildResult inner = BuildSwitchPatternText(
                        parenthesized.Inner, governingType, blockLocation, insertionIndex, hirNodes,
                        patchIndex, graphSafetyIndex, boundNodes, types, patternNames);
                    return inner.Text is null
                        ? inner
                        : SwitchPatternBuildResult.Success($"({inner.Text})");
                }
            default:
                return SwitchPatternBuildResult.Failure(
                    SemanticPatchDiagnosticCodes.InvalidSwitchSection,
                    $"Switch pattern payload '{pattern.GetType().Name}' is not supported.");
        }
    }

    private static bool TryResolvePatternType(
        string typeName,
        SemanticId expectedTypeId,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        out TypeSyntax? syntax,
        out string? failure)
    {
        if (!types.TryGetValue(expectedTypeId, out TypeDescriptor? type) ||
            !type.IsGraphSafe || !TypeNameMatches(typeName, type))
        {
            syntax = null;
            failure = $"Pattern type '{typeName}' does not match graph-safe semantic type " +
                $"'{expectedTypeId}'.";
            return false;
        }

        syntax = SyntaxFactory.ParseTypeName(
            typeName,
            options: CSharpToHirLowerer.ParseOptions,
            consumeFullText: true);
        if (syntax.ContainsDiagnostics || syntax.SpanStart != 0 ||
            syntax.Span.Length != typeName.Length)
        {
            syntax = null;
            failure = $"Pattern type '{typeName}' is not one C# type name.";
            return false;
        }

        failure = null;
        return true;
    }

    private static bool ValidatePatternVariable(
        string name,
        HirMethodDeclaration method,
        ISet<string> patternNames,
        out string? failure)
    {
        if (!SyntaxFacts.IsValidIdentifier(name) ||
            SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None)
        {
            failure = $"Pattern variable '{name}' is not one unescaped C# identifier.";
            return false;
        }

        if (HasNameConflict(method, name) || !patternNames.Add(name))
        {
            failure = $"Pattern variable '{name}' conflicts with an existing or authored name.";
            return false;
        }

        failure = null;
        return true;
    }

    private static ExpressionBuildResult BuildForAssignmentExpression(
        ForAssignmentPatchExpression assignment,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types)
    {
        if (!hirNodes.TryGetValue(assignment.TargetDefinitionId, out HirNode? targetNode) ||
            targetNode is not HirVariableDeclarationStatement target ||
            !patchIndex.TryGetStatement(
                target.Id,
                out HirPatchIndex.StatementLocation? targetLocation) ||
            targetLocation!.Method.Id != blockLocation.Method.Id ||
            !string.Equals(target.Name, assignment.ExpectedName, StringComparison.Ordinal) ||
            HasNameConflict(blockLocation.Method, target.Name, target.Id) ||
            !patchIndex.DoesStatementDominateInsertion(
                target.Id,
                blockLocation.Block.Id,
                insertionIndex) ||
            !graphSafetyIndex.DoesDefinitionDominateInsertion(
                target.Id,
                blockLocation,
                insertionIndex))
        {
            return ExpressionBuildResult.Failure(
                SemanticPatchDiagnosticCodes.AssignmentTargetNotAvailable,
                $"For initializer target '{assignment.TargetDefinitionId}' is not an unshadowed " +
                "dominating local in the target method.");
        }

        if (!types.TryGetValue(assignment.ExpectedTypeId, out TypeDescriptor? targetType) ||
            !IsStableGraphExpressionType(targetType) ||
            !boundNodes.TryGetValue(target.Id, out BoundNodeInfo? targetBinding) ||
            targetBinding.TypeId != assignment.ExpectedTypeId)
        {
            return ExpressionBuildResult.Failure(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"For initializer target '{target.Name}' no longer has graph-safe semantic type " +
                $"'{assignment.ExpectedTypeId}'.");
        }

        ExpressionBuildResult value = BuildPatchExpression(
            assignment.Value,
            blockLocation,
            insertionIndex,
            hirNodes,
            patchIndex,
            graphSafetyIndex,
            boundNodes,
            types);
        if (value.Syntax is null || value.Type?.Id != assignment.ExpectedTypeId)
        {
            return value.Syntax is null
                ? value
                : ExpressionBuildResult.Failure(
                    SemanticPatchDiagnosticCodes.TypeMismatch,
                    "A for assignment initializer value must exactly match its target type.");
        }

        AssignmentExpressionSyntax syntax = SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.IdentifierName(target.Name),
            value.Syntax);
        return ExpressionBuildResult.Success(syntax, targetType);
    }

    private static ExpressionBuildResult BuildForMutationExpression(
        ForMutationPatchExpression mutation,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        IReadOnlyDictionary<int, AuthoredForLocal> authoredLocals)
    {
        if (!types.TryGetValue(mutation.ExpectedTypeId, out TypeDescriptor? mutationType) ||
            (!IsArithmeticOperatorType(mutationType) && mutationType.Kind != UnifiedTypeKind.Enum))
        {
            return ExpressionBuildResult.Failure(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"For mutation type '{mutation.ExpectedTypeId}' does not support ++ or --.");
        }

        ExpressionBuildResult operand = BuildPatchExpression(
            mutation.Operand,
            blockLocation,
            insertionIndex,
            hirNodes,
            patchIndex,
            graphSafetyIndex,
            boundNodes,
            types,
            authoredLocals);
        if (operand.Syntax is not IdentifierNameSyntax || operand.Type?.Id != mutation.ExpectedTypeId)
        {
            return operand.Syntax is null
                ? operand
                : ExpressionBuildResult.Failure(
                    SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                    "A for mutation operand must resolve to one same-typed writable local.");
        }

        SyntaxKind kind = (mutation.Operator, mutation.IsPostfix) switch
        {
            ("++", false) => SyntaxKind.PreIncrementExpression,
            ("--", false) => SyntaxKind.PreDecrementExpression,
            ("++", true) => SyntaxKind.PostIncrementExpression,
            ("--", true) => SyntaxKind.PostDecrementExpression,
            _ => SyntaxKind.None,
        };
        if (kind == SyntaxKind.None)
        {
            return ExpressionBuildResult.Failure(
                SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                $"For incrementor operator '{mutation.Operator}' is not supported.");
        }

        ExpressionSyntax syntax = mutation.IsPostfix
            ? SyntaxFactory.PostfixUnaryExpression(kind, operand.Syntax)
            : SyntaxFactory.PrefixUnaryExpression(kind, operand.Syntax);
        return ExpressionBuildResult.Success(syntax, mutationType);
    }

    private static StatementBuildResult BuildControlTransferStatement(
        bool isContinue,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex)
    {
        bool hasTarget = isContinue
            ? patchIndex.TryGetNearestEnclosingLoop(
                blockLocation.Block.Id,
                out HirStatement? target)
            : patchIndex.TryGetNearestEnclosingBreakable(
                blockLocation.Block.Id,
                out target);
        if (!hasTarget)
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                $"A graph-authored {(isContinue ? "continue" : "break")} requires an enclosing " +
                (isContinue
                    ? "while, do, for, or foreach loop in the current HIR."
                    : "loop or switch in the current HIR."));
        }

        if (!graphSafetyIndex.IsInsertionReachable(blockLocation, insertionIndex))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.UnreachableInsertion,
                $"A graph-authored {(isContinue ? "continue" : "break")} requires a reachable " +
                $"insertion gap inside '{target!.Id}'.");
        }

        return StatementBuildResult.Success(isContinue
            ? SyntaxFactory.ContinueStatement()
            : SyntaxFactory.BreakStatement());
    }

    private static StatementBuildResult BuildReturnStatement(
        ReturnPatchStatement returnStatement,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, MethodDescriptor> methods,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types)
    {
        if (!methods.TryGetValue(blockLocation.Method.Id, out MethodDescriptor? method) ||
            method.ReturnTypeId != returnStatement.ExpectedReturnTypeId ||
            !types.TryGetValue(returnStatement.ExpectedReturnTypeId, out TypeDescriptor? returnType) ||
            !returnType.IsGraphSafe)
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"Return payload type '{returnStatement.ExpectedReturnTypeId}' does not match the " +
                $"graph-safe return type of method '{blockLocation.Method.Name}'.");
        }

        if (blockLocation.Method.IsAsync || returnType.Kind == UnifiedTypeKind.TaskLike)
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                "Return insertion for async or task-like methods is outside the current bounded slice.");
        }

        if (returnType.Kind == UnifiedTypeKind.Void)
        {
            return returnStatement.Expression is null
                ? StatementBuildResult.Success(SyntaxFactory.ReturnStatement())
                : StatementBuildResult.Failure(
                    SemanticPatchDiagnosticCodes.TypeMismatch,
                    "A void method return cannot carry a value expression.");
        }

        if (returnStatement.Expression is null)
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"Method '{blockLocation.Method.Name}' requires a return value of semantic type " +
                $"'{returnStatement.ExpectedReturnTypeId}'.");
        }

        if (returnStatement.Expression.ExpectedTypeId != returnStatement.ExpectedReturnTypeId)
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"Return expression type '{returnStatement.Expression.ExpectedTypeId}' does not match " +
                $"method return type '{returnStatement.ExpectedReturnTypeId}'.");
        }

        ExpressionBuildResult expression = BuildPatchExpression(
            returnStatement.Expression,
            blockLocation,
            insertionIndex,
            hirNodes,
            patchIndex,
            graphSafetyIndex,
            boundNodes,
            types);
        if (expression.Syntax is null || expression.Type?.Id != returnStatement.ExpectedReturnTypeId)
        {
            return StatementBuildResult.Failure(
                expression.DiagnosticCode ?? SemanticPatchDiagnosticCodes.TypeMismatch,
                expression.DiagnosticMessage ?? "The graph-authored return expression is invalid.");
        }

        ReturnStatementSyntax syntax = SyntaxFactory
            .ReturnStatement(expression.Syntax)
            .WithReturnKeyword(SyntaxFactory.Token(SyntaxKind.ReturnKeyword)
                .WithTrailingTrivia(SyntaxFactory.Space))
            .NormalizeWhitespace()
            .WithoutLeadingTrivia()
            .WithoutTrailingTrivia();
        return StatementBuildResult.Success(syntax);
    }

    private static ExpressionBuildResult BuildPatchExpression(
        PatchExpression expression,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        IReadOnlyDictionary<int, AuthoredForLocal>? authoredForLocals = null,
        SemanticId? forHeaderLoopId = null)
    {
        if (!types.TryGetValue(expression.ExpectedTypeId, out TypeDescriptor? expectedType) ||
            !IsStableGraphExpressionType(expectedType))
        {
            return ExpressionBuildResult.Failure(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"Graph-authored expression type '{expression.ExpectedTypeId}' is missing or outside " +
                "the graph-safe scalar subset.");
        }

        switch (expression)
        {
            case LiteralPatchExpression literal:
                if (!TryParseLiteral(literal.ValueText, out LiteralExpressionSyntax? literalSyntax) ||
                    literalSyntax!.Token.Value is not object value ||
                    !string.Equals(
                        value.GetType().FullName,
                        expectedType.MetadataName,
                        StringComparison.Ordinal))
                {
                    return ExpressionBuildResult.Failure(
                        SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                        $"Literal '{literal.ValueText}' is not one scalar C# literal with expected type " +
                        $"'{expectedType.MetadataName}'. Signed literals must use an explicit unary payload.");
                }

                return ExpressionBuildResult.Success(literalSyntax.WithoutTrivia(), expectedType);

            case DefaultPatchExpression defaultExpression:
                {
                    TypeSyntax typeSyntax = SyntaxFactory.ParseTypeName(
                        defaultExpression.TypeName,
                        options: CSharpToHirLowerer.ParseOptions,
                        consumeFullText: true);
                    if (typeSyntax.ContainsDiagnostics ||
                        typeSyntax.Span.Length != defaultExpression.TypeName.Length ||
                        !TypeNameMatches(defaultExpression.TypeName, expectedType))
                    {
                        return ExpressionBuildResult.Failure(
                            SemanticPatchDiagnosticCodes.TypeMismatch,
                            $"Default type '{defaultExpression.TypeName}' does not match semantic " +
                            $"type '{expression.ExpectedTypeId}'.");
                    }

                    string? defaultText = expectedType.MetadataName switch
                    {
                        "System.Boolean" => "false",
                        "System.Char" => "'\\0'",
                        "System.SByte" => "(sbyte)0",
                        "System.Byte" => "(byte)0",
                        "System.Int16" => "(short)0",
                        "System.UInt16" => "(ushort)0",
                        "System.Int32" => "0",
                        "System.UInt32" => "0U",
                        "System.Int64" => "0L",
                        "System.UInt64" => "0UL",
                        "System.Single" => "0.0f",
                        "System.Double" => "0.0",
                        "System.String" => "\"\"",
                        _ when expectedType.Kind == UnifiedTypeKind.Enum =>
                            $"({defaultExpression.TypeName})0",
                        _ => null,
                    };
                    if (defaultText is null)
                    {
                        return ExpressionBuildResult.Failure(
                            SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                            $"Type '{expectedType.DisplayName}' has no bounded graph default value.");
                    }
                    ExpressionSyntax syntax = SyntaxFactory.ParseExpression(
                        defaultText,
                        options: CSharpToHirLowerer.ParseOptions,
                        consumeFullText: true);
                    if (syntax.ContainsDiagnostics || syntax.Span.Length != defaultText.Length)
                    {
                        return ExpressionBuildResult.Failure(
                            SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                            $"Type '{expectedType.DisplayName}' default could not be represented in C#.");
                    }
                    return ExpressionBuildResult.Success(syntax.WithoutTrivia(), expectedType);
                }

            case LocalReferencePatchExpression local:
                return BuildLocalReferenceExpression(
                    local,
                    expectedType,
                    blockLocation,
                    insertionIndex,
                    hirNodes,
                    patchIndex,
                    graphSafetyIndex,
                    boundNodes,
                    forHeaderLoopId);

            case ForLocalReferencePatchExpression forLocal:
                if (authoredForLocals is null ||
                    !authoredForLocals.TryGetValue(
                        forLocal.VariableIndex,
                        out AuthoredForLocal authoredLocal) ||
                    !string.Equals(
                        forLocal.ExpectedName,
                        authoredLocal.Name,
                        StringComparison.Ordinal) ||
                    forLocal.ExpectedTypeId != authoredLocal.Type.Id)
                {
                    return ExpressionBuildResult.Failure(
                        SemanticPatchDiagnosticCodes.ValueSourceNotAvailable,
                        $"For-local source '{forLocal.VariableIndex}:{forLocal.ExpectedName}' " +
                        "does not resolve to a currently declared for variable.");
                }

                return ExpressionBuildResult.Success(
                    SyntaxFactory.IdentifierName(authoredLocal.Name),
                    authoredLocal.Type);

            case UnaryPatchExpression unary:
                {
                    if (!GraphExpressionUnaryOperators.Contains(unary.Operator) ||
                        !TryParseUnaryOperator(unary.Operator, out PrefixUnaryExpressionSyntax? parsedUnary))
                    {
                        return ExpressionBuildResult.Failure(
                            SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                            $"Unary operator '{unary.Operator}' is outside the graph-authored expression subset.");
                    }

                    ExpressionBuildResult operand = BuildPatchExpression(
                        unary.Operand,
                        blockLocation,
                        insertionIndex,
                        hirNodes,
                        patchIndex,
                        graphSafetyIndex,
                        boundNodes,
                        types,
                        authoredForLocals,
                        forHeaderLoopId);
                    if (operand.Syntax is null || operand.Type is null)
                    {
                        return operand;
                    }

                    if (!IsValidGraphUnaryType(unary.Operator, operand.Type, expectedType))
                    {
                        return ExpressionBuildResult.Failure(
                            SemanticPatchDiagnosticCodes.TypeMismatch,
                            $"Unary operator '{unary.Operator}' does not preserve the declared operand/result " +
                            "types in the bounded scalar subset.");
                    }

                    ExpressionSyntax operandSyntax = ParenthesizeCompositeExpression(operand.Syntax);
                    PrefixUnaryExpressionSyntax syntax = SyntaxFactory.PrefixUnaryExpression(
                        parsedUnary!.Kind(),
                        parsedUnary.OperatorToken.WithoutTrivia(),
                        operandSyntax);
                    return ExpressionBuildResult.Success(syntax, expectedType);
                }

            case BinaryPatchExpression binary:
                {
                    if (!GraphExpressionBinaryOperators.Contains(binary.Operator) ||
                        !TryParseBinaryOperator(binary.Operator, out BinaryExpressionSyntax? parsedBinary))
                    {
                        return ExpressionBuildResult.Failure(
                            SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                            $"Binary operator '{binary.Operator}' is outside the graph-authored expression subset.");
                    }

                    ExpressionBuildResult left = BuildPatchExpression(
                        binary.Left,
                        blockLocation,
                        insertionIndex,
                        hirNodes,
                        patchIndex,
                        graphSafetyIndex,
                        boundNodes,
                        types,
                        authoredForLocals,
                        forHeaderLoopId);
                    if (left.Syntax is null || left.Type is null)
                    {
                        return left;
                    }

                    ExpressionBuildResult right = BuildPatchExpression(
                        binary.Right,
                        blockLocation,
                        insertionIndex,
                        hirNodes,
                        patchIndex,
                        graphSafetyIndex,
                        boundNodes,
                        types,
                        authoredForLocals,
                        forHeaderLoopId);
                    if (right.Syntax is null || right.Type is null)
                    {
                        return right;
                    }

                    if (!IsValidGraphBinaryType(binary.Operator, left.Type, right.Type, expectedType))
                    {
                        return ExpressionBuildResult.Failure(
                            SemanticPatchDiagnosticCodes.TypeMismatch,
                            $"Binary operator '{binary.Operator}' does not match the declared operand/result " +
                            "types in the bounded scalar subset.");
                    }

                    BinaryExpressionSyntax syntax = SyntaxFactory.BinaryExpression(
                        parsedBinary!.Kind(),
                        ParenthesizeCompositeExpression(left.Syntax),
                        parsedBinary.OperatorToken.WithoutTrivia(),
                        ParenthesizeCompositeExpression(right.Syntax));
                    return ExpressionBuildResult.Success(syntax, expectedType);
                }

            case CastPatchExpression cast:
                {
                    ExpressionBuildResult operand = BuildPatchExpression(
                        cast.Operand,
                        blockLocation,
                        insertionIndex,
                        hirNodes,
                        patchIndex,
                        graphSafetyIndex,
                        boundNodes,
                        types,
                        authoredForLocals,
                        forHeaderLoopId);
                    if (operand.Syntax is null || operand.Type is null)
                    {
                        return operand;
                    }

                    TypeSyntax typeSyntax = SyntaxFactory.ParseTypeName(
                        cast.TypeName,
                        options: CSharpToHirLowerer.ParseOptions,
                        consumeFullText: true);
                    if (typeSyntax.ContainsDiagnostics ||
                        typeSyntax.SpanStart != 0 ||
                        typeSyntax.Span.Length != cast.TypeName.Length ||
                        !TypeNameMatches(cast.TypeName, expectedType))
                    {
                        return ExpressionBuildResult.Failure(
                            SemanticPatchDiagnosticCodes.TypeMismatch,
                            $"Cast type '{cast.TypeName}' does not match semantic type " +
                            $"'{expression.ExpectedTypeId}'.");
                    }

                    CastExpressionSyntax syntax = SyntaxFactory.CastExpression(
                        typeSyntax.WithoutTrivia(),
                        ParenthesizeCompositeExpression(operand.Syntax));
                    return ExpressionBuildResult.Success(syntax, expectedType);
                }

            case ConditionalPatchExpression conditional:
                {
                    ExpressionBuildResult condition = BuildPatchExpression(
                        conditional.Condition,
                        blockLocation,
                        insertionIndex,
                        hirNodes,
                        patchIndex,
                        graphSafetyIndex,
                        boundNodes,
                        types,
                        authoredForLocals,
                        forHeaderLoopId);
                    if (condition.Syntax is null || condition.Type is null)
                    {
                        return condition;
                    }

                    ExpressionBuildResult whenTrue = BuildPatchExpression(
                        conditional.WhenTrue,
                        blockLocation,
                        insertionIndex,
                        hirNodes,
                        patchIndex,
                        graphSafetyIndex,
                        boundNodes,
                        types,
                        authoredForLocals,
                        forHeaderLoopId);
                    if (whenTrue.Syntax is null || whenTrue.Type is null)
                    {
                        return whenTrue;
                    }

                    ExpressionBuildResult whenFalse = BuildPatchExpression(
                        conditional.WhenFalse,
                        blockLocation,
                        insertionIndex,
                        hirNodes,
                        patchIndex,
                        graphSafetyIndex,
                        boundNodes,
                        types,
                        authoredForLocals,
                        forHeaderLoopId);
                    if (whenFalse.Syntax is null || whenFalse.Type is null)
                    {
                        return whenFalse;
                    }

                    if (!IsBoolean(condition.Type) ||
                        !IsSameType(whenTrue.Type, expectedType) ||
                        !IsSameType(whenFalse.Type, expectedType))
                    {
                        return ExpressionBuildResult.Failure(
                            SemanticPatchDiagnosticCodes.TypeMismatch,
                            "A conditional expression requires a Boolean condition and two branches " +
                            "whose semantic types exactly match its declared result type.");
                    }

                    ConditionalExpressionSyntax syntax = SyntaxFactory.ConditionalExpression(
                        ParenthesizeCompositeExpression(condition.Syntax),
                        ParenthesizeCompositeExpression(whenTrue.Syntax),
                        ParenthesizeCompositeExpression(whenFalse.Syntax));
                    return ExpressionBuildResult.Success(syntax, expectedType);
                }

            case ConditionalMemberAccessPatchExpression conditionalAccess:
                {
                    if (!SyntaxFacts.IsValidIdentifier(conditionalAccess.MemberName) ||
                        SyntaxFacts.GetKeywordKind(conditionalAccess.MemberName) != SyntaxKind.None)
                    {
                        return ExpressionBuildResult.Failure(
                            SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                            $"Conditional member name '{conditionalAccess.MemberName}' is not one " +
                            "unescaped C# identifier.");
                    }

                    ExpressionBuildResult receiver = BuildPatchExpression(
                        conditionalAccess.Receiver,
                        blockLocation,
                        insertionIndex,
                        hirNodes,
                        patchIndex,
                        graphSafetyIndex,
                        boundNodes,
                        types,
                        authoredForLocals,
                        forHeaderLoopId);
                    if (receiver.Syntax is null || receiver.Type is null)
                    {
                        return receiver;
                    }

                    if ((receiver.Type.Kind != UnifiedTypeKind.String &&
                            receiver.Type.Nullability != UnifiedNullability.Nullable) ||
                        expectedType.Nullability != UnifiedNullability.Nullable)
                    {
                        return ExpressionBuildResult.Failure(
                            SemanticPatchDiagnosticCodes.TypeMismatch,
                            "A conditional member access requires a reference-like or nullable " +
                            "receiver and a nullable declared result type.");
                    }

                    ConditionalAccessExpressionSyntax syntax =
                        SyntaxFactory.ConditionalAccessExpression(
                            ParenthesizeCompositeExpression(receiver.Syntax),
                            SyntaxFactory.MemberBindingExpression(
                                SyntaxFactory.IdentifierName(conditionalAccess.MemberName)));
                    return ExpressionBuildResult.Success(syntax, expectedType);
                }

            case MemberAccessPatchExpression memberAccess:
                {
                    if (memberAccess.Receiver is not LocalReferencePatchExpression receiverReference ||
                        !SyntaxFacts.IsValidIdentifier(memberAccess.MemberName) ||
                        SyntaxFacts.GetKeywordKind(memberAccess.MemberName) != SyntaxKind.None ||
                        !types.TryGetValue(
                            receiverReference.ExpectedTypeId,
                            out TypeDescriptor? receiverType) ||
                        !receiverType.IsGraphSafe)
                    {
                        return ExpressionBuildResult.Failure(
                            SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                            "A member access requires a graph-safe local receiver and one C# member name.");
                    }

                    ExpressionBuildResult receiver = BuildLocalReferenceExpression(
                        receiverReference,
                        receiverType,
                        blockLocation,
                        insertionIndex,
                        hirNodes,
                        patchIndex,
                        graphSafetyIndex,
                        boundNodes,
                        forHeaderLoopId);
                    if (receiver.Syntax is null)
                    {
                        return receiver;
                    }

                    MemberAccessExpressionSyntax syntax = SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        receiver.Syntax,
                        SyntaxFactory.IdentifierName(memberAccess.MemberName));
                    return ExpressionBuildResult.Success(syntax, expectedType);
                }

            case MemberInvocationPatchExpression invocation:
                {
                    if (invocation.Receiver is not LocalReferencePatchExpression receiverReference ||
                        !SyntaxFacts.IsValidIdentifier(invocation.MemberName) ||
                        SyntaxFacts.GetKeywordKind(invocation.MemberName) != SyntaxKind.None ||
                        !types.TryGetValue(
                            receiverReference.ExpectedTypeId,
                            out TypeDescriptor? receiverType) ||
                        !receiverType.IsGraphSafe)
                    {
                        return ExpressionBuildResult.Failure(
                            SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                            "A member invocation requires a graph-safe local receiver and one C# method name.");
                    }

                    ExpressionBuildResult receiver = BuildLocalReferenceExpression(
                        receiverReference,
                        receiverType,
                        blockLocation,
                        insertionIndex,
                        hirNodes,
                        patchIndex,
                        graphSafetyIndex,
                        boundNodes,
                        forHeaderLoopId);
                    if (receiver.Syntax is null)
                    {
                        return receiver;
                    }

                    var arguments = new List<ArgumentSyntax>(invocation.Arguments.Count);
                    foreach (PatchExpression argument in invocation.Arguments)
                    {
                        ExpressionBuildResult authoredArgument = BuildPatchExpression(
                            argument,
                            blockLocation,
                            insertionIndex,
                            hirNodes,
                            patchIndex,
                            graphSafetyIndex,
                            boundNodes,
                            types,
                            authoredForLocals,
                            forHeaderLoopId);
                        if (authoredArgument.Syntax is null)
                        {
                            return authoredArgument;
                        }
                        arguments.Add(SyntaxFactory.Argument(authoredArgument.Syntax));
                    }

                    MemberAccessExpressionSyntax target = SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        receiver.Syntax,
                        SyntaxFactory.IdentifierName(invocation.MemberName));
                    InvocationExpressionSyntax syntax = SyntaxFactory.InvocationExpression(
                        target,
                        SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
                    return ExpressionBuildResult.Success(syntax, expectedType);
                }

            default:
                return ExpressionBuildResult.Failure(
                    SemanticPatchDiagnosticCodes.InvalidGraphExpression,
                    $"Graph-authored expression '{expression.GetType().Name}' is not supported.");
        }
    }

    private static ExpressionBuildResult BuildLocalReferenceExpression(
        LocalReferencePatchExpression local,
        TypeDescriptor expectedType,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        SemanticId? forHeaderLoopId)
    {
        if (!hirNodes.TryGetValue(local.SourceDefinitionId, out HirNode? source))
        {
            return UnavailableGraphSource(local, "was not found in the current HIR");
        }

        string? actualName = source switch
        {
            HirParameter parameter when blockLocation.Method.Parameters.Any(
                candidate => candidate.Id == parameter.Id) => parameter.Name,
            HirVariableDeclarationStatement variable when
                patchIndex.TryGetStatement(
                    variable.Id,
                    out HirPatchIndex.StatementLocation? sourceLocation) &&
                sourceLocation!.Method.Id == blockLocation.Method.Id &&
                patchIndex.DoesStatementDominateInsertion(
                    variable.Id,
                    blockLocation.Block.Id,
                    insertionIndex) &&
                graphSafetyIndex.DoesDefinitionDominateInsertion(
                    variable.Id,
                    blockLocation,
                    insertionIndex) => variable.Name,
            HirForVariableDeclarator variable when
                forHeaderLoopId is SemanticId loopId &&
                patchIndex.IsForVariableDeclaredBy(variable.Id, loopId) ||
                patchIndex.IsForVariableInScopeAtBlock(
                    variable.Id,
                    blockLocation.Block.Id) &&
                graphSafetyIndex.DoesDefinitionDominateInsertion(
                    variable.Id,
                    blockLocation,
                    insertionIndex) => variable.Name,
            HirForEachVariable variable when
                patchIndex.IsForEachVariableInScopeAtBlock(
                    variable.Id,
                    blockLocation.Block.Id) &&
                graphSafetyIndex.DoesDefinitionDominateInsertion(
                    variable.Id,
                    blockLocation,
                    insertionIndex) => variable.Name,
            _ => null,
        };
        if (actualName is null)
        {
            return UnavailableGraphSource(
                local,
                "is not a parameter or a local that lexically and CFG-dominates the reachable insertion gap");
        }

        if (!string.Equals(actualName, local.ExpectedName, StringComparison.Ordinal) ||
            HasNameConflict(blockLocation.Method, actualName, local.SourceDefinitionId))
        {
            return UnavailableGraphSource(
                local,
                $"is named '{actualName}', not '{local.ExpectedName}', or is shadowed");
        }

        if (!boundNodes.TryGetValue(local.SourceDefinitionId, out BoundNodeInfo? binding) ||
            binding.TypeId != local.ExpectedTypeId)
        {
            return ExpressionBuildResult.Failure(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"Graph value source '{local.SourceDefinitionId}' no longer has expected type " +
                $"'{local.ExpectedTypeId}'.");
        }

        return ExpressionBuildResult.Success(
            SyntaxFactory.IdentifierName(actualName),
            expectedType);
    }

    private static ExpressionBuildResult BuildForEachCollectionExpression(
        PatchExpression expression,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types)
    {
        if (expression is not LocalReferencePatchExpression local ||
            !types.TryGetValue(expression.ExpectedTypeId, out TypeDescriptor? collectionType) ||
            !IsForEachCollectionType(collectionType))
        {
            return ExpressionBuildResult.Failure(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                "A foreach collection must be a local/parameter reference with exact System.String, " +
                "graph-safe one-dimensional array, or graph-safe IEnumerable<T>-backed type.");
        }

        return BuildLocalReferenceExpression(
            local,
            collectionType,
            blockLocation,
            insertionIndex,
            hirNodes,
            patchIndex,
            graphSafetyIndex,
            boundNodes,
            forHeaderLoopId: null);
    }

    private static bool IsForEachCollectionType(TypeDescriptor type) =>
        type.IsGraphSafe &&
        type.Kind is UnifiedTypeKind.String or UnifiedTypeKind.Array or UnifiedTypeKind.Enumerable;

    private static bool ForEachCollectionElementMatches(
        TypeDescriptor collectionType,
        TypeDescriptor elementType) => collectionType.Kind switch
        {
            UnifiedTypeKind.String =>
                string.Equals(elementType.MetadataName, "System.Char", StringComparison.Ordinal),
            UnifiedTypeKind.Array when collectionType.MetadataName.EndsWith("[]", StringComparison.Ordinal) =>
                string.Equals(
                    collectionType.MetadataName[..^2],
                    elementType.MetadataName,
                    StringComparison.Ordinal),
            UnifiedTypeKind.Enumerable => collectionType.ElementTypeId == elementType.Id,
            _ => false,
        };

    private static ExpressionBuildResult UnavailableGraphSource(
        LocalReferencePatchExpression local,
        string reason) => ExpressionBuildResult.Failure(
        SemanticPatchDiagnosticCodes.ValueSourceNotAvailable,
        $"Graph value source '{local.SourceDefinitionId}' {reason}.");

    private static bool IsStableGraphExpressionType(TypeDescriptor type) =>
        type.IsGraphSafe &&
        (type.Kind is UnifiedTypeKind.Boolean or
            UnifiedTypeKind.Integer or
            UnifiedTypeKind.FloatingPoint or
            UnifiedTypeKind.Enum or
            UnifiedTypeKind.String) &&
        !string.Equals(type.MetadataName, "System.Decimal", StringComparison.Ordinal);

    private static bool IsValidGraphUnaryType(
        string operatorText,
        TypeDescriptor operand,
        TypeDescriptor result)
    {
        if (operatorText == "!")
        {
            return IsBoolean(operand) && IsSameType(operand, result);
        }

        if (operatorText == "~")
        {
            return IsSameType(operand, result) &&
                (operand.Kind == UnifiedTypeKind.Enum || IsIntegralOperatorType(operand));
        }

        if (operatorText == "+")
        {
            return IsSameType(operand, result) && IsArithmeticOperatorType(operand);
        }

        return operatorText == "-" &&
            IsSameType(operand, result) &&
            operand.MetadataName is "System.Int32" or "System.Int64" or "System.Single" or "System.Double";
    }

    private static bool IsValidGraphBinaryType(
        string operatorText,
        TypeDescriptor left,
        TypeDescriptor right,
        TypeDescriptor result)
    {
        if (operatorText is "+" or "-" or "*")
        {
            return IsSameType(left, right) && IsSameType(left, result) &&
                IsArithmeticOperatorType(left);
        }

        if (operatorText is "<<" or ">>")
        {
            return IsSameType(left, result) &&
                IsIntegralOperatorType(left) &&
                string.Equals(right.MetadataName, "System.Int32", StringComparison.Ordinal);
        }

        if (operatorText is "<" or ">" or "<=" or ">=")
        {
            return IsSameType(left, right) && IsArithmeticOperatorType(left) && IsBoolean(result);
        }

        if (operatorText is "==" or "!=")
        {
            return IsSameType(left, right) &&
                (IsBoolean(left) || IsArithmeticOperatorType(left) || left.Kind == UnifiedTypeKind.Enum) &&
                IsBoolean(result);
        }

        if (operatorText is "&&" or "||")
        {
            return IsBoolean(left) && IsBoolean(right) && IsBoolean(result);
        }

        if (operatorText == "??")
        {
            return left.Nullability == UnifiedNullability.Nullable &&
                IsSameType(right, result) &&
                IsSameTypeIgnoringNullability(left, result);
        }

        return (operatorText is "&" or "^" or "|") &&
            IsSameType(left, right) &&
            IsSameType(left, result) &&
            (IsBoolean(left) || IsIntegralOperatorType(left) || left.Kind == UnifiedTypeKind.Enum);
    }

    private static bool IsBoolean(TypeDescriptor type) =>
        type.Kind == UnifiedTypeKind.Boolean &&
        string.Equals(type.MetadataName, "System.Boolean", StringComparison.Ordinal);

    private static bool IsArithmeticOperatorType(TypeDescriptor type) =>
        type.MetadataName is
            "System.Int32" or
            "System.UInt32" or
            "System.Int64" or
            "System.UInt64" or
            "System.Single" or
            "System.Double";

    private static bool IsIntegralOperatorType(TypeDescriptor type) =>
        type.MetadataName is
            "System.Int32" or
            "System.UInt32" or
            "System.Int64" or
            "System.UInt64";

    private static bool IsSameType(TypeDescriptor left, TypeDescriptor right) => left.Id == right.Id;

    private static bool IsSameTypeIgnoringNullability(
        TypeDescriptor left,
        TypeDescriptor right) =>
        left.Kind == right.Kind &&
        string.Equals(
            left.MetadataName.TrimEnd('?'),
            right.MetadataName.TrimEnd('?'),
            StringComparison.Ordinal);

    private static ExpressionSyntax ParenthesizeCompositeExpression(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax or IdentifierNameSyntax
            ? expression
            : SyntaxFactory.ParenthesizedExpression(expression);

    private static bool TryCreateLocalLiteralStatement(
        LocalLiteralDeclarationPatchStatement local,
        out LocalDeclarationStatementSyntax? statement)
    {
        statement = null;
        if (!SyntaxFacts.IsValidIdentifier(local.Name) ||
            SyntaxFacts.GetKeywordKind(local.Name) != SyntaxKind.None ||
            !TryParseLiteral(local.ValueText, out LiteralExpressionSyntax? literal))
        {
            return false;
        }

        TypeSyntax type = SyntaxFactory.ParseTypeName(
            local.TypeName,
            options: CSharpToHirLowerer.ParseOptions,
            consumeFullText: true);
        if (type.ContainsDiagnostics || type.SpanStart != 0 || type.Span.Length != local.TypeName.Length)
        {
            return false;
        }

        EqualsValueClauseSyntax initializer = SyntaxFactory.EqualsValueClause(
            SyntaxFactory.Token(SyntaxKind.EqualsToken)
                .WithLeadingTrivia(SyntaxFactory.Space)
                .WithTrailingTrivia(SyntaxFactory.Space),
            literal!.WithoutTrivia());
        VariableDeclaratorSyntax variable = SyntaxFactory.VariableDeclarator(
                SyntaxFactory.Identifier(local.Name))
            .WithInitializer(initializer);
        VariableDeclarationSyntax declaration = SyntaxFactory.VariableDeclaration(
            type.WithoutTrivia().WithTrailingTrivia(SyntaxFactory.Space),
            SyntaxFactory.SingletonSeparatedList(variable));
        statement = SyntaxFactory.LocalDeclarationStatement(declaration);
        return true;
    }

    private static bool TryCreateTemporaryStatement(
        string name,
        string typeName,
        ExpressionSyntax initializerExpression,
        out LocalDeclarationStatementSyntax? statement)
    {
        statement = null;
        if (!SyntaxFacts.IsValidIdentifier(name) ||
            SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None)
        {
            return false;
        }

        TypeSyntax type = SyntaxFactory.ParseTypeName(
            typeName,
            options: CSharpToHirLowerer.ParseOptions,
            consumeFullText: true);
        if (type.ContainsDiagnostics || type.SpanStart != 0 || type.Span.Length != typeName.Length)
        {
            return false;
        }

        EqualsValueClauseSyntax initializer = SyntaxFactory.EqualsValueClause(
            SyntaxFactory.Token(SyntaxKind.EqualsToken)
                .WithLeadingTrivia(SyntaxFactory.Space)
                .WithTrailingTrivia(SyntaxFactory.Space),
            initializerExpression.WithoutLeadingTrivia().WithoutTrailingTrivia());
        VariableDeclaratorSyntax variable = SyntaxFactory.VariableDeclarator(
                SyntaxFactory.Identifier(name))
            .WithInitializer(initializer);
        VariableDeclarationSyntax declaration = SyntaxFactory.VariableDeclaration(
            type.WithoutTrivia().WithTrailingTrivia(SyntaxFactory.Space),
            SyntaxFactory.SingletonSeparatedList(variable));
        statement = SyntaxFactory.LocalDeclarationStatement(declaration);
        return true;
    }

    private static InsertionFormatting GetInsertionFormatting(
        ParsedDocument document,
        BlockSyntax block,
        int insertionIndex) => GetInsertionFormatting(
            document,
            new EditableStatementContainer(block, block.Statements),
            insertionIndex);

    private static InsertionFormatting GetInsertionFormatting(
        ParsedDocument document,
        EditableStatementContainer container,
        int insertionIndex)
    {
        string indentation;
        if (container.Statements.Count > 0)
        {
            StatementSyntax reference = insertionIndex < container.Statements.Count
                ? container.Statements[insertionIndex]
                : container.Statements[^1];
            indentation = GetLineIndentation(document, reference);
        }
        else if (container.Syntax is BlockSyntax block)
        {
            int blockColumn = block.OpenBraceToken.GetLocation().GetLineSpan().StartLinePosition.Character;
            indentation = new string(' ', blockColumn + 4);
        }
        else
        {
            SwitchSectionSyntax section = (SwitchSectionSyntax)container.Syntax;
            int labelColumn = section.Labels[0].GetLocation()
                .GetLineSpan().StartLinePosition.Character;
            indentation = new string(' ', labelColumn + 4);
        }

        return new InsertionFormatting(indentation, GetEndOfLine(document));
    }

    private static StatementSyntax FormatInsertedStatement(
        StatementSyntax statement,
        InsertionFormatting formatting)
    {
        if (statement is not (
                IfStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or
                ForEachStatementSyntax or SwitchStatementSyntax))
        {
            return statement
                .WithLeadingTrivia(SyntaxFactory.Whitespace(formatting.Indentation))
                .WithTrailingTrivia(SyntaxFactory.EndOfLine(formatting.EndOfLine));
        }

        string normalized = statement
            .NormalizeWhitespace(indentation: "    ", eol: formatting.EndOfLine)
            .ToFullString();
        string indented = formatting.Indentation + normalized.Replace(
            formatting.EndOfLine,
            formatting.EndOfLine + formatting.Indentation,
            StringComparison.Ordinal) + formatting.EndOfLine;
        StatementSyntax formatted = SyntaxFactory.ParseStatement(
            indented,
            options: CSharpToHirLowerer.ParseOptions,
            consumeFullText: true);
        return formatted.ContainsDiagnostics
            ? throw new InvalidOperationException(
                "A constructed structured statement could not be formatted.")
            : formatted;
    }

    private static StatementSyntax ReindentMovedStatement(
        StatementSyntax statement,
        string destinationIndentation)
    {
        SyntaxTriviaList leading = statement.GetLeadingTrivia();
        var rewritten = new List<SyntaxTrivia>(leading.Count + 1);
        bool atLineStart = true;
        foreach (SyntaxTrivia trivia in leading)
        {
            if (atLineStart && trivia.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                rewritten.Add(SyntaxFactory.Whitespace(destinationIndentation));
                atLineStart = false;
                continue;
            }

            if (atLineStart && !trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                rewritten.Add(SyntaxFactory.Whitespace(destinationIndentation));
            }
            rewritten.Add(trivia);
            atLineStart = trivia.IsKind(SyntaxKind.EndOfLineTrivia);
        }
        if (atLineStart)
        {
            rewritten.Add(SyntaxFactory.Whitespace(destinationIndentation));
        }
        return statement.WithLeadingTrivia(SyntaxFactory.TriviaList(rewritten));
    }

    private static ExpressionSyntax FormatReplacementExpression(
        ExpressionSyntax replacement,
        ExpressionSyntax original) => replacement
        .NormalizeWhitespace()
        .WithoutLeadingTrivia()
        .WithoutTrailingTrivia()
        .WithTriviaFrom(original);

    private static string GetLineIndentation(ParsedDocument document, SyntaxNode node)
    {
        SourceText text = document.SyntaxTree.GetText();
        TextLine line = text.Lines.GetLineFromPosition(node.SpanStart);
        string indentation = text.ToString(TextSpan.FromBounds(line.Start, node.SpanStart));
        return indentation.All(static character => character is ' ' or '\t')
            ? indentation
            : new string(' ', node.GetLocation().GetLineSpan().StartLinePosition.Character);
    }

    private static string GetEndOfLine(ParsedDocument document) =>
        document.Source.Source.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : "\n";

    private static bool TypeNameMatches(string typeName, TypeDescriptor type) =>
        string.Equals(typeName, type.DisplayName, StringComparison.Ordinal) ||
        string.Equals(typeName, type.MetadataName, StringComparison.Ordinal);

    private static bool TryAddReplacement(
        IDictionary<string, List<PendingReplacement>> replacements,
        PendingReplacement replacement,
        out UnifiedDiagnostic? diagnostic)
    {
        if (!replacements.TryGetValue(replacement.DocumentPath, out List<PendingReplacement>? documentEdits))
        {
            documentEdits = [];
            replacements.Add(replacement.DocumentPath, documentEdits);
        }

        PendingReplacement? overlap = documentEdits.FirstOrDefault(
            existing => existing.Original.Span.OverlapsWith(replacement.Original.Span));
        if (overlap is not null)
        {
            diagnostic = new UnifiedDiagnostic(
                SemanticPatchDiagnosticCodes.OverlappingOperations,
                UnifiedDiagnosticSeverity.Error,
                $"Patch operations overlap at source spans {overlap.Original.Span} and " +
                $"{replacement.Original.Span}; split them into sequential patches.",
                RoslynSourceSpan.FromNode(replacement.Original, replacement.DocumentPath));
            return false;
        }

        documentEdits.Add(replacement);
        diagnostic = null;
        return true;
    }

    private static CSharpSourceDocument ReplaceDocument(
        CSharpSourceDocument source,
        ParsedDocument document,
        IReadOnlyDictionary<string, List<PendingReplacement>> replacements,
        IdentitySidecarDocument? effectiveSidecar,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes)
    {
        if (!replacements.TryGetValue(source.LogicalPath, out List<PendingReplacement>? documentReplacements))
        {
            return effectiveSidecar is null
                ? source
                : new CSharpSourceDocument(source.LogicalPath, source.Source, effectiveSidecar);
        }

        IReadOnlyDictionary<TextSpan, PendingReplacement> bySpan = documentReplacements
            .ToDictionary(static replacement => replacement.Original.Span);
        var globalAnnotations = new Dictionary<SyntaxNode, List<SyntaxAnnotation>>();
        var globalPins = new List<PendingPin>();
        if (effectiveSidecar is not null)
        {
            foreach (IdentityEntry entry in effectiveSidecar.Entries)
            {
                if (!hirNodes.TryGetValue(entry.Id, out HirNode? hirNode) ||
                    !string.Equals(hirNode.Span.FilePath, source.LogicalPath, StringComparison.Ordinal) ||
                    !TryFindIdentitySyntax(document, hirNode, out SyntaxNode? syntax) ||
                    documentReplacements.Any(replacement =>
                        replacement.Original.Span.Contains(syntax!.Span)))
                {
                    continue;
                }

                var annotation = new SyntaxAnnotation(IdentityAnnotationKind, entry.Id.Value);
                if (!globalAnnotations.TryGetValue(
                        syntax!,
                        out List<SyntaxAnnotation>? syntaxAnnotations))
                {
                    syntaxAnnotations = [];
                    globalAnnotations.Add(syntax!, syntaxAnnotations);
                }
                syntaxAnnotations.Add(annotation);
                globalPins.Add(new PendingPin(entry.Id, entry.Kind, annotation));
            }
        }

        SyntaxNode[] changedNodes = documentReplacements
            .Select(static replacement => replacement.Original)
            .Concat(globalAnnotations.Keys)
            .ToArray();
        SyntaxNode changedRoot = document.Root.ReplaceNodes(
            changedNodes,
            (original, rewritten) => bySpan.TryGetValue(original.Span, out PendingReplacement? replacement)
                ? replacement.Syntax
                : rewritten.WithAdditionalAnnotations(globalAnnotations[original].ToArray()));
        CSharpIdentityPin[] pins = effectiveSidecar is null
            ? []
            : documentReplacements.SelectMany(static replacement => replacement.Pins)
                .Concat(globalPins)
                .Select(pin => new CSharpIdentityPin(
                    pin.Id,
                    pin.Kind,
                    RoslynSourceSpan.FromNode(
                        changedRoot.GetAnnotatedNodes(pin.Annotation).Single(),
                        source.LogicalPath)))
                .ToArray();
        return new CSharpSourceDocument(
            source.LogicalPath,
            changedRoot.ToFullString(),
            effectiveSidecar,
            pins);
    }

    private static bool TryFindIdentitySyntax(
        ParsedDocument document,
        HirNode hirNode,
        out SyntaxNode? syntax)
    {
        SyntaxNode[] matches = document.Root
            .DescendantNodesAndSelf()
            .Where(candidate => IdentitySyntaxMatches(hirNode, candidate) &&
                RoslynSourceSpan.FromNode(candidate, document.Source.LogicalPath) == hirNode.Span)
            .ToArray();
        syntax = matches.Length == 1 ? matches[0] : null;
        return syntax is not null;
    }

    private static bool IdentitySyntaxMatches(HirNode hirNode, SyntaxNode syntax) => hirNode switch
    {
        HirParameter => syntax is ParameterSyntax,
        HirBlockStatement => syntax is BlockSyntax or SwitchSectionSyntax,
        HirVariableDeclarationStatement => syntax is LocalDeclarationStatementSyntax,
        HirExpressionStatement => syntax is ExpressionStatementSyntax,
        HirIfStatement => syntax is IfStatementSyntax,
        HirWhileStatement => syntax is WhileStatementSyntax,
        HirDoWhileStatement => syntax is DoStatementSyntax,
        HirForStatement => syntax is ForStatementSyntax,
        HirForEachStatement => syntax is ForEachStatementSyntax,
        HirSwitchStatement => syntax is SwitchStatementSyntax,
        HirSwitchSection => syntax is SwitchSectionSyntax,
        HirSwitchLabel => syntax is SwitchLabelSyntax,
        HirConstantPattern => syntax is ConstantPatternSyntax or ExpressionSyntax,
        HirDeclarationPattern => syntax is DeclarationPatternSyntax,
        HirTypePattern => syntax is TypePatternSyntax,
        HirVarPattern => syntax is VarPatternSyntax,
        HirDiscardPattern => syntax is DiscardPatternSyntax,
        HirRelationalPattern => syntax is RelationalPatternSyntax,
        HirLogicalPattern => syntax is BinaryPatternSyntax,
        HirNotPattern => syntax is UnaryPatternSyntax,
        HirParenthesizedPattern => syntax is ParenthesizedPatternSyntax,
        HirPatternVariable => syntax is VariableDesignationSyntax,
        HirForEachVariable => syntax is TypeSyntax,
        HirForVariableDeclaration => syntax is VariableDeclarationSyntax,
        HirForVariableDeclarator => syntax is VariableDeclaratorSyntax,
        HirBreakStatement => syntax is BreakStatementSyntax,
        HirContinueStatement => syntax is ContinueStatementSyntax,
        HirReturnStatement => syntax is ReturnStatementSyntax,
        HirLiteralExpression => syntax is LiteralExpressionSyntax,
        HirIdentifierExpression => syntax is IdentifierNameSyntax,
        HirThisExpression => syntax is ThisExpressionSyntax,
        HirMemberAccessExpression => syntax is MemberAccessExpressionSyntax,
        HirConditionalAccessExpression => syntax is ConditionalAccessExpressionSyntax,
        HirConditionalMemberBindingExpression => syntax is MemberBindingExpressionSyntax,
        HirInvocationExpression => syntax is InvocationExpressionSyntax,
        HirBinaryExpression => syntax is BinaryExpressionSyntax,
        HirUnaryExpression => syntax is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax,
        HirAssignmentExpression => syntax is AssignmentExpressionSyntax,
        HirAwaitExpression => syntax is AwaitExpressionSyntax,
        HirCastExpression => syntax is CastExpressionSyntax,
        HirConditionalExpression => syntax is ConditionalExpressionSyntax,
        _ => false,
    };

    private static OperationPlan? RequireType(
        HirNode node,
        SemanticId expectedTypeId,
        string role,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes)
    {
        if (!boundNodes.TryGetValue(node.Id, out BoundNodeInfo? binding) ||
            binding.TypeId is not SemanticId actualTypeId ||
            actualTypeId != expectedTypeId)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.PreconditionFailed,
                $"Semantic type precondition for {role} failed: expected '{expectedTypeId}', but the " +
                $"current type is '{binding?.TypeId}'.",
                node);
        }

        return null;
    }

    private static bool HasEffects(
        HirExpression expression,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes) => expression
        .DescendantsAndSelf()
        .Any(node => node is HirAssignmentExpression or HirAwaitExpression ||
            node is HirUnaryExpression { Operator: "++" or "--" } ||
            boundNodes.TryGetValue(node.Id, out BoundNodeInfo? binding) &&
            binding.Effects != MethodEffects.None);

    private static bool AreOperatorsCompatible(
        IReadOnlyDictionary<string, string> families,
        string expectedOperator,
        string newOperator) =>
        families.TryGetValue(expectedOperator, out string? expectedFamily) &&
        families.TryGetValue(newOperator, out string? newFamily) &&
        expectedFamily == newFamily;

    private static bool TryFindSyntax<TSyntax>(
        IReadOnlyDictionary<string, ParsedDocument> documents,
        HirNode node,
        out ParsedDocument? document,
        out TSyntax? syntax)
        where TSyntax : SyntaxNode
    {
        if (!documents.TryGetValue(node.Span.FilePath, out document))
        {
            syntax = null;
            return false;
        }

        ParsedDocument resolvedDocument = document;
        syntax = resolvedDocument.Root
            .DescendantNodes()
            .OfType<TSyntax>()
            .SingleOrDefault(candidate => RoslynSourceSpan.FromNode(
                candidate,
                resolvedDocument.Source.LogicalPath) == node.Span);
        return syntax is not null;
    }

    private static bool TryFindEditableStatementContainer(
        IReadOnlyDictionary<string, ParsedDocument> documents,
        HirBlockStatement block,
        out ParsedDocument? document,
        out EditableStatementContainer? container)
    {
        if (TryFindSyntax(documents, block, out document, out BlockSyntax? blockSyntax))
        {
            container = new EditableStatementContainer(blockSyntax!, blockSyntax!.Statements);
            return true;
        }

        if (TryFindSyntax(
                documents,
                block,
                out document,
                out SwitchSectionSyntax? sectionSyntax))
        {
            container = new EditableStatementContainer(
                sectionSyntax!,
                sectionSyntax!.Statements);
            return true;
        }

        container = null;
        return false;
    }

    private static bool TryCreateEditableStatementContainer(
        SyntaxNode syntax,
        out EditableStatementContainer? container)
    {
        container = syntax switch
        {
            BlockSyntax block => new EditableStatementContainer(block, block.Statements),
            SwitchSectionSyntax section => new EditableStatementContainer(
                section,
                section.Statements),
            _ => null,
        };
        return container is not null;
    }

    private static bool TryParseLiteral(
        string text,
        out LiteralExpressionSyntax? literal)
    {
        ExpressionSyntax expression = SyntaxFactory.ParseExpression(
            text,
            options: CSharpToHirLowerer.ParseOptions,
            consumeFullText: true);
        literal = expression as LiteralExpressionSyntax;
        return literal is not null &&
            !literal.ContainsDiagnostics &&
            literal.SpanStart == 0 &&
            literal.Span.Length == text.Length;
    }

    private static bool TryParseBinaryOperator(
        string operatorText,
        out BinaryExpressionSyntax? binary)
    {
        ExpressionSyntax expression = SyntaxFactory.ParseExpression(
            $"left {operatorText} right",
            options: CSharpToHirLowerer.ParseOptions,
            consumeFullText: true);
        binary = expression as BinaryExpressionSyntax;
        return binary is not null &&
            !binary.ContainsDiagnostics &&
            binary.OperatorToken.Text == operatorText;
    }

    private static bool TryParseUnaryOperator(
        string operatorText,
        out PrefixUnaryExpressionSyntax? unary)
    {
        ExpressionSyntax expression = SyntaxFactory.ParseExpression(
            $"{operatorText}operand",
            options: CSharpToHirLowerer.ParseOptions,
            consumeFullText: true);
        unary = expression as PrefixUnaryExpressionSyntax;
        return unary is not null &&
            !unary.ContainsDiagnostics &&
            unary.OperatorToken.Text == operatorText;
    }

    private static string GetIdentityKind(HirExpression expression) => expression switch
    {
        HirLiteralExpression => "literal",
        HirIdentifierExpression => "identifier",
        HirThisExpression => "this",
        HirMemberAccessExpression => "memberAccess",
        HirConditionalAccessExpression => "conditionalAccess",
        HirConditionalMemberBindingExpression => "conditionalMemberBinding",
        HirInvocationExpression => "invocation",
        HirBinaryExpression => "binary",
        HirUnaryExpression => "unary",
        HirAssignmentExpression => "assignment",
        HirAwaitExpression => "await",
        HirCastExpression => "cast",
        HirConditionalExpression => "conditional",
        _ => throw new ArgumentOutOfRangeException(nameof(expression), expression.GetType().Name),
    };

    private static string GetIdentityKind(HirStatement statement) => statement switch
    {
        HirBlockStatement => "block",
        HirVariableDeclarationStatement => "variableDeclaration",
        HirExpressionStatement => "expressionStatement",
        HirIfStatement => "if",
        HirWhileStatement => "while",
        HirDoWhileStatement => "doWhile",
        HirForStatement => "for",
        HirForEachStatement => "forEach",
        HirSwitchStatement => "switch",
        HirBreakStatement => "break",
        HirContinueStatement => "continue",
        HirReturnStatement => "return",
        _ => throw new ArgumentOutOfRangeException(nameof(statement), statement.GetType().Name),
    };

    private static OperationPlan MissingTarget(SemanticId targetId) => new(
        null,
        new UnifiedDiagnostic(
            SemanticPatchDiagnosticCodes.TargetNotFound,
            UnifiedDiagnosticSeverity.Error,
            $"Patch target '{targetId}' was not found in the current HIR.",
            SourceSpan.Unknown("<patch>"),
            targetId));

    private static OperationPlan MissingSyntax(HirNode target, string syntaxKind) => DiagnosticPlan(
        SemanticPatchDiagnosticCodes.TargetNotFound,
        $"Roslyn syntax for {syntaxKind} target '{target.Id}' was not found at its HIR source span.",
        target);

    private static OperationPlan KindMismatch(
        HirNode target,
        SemanticId targetId,
        string expectedKind) => DiagnosticPlan(
        SemanticPatchDiagnosticCodes.TargetKindMismatch,
        $"Patch target '{targetId}' is '{target.GetType().Name}', not {expectedKind}.",
        target);

    private static OperationPlan OperatorPreconditionFailure(
        ChangeOperatorPatchOperation operation,
        string actualOperator,
        HirNode target) => DiagnosticPlan(
        SemanticPatchDiagnosticCodes.PreconditionFailed,
        $"Operator precondition failed: expected '{operation.ExpectedOperator}', but the current operator " +
        $"is '{actualOperator}'.",
        target);

    private static OperationPlan InvalidOperatorFamily(
        ChangeOperatorPatchOperation operation,
        string arity,
        HirNode target) => DiagnosticPlan(
        SemanticPatchDiagnosticCodes.InvalidOperator,
        $"Cannot change {arity} operator '{operation.ExpectedOperator}' to '{operation.NewOperator}': " +
        "both operators must be supported and belong to the same result-preserving operator family.",
        target);

    private static OperationPlan InvalidOperatorSyntax(
        ChangeOperatorPatchOperation operation,
        HirNode target) => DiagnosticPlan(
        SemanticPatchDiagnosticCodes.InvalidOperator,
        $"'{operation.NewOperator}' is not one supported C# operator token for this expression.",
        target);

    private static OperationPlan SiblingFailure(
        HirNode target,
        SemanticId? expectedPreviousSiblingId,
        SemanticId? expectedNextSiblingId,
        SemanticId? expectedParentBlockId = null) => DiagnosticPlan(
        SemanticPatchDiagnosticCodes.SiblingPreconditionFailed,
        $"Statement position precondition failed for '{target.Id}'. Expected parent " +
        $"'{expectedParentBlockId}', previous sibling '{expectedPreviousSiblingId}', and next sibling " +
        $"'{expectedNextSiblingId}'. Re-analyze the block before editing it.",
        target);

    private static OperationPlan DiagnosticPlan(
        string code,
        string message,
        HirNode target) => new(
        null,
        new UnifiedDiagnostic(
            code,
            UnifiedDiagnosticSeverity.Error,
            message,
            target.Span,
            target.Id));

    private static OperationPlan SuccessPlan(
        ParsedDocument document,
        PendingReplacement replacement) =>
        replacement.DocumentPath == document.Source.LogicalPath
            ? new OperationPlan(replacement, null)
            : throw new InvalidOperationException("A planned replacement was assigned to the wrong document.");

    private static CSharpPatchResult Failure(params UnifiedDiagnostic[] diagnostics) =>
        new(null, null, diagnostics);

    private static CSharpPatchResult Failure(IReadOnlyList<UnifiedDiagnostic> diagnostics) =>
        new(null, null, diagnostics);

    private sealed record ParsedDocument(CSharpSourceDocument Source, SyntaxTree SyntaxTree)
    {
        public SyntaxNode Root { get; } = SyntaxTree.GetRoot();
    }

    private sealed record OperationPlan(
        PendingReplacement? Replacement,
        UnifiedDiagnostic? Diagnostic);

    private sealed record StatementBuildResult(
        StatementSyntax? Syntax,
        string? DiagnosticCode,
        string? DiagnosticMessage)
    {
        public static StatementBuildResult Success(StatementSyntax syntax) => new(syntax, null, null);

        public static StatementBuildResult Failure(string code, string message) => new(null, code, message);
    }

    private sealed record ExpressionBuildResult(
        ExpressionSyntax? Syntax,
        TypeDescriptor? Type,
        string? DiagnosticCode,
        string? DiagnosticMessage)
    {
        public static ExpressionBuildResult Success(ExpressionSyntax syntax, TypeDescriptor type) =>
            new(syntax, type, null, null);

        public static ExpressionBuildResult Failure(string code, string message) =>
            new(null, null, code, message);
    }

    private sealed record SwitchSectionBuildResult(
        SwitchSectionSyntax? Syntax,
        string? DiagnosticCode,
        string? DiagnosticMessage)
    {
        public static SwitchSectionBuildResult Success(SwitchSectionSyntax syntax) =>
            new(syntax, null, null);

        public static SwitchSectionBuildResult Failure(string code, string message) =>
            new(null, code, message);
    }

    private sealed record SwitchPatternBuildResult(
        string? Text,
        string? DiagnosticCode,
        string? DiagnosticMessage)
    {
        public static SwitchPatternBuildResult Success(string text) =>
            new(text, null, null);

        public static SwitchPatternBuildResult Failure(string code, string message) =>
            new(null, code, message);
    }

    private readonly record struct AuthoredForLocal(
        string Name,
        TypeDescriptor Type);

    private sealed record PendingReplacement(
        string DocumentPath,
        SyntaxNode Original,
        SyntaxNode Syntax,
        IReadOnlyList<PendingPin> Pins);

    private sealed record PendingPin(
        SemanticId Id,
        string Kind,
        SyntaxAnnotation Annotation);

    private sealed record EditableStatementContainer(
        SyntaxNode Syntax,
        SyntaxList<StatementSyntax> Statements)
    {
        public SyntaxNode WithStatements(SyntaxList<StatementSyntax> statements) => Syntax switch
        {
            BlockSyntax block => block.WithStatements(statements),
            SwitchSectionSyntax section => section.WithStatements(statements),
            _ => throw new InvalidOperationException(
                $"Unsupported editable statement container '{Syntax.GetType().Name}'."),
        };
    }

    private readonly record struct InsertionFormatting(
        string Indentation,
        string EndOfLine);
}
