using System.Runtime.InteropServices;

namespace UnifiedScript.Unreal;

public sealed partial class InProcessUnrealNativeInvocationTransport : IUnrealNativeObjectTransport
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate UnrealNativeObjectStatus NativeOpenScope(nint context, out ulong scope);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate UnrealNativeObjectStatus NativeCloseScope(nint context, ulong scope);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate UnrealNativeObjectStatus NativeReleaseObject(nint context, ulong scope, ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate UnrealNativeObjectStatus NativeInvokeObject(nint context, ulong scope,
        ulong target, ulong memberId, UnrealNativeValueV3* arguments, uint count,
        out UnrealNativeValueV3 value, out ulong handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTransportTableV2NativeObjects
    {
        public NativeTransportTableV2AsyncServices Base;
        public nint NativeObjectTransport;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeObjectHeader { public uint AbiVersion, TableSize, ValueSize, Reserved; }

    private NativeOpenScope? _nativeOpenScope;
    private NativeCloseScope? _nativeCloseScope;
    private NativeReleaseObject? _nativeReleaseObject;
    private NativeInvokeObject? _nativeInvokeObject;
    private readonly HashSet<ulong> _nativeObjectScopes = [];

    private void LoadNativeObjects(nint tablePointer, NativeTransportTableV1 parent)
    {
        if (parent.AbiVersion != 2 || parent.TableSize < Marshal.SizeOf<NativeTransportTableV2NativeObjects>()) return;
        var extension = Marshal.PtrToStructure<NativeTransportTableV2NativeObjects>(tablePointer);
        if (extension.NativeObjectTransport == 0) return;
        var header = Marshal.PtrToStructure<NativeObjectHeader>(extension.NativeObjectTransport);
        if (header.AbiVersion != 1 || header.TableSize < Marshal.SizeOf<UnrealNativeObjectTransportTableV1>() ||
            header.ValueSize != UnrealNativeAbiV3.ValueSize || header.Reserved != 0)
            throw new InvalidOperationException("The native object ownership ABI has an incompatible layout.");
        var table = Marshal.PtrToStructure<UnrealNativeObjectTransportTableV1>(extension.NativeObjectTransport);
        if (table.RetainContext != parent.RetainContext || table.ReleaseContext != parent.ReleaseContext ||
            table.OpenScope == 0 || table.CloseScope == 0 || table.ReleaseObject == 0 || table.Invoke == 0)
            throw new InvalidOperationException("The native object ownership table is incomplete or belongs to another context ABI.");
        _nativeOpenScope = Marshal.GetDelegateForFunctionPointer<NativeOpenScope>(table.OpenScope);
        _nativeCloseScope = Marshal.GetDelegateForFunctionPointer<NativeCloseScope>(table.CloseScope);
        _nativeReleaseObject = Marshal.GetDelegateForFunctionPointer<NativeReleaseObject>(table.ReleaseObject);
        _nativeInvokeObject = Marshal.GetDelegateForFunctionPointer<NativeInvokeObject>(table.Invoke);
    }

    public bool SupportsNativeObjects => !_disposed && _nativeOpenScope is not null;

    public UnrealNativeObjectStatus OpenNativeObjectScope(out ulong scope)
    {
        lock (_lifetimeGate)
        {
            scope = 0;
            if (_disposed || _nativeOpenScope is null) return UnrealNativeObjectStatus.Unavailable;
            var status = _nativeOpenScope(_context, out scope);
            if (status == UnrealNativeObjectStatus.Success) _nativeObjectScopes.Add(scope);
            return status;
        }
    }

    public UnrealNativeObjectStatus CloseNativeObjectScope(ulong scope)
    {
        lock (_lifetimeGate)
        {
            if (_disposed || _nativeCloseScope is null) return UnrealNativeObjectStatus.Unavailable;
            if (!_nativeObjectScopes.Contains(scope)) return UnrealNativeObjectStatus.InvalidScope;
            var status = _nativeCloseScope(_context, scope);
            if (status is UnrealNativeObjectStatus.Success or UnrealNativeObjectStatus.InvalidScope or UnrealNativeObjectStatus.Unavailable)
                _nativeObjectScopes.Remove(scope);
            return status;
        }
    }

    public UnrealNativeObjectStatus ReleaseNativeObject(ulong scope, UnrealNativeObjectHandle handle)
    {
        lock (_lifetimeGate)
        {
            if (_disposed || _nativeReleaseObject is null) return UnrealNativeObjectStatus.Unavailable;
            if (!_nativeObjectScopes.Contains(scope)) return UnrealNativeObjectStatus.InvalidScope;
            return _nativeReleaseObject(_context, scope, handle.Value);
        }
    }

    public unsafe UnrealNativeObjectStatus InvokeNativeObject(ulong scope, UnrealNativeObjectHandle target,
        ulong memberId, ReadOnlySpan<UnrealNativeValueV3> arguments, out UnrealNativeObjectCallResult result)
    {
        lock (_lifetimeGate)
        {
            result = default;
            if (_disposed || _nativeInvokeObject is null) return UnrealNativeObjectStatus.Unavailable;
            if (!_nativeObjectScopes.Contains(scope)) return UnrealNativeObjectStatus.InvalidScope;
            if (arguments.Length > 16) return UnrealNativeObjectStatus.InvalidArgument;
            fixed (UnrealNativeValueV3* pointer = arguments)
            {
                var status = _nativeInvokeObject(_context, scope, target.Value, memberId, pointer,
                    (uint)arguments.Length, out var value, out ulong handle);
                if (status == UnrealNativeObjectStatus.Success) result = new(value, new(handle));
                return status;
            }
        }
    }

    private void CloseNativeObjectScopes()
    {
        foreach (ulong scope in _nativeObjectScopes.ToArray())
        {
            var status = CloseNativeObjectScope(scope);
            if (status is not (UnrealNativeObjectStatus.InvalidScope or UnrealNativeObjectStatus.Unavailable))
                UnrealNativeObjectScope.RequireSuccess(status, "dispose transport scope");
        }
    }
}
