namespace UnifiedScript.Unreal;

/// <summary>
/// Per-script-generation view over a World bridge. It preserves every fast
/// invocation capability while owning event subscriptions created by that
/// generation, so reload and World teardown cannot retain an old ALC.
/// </summary>
internal sealed class UnrealGenerationInvocationBridge :
    IUnrealInvocationBridge,
    IUnrealNativeBindingBridge,
    IUnrealNativeObjectProvider,
    IUnrealCallbackProvider,
    IUnrealFastInvocationBridge,
    IUnrealTypedInvocationBridge,
    IUnrealOutputInvocationBridge,
    IUnrealEventBridge,
    IUnrealWorldDataPlaneProvider,
    IUnrealAsyncServiceProvider,
    IDisposable
{
    private readonly IUnrealInvocationBridge _inner;
    private readonly List<IUnrealEventSubscription> _subscriptions = [];
    private bool _disposed;
    private readonly object _nativeObjectGate = new();
    private UnrealNativeObjectScope? _nativeObjects;
    private UnrealCallbackScope? _callbacks;

    public UnrealCallbackScope Callbacks
    {
        get
        {
            lock (_nativeObjectGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _callbacks ??= LiveInner is IUnrealCallbackScopeFactory factory
                    ? factory.CreateCallbackScope()
                    : throw new NotSupportedException("The World bridge does not support generation-owned callbacks.");
            }
        }
    }

    public UnrealNativeObjectScope NativeObjects
    {
        get
        {
            lock (_nativeObjectGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _nativeObjects ??= LiveInner is IUnrealNativeObjectScopeFactory factory
                    ? factory.CreateNativeObjectScope()
                    : throw new NotSupportedException("The World bridge does not support generation-owned native objects.");
            }
        }
    }

    public UnrealGenerationInvocationBridge(IUnrealInvocationBridge inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public bool SupportsFastInvocation =>
        !_disposed && _inner is IUnrealFastInvocationBridge bridge &&
        bridge.SupportsFastInvocation;

    public void RequireNativeBindings(IReadOnlyList<string> memberPaths)
    {
        if (LiveInner is not IUnrealNativeBindingBridge bridge)
            throw new NotSupportedException("The World bridge does not support native extension bindings.");
        bridge.RequireNativeBindings(memberPaths);
    }

    public bool SupportsTypedInvocation =>
        !_disposed && _inner is IUnrealTypedInvocationBridge bridge &&
        bridge.SupportsTypedInvocation;

    public UnrealWorldDataPlane DataPlane =>
        !_disposed && _inner is IUnrealWorldDataPlaneProvider provider
            ? provider.DataPlane
            : UnrealWorldDataPlane.Unavailable;

    IUnrealAsyncServiceTransport? IUnrealAsyncServiceProvider.AsyncTransport =>
        !_disposed && _inner is IUnrealAsyncServiceProvider provider
            ? provider.AsyncTransport
            : null;

    public T ReadProperty<T>(UnrealObjectHandle target, string propertyPath) =>
        LiveInner.ReadProperty<T>(target, propertyPath);

    public void WriteProperty<T>(UnrealObjectHandle target, string propertyPath, T value) =>
        LiveInner.WriteProperty(target, propertyPath, value);

    public T Invoke<T>(UnrealObjectHandle target, string functionPath, IReadOnlyList<object?> arguments) =>
        LiveInner.Invoke<T>(target, functionPath, arguments);

    public void Invoke(UnrealObjectHandle target, string functionPath, IReadOnlyList<object?> arguments) =>
        LiveInner.Invoke(target, functionPath, arguments);

    public T ReadProperty<T>(UnrealObjectHandle target, ulong memberId, string memberPath) =>
        Fast.ReadProperty<T>(target, memberId, memberPath);

    public void WriteProperty<T>(UnrealObjectHandle target, ulong memberId, string memberPath, T value) =>
        Fast.WriteProperty(target, memberId, memberPath, value);

    public T Invoke<T>(UnrealObjectHandle target, ulong memberId, string memberPath, IReadOnlyList<object?> arguments) =>
        Fast.Invoke<T>(target, memberId, memberPath, arguments);

    public void Invoke(UnrealObjectHandle target, ulong memberId, string memberPath, IReadOnlyList<object?> arguments) =>
        Fast.Invoke(target, memberId, memberPath, arguments);

    public TResult Invoke<TResult>(UnrealObjectHandle target, ulong memberId, string memberPath) =>
        Typed.Invoke<TResult>(target, memberId, memberPath);

    public TResult Invoke<TResult, T0>(UnrealObjectHandle target, ulong memberId, string memberPath, T0 argument0) =>
        Typed.Invoke<TResult, T0>(target, memberId, memberPath, argument0);

    public TResult Invoke<TResult, T0, T1>(UnrealObjectHandle target, ulong memberId, string memberPath, T0 argument0, T1 argument1) =>
        Typed.Invoke<TResult, T0, T1>(target, memberId, memberPath, argument0, argument1);

    public TResult Invoke<TResult, T0, T1, T2>(UnrealObjectHandle target, ulong memberId, string memberPath, T0 argument0, T1 argument1, T2 argument2) =>
        Typed.Invoke<TResult, T0, T1, T2>(target, memberId, memberPath, argument0, argument1, argument2);

    public TResult Invoke<TResult, T0, T1, T2, T3>(UnrealObjectHandle target, ulong memberId, string memberPath, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Typed.Invoke<TResult, T0, T1, T2, T3>(target, memberId, memberPath, argument0, argument1, argument2, argument3);

    public void Invoke(UnrealObjectHandle target, ulong memberId, string memberPath) =>
        Typed.Invoke(target, memberId, memberPath);

    public void Invoke<T0>(UnrealObjectHandle target, ulong memberId, string memberPath, T0 argument0) =>
        Typed.Invoke(target, memberId, memberPath, argument0);

    public void Invoke<T0, T1>(UnrealObjectHandle target, ulong memberId, string memberPath, T0 argument0, T1 argument1) =>
        Typed.Invoke(target, memberId, memberPath, argument0, argument1);

    public void Invoke<T0, T1, T2>(UnrealObjectHandle target, ulong memberId, string memberPath, T0 argument0, T1 argument1, T2 argument2) =>
        Typed.Invoke(target, memberId, memberPath, argument0, argument1, argument2);

    public void Invoke<T0, T1, T2, T3>(UnrealObjectHandle target, ulong memberId, string memberPath, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Typed.Invoke(target, memberId, memberPath, argument0, argument1, argument2, argument3);

    public UnrealOutputInvocationResult InvokeWithOutputs(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        Type returnType,
        IReadOnlyList<UnrealOutputArgument> arguments) =>
        Output.InvokeWithOutputs(target, memberId, memberPath, returnType, arguments);

    public IUnrealEventSubscription Subscribe(
        UnrealObjectHandle source,
        UnrealEventDescriptor descriptor,
        Delegate handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_inner is not IUnrealEventBridge eventBridge)
        {
            throw new NotSupportedException(
                "The World bridge does not support reflected delegate subscriptions.");
        }
        IUnrealEventSubscription subscription =
            eventBridge.Subscribe(source, descriptor, handler);
        _subscriptions.Add(subscription);
        return subscription;
    }

    public void ClearSubscriptions()
    {
        List<Exception>? failures = null;
        for (int index = _subscriptions.Count - 1; index >= 0; index--)
        {
            try
            {
                _subscriptions[index].Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        _subscriptions.Clear();
        if (failures is not null)
        {
            throw new AggregateException(
                "One or more reflected event subscriptions failed to release.",
                failures);
        }
    }

    public void Dispose()
    {
        lock (_nativeObjectGate)
        {
            if (_disposed) return;
            // A worker-thread native release is rejected and must remain retryable.
            _callbacks?.Dispose();
            _nativeObjects?.Dispose();
            _disposed = true;
        }
        ClearSubscriptions();
    }

    private IUnrealInvocationBridge LiveInner
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _inner;
        }
    }

    private IUnrealFastInvocationBridge Fast =>
        LiveInner as IUnrealFastInvocationBridge ??
        throw new NotSupportedException("The World bridge does not support fast invocation.");

    private IUnrealTypedInvocationBridge Typed =>
        LiveInner as IUnrealTypedInvocationBridge ??
        throw new NotSupportedException("The World bridge does not support typed invocation.");

    private IUnrealOutputInvocationBridge Output =>
        LiveInner as IUnrealOutputInvocationBridge ??
        throw new NotSupportedException("The World bridge does not support output parameters.");
}
