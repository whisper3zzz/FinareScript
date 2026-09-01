using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealWorldServiceGenerationTests
{
    [Fact]
    public void GeneratesStaticServiceComplexStructAndVisualMakeBreakNodes()
    {
        UnrealReflectionManifest manifest = CreateManifest();

        UnrealBindingGenerationResult managed = new UnrealBindingGenerator().Generate(manifest);
        UnrealNativeThunkGenerationResult native = new UnrealNativeThunkGenerator().Generate(manifest);

        Assert.Empty(managed.Diagnostics);
        Assert.Empty(native.Diagnostics);
        Assert.Contains("readonly record struct HitResult(", managed.Source, StringComparison.Ordinal);
        Assert.Contains(
            "UnrealStructFieldAttribute(0, \"",
            managed.Source,
            StringComparison.Ordinal);
        Assert.Contains("UnrealStaticService", managed.Source, StringComparison.Ordinal);
        Assert.Contains("out HitResult outHit", managed.Source, StringComparison.Ordinal);
        Assert.Contains("UFixtureWorldLibrary::LineTraceSingle(", native.Source, StringComparison.Ordinal);
        Assert.Contains("FUnifiedScriptValue::FromStruct", native.Source, StringComparison.Ordinal);
        Assert.Contains("TryGetStruct", native.Source, StringComparison.Ordinal);
        Assert.Contains("#include \"Particles/ParticleSystem.h\"", native.Source, StringComparison.Ordinal);

        UnrealApiContractProjection projection = UnrealApiContractProjector.Project(
            manifest,
            new UnrealApiContract(
                UnrealApiContract.CurrentSchemaVersion,
                "Fixture.World",
                "Fixture.Generated",
                "FixtureGeneratedThunks.h",
                "UE::Fixture::Generated",
                [
                    new("/Script/FixtureRuntime.HitResult", ["bBlockingHit", "Location"]),
                    new("/Script/FixtureRuntime.FixtureWorldLibrary", ["LineTraceSingle"]),
                ]));
        UnrealApiNodeCatalog catalog = UnrealApiNodeCatalogGenerator.Generate(projection);
        Assert.Contains(
            catalog.Types.SelectMany(static type => type.Nodes),
            static node => node.Kind == "structMake");
        Assert.Contains(
            catalog.Types.SelectMany(static type => type.Nodes),
            static node => node.Kind == "structBreak");
        UnrealApiNodeDescriptor call = Assert.Single(
            catalog.Types.SelectMany(static type => type.Nodes),
            static node => node.Kind == "function");
        Assert.Contains(call.Pins, static pin => pin.Name == "Context");
        Assert.DoesNotContain(call.Pins, static pin => pin.Name == "Target");
    }

    private static UnrealReflectionManifest CreateManifest()
    {
        const string structPath = "/Script/FixtureRuntime.HitResult";
        const string servicePath = "/Script/FixtureRuntime.FixtureWorldLibrary";
        var vector = new UnrealTypeReference(
            UnrealValueKind.Struct,
            "FVector",
            "/Script/CoreUObject.Vector");
        var hitResult = new UnrealTypeReference(UnrealValueKind.Struct, "FHitResult", structPath);
        var particleSystem = new UnrealTypeReference(
            UnrealValueKind.Object,
            "UParticleSystem*",
            "/Script/Engine.ParticleSystem",
            HeaderPath: "Particles/ParticleSystem.h");
        return UnrealReflectionManifest.Create(
            new UnrealEngineIdentity(5, 8, 1, "fixture"),
            ["FixtureRuntime"],
            [
                new UnrealReflectedType(
                    structPath,
                    "HitResult",
                    "FHitResult",
                    "FixtureRuntime",
                    UnrealReflectedTypeKind.Struct,
                    null,
                    new UnrealTypeTraits(false, true),
                    [
                        new(
                            structPath + ":bBlockingHit",
                            "bBlockingHit",
                            new UnrealTypeReference(UnrealValueKind.Boolean, "bool"),
                            new UnrealPropertyAccess(true, false, true)),
                        new(
                            structPath + ":Location",
                            "Location",
                            vector,
                            new UnrealPropertyAccess(true, false, true)),
                    ],
                    [],
                    "Engine/HitResult.h"),
                new UnrealReflectedType(
                    servicePath,
                    "FixtureWorldLibrary",
                    "UFixtureWorldLibrary",
                    "FixtureRuntime",
                    UnrealReflectedTypeKind.Class,
                    "/Script/Engine.BlueprintFunctionLibrary",
                    new UnrealTypeTraits(false, false),
                    [],
                    [
                        new UnrealReflectedFunction(
                            servicePath + ":LineTraceSingle",
                            "LineTraceSingle",
                            new UnrealTypeReference(UnrealValueKind.Boolean, "bool"),
                            [
                                new(
                                    "Start",
                                    vector,
                                    UnrealParameterDirection.Input),
                                new(
                                    "EmitterTemplate",
                                    particleSystem,
                                    UnrealParameterDirection.Input),
                                new(
                                    "OutHit",
                                    hitResult,
                                    UnrealParameterDirection.Output),
                            ],
                            new UnrealFunctionTraits(true, false, true)),
                    ],
                    "FixtureWorldLibrary.h"),
            ]);
    }
}
