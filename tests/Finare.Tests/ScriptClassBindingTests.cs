using System.Text.Json;
using Finare.Core;
using Finare.CSharp;
using Finare.Runtime;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Authoring")]
public sealed class ScriptClassBindingTests
{
    [Fact]
    public void CSharpClassBindingRoundTripsThroughJsonAndRuntimeDeclaration()
    {
        const string primary = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Game/Bound")]
            [UnrealScriptClass(
                UnrealScriptClassKind.Actor,
                BaseClassPath = "/Script/Engine.Actor",
                GeneratedClassPath = "/Game/Tests/BP_Bound.BP_Bound_C")]
            public partial class Bound
            {
                public int Count { get; set; } = 7;

                [Visual]
                public int Read()
                {
                    return Count;
                }
            }
            """;
        const string secondary = """
            using Finare;

            public partial class Bound
            {
                public int Other { get; set; } = 3;
            }
            """;

        var request = new CSharpCompilationRequest(
            "ClassBindingFixture",
            [
                new CSharpSourceDocument("Game/Bound.cs", primary),
                new CSharpSourceDocument("Game/Bound.Part.cs", secondary),
            ],
            [typeof(UnrealObjectScript).Assembly.Location]);
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        ScriptAssetDeclaration asset = Assert.Single(result.ScriptAssets!.Assets);
        ScriptClassBindingDeclaration binding = Assert.IsType<ScriptClassBindingDeclaration>(asset.ClassBinding);
        Assert.Equal("Actor", binding.Kind);
        Assert.Equal("/Script/Engine.Actor", binding.BaseClassPath);
        Assert.Equal("/Game/Tests/BP_Bound.BP_Bound_C", binding.GeneratedClassPath);
        Assert.Contains(asset.States, state => state.Name == "Count");
        Assert.Contains(asset.States, state => state.Name == "Other");
        Assert.Contains(asset.Functions, function => function.Name == "Read");

        string json = ScriptAssetJson.Serialize(result.ScriptAssets, writeIndented: false);
        Assert.Contains(ScriptAssetDocument.ClassBindingSchemaVersion, json, StringComparison.Ordinal);
        ScriptAssetDocument jsonRoundTrip = ScriptAssetJson.Deserialize(json);
        Assert.Equal(binding, Assert.Single(jsonRoundTrip.Assets).ClassBinding);

        FinareRuntimeDeclarationDocument runtime =
            FinareRuntimeDeclarationBinary.FromScriptAssets(result.ScriptAssets);
        byte[] encoded = FinareRuntimeDeclarationBinary.Encode(runtime);
        Assert.Equal(5, BitConverter.ToUInt16(encoded, 10));
        FinareRuntimeDeclarationDocument decoded =
            FinareRuntimeDeclarationBinary.Decode(encoded);
        Assert.Equal(binding, Assert.Single(decoded.Assets).ClassBinding);
        Assert.Equal(
            asset.States.Select(static state => state.Id),
            Assert.Single(decoded.Assets).States.Select(static state => state.Id));

        Assert.Throws<InvalidDataException>(() =>
            FinareRuntimeDeclarationBinary.Decode(encoded[..^1]));
    }

    [Fact]
    public void ExplicitBasePathChangesDoNotChangePersistentDeclarationIds()
    {
        const string template = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Game/StableBound")]
            [UnrealScriptClass(
                UnrealScriptClassKind.Actor,
                BaseClassPath = "__BASE_CLASS_PATH__",
                GeneratedClassPath = "/Game/Tests/BP_StableBound.BP_StableBound_C")]
            public sealed class StableBound
            {
                public int Count { get; set; } = 7;

                [Visual]
                public int Read()
                {
                    return Count;
                }
            }
            """;
        CSharpAnalysisResult first = Analyze(template.Replace(
            "__BASE_CLASS_PATH__",
            "/Script/Engine.Actor",
            StringComparison.Ordinal));
        CSharpAnalysisResult changed = Analyze(template.Replace(
            "__BASE_CLASS_PATH__",
            "/Script/Engine.Object",
            StringComparison.Ordinal));

        Assert.True(first.Succeeded, string.Join(Environment.NewLine, first.Diagnostics));
        Assert.True(changed.Succeeded, string.Join(Environment.NewLine, changed.Diagnostics));
        ScriptAssetDeclaration firstAsset = Assert.Single(first.ScriptAssets!.Assets);
        ScriptAssetDeclaration changedAsset = Assert.Single(changed.ScriptAssets!.Assets);
        Assert.Equal(firstAsset.Id, changedAsset.Id);
        Assert.Equal(
            firstAsset.States.Select(static state => state.Id),
            changedAsset.States.Select(static state => state.Id));
        Assert.Equal(
            firstAsset.Functions.Select(static function => function.Id),
            changedAsset.Functions.Select(static function => function.Id));
        Assert.Equal("/Script/Engine.Object", changedAsset.ClassBinding!.BaseClassPath);
    }

    [Fact]
    public void ClassBindingWithNoExplicitPathsKeepsNullPathSemantics()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Game/UnpinnedBinding")]
            [UnrealScriptClass(UnrealScriptClassKind.Object)]
            public sealed class UnpinnedBinding
            {
                public int Count { get; set; } = 1;
            }
            """;
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "Game/UnpinnedBinding.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        ScriptClassBindingDeclaration binding =
            Assert.Single(result.ScriptAssets!.Assets).ClassBinding!;
        Assert.Equal("Object", binding.Kind);
        Assert.Null(binding.BaseClassPath);
        Assert.Null(binding.GeneratedClassPath);
        ScriptAssetDocument roundTrip = ScriptAssetJson.Deserialize(
            ScriptAssetJson.Serialize(result.ScriptAssets, writeIndented: false));
        Assert.Null(Assert.Single(roundTrip.Assets).ClassBinding!.GeneratedClassPath);
    }

    [Fact]
    public void CoreTreatsBindingKindAndPathsAsOpaqueText()
    {
        ScriptAssetDeclaration asset = new(
            SemanticId.FromKey("opaque-binding-asset"),
            "Game.OpaqueBinding",
            "Game/OpaqueBinding",
            SourceSpan.Unknown("OpaqueBinding.cs"),
            [],
            [],
            ClassBinding: new ScriptClassBindingDeclaration(
                "VendorSpecificKind",
                "opaque-base-token",
                "opaque-generated-token"));
        ScriptAssetDocument document = ScriptAssetDocument.Create(
            SemanticId.FromKey("opaque-binding-compilation"),
            SemanticId.FromKey("opaque-binding-configuration"),
            [asset]);

        ScriptAssetDocument jsonRoundTrip = ScriptAssetJson.Deserialize(
            ScriptAssetJson.Serialize(document, writeIndented: false));
        Assert.Equal(asset.ClassBinding, Assert.Single(jsonRoundTrip.Assets).ClassBinding);
        FinareRuntimeDeclarationDocument runtime =
            FinareRuntimeDeclarationBinary.FromScriptAssets(jsonRoundTrip);
        FinareRuntimeDeclarationDocument binaryRoundTrip =
            FinareRuntimeDeclarationBinary.Decode(
                FinareRuntimeDeclarationBinary.Encode(runtime));
        Assert.Equal(asset.ClassBinding, Assert.Single(binaryRoundTrip.Assets).ClassBinding);
    }

    [Theory]
    [InlineData("(UnrealScriptClassKind)99")]
    [InlineData("UnrealScriptClassKind.Actor, GeneratedClassPath = \" \"")]
    [InlineData("UnrealScriptClassKind.Actor, BaseClassPath = \"Engine.Actor\"")]
    public void InvalidClassBindingMetadataBlocksAssetPublication(string constructor)
    {
        string source = $$"""
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Game/InvalidBinding")]
            [UnrealScriptClass({{constructor}})]
            public sealed class InvalidBinding
            {
                public int Count { get; set; } = 1;
            }
            """;
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "Game/InvalidBinding.cs");

        Assert.False(result.Succeeded);
        Assert.Null(result.ScriptAssets);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == DeclarationDiagnosticCodes.InvalidScriptClassBinding);
    }

    [Theory]
    [InlineData(ScriptAssetDocument.CurrentSchemaVersion)]
    [InlineData(ScriptAssetDocument.OverrideSchemaVersion)]
    public void ClassBindingCannotBeCarriedByLegacyJsonSchema(string legacySchemaVersion)
    {
        ScriptAssetDeclaration asset = new(
            SemanticId.FromKey("class-binding-legacy-asset"),
            "Game.LegacyBinding",
            "Game/LegacyBinding",
            SourceSpan.Unknown("LegacyBinding.cs"),
            [],
            [],
            ClassBinding: new ScriptClassBindingDeclaration(
                "Actor",
                "/Script/Engine.Actor"));
        ScriptAssetDocument legacy = new(
            legacySchemaVersion,
            SemanticId.FromKey("class-binding-legacy-compilation"),
            SemanticId.FromKey("class-binding-legacy-configuration"),
            [asset]);

        Assert.Throws<JsonException>(() => ScriptAssetJson.NormalizeAndValidate(legacy));
    }

    [Fact]
    public void BinaryClassBindingSlotSupportsMixedBindingAndLegacyAssets()
    {
        static JsonElement Number(long value) =>
            JsonDocument.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .RootElement.Clone();

        ScriptAssetDeclaration bound = new(
            SemanticId.FromKey("mixed-bound-asset"),
            "Game.MixedBound",
            "Game/MixedBound",
            SourceSpan.Unknown("MixedBound.cs"),
            [new ScriptStateDeclaration(
                SemanticId.FromKey("mixed-bound-state"),
                "BoundValue",
                "int",
                "System.Int32",
                ScriptStateTypeKind.Integer,
                Number(7),
                SourceSpan.Unknown("MixedBound.cs"),
                ScriptExposure.None)],
            [],
            LifecycleHooks: null,
            ClassBinding: new ScriptClassBindingDeclaration(
                "Actor",
                "/Script/Engine.Actor"));
        ScriptAssetDeclaration legacy = new(
            SemanticId.FromKey("mixed-legacy-asset"),
            "Game.MixedLegacy",
            "Game/MixedLegacy",
            SourceSpan.Unknown("MixedLegacy.cs"),
            [new ScriptStateDeclaration(
                SemanticId.FromKey("mixed-legacy-state"),
                "LegacyValue",
                "int",
                "System.Int32",
                ScriptStateTypeKind.Integer,
                Number(11),
                SourceSpan.Unknown("MixedLegacy.cs"),
                ScriptExposure.None)],
            []);
        ScriptAssetDocument document = ScriptAssetDocument.Create(
            SemanticId.FromKey("mixed-binding-compilation"),
            SemanticId.FromKey("mixed-binding-configuration"),
            [bound, legacy]);

        FinareRuntimeDeclarationDocument runtime =
            FinareRuntimeDeclarationBinary.FromScriptAssets(document);
        byte[] encoded = FinareRuntimeDeclarationBinary.Encode(runtime);
        Assert.Equal(5, BitConverter.ToUInt16(encoded, 10));

        FinareRuntimeDeclarationDocument decoded =
            FinareRuntimeDeclarationBinary.Decode(encoded);
        Assert.Equal(
            ["Game/MixedBound", "Game/MixedLegacy"],
            decoded.Assets.Select(static asset => asset.AssetPath).ToArray());
        FinareRuntimeAssetDeclaration decodedBound =
            Assert.Single(decoded.Assets, static asset => asset.AssetPath == "Game/MixedBound");
        FinareRuntimeAssetDeclaration decodedLegacy =
            Assert.Single(decoded.Assets, static asset => asset.AssetPath == "Game/MixedLegacy");
        Assert.Equal(bound.Id, decodedBound.Id);
        Assert.Equal(Assert.Single(bound.States).Id, Assert.Single(decodedBound.States).Id);
        Assert.Equal(bound.ClassBinding, decodedBound.ClassBinding);
        Assert.Equal(legacy.Id, decodedLegacy.Id);
        Assert.Equal(Assert.Single(legacy.States).Id, Assert.Single(decodedLegacy.States).Id);
        Assert.Null(decodedLegacy.ClassBinding);
    }

    [Fact]
    public void AssetsWithoutClassBindingRetainLegacyBinaryVersionAndDecode()
    {
        ScriptAssetDeclaration asset = new(
            SemanticId.FromKey("legacy-runtime-asset"),
            "Game.LegacyRuntime",
            "Game/LegacyRuntime",
            SourceSpan.Unknown("LegacyRuntime.cs"),
            [],
            []);
        ScriptAssetDocument document = ScriptAssetDocument.Create(
            SemanticId.FromKey("legacy-runtime-compilation"),
            SemanticId.FromKey("legacy-runtime-configuration"),
            [asset]);

        FinareRuntimeDeclarationDocument runtime =
            FinareRuntimeDeclarationBinary.FromScriptAssets(document);
        byte[] encoded = FinareRuntimeDeclarationBinary.Encode(runtime);

        Assert.Equal(1, BitConverter.ToUInt16(encoded, 10));
        FinareRuntimeDeclarationDocument decoded =
            FinareRuntimeDeclarationBinary.Decode(encoded);
        Assert.Null(Assert.Single(decoded.Assets).ClassBinding);
    }

    [Fact]
    public void BinaryClassBindingSlotRejectsInvalidPresenceAndUnsupportedMinor()
    {
        ScriptAssetDocument document = CreateOpaqueDocument();
        FinareRuntimeDeclarationDocument runtime =
            FinareRuntimeDeclarationBinary.FromScriptAssets(document);
        byte[] encoded = FinareRuntimeDeclarationBinary.Encode(runtime);
        int presenceOffset = FindClassBindingPresenceOffset(encoded);

        byte[] invalidPresence = encoded.ToArray();
        invalidPresence[presenceOffset] = 2;
        Assert.Throws<InvalidDataException>(() =>
            FinareRuntimeDeclarationBinary.Decode(invalidPresence));

        byte[] unsupportedMinor = encoded.ToArray();
        unsupportedMinor[10] = 6;
        unsupportedMinor[11] = 0;
        Assert.Throws<InvalidDataException>(() =>
            FinareRuntimeDeclarationBinary.Decode(unsupportedMinor));
    }

    [Fact]
    public void ClassBindingDoesNotPropagateFromAnAncestorAttribute()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [UnrealScriptClass(
                UnrealScriptClassKind.Actor,
                BaseClassPath = "/Script/FinareRuntime.FinareActor")]
            public class BoundBase
            {
                public int BaseValue { get; set; } = 1;
            }

            [ScriptAsset("Game/DerivedWithoutBinding")]
            public sealed class DerivedWithoutBinding : BoundBase
            {
                public int DerivedValue { get; set; } = 2;
            }
            """;
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "Game/DerivedWithoutBinding.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        ScriptAssetDeclaration derived = result.ScriptAssets!.Assets.Single(
            static asset => asset.QualifiedName == "DerivedWithoutBinding");
        Assert.Null(derived.ClassBinding);
    }

    [Fact]
    public void EmitsClassBindingJsonAndChunkFixtureForNativeConsumer()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Game/ClassBindingFixture")]
            [UnrealScriptClass(
                UnrealScriptClassKind.Actor,
                BaseClassPath = "/Script/FinareRuntime.FinareActor")]
            public sealed class ClassBindingFixture
            {
                public int Count { get; set; } = 3;
            }
            """;
        var request = new CSharpCompilationRequest(
            "ClassBindingFixture",
            [new CSharpSourceDocument("Game/ClassBindingFixture.cs", source)],
            [typeof(UnrealObjectScript).Assembly.Location]);
        CSharpProgramEmitResult emitted = new CSharpProgramEmitter().Emit(request);
        Assert.True(emitted.Succeeded, string.Join(Environment.NewLine, emitted.Diagnostics));
        ScriptAssetDocument assets = Assert.IsType<CSharpProgramImage>(emitted.Image).ScriptAssets;
        CSharpProgramChunkEmitResult chunk = new CSharpProgramChunkCompiler().Compile(
            Assert.IsType<CSharpProgramImage>(emitted.Image));

        string directory = Path.Combine(
            FindRepositoryRoot(),
            "artifacts",
            "m13-3c-4-class-composition",
            "class-binding");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "class-binding-fixture.asset.json"),
            ScriptAssetJson.Serialize(assets) + Environment.NewLine);
        File.WriteAllBytes(
            Path.Combine(directory, "class-binding-fixture.uschunk"),
            chunk.Content);
    }

    [Fact]
    public void EmitsNativePublicationWorldFixtureForIndependentManagedOwners()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Game/C4PublicationOwner")]
            [UnrealScriptClass(
                UnrealScriptClassKind.Actor,
                GeneratedClassPath = "/Game/Temp/C4PublicationOwner.C4PublicationOwner_C")]
            public sealed class C4PublicationOwner : UnrealActorScript
            {
                public int Count { get; set; } = 1;

                protected override void OnBeginPlay() => Count += 100;

                protected override void OnTick(float deltaSeconds) => Count += 1;
            }

            [ScriptAsset("Game/C4PublicationDescendant")]
            [UnrealScriptClass(
                UnrealScriptClassKind.Actor,
                GeneratedClassPath = "/Game/Temp/C4PublicationDescendant.C4PublicationDescendant_C")]
            public sealed class C4PublicationDescendant : UnrealActorScript
            {
                public int DescCount { get; set; } = 2;

                protected override void OnBeginPlay() => DescCount += 200;

                protected override void OnTick(float deltaSeconds) => DescCount += 2;
            }
            """;
        string unrealAssembly = typeof(UnrealActorScript).Assembly.Location;
        string coreAssembly = typeof(ScriptAssetAttribute).Assembly.Location;
        var request = new CSharpCompilationRequest(
            "C4PublicationWorldFixture",
            [new CSharpSourceDocument("Game/C4PublicationWorldFixture.cs", source)],
            referencePaths: [unrealAssembly, coreAssembly],
            runtimeReferencePaths: [unrealAssembly, coreAssembly]);
        CSharpProgramEmitResult emitted = new CSharpProgramEmitter().Emit(request);
        Assert.True(emitted.Succeeded, string.Join(Environment.NewLine, emitted.Diagnostics));
        CSharpProgramImage image = Assert.IsType<CSharpProgramImage>(emitted.Image);
        CSharpProgramChunkEmitResult chunk = new CSharpProgramChunkCompiler().Compile(
            image,
            new CSharpProgramChunkOptions(
                RuntimeReferencePaths: [unrealAssembly, coreAssembly],
                EntryTypeNames: ["C4PublicationOwner", "C4PublicationDescendant"]));

        string directory = Path.Combine(
            FindRepositoryRoot(),
            "artifacts",
            "finare-usability",
            "c4-publication-world-fixture");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "c4-publication-world-fixture.asset.json"),
            ScriptAssetJson.Serialize(image.ScriptAssets) + Environment.NewLine);
        File.WriteAllBytes(
            Path.Combine(directory, "c4-publication-world-fixture.uschunk"),
            chunk.Content);
    }

    private static CSharpAnalysisResult Analyze(string source) =>
        new CSharpSemanticPipeline().Analyze(
            source,
            "Game/StableBound.cs");

    private static ScriptAssetDocument CreateOpaqueDocument() => ScriptAssetDocument.Create(
        SemanticId.FromKey("opaque-binding-compilation"),
        SemanticId.FromKey("opaque-binding-configuration"),
        [new ScriptAssetDeclaration(
            SemanticId.FromKey("opaque-binding-asset"),
            "Game.OpaqueBinding",
            "Game/OpaqueBinding",
            SourceSpan.Unknown("OpaqueBinding.cs"),
            [],
            [],
            ClassBinding: new ScriptClassBindingDeclaration(
                "VendorSpecificKind",
                "opaque-base-token",
                "opaque-generated-token"))]);

    private static int FindClassBindingPresenceOffset(byte[] encoded)
    {
        int offset = 16;
        offset = SkipString(encoded, offset);
        offset = SkipString(encoded, offset);
        offset += 4; // asset count
        offset = SkipString(encoded, offset); // asset ID
        offset = SkipString(encoded, offset); // qualified name
        offset = SkipString(encoded, offset); // asset path
        return offset;
    }

    private static int SkipString(byte[] encoded, int offset)
    {
        int length = BitConverter.ToInt32(encoded, offset);
        return checked(offset + 4 + length);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "src",
                    "Finare.Core",
                    "Finare.Core.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
