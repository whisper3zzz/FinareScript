namespace UnifiedScript.Unreal;

/// <summary>
/// Managed behavior base for a World-owned Unreal object. The native host owns
/// both handles; managed code never receives a raw UObject address.
/// </summary>
public abstract class UnrealObjectScript : IUnrealManagedScript
{
    private CancellationTokenSource? _lifetime;
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
        _running = true;
        try
        {
            OnInitialized();
        }
        catch
        {
            _lifetime.Cancel();
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
        OnUpdate(deltaSeconds);
    }

    void IUnrealManagedScript.OnStop()
    {
        if (!_running)
        {
            return;
        }

        CancellationTokenSource lifetime = _lifetime!;
        lifetime.Cancel();
        try
        {
            OnDestroyed();
        }
        finally
        {
            _running = false;
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
}
