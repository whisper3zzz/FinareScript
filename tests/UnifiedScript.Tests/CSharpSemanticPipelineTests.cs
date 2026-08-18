using UnifiedScript.CSharp;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Graph;
using UnifiedScript.Semantics;

namespace UnifiedScript.Tests;

public sealed class CSharpSemanticPipelineTests
{
    private const string Source = """
        using System;
        using UnifiedScript;

        namespace Game;

        public sealed class Calculator
        {
            public float Minimum { get; set; }

            [Visual]
            public float Calculate(float baseDamage, float multiplier)
            {
                float scaled = MathF.Max(baseDamage * multiplier, Minimum);
                Log(scaled);
                return scaled;
            }

            private void Log(float value)
            {
            }
        }
        """;

    [Fact]
    public void Analyze_BindsTypesSymbolsMethodsAndEffects()
    {
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(Source, "Calculator.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(result.Hir!.Types).Methods);
        HirVariableDeclarationStatement local =
            Assert.IsType<HirVariableDeclarationStatement>(method.Body.Statements[0]);
        HirInvocationExpression logCall = Assert.IsType<HirInvocationExpression>(
            Assert.IsType<HirExpressionStatement>(method.Body.Statements[1]).Expression);
        HirIdentifierExpression logArgument = Assert.IsType<HirIdentifierExpression>(Assert.Single(logCall.Arguments));
        HirIdentifierExpression returnedLocal = Assert.IsType<HirIdentifierExpression>(
            Assert.IsType<HirReturnStatement>(method.Body.Statements[2]).Expression);

        BoundCompilation semantics = result.Semantics!;
        BoundNodeInfo argumentBinding = FindNode(semantics, logArgument.Id);
        BoundNodeInfo returnBinding = FindNode(semantics, returnedLocal.Id);
        BoundNodeInfo logBinding = FindNode(semantics, logCall.Id);
        MethodDescriptor logMethod = FindMethod(semantics, logBinding.InvokedMethodId!.Value);

        Assert.Equal(local.Id, argumentBinding.ReferencedSymbolId);
        Assert.Equal(local.Id, returnBinding.ReferencedSymbolId);
        Assert.Equal("Game.Calculator.Log(System.Single)", logMethod.MetadataName);
        Assert.True(logMethod.Effects.HasFlag(MethodEffects.WritesState));
        Assert.All(
            method.Parameters,
            parameter => Assert.True(FindType(semantics, FindNode(semantics, parameter.Id).TypeId!.Value).IsGraphSafe));
    }

    [Fact]
    public void Project_UsesDataOnlyForPureCallsAndControlForImpureCalls()
    {
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(Source, "Calculator.cs");
        GraphFunction graph = Assert.Single(new HirGraphProjector()
            .Project(result.Hir!, result.Semantics!)
            .Functions);

        GraphNode mathCall = Assert.Single(
            graph.Nodes,
            static node => node.Kind == "invocation" && node.Properties["method"].Contains("MathF.Max"));
        GraphNode logCall = Assert.Single(
            graph.Nodes,
            static node => node.Kind == "invocation" && node.Properties["method"].Contains("Calculator.Log"));

        Assert.DoesNotContain(mathCall.Ports, static port => port.Kind == GraphPortKind.Control);
        Assert.Contains(mathCall.Ports, static port => port.Name == "value" && port.Kind == GraphPortKind.Data);
        Assert.Contains(logCall.Ports, static port => port.Name == "execIn" && port.Kind == GraphPortKind.Control);
        Assert.Contains(logCall.Ports, static port => port.Name == "execOut" && port.Kind == GraphPortKind.Control);
        Assert.DoesNotContain(logCall.Ports, static port => port.Name == "value");
        Assert.Contains(graph.Edges, edge => edge.Kind == GraphEdgeKind.Control && edge.ToNode == logCall.Id);
        Assert.Contains(graph.Edges, edge => edge.Kind == GraphEdgeKind.Control && edge.FromNode == logCall.Id);
    }

    [Fact]
    public void Analyze_RejectsGraphUnsafeVisualSignature()
    {
        const string source = """
            using UnifiedScript;

            sealed class Door
            {
            }

            sealed class Controller
            {
                [Visual]
                Door Echo(Door value)
                {
                    return value;
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(source, "Controller.cs");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Hir);
        Assert.Null(result.Semantics);
        Assert.Equal(
            2,
            result.Diagnostics.Count(
                static diagnostic => diagnostic.Code == SemanticDiagnosticCodes.GraphUnsafeSignature));
    }

    [Fact]
    public void Analyze_IsDeterministicIncludingSemanticJson()
    {
        CSharpAnalysisResult first = new CSharpSemanticPipeline().Analyze(Source, "Calculator.cs");
        CSharpAnalysisResult second = new CSharpSemanticPipeline().Analyze(Source, "Calculator.cs");

        Assert.Equal(
            SemanticJson.Serialize(first.Semantics!),
            SemanticJson.Serialize(second.Semantics!));
        Assert.Contains(
            HirDocument.CurrentSchemaVersion,
            HirJson.Serialize(HirDocument.Create(first.Hir!)));
    }

    [Fact]
    public void Project_SequencesTaskInvocationAwaitAndReturn()
    {
        const string source = """
            using System.Threading.Tasks;
            using UnifiedScript;

            static class Loader
            {
                [Visual]
                public static async Task<int> Load()
                {
                    return await Fetch();
                }

                private static Task<int> Fetch() => Task.FromResult(42);
            }
            """;
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(source, "Loader.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        GraphFunction graph = Assert.Single(new HirGraphProjector()
            .Project(result.Hir!, result.Semantics!)
            .Functions);
        GraphNode invocation = Assert.Single(
            graph.Nodes,
            static node => node.Kind == "invocation" && node.Properties["method"].Contains("Loader.Fetch"));
        GraphNode awaitNode = Assert.Single(graph.Nodes, static node => node.Kind == "await");
        GraphNode returnNode = Assert.Single(graph.Nodes, static node => node.Kind == "return");

        Assert.Contains(
            graph.Edges,
            edge => edge.Kind == GraphEdgeKind.Control &&
                edge.FromNode == invocation.Id && edge.FromPort == "execOut" &&
                edge.ToNode == awaitNode.Id && edge.ToPort == "execIn");
        Assert.Contains(
            graph.Edges,
            edge => edge.Kind == GraphEdgeKind.Control &&
                edge.FromNode == awaitNode.Id && edge.FromPort == "execOut" &&
                edge.ToNode == returnNode.Id && edge.ToPort == "in");
        Assert.Equal("readsState|writesState|latent|async", invocation.Properties["effects"]);
        Assert.Equal("int", awaitNode.Properties["resolvedType"]);
    }

    [Fact]
    public void AnalyzeAndProject_PreserveTypedLazyConditionalBranches()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Select(bool enabled, int value, int fallback)
                {
                    return enabled ? value + 1 : fallback;
                }
            }
            """;
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "ConditionalRules.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirCompilationUnit hir = Assert.IsType<HirCompilationUnit>(result.Hir);
        BoundCompilation semantics = Assert.IsType<BoundCompilation>(result.Semantics);
        HirConditionalExpression conditional = Assert.Single(
            hir.DescendantsAndSelf().OfType<HirConditionalExpression>());
        BoundNodeInfo conditionalBinding = FindNode(semantics, conditional.Id);
        BoundNodeInfo conditionBinding = FindNode(semantics, conditional.Condition.Id);
        Assert.Equal(UnifiedTypeKind.Integer, FindType(
            semantics,
            conditionalBinding.TypeId!.Value).Kind);
        Assert.Equal(UnifiedTypeKind.Boolean, FindType(
            semantics,
            conditionBinding.TypeId!.Value).Kind);

        GraphDocument document = new HirGraphProjector().Project(hir, semantics);
        Assert.Equal("unifiedscript.graph/v0.10", document.SchemaVersion);
        GraphFunction graph = Assert.Single(document.Functions);
        GraphNode select = Assert.Single(graph.Nodes, static node => node.Kind == "conditional");
        Assert.Equal("Select", select.Label);
        Assert.Equal("lazyBranches", select.Properties["evaluation"]);
        Assert.Contains(select.Ports, static port => port.Name == "condition");
        Assert.Contains(select.Ports, static port => port.Name == "whenTrue");
        Assert.Contains(select.Ports, static port => port.Name == "whenFalse");
        Assert.Contains(select.Ports, static port =>
            port.Name == "value" && port.Direction == GraphPortDirection.Output);
        Assert.Contains(graph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Data && edge.ToNode == select.Id &&
            edge.ToPort == "condition");
        Assert.Contains(graph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Data && edge.ToNode == select.Id &&
            edge.ToPort == "whenTrue");
        Assert.Contains(graph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Data && edge.ToNode == select.Id &&
            edge.ToPort == "whenFalse");
    }

    [Fact]
    public void Analyze_RejectsEffectfulConditionalBranchesThatCannotRemainLazy()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Select(bool enabled, int value)
                {
                    return enabled ? Mutate(value) : value;
                }

                static int Mutate(int value)
                {
                    return value + 1;
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "EffectfulConditional.cs");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Hir);
        Assert.Null(result.Semantics);
        UnifiedDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == SemanticDiagnosticCodes.ConditionalBranchEffectsNotSupported);
        Assert.Contains("lazy branch evaluation", diagnostic.Message, StringComparison.Ordinal);
        Assert.IsType<HirInvocationExpression>(Assert.Single(
            result.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>()));
    }

    [Fact]
    public void AnalyzeAndProject_DeclareShortCircuitRightEvaluationForLogicalOperators()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static bool All(bool left, bool right)
                {
                    return left && right;
                }

                [Visual]
                static bool Any(bool left, bool right)
                {
                    return left || right;
                }

                [Visual]
                static bool EagerAll(bool left, bool right)
                {
                    return left & right;
                }

                [Visual]
                static bool EagerAny(bool left, bool right)
                {
                    return left | right;
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "ShortCircuitRules.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirCompilationUnit hir = Assert.IsType<HirCompilationUnit>(result.Hir);
        BoundCompilation semantics = Assert.IsType<BoundCompilation>(result.Semantics);
        GraphDocument document = new HirGraphProjector().Project(hir, semantics);
        Assert.Equal("unifiedscript.graph/v0.10", document.SchemaVersion);
        GraphNode[] logicalNodes = document.Functions
            .SelectMany(static function => function.Nodes)
            .Where(static node => node.Kind == "binary" &&
                node.Properties["operator"] is "&&" or "||")
            .OrderBy(static node => node.Properties["operator"], StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(2, logicalNodes.Length);
        GraphNode logicalAnd = Assert.Single(
            logicalNodes,
            static node => node.Properties["operator"] == "&&");
        GraphNode logicalOr = Assert.Single(
            logicalNodes,
            static node => node.Properties["operator"] == "||");
        Assert.Equal("shortCircuitRight", logicalAnd.Properties["evaluation"]);
        Assert.Equal("whenLeftTrue", logicalAnd.Properties["rightEvaluation"]);
        Assert.Equal("shortCircuitRight", logicalOr.Properties["evaluation"]);
        Assert.Equal("whenLeftFalse", logicalOr.Properties["rightEvaluation"]);
        Assert.All(logicalNodes, static node =>
        {
            Assert.Contains(node.Ports, static port => port.Name == "left");
            Assert.Contains(node.Ports, static port => port.Name == "right");
            Assert.Contains(node.Ports, static port => port.Name == "value");
        });
        GraphNode[] eagerNodes = document.Functions
            .SelectMany(static function => function.Nodes)
            .Where(static node => node.Kind == "binary" &&
                node.Properties["operator"] is "&" or "|")
            .ToArray();
        Assert.Equal(2, eagerNodes.Length);
        Assert.All(eagerNodes, static node =>
        {
            Assert.DoesNotContain("evaluation", node.Properties.Keys);
            Assert.DoesNotContain("rightEvaluation", node.Properties.Keys);
        });
    }

    [Fact]
    public void Analyze_RejectsEffectfulShortCircuitRightOperands()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static bool All(bool enabled)
                {
                    return enabled && Probe();
                }

                [Visual]
                static bool Any(bool enabled)
                {
                    return enabled || Probe();
                }

                [Visual]
                static bool Nested(bool left, bool right)
                {
                    return left && (right && Probe());
                }

                static bool Probe()
                {
                    return true;
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "EffectfulShortCircuitRight.cs");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Hir);
        Assert.Null(result.Semantics);
        UnifiedDiagnostic[] diagnostics = result.Diagnostics
            .Where(static diagnostic =>
                diagnostic.Code == SemanticDiagnosticCodes.ShortCircuitRightEffectsNotSupported)
            .ToArray();
        Assert.Equal(3, diagnostics.Length);
        Assert.All(diagnostics, static diagnostic =>
            Assert.Contains("short-circuit evaluation", diagnostic.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeAndProject_SequenceAlwaysEvaluatedShortCircuitLeftEffects()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static bool Run(bool enabled)
                {
                    return Probe() && enabled;
                }

                static bool Probe()
                {
                    return true;
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "EffectfulShortCircuitLeft.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirCompilationUnit hir = Assert.IsType<HirCompilationUnit>(result.Hir);
        BoundCompilation semantics = Assert.IsType<BoundCompilation>(result.Semantics);
        GraphFunction graph = Assert.Single(new HirGraphProjector()
            .Project(hir, semantics)
            .Functions);
        GraphNode invocation = Assert.Single(graph.Nodes, static node =>
            node.Kind == "invocation" && node.Properties["method"].Contains("Rules.Probe"));
        GraphNode logicalAnd = Assert.Single(graph.Nodes, static node =>
            node.Kind == "binary" && node.Properties["operator"] == "&&");
        GraphNode returnNode = Assert.Single(graph.Nodes, static node => node.Kind == "return");

        Assert.Equal("whenLeftTrue", logicalAnd.Properties["rightEvaluation"]);
        Assert.Contains(graph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Data &&
            edge.FromNode == invocation.Id && edge.FromPort == "value" &&
            edge.ToNode == logicalAnd.Id && edge.ToPort == "left");
        Assert.Contains(graph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Control &&
            edge.FromNode == invocation.Id && edge.FromPort == "execOut" &&
            edge.ToNode == returnNode.Id && edge.ToPort == "in");
    }

    [Fact]
    public void AnalyzeAndProject_AllowsCataloguedPureShortCircuitRightCalls()
    {
        const string source = """
            using System;
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static bool Run(bool enabled, int value)
                {
                    return enabled && Math.Abs(value) > 0;
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "PureShortCircuitRight.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirCompilationUnit hir = Assert.IsType<HirCompilationUnit>(result.Hir);
        BoundCompilation semantics = Assert.IsType<BoundCompilation>(result.Semantics);
        GraphFunction graph = Assert.Single(new HirGraphProjector()
            .Project(hir, semantics)
            .Functions);
        GraphNode invocation = Assert.Single(graph.Nodes, static node =>
            node.Kind == "invocation" && node.Properties["method"].Contains("System.Math.Abs"));
        GraphNode logicalAnd = Assert.Single(graph.Nodes, static node =>
            node.Kind == "binary" && node.Properties["operator"] == "&&");

        Assert.Equal("whenLeftTrue", logicalAnd.Properties["rightEvaluation"]);
        Assert.DoesNotContain(invocation.Ports, static port =>
            port.Kind == GraphPortKind.Control);
    }

    [Fact]
    public void AnalyzeAndProject_DeclareNullCoalescingRightEvaluationAndTypes()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int ResolveNumber(int? value, int fallback)
                {
                    return value ?? fallback;
                }

                [Visual]
                static string ResolveText(string? value, string fallback)
                {
                    return value ?? fallback;
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "CoalesceRules.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirCompilationUnit hir = Assert.IsType<HirCompilationUnit>(result.Hir);
        BoundCompilation semantics = Assert.IsType<BoundCompilation>(result.Semantics);
        HirBinaryExpression[] coalesces = hir.DescendantsAndSelf()
            .OfType<HirBinaryExpression>()
            .Where(static binary => binary.Operator == "??")
            .ToArray();
        Assert.Equal(2, coalesces.Length);
        Assert.All(coalesces, coalesce =>
        {
            TypeDescriptor leftType = FindType(
                semantics,
                FindNode(semantics, coalesce.Left.Id).TypeId!.Value);
            Assert.Equal(UnifiedNullability.Nullable, leftType.Nullability);
            Assert.Equal(
                FindNode(semantics, coalesce.Right.Id).TypeId,
                FindNode(semantics, coalesce.Id).TypeId);
        });

        GraphDocument document = new HirGraphProjector().Project(hir, semantics);
        Assert.Equal("unifiedscript.graph/v0.10", document.SchemaVersion);
        GraphNode[] graphCoalesces = document.Functions
            .SelectMany(static function => function.Nodes)
            .Where(static node => node.Kind == "binary" &&
                node.Properties["operator"] == "??")
            .ToArray();
        Assert.Equal(2, graphCoalesces.Length);
        Assert.All(graphCoalesces, static node =>
        {
            Assert.Equal("shortCircuitRight", node.Properties["evaluation"]);
            Assert.Equal("whenLeftNull", node.Properties["rightEvaluation"]);
            Assert.Equal("leftNonNullOrRight", node.Properties["resultSelection"]);
            Assert.Contains(node.Ports, static port => port.Name == "left");
            Assert.Contains(node.Ports, static port => port.Name == "right");
            Assert.Contains(node.Ports, static port => port.Name == "value");
        });
    }

    [Fact]
    public void Analyze_RejectsEffectfulNullCoalescingRightOperands()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Resolve(int? value)
                {
                    return value ?? Probe();
                }

                [Visual]
                static int ResolveNested(int? first, int? second)
                {
                    return first ?? (second ?? Probe());
                }

                static int Probe()
                {
                    return 1;
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "EffectfulCoalesceRight.cs");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Hir);
        Assert.Null(result.Semantics);
        UnifiedDiagnostic[] diagnostics = result.Diagnostics
            .Where(static diagnostic =>
                diagnostic.Code == SemanticDiagnosticCodes.CoalesceRightEffectsNotSupported)
            .ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, static diagnostic =>
            Assert.Contains("null-coalescing evaluation", diagnostic.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_RejectsNullCoalescingConversionsNotSerializedForTheSharedVm()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Resolve(short? value, int fallback)
                {
                    return value ?? fallback;
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "ConvertedCoalesce.cs");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Hir);
        Assert.Null(result.Semantics);
        UnifiedDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == SemanticDiagnosticCodes.CoalesceConversionNotSupported);
        Assert.Contains("shared VM execution", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeAndProject_SequenceNullCoalescingLeftEffectsAndAllowPureRightCalls()
    {
        const string source = """
            using System;
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int PollThenFallback(int fallback)
                {
                    return Poll() ?? fallback;
                }

                [Visual]
                static int PureFallback(int? value, int fallback)
                {
                    return value ?? Math.Abs(fallback);
                }

                static int? Poll()
                {
                    return null;
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "CoalesceEffects.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirCompilationUnit hir = Assert.IsType<HirCompilationUnit>(result.Hir);
        BoundCompilation semantics = Assert.IsType<BoundCompilation>(result.Semantics);
        GraphDocument document = new HirGraphProjector().Project(hir, semantics);
        GraphFunction effectfulGraph = Assert.Single(document.Functions, static function =>
            function.QualifiedName.EndsWith("PollThenFallback", StringComparison.Ordinal));
        GraphNode poll = Assert.Single(effectfulGraph.Nodes, static node =>
            node.Kind == "invocation" && node.Properties["method"].Contains("Rules.Poll"));
        GraphNode effectfulReturn = Assert.Single(
            effectfulGraph.Nodes,
            static node => node.Kind == "return");
        Assert.Contains(effectfulGraph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Control &&
            edge.FromNode == poll.Id && edge.FromPort == "execOut" &&
            edge.ToNode == effectfulReturn.Id && edge.ToPort == "in");

        GraphFunction pureGraph = Assert.Single(document.Functions, static function =>
            function.QualifiedName.EndsWith("PureFallback", StringComparison.Ordinal));
        GraphNode math = Assert.Single(pureGraph.Nodes, static node =>
            node.Kind == "invocation" && node.Properties["method"].Contains("System.Math.Abs"));
        GraphNode coalesce = Assert.Single(pureGraph.Nodes, static node =>
            node.Kind == "binary" && node.Properties["operator"] == "??");
        Assert.Equal("whenLeftNull", coalesce.Properties["rightEvaluation"]);
        Assert.DoesNotContain(math.Ports, static port => port.Kind == GraphPortKind.Control);
    }

    [Fact]
    public void AnalyzeAndProject_PreservesNullConditionalReceiverEvaluationAndLiftedResult()
    {
        const string source = """
            #nullable enable

            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int? Measure(string? value)
                {
                    return Observe(value)?.Length;
                }

                static string? Observe(string? value)
                {
                    return value;
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "NullConditionalEffects.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirCompilationUnit hir = Assert.IsType<HirCompilationUnit>(result.Hir);
        BoundCompilation semantics = Assert.IsType<BoundCompilation>(result.Semantics);
        HirConditionalAccessExpression conditionalAccess = Assert.Single(
            hir.DescendantsAndSelf().OfType<HirConditionalAccessExpression>());
        HirInvocationExpression receiver = Assert.IsType<HirInvocationExpression>(
            conditionalAccess.Receiver);
        HirConditionalMemberBindingExpression memberBinding =
            Assert.IsType<HirConditionalMemberBindingExpression>(conditionalAccess.WhenNotNull);
        TypeDescriptor resultType = FindType(
            semantics,
            FindNode(semantics, conditionalAccess.Id).TypeId!.Value);
        Assert.Equal(UnifiedTypeKind.Integer, resultType.Kind);
        Assert.Equal(UnifiedNullability.Nullable, resultType.Nullability);

        GraphDocument document = new HirGraphProjector().Project(hir, semantics);
        Assert.Equal("unifiedscript.graph/v0.10", document.SchemaVersion);
        GraphFunction graph = Assert.Single(document.Functions);
        GraphNode conditionalNode = Assert.Single(
            graph.Nodes,
            node => node.Id == conditionalAccess.Id && node.Kind == "conditionalAccess");
        GraphNode memberNode = Assert.Single(
            graph.Nodes,
            node => node.Id == memberBinding.Id && node.Kind == "conditionalMemberBinding");
        GraphNode receiverNode = Assert.Single(graph.Nodes, node => node.Id == receiver.Id);
        GraphNode valueReturn = Assert.Single(graph.Nodes, static node => node.Kind == "return");
        Assert.Equal("conditionalWhenNotNull", conditionalNode.Properties["evaluation"]);
        Assert.Equal(
            "whenReceiverNonNull",
            conditionalNode.Properties["whenNotNullEvaluation"]);
        Assert.Equal("whenNotNullOrNull", conditionalNode.Properties["resultSelection"]);
        Assert.Contains(graph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Data &&
            edge.FromNode == receiverNode.Id && edge.FromPort == "value" &&
            edge.ToNode == conditionalNode.Id && edge.ToPort == "receiver");
        Assert.Contains(graph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Data &&
            edge.FromNode == receiverNode.Id && edge.FromPort == "value" &&
            edge.ToNode == memberNode.Id && edge.ToPort == "target");
        Assert.Contains(graph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Data &&
            edge.FromNode == memberNode.Id && edge.FromPort == "value" &&
            edge.ToNode == conditionalNode.Id && edge.ToPort == "whenNotNull");
        Assert.Contains(graph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Control &&
            edge.FromNode == receiverNode.Id && edge.FromPort == "execOut" &&
            edge.ToNode == valueReturn.Id && edge.ToPort == "in");
    }

    [Fact]
    public void Analyze_RejectsEffectfulNullConditionalPortionsThatCannotRemainLazy()
    {
        const string source = """
            #nullable enable

            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static string? Normalize(string? value)
                {
                    return value?.Trim();
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "EffectfulNullConditional.cs");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Hir);
        Assert.Null(result.Semantics);
        UnifiedDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == SemanticDiagnosticCodes.ConditionalAccessEffectsNotSupported);
        Assert.Contains("null-conditional evaluation", diagnostic.Message, StringComparison.Ordinal);
        Assert.IsType<HirInvocationExpression>(Assert.Single(
            result.Hir!.DescendantsAndSelf().OfType<HirConditionalAccessExpression>()).WhenNotNull);
    }

    [Fact]
    public void AnalyzeAndProject_AllowsCataloguedPureNullConditionalCallChains()
    {
        const string source = """
            #nullable enable

            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int? NormalizeAndMeasure(string? value)
                {
                    return value?.Trim()?.Length;
                }
            }
            """;
        MethodEffectCatalog catalog = MethodEffectCatalog.CreateDefault().WithOverrides(
        [
            new MethodEffectRule(
                "system.string.trim.pure",
                "*",
                "System.String",
                "Trim",
                MethodEffects.None),
        ]);
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "PureNullConditionalChain.cs",
            effectCatalog: catalog);

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirCompilationUnit hir = Assert.IsType<HirCompilationUnit>(result.Hir);
        BoundCompilation semantics = Assert.IsType<BoundCompilation>(result.Semantics);
        Assert.Equal(
            2,
            hir.DescendantsAndSelf().OfType<HirConditionalAccessExpression>().Count());
        Assert.Equal(
            2,
            hir.DescendantsAndSelf().OfType<HirConditionalMemberBindingExpression>().Count());
        GraphFunction graph = Assert.Single(new HirGraphProjector().Project(hir, semantics).Functions);
        GraphNode trim = Assert.Single(graph.Nodes, static node =>
            node.Kind == "invocation" && node.Properties["method"].Contains("System.String.Trim"));
        Assert.DoesNotContain(trim.Ports, static port => port.Kind == GraphPortKind.Control);
        Assert.Equal(
            2,
            graph.Nodes.Count(static node => node.Kind == "conditionalAccess"));
        Assert.All(
            graph.Nodes.Where(static node => node.Kind == "conditionalAccess"),
            static node => Assert.Equal(
                "whenReceiverNonNull",
                node.Properties["whenNotNullEvaluation"]));
    }

    [Fact]
    public void AnalyzeAndProject_PreservesForPhaseOrderAndContinueIncrementTarget()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Sum(int count)
                {
                    int total = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (i == 2)
                        {
                            continue;
                        }

                        total = total + i;
                    }

                    return total;
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(source, "ForPhases.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirCompilationUnit hir = Assert.IsType<HirCompilationUnit>(result.Hir);
        HirForStatement statement = Assert.Single(hir.DescendantsAndSelf().OfType<HirForStatement>());
        HirForVariableDeclarator variable = Assert.Single(statement.Declaration!.Variables);
        HirUnaryExpression increment = Assert.IsType<HirUnaryExpression>(
            Assert.Single(statement.Incrementors));
        HirContinueStatement nextIteration = Assert.Single(
            statement.Body.DescendantsAndSelf().OfType<HirContinueStatement>());
        HirIdentifierExpression conditionLocal = Assert.IsType<HirIdentifierExpression>(
            Assert.IsType<HirBinaryExpression>(statement.Condition).Left);
        Assert.Equal(
            variable.Id,
            FindNode(result.Semantics!, conditionLocal.Id).ReferencedSymbolId);

        GraphDocument document = new HirGraphProjector().Project(hir, result.Semantics);
        Assert.Equal("unifiedscript.graph/v0.10", document.SchemaVersion);
        GraphFunction graph = Assert.Single(document.Functions);
        GraphNode loopNode = Assert.Single(graph.Nodes, node => node.Id == statement.Id);
        GraphNode variableNode = Assert.Single(graph.Nodes, node => node.Id == variable.Id);
        GraphNode incrementNode = Assert.Single(graph.Nodes, node => node.Id == increment.Id);
        GraphRegion loopRegion = Assert.Single(graph.Regions, region =>
            region.Kind == GraphRegionKind.Loop && region.SourceNodeId == statement.Id);
        GraphRegion initializerRegion = graph.Regions.Single(region =>
            region.Kind == GraphRegionKind.LoopInitializer &&
            region.ParentRegionId == loopRegion.Id);
        GraphRegion bodyRegion = graph.Regions.Single(region =>
            region.Kind == GraphRegionKind.LoopBody &&
            region.ParentRegionId == loopRegion.Id);
        GraphRegion incrementRegion = graph.Regions.Single(region =>
            region.Kind == GraphRegionKind.LoopIncrement &&
            region.ParentRegionId == loopRegion.Id);

        Assert.Equal(
            [initializerRegion.Id, bodyRegion.Id, incrementRegion.Id],
            loopRegion.ChildRegionIds);
        Assert.Equal("for", loopNode.Properties["loopForm"]);
        Assert.Equal("initializer,condition,body,incrementors", loopNode.Properties["evaluationOrder"]);
        Assert.Equal("incrementors", loopNode.Properties["continueTarget"]);
        Assert.Equal("forInitializer", variableNode.Properties["declarationContext"]);
        Assert.Equal("postfix", incrementNode.Properties["position"]);
        Assert.Contains(incrementNode.Ports, static port =>
            port.Name == "execIn" && port.Kind == GraphPortKind.Control);
        Assert.Contains(graph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Control &&
            edge.FromNode == variable.Id && edge.FromPort == "then" &&
            edge.ToNode == statement.Id && edge.ToPort == "in");
        Assert.Contains(graph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Control &&
            edge.FromNode == nextIteration.Id && edge.FromPort == "then" &&
            edge.ToNode == increment.Id && edge.ToPort == "execIn");
        Assert.Contains(graph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Control &&
            edge.FromNode == increment.Id && edge.FromPort == "execOut" &&
            edge.ToNode == statement.Id && edge.ToPort == "in");

        GraphControlFlowFunction flowFunction = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(document).Functions);
        GraphControlFlowLoop flow = Assert.Single(flowFunction.Loops);
        Assert.Equal(initializerRegion.Id, flow.InitializerRegionId);
        Assert.Equal(incrementRegion.Id, flow.IncrementRegionId);
        Assert.Equal(statement.Id, flow.HeaderEntryNodeId);
        Assert.Equal(increment.Id, flow.ContinueTargetNodeId);
        Assert.Contains(nextIteration.Id, flow.ContinueNodeIds);
        Assert.Contains(flow.BackEdgeArcIds, arcId =>
            flowFunction.Arcs.Single(arc => arc.Id == arcId).FromNodeId == increment.Id);
    }

    private static BoundNodeInfo FindNode(BoundCompilation semantics, SemanticId id) =>
        Assert.Single(semantics.Nodes, node => node.NodeId == id);

    private static MethodDescriptor FindMethod(BoundCompilation semantics, SemanticId id) =>
        Assert.Single(semantics.Methods, method => method.Id == id);

    private static TypeDescriptor FindType(BoundCompilation semantics, SemanticId id) =>
        Assert.Single(semantics.Types, type => type.Id == id);
}
