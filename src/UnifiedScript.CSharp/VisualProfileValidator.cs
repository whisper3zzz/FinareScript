using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnifiedScript.Core;

namespace UnifiedScript.CSharp;

public sealed class VisualProfileValidator
{
    public IReadOnlyList<UnifiedDiagnostic> Validate(SyntaxTree syntaxTree)
    {
        ArgumentNullException.ThrowIfNull(syntaxTree);
        SyntaxNode root = syntaxTree.GetRoot();
        string filePath = string.IsNullOrEmpty(syntaxTree.FilePath) ? "<memory>" : syntaxTree.FilePath;
        var diagnostics = new List<UnifiedDiagnostic>();

        foreach (MethodDeclarationSyntax method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!IsVisualMethod(method))
            {
                continue;
            }

            ValidateMethodDeclaration(method, filePath, diagnostics);
            new VisualMethodWalker(filePath, diagnostics).Visit(method);
        }

        return diagnostics;
    }

    public static bool IsVisualMethod(MethodDeclarationSyntax method) =>
        method.AttributeLists
            .SelectMany(static list => list.Attributes)
            .Any(static attribute => IsVisualAttributeName(attribute.Name));

    private static bool IsVisualAttributeName(NameSyntax name)
    {
        string shortName = name switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            _ => name.ToString().Split('.').Last(),
        };

        return shortName is "Visual" or "VisualAttribute";
    }

    private static void ValidateMethodDeclaration(
        MethodDeclarationSyntax method,
        string filePath,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        if (method.Body is null)
        {
            Add(
                diagnostics,
                VisualProfileDiagnosticCodes.BlockBodyRequired,
                "A [Visual] method must use a block body in profile v0.1.",
                method,
                filePath);
        }

        if (method.TypeParameterList is not null)
        {
            Add(
                diagnostics,
                VisualProfileDiagnosticCodes.GenericVisualMethodNotSupported,
                "Generic [Visual] methods are not supported in profile v0.1.",
                method.TypeParameterList,
                filePath);
        }

        if (method.Modifiers.Any(SyntaxKind.UnsafeKeyword))
        {
            Add(
                diagnostics,
                VisualProfileDiagnosticCodes.UnsafeNotSupported,
                "Unsafe code is not graph-compatible.",
                method,
                filePath);
        }

        if (method.ReturnType is RefTypeSyntax)
        {
            Add(
                diagnostics,
                VisualProfileDiagnosticCodes.RefReturnNotSupported,
                "Ref return values are not supported in profile v0.1.",
                method.ReturnType,
                filePath);
        }

        foreach (ParameterSyntax parameter in method.ParameterList.Parameters)
        {
            if (parameter.Modifiers.Count > 0)
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.ParameterModifierNotSupported,
                    $"Parameter modifier '{parameter.Modifiers}' is not supported in profile v0.1.",
                    parameter,
                    filePath);
            }


            if (parameter.Default is not null)
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.DefaultParameterValueNotSupported,
                    "Default parameter values are not represented in profile v0.1.",
                    parameter.Default,
                    filePath);
            }
        }
    }

    private static void Add(
        ICollection<UnifiedDiagnostic> diagnostics,
        string code,
        string message,
        SyntaxNode node,
        string filePath)
    {
        diagnostics.Add(new UnifiedDiagnostic(
            code,
            UnifiedDiagnosticSeverity.Error,
            message,
            RoslynSourceSpan.FromNode(node, filePath)));
    }

    private sealed class VisualMethodWalker(
        string filePath,
        ICollection<UnifiedDiagnostic> diagnostics) : CSharpSyntaxWalker
    {
        public override void Visit(SyntaxNode? node)
        {
            if (node is null)
            {
                return;
            }

            if (TryReportExplicitlyForbidden(node))
            {
                return;
            }

            // Roslyn models TypeSyntax as an ExpressionSyntax-derived node. Only nodes that are
            // actually inside a declaration/type slot bypass the executable-expression whitelist.
            // A GenericName used as an invocation target therefore remains unsupported rather than
            // losing its type arguments during lowering.
            if (node is TypeSyntax typeSyntax && IsTypePosition(typeSyntax))
            {
                base.Visit(node);
                return;
            }

            if (node is StatementSyntax statement && !IsSupportedStatement(statement))
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.StatementNotSupported,
                    $"Statement '{statement.Kind()}' is not supported in Visual C# profile v0.1.",
                    statement,
                    filePath);
                return;
            }

            if (node is ExpressionSyntax expression && !IsSupportedExpression(expression))
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.ExpressionNotSupported,
                    $"Expression '{expression.Kind()}' is not supported in Visual C# profile v0.1.",
                    expression,
                    filePath);
                return;
            }

            base.Visit(node);
        }

        public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            if (node.Modifiers.Count > 0 ||
                !node.UsingKeyword.IsKind(SyntaxKind.None) ||
                !node.AwaitKeyword.IsKind(SyntaxKind.None))
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.LocalModifierNotSupported,
                    "Const, ref, scoped, and using local declarations are not represented in profile v0.1.",
                    node,
                    filePath);
            }

            if (node.Declaration.Variables.Count != 1)
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.SingleVariableDeclarationRequired,
                    "Declare one local variable per statement in profile v0.1.",
                    node,
                    filePath);
            }

            base.VisitLocalDeclarationStatement(node);
        }

        public override void VisitArgument(ArgumentSyntax node)
        {
            if (node.NameColon is not null)
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.NamedArgumentNotSupported,
                    "Named arguments are not represented in profile v0.1; use positional arguments.",
                    node.NameColon,
                    filePath);
            }

            if (!node.RefKindKeyword.IsKind(SyntaxKind.None))
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.ArgumentModifierNotSupported,
                    $"Argument modifier '{node.RefKindKeyword.ValueText}' is not supported in profile v0.1.",
                    node,
                    filePath);
            }

            base.VisitArgument(node);
        }

        private bool TryReportExplicitlyForbidden(SyntaxNode node)
        {
            (string Code, string Message)? diagnostic = node switch
            {
                IdentifierNameSyntax identifier when IsDynamicType(identifier) => (
                    VisualProfileDiagnosticCodes.DynamicNotSupported,
                    "The 'dynamic' type is not graph-compatible."),
                UnsafeStatementSyntax or PointerTypeSyntax or FunctionPointerTypeSyntax => (
                    VisualProfileDiagnosticCodes.UnsafeNotSupported,
                    "Unsafe and pointer syntax is not graph-compatible."),
                GotoStatementSyntax => (
                    VisualProfileDiagnosticCodes.GotoNotSupported,
                    "Arbitrary goto control flow is not graph-compatible."),
                YieldStatementSyntax => (
                    VisualProfileDiagnosticCodes.YieldNotSupported,
                    "Iterator state machines are not supported in profile v0.1."),
                LocalFunctionStatementSyntax => (
                    VisualProfileDiagnosticCodes.LocalFunctionNotSupported,
                    "Local functions are not supported in profile v0.1."),
                AnonymousFunctionExpressionSyntax => (
                    VisualProfileDiagnosticCodes.LambdaNotSupported,
                    "Lambdas are not supported in profile v0.1."),
                TryStatementSyntax or ThrowStatementSyntax or ThrowExpressionSyntax => (
                    VisualProfileDiagnosticCodes.ExceptionFlowNotSupported,
                    "Exception control flow is not supported in profile v0.1."),
                LockStatementSyntax => (
                    VisualProfileDiagnosticCodes.LockNotSupported,
                    "Lock statements are not supported in Visual gameplay code."),
                QueryExpressionSyntax => (
                    VisualProfileDiagnosticCodes.QueryNotSupported,
                    "LINQ query syntax is not supported in profile v0.1."),
                StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax => (
                    VisualProfileDiagnosticCodes.StackAllocNotSupported,
                    "Stack allocation is not graph-compatible."),
                _ => null,
            };

            if (diagnostic is null)
            {
                return false;
            }

            Add(diagnostics, diagnostic.Value.Code, diagnostic.Value.Message, node, filePath);
            return true;
        }

        private static bool IsDynamicType(IdentifierNameSyntax identifier)
        {
            if (identifier.Identifier.ValueText != "dynamic")
            {
                return false;
            }

            SyntaxNode typeRoot = identifier;
            while (typeRoot.Parent is TypeSyntax)
            {
                typeRoot = typeRoot.Parent;
            }

            return typeRoot.Parent switch
            {
                ParameterSyntax parameter => ReferenceEquals(parameter.Type, typeRoot),
                VariableDeclarationSyntax declaration => ReferenceEquals(declaration.Type, typeRoot),
                MethodDeclarationSyntax method => ReferenceEquals(method.ReturnType, typeRoot),
                CastExpressionSyntax cast => ReferenceEquals(cast.Type, typeRoot),
                ObjectCreationExpressionSyntax creation => ReferenceEquals(creation.Type, typeRoot),
                TypeOfExpressionSyntax typeOf => ReferenceEquals(typeOf.Type, typeRoot),
                DefaultExpressionSyntax defaultExpression => ReferenceEquals(defaultExpression.Type, typeRoot),
                _ => false,
            };
        }

        private static bool IsTypePosition(TypeSyntax typeSyntax)
        {
            foreach (SyntaxNode node in typeSyntax.AncestorsAndSelf())
            {
                bool containsType = node switch
                {
                    AttributeSyntax attribute => attribute.Name.Span.Contains(typeSyntax.Span),
                    ParameterSyntax parameter => parameter.Type?.Span.Contains(typeSyntax.Span) == true,
                    VariableDeclarationSyntax declaration => declaration.Type.Span.Contains(typeSyntax.Span),
                    MethodDeclarationSyntax method => method.ReturnType.Span.Contains(typeSyntax.Span),
                    CastExpressionSyntax cast => cast.Type.Span.Contains(typeSyntax.Span),
                    ObjectCreationExpressionSyntax creation => creation.Type.Span.Contains(typeSyntax.Span),
                    TypeOfExpressionSyntax typeOf => typeOf.Type.Span.Contains(typeSyntax.Span),
                    DefaultExpressionSyntax defaultExpression =>
                        defaultExpression.Type.Span.Contains(typeSyntax.Span),
                    _ => false,
                };

                if (containsType)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSupportedStatement(StatementSyntax statement) => statement.Kind() is
            SyntaxKind.Block or
            SyntaxKind.LocalDeclarationStatement or
            SyntaxKind.ExpressionStatement or
            SyntaxKind.IfStatement or
            SyntaxKind.ReturnStatement or
            SyntaxKind.EmptyStatement;

        private static bool IsSupportedExpression(ExpressionSyntax expression) => expression is
            LiteralExpressionSyntax or
            IdentifierNameSyntax or
            ThisExpressionSyntax or
            MemberAccessExpressionSyntax or
            InvocationExpressionSyntax or
            BinaryExpressionSyntax or
            PrefixUnaryExpressionSyntax or
            ParenthesizedExpressionSyntax or
            AssignmentExpressionSyntax or
            AwaitExpressionSyntax or
            CastExpressionSyntax;
    }
}
