using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.Tests;

public sealed class ScriptAssetJsonTests
{
    [Fact]
    public void RoundTripNormalizesAssetsStatesAndFunctions()
    {
        SemanticId compilationId = SemanticId.FromKey("declaration-json-compilation");
        SemanticId configurationId = SemanticId.FromKey("declaration-json-configuration");
        ScriptAssetDeclaration asset = new(
            SemanticId.FromKey("asset:damage"),
            "Game.DamagePolicy",
            "Combat/DamagePolicy",
            SourceSpan.Unknown("DamagePolicy.cs"),
            [
                State("Zeta", 12),
                State("Alpha", 3),
            ],
            [
                new ScriptFunctionDeclaration(
                    SemanticId.FromKey("function:zeta"),
                    "Zeta",
                    "Game.DamagePolicy.Zeta(System.Int32)",
                    "System.Int32",
                    false,
                    true,
                    false,
                    [new ScriptParameterDeclaration(
                        SemanticId.FromKey("parameter:value"),
                        "value",
                        "System.Int32")]),
                new ScriptFunctionDeclaration(
                    SemanticId.FromKey("function:alpha"),
                    "Alpha",
                    "Game.DamagePolicy.Alpha()",
                    "System.Void",
                    false,
                    true,
                    false,
                    []),
            ],
            [
                new ScriptLifecycleHookDeclaration(
                    SemanticId.FromKey("lifecycle:tick"),
                    ScriptLifecycleHookKind.Tick,
                    "OnTick",
                    SourceSpan.Unknown("DamagePolicy.cs")),
                new ScriptLifecycleHookDeclaration(
                    SemanticId.FromKey("lifecycle:begin"),
                    ScriptLifecycleHookKind.BeginPlay,
                    "OnBeginPlay",
                    SourceSpan.Unknown("DamagePolicy.cs")),
            ]);
        ScriptAssetDocument document = ScriptAssetDocument.Create(
            compilationId,
            configurationId,
            [asset]);

        string json = ScriptAssetJson.Serialize(document);
        ScriptAssetDocument roundTripped = ScriptAssetJson.Deserialize(json);

        ScriptAssetDeclaration normalized = Assert.Single(roundTripped.Assets);
        Assert.Equal(["Alpha", "Zeta"], normalized.States.Select(static state => state.Name));
        Assert.Equal(
            ["Game.DamagePolicy.Alpha()", "Game.DamagePolicy.Zeta(System.Int32)"],
            normalized.Functions.Select(static function => function.QualifiedName));
        Assert.All(normalized.Functions, static function => Assert.True(function.IsPublic));
        Assert.All(normalized.Functions, static function => Assert.False(function.IsEvent));
        Assert.Equal(
            [ScriptLifecycleHookKind.BeginPlay, ScriptLifecycleHookKind.Tick],
            normalized.LifecycleHooks!.Select(static hook => hook.Kind));
        Assert.Equal(3, normalized.States[0].DefaultValue.GetInt32());
        Assert.Equal(ScriptAssetDocument.CurrentSchemaVersion, roundTripped.SchemaVersion);
    }

    [Fact]
    public void DeserializeRejectsUnknownMembersAndIncompatibleDefaults()
    {
        ScriptAssetDocument document = ScriptAssetDocument.Create(
            SemanticId.FromKey("unknown-compilation"),
            SemanticId.FromKey("unknown-configuration"),
            [new ScriptAssetDeclaration(
                SemanticId.FromKey("unknown-asset"),
                "Game.Asset",
                "Game/Asset",
                SourceSpan.Unknown("Asset.cs"),
                [State("Count", 1)],
                [])]);
        string json = ScriptAssetJson.Serialize(document, writeIndented: false);

        string unknown = json.Replace("\"assets\":", "\"futureField\":true,\"assets\":", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => ScriptAssetJson.Deserialize(unknown));

        string wrongDefault = json.Replace("\"defaultValue\":1", "\"defaultValue\":\"one\"", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => ScriptAssetJson.Deserialize(wrongDefault));
    }

    private static ScriptStateDeclaration State(string name, int defaultValue) => new(
        SemanticId.FromKey($"state:{name}"),
        name,
        "int",
        "System.Int32",
        ScriptStateTypeKind.Integer,
        JsonSerializer.SerializeToElement(defaultValue),
        SourceSpan.Unknown("DamagePolicy.cs"),
        ScriptExposure.None);
}
