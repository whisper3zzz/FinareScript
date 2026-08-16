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
            AddBlock(method.Body, method, null, null);
        }
    }

    public bool TryGetBlock(SemanticId id, out BlockLocation? location) =>
        blocks.TryGetValue(id, out location);

    public bool TryGetStatement(SemanticId id, out StatementLocation? location) =>
        statements.TryGetValue(id, out location);

    public bool TryGetExpression(SemanticId id, out ExpressionLocation? location) =>
        expressions.TryGetValue(id, out location);

    public bool DoesStatementDominate(
        SemanticId definitionStatementId,
        SemanticId consumerStatementId)
    {
        if (!statements.TryGetValue(definitionStatementId, out StatementLocation? definition) ||
            !statements.TryGetValue(consumerStatementId, out StatementLocation? consumer) ||
            definition.Method.Id != consumer.Method.Id)
        {
            return false;
        }

        if (definition.ParentBlock.Id == consumer.ParentBlock.Id)
        {
            return definition.Index < consumer.Index;
        }

        HirBlockStatement current = consumer.ParentBlock;
        while (blocks.TryGetValue(current.Id, out BlockLocation? currentLocation) &&
            currentLocation.ParentBlock is not null &&
            currentLocation.EntryStatement is not null)
        {
            HirBlockStatement parent = currentLocation.ParentBlock;
            if (parent.Id == definition.ParentBlock.Id &&
                statements.TryGetValue(
                    currentLocation.EntryStatement.Id,
                    out StatementLocation? entryLocation))
            {
                return definition.Index < entryLocation.Index;
            }

            current = parent;
        }

        return false;
    }

    public bool DoesStatementDominateInsertion(
        SemanticId definitionStatementId,
        SemanticId targetBlockId,
        int insertionIndex)
    {
        if (!statements.TryGetValue(definitionStatementId, out StatementLocation? definition) ||
            !blocks.TryGetValue(targetBlockId, out BlockLocation? target) ||
            definition.Method.Id != target.Method.Id ||
            insertionIndex < 0 ||
            insertionIndex > target.Block.Statements.Count)
        {
            return false;
        }

        if (definition.ParentBlock.Id == targetBlockId)
        {
            return definition.Index < insertionIndex;
        }

        HirBlockStatement current = target.Block;
        while (blocks.TryGetValue(current.Id, out BlockLocation? currentLocation) &&
            currentLocation.ParentBlock is not null &&
            currentLocation.EntryStatement is not null)
        {
            HirBlockStatement parent = currentLocation.ParentBlock;
            if (parent.Id == definition.ParentBlock.Id &&
                statements.TryGetValue(
                    currentLocation.EntryStatement.Id,
                    out StatementLocation? entryLocation))
            {
                return definition.Index < entryLocation.Index;
            }

            current = parent;
        }

        return false;
    }

    private void AddBlock(
        HirBlockStatement block,
        HirMethodDeclaration method,
        HirBlockStatement? parentBlock,
        HirStatement? entryStatement)
    {
        blocks.Add(block.Id, new BlockLocation(block, method, parentBlock, entryStatement));
        for (int index = 0; index < block.Statements.Count; index++)
        {
            HirStatement statement = block.Statements[index];
            statements.Add(statement.Id, new StatementLocation(statement, block, method, index));
            switch (statement)
            {
                case HirBlockStatement nested:
                    AddBlock(nested, method, block, nested);
                    break;
                case HirVariableDeclarationStatement { Initializer: not null } variable:
                    AddExpression(variable.Initializer, variable, block, method, null);
                    break;
                case HirExpressionStatement expressionStatement:
                    AddExpression(expressionStatement.Expression, expressionStatement, block, method, null);
                    break;
                case HirIfStatement ifStatement:
                    AddExpression(ifStatement.Condition, ifStatement, block, method, null);
                    AddBlock(ifStatement.Then, method, block, ifStatement);
                    if (ifStatement.Else is not null)
                    {
                        AddBlock(ifStatement.Else, method, block, ifStatement);
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
        HirMethodDeclaration Method,
        HirBlockStatement? ParentBlock,
        HirStatement? EntryStatement);

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
