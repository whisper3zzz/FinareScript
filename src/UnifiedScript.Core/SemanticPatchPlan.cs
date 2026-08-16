using System.Text.Json.Serialization;

namespace UnifiedScript.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "step")]
[JsonDerivedType(typeof(IntroduceTemporaryPatchPlanStep), "introduceTemporary")]
[JsonDerivedType(typeof(ReconnectValuePatchPlanStep), "reconnectValue")]
public abstract record SemanticPatchPlanStep(
    string StepId,
    SemanticId TargetNodeId);

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
    public const string CurrentSchemaVersion = "unifiedscript.patch-plan/v0.1";

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
