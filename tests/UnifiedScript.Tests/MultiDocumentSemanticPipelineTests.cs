using UnifiedScript.CSharp;
using UnifiedScript.Core.Hir;
using UnifiedScript.Graph;
using UnifiedScript.Semantics;

namespace UnifiedScript.Tests;

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
        using UnifiedScript;

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
            using UnifiedScript;

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
            "UnifiedScript.MultiDocument.Tests",
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
