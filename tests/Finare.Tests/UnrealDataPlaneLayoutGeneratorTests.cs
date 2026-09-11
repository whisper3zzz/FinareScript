using System.Text.Json;
using System.Text.Json.Nodes;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed class UnrealDataPlaneLayoutGeneratorTests
{
    [Fact]
    public void CheckedInManagedAndNativeLayoutsAreDeterministicSchemaOutputs()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "UnrealFixtures");
        UnrealDataPlaneLayoutSchema schema = UnrealDataPlaneLayoutJson.Deserialize(
            File.ReadAllText(Path.Combine(
                fixtureDirectory,
                "FinareRuntime.data-plane.json")));
        var generator = new UnrealDataPlaneLayoutGenerator();

        UnrealDataPlaneLayoutGenerationResult first = generator.Generate(schema);
        UnrealDataPlaneLayoutGenerationResult second = generator.Generate(schema);

        Assert.Equal(0xADC8F8D40068A6C4UL, first.SchemaFingerprint);
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
                    "FinareDataPlaneLayouts.gen.h"))
                .ReplaceLineEndings("\n"),
            first.NativeSource);
        Assert.Contains(
            "public partial struct UnrealCommandV4",
            first.ManagedSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "static_assert(sizeof(FFinareTraceResultV4) == 168);",
            first.NativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "MaximumTransformSnapshotCount = 65536u",
            first.NativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "MaximumGeneratedSnapshotCount = 65536u",
            first.NativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "MaximumGeneratedEventCount = 65536u",
            first.NativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "MaximumGeneratedChannelBytes = 16777216u",
            first.NativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "static_assert(sizeof(FFinareGeneratedDataViewV1) == 64);",
            first.NativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "static_assert(sizeof(FFinareFixedValueCollectionSnapshotV1) == 104);",
            first.NativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "static_assert(sizeof(FFinareBufferedGameplayEventV1) == 48);",
            first.NativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "static_assert(sizeof(FFinareGameplayDataTelemetryV1) == 192);",
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
        "FinareRuntime.data-plane.json"));
}
