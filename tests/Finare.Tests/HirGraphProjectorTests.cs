using Finare.CSharp;
using Finare.Core;
using Finare.Core.Hir;
using Finare.Graph;

namespace Finare.Tests;

[Trait("Category", "Graph")]
public sealed class HirGraphProjectorTests
{
    private const string Source = """
        namespace Game;

        class DoorController
        {
            [Visual]
            void BeginPlay()
            {
                bool ready = Target != null;
                if (ready)
                {
                    Target.Open();
                }
            }
        }
        """;

    [Fact]
    public void Project_ProducesControlAndDataGraph()
    {
        var lowering = new CSharpToHirLowerer().Lower(Source, "DoorController.cs");

        GraphDocument graph = new HirGraphProjector().Project(lowering.CompilationUnit!);
        GraphFunction function = Assert.Single(graph.Functions);

        Assert.Contains(function.Nodes, static node => node.Kind == "entry");
        Assert.Contains(function.Nodes, static node => node.Kind == "branch");
        Assert.Contains(function.Nodes, static node => node.Kind == "invocation");
        Assert.Contains(function.Edges, static edge => edge.Kind == GraphEdgeKind.Control);
        Assert.Contains(function.Edges, static edge => edge.Kind == GraphEdgeKind.Data);
    }

    [Fact]
    public void Project_AssignsEveryNodeToNestedFunctionBlockAndBranchRegions()
    {
        var lowering = new CSharpToHirLowerer().Lower(Source, "DoorController.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(lowering.CompilationUnit!.Types).Methods);
        HirIfStatement statement = Assert.IsType<HirIfStatement>(method.Body.Statements[1]);

        GraphFunction function = Assert.Single(new HirGraphProjector()
            .Project(lowering.CompilationUnit)
            .Functions);
        IReadOnlyDictionary<SemanticId, GraphRegion> regions = function.Regions
            .ToDictionary(static region => region.Id);
        GraphRegion root = regions[function.RootRegionId];
        GraphRegion body = Assert.Single(
            root.ChildRegionIds.Select(id => regions[id]),
            static region => region.Kind == GraphRegionKind.Block);
        GraphRegion ifRegion = Assert.Single(
            body.ChildRegionIds.Select(id => regions[id]),
            static region => region.Kind == GraphRegionKind.If);
        GraphRegion thenRegion = regions[ifRegion.ChildRegionIds[0]];
        GraphRegion elseRegion = regions[ifRegion.ChildRegionIds[1]];

        Assert.Equal(GraphRegionKind.Function, root.Kind);
        Assert.Equal(method.Id, root.SourceNodeId);
        Assert.Equal(method.Body.Id, body.SourceNodeId);
        Assert.Equal(statement.Id, ifRegion.SourceNodeId);
        Assert.Equal(GraphRegionKind.Then, thenRegion.Kind);
        Assert.Equal(statement.Then.Id, thenRegion.SourceNodeId);
        Assert.False(thenRegion.IsImplicit);
        Assert.Equal(GraphRegionKind.Else, elseRegion.Kind);
        Assert.Equal(statement.Id, elseRegion.SourceNodeId);
        Assert.True(elseRegion.IsImplicit);
        Assert.Empty(elseRegion.NodeIds);
        Assert.Contains(statement.Id, ifRegion.NodeIds);
        Assert.Contains(
            thenRegion.NodeIds.Select(id => Assert.Single(function.Nodes, node => node.Id == id)),
            static node => node.Kind == "invocation");
        Assert.Equal(
            function.Nodes.Select(static node => node.Id).OrderBy(static id => id.Value),
            function.Regions
                .SelectMany(static region => region.NodeIds)
                .OrderBy(static id => id.Value));
    }

    [Fact]
    public void Project_RepresentsExplicitElseAndEarlyReturnsAsSiblingRegions()
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
        var lowering = new CSharpToHirLowerer().Lower(source, "Rules.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(lowering.CompilationUnit!.Types).Methods);
        HirIfStatement statement = Assert.IsType<HirIfStatement>(Assert.Single(method.Body.Statements));

        GraphFunction function = Assert.Single(new HirGraphProjector()
            .Project(lowering.CompilationUnit)
            .Functions);
        GraphRegion ifRegion = Assert.Single(
            function.Regions,
            static region => region.Kind == GraphRegionKind.If);
        GraphRegion thenRegion = Assert.Single(
            function.Regions,
            static region => region.Kind == GraphRegionKind.Then);
        GraphRegion elseRegion = Assert.Single(
            function.Regions,
            static region => region.Kind == GraphRegionKind.Else);
        HirReturnStatement thenReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(statement.Then.Statements));
        HirReturnStatement elseReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(statement.Else!.Statements));

        Assert.Equal([thenRegion.Id, elseRegion.Id], ifRegion.ChildRegionIds);
        Assert.False(elseRegion.IsImplicit);
        Assert.Equal(statement.Else.Id, elseRegion.SourceNodeId);
        Assert.Contains(thenReturn.Id, thenRegion.NodeIds);
        Assert.Contains(elseReturn.Id, elseRegion.NodeIds);
        Assert.Contains(
            function.Edges,
            edge => edge.Kind == GraphEdgeKind.Control &&
                edge.FromNode == statement.Id &&
                edge.FromPort == "then" &&
                edge.ToNode == thenReturn.Id);
        Assert.Contains(
            function.Edges,
            edge => edge.Kind == GraphEdgeKind.Control &&
                edge.FromNode == statement.Id &&
                edge.FromPort == "else" &&
                edge.ToNode == elseReturn.Id);
    }

    [Fact]
    public void Project_CreatesAChildBlockRegionForNestedLexicalBlock()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static int Run()
                {
                    {
                        int value = 1;
                    }

                    return 2;
                }
            }
            """;
        var lowering = new CSharpToHirLowerer().Lower(source, "NestedBlock.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(lowering.CompilationUnit!.Types).Methods);
        HirBlockStatement nestedBlock = Assert.IsType<HirBlockStatement>(method.Body.Statements[0]);
        HirReturnStatement fallback = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);

        GraphFunction function = Assert.Single(new HirGraphProjector()
            .Project(lowering.CompilationUnit)
            .Functions);
        GraphRegion body = Assert.Single(
            function.Regions,
            region => region.Kind == GraphRegionKind.Block && region.SourceNodeId == method.Body.Id);
        GraphRegion nested = Assert.Single(
            function.Regions,
            region => region.Kind == GraphRegionKind.Block && region.SourceNodeId == nestedBlock.Id);

        Assert.Equal(body.Id, nested.ParentRegionId);
        Assert.Contains(nested.Id, body.ChildRegionIds);
        Assert.Contains(fallback.Id, body.NodeIds);
        Assert.DoesNotContain(fallback.Id, nested.NodeIds);
    }

    [Fact]
    public void Serialize_IsDeterministic()
    {
        var lowering = new CSharpToHirLowerer().Lower(Source, "DoorController.cs");
        var projector = new HirGraphProjector();

        string first = GraphJson.Serialize(projector.Project(lowering.CompilationUnit!));
        string second = GraphJson.Serialize(projector.Project(lowering.CompilationUnit!));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Project_RepresentsLoopBodyBreakContinueAndBackEdges()
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
        var lowering = new CSharpToHirLowerer().Lower(source, "LoopProjection.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(lowering.CompilationUnit!.Types).Methods);
        HirWhileStatement loop = Assert.IsType<HirWhileStatement>(method.Body.Statements[0]);
        HirBreakStatement breakStatement = Assert.Single(
            loop.Body.DescendantsAndSelf().OfType<HirBreakStatement>());
        HirContinueStatement continueStatement = Assert.Single(
            loop.Body.DescendantsAndSelf().OfType<HirContinueStatement>());
        HirReturnStatement continuation = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);

        GraphFunction function = Assert.Single(new HirGraphProjector()
            .Project(lowering.CompilationUnit)
            .Functions);
        GraphRegion loopRegion = Assert.Single(
            function.Regions,
            region => region.Kind == GraphRegionKind.Loop && region.SourceNodeId == loop.Id);
        GraphRegion bodyRegion = Assert.Single(
            function.Regions,
            region => region.Kind == GraphRegionKind.LoopBody && region.SourceNodeId == loop.Body.Id);

        Assert.Equal(loopRegion.Id, bodyRegion.ParentRegionId);
        Assert.Equal([bodyRegion.Id], loopRegion.ChildRegionIds);
        Assert.Contains(loop.Id, loopRegion.NodeIds);
        Assert.Contains(function.Nodes, node => node.Id == breakStatement.Id && node.Kind == "break");
        Assert.Contains(
            function.Edges,
            edge => edge.Kind == GraphEdgeKind.Control &&
                edge.FromNode == continueStatement.Id && edge.ToNode == loop.Id);
        Assert.Contains(
            function.Edges,
            edge => edge.Kind == GraphEdgeKind.Control &&
                edge.FromNode == breakStatement.Id && edge.ToNode == continuation.Id);
        Assert.Contains(
            function.Edges,
            edge => edge.Kind == GraphEdgeKind.Control &&
                edge.FromNode == loop.Id && edge.FromPort == "exit" &&
                edge.ToNode == continuation.Id);
    }
}
