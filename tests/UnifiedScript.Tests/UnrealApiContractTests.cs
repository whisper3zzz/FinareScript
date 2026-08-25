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
