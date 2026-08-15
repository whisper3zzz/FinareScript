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
        .Any(node => boundNodes.TryGetValue(node.Id, out BoundNodeInfo? binding) &&
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
}
