using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.Tests;

public sealed class DeclarationPatchJsonTests
{
    [Fact]
    public void RoundTripPreservesStrictTypedOperations()
    {
        DeclarationPatchDocument document = DeclarationPatchDocument.Create(
            SemanticId.FromKey("source"),
            SemanticId.FromKey("configuration"),
            [
                new AddScriptStatePatchOperation(
                    SemanticId.FromKey("asset"),
                    Spec("Added", "int", 7, new ScriptExposure(true, false, false, "Tuning"))),
                new UpdateScriptStatePatchOperation(
                    SemanticId.FromKey("updated"),
                    Expected("Old", 1),
                    Spec("New", "int", 2, ScriptExposure.None)),
                new RemoveScriptStatePatchOperation(
                    SemanticId.FromKey("removed"),
                    Expected("Removed", 3)),
                new AddUnrealEventHandlerPatchOperation(
                    SemanticId.FromKey("asset"),
                    new UnrealEventHandlerPatchSpec(
                        "OnChanged",
                        "global::Game.FixtureComponent",
                        "/Script/Fixture.FixtureComponent:OnChanged",
                        "target",
                        null,
                        [new("value", "int")])),
            ]);

        string json = DeclarationPatchJson.Serialize(document);
        DeclarationPatchDocument reparsed = DeclarationPatchJson.Deserialize(json);

        Assert.Equal(document.SchemaVersion, reparsed.SchemaVersion);
        Assert.Equal(document.SourceCompilationId, reparsed.SourceCompilationId);
        Assert.Equal(document.SemanticConfigurationId, reparsed.SemanticConfigurationId);
        Assert.Equal(json, DeclarationPatchJson.Serialize(reparsed));
        Assert.Contains("\"operation\": \"addState\"", json);
        Assert.Contains("\"operation\": \"updateState\"", json);
        Assert.Contains("\"operation\": \"removeState\"", json);
        Assert.Contains("\"operation\": \"addUnrealEventHandler\"", json);
    }

    [Fact]
    public void DeserializeMigratesLegacyDeclarationPatchToV02()
    {
        DeclarationPatchDocument current = DeclarationPatchDocument.Create(
            SemanticId.FromKey("source"),
            SemanticId.FromKey("configuration"),
            [new RemoveScriptStatePatchOperation(
                SemanticId.FromKey("state"),
                Expected("Count", 1))]);
        string legacy = DeclarationPatchJson.Serialize(current).Replace(
            DeclarationPatchDocument.CurrentSchemaVersion,
            DeclarationPatchDocument.LegacySchemaVersion,
            StringComparison.Ordinal);

        DeclarationPatchDocument migrated = DeclarationPatchJson.Deserialize(legacy);

        Assert.Equal(DeclarationPatchDocument.CurrentSchemaVersion, migrated.SchemaVersion);
    }

    [Fact]
    public void DeserializeRejectsUnknownMembers()
    {
        DeclarationPatchDocument document = DeclarationPatchDocument.Create(
            SemanticId.FromKey("source"),
            SemanticId.FromKey("configuration"),
            [new RemoveScriptStatePatchOperation(SemanticId.FromKey("state"), Expected("Count", 1))]);
        string json = DeclarationPatchJson.Serialize(document).Replace(
            "\"operations\":",
            "\"unexpected\": true, \"operations\":",
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => DeclarationPatchJson.Deserialize(json));
    }

    [Fact]
    public void CreateRejectsDuplicateMutationTargetsAndNoOpUpdates()
    {
        SemanticId target = SemanticId.FromKey("state");
        Assert.Throws<JsonException>(() => DeclarationPatchDocument.Create(
            SemanticId.FromKey("source"),
            SemanticId.FromKey("configuration"),
            [
                new RemoveScriptStatePatchOperation(target, Expected("Count", 1)),
                new RemoveScriptStatePatchOperation(target, Expected("Count", 1)),
            ]));

        Assert.Throws<JsonException>(() => DeclarationPatchDocument.Create(
            SemanticId.FromKey("source"),
            SemanticId.FromKey("configuration"),
            [new UpdateScriptStatePatchOperation(
                target,
                Expected("Count", 1),
                Spec("Count", "int", 1, ScriptExposure.None))]));
    }

    [Theory]
    [InlineData(ScriptStateTypeKind.Boolean, "1")]
    [InlineData(ScriptStateTypeKind.Integer, "1.5")]
    [InlineData(ScriptStateTypeKind.Enum, "null")]
    public void CreateRejectsDefaultsThatDoNotMatchTypeKind(
        ScriptStateTypeKind kind,
        string defaultJson)
    {
        Assert.Throws<JsonException>(() => DeclarationPatchDocument.Create(
            SemanticId.FromKey("source"),
            SemanticId.FromKey("configuration"),
            [new AddScriptStatePatchOperation(
                SemanticId.FromKey("asset"),
                new ScriptStatePatchSpec(
                    "Value",
                    "int",
                    kind,
                    JsonDocument.Parse(defaultJson).RootElement.Clone(),
                    ScriptExposure.None))]));
    }

    private static ScriptStatePatchPrecondition Expected(string name, int value) => new(
        name,
        "System.Int32",
        ScriptStateTypeKind.Integer,
        JsonSerializer.SerializeToElement(value),
        ScriptExposure.None);

    private static ScriptStatePatchSpec Spec(
        string name,
        string typeName,
        int value,
        ScriptExposure exposure) => new(
        name,
        typeName,
        ScriptStateTypeKind.Integer,
        JsonSerializer.SerializeToElement(value),
        exposure);
}
