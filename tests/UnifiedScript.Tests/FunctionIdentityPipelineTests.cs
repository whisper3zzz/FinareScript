using UnifiedScript.Authoring;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.CSharp;
using UnifiedScript.Graph;
using UnifiedScript.Runtime;

namespace UnifiedScript.Tests;

public sealed class FunctionIdentityPipelineTests
{
    private const string Path = "Game/FunctionIdentity.cs";
    private const string Source = """
        using UnifiedScript;

        namespace Game;

        [ScriptAsset("Game/FunctionIdentity")]
        public sealed class FunctionIdentity
        {
            [Visual]
            public static int Callee(int value)
            {
                return value + 1;
            }

            [Visual]
            public static int Caller(int value)
            {
                return Callee(value) + 1;
            }
        }
        """;

    [Fact]
    public void PersistentFunctionIdsRemainUnifiedAcrossGraphAuthoringAndCoreClr()
    {
        CSharpAnalysisResult baseline = AnalyzeWithSidecar(
            Source,
            IdentitySidecarDocument.CreateEmpty(Path));
        IdentitySidecarDocument sidecar = Assert.Single(baseline.IdentitySidecars);
        CSharpCompilationRequest request = Request(Source, sidecar);
        CSharpAnalysisResult original = Analyze(request);

        AssertUnifiedFunctionIds(original);
        ScriptAssetDeclaration asset = Assert.Single(original.ScriptAssets!.Assets);
        ScriptFunctionDeclaration caller = asset.Functions.Single(static function => function.Name == "Caller");
        ScriptFunctionDeclaration callee = asset.Functions.Single(static function => function.Name == "Callee");
        HirMethodDeclaration callerMethod = original.Hir!.Types
            .SelectMany(static type => type.Methods)
            .Single(method => method.Name == "Caller");
        HirMethodDeclaration calleeMethod = original.Hir.Types
            .SelectMany(static type => type.Methods)
            .Single(method => method.Name == "Callee");
        Assert.Equal(caller.Id, callerMethod.Id);
        Assert.Equal(callee.Id, calleeMethod.Id);

        HirInvocationExpression invocation = Assert.Single(
            callerMethod.DescendantsAndSelf().OfType<HirInvocationExpression>());
        Assert.Equal(callee.Id, original.Semantics!.Nodes.Single(node => node.NodeId == invocation.Id)
            .InvokedMethodId);

        GraphDocument graph = new HirGraphProjector().Project(
            original.Hir,
            original.Semantics,
            original.DeclarationReferences);
        AuthoringSurfaceDocument surface = new AuthoringSurfaceProjector().Project(
            original.ScriptAssets,
            graph);
        Assert.Equal(
            asset.Functions.Select(static function => function.Id).OrderBy(static id => id.Value),
            surface.Assets.Single().Functions.Select(static function => function.Id)
                .OrderBy(static id => id.Value));

        using CoreClrExecutionSession session = new CoreClrExecutionHost().CompileAndLoad(request);
        Assert.Equal(caller.Id, session.ResolveVisualMethodId("Game.FunctionIdentity.Caller"));
        ExecutionResult execution = session.Execute(new ExecutionInvocation(
            caller.Id,
            [new ExecutionArgument("40")]));
        Assert.Equal(ExecutionOutcome.Returned, execution.Outcome);
        Assert.Equal("42", execution.ReturnValue!.Json);

        DeclarationPatchDocument rename = DeclarationPatchDocument.Create(
            original.ScriptAssets.SourceCompilationId,
            original.ScriptAssets.SemanticConfigurationId,
            [new UpdateScriptFunctionPatchOperation(
                callee.Id,
                new ScriptFunctionPatchPrecondition(
                    callee.Name,
                    callee.ReturnTypeMetadataName,
                    callee.IsStatic,
                    callee.Parameters.Select(static parameter =>
                        new ScriptFunctionParameterPatchPrecondition(
                            parameter.Name,
                            parameter.TypeMetadataName)).ToArray()),
                new ScriptFunctionPatchSpec(
                    "RenamedCallee",
                    "int",
                    IsStatic: true,
                    [new ScriptFunctionParameterPatchSpec("value", "int")]))]);
        CSharpPatchResult patched = new CSharpDeclarationPatchApplier().Apply(request, rename);
        Assert.True(patched.Succeeded, string.Join(Environment.NewLine, patched.Diagnostics));
        CSharpAnalysisResult renamed = patched.Analysis!;
        AssertUnifiedFunctionIds(renamed);
        Assert.Equal(caller.Id, renamed.ScriptAssets!.Assets.Single().Functions
            .Single(static function => function.Name == "Caller").Id);
        Assert.Equal(callee.Id, renamed.ScriptAssets.Assets.Single().Functions
            .Single(static function => function.Name == "RenamedCallee").Id);
        GraphDocument renamedGraph = new HirGraphProjector().Project(
            renamed.Hir!,
            renamed.Semantics,
            renamed.DeclarationReferences);
        AuthoringSurfaceDocument renamedSurface = new AuthoringSurfaceProjector().Project(
            renamed.ScriptAssets,
            renamedGraph);
        Assert.Equal(
            renamed.ScriptAssets.Assets.Single().Functions.Select(static function => function.Id)
                .OrderBy(static id => id.Value),
            renamedSurface.Assets.Single().Functions.Select(static function => function.Id)
                .OrderBy(static id => id.Value));
        HirMethodDeclaration renamedCallerMethod = renamed.Hir!.Types
            .SelectMany(static type => type.Methods)
            .Single(method => method.Name == "Caller");
        HirInvocationExpression renamedInvocation = Assert.Single(
            renamedCallerMethod.DescendantsAndSelf().OfType<HirInvocationExpression>());
        Assert.Equal(
            callee.Id,
            renamed.Semantics!.Nodes.Single(node => node.NodeId == renamedInvocation.Id)
                .InvokedMethodId);

        using CoreClrExecutionSession renamedSession = new CoreClrExecutionHost().CompileAndLoad(
            patched.PatchedRequest!);
        Assert.Equal(caller.Id, renamedSession.ResolveVisualMethodId("Game.FunctionIdentity.Caller"));
        ExecutionResult renamedExecution = renamedSession.Execute(new ExecutionInvocation(
            caller.Id,
            [new ExecutionArgument("40")]));
        Assert.Equal(ExecutionOutcome.Returned, renamedExecution.Outcome);
        Assert.Equal("42", renamedExecution.ReturnValue!.Json);
    }

    [Fact]
    public void SidecarWithoutFunctionEntriesUpgradesUsingCanonicalHirMethodIds()
    {
        CSharpAnalysisResult canonical = Analyze(CSharpCompilationRequest.Single(Source, Path));
        IReadOnlyDictionary<string, SemanticId> canonicalFunctionIds = canonical.Hir!.Types
            .SelectMany(static type => type.Methods)
            .Where(static method => method.IsVisual)
            .ToDictionary(static method => method.Name, static method => method.Id);
        CSharpAnalysisResult baseline = AnalyzeWithSidecar(
            Source,
            IdentitySidecarDocument.CreateEmpty(Path));
        IdentitySidecarDocument baselineSidecar = Assert.Single(baseline.IdentitySidecars);
        IdentitySidecarDocument legacySidecar = baselineSidecar with
        {
            Entries = baselineSidecar.Entries
                .Where(static entry => entry.Kind != "function")
                .ToArray(),
        };

        CSharpAnalysisResult upgraded = Analyze(Request(Source, legacySidecar));
        Assert.True(upgraded.Succeeded, string.Join(Environment.NewLine, upgraded.Diagnostics));
        AssertUnifiedFunctionIds(upgraded);
        ScriptAssetDeclaration asset = Assert.Single(upgraded.ScriptAssets!.Assets);
        Assert.All(asset.Functions, function =>
            Assert.Equal(canonicalFunctionIds[function.Name], function.Id));
        Assert.All(upgraded.Hir!.Types.SelectMany(static type => type.Methods)
            .Where(static method => method.IsVisual), method =>
            Assert.Equal(canonicalFunctionIds[method.Name], method.Id));
        Assert.All(asset.Functions, function =>
        {
            HirMethodDeclaration method = upgraded.Hir.Types
                .SelectMany(static type => type.Methods)
                .Single(candidate => candidate.Name == function.Name);
            Assert.Equal(method.Id, function.Id);
        });
        Assert.All(
            Assert.Single(upgraded.IdentitySidecars).Entries.Where(static entry => entry.Kind == "function"),
            entry => Assert.Contains(asset.Functions, function => function.Id == entry.Id));
    }

    private static void AssertUnifiedFunctionIds(CSharpAnalysisResult analysis)
    {
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        ScriptAssetDeclaration asset = Assert.Single(analysis.ScriptAssets!.Assets);
        HirMethodDeclaration[] methods = analysis.Hir!.Types
            .SelectMany(static type => type.Methods)
            .Where(static method => method.IsVisual)
            .ToArray();
        Assert.Equal(
            asset.Functions.Select(static function => function.Id).OrderBy(static id => id.Value),
            methods.Select(static method => method.Id).OrderBy(static id => id.Value));
    }

    private static CSharpCompilationRequest Request(
        string source,
        IdentitySidecarDocument sidecar) => new(
        "FunctionIdentityPipeline",
        [new CSharpSourceDocument(Path, source, sidecar)]);

    private static CSharpAnalysisResult AnalyzeWithSidecar(
        string source,
        IdentitySidecarDocument sidecar) => Analyze(Request(source, sidecar));

    private static CSharpAnalysisResult Analyze(CSharpCompilationRequest request)
    {
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        return result;
    }
}
