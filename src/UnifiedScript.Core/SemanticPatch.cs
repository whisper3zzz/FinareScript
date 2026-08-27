using System.Text.Json.Serialization;

namespace UnifiedScript.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(ReplaceLiteralPatchOperation), "replaceLiteral")]
[JsonDerivedType(typeof(ChangeOperatorPatchOperation), "changeOperator")]
[JsonDerivedType(typeof(ReconnectArgumentPatchOperation), "reconnectArgument")]
[JsonDerivedType(typeof(ReplaceExpressionInputPatchOperation), "replaceExpressionInput")]
[JsonDerivedType(typeof(ReplaceAssignmentValuePatchOperation), "replaceAssignmentValue")]
[JsonDerivedType(typeof(ReplaceWhileConditionPatchOperation), "replaceWhileCondition")]
[JsonDerivedType(typeof(ReplaceDoWhileConditionPatchOperation), "replaceDoWhileCondition")]
[JsonDerivedType(typeof(ReplaceForConditionPatchOperation), "replaceForCondition")]
[JsonDerivedType(typeof(ReplaceForEachCollectionPatchOperation), "replaceForEachCollection")]
[JsonDerivedType(
    typeof(ReplaceForDeclarationInitializerPatchOperation),
    "replaceForDeclarationInitializer")]
[JsonDerivedType(
    typeof(ReplaceForAssignmentInitializerValuePatchOperation),
    "replaceForAssignmentInitializerValue")]
[JsonDerivedType(typeof(ReplaceForIncrementorPatchOperation), "replaceForIncrementor")]
[JsonDerivedType(typeof(ReplaceIfConditionPatchOperation), "replaceIfCondition")]
[JsonDerivedType(typeof(ReplaceLocalInitializerPatchOperation), "replaceLocalInitializer")]
[JsonDerivedType(typeof(ReplaceReturnValuePatchOperation), "replaceReturnValue")]
[JsonDerivedType(typeof(InsertStatementPatchOperation), "insertStatement")]
[JsonDerivedType(typeof(DeleteStatementPatchOperation), "deleteStatement")]
[JsonDerivedType(typeof(MoveStatementPatchOperation), "moveStatement")]
[JsonDerivedType(typeof(DuplicateStatementPatchOperation), "duplicateStatement")]
[JsonDerivedType(typeof(CollapseStatementRangePatchOperation), "collapseStatements")]
[JsonDerivedType(typeof(ExpandBlockPatchOperation), "expandBlock")]
[JsonDerivedType(typeof(InsertSwitchSectionPatchOperation), "insertSwitchSection")]
[JsonDerivedType(typeof(ReplaceSwitchSectionPatchOperation), "replaceSwitchSection")]
[JsonDerivedType(typeof(MoveSwitchSectionPatchOperation), "moveSwitchSection")]
[JsonDerivedType(typeof(DeleteSwitchSectionPatchOperation), "deleteSwitchSection")]
[JsonDerivedType(typeof(IntroduceTemporaryPatchOperation), "introduceTemporary")]
[JsonDerivedType(typeof(ReconnectValuePatchOperation), "reconnectValue")]
[JsonDerivedType(typeof(ReconnectDataEdgePatchOperation), "reconnectDataEdge")]
[JsonDerivedType(typeof(DisconnectDataEdgePatchOperation), "disconnectDataEdge")]
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

public sealed record ReplaceDoWhileConditionPatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedConditionNodeId,
    SemanticId ExpectedConditionTypeId,
    PatchExpression NewCondition) : SemanticPatchOperation(TargetNodeId);

public sealed record ReplaceForConditionPatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedConditionNodeId,
    SemanticId ExpectedConditionTypeId,
    PatchExpression NewCondition) : SemanticPatchOperation(TargetNodeId);

public sealed record ReplaceForEachCollectionPatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedCollectionNodeId,
    SemanticId ExpectedCollectionTypeId,
    PatchExpression NewCollection) : SemanticPatchOperation(TargetNodeId);

public sealed record ReplaceForDeclarationInitializerPatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedForStatementId,
    string ExpectedName,
    SemanticId ExpectedInitializerNodeId,
    SemanticId ExpectedTypeId,
    PatchExpression NewInitializer) : SemanticPatchOperation(TargetNodeId);

public sealed record ReplaceForAssignmentInitializerValuePatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedForStatementId,
    int InitializerIndex,
    string ExpectedOperator,
    SemanticId ExpectedTargetNodeId,
    SemanticId ExpectedTargetDefinitionId,
    string ExpectedTargetName,
    SemanticId ExpectedValueNodeId,
    SemanticId ExpectedTypeId,
    PatchExpression NewValue) : SemanticPatchOperation(TargetNodeId);

public sealed record ReplaceForIncrementorPatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedForStatementId,
    int IncrementorIndex,
    string ExpectedOperator,
    bool ExpectedIsPostfix,
    SemanticId ExpectedOperandNodeId,
    SemanticId ExpectedOperandDefinitionId,
    string ExpectedOperandName,
    SemanticId ExpectedTypeId,
    string NewOperator,
    bool NewIsPostfix,
    PatchExpression NewOperand) : SemanticPatchOperation(TargetNodeId);

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
[JsonDerivedType(typeof(LocalDeclarationPatchStatement), "localDeclaration")]
[JsonDerivedType(typeof(LocalAssignmentPatchStatement), "localAssignment")]
[JsonDerivedType(typeof(IfPatchStatement), "if")]
[JsonDerivedType(typeof(WhilePatchStatement), "while")]
[JsonDerivedType(typeof(DoWhilePatchStatement), "doWhile")]
[JsonDerivedType(typeof(ForPatchStatement), "for")]
[JsonDerivedType(typeof(ForEachPatchStatement), "forEach")]
[JsonDerivedType(typeof(SwitchPatchStatement), "switch")]
[JsonDerivedType(typeof(BreakPatchStatement), "break")]
[JsonDerivedType(typeof(ContinuePatchStatement), "continue")]
[JsonDerivedType(typeof(ReturnPatchStatement), "return")]
public abstract record InsertPatchStatement;

public sealed record LocalLiteralDeclarationPatchStatement(
    string Name,
    string TypeName,
    string ValueText,
    SemanticId ExpectedTypeId) : InsertPatchStatement;

public sealed record LocalDeclarationPatchStatement(
    string Name,
    string TypeName,
    PatchExpression Initializer,
    SemanticId ExpectedTypeId) : InsertPatchStatement;

public sealed record LocalAssignmentPatchStatement(
    SemanticId TargetDefinitionId,
    string ExpectedName,
    SemanticId ExpectedTypeId,
    PatchExpression Value) : InsertPatchStatement;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "expression")]
[JsonDerivedType(typeof(LiteralPatchExpression), "literal")]
[JsonDerivedType(typeof(DefaultPatchExpression), "default")]
[JsonDerivedType(typeof(LocalReferencePatchExpression), "localReference")]
[JsonDerivedType(typeof(ProducedLocalReferencePatchExpression), "producedLocalReference")]
[JsonDerivedType(typeof(ForLocalReferencePatchExpression), "forLocalReference")]
[JsonDerivedType(typeof(UnaryPatchExpression), "unary")]
[JsonDerivedType(typeof(BinaryPatchExpression), "binary")]
[JsonDerivedType(typeof(CastPatchExpression), "cast")]
[JsonDerivedType(typeof(ConditionalPatchExpression), "conditional")]
[JsonDerivedType(typeof(ConditionalMemberAccessPatchExpression), "conditionalMemberAccess")]
[JsonDerivedType(typeof(MemberAccessPatchExpression), "memberAccess")]
[JsonDerivedType(typeof(MemberInvocationPatchExpression), "memberInvocation")]
public abstract record PatchExpression(SemanticId ExpectedTypeId);

public sealed record LiteralPatchExpression(
    string ValueText,
    SemanticId ExpectedTypeId) : PatchExpression(ExpectedTypeId);

public sealed record DefaultPatchExpression(
    string TypeName,
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

public sealed record MemberAccessPatchExpression(
    PatchExpression Receiver,
    string MemberName,
    SemanticId ExpectedTypeId) : PatchExpression(ExpectedTypeId);

public sealed record MemberInvocationPatchExpression(
    PatchExpression Receiver,
    string MemberName,
    IReadOnlyList<PatchExpression> Arguments,
    SemanticId ExpectedTypeId) : PatchExpression(ExpectedTypeId);

public sealed record IfPatchStatement(
    PatchExpression Condition) : InsertPatchStatement;

public sealed record WhilePatchStatement(
    PatchExpression Condition) : InsertPatchStatement;

public sealed record DoWhilePatchStatement(
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

public sealed record ForEachPatchStatement(
    string TypeName,
    string VariableName,
    SemanticId ExpectedElementTypeId,
    PatchExpression Collection) : InsertPatchStatement;

public sealed record SwitchPatchStatement(
    PatchExpression GoverningExpression,
    SemanticId ExpectedGoverningTypeId,
    IReadOnlyList<SwitchSectionPatch> Sections) : InsertPatchStatement;

public sealed record SwitchSectionPatch(IReadOnlyList<SwitchLabelPatch> Labels);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "label")]
[JsonDerivedType(typeof(DefaultSwitchLabelPatch), "default")]
[JsonDerivedType(typeof(PatternSwitchLabelPatch), "pattern")]
public abstract record SwitchLabelPatch;

public sealed record DefaultSwitchLabelPatch : SwitchLabelPatch;

public sealed record PatternSwitchLabelPatch(
    SwitchPatternPatch Pattern,
    PatchExpression? Guard) : SwitchLabelPatch;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "pattern")]
[JsonDerivedType(typeof(ConstantSwitchPatternPatch), "constant")]
[JsonDerivedType(typeof(DeclarationSwitchPatternPatch), "declaration")]
[JsonDerivedType(typeof(TypeSwitchPatternPatch), "type")]
[JsonDerivedType(typeof(VarSwitchPatternPatch), "var")]
[JsonDerivedType(typeof(DiscardSwitchPatternPatch), "discard")]
[JsonDerivedType(typeof(RelationalSwitchPatternPatch), "relational")]
[JsonDerivedType(typeof(LogicalSwitchPatternPatch), "logical")]
[JsonDerivedType(typeof(NotSwitchPatternPatch), "not")]
[JsonDerivedType(typeof(ParenthesizedSwitchPatternPatch), "parenthesized")]
public abstract record SwitchPatternPatch;

public sealed record ConstantSwitchPatternPatch(PatchExpression Value) : SwitchPatternPatch;

public sealed record DeclarationSwitchPatternPatch(
    string TypeName,
    string VariableName,
    bool IsDiscard,
    SemanticId ExpectedTypeId) : SwitchPatternPatch;

public sealed record TypeSwitchPatternPatch(
    string TypeName,
    SemanticId ExpectedTypeId) : SwitchPatternPatch;

public sealed record VarSwitchPatternPatch(
    string VariableName,
    bool IsDiscard) : SwitchPatternPatch;

public sealed record DiscardSwitchPatternPatch : SwitchPatternPatch;

public sealed record RelationalSwitchPatternPatch(
    string Operator,
    PatchExpression Value) : SwitchPatternPatch;

public sealed record LogicalSwitchPatternPatch(
    string Operator,
    SwitchPatternPatch Left,
    SwitchPatternPatch Right) : SwitchPatternPatch;

public sealed record NotSwitchPatternPatch(SwitchPatternPatch Operand) : SwitchPatternPatch;

public sealed record ParenthesizedSwitchPatternPatch(
    SwitchPatternPatch Inner) : SwitchPatternPatch;

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

public sealed record MoveStatementPatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedSourceParentBlockId,
    SemanticId? ExpectedSourcePreviousSiblingId,
    SemanticId? ExpectedSourceNextSiblingId,
    SemanticId DestinationParentBlockId,
    SemanticId? ExpectedDestinationPreviousSiblingId,
    SemanticId? ExpectedDestinationNextSiblingId) : SemanticPatchOperation(TargetNodeId);

public sealed record DuplicateStatementPatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedParentBlockId,
    SemanticId? ExpectedSourcePreviousSiblingId,
    SemanticId? ExpectedSourceNextSiblingId,
    SemanticId? ExpectedDestinationPreviousSiblingId,
    SemanticId? ExpectedDestinationNextSiblingId,
    string? NewLocalName) : SemanticPatchOperation(TargetNodeId);

public sealed record CollapseStatementRangePatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedParentBlockId,
    IReadOnlyList<SemanticId> ExpectedStatementIds,
    SemanticId? ExpectedPreviousSiblingId,
    SemanticId? ExpectedNextSiblingId) : SemanticPatchOperation(TargetNodeId);

public sealed record ExpandBlockPatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedParentBlockId,
    IReadOnlyList<SemanticId> ExpectedChildStatementIds,
    SemanticId? ExpectedPreviousSiblingId,
    SemanticId? ExpectedNextSiblingId) : SemanticPatchOperation(TargetNodeId);

public sealed record InsertSwitchSectionPatchOperation(
    SemanticId TargetNodeId,
    SemanticId? ExpectedPreviousSectionId,
    SemanticId? ExpectedNextSectionId,
    SwitchSectionPatch NewSection) : SemanticPatchOperation(TargetNodeId);

public sealed record ReplaceSwitchSectionPatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedSwitchStatementId,
    IReadOnlyList<SemanticId> ExpectedLabelIds,
    IReadOnlyList<SwitchLabelPatch> NewLabels) : SemanticPatchOperation(TargetNodeId);

public sealed record MoveSwitchSectionPatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedSwitchStatementId,
    SemanticId? ExpectedSourcePreviousSectionId,
    SemanticId? ExpectedSourceNextSectionId,
    SemanticId? ExpectedDestinationPreviousSectionId,
    SemanticId? ExpectedDestinationNextSectionId) : SemanticPatchOperation(TargetNodeId);

public sealed record DeleteSwitchSectionPatchOperation(
    SemanticId TargetNodeId,
    SemanticId ExpectedSwitchStatementId,
    SemanticId? ExpectedPreviousSectionId,
    SemanticId? ExpectedNextSectionId,
    IReadOnlyList<SemanticId> ExpectedLabelIds) : SemanticPatchOperation(TargetNodeId);

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

public sealed record ReconnectDataEdgePatchOperation(
    SemanticId TargetNodeId,
    string TargetPort,
    SemanticId ExpectedEdgeId,
    SemanticId ExpectedSourceNodeId,
    string ExpectedSourcePort,
    SemanticId NewSourceNodeId,
    string NewSourcePort,
    SemanticId ExpectedTypeId) : SemanticPatchOperation(TargetNodeId);

public sealed record DisconnectDataEdgePatchOperation(
    SemanticId TargetNodeId,
    string TargetPort,
    SemanticId ExpectedEdgeId,
    SemanticId ExpectedSourceNodeId,
    string ExpectedSourcePort,
    SemanticId ExpectedTypeId) : SemanticPatchOperation(TargetNodeId);

public sealed record SemanticPatchDocument(
    string SchemaVersion,
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId,
    IReadOnlyList<SemanticPatchOperation> Operations)
{
    public const string CurrentSchemaVersion = "unifiedscript.patch/v0.25";

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
