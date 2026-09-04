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

[AttributeUsage(AttributeTargets.Class)]
public sealed class UnrealNativeObjectTypeAttribute(string path) : Attribute
{
    public string Path { get; } = path;
}

/// <summary>Base for generated ordinary C++ wrappers, deliberately not an UnrealObject.</summary>
public abstract class UnrealNativeObject : IDisposable
{
    protected UnrealNativeObject(UnrealNativeObjectLease lease) => Lease = lease ?? throw new ArgumentNullException(nameof(lease));
    protected UnrealNativeObjectLease Lease { get; }
    internal UnrealNativeObjectLease ArgumentLease => Lease;
    public UnrealNativeObjectHandle Handle => Lease.Handle;
    public void Dispose() => Lease.Dispose();
}

public static class UnrealNativeObjectValues
{
    public static UnrealNativeValueV3 Encode<T>(T value)
    {
        int bytes = 0;
        if (UnrealNativeValueCodecV3.TryEncode(value, Span<byte>.Empty, ref bytes, out var result, out _) && bytes == 0 && IsFixed(result))
            return result;
        throw new NotSupportedException($"'{typeof(T)}' is not a native-object fixed value.");
    }
    public static T Decode<T>(UnrealNativeValueV3 value) =>
        IsFixed(value) && UnrealNativeValueCodecV3.TryDecode<T>(value, ReadOnlySpan<byte>.Empty, 0, out var result)
            ? result : throw new InvalidOperationException($"Native-object result is not '{typeof(T)}'.");
    private static bool IsFixed(UnrealNativeValueV3 value) =>
        value.Flags == 0 && value.Reserved16 == 0 && value.Auxiliary == 0 &&
        (value.Kind is >= UnrealNativeValueKindV3.Boolean and <= UnrealNativeValueKindV3.Enum or
            UnrealNativeValueKindV3.Vector3 or UnrealNativeValueKindV3.Rotator3);
}

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
    // Older transports remain usable for fixed values but cannot accept object cells.
    bool SupportsNativeObjectArguments => false;
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

    /// <summary>Encode a synchronous, non-transferring argument in this exact ownership scope.</summary>
    public UnrealNativeValueV3 EncodeObject(UnrealNativeObject? value, ulong typeId, bool allowNull = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_transport.SupportsNativeObjectArguments)
                throw new NotSupportedException("The installed native-object transport does not support object arguments.");
        }
        if (typeId == 0) throw new ArgumentOutOfRangeException(nameof(typeId));
        if (value is null && !allowNull) throw new ArgumentNullException(nameof(value));
        // Do not nest the scope lock around a lease lock; invocation/disposal
        // locks the lease first. Native resolution checks liveness again at use.
        return new UnrealNativeValueV3
        {
            Kind = UnrealNativeValueKindV3.NativeObject,
            TypeId = typeId,
            Flags = value is null ? UnrealNativeValueFlagsV3.Null : UnrealNativeValueFlagsV3.None,
            Payload0 = value?.ArgumentLease.ArgumentHandle(this).Value ?? 0,
        };
    }

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

    internal UnrealNativeObjectHandle ArgumentHandle(UnrealNativeObjectScope expectedScope)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ReferenceEquals(_scope, expectedScope))
                throw new UnrealNativeObjectException(UnrealNativeObjectStatus.WrongOwner, "argument scope");
            return Handle;
        }
    }

    public UnrealNativeValueV3 EncodeObject(UnrealNativeObject? value, ulong typeId, bool allowNull = false) =>
        _scope.EncodeObject(value, typeId, allowNull);

    public UnrealNativeObjectCallResult Invoke(ulong memberId, ReadOnlySpan<UnrealNativeValueV3> arguments)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _scope.Invoke(Handle, memberId, arguments);
        }
    }

    public UnrealNativeObjectLease Borrow(ulong memberId, ReadOnlySpan<UnrealNativeValueV3> arguments)
        => Acquire(memberId, arguments);

    /// <summary>Acquire a returned object; the native declaration determines owned vs borrowed.</summary>
    public UnrealNativeObjectLease Acquire(ulong memberId, ReadOnlySpan<UnrealNativeValueV3> arguments)
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

/// <summary>Optional v1 tail; bit 0 admits typed object argument cells.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealNativeObjectTransportTableWithCapabilities
{
    public UnrealNativeObjectTransportTableV1 Base;
    public ulong Capabilities;
}
