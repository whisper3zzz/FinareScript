using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.Tests;

public sealed class SemanticPatchPlanJsonTests
{
    [Fact]
    public void SerializeAndDeserialize_PreserveSymbolicSourceSteps()
    {
        SemanticId floatType = SemanticId.FromKey("System.Single");
        SemanticId blockId = SemanticId.FromKey("block");
        SemanticId returnId = SemanticId.FromKey("return");
        var introduction = new IntroduceTemporaryPatchPlanStep(
            "sharedDamage",
            SemanticId.FromKey("first-expression"),
            returnId,
            blockId,
            null,
            null,
            "sharedDamage",
            "float",
            floatType);
        var reconnect = new ReconnectValuePatchPlanStep(
            "connectSecondConsumer",
            SemanticId.FromKey("second-expression"),
            introduction.StepId,
            returnId,
            blockId,
            null,
            null,
            floatType);
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("plan-compilation"),
            SemanticId.FromKey("plan-configuration"),
            [introduction, reconnect]);

        string json = SemanticPatchPlanJson.Serialize(plan);
        SemanticPatchPlanDocument roundTrip = SemanticPatchPlanJson.Deserialize(json);

        Assert.Equal(SemanticPatchPlanDocument.CurrentSchemaVersion, roundTrip.SchemaVersion);
        Assert.Equal(introduction, Assert.IsType<IntroduceTemporaryPatchPlanStep>(roundTrip.Steps[0]));
        Assert.Equal(reconnect, Assert.IsType<ReconnectValuePatchPlanStep>(roundTrip.Steps[1]));
        Assert.Contains("\"step\": \"introduceTemporary\"", json);
        Assert.Contains("\"step\": \"reconnectValue\"", json);
        Assert.Contains("\"sourceStepId\": \"sharedDamage\"", json);
    }

    [Fact]
    public void Create_RejectsReconnectThatReferencesAFutureIntroduction()
    {
        SemanticId floatType = SemanticId.FromKey("System.Single");
        SemanticId blockId = SemanticId.FromKey("block");
        SemanticId returnId = SemanticId.FromKey("return");

        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("plan-compilation"),
            SemanticId.FromKey("plan-configuration"),
            [
                new ReconnectValuePatchPlanStep(
                    "connectFirst",
                    SemanticId.FromKey("consumer"),
                    "laterIntroduction",
                    returnId,
                    blockId,
                    null,
                    null,
                    floatType),
                new IntroduceTemporaryPatchPlanStep(
                    "laterIntroduction",
                    SemanticId.FromKey("source"),
                    returnId,
                    blockId,
                    null,
                    null,
                    "sharedValue",
                    "float",
                    floatType),
            ]));
    }

    [Fact]
    public void Create_RejectsDuplicateTargetsAndMismatchedHandleTypes()
    {
        SemanticId floatType = SemanticId.FromKey("System.Single");
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId targetId = SemanticId.FromKey("same-target");
        SemanticId blockId = SemanticId.FromKey("block");
        SemanticId returnId = SemanticId.FromKey("return");
        var introduction = new IntroduceTemporaryPatchPlanStep(
            "sharedValue",
            targetId,
            returnId,
            blockId,
            null,
            null,
            "sharedValue",
            "float",
            floatType);

        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("plan-compilation"),
            SemanticId.FromKey("plan-configuration"),
            [
                introduction,
                new ReconnectValuePatchPlanStep(
                    "duplicateTarget",
                    targetId,
                    introduction.StepId,
                    returnId,
                    blockId,
                    null,
                    null,
                    floatType),
            ]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("plan-compilation"),
            SemanticId.FromKey("plan-configuration"),
            [
                introduction,
                new ReconnectValuePatchPlanStep(
                    "wrongType",
                    SemanticId.FromKey("other-target"),
                    introduction.StepId,
                    returnId,
                    blockId,
                    null,
                    null,
                    intType),
            ]));
    }
}
