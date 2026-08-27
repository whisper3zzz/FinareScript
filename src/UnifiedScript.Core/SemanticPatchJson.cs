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
                case ReplaceDoWhileConditionPatchOperation replaceDoWhileCondition:
                    ValidateReplaceDoWhileCondition(replaceDoWhileCondition);
                    break;
                case ReplaceForConditionPatchOperation replaceForCondition:
                    ValidateReplaceForCondition(replaceForCondition);
                    break;
                case ReplaceForEachCollectionPatchOperation replaceForEachCollection:
                    ValidateReplaceForEachCollection(replaceForEachCollection);
                    break;
                case ReplaceForDeclarationInitializerPatchOperation replaceForDeclaration:
                    ValidateReplaceForDeclarationInitializer(replaceForDeclaration);
                    break;
                case ReplaceForAssignmentInitializerValuePatchOperation replaceForAssignment:
                    ValidateReplaceForAssignmentInitializerValue(replaceForAssignment);
                    break;
                case ReplaceForIncrementorPatchOperation replaceForIncrementor:
                    ValidateReplaceForIncrementor(replaceForIncrementor);
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
                case MoveStatementPatchOperation moveStatement:
                    ValidateMoveStatement(moveStatement);
                    break;
                case DuplicateStatementPatchOperation duplicateStatement:
                    ValidateDuplicateStatement(duplicateStatement);
                    break;
                case InsertSwitchSectionPatchOperation insertSwitchSection:
                    ValidateInsertSwitchSection(insertSwitchSection);
                    break;
                case ReplaceSwitchSectionPatchOperation replaceSwitchSection:
                    ValidateReplaceSwitchSection(replaceSwitchSection);
                    break;
                case MoveSwitchSectionPatchOperation moveSwitchSection:
                    ValidateMoveSwitchSection(moveSwitchSection);
                    break;
                case DeleteSwitchSectionPatchOperation deleteSwitchSection:
                    ValidateDeleteSwitchSection(deleteSwitchSection);
                    break;
                case IntroduceTemporaryPatchOperation introduceTemporary:
                    ValidateIntroduceTemporary(introduceTemporary);
                    break;
                case ReconnectValuePatchOperation reconnectValue:
                    ValidateReconnectValue(reconnectValue);
                    break;
                case ReconnectDataEdgePatchOperation reconnectDataEdge:
                    ValidateReconnectDataEdge(reconnectDataEdge);
                    break;
                case DisconnectDataEdgePatchOperation disconnectDataEdge:
                    ValidateDisconnectDataEdge(disconnectDataEdge);
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
        if (inputPort is "left" or "right" or "operand" or "receiver" or
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

    private static void ValidateReplaceForCondition(
        ReplaceForConditionPatchOperation operation)
    {
        if (operation.ExpectedConditionNodeId.IsEmpty ||
            operation.ExpectedConditionTypeId.IsEmpty ||
            operation.NewCondition is null)
        {
            throw new JsonException(
                "For-condition replacement requires the current condition endpoint, expected type, " +
                "and a typed new condition.");
        }

        if (operation.NewCondition.ExpectedTypeId != operation.ExpectedConditionTypeId)
        {
            throw new JsonException(
                "A for replacement condition must declare the expected condition type.");
        }

        ValidatePatchExpression(operation.NewCondition, 0);
    }

    private static void ValidateReplaceDoWhileCondition(
        ReplaceDoWhileConditionPatchOperation operation)
    {
        if (operation.ExpectedConditionNodeId.IsEmpty ||
            operation.ExpectedConditionTypeId.IsEmpty ||
            operation.NewCondition is null)
        {
            throw new JsonException(
                "Do-while-condition replacement requires the current condition endpoint, expected " +
                "type, and a typed new condition.");
        }

        if (operation.NewCondition.ExpectedTypeId != operation.ExpectedConditionTypeId)
        {
            throw new JsonException(
                "A do-while replacement condition must declare the expected condition type.");
        }

        ValidatePatchExpression(operation.NewCondition, 0);
    }

    private static void ValidateReplaceForEachCollection(
        ReplaceForEachCollectionPatchOperation operation)
    {
        if (operation.ExpectedCollectionNodeId.IsEmpty ||
            operation.ExpectedCollectionTypeId.IsEmpty ||
            operation.NewCollection is null)
        {
            throw new JsonException(
                "Foreach-collection replacement requires the current collection endpoint, " +
                "expected collection type, and a typed new collection.");
        }

        if (operation.NewCollection.ExpectedTypeId != operation.ExpectedCollectionTypeId)
        {
            throw new JsonException(
                "A foreach replacement collection must preserve the exact collection type.");
        }

        ValidatePatchExpression(operation.NewCollection, 0);
    }

    private static void ValidateReplaceForDeclarationInitializer(
        ReplaceForDeclarationInitializerPatchOperation operation)
    {
        if (operation.ExpectedForStatementId.IsEmpty ||
            string.IsNullOrWhiteSpace(operation.ExpectedName) ||
            operation.ExpectedInitializerNodeId.IsEmpty ||
            operation.ExpectedTypeId.IsEmpty ||
            operation.NewInitializer is null)
        {
            throw new JsonException(
                "For declaration-initializer replacement requires the owning for statement, " +
                "expected variable name, current initializer endpoint, exact type, and a typed value.");
        }

        if (operation.NewInitializer.ExpectedTypeId != operation.ExpectedTypeId)
        {
            throw new JsonException(
                "A for declaration initializer must declare the expected variable type.");
        }

        ValidatePatchExpression(operation.NewInitializer, 0);
    }

    private static void ValidateReplaceForAssignmentInitializerValue(
        ReplaceForAssignmentInitializerValuePatchOperation operation)
    {
        if (operation.InitializerIndex < 0)
        {
            throw new JsonException("A for assignment-initializer index cannot be negative.");
        }

        if (!string.Equals(operation.ExpectedOperator, "=", StringComparison.Ordinal))
        {
            throw new JsonException(
                "For assignment-initializer replacement requires the simple '=' operator.");
        }

        if (operation.ExpectedForStatementId.IsEmpty ||
            operation.ExpectedTargetNodeId.IsEmpty ||
            operation.ExpectedTargetDefinitionId.IsEmpty ||
            string.IsNullOrWhiteSpace(operation.ExpectedTargetName) ||
            operation.ExpectedValueNodeId.IsEmpty ||
            operation.ExpectedTypeId.IsEmpty ||
            operation.NewValue is null)
        {
            throw new JsonException(
                "For assignment-initializer replacement requires its owning loop and index, " +
                "target/value endpoints, target definition, expected name and exact type.");
        }

        if (operation.NewValue.ExpectedTypeId != operation.ExpectedTypeId)
        {
            throw new JsonException(
                "A for assignment initializer value must declare the expected assignment type.");
        }

        ValidatePatchExpression(operation.NewValue, 0);
    }

    private static void ValidateReplaceForIncrementor(
        ReplaceForIncrementorPatchOperation operation)
    {
        if (operation.IncrementorIndex < 0)
        {
            throw new JsonException("A for incrementor index cannot be negative.");
        }

        if (operation.ExpectedForStatementId.IsEmpty ||
            operation.ExpectedOperandNodeId.IsEmpty ||
            operation.ExpectedOperandDefinitionId.IsEmpty ||
            string.IsNullOrWhiteSpace(operation.ExpectedOperandName) ||
            operation.ExpectedTypeId.IsEmpty ||
            operation.NewOperand is null)
        {
            throw new JsonException(
                "For-incrementor replacement requires its owning loop and index, operand endpoint, " +
                "operand definition, expected name and exact type, and a typed new operand.");
        }

        if (operation.ExpectedOperator is not ("++" or "--") ||
            operation.NewOperator is not ("++" or "--"))
        {
            throw new JsonException("For incrementors currently support only '++' and '--'.");
        }

        if (operation.NewOperand is not LocalReferencePatchExpression newOperand ||
            newOperand.ExpectedTypeId != operation.ExpectedTypeId)
        {
            throw new JsonException(
                "A replacement for incrementor operand must be a local reference of the exact type.");
        }

        bool operandChanges = newOperand.SourceDefinitionId != operation.ExpectedOperandDefinitionId ||
            !string.Equals(
                newOperand.ExpectedName,
                operation.ExpectedOperandName,
                StringComparison.Ordinal);
        if (!operandChanges &&
            operation.NewOperator == operation.ExpectedOperator &&
            operation.NewIsPostfix == operation.ExpectedIsPostfix)
        {
            throw new JsonException("A for incrementor replacement must change the mutation.");
        }

        ValidatePatchExpression(operation.NewOperand, 0);
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
            case LocalDeclarationPatchStatement local:
                if (string.IsNullOrWhiteSpace(local.Name) ||
                    string.IsNullOrWhiteSpace(local.TypeName) ||
                    local.Initializer is null ||
                    local.ExpectedTypeId.IsEmpty ||
                    local.Initializer.ExpectedTypeId != local.ExpectedTypeId)
                {
                    throw new JsonException(
                        "A local insertion requires a name, explicit type, and same-typed initializer.");
                }

                ValidatePatchExpression(local.Initializer, 0);
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
            case DoWhilePatchStatement doWhileStatement:
                if (doWhileStatement.Condition is null)
                {
                    throw new JsonException(
                        "A do-while insertion requires a typed condition expression.");
                }

                ValidatePatchExpression(doWhileStatement.Condition, 0);
                break;
            case ForPatchStatement forStatement:
                ValidateForStatement(forStatement);
                break;
            case ForEachPatchStatement forEachStatement:
                if (string.IsNullOrWhiteSpace(forEachStatement.TypeName) ||
                    string.IsNullOrWhiteSpace(forEachStatement.VariableName) ||
                    forEachStatement.ExpectedElementTypeId.IsEmpty ||
                    forEachStatement.Collection is null)
                {
                    throw new JsonException(
                        "A foreach insertion requires an explicit element type, variable name, " +
                        "element type ID, and typed collection expression.");
                }

                ValidatePatchExpression(forEachStatement.Collection, 0);
                break;
            case SwitchPatchStatement switchStatement:
                if (switchStatement.GoverningExpression is null ||
                    switchStatement.ExpectedGoverningTypeId.IsEmpty ||
                    switchStatement.GoverningExpression.ExpectedTypeId !=
                    switchStatement.ExpectedGoverningTypeId)
                {
                    throw new JsonException(
                        "A switch insertion requires a governing expression with its exact expected type.");
                }

                ValidatePatchExpression(switchStatement.GoverningExpression, 0);
                ValidateSwitchSections(switchStatement.Sections);
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

    private static void ValidateForStatement(ForPatchStatement statement)
    {
        if (statement.Initializers is null || statement.Incrementors is null)
        {
            throw new JsonException(
                "A for insertion requires non-null initializer and incrementor lists.");
        }

        if (statement.Declaration is not null && statement.Initializers.Count > 0)
        {
            throw new JsonException(
                "A for insertion cannot combine a declaration initializer with expression initializers.");
        }

        IReadOnlyList<ForVariableDeclaratorPatch> variables =
            statement.Declaration?.Variables ?? [];
        if (statement.Declaration is not null)
        {
            if (string.IsNullOrWhiteSpace(statement.Declaration.TypeName) ||
                statement.Declaration.Variables is null ||
                statement.Declaration.Variables.Count != 1)
            {
                throw new JsonException(
                    "The bounded for constructor requires one typed declaration variable.");
            }

            ForVariableDeclaratorPatch variable = statement.Declaration.Variables[0];
            if (string.IsNullOrWhiteSpace(variable.Name) ||
                variable.ExpectedTypeId.IsEmpty ||
                variable.Initializer is null ||
                variable.Initializer.ExpectedTypeId != variable.ExpectedTypeId)
            {
                throw new JsonException(
                    "A for declaration variable requires a name, expected type, and same-typed initializer.");
            }

            ValidatePatchExpression(variable.Initializer, 0, []);
        }

        foreach (ForAssignmentPatchExpression? initializer in statement.Initializers)
        {
            if (initializer is null ||
                initializer.TargetDefinitionId.IsEmpty ||
                string.IsNullOrWhiteSpace(initializer.ExpectedName) ||
                initializer.ExpectedTypeId.IsEmpty ||
                initializer.Value is null ||
                initializer.Value.ExpectedTypeId != initializer.ExpectedTypeId)
            {
                throw new JsonException(
                    "A for assignment initializer requires an existing target, expected name/type, " +
                    "and same-typed value.");
            }

            ValidatePatchExpression(initializer.Value, 0);
        }

        if (statement.Condition is not null)
        {
            ValidatePatchExpression(statement.Condition, 0, variables);
        }

        foreach (ForMutationPatchExpression? incrementor in statement.Incrementors)
        {
            if (incrementor is null ||
                incrementor.Operand is not (
                    LocalReferencePatchExpression or ForLocalReferencePatchExpression) ||
                incrementor.Operator is not ("++" or "--") ||
                incrementor.ExpectedTypeId.IsEmpty ||
                incrementor.Operand.ExpectedTypeId != incrementor.ExpectedTypeId)
            {
                throw new JsonException(
                    "A for incrementor requires a local/for-local operand, ++ or --, and one exact type.");
            }

            ValidatePatchExpression(incrementor.Operand, 0, variables);
        }
    }

    private static void ValidatePatchExpression(
        PatchExpression expression,
        int depth,
        IReadOnlyList<ForVariableDeclaratorPatch>? forVariables = null)
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
            case DefaultPatchExpression defaultExpression:
                if (string.IsNullOrWhiteSpace(defaultExpression.TypeName))
                {
                    throw new JsonException(
                        "A graph-authored default expression requires an explicit type name.");
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
            case ForLocalReferencePatchExpression forLocal:
                if (forVariables is null ||
                    forLocal.VariableIndex < 0 ||
                    forLocal.VariableIndex >= forVariables.Count ||
                    string.IsNullOrWhiteSpace(forLocal.ExpectedName) ||
                    !string.Equals(
                        forLocal.ExpectedName,
                        forVariables[forLocal.VariableIndex].Name,
                        StringComparison.Ordinal) ||
                    forLocal.ExpectedTypeId !=
                        forVariables[forLocal.VariableIndex].ExpectedTypeId)
                {
                    throw new JsonException(
                        "A for-local reference must resolve by index to the declared name and exact type.");
                }

                break;
            case UnaryPatchExpression unary:
                if (string.IsNullOrWhiteSpace(unary.Operator) || unary.Operand is null)
                {
                    throw new JsonException(
                        "A graph-authored unary expression requires an operator and operand.");
                }

                ValidatePatchExpression(unary.Operand, depth + 1, forVariables);
                break;
            case BinaryPatchExpression binary:
                if (string.IsNullOrWhiteSpace(binary.Operator) ||
                    binary.Left is null ||
                    binary.Right is null)
                {
                    throw new JsonException(
                        "A graph-authored binary expression requires an operator and two operands.");
                }

                ValidatePatchExpression(binary.Left, depth + 1, forVariables);
                ValidatePatchExpression(binary.Right, depth + 1, forVariables);
                break;
            case CastPatchExpression cast:
                if (string.IsNullOrWhiteSpace(cast.TypeName) || cast.Operand is null)
                {
                    throw new JsonException(
                        "A graph-authored cast requires a type name and operand.");
                }

                ValidatePatchExpression(cast.Operand, depth + 1, forVariables);
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

                ValidatePatchExpression(conditional.Condition, depth + 1, forVariables);
                ValidatePatchExpression(conditional.WhenTrue, depth + 1, forVariables);
                ValidatePatchExpression(conditional.WhenFalse, depth + 1, forVariables);
                break;
            case ConditionalMemberAccessPatchExpression conditionalMemberAccess:
                if (conditionalMemberAccess.Receiver is null ||
                    string.IsNullOrWhiteSpace(conditionalMemberAccess.MemberName))
                {
                    throw new JsonException(
                        "A graph-authored conditional member access requires a receiver and member name.");
                }

                ValidatePatchExpression(
                    conditionalMemberAccess.Receiver,
                    depth + 1,
                    forVariables);
                break;
            case MemberAccessPatchExpression memberAccess:
                if (memberAccess.Receiver is null ||
                    string.IsNullOrWhiteSpace(memberAccess.MemberName))
                {
                    throw new JsonException(
                        "A graph-authored member access requires a receiver and member name.");
                }

                ValidatePatchExpression(memberAccess.Receiver, depth + 1, forVariables);
                break;
            case MemberInvocationPatchExpression invocation:
                if (invocation.Receiver is null ||
                    string.IsNullOrWhiteSpace(invocation.MemberName) ||
                    invocation.Arguments is null ||
                    invocation.Arguments.Any(static argument => argument is null))
                {
                    throw new JsonException(
                        "A graph-authored member invocation requires a receiver, member name, and arguments.");
                }

                ValidatePatchExpression(invocation.Receiver, depth + 1, forVariables);
                foreach (PatchExpression argument in invocation.Arguments)
                {
                    ValidatePatchExpression(argument, depth + 1, forVariables);
                }
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

    private static void ValidateMoveStatement(MoveStatementPatchOperation operation)
    {
        if (operation.ExpectedSourceParentBlockId.IsEmpty ||
            operation.DestinationParentBlockId.IsEmpty)
        {
            throw new JsonException(
                "Statement movement requires non-empty source and destination block IDs.");
        }

        ValidateSiblingIds(
            operation.ExpectedSourcePreviousSiblingId,
            operation.ExpectedSourceNextSiblingId);
        ValidateSiblingTarget(
            operation.TargetNodeId,
            operation.ExpectedSourcePreviousSiblingId,
            operation.ExpectedSourceNextSiblingId,
            "moved statement");
        ValidateSiblingIds(
            operation.ExpectedDestinationPreviousSiblingId,
            operation.ExpectedDestinationNextSiblingId);
        ValidateSiblingTarget(
            operation.TargetNodeId,
            operation.ExpectedDestinationPreviousSiblingId,
            operation.ExpectedDestinationNextSiblingId,
            "destination gap");
    }

    private static void ValidateDuplicateStatement(DuplicateStatementPatchOperation operation)
    {
        if (operation.ExpectedParentBlockId.IsEmpty)
        {
            throw new JsonException(
                "Statement duplication requires a non-empty parent block ID.");
        }

        ValidateSiblingIds(
            operation.ExpectedSourcePreviousSiblingId,
            operation.ExpectedSourceNextSiblingId);
        ValidateSiblingTarget(
            operation.TargetNodeId,
            operation.ExpectedSourcePreviousSiblingId,
            operation.ExpectedSourceNextSiblingId,
            "copied statement");
        ValidateSiblingIds(
            operation.ExpectedDestinationPreviousSiblingId,
            operation.ExpectedDestinationNextSiblingId);
        if (operation.ExpectedDestinationPreviousSiblingId is null &&
            operation.ExpectedDestinationNextSiblingId is null)
        {
            throw new JsonException(
                "Statement duplication requires an exact non-empty destination gap.");
        }
        if (operation.NewLocalName is not null &&
            (string.IsNullOrWhiteSpace(operation.NewLocalName) || operation.NewLocalName.Length > 128))
        {
            throw new JsonException(
                "A duplicated local name must be non-empty and at most 128 characters.");
        }
    }

    private static void ValidateInsertSwitchSection(
        InsertSwitchSectionPatchOperation operation)
    {
        ValidateSiblingIds(
            operation.ExpectedPreviousSectionId,
            operation.ExpectedNextSectionId);
        ValidateSwitchSections([operation.NewSection]);
    }

    private static void ValidateReplaceSwitchSection(
        ReplaceSwitchSectionPatchOperation operation)
    {
        if (operation.ExpectedSwitchStatementId.IsEmpty ||
            operation.ExpectedLabelIds is null ||
            operation.ExpectedLabelIds.Count == 0 ||
            operation.ExpectedLabelIds.Any(static id => id.IsEmpty) ||
            operation.ExpectedLabelIds.Distinct().Count() != operation.ExpectedLabelIds.Count)
        {
            throw new JsonException(
                "Switch-section replacement requires one parent switch and distinct expected label IDs.");
        }

        ValidateSwitchLabels(operation.NewLabels);
    }

    private static void ValidateMoveSwitchSection(MoveSwitchSectionPatchOperation operation)
    {
        if (operation.ExpectedSwitchStatementId.IsEmpty)
        {
            throw new JsonException("Switch-section movement requires an expected parent switch ID.");
        }

        ValidateSiblingIds(
            operation.ExpectedSourcePreviousSectionId,
            operation.ExpectedSourceNextSectionId);
        ValidateSiblingTarget(
            operation.TargetNodeId,
            operation.ExpectedSourcePreviousSectionId,
            operation.ExpectedSourceNextSectionId,
            "moved switch section");
        ValidateSiblingIds(
            operation.ExpectedDestinationPreviousSectionId,
            operation.ExpectedDestinationNextSectionId);
        ValidateSiblingTarget(
            operation.TargetNodeId,
            operation.ExpectedDestinationPreviousSectionId,
            operation.ExpectedDestinationNextSectionId,
            "switch-section destination gap");
    }

    private static void ValidateDeleteSwitchSection(
        DeleteSwitchSectionPatchOperation operation)
    {
        if (operation.ExpectedSwitchStatementId.IsEmpty ||
            operation.ExpectedLabelIds is null ||
            operation.ExpectedLabelIds.Count == 0 ||
            operation.ExpectedLabelIds.Any(static id => id.IsEmpty) ||
            operation.ExpectedLabelIds.Distinct().Count() != operation.ExpectedLabelIds.Count)
        {
            throw new JsonException(
                "Switch-section deletion requires one parent switch and distinct expected label IDs.");
        }

        ValidateSiblingIds(operation.ExpectedPreviousSectionId, operation.ExpectedNextSectionId);
        ValidateSiblingTarget(
            operation.TargetNodeId,
            operation.ExpectedPreviousSectionId,
            operation.ExpectedNextSectionId,
            "deleted switch section");
    }

    private static void ValidateSwitchSections(IReadOnlyList<SwitchSectionPatch>? sections)
    {
        if (sections is null || sections.Count == 0 || sections.Count > 64 ||
            sections.Any(static section => section is null))
        {
            throw new JsonException(
                "A graph-authored switch requires between one and 64 non-null sections.");
        }

        foreach (SwitchSectionPatch section in sections)
        {
            ValidateSwitchLabels(section.Labels);
        }

        if (sections.SelectMany(static section => section.Labels)
            .Count(static label => label is DefaultSwitchLabelPatch) > 1)
        {
            throw new JsonException("A graph-authored switch can contain at most one default label.");
        }
    }

    private static void ValidateSwitchLabels(IReadOnlyList<SwitchLabelPatch>? labels)
    {
        if (labels is null || labels.Count == 0 || labels.Count > 32 ||
            labels.Any(static label => label is null))
        {
            throw new JsonException(
                "A graph-authored switch section requires between one and 32 non-null labels.");
        }

        foreach (SwitchLabelPatch label in labels)
        {
            switch (label)
            {
                case DefaultSwitchLabelPatch:
                    break;
                case PatternSwitchLabelPatch pattern:
                    if (pattern.Pattern is null)
                    {
                        throw new JsonException("A switch pattern label requires a pattern.");
                    }

                    ValidateSwitchPattern(pattern.Pattern, 0);
                    if (pattern.Guard is not null)
                    {
                        ValidatePatchExpression(pattern.Guard, 0);
                    }
                    break;
                default:
                    throw new JsonException(
                        $"Unsupported graph-authored switch label '{label.GetType().Name}'.");
            }
        }
    }

    private static void ValidateSwitchPattern(SwitchPatternPatch pattern, int depth)
    {
        if (depth > 16)
        {
            throw new JsonException("A graph-authored switch pattern cannot exceed depth 16.");
        }

        switch (pattern)
        {
            case ConstantSwitchPatternPatch constant when constant.Value is not null:
                ValidatePatchExpression(constant.Value, 0);
                break;
            case DeclarationSwitchPatternPatch declaration:
                if (string.IsNullOrWhiteSpace(declaration.TypeName) ||
                    declaration.ExpectedTypeId.IsEmpty ||
                    declaration.IsDiscard != string.IsNullOrWhiteSpace(declaration.VariableName))
                {
                    throw new JsonException(
                        "A declaration pattern requires a type and either one variable or a discard.");
                }
                break;
            case TypeSwitchPatternPatch type:
                if (string.IsNullOrWhiteSpace(type.TypeName) || type.ExpectedTypeId.IsEmpty)
                {
                    throw new JsonException("A type pattern requires a type and expected type ID.");
                }
                break;
            case VarSwitchPatternPatch varPattern:
                if (varPattern.IsDiscard != string.IsNullOrWhiteSpace(varPattern.VariableName))
                {
                    throw new JsonException(
                        "A var pattern requires either one variable name or a discard.");
                }
                break;
            case DiscardSwitchPatternPatch:
                break;
            case RelationalSwitchPatternPatch relational:
                if (relational.Operator is not ("<" or "<=" or ">" or ">=") ||
                    relational.Value is null)
                {
                    throw new JsonException(
                        "A relational pattern requires <, <=, >, or >= and one typed value.");
                }
                ValidatePatchExpression(relational.Value, 0);
                break;
            case LogicalSwitchPatternPatch logical:
                if (logical.Operator is not ("and" or "or") ||
                    logical.Left is null || logical.Right is null)
                {
                    throw new JsonException(
                        "A logical pattern requires 'and' or 'or' and two operands.");
                }
                ValidateSwitchPattern(logical.Left, depth + 1);
                ValidateSwitchPattern(logical.Right, depth + 1);
                break;
            case NotSwitchPatternPatch notPattern when notPattern.Operand is not null:
                ValidateSwitchPattern(notPattern.Operand, depth + 1);
                break;
            case ParenthesizedSwitchPatternPatch parenthesized
                when parenthesized.Inner is not null:
                ValidateSwitchPattern(parenthesized.Inner, depth + 1);
                break;
            default:
                throw new JsonException(
                    $"Unsupported graph-authored switch pattern '{pattern.GetType().Name}'.");
        }
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

    private static void ValidateReconnectDataEdge(ReconnectDataEdgePatchOperation operation)
    {
        if (operation.ExpectedEdgeId.IsEmpty || operation.ExpectedSourceNodeId.IsEmpty ||
            operation.NewSourceNodeId.IsEmpty || operation.ExpectedTypeId.IsEmpty ||
            string.IsNullOrWhiteSpace(operation.TargetPort) ||
            string.IsNullOrWhiteSpace(operation.ExpectedSourcePort) ||
            string.IsNullOrWhiteSpace(operation.NewSourcePort))
        {
            throw new JsonException(
                "A data-edge reconnection requires exact edge, endpoint, port, and type preconditions.");
        }

        if (operation.TargetNodeId == operation.NewSourceNodeId ||
            (operation.ExpectedSourceNodeId == operation.NewSourceNodeId &&
                string.Equals(
                    operation.ExpectedSourcePort,
                    operation.NewSourcePort,
                    StringComparison.Ordinal)))
        {
            throw new JsonException(
                "A data-edge reconnection must select a distinct, non-self source endpoint.");
        }
    }

    private static void ValidateDisconnectDataEdge(DisconnectDataEdgePatchOperation operation)
    {
        if (operation.ExpectedEdgeId.IsEmpty || operation.ExpectedSourceNodeId.IsEmpty ||
            operation.ExpectedTypeId.IsEmpty || string.IsNullOrWhiteSpace(operation.TargetPort) ||
            string.IsNullOrWhiteSpace(operation.ExpectedSourcePort))
        {
            throw new JsonException(
                "A data-edge disconnection requires exact edge, endpoint, port, and type preconditions.");
        }
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
