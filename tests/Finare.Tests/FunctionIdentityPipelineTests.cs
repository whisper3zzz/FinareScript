using Finare.Authoring;
using Finare.Core;
using Finare.Core.Hir;
using Finare.CSharp;
using Finare.Graph;
using Finare.Runtime;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Compiler")]
public sealed class FunctionIdentityPipelineTests
{
    private const string Path = "Game/FunctionIdentity.cs";
    private const string Source = """
        using Finare;

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

    [Fact]
    public void PersistentFunctionAndParameterIdsSurviveGraphToOpaqueAndBack()
    {
        const string graphSource = """
            using System;
            using Finare;
            using Finare.Unreal;

            namespace Game;

            public sealed class FunctionIdentityEntry : UnrealObjectScript
            {
                [UnrealBlueprintEvent]
                public int Calculate(int value)
                {
                    return value + 1;
                }
            }
            """;
        const string opaqueSource = """
            using System;
            using Finare;
            using Finare.Unreal;

            namespace Game;

            public sealed class FunctionIdentityEntry : UnrealObjectScript
            {
                [UnrealBlueprintEvent]
                public int Calculate(int value)
                {
                    Func<int, int> addOne = item => item + 1;
                    return addOne(value);
                }
            }
            """;
        string unrealAssembly = typeof(UnrealObjectScript).Assembly.Location;
        IdentitySidecarDocument empty = IdentitySidecarDocument.CreateEmpty(Path);
        CSharpCompilationRequest graphRequest = new(
            "FunctionIdentityEntry",
            [new CSharpSourceDocument(Path, graphSource, empty)],
            [unrealAssembly],
            runtimeReferencePaths: [unrealAssembly]);
        CSharpAnalysisResult graph = Analyze(graphRequest);
        IdentitySidecarDocument initialSidecar = Assert.Single(graph.IdentitySidecars);
        ScriptFunctionDeclaration graphFunction = Assert.Single(
            Assert.Single(graph.ScriptAssets!.Assets).Functions);
        SemanticId graphParameterId = Assert.Single(graphFunction.Parameters).Id;
        Assert.Single(graph.Hir!.Types.SelectMany(static type => type.Methods));

        CSharpAnalysisResult opaque = Analyze(new CSharpCompilationRequest(
            "FunctionIdentityEntry",
            [new CSharpSourceDocument(Path, opaqueSource, initialSidecar)],
            [unrealAssembly],
            runtimeReferencePaths: [unrealAssembly]));
        ScriptFunctionDeclaration opaqueFunction = Assert.Single(
            Assert.Single(opaque.ScriptAssets!.Assets).Functions);
        Assert.Equal(graphFunction.Id, opaqueFunction.Id);
        Assert.Equal(graphParameterId, Assert.Single(opaqueFunction.Parameters).Id);
        Assert.Equal(
            graphFunction.Id,
            Assert.Single(opaque.MethodProjections).Id);
        Assert.Equal(
            graphFunction.Id,
            Assert.Single(opaque.Profile!.Methods).Id);
        Assert.Equal(
            ScriptFunctionBodyRepresentation.OpaqueCode,
            opaqueFunction.BodyRepresentation);
        Assert.Empty(opaque.Hir!.Types.SelectMany(static type => type.Methods));

        CSharpAnalysisResult restored = Analyze(new CSharpCompilationRequest(
            "FunctionIdentityEntry",
            [new CSharpSourceDocument(Path, graphSource, Assert.Single(opaque.IdentitySidecars))],
            [unrealAssembly],
            runtimeReferencePaths: [unrealAssembly]));
        ScriptFunctionDeclaration restoredFunction = Assert.Single(
            Assert.Single(restored.ScriptAssets!.Assets).Functions);
        Assert.Equal(graphFunction.Id, restoredFunction.Id);
        Assert.Equal(graphParameterId, Assert.Single(restoredFunction.Parameters).Id);
        Assert.Equal(
            graphFunction.Id,
            Assert.Single(restored.Hir!.Types.SelectMany(static type => type.Methods)).Id);
    }

    [Fact]
    public void RefOutFunctionIdsRemainStableAcrossGraphOpaqueGraphWithNonBootstrapSidecar()
    {
        const string path = "Game/RefOutIdentity.cs";
        const string graphSource = """
            using System;
            using Finare;
            using Finare.Unreal;

            namespace Game;

            public sealed class RefOutIdentityEntry : UnrealObjectScript
            {
                [UnrealBlueprintEvent]
                public int Calculate(ref int value, out int doubled)
                {
                    doubled = value + 1;
                    return doubled;
                }
            }
            """;
        const string opaqueSource = """
            using System;
            using Finare;
            using Finare.Unreal;

            namespace Game;

            public sealed class RefOutIdentityEntry : UnrealObjectScript
            {
                [UnrealBlueprintEvent]
                public int Calculate(ref int value, out int doubled)
                {
                    Func<int, int> twice = item => item * 2;
                    doubled = twice(value);
                    return doubled;
                }
            }
            """;
        string unrealAssembly = typeof(UnrealObjectScript).Assembly.Location;
        IdentitySidecarDocument empty = IdentitySidecarDocument.CreateEmpty(path);
        CSharpAnalysisResult bootstrap = Analyze(new CSharpCompilationRequest(
            "RefOutIdentity",
            [new CSharpSourceDocument(path, graphSource, empty)],
            [unrealAssembly],
            runtimeReferencePaths: [unrealAssembly]));
        IdentitySidecarDocument bootstrapSidecar = Assert.Single(bootstrap.IdentitySidecars);
        IdentityEntry bootstrapFunction = Assert.Single(
            bootstrapSidecar.Entries,
            static entry => entry.Kind == "function");
        IdentityEntry[] bootstrapParameters = bootstrapSidecar.Entries
            .Where(static entry => entry.Kind == "parameter")
            .OrderBy(static entry => entry.Anchor.Start)
            .ToArray();
        Assert.Equal(2, bootstrapParameters.Length);
        SemanticId functionId = SemanticId.FromKey("ref-out/non-bootstrap/function");
        SemanticId[] parameterIds = [
            SemanticId.FromKey("ref-out/non-bootstrap/parameter/value"),
            SemanticId.FromKey("ref-out/non-bootstrap/parameter/doubled"),
        ];
        Dictionary<SemanticId, SemanticId> replacements = new()
        {
            [bootstrapFunction.Id] = functionId,
            [bootstrapParameters[0].Id] = parameterIds[0],
            [bootstrapParameters[1].Id] = parameterIds[1],
        };
        IdentitySidecarDocument nonBootstrapSidecar = bootstrapSidecar with
        {
            Entries = bootstrapSidecar.Entries
                .Select(entry => replacements.TryGetValue(entry.Id, out SemanticId replacement)
                    ? entry with { Id = replacement }
                    : entry)
                .ToArray(),
        };

        CSharpCompilationRequest graphRequest = new(
            "RefOutIdentity",
            [new CSharpSourceDocument(path, graphSource, nonBootstrapSidecar)],
            [unrealAssembly],
            runtimeReferencePaths: [unrealAssembly]);
        CSharpAnalysisResult graph = Analyze(graphRequest);
        ScriptFunctionDeclaration graphFunction = Assert.Single(
            Assert.Single(graph.ScriptAssets!.Assets).Functions);
        Assert.Equal(functionId, graphFunction.Id);
        Assert.Equal(parameterIds, graphFunction.Parameters.Select(static parameter => parameter.Id));
        Assert.Equal(functionId, Assert.Single(graph.Hir!.Types.SelectMany(static type => type.Methods)).Id);
        Assert.Equal(functionId, Assert.Single(graph.Profile.Methods).Id);
        Assert.Equal(
            functionId,
            Assert.Single(
                graph.IdentitySidecars.SelectMany(static sidecar => sidecar.Entries),
                static entry => entry.Kind == "function").Id);
        Assert.Equal(
            graph.ScriptAssets!.Assets[0].Id,
            graph.DeclarationOwners[functionId]);

        CSharpCompilationRequest opaqueRequest = new(
            "RefOutIdentity",
            [new CSharpSourceDocument(
                path,
                opaqueSource,
                Assert.Single(graph.IdentitySidecars))],
            [unrealAssembly],
            runtimeReferencePaths: [unrealAssembly]);
        CSharpAnalysisResult opaque = Analyze(opaqueRequest);
        ScriptAssetDeclaration opaqueAsset = Assert.Single(opaque.ScriptAssets!.Assets);
        ScriptFunctionDeclaration opaqueFunction = Assert.Single(opaqueAsset.Functions);
        Assert.Equal(functionId, opaqueFunction.Id);
        Assert.Equal(parameterIds, opaqueFunction.Parameters.Select(static parameter => parameter.Id));
        Assert.Equal(
            [ScriptParameterDirection.InputOutput, ScriptParameterDirection.Output],
            opaqueFunction.Parameters.Select(static parameter => parameter.Direction));
        Assert.Equal(functionId, Assert.Single(opaque.MethodProjections).Id);
        Assert.Equal(functionId, Assert.Single(opaque.Profile.Methods).Id);
        Assert.Equal(
            parameterIds.OrderBy(static id => id.Value),
            opaque.IdentitySidecars
                .SelectMany(static sidecar => sidecar.Entries)
                .Where(static entry => entry.Kind == "parameter")
                .Select(static entry => entry.Id)
                .OrderBy(static id => id.Value));
        Assert.Equal(opaqueAsset.Id, opaque.DeclarationOwners[functionId]);
        Assert.Empty(opaque.Hir!.Types.SelectMany(static type => type.Methods));

        CSharpProgramEmitResult emitted = new CSharpProgramEmitter().Emit(opaqueRequest);
        Assert.True(emitted.Succeeded, string.Join(Environment.NewLine, emitted.Diagnostics));
        CSharpProgramImage opaqueImage = Assert.IsType<CSharpProgramImage>(emitted.Image);
        CSharpMethodSourceMap opaqueMap = Assert.Single(
            opaqueImage.SourceMap.Methods,
            method => method.MethodId == functionId);
        Assert.NotEqual(0, opaqueMap.MetadataToken);
        Assert.NotEmpty(opaqueMap.SequencePoints);
        Assert.Empty(opaqueMap.Nodes);

        CSharpCompilationRequest restoredRequest = new(
            "RefOutIdentity",
            [new CSharpSourceDocument(
                path,
                graphSource,
                Assert.Single(opaque.IdentitySidecars))],
            [unrealAssembly],
            runtimeReferencePaths: [unrealAssembly]);
        CSharpAnalysisResult restored = Analyze(restoredRequest);
        ScriptFunctionDeclaration restoredFunction = Assert.Single(
            Assert.Single(restored.ScriptAssets!.Assets).Functions);
        Assert.Equal(functionId, restoredFunction.Id);
        Assert.Equal(parameterIds, restoredFunction.Parameters.Select(static parameter => parameter.Id));
        Assert.Equal(functionId, Assert.Single(restored.Profile.Methods).Id);
        Assert.Equal(functionId, Assert.Single(
            restored.Hir!.Types.SelectMany(static type => type.Methods)).Id);
        Assert.Equal(
            restored.ScriptAssets!.Assets[0].Id,
            restored.DeclarationOwners[functionId]);

        CSharpProgramEmitResult restoredEmit = new CSharpProgramEmitter().Emit(restoredRequest);
        Assert.True(restoredEmit.Succeeded, string.Join(Environment.NewLine, restoredEmit.Diagnostics));
        CSharpMethodSourceMap restoredMap = Assert.Single(
            Assert.IsType<CSharpProgramImage>(restoredEmit.Image).SourceMap.Methods,
            method => method.MethodId == functionId);
        Assert.NotEqual(0, restoredMap.MetadataToken);
        Assert.NotEmpty(restoredMap.Nodes);
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
