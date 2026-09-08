using UnifiedScript.Core;
using UnifiedScript.Core.Hir;

namespace UnifiedScript.CSharp;

internal static class HirIdentityRewriter
{
    public static HirCompilationUnit Rewrite(
        HirCompilationUnit compilation,
        IReadOnlyDictionary<SemanticId, SemanticId> identities) => compilation with
        {
            Types = compilation.Types.Select(type => RewriteType(type, identities)).ToArray(),
        };

    private static HirTypeDeclaration RewriteType(
        HirTypeDeclaration type,
        IReadOnlyDictionary<SemanticId, SemanticId> identities) => type with
        {
            Methods = type.Methods.Select(method => RewriteMethod(method, identities)).ToArray(),
        };

    private static HirMethodDeclaration RewriteMethod(
        HirMethodDeclaration method,
        IReadOnlyDictionary<SemanticId, SemanticId> identities) => method with
        {
            Id = GetId(method, identities),
            Parameters = method.Parameters.Select(parameter => parameter with
            {
                Id = GetId(parameter, identities),
            }).ToArray(),
            Body = RewriteBlock(method.Body, identities),
        };

    private static HirBlockStatement RewriteBlock(
        HirBlockStatement block,
        IReadOnlyDictionary<SemanticId, SemanticId> identities) => block with
        {
            Id = GetId(block, identities),
            Statements = block.Statements.Select(statement => RewriteStatement(statement, identities)).ToArray(),
        };

    private static HirStatement RewriteStatement(
        HirStatement statement,
        IReadOnlyDictionary<SemanticId, SemanticId> identities) => statement switch
        {
            HirBlockStatement block => RewriteBlock(block, identities),
            HirVariableDeclarationStatement variable => variable with
            {
                Id = GetId(variable, identities),
                Initializer = variable.Initializer is null
                    ? null
                    : RewriteExpression(variable.Initializer, identities),
            },
            HirExpressionStatement expression => expression with
            {
                Id = GetId(expression, identities),
                Expression = RewriteExpression(expression.Expression, identities),
            },
            HirIfStatement ifStatement => ifStatement with
            {
                Id = GetId(ifStatement, identities),
                Condition = RewriteExpression(ifStatement.Condition, identities),
                Then = RewriteBlock(ifStatement.Then, identities),
                Else = ifStatement.Else is null ? null : RewriteBlock(ifStatement.Else, identities),
            },
            HirWhileStatement whileStatement => whileStatement with
            {
                Id = GetId(whileStatement, identities),
                Condition = RewriteExpression(whileStatement.Condition, identities),
                Body = RewriteBlock(whileStatement.Body, identities),
            },
            HirDoWhileStatement doWhileStatement => doWhileStatement with
            {
                Id = GetId(doWhileStatement, identities),
                Body = RewriteBlock(doWhileStatement.Body, identities),
                Condition = RewriteExpression(doWhileStatement.Condition, identities),
            },
            HirForStatement forStatement => forStatement with
            {
                Id = GetId(forStatement, identities),
                Declaration = forStatement.Declaration is null
                    ? null
                    : RewriteForDeclaration(forStatement.Declaration, identities),
                Initializers = forStatement.Initializers
                    .Select(initializer => RewriteExpression(initializer, identities))
                    .ToArray(),
                Condition = forStatement.Condition is null
                    ? null
                    : RewriteExpression(forStatement.Condition, identities),
                Incrementors = forStatement.Incrementors
                    .Select(incrementor => RewriteExpression(incrementor, identities))
                    .ToArray(),
                Body = RewriteBlock(forStatement.Body, identities),
            },
            HirForEachStatement forEachStatement => forEachStatement with
            {
                Id = GetId(forEachStatement, identities),
                Variable = forEachStatement.Variable with
                {
                    Id = GetId(forEachStatement.Variable, identities),
                },
                Collection = RewriteExpression(forEachStatement.Collection, identities),
                Body = RewriteBlock(forEachStatement.Body, identities),
            },
            HirSwitchStatement switchStatement => switchStatement with
            {
                Id = GetId(switchStatement, identities),
                GoverningExpression = RewriteExpression(
                    switchStatement.GoverningExpression,
                    identities),
                Sections = switchStatement.Sections
                    .Select(section => RewriteSwitchSection(section, identities))
                    .ToArray(),
            },
            HirBreakStatement breakStatement => breakStatement with
            {
                Id = GetId(breakStatement, identities),
            },
            HirContinueStatement continueStatement => continueStatement with
            {
                Id = GetId(continueStatement, identities),
            },
            HirReturnStatement returnStatement => returnStatement with
            {
                Id = GetId(returnStatement, identities),
                Expression = returnStatement.Expression is null
                    ? null
                    : RewriteExpression(returnStatement.Expression, identities),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(statement), statement.GetType().Name),
        };

    private static HirSwitchSection RewriteSwitchSection(
        HirSwitchSection section,
        IReadOnlyDictionary<SemanticId, SemanticId> identities) => section with
        {
            Id = GetId(section, identities),
            Labels = section.Labels
                .Select(label => RewriteSwitchLabel(label, identities))
                .ToArray(),
            Body = RewriteBlock(section.Body, identities),
        };

    private static HirSwitchLabel RewriteSwitchLabel(
        HirSwitchLabel label,
        IReadOnlyDictionary<SemanticId, SemanticId> identities) => label switch
        {
            HirPatternSwitchLabel pattern => pattern with
            {
                Id = GetId(pattern, identities),
                Pattern = RewritePattern(pattern.Pattern, identities),
                Guard = pattern.Guard is null
                    ? null
                    : RewriteExpression(pattern.Guard, identities),
            },
            HirDefaultSwitchLabel @default => @default with
            {
                Id = GetId(@default, identities),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(label), label.GetType().Name),
        };

    private static HirPattern RewritePattern(
        HirPattern pattern,
        IReadOnlyDictionary<SemanticId, SemanticId> identities) => pattern switch
        {
            HirConstantPattern constant => constant with
            {
                Id = GetId(constant, identities),
                Value = RewriteExpression(constant.Value, identities),
            },
            HirDeclarationPattern declaration => declaration with
            {
                Id = GetId(declaration, identities),
                Variable = declaration.Variable is null
                    ? null
                    : declaration.Variable with
                    {
                        Id = GetId(declaration.Variable, identities),
                    },
            },
            HirTypePattern type => type with { Id = GetId(type, identities) },
            HirVarPattern varPattern => varPattern with
            {
                Id = GetId(varPattern, identities),
                Variable = varPattern.Variable is null
                    ? null
                    : varPattern.Variable with
                    {
                        Id = GetId(varPattern.Variable, identities),
                    },
            },
            HirDiscardPattern discard => discard with { Id = GetId(discard, identities) },
            HirRelationalPattern relational => relational with
            {
                Id = GetId(relational, identities),
                Value = RewriteExpression(relational.Value, identities),
            },
            HirLogicalPattern logical => logical with
            {
                Id = GetId(logical, identities),
                Left = RewritePattern(logical.Left, identities),
                Right = RewritePattern(logical.Right, identities),
            },
            HirNotPattern not => not with
            {
                Id = GetId(not, identities),
                Operand = RewritePattern(not.Operand, identities),
            },
            HirParenthesizedPattern parenthesized => parenthesized with
            {
                Id = GetId(parenthesized, identities),
                Pattern = RewritePattern(parenthesized.Pattern, identities),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(pattern), pattern.GetType().Name),
        };

    private static HirForVariableDeclaration RewriteForDeclaration(
        HirForVariableDeclaration declaration,
        IReadOnlyDictionary<SemanticId, SemanticId> identities) => declaration with
        {
            Id = GetId(declaration, identities),
            Variables = declaration.Variables.Select(variable => variable with
            {
                Id = GetId(variable, identities),
                Initializer = variable.Initializer is null
                    ? null
                    : RewriteExpression(variable.Initializer, identities),
            }).ToArray(),
        };

    private static HirExpression RewriteExpression(
        HirExpression expression,
        IReadOnlyDictionary<SemanticId, SemanticId> identities) => expression switch
        {
            HirLiteralExpression literal => literal with { Id = GetId(literal, identities) },
            HirIdentifierExpression identifier => identifier with { Id = GetId(identifier, identities) },
            HirThisExpression @this => @this with { Id = GetId(@this, identities) },
            HirMemberAccessExpression member => member with
            {
                Id = GetId(member, identities),
                Target = RewriteExpression(member.Target, identities),
            },
            HirConditionalAccessExpression conditionalAccess => conditionalAccess with
            {
                Id = GetId(conditionalAccess, identities),
                Receiver = RewriteExpression(conditionalAccess.Receiver, identities),
                WhenNotNull = RewriteExpression(conditionalAccess.WhenNotNull, identities),
            },
            HirConditionalMemberBindingExpression memberBinding => memberBinding with
            {
                Id = GetId(memberBinding, identities),
            },
            HirInvocationExpression invocation => invocation with
            {
                Id = GetId(invocation, identities),
                Target = RewriteExpression(invocation.Target, identities),
                Arguments = invocation.Arguments
                    .Select(argument => RewriteExpression(argument, identities))
                    .ToArray(),
            },
            HirBinaryExpression binary => binary with
            {
                Id = GetId(binary, identities),
                Left = RewriteExpression(binary.Left, identities),
                Right = RewriteExpression(binary.Right, identities),
            },
            HirUnaryExpression unary => unary with
            {
                Id = GetId(unary, identities),
                Operand = RewriteExpression(unary.Operand, identities),
            },
            HirAssignmentExpression assignment => assignment with
            {
                Id = GetId(assignment, identities),
                Target = RewriteExpression(assignment.Target, identities),
                Value = RewriteExpression(assignment.Value, identities),
            },
            HirAwaitExpression awaitExpression => awaitExpression with
            {
                Id = GetId(awaitExpression, identities),
                Operand = RewriteExpression(awaitExpression.Operand, identities),
            },
            HirCastExpression cast => cast with
            {
                Id = GetId(cast, identities),
                Operand = RewriteExpression(cast.Operand, identities),
            },
            HirConditionalExpression conditional => conditional with
            {
                Id = GetId(conditional, identities),
                Condition = RewriteExpression(conditional.Condition, identities),
                WhenTrue = RewriteExpression(conditional.WhenTrue, identities),
                WhenFalse = RewriteExpression(conditional.WhenFalse, identities),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(expression), expression.GetType().Name),
        };

    private static SemanticId GetId(
        HirNode node,
        IReadOnlyDictionary<SemanticId, SemanticId> identities) =>
        identities.TryGetValue(node.Id, out SemanticId id) ? id : node.Id;
}
