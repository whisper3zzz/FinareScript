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
    public void Apply_ReconnectsCompilerOwnedDataEdgeThroughExistingSafeExpressionPipeline()
    {
        const string source = """
            using UnifiedScript;

            namespace Game;

            public static class RewireSample
            {
                [Visual]
                public static int Resolve(int value)
                {
                    int seed = value + 1;
                    int result = value + 2;
                    return result;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RewireSample.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(original.Succeeded, string.Join(Environment.NewLine, original.Diagnostics));
        GraphFunction function = Assert.Single(new HirGraphProjector().Project(
            original.Hir!,
            original.Semantics!).Functions);
        GraphNode local = Assert.Single(function.Nodes, static node =>
            node.Kind == "variable" && node.Properties["name"] == "seed");
        GraphNode targetLocal = Assert.Single(function.Nodes, static node =>
            node.Kind == "variable" && node.Properties["name"] == "result");
        GraphEdge sourceEdge = Assert.Single(function.Edges, edge =>
            edge.Kind == GraphEdgeKind.Data && edge.ToNode == local.Id && edge.ToPort == "value");
        GraphEdge targetEdge = Assert.Single(function.Edges, edge =>
            edge.Kind == GraphEdgeKind.Data && edge.ToNode == targetLocal.Id && edge.ToPort == "value");
        GraphNode sourceExpression = Assert.Single(
            function.Nodes,
            node => node.Id == sourceEdge.FromNode);
        SemanticId valueType = new(sourceExpression.Properties["typeId"]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectDataEdgePatchOperation(
                targetLocal.Id,
                "value",
                targetEdge.Id,
                targetEdge.FromNode,
                targetEdge.FromPort,
                sourceExpression.Id,
                sourceEdge.FromPort,
                valueType)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("int seed = value + 1;", patchedSource);
        Assert.Contains("int result = value + 1;", patchedSource);
        Assert.DoesNotContain("int result = value + 2;", patchedSource);
    }

    [Fact]
    public void Apply_DisconnectsDataEdgeToExplicitTypedDefault()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        GraphFunction function = Assert.Single(new HirGraphProjector().Project(
            original.Hir!, original.Semantics!).Functions);
        GraphNode binary = Assert.Single(function.Nodes, static node => node.Kind == "binary");
        GraphEdge edge = Assert.Single(function.Edges, candidate =>
            candidate.Kind == GraphEdgeKind.Data && candidate.ToNode == binary.Id &&
            candidate.ToPort == "right");
        GraphNode source = Assert.Single(function.Nodes, node => node.Id == edge.FromNode);
        SemanticId typeId = new(source.Properties["typeId"]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new DisconnectDataEdgePatchOperation(
                binary.Id,
                "right",
                edge.Id,
                edge.FromNode,
                edge.FromPort,
                typeId)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("return value + 0;", Assert.Single(result.PatchedRequest!.Sources).Source);
    }

    [Fact]
    public void Apply_SetsCompilerOwnedDataInputToTypedLiteral()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        GraphFunction function = Assert.Single(new HirGraphProjector().Project(
            original.Hir!, original.Semantics!).Functions);
        GraphNode binary = Assert.Single(function.Nodes, static node => node.Kind == "binary");
        GraphEdge edge = Assert.Single(function.Edges, candidate =>
            candidate.Kind == GraphEdgeKind.Data && candidate.ToNode == binary.Id &&
            candidate.ToPort == "right");
        GraphNode source = Assert.Single(function.Nodes, node => node.Id == edge.FromNode);
        SemanticId typeId = new(source.Properties["typeId"]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new SetDataInputValuePatchOperation(
                binary.Id,
                "right",
                edge.Id,
                edge.FromNode,
                edge.FromPort,
                typeId,
                new LiteralPatchExpression("42", typeId))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("return value + 42;", Assert.Single(result.PatchedRequest!.Sources).Source);
    }

    [Fact]
    public void Apply_RejectsDataInputLiteralWhoseRuntimeTypeDoesNotMatchPin()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        GraphFunction function = Assert.Single(new HirGraphProjector().Project(
            original.Hir!, original.Semantics!).Functions);
        GraphNode binary = Assert.Single(function.Nodes, static node => node.Kind == "binary");
        GraphEdge edge = Assert.Single(function.Edges, candidate =>
            candidate.Kind == GraphEdgeKind.Data && candidate.ToNode == binary.Id &&
            candidate.ToPort == "right");
        GraphNode source = Assert.Single(function.Nodes, node => node.Id == edge.FromNode);
        SemanticId typeId = new(source.Properties["typeId"]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new SetDataInputValuePatchOperation(
                binary.Id,
                "right",
                edge.Id,
                edge.FromNode,
                edge.FromPort,
                typeId,
                new LiteralPatchExpression("false", typeId))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidGraphExpression);
    }

    [Fact]
    public void Apply_InsertsStructuredUnrealMemberInvocationLocal()
    {
        const string source = """
            using UnifiedScript;

            namespace UnifiedScript.Unreal
            {
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public sealed class UnrealTypeAttribute(string path) : System.Attribute;
            }

            namespace Game
            {
                [UnifiedScript.Unreal.UnrealType("/Script/Game.Api")]
                public sealed class Api
                {
                    public int Add(int left, int right) => left + right;
                }

                public static class Script
                {
                    [Visual]
                    public static int Run(Api api, int left, int right)
                    {
                        return left;
                    }
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(source, "Game/ApiGraph.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(original.Succeeded, string.Join(Environment.NewLine, original.Diagnostics));
        HirMethodDeclaration method = Assert.Single(
            Assert.Single(original.Hir!.Types, static type => type.Name == "Script").Methods);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        HirParameter api = method.Parameters.Single(static parameter => parameter.Name == "api");
        HirParameter left = method.Parameters.Single(static parameter => parameter.Name == "left");
        HirParameter right = method.Parameters.Single(static parameter => parameter.Name == "right");
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> bindings = original.Semantics!.Nodes
            .ToDictionary(static node => node.NodeId);
        SemanticId apiType = bindings[api.Id].TypeId!.Value;
        SemanticId intType = bindings[left.Id].TypeId!.Value;
        var invocation = new MemberInvocationPatchExpression(
            new LocalReferencePatchExpression(api.Id, api.Name, apiType),
            "Add",
            [
                new LocalReferencePatchExpression(left.Id, left.Name, intType),
                new LocalReferencePatchExpression(right.Id, right.Name, intType),
            ],
            intType);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                valueReturn.Id,
                new LocalDeclarationPatchStatement("apiResult", "int", invocation, intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("int apiResult = api.Add(left, right);", patchedSource);
        Assert.Contains("return left;", patchedSource);
    }

    [Fact]
    public void Apply_InsertsStructuredUnrealMemberAccessLocal()
    {
        const string source = """
            using UnifiedScript;

            namespace UnifiedScript.Unreal
            {
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public sealed class UnrealTypeAttribute(string path) : System.Attribute;
            }

            namespace Game
            {
                [UnifiedScript.Unreal.UnrealType("/Script/Game.Api")]
                public sealed class Api
                {
                    public int Count { get; }
                }

                public static class Script
                {
                    [Visual]
                    public static int Run(Api api)
                    {
                        return 0;
                    }
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(source, "Game/ApiPropertyGraph.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(original.Succeeded, string.Join(Environment.NewLine, original.Diagnostics));
        HirMethodDeclaration method = Assert.Single(
            Assert.Single(original.Hir!.Types, static type => type.Name == "Script").Methods);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        HirParameter api = Assert.Single(method.Parameters);
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> bindings = original.Semantics!.Nodes
            .ToDictionary(static node => node.NodeId);
        SemanticId apiType = bindings[api.Id].TypeId!.Value;
        SemanticId intType = bindings[valueReturn.Expression!.Id].TypeId!.Value;
        var access = new MemberAccessPatchExpression(
            new LocalReferencePatchExpression(api.Id, api.Name, apiType),
            "Count",
            intType);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                valueReturn.Id,
                new LocalDeclarationPatchStatement("propertyValue", "int", access, intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("int propertyValue = api.Count;", patchedSource);
        Assert.Contains("return 0;", patchedSource);
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
    public void Apply_ReplacesNestedBinaryInputAndPreservesUnaffectedSpineIdentities()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    int offset = 2;
                    return (value + 1) * 3;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/NestedInput.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement offset = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HirBinaryExpression outer = Assert.IsType<HirBinaryExpression>(returnStatement.Expression);
        HirBinaryExpression addition = Assert.IsType<HirBinaryExpression>(outer.Left);
        HirExpression oldInput = addition.Right;
        SemanticId intType = GetTypeId(original, addition);
        HashSet<SemanticId> preservedIds = Assert.Single(original.IdentitySidecars).Entries
            .Select(static entry => entry.Id)
            .Where(id => id != oldInput.Id)
            .ToHashSet();
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                addition.Id,
                "right",
                oldInput.Id,
                intType,
                intType,
                new LocalReferencePatchExpression(offset.Id, offset.Name, intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "return (value + offset) * 3;",
            Assert.Single(result.PatchedRequest!.Sources).Source);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement changedOffset = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[0]);
        HirReturnStatement changedReturn = Assert.IsType<HirReturnStatement>(changedMethod.Body.Statements[1]);
        HirBinaryExpression changedOuter = Assert.IsType<HirBinaryExpression>(changedReturn.Expression);
        HirBinaryExpression changedAddition = Assert.IsType<HirBinaryExpression>(changedOuter.Left);
        HirIdentifierExpression connectedOffset = Assert.IsType<HirIdentifierExpression>(changedAddition.Right);
        Assert.Equal(returnStatement.Id, changedReturn.Id);
        Assert.Equal(outer.Id, changedOuter.Id);
        Assert.Equal(addition.Id, changedAddition.Id);
        Assert.Equal(addition.Left.Id, changedAddition.Left.Id);
        Assert.Equal(offset.Id, changedOffset.Id);
        Assert.Equal(
            changedOffset.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == connectedOffset.Id).ReferencedSymbolId);

        IdentitySidecarDocument after = Assert.Single(result.Analysis.IdentitySidecars);
        Assert.Subset(
            after.Entries.Select(static entry => entry.Id).ToHashSet(),
            preservedIds);
        Assert.Contains(after.Tombstones, tombstone => tombstone.Id == oldInput.Id);
        GraphFunction function = Assert.Single(new HirGraphProjector()
            .Project(result.Analysis.Hir, result.Analysis.Semantics)
            .Functions);
        Assert.Contains(
            function.Edges,
            edge => edge.FromNode == connectedOffset.Id &&
                edge.ToNode == changedAddition.Id &&
                edge.ToPort == "right");
    }

    [Fact]
    public void Apply_ReplacesInvocationArgumentInputUsingGraphPortName()
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
                static int Run(int value)
                {
                    return Api.Combine(value, 1);
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/InvocationInput.cs");
        HirInvocationExpression invocation = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>());
        SemanticId intType = GetTypeId(original, invocation.Arguments[1]);
        SemanticId invocationType = GetTypeId(original, invocation);
        HirParameter value = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirParameter>(),
            static parameter => parameter.Name == "value");
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                invocation.Id,
                "arg1",
                invocation.Arguments[1].Id,
                intType,
                invocationType,
                new BinaryPatchExpression(
                    "+",
                    new LocalReferencePatchExpression(value.Id, value.Name, intType),
                    new LiteralPatchExpression("2", intType),
                    intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "Api.Combine(value, (value + 2))",
            Assert.Single(result.PatchedRequest!.Sources).Source);
        HirInvocationExpression changedInvocation = Assert.Single(
            result.Analysis!.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>());
        Assert.Equal(invocation.Id, changedInvocation.Id);
        Assert.Equal(invocation.Arguments[0].Id, changedInvocation.Arguments[0].Id);
        Assert.IsType<HirBinaryExpression>(changedInvocation.Arguments[1]);
    }

    [Fact]
    public void Apply_ReplacesUnaryAndCastOperandPorts()
    {
        const string unarySource = """
            using UnifiedScript;

            static class UnaryScript
            {
                [Visual]
                static int Run(int value)
                {
                    return -value;
                }
            }
            """;
        (CSharpCompilationRequest unaryRequest, CSharpAnalysisResult unaryAnalysis) =
            CreatePersistentRequest(unarySource, "Game/UnaryInput.cs");
        HirUnaryExpression unary = Assert.Single(
            unaryAnalysis.Hir!.DescendantsAndSelf().OfType<HirUnaryExpression>());
        SemanticId unaryType = GetTypeId(unaryAnalysis, unary);
        SemanticPatchDocument unaryPatch = SemanticPatchDocument.Create(
            unaryAnalysis.Hir!.Id,
            unaryAnalysis.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                unary.Id,
                "operand",
                unary.Operand.Id,
                unaryType,
                unaryType,
                new LiteralPatchExpression("2", unaryType))]);

        CSharpPatchResult unaryResult = new CSharpSourcePatchApplier().Apply(unaryRequest, unaryPatch);

        Assert.True(unaryResult.Succeeded, string.Join(Environment.NewLine, unaryResult.Diagnostics));
        Assert.Contains("return -2;", Assert.Single(unaryResult.PatchedRequest!.Sources).Source);
        Assert.Equal(
            unary.Id,
            Assert.Single(unaryResult.Analysis!.Hir!.DescendantsAndSelf()
                .OfType<HirUnaryExpression>()).Id);

        const string castSource = """
            using UnifiedScript;

            static class CastScript
            {
                [Visual]
                static int Run(float value)
                {
                    return (int)value;
                }
            }
            """;
        (CSharpCompilationRequest castRequest, CSharpAnalysisResult castAnalysis) =
            CreatePersistentRequest(castSource, "Game/CastInput.cs");
        HirCastExpression cast = Assert.Single(
            castAnalysis.Hir!.DescendantsAndSelf().OfType<HirCastExpression>());
        SemanticId operandType = GetTypeId(castAnalysis, cast.Operand);
        SemanticPatchDocument castPatch = SemanticPatchDocument.Create(
            castAnalysis.Hir!.Id,
            castAnalysis.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                cast.Id,
                "operand",
                cast.Operand.Id,
                operandType,
                GetTypeId(castAnalysis, cast),
                new LiteralPatchExpression("2.0f", operandType))]);

        CSharpPatchResult castResult = new CSharpSourcePatchApplier().Apply(castRequest, castPatch);

        Assert.True(castResult.Succeeded, string.Join(Environment.NewLine, castResult.Diagnostics));
        Assert.Contains("return (int)2.0f;", Assert.Single(castResult.PatchedRequest!.Sources).Source);
        Assert.Equal(
            cast.Id,
            Assert.Single(castResult.Analysis!.Hir!.DescendantsAndSelf()
                .OfType<HirCastExpression>()).Id);
    }

    [Fact]
    public void Apply_BuildsTypedConditionalAndEditsItsExactInputPort()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Select(bool enabled, int value, int fallback)
                {
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ConditionalPatch.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirExpression oldValue = Assert.IsAssignableFrom<HirExpression>(returnStatement.Expression);
        SemanticId boolType = GetTypeId(original, method.Parameters[0]);
        SemanticId intType = GetTypeId(original, oldValue);
        var applier = new CSharpSourcePatchApplier();
        SemanticPatchDocument buildPatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceReturnValuePatchOperation(
                returnStatement.Id,
                oldValue.Id,
                intType,
                new ConditionalPatchExpression(
                    new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        method.Parameters[0].Name,
                        boolType),
                    new LocalReferencePatchExpression(
                        method.Parameters[1].Id,
                        method.Parameters[1].Name,
                        intType),
                    new LocalReferencePatchExpression(
                        method.Parameters[2].Id,
                        method.Parameters[2].Name,
                        intType),
                    intType))]);

        CSharpPatchResult built = applier.Apply(request, buildPatch);

        Assert.True(built.Succeeded, string.Join(Environment.NewLine, built.Diagnostics));
        CSharpCompilationRequest builtRequest = Assert.IsType<CSharpCompilationRequest>(built.PatchedRequest);
        Assert.Contains(
            "return enabled ? value : fallback;",
            Assert.Single(builtRequest.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult builtAnalysis = Assert.IsType<CSharpAnalysisResult>(built.Analysis);
        HirCompilationUnit builtHir = Assert.IsType<HirCompilationUnit>(builtAnalysis.Hir);
        BoundCompilation builtSemantics = Assert.IsType<BoundCompilation>(builtAnalysis.Semantics);
        HirConditionalExpression conditional = Assert.Single(
            builtHir.DescendantsAndSelf().OfType<HirConditionalExpression>());
        SemanticId originalTrueId = conditional.WhenTrue.Id;
        SemanticId oldFalseId = conditional.WhenFalse.Id;
        SemanticPatchDocument editPortPatch = SemanticPatchDocument.Create(
            builtHir.Id,
            builtSemantics.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                conditional.Id,
                "whenFalse",
                oldFalseId,
                intType,
                intType,
                new BinaryPatchExpression(
                    "+",
                    new LocalReferencePatchExpression(
                        method.Parameters[1].Id,
                        method.Parameters[1].Name,
                        intType),
                    new LiteralPatchExpression("1", intType),
                    intType))]);

        IReadOnlyDictionary<string, IdentitySidecarDocument> builtSidecars = builtAnalysis.IdentitySidecars
            .ToDictionary(static sidecar => sidecar.SourcePath, StringComparer.Ordinal);
        var advancedRequest = new CSharpCompilationRequest(
            builtRequest.AssemblyName,
            builtRequest.Sources.Select(source => new CSharpSourceDocument(
                source.LogicalPath,
                source.Source,
                builtSidecars[source.LogicalPath])),
            builtRequest.ReferencePaths,
            builtRequest.EffectCatalog);
        CSharpPatchResult edited = applier.Apply(advancedRequest, editPortPatch);

        Assert.True(edited.Succeeded, string.Join(Environment.NewLine, edited.Diagnostics));
        Assert.Contains(
            "return enabled ? value : (value + 1);",
            Assert.Single(edited.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult editedAnalysis = Assert.IsType<CSharpAnalysisResult>(edited.Analysis);
        HirCompilationUnit editedHir = Assert.IsType<HirCompilationUnit>(editedAnalysis.Hir);
        HirConditionalExpression changed = Assert.Single(
            editedHir.DescendantsAndSelf().OfType<HirConditionalExpression>());
        HirBinaryExpression newFalse = Assert.IsType<HirBinaryExpression>(changed.WhenFalse);
        Assert.Equal(conditional.Id, changed.Id);
        Assert.Equal(originalTrueId, changed.WhenTrue.Id);
        Assert.Contains(
            Assert.Single(editedAnalysis.IdentitySidecars).Tombstones,
            tombstone => tombstone.Id == oldFalseId);
        GraphFunction graph = Assert.Single(new HirGraphProjector()
            .Project(editedHir, editedAnalysis.Semantics)
            .Functions);
        Assert.Contains(
            graph.Edges,
            edge => edge.FromNode == newFalse.Id &&
                edge.ToNode == changed.Id &&
                edge.ToPort == "whenFalse");

        SemanticPatchDocument invalidConditionPatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceReturnValuePatchOperation(
                returnStatement.Id,
                oldValue.Id,
                intType,
                new ConditionalPatchExpression(
                    new LocalReferencePatchExpression(
                        method.Parameters[1].Id,
                        method.Parameters[1].Name,
                        intType),
                    new LiteralPatchExpression("1", intType),
                    new LiteralPatchExpression("2", intType),
                    intType))]);

        CSharpPatchResult invalid = applier.Apply(request, invalidConditionPatch);

        Assert.False(invalid.Succeeded);
        Assert.Contains(
            invalid.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Apply_BuildsTypedNullCoalescingAndEditsItsExactInputPort()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Resolve(int? value, int fallback)
                {
                    return fallback;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/CoalescePatch.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirExpression oldValue = Assert.IsAssignableFrom<HirExpression>(returnStatement.Expression);
        SemanticId nullableIntType = GetTypeId(original, method.Parameters[0]);
        SemanticId intType = GetTypeId(original, method.Parameters[1]);
        var applier = new CSharpSourcePatchApplier();
        SemanticPatchDocument buildPatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceReturnValuePatchOperation(
                returnStatement.Id,
                oldValue.Id,
                intType,
                new BinaryPatchExpression(
                    "??",
                    new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        method.Parameters[0].Name,
                        nullableIntType),
                    new LocalReferencePatchExpression(
                        method.Parameters[1].Id,
                        method.Parameters[1].Name,
                        intType),
                    intType))]);

        CSharpPatchResult built = applier.Apply(request, buildPatch);

        Assert.True(built.Succeeded, string.Join(Environment.NewLine, built.Diagnostics));
        CSharpCompilationRequest builtRequest = Assert.IsType<CSharpCompilationRequest>(built.PatchedRequest);
        Assert.Contains(
            "return value ?? fallback;",
            Assert.Single(builtRequest.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult builtAnalysis = Assert.IsType<CSharpAnalysisResult>(built.Analysis);
        HirCompilationUnit builtHir = Assert.IsType<HirCompilationUnit>(builtAnalysis.Hir);
        BoundCompilation builtSemantics = Assert.IsType<BoundCompilation>(builtAnalysis.Semantics);
        HirBinaryExpression coalesce = Assert.Single(
            builtHir.DescendantsAndSelf().OfType<HirBinaryExpression>(),
            static binary => binary.Operator == "??");
        SemanticId originalLeftId = coalesce.Left.Id;
        SemanticId oldRightId = coalesce.Right.Id;
        SemanticPatchDocument editPortPatch = SemanticPatchDocument.Create(
            builtHir.Id,
            builtSemantics.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                coalesce.Id,
                "right",
                oldRightId,
                intType,
                intType,
                new LiteralPatchExpression("2", intType))]);
        IReadOnlyDictionary<string, IdentitySidecarDocument> builtSidecars = builtAnalysis.IdentitySidecars
            .ToDictionary(static sidecar => sidecar.SourcePath, StringComparer.Ordinal);
        var advancedRequest = new CSharpCompilationRequest(
            builtRequest.AssemblyName,
            builtRequest.Sources.Select(source => new CSharpSourceDocument(
                source.LogicalPath,
                source.Source,
                builtSidecars[source.LogicalPath])),
            builtRequest.ReferencePaths,
            builtRequest.EffectCatalog);

        CSharpPatchResult edited = applier.Apply(advancedRequest, editPortPatch);

        Assert.True(edited.Succeeded, string.Join(Environment.NewLine, edited.Diagnostics));
        Assert.Contains(
            "return value ?? 2;",
            Assert.Single(edited.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult editedAnalysis = Assert.IsType<CSharpAnalysisResult>(edited.Analysis);
        HirCompilationUnit editedHir = Assert.IsType<HirCompilationUnit>(editedAnalysis.Hir);
        BoundCompilation editedSemantics = Assert.IsType<BoundCompilation>(editedAnalysis.Semantics);
        HirBinaryExpression changed = Assert.Single(
            editedHir.DescendantsAndSelf().OfType<HirBinaryExpression>(),
            static binary => binary.Operator == "??");
        Assert.Equal(coalesce.Id, changed.Id);
        Assert.Equal(originalLeftId, changed.Left.Id);
        Assert.Contains(
            Assert.Single(editedAnalysis.IdentitySidecars).Tombstones,
            tombstone => tombstone.Id == oldRightId);
        GraphNode graphCoalesce = Assert.Single(
            Assert.Single(new HirGraphProjector()
                .Project(editedHir, editedSemantics)
                .Functions)
                .Nodes,
            static node => node.Kind == "binary" && node.Properties["operator"] == "??");
        Assert.Equal("whenLeftNull", graphCoalesce.Properties["rightEvaluation"]);

        SemanticPatchDocument invalidPatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new ReplaceReturnValuePatchOperation(
                returnStatement.Id,
                oldValue.Id,
                intType,
                new BinaryPatchExpression(
                    "??",
                    new LocalReferencePatchExpression(
                        method.Parameters[1].Id,
                        method.Parameters[1].Name,
                        intType),
                    new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        method.Parameters[0].Name,
                        nullableIntType),
                    intType))]);

        CSharpPatchResult invalid = applier.Apply(request, invalidPatch);

        Assert.False(invalid.Succeeded);
        Assert.Contains(
            invalid.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Apply_BuildsConditionalMemberAccessAndEditsItsReceiverPort()
    {
        const string source = """
            #nullable enable

            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int? Measure(string? primary, string? fallback)
                {
                    return null;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ConditionalMemberAccessPatch.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirExpression oldValue = Assert.IsAssignableFrom<HirExpression>(valueReturn.Expression);
        SemanticId nullableStringType = GetTypeId(original, method.Parameters[0]);
        SemanticId nullableIntType = GetTypeId(original, oldValue);
        var applier = new CSharpSourcePatchApplier();
        SemanticPatchDocument buildPatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceReturnValuePatchOperation(
                valueReturn.Id,
                oldValue.Id,
                nullableIntType,
                new ConditionalMemberAccessPatchExpression(
                    new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        method.Parameters[0].Name,
                        nullableStringType),
                    "Length",
                    nullableIntType))]);

        CSharpPatchResult built = applier.Apply(request, buildPatch);

        Assert.True(built.Succeeded, string.Join(Environment.NewLine, built.Diagnostics));
        CSharpCompilationRequest builtRequest = Assert.IsType<CSharpCompilationRequest>(
            built.PatchedRequest);
        Assert.Contains(
            "return primary?.Length;",
            Assert.Single(builtRequest.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult builtAnalysis = Assert.IsType<CSharpAnalysisResult>(built.Analysis);
        HirCompilationUnit builtHir = Assert.IsType<HirCompilationUnit>(builtAnalysis.Hir);
        HirConditionalAccessExpression conditionalAccess = Assert.Single(
            builtHir.DescendantsAndSelf().OfType<HirConditionalAccessExpression>());
        HirConditionalMemberBindingExpression memberBinding =
            Assert.IsType<HirConditionalMemberBindingExpression>(conditionalAccess.WhenNotNull);
        SemanticId oldReceiverId = conditionalAccess.Receiver.Id;
        SemanticPatchDocument editPatch = SemanticPatchDocument.Create(
            builtHir.Id,
            builtAnalysis.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                conditionalAccess.Id,
                "receiver",
                oldReceiverId,
                nullableStringType,
                nullableIntType,
                new LocalReferencePatchExpression(
                    method.Parameters[1].Id,
                    method.Parameters[1].Name,
                    nullableStringType))]);
        IReadOnlyDictionary<string, IdentitySidecarDocument> sidecars = builtAnalysis.IdentitySidecars
            .ToDictionary(static sidecar => sidecar.SourcePath, StringComparer.Ordinal);
        var advancedRequest = new CSharpCompilationRequest(
            builtRequest.AssemblyName,
            builtRequest.Sources.Select(document => new CSharpSourceDocument(
                document.LogicalPath,
                document.Source,
                sidecars[document.LogicalPath])),
            builtRequest.ReferencePaths,
            builtRequest.EffectCatalog);

        CSharpPatchResult edited = applier.Apply(advancedRequest, editPatch);

        Assert.True(edited.Succeeded, string.Join(Environment.NewLine, edited.Diagnostics));
        Assert.Contains(
            "return fallback?.Length;",
            Assert.Single(edited.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult editedAnalysis = Assert.IsType<CSharpAnalysisResult>(edited.Analysis);
        HirCompilationUnit editedHir = Assert.IsType<HirCompilationUnit>(editedAnalysis.Hir);
        BoundCompilation editedSemantics = Assert.IsType<BoundCompilation>(editedAnalysis.Semantics);
        HirConditionalAccessExpression changed = Assert.Single(
            editedHir.DescendantsAndSelf().OfType<HirConditionalAccessExpression>());
        Assert.Equal(conditionalAccess.Id, changed.Id);
        Assert.Equal(memberBinding.Id, changed.WhenNotNull.Id);
        Assert.Equal(
            "fallback",
            Assert.IsType<HirIdentifierExpression>(changed.Receiver).Name);
        Assert.Contains(
            Assert.Single(editedAnalysis.IdentitySidecars).Tombstones,
            tombstone => tombstone.Id == oldReceiverId);
        GraphFunction graph = Assert.Single(new HirGraphProjector()
            .Project(editedHir, editedSemantics)
            .Functions);
        Assert.Contains(graph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Data &&
            edge.FromNode == changed.Receiver.Id &&
            edge.ToNode == changed.Id &&
            edge.ToPort == "receiver");
        Assert.Contains(graph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Data &&
            edge.FromNode == changed.Receiver.Id &&
            edge.ToNode == changed.WhenNotNull.Id &&
            edge.ToPort == "target");
    }

    [Fact]
    public void Apply_RejectsConditionalMemberAccessWithAHiddenResultConversion()
    {
        const string source = """
            #nullable enable

            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static long? Measure(string? value)
                {
                    return null;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/ConvertedConditionalMemberAccess.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(original.Succeeded, string.Join(Environment.NewLine, original.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId nullableStringType = GetTypeId(original, method.Parameters[0]);
        SemanticId nullableLongType = GetTypeId(original, valueReturn.Expression!);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceReturnValuePatchOperation(
                valueReturn.Id,
                valueReturn.Expression!.Id,
                nullableLongType,
                new ConditionalMemberAccessPatchExpression(
                    new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        method.Parameters[0].Name,
                        nullableStringType),
                    "Length",
                    nullableLongType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code is SemanticDiagnosticCodes.ConditionalAccessTypeShapeNotSupported or
                SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Apply_RejectsStaleOrIncompatibleExpressionInputPort()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            Source,
            "Game/RejectInputPort.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirBinaryExpression binary = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirBinaryExpression>());
        SemanticId intType = GetTypeId(original, binary);
        var applier = new CSharpSourcePatchApplier();
        SemanticPatchDocument incompatiblePort = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                binary.Id,
                "operand",
                binary.Right.Id,
                intType,
                intType,
                new LiteralPatchExpression("2", intType))]);
        SemanticPatchDocument staleEndpoint = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                binary.Id,
                "right",
                SemanticId.FromKey("stale-input-endpoint"),
                intType,
                intType,
                new LiteralPatchExpression("2", intType))]);
        SemanticId staleInputType = SemanticId.FromKey("stale-input-port-type");
        SemanticPatchDocument staleInputTypePatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                binary.Id,
                "right",
                binary.Right.Id,
                staleInputType,
                intType,
                new LiteralPatchExpression("2", staleInputType))]);
        SemanticPatchDocument staleTargetTypePatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                binary.Id,
                "right",
                binary.Right.Id,
                intType,
                SemanticId.FromKey("stale-input-parent-type"),
                new LiteralPatchExpression("2", intType))]);

        CSharpPatchResult incompatibleResult = applier.Apply(request, incompatiblePort);
        CSharpPatchResult staleResult = applier.Apply(request, staleEndpoint);
        CSharpPatchResult staleInputTypeResult = applier.Apply(request, staleInputTypePatch);
        CSharpPatchResult staleTargetTypeResult = applier.Apply(request, staleTargetTypePatch);

        Assert.False(incompatibleResult.Succeeded);
        Assert.Null(incompatibleResult.PatchedRequest);
        Assert.Contains(
            incompatibleResult.Diagnostics,
            static diagnostic =>
                diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidExpressionInputPort);
        Assert.False(staleResult.Succeeded);
        Assert.Null(staleResult.PatchedRequest);
        Assert.Contains(
            staleResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.PreconditionFailed);
        Assert.False(staleInputTypeResult.Succeeded);
        Assert.Null(staleInputTypeResult.PatchedRequest);
        Assert.Contains(
            staleInputTypeResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.PreconditionFailed);
        Assert.False(staleTargetTypeResult.Succeeded);
        Assert.Null(staleTargetTypeResult.PatchedRequest);
        Assert.Contains(
            staleTargetTypeResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.PreconditionFailed);
    }

    [Fact]
    public void Apply_RejectsEffectfulExpressionInputAndPreservesBoundaryComments()
    {
        const string effectfulSource = """
            using UnifiedScript;

            static class Api
            {
                public static int Next() => 1;
                public static int Combine(int first, int second) => first + second;
            }

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    return Api.Combine(Api.Next(), value);
                }
            }
            """;
        CSharpCompilationRequest effectfulRequest = CSharpCompilationRequest.Single(
            effectfulSource,
            "Game/RejectEffectfulInput.cs");
        CSharpAnalysisResult effectfulAnalysis = new CSharpSemanticPipeline().Analyze(effectfulRequest);
        HirInvocationExpression outerInvocation = Assert.Single(
            effectfulAnalysis.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>(),
            static invocation => invocation.Arguments.Count == 2);
        SemanticId intType = GetTypeId(effectfulAnalysis, outerInvocation.Arguments[0]);
        SemanticPatchDocument effectfulPatch = SemanticPatchDocument.Create(
            effectfulAnalysis.Hir!.Id,
            effectfulAnalysis.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                outerInvocation.Id,
                "arg0",
                outerInvocation.Arguments[0].Id,
                intType,
                GetTypeId(effectfulAnalysis, outerInvocation),
                new LiteralPatchExpression("2", intType))]);

        CSharpPatchResult effectfulResult = new CSharpSourcePatchApplier().Apply(
            effectfulRequest,
            effectfulPatch);

        Assert.False(effectfulResult.Succeeded);
        Assert.Null(effectfulResult.PatchedRequest);
        Assert.Contains(
            effectfulResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.EffectOrderViolation);

        const string commentedSource = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    return value + /* keep input note */ 1;
                }
            }
            """;
        CSharpCompilationRequest commentedRequest = CSharpCompilationRequest.Single(
            commentedSource,
            "Game/RejectCommentedInput.cs");
        CSharpAnalysisResult commentedAnalysis = new CSharpSemanticPipeline().Analyze(commentedRequest);
        HirBinaryExpression commentedBinary = Assert.Single(
            commentedAnalysis.Hir!.DescendantsAndSelf().OfType<HirBinaryExpression>());
        SemanticPatchDocument commentedPatch = SemanticPatchDocument.Create(
            commentedAnalysis.Hir!.Id,
            commentedAnalysis.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                commentedBinary.Id,
                "right",
                commentedBinary.Right.Id,
                GetTypeId(commentedAnalysis, commentedBinary.Right),
                GetTypeId(commentedAnalysis, commentedBinary),
                new LiteralPatchExpression(
                    "2",
                    GetTypeId(commentedAnalysis, commentedBinary.Right))) ]);

        CSharpPatchResult commentedResult = new CSharpSourcePatchApplier().Apply(
            commentedRequest,
            commentedPatch);

        Assert.True(commentedResult.Succeeded, string.Join(Environment.NewLine, commentedResult.Diagnostics));
        Assert.Contains(
            "return value + /* keep input note */ 2;",
            Assert.Single(commentedResult.PatchedRequest!.Sources).Source);

        const string embeddedCommentSource = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    return (value + /* embedded note */ 1) * 3;
                }
            }
            """;
        CSharpCompilationRequest embeddedCommentRequest = CSharpCompilationRequest.Single(
            embeddedCommentSource,
            "Game/RejectEmbeddedInputComment.cs");
        CSharpAnalysisResult embeddedCommentAnalysis = new CSharpSemanticPipeline().Analyze(
            embeddedCommentRequest);
        HirReturnStatement embeddedReturn = Assert.Single(
            embeddedCommentAnalysis.Hir!.DescendantsAndSelf().OfType<HirReturnStatement>());
        HirBinaryExpression embeddedOuter = Assert.IsType<HirBinaryExpression>(
            embeddedReturn.Expression);
        SemanticId embeddedInputType = GetTypeId(embeddedCommentAnalysis, embeddedOuter.Left);
        SemanticPatchDocument embeddedCommentPatch = SemanticPatchDocument.Create(
            embeddedCommentAnalysis.Hir!.Id,
            embeddedCommentAnalysis.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                embeddedOuter.Id,
                "left",
                embeddedOuter.Left.Id,
                embeddedInputType,
                GetTypeId(embeddedCommentAnalysis, embeddedOuter),
                new LiteralPatchExpression("2", embeddedInputType))]);

        CSharpPatchResult embeddedCommentResult = new CSharpSourcePatchApplier().Apply(
            embeddedCommentRequest,
            embeddedCommentPatch);

        Assert.False(embeddedCommentResult.Succeeded);
        Assert.Null(embeddedCommentResult.PatchedRequest);
        Assert.Contains(
            embeddedCommentResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidReplacement);
        Assert.Equal(embeddedCommentSource, Assert.Single(embeddedCommentRequest.Sources).Source);
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
    public void Apply_InsertsLocalLiteralAndAllocatesOnlyNewStatementIdentities()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage)
                {
                    return damage;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/InsertLocal.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        SemanticId floatType = GetTypeId(original, returnStatement.Expression!);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                returnStatement.Id,
                new LocalLiteralDeclarationPatchStatement("scale", "float", "2.0f", floatType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("float scale = 2.0f;\n        return damage;", patchedSource);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement inserted = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[0]);
        Assert.Equal("scale", inserted.Name);
        Assert.Equal("2.0f", Assert.IsType<HirLiteralExpression>(inserted.Initializer).ValueText);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);
        Assert.Equal(returnStatement.Id, changedMethod.Body.Statements[1].Id);

        IdentitySidecarDocument before = Assert.Single(original.IdentitySidecars);
        IdentitySidecarDocument after = Assert.Single(result.Analysis.IdentitySidecars);
        HashSet<SemanticId> afterIds = after.Entries.Select(static entry => entry.Id).ToHashSet();
        Assert.All(before.Entries, entry => Assert.Contains(entry.Id, afterIds));
        Assert.Equal(before.Entries.Count + 2, after.Entries.Count);
        Assert.Empty(after.Tombstones);

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        Assert.Contains(
            Assert.Single(graph.Functions).Nodes,
            node => node.Id == inserted.Id && node.Kind == "variable");
    }

    [Fact]
    public void Apply_InsertsCfgCheckedLocalAssignmentWithTypedPureValue()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int seed)
                {
                    int result = seed;
                    return result;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/InsertAssignment.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, resultDefinition);
        var assignment = new LocalAssignmentPatchStatement(
            resultDefinition.Id,
            resultDefinition.Name,
            intType,
            new BinaryPatchExpression(
                "+",
                new LocalReferencePatchExpression(
                    resultDefinition.Id,
                    resultDefinition.Name,
                    intType),
                new LiteralPatchExpression("1", intType),
                intType));
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                resultDefinition.Id,
                returnStatement.Id,
                assignment)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "int result = seed;\n        result = result + 1;\n        return result;",
            Assert.Single(result.PatchedRequest!.Sources).Source);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirExpressionStatement assignmentStatement = Assert.IsType<HirExpressionStatement>(
            changedMethod.Body.Statements[1]);
        HirAssignmentExpression assignmentExpression = Assert.IsType<HirAssignmentExpression>(
            assignmentStatement.Expression);
        Assert.Equal("=", assignmentExpression.Operator);
        HirIdentifierExpression assignmentTarget = Assert.IsType<HirIdentifierExpression>(
            assignmentExpression.Target);
        Assert.Equal(
            resultDefinition.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == assignmentTarget.Id).ReferencedSymbolId);

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphControlFlowFunction flow = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(graph).Functions);
        GraphControlFlowNode assignmentNode = Assert.Single(
            flow.Nodes,
            node => node.Id == assignmentExpression.Id);
        Assert.True(assignmentNode.IsReachable);
        Assert.Contains(resultDefinition.Id, assignmentNode.DominatorIds);
    }

    [Fact]
    public void Apply_InsertsAssignmentAtReachableBranchEndUsingBranchLocal()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool ready)
                {
                    int result = 0;
                    if (ready)
                    {
                        int branchValue = 2;
                    }

                    return result;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/InsertBranchAssignment.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirIfStatement ifStatement = Assert.IsType<HirIfStatement>(method.Body.Statements[1]);
        HirVariableDeclarationStatement branchValue = Assert.IsType<HirVariableDeclarationStatement>(
            Assert.Single(ifStatement.Then.Statements));
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                ifStatement.Then.Id,
                branchValue.Id,
                null,
                new LocalAssignmentPatchStatement(
                    resultDefinition.Id,
                    resultDefinition.Name,
                    intType,
                    new LocalReferencePatchExpression(
                        branchValue.Id,
                        branchValue.Name,
                        intType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "int branchValue = 2;\n            result = branchValue;",
            Assert.Single(result.PatchedRequest!.Sources).Source);
        HirIfStatement changedIf = Assert.IsType<HirIfStatement>(
            Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods).Body.Statements[1]);
        Assert.IsType<HirAssignmentExpression>(
            Assert.IsType<HirExpressionStatement>(changedIf.Then.Statements[1]).Expression);
    }

    [Fact]
    public void Apply_RejectsAssignmentToBranchLocalOutsideItsCfgScope()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool ready)
                {
                    if (ready)
                    {
                        int branchValue = 2;
                    }

                    return 0;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectBranchAssignmentTarget.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement ifStatement = Assert.IsType<HirIfStatement>(method.Body.Statements[0]);
        HirVariableDeclarationStatement branchValue = Assert.IsType<HirVariableDeclarationStatement>(
            Assert.Single(ifStatement.Then.Statements));
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, branchValue);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                ifStatement.Id,
                returnStatement.Id,
                new LocalAssignmentPatchStatement(
                    branchValue.Id,
                    branchValue.Name,
                    intType,
                    new LiteralPatchExpression("3", intType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code ==
                SemanticPatchDiagnosticCodes.AssignmentTargetNotAvailable);
    }

    [Fact]
    public void Apply_RejectsAssignmentAfterBothBranchesTerminate()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool ready)
                {
                    int result = 0;
                    if (ready)
                    {
                        return 1;
                    }
                    else
                    {
                        return 2;
                    }
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectUnreachableAssignment.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirIfStatement terminalIf = Assert.IsType<HirIfStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                terminalIf.Id,
                null,
                new LocalAssignmentPatchStatement(
                    resultDefinition.Id,
                    resultDefinition.Name,
                    intType,
                    new LiteralPatchExpression("3", intType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnreachableInsertion);
    }

    [Fact]
    public void Apply_RejectsAssignmentAtEndOfBranchWhoseNestedPathsTerminate()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool outer, bool inner)
                {
                    int result = 0;
                    if (outer)
                    {
                        if (inner)
                        {
                            return 1;
                        }
                        else
                        {
                            return 2;
                        }
                    }

                    return result;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectNestedTerminalAssignment.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirIfStatement outerIf = Assert.IsType<HirIfStatement>(method.Body.Statements[1]);
        HirIfStatement terminalInnerIf = Assert.IsType<HirIfStatement>(
            Assert.Single(outerIf.Then.Statements));
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                outerIf.Then.Id,
                terminalInnerIf.Id,
                null,
                new LocalAssignmentPatchStatement(
                    resultDefinition.Id,
                    resultDefinition.Name,
                    intType,
                    new LiteralPatchExpression("3", intType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnreachableInsertion);
    }

    [Fact]
    public void Apply_RejectsAssignmentWhenTargetTypePreconditionIsWrong()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int seed, bool flag)
                {
                    int result = seed;
                    return result;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectAssignmentType.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId boolType = Assert.Single(
            original.Semantics!.Types,
            static type => type.MetadataName == "System.Boolean").Id;
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                resultDefinition.Id,
                returnStatement.Id,
                new LocalAssignmentPatchStatement(
                    resultDefinition.Id,
                    resultDefinition.Name,
                    boolType,
                    new LiteralPatchExpression("true", boolType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Apply_RejectsAssignmentWhenTargetNamePreconditionIsStale()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int seed)
                {
                    int result = seed;
                    return result;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectAssignmentName.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                resultDefinition.Id,
                returnStatement.Id,
                new LocalAssignmentPatchStatement(
                    resultDefinition.Id,
                    "renamedResult",
                    intType,
                    new LiteralPatchExpression("3", intType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code ==
                SemanticPatchDiagnosticCodes.AssignmentTargetNotAvailable);
    }

    [Fact]
    public void Apply_InsertsIntoEmptyExplicitBlockWithStableIndentation()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static void Run(int seed)
                {
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/InsertEmpty.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        SemanticId intType = GetTypeId(original, Assert.Single(method.Parameters));
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                null,
                new LocalLiteralDeclarationPatchStatement("value", "int", "1", intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("{\n        int value = 1;\n    }", Assert.Single(result.PatchedRequest!.Sources).Source);
    }

    [Fact]
    public void Apply_InsertsTypedIfWithExplicitBlocksAndSynchronizesGraphLayout()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static bool Run(long damage, bool disabled)
                {
                    int marker = 10;
                    return !disabled;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/InsertIf.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement marker = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId longType = GetTypeId(original, method.Parameters[0]);
        SemanticId boolType = GetTypeId(original, method.Parameters[1]);
        SemanticId intType = GetTypeId(original, marker);
        PatchExpression condition = new BinaryPatchExpression(
            "&&",
            new BinaryPatchExpression(
                ">",
                new CastPatchExpression(
                    "int",
                    new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        "damage",
                        longType),
                    intType),
                new BinaryPatchExpression(
                    "+",
                    new LocalReferencePatchExpression(marker.Id, marker.Name, intType),
                    new LiteralPatchExpression("10", intType),
                    intType),
                boolType),
            new UnaryPatchExpression(
                "!",
                new LocalReferencePatchExpression(
                    method.Parameters[1].Id,
                    "disabled",
                    boolType),
                boolType),
            boolType);
        GraphDocument originalGraph = new HirGraphProjector().Project(
            original.Hir,
            original.Semantics);
        GraphFunction originalFunction = Assert.Single(originalGraph.Functions);
        var originalLayout = new GraphLayoutDocument(
            GraphLayoutDocument.CurrentSchemaVersion,
            [new GraphFunctionLayout(
                originalFunction.Id,
                originalFunction.Nodes.Select((node, index) => new GraphNodeLayout(
                    node.Id,
                    GraphNodePlacement.Placed,
                    new GraphLayoutPoint(index * 200, index * 80),
                    new GraphLayoutSize(220, 90),
                    false)).ToArray(),
                [],
                [])],
            []);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                marker.Id,
                returnStatement.Id,
                new IfPatchStatement(condition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("if (", patchedSource);
        Assert.Contains("damage", patchedSource);
        Assert.Contains("!disabled", patchedSource);
        Assert.Contains("        {\n        }\n        else\n        {\n        }", patchedSource);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirIfStatement inserted = Assert.IsType<HirIfStatement>(changedMethod.Body.Statements[1]);
        Assert.Empty(inserted.Then.Statements);
        Assert.NotNull(inserted.Else);
        Assert.Empty(inserted.Else.Statements);
        HirBinaryExpression logicalAnd = Assert.IsType<HirBinaryExpression>(inserted.Condition);
        Assert.Equal("&&", logicalAnd.Operator);
        HirBinaryExpression comparison = Assert.IsType<HirBinaryExpression>(logicalAnd.Left);
        Assert.Equal(">", comparison.Operator);
        Assert.IsType<HirCastExpression>(comparison.Left);
        HirBinaryExpression offset = Assert.IsType<HirBinaryExpression>(comparison.Right);
        HirIdentifierExpression markerReference = Assert.IsType<HirIdentifierExpression>(offset.Left);
        Assert.Equal(
            marker.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == markerReference.Id).ReferencedSymbolId);
        Assert.IsType<HirUnaryExpression>(logicalAnd.Right);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);
        Assert.Equal(marker.Id, changedMethod.Body.Statements[0].Id);
        Assert.Equal(returnStatement.Id, changedMethod.Body.Statements[2].Id);

        IdentitySidecarDocument beforeIdentity = Assert.Single(original.IdentitySidecars);
        IdentitySidecarDocument afterIdentity = Assert.Single(result.Analysis.IdentitySidecars);
        HashSet<SemanticId> beforeIds = beforeIdentity.Entries.Select(static entry => entry.Id).ToHashSet();
        HashSet<SemanticId> afterIds = afterIdentity.Entries.Select(static entry => entry.Id).ToHashSet();
        Assert.All(beforeIds, id => Assert.Contains(id, afterIds));
        Assert.Equal(12, afterIds.Count - beforeIds.Count);
        Assert.Empty(afterIdentity.Tombstones);

        GraphDocument patchedGraph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphFunction patchedFunction = Assert.Single(patchedGraph.Functions);
        Assert.Contains(patchedFunction.Nodes, node => node.Id == inserted.Id && node.Kind == "branch");
        GraphLayoutDocument synchronized = new GraphLayoutSynchronizer().Synchronize(
            patchedGraph,
            originalLayout);
        GraphFunctionLayout synchronizedFunction = Assert.Single(synchronized.Functions);
        HashSet<SemanticId> originalNodeIds = originalFunction.Nodes
            .Select(static node => node.Id)
            .ToHashSet();
        Assert.Equal(
            originalNodeIds.Count,
            synchronizedFunction.Nodes.Count(node => originalNodeIds.Contains(node.NodeId)));
        Assert.All(
            synchronizedFunction.Nodes.Where(node => originalNodeIds.Contains(node.NodeId)),
            static node => Assert.Equal(GraphNodePlacement.Placed, node.Placement));
        GraphNodeLayout[] newLayouts = synchronizedFunction.Nodes
            .Where(node => !originalNodeIds.Contains(node.NodeId))
            .ToArray();
        Assert.Equal(10, newLayouts.Length);
        Assert.All(newLayouts, static node => Assert.Equal(GraphNodePlacement.Unplaced, node.Placement));
        Assert.Empty(synchronized.OrphanedNodes);
    }

    [Fact]
    public void Apply_RejectsIfConditionThatReferencesALaterLocal()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static bool Run()
                {
                    bool ready = true;
                    return ready;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectLaterIfSource.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement ready = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        SemanticId boolType = GetTypeId(original, ready);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                ready.Id,
                new IfPatchStatement(new LocalReferencePatchExpression(
                    ready.Id,
                    ready.Name,
                    boolType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.ValueSourceNotAvailable);
    }

    [Fact]
    public void Apply_InsertsTypedEarlyReturnAtTheEndOfAnExistingBranch()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static bool Run(bool ready)
                {
                    if (ready)
                    {
                    }

                    return false;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/InsertEarlyReturn.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement existingIf = Assert.IsType<HirIfStatement>(method.Body.Statements[0]);
        SemanticId boolType = GetTypeId(original, method);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                existingIf.Then.Id,
                null,
                null,
                new ReturnPatchStatement(
                    new LiteralPatchExpression("true", boolType),
                    boolType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("if (ready)\n        {\n            return true;", Assert.Single(
            result.PatchedRequest!.Sources).Source, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirIfStatement changedIf = Assert.IsType<HirIfStatement>(changedMethod.Body.Statements[0]);
        HirReturnStatement insertedReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(changedIf.Then.Statements));
        Assert.Equal("true", Assert.IsType<HirLiteralExpression>(insertedReturn.Expression).ValueText);
        Assert.Equal(existingIf.Then.Id, changedIf.Then.Id);
        Assert.Empty(Assert.Single(result.Analysis.IdentitySidecars).Tombstones);
    }

    [Fact]
    public void Apply_InsertsVoidReturnWithoutAValue()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static void Run(bool ready)
                {
                    if (ready)
                    {
                    }
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/InsertVoidReturn.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(original.Succeeded, string.Join(Environment.NewLine, original.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement existingIf = Assert.IsType<HirIfStatement>(Assert.Single(method.Body.Statements));
        SemanticId voidType = GetTypeId(original, method);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                existingIf.Then.Id,
                null,
                null,
                new ReturnPatchStatement(null, voidType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("{\n            return;\n        }", Assert.Single(
            result.PatchedRequest!.Sources).Source, StringComparison.Ordinal);
        HirIfStatement changedIf = Assert.IsType<HirIfStatement>(Assert.Single(
            Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods).Body.Statements));
        Assert.Null(Assert.IsType<HirReturnStatement>(Assert.Single(changedIf.Then.Statements)).Expression);
    }

    [Fact]
    public void Apply_RejectsWrongReturnTypeAndInsertionAfterReturn()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static bool Run(int value)
                {
                    if (value > 0)
                    {
                    }

                    return false;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectInvalidReturn.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(original.Succeeded, string.Join(Environment.NewLine, original.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement existingIf = Assert.IsType<HirIfStatement>(method.Body.Statements[0]);
        HirReturnStatement existingReturn = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId boolType = GetTypeId(original, method);
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticPatchDocument wrongType = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                existingIf.Then.Id,
                null,
                null,
                new ReturnPatchStatement(
                    new LiteralPatchExpression("1", intType),
                    boolType))]);
        SemanticPatchDocument unreachable = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                existingReturn.Id,
                null,
                new LocalLiteralDeclarationPatchStatement("late", "bool", "true", boolType))]);

        CSharpPatchResult wrongTypeResult = new CSharpSourcePatchApplier().Apply(request, wrongType);
        CSharpPatchResult unreachableResult = new CSharpSourcePatchApplier().Apply(request, unreachable);

        Assert.False(wrongTypeResult.Succeeded);
        Assert.Contains(
            wrongTypeResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
        Assert.False(unreachableResult.Succeeded);
        Assert.Contains(
            unreachableResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidStatement);
    }

    [Fact]
    public void Apply_RejectsReturnInsertionForAsyncTaskLikeMethod()
    {
        const string source = """
            using System.Threading.Tasks;
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static async Task<int> Run(bool ready)
                {
                    if (ready)
                    {
                    }

                    return 1;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectAsyncReturn.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(original.Succeeded, string.Join(Environment.NewLine, original.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement existingIf = Assert.IsType<HirIfStatement>(method.Body.Statements[0]);
        SemanticId taskType = GetTypeId(original, method);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                existingIf.Then.Id,
                null,
                null,
                new ReturnPatchStatement(null, taskType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidStatement);
    }

    [Fact]
    public void Apply_RejectsUnsafeGraphAuthoredOperator()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static bool Run(int value)
                {
                    return value > 0;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectUnsafeIfExpression.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirBinaryExpression comparison = Assert.IsType<HirBinaryExpression>(returnStatement.Expression);
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticId boolType = GetTypeId(original, comparison);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                returnStatement.Id,
                new IfPatchStatement(new BinaryPatchExpression(
                    ">",
                    new BinaryPatchExpression(
                        "/",
                        new LocalReferencePatchExpression(
                            method.Parameters[0].Id,
                            "value",
                            intType),
                        new LiteralPatchExpression("0", intType),
                        intType),
                    new LiteralPatchExpression("0", intType),
                    boolType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidGraphExpression);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_InsertsLoopTransferIntoTextAuthoredFor(bool isContinue)
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int count)
                {
                    for (int index = 0; index < count; index++)
                    {
                    }

                    return count;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ForLoopTransfer.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirForStatement loop = Assert.IsType<HirForStatement>(method.Body.Statements[0]);
        InsertPatchStatement transfer = isContinue
            ? new ContinuePatchStatement()
            : new BreakPatchStatement();
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(loop.Body.Id, null, null, transfer)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains(isContinue ? "continue;" : "break;", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(
            Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirForStatement changedLoop = Assert.IsType<HirForStatement>(changedMethod.Body.Statements[0]);
        HirStatement insertedTransfer = Assert.Single(changedLoop.Body.Statements);
        HirUnaryExpression increment = Assert.IsType<HirUnaryExpression>(
            Assert.Single(changedLoop.Incrementors));
        GraphControlFlowLoop flow = Assert.Single(
            Assert.Single(new GraphControlFlowAnalyzer().AnalyzeAndValidate(
                new HirGraphProjector().Project(
                    result.Analysis.Hir,
                    result.Analysis.Semantics)).Functions).Loops);
        Assert.Equal(changedLoop.Id, flow.LoopNodeId);
        Assert.Equal(increment.Id, flow.ContinueTargetNodeId);
        if (isContinue)
        {
            Assert.IsType<HirContinueStatement>(insertedTransfer);
            Assert.Contains(insertedTransfer.Id, flow.ContinueNodeIds);
            Assert.Empty(flow.BreakNodeIds);
        }
        else
        {
            Assert.IsType<HirBreakStatement>(insertedTransfer);
            Assert.Contains(insertedTransfer.Id, flow.BreakNodeIds);
            Assert.Empty(flow.ContinueNodeIds);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_RejectsLoopTransferOutsideAnEnclosingLoop(bool isContinue)
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static void Run()
                {
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectUnscopedLoopTransfer.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        Assert.Empty(method.Body.Statements);
        InsertPatchStatement transfer = isContinue
            ? new ContinuePatchStatement()
            : new BreakPatchStatement();
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                null,
                transfer)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidStatement);
    }

    [Fact]
    public void Apply_InsertsBreakIntoNestedSwitchBlockButRejectsContinue()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value, bool stop)
                {
                    switch (value)
                    {
                        case 0:
                            if (stop)
                            {
                            }
                            break;
                        default:
                            break;
                    }

                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/SwitchTransfer.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirSwitchStatement @switch = Assert.IsType<HirSwitchStatement>(method.Body.Statements[0]);
        HirIfStatement nestedIf = Assert.IsType<HirIfStatement>(
            @switch.Sections[0].Body.Statements[0]);

        SemanticPatchDocument breakPatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                nestedIf.Then.Id,
                null,
                null,
                new BreakPatchStatement())]);
        CSharpPatchResult breakResult = new CSharpSourcePatchApplier().Apply(request, breakPatch);

        Assert.True(
            breakResult.Succeeded,
            string.Join(Environment.NewLine, breakResult.Diagnostics));
        GraphControlFlowSwitch flow = Assert.Single(Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(
                new HirGraphProjector().Project(
                    breakResult.Analysis!.Hir!,
                    breakResult.Analysis.Semantics!)).Functions).Switches);
        Assert.Equal(3, flow.BreakNodeIds.Count);

        SemanticPatchDocument continuePatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new InsertStatementPatchOperation(
                nestedIf.Then.Id,
                null,
                null,
                new ContinuePatchStatement())]);
        CSharpPatchResult continueResult = new CSharpSourcePatchApplier().Apply(
            request,
            continuePatch);

        Assert.False(continueResult.Succeeded);
        Assert.Contains(
            continueResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidStatement);
    }

    [Fact]
    public void Apply_MovesStatementWithinOneBlockAndPreservesSemanticIdentity()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run()
                {
                    int first = 1;
                    // move with second
                    int second = 2;
                    int third = 3;
                    return first + second + third;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/MoveStatement.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement first = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirVariableDeclarationStatement second = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[1]);
        HirVariableDeclarationStatement third = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[2]);
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[3]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new MoveStatementPatchOperation(
                second.Id,
                method.Body.Id,
                first.Id,
                third.Id,
                method.Body.Id,
                third.Id,
                @return.Id)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.True(
            patchedSource.IndexOf("int third = 3;", StringComparison.Ordinal) <
            patchedSource.IndexOf("// move with second", StringComparison.Ordinal));
        HirMethodDeclaration changed = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        Assert.Equal(
            [first.Id, third.Id, second.Id, @return.Id],
            changed.Body.Statements.Select(static statement => statement.Id));
        Assert.Equal(
            ["first", "third", "second"],
            changed.Body.Statements
                .OfType<HirVariableDeclarationStatement>()
                .Select(static variable => variable.Name));
        Assert.True(Assert.Single(new GraphControlFlowAnalyzer().AnalyzeAndValidate(
            new HirGraphProjector().Project(
                result.Analysis.Hir,
                result.Analysis.Semantics)).Functions).IsReducible);
    }

    [Fact]
    public void Apply_MovesStatementAcrossStructuredRegionsAndPreservesSemanticIdentity()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool ready)
                {
                    int outside = 1;
                    if (ready)
                    {
                        int inside = 2;
                    }

                    return outside;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/RejectCrossRegionMove.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement outside = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirIfStatement branch = Assert.IsType<HirIfStatement>(method.Body.Statements[1]);
        HirVariableDeclarationStatement inside = Assert.IsType<HirVariableDeclarationStatement>(
            Assert.Single(branch.Then.Statements));
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[2]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new MoveStatementPatchOperation(
                inside.Id,
                branch.Then.Id,
                null,
                null,
                method.Body.Id,
                outside.Id,
                branch.Id)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.True(
            patchedSource.IndexOf("int inside = 2;", StringComparison.Ordinal) <
            patchedSource.IndexOf("if (ready)", StringComparison.Ordinal));
        HirMethodDeclaration changed = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        Assert.Equal(
            [outside.Id, inside.Id, branch.Id, @return.Id],
            changed.Body.Statements.Select(static candidate => candidate.Id));
        HirIfStatement changedBranch = Assert.IsType<HirIfStatement>(changed.Body.Statements[2]);
        Assert.Empty(changedBranch.Then.Statements);
        Assert.Equal(branch.Then.Id, changedBranch.Then.Id);
        Assert.Equal(
            method.DescendantsAndSelf().Select(static node => node.Id).OrderBy(static id => id.Value),
            changed.DescendantsAndSelf().Select(static node => node.Id).OrderBy(static id => id.Value));
        Assert.True(Assert.Single(new GraphControlFlowAnalyzer().AnalyzeAndValidate(
            new HirGraphProjector().Project(
                result.Analysis.Hir,
                result.Analysis.Semantics!)).Functions).IsReducible);
    }

    [Fact]
    public void Apply_MovesOuterStatementIntoBranchBody()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool ready)
                {
                    int result = 0;
                    if (ready)
                    {
                        result = 1;
                    }

                    // move into the branch
                    result += 2;
                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/MoveIntoBranch.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDeclaration =
            Assert.IsType<HirVariableDeclarationStatement>(method.Body.Statements[0]);
        HirIfStatement branch = Assert.IsType<HirIfStatement>(method.Body.Statements[1]);
        HirExpressionStatement branchAssignment = Assert.IsType<HirExpressionStatement>(
            Assert.Single(branch.Then.Statements));
        HirExpressionStatement increment = Assert.IsType<HirExpressionStatement>(
            method.Body.Statements[2]);
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[3]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new MoveStatementPatchOperation(
                increment.Id,
                method.Body.Id,
                branch.Id,
                @return.Id,
                branch.Then.Id,
                branchAssignment.Id,
                null)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        int branchStart = patchedSource.IndexOf("if (ready)", StringComparison.Ordinal);
        int movedStart = patchedSource.IndexOf("// move into the branch", StringComparison.Ordinal);
        int returnStart = patchedSource.IndexOf("return result;", StringComparison.Ordinal);
        Assert.True(branchStart < movedStart && movedStart < returnStart);
        Assert.Contains("            // move into the branch", patchedSource, StringComparison.Ordinal);
        Assert.Contains("            result += 2;", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changed = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        Assert.Equal(
            [resultDeclaration.Id, branch.Id, @return.Id],
            changed.Body.Statements.Select(static candidate => candidate.Id));
        HirIfStatement changedBranch = Assert.IsType<HirIfStatement>(changed.Body.Statements[1]);
        Assert.Equal(
            [branchAssignment.Id, increment.Id],
            changedBranch.Then.Statements.Select(static candidate => candidate.Id));
        Assert.True(Assert.Single(new GraphControlFlowAnalyzer().AnalyzeAndValidate(
            new HirGraphProjector().Project(
                result.Analysis.Hir,
                result.Analysis.Semantics!)).Functions).IsReducible);
    }

    [Fact]
    public void Apply_MovesStatementFromSwitchSectionIntoLoopBody()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool ready)
                {
                    int result = 0;
                    while (ready)
                    {
                        result += 1;
                        break;
                    }

                    switch (result)
                    {
                        case 0:
                            result += 2;
                            break;
                        default:
                            break;
                    }

                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/MoveSwitchStatementIntoLoop.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirWhileStatement loop = Assert.IsType<HirWhileStatement>(method.Body.Statements[1]);
        HirExpressionStatement loopAssignment = Assert.IsType<HirExpressionStatement>(
            loop.Body.Statements[0]);
        HirBreakStatement loopBreak = Assert.IsType<HirBreakStatement>(loop.Body.Statements[1]);
        HirSwitchStatement @switch = Assert.IsType<HirSwitchStatement>(method.Body.Statements[2]);
        HirExpressionStatement caseAssignment = Assert.IsType<HirExpressionStatement>(
            @switch.Sections[0].Body.Statements[0]);
        HirBreakStatement caseBreak = Assert.IsType<HirBreakStatement>(
            @switch.Sections[0].Body.Statements[1]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new MoveStatementPatchOperation(
                caseAssignment.Id,
                @switch.Sections[0].Body.Id,
                null,
                caseBreak.Id,
                loop.Body.Id,
                loopAssignment.Id,
                loopBreak.Id)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirMethodDeclaration changed = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirWhileStatement changedLoop = Assert.IsType<HirWhileStatement>(changed.Body.Statements[1]);
        Assert.Equal(
            [loopAssignment.Id, caseAssignment.Id, loopBreak.Id],
            changedLoop.Body.Statements.Select(static candidate => candidate.Id));
        HirSwitchStatement changedSwitch = Assert.IsType<HirSwitchStatement>(changed.Body.Statements[2]);
        Assert.Equal(
            [caseBreak.Id],
            changedSwitch.Sections[0].Body.Statements.Select(static candidate => candidate.Id));
        Assert.True(Assert.Single(new GraphControlFlowAnalyzer().AnalyzeAndValidate(
            new HirGraphProjector().Project(
                result.Analysis.Hir,
                result.Analysis.Semantics!)).Functions).IsReducible);
    }

    [Fact]
    public void Apply_RejectsStatementMoveAcrossFunctions()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int First()
                {
                    int first = 1;
                    return first;
                }

                [Visual]
                static int Second()
                {
                    int second = 2;
                    return second;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/RejectCrossFunctionMove.cs");
        HirMethodDeclaration[] methods = Assert.Single(original.Hir!.Types).Methods.ToArray();
        HirVariableDeclarationStatement first = Assert.IsType<HirVariableDeclarationStatement>(
            methods[0].Body.Statements[0]);
        HirReturnStatement firstReturn = Assert.IsType<HirReturnStatement>(
            methods[0].Body.Statements[1]);
        HirVariableDeclarationStatement second = Assert.IsType<HirVariableDeclarationStatement>(
            methods[1].Body.Statements[0]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new MoveStatementPatchOperation(
                first.Id,
                methods[0].Body.Id,
                null,
                firstReturn.Id,
                methods[1].Body.Id,
                second.Id,
                methods[1].Body.Statements[1].Id)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        UnifiedDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(SemanticPatchDiagnosticCodes.InvalidControlFlowReconnection, diagnostic.Code);
        Assert.Contains("between functions", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_RejectsCrossRegionMoveOfStructuredStatement()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool ready)
                {
                    if (ready)
                    {
                        int inside = 1;
                    }

                    return 0;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/RejectStructuredCrossRegionMove.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement branch = Assert.IsType<HirIfStatement>(method.Body.Statements[0]);
        HirVariableDeclarationStatement inside = Assert.IsType<HirVariableDeclarationStatement>(
            Assert.Single(branch.Then.Statements));
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new MoveStatementPatchOperation(
                branch.Id,
                method.Body.Id,
                null,
                @return.Id,
                branch.Then.Id,
                inside.Id,
                null)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        UnifiedDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(SemanticPatchDiagnosticCodes.InvalidControlFlowReconnection, diagnostic.Code);
        Assert.Contains("explicit branch", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_RejectsStatementMoveThatBreaksLocalOrdering()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run()
                {
                    int first = 1;
                    int second = first + 1;
                    return second;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/RejectDependencyMove.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement first = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirVariableDeclarationStatement second = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[1]);
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[2]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new MoveStatementPatchOperation(
                first.Id,
                method.Body.Id,
                null,
                second.Id,
                method.Body.Id,
                second.Id,
                @return.Id)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        UnifiedDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(SemanticPatchDiagnosticCodes.InvalidControlFlowReconnection, diagnostic.Code);
        Assert.Contains("before local 'first' is declared", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_RejectsWhileInsertionAfterAnAllPathsReturnBranch()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool ready)
                {
                    if (ready)
                    {
                        return 1;
                    }
                    else
                    {
                        return 0;
                    }
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectUnreachableWhile.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement existingIf = Assert.IsType<HirIfStatement>(Assert.Single(method.Body.Statements));
        SemanticId boolType = GetTypeId(original, method.Parameters[0]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                existingIf.Id,
                null,
                new WhilePatchStatement(new LiteralPatchExpression("true", boolType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnreachableInsertion);
    }

    [Fact]
    public void Apply_RejectsWhileConditionThatIsNotSystemBoolean()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    return value;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectNonBooleanWhile.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                returnStatement.Id,
                new WhilePatchStatement(new LiteralPatchExpression("1", intType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Apply_ConstructsTypedForAndReprojectsItsPhaseContract()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int count, bool enabled)
                {
                    return count;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ForBuild.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticId boolType = GetTypeId(original, method.Parameters[1]);
        var index = new ForLocalReferencePatchExpression(0, "index", intType);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                valueReturn.Id,
                new ForPatchStatement(
                    new ForVariableDeclarationPatch(
                        "int",
                        [new ForVariableDeclaratorPatch(
                            "index",
                            new LiteralPatchExpression("0", intType),
                            intType)]),
                    [],
                    new BinaryPatchExpression(
                        "<",
                        index,
                        new LocalReferencePatchExpression(
                            method.Parameters[0].Id,
                            method.Parameters[0].Name,
                            intType),
                        boolType),
                    [new ForMutationPatchExpression(index, "++", true, intType)]))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "for (int index = 0; index < count; index++)",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(
            Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirForStatement inserted = Assert.IsType<HirForStatement>(changedMethod.Body.Statements[0]);
        HirForVariableDeclarator variable = Assert.Single(inserted.Declaration!.Variables);
        HirUnaryExpression increment = Assert.IsType<HirUnaryExpression>(
            Assert.Single(inserted.Incrementors));
        Assert.True(increment.IsPostfix);
        Assert.Equal(method.Id, changedMethod.Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);
        Assert.Equal(valueReturn.Id, changedMethod.Body.Statements[1].Id);
        Assert.Equal(
            variable.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId ==
                    Assert.IsType<HirIdentifierExpression>(
                        Assert.IsType<HirBinaryExpression>(inserted.Condition).Left).Id)
                .ReferencedSymbolId);

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphControlFlowLoop flow = Assert.Single(
            Assert.Single(new GraphControlFlowAnalyzer().AnalyzeAndValidate(graph).Functions).Loops);
        Assert.Equal(inserted.Id, flow.LoopNodeId);
        Assert.NotNull(flow.InitializerRegionId);
        Assert.NotNull(flow.IncrementRegionId);
        Assert.Equal(increment.Id, flow.ContinueTargetNodeId);
    }

    [Fact]
    public void Apply_ConstructsForWithOrderedAssignmentInitializer()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int count, bool enabled)
                {
                    int index = count;
                    return index;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ForAssignmentBuild.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement indexDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, indexDefinition);
        SemanticId boolType = GetTypeId(original, method.Parameters[1]);
        var index = new LocalReferencePatchExpression(
            indexDefinition.Id,
            indexDefinition.Name,
            intType);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                indexDefinition.Id,
                valueReturn.Id,
                new ForPatchStatement(
                    null,
                    [new ForAssignmentPatchExpression(
                        indexDefinition.Id,
                        indexDefinition.Name,
                        intType,
                        new LiteralPatchExpression("0", intType))],
                    new BinaryPatchExpression(
                        "<",
                        index,
                        new LocalReferencePatchExpression(
                            method.Parameters[0].Id,
                            method.Parameters[0].Name,
                            intType),
                        boolType),
                    [new ForMutationPatchExpression(index, "++", true, intType)]))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "for (index = 0; index < count; index++)",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult changedAnalysis = Assert.IsType<CSharpAnalysisResult>(result.Analysis);
        HirCompilationUnit changedHir = Assert.IsType<HirCompilationUnit>(changedAnalysis.Hir);
        HirForStatement inserted = Assert.IsType<HirForStatement>(
            Assert.Single(changedHir.DescendantsAndSelf().OfType<HirForStatement>()));
        Assert.Null(inserted.Declaration);
        Assert.Single(inserted.Initializers);
        GraphFunction graph = Assert.Single(new HirGraphProjector()
            .Project(changedHir, changedAnalysis.Semantics)
            .Functions);
        GraphNode loopNode = Assert.Single(graph.Nodes, node => node.Id == inserted.Id);
        Assert.Equal("expressions", loopNode.Properties["initializerKind"]);
    }

    [Fact]
    public void Apply_RejectsInsertionWhenSiblingNameOrTypePreconditionIsStale()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/InsertConflict.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        SemanticId intType = GetTypeId(original, returnStatement.Expression!);
        var applier = new CSharpSourcePatchApplier();
        SemanticPatchDocument staleSibling = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                SemanticId.FromKey("stale-next-sibling"),
                new LocalLiteralDeclarationPatchStatement("scale", "int", "2", intType))]);
        SemanticPatchDocument nameConflict = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                returnStatement.Id,
                new LocalLiteralDeclarationPatchStatement("value", "int", "2", intType))]);
        SemanticPatchDocument wrongType = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                returnStatement.Id,
                new LocalLiteralDeclarationPatchStatement(
                    "scale",
                    "float",
                    "2.0f",
                    SemanticId.FromKey("stale-type")))]);

        CSharpPatchResult staleResult = applier.Apply(request, staleSibling);
        CSharpPatchResult conflictResult = applier.Apply(request, nameConflict);
        CSharpPatchResult typeResult = applier.Apply(request, wrongType);

        Assert.False(staleResult.Succeeded);
        Assert.Contains(
            staleResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.SiblingPreconditionFailed);
        Assert.False(conflictResult.Succeeded);
        Assert.Contains(
            conflictResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.NameConflict);
        Assert.False(typeResult.Succeeded);
        Assert.Contains(
            typeResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Apply_DeletesUnusedLiteralLocalAndTombstonesItsSubtree()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage)
                {
                    // Preserve this design note after deleting the graph node.
                    float unused = 2.0f;
                    return damage;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/DeleteLocal.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement variable = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HashSet<SemanticId> removedIds = variable.DescendantsAndSelf()
            .Select(static node => node.Id)
            .ToHashSet();
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new DeleteStatementPatchOperation(
                variable.Id,
                method.Body.Id,
                null,
                returnStatement.Id)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.DoesNotContain("float unused", patchedSource);
        Assert.Contains(
            "// Preserve this design note after deleting the graph node.\n        return damage;",
            patchedSource);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        Assert.Equal(returnStatement.Id, Assert.Single(changedMethod.Body.Statements).Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);

        IdentitySidecarDocument before = Assert.Single(original.IdentitySidecars);
        IdentitySidecarDocument after = Assert.Single(result.Analysis.IdentitySidecars);
        Assert.All(removedIds, id => Assert.Contains(after.Tombstones, tombstone => tombstone.Id == id));
        Assert.Equal(
            before.Entries.Select(static entry => entry.Id).Where(id => !removedIds.Contains(id)).ToHashSet(),
            after.Entries.Select(static entry => entry.Id).ToHashSet());
    }

    [Fact]
    public void Apply_RejectsDeletingReferencedLocal()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage)
                {
                    float scale = 2.0f;
                    return damage * scale;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(source, "Game/DeleteReferenced.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement variable = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new DeleteStatementPatchOperation(
                variable.Id,
                method.Body.Id,
                null,
                method.Body.Statements[1].Id)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.SymbolStillReferenced);
    }

    [Fact]
    public void Apply_DeletesLastStatementAndMovesTrailingCommentBeforeCloseBrace()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static void Run()
                {
                    int unused = 1; // Preserve this trailing note.
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(source, "Game/DeleteLast.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirStatement statement = Assert.Single(method.Body.Statements);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new DeleteStatementPatchOperation(statement.Id, method.Body.Id, null, null)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.DoesNotContain("int unused", patchedSource);
        Assert.Contains("        // Preserve this trailing note.\n    }", patchedSource);
    }

    [Fact]
    public void Apply_DeletesPureStaticCallStatementButRejectsEffectfulCall()
    {
        const string pureSource = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float value)
                {
                    System.MathF.Abs(value);
                    return value;
                }
            }
            """;
        const string effectfulSource = """
            using UnifiedScript;

            static class Api
            {
                public static void Notify(int value) { }
            }

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    Api.Notify(value);
                    return value;
                }
            }
            """;
        CSharpPatchResult pureResult = ApplyDeleteFirstStatement(pureSource, "Game/DeletePureCall.cs");
        CSharpPatchResult effectfulResult = ApplyDeleteFirstStatement(
            effectfulSource,
            "Game/DeleteEffectfulCall.cs");

        Assert.True(pureResult.Succeeded, string.Join(Environment.NewLine, pureResult.Diagnostics));
        string purePatchedSource = Assert.Single(pureResult.PatchedRequest!.Sources).Source;
        Assert.DoesNotContain("MathF.Abs", purePatchedSource);
        Assert.Contains("{\n        return value;", purePatchedSource);
        Assert.False(effectfulResult.Succeeded);
        Assert.Null(effectfulResult.PatchedRequest);
        Assert.Contains(
            effectfulResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnsafeDeletion);
    }

    [Fact]
    public void Apply_IntroducesTemporaryForNestedPureExpressionAndPreservesExistingIdentities()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage)
                {
                    return damage * 2.0f + 1.0f;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/IntroduceTemporary.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        HirBinaryExpression target = Assert.Single(
            returnStatement.DescendantsAndSelf()
                .OfType<HirBinaryExpression>(),
            static binary => binary.Operator == "*");
        HirBinaryExpression outer = Assert.IsType<HirBinaryExpression>(returnStatement.Expression);
        SemanticId floatType = GetTypeId(original, target);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new IntroduceTemporaryPatchOperation(
                target.Id,
                returnStatement.Id,
                method.Body.Id,
                null,
                null,
                "sharedDamage",
                "float",
                floatType)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains(
            "float sharedDamage = damage * 2.0f;\n        return sharedDamage + 1.0f;",
            patchedSource);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement temporary = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[0]);
        HirBinaryExpression movedTarget = Assert.IsType<HirBinaryExpression>(temporary.Initializer);
        HirReturnStatement changedReturn = Assert.IsType<HirReturnStatement>(changedMethod.Body.Statements[1]);
        HirBinaryExpression changedOuter = Assert.IsType<HirBinaryExpression>(changedReturn.Expression);
        HirIdentifierExpression temporaryUse = Assert.IsType<HirIdentifierExpression>(changedOuter.Left);
        Assert.Equal(target.Id, movedTarget.Id);
        Assert.Equal(outer.Id, changedOuter.Id);
        Assert.Equal(returnStatement.Id, changedReturn.Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);

        BoundNodeInfo useBinding = Assert.Single(
            result.Analysis.Semantics!.Nodes,
            binding => binding.NodeId == temporaryUse.Id);
        Assert.Equal(temporary.Id, useBinding.ReferencedSymbolId);

        IdentitySidecarDocument before = Assert.Single(original.IdentitySidecars);
        IdentitySidecarDocument after = Assert.Single(result.Analysis.IdentitySidecars);
        HashSet<SemanticId> afterIds = after.Entries.Select(static entry => entry.Id).ToHashSet();
        Assert.All(before.Entries, entry => Assert.Contains(entry.Id, afterIds));
        Assert.Equal(before.Entries.Count + 2, after.Entries.Count);
        Assert.Empty(after.Tombstones);

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphNode graphUse = Assert.Single(
            Assert.Single(graph.Functions).Nodes,
            node => node.Id == temporaryUse.Id);
        Assert.Equal(temporary.Id.Value, graphUse.Properties["referencedSymbolId"]);
    }

    [Fact]
    public void Apply_IntroducesTemporaryBeforeLocalInitializerWithSiblingGuards()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage)
                {
                    float scaled = damage * 2.0f;
                    return scaled;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/IntroduceBeforeLocal.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement local = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HirExpression target = local.Initializer!;
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new IntroduceTemporaryPatchOperation(
                target.Id,
                local.Id,
                method.Body.Id,
                null,
                returnStatement.Id,
                "sharedDamage",
                "float",
                GetTypeId(original, target))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "float sharedDamage = damage * 2.0f;\n        float scaled = sharedDamage;",
            Assert.Single(result.PatchedRequest!.Sources).Source);
    }

    [Fact]
    public void Apply_IntroducesTemporaryForResolvedPureStaticCall()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float value)
                {
                    return System.MathF.Abs(value) + 1.0f;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/PureCallTemporary.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        HirInvocationExpression invocation = Assert.Single(
            returnStatement.DescendantsAndSelf().OfType<HirInvocationExpression>());
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new IntroduceTemporaryPatchOperation(
                invocation.Id,
                returnStatement.Id,
                method.Body.Id,
                null,
                null,
                "magnitude",
                "float",
                GetTypeId(original, invocation))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "float magnitude = System.MathF.Abs(value);\n        return magnitude + 1.0f;",
            Assert.Single(result.PatchedRequest!.Sources).Source);
    }

    [Fact]
    public void Apply_RejectsTemporaryIntroductionAcrossEffectfulEvaluationOrStaleContext()
    {
        const string source = """
            using UnifiedScript;

            static class Api
            {
                public static float Read() => 1.0f;
            }

            static class Script
            {
                [Visual]
                static float Run(float value)
                {
                    return Api.Read() + value;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/UnsafeTemporary.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(
            Assert.Single(original.Hir!.Types, static type => type.Name == "Script").Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        HirIdentifierExpression target = Assert.Single(
            returnStatement.DescendantsAndSelf()
                .OfType<HirIdentifierExpression>(),
            static identifier => identifier.Name == "value");
        SemanticId floatType = GetTypeId(original, target);
        IntroduceTemporaryPatchOperation operation = new(
            target.Id,
            returnStatement.Id,
            method.Body.Id,
            null,
            null,
            "sharedValue",
            "float",
            floatType);
        SemanticPatchDocument unsafePatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [operation]);
        SemanticPatchDocument stalePatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [operation with { ExpectedContainingStatementId = SemanticId.FromKey("stale-statement") }]);

        CSharpPatchResult unsafeResult = new CSharpSourcePatchApplier().Apply(request, unsafePatch);
        CSharpPatchResult staleResult = new CSharpSourcePatchApplier().Apply(request, stalePatch);

        Assert.False(unsafeResult.Succeeded);
        Assert.Contains(
            unsafeResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnsafeTemporaryIntroduction);
        Assert.False(staleResult.Succeeded);
        Assert.Contains(
            staleResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.PreconditionFailed);
    }

    [Fact]
    public void Apply_RejectsTemporaryIntroductionAcrossShortCircuitEvaluation()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static bool Run(bool enabled, int value, int divisor)
                {
                    return enabled && value / divisor > 1;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/ShortCircuitTemporary.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        HirBinaryExpression division = Assert.Single(
            returnStatement.DescendantsAndSelf().OfType<HirBinaryExpression>(),
            static binary => binary.Operator == "/");
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new IntroduceTemporaryPatchOperation(
                division.Id,
                returnStatement.Id,
                method.Body.Id,
                null,
                null,
                "quotient",
                "int",
                GetTypeId(original, division))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnsafeTemporaryIntroduction);
    }

    [Fact]
    public void Apply_ReconnectsConsumerToDominatingLocalAndTombstonesRemovedExpression()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage, float bonus)
                {
                    float sharedDamage = damage * 2.0f;
                    return bonus + 1.0f;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ReconnectValue.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement sourceDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HirBinaryExpression outer = Assert.IsType<HirBinaryExpression>(returnStatement.Expression);
        HirLiteralExpression target = Assert.IsType<HirLiteralExpression>(outer.Right);
        HashSet<SemanticId> removedIds = target.DescendantsAndSelf()
            .Select(static node => node.Id)
            .ToHashSet();
        SemanticId floatType = GetTypeId(original, target);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectValuePatchOperation(
                target.Id,
                sourceDefinition.Id,
                returnStatement.Id,
                method.Body.Id,
                sourceDefinition.Id,
                null,
                floatType,
                GetTypeId(original, sourceDefinition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "float sharedDamage = damage * 2.0f;\n        return bonus + sharedDamage;",
            Assert.Single(result.PatchedRequest!.Sources).Source);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement changedDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[0]);
        HirReturnStatement changedReturn = Assert.IsType<HirReturnStatement>(changedMethod.Body.Statements[1]);
        HirBinaryExpression changedOuter = Assert.IsType<HirBinaryExpression>(changedReturn.Expression);
        HirIdentifierExpression newUse = Assert.IsType<HirIdentifierExpression>(changedOuter.Right);
        Assert.Equal(sourceDefinition.Id, changedDefinition.Id);
        Assert.Equal(outer.Id, changedOuter.Id);
        Assert.Equal(returnStatement.Id, changedReturn.Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);

        BoundNodeInfo useBinding = Assert.Single(
            result.Analysis.Semantics!.Nodes,
            binding => binding.NodeId == newUse.Id);
        Assert.Equal(sourceDefinition.Id, useBinding.ReferencedSymbolId);

        IdentitySidecarDocument before = Assert.Single(original.IdentitySidecars);
        IdentitySidecarDocument after = Assert.Single(result.Analysis.IdentitySidecars);
        HashSet<SemanticId> beforeIds = before.Entries.Select(static entry => entry.Id).ToHashSet();
        HashSet<SemanticId> afterIds = after.Entries.Select(static entry => entry.Id).ToHashSet();
        Assert.All(
            beforeIds.Where(id => !removedIds.Contains(id)),
            id => Assert.Contains(id, afterIds));
        Assert.All(
            removedIds,
            id => Assert.Contains(after.Tombstones, tombstone => tombstone.Id == id));
        SemanticId allocatedUseId = Assert.Single(afterIds, id => !beforeIds.Contains(id));
        Assert.Equal(newUse.Id, allocatedUseId);

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphNode graphUse = Assert.Single(
            Assert.Single(graph.Functions).Nodes,
            node => node.Id == newUse.Id);
        Assert.Equal("identifier", graphUse.Kind);
        Assert.Equal(sourceDefinition.Id.Value, graphUse.Properties["referencedSymbolId"]);
    }

    [Fact]
    public void Apply_ReconnectsNestedConsumerWhenRootLocalDominatesBranch()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage, bool enabled)
                {
                    float sharedDamage = damage * 2.0f;
                    if (enabled)
                    {
                        return 1.0f;
                    }

                    return sharedDamage;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/ReconnectNestedValue.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement sourceDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirIfStatement ifStatement = Assert.IsType<HirIfStatement>(method.Body.Statements[1]);
        HirReturnStatement nestedReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(ifStatement.Then.Statements));
        HirExpression target = Assert.IsType<HirLiteralExpression>(nestedReturn.Expression);
        SemanticId floatType = GetTypeId(original, target);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectValuePatchOperation(
                target.Id,
                sourceDefinition.Id,
                nestedReturn.Id,
                ifStatement.Then.Id,
                null,
                null,
                floatType,
                GetTypeId(original, sourceDefinition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("return sharedDamage;", patchedSource);
        Assert.Equal(2, patchedSource.Split("return sharedDamage;", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Apply_RejectsValueSourceThatDoesNotDominateConsumer()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage, bool enabled)
                {
                    if (enabled)
                    {
                        float branchValue = damage * 2.0f;
                    }

                    return 1.0f;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectNonDominatingValue.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement ifStatement = Assert.IsType<HirIfStatement>(method.Body.Statements[0]);
        HirVariableDeclarationStatement sourceDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            Assert.Single(ifStatement.Then.Statements));
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HirExpression target = Assert.IsType<HirLiteralExpression>(returnStatement.Expression);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectValuePatchOperation(
                target.Id,
                sourceDefinition.Id,
                returnStatement.Id,
                method.Body.Id,
                ifStatement.Id,
                null,
                GetTypeId(original, target),
                GetTypeId(original, sourceDefinition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.ValueSourceNotAvailable);
    }

    [Fact]
    public void Apply_RejectsValueReconnectionIntoUnreachableConsumer()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run()
                {
                    int sourceValue = 1;
                    return sourceValue;
                    int lateValue = 2;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectUnreachableReconnect.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement sourceDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HirVariableDeclarationStatement lateDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[2]);
        HirExpression target = Assert.IsType<HirLiteralExpression>(lateDefinition.Initializer);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectValuePatchOperation(
                target.Id,
                sourceDefinition.Id,
                lateDefinition.Id,
                method.Body.Id,
                returnStatement.Id,
                null,
                GetTypeId(original, target),
                GetTypeId(original, sourceDefinition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.ValueSourceNotAvailable);
    }

    [Fact]
    public void Apply_RejectsValueReconnectionThatRetargetsAssignmentWrite()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run()
                {
                    int sourceValue = 1;
                    int targetValue = 2;
                    targetValue = 3;
                    return targetValue;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectAssignmentRetarget.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement sourceDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirVariableDeclarationStatement targetDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[1]);
        HirExpressionStatement assignmentStatement = Assert.IsType<HirExpressionStatement>(
            method.Body.Statements[2]);
        HirAssignmentExpression assignment = Assert.IsType<HirAssignmentExpression>(
            assignmentStatement.Expression);
        HirIdentifierExpression target = Assert.IsType<HirIdentifierExpression>(assignment.Target);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[3]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectValuePatchOperation(
                target.Id,
                sourceDefinition.Id,
                assignmentStatement.Id,
                method.Body.Id,
                targetDefinition.Id,
                returnStatement.Id,
                GetTypeId(original, target),
                GetTypeId(original, sourceDefinition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnsafeValueReconnection);
    }

    [Fact]
    public void Apply_RejectsRemovingEffectfulConsumerDuringValueReconnection()
    {
        const string source = """
            using UnifiedScript;

            static class Api
            {
                public static float Read() => 1.0f;
            }

            static class Script
            {
                [Visual]
                static float Run(float value)
                {
                    float sharedValue = value * 2.0f;
                    return Api.Read();
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectEffectfulReconnect.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(
            Assert.Single(original.Hir!.Types, static type => type.Name == "Script").Methods);
        HirVariableDeclarationStatement sourceDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HirExpression target = Assert.IsType<HirInvocationExpression>(returnStatement.Expression);
        SemanticId floatType = GetTypeId(original, target);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectValuePatchOperation(
                target.Id,
                sourceDefinition.Id,
                returnStatement.Id,
                method.Body.Id,
                sourceDefinition.Id,
                null,
                floatType,
                GetTypeId(original, sourceDefinition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnsafeValueReconnection);
    }

    [Fact]
    public void Apply_RejectsValueReconnectionThatWouldDiscardCommentTrivia()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float value)
                {
                    float sharedValue = value * 2.0f;
                    return (value + /* keep this rationale */ 1.0f);
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectReconnectComment.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement sourceDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HirBinaryExpression target = Assert.IsType<HirBinaryExpression>(returnStatement.Expression);
        SemanticId floatType = GetTypeId(original, target);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectValuePatchOperation(
                target.Id,
                sourceDefinition.Id,
                returnStatement.Id,
                method.Body.Id,
                sourceDefinition.Id,
                null,
                floatType,
                GetTypeId(original, sourceDefinition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnsafeValueReconnection);
    }

    [Fact]
    public void Apply_RejectsValueReconnectionAcrossDifferentSemanticTypes()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float value)
                {
                    float sharedValue = value * 2.0f;
                    int consumer = 1;
                    return sharedValue + consumer;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectReconnectType.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement sourceDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirVariableDeclarationStatement consumer = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[1]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[2]);
        HirExpression target = Assert.IsType<HirLiteralExpression>(consumer.Initializer);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectValuePatchOperation(
                target.Id,
                sourceDefinition.Id,
                consumer.Id,
                method.Body.Id,
                sourceDefinition.Id,
                returnStatement.Id,
                GetTypeId(original, target),
                GetTypeId(original, sourceDefinition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
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

    private static CSharpPatchResult ApplyDeleteFirstStatement(string source, string path)
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(source, path);
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(original.Succeeded, string.Join(Environment.NewLine, original.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirStatement target = method.Body.Statements[0];
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new DeleteStatementPatchOperation(
                target.Id,
                method.Body.Id,
                null,
                method.Body.Statements[1].Id)]);
        return new CSharpSourcePatchApplier().Apply(request, patch);
    }

    [Fact]
    public void Apply_PatchesLoopConditionWithoutRecreatingLoopIdentity()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value)
                {
                    while (value > 10)
                    {
                        value = value - 1;
                    }

                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) =
            CreatePersistentRequest(source, "Game/LoopPatch.cs");
        HirCompilationUnit originalHir = original.Hir!;
        HirWhileStatement originalLoop = Assert.Single(
            originalHir.DescendantsAndSelf().OfType<HirWhileStatement>());
        HirLiteralExpression literal = Assert.Single(
            originalLoop.Condition.DescendantsAndSelf().OfType<HirLiteralExpression>());
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            originalHir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceLiteralPatchOperation(literal.Id, "10", "100")]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("while (value > 100)", Assert.Single(result.PatchedRequest!.Sources).Source);
        CSharpAnalysisResult changedAnalysis = result.Analysis!;
        HirCompilationUnit changedHir = changedAnalysis.Hir!;
        HirWhileStatement changedLoop = Assert.Single(
            changedHir.DescendantsAndSelf().OfType<HirWhileStatement>());
        Assert.Equal(originalLoop.Id, changedLoop.Id);
        Assert.Equal(
            Assert.Single(original.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet(),
            Assert.Single(changedAnalysis.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet());
        Assert.Empty(Assert.Single(changedAnalysis.IdentitySidecars).Tombstones);
        GraphControlFlowLoop loop = Assert.Single(Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(
                new HirGraphProjector().Project(changedHir, changedAnalysis.Semantics)).Functions).Loops);
        Assert.Equal(changedLoop.Id, loop.LoopNodeId);
    }

    [Fact]
    public void Apply_EditsForClauseExpressionWithoutRecreatingForStructure()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value)
                {
                    for (int index = 0; index < 3; index++)
                    {
                        value = value + 1;
                    }

                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) =
            CreatePersistentRequest(source, "Game/ForClauseEdit.cs");
        HirCompilationUnit originalHir = Assert.IsType<HirCompilationUnit>(original.Hir);
        HirForStatement originalLoop = Assert.Single(
            originalHir.DescendantsAndSelf().OfType<HirForStatement>());
        HirForVariableDeclaration originalDeclaration = Assert.IsType<HirForVariableDeclaration>(
            originalLoop.Declaration);
        HirForVariableDeclarator originalVariable = Assert.Single(originalDeclaration.Variables);
        HirBinaryExpression originalCondition = Assert.IsType<HirBinaryExpression>(
            originalLoop.Condition);
        HirLiteralExpression originalLimit = Assert.IsType<HirLiteralExpression>(
            originalCondition.Right);
        HirUnaryExpression originalIncrement = Assert.IsType<HirUnaryExpression>(
            Assert.Single(originalLoop.Incrementors));
        SemanticId intType = GetTypeId(original, originalLimit);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            originalHir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                originalCondition.Id,
                "right",
                originalLimit.Id,
                intType,
                GetTypeId(original, originalCondition),
                new LiteralPatchExpression("4", intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "for (int index = 0; index < 4; index++)",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult changedAnalysis = Assert.IsType<CSharpAnalysisResult>(result.Analysis);
        HirCompilationUnit changedHir = Assert.IsType<HirCompilationUnit>(changedAnalysis.Hir);
        HirForStatement changedLoop = Assert.Single(
            changedHir.DescendantsAndSelf().OfType<HirForStatement>());
        HirForVariableDeclaration changedDeclaration = Assert.IsType<HirForVariableDeclaration>(
            changedLoop.Declaration);
        HirForVariableDeclarator changedVariable = Assert.Single(changedDeclaration.Variables);
        HirBinaryExpression changedCondition = Assert.IsType<HirBinaryExpression>(
            changedLoop.Condition);
        HirLiteralExpression changedLimit = Assert.IsType<HirLiteralExpression>(
            changedCondition.Right);
        HirUnaryExpression changedIncrement = Assert.IsType<HirUnaryExpression>(
            Assert.Single(changedLoop.Incrementors));
        Assert.Equal(originalLoop.Id, changedLoop.Id);
        Assert.Equal(originalLoop.Body.Id, changedLoop.Body.Id);
        Assert.Equal(originalDeclaration.Id, changedDeclaration.Id);
        Assert.Equal(originalVariable.Id, changedVariable.Id);
        Assert.Equal(originalCondition.Id, changedCondition.Id);
        Assert.Equal(originalIncrement.Id, changedIncrement.Id);
        Assert.NotEqual(originalLimit.Id, changedLimit.Id);
        Assert.Equal("4", changedLimit.ValueText);
        IdentitySidecarDocument identity = Assert.Single(changedAnalysis.IdentitySidecars);
        Assert.Contains(identity.Tombstones, tombstone => tombstone.Id == originalLimit.Id);

        GraphControlFlowLoop flow = Assert.Single(Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(
                new HirGraphProjector().Project(
                    changedHir,
                    changedAnalysis.Semantics)).Functions).Loops);
        Assert.Equal(changedLoop.Id, flow.LoopNodeId);
        Assert.NotNull(flow.InitializerRegionId);
        Assert.NotNull(flow.IncrementRegionId);
        Assert.Equal(changedIncrement.Id, flow.ContinueTargetNodeId);
    }

    [Fact]
    public void Apply_ReplacesAssignmentValueAndWhileConditionWithoutRecreatingStructure()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value, int stopAt, bool enabled)
                {
                    int result = value;
                    while (result > 0)
                    {
                        result = result - 1;
                    }

                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) =
            CreatePersistentRequest(source, "Game/StructuredExpressionEdit.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirWhileStatement loop = Assert.IsType<HirWhileStatement>(method.Body.Statements[1]);
        HirExpressionStatement assignmentStatement = Assert.IsType<HirExpressionStatement>(
            Assert.Single(loop.Body.Statements));
        HirAssignmentExpression assignment = Assert.IsType<HirAssignmentExpression>(
            assignmentStatement.Expression);
        HirIdentifierExpression assignmentTarget = Assert.IsType<HirIdentifierExpression>(assignment.Target);
        HashSet<SemanticId> replacedExpressionIds = assignment.Value
            .DescendantsAndSelf()
            .Concat(loop.Condition.DescendantsAndSelf())
            .Select(static node => node.Id)
            .ToHashSet();
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticId boolType = GetTypeId(original, loop.Condition);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                new ReplaceAssignmentValuePatchOperation(
                    assignment.Id,
                    assignment.Operator,
                    assignment.Target.Id,
                    resultDefinition.Id,
                    resultDefinition.Name,
                    assignment.Value.Id,
                    intType,
                    new BinaryPatchExpression(
                        "-",
                        new LocalReferencePatchExpression(
                            resultDefinition.Id,
                            resultDefinition.Name,
                            intType),
                        new LiteralPatchExpression("2", intType),
                        intType)),
                new ReplaceWhileConditionPatchOperation(
                    loop.Id,
                    loop.Condition.Id,
                    boolType,
                    new BinaryPatchExpression(
                        "&&",
                        new LocalReferencePatchExpression(
                            method.Parameters[2].Id,
                            method.Parameters[2].Name,
                            boolType),
                        new BinaryPatchExpression(
                            ">",
                            new LocalReferencePatchExpression(
                                resultDefinition.Id,
                                resultDefinition.Name,
                                intType),
                            new LocalReferencePatchExpression(
                                method.Parameters[1].Id,
                                method.Parameters[1].Name,
                                intType),
                            boolType),
                        boolType)),
            ]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("while (enabled && (result > stopAt))", patchedSource, StringComparison.Ordinal);
        Assert.Contains("result = result - 2;", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirWhileStatement changedLoop = Assert.IsType<HirWhileStatement>(changedMethod.Body.Statements[1]);
        HirExpressionStatement changedAssignmentStatement = Assert.IsType<HirExpressionStatement>(
            Assert.Single(changedLoop.Body.Statements));
        HirAssignmentExpression changedAssignment = Assert.IsType<HirAssignmentExpression>(
            changedAssignmentStatement.Expression);
        Assert.Equal(method.Id, changedMethod.Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);
        Assert.Equal(loop.Id, changedLoop.Id);
        Assert.Equal(loop.Body.Id, changedLoop.Body.Id);
        Assert.Equal(assignmentStatement.Id, changedAssignmentStatement.Id);
        Assert.Equal(assignment.Id, changedAssignment.Id);
        Assert.Equal(assignmentTarget.Id, changedAssignment.Target.Id);
        Assert.Equal(
            resultDefinition.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == changedAssignment.Target.Id).ReferencedSymbolId);

        IdentitySidecarDocument afterIdentity = Assert.Single(result.Analysis.IdentitySidecars);
        HashSet<SemanticId> liveIds = afterIdentity.Entries
            .Select(static entry => entry.Id)
            .ToHashSet();
        HashSet<SemanticId> tombstoneIds = afterIdentity.Tombstones
            .Select(static tombstone => tombstone.Id)
            .ToHashSet();
        Assert.All(
            replacedExpressionIds,
            id => Assert.True(liveIds.Contains(id) ^ tombstoneIds.Contains(id)));
        Assert.Contains(loop.Condition.Id, tombstoneIds);
        Assert.Contains(
            Assert.Single(assignment.Value.DescendantsAndSelf().OfType<HirLiteralExpression>()).Id,
            tombstoneIds);
        Assert.Contains(
            Assert.Single(loop.Condition.DescendantsAndSelf().OfType<HirLiteralExpression>()).Id,
            tombstoneIds);
        SemanticId[] preservedIds =
        [
            method.Body.Id,
            resultDefinition.Id,
            loop.Id,
            loop.Body.Id,
            assignmentStatement.Id,
            assignment.Id,
            assignmentTarget.Id,
        ];
        Assert.All(
            preservedIds,
            id => Assert.Contains(afterIdentity.Entries, entry => entry.Id == id));

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphFunction function = Assert.Single(graph.Functions);
        Assert.Contains(function.Nodes, node => node.Id == loop.Id && node.Kind == "loop");
        Assert.Contains(function.Nodes, node => node.Id == assignment.Id && node.Kind == "assignment");
        GraphControlFlowFunction flow = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(graph).Functions);
        GraphControlFlowLoop changedFlow = Assert.Single(flow.Loops);
        Assert.True(flow.IsReducible);
        Assert.Equal(loop.Id, changedFlow.LoopNodeId);
        Assert.Equal(assignment.Id, changedFlow.BodyEntryNodeId);
        Assert.NotEmpty(changedFlow.BackEdgeArcIds);
    }

    [Fact]
    public void Apply_ReplacesDoWhileConditionWithoutRecreatingBodyOrLoopIdentity()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value, int stopAt, bool enabled)
                {
                    int result = value;
                    do
                    {
                        result = result - 1;
                    }
                    while (result > 0);

                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) =
            CreatePersistentRequest(source, "Game/DoWhileConditionEdit.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirDoWhileStatement loop = Assert.IsType<HirDoWhileStatement>(method.Body.Statements[1]);
        HirExpressionStatement bodyStatement = Assert.IsType<HirExpressionStatement>(
            Assert.Single(loop.Body.Statements));
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticId boolType = GetTypeId(original, loop.Condition);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceDoWhileConditionPatchOperation(
                loop.Id,
                loop.Condition.Id,
                boolType,
                new BinaryPatchExpression(
                    "&&",
                    new LocalReferencePatchExpression(
                        method.Parameters[2].Id,
                        method.Parameters[2].Name,
                        boolType),
                    new BinaryPatchExpression(
                        ">",
                        new LocalReferencePatchExpression(
                            resultDefinition.Id,
                            resultDefinition.Name,
                            intType),
                        new LocalReferencePatchExpression(
                            method.Parameters[1].Id,
                            method.Parameters[1].Name,
                            intType),
                        boolType),
                    boolType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("while (enabled && (result > stopAt));", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirDoWhileStatement changedLoop = Assert.IsType<HirDoWhileStatement>(
            changedMethod.Body.Statements[1]);
        Assert.Equal(method.Id, changedMethod.Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);
        Assert.Equal(loop.Id, changedLoop.Id);
        Assert.Equal(loop.Body.Id, changedLoop.Body.Id);
        Assert.Equal(bodyStatement.Id, Assert.Single(changedLoop.Body.Statements).Id);
        Assert.NotEqual(loop.Condition.Id, changedLoop.Condition.Id);
    }

    [Fact]
    public void Apply_RejectsAssignmentValueReplacementWithAStaleEndpoint()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value)
                {
                    int result = value;
                    result = result - 1;
                    return result;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/StaleAssignmentValueEdit.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement definition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirAssignmentExpression assignment = Assert.IsType<HirAssignmentExpression>(
            Assert.IsType<HirExpressionStatement>(method.Body.Statements[1]).Expression);
        SemanticId intType = GetTypeId(original, definition);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceAssignmentValuePatchOperation(
                assignment.Id,
                assignment.Operator,
                assignment.Target.Id,
                definition.Id,
                definition.Name,
                SemanticId.FromKey("stale-assignment-value"),
                intType,
                new LiteralPatchExpression("2", intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.PreconditionFailed);
    }

    [Fact]
    public void Apply_RejectsWhileConditionReplacementThatWouldDiscardAComment()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value, bool enabled)
                {
                    while (value /* keep threshold rationale */ > 0)
                    {
                        value = value - 1;
                    }

                    return value;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/CommentedLoopConditionEdit.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirWhileStatement loop = Assert.IsType<HirWhileStatement>(method.Body.Statements[0]);
        SemanticId boolType = GetTypeId(original, loop.Condition);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceWhileConditionPatchOperation(
                loop.Id,
                loop.Condition.Id,
                boolType,
                new LocalReferencePatchExpression(
                    method.Parameters[1].Id,
                    method.Parameters[1].Name,
                    boolType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidReplacement);
    }

    [Fact]
    public void Apply_ReplacesIfConditionLocalInitializerAndReturnValueWithoutRecreatingStatements()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value, int stopAt, bool enabled)
                {
                    int result = value + 1;
                    if (enabled)
                    {
                        return result;
                    }

                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/GeneralExpressionEndpointEdit.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement local = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirIfStatement branch = Assert.IsType<HirIfStatement>(method.Body.Statements[1]);
        HirReturnStatement branchReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(branch.Then.Statements));
        HirExpression originalInitializer = Assert.IsAssignableFrom<HirExpression>(local.Initializer);
        HirExpression originalReturnValue = Assert.IsAssignableFrom<HirExpression>(branchReturn.Expression);
        SemanticId intType = GetTypeId(original, local);
        SemanticId boolType = GetTypeId(original, branch.Condition);
        HashSet<SemanticId> oldEndpointIds = originalInitializer
            .DescendantsAndSelf()
            .Concat(branch.Condition.DescendantsAndSelf())
            .Concat(originalReturnValue.DescendantsAndSelf())
            .Select(static node => node.Id)
            .ToHashSet();
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                new ReplaceLocalInitializerPatchOperation(
                    local.Id,
                    local.Name,
                    originalInitializer.Id,
                    intType,
                    new BinaryPatchExpression(
                        "-",
                        new LocalReferencePatchExpression(
                            method.Parameters[0].Id,
                            method.Parameters[0].Name,
                            intType),
                        new LiteralPatchExpression("2", intType),
                        intType)),
                new ReplaceIfConditionPatchOperation(
                    branch.Id,
                    branch.Condition.Id,
                    boolType,
                    new BinaryPatchExpression(
                        "&&",
                        new LocalReferencePatchExpression(
                            method.Parameters[2].Id,
                            method.Parameters[2].Name,
                            boolType),
                        new BinaryPatchExpression(
                            ">",
                            new LocalReferencePatchExpression(local.Id, local.Name, intType),
                            new LocalReferencePatchExpression(
                                method.Parameters[1].Id,
                                method.Parameters[1].Name,
                                intType),
                            boolType),
                        boolType)),
                new ReplaceReturnValuePatchOperation(
                    branchReturn.Id,
                    originalReturnValue.Id,
                    intType,
                    new BinaryPatchExpression(
                        "+",
                        new LocalReferencePatchExpression(local.Id, local.Name, intType),
                        new LocalReferencePatchExpression(
                            method.Parameters[1].Id,
                            method.Parameters[1].Name,
                            intType),
                        intType)),
            ]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("int result = value - 2;", patchedSource, StringComparison.Ordinal);
        Assert.Contains("if (enabled && (result > stopAt))", patchedSource, StringComparison.Ordinal);
        Assert.Contains("return result + stopAt;", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement changedLocal = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[0]);
        HirIfStatement changedBranch = Assert.IsType<HirIfStatement>(changedMethod.Body.Statements[1]);
        HirReturnStatement changedBranchReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(changedBranch.Then.Statements));
        Assert.Equal(method.Id, changedMethod.Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);
        Assert.Equal(local.Id, changedLocal.Id);
        Assert.Equal(branch.Id, changedBranch.Id);
        Assert.Equal(branch.Then.Id, changedBranch.Then.Id);
        Assert.Null(branch.Else);
        Assert.Null(changedBranch.Else);
        Assert.Equal(branchReturn.Id, changedBranchReturn.Id);
        Assert.Equal(method.Body.Statements[2].Id, changedMethod.Body.Statements[2].Id);

        IdentitySidecarDocument identity = Assert.Single(result.Analysis.IdentitySidecars);
        HashSet<SemanticId> liveIds = identity.Entries.Select(static entry => entry.Id).ToHashSet();
        HashSet<SemanticId> tombstoneIds = identity.Tombstones
            .Select(static tombstone => tombstone.Id)
            .ToHashSet();
        Assert.All(oldEndpointIds, id => Assert.True(liveIds.Contains(id) ^ tombstoneIds.Contains(id)));
        Assert.All(
            new[] { local.Id, branch.Id, branch.Then.Id, branchReturn.Id },
            id => Assert.Contains(id, liveIds));
        Assert.NotEmpty(tombstoneIds.Intersect(oldEndpointIds));

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphFunction function = Assert.Single(graph.Functions);
        Assert.Contains(function.Nodes, node => node.Id == local.Id && node.Kind == "variable");
        Assert.Contains(function.Nodes, node => node.Id == branch.Id && node.Kind == "branch");
        Assert.Contains(function.Nodes, node => node.Id == branchReturn.Id && node.Kind == "return");
        GraphControlFlowFunction flow = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(graph).Functions);
        GraphControlFlowBranch branchFlow = Assert.Single(flow.Branches);
        Assert.True(flow.IsReducible);
        Assert.Equal(branch.Id, branchFlow.BranchNodeId);
        Assert.True(branchFlow.ThenExitsFunction);
        Assert.False(branchFlow.ElseExitsFunction);
    }

    [Fact]
    public void Apply_RejectsGeneralEndpointReplacementThatWouldDiscardAComment()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value)
                {
                    int result = value /* preserve rationale */ + 1;
                    return result;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/CommentedInitializerEdit.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement local = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        SemanticId intType = GetTypeId(original, local);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceLocalInitializerPatchOperation(
                local.Id,
                local.Name,
                local.Initializer!.Id,
                intType,
                new LiteralPatchExpression("2", intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidReplacement);
    }

    [Fact]
    public void Apply_EditsEverySupportedForHeaderEndpointWithoutRecreatingStructure()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int count, bool enabled)
                {
                    int cursor = count;
                    for (int index = 0; index < count; index++)
                    {
                        cursor = cursor + index;
                    }

                    for (cursor = 0; cursor < count; cursor++)
                    {
                        cursor = cursor + 1;
                    }

                    return cursor;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ForHeaderEndpointEdit.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement cursor = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirForStatement declarationLoop = Assert.IsType<HirForStatement>(method.Body.Statements[1]);
        HirForVariableDeclarator index = Assert.Single(declarationLoop.Declaration!.Variables);
        HirAssignmentExpression assignmentInitializer = Assert.IsType<HirAssignmentExpression>(
            Assert.Single(Assert.IsType<HirForStatement>(method.Body.Statements[2]).Initializers));
        HirForStatement assignmentLoop = Assert.IsType<HirForStatement>(method.Body.Statements[2]);
        HirUnaryExpression incrementor = Assert.IsType<HirUnaryExpression>(
            Assert.Single(declarationLoop.Incrementors));
        HirIdentifierExpression incrementorOperand = Assert.IsType<HirIdentifierExpression>(
            incrementor.Operand);
        HirIdentifierExpression assignmentTarget = Assert.IsType<HirIdentifierExpression>(
            assignmentInitializer.Target);
        SemanticId intType = GetTypeId(original, index);
        SemanticId boolType = GetTypeId(original, declarationLoop.Condition!);
        HashSet<SemanticId> replacedIds = index.Initializer!.DescendantsAndSelf()
            .Concat(declarationLoop.Condition!.DescendantsAndSelf())
            .Concat(assignmentInitializer.Value.DescendantsAndSelf())
            .Select(static node => node.Id)
            .ToHashSet();

        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                new ReplaceForConditionPatchOperation(
                    declarationLoop.Id,
                    declarationLoop.Condition!.Id,
                    boolType,
                    new BinaryPatchExpression(
                        "&&",
                        new LocalReferencePatchExpression(
                            method.Parameters[1].Id,
                            method.Parameters[1].Name,
                            boolType),
                        new BinaryPatchExpression(
                            "<",
                            new LocalReferencePatchExpression(index.Id, index.Name, intType),
                            new LocalReferencePatchExpression(
                                method.Parameters[0].Id,
                                method.Parameters[0].Name,
                                intType),
                            boolType),
                        boolType)),
                new ReplaceForDeclarationInitializerPatchOperation(
                    index.Id,
                    declarationLoop.Id,
                    index.Name,
                    index.Initializer!.Id,
                    intType,
                    new LiteralPatchExpression("1", intType)),
                new ReplaceForAssignmentInitializerValuePatchOperation(
                    assignmentInitializer.Id,
                    assignmentLoop.Id,
                    0,
                    assignmentInitializer.Operator,
                    assignmentInitializer.Target.Id,
                    cursor.Id,
                    cursor.Name,
                    assignmentInitializer.Value.Id,
                    intType,
                    new LiteralPatchExpression("2", intType)),
                new ReplaceForIncrementorPatchOperation(
                    incrementor.Id,
                    declarationLoop.Id,
                    0,
                    incrementor.Operator,
                    incrementor.IsPostfix,
                    incrementor.Operand.Id,
                    index.Id,
                    index.Name,
                    intType,
                    "--",
                    false,
                    new LocalReferencePatchExpression(index.Id, index.Name, intType)),
            ]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains(
            "for (int index = 1; enabled && (index < count); --index)",
            patchedSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "for (cursor = 2; cursor < count; cursor++)",
            patchedSource,
            StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(
            Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirForStatement changedDeclarationLoop = Assert.IsType<HirForStatement>(
            changedMethod.Body.Statements[1]);
        HirForStatement changedAssignmentLoop = Assert.IsType<HirForStatement>(
            changedMethod.Body.Statements[2]);
        HirForVariableDeclarator changedIndex = Assert.Single(
            changedDeclarationLoop.Declaration!.Variables);
        HirUnaryExpression changedIncrementor = Assert.IsType<HirUnaryExpression>(
            Assert.Single(changedDeclarationLoop.Incrementors));
        HirAssignmentExpression changedAssignmentInitializer = Assert.IsType<HirAssignmentExpression>(
            Assert.Single(changedAssignmentLoop.Initializers));
        Assert.Equal(declarationLoop.Id, changedDeclarationLoop.Id);
        Assert.Equal(declarationLoop.Body.Id, changedDeclarationLoop.Body.Id);
        Assert.Equal(index.Id, changedIndex.Id);
        Assert.Equal(incrementor.Id, changedIncrementor.Id);
        Assert.Equal(incrementorOperand.Id, changedIncrementor.Operand.Id);
        Assert.Equal(assignmentLoop.Id, changedAssignmentLoop.Id);
        Assert.Equal(assignmentInitializer.Id, changedAssignmentInitializer.Id);
        Assert.Equal(assignmentTarget.Id, changedAssignmentInitializer.Target.Id);
        Assert.Equal("--", changedIncrementor.Operator);
        Assert.False(changedIncrementor.IsPostfix);

        IdentitySidecarDocument identity = Assert.Single(result.Analysis.IdentitySidecars);
        HashSet<SemanticId> liveIds = identity.Entries.Select(static entry => entry.Id).ToHashSet();
        HashSet<SemanticId> tombstoneIds = identity.Tombstones
            .Select(static tombstone => tombstone.Id)
            .ToHashSet();
        Assert.All(replacedIds, id => Assert.True(liveIds.Contains(id) ^ tombstoneIds.Contains(id)));
        Assert.Contains(index.Initializer.Id, tombstoneIds);
        Assert.Contains(declarationLoop.Condition.Id, tombstoneIds);
        Assert.Contains(assignmentInitializer.Value.Id, tombstoneIds);
        GraphControlFlowFunction flow = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(
                new HirGraphProjector().Project(
                    result.Analysis.Hir,
                    result.Analysis.Semantics)).Functions);
        Assert.Equal(2, flow.Loops.Count);
        Assert.Contains(flow.Loops, candidate =>
            candidate.LoopNodeId == changedDeclarationLoop.Id &&
            candidate.ContinueTargetNodeId == changedIncrementor.Id);
    }

    [Fact]
    public void Apply_RejectsForConditionReplacementForAnOmittedConditionAtomically()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static void Run(bool enabled)
                {
                    for (;;)
                    {
                        break;
                    }
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/OmittedForConditionEdit.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirForStatement loop = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirForStatement>());
        SemanticId boolType = GetTypeId(original, Assert.Single(method.Parameters));
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceForConditionPatchOperation(
                loop.Id,
                SemanticId.FromKey("missing-for-condition"),
                boolType,
                new LiteralPatchExpression("false", boolType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TargetKindMismatch);
    }

    [Fact]
    public void Apply_RebindsAForIncrementorOperandAndTombstonesTheOldUse()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int count)
                {
                    int cursor = 0;
                    int step = 0;
                    for (cursor = 0; cursor < count; cursor++)
                    {
                        step = step + 1;
                    }

                    return step;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ForIncrementorOperandEdit.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement step = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[1]);
        HirForStatement loop = Assert.IsType<HirForStatement>(method.Body.Statements[2]);
        HirUnaryExpression incrementor = Assert.IsType<HirUnaryExpression>(
            Assert.Single(loop.Incrementors));
        HirIdentifierExpression oldOperand = Assert.IsType<HirIdentifierExpression>(
            incrementor.Operand);
        SemanticId cursorId = Assert.Single(
            original.Semantics!.Nodes,
            binding => binding.NodeId == oldOperand.Id).ReferencedSymbolId!.Value;
        SemanticId intType = GetTypeId(original, incrementor);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new ReplaceForIncrementorPatchOperation(
                incrementor.Id,
                loop.Id,
                0,
                incrementor.Operator,
                incrementor.IsPostfix,
                oldOperand.Id,
                cursorId,
                oldOperand.Name,
                intType,
                incrementor.Operator,
                incrementor.IsPostfix,
                new LocalReferencePatchExpression(step.Id, step.Name, intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "for (cursor = 0; cursor < count; step++)",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        HirForStatement changedLoop = Assert.IsType<HirForStatement>(
            Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods).Body.Statements[2]);
        HirUnaryExpression changedIncrementor = Assert.IsType<HirUnaryExpression>(
            Assert.Single(changedLoop.Incrementors));
        Assert.Equal(loop.Id, changedLoop.Id);
        Assert.Equal(incrementor.Id, changedIncrementor.Id);
        Assert.NotEqual(oldOperand.Id, changedIncrementor.Operand.Id);
        Assert.Equal(
            step.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == changedIncrementor.Operand.Id).ReferencedSymbolId);
        Assert.Contains(
            Assert.Single(result.Analysis.IdentitySidecars).Tombstones,
            tombstone => tombstone.Id == oldOperand.Id);
    }

    [Fact]
    public void Apply_ReplacesForEachCollectionAndPreservesLoopIdentity()
    {
        const string source = """
            using System.Collections.Generic;
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Count(IEnumerable<int> primary, IEnumerable<int> fallback)
                {
                    int total = 0;
                    foreach (int value in primary)
                    {
                        total = total + 1;
                    }

                    return total;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ForEachCollectionEdit.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirForEachStatement loop = Assert.IsType<HirForEachStatement>(method.Body.Statements[1]);
        SemanticId collectionType = GetTypeId(original, loop.Collection);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceForEachCollectionPatchOperation(
                loop.Id,
                loop.Collection.Id,
                collectionType,
                new LocalReferencePatchExpression(
                    method.Parameters[1].Id,
                    method.Parameters[1].Name,
                    collectionType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "foreach (int value in fallback)",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(
            Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirForEachStatement changedLoop = Assert.IsType<HirForEachStatement>(
            changedMethod.Body.Statements[1]);
        HirIdentifierExpression changedCollection = Assert.IsType<HirIdentifierExpression>(
            changedLoop.Collection);
        Assert.Equal(loop.Id, changedLoop.Id);
        Assert.Equal(loop.Variable.Id, changedLoop.Variable.Id);
        Assert.Equal(loop.Body.Id, changedLoop.Body.Id);
        Assert.Equal(
            method.Parameters[1].Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == changedCollection.Id).ReferencedSymbolId);
        Assert.Equal(
            ForEachIterationStrategy.Enumerator,
            Assert.Single(result.Analysis.Semantics.ForEachLoops).Strategy);
    }

    [Fact]
    public void Apply_ReplacesAsyncForEachCollectionAndPreservesAwaitProtocol()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static async Task<int> Count(
                    IAsyncEnumerable<int> primary,
                    IAsyncEnumerable<int> fallback)
                {
                    int total = 0;
                    await foreach (int value in primary)
                    {
                        total = total + value;
                    }

                    return total;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/AsyncForEachCollectionEdit.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirForEachStatement loop = Assert.IsType<HirForEachStatement>(method.Body.Statements[1]);
        SemanticId collectionType = GetTypeId(original, loop.Collection);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceForEachCollectionPatchOperation(
                loop.Id,
                loop.Collection.Id,
                collectionType,
                new LocalReferencePatchExpression(
                    method.Parameters[1].Id,
                    method.Parameters[1].Name,
                    collectionType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "await foreach (int value in fallback)",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        HirForEachStatement changedLoop = Assert.Single(
            result.Analysis!.Hir!.DescendantsAndSelf().OfType<HirForEachStatement>());
        Assert.Equal(loop.Id, changedLoop.Id);
        Assert.Equal(loop.Variable.Id, changedLoop.Variable.Id);
        Assert.True(changedLoop.IsAsync);
        BoundForEachInfo binding = Assert.Single(result.Analysis.Semantics!.ForEachLoops);
        Assert.Equal(ForEachIterationStrategy.AsyncEnumerator, binding.Strategy);
        Assert.Equal(ForEachDisposalKind.Required, binding.DisposalKind);
    }

    [Fact]
    public void Apply_ReplacesBorrowedRefReadonlyForEachCollectionWithoutLosingAliasMode()
    {
        const string source = """
            using System;
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Count(int[] first, int[] second)
                {
                    ReadOnlySpan<int> primary = first;
                    ReadOnlySpan<int> fallback = second;
                    int total = 0;
                    foreach (ref readonly int value in primary)
                    {
                        total = total + value;
                    }

                    return total;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/BorrowedForEachCollectionEdit.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirForEachStatement loop = Assert.Single(
            method.Body.DescendantsAndSelf().OfType<HirForEachStatement>());
        HirVariableDeclarationStatement fallback = method.Body.Statements
            .OfType<HirVariableDeclarationStatement>()
            .Single(static local => local.Name == "fallback");
        SemanticId collectionType = GetTypeId(original, loop.Collection);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceForEachCollectionPatchOperation(
                loop.Id,
                loop.Collection.Id,
                collectionType,
                new LocalReferencePatchExpression(
                    fallback.Id,
                    fallback.Name,
                    collectionType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "foreach (ref readonly int value in fallback)",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        HirForEachStatement changedLoop = Assert.Single(
            result.Analysis!.Hir!.DescendantsAndSelf().OfType<HirForEachStatement>());
        Assert.Equal(loop.Id, changedLoop.Id);
        Assert.Equal(loop.Variable.Id, changedLoop.Variable.Id);
        Assert.Equal(HirForEachIterationKind.RefReadOnly, changedLoop.IterationKind);
        BoundForEachInfo binding = Assert.Single(result.Analysis.Semantics!.ForEachLoops);
        Assert.Equal(ForEachIterationKind.RefReadOnly, binding.IterationKind);
        Assert.Equal(ForEachDisposalKind.None, binding.DisposalKind);
    }

    [Fact]
    public void Apply_InsertsStatementDirectlyIntoSwitchSectionBody()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    switch (value)
                    {
                        case 0:
                            break;
                        default:
                            break;
                    }

                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/SwitchSectionBodyInsert.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirSwitchStatement @switch = Assert.IsType<HirSwitchStatement>(method.Body.Statements[0]);
        HirSwitchSection firstSection = @switch.Sections[0];
        HirBreakStatement existingBreak = Assert.IsType<HirBreakStatement>(
            Assert.Single(firstSection.Body.Statements));
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                firstSection.Body.Id,
                null,
                existingBreak.Id,
                new LocalLiteralDeclarationPatchStatement(
                    "sectionValue",
                    "int",
                    "3",
                    intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "int sectionValue = 3;",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(
            Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirSwitchStatement changedSwitch = Assert.IsType<HirSwitchStatement>(
            changedMethod.Body.Statements[0]);
        Assert.Equal(@switch.Id, changedSwitch.Id);
        Assert.Equal(firstSection.Id, changedSwitch.Sections[0].Id);
        Assert.Equal(firstSection.Body.Id, changedSwitch.Sections[0].Body.Id);
        Assert.Equal(existingBreak.Id, changedSwitch.Sections[0].Body.Statements[1].Id);
    }

    [Fact]
    public void Apply_InsertsStructuredSwitchWithPatternsAndReducibleCfg()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/InsertSwitch.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                @return.Id,
                new SwitchPatchStatement(
                    new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        "value",
                        intType),
                    intType,
                    [
                        new SwitchSectionPatch([new PatternSwitchLabelPatch(
                            new ConstantSwitchPatternPatch(
                                new LiteralPatchExpression("0", intType)),
                            null)]),
                        new SwitchSectionPatch([new PatternSwitchLabelPatch(
                            new RelationalSwitchPatternPatch(
                                ">",
                                new LiteralPatchExpression("10", intType)),
                            null)]),
                        new SwitchSectionPatch([new DefaultSwitchLabelPatch()]),
                    ]))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("switch (value)", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(
            Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirSwitchStatement changedSwitch = Assert.IsType<HirSwitchStatement>(
            changedMethod.Body.Statements[0]);
        Assert.Equal(3, changedSwitch.Sections.Count);
        Assert.IsType<HirRelationalPattern>(
            Assert.IsType<HirPatternSwitchLabel>(
                Assert.Single(changedSwitch.Sections[1].Labels)).Pattern);
        Assert.All(changedSwitch.Sections, static section =>
            Assert.IsType<HirBreakStatement>(Assert.Single(section.Body.Statements)));
        GraphControlFlowSwitch flow = Assert.Single(Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(
                new HirGraphProjector().Project(
                    result.Analysis.Hir,
                    result.Analysis.Semantics!)).Functions).Switches);
        Assert.Equal(changedSwitch.Id, flow.SwitchNodeId);
    }

    [Fact]
    public void Apply_InsertsRelabelsMovesAndDeletesSwitchSections()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    switch (value)
                    {
                        case 0:
                            break;
                        case 1:
                            break;
                        default:
                            break;
                    }

                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/EditSwitchSections.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirSwitchStatement @switch = Assert.IsType<HirSwitchStatement>(method.Body.Statements[0]);
        HirSwitchSection first = @switch.Sections[0];
        HirSwitchSection second = @switch.Sections[1];
        HirSwitchSection fallback = @switch.Sections[2];
        SemanticId intType = GetTypeId(original, method.Parameters[0]);

        CSharpPatchResult inserted = new CSharpSourcePatchApplier().Apply(
            request,
            SemanticPatchDocument.Create(
                original.Hir.Id,
                original.Semantics!.ConfigurationId,
                [new InsertSwitchSectionPatchOperation(
                    @switch.Id,
                    second.Id,
                    fallback.Id,
                    new SwitchSectionPatch([new PatternSwitchLabelPatch(
                        new ConstantSwitchPatternPatch(
                            new LiteralPatchExpression("2", intType)),
                        null)]))]));
        Assert.True(inserted.Succeeded, string.Join(Environment.NewLine, inserted.Diagnostics));
        HirMethodDeclaration insertedMethod = Assert.Single(
            Assert.Single(inserted.Analysis!.Hir!.Types).Methods);
        HirSwitchStatement insertedSwitch = Assert.IsType<HirSwitchStatement>(
            insertedMethod.Body.Statements[0]);
        Assert.Equal(4, insertedSwitch.Sections.Count);
        Assert.Equal(
            [first.Id, second.Id, fallback.Id],
            insertedSwitch.Sections
                .Where(section => section.Id == first.Id || section.Id == second.Id ||
                    section.Id == fallback.Id)
                .Select(static section => section.Id)
                .ToArray());

        CSharpPatchResult relabeled = new CSharpSourcePatchApplier().Apply(
            request,
            SemanticPatchDocument.Create(
                original.Hir.Id,
                original.Semantics.ConfigurationId,
                [new ReplaceSwitchSectionPatchOperation(
                    first.Id,
                    @switch.Id,
                    first.Labels.Select(static label => label.Id).ToArray(),
                    [new PatternSwitchLabelPatch(
                        new ConstantSwitchPatternPatch(
                            new LiteralPatchExpression("5", intType)),
                        null)])]));
        Assert.True(relabeled.Succeeded, string.Join(Environment.NewLine, relabeled.Diagnostics));
        HirMethodDeclaration relabeledMethod = Assert.Single(
            Assert.Single(relabeled.Analysis!.Hir!.Types).Methods);
        HirSwitchStatement relabeledSwitch = Assert.IsType<HirSwitchStatement>(
            relabeledMethod.Body.Statements[0]);
        Assert.Equal(first.Id, relabeledSwitch.Sections[0].Id);
        Assert.Equal("case 5", Assert.Single(relabeledSwitch.Sections[0].Labels).DisplayText);

        CSharpPatchResult moved = new CSharpSourcePatchApplier().Apply(
            request,
            SemanticPatchDocument.Create(
                original.Hir.Id,
                original.Semantics.ConfigurationId,
                [new MoveSwitchSectionPatchOperation(
                    first.Id,
                    @switch.Id,
                    null,
                    second.Id,
                    second.Id,
                    fallback.Id)]));
        Assert.True(moved.Succeeded, string.Join(Environment.NewLine, moved.Diagnostics));
        HirMethodDeclaration movedMethod = Assert.Single(
            Assert.Single(moved.Analysis!.Hir!.Types).Methods);
        HirSwitchStatement movedSwitch = Assert.IsType<HirSwitchStatement>(
            movedMethod.Body.Statements[0]);
        Assert.Equal(
            [second.Id, first.Id, fallback.Id],
            movedSwitch.Sections.Select(static section => section.Id).ToArray());

        CSharpPatchResult deleted = new CSharpSourcePatchApplier().Apply(
            request,
            SemanticPatchDocument.Create(
                original.Hir.Id,
                original.Semantics.ConfigurationId,
                [new DeleteSwitchSectionPatchOperation(
                    second.Id,
                    @switch.Id,
                    first.Id,
                    fallback.Id,
                    second.Labels.Select(static label => label.Id).ToArray())]));
        Assert.True(deleted.Succeeded, string.Join(Environment.NewLine, deleted.Diagnostics));
        HirMethodDeclaration deletedMethod = Assert.Single(
            Assert.Single(deleted.Analysis!.Hir!.Types).Methods);
        HirSwitchStatement deletedSwitch = Assert.IsType<HirSwitchStatement>(
            deletedMethod.Body.Statements[0]);
        Assert.Equal([first.Id, fallback.Id],
            deletedSwitch.Sections.Select(static section => section.Id).ToArray());
    }

    [Fact]
    public void Apply_DuplicatesExpressionStatementWithFreshCopyIdentity()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run()
                {
                    int value = 1;
                    // keep this comment with both copies
                    value += 2;
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/DuplicateExpression.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement value = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirExpressionStatement expression = Assert.IsType<HirExpressionStatement>(
            method.Body.Statements[1]);
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[2]);
        HashSet<SemanticId> originalSubtreeIds = expression.DescendantsAndSelf()
            .Select(static node => node.Id)
            .ToHashSet();
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new DuplicateStatementPatchOperation(
                expression.Id,
                method.Body.Id,
                value.Id,
                @return.Id,
                expression.Id,
                @return.Id,
                null)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Equal(2, patchedSource.Split("value += 2;", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            2,
            patchedSource.Split("// keep this comment with both copies", StringSplitOptions.None).Length - 1);
        HirMethodDeclaration changed = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        Assert.Equal(value.Id, changed.Body.Statements[0].Id);
        HirExpressionStatement originalExpression = Assert.IsType<HirExpressionStatement>(
            changed.Body.Statements[1]);
        HirExpressionStatement copiedExpression = Assert.IsType<HirExpressionStatement>(
            changed.Body.Statements[2]);
        Assert.Equal(expression.Id, originalExpression.Id);
        Assert.Equal(@return.Id, changed.Body.Statements[3].Id);
        Assert.Empty(originalSubtreeIds.Intersect(
            copiedExpression.DescendantsAndSelf().Select(static node => node.Id)));
    }

    [Fact]
    public void Apply_DuplicatesStructuredStatementAndKeepsOriginalNestedIdentities()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool ready)
                {
                    int result = 0;
                    // duplicate the complete branch
                    if (ready)
                    {
                        int inside = 1;
                        result += inside;
                    }

                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/DuplicateBranch.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition =
            Assert.IsType<HirVariableDeclarationStatement>(method.Body.Statements[0]);
        HirIfStatement branch = Assert.IsType<HirIfStatement>(method.Body.Statements[1]);
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[2]);
        HashSet<SemanticId> originalBranchIds = branch.DescendantsAndSelf()
            .Select(static node => node.Id)
            .ToHashSet();
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new DuplicateStatementPatchOperation(
                branch.Id,
                method.Body.Id,
                resultDefinition.Id,
                @return.Id,
                branch.Id,
                @return.Id,
                null)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Equal(2, patchedSource.Split("// duplicate the complete branch", StringSplitOptions.None).Length - 1);
        HirMethodDeclaration changed = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirIfStatement originalBranch = Assert.IsType<HirIfStatement>(changed.Body.Statements[1]);
        HirIfStatement copiedBranch = Assert.IsType<HirIfStatement>(changed.Body.Statements[2]);
        Assert.Equal(
            originalBranchIds.OrderBy(static id => id.Value),
            originalBranch.DescendantsAndSelf().Select(static node => node.Id).OrderBy(static id => id.Value));
        Assert.Empty(originalBranchIds.Intersect(
            copiedBranch.DescendantsAndSelf().Select(static node => node.Id)));
        Assert.Equal(@return.Id, changed.Body.Statements[3].Id);
    }

    [Fact]
    public void Apply_DuplicatesLocalWithNewNameWithoutRebindingOriginalUses()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run()
                {
                    int value = 1;
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/DuplicateLocal.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement value = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new DuplicateStatementPatchOperation(
                value.Id,
                method.Body.Id,
                null,
                @return.Id,
                value.Id,
                @return.Id,
                "valueCopy")]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirMethodDeclaration changed = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement originalValue =
            Assert.IsType<HirVariableDeclarationStatement>(changed.Body.Statements[0]);
        HirVariableDeclarationStatement copiedValue =
            Assert.IsType<HirVariableDeclarationStatement>(changed.Body.Statements[1]);
        HirReturnStatement changedReturn = Assert.IsType<HirReturnStatement>(changed.Body.Statements[2]);
        HirIdentifierExpression returnedValue = Assert.IsType<HirIdentifierExpression>(changedReturn.Expression);
        Assert.Equal(value.Id, originalValue.Id);
        Assert.Equal("valueCopy", copiedValue.Name);
        Assert.NotEqual(value.Id, copiedValue.Id);
        Assert.Equal(@return.Id, changedReturn.Id);
        Assert.Equal(
            value.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == returnedValue.Id).ReferencedSymbolId);
    }

    [Fact]
    public void Apply_RejectsTerminalOrStaleStatementDuplication()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run()
                {
                    int value = 1;
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/RejectStatementDuplication.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement value = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);

        CSharpPatchResult terminalResult = new CSharpSourcePatchApplier().Apply(
            request,
            SemanticPatchDocument.Create(
                original.Hir.Id,
                original.Semantics!.ConfigurationId,
                [new DuplicateStatementPatchOperation(
                    @return.Id,
                    method.Body.Id,
                    value.Id,
                    null,
                    value.Id,
                    @return.Id,
                    null)]));
        Assert.False(terminalResult.Succeeded);
        Assert.Equal(
            SemanticPatchDiagnosticCodes.InvalidStatementDuplication,
            Assert.Single(terminalResult.Diagnostics).Code);

        CSharpPatchResult staleResult = new CSharpSourcePatchApplier().Apply(
            request,
            SemanticPatchDocument.Create(
                original.Hir.Id,
                original.Semantics.ConfigurationId,
                [new DuplicateStatementPatchOperation(
                    value.Id,
                    method.Body.Id,
                    null,
                    @return.Id,
                    value.Id,
                    null,
                    "valueCopy") ]));
        Assert.False(staleResult.Succeeded);
        Assert.Equal(
            SemanticPatchDiagnosticCodes.SiblingPreconditionFailed,
            Assert.Single(staleResult.Diagnostics).Code);
    }

    [Fact]
    public void Apply_DuplicatesConsecutiveRangeAcrossRegionsAndRenamesInternalUses()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool first, int input)
                {
                    if (first)
                    {
                        // copy this complete range
                        int scaled = input * 2;
                        scaled += 1;
                    }
                    else
                    {
                        input += 10;
                    }

                    return input;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/DuplicateRangeAcrossRegions.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement branch = Assert.IsType<HirIfStatement>(method.Body.Statements[0]);
        HirVariableDeclarationStatement local =
            Assert.IsType<HirVariableDeclarationStatement>(branch.Then.Statements[0]);
        HirExpressionStatement increment =
            Assert.IsType<HirExpressionStatement>(branch.Then.Statements[1]);
        HirExpressionStatement destination =
            Assert.IsType<HirExpressionStatement>(branch.Else!.Statements[0]);
        HashSet<SemanticId> originalRangeIds = branch.Then.Statements
            .SelectMany(static statement => statement.DescendantsAndSelf())
            .Select(static node => node.Id)
            .ToHashSet();
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new DuplicateStatementRangePatchOperation(
                local.Id,
                branch.Then.Id,
                [local.Id, increment.Id],
                null,
                null,
                branch.Else.Id,
                destination.Id,
                null,
                [new StatementCopyLocalRename(local.Id, "scaled", "scaledCopy")])]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Equal(
            2,
            patchedSource.Split("// copy this complete range", StringSplitOptions.None).Length - 1);
        Assert.Contains("int scaledCopy = input * 2;", patchedSource, StringComparison.Ordinal);
        Assert.Contains("scaledCopy += 1;", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changed = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirIfStatement changedBranch = Assert.IsType<HirIfStatement>(changed.Body.Statements[0]);
        Assert.Equal([local.Id, increment.Id],
            changedBranch.Then.Statements.Select(static statement => statement.Id).ToArray());
        HirVariableDeclarationStatement copiedLocal =
            Assert.IsType<HirVariableDeclarationStatement>(changedBranch.Else!.Statements[1]);
        HirExpressionStatement copiedIncrement =
            Assert.IsType<HirExpressionStatement>(changedBranch.Else.Statements[2]);
        Assert.Equal("scaledCopy", copiedLocal.Name);
        Assert.Empty(originalRangeIds.Intersect(
            changedBranch.Else.Statements.Skip(1)
                .SelectMany(static statement => statement.DescendantsAndSelf())
                .Select(static node => node.Id)));
        HirIdentifierExpression copiedReference = copiedIncrement
            .DescendantsAndSelf()
            .OfType<HirIdentifierExpression>()
            .Single(static identifier => identifier.Name == "scaledCopy");
        Assert.Equal(
            copiedLocal.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == copiedReference.Id).ReferencedSymbolId);
    }

    [Fact]
    public void Apply_RejectsCrossRegionCopyThatWouldRebindExternalLocal()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool first)
                {
                    int result = 0;
                    if (first)
                    {
                        int value = 1;
                        result += value;
                    }
                    else
                    {
                        int value = 2;
                        result += value;
                    }

                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/RejectCrossRegionRebinding.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement branch = Assert.IsType<HirIfStatement>(method.Body.Statements[1]);
        HirVariableDeclarationStatement sourceValue =
            Assert.IsType<HirVariableDeclarationStatement>(branch.Then.Statements[0]);
        HirExpressionStatement copied =
            Assert.IsType<HirExpressionStatement>(branch.Then.Statements[1]);
        HirExpressionStatement destination =
            Assert.IsType<HirExpressionStatement>(branch.Else!.Statements[1]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new DuplicateStatementRangePatchOperation(
                copied.Id,
                branch.Then.Id,
                [copied.Id],
                sourceValue.Id,
                null,
                branch.Else.Id,
                destination.Id,
                null,
                [])]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Equal(
            SemanticPatchDiagnosticCodes.ValueSourceNotAvailable,
            Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Apply_ExtractsRangeWithCallerVisibleLocalResultAndStableIdentities()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int input)
                {
                    // preserve extraction comment
                    int scaled = input * 2;
                    scaled += 3;
                    return scaled;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ExtractLocalResult.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement local =
            Assert.IsType<HirVariableDeclarationStatement>(method.Body.Statements[0]);
        HirExpressionStatement increment =
            Assert.IsType<HirExpressionStatement>(method.Body.Statements[1]);
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[2]);
        SemanticId initializerId = local.Initializer!.Id;
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ExtractCallablePatchOperation(
                local.Id,
                method.Id,
                method.Body.Id,
                [local.Id, increment.Id],
                null,
                @return.Id,
                "ExtractDamage")]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("int scaled = ExtractDamage(input);", patchedSource, StringComparison.Ordinal);
        Assert.Contains("private static int ExtractDamage(int input)", patchedSource, StringComparison.Ordinal);
        Assert.Equal(
            1,
            patchedSource.Split("// preserve extraction comment", StringSplitOptions.None).Length - 1);

        HirTypeDeclaration changedType = Assert.Single(result.Analysis!.Hir!.Types);
        HirMethodDeclaration caller = changedType.Methods.Single(static candidate => candidate.Name == "Run");
        HirMethodDeclaration helper = changedType.Methods.Single(
            static candidate => candidate.Name == "ExtractDamage");
        HirVariableDeclarationStatement callerLocal =
            Assert.IsType<HirVariableDeclarationStatement>(caller.Body.Statements[0]);
        Assert.Equal(local.Id, callerLocal.Id);
        Assert.Equal(@return.Id, caller.Body.Statements[1].Id);
        HirVariableDeclarationStatement helperLocal =
            Assert.IsType<HirVariableDeclarationStatement>(helper.Body.Statements[0]);
        Assert.NotEqual(local.Id, helperLocal.Id);
        Assert.Equal(initializerId, helperLocal.Initializer!.Id);
        Assert.Equal(increment.Id, helper.Body.Statements[1].Id);
        Assert.IsType<HirReturnStatement>(helper.Body.Statements[2]);
        Assert.DoesNotContain(helper.Parameters[0].Id, method.Parameters.Select(static parameter => parameter.Id));
        HirInvocationExpression call = Assert.IsType<HirInvocationExpression>(callerLocal.Initializer);
        Assert.Equal(
            helper.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == call.Id).InvokedMethodId);
    }

    [Fact]
    public void Apply_ExtractsMutationAsOneReturnedWriteBack()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int input)
                {
                    int result = 10;
                    result += input;
                    result *= 2;
                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ExtractWriteBack.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition =
            Assert.IsType<HirVariableDeclarationStatement>(method.Body.Statements[0]);
        HirExpressionStatement add = Assert.IsType<HirExpressionStatement>(method.Body.Statements[1]);
        HirExpressionStatement multiply = Assert.IsType<HirExpressionStatement>(method.Body.Statements[2]);
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[3]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ExtractCallablePatchOperation(
                add.Id,
                method.Id,
                method.Body.Id,
                [add.Id, multiply.Id],
                resultDefinition.Id,
                @return.Id,
                "ExtractAccumulation")]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains(
            "result = ExtractAccumulation(input, result);",
            patchedSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "private static int ExtractAccumulation(int input, int result)",
            patchedSource,
            StringComparison.Ordinal);
        HirTypeDeclaration changedType = Assert.Single(result.Analysis!.Hir!.Types);
        HirMethodDeclaration caller = changedType.Methods.Single(static candidate => candidate.Name == "Run");
        HirMethodDeclaration helper = changedType.Methods.Single(
            static candidate => candidate.Name == "ExtractAccumulation");
        Assert.Equal(resultDefinition.Id, caller.Body.Statements[0].Id);
        Assert.Equal(@return.Id, caller.Body.Statements[2].Id);
        Assert.Equal(add.Id, helper.Body.Statements[0].Id);
        Assert.Equal(multiply.Id, helper.Body.Statements[1].Id);
        Assert.Equal(["input", "result"], helper.Parameters.Select(static parameter => parameter.Name));
    }

    [Fact]
    public void Apply_RejectsCallableExtractionWithMultipleLiveOutputs()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int input)
                {
                    int left = 0;
                    int right = 0;
                    left += input;
                    right += input;
                    return left + right;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/RejectExtractMultipleOutputs.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement left =
            Assert.IsType<HirVariableDeclarationStatement>(method.Body.Statements[0]);
        HirVariableDeclarationStatement right =
            Assert.IsType<HirVariableDeclarationStatement>(method.Body.Statements[1]);
        HirExpressionStatement mutateLeft =
            Assert.IsType<HirExpressionStatement>(method.Body.Statements[2]);
        HirExpressionStatement mutateRight =
            Assert.IsType<HirExpressionStatement>(method.Body.Statements[3]);
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[4]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ExtractCallablePatchOperation(
                mutateLeft.Id,
                method.Id,
                method.Body.Id,
                [mutateLeft.Id, mutateRight.Id],
                right.Id,
                @return.Id,
                "ExtractBoth")]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Equal(
            SemanticPatchDiagnosticCodes.InvalidCallableExtraction,
            Assert.Single(result.Diagnostics).Code);
        Assert.Contains("multiple live outputs", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.NotEqual(left.Id, right.Id);
    }

    [Fact]
    public void Apply_ExtractsVoidCallableWithInferredInput()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int input)
                {
                    System.Math.Abs(input);
                    System.Math.Max(input, 2);
                    return input;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ExtractVoidCallable.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirExpressionStatement first = Assert.IsType<HirExpressionStatement>(method.Body.Statements[0]);
        HirExpressionStatement second = Assert.IsType<HirExpressionStatement>(method.Body.Statements[1]);
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[2]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ExtractCallablePatchOperation(
                first.Id,
                method.Id,
                method.Body.Id,
                [first.Id, second.Id],
                null,
                @return.Id,
                "ExtractCalls")]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("ExtractCalls(input);", patchedSource, StringComparison.Ordinal);
        Assert.Contains("private static void ExtractCalls(int input)", patchedSource, StringComparison.Ordinal);
        HirTypeDeclaration changedType = Assert.Single(result.Analysis!.Hir!.Types);
        HirMethodDeclaration helper = changedType.Methods.Single(
            static candidate => candidate.Name == "ExtractCalls");
        Assert.Equal([first.Id, second.Id], helper.Body.Statements.Select(static statement => statement.Id));
        Assert.Equal("void", helper.ReturnType);
    }

    [Fact]
    public void Apply_RejectsCallableExtractionWithEscapingReturn()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int input)
                {
                    if (input > 0)
                    {
                        return input;
                    }

                    return 0;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/RejectExtractReturn.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement branch = Assert.IsType<HirIfStatement>(method.Body.Statements[0]);
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ExtractCallablePatchOperation(
                branch.Id,
                method.Id,
                method.Body.Id,
                [branch.Id],
                null,
                @return.Id,
                "ExtractBranch")]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Equal(
            SemanticPatchDiagnosticCodes.InvalidCallableExtraction,
            Assert.Single(result.Diagnostics).Code);
        Assert.Contains("return/break/continue", result.Diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_CollapsesAndExpandsLexicalScopeWithStableChildIdentities()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool ready)
                {
                    int result = 0;
                    // preserve this range comment
                    result += 1;
                    if (ready)
                    {
                        result += 2;
                    }

                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/CollapseScope.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition =
            Assert.IsType<HirVariableDeclarationStatement>(method.Body.Statements[0]);
        HirExpressionStatement increment = Assert.IsType<HirExpressionStatement>(
            method.Body.Statements[1]);
        HirIfStatement branch = Assert.IsType<HirIfStatement>(method.Body.Statements[2]);
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[3]);
        HashSet<SemanticId> originalIds = method.DescendantsAndSelf()
            .Select(static node => node.Id)
            .ToHashSet();
        SemanticPatchDocument collapse = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new CollapseStatementRangePatchOperation(
                increment.Id,
                method.Body.Id,
                [increment.Id, branch.Id],
                resultDefinition.Id,
                @return.Id)]);

        CSharpPatchResult collapsed = new CSharpSourcePatchApplier().Apply(request, collapse);

        Assert.True(collapsed.Succeeded, string.Join(Environment.NewLine, collapsed.Diagnostics));
        string collapsedSource = Assert.Single(collapsed.PatchedRequest!.Sources).Source;
        Assert.Contains(
            "        {\n            // preserve this range comment",
            collapsedSource.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        HirMethodDeclaration collapsedMethod = Assert.Single(
            Assert.Single(collapsed.Analysis!.Hir!.Types).Methods);
        HirBlockStatement nested = Assert.IsType<HirBlockStatement>(collapsedMethod.Body.Statements[1]);
        Assert.Equal([increment.Id, branch.Id], nested.Statements.Select(static statement => statement.Id));
        Assert.DoesNotContain(nested.Id, originalIds);
        Assert.Equal(resultDefinition.Id, collapsedMethod.Body.Statements[0].Id);
        Assert.Equal(@return.Id, collapsedMethod.Body.Statements[2].Id);

        SemanticPatchDocument expand = SemanticPatchDocument.Create(
            collapsed.Analysis.Hir.Id,
            collapsed.Analysis.Semantics!.ConfigurationId,
            [new ExpandBlockPatchOperation(
                nested.Id,
                collapsedMethod.Body.Id,
                [increment.Id, branch.Id],
                resultDefinition.Id,
                @return.Id)]);
        CSharpSourceDocument collapsedDocument = Assert.Single(collapsed.PatchedRequest.Sources);
        var expandedRequest = new CSharpCompilationRequest(
            collapsed.PatchedRequest.AssemblyName,
            [new CSharpSourceDocument(
                collapsedDocument.LogicalPath,
                collapsedDocument.Source,
                Assert.Single(collapsed.Analysis.IdentitySidecars))],
            collapsed.PatchedRequest.ReferencePaths,
            collapsed.PatchedRequest.EffectCatalog,
            collapsed.PatchedRequest.Settings,
            collapsed.PatchedRequest.RuntimeReferencePaths,
            collapsed.PatchedRequest.IncludeDefaultReferences);
        CSharpPatchResult expanded = new CSharpSourcePatchApplier().Apply(
            expandedRequest,
            expand);

        Assert.True(expanded.Succeeded, string.Join(Environment.NewLine, expanded.Diagnostics));
        HirMethodDeclaration expandedMethod = Assert.Single(
            Assert.Single(expanded.Analysis!.Hir!.Types).Methods);
        Assert.Equal(
            [resultDefinition.Id, increment.Id, branch.Id, @return.Id],
            expandedMethod.Body.Statements.Select(static statement => statement.Id));
        Assert.Equal(
            originalIds.OrderBy(static id => id.Value),
            expandedMethod.DescendantsAndSelf()
                .Select(static node => node.Id)
                .OrderBy(static id => id.Value));
        Assert.Contains(
            Assert.Single(expanded.Analysis.IdentitySidecars).Tombstones,
            tombstone => tombstone.Id == nested.Id);
        Assert.True(Assert.Single(new GraphControlFlowAnalyzer().AnalyzeAndValidate(
            new HirGraphProjector().Project(
                expanded.Analysis.Hir,
                expanded.Analysis.Semantics!)).Functions).IsReducible);
    }

    [Fact]
    public void Apply_RejectsCollapseThatWouldMoveLocalOutOfScope()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run()
                {
                    int value = 1;
                    value += 2;
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/RejectUnsafeCollapse.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement value = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirExpressionStatement assignment = Assert.IsType<HirExpressionStatement>(
            method.Body.Statements[1]);
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[2]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new CollapseStatementRangePatchOperation(
                value.Id,
                method.Body.Id,
                [value.Id, assignment.Id],
                null,
                @return.Id)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error);
        Assert.DoesNotContain("{\r\n            int value", Assert.Single(request.Sources).Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_RejectsExpansionWithBraceOwnedComment()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run()
                {
                    // scope ownership
                    {
                        int value = 1;
                        value += 2;
                    }

                    return 0;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/RejectCommentExpansion.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirBlockStatement block = Assert.IsType<HirBlockStatement>(method.Body.Statements[0]);
        HirReturnStatement @return = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ExpandBlockPatchOperation(
                block.Id,
                method.Body.Id,
                block.Statements.Select(static statement => statement.Id).ToArray(),
                null,
                @return.Id)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Equal(
            SemanticPatchDiagnosticCodes.InvalidRegionExpansion,
            Assert.Single(result.Diagnostics).Code);
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
