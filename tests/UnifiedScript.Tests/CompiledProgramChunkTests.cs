using UnifiedScript.Core;
using UnifiedScript.CSharp;
using UnifiedScript.Runtime;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class CompiledProgramChunkTests
{
    private const string Source = """
        using UnifiedScript;

        namespace Tests.Chunks;

        [ScriptAsset("Tests/ChunkPolicy")]
        public sealed class ChunkPolicy
        {
            public bool Enabled { get; set; } = true;

            public int Count { get; set; } = 42;

            public string Label { get; set; } = "ready";

            [Visual]
            public int Evaluate(int input)
            {
                return Enabled ? input + Count : 0;
            }
        }
        """;

    [Fact]
    public void Compiler_ProducesDeterministicBinaryRuntimePayloads()
    {
        CSharpProgramImage image = EmitImage();
        var compiler = new CSharpProgramChunkCompiler();

        CSharpProgramChunkEmitResult first = compiler.Compile(image);
        CSharpProgramChunkEmitResult second = compiler.Compile(image);

        Assert.Equal(first.Content, second.Content);
        Assert.Equal(first.Chunk.ContentDigest, second.Chunk.ContentDigest);
        Assert.Equal(image.AssemblyName, first.RuntimeManifest.AssemblyName);
        Assert.Single(first.RuntimeManifest.EntryTypeNames);
        Assert.Equal("Tests.Chunks.ChunkPolicy", first.RuntimeManifest.EntryTypeNames[0]);

        UnifiedScriptRuntimeManifest manifest = UnifiedScriptRuntimeManifestBinary.Decode(
            first.Chunk.GetRequiredSection(
                UnifiedScriptChunkSectionKind.RuntimeManifest).Data.Span);
        Assert.Equal(first.RuntimeManifest.AssemblyName, manifest.AssemblyName);
        Assert.Equal(first.RuntimeManifest.SourceCompilationId, manifest.SourceCompilationId);
        Assert.Equal(
            first.RuntimeManifest.SemanticConfigurationId,
            manifest.SemanticConfigurationId);
        Assert.Equal(first.RuntimeManifest.ProgramImageKey, manifest.ProgramImageKey);
        Assert.Equal(first.RuntimeManifest.EntryTypeNames, manifest.EntryTypeNames);
        Assert.Equal(
            image.PortableExecutable,
            first.Chunk.GetRequiredSection(
                UnifiedScriptChunkSectionKind.ManagedAssembly).Data.ToArray());
    }

    [Fact]
    public void Compiler_CanStripPortablePdbFromRuntimeDeliveryChunk()
    {
        CSharpProgramChunkEmitResult result = new CSharpProgramChunkCompiler().Compile(
            EmitImage(),
            new CSharpProgramChunkOptions(IncludePortablePdb: false));

        Assert.False(result.Chunk.TryGetSection(
            UnifiedScriptChunkSectionKind.PortablePdb,
            out _));
        Assert.Equal(
            Enum.GetValues<UnifiedScriptChunkSectionKind>()
                .Count(static kind => (uint)kind < 0x00001000),
            result.Chunk.Sections.Count);
    }

    [Fact]
    public void DeclarationBinary_PreservesTypedDefaultsWithoutTextJson()
    {
        CSharpProgramChunkEmitResult result = new CSharpProgramChunkCompiler().Compile(EmitImage());

        UnifiedScriptRuntimeDeclarationDocument declarations =
            UnifiedScriptRuntimeDeclarationBinary.Decode(result.Chunk.GetRequiredSection(
                UnifiedScriptChunkSectionKind.DeclarationData).Data.Span);

        UnifiedScriptRuntimeAssetDeclaration asset = Assert.Single(declarations.Assets);
        Assert.Equal("Tests/ChunkPolicy", asset.AssetPath);
        IReadOnlyDictionary<string, UnifiedScriptRuntimeStateDeclaration> states = asset.States
            .ToDictionary(static state => state.Name, StringComparer.Ordinal);
        Assert.True(states["Enabled"].DefaultValue.BooleanValue);
        Assert.Equal(42, states["Count"].DefaultValue.SignedIntegerValue);
        Assert.Equal("ready", states["Label"].DefaultValue.TextValue);
        Assert.Equal(
            UnifiedScriptRuntimeStateValueKind.SignedInteger,
            states["Count"].DefaultValue.Kind);

        ReadOnlySpan<byte> payload = result.Chunk.GetRequiredSection(
            UnifiedScriptChunkSectionKind.DeclarationData).Data.Span;
        Assert.Equal("USRDEC\0\0"u8.ToArray(), payload[..8].ToArray());
        Assert.NotEqual((byte)'{', payload[0]);
    }

    [Fact]
    public void BindingAndDependencyPayloads_AreSortedAndStrictlyRoundTrip()
    {
        var bindings = new UnifiedScriptRuntimeBindingManifest(
        [
            new("z-contract", Digest('b'), 3, 5),
            new("a-contract", Digest('a'), 1, 4),
        ]);
        var dependencies = new UnifiedScriptRuntimeDependencyManifest(
        [
            new(
                UnifiedScriptRuntimeDependencyKind.NativeLibrary,
                "hostfxr",
                "10.0.0",
                Digest('d'),
                Optional: false),
            new(
                UnifiedScriptRuntimeDependencyKind.ManagedAssembly,
                "UnifiedScript.Unreal",
                "1.0.0.0",
                Digest('c'),
                Optional: false),
        ]);

        UnifiedScriptRuntimeBindingManifest decodedBindings =
            UnifiedScriptRuntimeBindingManifestBinary.Decode(
                UnifiedScriptRuntimeBindingManifestBinary.Encode(bindings));
        UnifiedScriptRuntimeDependencyManifest decodedDependencies =
            UnifiedScriptRuntimeDependencyManifestBinary.Decode(
                UnifiedScriptRuntimeDependencyManifestBinary.Encode(dependencies));

        Assert.Equal(
            ["a-contract", "z-contract"],
            decodedBindings.Requirements.Select(static requirement => requirement.ContractId));
        Assert.Equal(
            [
                UnifiedScriptRuntimeDependencyKind.ManagedAssembly,
                UnifiedScriptRuntimeDependencyKind.NativeLibrary,
            ],
            decodedDependencies.Dependencies.Select(static dependency => dependency.Kind));
    }

    [Fact]
    public void EmitCommand_WritesVerifiedChunkBesideLooseMigrationArtifacts()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "UnifiedScript.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "ChunkPolicy.cs");
            string outputPath = Path.Combine(directory, "out");
            File.WriteAllText(sourcePath, Source);

            int exitCode = UnifiedScript.Cli.Program.Main(
                ["emit", sourcePath, "--out", outputPath, "--assembly", "ChunkPolicy"]);

            Assert.Equal(0, exitCode);
            string chunkPath = Path.Combine(outputPath, "ChunkPolicy.uschunk");
            Assert.True(File.Exists(chunkPath));
            UnifiedScriptChunk chunk = UnifiedScriptProgramChunk.Read(File.ReadAllBytes(chunkPath));
            UnifiedScriptRuntimeManifest manifest = UnifiedScriptRuntimeManifestBinary.Decode(
                chunk.GetRequiredSection(
                    UnifiedScriptChunkSectionKind.RuntimeManifest).Data.Span);
            Assert.Equal("ChunkPolicy", manifest.AssemblyName);
            Assert.True(File.Exists(Path.Combine(outputPath, "ChunkPolicy.dll")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CoreClrHost_LoadsAssemblyDirectlyFromVerifiedChunk()
    {
        CSharpProgramChunkEmitResult result = new CSharpProgramChunkCompiler().Compile(EmitImage());

        using CoreClrCompiledChunkSession session = new CoreClrCompiledChunkHost().Load(
            result.Content);
        object entry = session.CreateEntryInstance("Tests.Chunks.ChunkPolicy");

        Assert.Equal("Tests.Chunks.ChunkPolicy", entry.GetType().FullName);
        Assert.Equal(result.Chunk.ContentDigest, session.Chunk.ContentDigest);
        Assert.Equal(
            result.RuntimeManifest.SourceCompilationId,
            session.Declarations.SourceCompilationId);
        Assert.Throws<ArgumentException>(() =>
            session.CreateEntryInstance("Tests.Chunks.NotAnEntry"));
    }

    [Fact]
    public void CoreClrHost_RequiresExactDeclaredDependencyEvidence()
    {
        string runtimeReference = typeof(SemanticId).Assembly.Location;
        CSharpProgramChunkEmitResult result = new CSharpProgramChunkCompiler().Compile(
            EmitImage(),
            new CSharpProgramChunkOptions(RuntimeReferencePaths: [runtimeReference]));

        FileNotFoundException missing = Assert.Throws<FileNotFoundException>(() =>
            new CoreClrCompiledChunkHost().Load(result.Content));
        Assert.Contains("UnifiedScript.Core", missing.Message, StringComparison.Ordinal);

        using CoreClrCompiledChunkSession session = new CoreClrCompiledChunkHost().Load(
            result.Content,
            [runtimeReference]);
        Assert.Single(session.Dependencies.Dependencies);
    }

    [Fact]
    public void UnrealWorldLoader_LoadsDeclaredScriptTypeFromChunkPath()
    {
        const string source = """
            using UnifiedScript.Unreal;

            namespace Tests.Chunks;

            public sealed class ChunkWorldScript : IUnrealManagedScript
            {
                public void OnStart(UnrealScriptContext context) { }

                public void OnTick(float deltaSeconds) { }

                public void OnStop() { }
            }
            """;
        string unrealAssembly = typeof(IUnrealManagedScript).Assembly.Location;
        CSharpProgramEmitResult emitted = new CSharpProgramEmitter().Emit(
            new CSharpCompilationRequest(
                "UnifiedScript.Unreal.ChunkWorldTest",
                [new CSharpSourceDocument("ChunkWorldScript.cs", source)],
                referencePaths: [unrealAssembly],
                runtimeReferencePaths: [unrealAssembly]));
        Assert.True(emitted.Succeeded, string.Join(Environment.NewLine, emitted.Diagnostics));
        CSharpProgramChunkEmitResult chunk = new CSharpProgramChunkCompiler().Compile(
            Assert.IsType<CSharpProgramImage>(emitted.Image),
            new CSharpProgramChunkOptions(
                RuntimeReferencePaths: [unrealAssembly],
                EntryTypeNames: ["Tests.Chunks.ChunkWorldScript"]));
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".uschunk");
        try
        {
            File.WriteAllBytes(path, chunk.Content);

            string loadedType = UnrealWorldManagedEntryPoint.ValidateCompiledProgramForTests(
                path,
                "Tests.Chunks.ChunkWorldScript");

            Assert.Equal("Tests.Chunks.ChunkWorldScript", loadedType);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static CSharpProgramImage EmitImage()
    {
        CSharpProgramEmitResult result = new CSharpProgramEmitter().Emit(
            new CSharpCompilationRequest(
                "UnifiedScript.ChunkTests",
                [new CSharpSourceDocument("ChunkPolicy.cs", Source)]));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        return Assert.IsType<CSharpProgramImage>(result.Image);
    }

    private static string Digest(char digit) => "sha256:" + new string(digit, 64);
}
