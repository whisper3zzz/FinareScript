using System.Text.Json.Serialization;

namespace UnifiedScript.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "step")]
[JsonDerivedType(typeof(ReplaceLiteralPatchPlanStep), "replaceLiteral")]
[JsonDerivedType(typeof(ChangeOperatorPatchPlanStep), "changeOperator")]
[JsonDerivedType(typeof(ReconnectArgumentPatchPlanStep), "reconnectArgument")]
[JsonDerivedType(typeof(InsertStatementPatchPlanStep), "insertStatement")]
[JsonDerivedType(typeof(InsertBranchStatementPatchPlanStep), "insertBranchStatement")]
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
    public const string CurrentSchemaVersion = "unifiedscript.patch-plan/v0.5";

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
