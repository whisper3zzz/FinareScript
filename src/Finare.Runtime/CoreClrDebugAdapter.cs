using Finare.Core;
using Finare.Core.Hir;
using Finare.CSharp;

namespace Finare.Runtime;

public sealed class CoreClrDebugAdapter : IDisposable
{
    private readonly object gate = new();
    private readonly CSharpProgramEmitter emitter = new();
    private readonly CoreClrExecutionHost executionHost = new();
    private readonly SemanticBreakpointManager breakpointManager = new();
    private CoreClrExecutionSession? session;
    private CoreClrProgramGeneration? generation;
    private BreakpointBindingSnapshot? debugBindings;
    private CoreClrDebugCoordinator? coordinator;
    private Task<ExecutionResult>? executionTask;
    private long generationNumber;
    private long executionNumber;
    private CoreClrDebugPreparedReload? preparedReload;
    private bool disposed;

    public CoreClrDebugAdapter(SemanticId? domainId = null)
    {
        DomainId = domainId ?? SemanticId.New();
        if (DomainId.IsEmpty)
        {
            throw new ArgumentException("A debug adapter domain ID cannot be empty.", nameof(domainId));
        }
    }

    public SemanticId DomainId { get; }

    public CoreClrProgramGeneration? CurrentGeneration
    {
        get
        {
            lock (gate)
            {
                return generation;
            }
        }
    }

    public BreakpointBindingSnapshot? CurrentBreakpoints
    {
        get
        {
            lock (gate)
            {
                return debugBindings;
            }
        }
    }

    public WeakReference? CurrentLoadContextReference
    {
        get
        {
            lock (gate)
            {
                return session?.LoadContextReference;
            }
        }
    }

    public CoreClrDebugReloadResult Reload(CSharpCompilationRequest request, Action? beforeCommit = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureNotRunning();
            EnsureNoPreparedReload();
            CSharpProgramEmitResult emitted = emitter.Emit(
                request,
                new CSharpDebugInstrumentation(typeof(CoreClrDebugProbe).Assembly.Location));
            if (!emitted.Succeeded || emitted.Image is null)
            {
                throw new ExecutionCompilationException(emitted.Diagnostics);
            }

            CSharpProgramImage image = emitted.Image;
            CoreClrExecutionSession nextSession = executionHost.Load(
                image,
                request.RuntimeReferencePaths);
            try
            {
                ProgramImageKey imageKey = ProgramImageKey.FromImage(image);
                long nextNumber = checked(generationNumber + 1);
                var nextGeneration = new CoreClrProgramGeneration(
                    DomainId,
                    nextNumber,
                    SemanticId.FromKey(
                        $"coreclr-debug-generation/v0.1|{DomainId}|{nextNumber}|{imageKey}"),
                    imageKey,
                    image.Semantics.SourceCompilationId,
                    image.Semantics.ConfigurationId);
                // Publication can fail after dependencies have changed on disk.
                // Keep the old loaded program alive until the candidate's outputs commit.
                beforeCommit?.Invoke();
                BreakpointBindingSnapshot bound = breakpointManager.Bind(
                    nextGeneration,
                    image.SourceMap);
                BreakpointBindingSnapshot nextBindings = RestrictToProbeNodes(bound, image.Hir);
                CoreClrExecutionSession? previous = session;
                session = nextSession;
                generation = nextGeneration;
                generationNumber = nextNumber;
                debugBindings = nextBindings;
                coordinator = null;
                executionTask = null;
                previous?.Dispose();
                return new CoreClrDebugReloadResult(
                    nextGeneration,
                    nextBindings,
                    image.IdentitySidecars);
            }
            catch
            {
                nextSession.Dispose();
                throw;
            }
        }
    }

    public CoreClrDebugPreparedReload PrepareReload(CSharpCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureNotRunning();
            EnsureNoPreparedReload();
            CSharpProgramEmitResult emitted = emitter.Emit(
                request,
                new CSharpDebugInstrumentation(typeof(CoreClrDebugProbe).Assembly.Location));
            if (!emitted.Succeeded || emitted.Image is null)
            {
                throw new ExecutionCompilationException(emitted.Diagnostics);
            }

            CSharpProgramImage image = emitted.Image;
            CoreClrExecutionSession nextSession = executionHost.Load(
                image,
                request.RuntimeReferencePaths);
            try
            {
                ProgramImageKey imageKey = ProgramImageKey.FromImage(image);
                long nextNumber = checked(generationNumber + 1);
                var nextGeneration = new CoreClrProgramGeneration(
                    DomainId,
                    nextNumber,
                    SemanticId.FromKey(
                        $"coreclr-debug-generation/v0.1|{DomainId}|{nextNumber}|{imageKey}"),
                    imageKey,
                    image.Semantics.SourceCompilationId,
                    image.Semantics.ConfigurationId);
                BreakpointBindingSnapshot bound = breakpointManager.PreviewBind(
                    nextGeneration,
                    image.SourceMap);
                BreakpointBindingSnapshot nextBindings = RestrictToProbeNodes(bound, image.Hir);
                CoreClrDebugPreparedReload prepared = new CoreClrDebugPreparedReload(
                    this,
                    nextSession,
                    generation,
                    sourceMap: image.SourceMap,
                    new CoreClrDebugReloadResult(
                        nextGeneration,
                        nextBindings,
                        image.IdentitySidecars));
                preparedReload = prepared;
                return prepared;
            }
            catch
            {
                nextSession.Dispose();
                throw;
            }
        }
    }

    internal void CommitPreparedReload(CoreClrDebugPreparedReload prepared)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!ReferenceEquals(prepared.Owner, this) || prepared.IsCompleted)
                throw new InvalidOperationException("The CoreCLR prepared reload is no longer available.");
            EnsureNotRunning();
            if (!ReferenceEquals(preparedReload, prepared) ||
                (prepared.BaselineGeneration is not null &&
                 (generation is null ||
                  generation.Number != prepared.BaselineGeneration.Number ||
                  generation.GenerationId != prepared.BaselineGeneration.GenerationId)))
            {
                throw new InvalidOperationException("The CoreCLR prepared reload is stale; prepare a new candidate.");
            }
            CoreClrExecutionSession? previous = session;
            session = prepared.Session;
            generation = prepared.Result.Generation;
            generationNumber = generation.Number;
            BreakpointBindingSnapshot committedBindings = breakpointManager.CommitPreviewBind(
                prepared.Result.Generation,
                prepared.SourceMap);
            debugBindings = RestrictToProbeNodes(committedBindings, prepared.Session.Hir);
            coordinator = null;
            executionTask = null;
            preparedReload = null;
            prepared.MarkCompleted();
            previous?.Dispose();
        }
    }

    internal void DiscardPreparedReload(CoreClrDebugPreparedReload prepared)
    {
        lock (gate)
        {
            if (!ReferenceEquals(prepared.Owner, this) || prepared.IsCompleted)
                return;
            if (ReferenceEquals(preparedReload, prepared))
                preparedReload = null;
            prepared.MarkCompleted();
            prepared.Session.Dispose();
        }
    }

    public BreakpointBindingSnapshot SetBreakpoint(SemanticBreakpoint breakpoint)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureNoPreparedReload();
            CoreClrExecutionSession current = session ?? throw new InvalidOperationException(
                "The debug adapter has no active CoreCLR program generation.");
            BreakpointBindingSnapshot bound = breakpointManager.Set(breakpoint) ??
                throw new InvalidOperationException("The debug adapter has no bound source map.");
            debugBindings = RestrictToProbeNodes(bound, current.Hir);
            coordinator?.UpdateBindings(debugBindings);
            return debugBindings;
        }
    }

    public bool RemoveBreakpoint(SemanticId breakpointId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureNoPreparedReload();
            bool removed = breakpointManager.Remove(breakpointId);
            if (removed && session is not null && breakpointManager.CurrentSnapshot is not null)
            {
                debugBindings = RestrictToProbeNodes(
                    breakpointManager.CurrentSnapshot,
                    session.Hir);
                coordinator?.UpdateBindings(debugBindings);
            }

            return removed;
        }
    }

    public SemanticId ResolveVisualMethodId(string qualifiedMethodName)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return (session ?? throw new InvalidOperationException(
                "The debug adapter has no active CoreCLR program generation."))
                .ResolveVisualMethodId(qualifiedMethodName);
        }
    }

    public CoreClrDebugExecution Start(ExecutionInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureNotRunning();
            EnsureNoPreparedReload();
            CoreClrExecutionSession currentSession = session ?? throw new InvalidOperationException(
                "The debug adapter has no active CoreCLR program generation.");
            CoreClrProgramGeneration currentGeneration = generation!;
            BreakpointBindingSnapshot currentBindings = debugBindings!;
            long executionId = checked(++executionNumber);
            var nextCoordinator = new CoreClrDebugCoordinator(
                currentGeneration,
                executionId,
                currentSession.SourceMap,
                currentSession.Hir,
                currentBindings);
            coordinator = nextCoordinator;
            executionTask = Task.Run(() =>
            {
                try
                {
                    using IDisposable attachment = CoreClrDebugProbe.Attach(nextCoordinator);
                    return currentSession.Execute(invocation);
                }
                finally
                {
                    nextCoordinator.Complete();
                }
            });
            return new CoreClrDebugExecution(currentGeneration, executionId);
        }
    }

    public Task<CoreClrDebugPause> WaitForPauseAsync(
        long executionId,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return GetCoordinator(executionId).WaitForPauseAsync(cancellationToken);
        }
    }

    public IReadOnlyList<CoreClrDebugStackFrame> GetStackTrace(
        SemanticId generationId,
        SemanticId pauseId)
    {
        lock (gate)
        {
            ValidateGeneration(generationId);
            return (coordinator ?? throw NoExecution()).GetStackTrace(pauseId);
        }
    }

    public IReadOnlyList<CoreClrDebugValue> GetLocals(
        SemanticId generationId,
        SemanticId pauseId,
        long frameId)
    {
        lock (gate)
        {
            ValidateGeneration(generationId);
            return (coordinator ?? throw NoExecution()).GetLocals(pauseId, frameId);
        }
    }

    public CoreClrDebugEvaluation Evaluate(
        SemanticId generationId,
        SemanticId pauseId,
        long frameId,
        string expression)
    {
        lock (gate)
        {
            ValidateGeneration(generationId);
            return (coordinator ?? throw NoExecution()).Evaluate(pauseId, frameId, expression);
        }
    }

    public void Pause(SemanticId generationId, long executionId)
    {
        lock (gate)
        {
            ValidateGeneration(generationId);
            GetCoordinator(executionId).RequestPause();
        }
    }

    public void Continue(SemanticId generationId, SemanticId pauseId)
    {
        lock (gate)
        {
            ValidateGeneration(generationId);
            (coordinator ?? throw NoExecution()).Continue(pauseId);
        }
    }

    public void StepOver(SemanticId generationId, SemanticId pauseId)
    {
        lock (gate)
        {
            ValidateGeneration(generationId);
            (coordinator ?? throw NoExecution()).StepOver(pauseId);
        }
    }

    public void StepInto(SemanticId generationId, SemanticId pauseId)
    {
        lock (gate)
        {
            ValidateGeneration(generationId);
            (coordinator ?? throw NoExecution()).StepInto(pauseId);
        }
    }

    public void StepOut(SemanticId generationId, SemanticId pauseId)
    {
        lock (gate)
        {
            ValidateGeneration(generationId);
            (coordinator ?? throw NoExecution()).StepOut(pauseId);
        }
    }

    public Task<ExecutionResult> WaitForCompletionAsync(
        long executionId,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            _ = GetCoordinator(executionId);
            return executionTask!.WaitAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        CoreClrDebugCoordinator? activeCoordinator;
        CoreClrExecutionSession? activeSession;
        CoreClrDebugPreparedReload? pendingReload;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            activeCoordinator = coordinator;
            activeSession = session;
            pendingReload = preparedReload;
            coordinator = null;
            session = null;
            preparedReload = null;
            generation = null;
            debugBindings = null;
            breakpointManager.Unbind();
            disposed = true;
        }

        activeCoordinator?.Abort();
        activeSession?.Dispose();
        pendingReload?.Dispose();
    }

    private static BreakpointBindingSnapshot RestrictToProbeNodes(
        BreakpointBindingSnapshot snapshot,
        HirCompilationUnit hir)
    {
        HashSet<SemanticId> probeNodeIds = hir.DescendantsAndSelf()
            .OfType<HirStatement>()
            .Where(static statement => statement is not HirBlockStatement)
            .Select(static statement => statement.Id)
            .ToHashSet();
        SemanticBreakpointBinding[] bindings = snapshot.Bindings
            .Select(binding => binding.State == BreakpointBindingState.Bound &&
                    !probeNodeIds.Contains(binding.NodeId)
                ? binding with
                {
                    State = BreakpointBindingState.Pending,
                    MethodId = null,
                    MetadataToken = null,
                    NodeSourceSpan = null,
                    Locations = [],
                    Message = "The cooperative CoreCLR backend currently probes statement nodes only.",
                }
                : binding)
            .ToArray();
        return BreakpointProtocolJson.NormalizeAndValidate(snapshot with { Bindings = bindings });
    }

    private CoreClrDebugCoordinator GetCoordinator(long executionId)
    {
        CoreClrDebugCoordinator current = coordinator ?? throw NoExecution();
        if (current.ExecutionId != executionId)
        {
            throw new BreakpointProtocolException(
                "UDB1005",
                $"Debug execution '{executionId}' is stale or does not exist.");
        }

        return current;
    }

    private void ValidateGeneration(SemanticId generationId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (generationId.IsEmpty || generation is null || generation.GenerationId != generationId)
        {
            throw new BreakpointProtocolException(
                "UDB1002",
                "The debug command belongs to a stale or different CoreCLR program generation.");
        }
    }

    private void EnsureNotRunning()
    {
        if (executionTask is { IsCompleted: false })
        {
            throw new BreakpointProtocolException(
                "UDB1004",
                "A CoreCLR debug execution is already running or suspended.");
        }
    }

    private void EnsureNoPreparedReload()
    {
        if (preparedReload is { IsCompleted: false })
        {
            throw new InvalidOperationException(
                "A CoreCLR candidate reload is prepared for authoring publication; finish or cancel it before changing debug state.");
        }
    }

    private static BreakpointProtocolException NoExecution() => new(
        "UDB1005",
        "The debug adapter has no active execution.");
}

public sealed class CoreClrDebugPreparedReload : IDisposable
{
    internal CoreClrDebugPreparedReload(
        CoreClrDebugAdapter owner,
        CoreClrExecutionSession session,
        CoreClrProgramGeneration? baselineGeneration,
        CSharpProgramSourceMap sourceMap,
        CoreClrDebugReloadResult result)
    {
        Owner = owner;
        Session = session;
        BaselineGeneration = baselineGeneration;
        SourceMap = sourceMap;
        Result = result;
    }

    internal CoreClrDebugAdapter Owner { get; }
    internal CoreClrExecutionSession Session { get; }
    internal CoreClrProgramGeneration? BaselineGeneration { get; }
    internal CSharpProgramSourceMap SourceMap { get; }
    internal bool IsCompleted { get; private set; }
    public CoreClrDebugReloadResult Result { get; }

    public void Commit() => Owner.CommitPreparedReload(this);

    public void Dispose() => Owner.DiscardPreparedReload(this);

    internal void MarkCompleted() => IsCompleted = true;
}

public sealed class CoreClrDebugLoopbackTransport : ICoreClrDebugTransport
{
    private readonly CoreClrDebugAdapter adapter;

    public CoreClrDebugLoopbackTransport(CoreClrDebugAdapter? adapter = null)
    {
        this.adapter = adapter ?? new CoreClrDebugAdapter();
    }

    public CoreClrDebugReloadResult Reload(CSharpCompilationRequest request) =>
        adapter.Reload(request);

    public CoreClrDebugReloadResult Reload(CSharpCompilationRequest request, Action beforeCommit) =>
        adapter.Reload(request, beforeCommit);

    public CoreClrDebugPreparedReload PrepareReload(CSharpCompilationRequest request) =>
        adapter.PrepareReload(request);

    public BreakpointBindingSnapshot SetBreakpoint(SemanticBreakpoint breakpoint) =>
        adapter.SetBreakpoint(breakpoint);

    public bool RemoveBreakpoint(SemanticId breakpointId) =>
        adapter.RemoveBreakpoint(breakpointId);

    public SemanticId ResolveVisualMethodId(string qualifiedMethodName) =>
        adapter.ResolveVisualMethodId(qualifiedMethodName);

    public CoreClrDebugExecution Start(ExecutionInvocation invocation) => adapter.Start(invocation);

    public Task<CoreClrDebugPause> WaitForPauseAsync(
        long executionId,
        CancellationToken cancellationToken = default) =>
        adapter.WaitForPauseAsync(executionId, cancellationToken);

    public IReadOnlyList<CoreClrDebugStackFrame> GetStackTrace(
        SemanticId generationId,
        SemanticId pauseId) => adapter.GetStackTrace(generationId, pauseId);

    public IReadOnlyList<CoreClrDebugValue> GetLocals(
        SemanticId generationId,
        SemanticId pauseId,
        long frameId) => adapter.GetLocals(generationId, pauseId, frameId);

    public CoreClrDebugEvaluation Evaluate(
        SemanticId generationId,
        SemanticId pauseId,
        long frameId,
        string expression) => adapter.Evaluate(generationId, pauseId, frameId, expression);

    public void Pause(SemanticId generationId, long executionId) =>
        adapter.Pause(generationId, executionId);

    public void Continue(SemanticId generationId, SemanticId pauseId) =>
        adapter.Continue(generationId, pauseId);

    public void StepOver(SemanticId generationId, SemanticId pauseId) =>
        adapter.StepOver(generationId, pauseId);

    public void StepInto(SemanticId generationId, SemanticId pauseId) =>
        adapter.StepInto(generationId, pauseId);

    public void StepOut(SemanticId generationId, SemanticId pauseId) =>
        adapter.StepOut(generationId, pauseId);

    public Task<ExecutionResult> WaitForCompletionAsync(
        long executionId,
        CancellationToken cancellationToken = default) =>
        adapter.WaitForCompletionAsync(executionId, cancellationToken);

    public void Dispose() => adapter.Dispose();
}
