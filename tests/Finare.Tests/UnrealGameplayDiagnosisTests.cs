using System.Text.Json.Nodes;
using Finare.Core;
using Finare.Core.Hir;
using Finare.CSharp;
using Finare.Runtime;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed class UnrealGameplayDiagnosisTests
{
    private const string Path = "Tests/Gameplay/DefectiveDamage.cs";
    private const string Source = """
        using Finare;

        namespace Tests.Gameplay;

        public static class DefectiveDamage
        {
            [Visual]
            public static int Calculate(int baseDamage)
            {
                int damage = 0;
                return damage;
            }
        }
        """;

    [Fact]
    public async Task LiveDebugValueAndProfileDeltaLocateDefectAndRegressionWithoutLogs()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, Path);
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(
            analysis.Hir!.DescendantsAndSelf().OfType<HirMethodDeclaration>());
        HirVariableDeclarationStatement declaration = Assert.Single(
            analysis.Hir!.DescendantsAndSelf().OfType<HirVariableDeclarationStatement>());
        HirReturnStatement returnStatement = Assert.Single(
            analysis.Hir!.DescendantsAndSelf().OfType<HirReturnStatement>());
        using var debug = new CoreClrDebugLoopbackTransport();
        CoreClrDebugReloadResult reload = debug.Reload(request);
        _ = debug.SetBreakpoint(new SemanticBreakpoint(
            SemanticId.FromKey("gameplay-defect-return"),
            returnStatement.Id));
        SemanticId methodId = debug.ResolveVisualMethodId(
            "Tests.Gameplay.DefectiveDamage.Calculate");
        CoreClrDebugExecution execution = debug.Start(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("20")]));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CoreClrDebugPause pause = await debug.WaitForPauseAsync(
            execution.ExecutionId,
            timeout.Token);
        CoreClrDebugStackFrame frame = Assert.Single(debug.GetStackTrace(
            reload.Generation.GenerationId,
            pause.PauseId));
        IReadOnlyList<CoreClrDebugValue> locals = debug.GetLocals(
            reload.Generation.GenerationId,
            pause.PauseId,
            frame.FrameId);
        UnrealGameplayDebugObservation observation =
            UnrealGameplayDebugObservation.Capture(
                pause,
                frame,
                locals,
                "damage",
                expectedDisplay: "20");
        debug.Continue(reload.Generation.GenerationId, pause.PauseId);
        _ = await debug.WaitForCompletionAsync(execution.ExecutionId, timeout.Token);

        UnrealGameplayProfileTrace baseline = CreateTrace(
            "baseline",
            CreateNode(method.Id, returnStatement.Id, 60_000,
                new UnrealGameplayProfileBoundary("reflectionFallback", 1, 20_000)),
            CreateNode(method.Id, declaration.Id, 200_000,
                new UnrealGameplayProfileBoundary("batchTrace", 1, 100_000)));
        UnrealGameplayProfileTrace current = CreateTrace(
            "current",
            CreateNode(method.Id, returnStatement.Id, 70_000,
                new UnrealGameplayProfileBoundary("reflectionFallback", 1, 30_000)),
            CreateNode(method.Id, declaration.Id, 650_000,
                new UnrealGameplayProfileBoundary("batchTrace", 1, 500_000)));
        UnrealGameplayDiagnosisReport report = UnrealGameplayDiagnosis.Analyze(
            baseline,
            current,
            observation,
            [
                new UnrealGameplayNodeLocation(
                    method.Id.Value,
                    returnStatement.Id.Value,
                    returnStatement.Span),
                new UnrealGameplayNodeLocation(
                    method.Id.Value,
                    declaration.Id.Value,
                    declaration.Span),
            ],
            new UnrealGameplayDiagnosisOptions(
                MinimumRegressionRatio: 1.25,
                MinimumAverageIncreaseNanoseconds: 100_000));

        Assert.True(report.AcceptanceSatisfied);
        UnrealGameplayDiagnosisFinding defect = Assert.Single(
            report.Findings,
            static finding => finding.Kind == UnrealGameplayDiagnosisKind.GameplayDefect);
        Assert.Equal(returnStatement.Id.Value, defect.NodeId);
        Assert.Equal(returnStatement.Span, defect.SourceSpan);
        Assert.Equal("reflectionFallback", defect.RuntimeBoundary);
        Assert.Equal("damage", defect.LocalName);
        Assert.Equal("0", defect.ActualDisplay);
        UnrealGameplayDiagnosisFinding regression = Assert.Single(
            report.Findings,
            static finding =>
                finding.Kind == UnrealGameplayDiagnosisKind.PerformanceRegression);
        Assert.Equal(declaration.Id.Value, regression.NodeId);
        Assert.Equal(declaration.Span, regression.SourceSpan);
        Assert.Equal("batchTrace", regression.RuntimeBoundary);
        Assert.Equal(100_000, regression.BaselineAverageNanoseconds);
        Assert.Equal(500_000, regression.CurrentAverageNanoseconds);
        Assert.Equal(5.0, regression.Ratio);
        string json = UnrealGameplayDiagnosisJson.Serialize(report);
        Assert.Contains(UnrealGameplayDiagnosisReport.CurrentSchemaVersion, json);
        Assert.Contains(returnStatement.Id.Value, json);
        Assert.DoesNotContain("Console", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckedInDiagnosisFixtureReproducesThePortableReport()
    {
        string directory = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "UnrealDiagnostics");
        UnrealGameplayProfileTrace baseline =
            UnrealGameplayDiagnosisJson.DeserializeTrace(File.ReadAllText(
                System.IO.Path.Combine(directory, "GameplayDiagnosis.baseline.json")));
        UnrealGameplayProfileTrace current =
            UnrealGameplayDiagnosisJson.DeserializeTrace(File.ReadAllText(
                System.IO.Path.Combine(directory, "GameplayDiagnosis.current.json")));
        UnrealGameplayDiagnosisEvidence evidence =
            UnrealGameplayDiagnosisJson.DeserializeEvidence(File.ReadAllText(
                System.IO.Path.Combine(directory, "GameplayDiagnosis.evidence.json")));
        CSharpAnalysisResult sourceAnalysis = new CSharpSemanticPipeline().Analyze(
            CSharpCompilationRequest.Single(
                File.ReadAllText(System.IO.Path.Combine(
                    directory,
                    "GameplayDiagnosisFixture.cs")),
                "samples/Unreal/Diagnostics/GameplayDiagnosisFixture.cs"));
        HirMethodDeclaration sourceMethod = Assert.Single(
            sourceAnalysis.Hir!.DescendantsAndSelf().OfType<HirMethodDeclaration>());
        Dictionary<string, HirNode> sourceNodes = sourceAnalysis.Hir!
            .DescendantsAndSelf()
            .ToDictionary(static node => node.Id.Value);
        Assert.All(evidence.NodeLocations, location =>
        {
            Assert.Equal(sourceMethod.Id.Value, location.MethodId);
            Assert.Equal(location.SourceSpan, sourceNodes[location.NodeId].Span);
        });

        UnrealGameplayDiagnosisReport actual = UnrealGameplayDiagnosis.Analyze(
            baseline,
            current,
            evidence.DebugObservation,
            evidence.NodeLocations,
            evidence.Options);
        JsonNode expectedJson = JsonNode.Parse(File.ReadAllText(
            System.IO.Path.Combine(directory, "GameplayDiagnosis.report.json")))!;
        JsonNode actualJson = JsonNode.Parse(
            UnrealGameplayDiagnosisJson.Serialize(actual))!;

        Assert.True(actual.AcceptanceSatisfied);
        Assert.True(JsonNode.DeepEquals(expectedJson, actualJson));
    }

    private static UnrealGameplayProfileNode CreateNode(
        SemanticId methodId,
        SemanticId nodeId,
        long totalNanoseconds,
        UnrealGameplayProfileBoundary boundary) => new(
            methodId.Value,
            nodeId.Value,
            1,
            totalNanoseconds,
            [boundary]);

    private static UnrealGameplayProfileTrace CreateTrace(
        string traceId,
        params UnrealGameplayProfileNode[] nodes)
    {
        var pressure = new UnrealGameplayProfileBufferPressure(0, 0, 0, 0);
        var frame = new UnrealGameplayProfileFrame(
            1,
            "1",
            1,
            "Tests.Gameplay.DefectiveDamage",
            0.016f,
            "ManagedTick",
            [],
            nodes.Sum(static node => node.TotalNanoseconds),
            0,
            [],
            nodes,
            pressure);
        var aggregate = new UnrealGameplayProfileAggregate(
            1,
            frame.DurationNanoseconds,
            0,
            [],
            [],
            nodes,
            pressure);
        return new UnrealGameplayProfileTrace(
            UnrealGameplayProfileTrace.CurrentSchemaVersion,
            traceId,
            DateTimeOffset.UnixEpoch,
            "world-fixture",
            "script-fixture",
            1,
            0,
            [frame],
            aggregate);
    }
}
