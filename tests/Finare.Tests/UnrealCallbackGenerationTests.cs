using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Finare.CSharp;
using Finare.Graph;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed class UnrealCallbackGenerationTests
{
    private const string Owner = "/Script/Fixture.CallbackHost";
    private const string Signature = "/Script/Fixture.Evaluate__DelegateSignature";
    private static UnrealTypeReference Callback() => new(UnrealValueKind.Callback, "FEvaluate", Signature, HeaderPath: "CallbackHost.h")
    {
        CallbackSignature = new(new(UnrealValueKind.Boolean, "bool"),
        [new("Value", new(UnrealValueKind.Int32, "int32"), UnrealParameterDirection.Input),
         new("Text", new(UnrealValueKind.String, "FString"), UnrealParameterDirection.InputOutput),
         new("Position", new(UnrealValueKind.Struct, "FVector", "/Script/CoreUObject.Vector"), UnrealParameterDirection.Output)]),
    };
    private static UnrealReflectionManifest Manifest(UnrealTypeReference? callback = null) => UnrealReflectionManifest.Create(
        new(5, 8, 1, "test"), ["Fixture"],
        [new(Owner, "CallbackHost", "UCallbackHost", "Fixture", UnrealReflectedTypeKind.Class, null, new(false, true), [],
        [new(Owner + ":Evaluate", "Evaluate", new(UnrealValueKind.Boolean, "bool"),
            [new("Callback", callback ?? Callback(), UnrealParameterDirection.Input)], new(false, false, true))], "CallbackHost.h")]);

    [Fact]
    public void ClassScopedCallbackSignatureRetainsItsExactReflectedFieldPath()
    {
        var callback = Callback() with { TypePath = Owner + ":Evaluate__DelegateSignature" };
        var manifest = UnrealReflectionJson.Deserialize(UnrealReflectionJson.Serialize(Manifest(callback)));
        Assert.Equal(callback.TypePath, manifest.Types[0].Functions[0].Parameters[0].Type.TypePath);
        var generated = new UnrealBindingGenerator().Generate(manifest);
        var compilation = new CSharpCompilationHost().Create(CSharpSyntaxTree.ParseText(generated.Source),
            [MetadataReference.CreateFromFile(typeof(UnrealCallback<>).Assembly.Location)]);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains(callback.TypePath, generated.Source);
    }

    [Fact]
    public void CallbackSignaturesRoundTripAndGenerateTypedLeaseFactories()
    {
        var manifest = Manifest();
        string json = UnrealReflectionJson.Serialize(manifest);
        Assert.Equal(json, UnrealReflectionJson.Serialize(UnrealReflectionJson.Deserialize(json)));
        var generated = new UnrealBindingGenerator().Generate(manifest);
        Assert.Empty(generated.Diagnostics);
        Assert.Contains("public delegate bool EvaluateHandler(int value, ref string text, out global::Finare.Unreal.UnrealVector position)", generated.Source);
        Assert.Contains("UnrealCallback<EvaluateHandler>", generated.Source);
        Assert.Contains(".AsArgument(Bridge)", generated.Source);
        Assert.Contains("CreateEvaluateCallback(", generated.Source);
        var compilation = new CSharpCompilationHost().Create(CSharpSyntaxTree.ParseText(generated.Source),
            [MetadataReference.CreateFromFile(typeof(UnrealCallback<>).Assembly.Location)]);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal(generated.Source, new UnrealBindingGenerator().Generate(manifest).Source);
        var native = new UnrealNativeThunkGenerator().Generate(manifest);
        Assert.Empty(native.Diagnostics);
        Assert.Contains("Finare_ResolveCallback(Registry, Arguments[0]", native.Source);
        Assert.Contains("FEvaluate Argument0;", native.Source);
        Assert.Contains("Argument0.BindUFunction(", native.Source);
    }

    [Fact]
    public void CallbackOnlyDependenciesReachBindingsAndNodeSignatures()
    {
        var fixture = UnrealReflectionJson.Deserialize(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "UnrealBindingMatrix.reflection.json")));
        var value = new UnrealTypeReference(UnrealValueKind.Struct, "FOuterData",
            "/Script/BindingMatrix.OuterData", HeaderPath: "FixtureTypes.h");
        var callback = Callback() with
        {
            CallbackSignature = new(value,
                [new("Data", value, UnrealParameterDirection.InputOutput)]),
        };
        var manifest = Manifest(callback) with
        {
            Modules = ["Fixture", "BindingMatrix"],
            Types = [.. Manifest(callback).Types, .. fixture.Types],
        };
        var contract = new UnrealApiContract(UnrealApiContract.CurrentSchemaVersion,
            "Callback.Contract", "Generated", "CallbackHost.h", "Generated", [new(Owner, ["Evaluate"])]);
        var projection = UnrealApiContractProjector.Project(manifest, contract);
        Assert.Contains(projection.Reflection.Types, t => t.Path == value.TypePath);
        Assert.Contains(projection.Reflection.Types, t => t.Path == "/Script/BindingMatrix.InnerData");
        Assert.Contains(projection.Reflection.Types, t => t.Path == "/Script/BindingMatrix.FixtureMode");
        var generated = new UnrealBindingGenerator().Generate(projection.Reflection);
        Assert.Empty(generated.Diagnostics);
        var compilation = new CSharpCompilationHost().Create(CSharpSyntaxTree.ParseText(generated.Source),
            [MetadataReference.CreateFromFile(typeof(UnrealCallback<>).Assembly.Location)]);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var nodes = UnrealApiNodeCatalogGenerator.Generate(projection);
        var node = Assert.Single(nodes.Types.Single(t => t.Path == Owner).Nodes, n => n.Title == "Evaluate");
        var pin = Assert.Single(node.Pins, p => p.Name == "Callback");
        Assert.Equal("input", pin.Direction);
        Assert.Equal(UnrealValueKind.Callback, pin.Type!.Kind);
        Assert.Equal(value.TypePath, pin.Type.CallbackSignature!.ReturnType.TypePath);
        Assert.Equal(UnrealParameterDirection.InputOutput, pin.Type.CallbackSignature.Parameters[0].Direction);
        Assert.Equal($"0x{UnrealMemberId.FromPath(Owner + ":Evaluate"):X16}", node.MemberId);
        using var json = JsonDocument.Parse(UnrealApiNodeCatalogGenerator.Serialize(nodes));
        Assert.Contains("callbackSignature", json.RootElement.GetRawText());
    }

    [Fact]
    public void TypedCallbackLeaseCanCrossVisualCallBoundaryWithoutMakingDelegateBodiesEditable()
    {
        var bindings = new UnrealBindingGenerator().Generate(Manifest()).Source;
        const string gameplay = """
            using Finare;
            using Finare.Unreal;
            using Finare.Unreal.Generated;
            public sealed class CallbackGameplay
            {
                [Visual]
                public bool Evaluate(CallbackHost host, UnrealCallback<EvaluateHandler> callback)
                {
                    return host.Evaluate(callback);
                }
            }
            """;
        var result = new CSharpSemanticPipeline().Analyze(new CSharpCompilationRequest("CallbackVisual",
            [new("Bindings.cs", bindings), new("Gameplay.cs", gameplay)], [typeof(UnrealCallback<>).Assembly.Location]));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var graph = new HirGraphProjector().Project(result.Hir!, result.Semantics!);
        Assert.Single(graph.Functions);
        Assert.Contains(graph.Functions[0].Nodes, n => n.Kind == "invocation" && n.Properties["method"].Contains("CallbackHost.Evaluate"));
    }

    [Theory]
    [InlineData("missing-signature")]
    [InlineData("nested")]
    [InlineData("default")]
    [InlineData("duplicate-parameter")]
    [InlineData("wrong-kind")]
    [InlineData("array")]
    [InlineData("return")]
    [InlineData("output")]
    public void UnsupportedCallbackShapesFailBeforeGeneration(string change)
    {
        var callback = Callback();
        switch (change)
        {
            case "missing-signature": callback = callback with { CallbackSignature = null }; break;
            case "nested": callback = callback with { CallbackSignature = callback.CallbackSignature! with { ReturnType = Callback() } }; break;
            case "default": callback = callback with { CallbackSignature = callback.CallbackSignature! with { Parameters =
                [callback.CallbackSignature.Parameters[0] with { DefaultValue = "3" }] } }; break;
            case "duplicate-parameter": callback = callback with { CallbackSignature = callback.CallbackSignature! with { Parameters =
                [callback.CallbackSignature.Parameters[0], callback.CallbackSignature.Parameters[0]] } }; break;
            case "wrong-kind": callback = callback with { Kind = UnrealValueKind.Struct }; break;
            case "array": callback = new(UnrealValueKind.Array, "TArray<FEvaluate>", ElementType: callback); break;
        }
        var manifest = Manifest(callback);
        var type = manifest.Types[0];
        var function = type.Functions[0];
        if (change == "return") function = function with { ReturnType = callback };
        if (change == "output") function = function with { Parameters = [function.Parameters[0] with { Direction = UnrealParameterDirection.Output }] };
        manifest = manifest with { Types = [type with { Functions = [function] }] };
        Assert.Throws<JsonException>(() => UnrealReflectionJson.NormalizeAndValidate(manifest));
    }

    [Fact]
    public void ReusedCallbackIdentityCannotHideADifferentSignature()
    {
        var manifest = Manifest();
        var type = manifest.Types[0];
        var second = type.Functions[0] with
        {
            Name = "Other", Path = Owner + ":Other",
            Parameters = [new("Callback", Callback() with
            {
                CallbackSignature = Callback().CallbackSignature! with { ReturnType = new(UnrealValueKind.Int32, "int32") },
            }, UnrealParameterDirection.Input)],
        };
        Assert.Throws<JsonException>(() => UnrealReflectionJson.NormalizeAndValidate(manifest with
            { Types = [type with { Functions = [type.Functions[0], second] }] }));
    }
}
