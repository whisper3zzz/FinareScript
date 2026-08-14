using UnifiedScript.CSharp;
using UnifiedScript.Graph;

namespace UnifiedScript.Tests;

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
    public void Serialize_IsDeterministic()
    {
        var lowering = new CSharpToHirLowerer().Lower(Source, "DoorController.cs");
        var projector = new HirGraphProjector();

        string first = GraphJson.Serialize(projector.Project(lowering.CompilationUnit!));
        string second = GraphJson.Serialize(projector.Project(lowering.CompilationUnit!));

        Assert.Equal(first, second);
    }
}

