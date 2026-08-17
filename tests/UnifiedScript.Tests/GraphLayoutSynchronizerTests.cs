using UnifiedScript.CSharp;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Graph;

namespace UnifiedScript.Tests;

public sealed class GraphLayoutSynchronizerTests
{
    [Fact]
    public void Create_MarksEveryProjectedNodeExplicitlyUnplaced()
    {
        GraphDocument graph = CreateGraph(
            SemanticId.FromKey("create-layout-function"),
            [SemanticId.FromKey("create-layout-b"), SemanticId.FromKey("create-layout-a")]);

        GraphLayoutDocument layout = new GraphLayoutSynchronizer().Create(graph);

        GraphFunctionLayout function = Assert.Single(layout.Functions);
        Assert.Equal(
            graph.Functions[0].Nodes.Select(static node => node.Id).OrderBy(static id => id.Value),
            function.Nodes.Select(static node => node.NodeId));
        Assert.All(function.Nodes, static node =>
        {
            Assert.Equal(GraphNodePlacement.Unplaced, node.Placement);
            Assert.Null(node.Position);
            Assert.Null(node.Size);
            Assert.False(node.Collapsed);
        });
        Assert.Empty(layout.OrphanedNodes);
    }

    [Fact]
    public void Synchronize_PreservesSurvivorsAddsUnplacedNodesAndOrphansRemovedNodes()
    {
        SemanticId functionId = SemanticId.FromKey("sync-layout-function");
        SemanticId survivorId = SemanticId.FromKey("sync-layout-survivor");
        SemanticId removedId = SemanticId.FromKey("sync-layout-removed");
        SemanticId addedId = SemanticId.FromKey("sync-layout-added");
        SemanticId survivingEdgeId = SemanticId.FromKey("sync-layout-edge");
        SemanticId removedEdgeId = SemanticId.FromKey("sync-layout-removed-edge");
        GraphNodeLayout survivor = Placed(survivorId, 25, 50, collapsed: true);
        GraphNodeLayout removed = Placed(removedId, 400, 50);
        var existing = new GraphLayoutDocument(
            GraphLayoutDocument.CurrentSchemaVersion,
            [new GraphFunctionLayout(
                functionId,
                [survivor, removed],
                [new GraphCommentRegion(
                    "region",
                    "Retained region",
                    new GraphLayoutBounds(0, 0, 800, 400),
                    [survivorId, removedId],
                    false)],
                [
                    new GraphRoutingHint(survivingEdgeId, [new GraphLayoutPoint(100, 100)]),
                    new GraphRoutingHint(removedEdgeId, [new GraphLayoutPoint(200, 200)]),
                ])],
            []);
        GraphDocument updatedGraph = CreateGraph(
            functionId,
            [survivorId, addedId],
            [survivingEdgeId]);

        GraphLayoutDocument synchronized = new GraphLayoutSynchronizer().Synchronize(
            updatedGraph,
            existing);

        GraphFunctionLayout function = Assert.Single(synchronized.Functions);
        Assert.Equal(survivor, Assert.Single(function.Nodes, node => node.NodeId == survivorId));
        GraphNodeLayout added = Assert.Single(function.Nodes, node => node.NodeId == addedId);
        Assert.Equal(GraphNodePlacement.Unplaced, added.Placement);
        Assert.Null(added.Position);
        Assert.Null(added.Size);
        GraphCommentRegion comment = Assert.Single(function.CommentRegions);
        Assert.Equal([survivorId], comment.NodeIds);
        Assert.Equal(survivingEdgeId, Assert.Single(function.RoutingHints).EdgeId);
        GraphLayoutOrphan orphan = Assert.Single(synchronized.OrphanedNodes);
        Assert.Equal(removed, orphan.Node);
        Assert.Equal(GraphLayoutOrphanReason.MissingNode, orphan.Reason);
    }

    [Fact]
    public void Synchronize_RestoresAnOrphanWhenItsSemanticIdReappears()
    {
        SemanticId functionId = SemanticId.FromKey("restore-layout-function");
        SemanticId nodeId = SemanticId.FromKey("restore-layout-node");
        GraphDocument graph = CreateGraph(functionId, [nodeId]);
        GraphNodeLayout placed = Placed(nodeId, -300, 125, collapsed: true);
        var layout = new GraphLayoutDocument(
            GraphLayoutDocument.CurrentSchemaVersion,
            [new GraphFunctionLayout(functionId, [placed], [], [])],
            []);
        var synchronizer = new GraphLayoutSynchronizer();

        GraphLayoutDocument missing = synchronizer.Synchronize(
            CreateGraph(SemanticId.FromKey("another-function"), []),
            layout);
        GraphLayoutOrphan orphan = Assert.Single(missing.OrphanedNodes);
        Assert.Equal(GraphLayoutOrphanReason.MissingFunction, orphan.Reason);

        GraphLayoutDocument functionOnly = synchronizer.Synchronize(
            CreateGraph(functionId, []),
            missing);
        Assert.Equal(
            GraphLayoutOrphanReason.MissingNode,
            Assert.Single(functionOnly.OrphanedNodes).Reason);

        GraphLayoutDocument restored = synchronizer.Synchronize(graph, functionOnly);

        Assert.Equal(placed, Assert.Single(Assert.Single(restored.Functions).Nodes));
        Assert.Empty(restored.OrphanedNodes);
    }

    [Fact]
    public void SemanticInsertion_PreservesExistingLayoutAndMarksNewGraphNodesUnplaced()
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
            "Game/LayoutPatch.cs");
        HirCompilationUnit originalHir = original.Hir!;
        GraphDocument originalGraph = new HirGraphProjector().Project(
            originalHir,
            original.Semantics);
        GraphFunction originalFunction = Assert.Single(originalGraph.Functions);
        GraphNodeLayout[] placedNodes = originalFunction.Nodes
            .Select((node, index) => Placed(node.Id, index * 250, index * 100))
            .ToArray();
        var existingLayout = new GraphLayoutDocument(
            GraphLayoutDocument.CurrentSchemaVersion,
            [new GraphFunctionLayout(originalFunction.Id, placedNodes, [], [])],
            []);
        HirMethodDeclaration method = Assert.Single(Assert.Single(originalHir.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId floatType = Assert.Single(
            original.Semantics!.Nodes,
            candidate => candidate.NodeId == returnStatement.Expression!.Id).TypeId!.Value;
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            originalHir.Id,
            original.Semantics.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                returnStatement.Id,
                new LocalLiteralDeclarationPatchStatement("scale", "float", "2.0f", floatType))]);

        CSharpPatchResult patchResult = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(patchResult.Succeeded, string.Join(Environment.NewLine, patchResult.Diagnostics));
        IdentitySidecarDocument patchedIdentity = Assert.Single(patchResult.Analysis!.IdentitySidecars);
        string identityBeforeLayoutSync = IdentitySidecarJson.Serialize(patchedIdentity);
        GraphDocument patchedGraph = new HirGraphProjector().Project(
            patchResult.Analysis.Hir!,
            patchResult.Analysis.Semantics);
        GraphLayoutDocument synchronized = new GraphLayoutSynchronizer().Synchronize(
            patchedGraph,
            existingLayout);

        GraphFunctionLayout function = Assert.Single(synchronized.Functions);
        HashSet<SemanticId> originalNodeIds = originalFunction.Nodes
            .Select(static node => node.Id)
            .ToHashSet();
        Assert.All(
            function.Nodes.Where(node => originalNodeIds.Contains(node.NodeId)),
            node => Assert.Equal(
                Assert.Single(placedNodes, placed => placed.NodeId == node.NodeId),
                node));
        GraphNodeLayout[] newNodes = function.Nodes
            .Where(node => !originalNodeIds.Contains(node.NodeId))
            .ToArray();
        Assert.Equal(2, newNodes.Length);
        Assert.All(newNodes, static node => Assert.Equal(GraphNodePlacement.Unplaced, node.Placement));
        Assert.Empty(synchronized.OrphanedNodes);
        Assert.Equal(identityBeforeLayoutSync, IdentitySidecarJson.Serialize(patchedIdentity));
    }

    private static GraphNodeLayout Placed(
        SemanticId nodeId,
        double x,
        double y,
        bool collapsed = false) => new(
            nodeId,
            GraphNodePlacement.Placed,
            new GraphLayoutPoint(x, y),
            new GraphLayoutSize(220, 90),
            collapsed);

    private static GraphDocument CreateGraph(
        SemanticId functionId,
        IReadOnlyList<SemanticId> nodeIds,
        IReadOnlyList<SemanticId>? edgeIds = null)
    {
        GraphNode[] nodes = nodeIds
            .Select(nodeId => new GraphNode(
                nodeId,
                "test",
                "Test",
                SourceSpan.Unknown("Test.cs"),
                [],
                new Dictionary<string, string>()))
            .ToArray();
        GraphEdge[] edges = (edgeIds ?? [])
            .Select(edgeId => new GraphEdge(
                edgeId,
                GraphEdgeKind.Data,
                nodeIds[0],
                "value",
                nodeIds[0],
                "input"))
            .ToArray();
        SemanticId rootRegionId = SemanticId.FromKey($"{functionId}:test-region:function");
        SemanticId bodyRegionId = SemanticId.FromKey($"{functionId}:test-region:block");
        SourceSpan span = SourceSpan.Unknown("Test.cs");
        return new GraphDocument(
            GraphDocument.CurrentSchemaVersion,
            SemanticId.FromKey($"graph-source-{functionId}"),
            null,
            [new GraphFunction(
                functionId,
                "Game.Script.Run",
                nodes,
                edges,
                rootRegionId,
                [
                    new GraphRegion(
                        rootRegionId,
                        GraphRegionKind.Function,
                        null,
                        functionId,
                        span,
                        false,
                        [],
                        [bodyRegionId]),
                    new GraphRegion(
                        bodyRegionId,
                        GraphRegionKind.Block,
                        rootRegionId,
                        functionId,
                        span,
                        false,
                        nodeIds,
                        []),
                ])]);
    }

    private static (CSharpCompilationRequest Request, CSharpAnalysisResult Analysis) CreatePersistentRequest(
        string source,
        string path)
    {
        var initializingRequest = new CSharpCompilationRequest(
            "UnifiedScript.Layout.Tests",
            [new CSharpSourceDocument(path, source, IdentitySidecarDocument.CreateEmpty(path))]);
        CSharpAnalysisResult initialized = new CSharpSemanticPipeline().Analyze(initializingRequest);
        Assert.True(initialized.Succeeded, string.Join(Environment.NewLine, initialized.Diagnostics));
        var request = new CSharpCompilationRequest(
            "UnifiedScript.Layout.Tests",
            [new CSharpSourceDocument(path, source, Assert.Single(initialized.IdentitySidecars))]);
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        return (request, analysis);
    }
}
