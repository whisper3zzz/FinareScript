using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealBindingCoverageTests
{
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
        Assert.Equal(1, report.Summary.NativeThunkCount);
        Assert.Equal(1, report.Summary.ReflectionFallbackCount);
        Assert.Equal(3, report.Summary.RejectedCount);
        Assert.Equal(
            UnrealBindingDisposition.ReflectionFallback,
            Assert.Single(report.Entries, static item => item.Path.EndsWith(":FallbackValue")).Disposition);
        Assert.Equal(
            UnrealBindingDisposition.Rejected,
            Assert.Single(report.Entries, static item => item.Path.EndsWith(":RejectedValue")).Disposition);
        UnrealBindingCoverageEntry outputEntry = Assert.Single(
            report.Entries,
            static item => item.Path.EndsWith(":OutputCall"));
        Assert.Equal(UnrealBindingDisposition.Rejected, outputEntry.Disposition);
        Assert.Contains("USUE4001", outputEntry.DiagnosticCodes);
        Assert.Contains("output or reference", outputEntry.Message, StringComparison.OrdinalIgnoreCase);

        string first = UnrealBindingCoverageJson.Serialize(report);
        string second = UnrealBindingCoverageJson.Serialize(report);
        Assert.Equal(first, second);
        Assert.Contains(UnrealBindingCoverageReport.CurrentSchemaVersion, first, StringComparison.Ordinal);
    }
}
