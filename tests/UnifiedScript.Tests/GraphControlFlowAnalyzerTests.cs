using System.Text.Json;
using UnifiedScript.CSharp;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Graph;

namespace UnifiedScript.Tests;

public sealed class GraphControlFlowAnalyzerTests
{
    [Fact]
    public void Analyze_ComputesStraightLineDominanceAndSyntheticExit()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run()
                {
                    int value = 1;
                    return value;
                }
            }
            """;
        (HirMethodDeclaration method, GraphDocument graph) = Project(source, "StraightLine.cs");
        HirVariableDeclarationStatement local = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement result = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);

        GraphControlFlowFunction flow = Assert.Single(new GraphControlFlowAnalyzer()
            .AnalyzeAndValidate(graph)
            .Functions);
        GraphControlFlowNode entry = Node(flow, flow.EntryNodeId);
        GraphControlFlowNode localNode = Node(flow, local.Id);
        GraphControlFlowNode returnNode = Node(flow, result.Id);
        GraphControlFlowNode exit = Node(flow, flow.ExitNodeId);

        Assert.Equal(4, flow.Nodes.Count);
        Assert.True(exit.IsSyntheticExit);
        Assert.All(flow.Nodes, static node => Assert.True(node.IsReachable));
        Assert.Empty(flow.UnreachableNodeIds);
        Assert.True(flow.HasCompleteExitCoverage);
        Assert.True(flow.IsReducible);
        Assert.Empty(flow.CyclicComponents);
        Assert.Equal(entry.Id, localNode.ImmediateDominatorId);
        Assert.Equal(localNode.Id, returnNode.ImmediateDominatorId);
        Assert.Equal(returnNode.Id, exit.ImmediateDominatorId);
        Assert.Equal(localNode.Id, entry.ImmediatePostDominatorId);
        Assert.Equal(returnNode.Id, localNode.ImmediatePostDominatorId);
        Assert.Equal(exit.Id, returnNode.ImmediatePostDominatorId);
        Assert.Contains(
            flow.Arcs,
            arc => arc.IsSynthetic &&
                arc.FromNodeId == result.Id &&
                arc.FromPort == "return" &&
                arc.ToNodeId == exit.Id);
    }

    [Fact]
    public void Analyze_FindsStructuredMergeForTwoFallingThroughBranches()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(bool ready)
                {
                    if (ready)
                    {
                        int thenValue = 1;
                    }
                    else
                    {
                        int elseValue = 2;
                    }

                    return 0;
                }
            }
            """;
        (HirMethodDeclaration method, GraphDocument graph) = Project(source, "BranchMerge.cs");
        HirIfStatement statement = Assert.IsType<HirIfStatement>(method.Body.Statements[0]);
        HirVariableDeclarationStatement thenLocal = Assert.IsType<HirVariableDeclarationStatement>(
            Assert.Single(statement.Then.Statements));
        HirVariableDeclarationStatement elseLocal = Assert.IsType<HirVariableDeclarationStatement>(
            Assert.Single(statement.Else!.Statements));
        HirReturnStatement merge = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);

        GraphControlFlowFunction flow = Assert.Single(new GraphControlFlowAnalyzer()
            .AnalyzeAndValidate(graph)
            .Functions);
        GraphControlFlowBranch branch = Assert.Single(flow.Branches);
        GraphControlFlowNode branchNode = Node(flow, statement.Id);
        GraphControlFlowNode mergeNode = Node(flow, merge.Id);

        Assert.Equal(thenLocal.Id, branch.ThenEntryNodeId);
        Assert.Equal(elseLocal.Id, branch.ElseEntryNodeId);
        Assert.Equal(merge.Id, branch.MergeNodeId);
        Assert.False(branch.ThenExitsFunction);
        Assert.False(branch.ElseExitsFunction);
        Assert.Equal(statement.Id, mergeNode.ImmediateDominatorId);
        Assert.Equal(merge.Id, branchNode.ImmediatePostDominatorId);
        Assert.DoesNotContain(thenLocal.Id, mergeNode.DominatorIds);
        Assert.DoesNotContain(elseLocal.Id, mergeNode.DominatorIds);
        Assert.Contains(merge.Id, Node(flow, thenLocal.Id).PostDominatorIds);
        Assert.Contains(merge.Id, Node(flow, elseLocal.Id).PostDominatorIds);
    }

    [Fact]
    public void Analyze_ClassifiesOneSidedEarlyReturnAndImplicitElseFallthrough()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static bool Run(bool ready)
                {
                    if (ready)
                    {
                        return true;
                    }

                    return false;
                }
            }
            """;
        (HirMethodDeclaration method, GraphDocument graph) = Project(source, "EarlyReturn.cs");
        HirIfStatement statement = Assert.IsType<HirIfStatement>(method.Body.Statements[0]);
        HirReturnStatement thenReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(statement.Then.Statements));
        HirReturnStatement fallback = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);

        GraphControlFlowFunction flow = Assert.Single(new GraphControlFlowAnalyzer()
            .AnalyzeAndValidate(graph)
            .Functions);
        GraphControlFlowBranch branch = Assert.Single(flow.Branches);

        Assert.Equal(thenReturn.Id, branch.ThenEntryNodeId);
        Assert.Equal(fallback.Id, branch.ElseEntryNodeId);
        Assert.Null(branch.MergeNodeId);
        Assert.True(branch.ThenExitsFunction);
        Assert.False(branch.ElseExitsFunction);
        Assert.Equal(flow.ExitNodeId, Node(flow, statement.Id).ImmediatePostDominatorId);
        Assert.Equal(flow.ExitNodeId, Node(flow, thenReturn.Id).ImmediatePostDominatorId);
        Assert.Equal(flow.ExitNodeId, Node(flow, fallback.Id).ImmediatePostDominatorId);
    }

    [Fact]
    public void Analyze_ClassifiesBothExplicitBranchesAsFunctionExits()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static bool Run(bool ready)
                {
                    if (ready)
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            """;
        (_, GraphDocument graph) = Project(source, "BothReturn.cs");

        GraphControlFlowFunction flow = Assert.Single(new GraphControlFlowAnalyzer()
            .AnalyzeAndValidate(graph)
            .Functions);
        GraphControlFlowBranch branch = Assert.Single(flow.Branches);

        Assert.Null(branch.MergeNodeId);
        Assert.True(branch.ThenExitsFunction);
        Assert.True(branch.ElseExitsFunction);
        Assert.Equal(flow.ExitNodeId, Node(flow, branch.BranchNodeId).ImmediatePostDominatorId);
    }

    [Fact]
    public void Analyze_LeavesUnreachableControlNodeOutsideDominanceRelations()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static int Run()
                {
                    return 1;
                    int late = 2;
                }
            }
            """;
        (HirMethodDeclaration method, GraphDocument graph) = Project(source, "Unreachable.cs");
        HirVariableDeclarationStatement late = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[1]);

        GraphControlFlowFunction flow = Assert.Single(new GraphControlFlowAnalyzer()
            .AnalyzeAndValidate(graph)
            .Functions);
        GraphControlFlowNode lateNode = Node(flow, late.Id);

        Assert.False(lateNode.IsReachable);
        Assert.Equal([late.Id], flow.UnreachableNodeIds);
        Assert.Empty(lateNode.DominatorIds);
        Assert.Null(lateNode.ImmediateDominatorId);
        Assert.Empty(lateNode.PostDominatorIds);
        Assert.Null(lateNode.ImmediatePostDominatorId);
        Assert.True(flow.HasCompleteExitCoverage);
    }

    [Fact]
    public void Analyze_DetectsAndValidationRejectsIrreducibleTwoEntryCycle()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static int Run(bool ready)
                {
                    if (ready)
                    {
                        int thenValue = 1;
                    }
                    else
                    {
                        int elseValue = 2;
                    }

                    return 0;
                }
            }
            """;
        (_, GraphDocument original) = Project(source, "Irreducible.cs");
        GraphFunction function = Assert.Single(original.Functions);
        GraphNode thenNode = Assert.Single(
            function.Nodes,
            static node => node.Kind == "variable" && node.Properties["name"] == "thenValue");
        GraphNode elseNode = Assert.Single(
            function.Nodes,
            static node => node.Kind == "variable" && node.Properties["name"] == "elseValue");
        GraphNode fallback = Assert.Single(function.Nodes, static node => node.Kind == "return");
        GraphEdge[] edges = function.Edges.Select(edge =>
        {
            if (edge.Kind != GraphEdgeKind.Control || edge.ToNode != fallback.Id)
            {
                return edge;
            }

            SemanticId newTarget = edge.FromNode == thenNode.Id ? elseNode.Id : thenNode.Id;
            return edge with
            {
                Id = SemanticId.FromKey($"irreducible|{edge.FromNode}|{newTarget}"),
                ToNode = newTarget,
            };
        }).ToArray();
        GraphDocument irreducible = original with
        {
            Functions = [function with { Edges = edges }],
        };
        var analyzer = new GraphControlFlowAnalyzer();

        GraphControlFlowFunction flow = Assert.Single(analyzer.Analyze(irreducible).Functions);

        Assert.False(flow.IsReducible);
        Assert.False(flow.HasCompleteExitCoverage);
        GraphControlFlowComponent component = Assert.Single(flow.CyclicComponents);
        Assert.Equal(
            new[] { thenNode.Id, elseNode.Id }.OrderBy(static id => id.Value),
            component.NodeIds);
        Assert.Equal(
            new[] { thenNode.Id, elseNode.Id }.OrderBy(static id => id.Value),
            component.EntryNodeIds);
        Assert.False(component.IsReducible);
        Assert.Throws<InvalidOperationException>(() => analyzer.AnalyzeAndValidate(irreducible));
    }

    [Fact]
    public void Analyze_AcceptsReducibleSingleEntryCycle()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static int Run(bool repeat)
                {
                    if (repeat)
                    {
                        int body = 1;
                    }

                    return 0;
                }
            }
            """;
        (HirMethodDeclaration method, GraphDocument original) = Project(source, "Reducible.cs");
        HirIfStatement statement = Assert.IsType<HirIfStatement>(method.Body.Statements[0]);
        HirVariableDeclarationStatement body = Assert.IsType<HirVariableDeclarationStatement>(
            Assert.Single(statement.Then.Statements));
        HirReturnStatement fallback = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        GraphFunction function = Assert.Single(original.Functions);
        GraphEdge bodyExit = Assert.Single(
            function.Edges,
            edge => edge.Kind == GraphEdgeKind.Control &&
                edge.FromNode == body.Id &&
                edge.ToNode == fallback.Id);
        GraphDocument reducible = original with
        {
            Functions = [function with
            {
                Edges = function.Edges
                    .Select(edge => edge.Id == bodyExit.Id
                        ? edge with
                        {
                            Id = SemanticId.FromKey($"reducible|{body.Id}|{statement.Id}"),
                            ToNode = statement.Id,
                        }
                        : edge)
                    .ToArray(),
            }],
        };

        GraphControlFlowFunction flow = Assert.Single(new GraphControlFlowAnalyzer()
            .AnalyzeAndValidate(reducible)
            .Functions);

        Assert.True(flow.IsReducible);
        Assert.True(flow.HasCompleteExitCoverage);
        GraphControlFlowComponent component = Assert.Single(flow.CyclicComponents);
        Assert.Equal([statement.Id], component.EntryNodeIds);
        Assert.Contains(statement.Id, component.NodeIds);
        Assert.Contains(body.Id, component.NodeIds);
        Assert.True(component.IsReducible);
        Assert.Contains(statement.Id, Node(flow, body.Id).DominatorIds);
    }

    [Fact]
    public void Analyze_IsDeterministicForNestedBranches()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static int Run(bool outer, bool inner)
                {
                    if (outer)
                    {
                        if (inner)
                        {
                            return 1;
                        }
                    }

                    return 0;
                }
            }
            """;
        (_, GraphDocument graph) = Project(source, "NestedControlFlow.cs");
        var analyzer = new GraphControlFlowAnalyzer();

        GraphControlFlowAnalysis first = analyzer.AnalyzeAndValidate(graph);
        GraphControlFlowAnalysis second = analyzer.AnalyzeAndValidate(graph);
        string firstJson = JsonSerializer.Serialize(first, UnifiedJson.CreateOptions());
        string secondJson = JsonSerializer.Serialize(second, UnifiedJson.CreateOptions());

        Assert.Equal(firstJson, secondJson);
        GraphControlFlowFunction flow = Assert.Single(first.Functions);
        Assert.Equal(2, flow.Branches.Count);
        Assert.All(flow.Branches, static branch => Assert.Null(branch.MergeNodeId));
        Assert.Contains(flow.Branches, static branch => branch.ThenExitsFunction);
        Assert.All(flow.Branches, static branch => Assert.False(branch.ElseExitsFunction));
    }

    [Fact]
    public void Analyze_ClassifiesStructuredWhileHeaderBackEdgesAndTransfers()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static int Run(int value)
                {
                    while (value > 0)
                    {
                        if (value == 3)
                        {
                            value = value - 1;
                            continue;
                        }

                        if (value == 1)
                        {
                            break;
                        }

                        value = value - 1;
                    }

                    return value;
                }
            }
            """;
        (HirMethodDeclaration method, GraphDocument graph) = Project(source, "StructuredWhile.cs");
        HirWhileStatement statement = Assert.IsType<HirWhileStatement>(method.Body.Statements[0]);
        HirBreakStatement breakStatement = Assert.Single(
            statement.Body.DescendantsAndSelf().OfType<HirBreakStatement>());
        HirContinueStatement continueStatement = Assert.Single(
            statement.Body.DescendantsAndSelf().OfType<HirContinueStatement>());
        HirReturnStatement continuation = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);

        GraphControlFlowFunction flow = Assert.Single(new GraphControlFlowAnalyzer()
            .AnalyzeAndValidate(graph)
            .Functions);
        GraphControlFlowLoop loop = Assert.Single(flow.Loops);
        GraphControlFlowComponent component = Assert.Single(flow.CyclicComponents);

        Assert.Equal(statement.Id, loop.LoopNodeId);
        Assert.Equal(statement.Id, loop.HeaderEntryNodeId);
        Assert.Equal(continuation.Id, loop.ExitNodeId);
        Assert.Equal([breakStatement.Id], loop.BreakNodeIds);
        Assert.Equal([continueStatement.Id], loop.ContinueNodeIds);
        Assert.True(loop.BackEdgeArcIds.Count >= 2);
        Assert.Equal([statement.Id], component.EntryNodeIds);
        Assert.True(component.IsReducible);
        Assert.True(flow.IsReducible);
        Assert.Contains(statement.Id, Node(flow, continueStatement.Id).SuccessorIds);
        Assert.Contains(continuation.Id, Node(flow, breakStatement.Id).SuccessorIds);
    }

    [Fact]
    public void Analyze_UsesEffectfulConditionEntryAsContinueAndBackEdgeTarget()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run()
                {
                    while (ShouldContinue())
                    {
                        continue;
                    }

                    return 0;
                }

                static bool ShouldContinue() => false;
            }
            """;
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(source, "EffectfulWhile.cs");
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        HirWhileStatement statement = Assert.IsType<HirWhileStatement>(Assert.Single(
            Assert.Single(Assert.Single(analysis.Hir!.Types).Methods).Body.Statements,
            static candidate => candidate is HirWhileStatement));
        HirInvocationExpression condition = Assert.IsType<HirInvocationExpression>(statement.Condition);
        HirContinueStatement continueStatement = Assert.IsType<HirContinueStatement>(
            Assert.Single(statement.Body.Statements));
        GraphDocument graph = new HirGraphProjector().Project(analysis.Hir, analysis.Semantics);

        GraphControlFlowFunction flow = Assert.Single(new GraphControlFlowAnalyzer()
            .AnalyzeAndValidate(graph)
            .Functions);
        GraphControlFlowLoop loop = Assert.Single(flow.Loops);

        Assert.Equal(condition.Id, loop.HeaderEntryNodeId);
        Assert.Contains(condition.Id, Node(flow, continueStatement.Id).SuccessorIds);
        Assert.Contains(
            loop.BackEdgeArcIds,
            arcId => flow.Arcs.Single(arc => arc.Id == arcId).FromNodeId == continueStatement.Id);
        Assert.Equal([condition.Id], Assert.Single(flow.CyclicComponents).EntryNodeIds);
    }

    [Fact]
    public void Analyze_PreservesDoWhileInitialBodyEntryAndConditionContinueTarget()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static int Run(int value)
                {
                    do
                    {
                        value = value - 1;
                        if (value == 2)
                        {
                            continue;
                        }

                        if (value == 1)
                        {
                            break;
                        }
                    }
                    while (value > 0);

                    return value;
                }
            }
            """;
        (HirMethodDeclaration method, GraphDocument graph) = Project(source, "DoWhileFlow.cs");
        HirDoWhileStatement statement = Assert.IsType<HirDoWhileStatement>(method.Body.Statements[0]);
        HirExpressionStatement firstBodyStatement = Assert.IsType<HirExpressionStatement>(
            statement.Body.Statements[0]);
        HirAssignmentExpression firstBodyControl = Assert.IsType<HirAssignmentExpression>(
            firstBodyStatement.Expression);
        HirContinueStatement continueStatement = Assert.Single(
            statement.Body.DescendantsAndSelf().OfType<HirContinueStatement>());
        HirBreakStatement breakStatement = Assert.Single(
            statement.Body.DescendantsAndSelf().OfType<HirBreakStatement>());
        HirReturnStatement continuation = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        GraphFunction projected = Assert.Single(graph.Functions);
        GraphNode loopNode = projected.Nodes.Single(node => node.Id == statement.Id);
        GraphNode initialEntry = Assert.Single(projected.Nodes, static node => node.Kind == "loopEntry");

        Assert.Equal("doWhile", loopNode.Properties["loopForm"]);
        Assert.Equal("afterBody", loopNode.Properties["conditionEvaluation"]);
        Assert.Equal("body,condition", loopNode.Properties["evaluationOrder"]);
        Assert.Equal("condition", loopNode.Properties["continueTarget"]);
        Assert.Contains(projected.Edges, edge =>
            edge.Kind == GraphEdgeKind.Control &&
            edge.FromNode == initialEntry.Id && edge.FromPort == "then" &&
            edge.ToNode == firstBodyControl.Id && edge.ToPort == "execIn");

        GraphControlFlowFunction flow = Assert.Single(new GraphControlFlowAnalyzer()
            .AnalyzeAndValidate(graph)
            .Functions);
        GraphControlFlowLoop loop = Assert.Single(flow.Loops);

        Assert.Equal(firstBodyControl.Id, loop.HeaderEntryNodeId);
        Assert.Equal(statement.Id, loop.ContinueTargetNodeId);
        Assert.Equal(firstBodyControl.Id, loop.BodyEntryNodeId);
        Assert.Equal(continuation.Id, loop.ExitNodeId);
        Assert.Equal([continueStatement.Id], loop.ContinueNodeIds);
        Assert.Equal([breakStatement.Id], loop.BreakNodeIds);
        Assert.Contains(statement.Id, Node(flow, continueStatement.Id).SuccessorIds);
        Assert.Contains(continuation.Id, Node(flow, breakStatement.Id).SuccessorIds);
        Assert.NotEmpty(loop.BackEdgeArcIds);
        Assert.True(flow.IsReducible);
    }

    [Fact]
    public void Analyze_UsesEffectfulDoWhileConditionAsContinueTargetAfterBody()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                static bool Check(int value) => value > 0;

                [Visual]
                static int Run(int value)
                {
                    do
                    {
                        value = value - 1;
                        if (value == 2)
                        {
                            continue;
                        }
                    }
                    while (Check(value));

                    return value;
                }
            }
            """;
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(
            source,
            "EffectfulDoWhile.cs");
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(analysis.Hir!.Types).Methods);
        GraphDocument graph = new HirGraphProjector().Project(analysis.Hir, analysis.Semantics);
        HirDoWhileStatement statement = Assert.IsType<HirDoWhileStatement>(method.Body.Statements[0]);
        HirInvocationExpression condition = Assert.IsType<HirInvocationExpression>(statement.Condition);
        HirAssignmentExpression bodyEntry = Assert.IsType<HirAssignmentExpression>(
            Assert.IsType<HirExpressionStatement>(statement.Body.Statements[0]).Expression);
        HirContinueStatement continueStatement = Assert.Single(
            statement.Body.DescendantsAndSelf().OfType<HirContinueStatement>());

        GraphControlFlowFunction flow = Assert.Single(new GraphControlFlowAnalyzer()
            .AnalyzeAndValidate(graph)
            .Functions);
        GraphControlFlowLoop loop = Assert.Single(flow.Loops);

        Assert.Equal(bodyEntry.Id, loop.HeaderEntryNodeId);
        Assert.Equal(condition.Id, loop.ContinueTargetNodeId);
        Assert.Contains(condition.Id, Node(flow, continueStatement.Id).SuccessorIds);
        Assert.NotEmpty(loop.BackEdgeArcIds);
        Assert.True(flow.IsReducible);
    }

    [Fact]
    public void Analyze_ForEachCapturesCollectionOnceAndRoutesTransfersToIndexedHeader()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Sum(int[] values)
                {
                    int total = 0;
                    foreach (int value in values)
                    {
                        if (value == 0)
                        {
                            continue;
                        }

                        total = total + value;
                        if (total > 10)
                        {
                            break;
                        }
                    }

                    return total;
                }
            }
            """;
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(
            source,
            "ForEachFlow.cs");
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        HirForEachStatement statement = Assert.Single(
            analysis.Hir!.DescendantsAndSelf().OfType<HirForEachStatement>());
        HirContinueStatement next = Assert.Single(
            statement.Body.DescendantsAndSelf().OfType<HirContinueStatement>());
        HirBreakStatement stop = Assert.Single(
            statement.Body.DescendantsAndSelf().OfType<HirBreakStatement>());
        HirReturnStatement continuation = Assert.IsType<HirReturnStatement>(
            Assert.Single(Assert.Single(analysis.Hir!.Types).Methods).Body.Statements[2]);
        GraphDocument graph = new HirGraphProjector().Project(analysis.Hir!, analysis.Semantics);
        GraphFunction projected = Assert.Single(graph.Functions);
        GraphNode capture = Assert.Single(projected.Nodes, static node =>
            node.Kind == "collectionCapture");

        GraphControlFlowFunction flow = Assert.Single(new GraphControlFlowAnalyzer()
            .AnalyzeAndValidate(graph)
            .Functions);
        GraphControlFlowLoop loop = Assert.Single(flow.Loops);

        Assert.Equal(statement.Id, loop.HeaderEntryNodeId);
        Assert.Equal(statement.Id, loop.ContinueTargetNodeId);
        Assert.Equal(statement.Variable.Id, loop.BodyEntryNodeId);
        Assert.NotNull(loop.CollectionRegionId);
        Assert.Equal(continuation.Id, loop.ExitNodeId);
        Assert.Equal([next.Id], loop.ContinueNodeIds);
        Assert.Equal([stop.Id], loop.BreakNodeIds);
        Assert.Equal(statement.Id, Node(flow, capture.Id).SuccessorIds.Single());
        Assert.Equal(statement.Id, Node(flow, next.Id).SuccessorIds.Single());
        Assert.Equal(continuation.Id, Node(flow, stop.Id).SuccessorIds.Single());
        Assert.NotEmpty(loop.BackEdgeArcIds);
        Assert.True(flow.IsReducible);
    }

    [Fact]
    public void Analyze_ForExecutesInitializerOnceAndRoutesContinueThroughIncrementors()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static void Run()
                {
                    for (Reset(); Check(); Advance())
                    {
                        continue;
                    }
                }

                static void Reset() { }

                static bool Check() => false;

                static void Advance() { }
            }
            """;
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(
            source,
            "EffectfulFor.cs");
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        HirForStatement statement = Assert.Single(
            analysis.Hir!.DescendantsAndSelf().OfType<HirForStatement>());
        HirInvocationExpression reset = Assert.IsType<HirInvocationExpression>(
            Assert.Single(statement.Initializers));
        HirInvocationExpression check = Assert.IsType<HirInvocationExpression>(statement.Condition);
        HirInvocationExpression advance = Assert.IsType<HirInvocationExpression>(
            Assert.Single(statement.Incrementors));
        HirContinueStatement nextIteration = Assert.IsType<HirContinueStatement>(
            Assert.Single(statement.Body.Statements));
        GraphDocument graph = new HirGraphProjector().Project(analysis.Hir!, analysis.Semantics!);

        GraphControlFlowFunction flow = Assert.Single(new GraphControlFlowAnalyzer()
            .AnalyzeAndValidate(graph)
            .Functions);
        GraphControlFlowLoop loop = Assert.Single(flow.Loops);

        Assert.Equal(check.Id, loop.HeaderEntryNodeId);
        Assert.Equal(advance.Id, loop.ContinueTargetNodeId);
        Assert.Equal(advance.Id, Node(flow, nextIteration.Id).SuccessorIds.Single());
        Assert.Equal(check.Id, Node(flow, reset.Id).SuccessorIds.Single());
        Assert.Equal(check.Id, Node(flow, advance.Id).SuccessorIds.Single());
        Assert.Contains(
            loop.BackEdgeArcIds,
            arcId => flow.Arcs.Single(arc => arc.Id == arcId).FromNodeId == advance.Id);
        Assert.DoesNotContain(
            loop.BackEdgeArcIds,
            arcId => flow.Arcs.Single(arc => arc.Id == arcId).FromNodeId == reset.Id);
        Assert.Equal([check.Id], Assert.Single(flow.CyclicComponents).EntryNodeIds);
    }

    [Fact]
    public void Analyze_ForWithOmittedClausesUsesImplicitTrueHeaderAndExplicitPhaseRegions()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static int Run()
                {
                    for (;;)
                    {
                        break;
                    }

                    return 1;
                }
            }
            """;
        (HirMethodDeclaration method, GraphDocument graph) = Project(source, "InfiniteFor.cs");
        HirForStatement statement = Assert.IsType<HirForStatement>(method.Body.Statements[0]);
        GraphFunction function = Assert.Single(graph.Functions);
        GraphNode loopNode = Assert.Single(function.Nodes, node => node.Id == statement.Id);
        GraphRegion loopRegion = Assert.Single(function.Regions, region =>
            region.Kind == GraphRegionKind.Loop && region.SourceNodeId == statement.Id);
        GraphRegion initializerRegion = function.Regions.Single(region =>
            region.ParentRegionId == loopRegion.Id &&
            region.Kind == GraphRegionKind.LoopInitializer);
        GraphRegion incrementRegion = function.Regions.Single(region =>
            region.ParentRegionId == loopRegion.Id &&
            region.Kind == GraphRegionKind.LoopIncrement);

        Assert.Null(statement.Declaration);
        Assert.Empty(statement.Initializers);
        Assert.Null(statement.Condition);
        Assert.Empty(statement.Incrementors);
        Assert.Equal("implicitTrue", loopNode.Properties["conditionEvaluation"]);
        Assert.DoesNotContain(loopNode.Ports, static port => port.Name == "condition");
        Assert.True(initializerRegion.IsImplicit);
        Assert.True(incrementRegion.IsImplicit);

        GraphControlFlowLoop flow = Assert.Single(
            Assert.Single(new GraphControlFlowAnalyzer().AnalyzeAndValidate(graph).Functions).Loops);
        Assert.Equal(statement.Id, flow.HeaderEntryNodeId);
        Assert.Equal(statement.Id, flow.ContinueTargetNodeId);
        Assert.Empty(flow.BackEdgeArcIds);
    }

    [Fact]
    public void Analyze_BindsNestedBreakAndContinueToNearestLoop()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static int Run(int outer, int inner)
                {
                    while (outer > 0)
                    {
                        while (inner > 0)
                        {
                            if (inner == 2)
                            {
                                continue;
                            }

                            break;
                        }

                        break;
                    }

                    return 0;
                }
            }
            """;
        (HirMethodDeclaration method, GraphDocument graph) = Project(source, "NestedWhile.cs");
        HirWhileStatement outer = Assert.IsType<HirWhileStatement>(method.Body.Statements[0]);
        HirWhileStatement inner = Assert.IsType<HirWhileStatement>(outer.Body.Statements[0]);
        HirBreakStatement outerBreak = Assert.IsType<HirBreakStatement>(outer.Body.Statements[1]);
        HirBreakStatement innerBreak = Assert.Single(inner.Body.DescendantsAndSelf().OfType<HirBreakStatement>());
        HirContinueStatement innerContinue = Assert.Single(
            inner.Body.DescendantsAndSelf().OfType<HirContinueStatement>());

        GraphControlFlowFunction flow = Assert.Single(new GraphControlFlowAnalyzer()
            .AnalyzeAndValidate(graph)
            .Functions);
        GraphControlFlowLoop outerFlow = Assert.Single(flow.Loops, loop => loop.LoopNodeId == outer.Id);
        GraphControlFlowLoop innerFlow = Assert.Single(flow.Loops, loop => loop.LoopNodeId == inner.Id);

        Assert.Equal([outerBreak.Id], outerFlow.BreakNodeIds);
        Assert.Empty(outerFlow.ContinueNodeIds);
        Assert.Equal([innerBreak.Id], innerFlow.BreakNodeIds);
        Assert.Equal([innerContinue.Id], innerFlow.ContinueNodeIds);
        Assert.Equal(inner.Id, Node(flow, innerContinue.Id).SuccessorIds.Single());
        Assert.Equal(outerBreak.Id, Node(flow, innerBreak.Id).SuccessorIds.Single());
    }

    [Fact]
    public void Analyze_RepresentsEmptyLoopBodyAsReducibleSelfBackEdge()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static int Run(bool ready)
                {
                    while (ready)
                    {
                    }

                    return 0;
                }
            }
            """;
        (HirMethodDeclaration method, GraphDocument graph) = Project(source, "EmptyWhile.cs");
        HirWhileStatement statement = Assert.IsType<HirWhileStatement>(method.Body.Statements[0]);

        GraphControlFlowFunction flow = Assert.Single(new GraphControlFlowAnalyzer()
            .AnalyzeAndValidate(graph)
            .Functions);
        GraphControlFlowLoop loop = Assert.Single(flow.Loops);
        GraphControlFlowArc backEdge = Assert.Single(
            loop.BackEdgeArcIds.Select(id => flow.Arcs.Single(arc => arc.Id == id)));

        Assert.Equal(statement.Id, loop.HeaderEntryNodeId);
        Assert.Equal(statement.Id, loop.BodyEntryNodeId);
        Assert.Equal(statement.Id, backEdge.FromNodeId);
        Assert.Equal(statement.Id, backEdge.ToNodeId);
        Assert.Equal([statement.Id], Assert.Single(flow.CyclicComponents).NodeIds);
        Assert.True(flow.IsReducible);
    }

    [Fact]
    public void Analyze_RejectsContinueThatDoesNotTargetNearestLoopHeader()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static int Run(bool ready)
                {
                    while (ready)
                    {
                        continue;
                    }

                    return 0;
                }
            }
            """;
        (HirMethodDeclaration method, GraphDocument graph) = Project(source, "InvalidContinueTarget.cs");
        HirWhileStatement statement = Assert.IsType<HirWhileStatement>(method.Body.Statements[0]);
        HirContinueStatement continueStatement = Assert.IsType<HirContinueStatement>(
            Assert.Single(statement.Body.Statements));
        HirReturnStatement continuation = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        GraphFunction function = Assert.Single(graph.Functions);
        GraphEdge continueEdge = Assert.Single(
            function.Edges,
            edge => edge.Kind == GraphEdgeKind.Control && edge.FromNode == continueStatement.Id);
        GraphEdge invalidEdge = continueEdge with
        {
            Id = SemanticId.FromKey($"invalid-continue|{continueStatement.Id}|{continuation.Id}"),
            ToNode = continuation.Id,
        };
        GraphDocument invalid = graph with
        {
            Functions = [function with
            {
                Edges = function.Edges
                    .Select(edge => edge.Id == continueEdge.Id ? invalidEdge : edge)
                    .ToArray(),
            }],
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(invalid));

        Assert.Contains("nearest loop", exception.Message, StringComparison.Ordinal);
    }

    private static GraphControlFlowNode Node(GraphControlFlowFunction function, SemanticId id) =>
        Assert.Single(function.Nodes, node => node.Id == id);

    private static (HirMethodDeclaration Method, GraphDocument Graph) Project(
        string source,
        string path)
    {
        CSharpLoweringResult lowering = new CSharpToHirLowerer().Lower(source, path);
        Assert.True(lowering.Succeeded, string.Join(Environment.NewLine, lowering.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(lowering.CompilationUnit!.Types).Methods);
        return (method, new HirGraphProjector().Project(lowering.CompilationUnit));
    }
}
