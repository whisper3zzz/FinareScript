using Finare.Core;
using Finare.CSharp;
using Finare.Runtime;

namespace Finare.Tests;

[Trait("Category", "Runtime")]
public sealed class InheritedStateContractTests
{
    [Fact]
    public void JsonAndBinaryRoundTripPreserveSharedOwnerIdsAndProjectionDefaults()
    {
        (CSharpCompilationRequest request, CSharpAnalysisResult analysis) = CreateProjectionRequest();
        CSharpProgramEmitResult emitted = new CSharpProgramEmitter().Emit(request);

        Assert.True(emitted.Succeeded, string.Join(Environment.NewLine, emitted.Diagnostics));
        CSharpProgramImage image = Assert.IsType<CSharpProgramImage>(emitted.Image);
        ScriptAssetDeclaration shared = image.ScriptAssets.Assets.Single(
            static asset => asset.QualifiedName == "Game.SharedState");
        SemanticId stateId = shared.States.Single().Id;

        ScriptAssetDocument jsonRoundTrip = ScriptAssetJson.Deserialize(
            ScriptAssetJson.Serialize(image.ScriptAssets));
        AssertProjectionValues(jsonRoundTrip.Assets, stateId);

        CSharpProgramChunkEmitResult chunk = new CSharpProgramChunkCompiler().Compile(image);
        FinareRuntimeDeclarationDocument binaryRoundTrip =
            FinareRuntimeDeclarationBinary.Decode(
                chunk.Chunk.GetRequiredSection(FinareChunkSectionKind.DeclarationData).Data.Span);
        Assert.Equal(image.ScriptAssets.SourceCompilationId, binaryRoundTrip.SourceCompilationId);
        AssertProjectionValues(binaryRoundTrip.Assets, stateId);

        IdentitySidecarDocument originalStateSidecar = Assert.Single(
            analysis.IdentitySidecars,
            static sidecar => sidecar.SourcePath == "Game/SharedState.cs");
        SemanticId sidecarStateId = Assert.Single(
            originalStateSidecar.Entries,
            static entry => entry.Kind == "state").Id;
        Assert.Equal(stateId, sidecarStateId);

        const string renamedStateSource = """
            using Finare;

            namespace Game;

            public partial class SharedState
            {
                public int RenamedSeed { get; set; } = 3;
            }
            """;
        CSharpSourceDocument[] reorderedSources =
        [
            new(
                "Game/SharedOwner.cs",
                SharedOwnerSource,
                analysis.IdentitySidecars.Single(static sidecar =>
                    sidecar.SourcePath == "Game/SharedOwner.cs")),
            new(
                "Game/StateLeaves.cs",
                StateLeavesSource.Replace("base.Seed", "base.RenamedSeed", StringComparison.Ordinal),
                analysis.IdentitySidecars.Single(static sidecar =>
                    sidecar.SourcePath == "Game/StateLeaves.cs")),
            new(
                "Game/SharedState.cs",
                renamedStateSource,
                originalStateSidecar),
        ];
        CSharpAnalysisResult renamed = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "InheritedStateRoundTrip",
                reorderedSources));

        Assert.True(renamed.Succeeded, string.Join(Environment.NewLine, renamed.Diagnostics));
        ScriptStateDeclaration renamedOwnerState = renamed.ScriptAssets!.Assets.Single(
            static asset => asset.QualifiedName == "Game.SharedState").States.Single();
        Assert.Equal("RenamedSeed", renamedOwnerState.Name);
        Assert.Equal(stateId, renamedOwnerState.Id);
        Assert.Equal(
            stateId,
            renamed.ScriptAssets.Assets.Single(static asset => asset.QualifiedName == "Game.FollowerState")
                .States.Single().Id);
        Assert.Equal(
            stateId,
            renamed.ScriptAssets.Assets.Single(static asset => asset.QualifiedName == "Game.OverrideState")
                .States.Single().Id);
        Assert.Equal(
            stateId,
            Assert.Single(renamed.IdentitySidecars,
                static sidecar => sidecar.SourcePath == "Game/SharedState.cs")
                .Entries.Single(static entry => entry.Kind == "state").Id);
    }

    private static void AssertProjectionValues(
        IReadOnlyList<ScriptAssetDeclaration> assets,
        SemanticId stateId)
    {
        ScriptAssetDeclaration shared = assets.Single(
            static asset => asset.QualifiedName == "Game.SharedState");
        ScriptAssetDeclaration follower = assets.Single(
            static asset => asset.QualifiedName == "Game.FollowerState");
        ScriptAssetDeclaration overridden = assets.Single(
            static asset => asset.QualifiedName == "Game.OverrideState");
        Assert.Equal(stateId, shared.States.Single().Id);
        Assert.Equal(stateId, follower.States.Single().Id);
        Assert.Equal(stateId, overridden.States.Single().Id);
        Assert.Equal(3, shared.States.Single().DefaultValue.GetInt32());
        Assert.Equal(3, follower.States.Single().DefaultValue.GetInt32());
        Assert.Equal(9, overridden.States.Single().DefaultValue.GetInt32());
    }

    private static void AssertProjectionValues(
        IReadOnlyList<FinareRuntimeAssetDeclaration> assets,
        SemanticId stateId)
    {
        FinareRuntimeAssetDeclaration shared = assets.Single(
            static asset => asset.QualifiedName == "Game.SharedState");
        FinareRuntimeAssetDeclaration follower = assets.Single(
            static asset => asset.QualifiedName == "Game.FollowerState");
        FinareRuntimeAssetDeclaration overridden = assets.Single(
            static asset => asset.QualifiedName == "Game.OverrideState");
        Assert.Equal(stateId, shared.States.Single().Id);
        Assert.Equal(stateId, follower.States.Single().Id);
        Assert.Equal(stateId, overridden.States.Single().Id);
        Assert.Equal(3, shared.States.Single().DefaultValue.SignedIntegerValue);
        Assert.Equal(3, follower.States.Single().DefaultValue.SignedIntegerValue);
        Assert.Equal(9, overridden.States.Single().DefaultValue.SignedIntegerValue);
    }

    private static (CSharpCompilationRequest Request, CSharpAnalysisResult Analysis)
        CreateProjectionRequest()
    {
        CSharpSourceDocument[] initialSources =
        [
            new(
                "Game/StateLeaves.cs",
                StateLeavesSource,
                IdentitySidecarDocument.CreateEmpty("Game/StateLeaves.cs")),
            new(
                "Game/SharedState.cs",
                SharedStateSource,
                IdentitySidecarDocument.CreateEmpty("Game/SharedState.cs")),
            new(
                "Game/SharedOwner.cs",
                SharedOwnerSource,
                IdentitySidecarDocument.CreateEmpty("Game/SharedOwner.cs")),
        ];
        CSharpCompilationRequest initial = new(
            "InheritedStateRoundTrip",
            initialSources);
        CSharpAnalysisResult initialized = new CSharpSemanticPipeline().Analyze(initial);
        Assert.True(initialized.Succeeded, string.Join(Environment.NewLine, initialized.Diagnostics));
        CSharpCompilationRequest stable = new(
            "InheritedStateRoundTrip",
            initialSources.Select(source => new CSharpSourceDocument(
                source.LogicalPath,
                source.Source,
                initialized.IdentitySidecars.Single(sidecar =>
                    sidecar.SourcePath == source.LogicalPath))).ToArray());
        return (stable, new CSharpSemanticPipeline().Analyze(stable));
    }

    private const string SharedOwnerSource = """
        using Finare;

        namespace Game;

        [ScriptAsset("Game/SharedState")]
        public partial class SharedState
        {
        }
        """;

    private const string SharedStateSource = """
        using Finare;

        namespace Game;

        public partial class SharedState
        {
            public int Seed { get; set; } = 3;
        }
        """;

    private const string StateLeavesSource = """
        using Finare;

        namespace Game;

        [ScriptAsset("Game/FollowerState")]
        public sealed class FollowerState : SharedState
        {
        }

        [ScriptAsset("Game/OverrideState")]
        public sealed class OverrideState : SharedState
        {
            public OverrideState()
            {
                base.Seed = 9;
            }
        }
        """;
}
