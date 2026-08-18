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
            new ReplaceAssignmentValuePatchPlanStep(
                "replaceAssignmentValue",
                SemanticId.FromKey("assignment-expression"),
                "=",
                SemanticId.FromKey("assignment-target-expression"),
                SemanticId.FromKey("assignment-target-definition"),
                "value",
                SemanticId.FromKey("assignment-value-expression"),
                intType,
                new LiteralPatchExpression("2", intType)),
            new ReplaceWhileConditionPatchPlanStep(
                "replaceWhileCondition",
                SemanticId.FromKey("while-statement"),
                SemanticId.FromKey("while-condition"),
                SemanticId.FromKey("System.Boolean"),
                new LiteralPatchExpression(
                    "false",
                    SemanticId.FromKey("System.Boolean"))),
            new ReplaceForConditionPatchPlanStep(
                "replaceForCondition",
                SemanticId.FromKey("for-statement"),
                SemanticId.FromKey("for-condition"),
                SemanticId.FromKey("System.Boolean"),
                new LiteralPatchExpression(
                    "true",
                    SemanticId.FromKey("System.Boolean"))),
            new ReplaceForDeclarationInitializerPatchPlanStep(
                "replaceForDeclarationInitializer",
                SemanticId.FromKey("for-variable"),
                SemanticId.FromKey("for-declaration-statement"),
                "index",
                SemanticId.FromKey("for-declaration-initializer"),
                intType,
                new LiteralPatchExpression("1", intType)),
            new ReplaceForAssignmentInitializerValuePatchPlanStep(
                "replaceForAssignmentInitializerValue",
                SemanticId.FromKey("for-assignment-initializer"),
                SemanticId.FromKey("for-assignment-statement"),
                0,
                "=",
                SemanticId.FromKey("for-assignment-target"),
                SemanticId.FromKey("for-assignment-definition"),
                "cursor",
                SemanticId.FromKey("for-assignment-value"),
                intType,
                new LiteralPatchExpression("2", intType)),
            new ReplaceForIncrementorPatchPlanStep(
                "replaceForIncrementor",
                SemanticId.FromKey("for-incrementor"),
                SemanticId.FromKey("for-incrementor-statement"),
                0,
                "++",
                true,
                SemanticId.FromKey("for-incrementor-operand"),
                SemanticId.FromKey("for-incrementor-definition"),
                "index",
                intType,
                "--",
                false,
                new LocalReferencePatchExpression(
                    SemanticId.FromKey("for-incrementor-definition"),
                    "index",
                    intType)),
            new ReplaceIfConditionPatchPlanStep(
                "replaceIfCondition",
                SemanticId.FromKey("if-statement"),
                SemanticId.FromKey("if-condition"),
                SemanticId.FromKey("System.Boolean"),
                new LiteralPatchExpression(
                    "true",
                    SemanticId.FromKey("System.Boolean"))),
            new ReplaceLocalInitializerPatchPlanStep(
                "replaceLocalInitializer",
                SemanticId.FromKey("local-statement"),
                "value",
                SemanticId.FromKey("local-initializer"),
                intType,
                new LiteralPatchExpression("3", intType)),
            new ReplaceReturnValuePatchPlanStep(
                "replaceReturnValue",
                SemanticId.FromKey("return-statement"),
                SemanticId.FromKey("return-value"),
                intType,
                new LiteralPatchExpression("4", intType)),
            new InsertStatementPatchPlanStep(
                "insert",
                SemanticId.FromKey("block"),
                null,
                SemanticId.FromKey("first-statement"),
                new LocalLiteralDeclarationPatchStatement("value", "int", "1", intType)),
            new InsertStatementPatchPlanStep(
                "assign",
                SemanticId.FromKey("assignment-block"),
                SemanticId.FromKey("assignment-previous"),
                SemanticId.FromKey("assignment-next"),
                new LocalAssignmentPatchStatement(
                    SemanticId.FromKey("assignment-target"),
                    "value",
                    intType,
                    new LiteralPatchExpression("2", intType))),
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
        Assert.Contains("\"step\": \"replaceAssignmentValue\"", json, StringComparison.Ordinal);
        Assert.Contains("\"step\": \"replaceWhileCondition\"", json, StringComparison.Ordinal);
        Assert.Contains("\"step\": \"replaceForCondition\"", json, StringComparison.Ordinal);
        Assert.Contains(
            "\"step\": \"replaceForDeclarationInitializer\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"step\": \"replaceForAssignmentInitializerValue\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains("\"step\": \"replaceForIncrementor\"", json, StringComparison.Ordinal);
        Assert.Contains("\"step\": \"replaceIfCondition\"", json, StringComparison.Ordinal);
        Assert.Contains("\"step\": \"replaceLocalInitializer\"", json, StringComparison.Ordinal);
        Assert.Contains("\"step\": \"replaceReturnValue\"", json, StringComparison.Ordinal);
        Assert.Contains("\"step\": \"insertStatement\"", json, StringComparison.Ordinal);
        Assert.Contains("\"statement\": \"localAssignment\"", json, StringComparison.Ordinal);
        Assert.Contains("\"step\": \"deleteStatement\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveProducedWhileConditionHandle()
    {
        SemanticId blockId = SemanticId.FromKey("generated-loop-parent");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        var producer = new InsertStatementPatchPlanStep(
            "insertLoop",
            blockId,
            null,
            SemanticId.FromKey("generated-loop-next"),
            new WhilePatchStatement(new LiteralPatchExpression("true", boolType)));
        var replacement = new ReplaceProducedWhileConditionPatchPlanStep(
            "editGeneratedLoop",
            blockId,
            producer.StepId,
            new LiteralPatchExpression("false", boolType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("generated-loop-compilation"),
            SemanticId.FromKey("generated-loop-configuration"),
            [producer, replacement]);

        string json = SemanticPatchPlanJson.Serialize(plan);
        SemanticPatchPlanDocument roundTrip = SemanticPatchPlanJson.Deserialize(json);

        Assert.Equal(producer, Assert.IsType<InsertStatementPatchPlanStep>(roundTrip.Steps[0]));
        Assert.Equal(
            replacement,
            Assert.IsType<ReplaceProducedWhileConditionPatchPlanStep>(roundTrip.Steps[1]));
        Assert.Contains("\"step\": \"replaceProducedWhileCondition\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceStepId\": \"insertLoop\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveGeneralProducedExpressionHandles()
    {
        SemanticId blockId = SemanticId.FromKey("general-produced-parent");
        SemanticId nextId = SemanticId.FromKey("general-produced-next");
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        var localProducer = new InsertStatementPatchPlanStep(
            "insertLocal",
            blockId,
            null,
            nextId,
            new LocalLiteralDeclarationPatchStatement("offset", "int", "1", intType));
        var ifProducer = new InsertStatementPatchPlanStep(
            "insertIf",
            blockId,
            null,
            nextId,
            new IfPatchStatement(new LiteralPatchExpression("true", boolType)));
        var returnProducer = new InsertBranchStatementPatchPlanStep(
            "insertReturn",
            blockId,
            ifProducer.StepId,
            IfBranchKind.Then,
            new ReturnPatchStatement(new LiteralPatchExpression("0", intType), intType));
        SemanticPatchPlanStep[] steps =
        [
            localProducer,
            new ReplaceProducedLocalInitializerPatchPlanStep(
                "editProducedInitializer",
                blockId,
                localProducer.StepId,
                new LiteralPatchExpression("2", intType)),
            ifProducer,
            new ReplaceProducedIfConditionPatchPlanStep(
                "editProducedIf",
                blockId,
                ifProducer.StepId,
                new LiteralPatchExpression("false", boolType)),
            returnProducer,
            new ReplaceProducedReturnValuePatchPlanStep(
                "editProducedReturn",
                blockId,
                returnProducer.StepId,
                new LiteralPatchExpression("3", intType)),
        ];
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("general-produced-compilation"),
            SemanticId.FromKey("general-produced-configuration"),
            steps);

        string json = SemanticPatchPlanJson.Serialize(plan);
        SemanticPatchPlanDocument roundTrip = SemanticPatchPlanJson.Deserialize(json);

        Assert.Equal(steps, roundTrip.Steps);
        Assert.Contains(
            "\"step\": \"replaceProducedLocalInitializer\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"step\": \"replaceProducedIfCondition\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"step\": \"replaceProducedReturnValue\"",
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveProducedDoWhileConditionHandle()
    {
        SemanticId blockId = SemanticId.FromKey("generated-do-loop-parent");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        var producer = new InsertStatementPatchPlanStep(
            "insertDoLoop",
            blockId,
            null,
            SemanticId.FromKey("generated-do-loop-next"),
            new DoWhilePatchStatement(new LiteralPatchExpression("true", boolType)));
        var replacement = new ReplaceProducedDoWhileConditionPatchPlanStep(
            "editGeneratedDoLoop",
            blockId,
            producer.StepId,
            new LiteralPatchExpression("false", boolType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("generated-do-loop-compilation"),
            SemanticId.FromKey("generated-do-loop-configuration"),
            [producer, replacement]);

        string json = SemanticPatchPlanJson.Serialize(plan);
        SemanticPatchPlanDocument roundTrip = SemanticPatchPlanJson.Deserialize(json);

        Assert.Equal(producer, Assert.IsType<InsertStatementPatchPlanStep>(roundTrip.Steps[0]));
        Assert.Equal(
            replacement,
            Assert.IsType<ReplaceProducedDoWhileConditionPatchPlanStep>(roundTrip.Steps[1]));
        Assert.Contains(
            "\"step\": \"replaceProducedDoWhileCondition\"",
            json,
            StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("invalid-produced-do-compilation"),
            SemanticId.FromKey("generated-do-loop-configuration"),
            [
                producer with
                {
                    NewStatement = new WhilePatchStatement(
                        new LiteralPatchExpression("true", boolType)),
                },
                replacement,
            ]));
    }

    [Fact]
    public void Create_RejectsInvalidGeneralProducedExpressionHandles()
    {
        SemanticId blockId = SemanticId.FromKey("invalid-general-produced-parent");
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        var localProducer = new InsertStatementPatchPlanStep(
            "insertLocal",
            blockId,
            null,
            null,
            new LocalLiteralDeclarationPatchStatement("offset", "int", "1", intType));
        var ifProducer = new InsertStatementPatchPlanStep(
            "insertIf",
            blockId,
            null,
            null,
            new IfPatchStatement(new LiteralPatchExpression("true", boolType)));
        var voidReturnProducer = new InsertBranchStatementPatchPlanStep(
            "insertVoidReturn",
            blockId,
            ifProducer.StepId,
            IfBranchKind.Then,
            new ReturnPatchStatement(null, SemanticId.FromKey("System.Void")));

        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("wrong-produced-initializer-type-compilation"),
            SemanticId.FromKey("wrong-produced-initializer-type-configuration"),
            [
                localProducer,
                new ReplaceProducedLocalInitializerPatchPlanStep(
                    "wrongInitializerType",
                    blockId,
                    localProducer.StepId,
                    new LiteralPatchExpression("false", boolType)),
            ]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("future-produced-if-compilation"),
            SemanticId.FromKey("future-produced-if-configuration"),
            [
                new ReplaceProducedIfConditionPatchPlanStep(
                    "editFutureIf",
                    blockId,
                    ifProducer.StepId,
                    new LiteralPatchExpression("false", boolType)),
                ifProducer,
            ]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("void-produced-return-compilation"),
            SemanticId.FromKey("void-produced-return-configuration"),
            [
                ifProducer,
                voidReturnProducer,
                new ReplaceProducedReturnValuePatchPlanStep(
                    "editVoidReturn",
                    blockId,
                    voidReturnProducer.StepId,
                    new LiteralPatchExpression("0", intType)),
            ]));
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveNestedProducedLocalReferences()
    {
        SemanticId blockId = SemanticId.FromKey("produced-value-parent");
        SemanticId nextId = SemanticId.FromKey("produced-value-next");
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        var producer = new InsertStatementPatchPlanStep(
            "insertOffset",
            blockId,
            null,
            nextId,
            new LocalLiteralDeclarationPatchStatement("offset", "int", "2", intType));
        var reference = new ProducedLocalReferencePatchExpression(
            producer.StepId,
            "offset",
            intType);
        SemanticPatchPlanStep[] steps =
        [
            producer,
            new InsertStatementPatchPlanStep(
                "insertGuard",
                blockId,
                null,
                nextId,
                new IfPatchStatement(new BinaryPatchExpression(
                    ">",
                    reference,
                    new LiteralPatchExpression("0", intType),
                    boolType))),
            new ReplaceReturnValuePatchPlanStep(
                "editReturn",
                SemanticId.FromKey("produced-value-return"),
                SemanticId.FromKey("produced-value-return-expression"),
                intType,
                new BinaryPatchExpression(
                    "+",
                    reference,
                    new LiteralPatchExpression("1", intType),
                    intType)),
        ];
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("produced-value-compilation"),
            SemanticId.FromKey("produced-value-configuration"),
            steps);

        string json = SemanticPatchPlanJson.Serialize(plan);
        SemanticPatchPlanDocument roundTrip = SemanticPatchPlanJson.Deserialize(json);

        Assert.Equal(steps, roundTrip.Steps);
        Assert.Equal(2, json.Split("\"expression\": \"producedLocalReference\"").Length - 1);
        Assert.Contains("\"sourceStepId\": \"insertOffset\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveProducedReferenceInExpressionInputReplacement()
    {
        SemanticId blockId = SemanticId.FromKey("input-plan-block");
        SemanticId intType = SemanticId.FromKey("System.Int32");
        var producer = new InsertStatementPatchPlanStep(
            "insertOffset",
            blockId,
            null,
            SemanticId.FromKey("input-plan-next"),
            new LocalLiteralDeclarationPatchStatement("offset", "int", "2", intType));
        var replacement = new ReplaceExpressionInputPatchPlanStep(
            "connectOffset",
            SemanticId.FromKey("input-plan-binary"),
            "right",
            SemanticId.FromKey("input-plan-old-right"),
            intType,
            intType,
            new ProducedLocalReferencePatchExpression(
                producer.StepId,
                "offset",
                intType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("input-plan-compilation"),
            SemanticId.FromKey("input-plan-configuration"),
            [producer, replacement]);

        string json = SemanticPatchPlanJson.Serialize(plan);
        SemanticPatchPlanDocument roundTrip = SemanticPatchPlanJson.Deserialize(json);

        Assert.Equal(producer, roundTrip.Steps[0]);
        Assert.Equal(
            replacement,
            Assert.IsType<ReplaceExpressionInputPatchPlanStep>(roundTrip.Steps[1]));
        Assert.Contains("\"step\": \"replaceExpressionInput\"", json, StringComparison.Ordinal);
        Assert.Contains("\"expression\": \"producedLocalReference\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsInvalidProducedLocalReferences()
    {
        SemanticId blockId = SemanticId.FromKey("invalid-produced-value-parent");
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        var producer = new InsertStatementPatchPlanStep(
            "insertOffset",
            blockId,
            null,
            null,
            new LocalLiteralDeclarationPatchStatement("offset", "int", "2", intType));

        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("future-produced-value-compilation"),
            SemanticId.FromKey("future-produced-value-configuration"),
            [
                new InsertStatementPatchPlanStep(
                    "consumeFutureOffset",
                    blockId,
                    null,
                    null,
                    new IfPatchStatement(new ProducedLocalReferencePatchExpression(
                        producer.StepId,
                        "offset",
                        intType))),
                producer,
            ]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("wrong-produced-value-name-compilation"),
            SemanticId.FromKey("wrong-produced-value-name-configuration"),
            [
                producer,
                new InsertStatementPatchPlanStep(
                    "consumeRenamedOffset",
                    blockId,
                    null,
                    null,
                    new IfPatchStatement(new ProducedLocalReferencePatchExpression(
                        producer.StepId,
                        "renamedOffset",
                        intType))),
            ]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("wrong-produced-value-type-compilation"),
            SemanticId.FromKey("wrong-produced-value-type-configuration"),
            [
                producer,
                new InsertStatementPatchPlanStep(
                    "consumeBooleanOffset",
                    blockId,
                    null,
                    null,
                    new IfPatchStatement(new ProducedLocalReferencePatchExpression(
                        producer.StepId,
                        "offset",
                        boolType))),
            ]));
    }

    [Fact]
    public void Create_RejectsInvalidProducedWhileConditionHandles()
    {
        SemanticId blockId = SemanticId.FromKey("invalid-generated-loop-parent");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        SemanticId intType = SemanticId.FromKey("System.Int32");
        var producer = new InsertStatementPatchPlanStep(
            "insertLoop",
            blockId,
            null,
            SemanticId.FromKey("invalid-generated-loop-next"),
            new WhilePatchStatement(new LiteralPatchExpression("true", boolType)));

        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("missing-loop-source-compilation"),
            SemanticId.FromKey("missing-loop-source-configuration"),
            [
                new ReplaceProducedWhileConditionPatchPlanStep(
                    "editMissingLoop",
                    blockId,
                    producer.StepId,
                    new LiteralPatchExpression("false", boolType)),
                producer,
            ]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("wrong-loop-parent-compilation"),
            SemanticId.FromKey("wrong-loop-parent-configuration"),
            [
                producer,
                new ReplaceProducedWhileConditionPatchPlanStep(
                    "editWrongParent",
                    SemanticId.FromKey("another-loop-parent"),
                    producer.StepId,
                    new LiteralPatchExpression("false", boolType)),
            ]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("wrong-loop-type-compilation"),
            SemanticId.FromKey("wrong-loop-type-configuration"),
            [
                producer,
                new ReplaceProducedWhileConditionPatchPlanStep(
                    "editWrongType",
                    blockId,
                    producer.StepId,
                    new LiteralPatchExpression("0", intType)),
            ]));
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
    public void SerializeAndDeserialize_PreserveBranchReturnAndRejectLaterUnreachableStep()
    {
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        SemanticId blockId = SemanticId.FromKey("branch-return-block");
        var guard = new InsertStatementPatchPlanStep(
            "insertGuard",
            blockId,
            null,
            null,
            new IfPatchStatement(new LiteralPatchExpression("true", boolType)));
        var thenReturn = new InsertBranchStatementPatchPlanStep(
            "returnTrue",
            blockId,
            guard.StepId,
            IfBranchKind.Then,
            new ReturnPatchStatement(new LiteralPatchExpression("true", boolType), boolType));
        var elseReturn = new InsertBranchStatementPatchPlanStep(
            "returnFalse",
            blockId,
            guard.StepId,
            IfBranchKind.Else,
            new ReturnPatchStatement(new LiteralPatchExpression("false", boolType), boolType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("branch-return-compilation"),
            SemanticId.FromKey("branch-return-configuration"),
            [guard, thenReturn, elseReturn]);

        string json = SemanticPatchPlanJson.Serialize(plan);
        SemanticPatchPlanDocument roundTrip = SemanticPatchPlanJson.Deserialize(json);

        Assert.Equal(thenReturn, Assert.IsType<InsertBranchStatementPatchPlanStep>(roundTrip.Steps[1]));
        Assert.Equal(elseReturn, Assert.IsType<InsertBranchStatementPatchPlanStep>(roundTrip.Steps[2]));
        Assert.Contains("\"statement\": \"return\"", json, StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            plan.SourceCompilationId,
            plan.SemanticConfigurationId,
            [
                guard,
                thenReturn,
                new InsertBranchStatementPatchPlanStep(
                    "unreachableThenLocal",
                    blockId,
                    guard.StepId,
                    IfBranchKind.Then,
                    new LocalLiteralDeclarationPatchStatement(
                        "unreachable",
                        "bool",
                        "false",
                        boolType)),
            ]));
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveNestedBranchProducersRecursively()
    {
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId blockId = SemanticId.FromKey("nested-branch-block");
        var outer = new InsertStatementPatchPlanStep(
            "insertOuter",
            blockId,
            null,
            null,
            new IfPatchStatement(new LiteralPatchExpression("true", boolType)));
        var nested = new InsertBranchStatementPatchPlanStep(
            "insertNested",
            blockId,
            outer.StepId,
            IfBranchKind.Then,
            new IfPatchStatement(new LiteralPatchExpression("false", boolType)));
        var nestedThen = new InsertBranchStatementPatchPlanStep(
            "populateNestedThen",
            blockId,
            nested.StepId,
            IfBranchKind.Then,
            new LocalLiteralDeclarationPatchStatement("thenValue", "int", "1", intType));
        var nestedElse = new InsertBranchStatementPatchPlanStep(
            "populateNestedElse",
            blockId,
            nested.StepId,
            IfBranchKind.Else,
            new LocalLiteralDeclarationPatchStatement("elseValue", "int", "2", intType));
        var outerElse = new InsertBranchStatementPatchPlanStep(
            "populateOuterElse",
            blockId,
            outer.StepId,
            IfBranchKind.Else,
            new LocalLiteralDeclarationPatchStatement("fallbackValue", "int", "0", intType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("nested-branch-compilation"),
            SemanticId.FromKey("nested-branch-configuration"),
            [outer, nested, nestedThen, nestedElse, outerElse]);

        string json = SemanticPatchPlanJson.Serialize(plan);
        SemanticPatchPlanDocument roundTrip = SemanticPatchPlanJson.Deserialize(json);

        Assert.Equal(plan.Steps, roundTrip.Steps);
        Assert.Equal(
            nested,
            Assert.IsType<InsertBranchStatementPatchPlanStep>(roundTrip.Steps[1]));
        Assert.Contains("\"sourceStepId\": \"insertNested\"", json, StringComparison.Ordinal);
        Assert.Contains("\"statement\": \"if\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_PropagatesNestedTerminalPathsAndRejectsRetroactiveUnreachability()
    {
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        SemanticId blockId = SemanticId.FromKey("nested-terminal-block");
        var outer = new InsertStatementPatchPlanStep(
            "outer",
            blockId,
            null,
            null,
            new IfPatchStatement(new LiteralPatchExpression("true", boolType)));
        var nested = new InsertBranchStatementPatchPlanStep(
            "nested",
            blockId,
            outer.StepId,
            IfBranchKind.Then,
            new IfPatchStatement(new LiteralPatchExpression("true", boolType)));
        var nestedThenReturn = new InsertBranchStatementPatchPlanStep(
            "nestedThenReturn",
            blockId,
            nested.StepId,
            IfBranchKind.Then,
            new ReturnPatchStatement(new LiteralPatchExpression("true", boolType), boolType));
        var nestedElseReturn = new InsertBranchStatementPatchPlanStep(
            "nestedElseReturn",
            blockId,
            nested.StepId,
            IfBranchKind.Else,
            new ReturnPatchStatement(new LiteralPatchExpression("false", boolType), boolType));
        var outerElseValue = new InsertBranchStatementPatchPlanStep(
            "outerElseValue",
            blockId,
            outer.StepId,
            IfBranchKind.Else,
            new LocalLiteralDeclarationPatchStatement("fallback", "bool", "false", boolType));
        var unreachableOuterThen = new InsertBranchStatementPatchPlanStep(
            "unreachableOuterThen",
            blockId,
            outer.StepId,
            IfBranchKind.Then,
            new LocalLiteralDeclarationPatchStatement("late", "bool", "false", boolType));

        SemanticPatchPlanDocument siblingPlan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("nested-terminal-compilation"),
            SemanticId.FromKey("nested-terminal-configuration"),
            [outer, nested, nestedThenReturn, nestedElseReturn, outerElseValue]);
        Assert.Equal(5, siblingPlan.Steps.Count);
        SemanticPatchPlanDocument oneSidedPlan = SemanticPatchPlanDocument.Create(
            siblingPlan.SourceCompilationId,
            siblingPlan.SemanticConfigurationId,
            [outer, nested, unreachableOuterThen, nestedThenReturn]);
        Assert.Equal(4, oneSidedPlan.Steps.Count);

        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            siblingPlan.SourceCompilationId,
            siblingPlan.SemanticConfigurationId,
            [outer, nested, nestedThenReturn, nestedElseReturn, unreachableOuterThen]));

        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            siblingPlan.SourceCompilationId,
            siblingPlan.SemanticConfigurationId,
            [outer, nested, unreachableOuterThen, nestedThenReturn, nestedElseReturn]));
    }

    [Fact]
    public void Create_PropagatesTerminalStateAcrossMultipleNestedBranchLevels()
    {
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        SemanticId blockId = SemanticId.FromKey("deep-terminal-block");
        var outer = new InsertStatementPatchPlanStep(
            "outer",
            blockId,
            null,
            null,
            new IfPatchStatement(new LiteralPatchExpression("true", boolType)));
        InsertBranchStatementPatchPlanStep NestedIf(
            string stepId,
            string sourceStepId,
            IfBranchKind branch) => new(
            stepId,
            blockId,
            sourceStepId,
            branch,
            new IfPatchStatement(new LiteralPatchExpression("true", boolType)));
        InsertBranchStatementPatchPlanStep Return(
            string stepId,
            string sourceStepId,
            IfBranchKind branch,
            string value) => new(
            stepId,
            blockId,
            sourceStepId,
            branch,
            new ReturnPatchStatement(new LiteralPatchExpression(value, boolType), boolType));
        InsertBranchStatementPatchPlanStep middle = NestedIf("middle", outer.StepId, IfBranchKind.Then);
        InsertBranchStatementPatchPlanStep inner = NestedIf("inner", middle.StepId, IfBranchKind.Then);
        SemanticPatchPlanStep[] terminalTree =
        [
            outer,
            middle,
            inner,
            Return("innerThen", inner.StepId, IfBranchKind.Then, "true"),
            Return("innerElse", inner.StepId, IfBranchKind.Else, "false"),
            Return("middleElse", middle.StepId, IfBranchKind.Else, "false"),
        ];

        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("deep-terminal-compilation"),
            SemanticId.FromKey("deep-terminal-configuration"),
            [
                .. terminalTree,
                new InsertBranchStatementPatchPlanStep(
                    "unreachableOuterThen",
                    blockId,
                    outer.StepId,
                    IfBranchKind.Then,
                    new LocalLiteralDeclarationPatchStatement(
                        "late",
                        "bool",
                        "false",
                        boolType)),
            ]));
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
                payload: new LocalAssignmentPatchStatement(
                    SemanticId.FromKey("assignment-target"),
                    "value",
                    intType,
                    new LiteralPatchExpression("true", boolType)))]));
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveRecursiveLoopBodyConstruction()
    {
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId blockId = SemanticId.FromKey("loop-plan-block");
        SemanticId resultId = SemanticId.FromKey("loop-plan-result");
        var loop = new InsertStatementPatchPlanStep(
            "insertLoop",
            blockId,
            resultId,
            SemanticId.FromKey("loop-plan-return"),
            new WhilePatchStatement(new LiteralPatchExpression("true", boolType)));
        var guard = new InsertLoopBodyStatementPatchPlanStep(
            "insertGuard",
            blockId,
            loop.StepId,
            new IfPatchStatement(new LiteralPatchExpression("false", boolType)));
        var stop = new InsertBranchStatementPatchPlanStep(
            "stopLoop",
            blockId,
            guard.StepId,
            IfBranchKind.Then,
            new BreakPatchStatement());
        var decrement = new InsertLoopBodyStatementPatchPlanStep(
            "decrement",
            blockId,
            loop.StepId,
            new LocalAssignmentPatchStatement(
                resultId,
                "result",
                intType,
                new LiteralPatchExpression("0", intType)));
        var nextIteration = new InsertLoopBodyStatementPatchPlanStep(
            "nextIteration",
            blockId,
            loop.StepId,
            new ContinuePatchStatement());
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("loop-plan-compilation"),
            SemanticId.FromKey("loop-plan-configuration"),
            [loop, guard, stop, decrement, nextIteration]);

        string json = SemanticPatchPlanJson.Serialize(plan);
        SemanticPatchPlanDocument roundTrip = SemanticPatchPlanJson.Deserialize(json);

        Assert.Equal(plan.Steps, roundTrip.Steps);
        Assert.IsType<WhilePatchStatement>(
            Assert.IsType<InsertStatementPatchPlanStep>(roundTrip.Steps[0]).NewStatement);
        Assert.IsType<InsertLoopBodyStatementPatchPlanStep>(roundTrip.Steps[1]);
        Assert.IsType<BreakPatchStatement>(
            Assert.IsType<InsertBranchStatementPatchPlanStep>(roundTrip.Steps[2]).NewStatement);
        Assert.IsType<ContinuePatchStatement>(
            Assert.IsType<InsertLoopBodyStatementPatchPlanStep>(roundTrip.Steps[4]).NewStatement);
        Assert.Contains("\"step\": \"insertLoopBodyStatement\"", json, StringComparison.Ordinal);
        Assert.Contains("\"statement\": \"while\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveForProducerAndBodyLocalReference()
    {
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId blockId = SemanticId.FromKey("for-plan-block");
        var forLocal = new ForLocalReferencePatchExpression(0, "index", intType);
        var loop = new InsertStatementPatchPlanStep(
            "insertFor",
            blockId,
            null,
            null,
            new ForPatchStatement(
                new ForVariableDeclarationPatch(
                    "int",
                    [new ForVariableDeclaratorPatch(
                        "index",
                        new LiteralPatchExpression("0", intType),
                        intType)]),
                [],
                new BinaryPatchExpression(
                    "<",
                    forLocal,
                    new LiteralPatchExpression("10", intType),
                    boolType),
                [new ForMutationPatchExpression(forLocal, "++", true, intType)]));
        var body = new InsertLoopBodyStatementPatchPlanStep(
            "guardIndex",
            blockId,
            loop.StepId,
            new IfPatchStatement(new BinaryPatchExpression(
                "==",
                new ProducedLocalReferencePatchExpression(loop.StepId, "index", intType),
                new LiteralPatchExpression("2", intType),
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("for-plan-compilation"),
            SemanticId.FromKey("for-plan-configuration"),
            [loop, body]);

        string json = SemanticPatchPlanJson.Serialize(plan);
        SemanticPatchPlanDocument roundTrip = SemanticPatchPlanJson.Deserialize(json);

        ForPatchStatement roundTrippedFor = Assert.IsType<ForPatchStatement>(
            Assert.IsType<InsertStatementPatchPlanStep>(roundTrip.Steps[0]).NewStatement);
        Assert.Equal("index", Assert.Single(roundTrippedFor.Declaration!.Variables).Name);
        IfPatchStatement roundTrippedBody = Assert.IsType<IfPatchStatement>(
            Assert.IsType<InsertLoopBodyStatementPatchPlanStep>(roundTrip.Steps[1]).NewStatement);
        Assert.IsType<ProducedLocalReferencePatchExpression>(
            Assert.IsType<BinaryPatchExpression>(roundTrippedBody.Condition).Left);
        Assert.Contains("\"statement\": \"for\"", json, StringComparison.Ordinal);
        Assert.Contains("\"expression\": \"producedLocalReference\"", json,
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("invalid-for-condition-compilation"),
            SemanticId.FromKey("invalid-for-condition-configuration"),
            [
                loop,
                new ReplaceProducedWhileConditionPatchPlanStep(
                    "editAsWhile",
                    blockId,
                    loop.StepId,
                    new LiteralPatchExpression("true", boolType)),
            ]));
    }

    [Fact]
    public void Create_RejectsInvalidLoopSourcesAnchorsAndUnreachableAppends()
    {
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId blockId = SemanticId.FromKey("loop-validation-block");
        var loop = new InsertStatementPatchPlanStep(
            "insertLoop",
            blockId,
            null,
            null,
            new WhilePatchStatement(new LiteralPatchExpression("true", boolType)));
        InsertLoopBodyStatementPatchPlanStep Body(
            string stepId,
            string sourceStepId,
            SemanticId? target = null,
            InsertPatchStatement? payload = null) => new(
            stepId,
            target ?? blockId,
            sourceStepId,
            payload ?? new LocalLiteralDeclarationPatchStatement("value", "int", "1", intType));

        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("loop-validation-compilation"),
            SemanticId.FromKey("loop-validation-configuration"),
            [Body("populateFirst", loop.StepId), loop]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("loop-validation-compilation"),
            SemanticId.FromKey("loop-validation-configuration"),
            [loop, Body("missingSource", "missingLoop")]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("loop-validation-compilation"),
            SemanticId.FromKey("loop-validation-configuration"),
            [loop, Body("wrongAnchor", loop.StepId, SemanticId.FromKey("other-block"))]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("loop-validation-compilation"),
            SemanticId.FromKey("loop-validation-configuration"),
            [
                loop,
                Body("leaveLoop", loop.StepId, payload: new ContinuePatchStatement()),
                Body("unreachableValue", loop.StepId),
            ]));
    }

    [Fact]
    public void Create_PropagatesTerminalBranchesOnlyToTheirContainingLoopBody()
    {
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        SemanticId blockId = SemanticId.FromKey("loop-terminal-block");
        var outerLoop = new InsertStatementPatchPlanStep(
            "outerLoop",
            blockId,
            null,
            null,
            new WhilePatchStatement(new LiteralPatchExpression("true", boolType)));
        var terminalIf = new InsertLoopBodyStatementPatchPlanStep(
            "terminalIf",
            blockId,
            outerLoop.StepId,
            new IfPatchStatement(new LiteralPatchExpression("true", boolType)));
        var breakThen = new InsertBranchStatementPatchPlanStep(
            "breakThen",
            blockId,
            terminalIf.StepId,
            IfBranchKind.Then,
            new BreakPatchStatement());
        var continueElse = new InsertBranchStatementPatchPlanStep(
            "continueElse",
            blockId,
            terminalIf.StepId,
            IfBranchKind.Else,
            new ContinuePatchStatement());
        var unreachable = new InsertLoopBodyStatementPatchPlanStep(
            "unreachable",
            blockId,
            outerLoop.StepId,
            new ContinuePatchStatement());

        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("loop-terminal-compilation"),
            SemanticId.FromKey("loop-terminal-configuration"),
            [outerLoop, terminalIf, breakThen, continueElse, unreachable]));

        var innerLoop = new InsertLoopBodyStatementPatchPlanStep(
            "innerLoop",
            blockId,
            outerLoop.StepId,
            new WhilePatchStatement(new LiteralPatchExpression("true", boolType)));
        SemanticPatchPlanDocument nested = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("nested-loop-terminal-compilation"),
            SemanticId.FromKey("nested-loop-terminal-configuration"),
            [
                outerLoop,
                innerLoop,
                new InsertLoopBodyStatementPatchPlanStep(
                    "breakInner",
                    blockId,
                    innerLoop.StepId,
                    new BreakPatchStatement()),
                new InsertLoopBodyStatementPatchPlanStep(
                    "continueOuter",
                    blockId,
                    outerLoop.StepId,
                    new ContinuePatchStatement()),
            ]);

        Assert.Equal(4, nested.Steps.Count);
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
    public void SerializeAndDeserialize_PreserveChainedProducedExpressionInputHandles()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId returnId = SemanticId.FromKey("produced-expression-return");
        var first = new ReplaceReturnValuePatchPlanStep(
            "replaceReturn",
            returnId,
            SemanticId.FromKey("old-return-value"),
            intType,
            new BinaryPatchExpression(
                "+",
                new LocalReferencePatchExpression(
                    SemanticId.FromKey("value-parameter"),
                    "value",
                    intType),
                new LiteralPatchExpression("1", intType),
                intType));
        var second = new ReplaceProducedExpressionInputPatchPlanStep(
            "replaceGeneratedRight",
            returnId,
            first.StepId,
            [],
            "right",
            intType,
            intType,
            new BinaryPatchExpression(
                "*",
                new LocalReferencePatchExpression(
                    SemanticId.FromKey("value-parameter"),
                    "value",
                    intType),
                new LiteralPatchExpression("2", intType),
                intType));
        var third = new ReplaceProducedExpressionInputPatchPlanStep(
            "replaceNestedGeneratedRight",
            returnId,
            second.StepId,
            [],
            "right",
            intType,
            intType,
            new LiteralPatchExpression("3", intType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("produced-expression-compilation"),
            SemanticId.FromKey("produced-expression-configuration"),
            [first, second, third]);

        string json = SemanticPatchPlanJson.Serialize(plan);
        SemanticPatchPlanDocument roundTrip = SemanticPatchPlanJson.Deserialize(json);

        Assert.Equal(plan.SchemaVersion, roundTrip.SchemaVersion);
        Assert.Equal(plan.SourceCompilationId, roundTrip.SourceCompilationId);
        Assert.Equal(plan.SemanticConfigurationId, roundTrip.SemanticConfigurationId);
        Assert.Equal(first, roundTrip.Steps[0]);
        ReplaceProducedExpressionInputPatchPlanStep roundTripSecond = Assert.IsType<
            ReplaceProducedExpressionInputPatchPlanStep>(roundTrip.Steps[1]);
        ReplaceProducedExpressionInputPatchPlanStep roundTripThird = Assert.IsType<
            ReplaceProducedExpressionInputPatchPlanStep>(roundTrip.Steps[2]);
        Assert.Equal(second.StepId, roundTripSecond.StepId);
        Assert.Equal(second.TargetNodeId, roundTripSecond.TargetNodeId);
        Assert.Equal(second.SourceStepId, roundTripSecond.SourceStepId);
        Assert.Equal(second.TargetPath, roundTripSecond.TargetPath);
        Assert.Equal(second.InputPort, roundTripSecond.InputPort);
        Assert.Equal(second.NewInput, roundTripSecond.NewInput);
        Assert.Equal(third.StepId, roundTripThird.StepId);
        Assert.Equal(third.TargetPath, roundTripThird.TargetPath);
        Assert.Equal(third.NewInput, roundTripThird.NewInput);
        Assert.Equal("unifiedscript.patch-plan/v0.18", roundTrip.SchemaVersion);
        Assert.Contains("\"step\": \"replaceProducedExpressionInput\"", json);
        Assert.Contains("\"targetPath\": []", json);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveConditionalAccessReceiverHandle()
    {
        SemanticId nullableStringType = SemanticId.FromKey("System.String?");
        SemanticId nullableIntType = SemanticId.FromKey("System.Int32?");
        SemanticId returnId = SemanticId.FromKey("conditional-access-return");
        var producer = new ReplaceReturnValuePatchPlanStep(
            "replaceReturn",
            returnId,
            SemanticId.FromKey("old-return-value"),
            nullableIntType,
            new ConditionalMemberAccessPatchExpression(
                new LocalReferencePatchExpression(
                    SemanticId.FromKey("primary-parameter"),
                    "primary",
                    nullableStringType),
                "Length",
                nullableIntType));
        var consumer = new ReplaceProducedExpressionInputPatchPlanStep(
            "replaceReceiver",
            returnId,
            producer.StepId,
            [],
            "receiver",
            nullableStringType,
            nullableIntType,
            new LocalReferencePatchExpression(
                SemanticId.FromKey("fallback-parameter"),
                "fallback",
                nullableStringType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("conditional-access-compilation"),
            SemanticId.FromKey("conditional-access-configuration"),
            [producer, consumer]);

        string json = SemanticPatchPlanJson.Serialize(plan);
        SemanticPatchPlanDocument roundTrip = SemanticPatchPlanJson.Deserialize(json);

        Assert.Equal(producer, roundTrip.Steps[0]);
        ReplaceProducedExpressionInputPatchPlanStep roundTripConsumer = Assert.IsType<
            ReplaceProducedExpressionInputPatchPlanStep>(roundTrip.Steps[1]);
        Assert.Equal(consumer.StepId, roundTripConsumer.StepId);
        Assert.Equal(consumer.TargetNodeId, roundTripConsumer.TargetNodeId);
        Assert.Equal(consumer.SourceStepId, roundTripConsumer.SourceStepId);
        Assert.Equal(consumer.TargetPath, roundTripConsumer.TargetPath);
        Assert.Equal(consumer.InputPort, roundTripConsumer.InputPort);
        Assert.Equal(consumer.ExpectedInputTypeId, roundTripConsumer.ExpectedInputTypeId);
        Assert.Equal(consumer.ExpectedTargetTypeId, roundTripConsumer.ExpectedTargetTypeId);
        Assert.Equal(consumer.NewInput, roundTripConsumer.NewInput);
        Assert.Equal("unifiedscript.patch-plan/v0.18", roundTrip.SchemaVersion);
        Assert.Contains("\"expression\": \"conditionalMemberAccess\"", json);
        Assert.Contains("\"inputPort\": \"receiver\"", json);
    }

    [Fact]
    public void Create_RejectsInvalidProducedExpressionHandlesPathsAndTypes()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        SemanticId returnId = SemanticId.FromKey("invalid-produced-expression-return");
        var producer = new ReplaceReturnValuePatchPlanStep(
            "replaceReturn",
            returnId,
            SemanticId.FromKey("old-return-value"),
            intType,
            new BinaryPatchExpression(
                "+",
                new LiteralPatchExpression("1", intType),
                new LiteralPatchExpression("2", intType),
                intType));

        ReplaceProducedExpressionInputPatchPlanStep Consumer(
            string sourceStepId,
            SemanticId targetNodeId,
            IReadOnlyList<string> targetPath,
            string inputPort,
            SemanticId inputType,
            SemanticId targetType) => new(
            "consumeExpression",
            targetNodeId,
            sourceStepId,
            targetPath,
            inputPort,
            inputType,
            targetType,
            new LiteralPatchExpression("3", inputType));

        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("missing-expression-source-compilation"),
            SemanticId.FromKey("missing-expression-source-configuration"),
            [producer, Consumer("missing", returnId, [], "right", intType, intType)]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("wrong-expression-anchor-compilation"),
            SemanticId.FromKey("wrong-expression-anchor-configuration"),
            [
                producer,
                Consumer(
                    producer.StepId,
                    SemanticId.FromKey("other-anchor"),
                    [],
                    "right",
                    intType,
                    intType),
            ]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("invalid-expression-path-compilation"),
            SemanticId.FromKey("invalid-expression-path-configuration"),
            [producer, Consumer(producer.StepId, returnId, ["right"], "right", intType, intType)]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("invalid-expression-port-compilation"),
            SemanticId.FromKey("invalid-expression-port-configuration"),
            [producer, Consumer(producer.StepId, returnId, [], "operand", intType, intType)]));
        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("wrong-expression-type-compilation"),
            SemanticId.FromKey("wrong-expression-type-configuration"),
            [producer, Consumer(producer.StepId, returnId, [], "right", boolType, intType)]));
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

    [Fact]
    public void SerializeAndDeserialize_PreserveProducedForConditionHandle()
    {
        SemanticId blockId = SemanticId.FromKey("produced-for-parent");
        SemanticId intType = SemanticId.FromKey("produced-for-int-type");
        SemanticId boolType = SemanticId.FromKey("produced-for-bool-type");
        var index = new ForLocalReferencePatchExpression(0, "index", intType);
        var producer = new InsertStatementPatchPlanStep(
            "insertFor",
            blockId,
            null,
            SemanticId.FromKey("produced-for-next"),
            new ForPatchStatement(
                new ForVariableDeclarationPatch(
                    "int",
                    [new ForVariableDeclaratorPatch(
                        "index",
                        new LiteralPatchExpression("0", intType),
                        intType)]),
                [],
                new LiteralPatchExpression("true", boolType),
                [new ForMutationPatchExpression(index, "++", true, intType)]));
        var replacement = new ReplaceProducedForConditionPatchPlanStep(
            "replaceProducedForCondition",
            blockId,
            producer.StepId,
            new BinaryPatchExpression(
                "<",
                new ProducedLocalReferencePatchExpression(producer.StepId, "index", intType),
                new LiteralPatchExpression("4", intType),
                boolType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("produced-for-compilation"),
            SemanticId.FromKey("produced-for-configuration"),
            [producer, replacement]);

        string json = SemanticPatchPlanJson.Serialize(plan);
        SemanticPatchPlanDocument roundTrip = SemanticPatchPlanJson.Deserialize(json);

        InsertStatementPatchPlanStep roundTripProducer =
            Assert.IsType<InsertStatementPatchPlanStep>(roundTrip.Steps[0]);
        Assert.Equal(producer.StepId, roundTripProducer.StepId);
        Assert.Equal(producer.TargetNodeId, roundTripProducer.TargetNodeId);
        Assert.IsType<ForPatchStatement>(roundTripProducer.NewStatement);
        Assert.Equal(
            replacement,
            Assert.IsType<ReplaceProducedForConditionPatchPlanStep>(roundTrip.Steps[1]));
        Assert.Contains(
            "\"step\": \"replaceProducedForCondition\"",
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsProducedForConditionHandleForAnOmittedCondition()
    {
        SemanticId blockId = SemanticId.FromKey("omitted-produced-for-parent");
        SemanticId boolType = SemanticId.FromKey("omitted-produced-for-bool-type");
        var producer = new InsertStatementPatchPlanStep(
            "insertFor",
            blockId,
            null,
            null,
            new ForPatchStatement(null, [], null, []));

        Assert.Throws<JsonException>(() => SemanticPatchPlanDocument.Create(
            SemanticId.FromKey("omitted-produced-for-compilation"),
            SemanticId.FromKey("omitted-produced-for-configuration"),
            [
                producer,
                new ReplaceProducedForConditionPatchPlanStep(
                    "replaceMissingCondition",
                    blockId,
                    producer.StepId,
                    new LiteralPatchExpression("false", boolType)),
            ]));
    }
}
