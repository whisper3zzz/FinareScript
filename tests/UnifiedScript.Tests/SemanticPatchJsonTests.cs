using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.Tests;

public sealed class SemanticPatchJsonTests
{
    [Fact]
    public void SerializeAndDeserialize_PreservePolymorphicOperation()
    {
        SemanticId compilationId = SemanticId.FromKey("patch-test-compilation");
        SemanticId targetId = SemanticId.FromKey("patch-test-target");
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            compilationId,
            SemanticId.FromKey("patch-test-configuration"),
            [new ReplaceLiteralPatchOperation(targetId, "1", "42")]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal(SemanticPatchDocument.CurrentSchemaVersion, roundTrip.SchemaVersion);
        Assert.Equal(compilationId, roundTrip.SourceCompilationId);
        Assert.Equal(patch.SemanticConfigurationId, roundTrip.SemanticConfigurationId);
        var operation = Assert.IsType<ReplaceLiteralPatchOperation>(Assert.Single(roundTrip.Operations));
        Assert.Equal(targetId, operation.TargetNodeId);
        Assert.Equal("1", operation.ExpectedValueText);
        Assert.Equal("42", operation.NewValueText);
        Assert.Contains("\"operation\": \"replaceLiteral\"", json);
    }

    [Fact]
    public void Deserialize_RejectsUnknownSchema()
    {
        const string json = """
            {
              "schemaVersion": "unifiedscript.patch/v99",
              "sourceCompilationId": "us_0123456789abcdef0123456789abcdef",
              "semanticConfigurationId": "us_fedcba9876543210fedcba9876543210",
              "operations": []
            }
            """;

        Assert.Throws<JsonException>(() => SemanticPatchJson.Deserialize(json));
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveStructuralOperationsAndTypePreconditions()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        var changeOperator = new ChangeOperatorPatchOperation(
            SemanticId.FromKey("binary"),
            "+",
            "-",
            intType,
            intType,
            intType);
        var reconnect = new ReconnectArgumentPatchOperation(
            SemanticId.FromKey("invocation"),
            0,
            SemanticId.FromKey("argument-zero"),
            1,
            SemanticId.FromKey("argument-one"),
            intType,
            intType);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("structural-compilation"),
            SemanticId.FromKey("structural-configuration"),
            [changeOperator, reconnect]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal(changeOperator, Assert.IsType<ChangeOperatorPatchOperation>(roundTrip.Operations[0]));
        Assert.Equal(reconnect, Assert.IsType<ReconnectArgumentPatchOperation>(roundTrip.Operations[1]));
        Assert.Contains("\"operation\": \"changeOperator\"", json);
        Assert.Contains("\"operation\": \"reconnectArgument\"", json);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveExpressionInputReplacement()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        var replacement = new ReplaceExpressionInputPatchOperation(
            SemanticId.FromKey("nested-binary"),
            "right",
            SemanticId.FromKey("nested-binary-right"),
            intType,
            intType,
            new BinaryPatchExpression(
                "+",
                new LocalReferencePatchExpression(
                    SemanticId.FromKey("replacement-local"),
                    "offset",
                    intType),
                new LiteralPatchExpression("1", intType),
                intType));
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("input-replacement-compilation"),
            SemanticId.FromKey("input-replacement-configuration"),
            [replacement]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal(
            replacement,
            Assert.IsType<ReplaceExpressionInputPatchOperation>(Assert.Single(roundTrip.Operations)));
        Assert.Contains("\"operation\": \"replaceExpressionInput\"", json, StringComparison.Ordinal);
        Assert.Contains("\"inputPort\": \"right\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsInvalidExpressionInputReplacementProtocol()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        SemanticId targetId = SemanticId.FromKey("invalid-input-target");
        SemanticId inputId = SemanticId.FromKey("invalid-input-endpoint");

        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("invalid-input-port-compilation"),
            SemanticId.FromKey("invalid-input-port-configuration"),
            [new ReplaceExpressionInputPatchOperation(
                targetId,
                "arg01",
                inputId,
                intType,
                intType,
                new LiteralPatchExpression("1", intType))]));
        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("self-input-compilation"),
            SemanticId.FromKey("self-input-configuration"),
            [new ReplaceExpressionInputPatchOperation(
                targetId,
                "left",
                targetId,
                intType,
                intType,
                new LiteralPatchExpression("1", intType))]));
        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("wrong-input-type-compilation"),
            SemanticId.FromKey("wrong-input-type-configuration"),
            [new ReplaceExpressionInputPatchOperation(
                targetId,
                "operand",
                inputId,
                intType,
                intType,
                new LiteralPatchExpression("true", boolType))]));
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveStructuredExpressionReplacementOperations()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        var assignment = new ReplaceAssignmentValuePatchOperation(
            SemanticId.FromKey("edit-assignment"),
            "=",
            SemanticId.FromKey("edit-assignment-target"),
            SemanticId.FromKey("edit-assignment-definition"),
            "result",
            SemanticId.FromKey("edit-assignment-value"),
            intType,
            new LiteralPatchExpression("2", intType));
        var loop = new ReplaceWhileConditionPatchOperation(
            SemanticId.FromKey("edit-loop"),
            SemanticId.FromKey("edit-loop-condition"),
            boolType,
            new LiteralPatchExpression("false", boolType));
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("structured-edit-compilation"),
            SemanticId.FromKey("structured-edit-configuration"),
            [assignment, loop]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal(
            assignment,
            Assert.IsType<ReplaceAssignmentValuePatchOperation>(roundTrip.Operations[0]));
        Assert.Equal(
            loop,
            Assert.IsType<ReplaceWhileConditionPatchOperation>(roundTrip.Operations[1]));
        Assert.Contains("\"operation\": \"replaceAssignmentValue\"", json, StringComparison.Ordinal);
        Assert.Contains("\"operation\": \"replaceWhileCondition\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsStructuredReplacementTypeOrShapeMismatch()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");

        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("invalid-assignment-edit-compilation"),
            SemanticId.FromKey("invalid-assignment-edit-configuration"),
            [new ReplaceAssignmentValuePatchOperation(
                SemanticId.FromKey("invalid-assignment"),
                "+=",
                SemanticId.FromKey("invalid-assignment-target"),
                SemanticId.FromKey("invalid-assignment-definition"),
                "result",
                SemanticId.FromKey("invalid-assignment-value"),
                intType,
                new LiteralPatchExpression("1", intType))]));
        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("invalid-loop-edit-compilation"),
            SemanticId.FromKey("invalid-loop-edit-configuration"),
            [new ReplaceWhileConditionPatchOperation(
                SemanticId.FromKey("invalid-loop"),
                SemanticId.FromKey("invalid-loop-condition"),
                boolType,
                new LiteralPatchExpression("1", intType))]));
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveGeneralExpressionEndpointReplacements()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        var branch = new ReplaceIfConditionPatchOperation(
            SemanticId.FromKey("edit-if"),
            SemanticId.FromKey("edit-if-condition"),
            boolType,
            new LiteralPatchExpression("false", boolType));
        var initializer = new ReplaceLocalInitializerPatchOperation(
            SemanticId.FromKey("edit-local"),
            "result",
            SemanticId.FromKey("edit-initializer"),
            intType,
            new LiteralPatchExpression("2", intType));
        var returnValue = new ReplaceReturnValuePatchOperation(
            SemanticId.FromKey("edit-return"),
            SemanticId.FromKey("edit-return-value"),
            intType,
            new LiteralPatchExpression("3", intType));
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("general-endpoint-compilation"),
            SemanticId.FromKey("general-endpoint-configuration"),
            [branch, initializer, returnValue]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal(branch, Assert.IsType<ReplaceIfConditionPatchOperation>(roundTrip.Operations[0]));
        Assert.Equal(
            initializer,
            Assert.IsType<ReplaceLocalInitializerPatchOperation>(roundTrip.Operations[1]));
        Assert.Equal(
            returnValue,
            Assert.IsType<ReplaceReturnValuePatchOperation>(roundTrip.Operations[2]));
        Assert.Contains("\"operation\": \"replaceIfCondition\"", json, StringComparison.Ordinal);
        Assert.Contains("\"operation\": \"replaceLocalInitializer\"", json, StringComparison.Ordinal);
        Assert.Contains("\"operation\": \"replaceReturnValue\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsGeneralEndpointReplacementTypeOrShapeMismatch()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");

        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("invalid-if-edit-compilation"),
            SemanticId.FromKey("invalid-if-edit-configuration"),
            [new ReplaceIfConditionPatchOperation(
                SemanticId.FromKey("invalid-if"),
                SemanticId.FromKey("invalid-if-condition"),
                boolType,
                new LiteralPatchExpression("1", intType))]));
        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("invalid-initializer-edit-compilation"),
            SemanticId.FromKey("invalid-initializer-edit-configuration"),
            [new ReplaceLocalInitializerPatchOperation(
                SemanticId.FromKey("invalid-local"),
                " ",
                SemanticId.FromKey("invalid-initializer"),
                intType,
                new LiteralPatchExpression("1", intType))]));
        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("invalid-return-edit-compilation"),
            SemanticId.FromKey("invalid-return-edit-configuration"),
            [new ReplaceReturnValuePatchOperation(
                SemanticId.FromKey("invalid-return"),
                SemanticId.FromKey("invalid-return-value"),
                intType,
                new LiteralPatchExpression("false", boolType))]));
    }

    [Fact]
    public void Create_RejectsTransactionOnlyProducedLocalReference()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");

        JsonException exception = Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("standalone-produced-reference-compilation"),
            SemanticId.FromKey("standalone-produced-reference-configuration"),
            [new ReplaceReturnValuePatchOperation(
                SemanticId.FromKey("standalone-produced-reference-return"),
                SemanticId.FromKey("standalone-produced-reference-value"),
                intType,
                new ProducedLocalReferencePatchExpression(
                    "insertValue",
                    "value",
                    intType))]));

        Assert.Contains("transaction-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveStatementTemporaryAndSiblingPreconditions()
    {
        SemanticId blockId = SemanticId.FromKey("statement-block");
        SemanticId returnId = SemanticId.FromKey("statement-return");
        SemanticId localId = SemanticId.FromKey("statement-local");
        SemanticId floatType = SemanticId.FromKey("System.Single");
        var insert = new InsertStatementPatchOperation(
            blockId,
            null,
            returnId,
            new LocalLiteralDeclarationPatchStatement("scale", "float", "2.0f", floatType));
        var delete = new DeleteStatementPatchOperation(
            localId,
            blockId,
            null,
            returnId);
        var introduceTemporary = new IntroduceTemporaryPatchOperation(
            SemanticId.FromKey("statement-expression"),
            returnId,
            blockId,
            null,
            null,
            "sharedValue",
            "float",
            floatType);
        var reconnectValue = new ReconnectValuePatchOperation(
            SemanticId.FromKey("consumer-expression"),
            localId,
            returnId,
            blockId,
            localId,
            null,
            floatType,
            floatType);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("statement-compilation"),
            SemanticId.FromKey("statement-configuration"),
            [insert, delete, introduceTemporary, reconnectValue]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal(insert, Assert.IsType<InsertStatementPatchOperation>(roundTrip.Operations[0]));
        Assert.Equal(delete, Assert.IsType<DeleteStatementPatchOperation>(roundTrip.Operations[1]));
        Assert.Equal(
            introduceTemporary,
            Assert.IsType<IntroduceTemporaryPatchOperation>(roundTrip.Operations[2]));
        Assert.Equal(
            reconnectValue,
            Assert.IsType<ReconnectValuePatchOperation>(roundTrip.Operations[3]));
        Assert.IsType<LocalLiteralDeclarationPatchStatement>(
            Assert.IsType<InsertStatementPatchOperation>(roundTrip.Operations[0]).NewStatement);
        Assert.Contains("\"operation\": \"insertStatement\"", json);
        Assert.Contains("\"statement\": \"localLiteralDeclaration\"", json);
        Assert.Contains("\"operation\": \"deleteStatement\"", json);
        Assert.Contains("\"operation\": \"introduceTemporary\"", json);
        Assert.Contains("\"operation\": \"reconnectValue\"", json);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveTypedIfExpressionTree()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        SemanticId sourceDefinition = SemanticId.FromKey("damage-parameter");
        var condition = new BinaryPatchExpression(
            ">",
            new CastPatchExpression(
                "int",
                new LocalReferencePatchExpression(sourceDefinition, "damage", intType),
                intType),
            new UnaryPatchExpression(
                "-",
                new LiteralPatchExpression("1", intType),
                intType),
            boolType);
        var insert = new InsertStatementPatchOperation(
            SemanticId.FromKey("if-parent-block"),
            null,
            SemanticId.FromKey("if-next-statement"),
            new IfPatchStatement(condition));
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("if-compilation"),
            SemanticId.FromKey("if-configuration"),
            [insert]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal(insert, Assert.IsType<InsertStatementPatchOperation>(Assert.Single(roundTrip.Operations)));
        Assert.Contains("\"statement\": \"if\"", json);
        Assert.Contains("\"expression\": \"binary\"", json);
        Assert.Contains("\"expression\": \"cast\"", json);
        Assert.Contains("\"expression\": \"localReference\"", json);
        Assert.Contains("\"expression\": \"unary\"", json);
        Assert.Contains("\"expression\": \"literal\"", json);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveTypedWhileAndLoopTransfers()
    {
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        var loop = new WhilePatchStatement(new LiteralPatchExpression("true", boolType));
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("loop-compilation"),
            SemanticId.FromKey("loop-configuration"),
            [
                new InsertStatementPatchOperation(
                    SemanticId.FromKey("loop-parent-block"),
                    null,
                    null,
                    loop),
                new InsertStatementPatchOperation(
                    SemanticId.FromKey("break-parent-block"),
                    null,
                    null,
                    new BreakPatchStatement()),
                new InsertStatementPatchOperation(
                    SemanticId.FromKey("continue-parent-block"),
                    null,
                    null,
                    new ContinuePatchStatement()),
            ]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal(
            loop,
            Assert.IsType<WhilePatchStatement>(
                Assert.IsType<InsertStatementPatchOperation>(roundTrip.Operations[0]).NewStatement));
        Assert.IsType<BreakPatchStatement>(
            Assert.IsType<InsertStatementPatchOperation>(roundTrip.Operations[1]).NewStatement);
        Assert.IsType<ContinuePatchStatement>(
            Assert.IsType<InsertStatementPatchOperation>(roundTrip.Operations[2]).NewStatement);
        Assert.Contains("\"statement\": \"while\"", json, StringComparison.Ordinal);
        Assert.Contains("\"statement\": \"break\"", json, StringComparison.Ordinal);
        Assert.Contains("\"statement\": \"continue\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsWhileWhoseConditionDoesNotDeclareAType()
    {
        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("invalid-while-compilation"),
            SemanticId.FromKey("invalid-while-configuration"),
            [new InsertStatementPatchOperation(
                SemanticId.FromKey("invalid-while-block"),
                null,
                null,
                new WhilePatchStatement(new LiteralPatchExpression("true", default)))]));
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveValueAndVoidReturnStatements()
    {
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        SemanticId voidType = SemanticId.FromKey("System.Void");
        var valueReturn = new ReturnPatchStatement(
            new LiteralPatchExpression("true", boolType),
            boolType);
        var voidReturn = new ReturnPatchStatement(null, voidType);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("return-compilation"),
            SemanticId.FromKey("return-configuration"),
            [
                new InsertStatementPatchOperation(
                    SemanticId.FromKey("value-return-block"),
                    null,
                    null,
                    valueReturn),
                new InsertStatementPatchOperation(
                    SemanticId.FromKey("void-return-block"),
                    null,
                    null,
                    voidReturn),
            ]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal(
            valueReturn,
            Assert.IsType<ReturnPatchStatement>(
                Assert.IsType<InsertStatementPatchOperation>(roundTrip.Operations[0]).NewStatement));
        Assert.Equal(
            voidReturn,
            Assert.IsType<ReturnPatchStatement>(
                Assert.IsType<InsertStatementPatchOperation>(roundTrip.Operations[1]).NewStatement));
        Assert.Contains("\"statement\": \"return\"", json, StringComparison.Ordinal);
        Assert.Contains("\"expectedReturnTypeId\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveTypedLocalAssignment()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        var assignment = new LocalAssignmentPatchStatement(
            SemanticId.FromKey("assignment-target"),
            "result",
            intType,
            new BinaryPatchExpression(
                "+",
                new LocalReferencePatchExpression(
                    SemanticId.FromKey("assignment-source"),
                    "source",
                    intType),
                new LiteralPatchExpression("1", intType),
                intType));
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("assignment-compilation"),
            SemanticId.FromKey("assignment-configuration"),
            [new InsertStatementPatchOperation(
                SemanticId.FromKey("assignment-block"),
                null,
                null,
                assignment)]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        InsertStatementPatchOperation insertion = Assert.IsType<InsertStatementPatchOperation>(
            Assert.Single(roundTrip.Operations));
        Assert.Equal(assignment, Assert.IsType<LocalAssignmentPatchStatement>(insertion.NewStatement));
        Assert.Contains("\"statement\": \"localAssignment\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsLocalAssignmentWhoseValueDeclaresAnotherType()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");

        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("invalid-assignment-compilation"),
            SemanticId.FromKey("invalid-assignment-configuration"),
            [new InsertStatementPatchOperation(
                SemanticId.FromKey("invalid-assignment-block"),
                null,
                null,
                new LocalAssignmentPatchStatement(
                    SemanticId.FromKey("invalid-assignment-target"),
                    "result",
                    intType,
                    new LiteralPatchExpression("true", boolType)))]));
    }

    [Fact]
    public void Create_RejectsReturnWithoutAnExpectedMethodType()
    {
        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("invalid-return-compilation"),
            SemanticId.FromKey("invalid-return-configuration"),
            [new InsertStatementPatchOperation(
                SemanticId.FromKey("invalid-return-block"),
                null,
                null,
                new ReturnPatchStatement(null, default))]));
    }

    [Fact]
    public void Create_RejectsExcessivelyDeepGraphExpression()
    {
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        PatchExpression expression = new LiteralPatchExpression("true", boolType);
        for (int index = 0; index < 18; index++)
        {
            expression = new UnaryPatchExpression("!", expression, boolType);
        }

        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("deep-expression-compilation"),
            SemanticId.FromKey("deep-expression-configuration"),
            [new InsertStatementPatchOperation(
                SemanticId.FromKey("deep-expression-block"),
                null,
                null,
                new IfPatchStatement(expression))]));
    }

    [Fact]
    public void Create_RejectsDuplicateTargets()
    {
        SemanticId targetId = SemanticId.FromKey("duplicate-target");

        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("compilation"),
            SemanticId.FromKey("configuration"),
            [
                new ReplaceLiteralPatchOperation(targetId, "1", "2"),
                new ReplaceLiteralPatchOperation(targetId, "1", "3"),
            ]));
    }

    [Fact]
    public void Create_RejectsArgumentReconnectionWithTheSamePortTwice()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");

        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("compilation"),
            SemanticId.FromKey("configuration"),
            [
                new ReconnectArgumentPatchOperation(
                    SemanticId.FromKey("invocation"),
                    0,
                    SemanticId.FromKey("argument-zero"),
                    0,
                    SemanticId.FromKey("argument-one"),
                    intType,
                    intType),
            ]));
    }

    [Fact]
    public void Create_RejectsStatementOperationWithDuplicateSiblingPreconditions()
    {
        SemanticId siblingId = SemanticId.FromKey("same-sibling");

        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("compilation"),
            SemanticId.FromKey("configuration"),
            [
                new DeleteStatementPatchOperation(
                    SemanticId.FromKey("statement"),
                    SemanticId.FromKey("block"),
                    siblingId,
                    siblingId),
            ]));
    }

    [Fact]
    public void Create_RejectsTemporaryWithoutAContainingStatement()
    {
        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("compilation"),
            SemanticId.FromKey("configuration"),
            [
                new IntroduceTemporaryPatchOperation(
                    SemanticId.FromKey("expression"),
                    default,
                    SemanticId.FromKey("block"),
                    null,
                    null,
                    "sharedValue",
                    "float",
                    SemanticId.FromKey("System.Single")),
            ]));
    }

    [Fact]
    public void Create_RejectsValueReconnectionWithoutASourceDefinition()
    {
        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("compilation"),
            SemanticId.FromKey("configuration"),
            [
                new ReconnectValuePatchOperation(
                    SemanticId.FromKey("consumer"),
                    default,
                    SemanticId.FromKey("statement"),
                    SemanticId.FromKey("block"),
                    null,
                    null,
                    SemanticId.FromKey("System.Single"),
                    SemanticId.FromKey("System.Single")),
            ]));
    }
}
