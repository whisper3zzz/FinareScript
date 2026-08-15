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
    public void Apply_ChangesBinaryOperatorAndPreservesPersistentIdentity()
    {
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            Source,
            "Game/Calculator.cs");
        HirBinaryExpression binary = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirBinaryExpression>());
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ChangeOperatorPatchOperation(
                binary.Id,
                "+",
                "-",
                GetTypeId(original, binary),
                GetTypeId(original, binary.Left),
                GetTypeId(original, binary.Right))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("return value - 1; // keep trailing trivia", Assert.Single(result.PatchedRequest!.Sources).Source);
        HirBinaryExpression changed = Assert.Single(
            result.Analysis!.Hir!.DescendantsAndSelf().OfType<HirBinaryExpression>());
        Assert.Equal("-", changed.Operator);
        Assert.Equal(binary.Id, changed.Id);
        Assert.Equal(
            Assert.Single(original.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet(),
            Assert.Single(result.Analysis.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet());
        Assert.Empty(Assert.Single(result.Analysis.IdentitySidecars).Tombstones);
    }

    [Fact]
    public void Apply_ChangesUnaryOperatorWithinTheSameTypeFamily()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    return -value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/Unary.cs");
        HirUnaryExpression unary = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirUnaryExpression>());
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ChangeOperatorPatchOperation(
                unary.Id,
                "-",
                "+",
                GetTypeId(original, unary),
                GetTypeId(original, unary.Operand))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("return +value;", Assert.Single(result.PatchedRequest!.Sources).Source);
        HirUnaryExpression changed = Assert.Single(
            result.Analysis!.Hir!.DescendantsAndSelf().OfType<HirUnaryExpression>());
        Assert.Equal("+", changed.Operator);
        Assert.Equal(unary.Id, changed.Id);
    }

    [Fact]
    public void Apply_RejectsOperatorChangeAcrossResultFamilies()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirBinaryExpression binary = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirBinaryExpression>());
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ChangeOperatorPatchOperation(
                binary.Id,
                "+",
                "<",
                GetTypeId(original, binary),
                GetTypeId(original, binary.Left),
                GetTypeId(original, binary.Right))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidOperator);
    }

    [Fact]
    public void Apply_RejectsStaleOperatorTypePrecondition()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirBinaryExpression binary = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirBinaryExpression>());
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ChangeOperatorPatchOperation(
                binary.Id,
                "+",
                "-",
                SemanticId.FromKey("stale-result-type"),
                GetTypeId(original, binary.Left),
                GetTypeId(original, binary.Right))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.PreconditionFailed);
    }

    [Fact]
    public void Apply_ReconnectsPureSameTypeArgumentsAndPreservesTheirIdentities()
    {
        const string source = """
            using UnifiedScript;

            static class Api
            {
                public static int Combine(int first, int second) => (first * 10) + second;
            }

            static class Script
            {
                [Visual]
                static int Run(int first, int second)
                {
                    return Api.Combine(first, second);
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/Reconnect.cs");
        HirInvocationExpression invocation = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>());
        MethodDescriptor method = GetInvokedMethod(original, invocation);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectArgumentPatchOperation(
                invocation.Id,
                0,
                invocation.Arguments[0].Id,
                1,
                invocation.Arguments[1].Id,
                method.Parameters[0].TypeId,
                method.Parameters[1].TypeId)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("Api.Combine(second, first)", Assert.Single(result.PatchedRequest!.Sources).Source);
        HirInvocationExpression changed = Assert.Single(
            result.Analysis!.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>());
        Assert.Equal(invocation.Id, changed.Id);
        Assert.Equal(invocation.Arguments[1].Id, changed.Arguments[0].Id);
        Assert.Equal(invocation.Arguments[0].Id, changed.Arguments[1].Id);
        Assert.Equal(
            Assert.Single(original.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet(),
            Assert.Single(result.Analysis.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet());

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir!,
            result.Analysis.Semantics);
        GraphFunction function = Assert.Single(graph.Functions);
        Assert.Contains(
            function.Edges,
            edge => edge.FromNode == invocation.Arguments[1].Id &&
                edge.ToNode == invocation.Id && edge.ToPort == "arg0");
        Assert.Contains(
            function.Edges,
            edge => edge.FromNode == invocation.Arguments[0].Id &&
                edge.ToNode == invocation.Id && edge.ToPort == "arg1");
    }

    [Fact]
    public void Apply_RejectsArgumentReconnectionAcrossDifferentPortTypes()
    {
        const string source = """
            using UnifiedScript;

            static class Api
            {
                public static int Pick(int number, string text) => number;
            }

            static class Script
            {
                [Visual]
                static int Run(int number, string text)
                {
                    return Api.Pick(number, text);
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(source, "Game/TypedReconnect.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirInvocationExpression invocation = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>());
        MethodDescriptor method = GetInvokedMethod(original, invocation);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectArgumentPatchOperation(
                invocation.Id,
                0,
                invocation.Arguments[0].Id,
                1,
                invocation.Arguments[1].Id,
                method.Parameters[0].TypeId,
                method.Parameters[1].TypeId)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Apply_RejectsArgumentReconnectionThatWouldReorderEffects()
    {
        const string source = """
            using UnifiedScript;

            static class Api
            {
                public static int Next() => 1;
                public static int Combine(int first, int second) => first + second;
            }

            static class Script
            {
                [Visual]
                static int Run()
                {
                    return Api.Combine(Api.Next(), Api.Next());
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(source, "Game/EffectReconnect.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirInvocationExpression invocation = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>(),
            static candidate => candidate.Arguments.Count == 2);
        MethodDescriptor method = GetInvokedMethod(original, invocation);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectArgumentPatchOperation(
                invocation.Id,
                0,
                invocation.Arguments[0].Id,
                1,
                invocation.Arguments[1].Id,
                method.Parameters[0].TypeId,
                method.Parameters[1].TypeId)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.EffectOrderViolation);
    }

    [Fact]
    public void Apply_RejectsOverlappingStructuralOperationsAtomically()
    {
        const string source = """
            using UnifiedScript;

            static class Api
            {
                public static int Combine(int first, int second) => first + second;
            }

            static class Script
            {
                [Visual]
                static int Run(int first, int second)
                {
                    return Api.Combine(first + 1, second);
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(source, "Game/Overlap.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirInvocationExpression invocation = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>());
        HirBinaryExpression binary = Assert.IsType<HirBinaryExpression>(invocation.Arguments[0]);
        MethodDescriptor method = GetInvokedMethod(original, invocation);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [
                new ChangeOperatorPatchOperation(
                    binary.Id,
                    "+",
                    "-",
                    GetTypeId(original, binary),
                    GetTypeId(original, binary.Left),
                    GetTypeId(original, binary.Right)),
                new ReconnectArgumentPatchOperation(
                    invocation.Id,
                    0,
                    invocation.Arguments[0].Id,
                    1,
                    invocation.Arguments[1].Id,
                    method.Parameters[0].TypeId,
                    method.Parameters[1].TypeId),
            ]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.OverlappingOperations);
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

    private static SemanticId GetTypeId(CSharpAnalysisResult analysis, HirNode node) =>
        Assert.Single(analysis.Semantics!.Nodes, candidate => candidate.NodeId == node.Id).TypeId!.Value;

    private static MethodDescriptor GetInvokedMethod(
        CSharpAnalysisResult analysis,
        HirInvocationExpression invocation)
    {
        BoundNodeInfo binding = Assert.Single(
            analysis.Semantics!.Nodes,
            candidate => candidate.NodeId == invocation.Id);
        return Assert.Single(
            analysis.Semantics.Methods,
            method => method.Id == binding.InvokedMethodId);
    }

    private static (CSharpCompilationRequest Request, CSharpAnalysisResult Analysis) CreatePersistentRequest(
        string source,
        string path)
    {
        var initializingRequest = new CSharpCompilationRequest(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(path, source, IdentitySidecarDocument.CreateEmpty(path))]);
        CSharpAnalysisResult initialized = new CSharpSemanticPipeline().Analyze(initializingRequest);
        Assert.True(initialized.Succeeded, string.Join(Environment.NewLine, initialized.Diagnostics));
        var request = new CSharpCompilationRequest(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(path, source, Assert.Single(initialized.IdentitySidecars))]);
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        return (request, analysis);
    }

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
