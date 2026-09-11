using Microsoft.CodeAnalysis.CSharp;
using Finare.Authoring;
using Finare.CSharp;
using Finare.Core;
using Finare.Core.Hir;
using Finare.Graph;
using Finare.Semantics;

namespace Finare.Tests;

[Trait("Category", "Compiler")]
public sealed class SwitchStructuredControlFlowTests
{
    private const string Source = """
        using Finare;

        static class Rules
        {
            [Visual]
            static int Run(int value)
            {
                int result = 0;
                switch (value)
                {
                    case int number when number > 10:
                        result = number;
                        break;
                    case < 0:
                        result = 2;
                        break;
                    case 0:
                        break;
                    default:
                        result = -1;
                        break;
                }

                return result;
            }
        }
        """;

    [Fact]
    public void Lower_PreservesSwitchSectionsLabelsPatternsGuardsAndVariables()
    {
        CSharpLoweringResult lowering = new CSharpToHirLowerer().Lower(
            Source,
            "SwitchRules.cs");

        Assert.True(lowering.Succeeded, string.Join(Environment.NewLine, lowering.Diagnostics));
        HirMethodDeclaration method = Assert.Single(
            Assert.Single(lowering.CompilationUnit!.Types).Methods);
        HirSwitchStatement @switch = Assert.IsType<HirSwitchStatement>(
            method.Body.Statements[1]);
        Assert.Equal("unifiedscript.hir/v0.9", HirDocument.Create(lowering.CompilationUnit).SchemaVersion);
        Assert.Equal(4, @switch.Sections.Count);

        HirPatternSwitchLabel declarationLabel = Assert.IsType<HirPatternSwitchLabel>(
            Assert.Single(@switch.Sections[0].Labels));
        HirDeclarationPattern declaration = Assert.IsType<HirDeclarationPattern>(
            declarationLabel.Pattern);
        Assert.Equal("int", declaration.TypeName);
        Assert.Equal("number", declaration.Variable!.Name);
        Assert.IsType<HirBinaryExpression>(declarationLabel.Guard);
        Assert.IsType<HirRelationalPattern>(Assert.IsType<HirPatternSwitchLabel>(
            Assert.Single(@switch.Sections[1].Labels)).Pattern);
        Assert.IsType<HirConstantPattern>(Assert.IsType<HirPatternSwitchLabel>(
            Assert.Single(@switch.Sections[2].Labels)).Pattern);
        Assert.IsType<HirDefaultSwitchLabel>(Assert.Single(@switch.Sections[3].Labels));
        Assert.All(@switch.Sections, static section => Assert.NotEmpty(section.Body.Statements));
    }

    [Fact]
    public void Analyze_BindsPatternVariableAndProjectsStructuredSwitchCfg()
    {
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(
            Source,
            "SwitchRules.cs");

        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(analysis.Hir!.Types).Methods);
        HirSwitchStatement @switch = Assert.IsType<HirSwitchStatement>(
            method.Body.Statements[1]);
        HirDeclarationPattern declaration = Assert.IsType<HirDeclarationPattern>(
            Assert.IsType<HirPatternSwitchLabel>(
                Assert.Single(@switch.Sections[0].Labels)).Pattern);
        HirIdentifierExpression guardUse = Assert.IsType<HirIdentifierExpression>(
            Assert.IsType<HirBinaryExpression>(Assert.IsType<HirPatternSwitchLabel>(
                Assert.Single(@switch.Sections[0].Labels)).Guard).Left);
        HirIdentifierExpression bodyUse = Assert.IsType<HirIdentifierExpression>(
            Assert.IsType<HirAssignmentExpression>(Assert.IsType<HirExpressionStatement>(
                @switch.Sections[0].Body.Statements[0]).Expression).Value);
        BoundNodeInfo guardBinding = Assert.Single(
            analysis.Semantics!.Nodes,
            node => node.NodeId == guardUse.Id);
        BoundNodeInfo bodyBinding = Assert.Single(
            analysis.Semantics.Nodes,
            node => node.NodeId == bodyUse.Id);
        Assert.Equal(declaration.Variable!.Id, guardBinding.ReferencedSymbolId);
        Assert.Equal(declaration.Variable.Id, bodyBinding.ReferencedSymbolId);

        GraphDocument graph = new HirGraphProjector().Project(
            analysis.Hir,
            analysis.Semantics);
        Assert.Equal("unifiedscript.graph/v0.13", graph.SchemaVersion);
        GraphFunction function = Assert.Single(graph.Functions);
        GraphNode switchNode = Assert.Single(
            function.Nodes,
            static node => node.Kind == "switch");
        GraphNode[] labels = function.Nodes
            .Where(static node => node.Kind == "switchCase")
            .ToArray();
        Assert.Equal("4", switchNode.Properties["labelCount"]);
        Assert.Equal("true", switchNode.Properties["hasDefault"]);
        Assert.Equal(4, labels.Length);
        Dictionary<string, string> sectionIdsByBodyId = @switch.Sections.ToDictionary(
            static section => section.Body.Id.Value,
            static section => section.Id.Value,
            StringComparer.Ordinal);
        foreach (GraphNode label in labels)
        {
            string sectionBodyId = label.Properties["sectionId"];
            Assert.Equal(
                sectionIdsByBodyId[sectionBodyId],
                label.Properties["switchSectionId"]);
        }
        Assert.Contains(
            function.Nodes,
            node => node.Id == declaration.Variable.Id && node.Kind == "patternVariable");
        Assert.Equal(4, function.Regions.Count(static region =>
            region.Kind == GraphRegionKind.SwitchSection));

        AuthoringFunctionSurface surfaceFunction = Assert.Single(
            Assert.Single(new AuthoringSurfaceProjector().Project(
                analysis.ScriptAssets!,
                graph).Assets).Functions);
        AuthoringRegionSurface[] surfaceSections = surfaceFunction.Regions
            .Where(static region => region.Kind == GraphRegionKind.SwitchSection)
            .OrderBy(static region => region.Span.Start.Line)
            .ToArray();
        Assert.Equal(@switch.Sections.Select(static section => section.Body.Id),
            surfaceSections.Select(static region => region.SourceNodeId));
        AuthoringActionSurface[] surfaceLabels = surfaceFunction.Actions
            .Where(static action => action.GraphKind == "switchCase")
            .ToArray();
        Assert.All(surfaceLabels, action => Assert.Equal(
            sectionIdsByBodyId[action.Properties["sectionId"]],
            action.Properties["switchSectionId"]));
        Assert.All(surfaceSections, section => Assert.DoesNotContain(
            section.DirectStatementIds,
            id => surfaceFunction.Actions.Single(action => action.Id == id).GraphKind ==
                "switchCase"));

        GraphControlFlowFunction flow = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(graph).Functions);
        GraphControlFlowSwitch flowSwitch = Assert.Single(flow.Switches);
        HirReturnStatement continuation = Assert.IsType<HirReturnStatement>(
            method.Body.Statements[2]);
        Assert.Equal(@switch.Id, flowSwitch.SwitchNodeId);
        Assert.Equal(4, flowSwitch.SectionRegionIds.Count);
        Assert.Equal(4, flowSwitch.LabelNodeIds.Count);
        Assert.Equal(4, flowSwitch.BreakNodeIds.Count);
        Assert.Equal(continuation.Id, flowSwitch.ExitNodeId);
        Assert.True(flow.IsReducible);
        Assert.True(flow.HasCompleteExitCoverage);
    }

    [Fact]
    public void Validate_AllowsBoundedPatternsAndRejectsRecursivePatterns()
    {
        const string valid = """
            class Rules
            {
                [Visual]
                static int Run(int value)
                {
                    switch (value)
                    {
                        case 0:
                        case < 0:
                        case > 0 and < 10:
                        case not 42:
                        case var current when current > 100:
                            break;
                        default:
                            break;
                    }

                    return value;
                }
            }
            """;
        const string invalid = """
            class Point { public int X { get; set; } }
            class Rules
            {
                [Visual]
                static int Run(object value)
                {
                    switch (value)
                    {
                        case Point { X: > 0 }:
                            return 1;
                        default:
                            return 0;
                    }
                }
            }
            """;

        Assert.Empty(new VisualProfileValidator().Validate(
            CSharpSyntaxTree.ParseText(valid, path: "ValidPatterns.cs")));
        Assert.Contains(
            new VisualProfileValidator().Validate(
                CSharpSyntaxTree.ParseText(invalid, path: "InvalidPattern.cs")),
            static diagnostic =>
                diagnostic.Code == VisualProfileDiagnosticCodes.PatternNotSupported);
    }
}
