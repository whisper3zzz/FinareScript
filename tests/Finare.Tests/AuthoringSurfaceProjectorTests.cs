using System.Text.Json;
using Finare.Authoring;
using Finare.Core;
using Finare.CSharp;
using Finare.Graph;

namespace Finare.Tests;

public sealed class AuthoringSurfaceProjectorTests
{
    private const string Source = """
        using Finare;

        namespace Tests.Authoring;

        [ScriptAsset("Tests/CombatRules")]
        public sealed class CombatRules
        {
            public int Limit { get; set; } = 100;

            [Visual]
            public int Resolve(int input)
            {
                int scaled = input * 2;
                if (scaled > Limit)
                {
                    scaled = Limit;
                }

                return scaled;
            }

            [Visual]
            public int Countdown(int value)
            {
                while (value > 0)
                {
                    value = value - 1;
                }

                return value;
            }
        }

        [ScriptAsset("Tests/InventoryRules")]
        public sealed class InventoryRules
        {
            public int Total { get; set; } = 0;

            [Visual]
            public int Accumulate(int count)
            {
                for (int index = 0; index < count; index++)
                {
                    Total = Total + index;
                }

                return Total;
            }
        }
        """;

    [Fact]
    public void ProjectBuildsGenericMultiAssetHierarchyAndEditCapabilities()
    {
        (CSharpAnalysisResult analysis, GraphDocument graph) = Analyze();

        AuthoringSurfaceDocument surface = new AuthoringSurfaceProjector().Project(
            analysis.ScriptAssets!,
            graph);

        Assert.Equal(AuthoringSurfaceDocument.CurrentSchemaVersion, surface.SchemaVersion);
        Assert.Equal(2, surface.Assets.Count);
        Assert.Equal(3, surface.Assets.Sum(static asset => asset.Functions.Count));
        Assert.Equal(
            ["Tests/CombatRules", "Tests/InventoryRules"],
            surface.Assets.Select(static asset => asset.AssetPath));

        AuthoringFunctionSurface resolve = surface.Assets
            .SelectMany(static asset => asset.Functions)
            .Single(static function => function.Name == "Resolve");
        GraphFunction resolveGraph = graph.Functions.Single(function => function.Id == resolve.Id);
        Assert.Equal(
            resolveGraph.Nodes.Select(static node => node.Id).OrderBy(static id => id.Value),
            resolve.Actions.Select(static action => action.Id).OrderBy(static id => id.Value));
        Assert.Equal(
            resolveGraph.Edges.Select(static edge => edge.Id).OrderBy(static id => id.Value),
            resolve.Edges.Select(static edge => edge.Id).OrderBy(static id => id.Value));
        Assert.All(resolve.Actions, action => Assert.Equal(
            resolveGraph.Nodes.Single(node => node.Id == action.Id).Properties.ToArray(),
            action.Properties.ToArray()));
        Assert.Contains(resolve.Regions, static region => region.Kind == GraphRegionKind.If);
        Assert.Contains(resolve.Actions, static action => action.GraphKind == "branch");
        Assert.Contains(resolve.Actions, static action =>
            action.Summary.Contains("if (", StringComparison.Ordinal));
        Assert.Contains(resolve.Actions.SelectMany(static action => action.EditCapabilities),
            static edit => edit.Kind == AuthoringEditKind.Literal &&
                edit.Operation == "replaceLiteral");
        Assert.Contains(resolve.Actions.SelectMany(static action => action.EditCapabilities),
            static edit => edit.Kind == AuthoringEditKind.Operator &&
                edit.Operation == "changeOperator" && edit.Options.Count > 1);
        Assert.Contains(resolve.Edges, static edge => edge.Kind == GraphEdgeKind.Control);
        Assert.Contains(resolve.Edges, static edge => edge.Kind == GraphEdgeKind.Data);
        Assert.All(
            resolve.Edges.Where(static edge => edge.Kind == GraphEdgeKind.Data),
            edge =>
            {
                AuthoringPortSurface output = resolve.Actions.Single(action =>
                    action.Id == edge.FromNode).Ports.Single(port =>
                        port.Name == edge.FromPort && port.Direction == GraphPortDirection.Output);
                AuthoringPortSurface input = resolve.Actions.Single(action =>
                    action.Id == edge.ToNode).Ports.Single(port =>
                        port.Name == edge.ToPort && port.Direction == GraphPortDirection.Input);
                Assert.Equal(output.TypeId, input.TypeId);
                Assert.False(string.IsNullOrWhiteSpace(output.TypeDisplayName));
            });

        SemanticId limitId = surface.Assets
            .Single(static asset => asset.AssetPath == "Tests/CombatRules")
            .States.Single(static state => state.Name == "Limit").Id;
        Assert.Contains(resolve.Actions, action => action.DeclarationReferenceIds.Contains(limitId));

        AuthoringFunctionSurface countdown = surface.Assets
            .SelectMany(static asset => asset.Functions)
            .Single(static function => function.Name == "Countdown");
        AuthoringFunctionSurface accumulate = surface.Assets
            .SelectMany(static asset => asset.Functions)
            .Single(static function => function.Name == "Accumulate");
        Assert.Contains(countdown.Actions, static action => action.Summary.StartsWith(
            "while (", StringComparison.Ordinal));
        Assert.Contains(accumulate.Actions, static action => action.Summary.StartsWith(
            "for (", StringComparison.Ordinal));
        Assert.All(surface.Assets.SelectMany(static asset => asset.Functions), AssertOwnership);
    }

    [Fact]
    public void JsonRoundTripIsDeterministicAndRejectsDanglingRegionActions()
    {
        (CSharpAnalysisResult analysis, GraphDocument graph) = Analyze();
        AuthoringSurfaceDocument surface = new AuthoringSurfaceProjector().Project(
            analysis.ScriptAssets!,
            graph);

        string json = AuthoringSurfaceJson.Serialize(surface);
        AuthoringSurfaceDocument roundTripped = AuthoringSurfaceJson.Deserialize(json);

        Assert.Equal(json, AuthoringSurfaceJson.Serialize(roundTripped));
        AuthoringAssetSurface firstAsset = roundTripped.Assets[0];
        AuthoringFunctionSurface firstFunction = firstAsset.Functions[0];
        AuthoringRegionSurface root = firstFunction.Regions.Single(region =>
            region.Id == firstFunction.RootRegionId);
        SemanticId danglingId = SemanticId.FromKey("authoring-test:dangling-action");
        AuthoringRegionSurface invalidRoot = root with
        {
            DirectActionIds = [.. root.DirectActionIds, danglingId],
        };
        AuthoringFunctionSurface invalidFunction = firstFunction with
        {
            Regions = firstFunction.Regions
                .Select(region => region.Id == root.Id ? invalidRoot : region)
                .ToArray(),
        };
        AuthoringSurfaceDocument invalid = roundTripped with
        {
            Assets = [firstAsset with
            {
                Functions = firstAsset.Functions
                    .Select(function => function.Id == firstFunction.Id ? invalidFunction : function)
                    .ToArray(),
            }, .. roundTripped.Assets.Skip(1)],
        };

        Assert.Throws<JsonException>(() => AuthoringSurfaceJson.Serialize(invalid));

        AuthoringEdgeSurface firstEdge = firstFunction.Edges[0];
        AuthoringFunctionSurface invalidEdgeFunction = firstFunction with
        {
            Edges = [firstEdge with { ToPort = "missing" }, .. firstFunction.Edges.Skip(1)],
        };
        AuthoringSurfaceDocument invalidEdge = roundTripped with
        {
            Assets = [firstAsset with
            {
                Functions = firstAsset.Functions
                    .Select(function => function.Id == firstFunction.Id
                        ? invalidEdgeFunction
                        : function)
                    .ToArray(),
            }, .. roundTripped.Assets.Skip(1)],
        };
        Assert.Throws<JsonException>(() => AuthoringSurfaceJson.Serialize(invalidEdge));

        AuthoringEdgeSurface dataEdge = firstFunction.Edges.First(
            static edge => edge.Kind == GraphEdgeKind.Data);
        AuthoringActionSurface dataTarget = firstFunction.Actions.Single(
            action => action.Id == dataEdge.ToNode);
        AuthoringActionSurface invalidDataTarget = dataTarget with
        {
            Ports = dataTarget.Ports.Select(port =>
                port.Direction == GraphPortDirection.Input && port.Name == dataEdge.ToPort
                    ? port with
                    {
                        TypeId = SemanticId.FromKey("authoring-test:mismatched-edge-type"),
                        TypeDisplayName = "Mismatch",
                    }
                    : port).ToArray(),
        };
        AuthoringFunctionSurface invalidTypeFunction = firstFunction with
        {
            Actions = firstFunction.Actions.Select(action => action.Id == dataTarget.Id
                ? invalidDataTarget
                : action).ToArray(),
        };
        AuthoringSurfaceDocument invalidEdgeType = roundTripped with
        {
            Assets = [firstAsset with
            {
                Functions = firstAsset.Functions
                    .Select(function => function.Id == firstFunction.Id
                        ? invalidTypeFunction
                        : function)
                    .ToArray(),
            }, .. roundTripped.Assets.Skip(1)],
        };
        Assert.Throws<JsonException>(() => AuthoringSurfaceJson.Serialize(invalidEdgeType));
    }

    [Fact]
    public void NameofMethodReferenceProjectsAValidTypedSurface()
    {
        const string source = """
            using Finare;
            [ScriptAsset("Tests/Names")]
            public class Names
            {
                [Visual] public string ReadName() { return nameof(Target); }
                private void Target() { }
            }
            """;
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(source, "Names.cs");
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        GraphDocument graph = new HirGraphProjector().Project(
            analysis.Hir!, analysis.Semantics!, analysis.DeclarationReferences);
        AuthoringSurfaceDocument surface = new AuthoringSurfaceProjector().Project(
            analysis.ScriptAssets!, graph);
        AuthoringActionSurface invocation = surface.Assets.Single().Functions.Single().Actions
            .Single(action => action.GraphKind == "invocation");
        Assert.All(invocation.Ports.Where(port => port.Direction == GraphPortDirection.Input), port =>
        {
            Assert.Null(port.TypeId);
            Assert.Null(port.TypeDisplayName);
        });
        Assert.Equal("string", invocation.Ports.Single(port => port.Direction == GraphPortDirection.Output).TypeDisplayName);
        Assert.Equal(AuthoringSurfaceJson.Serialize(surface),
            AuthoringSurfaceJson.Serialize(AuthoringSurfaceJson.Deserialize(AuthoringSurfaceJson.Serialize(surface))));
    }

    private static (CSharpAnalysisResult Analysis, GraphDocument Graph) Analyze()
    {
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(
            Source,
            "Tests/Authoring/MultiAssetRules.cs");
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        GraphDocument graph = new HirGraphProjector().Project(
            analysis.Hir!,
            analysis.Semantics!,
            analysis.DeclarationReferences);
        return (analysis, graph);
    }

    private static void AssertOwnership(AuthoringFunctionSurface function)
    {
        SemanticId[] ownedActionIds = function.Regions
            .SelectMany(static region => region.DirectActionIds)
            .ToArray();
        Assert.Equal(function.Actions.Count, ownedActionIds.Length);
        Assert.Equal(function.Actions.Count, ownedActionIds.Distinct().Count());
        Assert.Equal(
            function.Actions.Select(static action => action.Id).OrderBy(static id => id.Value),
            ownedActionIds.OrderBy(static id => id.Value));
        Assert.All(function.Actions, action =>
            Assert.Contains(function.Regions, region =>
                region.Id == action.RegionId && region.DirectActionIds.Contains(action.Id)));
    }
}
