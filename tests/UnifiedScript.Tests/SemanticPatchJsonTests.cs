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
}
