using System.Text.Json.Serialization;

namespace UnifiedScript.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(ReplaceLiteralPatchOperation), "replaceLiteral")]
public abstract record SemanticPatchOperation(SemanticId TargetNodeId);

public sealed record ReplaceLiteralPatchOperation(
    SemanticId TargetNodeId,
    string ExpectedValueText,
    string NewValueText) : SemanticPatchOperation(TargetNodeId);

public sealed record SemanticPatchDocument(
    string SchemaVersion,
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId,
    IReadOnlyList<SemanticPatchOperation> Operations)
{
    public const string CurrentSchemaVersion = "unifiedscript.patch/v0.1";

    public static SemanticPatchDocument Create(
        SemanticId sourceCompilationId,
        SemanticId semanticConfigurationId,
        IEnumerable<SemanticPatchOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var document = new SemanticPatchDocument(
            CurrentSchemaVersion,
            sourceCompilationId,
            semanticConfigurationId,
            operations.ToArray());
        SemanticPatchJson.Validate(document);
        return document;
    }
}
