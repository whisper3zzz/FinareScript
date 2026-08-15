using UnifiedScript.CSharp;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Graph;
using UnifiedScript.Semantics;

namespace UnifiedScript.Tests;

public sealed class CSharpSourcePatchApplierTests
{
    private const string Source = """
        using UnifiedScript;

        namespace Game;

        public static class Calculator
        {
            [Visual]
            public static int Scale(int value)
            {
                // This comment and the surrounding formatting must survive graph edits.
                return value + 1; // keep trailing trivia
            }
        }
        """;

    [Fact]
    public void Apply_ReplacesLiteralPreservesTriviaAndReanalyzesGraph()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        (CSharpAnalysisResult original, HirLiteralExpression literal) = AnalyzeLiteral(request);
        SemanticPatchDocument patch = CreatePatch(original, literal, "42");

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("return value + 42; // keep trailing trivia", patchedSource);
        Assert.Contains("// This comment and the surrounding formatting must survive graph edits.", patchedSource);
        Assert.NotEqual(original.Hir!.Id, result.Analysis!.Hir!.Id);
        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        Assert.Equal(result.Analysis.Semantics!.ConfigurationId, graph.SemanticConfigurationId);
        GraphNode graphLiteral = Assert.Single(
            Assert.Single(graph.Functions)
            .Nodes,
            static node => node.Kind == "literal");
        Assert.Equal("42", graphLiteral.Properties["value"]);
    }

    [Fact]
    public void Apply_RejectsStaleSourceCompilationBaseline()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        (_, HirLiteralExpression literal) = AnalyzeLiteral(request);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("stale-source"),
            AnalyzeConfiguration(request),
            [new ReplaceLiteralPatchOperation(literal.Id, literal.ValueText, "2")]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.BaselineMismatch);
    }

    [Fact]
    public void Apply_RejectsChangedLiteralPrecondition()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        (CSharpAnalysisResult original, HirLiteralExpression literal) = AnalyzeLiteral(request);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceLiteralPatchOperation(literal.Id, "999", "2")]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.PreconditionFailed);
    }

    [Fact]
    public void Apply_RejectsChangedSemanticConfiguration()
    {
        CSharpCompilationRequest originalRequest = CSharpCompilationRequest.Single(
            Source,
            "Game/Calculator.cs");
        (CSharpAnalysisResult original, HirLiteralExpression literal) = AnalyzeLiteral(originalRequest);
        MethodEffectCatalog changedCatalog = MethodEffectCatalog.CreateDefault().WithOverrides(
        [
            new MethodEffectRule(
                "patch.configuration-change",
                "*",
                "Game.Api",
                "*",
                MethodEffects.None),
        ]);
        CSharpCompilationRequest changedRequest = CSharpCompilationRequest.Single(
            Source,
            "Game/Calculator.cs",
            changedCatalog);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(
            changedRequest,
            CreatePatch(original, literal, "2"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.ConfigurationMismatch);
    }

    [Fact]
    public void Apply_RejectsNonLiteralTarget()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirIdentifierExpression identifier = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirIdentifierExpression>());
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceLiteralPatchOperation(identifier.Id, identifier.Name, "2")]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TargetKindMismatch);
    }

    [Fact]
    public void Apply_RejectsReplacementThatChangesGraphTopology()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        (CSharpAnalysisResult original, HirLiteralExpression literal) = AnalyzeLiteral(request);
        SemanticPatchDocument patch = CreatePatch(original, literal, "1 + 2");

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidReplacement);
    }

    [Fact]
    public void Apply_RejectsSemanticallyInvalidResultAtomically()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        (CSharpAnalysisResult original, HirLiteralExpression literal) = AnalyzeLiteral(request);
        SemanticPatchDocument patch = CreatePatch(original, literal, "\"not an integer\"");

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code.StartsWith("CS", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_ChangesOnlyTheTargetedDocumentInMultiFileRequest()
    {
        const string supportSource = """
            namespace Game;

            public static class Support
            {
                public static int Identity(int value) => value;
            }
            """;
        var request = new CSharpCompilationRequest(
            "Patch.MultiFile.Tests",
            [
                new CSharpSourceDocument("Game/Support.cs", supportSource),
                new CSharpSourceDocument("Game/Calculator.cs", Source),
            ]);
        (CSharpAnalysisResult original, HirLiteralExpression literal) = AnalyzeLiteral(request);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(
            request,
            CreatePatch(original, literal, "7"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        CSharpSourceDocument patchedSupport = Assert.Single(
            result.PatchedRequest!.Sources,
            static source => source.LogicalPath == "Game/Support.cs");
        CSharpSourceDocument patchedCalculator = Assert.Single(
            result.PatchedRequest.Sources,
            static source => source.LogicalPath == "Game/Calculator.cs");
        Assert.Equal(supportSource, patchedSupport.Source);
        Assert.Contains("return value + 7;", patchedCalculator.Source);
    }

    [Fact]
    public void Analyze_SourceCompilationIdChangesWhenSingleFileContentChanges()
    {
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(Source, "Game/Calculator.cs");
        CSharpAnalysisResult changed = new CSharpSemanticPipeline().Analyze(
            Source.Replace("value + 1", "value + 2", StringComparison.Ordinal),
            "Game/Calculator.cs");

        Assert.NotEqual(original.Hir!.Id, changed.Hir!.Id);
    }

    private static SemanticPatchDocument CreatePatch(
        CSharpAnalysisResult original,
        HirLiteralExpression literal,
        string newValue) => SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceLiteralPatchOperation(literal.Id, literal.ValueText, newValue)]);

    private static SemanticId AnalyzeConfiguration(CSharpCompilationRequest request) =>
        new CSharpSemanticPipeline().Analyze(request).Semantics!.ConfigurationId;

    private static (CSharpAnalysisResult Analysis, HirLiteralExpression Literal) AnalyzeLiteral(
        CSharpCompilationRequest request)
    {
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        return (
            analysis,
            Assert.Single(analysis.Hir!.DescendantsAndSelf().OfType<HirLiteralExpression>()));
    }
}
