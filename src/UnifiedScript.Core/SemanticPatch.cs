using System.Text.Json.Serialization;

namespace UnifiedScript.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(ReplaceLiteralPatchOperation), "replaceLiteral")]
[JsonDerivedType(typeof(ChangeOperatorPatchOperation), "changeOperator")]
[JsonDerivedType(typeof(ReconnectArgumentPatchOperation), "reconnectArgument")]
[JsonDerivedType(typeof(ReplaceExpressionInputPatchOperation), "replaceExpressionInput")]
[JsonDerivedType(typeof(ReplaceAssignmentValuePatchOperation), "replaceAssignmentValue")]
[JsonDerivedType(typeof(ReplaceWhileConditionPatchOperation), "replaceWhileCondition")]
[JsonDerivedType(typeof(ReplaceIfConditionPatchOperation), "replaceIfCondition")]
[JsonDerivedType(typeof(ReplaceLocalInitializerPatchOperation), "replaceLocalInitializer")]
[JsonDerivedType(typeof(ReplaceReturnValuePatchOperation), "replaceReturnValue")]
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

public sealed record ReplaceExpressionInputPatchOperation(
    SemanticId TargetNodeId,
    string InputPort,
    SemanticId ExpectedInputNodeId,
    SemanticId ExpectedInputTypeId,
    SemanticId ExpectedTargetTypeId,
    PatchExpression NewInput) : SemanticPatchOperation(TargetNodeId);

public sealed record ReplaceAssignmentValuePatchOperation(
    SemanticId TargetNodeId,
    string ExpectedOperator,
    SemanticId ExpectedTargetNodeId,
    SemanticId ExpectedTargetDefinitionId,
    string ExpectedTargetName,
    SemanticId ExpectedValueNodeId,
    SemanticId ExpectedTypeId,
    PatchExpression NewValue) : SemanticPatchOperation(TargetNodeId);

public sealed record ReplaceWhileConditionPatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedConditionNodeId,
    SemanticId ExpectedConditionTypeId,
    PatchExpression NewCondition) : SemanticPatchOperation(TargetNodeId);

public sealed record ReplaceIfConditionPatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedConditionNodeId,
    SemanticId ExpectedConditionTypeId,
    PatchExpression NewCondition) : SemanticPatchOperation(TargetNodeId);

public sealed record ReplaceLocalInitializerPatchOperation(
    SemanticId TargetNodeId,
    string ExpectedName,
    SemanticId ExpectedInitializerNodeId,
    SemanticId ExpectedTypeId,
    PatchExpression NewInitializer) : SemanticPatchOperation(TargetNodeId);

public sealed record ReplaceReturnValuePatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedValueNodeId,
    SemanticId ExpectedReturnTypeId,
    PatchExpression NewValue) : SemanticPatchOperation(TargetNodeId);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "statement")]
[JsonDerivedType(typeof(LocalLiteralDeclarationPatchStatement), "localLiteralDeclaration")]
[JsonDerivedType(typeof(LocalAssignmentPatchStatement), "localAssignment")]
[JsonDerivedType(typeof(IfPatchStatement), "if")]
[JsonDerivedType(typeof(WhilePatchStatement), "while")]
[JsonDerivedType(typeof(ForPatchStatement), "for")]
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
[JsonDerivedType(typeof(ProducedLocalReferencePatchExpression), "producedLocalReference")]
[JsonDerivedType(typeof(ForLocalReferencePatchExpression), "forLocalReference")]
[JsonDerivedType(typeof(UnaryPatchExpression), "unary")]
[JsonDerivedType(typeof(BinaryPatchExpression), "binary")]
[JsonDerivedType(typeof(CastPatchExpression), "cast")]
[JsonDerivedType(typeof(ConditionalPatchExpression), "conditional")]
[JsonDerivedType(typeof(ConditionalMemberAccessPatchExpression), "conditionalMemberAccess")]
public abstract record PatchExpression(SemanticId ExpectedTypeId);

public sealed record LiteralPatchExpression(
    string ValueText,
    SemanticId ExpectedTypeId) : PatchExpression(ExpectedTypeId);

public sealed record LocalReferencePatchExpression(
    SemanticId SourceDefinitionId,
    string ExpectedName,
    SemanticId ExpectedTypeId) : PatchExpression(ExpectedTypeId);

public sealed record ProducedLocalReferencePatchExpression(
    string SourceStepId,
    string ExpectedName,
    SemanticId ExpectedTypeId) : PatchExpression(ExpectedTypeId);

public sealed record ForLocalReferencePatchExpression(
    int VariableIndex,
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

public sealed record ConditionalPatchExpression(
    PatchExpression Condition,
    PatchExpression WhenTrue,
    PatchExpression WhenFalse,
    SemanticId ExpectedTypeId) : PatchExpression(ExpectedTypeId);

public sealed record ConditionalMemberAccessPatchExpression(
    PatchExpression Receiver,
    string MemberName,
    SemanticId ExpectedTypeId) : PatchExpression(ExpectedTypeId);

public sealed record IfPatchStatement(
    PatchExpression Condition) : InsertPatchStatement;

public sealed record WhilePatchStatement(
    PatchExpression Condition) : InsertPatchStatement;

public sealed record ForVariableDeclarationPatch(
    string TypeName,
    IReadOnlyList<ForVariableDeclaratorPatch> Variables);

public sealed record ForVariableDeclaratorPatch(
    string Name,
    PatchExpression Initializer,
    SemanticId ExpectedTypeId);

public sealed record ForAssignmentPatchExpression(
    SemanticId TargetDefinitionId,
    string ExpectedName,
    SemanticId ExpectedTypeId,
    PatchExpression Value);

public sealed record ForMutationPatchExpression(
    PatchExpression Operand,
    string Operator,
    bool IsPostfix,
    SemanticId ExpectedTypeId);

public sealed record ForPatchStatement(
    ForVariableDeclarationPatch? Declaration,
    IReadOnlyList<ForAssignmentPatchExpression> Initializers,
    PatchExpression? Condition,
    IReadOnlyList<ForMutationPatchExpression> Incrementors) : InsertPatchStatement;

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
    public const string CurrentSchemaVersion = "unifiedscript.patch/v0.15";

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
