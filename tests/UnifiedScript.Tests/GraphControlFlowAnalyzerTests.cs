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
