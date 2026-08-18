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
        string parameterSignature = string.Join(
            ",",
            method.ParameterList.Parameters.Select(static parameter => parameter.Type?.ToString() ?? "?"));
        string methodKey = $"{qualifiedTypeName}.{method.Identifier.ValueText}({parameterSignature})";
        SemanticId methodId = CSharpSemanticIds.ForSymbol("method", methodKey);
        IReadOnlyList<HirParameter> parameters = method.ParameterList.Parameters
            .Select(parameter => new HirParameter(
                CSharpSemanticIds.ForNode(filePath, methodKey, parameter, $"parameter:{parameter.Identifier.ValueText}"),
                RoslynSourceSpan.FromNode(parameter, filePath),
                parameter.Identifier.ValueText,
                parameter.Type?.ToString() ?? "unknown"))
            .ToArray();

        HirBlockStatement body = LowerBlock(method.Body!, filePath, methodKey);
        return new HirMethodDeclaration(
            methodId,
            RoslynSourceSpan.FromNode(method, filePath),
            method.Identifier.ValueText,
            method.ReturnType.ToString(),
            true,
            method.Modifiers.Any(SyntaxKind.StaticKeyword),
            method.Modifiers.Any(SyntaxKind.AsyncKeyword),
            parameters,
            body);
    }

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
