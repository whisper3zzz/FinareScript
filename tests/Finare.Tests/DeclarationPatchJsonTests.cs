using System.Text.Json;
using Finare.Core;

namespace Finare.Tests;

public sealed class DeclarationPatchJsonTests
{
    [Fact]
    public void RoundTripPreservesStrictTypedOperations()
    {
        DeclarationPatchDocument document = DeclarationPatchDocument.Create(
            SemanticId.FromKey("source"),
            SemanticId.FromKey("configuration"),
            [
                new AddScriptAssetPatchOperation(
                    "Game/Scripts.cs",
                    new ScriptAssetPatchSpec(
                        "Game",
                        "PlayerScript",
                        "Gameplay/Player",
                        "global::Finare.Unreal.UnrealActorScript",
                        IncludeActorLifecycle: true)),
                new UpdateScriptAssetPatchOperation(
                    SemanticId.FromKey("renamed-asset"),
                    new ScriptAssetPatchPrecondition("Game.OldScript", "Gameplay/Old"),
                    "Gameplay/Renamed"),
                new RemoveScriptAssetPatchOperation(
                    SemanticId.FromKey("removed-asset"),
                    new ScriptAssetPatchPrecondition("Game.RemovedScript", "Gameplay/Removed")),
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
                new AddScriptFunctionPatchOperation(
                    SemanticId.FromKey("asset"),
                    Function("Resolve", "int", false, ("value", "int"))),
                new UpdateScriptFunctionPatchOperation(
                    SemanticId.FromKey("updated-function"),
                    ExpectedFunction("OldResolve", "System.Int32", false, ("value", "System.Int32")),
                    Function("NewResolve", "float", true, ("amount", "float"))),
                new RemoveScriptFunctionPatchOperation(
                    SemanticId.FromKey("removed-function"),
                    ExpectedFunction("Removed", "System.Void", false)),
                new AddUnrealLifecycleHookPatchOperation(
                    SemanticId.FromKey("asset"),
                    "tick"),
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
        Assert.Contains("\"operation\": \"addAsset\"", json);
        Assert.Contains("\"operation\": \"updateAsset\"", json);
        Assert.Contains("\"operation\": \"removeAsset\"", json);
        Assert.Contains("\"operation\": \"addState\"", json);
        Assert.Contains("\"operation\": \"updateState\"", json);
        Assert.Contains("\"operation\": \"removeState\"", json);
        Assert.Contains("\"operation\": \"addFunction\"", json);
        Assert.Contains("\"operation\": \"updateFunction\"", json);
        Assert.Contains("\"operation\": \"removeFunction\"", json);
        Assert.Contains("\"operation\": \"addLifecycleHook\"", json);
        Assert.Contains("\"operation\": \"addUnrealEventHandler\"", json);
    }

    [Fact]
    public void DeserializeMigratesLegacyDeclarationPatchToCurrentSchema()
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

    private static ScriptFunctionPatchSpec Function(
        string name,
        string returnType,
        bool isStatic,
        params (string Name, string Type)[] parameters) => new(
        name,
        returnType,
        isStatic,
        parameters.Select(static parameter =>
            new ScriptFunctionParameterPatchSpec(parameter.Name, parameter.Type)).ToArray());

    private static ScriptFunctionPatchPrecondition ExpectedFunction(
        string name,
        string returnType,
        bool isStatic,
        params (string Name, string Type)[] parameters) => new(
        name,
        returnType,
        isStatic,
        parameters.Select(static parameter =>
            new ScriptFunctionParameterPatchPrecondition(parameter.Name, parameter.Type)).ToArray());
}
