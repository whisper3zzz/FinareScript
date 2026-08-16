using UnifiedScript.Core;
using UnifiedScript.Core.Hir;

namespace UnifiedScript.CSharp;

internal sealed class HirPatchIndex
{
    private readonly Dictionary<SemanticId, BlockLocation> blocks = [];
    private readonly Dictionary<SemanticId, StatementLocation> statements = [];
    private readonly Dictionary<SemanticId, ExpressionLocation> expressions = [];

    public HirPatchIndex(HirCompilationUnit compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        foreach (HirMethodDeclaration method in compilation.Types
                     .SelectMany(static type => type.Methods))
        {
            AddBlock(method.Body, method);
        }
    }

    public bool TryGetBlock(SemanticId id, out BlockLocation? location) =>
        blocks.TryGetValue(id, out location);

    public bool TryGetStatement(SemanticId id, out StatementLocation? location) =>
        statements.TryGetValue(id, out location);

    public bool TryGetExpression(SemanticId id, out ExpressionLocation? location) =>
        expressions.TryGetValue(id, out location);

    private void AddBlock(HirBlockStatement block, HirMethodDeclaration method)
    {
        blocks.Add(block.Id, new BlockLocation(block, method));
        for (int index = 0; index < block.Statements.Count; index++)
        {
            HirStatement statement = block.Statements[index];
            statements.Add(statement.Id, new StatementLocation(statement, block, method, index));
            switch (statement)
            {
                case HirBlockStatement nested:
                    AddBlock(nested, method);
                    break;
                case HirVariableDeclarationStatement { Initializer: not null } variable:
                    AddExpression(variable.Initializer, variable, block, method, null);
                    break;
                case HirExpressionStatement expressionStatement:
                    AddExpression(expressionStatement.Expression, expressionStatement, block, method, null);
                    break;
                case HirIfStatement ifStatement:
                    AddExpression(ifStatement.Condition, ifStatement, block, method, null);
                    AddBlock(ifStatement.Then, method);
                    if (ifStatement.Else is not null)
                    {
                        AddBlock(ifStatement.Else, method);
                    }

                    break;
                case HirReturnStatement { Expression: not null } returnStatement:
                    AddExpression(returnStatement.Expression, returnStatement, block, method, null);
                    break;
            }
        }
    }

    private void AddExpression(
        HirExpression expression,
        HirStatement containingStatement,
        HirBlockStatement parentBlock,
        HirMethodDeclaration method,
        HirExpression? parentExpression)
    {
        expressions.Add(
            expression.Id,
            new ExpressionLocation(
                expression,
                containingStatement,
                parentBlock,
                method,
                parentExpression));
        switch (expression)
        {
            case HirMemberAccessExpression member:
                AddExpression(member.Target, containingStatement, parentBlock, method, member);
                break;
            case HirInvocationExpression invocation:
                AddExpression(invocation.Target, containingStatement, parentBlock, method, invocation);
                foreach (HirExpression argument in invocation.Arguments)
                {
                    AddExpression(argument, containingStatement, parentBlock, method, invocation);
                }

                break;
            case HirBinaryExpression binary:
                AddExpression(binary.Left, containingStatement, parentBlock, method, binary);
                AddExpression(binary.Right, containingStatement, parentBlock, method, binary);
                break;
            case HirUnaryExpression unary:
                AddExpression(unary.Operand, containingStatement, parentBlock, method, unary);
                break;
            case HirAssignmentExpression assignment:
                AddExpression(assignment.Target, containingStatement, parentBlock, method, assignment);
                AddExpression(assignment.Value, containingStatement, parentBlock, method, assignment);
                break;
            case HirAwaitExpression awaitExpression:
                AddExpression(awaitExpression.Operand, containingStatement, parentBlock, method, awaitExpression);
                break;
            case HirCastExpression cast:
                AddExpression(cast.Operand, containingStatement, parentBlock, method, cast);
                break;
        }
    }

    internal sealed record BlockLocation(
        HirBlockStatement Block,
        HirMethodDeclaration Method);

    internal sealed record StatementLocation(
        HirStatement Statement,
        HirBlockStatement ParentBlock,
        HirMethodDeclaration Method,
        int Index);

    internal sealed record ExpressionLocation(
        HirExpression Expression,
        HirStatement ContainingStatement,
        HirBlockStatement ParentBlock,
        HirMethodDeclaration Method,
        HirExpression? ParentExpression);
}
