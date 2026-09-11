using Finare.CSharp;
using Finare.Core;
using Finare.Core.Hir;
using Finare.Graph;
using Finare.Semantics;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Compiler")]
public sealed class MultiDocumentSemanticPipelineTests
{
    private const string ApiSource = """
        namespace Game;

        public static class GameplayApi
        {
            public static float Clamp(float value) => value;
        }
        """;

    private const string ControllerSource = """
        using Finare;

        namespace Game;

        public sealed class Controller
        {
            [Visual]
            public float Execute(float value)
            {
                float result = GameplayApi.Clamp(value);
                return result;
            }
        }
        """;

    [Fact]
    public void Analyze_BindsCallsAcrossSourceDocuments()
    {
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(CreateRequest(reverse: false));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(result.Hir!.Types).Methods);
        HirInvocationExpression invocation = Assert.IsType<HirInvocationExpression>(
            Assert.IsType<HirVariableDeclarationStatement>(method.Body.Statements[0]).Initializer);
        BoundNodeInfo binding = Assert.Single(
            result.Semantics!.Nodes,
            node => node.NodeId == invocation.Id);
        MethodDescriptor descriptor = Assert.Single(
            result.Semantics.Methods,
            candidate => candidate.Id == binding.InvokedMethodId);

        Assert.Equal("Game.GameplayApi.Clamp(System.Single)", descriptor.MetadataName);
        Assert.Equal(MethodEffects.ReadsState | MethodEffects.WritesState, descriptor.Effects);
        Assert.Equal("Game/Controller.cs", method.Span.FilePath);
    }

    [Fact]
    public void Analyze_LowersAllVisualMethodsFromPartialTypeShardsAndPreservesOwnersAndSpans()
    {
        const string primarySource = """
            using Finare;
            using Finare.Unreal;

            namespace Game;

            [ScriptAsset("Game/SplitController", ClassId = "us_00000000000000000000000000000021")]
            [UnrealScriptClass(UnrealScriptClassKind.Object)]
            public sealed partial class SplitController
            {
                [Expose]
                public int Seed { get; set; } = 3;

                [Visual]
                public int ReadSeed()
                {
                    return Seed;
                }
            }
            """;
        const string methodsSource = """
            using Finare;

            namespace Game;

            public sealed partial class SplitController
            {
                [Visual]
                public int Forward()
                {
                    return ReadSeed();
                }
            }
            """;
        CSharpSourceDocument[] sources =
        [
            new(
                "Game/SplitController.cs",
                primarySource,
                IdentitySidecarDocument.CreateEmpty("Game/SplitController.cs")),
            new(
                "Game/SplitController.Methods.cs",
                methodsSource,
                IdentitySidecarDocument.CreateEmpty("Game/SplitController.Methods.cs")),
        ];
        CSharpCompilationRequest request = new(
            "Finare.MultiDocument.Partial",
            sources,
            [typeof(UnrealObjectScript).Assembly.Location]);

        CSharpSemanticPipeline pipeline = new();
        CSharpAnalysisResult first = pipeline.Analyze(request);

        Assert.True(first.Succeeded, string.Join(Environment.NewLine, first.Diagnostics));
        ScriptAssetDeclaration asset = Assert.Single(first.ScriptAssets!.Assets);
        Assert.Equal("Game.SplitController", asset.QualifiedName);
        Assert.Equal(["Forward", "ReadSeed"], asset.Functions.Select(static function => function.Name).Order());
        Assert.Equal(3, first.DeclarationOwners.Count);
        Assert.All(asset.Functions, function => Assert.Equal(asset.Id, first.DeclarationOwners[function.Id]));

        IReadOnlyList<HirMethodDeclaration> hirMethods = first.Hir!.Types
            .SelectMany(static type => type.Methods)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["Forward", "ReadSeed"], hirMethods.Select(static method => method.Name));
        Assert.Equal("Game/SplitController.Methods.cs", hirMethods[0].Span.FilePath);
        Assert.Equal("Game/SplitController.cs", hirMethods[1].Span.FilePath);

        GraphDocument graph = new HirGraphProjector().Project(
            first.Hir,
            first.Semantics,
            first.DeclarationReferences);
        Assert.Equal(
            hirMethods.Select(static method => method.Id.Value).Order(StringComparer.Ordinal),
            graph.Functions.Select(static function => function.Id.Value).Order(StringComparer.Ordinal));
        Assert.Contains(
            graph.Functions.SelectMany(static function => function.Nodes),
            node => node.Span.FilePath == "Game/SplitController.Methods.cs");
        Assert.Contains(
            graph.Functions.SelectMany(static function => function.Nodes),
            node => node.Span.FilePath == "Game/SplitController.cs");
        GraphDocument graphRoundTrip = GraphJson.Deserialize(GraphJson.Serialize(graph, writeIndented: false));
        Assert.Equal(
            graph.Functions.Select(static function => function.Id),
            graphRoundTrip.Functions.Select(static function => function.Id));
        Assert.Equal(
            graph.Functions.SelectMany(static function => function.Nodes)
                .Select(static node => node.Span.FilePath)
                .Order(StringComparer.Ordinal),
            graphRoundTrip.Functions.SelectMany(static function => function.Nodes)
                .Select(static node => node.Span.FilePath)
                .Order(StringComparer.Ordinal));

        IReadOnlyDictionary<string, IdentitySidecarDocument> sidecars = first.IdentitySidecars
            .ToDictionary(static sidecar => sidecar.SourcePath, StringComparer.Ordinal);
        Assert.Equal(2, sidecars.Count);
        CSharpSourceDocument[] withSidecars = sources
            .Select(source => new CSharpSourceDocument(
                source.LogicalPath,
                source.Source,
                sidecars[source.LogicalPath]))
            .Reverse()
            .ToArray();
        CSharpAnalysisResult reapplied = pipeline.Analyze(new CSharpCompilationRequest(
            request.AssemblyName,
            withSidecars,
            request.ReferencePaths));

        Assert.True(reapplied.Succeeded, string.Join(Environment.NewLine, reapplied.Diagnostics));
        ScriptAssetDeclaration reappliedAsset = Assert.Single(reapplied.ScriptAssets!.Assets);
        Assert.Equal(
            asset.Functions.ToDictionary(static function => function.Name, static function => function.Id),
            reappliedAsset.Functions.ToDictionary(static function => function.Name, static function => function.Id));
        Assert.Equal(
            hirMethods.Select(static method => (method.Name, method.Id, method.Span.FilePath)),
            reapplied.Hir!.Types
                .SelectMany(static type => type.Methods)
                .OrderBy(static method => method.Name, StringComparer.Ordinal)
                .Select(static method => (method.Name, method.Id, method.Span.FilePath)));
        Assert.Equal(
            first.IdentitySidecars
                .OrderBy(static sidecar => sidecar.SourcePath, StringComparer.Ordinal)
                .Select(static sidecar => IdentitySidecarJson.Serialize(sidecar)),
            reapplied.IdentitySidecars
                .OrderBy(static sidecar => sidecar.SourcePath, StringComparer.Ordinal)
                .Select(static sidecar => IdentitySidecarJson.Serialize(sidecar)));
    }

    [Fact]
    public void Analyze_IsDeterministicRegardlessOfRequestSourceOrder()
    {
        CSharpAnalysisResult forward = new CSharpSemanticPipeline().Analyze(CreateRequest(reverse: false));
        CSharpAnalysisResult reverse = new CSharpSemanticPipeline().Analyze(CreateRequest(reverse: true));

        Assert.Equal(forward.Hir!.Id, reverse.Hir!.Id);
        Assert.Equal(forward.Semantics!.ConfigurationId, reverse.Semantics!.ConfigurationId);
        Assert.Equal(
            SemanticJson.Serialize(forward.Semantics),
            SemanticJson.Serialize(reverse.Semantics));
        Assert.Equal(
            GraphJson.Serialize(new HirGraphProjector().Project(forward.Hir, forward.Semantics)),
            GraphJson.Serialize(new HirGraphProjector().Project(reverse.Hir, reverse.Semantics)));
    }

    [Fact]
    public void Request_RejectsDuplicateLogicalPaths()
    {
        var sources = new[]
        {
            new CSharpSourceDocument("Duplicate.cs", ApiSource),
            new CSharpSourceDocument("Duplicate.cs", ControllerSource),
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new CSharpCompilationRequest("Duplicate.Tests", sources));

        Assert.Contains("Duplicate logical source path", exception.Message);
    }

    [Fact]
    public void EffectCatalogOverride_ChangesInvocationFromControlToDataOnly()
    {
        const string api = """
            public static class Api
            {
                public static float Sample(float value) => value;
            }
            """;
        const string script = """
            using Finare;

            public static class Script
            {
                [Visual]
                public static float Run(float value)
                {
                    return Api.Sample(value);
                }
            }
            """;
        var sources = new[]
        {
            new CSharpSourceDocument("Api.cs", api),
            new CSharpSourceDocument("Script.cs", script),
        };
        CSharpAnalysisResult conservative = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest("Effects.Tests", sources));
        MethodEffectCatalog catalog = MethodEffectCatalog.CreateDefault().WithOverrides(
        [
            new MethodEffectRule(
                "effects.api.sample",
                "Effects.Tests",
                "Api",
                "Sample",
                MethodEffects.None),
        ]);
        CSharpAnalysisResult configured = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest("Effects.Tests", sources, effectCatalog: catalog));

        GraphNode conservativeCall = FindInvocation(conservative);
        GraphNode configuredCall = FindInvocation(configured);
        Assert.Contains(conservativeCall.Ports, static port => port.Kind == GraphPortKind.Control);
        Assert.DoesNotContain(configuredCall.Ports, static port => port.Kind == GraphPortKind.Control);
        Assert.NotEqual(
            conservative.Semantics!.ConfigurationId,
            configured.Semantics!.ConfigurationId);
    }

    private static CSharpCompilationRequest CreateRequest(bool reverse)
    {
        CSharpSourceDocument[] sources =
        [
            new("Game/Api.cs", ApiSource),
            new("Game/Controller.cs", ControllerSource),
        ];
        return new CSharpCompilationRequest(
            "Finare.MultiDocument.Tests",
            reverse ? sources.Reverse() : sources);
    }

    private static GraphNode FindInvocation(CSharpAnalysisResult result)
    {
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        return Assert.Single(
            Assert.Single(new HirGraphProjector().Project(result.Hir!, result.Semantics!).Functions).Nodes,
            static node => node.Kind == "invocation");
    }
}
