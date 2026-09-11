namespace Finare.Unreal;

using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
internal struct UnrealNativeObjectReferenceTransportTableV1
{
    public uint AbiVersion;
    public uint TableSize;
    public uint ValueSize;
    public uint Reserved;
    public nint RetainContext;
    public nint ReleaseContext;
    public nint OpenScope;
    public nint CloseScope;
    public nint Retain;
    public nint Release;
}

/// <summary>
/// A borrowed UObject handle can be retained for the lifetime of an explicit
/// generation-owned lease. The lease does not root the UObject; Unreal still
/// invalidates it when its world or object is destroyed.
/// </summary>
public sealed class UnrealObjectReference : IDisposable
{
    private readonly UnrealObjectReferenceScope _scope;
    private readonly object _gate = new();
    private readonly UnrealObjectHandle _handle;
    private bool _disposed;

    private UnrealObjectReference(UnrealObjectReferenceScope scope, UnrealObjectHandle handle)
    {
        _scope = scope;
        _handle = handle;
    }

    public UnrealObjectHandle Handle
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed || _scope.IsDisposed, this);
                return _handle;
            }
        }
    }

    public static UnrealObjectReference Retain(
        IUnrealInvocationBridge bridge,
        UnrealObjectHandle handle)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        if (!handle.IsWellFormed)
        {
            throw new ArgumentException("A well-formed Unreal UObject handle is required.", nameof(handle));
        }
        if (bridge is not IUnrealOwnedObjectReferenceScopeFactory factory)
        {
            throw new NotSupportedException(
                "UObject reference leases require a generation-owned Unreal bridge.");
        }
        UnrealObjectReferenceScope scope = factory.GetObjectReferenceScope();
        scope.Retain(handle);
        return new UnrealObjectReference(scope, handle);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Release must use the captured value directly. The public Handle
            // getter intentionally rejects access after a scope was closed,
            // while cleanup after generation teardown is still required.
            if (_scope.Release(_handle)) _disposed = true;
        }
    }
}

public enum UnrealObjectReferenceStatus : byte
{
    Success = 0,
    Unavailable = 1,
    WrongThread = 2,
    InvalidScope = 3,
    InvalidHandle = 4,
    StaleHandle = 5,
    WorldMismatch = 6,
}

internal interface IUnrealObjectReferenceTransport
{
    bool SupportsObjectReferences { get; }
    UnrealObjectReferenceStatus OpenObjectReferenceScope(out ulong scope);
    UnrealObjectReferenceStatus CloseObjectReferenceScope(ulong scope);
    UnrealObjectReferenceStatus RetainObjectReference(ulong scope, UnrealObjectHandle handle);
    UnrealObjectReferenceStatus ReleaseObjectReference(ulong scope, UnrealObjectHandle handle);
}

internal interface IUnrealObjectReferenceScopeFactory
{
    UnrealObjectReferenceScope GetObjectReferenceScope();
}

// The World bridge implements the factory only so a generation can acquire a
// scope. Public leases must never acquire an unowned World scope: a lease can
// outlive the call that created it, while the generation owns cleanup at stop
// and reload.
internal interface IUnrealOwnedObjectReferenceScopeFactory : IUnrealObjectReferenceScopeFactory
{
}

internal sealed class UnrealObjectReferenceScope : IDisposable
{
    private readonly IUnrealObjectReferenceTransport _transport;
    private readonly ulong _scope;
    private readonly object _gate = new();
    private bool _disposed;

    internal bool IsDisposed
    {
        get
        {
            lock (_gate) return _disposed;
        }
    }

    internal UnrealObjectReferenceScope(IUnrealObjectReferenceTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (!_transport.SupportsObjectReferences)
        {
            throw new NotSupportedException(
                "The installed Unreal transport does not support UObject reference leases.");
        }
        UnrealObjectReferenceStatus status = _transport.OpenObjectReferenceScope(out _scope);
        Require(status, "open UObject reference scope");
        if (_scope == 0) throw new InvalidOperationException("The native UObject reference transport returned an empty scope.");
    }

    internal void Retain(UnrealObjectHandle handle)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Require(_transport.RetainObjectReference(_scope, handle), "retain UObject reference");
        }
    }

    internal bool Release(UnrealObjectHandle handle)
    {
        lock (_gate)
        {
            if (_disposed) return true;
            UnrealObjectReferenceStatus status = _transport.ReleaseObjectReference(_scope, handle);
            if (status is not (UnrealObjectReferenceStatus.Success or
                UnrealObjectReferenceStatus.StaleHandle or
                UnrealObjectReferenceStatus.Unavailable or
                UnrealObjectReferenceStatus.InvalidScope))
            {
                throw new InvalidOperationException(
                    $"Unable to release the Unreal UObject reference ({status}).");
            }
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            UnrealObjectReferenceStatus status = _transport.CloseObjectReferenceScope(_scope);
            if (status is not (UnrealObjectReferenceStatus.Success or
                UnrealObjectReferenceStatus.StaleHandle or
                UnrealObjectReferenceStatus.Unavailable or
                UnrealObjectReferenceStatus.InvalidScope))
            {
                throw new InvalidOperationException(
                    $"Unable to close the Unreal UObject reference scope ({status}).");
            }
            _disposed = true;
        }
    }

    private static void Require(UnrealObjectReferenceStatus status, string operation)
    {
        if (status != UnrealObjectReferenceStatus.Success)
        {
            throw new InvalidOperationException($"Unable to {operation}: {status}.");
        }
    }
}
