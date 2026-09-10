using Finare.Core;
using Finare.CSharp;

namespace Finare.Runtime;

public sealed record CoreClrReloadResult(
    CoreClrProgramGeneration Generation,
    bool CacheHit,
    BreakpointBindingSnapshot Breakpoints,
    IReadOnlyList<IdentitySidecarDocument> IdentitySidecars);

public sealed class CoreClrReloadDomain : IDisposable
{
    private readonly object gate = new();
    private readonly CoreClrExecutionHost executionHost = new();
    private readonly CoreClrProgramImageCache imageCache;
    private readonly SemanticBreakpointManager breakpointManager = new();
    private CoreClrExecutionSession? session;
    private CoreClrProgramGeneration? generation;
    private long generationNumber;
    private bool disposed;

    public CoreClrReloadDomain(
        CoreClrProgramImageCache? imageCache = null,
        SemanticId? domainId = null)
    {
        this.imageCache = imageCache ?? new CoreClrProgramImageCache();
        DomainId = domainId ?? SemanticId.New();
        if (DomainId.IsEmpty)
        {
            throw new ArgumentException("A reload domain ID cannot be empty.", nameof(domainId));
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
                return breakpointManager.CurrentSnapshot;
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

    public CoreClrReloadResult Reload(CSharpCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ProgramImageCacheResult cached = imageCache.GetOrAdd(request);
            CoreClrExecutionSession nextSession = executionHost.Load(
                cached.Image,
                request.RuntimeReferencePaths);
            try
            {
                long nextNumber = checked(generationNumber + 1);
                var nextGeneration = new CoreClrProgramGeneration(
                    DomainId,
                    nextNumber,
                    SemanticId.FromKey(
                        $"coreclr-generation/v0.1|{DomainId}|{nextNumber}|{cached.ImageKey}"),
                    cached.ImageKey,
                    cached.Image.Semantics.SourceCompilationId,
                    cached.Image.Semantics.ConfigurationId);
                BreakpointBindingSnapshot breakpoints = breakpointManager.Bind(
                    nextGeneration,
                    cached.Image.SourceMap);
                CoreClrExecutionSession? previousSession = session;
                session = nextSession;
                generation = nextGeneration;
                generationNumber = nextNumber;
                previousSession?.Dispose();
                return new CoreClrReloadResult(
                    nextGeneration,
                    cached.CacheHit,
                    breakpoints,
                    cached.Image.IdentitySidecars);
            }
            catch
            {
                nextSession.Dispose();
                throw;
            }
        }
    }

    public BreakpointBindingSnapshot? SetBreakpoint(SemanticBreakpoint breakpoint)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return breakpointManager.Set(breakpoint);
        }
    }

    public bool RemoveBreakpoint(SemanticId breakpointId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return breakpointManager.Remove(breakpointId);
        }
    }

    public BreakpointHitResolution ResolveHit(CoreClrBreakpointHit hit)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return breakpointManager.ResolveHit(hit);
        }
    }

    public ExecutionResult Execute(ExecutionInvocation invocation)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            CoreClrExecutionSession current = session ?? throw new InvalidOperationException(
                "The reload domain has no active CoreCLR program generation.");
            return current.Execute(invocation);
        }
    }

    public SemanticId ResolveVisualMethodId(string qualifiedMethodName)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            CoreClrExecutionSession current = session ?? throw new InvalidOperationException(
                "The reload domain has no active CoreCLR program generation.");
            return current.ResolveVisualMethodId(qualifiedMethodName);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            session?.Dispose();
            session = null;
            generation = null;
            breakpointManager.Unbind();
            disposed = true;
        }
    }
}
