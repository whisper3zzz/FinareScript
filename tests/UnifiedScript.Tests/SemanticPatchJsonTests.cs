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
    public void SerializeAndDeserialize_PreserveDoWhileConditionEditAndConstructor()
    {
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        var replacement = new ReplaceDoWhileConditionPatchOperation(
            SemanticId.FromKey("edit-do-loop"),
            SemanticId.FromKey("edit-do-condition"),
            boolType,
            new LiteralPatchExpression("false", boolType));
        var insertion = new InsertStatementPatchOperation(
            SemanticId.FromKey("do-parent"),
            null,
            null,
            new DoWhilePatchStatement(new LiteralPatchExpression("true", boolType)));
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("do-compilation"),
            SemanticId.FromKey("do-configuration"),
            [replacement, insertion]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal(
            replacement,
            Assert.IsType<ReplaceDoWhileConditionPatchOperation>(roundTrip.Operations[0]));
        Assert.IsType<DoWhilePatchStatement>(
            Assert.IsType<InsertStatementPatchOperation>(roundTrip.Operations[1]).NewStatement);
        Assert.Contains("\"operation\": \"replaceDoWhileCondition\"", json, StringComparison.Ordinal);
        Assert.Contains("\"statement\": \"doWhile\"", json, StringComparison.Ordinal);
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
    public void SerializeAndDeserialize_PreserveRegionAwareStatementMove()
    {
        SemanticId sourceBlockId = SemanticId.FromKey("move-source-block");
        SemanticId destinationBlockId = SemanticId.FromKey("move-destination-block");
        var move = new MoveStatementPatchOperation(
            SemanticId.FromKey("move-target"),
            sourceBlockId,
            SemanticId.FromKey("move-old-previous"),
            SemanticId.FromKey("move-old-next"),
            destinationBlockId,
            SemanticId.FromKey("move-new-previous"),
            SemanticId.FromKey("move-new-next"));
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("move-compilation"),
            SemanticId.FromKey("move-configuration"),
            [move]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal(
            "unifiedscript.patch/v0.28",
            roundTrip.SchemaVersion);
        Assert.Equal(
            move,
            Assert.IsType<MoveStatementPatchOperation>(Assert.Single(roundTrip.Operations)));
        Assert.Contains("\"operation\": \"moveStatement\"", json, StringComparison.Ordinal);
        Assert.Contains("\"destinationParentBlockId\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveIdentitySafeStatementDuplication()
    {
        SemanticId statementId = SemanticId.FromKey("copy-target");
        SemanticId blockId = SemanticId.FromKey("copy-block");
        var duplicate = new DuplicateStatementPatchOperation(
            statementId,
            blockId,
            SemanticId.FromKey("copy-source-previous"),
            SemanticId.FromKey("copy-source-next"),
            statementId,
            SemanticId.FromKey("copy-destination-next"),
            "valueCopy");
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("copy-compilation"),
            SemanticId.FromKey("copy-configuration"),
            [duplicate]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal("unifiedscript.patch/v0.28", roundTrip.SchemaVersion);
        Assert.Equal(
            duplicate,
            Assert.IsType<DuplicateStatementPatchOperation>(Assert.Single(roundTrip.Operations)));
        Assert.Contains("\"operation\": \"duplicateStatement\"", json, StringComparison.Ordinal);
        Assert.Contains("\"newLocalName\": \"valueCopy\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveCrossRegionStatementRangeDuplication()
    {
        SemanticId first = SemanticId.FromKey("range-copy-first");
        SemanticId second = SemanticId.FromKey("range-copy-second");
        var duplicate = new DuplicateStatementRangePatchOperation(
            first,
            SemanticId.FromKey("range-copy-source-block"),
            [first, second],
            SemanticId.FromKey("range-copy-source-previous"),
            SemanticId.FromKey("range-copy-source-next"),
            SemanticId.FromKey("range-copy-destination-block"),
            SemanticId.FromKey("range-copy-destination-previous"),
            SemanticId.FromKey("range-copy-destination-next"),
            [new StatementCopyLocalRename(first, "value", "valueCopy")]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("range-copy-compilation"),
            SemanticId.FromKey("range-copy-configuration"),
            [duplicate]);

        string json = SemanticPatchJson.Serialize(patch);
        DuplicateStatementRangePatchOperation roundTrip =
            Assert.IsType<DuplicateStatementRangePatchOperation>(
                Assert.Single(SemanticPatchJson.Deserialize(json).Operations));

        Assert.Equal(duplicate.TargetNodeId, roundTrip.TargetNodeId);
        Assert.Equal(duplicate.ExpectedSourceParentBlockId, roundTrip.ExpectedSourceParentBlockId);
        Assert.Equal(duplicate.ExpectedStatementIds, roundTrip.ExpectedStatementIds);
        Assert.Equal(duplicate.DestinationParentBlockId, roundTrip.DestinationParentBlockId);
        StatementCopyLocalRename rename = Assert.Single(roundTrip.LocalRenames);
        Assert.Equal("value", rename.ExpectedName);
        Assert.Equal("valueCopy", rename.NewName);
        Assert.Contains("\"operation\": \"duplicateStatements\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveCallableExtractionContract()
    {
        SemanticId first = SemanticId.FromKey("extract-first");
        SemanticId second = SemanticId.FromKey("extract-second");
        var extract = new ExtractCallablePatchOperation(
            first,
            SemanticId.FromKey("extract-method"),
            SemanticId.FromKey("extract-block"),
            [first, second],
            SemanticId.FromKey("extract-previous"),
            SemanticId.FromKey("extract-next"),
            "ResolveDamage");
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("extract-compilation"),
            SemanticId.FromKey("extract-configuration"),
            [extract]);

        string json = SemanticPatchJson.Serialize(patch);
        ExtractCallablePatchOperation roundTrip = Assert.IsType<ExtractCallablePatchOperation>(
            Assert.Single(SemanticPatchJson.Deserialize(json).Operations));

        Assert.Equal("unifiedscript.patch/v0.28", patch.SchemaVersion);
        Assert.Equal(extract.TargetNodeId, roundTrip.TargetNodeId);
        Assert.Equal(extract.ExpectedContainingMethodId, roundTrip.ExpectedContainingMethodId);
        Assert.Equal(extract.ExpectedParentBlockId, roundTrip.ExpectedParentBlockId);
        Assert.Equal(extract.ExpectedStatementIds, roundTrip.ExpectedStatementIds);
        Assert.Equal(extract.ExpectedPreviousSiblingId, roundTrip.ExpectedPreviousSiblingId);
        Assert.Equal(extract.ExpectedNextSiblingId, roundTrip.ExpectedNextSiblingId);
        Assert.Equal(extract.MethodName, roundTrip.MethodName);
        Assert.Contains("\"operation\": \"extractCallable\"", json, StringComparison.Ordinal);
        Assert.Contains("\"methodName\": \"ResolveDamage\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveLexicalCollapseAndExpansion()
    {
        SemanticId parentBlock = SemanticId.FromKey("scope-parent");
        SemanticId first = SemanticId.FromKey("scope-first");
        SemanticId second = SemanticId.FromKey("scope-second");
        SemanticId nestedBlock = SemanticId.FromKey("scope-nested");
        SemanticId previous = SemanticId.FromKey("scope-previous");
        SemanticId next = SemanticId.FromKey("scope-next");
        var collapse = new CollapseStatementRangePatchOperation(
            first,
            parentBlock,
            [first, second],
            previous,
            next);
        var expand = new ExpandBlockPatchOperation(
            nestedBlock,
            parentBlock,
            [first, second],
            previous,
            next);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("scope-compilation"),
            SemanticId.FromKey("scope-configuration"),
            [collapse, expand]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal("unifiedscript.patch/v0.28", roundTrip.SchemaVersion);
        CollapseStatementRangePatchOperation collapsedRoundTrip =
            Assert.IsType<CollapseStatementRangePatchOperation>(roundTrip.Operations[0]);
        ExpandBlockPatchOperation expandedRoundTrip =
            Assert.IsType<ExpandBlockPatchOperation>(roundTrip.Operations[1]);
        Assert.Equal(collapse.TargetNodeId, collapsedRoundTrip.TargetNodeId);
        Assert.Equal(collapse.ExpectedParentBlockId, collapsedRoundTrip.ExpectedParentBlockId);
        Assert.Equal(collapse.ExpectedStatementIds, collapsedRoundTrip.ExpectedStatementIds);
        Assert.Equal(expand.TargetNodeId, expandedRoundTrip.TargetNodeId);
        Assert.Equal(expand.ExpectedParentBlockId, expandedRoundTrip.ExpectedParentBlockId);
        Assert.Equal(expand.ExpectedChildStatementIds, expandedRoundTrip.ExpectedChildStatementIds);
        Assert.Contains("\"operation\": \"collapseStatements\"", json, StringComparison.Ordinal);
        Assert.Contains("\"operation\": \"expandBlock\"", json, StringComparison.Ordinal);
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
    public void SerializeAndDeserialize_PreserveTypedConditionalExpression()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        var expression = new ConditionalPatchExpression(
            new LocalReferencePatchExpression(
                SemanticId.FromKey("enabled-parameter"),
                "enabled",
                boolType),
            new LiteralPatchExpression("1", intType),
            new LiteralPatchExpression("2", intType),
            intType);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("conditional-compilation"),
            SemanticId.FromKey("conditional-configuration"),
            [new ReplaceReturnValuePatchOperation(
                SemanticId.FromKey("conditional-return"),
                SemanticId.FromKey("conditional-old-value"),
                intType,
                expression)]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        ReplaceReturnValuePatchOperation operation = Assert.IsType<ReplaceReturnValuePatchOperation>(
            Assert.Single(roundTrip.Operations));
        Assert.Equal(expression, Assert.IsType<ConditionalPatchExpression>(operation.NewValue));
        Assert.Equal(SemanticPatchDocument.CurrentSchemaVersion, roundTrip.SchemaVersion);
        Assert.Contains("\"expression\": \"conditional\"", json);
        Assert.Contains("\"whenTrue\"", json);
        Assert.Contains("\"whenFalse\"", json);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveTypedNullCoalescingBinaryExpression()
    {
        SemanticId nullableIntType = SemanticId.FromKey("System.Nullable<System.Int32>");
        SemanticId intType = SemanticId.FromKey("System.Int32");
        var expression = new BinaryPatchExpression(
            "??",
            new LocalReferencePatchExpression(
                SemanticId.FromKey("optional-value-parameter"),
                "value",
                nullableIntType),
            new LiteralPatchExpression("1", intType),
            intType);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("coalesce-compilation"),
            SemanticId.FromKey("coalesce-configuration"),
            [new ReplaceReturnValuePatchOperation(
                SemanticId.FromKey("coalesce-return"),
                SemanticId.FromKey("coalesce-old-value"),
                intType,
                expression)]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        ReplaceReturnValuePatchOperation operation = Assert.IsType<ReplaceReturnValuePatchOperation>(
            Assert.Single(roundTrip.Operations));
        Assert.Equal(expression, Assert.IsType<BinaryPatchExpression>(operation.NewValue));
        Assert.Equal(SemanticPatchDocument.CurrentSchemaVersion, roundTrip.SchemaVersion);
        Assert.Contains("\"operator\": \"??\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveTypedConditionalMemberAccess()
    {
        SemanticId nullableStringType = SemanticId.FromKey("System.String?");
        SemanticId nullableIntType = SemanticId.FromKey("System.Int32?");
        var expression = new ConditionalMemberAccessPatchExpression(
            new LocalReferencePatchExpression(
                SemanticId.FromKey("text-parameter"),
                "text",
                nullableStringType),
            "Length",
            nullableIntType);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("conditional-access-compilation"),
            SemanticId.FromKey("conditional-access-configuration"),
            [new ReplaceReturnValuePatchOperation(
                SemanticId.FromKey("conditional-access-return"),
                SemanticId.FromKey("conditional-access-old-value"),
                nullableIntType,
                expression)]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        ReplaceReturnValuePatchOperation operation = Assert.IsType<ReplaceReturnValuePatchOperation>(
            Assert.Single(roundTrip.Operations));
        Assert.Equal(
            expression,
            Assert.IsType<ConditionalMemberAccessPatchExpression>(operation.NewValue));
        Assert.Equal(SemanticPatchDocument.CurrentSchemaVersion, roundTrip.SchemaVersion);
        Assert.Contains("\"expression\": \"conditionalMemberAccess\"", json);
        Assert.Contains("\"memberName\": \"Length\"", json);
        Assert.Contains("\"receiver\"", json);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveForEachCollectionEditAndInsertion()
    {
        SemanticId stringType = SemanticId.FromKey("foreach-string-type");
        SemanticId charType = SemanticId.FromKey("foreach-char-type");
        var collection = new LocalReferencePatchExpression(
            SemanticId.FromKey("foreach-source"),
            "values",
            stringType);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("foreach-compilation"),
            SemanticId.FromKey("foreach-configuration"),
            [
                new ReplaceForEachCollectionPatchOperation(
                    SemanticId.FromKey("foreach-loop"),
                    SemanticId.FromKey("foreach-old-collection"),
                    stringType,
                    collection),
                new InsertStatementPatchOperation(
                    SemanticId.FromKey("foreach-block"),
                    null,
                    null,
                    new ForEachPatchStatement("char", "value", charType, collection)),
            ]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.IsType<ReplaceForEachCollectionPatchOperation>(roundTrip.Operations[0]);
        InsertStatementPatchOperation insertion = Assert.IsType<InsertStatementPatchOperation>(
            roundTrip.Operations[1]);
        Assert.Equal(
            new ForEachPatchStatement("char", "value", charType, collection),
            Assert.IsType<ForEachPatchStatement>(insertion.NewStatement));
        Assert.Equal(SemanticPatchDocument.CurrentSchemaVersion, roundTrip.SchemaVersion);
        Assert.Contains("\"operation\": \"replaceForEachCollection\"", json);
        Assert.Contains("\"statement\": \"forEach\"", json);
    }

    [Fact]
    public void Create_RejectsInvalidConditionalMemberAccessShapeOrSelectedPort()
    {
        SemanticId stringType = SemanticId.FromKey("System.String");
        SemanticId nullableIntType = SemanticId.FromKey("System.Int32?");
        var receiver = new LocalReferencePatchExpression(
            SemanticId.FromKey("conditional-access-receiver"),
            "text",
            stringType);

        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("missing-conditional-receiver-compilation"),
            SemanticId.FromKey("missing-conditional-receiver-configuration"),
            [new ReplaceReturnValuePatchOperation(
                SemanticId.FromKey("missing-conditional-receiver-return"),
                SemanticId.FromKey("missing-conditional-receiver-value"),
                nullableIntType,
                new ConditionalMemberAccessPatchExpression(null!, "Length", nullableIntType))]));
        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("missing-conditional-member-compilation"),
            SemanticId.FromKey("missing-conditional-member-configuration"),
            [new ReplaceReturnValuePatchOperation(
                SemanticId.FromKey("missing-conditional-member-return"),
                SemanticId.FromKey("missing-conditional-member-value"),
                nullableIntType,
                new ConditionalMemberAccessPatchExpression(receiver, " ", nullableIntType))]));
        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("selected-conditional-port-compilation"),
            SemanticId.FromKey("selected-conditional-port-configuration"),
            [new ReplaceExpressionInputPatchOperation(
                SemanticId.FromKey("selected-conditional-port-root"),
                "whenNotNull",
                SemanticId.FromKey("selected-conditional-port-value"),
                nullableIntType,
                nullableIntType,
                new LiteralPatchExpression("1", nullableIntType))]));
    }

    [Fact]
    public void Create_RejectsConditionalBranchesWithMismatchedDeclaredTypes()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");

        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("invalid-conditional-compilation"),
            SemanticId.FromKey("invalid-conditional-configuration"),
            [new ReplaceReturnValuePatchOperation(
                SemanticId.FromKey("invalid-conditional-return"),
                SemanticId.FromKey("invalid-conditional-value"),
                intType,
                new ConditionalPatchExpression(
                    new LiteralPatchExpression("true", boolType),
                    new LiteralPatchExpression("1", intType),
                    new LiteralPatchExpression("false", boolType),
                    intType))]));
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
    public void SerializeAndDeserialize_PreserveBoundedForStatement()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        var local = new ForLocalReferencePatchExpression(0, "index", intType);
        var forStatement = new ForPatchStatement(
            new ForVariableDeclarationPatch(
                "int",
                [new ForVariableDeclaratorPatch(
                    "index",
                    new LiteralPatchExpression("0", intType),
                    intType)]),
            [],
            new BinaryPatchExpression(
                "<",
                local,
                new LiteralPatchExpression("10", intType),
                boolType),
            [new ForMutationPatchExpression(local, "++", true, intType)]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("for-compilation"),
            SemanticId.FromKey("for-configuration"),
            [new InsertStatementPatchOperation(
                SemanticId.FromKey("for-parent-block"),
                null,
                null,
                forStatement)]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        ForPatchStatement roundTrippedFor = Assert.IsType<ForPatchStatement>(
            Assert.IsType<InsertStatementPatchOperation>(
                Assert.Single(roundTrip.Operations)).NewStatement);
        Assert.Equal(forStatement.Declaration!.TypeName, roundTrippedFor.Declaration!.TypeName);
        Assert.Equal(
            Assert.Single(forStatement.Declaration.Variables),
            Assert.Single(roundTrippedFor.Declaration.Variables));
        Assert.Empty(roundTrippedFor.Initializers);
        Assert.Equal(forStatement.Condition, roundTrippedFor.Condition);
        Assert.Equal(
            Assert.Single(forStatement.Incrementors),
            Assert.Single(roundTrippedFor.Incrementors));
        Assert.Contains("\"statement\": \"for\"", json, StringComparison.Ordinal);
        Assert.Contains("\"expression\": \"forLocalReference\"", json, StringComparison.Ordinal);
        Assert.Contains("\"isPostfix\": true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsLossyOrOutOfScopeForPayloads()
    {
        SemanticId intType = SemanticId.FromKey("System.Int32");
        SemanticId boolType = SemanticId.FromKey("System.Boolean");
        ForVariableDeclarationPatch declaration = new(
            "int",
            [new ForVariableDeclaratorPatch(
                "index",
                new LiteralPatchExpression("0", intType),
                intType)]);
        ForAssignmentPatchExpression initializer = new(
            SemanticId.FromKey("existing-index"),
            "index",
            intType,
            new LiteralPatchExpression("0", intType));

        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("mixed-for-compilation"),
            SemanticId.FromKey("mixed-for-configuration"),
            [new InsertStatementPatchOperation(
                SemanticId.FromKey("mixed-for-block"),
                null,
                null,
                new ForPatchStatement(
                    declaration,
                    [initializer],
                    new LiteralPatchExpression("true", boolType),
                    []))]));
        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("unscoped-for-local-compilation"),
            SemanticId.FromKey("unscoped-for-local-configuration"),
            [new InsertStatementPatchOperation(
                SemanticId.FromKey("unscoped-for-local-block"),
                null,
                null,
                new IfPatchStatement(
                    new ForLocalReferencePatchExpression(0, "index", boolType)))]));
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

    [Fact]
    public void SerializeAndDeserialize_PreserveDataEdgeReconnection()
    {
        SemanticId targetId = SemanticId.FromKey("data-edge-target");
        SemanticId edgeId = SemanticId.FromKey("data-edge-old-edge");
        SemanticId oldSourceId = SemanticId.FromKey("data-edge-old-source");
        SemanticId newSourceId = SemanticId.FromKey("data-edge-new-source");
        SemanticId typeId = SemanticId.FromKey("System.Int32");
        var operation = new ReconnectDataEdgePatchOperation(
            targetId,
            "value",
            edgeId,
            oldSourceId,
            "result",
            newSourceId,
            "result",
            typeId);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("data-edge-compilation"),
            SemanticId.FromKey("data-edge-configuration"),
            [operation]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal(
            operation,
            Assert.IsType<ReconnectDataEdgePatchOperation>(Assert.Single(roundTrip.Operations)));
        Assert.Contains("\"operation\": \"reconnectDataEdge\"", json, StringComparison.Ordinal);
        Assert.Equal(SemanticPatchDocument.CurrentSchemaVersion, roundTrip.SchemaVersion);
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveTypedDataInputValue()
    {
        SemanticId targetId = SemanticId.FromKey("data-input-value-target");
        SemanticId edgeId = SemanticId.FromKey("data-input-value-edge");
        SemanticId sourceId = SemanticId.FromKey("data-input-value-source");
        SemanticId typeId = SemanticId.FromKey("System.Int32");
        var operation = new SetDataInputValuePatchOperation(
            targetId,
            "right",
            edgeId,
            sourceId,
            "value",
            typeId,
            new LiteralPatchExpression("42", typeId));
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("data-input-value-compilation"),
            SemanticId.FromKey("data-input-value-configuration"),
            [operation]);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal(
            operation,
            Assert.IsType<SetDataInputValuePatchOperation>(Assert.Single(roundTrip.Operations)));
        Assert.Contains("\"operation\": \"setDataInputValue\"", json, StringComparison.Ordinal);
        Assert.Contains("\"expression\": \"literal\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsDataInputValueWithMismatchedExpressionType()
    {
        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("invalid-data-input-value-compilation"),
            SemanticId.FromKey("invalid-data-input-value-configuration"),
            [new SetDataInputValuePatchOperation(
                SemanticId.FromKey("invalid-data-input-value-target"),
                "right",
                SemanticId.FromKey("invalid-data-input-value-edge"),
                SemanticId.FromKey("invalid-data-input-value-source"),
                "value",
                SemanticId.FromKey("System.Int32"),
                new LiteralPatchExpression(
                    "false",
                    SemanticId.FromKey("System.Boolean")))]));
    }

    [Fact]
    public void Create_RejectsNoOpOrSelfDataEdgeReconnection()
    {
        SemanticId targetId = SemanticId.FromKey("invalid-data-edge-target");
        SemanticId sourceId = SemanticId.FromKey("invalid-data-edge-source");
        SemanticId edgeId = SemanticId.FromKey("invalid-data-edge-edge");
        SemanticId typeId = SemanticId.FromKey("System.Int32");

        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("invalid-data-edge-no-op-compilation"),
            SemanticId.FromKey("invalid-data-edge-no-op-configuration"),
            [new ReconnectDataEdgePatchOperation(
                targetId,
                "value",
                edgeId,
                sourceId,
                "result",
                sourceId,
                "result",
                typeId)]));
        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("invalid-data-edge-self-compilation"),
            SemanticId.FromKey("invalid-data-edge-self-configuration"),
            [new ReconnectDataEdgePatchOperation(
                targetId,
                "value",
                edgeId,
                sourceId,
                "result",
                targetId,
                "result",
                typeId)]));
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveForHeaderEndpointOperations()
    {
        SemanticId intType = SemanticId.FromKey("for-header-int-type");
        SemanticId boolType = SemanticId.FromKey("for-header-bool-type");
        SemanticId loopId = SemanticId.FromKey("for-header-loop");
        SemanticId indexId = SemanticId.FromKey("for-header-index");
        SemanticPatchOperation[] operations =
        [
            new ReplaceForConditionPatchOperation(
                loopId,
                SemanticId.FromKey("for-header-condition"),
                boolType,
                new LiteralPatchExpression("false", boolType)),
            new ReplaceForDeclarationInitializerPatchOperation(
                indexId,
                loopId,
                "index",
                SemanticId.FromKey("for-header-declaration-value"),
                intType,
                new LiteralPatchExpression("1", intType)),
            new ReplaceForAssignmentInitializerValuePatchOperation(
                SemanticId.FromKey("for-header-assignment"),
                SemanticId.FromKey("for-header-assignment-loop"),
                0,
                "=",
                SemanticId.FromKey("for-header-assignment-target"),
                SemanticId.FromKey("for-header-assignment-definition"),
                "cursor",
                SemanticId.FromKey("for-header-assignment-value"),
                intType,
                new LiteralPatchExpression("2", intType)),
            new ReplaceForIncrementorPatchOperation(
                SemanticId.FromKey("for-header-incrementor"),
                loopId,
                0,
                "++",
                true,
                SemanticId.FromKey("for-header-incrementor-operand"),
                indexId,
                "index",
                intType,
                "--",
                false,
                new LocalReferencePatchExpression(indexId, "index", intType)),
        ];
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("for-header-compilation"),
            SemanticId.FromKey("for-header-configuration"),
            operations);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal(operations, roundTrip.Operations);
        Assert.Contains("\"operation\": \"replaceForCondition\"", json, StringComparison.Ordinal);
        Assert.Contains(
            "\"operation\": \"replaceForDeclarationInitializer\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"operation\": \"replaceForAssignmentInitializerValue\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains("\"operation\": \"replaceForIncrementor\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsInvalidForHeaderEndpointPayloads()
    {
        SemanticId intType = SemanticId.FromKey("invalid-for-header-int-type");
        SemanticId loopId = SemanticId.FromKey("invalid-for-header-loop");
        SemanticId indexId = SemanticId.FromKey("invalid-for-header-index");

        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("invalid-for-index-compilation"),
            SemanticId.FromKey("invalid-for-index-configuration"),
            [new ReplaceForAssignmentInitializerValuePatchOperation(
                SemanticId.FromKey("invalid-for-assignment"),
                loopId,
                -1,
                "=",
                SemanticId.FromKey("invalid-for-assignment-target"),
                indexId,
                "index",
                SemanticId.FromKey("invalid-for-assignment-value"),
                intType,
                new LiteralPatchExpression("0", intType))]));
        Assert.Throws<JsonException>(() => SemanticPatchDocument.Create(
            SemanticId.FromKey("invalid-for-mutation-compilation"),
            SemanticId.FromKey("invalid-for-mutation-configuration"),
            [new ReplaceForIncrementorPatchOperation(
                SemanticId.FromKey("invalid-for-mutation"),
                loopId,
                0,
                "++",
                true,
                SemanticId.FromKey("invalid-for-mutation-operand"),
                indexId,
                "index",
                intType,
                "++",
                true,
                new LocalReferencePatchExpression(indexId, "index", intType))]));
    }

    [Fact]
    public void SerializeAndDeserialize_PreserveSwitchAuthoringOperationsAndPatterns()
    {
        SemanticId intType = SemanticId.FromKey("switch-authoring-int");
        SemanticId boolType = SemanticId.FromKey("switch-authoring-bool");
        SemanticId switchId = SemanticId.FromKey("switch-authoring-switch");
        SemanticId firstSection = SemanticId.FromKey("switch-authoring-first-section");
        SemanticId secondSection = SemanticId.FromKey("switch-authoring-second-section");
        SemanticId movedSection = SemanticId.FromKey("switch-authoring-moved-section");
        SwitchSectionPatch authoredSection = new([
            new PatternSwitchLabelPatch(
                new LogicalSwitchPatternPatch(
                    "and",
                    new RelationalSwitchPatternPatch(
                        ">=",
                        new LiteralPatchExpression("1", intType)),
                    new NotSwitchPatternPatch(new ParenthesizedSwitchPatternPatch(
                        new ConstantSwitchPatternPatch(
                            new LiteralPatchExpression("5", intType))))),
                new LiteralPatchExpression("true", boolType)),
        ]);
        SemanticPatchOperation[] operations =
        [
            new InsertStatementPatchOperation(
                SemanticId.FromKey("switch-authoring-block"),
                null,
                null,
                new SwitchPatchStatement(
                    new LiteralPatchExpression("0", intType),
                    intType,
                    [authoredSection, new SwitchSectionPatch([new DefaultSwitchLabelPatch()])])),
            new InsertSwitchSectionPatchOperation(
                switchId,
                firstSection,
                secondSection,
                authoredSection),
            new ReplaceSwitchSectionPatchOperation(
                firstSection,
                switchId,
                [SemanticId.FromKey("switch-authoring-label")],
                [new PatternSwitchLabelPatch(
                    new TypeSwitchPatternPatch("int", intType),
                    null)]),
            new MoveSwitchSectionPatchOperation(
                movedSection,
                switchId,
                null,
                secondSection,
                secondSection,
                null),
            new DeleteSwitchSectionPatchOperation(
                secondSection,
                switchId,
                firstSection,
                null,
                [SemanticId.FromKey("switch-authoring-delete-label")]),
        ];
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("switch-authoring-compilation"),
            SemanticId.FromKey("switch-authoring-configuration"),
            operations);

        string json = SemanticPatchJson.Serialize(patch);
        SemanticPatchDocument roundTrip = SemanticPatchJson.Deserialize(json);

        Assert.Equal(json, SemanticPatchJson.Serialize(roundTrip));
        Assert.Equal(operations.Length, roundTrip.Operations.Count);
        Assert.Contains("\"statement\": \"switch\"", json, StringComparison.Ordinal);
        Assert.Contains("\"pattern\": \"logical\"", json, StringComparison.Ordinal);
        Assert.Contains("\"operation\": \"moveSwitchSection\"", json, StringComparison.Ordinal);
        Assert.Equal("unifiedscript.patch/v0.28", roundTrip.SchemaVersion);
    }
}
