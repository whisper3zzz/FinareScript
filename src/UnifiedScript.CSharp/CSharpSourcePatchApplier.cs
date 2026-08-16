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
                InsertStatementPatchOperation insertStatement => PlanStatementInsertion(
                    insertStatement,
                    hirNodes,
                    patchIndex,
                    types,
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
                effectiveSidecars.GetValueOrDefault(source.LogicalPath)))
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

        return new CSharpPatchResult(patchedRequest, patchedAnalysis, patchedAnalysis.Diagnostics);
    }

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

    private static OperationPlan PlanStatementInsertion(
        InsertStatementPatchOperation operation,
        IReadOnlyDictionary<SemanticId, HirNode> hirNodes,
        HirPatchIndex patchIndex,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
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

        if (operation.NewStatement is not LocalLiteralDeclarationPatchStatement local)
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                $"Inserted statement payload '{operation.NewStatement.GetType().Name}' is not supported.",
                block);
        }

        if (!types.TryGetValue(local.ExpectedTypeId, out TypeDescriptor? expectedType) ||
            !expectedType.IsGraphSafe ||
            !TypeNameMatches(local.TypeName, expectedType))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.TypeMismatch,
                $"Inserted local type '{local.TypeName}' does not match graph-safe semantic type " +
                $"'{local.ExpectedTypeId}'. The type must already exist in the bound compilation.",
                block);
        }

        if (HasNameConflict(blockLocation!.Method, local.Name))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.NameConflict,
                $"A parameter or local named '{local.Name}' already exists in method " +
                $"'{blockLocation.Method.Name}'.",
                block);
        }

        if (!TryCreateLocalLiteralStatement(local, out LocalDeclarationStatementSyntax? newStatement))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.InvalidStatement,
                "The local-literal payload could not be represented as one C# local declaration.",
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
            AnnotateStatements(syntax, block.Statements);
        InsertionFormatting formatting = GetInsertionFormatting(document!, syntax, insertionIndex);
        newStatement = newStatement!
            .WithLeadingTrivia(SyntaxFactory.Whitespace(formatting.Indentation))
            .WithTrailingTrivia(SyntaxFactory.EndOfLine(formatting.EndOfLine));
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
                expressionLocation.ContainingStatement.Id))
        {
            return DiagnosticPlan(
                SemanticPatchDiagnosticCodes.ValueSourceNotAvailable,
                $"Local '{sourceDefinition.Name}' does not lexically dominate expression '{expression.Id}' " +
                "in the same method.",
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
        IdentitySidecarDocument? effectiveSidecar)
    {
        if (!replacements.TryGetValue(source.LogicalPath, out List<PendingReplacement>? documentReplacements))
        {
            return effectiveSidecar is null
                ? source
                : new CSharpSourceDocument(source.LogicalPath, source.Source, effectiveSidecar);
        }

        IReadOnlyDictionary<TextSpan, PendingReplacement> bySpan = documentReplacements
            .ToDictionary(static replacement => replacement.Original.Span);
        SyntaxNode changedRoot = document.Root.ReplaceNodes(
            documentReplacements.Select(static replacement => replacement.Original),
            (original, _) => bySpan[original.Span].Syntax);
        CSharpIdentityPin[] pins = effectiveSidecar is null
            ? []
            : documentReplacements
                .SelectMany(static replacement => replacement.Pins)
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
        HirInvocationExpression => "invocation",
        HirBinaryExpression => "binary",
        HirUnaryExpression => "unary",
        HirAssignmentExpression => "assignment",
        HirAwaitExpression => "await",
        HirCastExpression => "cast",
        _ => throw new ArgumentOutOfRangeException(nameof(expression), expression.GetType().Name),
    };

    private static string GetIdentityKind(HirStatement statement) => statement switch
    {
        HirBlockStatement => "block",
        HirVariableDeclarationStatement => "variableDeclaration",
        HirExpressionStatement => "expressionStatement",
        HirIfStatement => "if",
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
