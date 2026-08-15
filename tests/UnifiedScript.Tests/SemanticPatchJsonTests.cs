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
}
