using System.Text.Json;
using System.Text.Json.Nodes;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealDataPlaneLayoutGeneratorTests
{
    [Fact]
    public void CheckedInManagedAndNativeLayoutsAreDeterministicSchemaOutputs()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "UnrealFixtures");
        UnrealDataPlaneLayoutSchema schema = UnrealDataPlaneLayoutJson.Deserialize(
            File.ReadAllText(Path.Combine(
                fixtureDirectory,
                "UEUnifiedScriptRuntime.data-plane.json")));
        var generator = new UnrealDataPlaneLayoutGenerator();

        UnrealDataPlaneLayoutGenerationResult first = generator.Generate(schema);
        UnrealDataPlaneLayoutGenerationResult second = generator.Generate(schema);

        Assert.Equal(0x754A31DCE3A25636UL, first.SchemaFingerprint);
        Assert.Equal(first, second);
        Assert.Equal(
            File.ReadAllText(Path.Combine(
                    fixtureDirectory,
                    "UnrealDataPlaneLayouts.g.cs"))
                .ReplaceLineEndings("\n"),
            first.ManagedSource);
        Assert.Equal(
            File.ReadAllText(Path.Combine(
                    fixtureDirectory,
                    "UnifiedScriptDataPlaneLayouts.gen.h"))
                .ReplaceLineEndings("\n"),
            first.NativeSource);
        Assert.Contains(
            "public partial struct UnrealCommandV4",
            first.ManagedSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "static_assert(sizeof(FUnifiedScriptTraceResultV4) == 168);",
            first.NativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "MaximumTransformSnapshotCount = 65536u",
            first.NativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "static_assert(sizeof(FUnifiedScriptGameplayDataTelemetryV1) == 192);",
            first.NativeSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnknownPropertiesAndComputedLayoutDrift()
    {
        string json = LoadSchemaJson();
        JsonObject unknown = JsonNode.Parse(json)!.AsObject();
        unknown["unexpected"] = true;
        Assert.Throws<JsonException>(
            () => UnrealDataPlaneLayoutJson.Deserialize(unknown.ToJsonString()));

        JsonObject drifted = JsonNode.Parse(json)!.AsObject();
        JsonArray structs = drifted["structs"]!.AsArray();
        JsonObject command = structs
            .Select(static item => item!.AsObject())
            .Single(static item => item["name"]!.GetValue<string>() == "CommandV4");
        command["expectedSize"] = 216;
        UnrealDataPlaneLayoutSchema driftedSchema =
            UnrealDataPlaneLayoutJson.Deserialize(drifted.ToJsonString());
        JsonException exception = Assert.Throws<JsonException>(
            () => new UnrealDataPlaneLayoutGenerator().Generate(driftedSchema));
        Assert.Contains("computes to 224 bytes", exception.Message, StringComparison.Ordinal);
    }

    private static string LoadSchemaJson() => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "UnrealFixtures",
        "UEUnifiedScriptRuntime.data-plane.json"));
}
