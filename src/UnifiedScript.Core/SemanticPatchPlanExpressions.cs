namespace UnifiedScript.Core;

public static class SemanticPatchPlanExpressions
{
    public static IReadOnlyList<ProducedLocalReferencePatchExpression> FindProducedLocalReferences(
        SemanticPatchPlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var references = new List<ProducedLocalReferencePatchExpression>();
        foreach (PatchExpression expression in GetExpressions(step))
        {
            CollectProducedLocalReferences(expression, references);
        }

        return references;
    }

    public static SemanticPatchPlanStep ResolveProducedLocalReferences(
        SemanticPatchPlanStep step,
        Func<ProducedLocalReferencePatchExpression, LocalReferencePatchExpression> resolver)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(resolver);
        return step switch
        {
            ReplaceAssignmentValuePatchPlanStep replacement => replacement with
            {
                NewValue = Rewrite(replacement.NewValue, resolver),
            },
            ReplaceExpressionInputPatchPlanStep replacement => replacement with
            {
                NewInput = Rewrite(replacement.NewInput, resolver),
            },
            ReplaceProducedExpressionInputPatchPlanStep replacement => replacement with
            {
                NewInput = Rewrite(replacement.NewInput, resolver),
            },
            ReplaceWhileConditionPatchPlanStep replacement => replacement with
            {
                NewCondition = Rewrite(replacement.NewCondition, resolver),
            },
            ReplaceProducedWhileConditionPatchPlanStep replacement => replacement with
            {
                NewCondition = Rewrite(replacement.NewCondition, resolver),
            },
            ReplaceForConditionPatchPlanStep replacement => replacement with
            {
                NewCondition = Rewrite(replacement.NewCondition, resolver),
            },
            ReplaceProducedForConditionPatchPlanStep replacement => replacement with
            {
                NewCondition = Rewrite(replacement.NewCondition, resolver),
            },
            ReplaceForDeclarationInitializerPatchPlanStep replacement => replacement with
            {
                NewInitializer = Rewrite(replacement.NewInitializer, resolver),
            },
            ReplaceForAssignmentInitializerValuePatchPlanStep replacement => replacement with
            {
                NewValue = Rewrite(replacement.NewValue, resolver),
            },
            ReplaceForIncrementorPatchPlanStep replacement => replacement with
            {
                NewOperand = Rewrite(replacement.NewOperand, resolver),
            },
            ReplaceIfConditionPatchPlanStep replacement => replacement with
            {
                NewCondition = Rewrite(replacement.NewCondition, resolver),
            },
            ReplaceProducedIfConditionPatchPlanStep replacement => replacement with
            {
                NewCondition = Rewrite(replacement.NewCondition, resolver),
            },
            ReplaceLocalInitializerPatchPlanStep replacement => replacement with
            {
                NewInitializer = Rewrite(replacement.NewInitializer, resolver),
            },
            ReplaceProducedLocalInitializerPatchPlanStep replacement => replacement with
            {
                NewInitializer = Rewrite(replacement.NewInitializer, resolver),
            },
            ReplaceReturnValuePatchPlanStep replacement => replacement with
            {
                NewValue = Rewrite(replacement.NewValue, resolver),
            },
            ReplaceProducedReturnValuePatchPlanStep replacement => replacement with
            {
                NewValue = Rewrite(replacement.NewValue, resolver),
            },
            InsertStatementPatchPlanStep insertion => insertion with
            {
                NewStatement = Rewrite(insertion.NewStatement, resolver),
            },
            InsertBranchStatementPatchPlanStep insertion => insertion with
            {
                NewStatement = Rewrite(insertion.NewStatement, resolver),
            },
            InsertLoopBodyStatementPatchPlanStep insertion => insertion with
            {
                NewStatement = Rewrite(insertion.NewStatement, resolver),
            },
            _ => step,
        };
    }

    public static PatchExpression? GetAuthoredExpression(SemanticPatchPlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step switch
        {
            ReplaceAssignmentValuePatchPlanStep replacement => replacement.NewValue,
            ReplaceExpressionInputPatchPlanStep replacement => replacement.NewInput,
            ReplaceProducedExpressionInputPatchPlanStep replacement => replacement.NewInput,
            ReplaceWhileConditionPatchPlanStep replacement => replacement.NewCondition,
            ReplaceProducedWhileConditionPatchPlanStep replacement => replacement.NewCondition,
            ReplaceForConditionPatchPlanStep replacement => replacement.NewCondition,
            ReplaceProducedForConditionPatchPlanStep replacement => replacement.NewCondition,
            ReplaceForDeclarationInitializerPatchPlanStep replacement => replacement.NewInitializer,
            ReplaceForAssignmentInitializerValuePatchPlanStep replacement => replacement.NewValue,
            ReplaceForIncrementorPatchPlanStep replacement => replacement.NewOperand,
            ReplaceIfConditionPatchPlanStep replacement => replacement.NewCondition,
            ReplaceProducedIfConditionPatchPlanStep replacement => replacement.NewCondition,
            ReplaceLocalInitializerPatchPlanStep replacement => replacement.NewInitializer,
            ReplaceProducedLocalInitializerPatchPlanStep replacement => replacement.NewInitializer,
            ReplaceReturnValuePatchPlanStep replacement => replacement.NewValue,
            ReplaceProducedReturnValuePatchPlanStep replacement => replacement.NewValue,
            InsertStatementPatchPlanStep insertion => GetExpression(insertion.NewStatement),
            InsertBranchStatementPatchPlanStep insertion => GetExpression(insertion.NewStatement),
            InsertLoopBodyStatementPatchPlanStep insertion => GetExpression(insertion.NewStatement),
            _ => null,
        };
    }

    private static IEnumerable<PatchExpression> GetExpressions(SemanticPatchPlanStep step)
    {
        InsertPatchStatement? statement = step switch
        {
            InsertStatementPatchPlanStep insertion => insertion.NewStatement,
            InsertBranchStatementPatchPlanStep insertion => insertion.NewStatement,
            InsertLoopBodyStatementPatchPlanStep insertion => insertion.NewStatement,
            _ => null,
        };
        if (statement is not null)
        {
            foreach (PatchExpression statementExpression in GetExpressions(statement))
            {
                yield return statementExpression;
            }

            yield break;
        }

        PatchExpression? expression = GetAuthoredExpression(step);
        if (expression is not null)
        {
            yield return expression;
        }
    }

    private static IEnumerable<PatchExpression> GetExpressions(InsertPatchStatement statement)
    {
        switch (statement)
        {
            case LocalAssignmentPatchStatement assignment:
                yield return assignment.Value;
                break;
            case IfPatchStatement branch:
                yield return branch.Condition;
                break;
            case WhilePatchStatement loop:
                yield return loop.Condition;
                break;
            case ForPatchStatement forStatement:
                if (forStatement.Declaration is not null)
                {
                    foreach (ForVariableDeclaratorPatch variable in
                        forStatement.Declaration.Variables ?? [])
                    {
                        yield return variable.Initializer;
                    }
                }

                foreach (ForAssignmentPatchExpression initializer in forStatement.Initializers ?? [])
                {
                    yield return initializer.Value;
                }

                if (forStatement.Condition is not null)
                {
                    yield return forStatement.Condition;
                }

                foreach (ForMutationPatchExpression incrementor in forStatement.Incrementors ?? [])
                {
                    yield return incrementor.Operand;
                }

                break;
            case ReturnPatchStatement { Expression: not null } valueReturn:
                yield return valueReturn.Expression;
                break;
        }
    }

    private static PatchExpression? GetExpression(InsertPatchStatement? statement) => statement switch
    {
        LocalAssignmentPatchStatement assignment => assignment.Value,
        IfPatchStatement branch => branch.Condition,
        WhilePatchStatement loop => loop.Condition,
        ReturnPatchStatement valueReturn => valueReturn.Expression,
        _ => null,
    };

    private static InsertPatchStatement Rewrite(
        InsertPatchStatement? statement,
        Func<ProducedLocalReferencePatchExpression, LocalReferencePatchExpression> resolver)
    {
        if (statement is null)
        {
            return null!;
        }

        return statement switch
        {
            LocalAssignmentPatchStatement assignment => assignment with
            {
                Value = Rewrite(assignment.Value, resolver),
            },
            IfPatchStatement branch => branch with
            {
                Condition = Rewrite(branch.Condition, resolver),
            },
            WhilePatchStatement loop => loop with
            {
                Condition = Rewrite(loop.Condition, resolver),
            },
            ForPatchStatement loop => loop with
            {
                Declaration = loop.Declaration is null
                    ? null
                    : loop.Declaration with
                    {
                        Variables = (loop.Declaration.Variables ?? []).Select(variable => variable with
                        {
                            Initializer = Rewrite(variable.Initializer, resolver),
                        }).ToArray(),
                    },
                Initializers = (loop.Initializers ?? []).Select(initializer => initializer with
                {
                    Value = Rewrite(initializer.Value, resolver),
                }).ToArray(),
                Condition = loop.Condition is null
                    ? null
                    : Rewrite(loop.Condition, resolver),
                Incrementors = (loop.Incrementors ?? []).Select(incrementor => incrementor with
                {
                    Operand = Rewrite(incrementor.Operand, resolver),
                }).ToArray(),
            },
            ReturnPatchStatement { Expression: not null } valueReturn => valueReturn with
            {
                Expression = Rewrite(valueReturn.Expression, resolver),
            },
            _ => statement,
        };
    }

    private static PatchExpression Rewrite(
        PatchExpression? expression,
        Func<ProducedLocalReferencePatchExpression, LocalReferencePatchExpression> resolver)
    {
        if (expression is null)
        {
            return null!;
        }

        return expression switch
        {
            ProducedLocalReferencePatchExpression produced => resolver(produced) ??
                throw new InvalidOperationException(
                    "A produced-local expression resolver cannot return null."),
            UnaryPatchExpression { Operand: not null } unary => unary with
            {
                Operand = Rewrite(unary.Operand, resolver),
            },
            BinaryPatchExpression { Left: not null, Right: not null } binary => binary with
            {
                Left = Rewrite(binary.Left, resolver),
                Right = Rewrite(binary.Right, resolver),
            },
            CastPatchExpression { Operand: not null } cast => cast with
            {
                Operand = Rewrite(cast.Operand, resolver),
            },
            ConditionalPatchExpression
            {
                Condition: not null,
                WhenTrue: not null,
                WhenFalse: not null,
            } conditional => conditional with
            {
                Condition = Rewrite(conditional.Condition, resolver),
                WhenTrue = Rewrite(conditional.WhenTrue, resolver),
                WhenFalse = Rewrite(conditional.WhenFalse, resolver),
            },
            ConditionalMemberAccessPatchExpression { Receiver: not null } conditionalAccess =>
                conditionalAccess with
                {
                    Receiver = Rewrite(conditionalAccess.Receiver, resolver),
                },
            _ => expression,
        };
    }

    private static void CollectProducedLocalReferences(
        PatchExpression expression,
        ICollection<ProducedLocalReferencePatchExpression> references)
    {
        switch (expression)
        {
            case ProducedLocalReferencePatchExpression produced:
                references.Add(produced);
                break;
            case UnaryPatchExpression { Operand: not null } unary:
                CollectProducedLocalReferences(unary.Operand, references);
                break;
            case BinaryPatchExpression { Left: not null, Right: not null } binary:
                CollectProducedLocalReferences(binary.Left, references);
                CollectProducedLocalReferences(binary.Right, references);
                break;
            case CastPatchExpression { Operand: not null } cast:
                CollectProducedLocalReferences(cast.Operand, references);
                break;
            case ConditionalPatchExpression
            {
                Condition: not null,
                WhenTrue: not null,
                WhenFalse: not null,
            } conditional:
                CollectProducedLocalReferences(conditional.Condition, references);
                CollectProducedLocalReferences(conditional.WhenTrue, references);
                CollectProducedLocalReferences(conditional.WhenFalse, references);
                break;
            case ConditionalMemberAccessPatchExpression { Receiver: not null } conditionalAccess:
                CollectProducedLocalReferences(conditionalAccess.Receiver, references);
                break;
        }
    }
}
