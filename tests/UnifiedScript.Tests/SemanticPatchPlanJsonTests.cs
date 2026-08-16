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
    public void SerializeAndDeserialize_PreserveEveryDirectOperationStep()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId firstArgument = SemanticId.FromKey("first-argument");
        SemanticId secondArgument = SemanticId.FromKey("second-argument");
        SemanticPatchPlanStep[] steps =
        [
            new ReplaceLiteralPatchPlanStep(
                "replace",
                SemanticId.FromKey("literal"),
                "1",
                "2"),
            new ChangeOperatorPatchPlanStep(
                "operator",
                SemanticId.FromKey("binary"),
                "+",
                "-",
                intType,
                intType,
                intType),
            new ReconnectArgumentPatchPlanStep(
                "arguments",
                SemanticId.FromKey("invocation"),
                0,
                firstArgument,
                1,
                secondArgument,
                intType,
                intType),
            new InsertStatementPatchPlanStep(
                "insert",
                SemanticId.FromKey("block"),
                null,
                SemanticId.FromKey("first-statement"),
                new LocalLiteralDeclarationPatchStatement("value", "int", "1", intType)),
            new DeleteStatementPatchPlanStep(
                "delete",
                SemanticId.FromKey("obsolete-statement"),
                SemanticId.FromKey("delete-parent-block"),
                null,
                SemanticId.FromKey("next-statement")),
        ];
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("direct-plan-compilation"),
            SemanticId.FromKey("direct-plan-configuration"),
            steps);

        string json = SemanticPatchPlanJson.Serialize(plan);
        SemanticPatchPlanDocument roundTrip = SemanticPatchPlanJson.Deserialize(json);

        Assert.Equal(steps, roundTrip.Steps);
        Assert.Contains("\"step\": \"replaceLiteral\"", json, StringComparison.Ordinal);
        Assert.Contains("\"step\": \"changeOperator\"", json, StringComparison.Ordinal);
        Assert.Contains("\"step\": \"reconnectArgument\"", json, StringComparison.Ordinal);
        Assert.Contains("\"step\": \"insertStatement\"", json, StringComparison.Ordinal);
        Assert.Contains("\"step\": \"deleteStatement\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_AllowsReconnectToReferenceAnEarlierInsertedLocal()
    {
        SemanticId floatType = SemanticId.FromKey("System.Single");
        SemanticId blockId = SemanticId.FromKey("insert-source-block");
        string sourceStepId = "insertScale";

        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("insert-source-compilation"),
            SemanticId.FromKey("insert-source-configuration"),
            [
                new InsertStatementPatchPlanStep(
                    sourceStepId,
                    blockId,
                    null,
                    SemanticId.FromKey("return-statement"),
                    new LocalLiteralDeclarationPatchStatement(
                        "scale",
                        "float",
                        "2.0f",
                        floatType)),
                new ReconnectValuePatchPlanStep(
                    "connectScale",
                    SemanticId.FromKey("scale-consumer"),
                    sourceStepId,
                    SemanticId.FromKey("consumer-statement"),
                    blockId,
                    SemanticId.FromKey("previous-statement"),
                    null,
                    floatType),
            ]);

        Assert.Equal(SemanticPatchPlanDocument.CurrentSchemaVersion, plan.SchemaVersion);
        Assert.IsType<InsertStatementPatchPlanStep>(plan.Steps[0]);
        Assert.IsType<ReconnectValuePatchPlanStep>(plan.Steps[1]);
    }

    [Fact]
    public void Create_AllowsOrderedInsertionsToTargetTheSameBlock()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId blockId = SemanticId.FromKey("shared-insertion-block");
        SemanticId nextId = SemanticId.FromKey("shared-insertion-next");

        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("shared-insertion-compilation"),
            SemanticId.FromKey("shared-insertion-configuration"),
            [
                new InsertStatementPatchPlanStep(
                    "insertFirst",
                    blockId,
                    null,
                    nextId,
                    new LocalLiteralDeclarationPatchStatement("first", "int", "1", intType)),
                new InsertStatementPatchPlanStep(
                    "insertSecond",
                    blockId,
                    null,
                    nextId,
                    new LocalLiteralDeclarationPatchStatement("second", "int", "2", intType)),
            ]);

        Assert.Equal(2, plan.Steps.Count);
    }

    [Fact]
    public void Create_AllowsNonProducingIfInsertionButNotAReconnectHandleToIt()
    {
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        SemanticId blockId = SemanticId.FromKey("if-plan-block");
        var insertion = new InsertStatementPatchPlanStep(
            "insertGuard",
            blockId,
            null,
            SemanticId.FromKey("if-plan-next"),
            new IfPatchStatement(new LocalReferencePatchExpression(
                SemanticId.FromKey("if-plan-parameter"),
                "ready",
                boolType)));

        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("if-plan-compilation"),
            SemanticId.FromKey("if-plan-configuration"),
            [insertion]);

        string json = SemanticPatchPlanJson.Serialize(plan);
        Assert.Equal(insertion, Assert.Single(SemanticPatchPlanJson.Deserialize(json).Steps));
        Assert.Contains("\"statement\": \"if\"", json);

        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            plan.SourceCompilationId,
            plan.SemanticConfigurationId,
            [
                insertion,
                new ReconnectValuePatchPlanStep(
                    "invalidGuardHandle",
                    SemanticId.FromKey("if-plan-consumer"),
                    insertion.StepId,
                    SemanticId.FromKey("if-plan-statement"),
                    blockId,
                    null,
                    null,
                    boolType),
            ]));
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveTypedBranchInsertionsAndLocalHandles()
    {
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId blockId = SemanticId.FromKey("branch-plan-block");
        var guard = new InsertStatementPatchPlanStep(
            "insertGuard",
            blockId,
            null,
            SemanticId.FromKey("branch-plan-next"),
            new IfPatchStatement(new LocalReferencePatchExpression(
                SemanticId.FromKey("branch-plan-parameter"),
                "ready",
                boolType)));
        var thenInsertion = new InsertBranchStatementPatchPlanStep(
            "insertThenValue",
            blockId,
            guard.StepId,
            IfBranchKind.Then,
            new LocalLiteralDeclarationPatchStatement("thenValue", "int", "1", intType));
        var elseInsertion = new InsertBranchStatementPatchPlanStep(
            "insertElseValue",
            blockId,
            guard.StepId,
            IfBranchKind.Else,
            new LocalLiteralDeclarationPatchStatement("elseValue", "int", "2", intType));
        var reconnect = new ReconnectValuePatchPlanStep(
            "connectBranchValue",
            SemanticId.FromKey("branch-plan-consumer"),
            thenInsertion.StepId,
            SemanticId.FromKey("branch-plan-consumer-statement"),
            blockId,
            null,
            null,
            intType);
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("branch-plan-compilation"),
            SemanticId.FromKey("branch-plan-configuration"),
            [guard, thenInsertion, elseInsertion, reconnect]);

        string json = SemanticPatchPlanJson.Serialize(plan);
        SemanticPatchPlanDocument roundTrip = SemanticPatchPlanJson.Deserialize(json);

        Assert.Equal(thenInsertion, Assert.IsType<InsertBranchStatementPatchPlanStep>(roundTrip.Steps[1]));
        Assert.Equal(elseInsertion, Assert.IsType<InsertBranchStatementPatchPlanStep>(roundTrip.Steps[2]));
        Assert.Contains("\"step\": \"insertBranchStatement\"", json, StringComparison.Ordinal);
        Assert.Contains("\"branch\": \"then\"", json, StringComparison.Ordinal);
        Assert.Contains("\"branch\": \"else\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsInvalidBranchSourcesAnchorsAndPayloads()
    {
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId blockId = SemanticId.FromKey("branch-validation-block");
        var guard = new InsertStatementPatchPlanStep(
            "insertGuard",
            blockId,
            null,
            null,
            new IfPatchStatement(new LiteralPatchExpression("true", boolType)));
        InsertBranchStatementPatchPlanStep BranchStep(
            string sourceStepId,
            SemanticId? target = null,
            InsertPatchStatement? payload = null) => new(
            "populateThen",
            target ?? blockId,
            sourceStepId,
            IfBranchKind.Then,
            payload ?? new LocalLiteralDeclarationPatchStatement("value", "int", "1", intType));

        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("branch-validation-compilation"),
            SemanticId.FromKey("branch-validation-configuration"),
            [BranchStep(guard.StepId), guard]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("branch-validation-compilation"),
            SemanticId.FromKey("branch-validation-configuration"),
            [guard, BranchStep("missingGuard")]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("branch-validation-compilation"),
            SemanticId.FromKey("branch-validation-configuration"),
            [guard, BranchStep(guard.StepId, SemanticId.FromKey("other-block"))]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("branch-validation-compilation"),
            SemanticId.FromKey("branch-validation-configuration"),
            [guard, BranchStep(
                guard.StepId,
                payload: new IfPatchStatement(new LiteralPatchExpression("true", boolType)))]));
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
