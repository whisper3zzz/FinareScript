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
        HirWhileStatement statement => [statement.Condition, statement.Body],
        HirDoWhileStatement statement => [statement.Body, statement.Condition],
        HirForStatement statement => GetForChildren(statement),
        HirForEachStatement statement => [statement.Variable, statement.Collection, statement.Body],
        HirSwitchStatement statement => statement.Sections
            .Cast<HirNode>()
            .Prepend(statement.GoverningExpression),
        HirSwitchSection section => section.Labels.Cast<HirNode>().Append(section.Body),
        HirPatternSwitchLabel { Guard: not null } label =>
            [label.Pattern, label.Guard],
        HirPatternSwitchLabel label => [label.Pattern],
        HirConstantPattern pattern => [pattern.Value],
        HirDeclarationPattern { Variable: not null } pattern => [pattern.Variable],
        HirVarPattern { Variable: not null } pattern => [pattern.Variable],
        HirRelationalPattern pattern => [pattern.Value],
        HirLogicalPattern pattern => [pattern.Left, pattern.Right],
        HirNotPattern pattern => [pattern.Operand],
        HirParenthesizedPattern pattern => [pattern.Pattern],
        HirForVariableDeclaration declaration => declaration.Variables,
        HirForVariableDeclarator { Initializer: not null } declarator => [declarator.Initializer],
        HirReturnStatement { Expression: not null } statement => [statement.Expression],
        HirMemberAccessExpression expression => [expression.Target],
        HirConditionalAccessExpression expression =>
            [expression.Receiver, expression.WhenNotNull],
        HirInvocationExpression expression => expression.Arguments
            .Cast<HirNode>()
            .Prepend(expression.Target),
        HirBinaryExpression expression => [expression.Left, expression.Right],
        HirUnaryExpression expression => [expression.Operand],
        HirAssignmentExpression expression => [expression.Target, expression.Value],
        HirAwaitExpression expression => [expression.Operand],
        HirCastExpression expression => [expression.Operand],
        HirConditionalExpression expression =>
            [expression.Condition, expression.WhenTrue, expression.WhenFalse],
        _ => [],
    };

    private static IEnumerable<HirNode> GetForChildren(HirForStatement statement)
    {
        if (statement.Declaration is not null)
        {
            yield return statement.Declaration;
        }

        foreach (HirExpression initializer in statement.Initializers)
        {
            yield return initializer;
        }

        if (statement.Condition is not null)
        {
            yield return statement.Condition;
        }

        foreach (HirExpression incrementor in statement.Incrementors)
        {
            yield return incrementor;
        }

        yield return statement.Body;
    }
}
