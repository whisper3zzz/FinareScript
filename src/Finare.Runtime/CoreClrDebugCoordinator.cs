using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;
using Finare.Core;
using Finare.Core.Hir;
using Finare.CSharp;

namespace Finare.Runtime;

internal sealed class CoreClrDebugCoordinator : ICoreClrDebugProbeSink
{
    private readonly object gate = new();
    private readonly CoreClrProgramGeneration generation;
    private readonly string programAssemblyName;
    private readonly IReadOnlyDictionary<SemanticId, SourceSpan> nodeSpans;
    private readonly IReadOnlyDictionary<SemanticId, int> methodTokens;
    private readonly Channel<CoreClrDebugPause> pauses = Channel.CreateBounded<CoreClrDebugPause>(
        new BoundedChannelOptions(1)
        {
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private BreakpointBindingSnapshot bindings;
    private PauseState? currentPause;
    private ManualResetEventSlim? resumeSignal;
    private bool pauseRequested;
    private StepState? step;
    private bool aborted;
    private long pauseNumber;

    public CoreClrDebugCoordinator(
        CoreClrProgramGeneration generation,
        long executionId,
        CSharpProgramSourceMap sourceMap,
        HirCompilationUnit hir,
        BreakpointBindingSnapshot bindings)
    {
        this.generation = generation ?? throw new ArgumentNullException(nameof(generation));
        ArgumentNullException.ThrowIfNull(sourceMap);
        programAssemblyName = sourceMap.AssemblyName;
        ArgumentNullException.ThrowIfNull(hir);
        this.bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        ExecutionId = executionId;
        nodeSpans = hir.DescendantsAndSelf()
            .GroupBy(static node => node.Id)
            .ToDictionary(static group => group.Key, static group => group.First().Span);
        methodTokens = sourceMap.Methods.ToDictionary(
            static method => method.MethodId,
            static method => method.MetadataToken);
    }

    public long ExecutionId { get; }

    public void UpdateBindings(BreakpointBindingSnapshot next)
    {
        ArgumentNullException.ThrowIfNull(next);
        lock (gate)
        {
            bindings = next;
        }
    }

    public Task<CoreClrDebugPause> WaitForPauseAsync(CancellationToken cancellationToken) =>
        pauses.Reader.ReadAsync(cancellationToken).AsTask();

    public IReadOnlyList<CoreClrDebugStackFrame> GetStackTrace(SemanticId pauseId)
    {
        lock (gate)
        {
            return GetPause(pauseId).Frames;
        }
    }

    public IReadOnlyList<CoreClrDebugValue> GetLocals(SemanticId pauseId, long frameId)
    {
        lock (gate)
        {
            PauseState pause = GetPause(pauseId);
            return pause.Locals.TryGetValue(frameId, out IReadOnlyList<CoreClrDebugValue>? locals)
                ? locals
                : throw new BreakpointProtocolException(
                    "UDB1007",
                    $"Stack frame '{frameId}' is not available in pause '{pauseId}'.");
        }
    }

    public CoreClrDebugEvaluation Evaluate(SemanticId pauseId, long frameId, string expression)
    {
        lock (gate)
        {
            PauseState pause = GetPause(pauseId);
            IReadOnlyList<CoreClrDebugValue> locals = pause.Locals.TryGetValue(
                frameId,
                out IReadOnlyList<CoreClrDebugValue>? found)
                ? found
                : throw new BreakpointProtocolException(
                    "UDB1007",
                    $"Stack frame '{frameId}' is not available in pause '{pauseId}'.");
            return CoreClrDebugExpressionEvaluator.Evaluate(
                generation,
                pauseId,
                frameId,
                expression,
                locals);
        }
    }

    public void RequestPause()
    {
        lock (gate)
        {
            if (!aborted)
            {
                pauseRequested = true;
            }
        }
    }

    public void Continue(SemanticId pauseId)
    {
        lock (gate)
        {
            _ = GetPause(pauseId);
            step = null;
            ResumeCore();
        }
    }

    public void StepOver(SemanticId pauseId)
    {
        lock (gate)
        {
            PauseState pause = GetPause(pauseId);
            step = new StepState(CoreClrDebugStepKind.Over, pause.Frames.Count);
            ResumeCore();
        }
    }

    public void StepInto(SemanticId pauseId)
    {
        lock (gate)
        {
            PauseState pause = GetPause(pauseId);
            step = new StepState(CoreClrDebugStepKind.Into, pause.Frames.Count);
            ResumeCore();
        }
    }

    public void StepOut(SemanticId pauseId)
    {
        lock (gate)
        {
            PauseState pause = GetPause(pauseId);
            if (pause.Frames.Count <= 1)
            {
                throw new BreakpointProtocolException(
                    "UDB1010",
                    "Step Out requires a caller frame in the current Visual C# stack.");
            }

            step = new StepState(CoreClrDebugStepKind.Out, pause.Frames.Count);
            ResumeCore();
        }
    }

    public void Complete(Exception? error = null)
    {
        ManualResetEventSlim? signal;
        lock (gate)
        {
            aborted = true;
            signal = resumeSignal;
            resumeSignal = null;
            currentPause = null;
        }

        signal?.Set();
        pauses.Writer.TryComplete(error);
    }

    public void Abort() => Complete(new ObjectDisposedException(nameof(CoreClrDebugCoordinator)));

    public void Hit(
        string methodIdText,
        string nodeIdText,
        IReadOnlyList<CoreClrDebugValue> locals,
        IReadOnlyList<CoreClrDebugFrameSnapshot> frameSnapshots)
    {
        if (!SemanticId.TryParse(methodIdText, out SemanticId methodId) ||
            !SemanticId.TryParse(nodeIdText, out SemanticId nodeId))
        {
            throw new BreakpointProtocolException(
                "UDB1008",
                "The emitted CoreCLR debug probe carried an invalid semantic identity.");
        }

        ManualResetEventSlim? signal;
        lock (gate)
        {
            if (aborted)
            {
                return;
            }

            SemanticId[] breakpointIds = bindings.Bindings
                .Where(static binding => binding.State == BreakpointBindingState.Bound)
                .Where(binding => binding.NodeId == nodeId)
                .Select(static binding => binding.BreakpointId)
                .ToArray();
            CoreClrDebugPauseReason? reason = breakpointIds.Length > 0
                ? CoreClrDebugPauseReason.Breakpoint
                : pauseRequested
                    ? CoreClrDebugPauseReason.Pause
                    : null;
            StackSnapshot stack = CaptureStack(methodId, nodeId, locals, frameSnapshots);
            CoreClrDebugStepKind? completedStep = null;
            if (reason is null && step is StepState stepState &&
                ShouldPauseForStep(stepState, stack.Frames.Count))
            {
                reason = CoreClrDebugPauseReason.Step;
                completedStep = stepState.Kind;
            }

            if (reason is null)
            {
                return;
            }

            pauseRequested = false;
            step = null;
            long number = checked(++pauseNumber);
            SemanticId pauseId = SemanticId.FromKey(
                $"coreclr-debug-pause/v0.1|{generation.GenerationId}|{ExecutionId}|{number}");
            var pause = new CoreClrDebugPause(
                CoreClrDebugPause.CurrentSchemaVersion,
                generation,
                ExecutionId,
                pauseId,
                reason.Value,
                completedStep,
                methodId,
                nodeId,
                breakpointIds);
            currentPause = new PauseState(pause, stack.Frames, stack.Locals);
            signal = new ManualResetEventSlim(initialState: false);
            resumeSignal = signal;
            if (!pauses.Writer.TryWrite(pause))
            {
                resumeSignal = null;
                currentPause = null;
                signal.Dispose();
                return;
            }
        }

        signal.Wait();
        signal.Dispose();
    }

    private StackSnapshot CaptureStack(
        SemanticId topMethodId,
        SemanticId topNodeId,
        IReadOnlyList<CoreClrDebugValue> topLocals,
        IReadOnlyList<CoreClrDebugFrameSnapshot> frameSnapshots)
    {
        StackFrame[] runtimeFrames = new StackTrace(fNeedFileInfo: true)
            .GetFrames()
            .Where(frame => string.Equals(
                frame.GetMethod()?.DeclaringType?.Assembly.GetName().Name,
                programAssemblyName,
                StringComparison.Ordinal))
            .ToArray();
        long baseFrameId = checked((pauseNumber + 1) * 1_000_000L);
        var frames = new List<CoreClrDebugStackFrame>(runtimeFrames.Length);
        var locals = new Dictionary<long, IReadOnlyList<CoreClrDebugValue>>();
        int snapshotIndex = 0;
        for (int index = 0; index < runtimeFrames.Length; index++)
        {
            StackFrame frame = runtimeFrames[index];
            MethodBase? method = frame.GetMethod();
            if (method is null)
            {
                continue;
            }

            long frameId = checked(baseFrameId + index + 1);
            CoreClrDebugFrameSnapshot? snapshot = null;
            if (frameSnapshots.Count > snapshotIndex &&
                SemanticId.TryParse(frameSnapshots[snapshotIndex].MethodId, out SemanticId snapshotMethodId) &&
                methodTokens.TryGetValue(snapshotMethodId, out int snapshotToken) &&
                snapshotToken == method.MetadataToken)
            {
                snapshot = frameSnapshots[snapshotIndex++];
            }

            SemanticId frameMethodId = snapshot is not null &&
                SemanticId.TryParse(snapshot.MethodId, out SemanticId snapshotMethodIdValue)
                    ? snapshotMethodIdValue
                    : index == 0 ? topMethodId : default;
            SemanticId spanNodeId = snapshot is not null &&
                SemanticId.TryParse(snapshot.NodeId, out SemanticId snapshotNodeId)
                    ? snapshotNodeId
                    : index == 0 ? topNodeId : default;
            SourceSpan? span = !spanNodeId.IsEmpty &&
                nodeSpans.TryGetValue(spanNodeId, out SourceSpan? found)
                    ? found
                    : null;
            string typeName = method.DeclaringType is null
                ? "<global>"
                : ExecutionValueCodec.GetMetadataName(method.DeclaringType);
            bool hasLocals = snapshot is not null || index == 0;
            frames.Add(new CoreClrDebugStackFrame(
                frameId,
                index,
                frameMethodId.IsEmpty ? null : frameMethodId,
                spanNodeId.IsEmpty ? null : spanNodeId,
                $"{typeName}.{method.Name}",
                method.MetadataToken,
                frame.GetILOffset(),
                span,
                hasLocals));
            locals.Add(
                frameId,
                snapshot?.Locals.ToArray() ?? (index == 0 ? topLocals.ToArray() : []));
        }

        return new StackSnapshot(frames, locals);
    }

    private static bool ShouldPauseForStep(StepState stepState, int currentDepth) =>
        stepState.Kind switch
        {
            CoreClrDebugStepKind.Into => true,
            CoreClrDebugStepKind.Over => currentDepth <= stepState.Depth,
            CoreClrDebugStepKind.Out => currentDepth < stepState.Depth,
            _ => throw new ArgumentOutOfRangeException(nameof(stepState)),
        };

    private PauseState GetPause(SemanticId pauseId)
    {
        if (pauseId.IsEmpty || currentPause is null || currentPause.Pause.PauseId != pauseId)
        {
            throw new BreakpointProtocolException(
                "UDB1006",
                $"Pause '{pauseId}' is stale or is not the current suspended execution.");
        }

        return currentPause;
    }

    private void ResumeCore()
    {
        ManualResetEventSlim signal = resumeSignal ?? throw new BreakpointProtocolException(
            "UDB1006",
            "The debug execution is not currently suspended.");
        resumeSignal = null;
        currentPause = null;
        signal.Set();
    }

    private sealed record PauseState(
        CoreClrDebugPause Pause,
        IReadOnlyList<CoreClrDebugStackFrame> Frames,
        IReadOnlyDictionary<long, IReadOnlyList<CoreClrDebugValue>> Locals);

    private sealed record StackSnapshot(
        IReadOnlyList<CoreClrDebugStackFrame> Frames,
        IReadOnlyDictionary<long, IReadOnlyList<CoreClrDebugValue>> Locals);

    private sealed record StepState(CoreClrDebugStepKind Kind, int Depth);
}
