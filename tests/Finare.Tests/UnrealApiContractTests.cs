using System.Text.Json;
using Finare.CSharp;
using Finare.Unreal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
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
        Assert.Equal("FinareComponent", nodeType.ManagedTypeName);
        Assert.Contains(nodeType.Nodes, static node => node.Id ==
            "ue.get|FinareComponent|ActiveGeneration");
        Assert.Contains(nodeType.Nodes, static node => node.Id ==
            "ue.call|FinareComponent|Add");
        UnrealApiNodeDescriptor add = Assert.Single(
            nodeType.Nodes,
            static node => node.Title == "Add");
        Assert.Equal(
            $"0x{UnrealMemberId.FromPath(add.MemberPath):X16}",
            add.MemberId);
        Assert.Contains(add.MemberId, native.Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContractPreservesBlueprintFunctionNamesWithSpacesAcrossOutputs()
    {
        UnrealReflectionManifest fixture = LoadFixture();
        const string sourceTypePath = "/Script/FinareRuntime.FinareComponent";
        const string typePath = "/Game/UI/Hud/W_Healthbar.W_Healthbar_C";
        UnrealReflectedType component = Assert.Single(
            fixture.Types,
            type => type.Path == sourceTypePath);
        UnrealReflectedFunction sourceFunction = Assert.Single(
            component.Functions,
            function => function.Name == "Add");
        UnrealReflectedFunction healthChanged = sourceFunction with
        {
            Name = "Health Changed",
            Path = typePath + ":Health Changed",
            Parameters = [],
            NativeBinding = null,
            Metadata = sourceFunction.Metadata with { DisplayName = null },
        };
        UnrealReflectedFunction healthChangedOverload = healthChanged with
        {
            Path = typePath + ":Health Changed(int32)",
            Parameters =
            [
                new UnrealReflectedParameter(
                    "Value",
                    new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                    UnrealParameterDirection.Input),
            ],
        };
        UnrealReflectedType blueprintType = component with
        {
            Path = typePath,
            Name = "W_Healthbar_C",
            NativeName = "UW_Healthbar_C",
            Module = "Game",
            HeaderPath = "Game/W_Healthbar.h",
            Functions = [healthChanged, healthChangedOverload],
            Properties = [],
            Delegates = [],
        };
        UnrealReflectionManifest reflection = fixture with
        {
            Modules = [.. fixture.Modules, "Game"],
            Types = [.. fixture.Types, blueprintType],
        };
        UnrealApiContract contract = new(
            UnrealApiContract.CurrentSchemaVersion,
            "Tests.BlueprintHealth",
            "Finare.Unreal.Generated",
            "FinareGeneratedThunks.h",
            "UE::Finare::Generated::Tests",
            [new(typePath, ["Health Changed()", "Health Changed(int32)"])]);

        UnrealApiContractProjection projection = UnrealApiContractProjector.Project(
            reflection,
            contract);
        UnrealBindingGenerationResult managed = new UnrealBindingGenerator().Generate(
            projection.Reflection,
            new UnrealBindingGeneratorOptions(projection.Contract.ManagedNamespace));
        UnrealNativeThunkGenerationResult native = new UnrealNativeThunkGenerator().Generate(
            projection.NativeReflection,
            new UnrealNativeThunkGeneratorOptions(
                projection.Contract.NativeHeader,
                projection.Contract.NativeNamespace));
        UnrealBindingCoverageReport coverage = UnrealBindingCoverageReport.Create(
            projection,
            managed,
            native);
        UnrealApiNodeCatalog nodes = UnrealApiNodeCatalogGenerator.Generate(projection);
        UnrealApiBrowserDocument browser = UnrealApiBrowserGenerator.Generate(
            projection,
            nodes,
            coverage);

        UnrealReflectedFunction[] selectedFunctions = projection.Reflection.Types
            .Single(type => type.Path == typePath)
            .Functions
            .ToArray();
        Assert.Equal(2, selectedFunctions.Length);
        UnrealReflectedFunction selectedFunction = Assert.Single(
            selectedFunctions,
            function => function.Path.EndsWith(":Health Changed", StringComparison.Ordinal));
        UnrealReflectedFunction selectedOverload = Assert.Single(
            selectedFunctions,
            function => function.Path.EndsWith(":Health Changed(int32)", StringComparison.Ordinal));
        Assert.Equal("Health Changed", selectedFunction.Name);
        Assert.Equal(
            "/Game/UI/Hud/W_Healthbar.W_Healthbar_C:Health Changed",
            selectedFunction.Path);
        Assert.Equal(
            "/Game/UI/Hud/W_Healthbar.W_Healthbar_C:Health Changed(int32)",
            selectedOverload.Path);
        Assert.Empty(managed.Diagnostics);
        Assert.Contains(native.Diagnostics, diagnostic =>
            diagnostic.Code == "USUE3010" &&
            diagnostic.MemberPath == selectedFunction.Path);
        Assert.Contains(native.Diagnostics, diagnostic =>
            diagnostic.Code == "USUE3010" &&
            diagnostic.MemberPath == selectedOverload.Path);
        Assert.Contains("Health_Changed", managed.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("Health Changed(", native.Source, StringComparison.Ordinal);

        CSharpCompilationRequest generatedRequest = CSharpCompilationRequest.Single(
            managed.Source,
            "GeneratedHealthBindings.cs");
        var generatedCompilation = new CSharpCompilationHost().Create(
            generatedRequest,
            [CSharpSyntaxTree.ParseText(managed.Source)],
            [MetadataReference.CreateFromFile(typeof(UnrealObjectHandle).Assembly.Location)]);
        using var generatedAssembly = new MemoryStream();
        var emit = generatedCompilation.Emit(generatedAssembly);
        Assert.DoesNotContain(
            emit.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        UnrealApiNodeDescriptor node = Assert.Single(
            nodes.Types.SelectMany(static type => type.Nodes),
            item => item.MemberPath == selectedFunction.Path);
        Assert.Equal("Health Changed", node.Title);
        Assert.Equal(selectedFunction.Path, node.MemberPath);
        Assert.Equal(
            $"0x{UnrealMemberId.FromPath(selectedFunction.Path):X16}",
            node.MemberId);
        UnrealApiBrowserMember browserMember = Assert.Single(
            browser.Types.Single(type => type.Path == typePath).Members,
            item => item.MemberPath == selectedFunction.Path);
        Assert.Equal("Health Changed", browserMember.Title);
        Assert.True(browserMember.Available);
        Assert.Equal(
            UnrealBindingDisposition.ReflectionFallback,
            browserMember.Disposition);
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
                    Types = [new("/Script/FinareRuntime.Missing", ["Add"])],
                }));
        Assert.Contains("absent", missingType.Message, StringComparison.OrdinalIgnoreCase);

        JsonException missingMember = Assert.Throws<JsonException>(() =>
            UnrealApiContractProjector.Project(
                reflection,
                CreateContract("MissingMember")));
        Assert.Contains("MissingMember", missingMember.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContractAcceptsSpacedRawAndOverloadSelectors()
    {
        UnrealApiContract contract = CreateContract(
            " Foo ",
            "Health Changed",
            "Health Changed(int32)",
            "Move(UE::Finare::NativeExample::FCounterChildconst&)");
        UnrealApiContract normalized = UnrealApiContractJson.Deserialize(
            UnrealApiContractJson.Serialize(contract));

        Assert.Equal(
            [
                "Foo",
                "Health Changed",
                "Health Changed(int32)",
                "Move(UE::Finare::NativeExample::FCounterChildconst&)",
            ],
            Assert.Single(normalized.Types).Members);
    }

    [Theory]
    [InlineData("Foo:")]
    [InlineData("Fo\u0001o")]
    [InlineData("Foo\u0001")]
    [InlineData("Foo(int, float)")]
    [InlineData("Foo(int )")]
    [InlineData("Foo(int)(other)")]
    [InlineData("Foo(\t)")]
    public void ContractRejectsMalformedMemberSelectors(string selector)
    {
        Assert.Throws<JsonException>(() => UnrealApiContractJson.Serialize(CreateContract(selector)));
    }

    [Fact]
    public void EmptyMemberSelectorsAreReservedForReflectedEnums()
    {
        UnrealReflectionManifest fixture = LoadFixture();
        const string enumPath = "/Script/Engine.EEndPlayReason";
        UnrealReflectedType endPlayReason = new(
            enumPath,
            "EEndPlayReason",
            "EEndPlayReason",
            "Engine",
            UnrealReflectedTypeKind.Enum,
            null,
            new UnrealTypeTraits(Abstract: false, BlueprintType: true),
            [],
            [],
            "Engine/EngineTypes.h")
        {
            EnumValues =
            [
                new UnrealEnumValueDefinition("Destroyed", 0),
                new UnrealEnumValueDefinition("LevelTransition", 1),
                new UnrealEnumValueDefinition("EndPlayInEditor", 2),
                new UnrealEnumValueDefinition("RemovedFromWorld", 3),
                new UnrealEnumValueDefinition("Quit", 4),
            ],
        };
        if (!fixture.Types.Any(type => type.Path == enumPath))
        {
            fixture = fixture with { Types = [.. fixture.Types, endPlayReason] };
        }

        UnrealApiContract enumContract = new(
            UnrealApiContract.CurrentSchemaVersion,
            "Tests.EnumSelector",
            "Finare.Unreal.Generated",
            "FinareGeneratedThunks.h",
            "UE::Finare::Generated::Tests",
            [new(enumPath, [])]);
        UnrealApiContractProjection projection = UnrealApiContractProjector.Project(
            fixture,
            enumContract);
        UnrealReflectedType selectedEnum = Assert.Single(projection.Reflection.Types);
        Assert.Equal(UnrealReflectedTypeKind.Enum, selectedEnum.Kind);
        Assert.Empty(selectedEnum.Properties);
        Assert.Empty(selectedEnum.Functions);
        Assert.Empty(selectedEnum.Delegates);

        JsonException emptyClass = Assert.Throws<JsonException>(() =>
            UnrealApiContractProjector.Project(
                fixture,
                enumContract with
                {
                    Types =
                    [new(
                        "/Script/FinareRuntime.FinareComponent",
                        [])],
                }));
        Assert.Contains("only reflected enum", emptyClass.Message, StringComparison.Ordinal);

        JsonException missingEnum = Assert.Throws<JsonException>(() =>
            UnrealApiContractProjector.Project(
                fixture,
                enumContract with { Types = [new("/Script/Engine.MissingEnum", [])] }));
        Assert.Contains("absent", missingEnum.Message, StringComparison.OrdinalIgnoreCase);

        string legacyEmptySelector = UnrealApiContractJson.Serialize(enumContract)
            .Replace(
                UnrealApiContract.CurrentSchemaVersion,
                UnrealApiContract.LegacySchemaVersion,
                StringComparison.Ordinal);
        JsonException legacyRejected = Assert.Throws<JsonException>(() =>
            UnrealApiContractJson.Deserialize(legacyEmptySelector));
        Assert.Contains("does not support an empty member selector", legacyRejected.Message);
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
        Assert.Equal("FinareComponent", type.GetProperty("managedTypeName").GetString());
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
                "Finare.Tests.Policy",
                "PolicyThunks.h",
                "UE::Finare::Generated::Policy",
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
        Assert.Equal(Finare.Core.UnifiedDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(member.MemberPath, diagnostic.MemberPaths);
    }

    private static UnrealReflectionManifest LoadFixture()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "UnrealFixtures",
            "FinareRuntime.reflection.json");
        return UnrealReflectionJson.Deserialize(File.ReadAllText(path));
    }

    private static UnrealApiContract CreateContract(params string[] members) => new(
        UnrealApiContract.CurrentSchemaVersion,
        "Tests.Runtime",
        "Finare.Unreal.Generated",
        "FinareGeneratedThunks.h",
        "UE::Finare::Generated::Tests",
        [new(
            "/Script/FinareRuntime.FinareComponent",
            members)]);
}
