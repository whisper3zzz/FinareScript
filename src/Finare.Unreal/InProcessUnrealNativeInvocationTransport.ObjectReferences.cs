using System.Runtime.InteropServices;

namespace Finare.Unreal;

public sealed partial class InProcessUnrealNativeInvocationTransport : IUnrealObjectReferenceTransport
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate UnrealObjectReferenceStatus NativeOpenObjectReferenceScope(nint context, out ulong scope);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate UnrealObjectReferenceStatus NativeCloseObjectReferenceScope(nint context, ulong scope);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate UnrealObjectReferenceStatus NativeObjectReferenceOperation(
        nint context, ulong scope, ulong handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTransportTableV2ObjectReferences
    {
        public NativeTransportTableV2NativeObjects Base;
        public nint CallbackTransport;
        public nint ObjectReferenceTransport;
    }

    private NativeOpenObjectReferenceScope? _openObjectReferenceScope;
    private NativeCloseObjectReferenceScope? _closeObjectReferenceScope;
    private NativeObjectReferenceOperation? _retainObjectReference;
    private NativeObjectReferenceOperation? _releaseObjectReference;
    private readonly HashSet<ulong> _objectReferenceScopes = [];

    private void LoadObjectReferences(nint tablePointer, NativeTransportTableV1 parent)
    {
        if (parent.AbiVersion != 2 ||
            parent.TableSize < Marshal.SizeOf<NativeTransportTableV2ObjectReferences>())
        {
            return;
        }
        NativeTransportTableV2ObjectReferences extension =
            Marshal.PtrToStructure<NativeTransportTableV2ObjectReferences>(tablePointer);
        if (extension.ObjectReferenceTransport == nint.Zero) return;
        NativeNativeObjectReferenceHeader header = Marshal.PtrToStructure<NativeNativeObjectReferenceHeader>(
            extension.ObjectReferenceTransport);
        uint baseSize = checked((uint)Marshal.SizeOf<UnrealNativeObjectReferenceTransportTableV1>());
        if (header.AbiVersion != 1 || header.TableSize < baseSize || header.ValueSize != 0 || header.Reserved != 0)
        {
            throw new InvalidOperationException("The optional UObject reference ABI has an incompatible layout.");
        }
        UnrealNativeObjectReferenceTransportTableV1 table =
            Marshal.PtrToStructure<UnrealNativeObjectReferenceTransportTableV1>(extension.ObjectReferenceTransport);
        if (table.RetainContext != parent.RetainContext || table.ReleaseContext != parent.ReleaseContext ||
            table.OpenScope == nint.Zero || table.CloseScope == nint.Zero ||
            table.Retain == nint.Zero || table.Release == nint.Zero)
        {
            throw new InvalidOperationException("The optional UObject reference table is incomplete or belongs to another context.");
        }
        _openObjectReferenceScope = Marshal.GetDelegateForFunctionPointer<NativeOpenObjectReferenceScope>(table.OpenScope);
        _closeObjectReferenceScope = Marshal.GetDelegateForFunctionPointer<NativeCloseObjectReferenceScope>(table.CloseScope);
        _retainObjectReference = Marshal.GetDelegateForFunctionPointer<NativeObjectReferenceOperation>(table.Retain);
        _releaseObjectReference = Marshal.GetDelegateForFunctionPointer<NativeObjectReferenceOperation>(table.Release);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeNativeObjectReferenceHeader
    {
        public uint AbiVersion, TableSize, ValueSize, Reserved;
    }

    public bool SupportsObjectReferences => !_disposed &&
        _openObjectReferenceScope is not null && _closeObjectReferenceScope is not null &&
        _retainObjectReference is not null && _releaseObjectReference is not null;

    public UnrealObjectReferenceStatus OpenObjectReferenceScope(out ulong scope)
    {
        lock (_lifetimeGate)
        {
            scope = 0;
            if (_disposed || _openObjectReferenceScope is null) return UnrealObjectReferenceStatus.Unavailable;
            UnrealObjectReferenceStatus status = _openObjectReferenceScope(_context, out scope);
            if (status == UnrealObjectReferenceStatus.Success && (scope == 0 || !_objectReferenceScopes.Add(scope)))
                throw new InvalidOperationException("The native UObject reference transport returned an invalid scope.");
            return status;
        }
    }

    public UnrealObjectReferenceStatus CloseObjectReferenceScope(ulong scope)
    {
        lock (_lifetimeGate)
        {
            if (_disposed || _closeObjectReferenceScope is null) return UnrealObjectReferenceStatus.Unavailable;
            if (!_objectReferenceScopes.Contains(scope)) return UnrealObjectReferenceStatus.InvalidScope;
            UnrealObjectReferenceStatus status = _closeObjectReferenceScope(_context, scope);
            if (status is UnrealObjectReferenceStatus.Success or UnrealObjectReferenceStatus.InvalidScope or UnrealObjectReferenceStatus.Unavailable)
                _objectReferenceScopes.Remove(scope);
            return status;
        }
    }

    public UnrealObjectReferenceStatus RetainObjectReference(ulong scope, UnrealObjectHandle handle)
    {
        lock (_lifetimeGate)
        {
            if (_disposed || _retainObjectReference is null) return UnrealObjectReferenceStatus.Unavailable;
            if (!_objectReferenceScopes.Contains(scope)) return UnrealObjectReferenceStatus.InvalidScope;
            return _retainObjectReference(_context, scope, handle.Value);
        }
    }

    public UnrealObjectReferenceStatus ReleaseObjectReference(ulong scope, UnrealObjectHandle handle)
    {
        lock (_lifetimeGate)
        {
            if (_disposed || _releaseObjectReference is null) return UnrealObjectReferenceStatus.Unavailable;
            if (!_objectReferenceScopes.Contains(scope)) return UnrealObjectReferenceStatus.InvalidScope;
            return _releaseObjectReference(_context, scope, handle.Value);
        }
    }

    private void CloseObjectReferenceScopes()
    {
        foreach (ulong scope in _objectReferenceScopes.ToArray())
        {
            UnrealObjectReferenceStatus status = CloseObjectReferenceScope(scope);
            if (status is not (UnrealObjectReferenceStatus.Success or UnrealObjectReferenceStatus.InvalidScope or UnrealObjectReferenceStatus.Unavailable))
                throw new InvalidOperationException($"Unable to close UObject reference scope: {status}.");
        }
    }
}
