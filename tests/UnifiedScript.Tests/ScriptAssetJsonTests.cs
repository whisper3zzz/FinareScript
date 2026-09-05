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

    [Fact]
    public void SoftReferenceStatesRoundTripAndRejectMalformedPaths()
    {
        ScriptAssetDeclaration asset = new(
            SemanticId.FromKey("soft-state-asset"),
            "Game.SoftState",
            "Game/SoftState",
            SourceSpan.Unknown("SoftState.cs"),
            [new ScriptStateDeclaration(
                SemanticId.FromKey("soft-state"),
                "Mesh",
                "UnrealSoftObjectHandle",
                "UnifiedScript.Unreal.UnrealSoftObjectHandle",
                ScriptStateTypeKind.SoftObject,
                JsonSerializer.SerializeToElement("/Game/Props/Cube.Cube:Mesh"),
                SourceSpan.Unknown("SoftState.cs"),
                ScriptExposure.None)],
            []);
        ScriptAssetDocument document = ScriptAssetDocument.Create(
            SemanticId.FromKey("soft-compilation"),
            SemanticId.FromKey("soft-configuration"),
            [asset]);

        ScriptStateDeclaration state = Assert.Single(
            ScriptAssetJson.Deserialize(ScriptAssetJson.Serialize(document)).Assets).States.Single();
        Assert.Equal(ScriptStateTypeKind.SoftObject, state.TypeKind);
        Assert.Equal("/Game/Props/Cube.Cube:Mesh", state.DefaultValue.GetString());

        string malformed = ScriptAssetJson.Serialize(document, writeIndented: false)
            .Replace("/Game/Props/Cube.Cube:Mesh", "Game/Props/Cube.Cube", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => ScriptAssetJson.Deserialize(malformed));
    }

    [Theory]
    [InlineData("/Game/A.A:Sub/Object")]
    [InlineData("/Game/A.A?B")]
    [InlineData("/Game/A.A#B")]
    [InlineData("/Game/A")]
    [InlineData("/Game/A.A.")]
    public void SoftReferencePathValidatorRejectsUnrealInvalidNames(string path)
    {
        Assert.False(UnrealObjectPathValidator.IsValid(path));
    }

    [Fact]
    public void SoftReferencePathValidatorAcceptsNestedSubobjects()
    {
        Assert.True(UnrealObjectPathValidator.IsValid("/Game/Props/Cube.Cube:Mesh.Nested"));
        Assert.True(UnrealObjectPathValidator.IsValid("/Game/Foo(Bar)/Cube.Cube"));
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
