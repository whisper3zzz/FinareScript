using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnifiedScript.CSharp;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealBindingGeneratorTests
{
    [Fact]
    public void GenerateProducesDeterministicCompilableManagedWrapper()
    {
        UnrealReflectionManifest manifest = CreateManifest();
        var generator = new UnrealBindingGenerator();

        UnrealBindingGenerationResult first = generator.Generate(manifest);
        UnrealBindingGenerationResult second = generator.Generate(manifest);

        Assert.Equal(first.Source, second.Source);
        Assert.Empty(first.Diagnostics);
        Assert.Contains(
            "[global::UnifiedScript.Unreal.UnrealTypeAttribute(\"/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent\")]",
            first.Source,
            StringComparison.Ordinal);
        Assert.Contains("public string ScriptAssetPath", first.Source, StringComparison.Ordinal);
        Assert.Contains(
            "get => ReadProperty<string>(\"/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:ScriptAssetPath\");",
            first.Source,
            StringComparison.Ordinal);
        Assert.Contains("public int Add(int left, int right)", first.Source, StringComparison.Ordinal);
        Assert.Contains(
            "Invoke<int>(\"/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:Add\", left, right);",
            first.Source,
            StringComparison.Ordinal);

        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(first.Source);
        CSharpCompilation compilation = new CSharpCompilationHost().Create(
            syntaxTree,
            [MetadataReference.CreateFromFile(typeof(UnrealObject).Assembly.Location)]);
        Diagnostic[] errors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
    }

    [Fact]
    public void GenerateSkipsDeferredSignaturesWithStableDiagnostics()
    {
        UnrealReflectionManifest manifest = CreateManifest();
        UnrealReflectedType type = manifest.Types[0];
        UnrealReflectedProperty unsupportedProperty = new(
            $"{type.Path}:Transform",
            "Transform",
            new UnrealTypeReference(
                UnrealValueKind.Struct,
                "FTransform",
                "/Script/CoreUObject.Transform"),
            new UnrealPropertyAccess(Readable: true, Writable: true, EditorVisible: true));
        UnrealReflectedFunction outFunction = new(
            $"{type.Path}:TryRead",
            "TryRead",
            new UnrealTypeReference(UnrealValueKind.Boolean, "bool"),
            [new UnrealReflectedParameter(
                "Value",
                new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                UnrealParameterDirection.Output)],
            new UnrealFunctionTraits(Static: false, Pure: false, Callable: true));
        manifest = manifest with
        {
            Types =
            [
                type with
                {
                    Properties = [.. type.Properties, unsupportedProperty],
                    Functions = [.. type.Functions, outFunction],
                },
            ],
        };

        UnrealBindingGenerationResult result = new UnrealBindingGenerator().Generate(manifest);

        Assert.Equal(["USUE2002", "USUE2004"], result.Diagnostics.Select(static item => item.Code));
        Assert.DoesNotContain(" Transform", result.Source, StringComparison.Ordinal);
        Assert.DoesNotContain(" TryRead", result.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedWrapperRequiresLiveHandleAndBridge()
    {
        Assert.Throws<ArgumentException>(() => new FixtureWrapper(
            new UnrealObjectHandle(0),
            new FixtureBridge()));
        Assert.Throws<ArgumentNullException>(() => new FixtureWrapper(
            new UnrealObjectHandle(1),
            null!));
    }

    [Fact]
    public void CheckedInFixtureMatchesDeterministicGeneratorOutput()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "UnrealFixtures");
        UnrealReflectionManifest manifest = UnrealReflectionJson.Deserialize(File.ReadAllText(
            Path.Combine(fixtureDirectory, "UEUnifiedScriptRuntime.reflection.json")));
        string expected = File.ReadAllText(
            Path.Combine(fixtureDirectory, "UEUnifiedScriptRuntime.bindings.cs"));

        UnrealBindingGenerationResult generated = new UnrealBindingGenerator().Generate(manifest);

        Assert.Empty(generated.Diagnostics);
        Assert.Equal(expected.ReplaceLineEndings("\n"), generated.Source);
    }

    private static UnrealReflectionManifest CreateManifest()
    {
        const string typePath = "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent";
        return UnrealReflectionManifest.Create(
            new UnrealEngineIdentity(5, 7, 4, "UE5"),
            ["UEUnifiedScriptRuntime"],
            [new UnrealReflectedType(
                typePath,
                "UnifiedScriptComponent",
                "UUnifiedScriptComponent",
                "UEUnifiedScriptRuntime",
                UnrealReflectedTypeKind.Class,
                "/Script/Engine.ActorComponent",
                new UnrealTypeTraits(Abstract: false, BlueprintType: true),
                [new UnrealReflectedProperty(
                    $"{typePath}:ScriptAssetPath",
                    "ScriptAssetPath",
                    new UnrealTypeReference(UnrealValueKind.String, "FString"),
                    new UnrealPropertyAccess(Readable: true, Writable: true, EditorVisible: true))],
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
                    new UnrealFunctionTraits(Static: false, Pure: true, Callable: true))])]);
    }

    private sealed class FixtureWrapper : UnrealObject
    {
        public FixtureWrapper(UnrealObjectHandle handle, IUnrealInvocationBridge bridge)
            : base(handle, bridge)
        {
        }
    }

    private sealed class FixtureBridge : IUnrealInvocationBridge
    {
        public T ReadProperty<T>(UnrealObjectHandle target, string propertyPath) => default!;

        public void WriteProperty<T>(UnrealObjectHandle target, string propertyPath, T value)
        {
        }

        public T Invoke<T>(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<object?> arguments) => default!;

        public void Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<object?> arguments)
        {
        }
    }
}
