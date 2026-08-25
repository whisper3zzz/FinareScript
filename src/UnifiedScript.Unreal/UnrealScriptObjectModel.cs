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

public abstract class UnrealObjectScript : IUnrealManagedScript, IUnrealManagedEventSink
{
    private CancellationTokenSource? _lifetime;
    private readonly List<LatentDelay> _latentDelays = [];
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
            CancelLatentDelays(_lifetime.Token);
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
        AdvanceLatentDelays(deltaSeconds);
        OnUpdate(deltaSeconds);
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
        OnUnrealEvent(scriptEvent);
    }

    void IUnrealManagedScript.OnStop()
    {
        if (!_running)
        {
            return;
        }

        CancellationTokenSource lifetime = _lifetime!;
        lifetime.Cancel();
        CancelLatentDelays(lifetime.Token);
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

    protected virtual void OnUnrealEvent(UnrealScriptEvent scriptEvent)
    {
    }

    /// <summary>
    /// Completes on a future managed Tick. The task is canceled synchronously
    /// on the game thread when this generation reloads or its World ends.
    /// </summary>
    protected Task DelayAsync(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }
        _ = RequireRunning();
        var delay = new LatentDelay(seconds);
        _latentDelays.Add(delay);
        return delay.Task;
    }

    protected Task NextTickAsync() => DelayAsync(0.0f);

    private void AdvanceLatentDelays(float deltaSeconds)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        }

        for (int index = _latentDelays.Count - 1; index >= 0; --index)
        {
            LatentDelay delay = _latentDelays[index];
            if (!delay.Advance(deltaSeconds))
            {
                continue;
            }
            _latentDelays.RemoveAt(index);
            delay.Complete();
        }
    }

    private void CancelLatentDelays(CancellationToken token)
    {
        foreach (LatentDelay delay in _latentDelays)
        {
            delay.Cancel(token);
        }
        _latentDelays.Clear();
    }

    private UnrealScriptContext RequireRunning() => _running
        ? Context
        : throw new InvalidOperationException("The Unreal script is not running.");

    private sealed class LatentDelay(float remainingSeconds)
    {
        private readonly TaskCompletionSource _completion = new();
        private float _remainingSeconds = remainingSeconds;

        public Task Task => _completion.Task;

        public bool Advance(float deltaSeconds)
        {
            _remainingSeconds -= deltaSeconds;
            return _remainingSeconds <= 0.0f;
        }

        public void Complete() => _completion.TrySetResult();

        public void Cancel(CancellationToken token) => _completion.TrySetCanceled(token);
    }
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
