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

            if (operation is ReplaceLiteralPatchOperation replace)
            {
                if (replace.ExpectedValueText is null || replace.NewValueText is null)
                {
                    throw new JsonException("Literal patch values cannot be null.");
                }

                if (replace.NewValueText.Length == 0)
                {
                    throw new JsonException("A literal replacement cannot be empty.");
                }
            }
            else
            {
                throw new JsonException(
                    $"Unsupported semantic patch operation '{operation.GetType().Name}'.");
            }
        }
    }
}
