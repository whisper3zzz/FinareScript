using System.Text.Json;
using Finare.CSharp;
using Finare.Unreal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed class UnrealInteropParityGenerationTests
{
    private const string BasePath = "/Native/Fixture.Base";
    private const string DerivedPath = "/Native/Fixture.Derived";
    private static readonly UnrealTypeReference Int = new(UnrealValueKind.Int32, "int32");
    private static UnrealReflectionManifest Manifest()
    {
        var parent = new UnrealReflectedType(BasePath, "Base", "FBase", "Fixture", UnrealReflectedTypeKind.NativeObject,
            null, new(false, false), [], [], "Fixture.h");
        var child = parent with { Path = DerivedPath, Name = "Derived", NativeName = "FDerived", BaseTypePath = BasePath };
        return UnrealReflectionManifest.Create(new(5, 8, 2, "fixture"), ["Fixture"], [parent, child]);
    }
    private static void Compile(string source)
    {
        var compilation = new CSharpCompilationHost().Create(CSharpSyntaxTree.ParseText(source),
            [MetadataReference.CreateFromFile(typeof(UnrealObject).Assembly.Location)]);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void InheritanceOnlyDeclarationsStillRegisterCastsAndCompile()
    {
        var manifest = Manifest();
        var managed = new UnrealBindingGenerator().Generate(manifest);
        var native = new UnrealNativeThunkGenerator().Generate(manifest);
        Compile(managed.Source);
        Assert.Empty(native.Diagnostics);
        Assert.Contains("class Derived : Base", managed.Source);
        Assert.Contains("static_cast<FBase*>(static_cast<FDerived*>(Pointer))", native.Source);
        Assert.Contains("Library(TConstArrayView<FFinareNativeObjectMemberEntry>(), MakeArrayView(Casts))", native.Source);
    }

    [Theory]
    [InlineData("/Native/Fixture.Missing")]
    [InlineData(DerivedPath)]
    public void InvalidNativeAncestorFailsBeforeCppGeneration(string parent)
    {
        var manifest = Manifest();
        manifest = manifest with { Types = [manifest.Types[0], manifest.Types[1] with { BaseTypePath = parent }] };
        Assert.Throws<JsonException>(() => new UnrealNativeThunkGenerator().Generate(manifest));
    }

    [Fact]
    public void StdFunctionAndVectorGenerateTypedLeasesAndBoundedMemory()
    {
        var manifest = Manifest();
        var callback = new UnrealTypeReference(UnrealValueKind.Callback, "std::function<int32(int32)>", DerivedPath + ":Transform")
        { CallbackSignature = new(Int, [new("Value", Int, UnrealParameterDirection.Input)]) };
        var child = manifest.Types[1] with
        {
            Properties = [new(DerivedPath + ":Samples", "Samples", new(UnrealValueKind.Array, "std::vector<int32>", ElementType: Int), new(true, true, false)) { NativeExtension = "Fixture" }],
            Functions = [new(DerivedPath + ":Save(std::function<int32(int32)>)", "Save", new(UnrealValueKind.Void, "void"),
                [new("Callback", callback, UnrealParameterDirection.Input)], new(false, false, true))
                { NativeBinding = new("Fixture", false, [UnrealNativeParameterPassing.Value]) }],
        };
        manifest = manifest with { Types = [manifest.Types[0], child] };
        var managed = new UnrealBindingGenerator().Generate(manifest);
        var native = new UnrealNativeThunkGenerator().Generate(manifest);
        Assert.Empty(managed.Diagnostics); Assert.Empty(native.Diagnostics); Compile(managed.Source);
        Assert.Contains("UnrealNativeBuffer<int>", managed.Source);
        Assert.Contains("UnrealCallback<TransformHandler>", managed.Source);
        Assert.Contains("Lease.EncodeCallback", managed.Source);
        Assert.Contains("Values::MakeStdFunction<int32, int32>", native.Source);
        Assert.Contains("Data = const_cast<int32*>(Buffer.data())", native.Source);
        Assert.Contains("Size != sizeof(int32)", native.Source);
        Assert.Contains("64u * 1024u * 1024u", native.Source);
    }

    [Fact]
    public void NativeExtensionJsonPreservesBaseTypeThroughProjection()
    {
        var baseField = new UnrealReflectedProperty(BasePath + ":Value", "Value", Int, new(true, true, false));
        var childField = baseField with { Path = DerivedPath + ":Other", Name = "Other" };
        var extensions = new UnrealNativeExtensionManifest(UnrealNativeExtensionManifest.CurrentSchemaVersion, "Fixture",
            [new(BasePath, "FBase", "Fixture.h", [baseField], []) { Kind = UnrealReflectedTypeKind.NativeObject },
             new(DerivedPath, "FDerived", "Fixture.h", [childField], []) { Kind = UnrealReflectedTypeKind.NativeObject, BaseTypePath = BasePath }]);
        var serialized = JsonSerializer.Serialize(extensions);
        var roundtrip = JsonSerializer.Deserialize<UnrealNativeExtensionManifest>(serialized)!;
        var empty = Manifest() with { Types = [] };
        var projected = UnrealNativeExtensions.Apply(empty, roundtrip);
        Assert.Equal(BasePath, projected.Types.Single(t => t.Path == DerivedPath).BaseTypePath);
        Compile(new UnrealBindingGenerator().Generate(projected).Source);
    }
}
