using System.Text.Json;
using UnifiedScript.CSharp;
using UnifiedScript.Core;
using UnifiedScript.Graph;

namespace UnifiedScript.Tests;

public sealed class SemanticGraphReviewTests
{
    private const string Source = """
        using UnifiedScript;

        static class Rules
        {
            [Visual]
            static int First()
            {
                return 1;
            }

            [Visual]
            static int Second()
            {
                return 2;
            }
        }
        """;

    [Fact]
    public void DiffIgnoresSourceSpansAndReportsStableValueChanges()
    {
        GraphDocument baseline = CreateGraph();
        GraphDocument spanOnly = RewriteSpans(baseline) with
        {
            SourceCompilationId = SemanticId.FromKey("span-only"),
        };
        SemanticGraphDiffDocument noNoise = new SemanticGraphDiffer().Compare(
            baseline,
            spanOnly);

        Assert.Empty(noNoise.Functions);
        Assert.Equal(0, noNoise.Summary.ChangeCount);

        GraphDocument target = ChangeLiteral(spanOnly, "Rules.First", "11");
        SemanticGraphDiffDocument diff = new SemanticGraphDiffer().Compare(baseline, target);
        SemanticGraphFunctionDiff function = Assert.Single(diff.Functions);
        SemanticGraphChange change = Assert.Single(
            function.Changes,
            static item => item.Facet == "property:value");

        Assert.Equal("Rules.First", function.QualifiedName);
        Assert.Equal(SemanticGraphChangeCategory.Value, change.Category);
        Assert.Equal("1", change.Before);
        Assert.Equal("11", change.After);
        Assert.Equal(
            SemanticGraphDiffDocument.CurrentSchemaVersion,
            diff.SchemaVersion);
        string json = SemanticGraphReviewJson.SerializeDiff(diff);
        Assert.Equal(
            json,
            SemanticGraphReviewJson.SerializeDiff(
                SemanticGraphReviewJson.DeserializeDiff(json)));
    }

    [Fact]
    public void MergeCombinesDisjointFunctionChangesDeterministically()
    {
        GraphDocument baseline = CreateGraph();
        GraphDocument ours = ChangeLiteral(baseline, "Rules.First", "10");
        GraphDocument theirs = ChangeLiteral(baseline, "Rules.Second", "20");

        SemanticGraphMergeResult result = new SemanticGraphMerger().Merge(
            baseline,
            ours,
            theirs);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.MergedGraph);
        Assert.Equal(SemanticGraphMergeStatus.Clean, result.Document.Status);
        Assert.Equal(2, result.Document.Resolutions.Count);
        Assert.Contains(
            result.Document.Resolutions,
            static resolution => resolution.Source == SemanticGraphMergeSource.Ours &&
                resolution.QualifiedName == "Rules.First");
        Assert.Contains(
            result.Document.Resolutions,
            static resolution => resolution.Source == SemanticGraphMergeSource.Theirs &&
                resolution.QualifiedName == "Rules.Second");
        Assert.Equal(2, new SemanticGraphDiffer().Compare(
            baseline,
            result.MergedGraph!).Summary.ModifiedFunctions);

        string report = SemanticGraphReviewJson.SerializeMerge(result.Document);
        Assert.Equal(
            report,
            SemanticGraphReviewJson.SerializeMerge(
                SemanticGraphReviewJson.DeserializeMerge(report)));
        Assert.Equal(
            GraphJson.Serialize(result.MergedGraph),
            GraphJson.Serialize(new SemanticGraphMerger().Merge(
                baseline,
                ours,
                theirs).MergedGraph!));
    }

    [Fact]
    public void MergeRejectsDifferentChangesToTheSameFunction()
    {
        GraphDocument baseline = CreateGraph();
        GraphDocument ours = ChangeLiteral(baseline, "Rules.First", "10");
        GraphDocument theirs = ChangeLiteral(baseline, "Rules.First", "12");

        SemanticGraphMergeResult result = new SemanticGraphMerger().Merge(
            baseline,
            ours,
            theirs);

        Assert.False(result.Succeeded);
        Assert.Null(result.MergedGraph);
        Assert.Null(result.Document.MergedCompilationId);
        Assert.Equal(SemanticGraphMergeStatus.Conflicted, result.Document.Status);
        SemanticGraphMergeConflict conflict = Assert.Single(result.Document.Conflicts);
        Assert.Equal("Rules.First", conflict.QualifiedName);
        Assert.Contains(
            conflict.Ours.Changes,
            static change => change.Facet == "property:value" && change.After == "10");
        Assert.Contains(
            conflict.Theirs.Changes,
            static change => change.Facet == "property:value" && change.After == "12");
    }

    [Fact]
    public void DiffRejectsDifferentSemanticConfigurations()
    {
        GraphDocument baseline = CreateGraph();
        GraphDocument incompatible = baseline with
        {
            SourceCompilationId = SemanticId.FromKey("incompatible-source"),
            SemanticConfigurationId = SemanticId.FromKey("incompatible-configuration"),
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new SemanticGraphDiffer().Compare(baseline, incompatible));

        Assert.Contains("same semantic configuration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiffJsonRejectsTamperedSummary()
    {
        GraphDocument baseline = CreateGraph();
        SemanticGraphDiffDocument diff = new SemanticGraphDiffer().Compare(
            baseline,
            ChangeLiteral(baseline, "Rules.First", "10"));

        JsonException exception = Assert.Throws<JsonException>(() =>
            SemanticGraphReviewJson.SerializeDiff(diff with
            {
                Summary = diff.Summary with { ChangeCount = 99 },
            }));

        Assert.Contains("summary", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SemanticDiffCliWritesDeterministicReportAndCanFailOnChange()
    {
        using var fixture = new TemporaryDirectory();
        GraphDocument baseline = CreateGraph();
        GraphDocument target = ChangeLiteral(baseline, "Rules.First", "10");
        string baselinePath = Path.Combine(fixture.Path, "baseline.graph.json");
        string targetPath = Path.Combine(fixture.Path, "target.graph.json");
        string reportPath = Path.Combine(fixture.Path, "semantic-diff.json");
        File.WriteAllText(baselinePath, GraphJson.Serialize(baseline));
        File.WriteAllText(targetPath, GraphJson.Serialize(target));

        int first = UnifiedScript.Cli.Program.Main([
            "semantic-diff", baselinePath, targetPath, "--out", reportPath,
            "--fail-on-change",
        ]);
        string report = File.ReadAllText(reportPath);
        int second = UnifiedScript.Cli.Program.Main([
            "semantic-diff", baselinePath, targetPath, "--out", reportPath,
            "--fail-on-change",
        ]);

        Assert.Equal(3, first);
        Assert.Equal(3, second);
        Assert.Equal(1, SemanticGraphReviewJson.DeserializeDiff(report).Summary.ChangeCount);
        Assert.Equal(report, File.ReadAllText(reportPath));
    }

    [Fact]
    public void SemanticMergeCliWritesCleanGraphOrConflictReportAtomically()
    {
        using var fixture = new TemporaryDirectory();
        GraphDocument baseline = CreateGraph();
        GraphDocument ours = ChangeLiteral(baseline, "Rules.First", "10");
        GraphDocument theirs = ChangeLiteral(baseline, "Rules.Second", "20");
        string baselinePath = WriteGraph(fixture.Path, "baseline", baseline);
        string oursPath = WriteGraph(fixture.Path, "ours", ours);
        string theirsPath = WriteGraph(fixture.Path, "theirs", theirs);
        string reportPath = Path.Combine(fixture.Path, "merge.json");
        string mergedPath = Path.Combine(fixture.Path, "merged.graph.json");

        int clean = UnifiedScript.Cli.Program.Main([
            "semantic-merge", baselinePath, oursPath, theirsPath,
            "--report", reportPath, "--out", mergedPath,
        ]);

        Assert.Equal(0, clean);
        Assert.True(File.Exists(mergedPath));
        Assert.Equal(SemanticGraphMergeStatus.Clean,
            SemanticGraphReviewJson.DeserializeMerge(File.ReadAllText(reportPath)).Status);
        Assert.Equal(2, new SemanticGraphDiffer().Compare(
            baseline,
            GraphJson.Deserialize(File.ReadAllText(mergedPath))).Summary.ModifiedFunctions);

        string conflictPath = WriteGraph(
            fixture.Path,
            "conflicting-theirs",
            ChangeLiteral(baseline, "Rules.First", "12"));
        string conflictReportPath = Path.Combine(fixture.Path, "conflict.json");
        string rejectedOutputPath = Path.Combine(fixture.Path, "rejected.graph.json");
        int conflicted = UnifiedScript.Cli.Program.Main([
            "semantic-merge", baselinePath, oursPath, conflictPath,
            "--report", conflictReportPath, "--out", rejectedOutputPath,
        ]);

        Assert.Equal(3, conflicted);
        Assert.False(File.Exists(rejectedOutputPath));
        Assert.Equal(SemanticGraphMergeStatus.Conflicted,
            SemanticGraphReviewJson.DeserializeMerge(File.ReadAllText(conflictReportPath)).Status);
    }

    [Fact]
    public void MergeJsonRejectsResolutionThatDoesNotMatchItsSelectedSource()
    {
        GraphDocument baseline = CreateGraph();
        SemanticGraphMergeDocument merge = new SemanticGraphMerger().Merge(
            baseline,
            ChangeLiteral(baseline, "Rules.First", "10"),
            baseline).Document;
        SemanticGraphFunctionResolution resolution = Assert.Single(merge.Resolutions);

        JsonException exception = Assert.Throws<JsonException>(() =>
            SemanticGraphReviewJson.SerializeMerge(merge with
            {
                Resolutions =
                [
                    resolution with { Source = SemanticGraphMergeSource.Theirs },
                ],
            }));

        Assert.Contains("selected source", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static GraphDocument CreateGraph()
    {
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(
            Source,
            "Rules.cs");
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        return new HirGraphProjector().Project(
            analysis.Hir!,
            analysis.Semantics!,
            analysis.DeclarationReferences);
    }

    private static GraphDocument ChangeLiteral(
        GraphDocument graph,
        string functionName,
        string value)
    {
        GraphFunction function = Assert.Single(
            graph.Functions,
            candidate => candidate.QualifiedName == functionName);
        GraphNode literal = Assert.Single(
            function.Nodes,
            static node => node.Properties.ContainsKey("value"));
        Dictionary<string, string> properties = literal.Properties.ToDictionary(
            static property => property.Key,
            static property => property.Value,
            StringComparer.Ordinal);
        properties["value"] = value;
        GraphFunction changed = function with
        {
            Nodes = function.Nodes.Select(node =>
                node.Id == literal.Id ? node with { Properties = properties } : node).ToArray(),
        };
        return GraphJson.NormalizeAndValidate(graph with
        {
            SourceCompilationId = SemanticId.FromKey(
                $"{graph.SourceCompilationId}:{functionName}:{value}"),
            Functions = graph.Functions.Select(candidate =>
                candidate.Id == function.Id ? changed : candidate).ToArray(),
        });
    }

    private static GraphDocument RewriteSpans(GraphDocument graph)
    {
        SourceSpan noise = SourceSpan.Unknown("FormattingOnly.cs");
        return graph with
        {
            Functions = graph.Functions.Select(function => function with
            {
                Nodes = function.Nodes.Select(node => node with { Span = noise }).ToArray(),
                Regions = function.Regions.Select(region => region with { Span = noise }).ToArray(),
            }).ToArray(),
        };
    }

    private static string WriteGraph(string directory, string name, GraphDocument graph)
    {
        string path = Path.Combine(directory, $"{name}.graph.json");
        File.WriteAllText(path, GraphJson.Serialize(graph));
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"UnifiedScriptSemanticReview-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
