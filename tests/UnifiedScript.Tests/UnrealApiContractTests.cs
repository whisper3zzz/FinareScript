using System.Text.Json;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealApiContractTests
{
    [Fact]
    public void ContractProjectsOneAllowlistIntoEveryGenerator()
    {
        UnrealReflectionManifest reflection = LoadFixture();
        UnrealApiContract contract = CreateContract(
            "ActiveGeneration",
            "Add",
            "StartScript");

        UnrealApiContractProjection projection = UnrealApiContractProjector.Project(
            reflection,
            contract);
        UnrealBindingGenerationResult managed = new UnrealBindingGenerator().Generate(
            projection.Reflection,
            new UnrealBindingGeneratorOptions(projection.Contract.ManagedNamespace));
        UnrealNativeThunkGenerationResult native = new UnrealNativeThunkGenerator().Generate(
            projection.Reflection,
            new UnrealNativeThunkGeneratorOptions(
                projection.Contract.NativeHeader,
                projection.Contract.NativeNamespace));
        UnrealApiNodeCatalog nodes = UnrealApiNodeCatalogGenerator.Generate(projection);
        UnrealBindingCoverageReport coverage = UnrealBindingCoverageReport.Create(
            projection.Reflection,
            managed,
            native);

        UnrealReflectedType selected = Assert.Single(projection.Reflection.Types);
        Assert.Equal(["ActiveGeneration"], selected.Properties.Select(static item => item.Name));
        Assert.Equal(["Add", "StartScript"], selected.Functions.Select(static item => item.Name));
        Assert.Empty(managed.Diagnostics);
        Assert.Empty(native.Diagnostics);
        Assert.Equal(3, coverage.Summary.MemberCount);
        Assert.Equal(3, coverage.Summary.NativeThunkCount);
        Assert.DoesNotContain("StopScript", managed.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("StopScript", native.Source, StringComparison.Ordinal);

        UnrealApiNodeType nodeType = Assert.Single(nodes.Types);
        Assert.Equal("UnifiedScriptComponent", nodeType.ManagedTypeName);
        Assert.Contains(nodeType.Nodes, static node => node.Id ==
            "ue.get|UnifiedScriptComponent|ActiveGeneration");
        Assert.Contains(nodeType.Nodes, static node => node.Id ==
            "ue.call|UnifiedScriptComponent|Add");
        UnrealApiNodeDescriptor add = Assert.Single(
            nodeType.Nodes,
            static node => node.Title == "Add");
        Assert.Equal(
            $"0x{UnrealMemberId.FromPath(add.MemberPath):X16}",
            add.MemberId);
        Assert.Contains(add.MemberId, native.Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContractRejectsStaleTypeAndMemberSelectors()
    {
        UnrealReflectionManifest reflection = LoadFixture();

        JsonException missingType = Assert.Throws<JsonException>(() =>
            UnrealApiContractProjector.Project(
                reflection,
                CreateContract("Add") with
                {
                    Types = [new("/Script/UEUnifiedScriptRuntime.Missing", ["Add"])],
                }));
        Assert.Contains("absent", missingType.Message, StringComparison.OrdinalIgnoreCase);

        JsonException missingMember = Assert.Throws<JsonException>(() =>
            UnrealApiContractProjector.Project(
                reflection,
                CreateContract("MissingMember")));
        Assert.Contains("MissingMember", missingMember.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContractJsonIsStrictCanonicalAndDeterministic()
    {
        UnrealApiContract contract = CreateContract("StartScript", "Add");
        string first = UnrealApiContractJson.Serialize(contract);
        string second = UnrealApiContractJson.Serialize(
            UnrealApiContractJson.Deserialize(first));

        Assert.Equal(first, second);
        Assert.True(first.IndexOf("Add", StringComparison.Ordinal) <
            first.IndexOf("StartScript", StringComparison.Ordinal));
        Assert.Throws<JsonException>(() => UnrealApiContractJson.Deserialize(
            first.Replace(
                "\"contractName\"",
                "\"unknown\": true, \"contractName\"",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void NodeCatalogIsReflectionCompatibleAndCarriesTypedPins()
    {
        UnrealApiContractProjection projection = UnrealApiContractProjector.Project(
            LoadFixture(),
            CreateContract("ActiveGeneration", "Add"));
        UnrealApiNodeCatalog catalog = UnrealApiNodeCatalogGenerator.Generate(projection);
        string json = UnrealApiNodeCatalogGenerator.Serialize(catalog);
        using JsonDocument parsed = JsonDocument.Parse(json);

        Assert.Equal(
            UnrealApiNodeCatalog.CurrentSchemaVersion,
            parsed.RootElement.GetProperty("schemaVersion").GetString());
        JsonElement type = Assert.Single(parsed.RootElement.GetProperty("types").EnumerateArray());
        Assert.Equal("UnifiedScriptComponent", type.GetProperty("managedTypeName").GetString());
        JsonElement add = Assert.Single(
            type.GetProperty("nodes").EnumerateArray(),
            static node => node.GetProperty("title").GetString() == "Add");
        Assert.Equal("function", add.GetProperty("kind").GetString());
        Assert.Contains(add.GetProperty("pins").EnumerateArray(), static pin =>
            pin.GetProperty("name").GetString() == "Target" &&
            pin.GetProperty("direction").GetString() == "input");
        Assert.Contains(add.GetProperty("pins").EnumerateArray(), static pin =>
            pin.GetProperty("name").GetString() == "ReturnValue" &&
            pin.GetProperty("direction").GetString() == "output");
    }

    [Fact]
    public void NodeCatalogCarriesGeneratedPaletteMetadataDefaultsAndRestrictions()
    {
        UnrealReflectionManifest fixture = UnrealReflectionJson.Deserialize(File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "UnrealBindingMatrix.reflection.json")));
        UnrealReflectedType fixtureType = Assert.Single(
            fixture.Types,
            static type => type.Path == "/Script/BindingMatrix.FixtureDerived");
        UnrealReflectedFunction spawn = Assert.Single(
            fixtureType.Functions,
            static function => function.Name == "Spawn");
        UnrealReflectedFunction worldTime = Assert.Single(
            fixtureType.Functions,
            static function => function.Name == "WorldTime");
        UnrealReflectionManifest reflection = fixture with
        {
            Types =
            [
                fixtureType with
                {
                    Functions =
                    [
                        spawn with
                        {
                            Metadata = spawn.Metadata with
                            {
                                Latent = true,
                                LatentInfoParameter = "Deferred",
                                Deprecated = true,
                                DeprecationMessage = "Use SpawnAsync.",
                            },
                        },
                        worldTime with
                        {
                            Metadata = worldTime.Metadata with { ThreadSafe = true },
                        },
                    ],
                },
            ],
        };
        UnrealApiContractProjection projection = UnrealApiContractProjector.Project(
            reflection,
            new UnrealApiContract(
                UnrealApiContract.CurrentSchemaVersion,
                "Tests.Policy",
                "UnifiedScript.Tests.Policy",
                "PolicyThunks.h",
                "UE::UnifiedScript::Generated::Policy",
                [new("/Script/BindingMatrix.FixtureDerived", ["Spawn", "WorldTime"])]));

        UnrealApiNodeCatalog catalog = UnrealApiNodeCatalogGenerator.Generate(projection);
        UnrealApiNodeDescriptor spawnNode = Assert.Single(
            catalog.Types.SelectMany(static type => type.Nodes),
            static node => node.MemberPath.EndsWith(":Spawn", StringComparison.Ordinal));
        Assert.Equal("Spawn Fixture", spawnNode.Title);
        Assert.Equal("Gameplay|Spawn", spawnNode.Category);
        Assert.Equal("Spawns fixture objects.", spawnNode.Tooltip);
        Assert.True(spawnNode.Latent);
        Assert.Equal("Deferred", spawnNode.LatentInfoParameter);
        Assert.Equal(UnrealApiThreadRequirement.GameThread, spawnNode.ThreadRequirement);
        Assert.True(spawnNode.Deprecated);
        Assert.Equal("Use SpawnAsync.", spawnNode.DeprecationMessage);
        Assert.Equal(
            ["1", "false"],
            spawnNode.Pins
                .Where(static pin => pin.DefaultValue is not null)
                .Select(static pin => pin.DefaultValue));

        UnrealApiNodeDescriptor worldNode = Assert.Single(
            catalog.Types.SelectMany(static type => type.Nodes),
            static node => node.MemberPath.EndsWith(":WorldTime", StringComparison.Ordinal));
        Assert.Equal(UnrealApiThreadRequirement.AnyThread, worldNode.ThreadRequirement);
        Assert.Equal(UnrealApiWorldRequirement.WorldContext, worldNode.WorldRequirement);
        Assert.Equal("WorldContextObject", worldNode.WorldContextParameter);

        string json = UnrealApiNodeCatalogGenerator.Serialize(catalog);
        Assert.Contains("\"threadRequirement\": \"anyThread\"", json, StringComparison.Ordinal);
        Assert.Contains("\"worldRequirement\": \"worldContext\"", json, StringComparison.Ordinal);
        Assert.Contains("\"latentInfoParameter\": \"Deferred\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiBrowserIsDeterministicAndJoinsNodesWithCoverageDiagnostics()
    {
        UnrealApiContractProjection projection = UnrealApiContractProjector.Project(
            LoadFixture(),
            CreateContract("Add"));
        UnrealBindingGenerationResult managed = new UnrealBindingGenerator().Generate(
            projection.Reflection,
            new UnrealBindingGeneratorOptions(projection.Contract.ManagedNamespace));
        UnrealNativeThunkGenerationResult native = new UnrealNativeThunkGenerator().Generate(
            projection.NativeReflection,
            new UnrealNativeThunkGeneratorOptions(
                projection.Contract.NativeHeader,
                projection.Contract.NativeNamespace));
        UnrealBindingCoverageReport generatedCoverage = UnrealBindingCoverageReport.Create(
            projection,
            managed,
            native);
        UnrealBindingCoverageEntry generatedEntry = Assert.Single(generatedCoverage.Entries);
        UnrealBindingCoverageReport coverage = generatedCoverage with
        {
            Summary = generatedCoverage.Summary with
            {
                DirectThunkCount = 0,
                RejectedCount = 1,
            },
            Entries =
            [
                generatedEntry with
                {
                    Disposition = UnrealBindingDisposition.Rejected,
                    DiagnosticCodes = ["USUE4999"],
                    Message = "The test contract rejects this route.",
                },
            ],
        };
        UnrealApiNodeCatalog nodes = UnrealApiNodeCatalogGenerator.Generate(projection);

        UnrealApiBrowserDocument browser = UnrealApiBrowserGenerator.Generate(
            projection,
            nodes,
            coverage);
        string first = UnrealApiBrowserJson.Serialize(browser);
        string second = UnrealApiBrowserJson.Serialize(
            UnrealApiBrowserJson.Deserialize(first));

        Assert.Equal(first, second);
        Assert.Equal(UnrealApiBrowserDocument.CurrentSchemaVersion, browser.SchemaVersion);
        Assert.Equal(64, browser.Sources.InputSha256.Length);
        Assert.Equal(1, browser.Summary.TypeCount);
        Assert.Equal(1, browser.Summary.MemberCount);
        Assert.Equal(1, browser.Summary.RejectedCount);
        UnrealApiBrowserMember member = Assert.Single(
            Assert.Single(browser.Types).Members,
            static item => item.Title == "Add");
        Assert.False(member.Available);
        Assert.Equal(UnrealBindingDisposition.Rejected, member.Disposition);
        Assert.Contains("USUE4999", member.DiagnosticCodes);
        UnrealApiBrowserDiagnostic diagnostic = Assert.Single(browser.Diagnostics);
        Assert.Equal(UnifiedScript.Core.UnifiedDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(member.MemberPath, diagnostic.MemberPaths);
    }

    private static UnrealReflectionManifest LoadFixture()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "UnrealFixtures",
            "UEUnifiedScriptRuntime.reflection.json");
        return UnrealReflectionJson.Deserialize(File.ReadAllText(path));
    }

    private static UnrealApiContract CreateContract(params string[] members) => new(
        UnrealApiContract.CurrentSchemaVersion,
        "Tests.Runtime",
        "UnifiedScript.Unreal.Generated",
        "UnifiedScriptGeneratedThunks.h",
        "UE::UnifiedScript::Generated::Tests",
        [new(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent",
            members)]);
}
