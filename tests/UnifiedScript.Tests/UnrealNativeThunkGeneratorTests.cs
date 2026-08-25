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
        Assert.Contains(
            "bool UE::UnifiedScript::Generated::InvokeInt32x2(",
            first.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "OutReturnValue = TypedTarget->Add(Argument0, Argument1);",
            first.Source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessEvent", first.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("FindFProperty", first.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateSupportsProjectOwnedHeaderAndNamespace()
    {
        UnrealNativeThunkGenerationResult result = new UnrealNativeThunkGenerator().Generate(
            CreateManifest(),
            new UnrealNativeThunkGeneratorOptions(
                "BlockBreakerGeneratedThunks.h",
                "UE::UnifiedScript::Generated::BlockBreaker"));

        Assert.Contains("#include \"BlockBreakerGeneratedThunks.h\"", result.Source, StringComparison.Ordinal);
        Assert.Contains(
            "namespace UE::UnifiedScript::Generated::BlockBreaker::Private",
            result.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "bool UE::UnifiedScript::Generated::BlockBreaker::Invoke(",
            result.Source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "bool UE::UnifiedScript::Generated::Invoke(",
            result.Source,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UE::Generated::")]
    [InlineData("UE::Generated;Injected")]
    [InlineData("9Generated")]
    public void GenerateRejectsUnsafeCppNamespace(string cppNamespace)
    {
        Assert.Throws<ArgumentException>(() => new UnrealNativeThunkGenerator().Generate(
            CreateManifest(),
            new UnrealNativeThunkGeneratorOptions(CppNamespace: cppNamespace)));
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
                                "/Script/FixtureRuntime.UnsupportedStruct"),
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
    public void GenerateEmitsScalarEnumAndCoreStructConversions()
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
                        .. type.Properties,
                        Property(type, "PreciseCount", UnrealValueKind.UInt64, "uint64"),
                        Property(type, "Weight", UnrealValueKind.Float64, "double"),
                        Property(type, "RowName", UnrealValueKind.Name, "FName"),
                        Property(type, "Label", UnrealValueKind.Text, "FText"),
                        Property(type, "Location", UnrealValueKind.Struct, "FVector", "/Script/CoreUObject.Vector"),
                        Property(type, "Rotation", UnrealValueKind.Struct, "FRotator", "/Script/CoreUObject.Rotator"),
                        Property(type, "Transform", UnrealValueKind.Struct, "FTransform", "/Script/CoreUObject.Transform"),
                        Property(type, "Mode", UnrealValueKind.Enum, "EFixtureMode", "/Script/FixtureRuntime.EFixtureMode"),
                    ],
                },
            ],
        };

        UnrealNativeThunkGenerationResult result = new UnrealNativeThunkGenerator().Generate(manifest);

        Assert.Empty(result.Diagnostics);
        Assert.Contains("FUnifiedScriptValue::FromUInt64(TypedTarget->PreciseCount)", result.Source, StringComparison.Ordinal);
        Assert.Contains("FUnifiedScriptValue::FromFloat64(TypedTarget->Weight)", result.Source, StringComparison.Ordinal);
        Assert.Contains("FUnifiedScriptValue::FromName(TypedTarget->RowName)", result.Source, StringComparison.Ordinal);
        Assert.Contains("FUnifiedScriptValue::FromText(TypedTarget->Label)", result.Source, StringComparison.Ordinal);
        Assert.Contains("FUnifiedScriptValue::FromVector(TypedTarget->Location)", result.Source, StringComparison.Ordinal);
        Assert.Contains("FUnifiedScriptValue::FromRotator(TypedTarget->Rotation)", result.Source, StringComparison.Ordinal);
        Assert.Contains("FUnifiedScriptValue::FromTransform(TypedTarget->Transform)", result.Source, StringComparison.Ordinal);
        Assert.Contains(".TryGetEnumById(NativeValueRaw, 0x", result.Source, StringComparison.Ordinal);
        Assert.Contains("EFixtureMode NativeValue = static_cast<EFixtureMode>(NativeValueRaw);", result.Source, StringComparison.Ordinal);
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
        Assert.Contains("DecodeObject(Registry, Arguments[0]", result.Source, StringComparison.Ordinal);
        Assert.Contains("TypedTarget->EchoObject(Argument0)", result.Source, StringComparison.Ordinal);
        Assert.Contains("TypedTarget->EchoClass(Argument0)", result.Source, StringComparison.Ordinal);
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

    private static UnrealReflectedProperty Property(
        UnrealReflectedType owner,
        string name,
        UnrealValueKind kind,
        string nativeName,
        string? typePath = null) =>
        new(
            $"{owner.Path}:{name}",
            name,
            new UnrealTypeReference(kind, nativeName, typePath),
            new UnrealPropertyAccess(true, true, true));
}
