using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Finare.Unreal;

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
    private Dictionary<string, UnrealBlueprintOverrideInvocationContract>? _parentContracts;
    private bool _running;
    private bool _initializing;
    private bool _destroying;
    private int _generatedFunctionCallDepth;

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

        UnrealGenerationInvocationBridge? generationBridge =
            context.Bridge as UnrealGenerationInvocationBridge;
        Context = context;
        _lifetime = new CancellationTokenSource();
        _asyncScope = new UnrealAsyncScope(
            context.Bridge,
            context.InstanceId,
            context.Generation,
            _lifetime.Token);
        _running = true;
        // Once a lifecycle start has been attempted, generated accessors must
        // never fall back to their declaration-side field. If initialization
        // fails, the existing running-state cleanup below still makes access
        // fail instead of exposing stale mirrored data.
        ActivateReflectedPropertyAccess();
        _initializing = true;
        _generatedFunctionCallDepth = 0;
        _parentContracts = new Dictionary<string, UnrealBlueprintOverrideInvocationContract>(
            StringComparer.Ordinal);
        try
        {
            generationBridge?.BindScript(this);
            OnInitialized();
            _initializing = false;
        }
        catch
        {
            generationBridge?.UnbindScript(this);
            try
            {
                _lifetime?.Cancel();
            }
            catch
            {
                // Preserve the initialization failure while still releasing
                // every generation-owned resource below.
            }
            try
            {
                _asyncScope?.Dispose();
            }
            catch
            {
                // Preserve the initialization failure while still releasing
                // every generation-owned resource below.
            }
            _asyncScope = null;
            _parentContracts = null;
            _lifetime?.Dispose();
            _lifetime = null;
            _running = false;
            _generatedFunctionCallDepth = 0;
            _initializing = false;
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
        // Async service V5 may complete a zero-delay request synchronously.
        _asyncScope!.Advance(deltaSeconds);
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

    internal void BeginGeneratedFunctionCall()
    {
        if (!_running)
        {
            throw new InvalidOperationException("The Unreal script instance is not running.");
        }
        _generatedFunctionCallDepth++;
    }

    internal void EndGeneratedFunctionCall()
    {
        if (!_running)
        {
            throw new InvalidOperationException("The Unreal script instance is not running.");
        }
        if (_generatedFunctionCallDepth <= 0)
        {
            throw new InvalidOperationException("No generated function call is active.");
        }
        _generatedFunctionCallDepth--;
    }

    void IUnrealManagedScript.OnStop()
    {
        if (!_running)
        {
            return;
        }

        CancellationTokenSource lifetime = _lifetime!;
        _destroying = true;
        UnrealGenerationInvocationBridge? generationBridge =
            Context.Bridge as UnrealGenerationInvocationBridge;
        ExceptionDispatchInfo? teardownFailure = null;
        try
        {
            lifetime.Cancel();
            _asyncScope!.Dispose();
            OnDestroyed();
        }
        catch (Exception exception)
        {
            teardownFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            try
            {
                _asyncScope?.Dispose();
            }
            catch (Exception exception)
            {
                teardownFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
            generationBridge?.UnbindScript(this);
            _running = false;
            _generatedFunctionCallDepth = 0;
            _asyncScope = null;
            _parentContracts = null;
            _lifetime = null;
            Context = default;
            _destroying = false;
            lifetime.Dispose();
        }
        teardownFailure?.Throw();
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
    /// Calls the inherited implementation for an override whose parameters
    /// are input-only. The parent route targets this script's host component.
    /// </summary>
    protected TResult CallParent<TResult>(
        string managedMethodName,
        params object?[] inputs)
    {
        UnrealBlueprintOverrideInvocationContract contract =
            ResolveParentContract(managedMethodName);
        if (contract.Method.ReturnType != typeof(TResult))
        {
            throw new InvalidOperationException(
                $"Blueprint parent method '{managedMethodName}' returns " +
                $"'{contract.Method.ReturnType}', not '{typeof(TResult)}'.");
        }
        IReadOnlyList<UnrealOutputArgument> arguments =
            CreateParentArguments(contract, inputs, allowOutputs: false);
        UnrealOutputInvocationResult result = InvokeParentCore(
            contract,
            typeof(TResult),
            arguments);
        return (TResult)result.ReturnValue!;
    }

    /// <summary>Calls an input-only void inherited implementation.</summary>
    protected void CallParent(
        string managedMethodName,
        params object?[] inputs)
    {
        UnrealBlueprintOverrideInvocationContract contract =
            ResolveParentContract(managedMethodName);
        if (contract.Method.ReturnType != typeof(void))
        {
            throw new InvalidOperationException(
                $"Blueprint parent method '{managedMethodName}' returns " +
                $"'{contract.Method.ReturnType}', not void.");
        }
        IReadOnlyList<UnrealOutputArgument> arguments =
            CreateParentArguments(contract, inputs, allowOutputs: false);
        _ = InvokeParentCore(contract, typeof(void), arguments);
    }

    /// <summary>
    /// Calls an inherited implementation and returns its return/ref/out values
    /// in reflected method order. Input and ref values are supplied in source
    /// parameter order; pure out parameters need no placeholder.
    /// </summary>
    protected UnrealOutputInvocationResult CallParentWithOutputs(
        string managedMethodName,
        params object?[] inputs)
    {
        UnrealBlueprintOverrideInvocationContract contract =
            ResolveParentContract(managedMethodName);
        IReadOnlyList<UnrealOutputArgument> arguments =
            CreateParentArguments(contract, inputs, allowOutputs: true);
        return InvokeParentCore(contract, contract.Method.ReturnType, arguments);
    }

    private UnrealBlueprintOverrideInvocationContract ResolveParentContract(
        string managedMethodName)
    {
        _ = RequireParentContext();
        ArgumentException.ThrowIfNullOrWhiteSpace(managedMethodName);
        Dictionary<string, UnrealBlueprintOverrideInvocationContract> contracts =
            _parentContracts ?? throw new InvalidOperationException(
                "Blueprint parent calls require an active managed generation.");
        if (!contracts.TryGetValue(managedMethodName, out UnrealBlueprintOverrideInvocationContract? contract))
        {
            contract = UnrealBlueprintOverrideContract.ResolveInvocation(
                GetType(),
                managedMethodName);
            contracts.Add(managedMethodName, contract);
        }
        return contract;
    }

    private static IReadOnlyList<UnrealOutputArgument> CreateParentArguments(
        UnrealBlueprintOverrideInvocationContract contract,
        IReadOnlyList<object?> inputs,
        bool allowOutputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        MethodInfo method = contract.Method;
        var arguments = new List<UnrealOutputArgument>(contract.Parameters.Length);
        int inputIndex = 0;
        foreach (ParameterInfo parameter in contract.Parameters)
        {
            Type parameterType = parameter.ParameterType;
            Type valueType = parameterType.IsByRef
                ? parameterType.GetElementType() ?? typeof(void)
                : parameterType;
            if (valueType == typeof(void))
            {
                throw new InvalidOperationException(
                    $"Blueprint parent method '{method.Name}' has an invalid void parameter.");
            }
            if (!parameterType.IsByRef)
            {
                if (inputIndex >= inputs.Count)
                {
                    throw new ArgumentException(
                        $"Blueprint parent method '{method.Name}' requires an input for parameter '{parameter.Name}'.",
                        nameof(inputs));
                }
                ValidateParentInput(method, parameter, valueType, inputs[inputIndex]);
                arguments.Add(new UnrealOutputArgument(
                    valueType,
                    UnrealParameterDirection.Input,
                    inputs[inputIndex++]));
                continue;
            }

            if (!allowOutputs)
            {
                throw new InvalidOperationException(
                    $"Blueprint parent method '{method.Name}' has ref/out parameters; " +
                    "use CallParentWithOutputs.");
            }
            if (parameter.IsOut)
            {
                arguments.Add(new UnrealOutputArgument(
                    valueType,
                    UnrealParameterDirection.Output,
                    null));
            }
            else
            {
                if (inputIndex >= inputs.Count)
                {
                    throw new ArgumentException(
                        $"Blueprint parent method '{method.Name}' requires an input for ref parameter '{parameter.Name}'.",
                        nameof(inputs));
                }
                ValidateParentInput(method, parameter, valueType, inputs[inputIndex]);
                arguments.Add(new UnrealOutputArgument(
                    valueType,
                    UnrealParameterDirection.InputOutput,
                    inputs[inputIndex++]));
            }
        }
        if (inputIndex != inputs.Count)
        {
            throw new ArgumentException(
                $"Blueprint parent method '{method.Name}' received {inputs.Count} input values but consumed {inputIndex}.",
                nameof(inputs));
        }
        return arguments;
    }

    private static void ValidateParentInput(
        MethodInfo method,
        ParameterInfo parameter,
        Type valueType,
        object? value)
    {
        if (value is null)
        {
            if (valueType.IsValueType && Nullable.GetUnderlyingType(valueType) is null)
            {
                throw new ArgumentException(
                    $"Blueprint parent method '{method.Name}' parameter '{parameter.Name}' " +
                    $"requires a non-null '{valueType}'.",
                    nameof(value));
            }
            return;
        }
        if (!valueType.IsInstanceOfType(value))
        {
            throw new ArgumentException(
                $"Blueprint parent method '{method.Name}' parameter '{parameter.Name}' " +
                $"requires a value assignable to '{valueType}', received '{value.GetType()}'.",
                nameof(value));
        }
    }

    private UnrealOutputInvocationResult InvokeParentCore(
        UnrealBlueprintOverrideInvocationContract contract,
        Type returnType,
        IReadOnlyList<UnrealOutputArgument> arguments)
    {
        UnrealScriptContext context = RequireParentContext();
        if (context.Bridge is not IUnrealParentInvocationBridge parentBridge)
        {
            throw new NotSupportedException(
                "The active Unreal generation does not support Blueprint parent calls.");
        }
        if (!parentBridge.SupportsParentInvocation)
        {
            throw new NotSupportedException(
                "The active Unreal generation does not advertise Blueprint parent invocation capability.");
        }
        string memberPath =
            contract.Attribute.DeclaringClassPath + ":" + contract.Attribute.FunctionName;
        ulong memberId = UnrealMemberId.FromPath(memberPath);
        int suspendedDepth = _generatedFunctionCallDepth;
        // A native parent body may synchronously re-enter this managed script.
        // Give that call a fresh generated-call depth, then restore the caller
        // depth even when the bridge or projection rejects the result.
        _generatedFunctionCallDepth = 0;
        try
        {
            bool project = UnrealObjectInvocationAdapter.ContainsObjectView(returnType) ||
                arguments.Any(a => UnrealObjectInvocationAdapter.ContainsObjectView(a.Type));
            UnrealOutputInvocationResult result = parentBridge.InvokeParentWithOutputs(
                context.Host,
                memberId,
                memberPath,
                project ? UnrealObjectInvocationAdapter.WireType(returnType) : returnType,
                project ? arguments.Select(a => new UnrealOutputArgument(UnrealObjectInvocationAdapter.WireType(a.Type),
                    a.Direction, a.Direction == UnrealParameterDirection.Output ? null :
                        UnrealObjectInvocationAdapter.Convert(a.Value, a.Type, context.Bridge, false))).ToArray() : arguments);
            if (project)
            {
                var outputArguments = arguments.Where(a => a.Direction != UnrealParameterDirection.Input).ToArray();
                if (result.Outputs.Count != outputArguments.Length)
                    throw new InvalidOperationException("Parent output count differs from its declared signature.");
                result = new UnrealOutputInvocationResult(
                    UnrealObjectInvocationAdapter.Convert(result.ReturnValue, returnType, context.Bridge, true),
                    outputArguments.Select((a, i) => UnrealObjectInvocationAdapter.Convert(result.Outputs[i], a.Type, context.Bridge, true)).ToArray());
            }
            return result;
        }
        finally
        {
            _generatedFunctionCallDepth = suspendedDepth;
        }
    }

    private UnrealScriptContext RequireParentContext()
    {
        if (!_running)
        {
            throw new InvalidOperationException(
                "Blueprint parent calls require an active managed generation.");
        }
        if (_initializing || _destroying)
        {
            throw new InvalidOperationException(
                "Blueprint parent calls are unavailable during managed initialization or destruction; " +
                "invoke them from an activated override body.");
        }
        return Context;
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
/// target handle is the owning Actor. For an Actor Script Class, the host
/// handle also identifies that native Actor; its execution component is an
/// internal carrier. A directly configured component host without a Script
/// Class retains its own component handle.
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
