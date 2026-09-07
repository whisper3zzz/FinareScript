using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;

namespace UnifiedScript.CSharp;

public sealed class CSharpToHirLowerer
{
    internal static readonly CSharpParseOptions ParseOptions = new(
        LanguageVersion.Preview,
        DocumentationMode.Parse,
        SourceCodeKind.Regular);

    private readonly VisualProfileValidator profileValidator = new();

    public CSharpLoweringResult Lower(string source, string filePath = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(source);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, ParseOptions, filePath);
        return Lower(syntaxTree);
    }

    internal CSharpLoweringResult Lower(SyntaxTree syntaxTree)
    {
        ArgumentNullException.ThrowIfNull(syntaxTree);
        string filePath = string.IsNullOrEmpty(syntaxTree.FilePath) ? "<memory>" : syntaxTree.FilePath;
        SyntaxNode root = syntaxTree.GetRoot();
        var diagnostics = syntaxTree.GetDiagnostics()
            .Select(diagnostic => FromRoslynDiagnostic(diagnostic, filePath))
            .ToList();
        diagnostics.AddRange(profileValidator.Validate(syntaxTree));

        if (diagnostics.Any(static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error))
        {
            return new CSharpLoweringResult(null, diagnostics);
        }

        IReadOnlyList<HirTypeDeclaration> types = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Select(type => LowerType(type, filePath))
            .Where(static type => type.Methods.Count > 0)
            .ToArray();

        var unit = new HirCompilationUnit(
            CSharpSemanticIds.ForSymbol("compilation", NormalizePath(filePath)),
            RoslynSourceSpan.FromNode(root, filePath),
            types);
        return new CSharpLoweringResult(unit, diagnostics);
    }

    private static HirTypeDeclaration LowerType(TypeDeclarationSyntax type, string filePath)
    {
        string @namespace = GetNamespace(type);
        string nestedName = GetNestedTypeName(type);
        string qualifiedName = string.IsNullOrEmpty(@namespace) ? nestedName : $"{@namespace}.{nestedName}";
        IReadOnlyList<HirMethodDeclaration> methods = type.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(VisualProfileValidator.IsVisualMethod)
            .Select(method => LowerMethod(method, filePath, qualifiedName))
            .ToArray();

        return new HirTypeDeclaration(
            CSharpSemanticIds.ForSymbol("type", qualifiedName),
            RoslynSourceSpan.FromNode(type, filePath),
            @namespace,
            nestedName,
            methods);
    }

    private static HirMethodDeclaration LowerMethod(
        MethodDeclarationSyntax method,
        string filePath,
        string qualifiedTypeName)
    {
        string methodKey = VisualProfileInspector.GetMethodSignature(qualifiedTypeName, method);
        SemanticId methodId = CSharpSemanticIds.ForSymbol("method", methodKey);
        IReadOnlyList<HirParameter> parameters = method.ParameterList.Parameters
            .Select(parameter => new HirParameter(
                CSharpSemanticIds.ForNode(filePath, methodKey, parameter, $"parameter:{parameter.Identifier.ValueText}"),
                RoslynSourceSpan.FromNode(parameter, filePath),
                parameter.Identifier.ValueText,
                parameter.Type?.ToString() ?? "unknown",
                parameter.Modifiers.Any(SyntaxKind.OutKeyword)
                    ? HirParameterDirection.Output
                    : parameter.Modifiers.Any(SyntaxKind.RefKeyword)
                        ? HirParameterDirection.InputOutput
                        : HirParameterDirection.Input))
            .ToArray();

        HirBlockStatement body = LowerBlock(method.Body!, filePath, methodKey);
        return new HirMethodDeclaration(
            methodId,
            RoslynSourceSpan.FromNode(method, filePath),
            method.Identifier.ValueText,
            method.ReturnType.ToString(),
            true,
            method.Modifiers.Any(SyntaxKind.PublicKeyword),
            method.Modifiers.Any(SyntaxKind.StaticKeyword),
            method.Modifiers.Any(SyntaxKind.AsyncKeyword),
            parameters,
            body)
        {
            Attributes = LowerAttributes(method),
        };
    }

    private static IReadOnlyList<HirAttribute>? LowerAttributes(MethodDeclarationSyntax method) =>
        method.AttributeLists
            .SelectMany(static list => list.Attributes)
            .Where(static attribute =>
            {
                string name = attribute.Name.ToString();
                int separator = name.LastIndexOf('.');
                name = separator >= 0 ? name[(separator + 1)..] : name;
                return name is "UnrealEvent" or "UnrealEventAttribute" or
                    "UnrealBlueprintEvent" or "UnrealBlueprintEventAttribute" or
                    "UnrealBlueprintOverride" or "UnrealBlueprintOverrideAttribute" or
                    "UnrealInputAction" or "UnrealInputActionAttribute" or
                    "UnrealInterfaceFunction" or "UnrealInterfaceFunctionAttribute" or
                    "UnrealRpc" or "UnrealRpcAttribute" or "UnrealRepNotify" or "UnrealRepNotifyAttribute";
            })
            .Select(attribute =>
            {
                string name = attribute.Name.ToString();
                int separator = name.LastIndexOf('.');
                name = separator >= 0 ? name[(separator + 1)..] : name;
                if (name.EndsWith("Attribute", StringComparison.Ordinal))
                {
                    name = name[..^"Attribute".Length];
                }

                var positional = new List<string>();
                var named = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (AttributeArgumentSyntax argument in
                         attribute.ArgumentList?.Arguments ?? default)
                {
                    string value = NormalizeAttributeArgument(argument.Expression);
                    string? argumentName = argument.NameEquals?.Name.Identifier.ValueText ??
                        argument.NameColon?.Name.Identifier.ValueText;
                    if (argumentName is null)
                    {
                        positional.Add(value);
                    }
                    else
                    {
                        named[argumentName] = value;
                    }
                }
                return new HirAttribute(name, positional, named);
            })
            .ToArray() is { Length: > 0 } attributes ? attributes : null;

    private static string NormalizeAttributeArgument(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) =>
            literal.Token.ValueText,
        TypeOfExpressionSyntax typeOf => typeOf.Type.ToString(),
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => expression.ToString(),
    };

    private static HirBlockStatement LowerBlock(BlockSyntax block, string filePath, string ownerKey) =>
        new(
            CSharpSemanticIds.ForNode(filePath, ownerKey, block),
            RoslynSourceSpan.FromNode(block, filePath),
            block.Statements.Select(statement => LowerStatement(statement, filePath, ownerKey)).ToArray());

    private static HirBlockStatement LowerAsBlock(StatementSyntax statement, string filePath, string ownerKey)
    {
        if (statement is BlockSyntax block)
        {
            return LowerBlock(block, filePath, ownerKey);
        }

        return new HirBlockStatement(
            CSharpSemanticIds.ForNode(filePath, ownerKey, statement, "implicitBlock"),
            RoslynSourceSpan.FromNode(statement, filePath),
            [LowerStatement(statement, filePath, ownerKey)]);
    }

    private static HirStatement LowerStatement(StatementSyntax statement, string filePath, string ownerKey) =>
        statement switch
        {
            BlockSyntax block => LowerBlock(block, filePath, ownerKey),
            LocalDeclarationStatementSyntax local => LowerLocal(local, filePath, ownerKey),
            ExpressionStatementSyntax expression => new HirExpressionStatement(
                CSharpSemanticIds.ForNode(filePath, ownerKey, expression),
                RoslynSourceSpan.FromNode(expression, filePath),
                LowerExpression(expression.Expression, filePath, ownerKey)),
            IfStatementSyntax ifStatement => new HirIfStatement(
                CSharpSemanticIds.ForNode(filePath, ownerKey, ifStatement),
                RoslynSourceSpan.FromNode(ifStatement, filePath),
                LowerExpression(ifStatement.Condition, filePath, ownerKey),
                LowerAsBlock(ifStatement.Statement, filePath, ownerKey),
                ifStatement.Else is null ? null : LowerAsBlock(ifStatement.Else.Statement, filePath, ownerKey)),
            WhileStatementSyntax whileStatement => new HirWhileStatement(
                CSharpSemanticIds.ForNode(filePath, ownerKey, whileStatement),
                RoslynSourceSpan.FromNode(whileStatement, filePath),
                LowerExpression(whileStatement.Condition, filePath, ownerKey),
                LowerAsBlock(whileStatement.Statement, filePath, ownerKey)),
            DoStatementSyntax doStatement => new HirDoWhileStatement(
                CSharpSemanticIds.ForNode(filePath, ownerKey, doStatement),
                RoslynSourceSpan.FromNode(doStatement, filePath),
                LowerAsBlock(doStatement.Statement, filePath, ownerKey),
                LowerExpression(doStatement.Condition, filePath, ownerKey)),
            ForStatementSyntax forStatement => LowerFor(forStatement, filePath, ownerKey),
            ForEachStatementSyntax forEachStatement => LowerForEach(
                forEachStatement,
                filePath,
                ownerKey),
            SwitchStatementSyntax switchStatement => LowerSwitch(
                switchStatement,
                filePath,
                ownerKey),
            BreakStatementSyntax breakStatement => new HirBreakStatement(
                CSharpSemanticIds.ForNode(filePath, ownerKey, breakStatement),
                RoslynSourceSpan.FromNode(breakStatement, filePath)),
            ContinueStatementSyntax continueStatement => new HirContinueStatement(
                CSharpSemanticIds.ForNode(filePath, ownerKey, continueStatement),
                RoslynSourceSpan.FromNode(continueStatement, filePath)),
            ReturnStatementSyntax returnStatement => new HirReturnStatement(
                CSharpSemanticIds.ForNode(filePath, ownerKey, returnStatement),
                RoslynSourceSpan.FromNode(returnStatement, filePath),
                returnStatement.Expression is null
                    ? null
                    : LowerExpression(returnStatement.Expression, filePath, ownerKey)),
            EmptyStatementSyntax empty => new HirBlockStatement(
                CSharpSemanticIds.ForNode(filePath, ownerKey, empty),
                RoslynSourceSpan.FromNode(empty, filePath),
                []),
            _ => throw new InvalidOperationException($"Profile validator admitted unsupported statement {statement.Kind()}.")
        };

    private static HirSwitchStatement LowerSwitch(
        SwitchStatementSyntax statement,
        string filePath,
        string ownerKey) => new(
            CSharpSemanticIds.ForNode(filePath, ownerKey, statement),
            RoslynSourceSpan.FromNode(statement, filePath),
            LowerExpression(statement.Expression, filePath, ownerKey),
            statement.Sections
                .Select(section => LowerSwitchSection(section, filePath, ownerKey))
                .ToArray());

    private static HirSwitchSection LowerSwitchSection(
        SwitchSectionSyntax section,
        string filePath,
        string ownerKey) => new(
            CSharpSemanticIds.ForNode(filePath, ownerKey, section),
            RoslynSourceSpan.FromNode(section, filePath),
            section.Labels
                .Select(label => LowerSwitchLabel(label, filePath, ownerKey))
                .ToArray(),
            new HirBlockStatement(
                CSharpSemanticIds.ForNode(filePath, ownerKey, section, "switchSectionBody"),
                RoslynSourceSpan.FromNode(section, filePath),
                section.Statements
                    .Select(child => LowerStatement(child, filePath, ownerKey))
                    .ToArray()));

    private static HirSwitchLabel LowerSwitchLabel(
        SwitchLabelSyntax label,
        string filePath,
        string ownerKey) => label switch
        {
            CaseSwitchLabelSyntax @case => new HirPatternSwitchLabel(
                CSharpSemanticIds.ForNode(filePath, ownerKey, @case),
                RoslynSourceSpan.FromNode(@case, filePath),
                NormalizeSwitchLabel(@case),
                new HirConstantPattern(
                    CSharpSemanticIds.ForNode(
                        filePath,
                        ownerKey,
                        @case.Value,
                        "switchConstantPattern"),
                    RoslynSourceSpan.FromNode(@case.Value, filePath),
                    @case.Value.ToString(),
                    LowerExpression(@case.Value, filePath, ownerKey)),
                null),
            CasePatternSwitchLabelSyntax @case => new HirPatternSwitchLabel(
                CSharpSemanticIds.ForNode(filePath, ownerKey, @case),
                RoslynSourceSpan.FromNode(@case, filePath),
                NormalizeSwitchLabel(@case),
                LowerPattern(@case.Pattern, filePath, ownerKey),
                @case.WhenClause is null
                    ? null
                    : LowerExpression(@case.WhenClause.Condition, filePath, ownerKey)),
            DefaultSwitchLabelSyntax @default => new HirDefaultSwitchLabel(
                CSharpSemanticIds.ForNode(filePath, ownerKey, @default),
                RoslynSourceSpan.FromNode(@default, filePath),
                "default"),
            _ => throw new InvalidOperationException(
                $"Profile validator admitted unsupported switch label {label.Kind()}.")
        };

    private static HirPattern LowerPattern(
        PatternSyntax pattern,
        string filePath,
        string ownerKey) => pattern switch
        {
            ConstantPatternSyntax constant => new HirConstantPattern(
                CSharpSemanticIds.ForNode(filePath, ownerKey, constant),
                RoslynSourceSpan.FromNode(constant, filePath),
                constant.ToString(),
                LowerExpression(constant.Expression, filePath, ownerKey)),
            DeclarationPatternSyntax declaration => new HirDeclarationPattern(
                CSharpSemanticIds.ForNode(filePath, ownerKey, declaration),
                RoslynSourceSpan.FromNode(declaration, filePath),
                declaration.ToString(),
                declaration.Type.ToString(),
                LowerPatternVariable(
                    declaration.Designation,
                    declaration.Type.ToString(),
                    filePath,
                    ownerKey),
                declaration.Designation is DiscardDesignationSyntax),
            TypePatternSyntax type => new HirTypePattern(
                CSharpSemanticIds.ForNode(filePath, ownerKey, type),
                RoslynSourceSpan.FromNode(type, filePath),
                type.ToString(),
                type.Type.ToString()),
            VarPatternSyntax varPattern => new HirVarPattern(
                CSharpSemanticIds.ForNode(filePath, ownerKey, varPattern),
                RoslynSourceSpan.FromNode(varPattern, filePath),
                varPattern.ToString(),
                LowerPatternVariable(
                    varPattern.Designation,
                    "var",
                    filePath,
                    ownerKey),
                varPattern.Designation is DiscardDesignationSyntax),
            DiscardPatternSyntax discard => new HirDiscardPattern(
                CSharpSemanticIds.ForNode(filePath, ownerKey, discard),
                RoslynSourceSpan.FromNode(discard, filePath),
                discard.ToString()),
            RelationalPatternSyntax relational => new HirRelationalPattern(
                CSharpSemanticIds.ForNode(filePath, ownerKey, relational),
                RoslynSourceSpan.FromNode(relational, filePath),
                relational.ToString(),
                relational.OperatorToken.ValueText,
                LowerExpression(relational.Expression, filePath, ownerKey)),
            BinaryPatternSyntax binary => new HirLogicalPattern(
                CSharpSemanticIds.ForNode(filePath, ownerKey, binary),
                RoslynSourceSpan.FromNode(binary, filePath),
                binary.ToString(),
                binary.OperatorToken.ValueText,
                LowerPattern(binary.Left, filePath, ownerKey),
                LowerPattern(binary.Right, filePath, ownerKey)),
            UnaryPatternSyntax unary => new HirNotPattern(
                CSharpSemanticIds.ForNode(filePath, ownerKey, unary),
                RoslynSourceSpan.FromNode(unary, filePath),
                unary.ToString(),
                LowerPattern(unary.Pattern, filePath, ownerKey)),
            ParenthesizedPatternSyntax parenthesized => new HirParenthesizedPattern(
                CSharpSemanticIds.ForNode(filePath, ownerKey, parenthesized),
                RoslynSourceSpan.FromNode(parenthesized, filePath),
                parenthesized.ToString(),
                LowerPattern(parenthesized.Pattern, filePath, ownerKey)),
            _ => throw new InvalidOperationException(
                $"Profile validator admitted unsupported pattern {pattern.Kind()}.")
        };

    private static HirPatternVariable? LowerPatternVariable(
        VariableDesignationSyntax designation,
        string typeName,
        string filePath,
        string ownerKey) => designation switch
        {
            SingleVariableDesignationSyntax variable => new HirPatternVariable(
                CSharpSemanticIds.ForNode(filePath, ownerKey, variable),
                RoslynSourceSpan.FromNode(variable, filePath),
                variable.Identifier.ValueText,
                typeName),
            DiscardDesignationSyntax => null,
            _ => throw new InvalidOperationException(
                $"Profile validator admitted unsupported designation {designation.Kind()}.")
        };

    private static string NormalizeSwitchLabel(SwitchLabelSyntax label) =>
        label.ToString().Trim().TrimEnd(':').TrimEnd();

    private static HirForStatement LowerFor(
        ForStatementSyntax statement,
        string filePath,
        string ownerKey) => new(
            CSharpSemanticIds.ForNode(filePath, ownerKey, statement),
            RoslynSourceSpan.FromNode(statement, filePath),
            statement.Declaration is null
                ? null
                : LowerForDeclaration(statement.Declaration, filePath, ownerKey),
            statement.Initializers
                .Select(initializer => LowerExpression(initializer, filePath, ownerKey))
                .ToArray(),
            statement.Condition is null
                ? null
                : LowerExpression(statement.Condition, filePath, ownerKey),
            statement.Incrementors
                .Select(incrementor => LowerExpression(incrementor, filePath, ownerKey))
                .ToArray(),
            LowerAsBlock(statement.Statement, filePath, ownerKey));

    private static HirForEachStatement LowerForEach(
        ForEachStatementSyntax statement,
        string filePath,
        string ownerKey)
    {
        RefTypeSyntax? refType = statement.Type as RefTypeSyntax;
        HirForEachIterationKind iterationKind = refType is null
            ? HirForEachIterationKind.Value
            : refType.ReadOnlyKeyword.IsKind(SyntaxKind.ReadOnlyKeyword)
                ? HirForEachIterationKind.RefReadOnly
                : HirForEachIterationKind.Ref;
        TypeSyntax elementType = refType?.Type ?? statement.Type;
        return new HirForEachStatement(
            CSharpSemanticIds.ForNode(filePath, ownerKey, statement),
            RoslynSourceSpan.FromNode(statement, filePath),
            new HirForEachVariable(
                CSharpSemanticIds.ForNode(
                    filePath,
                    ownerKey,
                    statement,
                    $"foreachVariable:{statement.Identifier.ValueText}"),
                RoslynSourceSpan.FromNode(statement.Type, filePath),
                statement.Identifier.ValueText,
                elementType.ToString()),
            LowerExpression(statement.Expression, filePath, ownerKey),
            LowerAsBlock(statement.Statement, filePath, ownerKey))
        {
            IterationKind = iterationKind,
            IsAsync = !statement.AwaitKeyword.IsKind(SyntaxKind.None),
        };
    }

    private static HirForVariableDeclaration LowerForDeclaration(
        VariableDeclarationSyntax declaration,
        string filePath,
        string ownerKey) => new(
            CSharpSemanticIds.ForNode(filePath, ownerKey, declaration),
            RoslynSourceSpan.FromNode(declaration, filePath),
            declaration.Type.ToString(),
            declaration.Variables.Select(variable => new HirForVariableDeclarator(
                CSharpSemanticIds.ForNode(filePath, ownerKey, variable),
                RoslynSourceSpan.FromNode(variable, filePath),
                variable.Identifier.ValueText,
                variable.Initializer is null
                    ? null
                    : LowerExpression(variable.Initializer.Value, filePath, ownerKey)))
                .ToArray());

    private static HirVariableDeclarationStatement LowerLocal(
        LocalDeclarationStatementSyntax local,
        string filePath,
        string ownerKey)
    {
        VariableDeclaratorSyntax variable = local.Declaration.Variables.Single();
        return new HirVariableDeclarationStatement(
            CSharpSemanticIds.ForNode(filePath, ownerKey, local),
            RoslynSourceSpan.FromNode(local, filePath),
            variable.Identifier.ValueText,
            local.Declaration.Type.ToString(),
            variable.Initializer is null
                ? null
                : LowerExpression(variable.Initializer.Value, filePath, ownerKey));
    }

    private static HirExpression LowerExpression(ExpressionSyntax expression, string filePath, string ownerKey) =>
        expression switch
        {
            LiteralExpressionSyntax literal => new HirLiteralExpression(
                CSharpSemanticIds.ForNode(filePath, ownerKey, literal),
                RoslynSourceSpan.FromNode(literal, filePath),
                literal.Token.Text),
            IdentifierNameSyntax identifier => new HirIdentifierExpression(
                CSharpSemanticIds.ForNode(filePath, ownerKey, identifier),
                RoslynSourceSpan.FromNode(identifier, filePath),
                identifier.Identifier.ValueText),
            ThisExpressionSyntax @this => new HirThisExpression(
                CSharpSemanticIds.ForNode(filePath, ownerKey, @this),
                RoslynSourceSpan.FromNode(@this, filePath)),
            MemberAccessExpressionSyntax member => new HirMemberAccessExpression(
                CSharpSemanticIds.ForNode(filePath, ownerKey, member),
                RoslynSourceSpan.FromNode(member, filePath),
                LowerExpression(member.Expression, filePath, ownerKey),
                member.Name.Identifier.ValueText),
            ConditionalAccessExpressionSyntax conditionalAccess =>
                new HirConditionalAccessExpression(
                    CSharpSemanticIds.ForNode(filePath, ownerKey, conditionalAccess),
                    RoslynSourceSpan.FromNode(conditionalAccess, filePath),
                    LowerExpression(conditionalAccess.Expression, filePath, ownerKey),
                    LowerExpression(conditionalAccess.WhenNotNull, filePath, ownerKey)),
            MemberBindingExpressionSyntax memberBinding =>
                new HirConditionalMemberBindingExpression(
                    CSharpSemanticIds.ForNode(filePath, ownerKey, memberBinding),
                    RoslynSourceSpan.FromNode(memberBinding, filePath),
                    memberBinding.Name.Identifier.ValueText),
            InvocationExpressionSyntax invocation => new HirInvocationExpression(
                CSharpSemanticIds.ForNode(filePath, ownerKey, invocation),
                RoslynSourceSpan.FromNode(invocation, filePath),
                LowerExpression(invocation.Expression, filePath, ownerKey),
                invocation.ArgumentList.Arguments
                    .Select(argument => LowerExpression(argument.Expression, filePath, ownerKey))
                    .ToArray()),
            BinaryExpressionSyntax binary => new HirBinaryExpression(
                CSharpSemanticIds.ForNode(filePath, ownerKey, binary),
                RoslynSourceSpan.FromNode(binary, filePath),
                binary.OperatorToken.Text,
                LowerExpression(binary.Left, filePath, ownerKey),
                LowerExpression(binary.Right, filePath, ownerKey)),
            PrefixUnaryExpressionSyntax unary => new HirUnaryExpression(
                CSharpSemanticIds.ForNode(filePath, ownerKey, unary),
                RoslynSourceSpan.FromNode(unary, filePath),
                unary.OperatorToken.Text,
                LowerExpression(unary.Operand, filePath, ownerKey)),
            PostfixUnaryExpressionSyntax unary => new HirUnaryExpression(
                CSharpSemanticIds.ForNode(filePath, ownerKey, unary),
                RoslynSourceSpan.FromNode(unary, filePath),
                unary.OperatorToken.Text,
                LowerExpression(unary.Operand, filePath, ownerKey),
                IsPostfix: true),
            ParenthesizedExpressionSyntax parenthesized =>
                LowerExpression(parenthesized.Expression, filePath, ownerKey),
            AssignmentExpressionSyntax assignment => new HirAssignmentExpression(
                CSharpSemanticIds.ForNode(filePath, ownerKey, assignment),
                RoslynSourceSpan.FromNode(assignment, filePath),
                assignment.OperatorToken.Text,
                LowerExpression(assignment.Left, filePath, ownerKey),
                LowerExpression(assignment.Right, filePath, ownerKey)),
            AwaitExpressionSyntax awaitExpression => new HirAwaitExpression(
                CSharpSemanticIds.ForNode(filePath, ownerKey, awaitExpression),
                RoslynSourceSpan.FromNode(awaitExpression, filePath),
                LowerExpression(awaitExpression.Expression, filePath, ownerKey)),
            CastExpressionSyntax cast => new HirCastExpression(
                CSharpSemanticIds.ForNode(filePath, ownerKey, cast),
                RoslynSourceSpan.FromNode(cast, filePath),
                cast.Type.ToString(),
                LowerExpression(cast.Expression, filePath, ownerKey)),
            ConditionalExpressionSyntax conditional => new HirConditionalExpression(
                CSharpSemanticIds.ForNode(filePath, ownerKey, conditional),
                RoslynSourceSpan.FromNode(conditional, filePath),
                LowerExpression(conditional.Condition, filePath, ownerKey),
                LowerExpression(conditional.WhenTrue, filePath, ownerKey),
                LowerExpression(conditional.WhenFalse, filePath, ownerKey)),
            _ => throw new InvalidOperationException($"Profile validator admitted unsupported expression {expression.Kind()}.")
        };

    internal static UnifiedDiagnostic FromRoslynDiagnostic(Diagnostic diagnostic, string filePath) =>
        new(
            diagnostic.Id,
            diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => UnifiedDiagnosticSeverity.Error,
                DiagnosticSeverity.Warning => UnifiedDiagnosticSeverity.Warning,
                _ => UnifiedDiagnosticSeverity.Info,
            },
            diagnostic.GetMessage(),
            RoslynSourceSpan.FromDiagnostic(diagnostic, filePath));

    private static string GetNamespace(SyntaxNode node) => string.Join(
        ".",
        node.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(static declaration => declaration.Name.ToString()));

    private static string GetNestedTypeName(TypeDeclarationSyntax type) => string.Join(
        ".",
        type.AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .Reverse()
            .Select(static declaration => declaration.Identifier.ValueText));

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}
