using System.Reflection;
using System.Runtime.Loader;
using Finare.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Finare.Tests;

public sealed class CSharpSourceGeneratorTests
{
    [Fact]
    public void NativePartialPropertiesKeepDeclarationDefaultsAndContainerTypes()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;
            using System.Collections.Generic;
            [ScriptAsset("Game/NativeProperties")]
            [UnrealScriptClass(UnrealScriptClassKind.ActorComponent, GeneratedClassPath = "/Game/NativeProperties.NativeProperties_C")]
            public partial class Behavior : UnrealActorComponentScript {
                [Expose] public partial int Health { get; set; } = 100;
                [Expose] public partial IReadOnlyList<int> Scores { get; set; } = [1, 2];
                [Visual] public int Damage(int amount) { Health -= amount; return Health; }
                [Visual] public UnrealObject? Echo(UnrealObject? value) { return value; }
            }
            """;
        var request = new CSharpCompilationRequest("NativeProperties",
            [new CSharpSourceDocument("NativeProperties.cs", source)],
            [typeof(Finare.Unreal.UnrealObjectScript).Assembly.Location],
            sourceGeneratorPaths: [typeof(Finare.Unreal.Generators.UnrealNativePropertyGenerator).Assembly.Location]);
        var result = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var asset = Assert.Single(result.ScriptAssets!.Assets);
        Assert.Equal(2, asset.States.Count);
        Assert.Equal(100, Assert.Single(asset.States, s => s.Name == "Health").DefaultValue.GetInt32());
        Assert.Equal("[1,2]", Assert.Single(asset.States, s => s.Name == "Scores").DefaultValue.GetRawText());
        using var output = new MemoryStream();
        var emitted = new CSharpCompilationHost().Create(request).Emit(output);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
    }

    [Fact]
    public void CompilerRunsCurrentGeneratorBytesAndIncludesThemInConfigurationIdentity()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Finare.GeneratorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string generator = Path.Combine(directory, "TestGenerator.dll");
            EmitGenerator(generator, 42);
            CSharpCompilationRequest first = Request(generator);
            Assert.Equal(42, Execute(new CSharpCompilationHost().Create(first)));
            EmitGenerator(generator, 43);
            CSharpCompilationRequest second = Request(generator);
            Assert.NotEqual(first.ConfigurationId, second.ConfigurationId);
            Assert.Equal(43, Execute(new CSharpCompilationHost().Create(second)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void GeneratorDiagnosticsCannotDisappearFromCompilation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Finare.GeneratorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string generator = Path.Combine(directory, "TestGenerator.dll");
            EmitGenerator(generator, 0, failure: true);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => new CSharpCompilationHost().Create(Request(generator)));
            Assert.Contains("fixture generation failed", error.Message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ProjectLoaderPreservesSdkAnalyzersForInProcessCompilation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Finare.GeneratorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string generator = Path.Combine(directory, "TestGenerator.dll");
            EmitGenerator(generator, 42);
            string project = Path.Combine(directory, "Fixture.csproj");
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Analyzer Include="TestGenerator.dll" /></ItemGroup>
                  <ItemGroup><PackageReference Include="Microsoft.Net.Compilers.Toolset" Version="5.6.0" PrivateAssets="all" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(directory, "Fixture.cs"), Request(generator).Sources[0].Source);
            CSharpProjectLoadResult loaded = new CSharpProjectLoader().Load(project);
            Assert.Contains(generator, loaded.Request.SourceGeneratorPaths);
            Assert.Equal(42, Execute(new CSharpCompilationHost().Create(loaded.Request)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static CSharpCompilationRequest Request(string generator) => new(
        "Fixture", [new CSharpSourceDocument("Fixture.cs",
            "public static partial class Answers { public static int Read() => Value; }")],
        sourceGeneratorPaths: [generator]);

    private static void EmitGenerator(string path, int answer, bool failure = false)
    {
        string body = failure
            ? "throw new System.InvalidOperationException(\"fixture generation failed\");"
            : $$"""context.AddSource("Answer.g.cs", Microsoft.CodeAnalysis.Text.SourceText.From("public static partial class Answers { public static int Value => {{answer}}; }", System.Text.Encoding.UTF8));""";
        string source = $$"""
            using Microsoft.CodeAnalysis;
            [Generator]
            public sealed class TestGenerator : ISourceGenerator {
                public void Initialize(GeneratorInitializationContext context) { }
                public void Execute(GeneratorExecutionContext context) { {{body}} }
            }
            """;
        var request = new CSharpCompilationRequest("TestGenerator",
            [new CSharpSourceDocument("Generator.cs", source)],
            [typeof(ISourceGenerator).Assembly.Location, typeof(CSharpCompilation).Assembly.Location]);
        var emitted = new CSharpCompilationHost().Create(request).Emit(path);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
    }

    private static int Execute(CSharpCompilation compilation)
    {
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        output.Position = 0;
        var context = new AssemblyLoadContext("GeneratorFixture", isCollectible: true);
        try
        {
            Assembly assembly = context.LoadFromStream(output);
            return (int)assembly.GetType("Answers")!.GetMethod("Read")!.Invoke(null, null)!;
        }
        finally { context.Unload(); }
    }
}
