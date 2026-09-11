using System.Text.Json;
using Finare.Unreal;

namespace Finare.Tests;

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
                    Delegates =
                    [
                        Delegate("/Script/AlphaRuntime.AlphaComponent:ZetaEvent", "ZetaEvent"),
                        Delegate("/Script/AlphaRuntime.AlphaComponent:AlphaEvent", "AlphaEvent"),
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
        Assert.Equal(
            ["AlphaEvent", "ZetaEvent"],
            roundTripped.Types[0].Delegates.Select(static item => item.Name));
        Assert.Equal("UE5", roundTripped.Engine.Branch);
        Assert.Equal(UnrealReflectionManifest.CurrentSchemaVersion, roundTripped.SchemaVersion);
        Assert.Equal(json, UnrealReflectionJson.Serialize(roundTripped));
    }

    [Fact]
    public void DeserializeMigratesLegacyManifestWithoutDelegates()
    {
        UnrealReflectionManifest manifest = UnrealReflectionManifest.Create(
            new UnrealEngineIdentity(5, 7, 4, "UE5"),
            ["FixtureRuntime"],
            [Type("/Script/FixtureRuntime.FixtureObject", "FixtureObject", "FixtureRuntime")]);
        string legacyJson = UnrealReflectionJson.Serialize(manifest).Replace(
            UnrealReflectionManifest.CurrentSchemaVersion,
            UnrealReflectionManifest.LegacySchemaVersion,
            StringComparison.Ordinal);

        UnrealReflectionManifest migrated = UnrealReflectionJson.Deserialize(legacyJson);

        Assert.Equal(UnrealReflectionManifest.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Empty(migrated.Types[0].Delegates);
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

    [Fact]
    public void RoundTripCarriesFunctionAndDelegateAuthoringPolicyMetadata()
    {
        UnrealReflectedFunction latent = Function(
            "/Script/FixtureRuntime.FixtureObject:Wait",
            "Wait") with
        {
            Parameters =
            [
                new UnrealReflectedParameter(
                    "WorldContextObject",
                    new UnrealTypeReference(
                        UnrealValueKind.Object,
                        "UObject*",
                        "/Script/CoreUObject.Object"),
                    UnrealParameterDirection.Input),
                new UnrealReflectedParameter(
                    "LatentInfo",
                    new UnrealTypeReference(
                        UnrealValueKind.Int32,
                        "int32"),
                    UnrealParameterDirection.Input),
            ],
            Metadata = new UnrealBlueprintMetadata(
                DisplayName: "Wait for Fixture",
                Category: "Fixture|Async",
                Tooltip: "Completes on a later frame.",
                WorldContextParameter: "WorldContextObject")
            {
                Latent = true,
                LatentInfoParameter = "LatentInfo",
            },
        };
        UnrealReflectedDelegate signal = Delegate(
            "/Script/FixtureRuntime.FixtureObject:OnSignal",
            "OnSignal") with
        {
            Metadata = new UnrealBlueprintMetadata(
                DisplayName: "Signal Received",
                Category: "Fixture|Events",
                Tooltip: "Raised by the fixture."),
        };
        UnrealReflectionManifest manifest = UnrealReflectionManifest.Create(
            new UnrealEngineIdentity(5, 8, 1, "UE5"),
            ["FixtureRuntime"],
            [Type("/Script/FixtureRuntime.FixtureObject", "FixtureObject", "FixtureRuntime") with
            {
                Functions = [latent],
                Delegates = [signal],
            }]);

        UnrealReflectionManifest roundTripped = UnrealReflectionJson.Deserialize(
            UnrealReflectionJson.Serialize(manifest));

        UnrealReflectedFunction function = Assert.Single(roundTripped.Types[0].Functions);
        Assert.True(function.Metadata.Latent);
        Assert.Equal("LatentInfo", function.Metadata.LatentInfoParameter);
        Assert.Equal("WorldContextObject", function.Metadata.WorldContextParameter);
        Assert.Equal("Fixture|Events", Assert.Single(roundTripped.Types[0].Delegates).Metadata.Category);

        Assert.Throws<JsonException>(() => UnrealReflectionJson.NormalizeAndValidate(
            manifest with
            {
                Types =
                [
                    manifest.Types[0] with
                    {
                        Functions =
                        [
                            latent with
                            {
                                Metadata = latent.Metadata with { ThreadSafe = true },
                            },
                        ],
                    },
                ],
            }));
    }

    [Fact]
    public void PreservesBlueprintFunctionNamesWithSpaces()
    {
        const string functionName = "Health Changed";
        UnrealReflectionManifest manifest = UnrealReflectionManifest.Create(
            new UnrealEngineIdentity(5, 8, 1, "UE5"),
            ["FixtureRuntime"],
            [Type("/Script/FixtureRuntime.FixtureObject", "FixtureObject", "FixtureRuntime") with
            {
                Functions =
                [
                    Function(
                        "/Script/FixtureRuntime.FixtureObject:Health Changed",
                        functionName),
                ],
            }]);

        string json = UnrealReflectionJson.Serialize(manifest, writeIndented: false);
        UnrealReflectionManifest roundTripped = UnrealReflectionJson.Deserialize(json);

        UnrealReflectedFunction function = Assert.Single(roundTripped.Types[0].Functions);
        Assert.Equal(functionName, function.Name);
        Assert.Equal(
            "/Script/FixtureRuntime.FixtureObject:Health Changed",
            function.Path);
        Assert.Equal(json, UnrealReflectionJson.Serialize(roundTripped, writeIndented: false));
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

    private static UnrealReflectedDelegate Delegate(string path, string name) => new(
        path,
        name,
        $"/Script/AlphaRuntime.AlphaComponent:{name}__DelegateSignature",
        new UnrealTypeReference(UnrealValueKind.Void, "void"),
        [new UnrealReflectedParameter(
            "Value",
            new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
            UnrealParameterDirection.Input)],
        new UnrealDelegateTraits(
            UnrealDelegateKind.Multicast,
            BlueprintAssignable: true,
            BlueprintCallable: true,
            BlueprintAuthorityOnly: false));
}
