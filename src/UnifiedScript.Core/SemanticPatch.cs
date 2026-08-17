using System.Text.Json.Serialization;

namespace UnifiedScript.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(ReplaceLiteralPatchOperation), "replaceLiteral")]
[JsonDerivedType(typeof(ChangeOperatorPatchOperation), "changeOperator")]
[JsonDerivedType(typeof(ReconnectArgumentPatchOperation), "reconnectArgument")]
[JsonDerivedType(typeof(InsertStatementPatchOperation), "insertStatement")]
[JsonDerivedType(typeof(DeleteStatementPatchOperation), "deleteStatement")]
[JsonDerivedType(typeof(IntroduceTemporaryPatchOperation), "introduceTemporary")]
[JsonDerivedType(typeof(ReconnectValuePatchOperation), "reconnectValue")]
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
[JsonDerivedType(typeof(LocalAssignmentPatchStatement), "localAssignment")]
[JsonDerivedType(typeof(IfPatchStatement), "if")]
[JsonDerivedType(typeof(WhilePatchStatement), "while")]
[JsonDerivedType(typeof(BreakPatchStatement), "break")]
[JsonDerivedType(typeof(ContinuePatchStatement), "continue")]
[JsonDerivedType(typeof(ReturnPatchStatement), "return")]
public abstract record InsertPatchStatement;

public sealed record LocalLiteralDeclarationPatchStatement(
    string Name,
    string TypeName,
    string ValueText,
    SemanticId ExpectedTypeId) : InsertPatchStatement;

public sealed record LocalAssignmentPatchStatement(
    SemanticId TargetDefinitionId,
    string ExpectedName,
    SemanticId ExpectedTypeId,
    PatchExpression Value) : InsertPatchStatement;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "expression")]
[JsonDerivedType(typeof(LiteralPatchExpression), "literal")]
[JsonDerivedType(typeof(LocalReferencePatchExpression), "localReference")]
[JsonDerivedType(typeof(UnaryPatchExpression), "unary")]
[JsonDerivedType(typeof(BinaryPatchExpression), "binary")]
[JsonDerivedType(typeof(CastPatchExpression), "cast")]
public abstract record PatchExpression(SemanticId ExpectedTypeId);

public sealed record LiteralPatchExpression(
    string ValueText,
    SemanticId ExpectedTypeId) : PatchExpression(ExpectedTypeId);

public sealed record LocalReferencePatchExpression(
    SemanticId SourceDefinitionId,
    string ExpectedName,
    SemanticId ExpectedTypeId) : PatchExpression(ExpectedTypeId);

public sealed record UnaryPatchExpression(
    string Operator,
    PatchExpression Operand,
    SemanticId ExpectedTypeId) : PatchExpression(ExpectedTypeId);

public sealed record BinaryPatchExpression(
    string Operator,
    PatchExpression Left,
    PatchExpression Right,
    SemanticId ExpectedTypeId) : PatchExpression(ExpectedTypeId);

public sealed record CastPatchExpression(
    string TypeName,
    PatchExpression Operand,
    SemanticId ExpectedTypeId) : PatchExpression(ExpectedTypeId);

public sealed record IfPatchStatement(
    PatchExpression Condition) : InsertPatchStatement;

public sealed record WhilePatchStatement(
    PatchExpression Condition) : InsertPatchStatement;

public sealed record BreakPatchStatement : InsertPatchStatement;

public sealed record ContinuePatchStatement : InsertPatchStatement;

public sealed record ReturnPatchStatement(
    PatchExpression? Expression,
    SemanticId ExpectedReturnTypeId) : InsertPatchStatement;

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

public sealed record IntroduceTemporaryPatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedContainingStatementId,
    SemanticId ExpectedParentBlockId,
    SemanticId? ExpectedPreviousSiblingId,
    SemanticId? ExpectedNextSiblingId,
    string Name,
    string TypeName,
    SemanticId ExpectedTypeId) : SemanticPatchOperation(TargetNodeId);

public sealed record ReconnectValuePatchOperation(
    SemanticId TargetNodeId,
    SemanticId SourceDefinitionId,
    SemanticId ExpectedContainingStatementId,
    SemanticId ExpectedParentBlockId,
    SemanticId? ExpectedPreviousSiblingId,
    SemanticId? ExpectedNextSiblingId,
    SemanticId ExpectedTargetTypeId,
    SemanticId ExpectedSourceTypeId) : SemanticPatchOperation(TargetNodeId);

public sealed record SemanticPatchDocument(
    string SchemaVersion,
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId,
    IReadOnlyList<SemanticPatchOperation> Operations)
{
    public const string CurrentSchemaVersion = "unifiedscript.patch/v0.9";

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
