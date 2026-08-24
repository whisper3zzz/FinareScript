using System.Text.Json;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealReflectionJsonTests
{
    [Fact]
    public void RoundTripNormalizesModulesTypesAndMembers()
    {
        UnrealReflectionManifest manifest = UnrealReflectionManifest.Create(
            new UnrealEngineIdentity(5, 7, 4, " UE5 "),
            ["ZetaRuntime", "AlphaRuntime"],
            [
                Type("/Script/ZetaRuntime.ZetaComponent", "ZetaComponent", "ZetaRuntime"),
                Type("/Script/AlphaRuntime.AlphaComponent", "AlphaComponent", "AlphaRuntime") with
                {
                    Properties =
                    [
                        Property("/Script/AlphaRuntime.AlphaComponent:Zeta", "Zeta", UnrealValueKind.Int32),
                        Property("/Script/AlphaRuntime.AlphaComponent:Alpha", "Alpha", UnrealValueKind.Boolean),
                    ],
                    Functions =
                    [
                        Function("/Script/AlphaRuntime.AlphaComponent:Zeta", "Zeta"),
                        Function("/Script/AlphaRuntime.AlphaComponent:Alpha", "Alpha"),
                    ],
                },
            ]);

        string json = UnrealReflectionJson.Serialize(manifest);
        UnrealReflectionManifest roundTripped = UnrealReflectionJson.Deserialize(json);

        Assert.Equal(["AlphaRuntime", "ZetaRuntime"], roundTripped.Modules);
        Assert.Equal(
            ["AlphaComponent", "ZetaComponent"],
            roundTripped.Types.Select(static type => type.Name));
        Assert.Equal(
            ["Alpha", "Zeta"],
            roundTripped.Types[0].Properties.Select(static property => property.Name));
        Assert.Equal(
            ["Alpha", "Zeta"],
            roundTripped.Types[0].Functions.Select(static function => function.Name));
        Assert.Equal("UE5", roundTripped.Engine.Branch);
        Assert.Equal(json, UnrealReflectionJson.Serialize(roundTripped));
    }

    [Fact]
    public void DeserializeRejectsUnknownFieldsDuplicatePathsAndMalformedContainers()
    {
        UnrealReflectionManifest manifest = UnrealReflectionManifest.Create(
            new UnrealEngineIdentity(5, 7, 4, "UE5"),
            ["FixtureRuntime"],
            [Type("/Script/FixtureRuntime.FixtureObject", "FixtureObject", "FixtureRuntime")]);
        string json = UnrealReflectionJson.Serialize(manifest, writeIndented: false);

        string unknown = json.Replace(
            "\"types\":",
            "\"futureField\":true,\"types\":",
            StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => UnrealReflectionJson.Deserialize(unknown));

        Assert.Throws<JsonException>(() => UnrealReflectionJson.NormalizeAndValidate(
            manifest with { Types = [manifest.Types[0], manifest.Types[0]] }));

        UnrealReflectedProperty malformed = Property(
            "/Script/FixtureRuntime.FixtureObject:Values",
            "Values",
            UnrealValueKind.Array) with
        {
            Type = new UnrealTypeReference(UnrealValueKind.Array, "TArray<int32>"),
        };
        Assert.Throws<JsonException>(() => UnrealReflectionJson.NormalizeAndValidate(
            manifest with
            {
                Types = [manifest.Types[0] with { Properties = [malformed] }],
            }));
    }

    private static UnrealReflectedType Type(string path, string name, string module) => new(
        path,
        name,
        $"U{name}",
        module,
        UnrealReflectedTypeKind.Class,
        "/Script/Engine.Object",
        new UnrealTypeTraits(Abstract: false, BlueprintType: true),
        [],
        []);

    private static UnrealReflectedProperty Property(
        string path,
        string name,
        UnrealValueKind kind) => new(
        path,
        name,
        new UnrealTypeReference(kind, kind.ToString()),
        new UnrealPropertyAccess(Readable: true, Writable: true, EditorVisible: true));

    private static UnrealReflectedFunction Function(string path, string name) => new(
        path,
        name,
        new UnrealTypeReference(UnrealValueKind.Void, "void"),
        [],
        new UnrealFunctionTraits(Static: false, Pure: false, Callable: true));
}
