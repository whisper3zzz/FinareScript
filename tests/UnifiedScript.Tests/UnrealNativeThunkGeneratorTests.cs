using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealNativeThunkGeneratorTests
{
    [Fact]
    public void GenerateProducesDeterministicDirectNativeDispatch()
    {
        UnrealReflectionManifest manifest = CreateManifest();
        var generator = new UnrealNativeThunkGenerator();

        UnrealNativeThunkGenerationResult first = generator.Generate(manifest);
        UnrealNativeThunkGenerationResult second = generator.Generate(manifest);

        Assert.Equal(first.Source, second.Source);
        Assert.Empty(first.Diagnostics);
        Assert.Contains("#include \"FixtureObject.h\"", first.Source, StringComparison.Ordinal);
        Assert.Contains(
            $"case 0x{UnrealMemberId.FromPath("/Script/FixtureRuntime.FixtureObject:Counter"):X16}ull:",
            first.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "TypedTarget->Counter = NativeValue;",
            first.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "TypedTarget->Add(Argument0, Argument1)",
            first.Source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessEvent", first.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("FindFProperty", first.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateRejectsNonPublicAndUnsupportedMembersWithStableDiagnostics()
    {
        UnrealReflectionManifest manifest = CreateManifest();
        UnrealReflectedType type = manifest.Types[0];
        manifest = manifest with
        {
            Types =
            [
                type with
                {
                    Properties =
                    [
                        type.Properties[0] with
                        {
                            Access = type.Properties[0].Access with
                            {
                                NativeAccess = UnrealNativeAccess.Private,
                            },
                        },
                        new UnrealReflectedProperty(
                            $"{type.Path}:Transform",
                            "Transform",
                            new UnrealTypeReference(
                                UnrealValueKind.Struct,
                                "FTransform",
                                "/Script/CoreUObject.Transform"),
                            new UnrealPropertyAccess(true, true, true)),
                    ],
                },
            ],
        };

        UnrealNativeThunkGenerationResult result = new UnrealNativeThunkGenerator().Generate(manifest);

        Assert.Equal(
            ["USUE3002", "USUE3003"],
            result.Diagnostics.Select(static diagnostic => diagnostic.Code));
        Assert.DoesNotContain("TypedTarget->Counter", result.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("TypedTarget->Transform", result.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckedInNativeFixtureMatchesGeneratorOutput()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "UnrealFixtures");
        UnrealReflectionManifest manifest = UnrealReflectionJson.Deserialize(File.ReadAllText(
            Path.Combine(fixtureDirectory, "UEUnifiedScriptRuntime.reflection.json")));
        string expected = File.ReadAllText(
            Path.Combine(fixtureDirectory, "UEUnifiedScriptRuntime.native-thunks.generated.cpp"));

        UnrealNativeThunkGenerationResult result = new UnrealNativeThunkGenerator().Generate(manifest);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(expected.ReplaceLineEndings("\n"), result.Source);
        Assert.Contains("TypedTarget->ManagedState", result.Source, StringComparison.Ordinal);
        Assert.Contains("TypedTarget->Add(Argument0, Argument1)", result.Source, StringComparison.Ordinal);
    }

    private static UnrealReflectionManifest CreateManifest()
    {
        const string typePath = "/Script/FixtureRuntime.FixtureObject";
        return UnrealReflectionManifest.Create(
            new UnrealEngineIdentity(5, 8, 1, "UE5"),
            ["FixtureRuntime"],
            [new UnrealReflectedType(
                typePath,
                "FixtureObject",
                "UFixtureObject",
                "FixtureRuntime",
                UnrealReflectedTypeKind.Class,
                "/Script/CoreUObject.Object",
                new UnrealTypeTraits(false, true),
                [new UnrealReflectedProperty(
                    $"{typePath}:Counter",
                    "Counter",
                    new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                    new UnrealPropertyAccess(true, true, true))],
                [new UnrealReflectedFunction(
                    $"{typePath}:Add",
                    "Add",
                    new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                    [
                        new UnrealReflectedParameter(
                            "Left",
                            new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                            UnrealParameterDirection.Input),
                        new UnrealReflectedParameter(
                            "Right",
                            new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                            UnrealParameterDirection.Input),
                    ],
                    new UnrealFunctionTraits(false, true, true))],
                "FixtureObject.h")]);
    }
}
