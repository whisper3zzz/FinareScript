using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
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
        var documents = request.Sources.ToDictionary(
            static source => source.LogicalPath,
            static source => new ParsedDocument(
                source,
                CSharpSyntaxTree.ParseText(
                    source.Source,
                    CSharpToHirLowerer.ParseOptions,
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
            request.EffectCatalog);
        CSharpAnalysisResult patchedAnalysis = pipeline.Analyze(patchedRequest);
        if (!patchedAnalysis.Succeeded)
        {
            return Failure(patchedAnalysis.Diagnostics);
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
            .Select(static source => CSharpSyntaxTree.ParseText(
                source.Source,
                CSharpToHirLowerer.ParseOptions,
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
            BreakPatchStatement => BuildLoopTransferStatement(
                isContinue: false,
                blockLocation!,
                insertionIndex,
                patchIndex,
                graphSafetyIndex),
            ContinuePatchStatement => BuildLoopTransferStatement(
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

        if (!TryFindSyntax(documents, block, out ParsedDocument? document, out BlockSyntax? syntax) ||
            syntax!.Statements.Count != block.Statements.Count)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                "Statement insertion currently requires an explicit C# block whose statement list " +
                "matches the analyzed HIR block.",
                block);
        }

        (List<StatementSyntax> annotatedStatements, List<PendingPin> pins) =
            AnnotateStatementTrees(document!, syntax, block.Statements);
        InsertionFormatting formatting = GetInsertionFormatting(document!, syntax, insertionIndex);
        StatementSyntax newStatement = FormatInsertedStatement(statementResult.Syntax, formatting);
        annotatedStatements.Insert(insertionIndex, newStatement);
        var blockAnnotation = new SyntaxAnnotation(IdentityAnnotationKind, block.Id.Value);
        BlockSyntax replacement = syntax
            .WithStatements(SyntaxFactory.List(annotatedStatements))
            .WithAdditionalAnnotations(blockAnnotation);
        pins.Add(new PendingPin(block.Id, "block", blockAnnotation));
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            syntax,
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
        SemanticId? forHeaderLoopId = null)
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

        ExpressionBuildResult replacement = BuildPatchExpression(
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
        if (!TryFindSyntax(
                documents,
                parentBlock,
                out ParsedDocument? document,
                out BlockSyntax? syntax) ||
            syntax!.Statements.Count != parentBlock.Statements.Count ||
            RoslynSourceSpan.FromNode(
                syntax.Statements[location.Index],
                document!.Source.LogicalPath) != statement.Span)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                "Statement deletion currently requires an explicit C# block whose statement list " +
                "matches the analyzed HIR block.",
                statement);
        }

        BlockSyntax removed = RemoveStatementPreservingComments(
            document!,
            syntax,
            location.Index);
        HirStatement[] survivingHir = parentBlock.Statements
            .Where((_, index) => index != location.Index)
            .ToArray();
        (List<StatementSyntax> annotatedStatements, List<PendingPin> pins) =
            AnnotateStatements(removed, survivingHir);
        var blockAnnotation = new SyntaxAnnotation(IdentityAnnotationKind, parentBlock.Id.Value);
        BlockSyntax replacement = removed
            .WithStatements(SyntaxFactory.List(annotatedStatements))
            .WithAdditionalAnnotations(blockAnnotation);
        pins.Add(new PendingPin(parentBlock.Id, "block", blockAnnotation));
        return SuccessPlan(document!, new PendingReplacement(
            document!.Source.LogicalPath,
            syntax,
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

    private static BlockSyntax RemoveStatementPreservingComments(
        ParsedDocument document,
        BlockSyntax block,
        int statementIndex)
    {
        StatementSyntax target = block.Statements[statementIndex];
        SyntaxTriviaList preservedTrivia = GetPreservedDeletionTrivia(document, target);
        SyntaxList<StatementSyntax> statements = block.Statements.RemoveAt(statementIndex);
        if (preservedTrivia.Count == 0)
        {
            return block.WithStatements(statements);
        }

        if (statementIndex < statements.Count)
        {
            StatementSyntax next = statements[statementIndex];
            statements = statements.Replace(
                next,
                next.WithLeadingTrivia(preservedTrivia.AddRange(next.GetLeadingTrivia())));
            return block.WithStatements(statements);
        }

        return block
            .WithStatements(statements)
            .WithCloseBraceToken(block.CloseBraceToken.WithLeadingTrivia(
                preservedTrivia.AddRange(block.CloseBraceToken.LeadingTrivia)));
    }

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
        IReadOnlyList<HirStatement> hirStatements)
    {
        if (block.Statements.Count != hirStatements.Count)
        {
            throw new ArgumentException("Roslyn and HIR statement counts must match.", nameof(hirStatements));
        }

        var statements = new List<StatementSyntax>(hirStatements.Count);
        var pins = new List<PendingPin>(hirStatements.Count + 1);
        for (int index = 0; index < hirStatements.Count; index++)
        {
            HirStatement hirStatement = hirStatements[index];
            var annotation = new SyntaxAnnotation(IdentityAnnotationKind, hirStatement.Id.Value);
            statements.Add(block.Statements[index].WithAdditionalAnnotations(annotation));
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
        IReadOnlyList<HirStatement> hirStatements)
    {
        if (block.Statements.Count != hirStatements.Count)
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
                    block.Statements[index],
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
        out int index)
    {
        if (expectedPreviousSiblingId is null && expectedNextSiblingId is null)
        {
            index = 0;
            return block.Statements.Count == 0;
        }

        if (expectedPreviousSiblingId is null)
        {
            index = 0;
            return block.Statements.Count > 0 && block.Statements[0].Id == expectedNextSiblingId;
        }

        if (expectedNextSiblingId is null)
        {
            index = block.Statements.Count;
            return block.Statements.Count > 0 && block.Statements[^1].Id == expectedPreviousSiblingId;
        }

        int previousIndex = block.Statements
            .Select((statement, statementIndex) => (statement, statementIndex))
            .Where(item => item.statement.Id == expectedPreviousSiblingId)
            .Select(static item => item.statementIndex)
            .DefaultIfEmpty(-1)
            .Single();
        index = previousIndex + 1;
        return previousIndex >= 0 &&
            index < block.Statements.Count &&
            block.Statements[index].Id == expectedNextSiblingId;
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

    private static StatementBuildResult BuildLoopTransferStatement(
        bool isContinue,
        HirPatchIndex.BlockLocation blockLocation,
        int insertionIndex,
        HirPatchIndex patchIndex,
        GraphPatchSafetyIndex graphSafetyIndex)
    {
        if (!patchIndex.TryGetNearestEnclosingLoop(blockLocation.Block.Id, out HirStatement? loop))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                $"A graph-authored {(isContinue ? "continue" : "break")} requires an enclosing " +
                "while, do, or for loop in the current HIR.");
        }

        if (!graphSafetyIndex.IsInsertionReachable(blockLocation, insertionIndex))
        {
            return StatementBuildResult.Failure(
                SemanticPatchDiagnosticCodes.UnreachableInsertion,
                $"A graph-authored {(isContinue ? "continue" : "break")} requires a reachable " +
                $"insertion gap inside loop '{loop!.Id}'.");
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
        int insertionIndex)
    {
        string indentation;
        if (block.Statements.Count > 0)
        {
            StatementSyntax reference = insertionIndex < block.Statements.Count
                ? block.Statements[insertionIndex]
                : block.Statements[^1];
            indentation = GetLineIndentation(document, reference);
        }
        else
        {
            int blockColumn = block.OpenBraceToken.GetLocation().GetLineSpan().StartLinePosition.Character;
            indentation = new string(' ', blockColumn + 4);
        }

        return new InsertionFormatting(indentation, GetEndOfLine(document));
    }

    private static StatementSyntax FormatInsertedStatement(
        StatementSyntax statement,
        InsertionFormatting formatting)
    {
        if (statement is not (
                IfStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax))
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
        var globalAnnotations = new Dictionary<SyntaxNode, SyntaxAnnotation>();
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
                globalAnnotations.Add(syntax!, annotation);
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
                : rewritten.WithAdditionalAnnotations(globalAnnotations[original]));
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
        HirBlockStatement => syntax is BlockSyntax,
        HirVariableDeclarationStatement => syntax is LocalDeclarationStatementSyntax,
        HirExpressionStatement => syntax is ExpressionStatementSyntax,
        HirIfStatement => syntax is IfStatementSyntax,
        HirWhileStatement => syntax is WhileStatementSyntax,
        HirDoWhileStatement => syntax is DoStatementSyntax,
        HirForStatement => syntax is ForStatementSyntax,
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

    private readonly record struct InsertionFormatting(
        string Indentation,
        string EndOfLine);
}
