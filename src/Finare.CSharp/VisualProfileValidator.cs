using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Finare.Core;

namespace Finare.CSharp;

public sealed class VisualProfileValidator
{
    private const string ProfileName = VisualCSharpProfileContract.CurrentDisplayName;

    private const string OpaqueEscapeHint =
        " Keep this implementation source-only by removing [Visual], then call it through an " +
        "opaque node with graph-safe parameters and return type.";

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

            diagnostics.AddRange(ValidateMethod(method));
        }

        return diagnostics;
    }

    public IReadOnlyList<UnifiedDiagnostic> ValidateMethod(MethodDeclarationSyntax method)
    {
        ArgumentNullException.ThrowIfNull(method);
        string filePath = string.IsNullOrEmpty(method.SyntaxTree.FilePath)
            ? "<memory>"
            : method.SyntaxTree.FilePath;
        var diagnostics = new List<UnifiedDiagnostic>();
        if (!IsVisualMethod(method))
        {
            return diagnostics;
        }

        ValidateMethodDeclaration(method, filePath, diagnostics);
        ValidateBlueprintOverrideDeclaration(method, filePath, diagnostics);
        new VisualMethodWalker(filePath, diagnostics).Visit(method);
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

        return shortName is "Visual" or "VisualAttribute" or
            "UnrealBlueprintEvent" or "UnrealBlueprintEventAttribute" or
            "UnrealBlueprintOverride" or "UnrealBlueprintOverrideAttribute" or
            "UnrealInputAction" or "UnrealInputActionAttribute" or
            "UnrealInterfaceFunction" or "UnrealInterfaceFunctionAttribute" or
            "UnrealRpc" or "UnrealRpcAttribute" or "UnrealRepNotify" or "UnrealRepNotifyAttribute";
    }

    private static void ValidateBlueprintOverrideDeclaration(
        MethodDeclarationSyntax method,
        string filePath,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        AttributeSyntax[] overrides = GetBlueprintOverrideAttributes(method).ToArray();
        if (overrides.Length == 0)
        {
            return;
        }

        if (!method.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            method.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            Add(
                diagnostics,
                VisualProfileDiagnosticCodes.InvalidBlueprintOverride,
                "A Blueprint override must be a public instance method.",
                method,
                filePath);
        }

        string[] conflictingAttributes =
        [
            "UnrealBlueprintEvent",
            "UnrealBlueprintEventAttribute",
            "UnrealInputAction",
            "UnrealInputActionAttribute",
            "UnrealEvent",
            "UnrealEventAttribute",
            "UnrealInterfaceFunction",
            "UnrealInterfaceFunctionAttribute",
            "UnrealRpc",
            "UnrealRpcAttribute",
            "UnrealRepNotify",
            "UnrealRepNotifyAttribute",
        ];
        if (method.AttributeLists
                .SelectMany(static list => list.Attributes)
                .Select(static attribute => attribute.Name.ToString().Split('.').Last())
                .Any(name => conflictingAttributes.Contains(name, StringComparer.Ordinal)))
        {
            Add(
                diagnostics,
                VisualProfileDiagnosticCodes.InvalidBlueprintOverride,
                "A Blueprint override cannot be combined with BlueprintEvent, InputAction, Delegate, Interface, Rpc, or RepNotify bindings.",
                method,
                filePath);
        }
    }

    private static IEnumerable<AttributeSyntax> GetBlueprintOverrideAttributes(
        MethodDeclarationSyntax method) => method.AttributeLists
        .SelectMany(static list => list.Attributes)
        .Where(static attribute => IsAttributeNamed(
            attribute,
            "UnrealBlueprintOverride",
            "UnrealBlueprintOverrideAttribute"));

    private static bool IsAttributeNamed(AttributeSyntax attribute, params string[] names)
    {
        string name = attribute.Name.ToString().Split('.').Last();
        return names.Contains(name, StringComparer.Ordinal);
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
                $"A [Visual] method must use a block body in {ProfileName}.",
                method,
                filePath);
        }

        if (method.TypeParameterList is not null)
        {
            Add(
                diagnostics,
                VisualProfileDiagnosticCodes.GenericVisualMethodNotSupported,
                $"Generic [Visual] methods are not supported in {ProfileName}.",
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
                $"Ref return values are not supported in {ProfileName}.",
                method.ReturnType,
                filePath);
        }

        foreach (ParameterSyntax parameter in method.ParameterList.Parameters)
        {
            bool reviewedOutputDirection = parameter.Modifiers.Count == 1 &&
                (parameter.Modifiers[0].IsKind(SyntaxKind.OutKeyword) ||
                 parameter.Modifiers[0].IsKind(SyntaxKind.RefKeyword));
            if (parameter.Modifiers.Count > 0 && !reviewedOutputDirection)
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.ParameterModifierNotSupported,
                    $"Parameter modifier '{parameter.Modifiers}' is not supported in {ProfileName}; " +
                    "the reviewed Blueprint direction matrix accepts only out and ref.",
                    parameter,
                    filePath);
            }


            if (parameter.Default is not null)
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.DefaultParameterValueNotSupported,
                    $"Default parameter values are not represented in {ProfileName}.",
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
            SupportsOpaqueEscape(code) ? message + OpaqueEscapeHint : message,
            RoslynSourceSpan.FromNode(node, filePath)));
    }

    private static bool SupportsOpaqueEscape(string code) => code is not (
        VisualProfileDiagnosticCodes.LoopControlTargetRequired or
        VisualProfileDiagnosticCodes.BreakTargetRequired);

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

            // Method attributes describe declaration/entry semantics; they are
            // compiled by Roslyn but are not executable nodes in the visual body.
            if (node is AttributeListSyntax)
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
                    $"Statement '{statement.Kind()}' is not supported in {ProfileName}.",
                    statement,
                    filePath);
                return;
            }

            if (node is PatternSyntax pattern && !IsSupportedPattern(pattern))
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.PatternNotSupported,
                    $"Pattern '{pattern.Kind()}' is not supported in {ProfileName}. " +
                    "Use constant, declaration, type, var, discard, relational, not, and/or, " +
                    "or parenthesized patterns.",
                    pattern,
                    filePath);
                return;
            }

            if (node is VariableDesignationSyntax designation && designation is not (
                    SingleVariableDesignationSyntax or DiscardDesignationSyntax))
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.PatternNotSupported,
                    "Only a single variable or discard designation is supported in a Visual pattern.",
                    designation,
                    filePath);
                return;
            }

            if (node is ExpressionSyntax expression && !IsSupportedExpression(expression))
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.ExpressionNotSupported,
                    $"Expression '{expression.Kind()}' is not supported in {ProfileName}.",
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
                    $"Const, ref, scoped, and using local declarations are not represented in " +
                    $"{ProfileName}.",
                    node,
                    filePath);
            }

            if (node.Declaration.Variables.Count != 1)
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.SingleVariableDeclarationRequired,
                    $"Declare one local variable per statement in {ProfileName}.",
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
                    $"Named arguments are not represented in {ProfileName}; use positional arguments.",
                    node.NameColon,
                    filePath);
            }

            if (!node.RefKindKeyword.IsKind(SyntaxKind.None))
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.ArgumentModifierNotSupported,
                    $"Argument modifier '{node.RefKindKeyword.ValueText}' is not supported in " +
                    $"{ProfileName}.",
                    node,
                    filePath);
            }

            base.VisitArgument(node);
        }

        public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
        {
            if (!node.IsKind(SyntaxKind.PostIncrementExpression) &&
                !node.IsKind(SyntaxKind.PostDecrementExpression))
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.ExpressionNotSupported,
                    $"Postfix expression '{node.Kind()}' is not supported in {ProfileName}.",
                    node,
                    filePath);
                return;
            }

            base.VisitPostfixUnaryExpression(node);
        }

        public override void VisitBreakStatement(BreakStatementSyntax node)
        {
            if (!node.Ancestors().Any(static ancestor =>
                    ancestor is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or
                        ForEachStatementSyntax or SwitchStatementSyntax))
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.BreakTargetRequired,
                    $"'break' requires an enclosing loop or switch in {ProfileName}.",
                    node,
                    filePath);
            }

            base.VisitBreakStatement(node);
        }

        public override void VisitContinueStatement(ContinueStatementSyntax node)
        {
            ValidateLoopControlTarget(node);
            base.VisitContinueStatement(node);
        }

        public override void VisitForEachStatement(ForEachStatementSyntax node)
        {
            if (!node.AwaitKeyword.IsKind(SyntaxKind.None) && node.Type is RefTypeSyntax)
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.StatementNotSupported,
                    "Await foreach cannot carry borrowed ref iteration across suspension points.",
                    node,
                    filePath);
                return;
            }

            if (node.Type is RefTypeSyntax refType &&
                !refType.ReadOnlyKeyword.IsKind(SyntaxKind.ReadOnlyKeyword))
            {
                Add(
                    diagnostics,
                    VisualProfileDiagnosticCodes.StatementNotSupported,
                    "Mutable ref foreach requires writable alias edges and is not yet represented " +
                    $"in {ProfileName}. Use ref readonly for borrowed iteration.",
                    node,
                    filePath);
                return;
            }

            base.VisitForEachStatement(node);
        }

        private void ValidateLoopControlTarget(StatementSyntax node)
        {
            if (node.Ancestors().Any(static ancestor =>
                    ancestor is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or
                        ForEachStatementSyntax))
            {
                return;
            }

            Add(
                diagnostics,
                VisualProfileDiagnosticCodes.LoopControlTargetRequired,
                $"'{node.Kind()}' requires an enclosing while, do, for, or foreach loop in " +
                $"{ProfileName}.",
                node,
                filePath);
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
                    $"Iterator state machines are not supported in {ProfileName}."),
                LocalFunctionStatementSyntax => (
                    VisualProfileDiagnosticCodes.LocalFunctionNotSupported,
                    $"Local functions are not supported in {ProfileName}."),
                AnonymousFunctionExpressionSyntax => (
                    VisualProfileDiagnosticCodes.LambdaNotSupported,
                    $"Lambdas are not supported in {ProfileName}."),
                TryStatementSyntax or ThrowStatementSyntax or ThrowExpressionSyntax => (
                    VisualProfileDiagnosticCodes.ExceptionFlowNotSupported,
                    $"Exception control flow is not supported in {ProfileName}."),
                LockStatementSyntax => (
                    VisualProfileDiagnosticCodes.LockNotSupported,
                    "Lock statements are not supported in Visual gameplay code."),
                QueryExpressionSyntax => (
                    VisualProfileDiagnosticCodes.QueryNotSupported,
                    $"LINQ query syntax is not supported in {ProfileName}."),
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
                    ForEachStatementSyntax forEach => forEach.Type.Span.Contains(typeSyntax.Span),
                    DeclarationPatternSyntax declarationPattern =>
                        declarationPattern.Type.Span.Contains(typeSyntax.Span),
                    TypePatternSyntax typePattern => typePattern.Type.Span.Contains(typeSyntax.Span),
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
            SyntaxKind.WhileStatement or
            SyntaxKind.DoStatement or
            SyntaxKind.ForStatement or
            SyntaxKind.ForEachStatement or
            SyntaxKind.SwitchStatement or
            SyntaxKind.BreakStatement or
            SyntaxKind.ContinueStatement or
            SyntaxKind.ReturnStatement or
            SyntaxKind.EmptyStatement;

        private static bool IsSupportedPattern(PatternSyntax pattern) => pattern is
            ConstantPatternSyntax or
            DeclarationPatternSyntax or
            TypePatternSyntax or
            VarPatternSyntax or
            DiscardPatternSyntax or
            RelationalPatternSyntax or
            BinaryPatternSyntax or
            UnaryPatternSyntax or
            ParenthesizedPatternSyntax;

        private static bool IsSupportedExpression(ExpressionSyntax expression) => expression is
            LiteralExpressionSyntax or
            IdentifierNameSyntax or
            ThisExpressionSyntax or
            MemberAccessExpressionSyntax or
            ConditionalAccessExpressionSyntax or
            MemberBindingExpressionSyntax or
            InvocationExpressionSyntax or
            BinaryExpressionSyntax or
            PrefixUnaryExpressionSyntax or
            PostfixUnaryExpressionSyntax or
            ParenthesizedExpressionSyntax or
            AssignmentExpressionSyntax or
            AwaitExpressionSyntax or
            CastExpressionSyntax or
            ConditionalExpressionSyntax or
            OmittedArraySizeExpressionSyntax;
    }
}
