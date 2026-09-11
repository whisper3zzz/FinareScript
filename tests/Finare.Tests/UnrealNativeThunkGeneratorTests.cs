using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
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
            "bool UE::Finare::Generated::InvokeInt32x2(",
            first.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "OutReturnValue = TypedTarget->Add(Argument0, Argument1);",
            first.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "bool UE::Finare::Generated::InvokeFixed(",
            first.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "FFinareNativeValueV3* OutReturnValue",
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
                "UE::Finare::Generated::BlockBreaker"));

        Assert.Contains("#include \"BlockBreakerGeneratedThunks.h\"", result.Source, StringComparison.Ordinal);
        Assert.Contains(
            "namespace UE::Finare::Generated::BlockBreaker::Private",
            result.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "bool UE::Finare::Generated::BlockBreaker::Invoke(",
            result.Source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "bool UE::Finare::Generated::Invoke(",
            result.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateCoversFixedValueFamiliesAcrossZeroToFourArguments()
    {
        UnrealReflectionManifest manifest = CreateManifest();
        UnrealReflectedType owner = manifest.Types[0];
        var int32Type = new UnrealTypeReference(UnrealValueKind.Int32, "int32");
        var floatType = new UnrealTypeReference(UnrealValueKind.Float32, "float");
        var boolType = new UnrealTypeReference(UnrealValueKind.Boolean, "bool");
        var objectType = new UnrealTypeReference(
            UnrealValueKind.Object,
            "UObject*",
            "/Script/CoreUObject.Object");
        var vectorType = new UnrealTypeReference(
            UnrealValueKind.Struct,
            "FVector",
            "/Script/CoreUObject.Vector");
        UnrealReflectedParameter Input(string name, UnrealTypeReference type) =>
            new(name, type, UnrealParameterDirection.Input);
        UnrealReflectedFunction Function(
            string name,
            UnrealTypeReference result,
            params UnrealReflectedParameter[] parameters) =>
            new(
                $"{owner.Path}:{name}",
                name,
                result,
                parameters,
                new UnrealFunctionTraits(false, true, true));

        manifest = manifest with
        {
            Types =
            [
                owner with
                {
                    Functions =
                    [
                        Function("Fixed0", int32Type),
                        Function("Fixed1", floatType, Input("Value", floatType)),
                        Function(
                            "Fixed2",
                            boolType,
                            Input("Enabled", boolType),
                            Input("Count", int32Type)),
                        Function(
                            "Fixed3",
                            objectType,
                            Input("Context", objectType),
                            Input("Enabled", boolType),
                            Input("Weight", floatType)),
                        Function(
                            "Fixed4",
                            vectorType,
                            Input("Location", vectorType),
                            Input("Count", int32Type),
                            Input("Weight", floatType),
                            Input("Enabled", boolType)),
                    ],
                },
            ],
        };

        string source = new UnrealNativeThunkGenerator().Generate(manifest).Source;

        foreach (string invocation in new[]
                 {
                     "TypedTarget->Fixed0()",
                     "TypedTarget->Fixed1(Argument0)",
                     "TypedTarget->Fixed2(Argument0, Argument1)",
                     "TypedTarget->Fixed3(Argument0, Argument1, Argument2)",
                     "TypedTarget->Fixed4(Argument0, Argument1, Argument2, Argument3)",
                 })
        {
            Assert.Contains(invocation, source, StringComparison.Ordinal);
        }
        Assert.Contains("EFinareNativeValueKindV3::Int32", source, StringComparison.Ordinal);
        Assert.Contains("EFinareNativeValueKindV3::Float32", source, StringComparison.Ordinal);
        Assert.Contains("EFinareNativeValueKindV3::Boolean", source, StringComparison.Ordinal);
        Assert.Contains("EFinareNativeValueKindV3::Object", source, StringComparison.Ordinal);
        Assert.Contains("EFinareNativeValueKindV3::Vector3", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateDispatchesUInterfaceFunctionsThroughExecuteWrappers()
    {
        const string typePath = "/Script/FixtureRuntime.FixtureContract";
        var int32Type = new UnrealTypeReference(UnrealValueKind.Int32, "int32");
        UnrealReflectionManifest manifest = UnrealReflectionManifest.Create(
            new UnrealEngineIdentity(5, 8, 1, "UE5"),
            ["FixtureRuntime"],
            [new UnrealReflectedType(
                typePath,
                "FixtureContract",
                "UFixtureContract",
                "FixtureRuntime",
                UnrealReflectedTypeKind.Class,
                "/Script/CoreUObject.Interface",
                new UnrealTypeTraits(true, true) { Interface = true },
                [],
                [new UnrealReflectedFunction(
                    $"{typePath}:Transform",
                    "Transform",
                    int32Type,
                    [new UnrealReflectedParameter(
                        "Value",
                        int32Type,
                        UnrealParameterDirection.Input)],
                    new UnrealFunctionTraits(false, false, true))],
                "FixtureContract.h")]);

        string source = new UnrealNativeThunkGenerator().Generate(manifest).Source;

        Assert.Contains(
            "Target->GetClass()->ImplementsInterface(UFixtureContract::StaticClass())",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "IFixtureContract::Execute_Transform(Target, Argument0)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ResolveTarget<UFixtureContract>",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TypedTarget->Transform",
            source,
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
        Assert.Contains("FFinareValue::FromUInt64(TypedTarget->PreciseCount)", result.Source, StringComparison.Ordinal);
        Assert.Contains("FFinareValue::FromFloat64(TypedTarget->Weight)", result.Source, StringComparison.Ordinal);
        Assert.Contains("FFinareValue::FromName(TypedTarget->RowName)", result.Source, StringComparison.Ordinal);
        Assert.Contains("FFinareValue::FromText(TypedTarget->Label)", result.Source, StringComparison.Ordinal);
        Assert.Contains("FFinareValue::FromVector(TypedTarget->Location)", result.Source, StringComparison.Ordinal);
        Assert.Contains("FFinareValue::FromRotator(TypedTarget->Rotation)", result.Source, StringComparison.Ordinal);
        Assert.Contains("FFinareValue::FromTransform(TypedTarget->Transform)", result.Source, StringComparison.Ordinal);
        Assert.Contains(".TryGetEnumById(NativeValueRaw, 0x", result.Source, StringComparison.Ordinal);
        Assert.Contains("EFixtureMode NativeValue = static_cast<EFixtureMode>(NativeValueRaw);", result.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckedInNativeFixtureMatchesGeneratorOutput()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "UnrealFixtures");
        UnrealReflectionManifest manifest = UnrealReflectionJson.Deserialize(File.ReadAllText(
            Path.Combine(fixtureDirectory, "FinareRuntime.reflection.json")));
        UnrealApiContract contract = UnrealApiContractJson.Deserialize(File.ReadAllText(
            Path.Combine(fixtureDirectory, "FinareRuntime.api-contract.json")));
        UnrealApiContractProjection projection = UnrealApiContractProjector.Project(
            manifest,
            contract);
        string expected = File.ReadAllText(
            Path.Combine(fixtureDirectory, "FinareRuntime.native-thunks.generated.cpp"));

        UnrealNativeThunkGenerationResult result = new UnrealNativeThunkGenerator().Generate(
            projection.Reflection,
            new UnrealNativeThunkGeneratorOptions(
                projection.Contract.NativeHeader,
                projection.Contract.NativeNamespace));

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
