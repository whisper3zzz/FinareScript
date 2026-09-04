using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnifiedScript.CSharp;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealNativeExtensionTests
{
    private const string Path = "/Script/Fixture.NativeObject";
    private static readonly UnrealTypeReference Int = new(UnrealValueKind.Int32, "int32");

    [Fact]
    public void OneContractGeneratesNativeManagedAndVisualSurfacesWithoutReflectionMetadata()
    {
        UnrealApiContract contract = Contract();
        string json = UnrealApiContractJson.Serialize(contract);
        Assert.Equal(json, UnrealApiContractJson.Serialize(UnrealApiContractJson.Deserialize(json)));
        UnrealApiContractProjection projection = UnrealApiContractProjector.Project(Reflection(), contract);
        var managed = new UnrealBindingGenerator().Generate(projection.Reflection);
        var native = new UnrealNativeThunkGenerator().Generate(projection.NativeReflection);
        Assert.Empty(managed.Diagnostics);
        Assert.Empty(native.Diagnostics);
        Assert.Contains("RequireNativeBindings(", managed.Source);
        Assert.Contains("static_cast<int32 (UNativeObject::*)(int32) const>(&UNativeObject::Scale)", native.Source);
        Assert.Contains("static_cast<double (UNativeObject::*)(double) const>(&UNativeObject::Scale)", native.Source);
        Assert.Contains("TypedTarget->Counter = NativeValue", native.Source);
        Assert.Contains("static_assert(std::is_same_v<std::remove_cv_t<decltype(TypedTarget->Counter)>, int32>", native.Source);
        var compilation = new CSharpCompilationHost().Create(
            CSharpSyntaxTree.ParseText(managed.Source),
            [MetadataReference.CreateFromFile(typeof(UnrealObject).Assembly.Location)]);
        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var nodes = UnrealApiNodeCatalogGenerator.Generate(projection);
        Assert.All(Assert.Single(nodes.Types).Nodes, static node => Assert.Equal("Fixture.Native", node.NativeExtension));
        var coverage = UnrealBindingCoverageReport.Create(projection, managed, native);
        Assert.Equal(3, coverage.Summary.MemberCount);
        Assert.Equal(0, coverage.Summary.ReflectionFallbackCount);
        Assert.Equal(0, coverage.Summary.RejectedCount);
        Assert.All(coverage.Entries, static entry => Assert.Equal("Fixture.Native", entry.NativeExtension));
    }

    [Theory]
    [InlineData("unknown-owner")]
    [InlineData("wrong-header")]
    [InlineData("collision")]
    [InlineData("private")]
    [InlineData("passing")]
    [InlineData("identity")]
    [InlineData("fallback")]
    [InlineData("unsupported")]
    public void InvalidNativeBindingsFailBeforePublishingAnySurface(string mutation)
    {
        UnrealApiContract contract = Contract();
        var extension = contract.NativeExtensions!.Types[0];
        var function = extension.Functions[0];
        var reflection = Reflection();
        switch (mutation)
        {
            case "unknown-owner": reflection = reflection with { Types = [] }; break;
            case "wrong-header": extension = extension with { HeaderPath = "Other.h" }; break;
            case "collision":
                reflection = reflection with { Types = [reflection.Types[0] with { Properties = [extension.Properties[0]] }] }; break;
            case "private":
                function = function with { Declaration = function.Declaration with { Traits = function.Declaration.Traits with { NativeAccess = UnrealNativeAccess.Private } } }; break;
            case "passing": function = function with { Parameters = [UnrealNativeParameterPassing.Reference] }; break;
            case "identity": function = function with { Declaration = function.Declaration with { Path = Path + ":Scale" } }; break;
            case "fallback":
                contract = contract with { Policy = new UnrealApiPolicy(UnrealApiPolicyAction.Allow,
                    [new UnrealApiPolicyRule(Path + ":Counter", UnrealApiPolicyAction.Allow, UnrealBindingRoute.ReflectionFallback)]) }; break;
            case "unsupported":
                function = function with { Declaration = function.Declaration with { ReturnType = new(UnrealValueKind.Unsupported, "FUnsupported") } }; break;
        }
        extension = extension with { Functions = [function, extension.Functions[1]] };
        contract = contract with { NativeExtensions = contract.NativeExtensions with { Types = [extension] } };
        Assert.Throws<JsonException>(() => UnrealApiContractProjector.Project(reflection, contract));
    }

    [Fact]
    public void ExistingContractsDoNotAcquireNativeExtensionMetadata()
    {
        string json = UnrealApiContractJson.Serialize(Contract() with { NativeExtensions = null });
        Assert.DoesNotContain("nativeExtensions", json);
        Assert.Throws<JsonException>(() => UnrealApiContractJson.Deserialize(
            UnrealApiContractJson.Serialize(Contract()).Replace("\"const\":", "\"unknown\":")));
    }

    [Fact]
    public void VoidZeroArgumentNativeMethodUsesExactMemberPointer()
    {
        var contract = Contract();
        var type = contract.NativeExtensions!.Types[0];
        var reset = new UnrealNativeExtensionFunction(
            new(Path + ":Reset()", "Reset", new(UnrealValueKind.Void, "void"), [], new(false, false, true)), false, []);
        contract = contract with
        {
            Types = [new(Path, ["Reset"])],
            NativeExtensions = contract.NativeExtensions with { Types = [type with { Functions = [reset] }] },
        };
        var projection = UnrealApiContractProjector.Project(Reflection(), contract);
        var generator = new UnrealNativeThunkGenerator();
        var first = generator.Generate(projection.NativeReflection);
        Assert.Empty(first.Diagnostics);
        Assert.Contains("static_cast<void (UNativeObject::*)()>(&UNativeObject::Reset)", first.Source);
        Assert.Equal(first.Source, generator.Generate(projection.NativeReflection).Source);
    }

    [Fact]
    public void SourceDrivenContractProposalCanResolveNativeOnlyMembers()
    {
        var requests = new UnrealPuertsContractRequestSet("unifiedscript.puerts-contract-requests/v0.1",
            "NativeFixture", new string('a', 64), null,
            [new("counter", "NativeObject", Path, "Counter", "contract-member", "contract", ["obj.Counter"], [])]);
        var proposal = new UnrealApiContractProposalGenerator().Generate(Reflection(), requests, Contract());
        Assert.Equal("already-selected", Assert.Single(proposal.Decisions).Status);
        Assert.NotNull(proposal.ProposedContract.NativeExtensions);
    }

    [Fact]
    public void ConstReferenceAndValueOverloadsCannotSilentlyCollideInCSharp()
    {
        var contract = Contract();
        var type = contract.NativeExtensions!.Types[0];
        var method = type.Functions[0];
        var constReference = method with
        {
            Parameters = [UnrealNativeParameterPassing.ConstReference],
            Declaration = method.Declaration with { Path = Path + ":Scale(int32const&)" },
        };
        Assert.Throws<JsonException>(() => UnrealApiContractProjector.Project(Reflection(), contract with
        {
            NativeExtensions = contract.NativeExtensions with { Types = [type with { Functions = [method, constReference] }] },
        }));
    }

    private static UnrealReflectionManifest Reflection() => UnrealReflectionManifest.Create(
        new UnrealEngineIdentity(5, 8, 1, "test"), ["Fixture"],
        [new UnrealReflectedType(Path, "NativeObject", "UNativeObject", "Fixture", UnrealReflectedTypeKind.Class,
            null, new(false, true), [], [], "NativeObject.h")]);

    private static UnrealApiContract Contract() => new(
        UnrealApiContract.CurrentSchemaVersion, "NativeFixture", "Fixture.Generated", "NativeThunks.h", "Fixture::Generated",
        [new(Path, ["Counter", "Scale(int32)", "Scale(double)"])])
    {
        NativeExtensions = new(UnrealNativeExtensionManifest.CurrentSchemaVersion, "Fixture.Native",
        [new(Path, "UNativeObject", "NativeObject.h",
            [new(Path + ":Counter", "Counter", Int, new(true, true, false))],
            [Method(Int), Method(new(UnrealValueKind.Float64, "double"))])]),
    };

    private static UnrealNativeExtensionFunction Method(UnrealTypeReference valueType) => new(
        new(Path + $":Scale({valueType.NativeName})", "Scale", valueType,
            [new("Value", valueType, UnrealParameterDirection.Input)], new(false, true, true)),
        true, [UnrealNativeParameterPassing.Value]);
}
