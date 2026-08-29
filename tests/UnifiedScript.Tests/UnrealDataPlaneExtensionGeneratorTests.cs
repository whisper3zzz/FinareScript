using System.Text.Json;
using System.Text.Json.Nodes;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealDataPlaneExtensionGeneratorTests
{
    [Fact]
    public void BlockBreakerExtensionGeneratesTypedManagedNativeAndCatalogOutputs()
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "UnrealFixtures");
        UnrealDataPlaneExtensionSchema schema = UnrealDataPlaneExtensionJson.Deserialize(
            File.ReadAllText(Path.Combine(
                fixtures,
                "BlockBreaker.data-plane-extension.json")));
        var generator = new UnrealDataPlaneExtensionGenerator();

        UnrealDataPlaneExtensionGenerationResult first = generator.Generate(schema);
        UnrealDataPlaneExtensionGenerationResult second = generator.Generate(schema);

        Assert.Equal(0xDE4BA9E28216D610UL, first.SchemaFingerprint);
        Assert.Equal(first, second);
        Assert.Equal(
            File.ReadAllText(Path.Combine(fixtures, "BlockBreakerDataPlaneExtension.g.cs"))
                .ReplaceLineEndings("\n"),
            first.ManagedSource);
        Assert.Equal(
            File.ReadAllText(Path.Combine(
                    fixtures,
                    "UnifiedScriptBlockBreakerDataPlaneExtension.gen.h"))
                .ReplaceLineEndings("\n"),
            first.NativeSource);
        Assert.Equal(
            File.ReadAllText(Path.Combine(
                    fixtures,
                    "BlockBreaker.data-plane-extension.catalog.json"))
                .ReplaceLineEndings("\n"),
            first.CatalogJson);
        Assert.Contains(
            "UnrealDataPlaneExtensionChannel<BlockBreakerTargetSnapshotV1> TargetSnapshots",
            first.ManagedSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "FUnifiedScriptDataPlaneExtensionDescriptor Descriptor",
            first.NativeSource,
            StringComparison.Ordinal);
        UnrealDataPlaneExtensionCatalog catalog =
            UnrealDataPlaneExtensionJson.DeserializeCatalog(first.CatalogJson);
        Assert.Equal("BlockBreaker.SpatialCombat", catalog.ExtensionId);
        Assert.Equal(2, catalog.Channels.Count);
        Assert.Equal(
            ["ImpactEvents", "TargetSnapshots"],
            catalog.Channels.Select(static channel => channel.Name));
    }

    [Fact]
    public void ExtensionRejectsUnknownFieldsLayoutDriftAndCapacityEscape()
    {
        string json = LoadSchemaJson();
        JsonObject unknown = JsonNode.Parse(json)!.AsObject();
        unknown["unexpected"] = true;
        Assert.Throws<JsonException>(
            () => UnrealDataPlaneExtensionJson.Deserialize(unknown.ToJsonString()));

        JsonObject drift = JsonNode.Parse(json)!.AsObject();
        drift["channels"]!.AsArray()[0]!.AsObject()["expectedSize"] = 96;
        UnrealDataPlaneExtensionSchema driftSchema =
            UnrealDataPlaneExtensionJson.Deserialize(drift.ToJsonString());
        JsonException driftError = Assert.Throws<JsonException>(
            () => new UnrealDataPlaneExtensionGenerator().Generate(driftSchema));
        Assert.Contains("computes to 88 bytes", driftError.Message, StringComparison.Ordinal);

        JsonObject escaped = JsonNode.Parse(json)!.AsObject();
        JsonObject channel = escaped["channels"]!.AsArray()[0]!.AsObject();
        channel["maximumEntries"] = 65537;
        Assert.Throws<JsonException>(
            () => UnrealDataPlaneExtensionJson.Deserialize(escaped.ToJsonString()));
    }

    [Fact]
    public void TypedDescriptorRejectsRowAndCompatibilityDrift()
    {
        var compatible = new UnrealDataPlaneExtensionIdentity(
            "Tests.Extension",
            1,
            1,
            UnrealSharedDataAbiV4.Version,
            UnrealDataPlaneLayoutV1.CompatibilityId,
            2);
        var channel = new UnrealDataPlaneExtensionChannel<TestRow>(
            compatible,
            "Rows",
            UnrealDataPlaneExtensionChannelKind.Snapshot,
            UnrealDataPlaneExtensionPublicationPhase.Native,
            3,
            8,
            4,
            32,
            "extension.tests.rows");
        Assert.Equal(8U, channel.EntrySize);

        Assert.Throws<ArgumentException>(() =>
            new UnrealDataPlaneExtensionChannel<TestRow>(
                compatible,
                "Rows",
                UnrealDataPlaneExtensionChannelKind.Snapshot,
                UnrealDataPlaneExtensionPublicationPhase.Native,
                3,
                16,
                4,
                64,
                "extension.tests.rows"));
        Assert.Throws<ArgumentException>(() =>
            new UnrealDataPlaneExtensionChannel<TestRow>(
                compatible,
                "Rows",
                UnrealDataPlaneExtensionChannelKind.Snapshot,
                UnrealDataPlaneExtensionPublicationPhase.Native,
                3,
                8,
                UnrealSharedDataAbiV4.MaximumGeneratedSnapshotCount + 1,
                UnrealSharedDataAbiV4.MaximumGeneratedChannelBytes,
                "extension.tests.rows"));
        Assert.Throws<ArgumentException>(() =>
            new UnrealDataPlaneExtensionChannel<TestRow>(
                compatible,
                "Rows",
                UnrealDataPlaneExtensionChannelKind.Snapshot,
                UnrealDataPlaneExtensionPublicationPhase.Native,
                3,
                8,
                4,
                UnrealSharedDataAbiV4.MaximumGeneratedChannelBytes + 1,
                "extension.tests.rows"));
        var incompatible = new UnrealDataPlaneExtensionIdentity(
            "Tests.Incompatible",
            4,
            1,
            99,
            UnrealDataPlaneLayoutV1.CompatibilityId,
            5);
        Assert.Throws<NotSupportedException>(incompatible.RequireCompatibleRuntime);
    }

    private static string LoadSchemaJson() => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "UnrealFixtures",
        "BlockBreaker.data-plane-extension.json"));

    private readonly record struct TestRow(int Left, int Right);
}
