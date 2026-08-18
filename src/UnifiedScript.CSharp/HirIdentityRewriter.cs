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
