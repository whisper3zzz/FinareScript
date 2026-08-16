using System.Text.Json.Serialization;

namespace UnifiedScript.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(ReplaceLiteralPatchOperation), "replaceLiteral")]
[JsonDerivedType(typeof(ChangeOperatorPatchOperation), "changeOperator")]
[JsonDerivedType(typeof(ReconnectArgumentPatchOperation), "reconnectArgument")]
[JsonDerivedType(typeof(InsertStatementPatchOperation), "insertStatement")]
[JsonDerivedType(typeof(DeleteStatementPatchOperation), "deleteStatement")]
public abstract record SemanticPatchOperation(SemanticId TargetNodeId);

public sealed record ReplaceLiteralPatchOperation(
    SemanticId TargetNodeId,
    string ExpectedValueText,
    string NewValueText) : SemanticPatchOperation(TargetNodeId);

public sealed record ChangeOperatorPatchOperation(
    SemanticId TargetNodeId,
    string ExpectedOperator,
    string NewOperator,
    SemanticId ExpectedResultTypeId,
    SemanticId ExpectedOperandTypeId,
    SemanticId? ExpectedRightOperandTypeId = null) : SemanticPatchOperation(TargetNodeId);

public sealed record ReconnectArgumentPatchOperation(
    SemanticId TargetNodeId,
    int ArgumentIndex,
    SemanticId ExpectedArgumentNodeId,
    int SourceArgumentIndex,
    SemanticId ExpectedSourceArgumentNodeId,
    SemanticId ExpectedArgumentTypeId,
    SemanticId ExpectedSourceArgumentTypeId) : SemanticPatchOperation(TargetNodeId);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "statement")]
[JsonDerivedType(typeof(LocalLiteralDeclarationPatchStatement), "localLiteralDeclaration")]
public abstract record InsertPatchStatement;

public sealed record LocalLiteralDeclarationPatchStatement(
    string Name,
    string TypeName,
    string ValueText,
    SemanticId ExpectedTypeId) : InsertPatchStatement;

public sealed record InsertStatementPatchOperation(
    SemanticId TargetNodeId,
    SemanticId? ExpectedPreviousSiblingId,
    SemanticId? ExpectedNextSiblingId,
    InsertPatchStatement NewStatement) : SemanticPatchOperation(TargetNodeId);

public sealed record DeleteStatementPatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedParentBlockId,
    SemanticId? ExpectedPreviousSiblingId,
    SemanticId? ExpectedNextSiblingId) : SemanticPatchOperation(TargetNodeId);

public sealed record SemanticPatchDocument(
    string SchemaVersion,
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId,
    IReadOnlyList<SemanticPatchOperation> Operations)
{
    public const string CurrentSchemaVersion = "unifiedscript.patch/v0.3";

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
