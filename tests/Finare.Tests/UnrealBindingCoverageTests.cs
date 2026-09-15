using Finare.Unreal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Finare.CSharp;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed class UnrealBindingCoverageTests
{
    [Fact]
    public void CommonValueFallbackClassificationMatchesTheLegacyCodecFamilies()
    {
        const string owner = "/Script/FixtureRuntime.Fixture";
        const string enumPath = "/Script/FixtureRuntime.EFixtureMode";
        UnrealTypeReference[] types =
        [
            new(UnrealValueKind.Int8, "int8"), new(UnrealValueKind.UInt8, "uint8"),
            new(UnrealValueKind.Int16, "int16"), new(UnrealValueKind.UInt16, "uint16"),
            new(UnrealValueKind.UInt32, "uint32"), new(UnrealValueKind.Int64, "int64"),
            new(UnrealValueKind.UInt64, "uint64"), new(UnrealValueKind.Enum, "EFixtureMode", enumPath),
            new(UnrealValueKind.Struct, "FRotator", "/Script/CoreUObject.Rotator"),
            new(UnrealValueKind.Struct, "FTransform", "/Script/CoreUObject.Transform"),
        ];
        var manifest = UnrealReflectionManifest.Create(new(5, 8, 1, "fixture"), ["FixtureRuntime"],
        [
            new(enumPath, "EFixtureMode", "EFixtureMode", "FixtureRuntime", UnrealReflectedTypeKind.Enum,
                null, new(false, true), [], [], "Fixture.h") { EnumValues = [new("Idle", 0), new("Active", 1)] },
            new(owner, "Fixture", "UFixture", "FixtureRuntime", UnrealReflectedTypeKind.Class,
                "/Script/CoreUObject.Object", new(false, true),
                types.Select((type, i) => new UnrealReflectedProperty($"{owner}:Value{i}", $"Value{i}", type,
                    new(true, true, true, UnrealNativeAccess.Protected))).ToArray(),
                types.Select((type, i) => new UnrealReflectedFunction($"{owner}:Echo{i}", $"Echo{i}", type,
                    [new("Value", type, UnrealParameterDirection.Input)], new(false, false, true, UnrealNativeAccess.Protected))).ToArray(), "Fixture.h"),
        ]);
        var managed = new UnrealBindingGenerator().Generate(manifest);
        var native = new UnrealNativeThunkGenerator().Generate(manifest);
        var report = UnrealBindingCoverageReport.Create(manifest, managed, native);
        Assert.Equal(types.Length * 2, report.Entries.Count);
        Assert.All(report.Entries, entry => Assert.Equal(UnrealBindingDisposition.ReflectionFallback, entry.Disposition));
        Assert.True(report.Gate.Passed);
    }

    [Fact]
    public void M71MatrixMatchesAllGoldenArtifactsAndCompiles()
    {
        string fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        UnrealReflectionManifest reflection = UnrealReflectionJson.Deserialize(File.ReadAllText(
            Path.Combine(fixtureRoot, "UnrealBindingMatrix.reflection.json")));
        UnrealApiContract contract = UnrealApiContractJson.Deserialize(File.ReadAllText(
            Path.Combine(fixtureRoot, "UnrealBindingMatrix.api-contract.json")));
        UnrealApiContractProjection projection = UnrealApiContractProjector.Project(reflection, contract);
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

        Assert.Equal(
            File.ReadAllText(Path.Combine(fixtureRoot, "UnrealBindingMatrix.bindings.golden.cs")),
            managed.Source);
        Assert.Equal(
            File.ReadAllText(Path.Combine(fixtureRoot, "UnrealBindingMatrix.thunks.golden.cpp")),
            native.Source);
        Assert.Equal(
            File.ReadAllText(Path.Combine(fixtureRoot, "UnrealBindingMatrix.coverage.golden.json")),
            UnrealBindingCoverageJson.Serialize(coverage));
        Assert.Equal(
            File.ReadAllText(Path.Combine(fixtureRoot, "UnrealBindingMatrix.nodes.golden.json")),
            UnrealApiNodeCatalogGenerator.Serialize(nodes));

        Assert.True(coverage.Gate.Passed);
        Assert.Equal(18, coverage.Summary.MemberCount);
        Assert.Equal(3, coverage.Summary.DirectThunkCount);
        Assert.Equal(12, coverage.Summary.CallFrameCount);
        Assert.Equal(1, coverage.Summary.DataPlaneOperationCount);
        Assert.Equal(1, coverage.Summary.ReflectionFallbackCount);
        Assert.Equal(1, coverage.Summary.RejectedCount);
        Assert.DoesNotContain("UnsafeValue", managed.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("BatchMove", managed.Source, StringComparison.Ordinal);
        Assert.Contains("public float Scale(float value)", managed.Source, StringComparison.Ordinal);
        Assert.Contains("public int Scale(int value)", managed.Source, StringComparison.Ordinal);
        Assert.Contains("public global::Finare.Unreal.UnrealObject? Spawn()", managed.Source, StringComparison.Ordinal);
        Assert.Contains("UnrealSoftObjectHandle", managed.Source, StringComparison.Ordinal);
        Assert.Equal(
            nodes.Types.SelectMany(static type => type.Nodes).Count(),
            nodes.Types.SelectMany(static type => type.Nodes)
                .Select(static node => node.Id).Distinct(StringComparer.Ordinal).Count());

        CSharpCompilation compilation = new CSharpCompilationHost().Create(
            CSharpSyntaxTree.ParseText(managed.Source),
            [MetadataReference.CreateFromFile(typeof(UnrealObject).Assembly.Location)]);
        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void CoverageBaselineReportsStableRegressionDiagnostics()
    {
        string fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        UnrealReflectionManifest reflection = UnrealReflectionJson.Deserialize(File.ReadAllText(
            Path.Combine(fixtureRoot, "UnrealBindingMatrix.reflection.json")));
        UnrealApiContract contract = UnrealApiContractJson.Deserialize(File.ReadAllText(
            Path.Combine(fixtureRoot, "UnrealBindingMatrix.api-contract.json"))) with
        {
            CoverageBaseline = new UnrealBindingCoverageBaseline(19, 4, 13, 2, 2, 0),
        };
        UnrealApiContractProjection projection = UnrealApiContractProjector.Project(reflection, contract);
        UnrealBindingGenerationResult managed = new UnrealBindingGenerator().Generate(projection.Reflection);
        UnrealNativeThunkGenerationResult native = new UnrealNativeThunkGenerator().Generate(
            projection.NativeReflection);

        UnrealBindingCoverageGate gate = UnrealBindingCoverageReport.Create(
            projection,
            managed,
            native).Gate;

        Assert.False(gate.Passed);
        Assert.Equal(
            ["USUE5101", "USUE5102", "USUE5103", "USUE5104", "USUE5105", "USUE5106"],
            gate.Diagnostics.Select(static diagnostic => diagnostic[..8]));
    }

    [Fact]
    public void CoverageSeparatesNativeFallbackAndRejectedMembers()
    {
        const string owner = "/Script/FixtureRuntime.Fixture";
        UnrealReflectedProperty Property(
            string name,
            UnrealTypeReference type,
            UnrealNativeAccess access = UnrealNativeAccess.Public) => new(
                $"{owner}:{name}",
                name,
                type,
                new UnrealPropertyAccess(true, true, true, access));
        var manifest = UnrealReflectionManifest.Create(
            new UnrealEngineIdentity(5, 8, 1, "fixture"),
            ["FixtureRuntime"],
            [new UnrealReflectedType(
                owner,
                "Fixture",
                "UFixture",
                "FixtureRuntime",
                UnrealReflectedTypeKind.Class,
                "/Script/CoreUObject.Object",
                new UnrealTypeTraits(false, true),
                [
                    Property("NativeValue", new UnrealTypeReference(UnrealValueKind.Int32, "int32")),
                    Property(
                        "FallbackValue",
                        new UnrealTypeReference(UnrealValueKind.String, "FString"),
                        UnrealNativeAccess.Protected),
                    Property(
                        "RejectedValue",
                        new UnrealTypeReference(
                            UnrealValueKind.Struct,
                            "FUnsupported",
                            "/Script/FixtureRuntime.Unsupported")),
                ],
                [
                    new UnrealReflectedFunction(
                        $"{owner}:StaticCall",
                        "StaticCall",
                        new UnrealTypeReference(UnrealValueKind.Void, "void"),
                        [],
                        new UnrealFunctionTraits(true, false, true)),
                    new UnrealReflectedFunction(
                        $"{owner}:OutputCall",
                        "OutputCall",
                        new UnrealTypeReference(UnrealValueKind.Boolean, "bool"),
                        [new UnrealReflectedParameter(
                            "Result",
                            new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                            UnrealParameterDirection.Output)],
                        new UnrealFunctionTraits(
                            false,
                            false,
                            true,
                            UnrealNativeAccess.Protected)),
                ],
                "Fixture.h")]);

        UnrealBindingGenerationResult managed = new UnrealBindingGenerator().Generate(manifest);
        UnrealNativeThunkGenerationResult native = new UnrealNativeThunkGenerator().Generate(manifest);
        UnrealBindingCoverageReport report = UnrealBindingCoverageReport.Create(manifest, managed, native);

        Assert.Equal(5, report.Summary.MemberCount);
        Assert.Equal(2, report.Summary.NativeThunkCount);
        Assert.Equal(2, report.Summary.ReflectionFallbackCount);
        Assert.Equal(1, report.Summary.RejectedCount);
        Assert.Equal(
            UnrealBindingDisposition.ReflectionFallback,
            Assert.Single(report.Entries, static item => item.Path.EndsWith(":FallbackValue")).Disposition);
        Assert.Equal(
            UnrealBindingDisposition.Rejected,
            Assert.Single(report.Entries, static item => item.Path.EndsWith(":RejectedValue")).Disposition);
        UnrealBindingCoverageEntry outputEntry = Assert.Single(
            report.Entries,
            static item => item.Path.EndsWith(":OutputCall"));
        Assert.Equal(UnrealBindingDisposition.ReflectionFallback, outputEntry.Disposition);
        Assert.Contains("USUE4104", outputEntry.DiagnosticCodes);

        string first = UnrealBindingCoverageJson.Serialize(report);
        string second = UnrealBindingCoverageJson.Serialize(report);
        Assert.Equal(first, second);
        Assert.Contains(UnrealBindingCoverageReport.CurrentSchemaVersion, first, StringComparison.Ordinal);
    }
}
