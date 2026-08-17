using System.Text.Json;

namespace UnifiedScript.Core;

public static class SemanticPatchJson
{
    public static string Serialize(SemanticPatchDocument document, bool writeIndented = true)
    {
        Validate(document);
        return JsonSerializer.Serialize(document, UnifiedJson.CreateOptions(writeIndented));
    }

    public static SemanticPatchDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        SemanticPatchDocument? document = JsonSerializer.Deserialize<SemanticPatchDocument>(
            json,
            UnifiedJson.CreateOptions());
        if (document is null)
        {
            throw new JsonException("The semantic patch document was empty.");
        }

        Validate(document);
        return document;
    }

    public static void Validate(SemanticPatchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != SemanticPatchDocument.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported semantic patch schema '{document.SchemaVersion}'. " +
                $"Expected '{SemanticPatchDocument.CurrentSchemaVersion}'.");
        }

        if (document.SourceCompilationId.IsEmpty)
        {
            throw new JsonException("A semantic patch requires a non-empty source compilation ID.");
        }

        if (document.SemanticConfigurationId.IsEmpty)
        {
            throw new JsonException("A semantic patch requires a non-empty semantic configuration ID.");
        }

        if (document.Operations is null || document.Operations.Count == 0)
        {
            throw new JsonException("A semantic patch requires at least one operation.");
        }

        var targets = new HashSet<SemanticId>();
        foreach (SemanticPatchOperation? operation in document.Operations)
        {
            if (operation is null)
            {
                throw new JsonException("Semantic patch operations cannot be null.");
            }

            if (operation.TargetNodeId.IsEmpty)
            {
                throw new JsonException("A semantic patch operation requires a target node ID.");
            }

            if (!targets.Add(operation.TargetNodeId))
            {
                throw new JsonException(
                    $"A semantic patch cannot target node '{operation.TargetNodeId}' more than once.");
            }

            switch (operation)
            {
                case ReplaceLiteralPatchOperation replace:
                    ValidateReplaceLiteral(replace);
                    break;
                case ChangeOperatorPatchOperation changeOperator:
                    ValidateChangeOperator(changeOperator);
                    break;
                case ReconnectArgumentPatchOperation reconnectArgument:
                    ValidateReconnectArgument(reconnectArgument);
                    break;
                case InsertStatementPatchOperation insertStatement:
                    ValidateInsertStatement(insertStatement);
                    break;
                case DeleteStatementPatchOperation deleteStatement:
                    ValidateDeleteStatement(deleteStatement);
                    break;
                case IntroduceTemporaryPatchOperation introduceTemporary:
                    ValidateIntroduceTemporary(introduceTemporary);
                    break;
                case ReconnectValuePatchOperation reconnectValue:
                    ValidateReconnectValue(reconnectValue);
                    break;
                default:
                    throw new JsonException(
                        $"Unsupported semantic patch operation '{operation.GetType().Name}'.");
            }
        }
    }

    private static void ValidateReplaceLiteral(ReplaceLiteralPatchOperation operation)
    {
        if (operation.ExpectedValueText is null || operation.NewValueText is null)
        {
            throw new JsonException("Literal patch values cannot be null.");
        }

        if (operation.NewValueText.Length == 0)
        {
            throw new JsonException("A literal replacement cannot be empty.");
        }
    }

    private static void ValidateChangeOperator(ChangeOperatorPatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.ExpectedOperator) ||
            string.IsNullOrWhiteSpace(operation.NewOperator))
        {
            throw new JsonException("An operator patch requires non-empty old and new operators.");
        }

        if (operation.ExpectedOperator == operation.NewOperator)
        {
            throw new JsonException("An operator patch must change the operator.");
        }

        if (operation.ExpectedResultTypeId.IsEmpty || operation.ExpectedOperandTypeId.IsEmpty ||
            operation.ExpectedRightOperandTypeId is SemanticId rightTypeId && rightTypeId.IsEmpty)
        {
            throw new JsonException("An operator patch requires non-empty expected semantic type IDs.");
        }
    }

    private static void ValidateReconnectArgument(ReconnectArgumentPatchOperation operation)
    {
        if (operation.ArgumentIndex < 0 || operation.SourceArgumentIndex < 0)
        {
            throw new JsonException("Argument indices cannot be negative.");
        }

        if (operation.ArgumentIndex == operation.SourceArgumentIndex)
        {
            throw new JsonException("Argument reconnection requires two distinct argument indices.");
        }

        if (operation.ExpectedArgumentNodeId.IsEmpty ||
            operation.ExpectedSourceArgumentNodeId.IsEmpty ||
            operation.ExpectedArgumentTypeId.IsEmpty ||
            operation.ExpectedSourceArgumentTypeId.IsEmpty)
        {
            throw new JsonException("Argument reconnection requires non-empty node and type preconditions.");
        }

        if (operation.ExpectedArgumentNodeId == operation.ExpectedSourceArgumentNodeId)
        {
            throw new JsonException("Argument reconnection requires two distinct argument nodes.");
        }
    }

    private static void ValidateInsertStatement(InsertStatementPatchOperation operation)
    {
        ValidateSiblingIds(
            operation.ExpectedPreviousSiblingId,
            operation.ExpectedNextSiblingId);
        if (operation.NewStatement is null)
        {
            throw new JsonException("Statement insertion requires a typed statement payload.");
        }

        switch (operation.NewStatement)
        {
            case LocalLiteralDeclarationPatchStatement local:
                if (string.IsNullOrWhiteSpace(local.Name) ||
                    string.IsNullOrWhiteSpace(local.TypeName) ||
                    string.IsNullOrWhiteSpace(local.ValueText) ||
                    local.ExpectedTypeId.IsEmpty)
                {
                    throw new JsonException(
                        "A local-literal declaration requires a name, type, value, and expected type ID.");
                }

                break;
            case LocalAssignmentPatchStatement assignment:
                if (assignment.TargetDefinitionId.IsEmpty ||
                    string.IsNullOrWhiteSpace(assignment.ExpectedName) ||
                    assignment.ExpectedTypeId.IsEmpty ||
                    assignment.Value is null)
                {
                    throw new JsonException(
                        "A local assignment requires a target definition, expected name and type, " +
                        "and typed value expression.");
                }

                if (assignment.Value.ExpectedTypeId != assignment.ExpectedTypeId)
                {
                    throw new JsonException(
                        "A local assignment value must declare the same expected type as its target.");
                }

                ValidatePatchExpression(assignment.Value, 0);
                break;
            case IfPatchStatement ifStatement:
                if (ifStatement.Condition is null)
                {
                    throw new JsonException("An if insertion requires a typed condition expression.");
                }

                ValidatePatchExpression(ifStatement.Condition, 0);
                break;
            case WhilePatchStatement whileStatement:
                if (whileStatement.Condition is null)
                {
                    throw new JsonException("A while insertion requires a typed condition expression.");
                }

                ValidatePatchExpression(whileStatement.Condition, 0);
                break;
            case BreakPatchStatement:
            case ContinuePatchStatement:
                break;
            case ReturnPatchStatement returnStatement:
                if (returnStatement.ExpectedReturnTypeId.IsEmpty)
                {
                    throw new JsonException(
                        "A return insertion requires a non-empty expected method return type ID.");
                }

                if (returnStatement.Expression is not null)
                {
                    ValidatePatchExpression(returnStatement.Expression, 0);
                }

                break;
            default:
                throw new JsonException(
                    $"Unsupported inserted statement payload '{operation.NewStatement.GetType().Name}'.");
        }
    }

    private static void ValidatePatchExpression(PatchExpression expression, int depth)
    {
        if (depth > 16)
        {
            throw new JsonException("A graph-authored expression cannot exceed 16 nested nodes.");
        }

        if (expression.ExpectedTypeId.IsEmpty)
        {
            throw new JsonException("Every graph-authored expression requires an expected type ID.");
        }

        switch (expression)
        {
            case LiteralPatchExpression literal:
                if (string.IsNullOrWhiteSpace(literal.ValueText))
                {
                    throw new JsonException("A graph-authored literal requires a value.");
                }

                break;
            case LocalReferencePatchExpression local:
                if (local.SourceDefinitionId.IsEmpty || string.IsNullOrWhiteSpace(local.ExpectedName))
                {
                    throw new JsonException(
                        "A graph-authored local reference requires a source definition and expected name.");
                }

                break;
            case UnaryPatchExpression unary:
                if (string.IsNullOrWhiteSpace(unary.Operator) || unary.Operand is null)
                {
                    throw new JsonException(
                        "A graph-authored unary expression requires an operator and operand.");
                }

                ValidatePatchExpression(unary.Operand, depth + 1);
                break;
            case BinaryPatchExpression binary:
                if (string.IsNullOrWhiteSpace(binary.Operator) ||
                    binary.Left is null ||
                    binary.Right is null)
                {
                    throw new JsonException(
                        "A graph-authored binary expression requires an operator and two operands.");
                }

                ValidatePatchExpression(binary.Left, depth + 1);
                ValidatePatchExpression(binary.Right, depth + 1);
                break;
            case CastPatchExpression cast:
                if (string.IsNullOrWhiteSpace(cast.TypeName) || cast.Operand is null)
                {
                    throw new JsonException(
                        "A graph-authored cast requires a type name and operand.");
                }

                ValidatePatchExpression(cast.Operand, depth + 1);
                break;
            default:
                throw new JsonException(
                    $"Unsupported graph-authored expression '{expression.GetType().Name}'.");
        }
    }

    private static void ValidateDeleteStatement(DeleteStatementPatchOperation operation)
    {
        if (operation.ExpectedParentBlockId.IsEmpty)
        {
            throw new JsonException("Statement deletion requires an expected parent block ID.");
        }

        ValidateSiblingIds(
            operation.ExpectedPreviousSiblingId,
            operation.ExpectedNextSiblingId);
        ValidateSiblingTarget(
            operation.TargetNodeId,
            operation.ExpectedPreviousSiblingId,
            operation.ExpectedNextSiblingId,
            "deleted statement");
    }

    private static void ValidateIntroduceTemporary(IntroduceTemporaryPatchOperation operation)
    {
        if (operation.ExpectedContainingStatementId.IsEmpty ||
            operation.ExpectedParentBlockId.IsEmpty ||
            operation.ExpectedTypeId.IsEmpty)
        {
            throw new JsonException(
                "Temporary introduction requires non-empty statement, parent block, and type IDs.");
        }

        if (string.IsNullOrWhiteSpace(operation.Name) ||
            string.IsNullOrWhiteSpace(operation.TypeName))
        {
            throw new JsonException("Temporary introduction requires a name and type name.");
        }

        ValidateSiblingIds(
            operation.ExpectedPreviousSiblingId,
            operation.ExpectedNextSiblingId);
        ValidateSiblingTarget(
            operation.ExpectedContainingStatementId,
            operation.ExpectedPreviousSiblingId,
            operation.ExpectedNextSiblingId,
            "containing statement");
    }

    private static void ValidateReconnectValue(ReconnectValuePatchOperation operation)
    {
        if (operation.SourceDefinitionId.IsEmpty ||
            operation.ExpectedContainingStatementId.IsEmpty ||
            operation.ExpectedParentBlockId.IsEmpty ||
            operation.ExpectedTargetTypeId.IsEmpty ||
            operation.ExpectedSourceTypeId.IsEmpty)
        {
            throw new JsonException(
                "Value reconnection requires non-empty source definition, statement, block, and type IDs.");
        }

        if (operation.SourceDefinitionId == operation.TargetNodeId)
        {
            throw new JsonException("A value reconnection source definition cannot be its target expression.");
        }

        ValidateSiblingIds(
            operation.ExpectedPreviousSiblingId,
            operation.ExpectedNextSiblingId);
        ValidateSiblingTarget(
            operation.ExpectedContainingStatementId,
            operation.ExpectedPreviousSiblingId,
            operation.ExpectedNextSiblingId,
            "containing statement");
    }

    private static void ValidateSiblingIds(
        SemanticId? previousSiblingId,
        SemanticId? nextSiblingId)
    {
        if ((previousSiblingId is SemanticId previous && previous.IsEmpty) ||
            (nextSiblingId is SemanticId next && next.IsEmpty))
        {
            throw new JsonException("Sibling preconditions cannot contain empty semantic IDs.");
        }

        if (previousSiblingId is not null && previousSiblingId == nextSiblingId)
        {
            throw new JsonException("Previous and next sibling preconditions must identify different nodes.");
        }
    }

    private static void ValidateSiblingTarget(
        SemanticId targetId,
        SemanticId? previousSiblingId,
        SemanticId? nextSiblingId,
        string targetRole)
    {
        if (previousSiblingId == targetId || nextSiblingId == targetId)
        {
            throw new JsonException($"A {targetRole} cannot also be its own sibling precondition.");
        }
    }
}
