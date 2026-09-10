using System.Reflection;

namespace Finare.Unreal;

public enum UnrealCallbackStatus : byte
{
    Success, Unavailable, WrongThread, InvalidScope, InvalidHandle,
    WrongSignature, WrongOwner, InvalidArgument, HandlerFailed, Exhausted,
}

public sealed class UnrealCallbackException(UnrealCallbackStatus status, string operation)
    : InvalidOperationException($"Unreal callback {operation} failed: {status}.")
{
    public UnrealCallbackStatus Status { get; } = status;
}

[AttributeUsage(AttributeTargets.Delegate)]
public sealed class UnrealCallbackTypeAttribute(string signaturePath) : Attribute
{
    public string SignaturePath { get; } = signaturePath;
}

public sealed record UnrealCallbackParameter(Type Type, UnrealParameterDirection Direction);

public sealed record UnrealCallbackDescriptor(
    string SignaturePath, Type ReturnType, IReadOnlyList<UnrealCallbackParameter> Parameters)
{
    public ulong SignatureId => UnrealMemberId.FromPath(SignaturePath);

    public static UnrealCallbackDescriptor For<T>() where T : Delegate
    {
        var type = typeof(T);
        var attribute = type.GetCustomAttribute<UnrealCallbackTypeAttribute>() ??
            throw new ArgumentException($"'{type}' is not a generated Unreal callback delegate.");
        var invoke = type.GetMethod("Invoke")!;
        if (string.IsNullOrWhiteSpace(attribute.SignaturePath) || !attribute.SignaturePath.StartsWith('/') ||
            invoke.ReturnType.IsByRef || invoke.GetParameters().Length > 16)
            throw new ArgumentException("Invalid Unreal callback signature.");
        var parameters = invoke.GetParameters().Select(p => new UnrealCallbackParameter(
            p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType,
            p.IsOut ? UnrealParameterDirection.Output : p.ParameterType.IsByRef
                ? UnrealParameterDirection.InputOutput : UnrealParameterDirection.Input)).ToArray();
        foreach (var valueType in parameters.Select(p => p.Type).Append(invoke.ReturnType).Where(t => t != typeof(void)))
            if (!UnrealNativeValueCodecV3.TryCreateResultDescriptor(valueType, out _, out _))
                throw new NotSupportedException($"Callback value type '{valueType}' has no ABI v3 codec.");
        return new(attribute.SignaturePath, invoke.ReturnType, parameters);
    }
}

/// <summary>Optional synchronous reverse-call transport; independent of event notifications.</summary>
public interface IUnrealNativeCallbackTransport
{
    bool SupportsCallbacks { get; }
    UnrealCallbackStatus OpenCallbackScope(out ulong scope);
    UnrealCallbackStatus CloseCallbackScope(ulong scope);
    UnrealCallbackStatus RegisterCallback(ulong scope, UnrealCallbackDescriptor descriptor, Delegate handler, out ulong token);
    UnrealCallbackStatus ReleaseCallback(ulong scope, ulong token);
}

public interface IUnrealCallbackProvider
{
    UnrealCallbackScope Callbacks { get; }
}

internal interface IUnrealCallbackScopeFactory
{
    UnrealCallbackScope CreateCallbackScope();
}

/// <summary>A generation's callbacks. Disposal disables native copies before releasing managed handlers.</summary>
public sealed class UnrealCallbackScope : IDisposable
{
    private readonly IUnrealNativeCallbackTransport _transport;
    private readonly object _gate = new();
    private readonly ulong _scope;
    private bool _disposed;

    public UnrealCallbackScope(IUnrealNativeCallbackTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (!transport.SupportsCallbacks) throw new NotSupportedException("The installed runtime does not support typed callbacks.");
        RequireSuccess(transport.OpenCallbackScope(out _scope), "open scope");
        if (_scope == 0) throw new InvalidOperationException("Native callback transport returned an empty scope.");
    }

    public static UnrealCallbackScope FromBridge(IUnrealInvocationBridge bridge) =>
        bridge is IUnrealCallbackProvider provider ? provider.Callbacks :
        throw new NotSupportedException("The bridge does not provide generation-owned callbacks.");

    public UnrealCallback<T> Create<T>(T handler) where T : Delegate
    {
        ArgumentNullException.ThrowIfNull(handler);
        var descriptor = UnrealCallbackDescriptor.For<T>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireSuccess(_transport.RegisterCallback(_scope, descriptor, handler, out ulong token), "register");
            if (token == 0) throw new InvalidOperationException("Native callback transport returned an empty token.");
            return new(this, token, descriptor.SignatureId);
        }
    }

    internal UnrealCallbackArgument Encode(ulong token, ulong signatureId, UnrealCallbackScope caller)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ReferenceEquals(caller, this)) throw new UnrealCallbackException(UnrealCallbackStatus.WrongOwner, "argument scope");
            return new(token, _scope, signatureId);
        }
    }

    internal void Release(ulong token)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var status = _transport.ReleaseCallback(_scope, token);
            if (status is not (UnrealCallbackStatus.Unavailable or UnrealCallbackStatus.InvalidScope or UnrealCallbackStatus.InvalidHandle))
                RequireSuccess(status, "release");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var status = _transport.CloseCallbackScope(_scope);
            if (status is not (UnrealCallbackStatus.Unavailable or UnrealCallbackStatus.InvalidScope)) RequireSuccess(status, "close scope");
            _disposed = true;
        }
    }

    internal static void RequireSuccess(UnrealCallbackStatus status, string operation)
    {
        if (status != UnrealCallbackStatus.Success) throw new UnrealCallbackException(status, operation);
    }
}

/// <summary>Typed retained callback. A native delegate copy is usable only while this lease and its scope are live.</summary>
public sealed class UnrealCallback<T> : IDisposable where T : Delegate
{
    private readonly UnrealCallbackScope _scope;
    private readonly ulong _token, _signatureId;
    private readonly object _gate = new();
    private bool _disposed;

    internal UnrealCallback(UnrealCallbackScope scope, ulong token, ulong signatureId)
    {
        _scope = scope;
        _token = token;
        _signatureId = signatureId;
    }

    public static UnrealCallback<T> Create(IUnrealInvocationBridge bridge, T handler) => UnrealCallbackScope.FromBridge(bridge).Create(handler);

    public UnrealCallbackArgument AsArgument(IUnrealInvocationBridge caller)
    {
        // Acquire the caller scope outside the lease lock, matching generation disposal ordering.
        var callerScope = UnrealCallbackScope.FromBridge(caller);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _scope.Encode(_token, _signatureId, callerScope);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _scope.Release(_token);
            _disposed = true;
        }
    }
}

/// <summary>Call-only value produced by the typed lease after checking its caller's generation.</summary>
public readonly struct UnrealCallbackArgument
{
    internal UnrealCallbackArgument(ulong token, ulong scope, ulong signatureId)
    {
        Token = token;
        Scope = scope;
        SignatureId = signatureId;
    }
    internal ulong Token { get; }
    internal ulong Scope { get; }
    internal ulong SignatureId { get; }
}
