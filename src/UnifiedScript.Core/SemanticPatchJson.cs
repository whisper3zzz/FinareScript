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
}
