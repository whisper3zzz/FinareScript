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
                case ReplaceExpressionInputPatchOperation replaceExpressionInput:
                    ValidateReplaceExpressionInput(replaceExpressionInput);
                    break;
                case ReplaceAssignmentValuePatchOperation replaceAssignmentValue:
                    ValidateReplaceAssignmentValue(replaceAssignmentValue);
                    break;
                case ReplaceWhileConditionPatchOperation replaceWhileCondition:
                    ValidateReplaceWhileCondition(replaceWhileCondition);
                    break;
                case ReplaceIfConditionPatchOperation replaceIfCondition:
                    ValidateReplaceIfCondition(replaceIfCondition);
                    break;
                case ReplaceLocalInitializerPatchOperation replaceLocalInitializer:
                    ValidateReplaceLocalInitializer(replaceLocalInitializer);
                    break;
                case ReplaceReturnValuePatchOperation replaceReturnValue:
                    ValidateReplaceReturnValue(replaceReturnValue);
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

    private static void ValidateReplaceExpressionInput(
        ReplaceExpressionInputPatchOperation operation)
    {
        if (!IsSupportedExpressionInputPort(operation.InputPort))
        {
            throw new JsonException(
                "Expression-input replacement requires a canonical 'left', 'right', 'operand', " +
                "'condition', 'whenTrue', 'whenFalse', or zero-based 'argN' Graph input port.");
        }

        if (operation.ExpectedInputNodeId.IsEmpty ||
            operation.ExpectedInputTypeId.IsEmpty ||
            operation.ExpectedTargetTypeId.IsEmpty ||
            operation.NewInput is null)
        {
            throw new JsonException(
                "Expression-input replacement requires current input, input type, target type, " +
                "and typed new-input preconditions.");
        }

        if (operation.TargetNodeId == operation.ExpectedInputNodeId)
        {
            throw new JsonException(
                "An expression input cannot also be its own target expression.");
        }

        if (operation.NewInput.ExpectedTypeId != operation.ExpectedInputTypeId)
        {
            throw new JsonException(
                "A replacement expression input must declare the expected input-port type.");
        }

        ValidatePatchExpression(operation.NewInput, 0);
    }

    private static bool IsSupportedExpressionInputPort(string? inputPort)
    {
        if (inputPort is "left" or "right" or "operand" or
            "condition" or "whenTrue" or "whenFalse")
        {
            return true;
        }

        return inputPort is not null &&
            inputPort.StartsWith("arg", StringComparison.Ordinal) &&
            int.TryParse(inputPort.AsSpan(3), out int argumentIndex) &&
            argumentIndex >= 0 &&
            string.Equals(inputPort, $"arg{argumentIndex}", StringComparison.Ordinal);
    }

    private static void ValidateReplaceAssignmentValue(
        ReplaceAssignmentValuePatchOperation operation)
    {
        if (!string.Equals(operation.ExpectedOperator, "=", StringComparison.Ordinal))
        {
            throw new JsonException(
                "Assignment-value replacement currently requires the expected simple '=' operator.");
        }

        if (operation.ExpectedTargetNodeId.IsEmpty ||
            operation.ExpectedTargetDefinitionId.IsEmpty ||
            string.IsNullOrWhiteSpace(operation.ExpectedTargetName) ||
            operation.ExpectedValueNodeId.IsEmpty ||
            operation.ExpectedTypeId.IsEmpty ||
            operation.NewValue is null)
        {
            throw new JsonException(
                "Assignment-value replacement requires target/value endpoints, the target definition, " +
                "expected name and type, and a typed new value.");
        }

        if (operation.NewValue.ExpectedTypeId != operation.ExpectedTypeId)
        {
            throw new JsonException(
                "An assignment replacement value must declare the expected assignment type.");
        }

        ValidatePatchExpression(operation.NewValue, 0);
    }

    private static void ValidateReplaceWhileCondition(
        ReplaceWhileConditionPatchOperation operation)
    {
        if (operation.ExpectedConditionNodeId.IsEmpty ||
            operation.ExpectedConditionTypeId.IsEmpty ||
            operation.NewCondition is null)
        {
            throw new JsonException(
                "While-condition replacement requires the current condition endpoint, expected type, " +
                "and a typed new condition.");
        }

        if (operation.NewCondition.ExpectedTypeId != operation.ExpectedConditionTypeId)
        {
            throw new JsonException(
                "A while replacement condition must declare the expected condition type.");
        }

        ValidatePatchExpression(operation.NewCondition, 0);
    }

    private static void ValidateReplaceIfCondition(
        ReplaceIfConditionPatchOperation operation)
    {
        if (operation.ExpectedConditionNodeId.IsEmpty ||
            operation.ExpectedConditionTypeId.IsEmpty ||
            operation.NewCondition is null)
        {
            throw new JsonException(
                "If-condition replacement requires the current condition endpoint, expected type, " +
                "and a typed new condition.");
        }

        if (operation.NewCondition.ExpectedTypeId != operation.ExpectedConditionTypeId)
        {
            throw new JsonException(
                "An if replacement condition must declare the expected condition type.");
        }

        ValidatePatchExpression(operation.NewCondition, 0);
    }

    private static void ValidateReplaceLocalInitializer(
        ReplaceLocalInitializerPatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.ExpectedName) ||
            operation.ExpectedInitializerNodeId.IsEmpty ||
            operation.ExpectedTypeId.IsEmpty ||
            operation.NewInitializer is null)
        {
            throw new JsonException(
                "Local-initializer replacement requires the expected local name, current initializer " +
                "endpoint, semantic type, and a typed new initializer.");
        }

        if (operation.NewInitializer.ExpectedTypeId != operation.ExpectedTypeId)
        {
            throw new JsonException(
                "A replacement local initializer must declare the expected local type.");
        }

        ValidatePatchExpression(operation.NewInitializer, 0);
    }

    private static void ValidateReplaceReturnValue(
        ReplaceReturnValuePatchOperation operation)
    {
        if (operation.ExpectedValueNodeId.IsEmpty ||
            operation.ExpectedReturnTypeId.IsEmpty ||
            operation.NewValue is null)
        {
            throw new JsonException(
                "Return-value replacement requires the current value endpoint, expected method return " +
                "type, and a typed new value.");
        }

        if (operation.NewValue.ExpectedTypeId != operation.ExpectedReturnTypeId)
        {
            throw new JsonException(
                "A replacement return value must declare the expected method return type.");
        }

        ValidatePatchExpression(operation.NewValue, 0);
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
            case ProducedLocalReferencePatchExpression:
                throw new JsonException(
                    "A produced-local reference is transaction-only and cannot appear in a " +
                    "standalone semantic patch.");
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
            case ConditionalPatchExpression conditional:
                if (conditional.Condition is null ||
                    conditional.WhenTrue is null ||
                    conditional.WhenFalse is null)
                {
                    throw new JsonException(
                        "A graph-authored conditional expression requires a condition and two branches.");
                }

                if (conditional.WhenTrue.ExpectedTypeId != conditional.ExpectedTypeId ||
                    conditional.WhenFalse.ExpectedTypeId != conditional.ExpectedTypeId)
                {
                    throw new JsonException(
                        "Both graph-authored conditional branches must declare the result type.");
                }

                ValidatePatchExpression(conditional.Condition, depth + 1);
                ValidatePatchExpression(conditional.WhenTrue, depth + 1);
                ValidatePatchExpression(conditional.WhenFalse, depth + 1);
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
