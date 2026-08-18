using System.Text.Json;
using UnifiedScript.CSharp;
using UnifiedScript.Core;
using UnifiedScript.Graph;

namespace UnifiedScript.Tests;

public sealed class GraphJsonTests
{
    private const string Source = """
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

    [Fact]
    public void Serialize_NormalizesAndRoundTripsExplicitRegions()
    {
        GraphDocument graph = CreateGraph();
        GraphFunction function = Assert.Single(graph.Functions);
        GraphDocument reordered = graph with
        {
            Functions = [function with
            {
                Nodes = function.Nodes.Reverse().ToArray(),
                Edges = function.Edges.Reverse().ToArray(),
                Regions = function.Regions.Reverse().ToArray(),
            }],
        };

        string json = GraphJson.Serialize(reordered);
        GraphDocument roundTrip = GraphJson.Deserialize(json);

        Assert.Equal(GraphDocument.CurrentSchemaVersion, roundTrip.SchemaVersion);
        Assert.Equal(json, GraphJson.Serialize(roundTrip));
        Assert.Contains("\"rootRegionId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"function\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"then\"", json, StringComparison.Ordinal);
        Assert.Contains("\"isImplicit\": true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_RejectsPreRegionGraphSchema()
    {
        string json = GraphJson.Serialize(CreateGraph())
            .Replace(
                GraphDocument.CurrentSchemaVersion,
                "unifiedscript.graph/v0.2",
                StringComparison.Ordinal);

        JsonException exception = Assert.Throws<JsonException>(() => GraphJson.Deserialize(json));

        Assert.Contains("Unsupported graph schema", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_RejectsMalformedDoWhileExecutionProperties()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value)
                {
                    do
                    {
                        value = value - 1;
                    }
                    while (value > 0);

                    return value;
                }
            }
            """;
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(
            source,
            "MalformedDoWhileGraph.cs");
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        GraphDocument graph = new HirGraphProjector().Project(analysis.Hir!, analysis.Semantics!);
        GraphFunction function = Assert.Single(graph.Functions);
        GraphNode loop = Assert.Single(function.Nodes, static node =>
            node.Kind == "loop" && node.Properties.GetValueOrDefault("loopForm") == "doWhile");
        Dictionary<string, string> invalidProperties = loop.Properties.ToDictionary(
            static property => property.Key,
            static property => property.Value,
            StringComparer.Ordinal);
        invalidProperties["conditionEvaluation"] = "beforeBody";
        GraphFunction invalidFunction = function with
        {
            Nodes = function.Nodes.Select(node =>
                node.Id == loop.Id ? node with { Properties = invalidProperties } : node).ToArray(),
        };

        JsonException exception = Assert.Throws<JsonException>(() =>
            GraphJson.Serialize(graph with { Functions = [invalidFunction] }));

        Assert.Contains("valid while, do-while", exception.Message);
    }

    [Fact]
    public void Serialize_RejectsNodeWithoutExactlyOneRegionOwner()
    {
        GraphDocument graph = CreateGraph();
        GraphFunction function = Assert.Single(graph.Functions);
        SemanticId nodeId = Assert.Single(
            function.Regions,
            static region => region.Kind == GraphRegionKind.Function).NodeIds[0];
        GraphRegion owner = Assert.Single(function.Regions, region => region.NodeIds.Contains(nodeId));
        GraphFunction invalidFunction = function with
        {
            Regions = function.Regions
                .Select(region => region.Id == owner.Id
                    ? region with { NodeIds = region.NodeIds.Where(id => id != nodeId).ToArray() }
                    : region)
                .ToArray(),
        };

        JsonException exception = Assert.Throws<JsonException>(() =>
            GraphJson.Serialize(graph with { Functions = [invalidFunction] }));

        Assert.Contains("does not belong to a direct region", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_RejectsBranchChildrenOutsideThenElseOrder()
    {
        GraphDocument graph = CreateGraph();
        GraphFunction function = Assert.Single(graph.Functions);
        GraphRegion branch = Assert.Single(
            function.Regions,
            static region => region.Kind == GraphRegionKind.If);
        GraphFunction invalidFunction = function with
        {
            Regions = function.Regions
                .Select(region => region.Id == branch.Id
                    ? region with { ChildRegionIds = region.ChildRegionIds.Reverse().ToArray() }
                    : region)
                .ToArray(),
        };

        JsonException exception = Assert.Throws<JsonException>(() =>
            GraphJson.Serialize(graph with { Functions = [invalidFunction] }));

        Assert.Contains("ordered then/else children", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_RejectsForChildrenOutsideInitializerBodyIncrementOrder()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static void Run(int count)
                {
                    for (int index = 0; index < count; index++)
                    {
                    }
                }
            }
            """;
        CSharpLoweringResult lowering = new CSharpToHirLowerer().Lower(source, "ForRegions.cs");
        Assert.True(lowering.Succeeded, string.Join(Environment.NewLine, lowering.Diagnostics));
        GraphDocument graph = new HirGraphProjector().Project(lowering.CompilationUnit!);
        GraphFunction function = Assert.Single(graph.Functions);
        GraphRegion loop = Assert.Single(
            function.Regions,
            static region => region.Kind == GraphRegionKind.Loop);
        GraphFunction invalidFunction = function with
        {
            Regions = function.Regions
                .Select(region => region.Id == loop.Id
                    ? region with { ChildRegionIds = region.ChildRegionIds.Reverse().ToArray() }
                    : region)
                .ToArray(),
        };

        JsonException exception = Assert.Throws<JsonException>(() =>
            GraphJson.Serialize(graph with { Functions = [invalidFunction] }));

        Assert.Contains("ordered for initializer/body/increment children", exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_RejectsImplicitNonElseRegion()
    {
        GraphDocument graph = CreateGraph();
        GraphFunction function = Assert.Single(graph.Functions);
        GraphRegion then = Assert.Single(
            function.Regions,
            static region => region.Kind == GraphRegionKind.Then);
        GraphFunction invalidFunction = function with
        {
            Regions = function.Regions
                .Select(region => region.Id == then.Id ? region with { IsImplicit = true } : region)
                .ToArray(),
        };

        JsonException exception = Assert.Throws<JsonException>(() =>
            GraphJson.Serialize(graph with { Functions = [invalidFunction] }));

        Assert.Contains("Only else and omitted for-clause regions may be implicit", exception.Message,
            StringComparison.Ordinal);
    }

    private static GraphDocument CreateGraph()
    {
        var lowering = new CSharpToHirLowerer().Lower(Source, "GraphRegions.cs");
        Assert.True(lowering.Succeeded, string.Join(Environment.NewLine, lowering.Diagnostics));
        return new HirGraphProjector().Project(lowering.CompilationUnit!);
    }
}
