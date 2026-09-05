namespace UnifiedScript.Unreal;

/// <summary>
/// Managed behavior base for a World-owned Unreal object. The native host owns
/// both handles; managed code never receives a raw UObject address.
/// </summary>
public enum UnrealScriptEventKind
{
    ActorBeginOverlap = 1,
    ActorEndOverlap = 2,
}

public readonly record struct UnrealScriptEvent(
    UnrealScriptEventKind Kind,
    UnrealObjectHandle Source,
    UnrealObjectHandle Argument);

public interface IUnrealManagedEventSink
{
    void OnUnrealEvent(UnrealScriptEvent scriptEvent);
}

public abstract partial class UnrealObjectScript : IUnrealManagedScript, IUnrealManagedEventSink
{
    private CancellationTokenSource? _lifetime;
    private UnrealAsyncScope? _asyncScope;
    private UnrealScriptStateSynchronizer.StateSession? _stateSession;
    private bool _running;

    public UnrealScriptContext Context { get; private set; }

    public IUnrealInvocationBridge Bridge => RequireRunning().Bridge;

    public UnrealObjectHandle HostHandle => RequireRunning().Host;

    public UnrealObjectHandle TargetHandle => RequireRunning().Target;

    public ulong InstanceId => RequireRunning().InstanceId;

    public int Generation => RequireRunning().Generation;

    public bool IsRunning => _running;

    /// <summary>Canceled before OnDestroyed runs and before a reload generation unloads.</summary>
    public CancellationToken Lifetime => _lifetime?.Token ??
        throw new InvalidOperationException("The Unreal script is not running.");

    void IUnrealManagedScript.OnStart(UnrealScriptContext context)
    {
        if (_running)
        {
            throw new InvalidOperationException("The Unreal script instance is already running.");
        }
        if (context.Bridge is null ||
            !context.Host.IsWellFormed ||
            !context.Target.IsWellFormed ||
            context.InstanceId == 0 ||
            context.Generation <= 0)
        {
            throw new ArgumentException("A live World-owned Unreal script context is required.", nameof(context));
        }

        Context = context;
        _lifetime = new CancellationTokenSource();
        _asyncScope = new UnrealAsyncScope(
            context.Bridge,
            context.InstanceId,
            context.Generation,
            _lifetime.Token);
        _running = true;
        try
        {
            _stateSession = UnrealScriptStateSynchronizer.Open(this, context);
            _stateSession?.Hydrate();
            OnInitialized();
            _stateSession?.Flush();
        }
        catch
        {
            _lifetime.Cancel();
            _asyncScope.Dispose();
            _asyncScope = null;
            _stateSession = null;
            _lifetime.Dispose();
            _lifetime = null;
            _running = false;
            Context = default;
            throw;
        }
    }

    void IUnrealManagedScript.OnTick(float deltaSeconds)
    {
        if (!_running)
        {
            throw new InvalidOperationException("The Unreal script instance is not running.");
        }
        _asyncScope!.Advance(deltaSeconds);
        _stateSession?.Hydrate();
        OnUpdate(deltaSeconds);
        _stateSession?.Flush();
    }

    void IUnrealManagedEventSink.OnUnrealEvent(UnrealScriptEvent scriptEvent)
    {
        if (!_running)
        {
            throw new InvalidOperationException("The Unreal script instance is not running.");
        }
        if (!scriptEvent.Source.IsWellFormed ||
            (!scriptEvent.Argument.IsNull && !scriptEvent.Argument.IsWellFormed))
        {
            throw new ArgumentException("A managed Unreal event contains an invalid object handle.", nameof(scriptEvent));
        }
        _stateSession?.Hydrate();
        OnUnrealEvent(scriptEvent);
        _stateSession?.Flush();
    }

    internal void BeginGeneratedFunctionCall()
    {
        if (!_running)
        {
            throw new InvalidOperationException("The Unreal script instance is not running.");
        }
        _stateSession?.Hydrate();
    }

    internal void EndGeneratedFunctionCall()
    {
        if (!_running)
        {
            throw new InvalidOperationException("The Unreal script instance is not running.");
        }
        _stateSession?.Flush();
    }

    void IUnrealManagedScript.OnStop()
    {
        if (!_running)
        {
            return;
        }

        CancellationTokenSource lifetime = _lifetime!;
        lifetime.Cancel();
        _asyncScope!.Dispose();
        try
        {
            _stateSession?.Hydrate();
            OnDestroyed();
            _stateSession?.Flush();
        }
        finally
        {
            _running = false;
            _asyncScope = null;
            _stateSession = null;
            _lifetime = null;
            Context = default;
            lifetime.Dispose();
        }
    }

    protected virtual void OnInitialized()
    {
    }

    protected virtual void OnUpdate(float deltaSeconds)
    {
    }

    protected virtual void OnDestroyed()
    {
    }

    protected virtual void OnUnrealEvent(UnrealScriptEvent scriptEvent)
    {
    }

    /// <summary>
    /// Completes on a future managed Tick. The task is canceled synchronously
    /// on the game thread when this generation reloads or its World ends.
    /// </summary>
    protected Task DelayAsync(float seconds)
    {
        _ = RequireRunning();
        return _asyncScope!.DelayAsync(seconds, 0.0f, Lifetime);
    }

    protected Task NextTickAsync() => DelayAsync(0.0f);

    /// <summary>Completes on Tick or fails with TimeoutException.</summary>
    protected Task DelayAsync(
        float seconds,
        float timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        _ = RequireRunning();
        return _asyncScope!.DelayAsync(seconds, timeoutSeconds, cancellationToken);
    }

    /// <summary>Loads an object through the World-owned async asset service.</summary>
    protected Task<UnrealObjectHandle> LoadObjectAsync(
        string objectPath,
        float timeoutSeconds = 0.0f,
        CancellationToken cancellationToken = default)
    {
        _ = RequireRunning();
        return _asyncScope!.LoadObjectAsync(
            objectPath,
            timeoutSeconds,
            cancellationToken);
    }

    protected Task<UnrealObjectHandle> LoadObjectAsync(
        UnrealSoftObjectHandle objectReference,
        float timeoutSeconds = 0.0f,
        CancellationToken cancellationToken = default) =>
        LoadObjectAsync(objectReference.AssetPath, timeoutSeconds, cancellationToken);

    /// <summary>Loads a class through the World-owned async asset service.</summary>
    protected Task<UnrealObjectHandle> LoadClassAsync(
        string classPath,
        float timeoutSeconds = 0.0f,
        CancellationToken cancellationToken = default)
    {
        _ = RequireRunning();
        return _asyncScope!.LoadClassAsync(
            classPath,
            timeoutSeconds,
            cancellationToken);
    }

    protected Task<UnrealObjectHandle> LoadClassAsync(
        UnrealSoftClassHandle classReference,
        float timeoutSeconds = 0.0f,
        CancellationToken cancellationToken = default) =>
        LoadClassAsync(classReference.AssetPath, timeoutSeconds, cancellationToken);

    private UnrealScriptContext RequireRunning() => _running
        ? Context
        : throw new InvalidOperationException("The Unreal script is not running.");

}

/// <summary>
/// Actor behavior surface corresponding to BeginPlay, Tick, and EndPlay. The
/// target handle is the owning Actor while the host handle identifies the
/// UUnifiedScriptComponent that owns the managed generation.
/// </summary>
public abstract class UnrealActorScript : UnrealObjectScript
{
    protected sealed override void OnInitialized() => OnBeginPlay();

    protected sealed override void OnUpdate(float deltaSeconds) => OnTick(deltaSeconds);

    protected sealed override void OnDestroyed() => OnEndPlay();

    protected virtual void OnBeginPlay()
    {
    }

    protected virtual void OnTick(float deltaSeconds)
    {
    }

    protected virtual void OnEndPlay()
    {
    }

    protected override void OnUnrealEvent(UnrealScriptEvent scriptEvent)
    {
        switch (scriptEvent.Kind)
        {
            case UnrealScriptEventKind.ActorBeginOverlap:
                OnActorBeginOverlap(scriptEvent.Argument);
                break;
            case UnrealScriptEventKind.ActorEndOverlap:
                OnActorEndOverlap(scriptEvent.Argument);
                break;
            default:
                base.OnUnrealEvent(scriptEvent);
                break;
        }
    }

    protected virtual void OnActorBeginOverlap(UnrealObjectHandle otherActor)
    {
    }

    protected virtual void OnActorEndOverlap(UnrealObjectHandle otherActor)
    {
    }
}

/// <summary>
/// ActorComponent behavior surface corresponding to BeginPlay, Tick, and
/// EndPlay. Unlike UnrealActorScript it does not claim Actor overlap events.
/// </summary>
public abstract class UnrealActorComponentScript : UnrealObjectScript
{
    protected sealed override void OnInitialized() => OnBeginPlay();

    protected sealed override void OnUpdate(float deltaSeconds) => OnTick(deltaSeconds);

    protected sealed override void OnDestroyed() => OnEndPlay();

    protected virtual void OnBeginPlay()
    {
    }

    protected virtual void OnTick(float deltaSeconds)
    {
    }

    protected virtual void OnEndPlay()
    {
    }
}
