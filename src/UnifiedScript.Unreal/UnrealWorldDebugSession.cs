using System.Text.Json;
using UnifiedScript.Core;
using UnifiedScript.Runtime;

namespace UnifiedScript.Unreal;

public sealed record UnrealWorldDebugConfiguration(
    string SchemaVersion,
    int RuntimeGeneration,
    CoreClrProgramGeneration Generation,
    IReadOnlyList<SemanticId> BreakpointNodeIds)
{
    public const string CurrentSchemaVersion = "unifiedscript.unreal-debug-config/v0.1";
}

public enum UnrealWorldDebugControl
{
    Pause = 1,
    Continue = 2,
    StepInto = 3,
    StepOver = 4,
    StepOut = 5,
}

internal sealed record UnrealWorldDebugFrameLocals(
    long FrameId,
    IReadOnlyList<CoreClrDebugValue> Locals);

internal sealed record UnrealWorldDebugSessionSnapshot(
    string SchemaVersion,
    string State,
    CoreClrProgramGeneration Generation,
    long ExecutionId,
    CoreClrDebugPause? Pause,
    IReadOnlyList<CoreClrDebugStackFrame> Frames,
    IReadOnlyList<CoreClrDebugValue> Locals,
    IReadOnlyList<UnrealWorldDebugFrameLocals> FrameLocals,
    ExecutionResult? Result,
    IReadOnlyList<SemanticBreakpointBinding> Breakpoints,
    string? RuntimeDiagnostic)
{
    public const string CurrentSchemaVersion = "unifiedscript.debug-session/v0.2";
}

internal sealed class UnrealWorldDebugController : IDisposable
{
    private readonly object gate = new();
    private readonly ulong worldHostId;
    private readonly ulong instanceId;
    private UnrealWorldDebugConfiguration? configuration;
    private UnrealWorldDebugExecution? execution;
    private UnrealWorldDebugSessionSnapshot? snapshot;
    private long executionNumber;
    private bool disposed;

    public UnrealWorldDebugController(ulong worldHostId, ulong instanceId)
    {
        this.worldHostId = worldHostId;
        this.instanceId = instanceId;
    }

    public bool IsConfigured
    {
        get
        {
            lock (gate)
            {
                return configuration is not null;
            }
        }
    }

    public bool IsActive
    {
        get
        {
            lock (gate)
            {
                return execution is { IsCompleted: false };
            }
        }
    }

    public void Configure(string json, int currentRuntimeGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        UnrealWorldDebugConfiguration candidate = JsonSerializer.Deserialize<UnrealWorldDebugConfiguration>(
                json,
                UnifiedJson.CreateOptions(writeIndented: false)) ??
            throw new JsonException("The live World debug configuration was empty.");
        if (candidate.SchemaVersion != UnrealWorldDebugConfiguration.CurrentSchemaVersion ||
            candidate.RuntimeGeneration != currentRuntimeGeneration ||
            candidate.RuntimeGeneration < 1 ||
            candidate.Generation is null ||
            candidate.Generation.GenerationId.IsEmpty ||
            candidate.Generation.SourceCompilationId.IsEmpty ||
            candidate.Generation.SemanticConfigurationId.IsEmpty ||
            candidate.BreakpointNodeIds is null ||
            candidate.BreakpointNodeIds.Any(static nodeId => nodeId.IsEmpty) ||
            candidate.BreakpointNodeIds.Distinct().Count() != candidate.BreakpointNodeIds.Count ||
            candidate.BreakpointNodeIds.Count > 4096)
        {
            throw new JsonException(
                "The live World debug configuration is stale, incomplete, or exceeds its breakpoint bound.");
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (execution is { IsCompleted: false })
            {
                throw new InvalidOperationException(
                    "A live World debug execution must finish before it can be reconfigured.");
            }
            configuration = candidate with
            {
                BreakpointNodeIds = candidate.BreakpointNodeIds.ToArray(),
            };
            snapshot = null;
        }
    }

    public void BeginTick(
        int runtimeGeneration,
        float deltaSeconds,
        Action<float> tick,
        Func<Exception, string> captureFailure)
    {
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentNullException.ThrowIfNull(captureFailure);
        UnrealWorldDebugExecution next;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            UnrealWorldDebugConfiguration current = configuration ??
                throw new InvalidOperationException("Live World semantic debugging is not configured.");
            if (current.RuntimeGeneration != runtimeGeneration)
            {
                throw new InvalidOperationException(
                    "The live World debug configuration belongs to a stale script generation.");
            }
            if (execution is { IsCompleted: false })
            {
                throw new InvalidOperationException(
                    "The previous live World debug execution is still running or suspended.");
            }

            long executionId = checked(++executionNumber);
            next = new UnrealWorldDebugExecution(
                worldHostId,
                instanceId,
                current,
                executionId,
                PublishSnapshot);
            execution = next;
            snapshot = next.CreateRunningSnapshot();
        }

        next.Start(deltaSeconds, tick, captureFailure);
    }

    public void Control(UnrealWorldDebugControl control, string? pauseId)
    {
        UnrealWorldDebugExecution current;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            current = execution ??
                throw new InvalidOperationException("There is no live World debug execution.");
        }
        current.Control(control, pauseId);
    }

    public string GetSnapshotJson()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            UnrealWorldDebugSessionSnapshot current = snapshot ??
                throw new InvalidOperationException(
                    "The live World debug session has not started an execution.");
            return JsonSerializer.Serialize(current, UnifiedJson.CreateOptions(writeIndented: false));
        }
    }

    public void Disable()
    {
        UnrealWorldDebugExecution? active;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            configuration = null;
            active = execution;
        }
        active?.Abort();
    }

    public void Dispose()
    {
        UnrealWorldDebugExecution? active;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            configuration = null;
            active = execution;
        }
        active?.Abort();
        active?.WaitForCompletion();
    }

    private void PublishSnapshot(
        UnrealWorldDebugExecution source,
        UnrealWorldDebugSessionSnapshot value)
    {
        lock (gate)
        {
            if (!disposed && ReferenceEquals(execution, source))
            {
                snapshot = value;
            }
        }
    }

    private sealed class UnrealWorldDebugExecution : ICoreClrDebugProbeSink
    {
        private readonly object executionGate = new();
        private readonly ulong worldHostId;
        private readonly ulong instanceId;
        private readonly UnrealWorldDebugConfiguration configuration;
        private readonly long executionId;
        private readonly Action<UnrealWorldDebugExecution, UnrealWorldDebugSessionSnapshot> publish;
        private readonly IReadOnlyList<SemanticBreakpointBinding> bindings;
        private readonly HashSet<SemanticId> breakpointNodes;
        private ManualResetEventSlim? resumeSignal;
        private CoreClrDebugPause? currentPause;
        private StepState? step;
        private Thread? workerThread;
        private int completed;
        private bool pauseRequested;
        private bool aborted;
        private long pauseNumber;
        private long nextFrameId;
        private SemanticId lastMethodId;

        public UnrealWorldDebugExecution(
            ulong worldHostId,
            ulong instanceId,
            UnrealWorldDebugConfiguration configuration,
            long executionId,
            Action<UnrealWorldDebugExecution, UnrealWorldDebugSessionSnapshot> publish)
        {
            this.worldHostId = worldHostId;
            this.instanceId = instanceId;
            this.configuration = configuration;
            this.executionId = executionId;
            this.publish = publish;
            breakpointNodes = configuration.BreakpointNodeIds.ToHashSet();
            bindings = configuration.BreakpointNodeIds.Select(nodeId =>
                new SemanticBreakpointBinding(
                    SemanticId.FromKey(
                        $"unreal-world-breakpoint/v0.1|{worldHostId}|{instanceId}|" +
                        $"{configuration.RuntimeGeneration}|{nodeId}"),
                    nodeId,
                    BreakpointBindingState.Bound,
                    null,
                    null,
                    null,
                    [],
                    null)).ToArray();
        }

        public bool IsCompleted => Volatile.Read(ref completed) != 0;

        public UnrealWorldDebugSessionSnapshot CreateRunningSnapshot() => new(
            UnrealWorldDebugSessionSnapshot.CurrentSchemaVersion,
            "running",
            configuration.Generation,
            executionId,
            null,
            [],
            [],
            [],
            null,
            bindings,
            null);

        public void Start(
            float deltaSeconds,
            Action<float> tick,
            Func<Exception, string> captureFailure)
        {
            workerThread = new Thread(() =>
            {
                Exception? failure = null;
                string? runtimeDiagnostic = null;
                try
                {
                    using IDisposable attachment = CoreClrDebugProbe.Attach(this);
                    tick(deltaSeconds);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    runtimeDiagnostic = captureFailure(exception);
                }
                finally
                {
                    Complete(failure, runtimeDiagnostic);
                    Volatile.Write(ref completed, 1);
                }
            })
            {
                IsBackground = true,
                Name = $"UnifiedScript.WorldDebug.{worldHostId}.{instanceId}.{executionId}",
            };
            workerThread.Start();
        }

        public void Control(UnrealWorldDebugControl control, string? pauseIdText)
        {
            lock (executionGate)
            {
                if (aborted)
                {
                    throw new InvalidOperationException("The live World debug execution was aborted.");
                }
                if (control == UnrealWorldDebugControl.Pause)
                {
                    if (currentPause is not null)
                    {
                        throw new InvalidOperationException("The live World execution is already paused.");
                    }
                    pauseRequested = true;
                    return;
                }

                CoreClrDebugPause pause = currentPause ??
                    throw new InvalidOperationException("The live World execution is not paused.");
                if (!SemanticId.TryParse(pauseIdText, out SemanticId pauseId) ||
                    pause.PauseId != pauseId)
                {
                    throw new InvalidOperationException(
                        "The live World debug command belongs to a stale pause.");
                }
                step = control switch
                {
                    UnrealWorldDebugControl.Continue => null,
                    UnrealWorldDebugControl.StepInto => new StepState(CoreClrDebugStepKind.Into, 0),
                    UnrealWorldDebugControl.StepOver => new StepState(
                        CoreClrDebugStepKind.Over,
                        CurrentDepth),
                    UnrealWorldDebugControl.StepOut when CurrentDepth > 1 => new StepState(
                        CoreClrDebugStepKind.Out,
                        CurrentDepth),
                    UnrealWorldDebugControl.StepOut => throw new InvalidOperationException(
                        "Step Out requires a caller frame in the live Visual C# stack."),
                    _ => throw new ArgumentOutOfRangeException(nameof(control)),
                };
                ResumeCore();
            }
        }

        private int CurrentDepth { get; set; }

        public void Abort()
        {
            ManualResetEventSlim? signal;
            lock (executionGate)
            {
                aborted = true;
                signal = resumeSignal;
                resumeSignal = null;
                currentPause = null;
            }
            signal?.Set();
        }

        public void WaitForCompletion() => workerThread?.Join();

        public void Hit(
            string methodIdText,
            string nodeIdText,
            IReadOnlyList<CoreClrDebugValue> locals,
            IReadOnlyList<CoreClrDebugFrameSnapshot> frameSnapshots)
        {
            if (!SemanticId.TryParse(methodIdText, out SemanticId methodId) ||
                !SemanticId.TryParse(nodeIdText, out SemanticId nodeId))
            {
                throw new InvalidOperationException(
                    "An instrumented live World probe carried an invalid semantic identity.");
            }

            ManualResetEventSlim? signal;
            lock (executionGate)
            {
                if (aborted)
                {
                    return;
                }
                lastMethodId = methodId;
                int currentDepth = Math.Min(frameSnapshots.Count == 0 ? 1 : frameSnapshots.Count, 64);
                CurrentDepth = currentDepth;
                SemanticId[] breakpointIds = bindings
                    .Where(binding => binding.NodeId == nodeId && breakpointNodes.Contains(nodeId))
                    .Select(static binding => binding.BreakpointId)
                    .ToArray();
                CoreClrDebugPauseReason? reason = breakpointIds.Length > 0
                    ? CoreClrDebugPauseReason.Breakpoint
                    : pauseRequested
                        ? CoreClrDebugPauseReason.Pause
                        : null;
                CoreClrDebugStepKind? completedStep = null;
                if (reason is null && step is StepState pendingStep &&
                    ShouldPauseForStep(pendingStep, currentDepth))
                {
                    reason = CoreClrDebugPauseReason.Step;
                    completedStep = pendingStep.Kind;
                }
                if (reason is null)
                {
                    return;
                }

                pauseRequested = false;
                step = null;
                long currentPauseNumber = checked(++pauseNumber);
                StackSnapshot stack = CaptureStack(methodId, nodeId, locals, frameSnapshots);
                SemanticId pauseId = SemanticId.FromKey(
                    $"unreal-world-pause/v0.1|{worldHostId}|{instanceId}|" +
                    $"{configuration.RuntimeGeneration}|{executionId}|{currentPauseNumber}");
                var pause = new CoreClrDebugPause(
                    CoreClrDebugPause.CurrentSchemaVersion,
                    configuration.Generation,
                    executionId,
                    pauseId,
                    reason.Value,
                    completedStep,
                    methodId,
                    nodeId,
                    breakpointIds);
                currentPause = pause;
                signal = new ManualResetEventSlim(initialState: false);
                resumeSignal = signal;
                publish(this, new UnrealWorldDebugSessionSnapshot(
                    UnrealWorldDebugSessionSnapshot.CurrentSchemaVersion,
                    "paused",
                    configuration.Generation,
                    executionId,
                    pause,
                    stack.Frames,
                    stack.FrameLocals.Count == 0 ? [] : stack.FrameLocals[0].Locals,
                    stack.FrameLocals,
                    null,
                    BindMethod(bindings, nodeId, methodId),
                    null));
            }

            signal.Wait();
            signal.Dispose();
        }

        private void Complete(Exception? failure, string? runtimeDiagnostic)
        {
            lock (executionGate)
            {
                currentPause = null;
                resumeSignal = null;
                SemanticId methodId = lastMethodId.IsEmpty
                    ? SemanticId.FromKey("unreal-world-debug-tick/v0.1")
                    : lastMethodId;
                ExecutionResult result = failure is null
                    ? ExecutionResult.Returned(
                        configuration.Generation.SourceCompilationId,
                        configuration.Generation.SemanticConfigurationId,
                        methodId,
                        new ExecutionValue("System.Void", "null"))
                    : ExecutionResult.Faulted(
                        configuration.Generation.SourceCompilationId,
                        configuration.Generation.SemanticConfigurationId,
                        methodId,
                        new ExecutionFault(
                            failure.GetType().FullName ?? failure.GetType().Name,
                            failure.Message));
                publish(this, new UnrealWorldDebugSessionSnapshot(
                    UnrealWorldDebugSessionSnapshot.CurrentSchemaVersion,
                    "completed",
                    configuration.Generation,
                    executionId,
                    null,
                    [],
                    [],
                    [],
                    result,
                    bindings,
                    runtimeDiagnostic));
            }
        }

        private void ResumeCore()
        {
            ManualResetEventSlim signal = resumeSignal ??
                throw new InvalidOperationException("The live World execution is not suspended.");
            resumeSignal = null;
            currentPause = null;
            publish(this, CreateRunningSnapshot());
            signal.Set();
        }

        private static bool ShouldPauseForStep(StepState state, int currentDepth) =>
            state.Kind switch
            {
                CoreClrDebugStepKind.Into => true,
                CoreClrDebugStepKind.Over => currentDepth <= state.Depth,
                CoreClrDebugStepKind.Out => currentDepth < state.Depth,
                _ => throw new ArgumentOutOfRangeException(nameof(state)),
            };

        private StackSnapshot CaptureStack(
            SemanticId topMethodId,
            SemanticId topNodeId,
            IReadOnlyList<CoreClrDebugValue> topLocals,
            IReadOnlyList<CoreClrDebugFrameSnapshot> snapshots)
        {
            CoreClrDebugFrameSnapshot[] source = snapshots.Count == 0
                ? [new CoreClrDebugFrameSnapshot(topMethodId.Value, topNodeId.Value, topLocals)]
                : snapshots.Take(64).ToArray();
            var frames = new List<CoreClrDebugStackFrame>(source.Length);
            var frameLocals = new List<UnrealWorldDebugFrameLocals>(source.Length);
            for (int index = 0; index < source.Length; index++)
            {
                CoreClrDebugFrameSnapshot item = source[index];
                SemanticId methodId = SemanticId.TryParse(item.MethodId, out SemanticId parsedMethod)
                    ? parsedMethod
                    : index == 0 ? topMethodId : default;
                SemanticId nodeId = SemanticId.TryParse(item.NodeId, out SemanticId parsedNode)
                    ? parsedNode
                    : index == 0 ? topNodeId : default;
                long frameId = checked(++nextFrameId);
                frames.Add(new CoreClrDebugStackFrame(
                    frameId,
                    index,
                    methodId.IsEmpty ? null : methodId,
                    nodeId.IsEmpty ? null : nodeId,
                    methodId.IsEmpty ? "<managed>" : methodId.Value,
                    0,
                    -1,
                    null,
                    true));
                frameLocals.Add(new UnrealWorldDebugFrameLocals(
                    frameId,
                    item.Locals.Take(128).ToArray()));
            }
            return new StackSnapshot(frames, frameLocals);
        }

        private static IReadOnlyList<SemanticBreakpointBinding> BindMethod(
            IReadOnlyList<SemanticBreakpointBinding> source,
            SemanticId nodeId,
            SemanticId methodId) => source.Select(binding =>
                binding.NodeId == nodeId ? binding with { MethodId = methodId } : binding).ToArray();

        private sealed record StepState(CoreClrDebugStepKind Kind, int Depth);

        private sealed record StackSnapshot(
            IReadOnlyList<CoreClrDebugStackFrame> Frames,
            IReadOnlyList<UnrealWorldDebugFrameLocals> FrameLocals);
    }
}
