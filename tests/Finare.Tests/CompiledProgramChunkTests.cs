using System.Text.Json;
using Finare.Core;
using Finare.CSharp;
using Finare.Runtime;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Runtime")]
public sealed class CompiledProgramChunkTests
{
    private const string Source = """
        using Finare;

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

        FinareRuntimeManifest manifest = FinareRuntimeManifestBinary.Decode(
            first.Chunk.GetRequiredSection(
                FinareChunkSectionKind.RuntimeManifest).Data.Span);
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
                FinareChunkSectionKind.ManagedAssembly).Data.ToArray());
    }

    [Fact]
    public void Compiler_DefaultEntriesIncludeScriptAssetsWithoutVisualMethods()
    {
        const string source = """
            using Finare;

            namespace Tests.Chunks;

            [ScriptAsset("Tests/ChunkLifecycle")]
            public sealed class ChunkLifecycle
            {
                public int Value { get; set; } = 0;
            }
            """;

        CSharpProgramChunkEmitResult result = new CSharpProgramChunkCompiler().Compile(
            EmitImage(source));

        Assert.Contains(
            "Tests.Chunks.ChunkLifecycle",
            result.RuntimeManifest.EntryTypeNames);
    }

    [Fact]
    public void Compiler_SourceOnlyEntryUsesDefaultAndExplicitTypeSelectionAndExecutesAfterRoundTrip()
    {
        const string source = """
            using System;
            using Finare.Unreal;

            namespace Tests.Chunks;

            public sealed class SourceOnlyChunkEntry : UnrealObjectScript
            {
                [UnrealBlueprintEvent]
                public int Calculate(int value)
                {
                    Func<int, int> addOne = item => item + 1;
                    return addOne(value);
                }
            }
            """;
        string unrealAssembly = typeof(UnrealObjectScript).Assembly.Location;
        CSharpProgramEmitResult emitted = new CSharpProgramEmitter().Emit(
            new CSharpCompilationRequest(
                "Finare.SourceOnlyChunkTests",
                [new CSharpSourceDocument("SourceOnlyChunkEntry.cs", source)],
                referencePaths: [unrealAssembly],
                runtimeReferencePaths: [unrealAssembly]));
        Assert.True(emitted.Succeeded, string.Join(Environment.NewLine, emitted.Diagnostics));
        CSharpProgramImage image = Assert.IsType<CSharpProgramImage>(emitted.Image);
        const string entryType = "Tests.Chunks.SourceOnlyChunkEntry";

        CSharpProgramChunkEmitResult defaultChunk = new CSharpProgramChunkCompiler().Compile(
            image,
            new CSharpProgramChunkOptions(RuntimeReferencePaths: [unrealAssembly]));
        Assert.Equal([entryType], defaultChunk.RuntimeManifest.EntryTypeNames);

        CSharpProgramChunkEmitResult explicitChunk = new CSharpProgramChunkCompiler().Compile(
            image,
            new CSharpProgramChunkOptions(
                RuntimeReferencePaths: [unrealAssembly],
                EntryTypeNames: [entryType]));
        Assert.Equal([entryType], explicitChunk.RuntimeManifest.EntryTypeNames);

        using CoreClrCompiledChunkSession session = new CoreClrCompiledChunkHost().Load(
            explicitChunk.Content,
            [unrealAssembly]);
        object instance = session.CreateEntryInstance(entryType);
        object? value = instance.GetType().GetMethod("Calculate")!.Invoke(instance, [41]);
        Assert.Equal(42, value);
    }

    [Fact]
    public void Compiler_CanStripPortablePdbFromRuntimeDeliveryChunk()
    {
        CSharpProgramChunkEmitResult result = new CSharpProgramChunkCompiler().Compile(
            EmitImage(),
            new CSharpProgramChunkOptions(IncludePortablePdb: false));

        Assert.False(result.Chunk.TryGetSection(
            FinareChunkSectionKind.PortablePdb,
            out _));
        Assert.Equal(
            Enum.GetValues<FinareChunkSectionKind>()
                .Count(static kind => (uint)kind < 0x00001000),
            result.Chunk.Sections.Count);
    }

    [Fact]
    public void DeclarationBinary_PreservesTypedDefaultsWithoutTextJson()
    {
        CSharpProgramChunkEmitResult result = new CSharpProgramChunkCompiler().Compile(EmitImage());

        FinareRuntimeDeclarationDocument declarations =
            FinareRuntimeDeclarationBinary.Decode(result.Chunk.GetRequiredSection(
                FinareChunkSectionKind.DeclarationData).Data.Span);

        FinareRuntimeAssetDeclaration asset = Assert.Single(declarations.Assets);
        Assert.Equal("Tests/ChunkPolicy", asset.AssetPath);
        IReadOnlyDictionary<string, FinareRuntimeStateDeclaration> states = asset.States
            .ToDictionary(static state => state.Name, StringComparer.Ordinal);
        Assert.True(states["Enabled"].DefaultValue.BooleanValue);
        Assert.Equal(42, states["Count"].DefaultValue.SignedIntegerValue);
        Assert.Equal("ready", states["Label"].DefaultValue.TextValue);
        Assert.Equal(
            FinareRuntimeStateValueKind.SignedInteger,
            states["Count"].DefaultValue.Kind);

        ReadOnlySpan<byte> payload = result.Chunk.GetRequiredSection(
            FinareChunkSectionKind.DeclarationData).Data.Span;
        Assert.Equal("USRDEC\0\0"u8.ToArray(), payload[..8].ToArray());
        Assert.NotEqual((byte)'{', payload[0]);
    }

    [Fact]
    public void DeclarationBinary_PreservesCommonUnrealStateShapesAndStructIdentity()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Tests/CommonState")]
            public sealed class CommonState
            {
                public UnrealName RowName { get; set; } = new("Player");
                public UnrealText Label { get; set; } = new("Ready");
                public UnrealObjectHandle Target { get; set; } = default;
                public UnrealClassHandle ActorClass { get; set; } = default;
                public UnrealVector Offset { get; set; } = new(1.0, 2.0, 3.0);
            }
            """;
        CSharpProgramImage image = EmitImage(
            source,
            [typeof(UnrealObjectScript).Assembly.Location]);
        CSharpProgramChunkEmitResult result = new CSharpProgramChunkCompiler().Compile(image);

        FinareRuntimeDeclarationDocument declarations =
            FinareRuntimeDeclarationBinary.Decode(result.Chunk.GetRequiredSection(
                FinareChunkSectionKind.DeclarationData).Data.Span);

        IReadOnlyDictionary<string, FinareRuntimeStateDeclaration> states =
            Assert.Single(declarations.Assets).States.ToDictionary(
                static state => state.Name,
                StringComparer.Ordinal);
        Assert.Equal(FinareRuntimeStateValueKind.Name, states["RowName"].DefaultValue.Kind);
        Assert.Equal("Player", states["RowName"].DefaultValue.TextValue);
        Assert.Equal(FinareRuntimeStateValueKind.Text, states["Label"].DefaultValue.Kind);
        Assert.Equal(FinareRuntimeStateValueKind.Null, states["Target"].DefaultValue.Kind);
        Assert.Equal(FinareRuntimeStateValueKind.Null, states["ActorClass"].DefaultValue.Kind);
        FinareRuntimeStateDeclaration offset = states["Offset"];
        Assert.Equal(FinareRuntimeStateValueKind.Vector, offset.DefaultValue.Kind);
        Assert.Equal(1.0, offset.DefaultValue.VectorX);
        Assert.Equal(2.0, offset.DefaultValue.VectorY);
        Assert.Equal(3.0, offset.DefaultValue.VectorZ);
        Assert.Equal("/Script/CoreUObject.Vector", offset.UnrealType?.UnrealTypePath);

        ReadOnlySpan<byte> payload = result.Chunk.GetRequiredSection(
            FinareChunkSectionKind.DeclarationData).Data.Span;
        Assert.Equal((byte)1, payload[10]);
        Assert.Equal((byte)0, payload[11]);
    }

    [Fact]
    public void DeclarationBinary_UsesMinor2ForTypedEnumsAndSpatialValues()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [UnrealEnum("/Script/FixtureRuntime.EFixtureMode", "EFixtureMode")]
            [UnrealValue]
            public readonly record struct FixtureMode(long Value) : IUnrealEnumValue
            {
                [UnrealEnumConstant(7)]
                public static FixtureMode Active { get; } = new(7);
            }

            [ScriptAsset("Tests/TypedSpatialState")]
            public sealed class TypedSpatialState
            {
                public FixtureMode Mode { get; set; } = FixtureMode.Active;
                public UnrealRotator Rotation { get; set; } = new(1, 2, 3);
                public UnrealTransform Transform { get; set; } = UnrealTransform.Identity;
            }
            """;
        CSharpProgramChunkEmitResult result = new CSharpProgramChunkCompiler().Compile(
            EmitImage(source, [typeof(UnrealObjectScript).Assembly.Location]));
        FinareRuntimeDeclarationDocument declarations =
            FinareRuntimeDeclarationBinary.Decode(result.Chunk.GetRequiredSection(
                FinareChunkSectionKind.DeclarationData).Data.Span);
        IReadOnlyDictionary<string, FinareRuntimeStateDeclaration> states =
            Assert.Single(declarations.Assets).States.ToDictionary(
                static state => state.Name,
                StringComparer.Ordinal);

        Assert.Equal(FinareRuntimeStateValueKind.Enum, states["Mode"].DefaultValue.Kind);
        Assert.Equal("7", states["Mode"].DefaultValue.TextValue);
        Assert.Equal(FinareRuntimeStateValueKind.Rotator, states["Rotation"].DefaultValue.Kind);
        Assert.Equal(1, states["Rotation"].DefaultValue.RotatorPitch);
        Assert.Equal(FinareRuntimeStateValueKind.Transform, states["Transform"].DefaultValue.Kind);
        Assert.Equal(1, states["Transform"].DefaultValue.TransformRotationW);
        Assert.Equal(1, states["Transform"].DefaultValue.TransformScaleX);

        ReadOnlySpan<byte> payload = result.Chunk.GetRequiredSection(
            FinareChunkSectionKind.DeclarationData).Data.Span;
        Assert.Equal((byte)2, payload[10]);
        Assert.Equal((byte)0, payload[11]);
    }

    [Fact]
    public void DeclarationBinary_RejectsTypedEnumPayloadWhenMinorVersionIsLowered()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [UnrealEnum("/Script/FixtureRuntime.EFixtureMode", "EFixtureMode")]
            [UnrealValue]
            public readonly record struct FixtureMode(long Value) : IUnrealEnumValue
            {
                [UnrealEnumConstant(7)]
                public static FixtureMode Active { get; } = new(7);
            }

            [ScriptAsset("Tests/TypedEnumCompatibility")]
            public sealed class TypedEnumCompatibility
            {
                public FixtureMode Mode { get; set; } = FixtureMode.Active;
            }
            """;
        CSharpProgramImage image = EmitImage(source, [typeof(UnrealObjectScript).Assembly.Location]);
        CSharpProgramChunkEmitResult result = new CSharpProgramChunkCompiler().Compile(image);
        byte[] payload = result.Chunk.GetRequiredSection(
            FinareChunkSectionKind.DeclarationData).Data.ToArray();
        payload[10] = 1;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            FinareRuntimeDeclarationBinary.Decode(payload));
        Assert.Contains("requires declaration minor version 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclarationBinary_RejectsSpatialPayloadWhenMinorVersionIsLowered()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Tests/SpatialCompatibility")]
            public sealed class SpatialCompatibility
            {
                public UnrealRotator Rotation { get; set; } = new(1, 2, 3);
                public UnrealTransform Transform { get; set; } = UnrealTransform.Identity;
            }
            """;
        CSharpProgramImage image = EmitImage(source, [typeof(UnrealObjectScript).Assembly.Location]);
        CSharpProgramChunkEmitResult result = new CSharpProgramChunkCompiler().Compile(image);
        byte[] payload = result.Chunk.GetRequiredSection(
            FinareChunkSectionKind.DeclarationData).Data.ToArray();
        payload[10] = 1;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            FinareRuntimeDeclarationBinary.Decode(payload));
        Assert.Contains("Unsupported runtime state value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclarationBinary_UsesMinor3ForSoftReferenceStatesAndRejectsOlderReaders()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Tests/SoftReferenceState")]
            public sealed class SoftReferenceState
            {
                public UnrealSoftObjectHandle Mesh { get; set; } = new("/Game/Props/Cube.Cube");
                public UnrealSoftClassHandle ActorClass { get; set; } = new("/Script/Engine.StaticMeshActor");
            }
            """;
        CSharpProgramChunkEmitResult result = new CSharpProgramChunkCompiler().Compile(
            EmitImage(source, [typeof(UnrealObjectScript).Assembly.Location]));
        byte[] payload = result.Chunk.GetRequiredSection(
            FinareChunkSectionKind.DeclarationData).Data.ToArray();
        Assert.Equal((byte)3, payload[10]);
        FinareRuntimeDeclarationDocument decoded =
            FinareRuntimeDeclarationBinary.Decode(payload);
        IReadOnlyDictionary<string, FinareRuntimeStateDeclaration> states =
            Assert.Single(decoded.Assets).States.ToDictionary(
                static state => state.Name,
                StringComparer.Ordinal);
        Assert.Equal(FinareRuntimeStateValueKind.SoftObject, states["Mesh"].DefaultValue.Kind);
        Assert.Equal("/Game/Props/Cube.Cube", states["Mesh"].DefaultValue.TextValue);
        Assert.Equal(FinareRuntimeStateValueKind.SoftClass, states["ActorClass"].DefaultValue.Kind);
        Assert.Equal("/Script/Engine.StaticMeshActor", states["ActorClass"].DefaultValue.TextValue);

        byte[] lowered = payload.ToArray();
        lowered[10] = 2;
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            FinareRuntimeDeclarationBinary.Decode(lowered));
        Assert.Contains("requires declaration minor version 3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclarationBinary_UsesTypedChildDefaultsForCollectionsAndRejectsOlderReaders()
    {
        const string source = """
            using System.Collections.Generic;
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Tests/CollectionState")]
            public sealed class CollectionState
            {
                public IReadOnlyList<UnrealVector> Points { get; set; } =
                    new List<UnrealVector> { new(1, 2, 3) };
                public IReadOnlySet<bool> Flags { get; set; } =
                    new HashSet<bool> { true, false };
                public IReadOnlySet<UnrealVector> VectorSet { get; set; } =
                    new HashSet<UnrealVector> { new(1, 2, 3), new(1, 2, 4) };
                public IReadOnlySet<UnrealName> Names { get; set; } =
                    new HashSet<UnrealName> { new("Alpha") };
                public IReadOnlyDictionary<UnrealName, UnrealSoftObjectHandle> Assets { get; set; } =
                    new Dictionary<UnrealName, UnrealSoftObjectHandle>
                    {
                        { new UnrealName("Mesh"), new UnrealSoftObjectHandle("/Game/Props/Cube.Cube") },
                    };
            }
            """;
        CSharpProgramChunkEmitResult result = new CSharpProgramChunkCompiler().Compile(
            EmitImage(source, [typeof(UnrealObjectScript).Assembly.Location]));
        byte[] payload = result.Chunk.GetRequiredSection(
            FinareChunkSectionKind.DeclarationData).Data.ToArray();
        Assert.Equal((byte)4, payload[10]);

        FinareRuntimeDeclarationDocument decoded =
            FinareRuntimeDeclarationBinary.Decode(payload);
        IReadOnlyDictionary<string, FinareRuntimeStateDeclaration> states =
            Assert.Single(decoded.Assets).States.ToDictionary(static state => state.Name, StringComparer.Ordinal);
        FinareRuntimeStateValue points = states["Points"].DefaultValue;
        Assert.Equal(FinareRuntimeStateValueKind.Array, points.Kind);
        Assert.Equal(FinareRuntimeStateValueKind.Vector, Assert.Single(points.ContainerValues!).Kind);
        Assert.Equal(3, Assert.Single(points.ContainerValues!).VectorZ);
        FinareRuntimeStateValue flags = states["Flags"].DefaultValue;
        Assert.Equal(2, flags.ContainerValues!.Count);
        Assert.Contains(flags.ContainerValues, static value => value.BooleanValue);
        Assert.Contains(flags.ContainerValues, static value => !value.BooleanValue);
        FinareRuntimeStateValue vectorSet = states["VectorSet"].DefaultValue;
        Assert.Equal(2, vectorSet.ContainerValues!.Count);
        Assert.NotEqual(
            vectorSet.ContainerValues[0].VectorZ,
            vectorSet.ContainerValues[1].VectorZ);
        FinareRuntimeStateValue assets = states["Assets"].DefaultValue;
        Assert.Equal(FinareRuntimeStateValueKind.Map, assets.Kind);
        Assert.Equal(2, assets.ContainerValues!.Count);
        Assert.Equal(FinareRuntimeStateValueKind.Name, assets.ContainerValues[0].Kind);
        Assert.Equal(FinareRuntimeStateValueKind.SoftObject, assets.ContainerValues[1].Kind);

        payload[10] = 3;
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            FinareRuntimeDeclarationBinary.Decode(payload));
        Assert.Contains("requires declaration minor version 4", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BindingAndDependencyPayloads_AreSortedAndStrictlyRoundTrip()
    {
        var bindings = new FinareRuntimeBindingManifest(
        [
            new("z-contract", Digest('b'), 3, 5),
            new("a-contract", Digest('a'), 1, 4),
        ]);
        var dependencies = new FinareRuntimeDependencyManifest(
        [
            new(
                FinareRuntimeDependencyKind.NativeLibrary,
                "hostfxr",
                "10.0.0",
                Digest('d'),
                Optional: false),
            new(
                FinareRuntimeDependencyKind.ManagedAssembly,
                "Finare.Unreal",
                "1.0.0.0",
                Digest('c'),
                Optional: false),
        ]);

        FinareRuntimeBindingManifest decodedBindings =
            FinareRuntimeBindingManifestBinary.Decode(
                FinareRuntimeBindingManifestBinary.Encode(bindings));
        FinareRuntimeDependencyManifest decodedDependencies =
            FinareRuntimeDependencyManifestBinary.Decode(
                FinareRuntimeDependencyManifestBinary.Encode(dependencies));

        Assert.Equal(
            ["a-contract", "z-contract"],
            decodedBindings.Requirements.Select(static requirement => requirement.ContractId));
        Assert.Equal(
            [
                FinareRuntimeDependencyKind.ManagedAssembly,
                FinareRuntimeDependencyKind.NativeLibrary,
            ],
            decodedDependencies.Dependencies.Select(static dependency => dependency.Kind));
    }

    [Fact]
    public void EmitCommand_WritesVerifiedChunkBesideLooseMigrationArtifacts()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "Finare.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "ChunkPolicy.cs");
            string outputPath = Path.Combine(directory, "out");
            File.WriteAllText(sourcePath, Source);

            int exitCode = Finare.Cli.Program.Main(
                ["emit", sourcePath, "--out", outputPath, "--assembly", "ChunkPolicy"]);

            Assert.Equal(0, exitCode);
            string chunkPath = Path.Combine(outputPath, "ChunkPolicy.uschunk");
            Assert.True(File.Exists(chunkPath));
            FinareChunk chunk = FinareProgramChunk.Read(File.ReadAllBytes(chunkPath));
            FinareRuntimeManifest manifest = FinareRuntimeManifestBinary.Decode(
                chunk.GetRequiredSection(
                    FinareChunkSectionKind.RuntimeManifest).Data.Span);
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
    public void ChunkProfilerMeasuresCopiesDecodeLoadAndEntryMethodJitPreparation()
    {
        CSharpProgramChunkEmitResult result = new CSharpProgramChunkCompiler().Compile(EmitImage());
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string chunkPath = Path.Combine(directory, "ChunkPolicy.uschunk");
        string reportPath = Path.Combine(directory, "profile.json");
        try
        {
            File.WriteAllBytes(chunkPath, result.Content);

            FinareCompiledChunkLoadProfileReport report =
                new FinareCompiledChunkLoadProfiler().Profile(
                    chunkPath,
                    trialCount: 3,
                    prepareEntryMethods: true);

            Assert.Equal(
                FinareCompiledChunkLoadProfileReport.CurrentSchemaVersion,
                report.SchemaVersion);
            Assert.Equal(result.Chunk.ContentDigest, report.ContentDigest);
            Assert.Equal(3, report.Trials.Count);
            Assert.True(report.Trials[0].ProcessFirstLoad);
            Assert.All(report.Trials.Skip(1), static trial =>
                Assert.False(trial.ProcessFirstLoad));
            Assert.All(report.Trials, trial =>
            {
                Assert.Equal(result.Content.Length,
                    trial.Phases.Single(static phase =>
                        phase.Phase == "inputMaterialization").BytesCopied +
                    trial.Phases.Single(static phase =>
                        phase.Phase == "storageMaterialization").BytesCopied);
                Assert.Equal(
                    result.Chunk.GetRequiredSection(
                        FinareChunkSectionKind.ManagedAssembly).Data.Length,
                    trial.Phases.Single(static phase =>
                        phase.Phase == "managedAssemblyMaterialization").BytesCopied);
                Assert.True(trial.Phases.Single(static phase =>
                    phase.Phase == "entryMethodJitPreparation").WorkItems > 0);
                Assert.True(trial.TotalLoadNanoseconds >= 0);
                Assert.True(trial.CurrentThreadAllocatedBytes > 0);
            });
            string json = FinareCompiledChunkLoadProfileJson.Serialize(report);
            Assert.Equal(
                json,
                FinareCompiledChunkLoadProfileJson.Serialize(
                    FinareCompiledChunkLoadProfileJson.Deserialize(json)));

            int exitCode = Finare.Cli.Program.Main([
                "chunk-profile",
                chunkPath,
                "--out",
                reportPath,
                "--trials",
                "2",
            ]);
            Assert.Equal(0, exitCode);
            Assert.Equal(
                2,
                FinareCompiledChunkLoadProfileJson.Deserialize(
                    File.ReadAllText(reportPath)).TrialCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
        Assert.Contains("Finare.Core", missing.Message, StringComparison.Ordinal);

        using CoreClrCompiledChunkSession session = new CoreClrCompiledChunkHost().Load(
            result.Content,
            [runtimeReference]);
        Assert.Single(session.Dependencies.Dependencies);
    }

    [Fact]
    public void CoreClrHost_ReusesVerifiedDependencySnapshotsAcrossCollectibleContexts()
    {
        string runtimeReference = typeof(SemanticId).Assembly.Location;
        CSharpProgramChunkEmitResult result = new CSharpProgramChunkCompiler().Compile(
            EmitImage(),
            new CSharpProgramChunkOptions(RuntimeReferencePaths: [runtimeReference]));
        var cache = new FinareManagedDependencyCache();
        var host = new CoreClrCompiledChunkHost(cache);

        CoreClrCompiledChunkLoadResult first = host.LoadProfiled(
            result.Content,
            [runtimeReference]);
        first.Session.Dispose();
        CoreClrCompiledChunkLoadResult second = host.LoadProfiled(
            result.Content,
            [runtimeReference]);
        second.Session.Dispose();

        Assert.Equal(0, first.Profile.DependencyCacheHitCount);
        Assert.Equal(1, first.Profile.DependencyCacheMissCount);
        Assert.Equal(new FileInfo(runtimeReference).Length,
            first.Profile.DependencySnapshotCopyBytes);
        Assert.Equal(1, second.Profile.DependencyCacheHitCount);
        Assert.Equal(0, second.Profile.DependencyCacheMissCount);
        Assert.Equal(0, second.Profile.DependencySnapshotCopyBytes);
        FinareManagedDependencyCacheSnapshot snapshot = cache.GetSnapshot();
        Assert.Equal(1, snapshot.EntryCount);
        Assert.Equal(1, snapshot.HitCount);
        Assert.Equal(1, snapshot.MissCount);
        Assert.True(snapshot.CachedBytes > 0);
    }

    [Fact]
    public void DependencyCache_UsesImmutableContentThenRejectsChangedDiskEvidence()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string referencePath = Path.Combine(directory, "Finare.Core.dll");
        File.Copy(typeof(SemanticId).Assembly.Location, referencePath);
        try
        {
            CSharpProgramChunkEmitResult result = new CSharpProgramChunkCompiler().Compile(
                EmitImage(),
                new CSharpProgramChunkOptions(RuntimeReferencePaths: [referencePath]));
            var cache = new FinareManagedDependencyCache();
            var host = new CoreClrCompiledChunkHost(cache);
            using (host.Load(result.Content, [referencePath]))
            {
            }

            using (var stream = new FileStream(referencePath, FileMode.Append, FileAccess.Write))
            {
                stream.WriteByte(0);
            }

            using (host.Load(result.Content, [referencePath]))
            {
            }
            Assert.Equal(1, cache.GetSnapshot().HitCount);

            cache.ClearCachedContent();
            InvalidDataException changed = Assert.Throws<InvalidDataException>(() =>
                host.Load(result.Content, [referencePath]));
            Assert.Contains("version or digest differs", changed.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DependencyCache_IsSingleFlightAndBounded()
    {
        string coreReference = typeof(SemanticId).Assembly.Location;
        CSharpProgramChunkEmitResult singleDependency =
            new CSharpProgramChunkCompiler().Compile(
                EmitImage(),
                new CSharpProgramChunkOptions(RuntimeReferencePaths: [coreReference]));
        var concurrentCache = new FinareManagedDependencyCache();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using CoreClrCompiledChunkSession session =
                new CoreClrCompiledChunkHost(concurrentCache).Load(
                    singleDependency.Content,
                    [coreReference]);
        })));

        FinareManagedDependencyCacheSnapshot concurrentSnapshot =
            concurrentCache.GetSnapshot();
        Assert.Equal(1, concurrentSnapshot.MissCount);
        Assert.Equal(7, concurrentSnapshot.HitCount);

        string csharpReference = typeof(CSharpProgramEmitter).Assembly.Location;
        CSharpProgramChunkEmitResult twoDependencies =
            new CSharpProgramChunkCompiler().Compile(
                EmitImage(),
                new CSharpProgramChunkOptions(
                    RuntimeReferencePaths: [coreReference, csharpReference]));
        var boundedCache = new FinareManagedDependencyCache(maximumEntryCount: 1);
        using (new CoreClrCompiledChunkHost(boundedCache).Load(
            twoDependencies.Content,
            [coreReference, csharpReference]))
        {
        }

        FinareManagedDependencyCacheSnapshot boundedSnapshot =
            boundedCache.GetSnapshot();
        Assert.Equal(1, boundedSnapshot.EntryCount);
        Assert.Equal(2, boundedSnapshot.MissCount);
        Assert.Equal(1, boundedSnapshot.EvictionCount);
    }

    [Fact]
    public void UnrealWorldLoader_LoadsDeclaredScriptTypeFromChunkPath()
    {
        const string source = """
            using Finare.Unreal;

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
                "Finare.Unreal.ChunkWorldTest",
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
            var cache = new FinareManagedDependencyCache();

            string firstLoadedType = UnrealWorldManagedEntryPoint.ValidateCompiledProgramForTests(
                path,
                "Tests.Chunks.ChunkWorldScript",
                cache);
            string secondLoadedType = UnrealWorldManagedEntryPoint.ValidateCompiledProgramForTests(
                path,
                "Tests.Chunks.ChunkWorldScript",
                cache);

            Assert.Equal("Tests.Chunks.ChunkWorldScript", firstLoadedType);
            Assert.Equal(firstLoadedType, secondLoadedType);
            FinareManagedDependencyCacheSnapshot snapshot = cache.GetSnapshot();
            Assert.Equal(1, snapshot.MissCount);
            Assert.Equal(1, snapshot.HitCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FirstTickProfileCommand_UsesTheGenerationPrewarmOperation()
    {
        const string source = """
            using System.Runtime.CompilerServices;
            using Finare.Unreal;

            namespace Tests.Chunks;

            public sealed class FirstTickScript : IUnrealManagedScript
            {
                public void OnStart(UnrealScriptContext context) { }

                [MethodImpl(MethodImplOptions.NoInlining)]
                public void OnTick(float deltaSeconds) => Step((int)deltaSeconds);

                public void OnStop() { }

                [MethodImpl(MethodImplOptions.NoInlining)]
                private static int Step(int value) => value + 1;
            }
            """;
        string unrealAssembly = typeof(IUnrealManagedScript).Assembly.Location;
        CSharpProgramEmitResult emitted = new CSharpProgramEmitter().Emit(
            new CSharpCompilationRequest(
                "Finare.FirstTickProfileTest",
                [new CSharpSourceDocument("FirstTickScript.cs", source)],
                referencePaths: [unrealAssembly],
                runtimeReferencePaths: [unrealAssembly]));
        Assert.True(emitted.Succeeded, string.Join(Environment.NewLine, emitted.Diagnostics));
        CSharpProgramChunkEmitResult chunk = new CSharpProgramChunkCompiler().Compile(
            Assert.IsType<CSharpProgramImage>(emitted.Image),
            new CSharpProgramChunkOptions(
                RuntimeReferencePaths: [unrealAssembly],
                EntryTypeNames: ["Tests.Chunks.FirstTickScript"]));
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string chunkPath = Path.Combine(directory, "program.uschunk");
            string reportPath = Path.Combine(directory, "profile.json");
            File.WriteAllBytes(chunkPath, chunk.Content);

            int exitCode = Finare.Cli.Program.Main([
                "chunk-first-tick-profile",
                chunkPath,
                "--type",
                "Tests.Chunks.FirstTickScript",
                "--out",
                reportPath,
                "--frame-gap-ms",
                "16",
                "--reference",
                unrealAssembly,
            ]);

            Assert.Equal(0, exitCode);
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
            JsonElement root = report.RootElement;
            Assert.Equal(
                "unifiedscript.generation-prewarm-trial/v0.1",
                root.GetProperty("schemaVersion").GetString());
            Assert.Equal("background", root.GetProperty("variant").GetString());
            Assert.True(root.GetProperty("scheduled").GetBoolean());
            Assert.True(
                root.GetProperty("prewarmAtFirstTick")
                    .GetProperty("firstTickObserved")
                    .GetBoolean());
            Assert.DoesNotContain(
                directory,
                File.ReadAllText(reportPath),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static CSharpProgramImage EmitImage(
        string source = Source,
        IReadOnlyList<string>? additionalReferences = null)
    {
        CSharpProgramEmitResult result = new CSharpProgramEmitter().Emit(
            new CSharpCompilationRequest(
                "Finare.ChunkTests",
                [new CSharpSourceDocument("ChunkPolicy.cs", source)],
                additionalReferences));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        return Assert.IsType<CSharpProgramImage>(result.Image);
    }

    private static string Digest(char digit) => "sha256:" + new string(digit, 64);
}
