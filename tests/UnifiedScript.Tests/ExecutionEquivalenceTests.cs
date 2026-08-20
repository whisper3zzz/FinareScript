using UnifiedScript.CSharp;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Runtime;

namespace UnifiedScript.Tests;

public sealed class ExecutionEquivalenceTests
{
    private const string ReferenceSource = """
        using System.Collections.Generic;
        using UnifiedScript;

        namespace Tests.Execution;

        public static class ReferenceRules
        {
            [Visual]
            public static int Last(
                IEnumerable<int> primary,
                IEnumerable<int> fallback,
                int seed)
            {
                int result = seed;
                foreach (int value in fallback)
                {
                    result = value;
                }

                return result;
            }
        }
        """;

    [Fact]
    public void TextAuthoredAndVisualPlanAuthoredProgramsExecuteIdentically()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "ExecutionFixtures");
        const string logicalPath = "samples/Patching/EnumerableForEachBuildRules.cs";
        string source = File.ReadAllText(Path.Combine(
            fixtureDirectory,
            "EnumerableForEachBuildRules.cs"));
        IdentitySidecarDocument identity = IdentitySidecarJson.Deserialize(File.ReadAllText(
            Path.Combine(fixtureDirectory, "EnumerableForEachBuildRules.cs.usids")));
        SemanticPatchPlanDocument plan = SemanticPatchPlanJson.Deserialize(File.ReadAllText(
            Path.Combine(fixtureDirectory, "BuildEnumerableForEach.plan.json")));
        var request = new CSharpCompilationRequest(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(logicalPath, source, identity)]);

        CSharpPatchResult patched = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(patched.Succeeded, string.Join(Environment.NewLine, patched.Diagnostics));
        (ExecutionResult visualResult, CSharpProgramSourceMap visualSourceMap) = Execute(
            patched.PatchedRequest!,
            "Samples.Patching.EnumerableForEachBuildRules.Last");
        (ExecutionResult textResult, _) = Execute(
            CSharpCompilationRequest.Single(ReferenceSource, "ReferenceRules.cs"),
            "Tests.Execution.ReferenceRules.Last");
        Assert.Equal(ExecutionOutcome.Returned, visualResult.Outcome);
        Assert.Equal(ExecutionOutcome.Returned, textResult.Outcome);
        Assert.Equal(textResult.ReturnValue, visualResult.ReturnValue);
        Assert.Equal("9", visualResult.ReturnValue!.Json);
        CSharpAnalysisResult patchedAnalysis = new CSharpSemanticPipeline()
            .Analyze(patched.PatchedRequest!);
        Assert.True(
            patchedAnalysis.Succeeded,
            string.Join(Environment.NewLine, patchedAnalysis.Diagnostics));
        HirForEachStatement generatedLoop = Assert.Single(
            patchedAnalysis.Hir!.DescendantsAndSelf().OfType<HirForEachStatement>());
        Assert.Contains(
            Assert.Single(visualSourceMap.Methods).Nodes,
            node => node.NodeId == generatedLoop.Id && node.IlOffsets.Count > 0);
    }

    private static (ExecutionResult Result, CSharpProgramSourceMap SourceMap) Execute(
        CSharpCompilationRequest request,
        string qualifiedMethodName)
    {
        using CoreClrExecutionSession session = new CoreClrExecutionHost().CompileAndLoad(request);
        SemanticId methodId = session.ResolveVisualMethodId(qualifiedMethodName);
        ExecutionResult result = session.Execute(new ExecutionInvocation(
            methodId,
            [
                new ExecutionArgument("[1,2]"),
                new ExecutionArgument("[8,9]"),
                new ExecutionArgument("3"),
            ]));
        return (result, session.SourceMap);
    }
}
