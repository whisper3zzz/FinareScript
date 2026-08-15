namespace UnifiedScript.Core.Hir;

public static class HirTraversal
{
    public static IEnumerable<HirNode> DescendantsAndSelf(this HirNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        yield return node;
        foreach (HirNode child in GetChildren(node))
        {
            foreach (HirNode descendant in child.DescendantsAndSelf())
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<HirNode> GetChildren(HirNode node) => node switch
    {
        HirCompilationUnit compilation => compilation.Types,
        HirTypeDeclaration type => type.Methods,
        HirMethodDeclaration method => method.Parameters.Cast<HirNode>().Append(method.Body),
        HirBlockStatement block => block.Statements,
        HirVariableDeclarationStatement { Initializer: not null } variable => [variable.Initializer],
        HirExpressionStatement statement => [statement.Expression],
        HirIfStatement { Else: not null } statement =>
            [statement.Condition, statement.Then, statement.Else],
        HirIfStatement statement => [statement.Condition, statement.Then],
        HirReturnStatement { Expression: not null } statement => [statement.Expression],
        HirMemberAccessExpression expression => [expression.Target],
        HirInvocationExpression expression => expression.Arguments
            .Cast<HirNode>()
            .Prepend(expression.Target),
        HirBinaryExpression expression => [expression.Left, expression.Right],
        HirUnaryExpression expression => [expression.Operand],
        HirAssignmentExpression expression => [expression.Target, expression.Value],
        HirAwaitExpression expression => [expression.Operand],
        HirCastExpression expression => [expression.Operand],
        _ => [],
    };
}
