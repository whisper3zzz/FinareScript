using System.Text.Json.Serialization;

namespace UnifiedScript.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "step")]
[JsonDerivedType(typeof(ReplaceLiteralPatchPlanStep), "replaceLiteral")]
[JsonDerivedType(typeof(ChangeOperatorPatchPlanStep), "changeOperator")]
[JsonDerivedType(typeof(ReconnectArgumentPatchPlanStep), "reconnectArgument")]
[JsonDerivedType(typeof(ReplaceExpressionInputPatchPlanStep), "replaceExpressionInput")]
[JsonDerivedType(
    typeof(ReplaceProducedExpressionInputPatchPlanStep),
    "replaceProducedExpressionInput")]
[JsonDerivedType(typeof(ReplaceAssignmentValuePatchPlanStep), "replaceAssignmentValue")]
[JsonDerivedType(typeof(ReplaceWhileConditionPatchPlanStep), "replaceWhileCondition")]
[JsonDerivedType(
    typeof(ReplaceProducedWhileConditionPatchPlanStep),
    "replaceProducedWhileCondition")]
[JsonDerivedType(typeof(ReplaceDoWhileConditionPatchPlanStep), "replaceDoWhileCondition")]
[JsonDerivedType(
    typeof(ReplaceProducedDoWhileConditionPatchPlanStep),
    "replaceProducedDoWhileCondition")]
[JsonDerivedType(typeof(ReplaceForConditionPatchPlanStep), "replaceForCondition")]
[JsonDerivedType(
    typeof(ReplaceProducedForConditionPatchPlanStep),
    "replaceProducedForCondition")]
[JsonDerivedType(typeof(ReplaceForEachCollectionPatchPlanStep), "replaceForEachCollection")]
[JsonDerivedType(
    typeof(ReplaceProducedForEachCollectionPatchPlanStep),
    "replaceProducedForEachCollection")]
[JsonDerivedType(
    typeof(ReplaceForDeclarationInitializerPatchPlanStep),
    "replaceForDeclarationInitializer")]
[JsonDerivedType(
    typeof(ReplaceForAssignmentInitializerValuePatchPlanStep),
    "replaceForAssignmentInitializerValue")]
[JsonDerivedType(typeof(ReplaceForIncrementorPatchPlanStep), "replaceForIncrementor")]
[JsonDerivedType(typeof(ReplaceIfConditionPatchPlanStep), "replaceIfCondition")]
[JsonDerivedType(
    typeof(ReplaceProducedIfConditionPatchPlanStep),
    "replaceProducedIfCondition")]
[JsonDerivedType(typeof(ReplaceLocalInitializerPatchPlanStep), "replaceLocalInitializer")]
[JsonDerivedType(
    typeof(ReplaceProducedLocalInitializerPatchPlanStep),
    "replaceProducedLocalInitializer")]
[JsonDerivedType(typeof(ReplaceReturnValuePatchPlanStep), "replaceReturnValue")]
[JsonDerivedType(
    typeof(ReplaceProducedReturnValuePatchPlanStep),
    "replaceProducedReturnValue")]
[JsonDerivedType(typeof(InsertStatementPatchPlanStep), "insertStatement")]
[JsonDerivedType(typeof(InsertBranchStatementPatchPlanStep), "insertBranchStatement")]
[JsonDerivedType(typeof(InsertLoopBodyStatementPatchPlanStep), "insertLoopBodyStatement")]
[JsonDerivedType(typeof(DeleteStatementPatchPlanStep), "deleteStatement")]
[JsonDerivedType(typeof(IntroduceTemporaryPatchPlanStep), "introduceTemporary")]
[JsonDerivedType(typeof(ReconnectValuePatchPlanStep), "reconnectValue")]
public abstract record SemanticPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId);

public sealed record ReplaceLiteralPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    string ExpectedValueText,
    string NewValueText) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public ReplaceLiteralPatchOperation ToOperation() => new(
        TargetNodeId,
        ExpectedValueText,
        NewValueText);
}

public sealed record ChangeOperatorPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    string ExpectedOperator,
    string NewOperator,
    SemanticId ExpectedResultTypeId,
    SemanticId ExpectedOperandTypeId,
    SemanticId? ExpectedRightOperandTypeId = null) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public ChangeOperatorPatchOperation ToOperation() => new(
        TargetNodeId,
        ExpectedOperator,
        NewOperator,
        ExpectedResultTypeId,
        ExpectedOperandTypeId,
        ExpectedRightOperandTypeId);
}

public sealed record ReconnectArgumentPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    int ArgumentIndex,
    SemanticId ExpectedArgumentNodeId,
    int SourceArgumentIndex,
    SemanticId ExpectedSourceArgumentNodeId,
    SemanticId ExpectedArgumentTypeId,
    SemanticId ExpectedSourceArgumentTypeId) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public ReconnectArgumentPatchOperation ToOperation() => new(
        TargetNodeId,
        ArgumentIndex,
        ExpectedArgumentNodeId,
        SourceArgumentIndex,
        ExpectedSourceArgumentNodeId,
        ExpectedArgumentTypeId,
        ExpectedSourceArgumentTypeId);
}

public sealed record ReplaceExpressionInputPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    string InputPort,
    SemanticId ExpectedInputNodeId,
    SemanticId ExpectedInputTypeId,
    SemanticId ExpectedTargetTypeId,
    PatchExpression NewInput) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public ReplaceExpressionInputPatchOperation ToOperation() => new(
        TargetNodeId,
        InputPort,
        ExpectedInputNodeId,
        ExpectedInputTypeId,
        ExpectedTargetTypeId,
        NewInput);
}

public sealed record ReplaceProducedExpressionInputPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    string SourceStepId,
    IReadOnlyList<string> TargetPath,
    string InputPort,
    SemanticId ExpectedInputTypeId,
    SemanticId ExpectedTargetTypeId,
    PatchExpression NewInput) : SemanticPatchPlanStep(StepId, TargetNodeId);

public sealed record ReplaceAssignmentValuePatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    string ExpectedOperator,
    SemanticId ExpectedTargetNodeId,
    SemanticId ExpectedTargetDefinitionId,
    string ExpectedTargetName,
    SemanticId ExpectedValueNodeId,
    SemanticId ExpectedTypeId,
    PatchExpression NewValue) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public ReplaceAssignmentValuePatchOperation ToOperation() => new(
        TargetNodeId,
        ExpectedOperator,
        ExpectedTargetNodeId,
        ExpectedTargetDefinitionId,
        ExpectedTargetName,
        ExpectedValueNodeId,
        ExpectedTypeId,
        NewValue);
}

public sealed record ReplaceWhileConditionPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    SemanticId ExpectedConditionNodeId,
    SemanticId ExpectedConditionTypeId,
    PatchExpression NewCondition) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public ReplaceWhileConditionPatchOperation ToOperation() => new(
        TargetNodeId,
        ExpectedConditionNodeId,
        ExpectedConditionTypeId,
        NewCondition);
}

public sealed record ReplaceProducedWhileConditionPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    string SourceStepId,
    PatchExpression NewCondition) : SemanticPatchPlanStep(StepId, TargetNodeId);

public sealed record ReplaceDoWhileConditionPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    SemanticId ExpectedConditionNodeId,
    SemanticId ExpectedConditionTypeId,
    PatchExpression NewCondition) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public ReplaceDoWhileConditionPatchOperation ToOperation() => new(
        TargetNodeId,
        ExpectedConditionNodeId,
        ExpectedConditionTypeId,
        NewCondition);
}

public sealed record ReplaceProducedDoWhileConditionPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    string SourceStepId,
    PatchExpression NewCondition) : SemanticPatchPlanStep(StepId, TargetNodeId);

public sealed record ReplaceForConditionPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    SemanticId ExpectedConditionNodeId,
    SemanticId ExpectedConditionTypeId,
    PatchExpression NewCondition) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public ReplaceForConditionPatchOperation ToOperation() => new(
        TargetNodeId,
        ExpectedConditionNodeId,
        ExpectedConditionTypeId,
        NewCondition);
}

public sealed record ReplaceProducedForConditionPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    string SourceStepId,
    PatchExpression NewCondition) : SemanticPatchPlanStep(StepId, TargetNodeId);

public sealed record ReplaceForEachCollectionPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    SemanticId ExpectedCollectionNodeId,
    SemanticId ExpectedCollectionTypeId,
    PatchExpression NewCollection) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public ReplaceForEachCollectionPatchOperation ToOperation() => new(
        TargetNodeId,
        ExpectedCollectionNodeId,
        ExpectedCollectionTypeId,
        NewCollection);
}

public sealed record ReplaceProducedForEachCollectionPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    string SourceStepId,
    PatchExpression NewCollection) : SemanticPatchPlanStep(StepId, TargetNodeId);

public sealed record ReplaceForDeclarationInitializerPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    SemanticId ExpectedForStatementId,
    string ExpectedName,
    SemanticId ExpectedInitializerNodeId,
    SemanticId ExpectedTypeId,
    PatchExpression NewInitializer) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public ReplaceForDeclarationInitializerPatchOperation ToOperation() => new(
        TargetNodeId,
        ExpectedForStatementId,
        ExpectedName,
        ExpectedInitializerNodeId,
        ExpectedTypeId,
        NewInitializer);
}

public sealed record ReplaceForAssignmentInitializerValuePatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    SemanticId ExpectedForStatementId,
    int InitializerIndex,
    string ExpectedOperator,
    SemanticId ExpectedTargetNodeId,
    SemanticId ExpectedTargetDefinitionId,
    string ExpectedTargetName,
    SemanticId ExpectedValueNodeId,
    SemanticId ExpectedTypeId,
    PatchExpression NewValue) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public ReplaceForAssignmentInitializerValuePatchOperation ToOperation() => new(
        TargetNodeId,
        ExpectedForStatementId,
        InitializerIndex,
        ExpectedOperator,
        ExpectedTargetNodeId,
        ExpectedTargetDefinitionId,
        ExpectedTargetName,
        ExpectedValueNodeId,
        ExpectedTypeId,
        NewValue);
}

public sealed record ReplaceForIncrementorPatchPlanStep(
    string StepId,
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
    PatchExpression NewOperand) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public ReplaceForIncrementorPatchOperation ToOperation() => new(
        TargetNodeId,
        ExpectedForStatementId,
        IncrementorIndex,
        ExpectedOperator,
        ExpectedIsPostfix,
        ExpectedOperandNodeId,
        ExpectedOperandDefinitionId,
        ExpectedOperandName,
        ExpectedTypeId,
        NewOperator,
        NewIsPostfix,
        NewOperand);
}

public sealed record ReplaceIfConditionPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    SemanticId ExpectedConditionNodeId,
    SemanticId ExpectedConditionTypeId,
    PatchExpression NewCondition) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public ReplaceIfConditionPatchOperation ToOperation() => new(
        TargetNodeId,
        ExpectedConditionNodeId,
        ExpectedConditionTypeId,
        NewCondition);
}

public sealed record ReplaceProducedIfConditionPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    string SourceStepId,
    PatchExpression NewCondition) : SemanticPatchPlanStep(StepId, TargetNodeId);

public sealed record ReplaceLocalInitializerPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    string ExpectedName,
    SemanticId ExpectedInitializerNodeId,
    SemanticId ExpectedTypeId,
    PatchExpression NewInitializer) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public ReplaceLocalInitializerPatchOperation ToOperation() => new(
        TargetNodeId,
        ExpectedName,
        ExpectedInitializerNodeId,
        ExpectedTypeId,
        NewInitializer);
}

public sealed record ReplaceProducedLocalInitializerPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    string SourceStepId,
    PatchExpression NewInitializer) : SemanticPatchPlanStep(StepId, TargetNodeId);

public sealed record ReplaceReturnValuePatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    SemanticId ExpectedValueNodeId,
    SemanticId ExpectedReturnTypeId,
    PatchExpression NewValue) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public ReplaceReturnValuePatchOperation ToOperation() => new(
        TargetNodeId,
        ExpectedValueNodeId,
        ExpectedReturnTypeId,
        NewValue);
}

public sealed record ReplaceProducedReturnValuePatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    string SourceStepId,
    PatchExpression NewValue) : SemanticPatchPlanStep(StepId, TargetNodeId);

public sealed record InsertStatementPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    SemanticId? ExpectedPreviousSiblingId,
    SemanticId? ExpectedNextSiblingId,
    InsertPatchStatement NewStatement) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public InsertStatementPatchOperation ToOperation() => new(
        TargetNodeId,
        ExpectedPreviousSiblingId,
        ExpectedNextSiblingId,
        NewStatement);
}

public enum IfBranchKind
{
    Then,
    Else,
}

public sealed record InsertBranchStatementPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    string SourceStepId,
    IfBranchKind Branch,
    InsertPatchStatement NewStatement) : SemanticPatchPlanStep(StepId, TargetNodeId);

public sealed record InsertLoopBodyStatementPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    string SourceStepId,
    InsertPatchStatement NewStatement) : SemanticPatchPlanStep(StepId, TargetNodeId);

public sealed record DeleteStatementPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    SemanticId ExpectedParentBlockId,
    SemanticId? ExpectedPreviousSiblingId,
    SemanticId? ExpectedNextSiblingId) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public DeleteStatementPatchOperation ToOperation() => new(
        TargetNodeId,
        ExpectedParentBlockId,
        ExpectedPreviousSiblingId,
        ExpectedNextSiblingId);
}

public sealed record IntroduceTemporaryPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    SemanticId ExpectedContainingStatementId,
    SemanticId ExpectedParentBlockId,
    SemanticId? ExpectedPreviousSiblingId,
    SemanticId? ExpectedNextSiblingId,
    string Name,
    string TypeName,
    SemanticId ExpectedTypeId) : SemanticPatchPlanStep(StepId, TargetNodeId)
{
    public IntroduceTemporaryPatchOperation ToOperation() => new(
        TargetNodeId,
        ExpectedContainingStatementId,
        ExpectedParentBlockId,
        ExpectedPreviousSiblingId,
        ExpectedNextSiblingId,
        Name,
        TypeName,
        ExpectedTypeId);
}

public sealed record ReconnectValuePatchPlanStep(
    string StepId,
    SemanticId TargetNodeId,
    string SourceStepId,
    SemanticId ExpectedContainingStatementId,
    SemanticId ExpectedParentBlockId,
    SemanticId? ExpectedPreviousSiblingId,
    SemanticId? ExpectedNextSiblingId,
    SemanticId ExpectedTypeId) : SemanticPatchPlanStep(StepId, TargetNodeId);

public sealed record SemanticPatchPlanDocument(
    string SchemaVersion,
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId,
    IReadOnlyList<SemanticPatchPlanStep> Steps)
{
    public const string CurrentSchemaVersion = "unifiedscript.patch-plan/v0.21";

    public static SemanticPatchPlanDocument Create(
        SemanticId sourceCompilationId,
        SemanticId semanticConfigurationId,
        IEnumerable<SemanticPatchPlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var document = new SemanticPatchPlanDocument(
            CurrentSchemaVersion,
            sourceCompilationId,
            semanticConfigurationId,
            steps.ToArray());
        SemanticPatchPlanJson.Validate(document);
        return document;
    }
}
