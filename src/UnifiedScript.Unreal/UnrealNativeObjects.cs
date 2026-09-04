using System.Runtime.InteropServices;

namespace UnifiedScript.Unreal;

public enum UnrealNativeObjectStatus : byte
{
    Success = 0, Unavailable = 1, WrongThread = 2, InvalidScope = 3,
    InvalidHandle = 4, WrongType = 5, WrongOwner = 6, InvalidArgument = 7,
    UnknownMember = 8, DuplicateMember = 9, Exhausted = 10,
}

/// <summary>Non-UObject identity. Never a pointer and never interchangeable with UnrealObjectHandle.</summary>
public readonly record struct UnrealNativeObjectHandle(ulong Value);

public readonly record struct UnrealNativeObjectCallResult(
    UnrealNativeValueV3 Value, UnrealNativeObjectHandle Handle);

public sealed class UnrealNativeObjectException(UnrealNativeObjectStatus status, string operation)
    : InvalidOperationException($"Native object {operation} failed: {status}.")
{
    public UnrealNativeObjectStatus Status { get; } = status;
}

/// <summary>Optional native-object transport, distinct from reflected UObject invocation.</summary>
public interface IUnrealNativeObjectTransport
{
    bool SupportsNativeObjects { get; }
    UnrealNativeObjectStatus OpenNativeObjectScope(out ulong scope);
    UnrealNativeObjectStatus CloseNativeObjectScope(ulong scope);
    UnrealNativeObjectStatus ReleaseNativeObject(ulong scope, UnrealNativeObjectHandle handle);
    UnrealNativeObjectStatus InvokeNativeObject(ulong scope, UnrealNativeObjectHandle target,
        ulong memberId, ReadOnlySpan<UnrealNativeValueV3> arguments, out UnrealNativeObjectCallResult result);
}

public interface IUnrealNativeObjectProvider
{
    UnrealNativeObjectScope NativeObjects { get; }
}

internal interface IUnrealNativeObjectScopeFactory
{
    UnrealNativeObjectScope CreateNativeObjectScope();
}

/// <summary>
/// One script generation's native ownership domain. The World transport also
/// tracks this scope, so World teardown cleans up even when managed code forgets
/// Dispose. Disposal is game-thread-only; a failed worker disposal can be retried.
/// </summary>
public sealed class UnrealNativeObjectScope : IDisposable
{
    private readonly IUnrealNativeObjectTransport _transport;
    private readonly ulong _scope;
    private readonly object _gate = new();
    private bool _disposed;

    public UnrealNativeObjectScope(IUnrealNativeObjectTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (!transport.SupportsNativeObjects)
            throw new NotSupportedException("The installed runtime does not provide native object bindings.");
        RequireSuccess(transport.OpenNativeObjectScope(out _scope), "open scope");
        if (_scope == 0) throw new InvalidOperationException("Native object transport returned an empty scope.");
    }

    public static UnrealNativeObjectScope FromBridge(IUnrealInvocationBridge bridge) =>
        bridge is IUnrealNativeObjectProvider provider ? provider.NativeObjects :
        throw new NotSupportedException("The bridge does not provide native object bindings.");

    public UnrealNativeObjectLease Construct(ulong constructorId, ReadOnlySpan<UnrealNativeValueV3> arguments) =>
        Acquire(default, constructorId, arguments);

    internal UnrealNativeObjectLease Acquire(UnrealNativeObjectHandle target, ulong memberId,
        ReadOnlySpan<UnrealNativeValueV3> arguments)
    {
        var result = Invoke(target, memberId, arguments);
        if (result.Handle.Value == 0)
            throw new InvalidOperationException("The native constructor/borrow member returned no object.");
        return new UnrealNativeObjectLease(this, result.Handle);
    }

    public UnrealNativeObjectCallResult Invoke(UnrealNativeObjectHandle target, ulong memberId,
        ReadOnlySpan<UnrealNativeValueV3> arguments)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireSuccess(_transport.InvokeNativeObject(_scope, target, memberId, arguments, out var result), "invoke");
            return result;
        }
    }

    internal void Release(UnrealNativeObjectHandle handle)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var status = _transport.ReleaseNativeObject(_scope, handle);
            // Owner release or World teardown may already have invalidated a borrow.
            if (status is not (UnrealNativeObjectStatus.InvalidHandle or UnrealNativeObjectStatus.InvalidScope or
                UnrealNativeObjectStatus.Unavailable)) RequireSuccess(status, "release");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var status = _transport.CloseNativeObjectScope(_scope);
            if (status is not (UnrealNativeObjectStatus.InvalidScope or UnrealNativeObjectStatus.Unavailable))
                RequireSuccess(status, "close scope");
            _disposed = true;
        }
    }

    internal static void RequireSuccess(UnrealNativeObjectStatus status, string operation)
    {
        if (status != UnrealNativeObjectStatus.Success) throw new UnrealNativeObjectException(status, operation);
    }
}

/// <summary>Disposing an owned lease destroys it; disposing a borrow only retires that alias.</summary>
public sealed class UnrealNativeObjectLease : IDisposable
{
    private readonly UnrealNativeObjectScope _scope;
    private readonly object _gate = new();
    private bool _disposed;
    public UnrealNativeObjectHandle Handle { get; }

    internal UnrealNativeObjectLease(UnrealNativeObjectScope scope, UnrealNativeObjectHandle handle)
    {
        _scope = scope;
        Handle = handle;
    }

    public UnrealNativeObjectCallResult Invoke(ulong memberId, ReadOnlySpan<UnrealNativeValueV3> arguments)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _scope.Invoke(Handle, memberId, arguments);
        }
    }

    public UnrealNativeObjectLease Borrow(ulong memberId, ReadOnlySpan<UnrealNativeValueV3> arguments)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _scope.Acquire(Handle, memberId, arguments);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _scope.Release(Handle);
            _disposed = true;
        }
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealNativeObjectTransportTableV1
{
    public uint AbiVersion, TableSize, ValueSize, Reserved;
    public nint RetainContext, ReleaseContext, OpenScope, CloseScope, ReleaseObject, Invoke;
}
