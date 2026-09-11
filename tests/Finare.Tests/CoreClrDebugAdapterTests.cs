using System.Runtime.CompilerServices;
using Finare.Core;
using Finare.Core.Hir;
using Finare.CSharp;
using Finare.Runtime;

namespace Finare.Tests;

[Trait("Category", "Runtime")]
public sealed class CoreClrDebugAdapterTests
{
    private const string Path = "Tests/Debug/Rules.cs";
    private const string Source = """
        using Finare;

        namespace Tests.Debug;

        public static class Rules
        {
            [Visual]
            public static int Run(int value)
            {
                int offset = 1;
                int result = value + offset;
                return result;
            }
        }
        """;

    private const string ScriptAssetSource = """
        using System;
        using Finare;

        namespace Tests.Debug;

        [ScriptAsset("Tests/DamagePolicy")]
        public sealed class DamagePolicy
        {
            public int BaseDamage { get; set; } = 20;

            public float CriticalMultiplier { get; set; } = 2.0f;

            public int MaxDamage { get; set; } = 999;

            [Visual]
            public float ApplyDamage(int incomingDamage, bool critical)
            {
                float scaled = incomingDamage * BaseDamage;
                if (critical)
                {
                    scaled *= CriticalMultiplier;
                }

                scaled = Math.Clamp(scaled, 0f, MaxDamage);
                return scaled;
            }
        }
        """;

    [Fact]
    public async Task OutParametersAreCapturedOnlyAfterDefiniteAssignment()
    {
        const string source = """
            using Finare;
            namespace Tests.Debug;
            public static class OutputRules
            {
                [Visual]
                public static int Assign(int input, out int result)
                {
                    result = input + 1;
                    return result;
                }
                [Visual]
                public static int Run(int input)
                {
                    return SourceRun(input);
                }
                private static int SourceRun(int input)
                {
                    int output = 0;
                    return Assign(input, out output);
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(source, "Tests/Debug/OutputRules.cs");
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(analysis.Succeeded, string.Join("\n", analysis.Diagnostics));
        HirExpressionStatement assignment = Assert.Single(analysis.Hir!.DescendantsAndSelf().OfType<HirExpressionStatement>());
        using var adapter = new CoreClrDebugAdapter(SemanticId.FromKey("out-parameter-debug"));
        CoreClrDebugReloadResult reload = adapter.Reload(request);
        adapter.SetBreakpoint(new SemanticBreakpoint(SemanticId.FromKey("before-out-write"), assignment.Id));
        CoreClrDebugExecution execution = adapter.Start(new ExecutionInvocation(
            adapter.ResolveVisualMethodId("Tests.Debug.OutputRules.Run"), [new ExecutionArgument("41")]));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CoreClrDebugPause before = await adapter.WaitForPauseAsync(execution.ExecutionId, timeout.Token);
        CoreClrDebugStackFrame frame = adapter.GetStackTrace(reload.Generation.GenerationId, before.PauseId)[0];
        Assert.DoesNotContain(adapter.GetLocals(reload.Generation.GenerationId, before.PauseId, frame.FrameId), value => value.Name == "result");
        adapter.StepOver(reload.Generation.GenerationId, before.PauseId);
        CoreClrDebugPause after = await adapter.WaitForPauseAsync(execution.ExecutionId, timeout.Token);
        CoreClrDebugStackFrame assignedFrame = adapter.GetStackTrace(reload.Generation.GenerationId, after.PauseId)[0];
        Assert.Equal("42", adapter.GetLocals(reload.Generation.GenerationId, after.PauseId, assignedFrame.FrameId).Single(value => value.Name == "result").Display);
        adapter.Continue(reload.Generation.GenerationId, after.PauseId);
        Assert.Equal("42", (await adapter.WaitForCompletionAsync(execution.ExecutionId, timeout.Token)).ReturnValue!.Json);
    }

    [Fact]
    public async Task PublicationFailureKeepsTheOriginalLoadedProgramAndGeneration()
    {
        using var adapter = new CoreClrDebugAdapter();
        CoreClrDebugReloadResult original = adapter.Reload(CSharpCompilationRequest.Single(Source, Path));
        WeakReference? originalContext = adapter.CurrentLoadContextReference;
        Assert.Throws<IOException>(() => adapter.Reload(CSharpCompilationRequest.Single(
            Source.Replace("offset = 1", "offset = 9", StringComparison.Ordinal), Path),
            () => throw new IOException("Cannot publish the new artifacts.")));
        Assert.Equal(original.Generation, adapter.CurrentGeneration);
        Assert.Same(originalContext, adapter.CurrentLoadContextReference);
        CoreClrDebugExecution execution = adapter.Start(new ExecutionInvocation(
            adapter.ResolveVisualMethodId("Tests.Debug.Rules.Run"), [new ExecutionArgument("41")]));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal("42", (await adapter.WaitForCompletionAsync(execution.ExecutionId, timeout.Token)).ReturnValue!.Json);
    }

    [Fact]
    public void PreparedReloadKeepsDomainGenerationAndBreakpointStateUntilCommit()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, Path);
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        HirVariableDeclarationStatement result = analysis.Hir!
            .DescendantsAndSelf()
            .OfType<HirVariableDeclarationStatement>()
            .Single(statement => statement.Name == "result");
        using var adapter = new CoreClrDebugAdapter(SemanticId.FromKey("prepared-reload-test"));
        CoreClrDebugReloadResult original = adapter.Reload(request);
        BreakpointBindingSnapshot originalBindings = adapter.SetBreakpoint(
            new SemanticBreakpoint(SemanticId.FromKey("prepared-result"), result.Id));

        using (CoreClrDebugPreparedReload discarded = adapter.PrepareReload(
                   CSharpCompilationRequest.Single(Source.Replace("offset = 1", "offset = 2", StringComparison.Ordinal), Path)))
        {
            Assert.Equal(original.Generation.DomainId, discarded.Result.Generation.DomainId);
            Assert.Equal(original.Generation.Number + 1, discarded.Result.Generation.Number);
            Assert.Equal(original.Generation, adapter.CurrentGeneration);
            Assert.Equal(originalBindings, adapter.CurrentBreakpoints);
            Assert.Throws<InvalidOperationException>(() => adapter.SetBreakpoint(
                new SemanticBreakpoint(SemanticId.FromKey("prepared-result-2"), result.Id)));
        }
        Assert.Equal(original.Generation, adapter.CurrentGeneration);
        Assert.Equal(originalBindings, adapter.CurrentBreakpoints);

        using (CoreClrDebugPreparedReload committed = adapter.PrepareReload(
                   CSharpCompilationRequest.Single(Source.Replace("offset = 1", "offset = 3", StringComparison.Ordinal), Path)))
        {
            committed.Commit();
            Assert.Equal(original.Generation.DomainId, adapter.CurrentGeneration!.DomainId);
            Assert.Equal(original.Generation.Number + 1, adapter.CurrentGeneration.Number);
            BreakpointBindingSnapshot nextBindings = adapter.SetBreakpoint(
                new SemanticBreakpoint(SemanticId.FromKey("prepared-result-3"), result.Id));
            Assert.Equal(adapter.CurrentGeneration, nextBindings.Generation);
            Assert.True(adapter.RemoveBreakpoint(SemanticId.FromKey("prepared-result-3")));
        }
    }

    [Fact]
    public async Task Breakpoint_ReallySuspendsCoreClrAndExposesStackAndLocalsThenSteps()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, Path);
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        HirVariableDeclarationStatement resultDeclaration = analysis.Hir!
            .DescendantsAndSelf()
            .OfType<HirVariableDeclarationStatement>()
            .Single(statement => statement.Name == "result");
        HirReturnStatement returnStatement = Assert.Single(
            analysis.Hir!.DescendantsAndSelf().OfType<HirReturnStatement>());
        using var adapter = new CoreClrDebugAdapter(SemanticId.FromKey("debug-adapter-test"));
        CoreClrDebugReloadResult reload = adapter.Reload(request);
        BreakpointBindingSnapshot bindings = adapter.SetBreakpoint(new SemanticBreakpoint(
            SemanticId.FromKey("debug-result-breakpoint"),
            resultDeclaration.Id));
        Assert.Equal(BreakpointBindingState.Bound, Assert.Single(bindings.Bindings).State);
        SemanticId methodId = adapter.ResolveVisualMethodId("Tests.Debug.Rules.Run");

        CoreClrDebugExecution execution = adapter.Start(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("41")]));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CoreClrDebugPause first = await adapter.WaitForPauseAsync(
            execution.ExecutionId,
            timeout.Token);
        Task<ExecutionResult> completion = adapter.WaitForCompletionAsync(
            execution.ExecutionId,
            timeout.Token);

        Assert.Equal(CoreClrDebugPauseReason.Breakpoint, first.Reason);
        Assert.Equal(resultDeclaration.Id, first.NodeId);
        Assert.False(completion.IsCompleted);
        CoreClrDebugStackFrame top = Assert.Single(
            adapter.GetStackTrace(reload.Generation.GenerationId, first.PauseId));
        Assert.Equal("Tests.Debug.Rules.Run", top.MethodMetadataName);
        Assert.Equal(resultDeclaration.Span, top.SourceSpan);
        IReadOnlyList<CoreClrDebugValue> locals = adapter.GetLocals(
            reload.Generation.GenerationId,
            first.PauseId,
            top.FrameId);
        Assert.Equal("1", locals.Single(local => local.Name == "offset").Display);
        Assert.Equal("41", locals.Single(local => local.Name == "value").Display);

        adapter.StepOver(reload.Generation.GenerationId, first.PauseId);
        CoreClrDebugPause second = await adapter.WaitForPauseAsync(
            execution.ExecutionId,
            timeout.Token);

        Assert.Equal(CoreClrDebugPauseReason.Step, second.Reason);
        Assert.Equal(CoreClrDebugStepKind.Over, second.StepKind);
        Assert.Equal(returnStatement.Id, second.NodeId);
        CoreClrDebugStackFrame secondTop = Assert.Single(
            adapter.GetStackTrace(reload.Generation.GenerationId, second.PauseId));
        IReadOnlyList<CoreClrDebugValue> secondLocals = adapter.GetLocals(
            reload.Generation.GenerationId,
            second.PauseId,
            secondTop.FrameId);
        Assert.Equal("42", secondLocals.Single(local => local.Name == "result").Display);

        adapter.Continue(reload.Generation.GenerationId, second.PauseId);
        ExecutionResult result = await completion;
        Assert.Equal("42", result.ReturnValue!.Json);
    }

    [Fact]
    public async Task PauseRequestStopsAtNextProbeAndReloadRejectsStaleGeneration()
    {
        using var transport = new CoreClrDebugLoopbackTransport(
            new CoreClrDebugAdapter(SemanticId.FromKey("debug-loopback-test")));
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, Path);
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        HirVariableDeclarationStatement offsetDeclaration = analysis.Hir!
            .DescendantsAndSelf()
            .OfType<HirVariableDeclarationStatement>()
            .Single(statement => statement.Name == "offset");
        CoreClrDebugReloadResult firstReload = transport.Reload(request);
        SemanticId offsetBreakpointId = SemanticId.FromKey("debug-offset-breakpoint");
        BreakpointBindingSnapshot initialBindings = transport.SetBreakpoint(
            new SemanticBreakpoint(offsetBreakpointId, offsetDeclaration.Id));
        Assert.Equal(BreakpointBindingState.Bound, Assert.Single(initialBindings.Bindings).State);
        SemanticId methodId = transport.ResolveVisualMethodId("Tests.Debug.Rules.Run");
        CoreClrDebugExecution execution = transport.Start(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("10")]));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CoreClrDebugPause breakpointPause = await transport.WaitForPauseAsync(
            execution.ExecutionId,
            timeout.Token);
        Assert.Equal(CoreClrDebugPauseReason.Breakpoint, breakpointPause.Reason);
        Assert.Equal(offsetDeclaration.Id, breakpointPause.NodeId);
        Assert.True(transport.RemoveBreakpoint(offsetBreakpointId));

        // Queue the explicit pause while the execution is suspended. Continue
        // then releases the first pause and the next probe deterministically
        // consumes the request before this tiny method can finish.
        transport.Pause(firstReload.Generation.GenerationId, execution.ExecutionId);
        transport.Continue(firstReload.Generation.GenerationId, breakpointPause.PauseId);
        CoreClrDebugPause pause = await transport.WaitForPauseAsync(
            execution.ExecutionId,
            timeout.Token);

        Assert.Equal(CoreClrDebugPauseReason.Pause, pause.Reason);
        BreakpointProtocolException activeReload = Assert.Throws<BreakpointProtocolException>(() =>
            transport.Reload(CSharpCompilationRequest.Single(Source, Path)));
        Assert.Equal("UDB1004", activeReload.Code);

        transport.Continue(firstReload.Generation.GenerationId, pause.PauseId);
        ExecutionResult result = await transport.WaitForCompletionAsync(
            execution.ExecutionId,
            timeout.Token);
        Assert.Equal("11", result.ReturnValue!.Json);
        CoreClrDebugReloadResult secondReload = transport.Reload(
            CSharpCompilationRequest.Single(Source, Path));
        Assert.Equal(2, secondReload.Generation.Number);
        BreakpointProtocolException stale = Assert.Throws<BreakpointProtocolException>(() =>
            transport.Pause(firstReload.Generation.GenerationId, execution.ExecutionId));
        Assert.Equal("UDB1002", stale.Code);
    }

    [Fact]
    public async Task ScriptAssetReceiverPausesOnItsRealAssignmentAndExposesLiveLocals()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            ScriptAssetSource,
            "Tests/Debug/DamagePolicy.cs");
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        HirExpressionStatement clampAssignment = analysis.Hir!
            .DescendantsAndSelf()
            .OfType<HirExpressionStatement>()
            .Single(statement => statement.Span.Start.Line == 24);
        Assert.IsType<HirAssignmentExpression>(clampAssignment.Expression);
        ScriptAssetDeclaration asset = Assert.Single(analysis.ScriptAssets!.Assets);
        using var transport = new CoreClrDebugLoopbackTransport();
        CoreClrDebugReloadResult reload = transport.Reload(request);
        _ = transport.SetBreakpoint(new SemanticBreakpoint(
            SemanticId.FromKey("script-asset-clamp-breakpoint"),
            clampAssignment.Id));
        SemanticId methodId = transport.ResolveVisualMethodId(
            "Tests.Debug.DamagePolicy.ApplyDamage");

        CoreClrDebugExecution execution = transport.Start(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("12"), new ExecutionArgument("true")],
            new ScriptAssetReceiver(asset.Id)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CoreClrDebugPause pause = await transport.WaitForPauseAsync(
            execution.ExecutionId,
            timeout.Token);
        CoreClrDebugStackFrame frame = Assert.Single(transport.GetStackTrace(
            reload.Generation.GenerationId,
            pause.PauseId));
        IReadOnlyList<CoreClrDebugValue> locals = transport.GetLocals(
            reload.Generation.GenerationId,
            pause.PauseId,
            frame.FrameId);

        Assert.Equal(CoreClrDebugPauseReason.Breakpoint, pause.Reason);
        Assert.Equal(clampAssignment.Id, pause.NodeId);
        Assert.Equal("12", locals.Single(local => local.Name == "incomingDamage").Display);
        Assert.Equal("true", locals.Single(local => local.Name == "critical").Display);
        Assert.Equal("480", locals.Single(local => local.Name == "scaled").Display);

        transport.Continue(reload.Generation.GenerationId, pause.PauseId);
        ExecutionResult result = await transport.WaitForCompletionAsync(
            execution.ExecutionId,
            timeout.Token);
        Assert.Equal("480", result.ReturnValue!.Json);
    }

    [Fact]
    public void ExpressionBreakpointIsPendingBecauseCooperativeBackendProbesStatements()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, Path);
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        HirBinaryExpression expression = Assert.Single(
            analysis.Hir!.DescendantsAndSelf().OfType<HirBinaryExpression>());
        using var adapter = new CoreClrDebugAdapter();
        _ = adapter.Reload(request);

        BreakpointBindingSnapshot bindings = adapter.SetBreakpoint(new SemanticBreakpoint(
            SemanticId.FromKey("debug-expression-breakpoint"),
            expression.Id));

        SemanticBreakpointBinding binding = Assert.Single(bindings.Bindings);
        Assert.Equal(BreakpointBindingState.Pending, binding.State);
        Assert.Contains("statement nodes", binding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureCreatesBoundedCycleSafeValueTreeWithoutObjectReferences()
    {
        var root = new DebugNode { Value = 7 };
        root.Next = root;

        CoreClrDebugValue captured = CoreClrDebugProbe.Capture("root", root);

        Assert.Equal(CoreClrDebugValueKind.Object, captured.Kind);
        Assert.Equal("7", captured.Children.Single(child => child.Name == "Value").Display);
        CoreClrDebugValue next = captured.Children.Single(child => child.Name == "Next");
        Assert.Equal(CoreClrDebugValueKind.Cycle, next.Kind);
        Assert.Equal("<cycle>", next.Display);
    }

    [Fact]
    public async Task StepOverSkipsProbesInCalledMethodWhileBreakpointStillShowsBothFrames()
    {
        const string nestedSource = """
            using Finare;

            namespace Tests.Debug;

            public static class NestedRules
            {
                [Visual]
                public static int Caller(int value)
                {
                    int result = Callee(value);
                    return result;
                }

                [Visual]
                public static int Callee(int value)
                {
                    int result = value + 1;
                    return result;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(nestedSource, Path);
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration caller = analysis.Hir!.Types
            .SelectMany(static type => type.Methods)
            .Single(static method => method.Name == "Caller");
        HirMethodDeclaration callee = analysis.Hir.Types
            .SelectMany(static type => type.Methods)
            .Single(static method => method.Name == "Callee");
        HirVariableDeclarationStatement callerCall = Assert.IsType<HirVariableDeclarationStatement>(
            caller.Body.Statements[0]);
        HirVariableDeclarationStatement calleeResult = Assert.IsType<HirVariableDeclarationStatement>(
            callee.Body.Statements[0]);
        using var adapter = new CoreClrDebugAdapter();
        CoreClrDebugReloadResult reload = adapter.Reload(request);
        SemanticId callerBreakpointId = SemanticId.FromKey("caller-call-breakpoint");
        _ = adapter.SetBreakpoint(new SemanticBreakpoint(
            callerBreakpointId,
            callerCall.Id));
        SemanticId methodId = adapter.ResolveVisualMethodId("Tests.Debug.NestedRules.Caller");
        CoreClrDebugExecution execution = adapter.Start(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("41")]));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CoreClrDebugPause callerPause = await adapter.WaitForPauseAsync(
            execution.ExecutionId,
            timeout.Token);

        adapter.StepOver(reload.Generation.GenerationId, callerPause.PauseId);
        CoreClrDebugPause afterCall = await adapter.WaitForPauseAsync(
            execution.ExecutionId,
            timeout.Token);

        Assert.Equal(CoreClrDebugPauseReason.Step, afterCall.Reason);
        Assert.Equal(Assert.IsType<HirReturnStatement>(caller.Body.Statements[1]).Id, afterCall.NodeId);
        Assert.NotEqual(calleeResult.Id, afterCall.NodeId);
        adapter.Continue(reload.Generation.GenerationId, afterCall.PauseId);
        Assert.Equal(
            "42",
            (await adapter.WaitForCompletionAsync(execution.ExecutionId, timeout.Token))
                .ReturnValue!.Json);

        Assert.True(adapter.RemoveBreakpoint(callerBreakpointId));
        SemanticId calleeBreakpointId = SemanticId.FromKey("callee-result-breakpoint");
        _ = adapter.SetBreakpoint(new SemanticBreakpoint(
            calleeBreakpointId,
            calleeResult.Id));
        CoreClrDebugExecution nestedExecution = adapter.Start(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("1")]));
        CoreClrDebugPause nestedPause = await adapter.WaitForPauseAsync(
            nestedExecution.ExecutionId,
            timeout.Token);
        IReadOnlyList<CoreClrDebugStackFrame> frames = adapter.GetStackTrace(
            reload.Generation.GenerationId,
            nestedPause.PauseId);

        Assert.Equal(2, frames.Count);
        Assert.Equal("Tests.Debug.NestedRules.Callee", frames[0].MethodMetadataName);
        Assert.Equal("Tests.Debug.NestedRules.Caller", frames[1].MethodMetadataName);
        Assert.Equal(calleeResult.Id, frames[0].NodeId);
        Assert.Equal(callerCall.Id, frames[1].NodeId);
        Assert.Equal(callerCall.Span, frames[1].SourceSpan);
        Assert.True(frames[0].HasLocals);
        Assert.True(frames[1].HasLocals);
        IReadOnlyList<CoreClrDebugValue> callerLocals = adapter.GetLocals(
            reload.Generation.GenerationId,
            nestedPause.PauseId,
            frames[1].FrameId);
        Assert.Equal("1", callerLocals.Single(local => local.Name == "value").Display);
        CoreClrDebugEvaluation evaluation = adapter.Evaluate(
            reload.Generation.GenerationId,
            nestedPause.PauseId,
            frames[1].FrameId,
            "value");
        Assert.Equal(CoreClrDebugEvaluation.CurrentSchemaVersion, evaluation.SchemaVersion);
        Assert.Equal("1", evaluation.Value.Display);
        BreakpointProtocolException invalidWatch = Assert.Throws<BreakpointProtocolException>(() =>
            adapter.Evaluate(
                reload.Generation.GenerationId,
                nestedPause.PauseId,
                frames[1].FrameId,
                "value + 1"));
        Assert.Equal("UDB1009", invalidWatch.Code);
        adapter.Continue(reload.Generation.GenerationId, nestedPause.PauseId);
        _ = await adapter.WaitForCompletionAsync(nestedExecution.ExecutionId, timeout.Token);

        Assert.True(adapter.RemoveBreakpoint(calleeBreakpointId));
        _ = adapter.SetBreakpoint(new SemanticBreakpoint(callerBreakpointId, callerCall.Id));
        CoreClrDebugExecution steppedExecution = adapter.Start(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("9")]));
        CoreClrDebugPause beforeCall = await adapter.WaitForPauseAsync(
            steppedExecution.ExecutionId,
            timeout.Token);
        adapter.StepInto(reload.Generation.GenerationId, beforeCall.PauseId);
        CoreClrDebugPause inCallee = await adapter.WaitForPauseAsync(
            steppedExecution.ExecutionId,
            timeout.Token);
        Assert.Equal(CoreClrDebugPauseReason.Step, inCallee.Reason);
        Assert.Equal(CoreClrDebugStepKind.Into, inCallee.StepKind);
        Assert.Equal(calleeResult.Id, inCallee.NodeId);
        Assert.Equal(2, adapter.GetStackTrace(
            reload.Generation.GenerationId,
            inCallee.PauseId).Count);

        adapter.StepOut(reload.Generation.GenerationId, inCallee.PauseId);
        CoreClrDebugPause afterCallee = await adapter.WaitForPauseAsync(
            steppedExecution.ExecutionId,
            timeout.Token);
        Assert.Equal(CoreClrDebugPauseReason.Step, afterCallee.Reason);
        Assert.Equal(CoreClrDebugStepKind.Out, afterCallee.StepKind);
        Assert.Equal(
            Assert.IsType<HirReturnStatement>(caller.Body.Statements[1]).Id,
            afterCallee.NodeId);
        adapter.Continue(reload.Generation.GenerationId, afterCallee.PauseId);
        Assert.Equal(
            "10",
            (await adapter.WaitForCompletionAsync(steppedExecution.ExecutionId, timeout.Token))
                .ReturnValue!.Json);
    }

    [Fact]
    public void DisposedDebugAdapterReleasesCollectibleContextAfterCapturedSourceEnum()
    {
        WeakReference loadContext = CaptureSourceEnumAndDispose();

        for (int attempt = 0; attempt < 10 && loadContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(loadContext.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureSourceEnumAndDispose()
    {
        const string enumSource = """
            using Finare;

            namespace Tests.Debug;

            public enum Mode
            {
                Off,
                On,
            }

            public static class EnumRules
            {
                [Visual]
                public static Mode Echo(Mode value)
                {
                    return value;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(enumSource, Path);
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        HirReturnStatement returnStatement = Assert.Single(
            analysis.Hir!.DescendantsAndSelf().OfType<HirReturnStatement>());
        var adapter = new CoreClrDebugAdapter();
        CoreClrDebugReloadResult reload = adapter.Reload(request);
        WeakReference loadContext = Assert.IsType<WeakReference>(
            adapter.CurrentLoadContextReference);
        _ = adapter.SetBreakpoint(new SemanticBreakpoint(
            SemanticId.FromKey("enum-return-breakpoint"),
            returnStatement.Id));
        SemanticId methodId = adapter.ResolveVisualMethodId("Tests.Debug.EnumRules.Echo");
        CoreClrDebugExecution execution = adapter.Start(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("\"On\"")]));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CoreClrDebugPause pause = adapter.WaitForPauseAsync(execution.ExecutionId, timeout.Token)
            .GetAwaiter()
            .GetResult();
        CoreClrDebugStackFrame frame = Assert.Single(adapter.GetStackTrace(
            reload.Generation.GenerationId,
            pause.PauseId));
        CoreClrDebugValue value = Assert.Single(adapter.GetLocals(
            reload.Generation.GenerationId,
            pause.PauseId,
            frame.FrameId));
        Assert.Equal("\"On\"", value.Display);
        adapter.Continue(reload.Generation.GenerationId, pause.PauseId);
        _ = adapter.WaitForCompletionAsync(execution.ExecutionId, timeout.Token)
            .GetAwaiter()
            .GetResult();
        adapter.Dispose();
        return loadContext;
    }

    private sealed class DebugNode
    {
        public DebugNode? Next;

        public int Value;
    }
}
