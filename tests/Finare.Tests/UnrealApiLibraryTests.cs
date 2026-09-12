using Finare.CSharp;
using Finare.Unreal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed class UnrealApiLibraryTests
{
    [Fact]
    public void AvailableLibraryCompilesBeforeUseAndAcceptsNewCallsWithoutRegeneration()
    {
        UnrealReflectionManifest reflection = UnrealReflectionJson.Deserialize(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "UnrealFixtures", "FinareRuntime.reflection.json")));
        UnrealApiContractProjection projection = UnrealApiLibrary.Project(reflection, "Game.Generated");
        UnrealBindingGenerationResult managed = new UnrealBindingGenerator().Generate(projection.Reflection,
            new UnrealBindingGeneratorOptions("Game.Generated"));
        UnrealNativeThunkGenerationResult native = new UnrealNativeThunkGenerator().Generate(projection.NativeReflection);
        Assert.Contains(" GetActorLocation(", managed.Source);
        Assert.DoesNotContain("/Script/Engine.Actor:K2_GetActorLocation", native.Source);
        Assert.DoesNotContain(UnrealBindingCoverageReport.Create(projection, managed, native).Entries,
            entry => entry.Disposition == UnrealBindingDisposition.Rejected);
        CSharpCompilation library = new CSharpCompilationHost().Create(CSharpSyntaxTree.ParseText(managed.Source),
            [MetadataReference.CreateFromFile(typeof(UnrealObject).Assembly.Location)]);
        Assert.Empty(library.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        foreach (string body in new[] { "return default;", "return actor.GetActorLocation();", "actor.SetActorTickEnabled(true); return actor.GetActorLocation();" })
        {
            CSharpCompilation gameplay = library.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                "public static class Gameplay { public static Finare.Unreal.UnrealVector Run(Game.Generated.Actor actor) { " + body + " } }"));
            Assert.Empty(gameplay.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        }
        Assert.Equal(managed.Source, new UnrealBindingGenerator().Generate(
            UnrealApiLibrary.Project(reflection, "Game.Generated").Reflection,
            new UnrealBindingGeneratorOptions("Game.Generated")).Source);
    }

    [Fact]
    public void BroadMetadataReservesCSharpNamesAndDeprecatedOverloadsCompile()
    {
        const string owner = "/Script/Game.Collision";
        var integer = new UnrealTypeReference(UnrealValueKind.Int32, "int32");
        UnrealReflectedFunction Function(string name) => new(owner + ":" + name, name, integer,
            [new("Value", integer, UnrealParameterDirection.Input) { DefaultValue = "1" }], new(false, false, true));
        var type = new UnrealReflectedType(owner, "Collision", "UCollision", "Game", UnrealReflectedTypeKind.Class,
            null, new(false, true), [new(owner + ":Collision", "Collision", integer, new(true, true, false))],
            [Function("GetType"), Function("Play"), Function("K2_Play"), Function("Old") with { Metadata = new() { Deprecated = true } }], "Collision.h");
        var enumeration = new UnrealReflectedType("/Script/Game.Mode", "Mode", "EMode", "Game", UnrealReflectedTypeKind.Enum,
            null, new(false, true), [], [], "Mode.h") { EnumValues = [new("Equals", 1), new("Value", 2)] };
        var reflection = UnrealReflectionManifest.Create(new(5, 8, 1, "test"), ["Game"], [type, enumeration]);
        var projection = UnrealApiLibrary.Project(reflection, "Game.Generated");
        var generated = new UnrealBindingGenerator().Generate(projection.Reflection, new("Game.Generated"));
        Assert.Contains("K2_Play(", generated.Source);
        var compilation = new CSharpCompilationHost().Create(CSharpSyntaxTree.ParseText(generated.Source),
            [MetadataReference.CreateFromFile(typeof(UnrealObject).Assembly.Location)]);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning));
    }

    [Fact]
    public void ReflectedStructLayoutKeepsSupportedFieldsWithoutCompilingPrivateHeaders()
    {
        const string owner = "/Script/Game.LibraryFixture";
        const string valuePath = "/Script/Game.Value";
        var integer = new UnrealTypeReference(UnrealValueKind.Int32, "int32");
        var value = new UnrealTypeReference(UnrealValueKind.Struct, "FValue", valuePath);
        var structure = new UnrealReflectedType(valuePath, "Value", "FValue", "Game",
            UnrealReflectedTypeKind.Struct, null, new(false, true),
            [new(valuePath + ":Count", "Count", integer, new(true, true, false)),
             new(valuePath + ":Fixed", "Fixed", new(UnrealValueKind.Unsupported, "int32[2]"), new(true, true, false))],
            [], "Private/Value.h");
        var function = new UnrealReflectedFunction(owner + ":Echo", "Echo", value,
            [new("Input", value, UnrealParameterDirection.Input)], new(false, false, true));
        var type = new UnrealReflectedType(owner, "LibraryFixture", "ULibraryFixture", "Game",
            UnrealReflectedTypeKind.Class, null, new(false, true), [], [function], "Private/LibraryFixture.h");
        var projection = UnrealApiLibrary.Project(
            UnrealReflectionManifest.Create(new(5, 8, 1, "test"), ["Game"], [type, structure]), "Game.Generated");
        Assert.Equal("Count", Assert.Single(projection.Reflection.Types.Single(t => t.Path == valuePath).Properties).Name);
        var managed = new UnrealBindingGenerator().Generate(projection.Reflection, new("Game.Generated"));
        var native = new UnrealNativeThunkGenerator().Generate(projection.NativeReflection);
        Assert.Contains(" Echo(", managed.Source);
        Assert.DoesNotContain("Private/", native.Source);
        Assert.DoesNotContain(UnrealBindingCoverageReport.Create(projection, managed, native).Entries,
            entry => entry.Disposition == UnrealBindingDisposition.Rejected);
        var compilation = new CSharpCompilationHost().Create(CSharpSyntaxTree.ParseText(managed.Source),
            [MetadataReference.CreateFromFile(typeof(UnrealObject).Assembly.Location)]);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void RefOutDefaultOverloadsUseGenericReflectionAndPreserveDirections()
    {
        const string owner = "/Script/Game.LibraryFixture";
        var integer = new UnrealTypeReference(UnrealValueKind.Int32, "int32");
        var function = new UnrealReflectedFunction(owner + ":Update", "Update", integer,
            [new("Total", integer, UnrealParameterDirection.InputOutput),
             new("Result", integer, UnrealParameterDirection.Output),
             new("Delta", integer, UnrealParameterDirection.Input) { DefaultValue = "1" }], new(false, false, true));
        var unsupported = function with { Name = "Unsupported", Path = owner + ":Unsupported",
            ReturnType = new(UnrealValueKind.Unsupported, "FUnknown") };
        var reflection = UnrealReflectionManifest.Create(new(5, 8, 1, "test"), ["Game"],
            [new(owner, "LibraryFixture", "ULibraryFixture", "Game", UnrealReflectedTypeKind.Class,
                "/Script/CoreUObject.Object", new(false, true), [], [function, unsupported], "LibraryFixture.h")]);
        var projection = UnrealApiLibrary.Project(reflection, "Game.Generated");
        var managed = new UnrealBindingGenerator().Generate(projection.Reflection, new("Game.Generated"));
        Assert.Contains("Update(ref total, out result, 1)", managed.Source);
        Assert.DoesNotContain("Unsupported(", managed.Source);
        Assert.Empty(projection.NativeReflection.Types.SelectMany(type => type.Functions));
        var compilation = new CSharpCompilationHost().Create(CSharpSyntaxTree.ParseText(managed.Source),
            [MetadataReference.CreateFromFile(typeof(UnrealObject).Assembly.Location)]);
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }
}
