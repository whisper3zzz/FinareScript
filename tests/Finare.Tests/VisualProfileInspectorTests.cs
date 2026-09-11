using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Finare.Authoring;
using Finare.CSharp;
using Finare.Graph;
using Finare.Semantics;

namespace Finare.Tests;

[Trait("Category", "Graph")]
public sealed class VisualProfileInspectorTests
{
    [Fact]
    public void InspectPublishesVersionedVisualAndOpaqueMethodBoundaries()
    {
        const string source = """
            using System;
            using Finare;

            sealed class Rules
            {
                [Visual]
                int Run(int value)
                {
                    return SourceOnly(value);
                }

                static int SourceOnly(int value)
                {
                    try
                    {
                        return value;
                    }
                    catch (Exception)
                    {
                        return 0;
                    }
                }

                [Visual]
                int Invalid(int value)
                {
                    try
                    {
                        return value;
                    }
                    catch (Exception)
                    {
                        return 0;
                    }
                }
            }
            """;
        VisualProfileReport report = new VisualProfileInspector().Inspect(
            [CSharpSyntaxTree.ParseText(source, path: "ProfileRules.cs")]);

        Assert.Equal(VisualProfileReport.CurrentSchemaVersion, report.SchemaVersion);
        Assert.Equal(VisualCSharpProfileContract.CurrentSchemaVersion, report.Profile.SchemaVersion);
        Assert.Equal(3, report.Methods.Count);
        VisualMethodProfile run = report.Methods.Single(static method => method.Name == "Run");
        VisualMethodProfile sourceOnly = report.Methods.Single(
            static method => method.Name == "SourceOnly");
        VisualMethodProfile invalid = report.Methods.Single(
            static method => method.Name == "Invalid");
        Assert.Equal(VisualMethodRepresentation.VisualGraph, run.Representation);
        Assert.True(run.GraphEditable);
        Assert.Equal("visualGraph", run.AuthoringBoundary);
        Assert.Equal(VisualMethodRepresentation.OpaqueCode, sourceOnly.Representation);
        Assert.False(sourceOnly.GraphEditable);
        Assert.Equal("sourceOnly", sourceOnly.AuthoringBoundary);
        Assert.Empty(sourceOnly.Diagnostics);
        Assert.False(invalid.GraphEditable);
        Assert.Contains(invalid.Diagnostics, static diagnostic =>
            diagnostic.Code == VisualProfileDiagnosticCodes.ExceptionFlowNotSupported &&
            diagnostic.Message.Contains("source-only", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("graph-safe", StringComparison.Ordinal));
        Assert.False(report.Succeeded);

        string json = VisualProfileJson.Serialize(report);
        Assert.Equal(json, VisualProfileJson.Serialize(VisualProfileJson.Deserialize(json)));
        Assert.Contains("unifiedscript.visual-csharp-profile/v0.3", json, StringComparison.Ordinal);

        VisualProfileReport malformed = report with
        {
            Methods = report.Methods.Select(method => method.Name == "SourceOnly"
                ? method with { GraphEditable = true }
                : method).ToArray(),
        };
        Assert.Throws<JsonException>(() => VisualProfileJson.Serialize(malformed));
    }

    [Fact]
    public void PipelineProjectsOpaqueHelperAsOneSourceOnlyCallNode()
    {
        const string source = """
            using System;
            using Finare;

            [ScriptAsset("Tests/OpaqueRules")]
            sealed class Rules
            {
                [Visual]
                int Run(int value)
                {
                    return SourceOnly(value);
                }

                static int SourceOnly(int value)
                {
                    try
                    {
                        return value;
                    }
                    catch (Exception)
                    {
                        return 0;
                    }
                }
            }
            """;
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(
            source,
            "OpaqueRules.cs");

        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        Assert.Equal(2, analysis.Profile.Methods.Count);
        Assert.Single(analysis.Profile.Methods, static method =>
            method.Representation == VisualMethodRepresentation.OpaqueCode);
        MethodDescriptor opaqueMethod = Assert.Single(analysis.Semantics!.Methods, static method =>
            method.MetadataName.Contains("SourceOnly", StringComparison.Ordinal));
        Assert.Equal(MethodBodyRepresentation.OpaqueCode, opaqueMethod.BodyRepresentation);

        GraphDocument graph = new HirGraphProjector().Project(
            analysis.Hir!,
            analysis.Semantics,
            analysis.DeclarationReferences);
        GraphNode invocation = Assert.Single(
            Assert.Single(graph.Functions).Nodes,
            static node => node.Kind == "invocation");
        Assert.Equal("opaqueCode", invocation.Properties["calleeBodyRepresentation"]);
        Assert.Equal("sourceOnly", invocation.Properties["authoringBoundary"]);
        Assert.Equal("false", invocation.Properties["calleeBodyEditable"]);
        Assert.Equal(
            GraphJson.Serialize(graph),
            GraphJson.Serialize(GraphJson.Deserialize(GraphJson.Serialize(graph))));

        AuthoringSurfaceDocument surface = new AuthoringSurfaceProjector().Project(
            analysis.ScriptAssets!,
            graph);
        AuthoringActionSurface call = Assert.Single(
            Assert.Single(Assert.Single(surface.Assets).Functions).Actions,
            static action => action.GraphKind == "invocation");
        Assert.Equal("opaqueCode", call.Properties["calleeBodyRepresentation"]);
        Assert.Equal("sourceOnly", call.Properties["authoringBoundary"]);
        Assert.Equal("false", call.Properties["calleeBodyEditable"]);
        Assert.Equal(
            AuthoringSurfaceJson.Serialize(surface),
            AuthoringSurfaceJson.Serialize(
                AuthoringSurfaceJson.Deserialize(AuthoringSurfaceJson.Serialize(surface))));

        Dictionary<string, string> invalidSurfaceProperties = call.Properties.ToDictionary(
            static property => property.Key,
            static property => property.Value,
            StringComparer.Ordinal);
        invalidSurfaceProperties.Remove("methodId");
        AuthoringAssetSurface surfaceAsset = Assert.Single(surface.Assets);
        AuthoringFunctionSurface surfaceFunction = Assert.Single(surfaceAsset.Functions);
        AuthoringSurfaceDocument malformedSurface = surface with
        {
            Assets = [surfaceAsset with
            {
                Functions = [surfaceFunction with
                {
                    Actions = surfaceFunction.Actions.Select(action => action.Id == call.Id
                        ? action with { Properties = invalidSurfaceProperties }
                        : action).ToArray(),
                }],
            }],
        };
        Assert.Throws<JsonException>(() => AuthoringSurfaceJson.Serialize(malformedSurface));

        Dictionary<string, string> invalidProperties = invocation.Properties.ToDictionary(
            static property => property.Key,
            static property => property.Value,
            StringComparer.Ordinal);
        invalidProperties["calleeBodyEditable"] = "true";
        GraphFunction function = Assert.Single(graph.Functions);
        GraphDocument malformed = graph with
        {
            Functions = [function with
            {
                Nodes = function.Nodes.Select(node => node.Id == invocation.Id
                    ? node with { Properties = invalidProperties }
                    : node).ToArray(),
            }],
        };
        Assert.Throws<JsonException>(() => GraphJson.Serialize(malformed));
    }

    [Fact]
    public void PipelineRejectsGraphUnsafeOpaqueCallSignatureWithBoundaryDiagnostic()
    {
        const string source = """
            using Finare;

            static class Rules
            {
                [Visual]
                static int Run(int value)
                {
                    return SourceOnly(value) == null ? 0 : 1;
                }

                static object SourceOnly(int value) => value;
            }
            """;
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(
            source,
            "UnsafeOpaqueBoundary.cs");

        Assert.False(analysis.Succeeded);
        Assert.NotNull(analysis.Hir);
        Assert.Null(analysis.Semantics);
        Assert.Contains(analysis.Diagnostics, static diagnostic =>
            diagnostic.Code == SemanticDiagnosticCodes.OpaqueCodeBoundaryNotSupported &&
            diagnostic.Message.Contains("return type 'object'", StringComparison.Ordinal));
        Assert.True(analysis.Profile.Succeeded);

        const string refReturnSource = """
            using Finare;

            static class RefRules
            {
                static int storage;

                [Visual]
                static int Run()
                {
                    return SourceOnly();
                }

                static ref int SourceOnly() => ref storage;
            }
            """;
        CSharpAnalysisResult refReturnAnalysis = new CSharpSemanticPipeline().Analyze(
            refReturnSource,
            "UnsafeOpaqueRefReturn.cs");

        Assert.False(refReturnAnalysis.Succeeded);
        Assert.Contains(refReturnAnalysis.Diagnostics, static diagnostic =>
            diagnostic.Code == SemanticDiagnosticCodes.OpaqueCodeBoundaryNotSupported &&
            diagnostic.Message.Contains("ref return", StringComparison.Ordinal));
    }
}
