using System.Collections.Generic;
using System.Collections.Immutable;
using Finare.CSharp;
using Finare.Unreal;
using Finare.Unreal.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed partial class UnrealNativePropertyGeneratorTests
{
    [Fact]
    public void GeneratesLivePartialPropertyWithMarkerAndCompilableAccessors()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            namespace GeneratorFixture;

            [UnrealScriptClass(UnrealScriptClassKind.ActorComponent, GeneratedClassPath = "/Game/GeneratorFixture_C")]
            public partial class HealthScript : UnrealActorComponentScript
            {
                [Expose(Replicated = true)]
                public partial int Health { get; set; } = 100;
            }
            """;

        (CSharpCompilation output, ImmutableArray<Diagnostic> generatorDiagnostics) = RunGenerator(source);

        Assert.Empty(generatorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains(
            output.SyntaxTrees.Select(tree => tree.ToString()),
            generated => generated.Contains(
                "UnrealNativePropertyAttribute(Replicated = true)",
                StringComparison.Ordinal));
        Assert.Contains(
            output.SyntaxTrees.Select(tree => tree.ToString()),
            generated => generated.Contains(
                "ReadReflectedProperty<global::System.Int32>(nameof(Health))",
                StringComparison.Ordinal));
        Assert.Contains(
            output.SyntaxTrees.Select(tree => tree.ToString()),
            generated => generated.Contains("else field = value;", StringComparison.Ordinal));

        Diagnostic[] errors = output.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
    }

    [Fact]
    public void ReportsDeclarationsThatCannotBeImplementedAsLivePartialProperties()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            public class InvalidFixture : UnrealActorComponentScript
            {
                [Expose]
                public partial int Health { get; set; } = 100;
            }
            """;

        (_, ImmutableArray<Diagnostic> generatorDiagnostics) = RunGenerator(source);

        Assert.Contains(generatorDiagnostics, diagnostic => diagnostic.Id == "FINAREGEN003");
    }

    [Fact]
    public void RejectsNonPartialExposePropertiesInsteadOfUsingAMirrorPath()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            public class LegacyFixture : UnrealActorComponentScript
            {
                [Expose]
                public int Health { get; set; } = 100;
            }
            """;

        (_, ImmutableArray<Diagnostic> generatorDiagnostics) = RunGenerator(source);

        Assert.Contains(generatorDiagnostics, diagnostic => diagnostic.Id == "FINAREGEN001");
    }

    [Fact]
    public void GeneratesAllInstancePropertiesForAUnrealObjectScriptScriptAsset()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Tests/ImplicitState")]
            [UnrealScriptClass(UnrealScriptClassKind.Object, GeneratedClassPath = "/Game/ImplicitState_C")]
            public sealed partial class ImplicitState : UnrealObjectScript
            {
                public partial int Value { get; set; } = 7;
                public static int Helper { get; } = 11;
            }
            """;

        (CSharpCompilation output, ImmutableArray<Diagnostic> generatorDiagnostics) = RunGenerator(source);

        Assert.DoesNotContain(generatorDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        string generated = output.SyntaxTrees
            .Select(tree => tree.ToString())
            .Single(tree => tree.Contains("UnrealNativePropertyAttribute", StringComparison.Ordinal));
        Assert.Contains("nameof(Value)", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(Helper)", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void LeavesExposePropertiesOnNonUnrealScriptTypesUntouched()
    {
        const string source = """
            using Finare;

            public sealed class PlainState
            {
                [Expose]
                public int Value { get; set; }
            }
            """;

        (_, ImmutableArray<Diagnostic> generatorDiagnostics) = RunGenerator(source);

        Assert.DoesNotContain(generatorDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void GeneratesNestedPartialOwnersWithNullableTypesAndConstraints()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            namespace GeneratorFixture;

            public partial class Outer<T> where T : class
            {
                public partial class Inner<U> : UnrealActorComponentScript where U : struct
                {
                    [Expose]
                    public partial string? Value { get; private set; }
                }
            }
            """;

        (CSharpCompilation output, ImmutableArray<Diagnostic> generatorDiagnostics) = RunGenerator(source);

        Assert.DoesNotContain(
            generatorDiagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(
            output.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        string generated = output.SyntaxTrees
            .Select(tree => tree.ToString())
            .Single(tree => tree.Contains("UnrealNativePropertyAttribute", StringComparison.Ordinal));
        Assert.Contains("partial class Outer<T>", generated, StringComparison.Ordinal);
        Assert.Contains("partial class Inner<U>", generated, StringComparison.Ordinal);
        Assert.Contains("global::System.String? Value", generated, StringComparison.Ordinal);
        Assert.Contains("private set", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ReflectedPropertyUsesLiveHostSkipsStateMirrorAndRejectsAfterStop()
    {
        var bridge = new LiveBridge { Authority = true };
        UnrealObjectHandle host = UnrealObjectHandle.Create(1, 4);
        UnrealObjectHandle actor = UnrealObjectHandle.Create(2, 4);
        string path = "/Game/LivePropertyFixture_C:Health";
        bridge.Values[path] = 321;
        var script = new LiveReflectedScript();
        IUnrealManagedScript lifecycle = script;

        // The declared initializer is the pre-activation field value.
        Assert.Equal(100, script.Health);
        lifecycle.OnStart(new UnrealScriptContext(bridge, host, actor, 17, 1));

        Assert.Equal(321, script.ReadLiveHealth());
        Assert.Equal(1, bridge.ReadCount);
        script.WriteLiveHealth(333);
        Assert.Equal(333, bridge.Values[path]);
        // The generated accessor remains live after writes; it never falls
        // back to the declaration-side field while the generation is active.
        Assert.Equal(333, script.Health);
        Assert.Equal(2, bridge.ReadCount);

        lifecycle.OnStop();

        Assert.Throws<ObjectDisposedException>(() => script.ReadLiveHealth());
        Assert.Throws<ObjectDisposedException>(() => script.WriteLiveHealth(444));
    }

    [Fact]
    public void ReplicatedReflectedWriteRequiresActorAuthority()
    {
        var bridge = new LiveBridge { Authority = false };
        UnrealObjectHandle host = UnrealObjectHandle.Create(3, 5);
        UnrealObjectHandle actor = UnrealObjectHandle.Create(4, 5);
        var script = new LiveReflectedScript();
        IUnrealManagedScript lifecycle = script;
        lifecycle.OnStart(new UnrealScriptContext(bridge, host, actor, 18, 1));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => script.WriteLiveHealth(12));

        Assert.Contains("Replicated reflected property", error.Message, StringComparison.Ordinal);
        Assert.Empty(bridge.Writes);
        lifecycle.OnStop();
    }

    private static (CSharpCompilation Output, ImmutableArray<Diagnostic> Diagnostics) RunGenerator(
        string source)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview));
        CSharpCompilation compilation = new CSharpCompilationHost().Create(
            syntaxTree,
            [
                MetadataReference.CreateFromFile(typeof(ExposeAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(UnrealObject).Assembly.Location),
            ]);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new UnrealNativePropertyGenerator().AsSourceGenerator()],
            parseOptions: (CSharpParseOptions)syntaxTree.Options);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics);
        return ((CSharpCompilation)output, diagnostics);
    }

    [UnrealScriptClass(
        UnrealScriptClassKind.ActorComponent,
        GeneratedClassPath = "/Game/LivePropertyFixture_C")]
    private sealed partial class LiveReflectedScript : UnrealActorComponentScript
    {
        [Expose(Replicated = true)]
        public partial int Health { get; set; } = 100;

        public int ReadLiveHealth() => ReadReflectedProperty<int>(nameof(Health));

        public void WriteLiveHealth(int value) =>
            WriteReflectedProperty(nameof(Health), value, replicated: true);
    }

    private sealed class LiveBridge : IUnrealInvocationBridge
    {
        public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

        public List<string> ReadPaths { get; } = [];

        public List<string> Writes { get; } = [];

        public int ReadCount { get; private set; }

        public bool Authority { get; init; }

        public T ReadProperty<T>(UnrealObjectHandle target, string propertyPath)
        {
            ReadPaths.Add(propertyPath);
            ReadCount++;
            return (T)(Values.TryGetValue(propertyPath, out object? value)
                ? value!
                : throw new KeyNotFoundException(propertyPath));
        }

        public void WriteProperty<T>(UnrealObjectHandle target, string propertyPath, T value)
        {
            Values[propertyPath] = value;
            Writes.Add(propertyPath);
        }

        public T Invoke<T>(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<object?> arguments) =>
            functionPath == "/Script/Engine.Actor:HasAuthority"
                ? (T)(object)Authority
                : throw new NotSupportedException(functionPath);

        public void Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<object?> arguments) =>
            throw new NotSupportedException(functionPath);
    }
}
