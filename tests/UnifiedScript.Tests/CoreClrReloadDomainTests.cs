using System.Runtime.CompilerServices;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.CSharp;
using UnifiedScript.Runtime;

namespace UnifiedScript.Tests;

public sealed class CoreClrReloadDomainTests
{
    private const string Path = "Tests/Reload/PersistentRules.cs";
    private const string Source = """
        using UnifiedScript;

        namespace Tests.Reload;

        public static class PersistentRules
        {
            [Visual]
            public static int Run(int value)
            {
                int offset = 1;
                return value + offset;
            }
        }
        """;

    [Fact]
    public void Reload_SameRequestUsesCacheButCreatesDistinctGenerationAndRejectsStaleHit()
    {
        CSharpCompilationRequest request = CreatePersistentRequest(Source);
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        HirReturnStatement returnStatement = Assert.Single(
            analysis.Hir!.DescendantsAndSelf().OfType<HirReturnStatement>());
        var cache = new CoreClrProgramImageCache();
        using var domain = new CoreClrReloadDomain(
            cache,
            SemanticId.FromKey("tests-reload-domain"));

        CoreClrReloadResult first = domain.Reload(request);
        var breakpoint = new SemanticBreakpoint(
            SemanticId.FromKey("tests-return-breakpoint"),
            returnStatement.Id);
        BreakpointBindingSnapshot firstBindings = Assert.IsType<BreakpointBindingSnapshot>(
            domain.SetBreakpoint(breakpoint));
        SemanticBreakpointBinding firstBinding = Assert.Single(firstBindings.Bindings);
        CoreClrBreakpointLocation firstLocation = Assert.Single(firstBinding.Locations.Take(1));
        CoreClrBreakpointHit staleHit = CoreClrBreakpointHit.Create(
            first.Generation,
            firstLocation.MetadataToken,
            firstLocation.IlOffset);

        CoreClrReloadResult second = domain.Reload(request);

        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        Assert.Equal(first.Generation.ImageKey, second.Generation.ImageKey);
        Assert.Equal(1, first.Generation.Number);
        Assert.Equal(2, second.Generation.Number);
        Assert.NotEqual(first.Generation.GenerationId, second.Generation.GenerationId);
        Assert.Equal(1, cache.HitCount);
        BreakpointProtocolException stale = Assert.Throws<BreakpointProtocolException>(() =>
            domain.ResolveHit(staleHit));
        Assert.Equal("UDB1002", stale.Code);

        SemanticBreakpointBinding rebound = Assert.Single(second.Breakpoints.Bindings);
        CoreClrBreakpointLocation reboundLocation = Assert.Single(rebound.Locations.Take(1));
        BreakpointHitResolution resolved = domain.ResolveHit(CoreClrBreakpointHit.Create(
            second.Generation,
            reboundLocation.MetadataToken,
            reboundLocation.IlOffset));
        Assert.Equal(returnStatement.Id, Assert.Single(resolved.Matches).NodeId);
        Assert.Equal(firstBinding.MethodId, rebound.MethodId);

        BreakpointBindingSnapshot disabledSnapshot = Assert.IsType<BreakpointBindingSnapshot>(
            domain.SetBreakpoint(breakpoint with { Enabled = false }));
        SemanticBreakpointBinding disabled = Assert.Single(disabledSnapshot.Bindings);
        Assert.Equal(BreakpointBindingState.Disabled, disabled.State);
        Assert.Empty(disabled.Locations);
        BreakpointProtocolException unbound = Assert.Throws<BreakpointProtocolException>(() =>
            domain.ResolveHit(CoreClrBreakpointHit.Create(
                second.Generation,
                reboundLocation.MetadataToken,
                reboundLocation.IlOffset)));
        Assert.Equal("UDB1003", unbound.Code);

        SemanticId methodId = domain.ResolveVisualMethodId("Tests.Reload.PersistentRules.Run");
        ExecutionResult execution = domain.Execute(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("41")]));
        Assert.Equal("42", execution.ReturnValue!.Json);
    }

    [Fact]
    public void Reload_FormattingChangePreservesNodeBreakpointAndMovesPdbSpan()
    {
        CSharpCompilationRequest initialRequest = CreatePersistentRequest(Source);
        CSharpAnalysisResult initialAnalysis = new CSharpSemanticPipeline().Analyze(initialRequest);
        HirReturnStatement originalReturn = Assert.Single(
            initialAnalysis.Hir!.DescendantsAndSelf().OfType<HirReturnStatement>());
        string formattedSource = Source.Replace(
            "        return value + offset;",
            "        // Reload should preserve the return identity.\n\n        return  value + offset;",
            StringComparison.Ordinal);
        IdentitySidecarDocument initialSidecar = Assert.Single(initialAnalysis.IdentitySidecars);
        var formattedRequest = new CSharpCompilationRequest(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(Path, formattedSource, initialSidecar)]);
        using var domain = new CoreClrReloadDomain();

        _ = domain.Reload(initialRequest);
        BreakpointBindingSnapshot before = Assert.IsType<BreakpointBindingSnapshot>(
            domain.SetBreakpoint(new SemanticBreakpoint(
                SemanticId.FromKey("formatting-breakpoint"),
                originalReturn.Id)));
        CoreClrReloadResult afterReload = domain.Reload(formattedRequest);

        SemanticBreakpointBinding after = Assert.Single(afterReload.Breakpoints.Bindings);
        Assert.Equal(BreakpointBindingState.Bound, after.State);
        Assert.Equal(originalReturn.Id, after.NodeId);
        Assert.NotEqual(
            Assert.Single(before.Bindings).NodeSourceSpan!.Start.Line,
            after.NodeSourceSpan!.Start.Line);
        Assert.False(afterReload.CacheHit);
        Assert.NotEqual(
            before.Generation.ImageKey,
            afterReload.Generation.ImageKey);
        Assert.Single(afterReload.IdentitySidecars);
    }

    [Fact]
    public void Reload_DeletedNodeBecomesPendingAndUpdatedSidecarCarriesTombstone()
    {
        CSharpCompilationRequest initialRequest = CreatePersistentRequest(Source);
        CSharpAnalysisResult initialAnalysis = new CSharpSemanticPipeline().Analyze(initialRequest);
        HirVariableDeclarationStatement removedLocal = Assert.Single(
            initialAnalysis.Hir!.DescendantsAndSelf().OfType<HirVariableDeclarationStatement>());
        IdentitySidecarDocument initialSidecar = Assert.Single(initialAnalysis.IdentitySidecars);
        string changedSource = Source
            .Replace("        int offset = 1;\n", string.Empty, StringComparison.Ordinal)
            .Replace("value + offset", "value + 1", StringComparison.Ordinal);
        var changedRequest = new CSharpCompilationRequest(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(Path, changedSource, initialSidecar)]);
        using var domain = new CoreClrReloadDomain();

        _ = domain.Reload(initialRequest);
        _ = domain.SetBreakpoint(new SemanticBreakpoint(
            SemanticId.FromKey("removed-local-breakpoint"),
            removedLocal.Id));
        CoreClrReloadResult changed = domain.Reload(changedRequest);

        SemanticBreakpointBinding binding = Assert.Single(changed.Breakpoints.Bindings);
        Assert.Equal(BreakpointBindingState.Pending, binding.State);
        Assert.Empty(binding.Locations);
        Assert.Contains("not executable", binding.Message, StringComparison.Ordinal);
        IdentitySidecarDocument updatedSidecar = Assert.Single(changed.IdentitySidecars);
        Assert.Contains(updatedSidecar.Tombstones, tombstone => tombstone.Id == removedLocal.Id);

        CoreClrReloadResult restored = domain.Reload(initialRequest);
        SemanticBreakpointBinding restoredBinding = Assert.Single(restored.Breakpoints.Bindings);
        Assert.Equal(BreakpointBindingState.Bound, restoredBinding.State);
        Assert.Equal(removedLocal.Id, restoredBinding.NodeId);
        Assert.NotEmpty(restoredBinding.Locations);
        Assert.True(restored.CacheHit);
    }

    [Fact]
    public void Reload_CompilationFailurePreservesCurrentGenerationBindingsAndExecution()
    {
        CSharpCompilationRequest initialRequest = CreatePersistentRequest(Source);
        CSharpAnalysisResult initialAnalysis = new CSharpSemanticPipeline().Analyze(initialRequest);
        HirReturnStatement returnStatement = Assert.Single(
            initialAnalysis.Hir!.DescendantsAndSelf().OfType<HirReturnStatement>());
        IdentitySidecarDocument sidecar = Assert.Single(initialAnalysis.IdentitySidecars);
        string invalidSource = Source.Replace(
            "value + offset",
            "value + \"invalid\"",
            StringComparison.Ordinal);
        var invalidRequest = new CSharpCompilationRequest(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(Path, invalidSource, sidecar)]);
        using var domain = new CoreClrReloadDomain();
        CoreClrReloadResult active = domain.Reload(initialRequest);
        _ = domain.SetBreakpoint(new SemanticBreakpoint(
            SemanticId.FromKey("transactional-reload-breakpoint"),
            returnStatement.Id));

        _ = Assert.Throws<ExecutionCompilationException>(() => domain.Reload(invalidRequest));

        Assert.Equal(active.Generation, domain.CurrentGeneration);
        BreakpointBindingSnapshot bindings = Assert.IsType<BreakpointBindingSnapshot>(
            domain.CurrentBreakpoints);
        Assert.Equal(active.Generation.GenerationId, bindings.Generation.GenerationId);
        Assert.Equal(BreakpointBindingState.Bound, Assert.Single(bindings.Bindings).State);
        SemanticId methodId = domain.ResolveVisualMethodId("Tests.Reload.PersistentRules.Run");
        ExecutionResult execution = domain.Execute(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("41")]));
        Assert.Equal("42", execution.ReturnValue!.Json);
    }

    [Fact]
    public void Dispose_RejectsFurtherReloadAndClearsActiveState()
    {
        CSharpCompilationRequest request = CreatePersistentRequest(Source);
        var domain = new CoreClrReloadDomain();
        _ = domain.Reload(request);

        domain.Dispose();

        Assert.Null(domain.CurrentGeneration);
        Assert.Null(domain.CurrentBreakpoints);
        Assert.Null(domain.CurrentLoadContextReference);
        Assert.Throws<ObjectDisposedException>(() => domain.Reload(request));
        domain.Dispose();
    }

    [Fact]
    public void Reload_DisposesPreviousCollectibleContext()
    {
        WeakReference previousContext = ReloadAndCapturePreviousContext();

        for (int attempt = 0; attempt < 10 && previousContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(previousContext.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReloadAndCapturePreviousContext()
    {
        CSharpCompilationRequest request = CreatePersistentRequest(Source);
        using var domain = new CoreClrReloadDomain();
        _ = domain.Reload(request);
        WeakReference previous = Assert.IsType<WeakReference>(domain.CurrentLoadContextReference);
        _ = domain.Reload(request);
        return previous;
    }

    private static CSharpCompilationRequest CreatePersistentRequest(string source)
    {
        var initializationRequest = new CSharpCompilationRequest(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(Path, source, IdentitySidecarDocument.CreateEmpty(Path))]);
        CSharpAnalysisResult initialized = new CSharpSemanticPipeline().Analyze(initializationRequest);
        Assert.True(initialized.Succeeded, string.Join(Environment.NewLine, initialized.Diagnostics));
        return new CSharpCompilationRequest(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(Path, source, Assert.Single(initialized.IdentitySidecars))]);
    }
}
