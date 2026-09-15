using System.Runtime.InteropServices;

namespace Finare.Unreal;

public sealed partial class InProcessUnrealNativeInvocationTransport : IUnrealNativeObjectTransport
{
    private const uint MaximumTextBytes = 16u * 1024u * 1024u;
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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate UnrealNativeObjectStatus NativeBeginText(nint context, ulong scope,
        ulong target, ulong memberId, UnrealNativeValueV3* arguments, uint count, byte* input, uint inputBytes,
        out ulong token, out uint requiredBytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate UnrealNativeObjectStatus NativeReadText(nint context, ulong scope, ulong token,
        byte* output, uint capacity, out uint requiredBytes, out UnrealNativeValueV3 value, out ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate UnrealNativeObjectStatus NativeDiscardText(nint context, ulong scope, ulong token);

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
    private bool _nativeObjectArguments;
    private NativeBeginText? _nativeBeginText;
    private NativeReadText? _nativeReadText;
    private NativeDiscardText? _nativeDiscardText;
    private uint _nativeTextMaxInputBytes, _nativeTextMaxOutputBytes;
    private nint _nativeObjectBorrow;
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeObjectMemoryTable { public UnrealNativeObjectTransportTableWithText Base; public nint Borrow; }
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
        if (header.TableSize >= Marshal.SizeOf<NativeObjectMemoryTable>())
        {
            var memory = Marshal.PtrToStructure<NativeObjectMemoryTable>(extension.NativeObjectTransport);
            if ((memory.Base.Base.Capabilities & 4UL) != 0)
                _nativeObjectBorrow = memory.Borrow != 0 ? memory.Borrow : throw new InvalidOperationException("Native memory transport is incomplete.");
        }
        if (table.RetainContext != parent.RetainContext || table.ReleaseContext != parent.ReleaseContext ||
            table.OpenScope == 0 || table.CloseScope == 0 || table.ReleaseObject == 0 || table.Invoke == 0)
            throw new InvalidOperationException("The native object ownership table is incomplete or belongs to another context ABI.");
        _nativeOpenScope = Marshal.GetDelegateForFunctionPointer<NativeOpenScope>(table.OpenScope);
        _nativeCloseScope = Marshal.GetDelegateForFunctionPointer<NativeCloseScope>(table.CloseScope);
        _nativeReleaseObject = Marshal.GetDelegateForFunctionPointer<NativeReleaseObject>(table.ReleaseObject);
        _nativeInvokeObject = Marshal.GetDelegateForFunctionPointer<NativeInvokeObject>(table.Invoke);
        if (header.TableSize >= Marshal.SizeOf<UnrealNativeObjectTransportTableWithCapabilities>())
            _nativeObjectArguments = (Marshal.PtrToStructure<UnrealNativeObjectTransportTableWithCapabilities>(extension.NativeObjectTransport).Capabilities & 1UL) != 0;
        if (header.TableSize >= Marshal.SizeOf<UnrealNativeObjectTransportTableWithText>())
        {
            var extended = Marshal.PtrToStructure<UnrealNativeObjectTransportTableWithText>(extension.NativeObjectTransport);
            if ((extended.Base.Capabilities & 2UL) != 0 && extended.TextTransport != 0)
            {
                var textHeader = Marshal.PtrToStructure<NativeObjectHeader>(extended.TextTransport);
                if (textHeader.AbiVersion != 1 || textHeader.TableSize < Marshal.SizeOf<UnrealNativeObjectTextTransportTableV1>() ||
                    textHeader.ValueSize != UnrealNativeAbiV3.ValueSize || textHeader.Reserved != 0)
                    throw new InvalidOperationException("The native text tail has an incompatible layout.");
                var text = Marshal.PtrToStructure<UnrealNativeObjectTextTransportTableV1>(extended.TextTransport);
                if (text.MaxInputBytes == 0 || text.MaxOutputBytes == 0 || text.MaxPendingBytes == 0 || text.MaxPendingCount == 0 ||
                    text.MaxInputBytes > MaximumTextBytes || text.MaxOutputBytes > MaximumTextBytes || text.MaxPendingBytes > MaximumTextBytes ||
                    text.Begin == 0 || text.Read == 0 || text.Discard == 0)
                    throw new InvalidOperationException("The native text tail is incomplete or unbounded.");
                _nativeBeginText = Marshal.GetDelegateForFunctionPointer<NativeBeginText>(text.Begin);
                _nativeReadText = Marshal.GetDelegateForFunctionPointer<NativeReadText>(text.Read);
                _nativeDiscardText = Marshal.GetDelegateForFunctionPointer<NativeDiscardText>(text.Discard);
                _nativeTextMaxInputBytes = text.MaxInputBytes;
                _nativeTextMaxOutputBytes = text.MaxOutputBytes;
            }
        }
    }

    public bool SupportsNativeObjects => !_disposed && _nativeOpenScope is not null;
    public bool SupportsNativeObjectArguments => SupportsNativeObjects && _nativeObjectArguments;
    public bool SupportsNativeObjectText => SupportsNativeObjects && _nativeBeginText is not null &&
        _nativeReadText is not null && _nativeDiscardText is not null;
    public uint NativeObjectTextMaxInputBytes => _nativeTextMaxInputBytes;
    public uint NativeObjectTextMaxOutputBytes => _nativeTextMaxOutputBytes;

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
            if (!_nativeObjectArguments)
                foreach (var argument in arguments)
                    if (argument.Kind == UnrealNativeValueKindV3.NativeObject) return UnrealNativeObjectStatus.Unavailable;
            fixed (UnrealNativeValueV3* pointer = arguments)
            {
                var status = _nativeInvokeObject(_context, scope, target.Value, memberId, pointer,
                    (uint)arguments.Length, out var value, out ulong handle);
                if (status == UnrealNativeObjectStatus.Success) result = new(value, new(handle));
                return status;
            }
        }
    }

    public unsafe UnrealNativeObjectStatus BeginNativeObjectText(ulong scope, UnrealNativeObjectHandle target, ulong memberId,
        ReadOnlySpan<UnrealNativeValueV3> arguments, ReadOnlySpan<byte> input, out ulong token, out uint requiredBytes)
    {
        lock (_lifetimeGate)
        {
            token = 0; requiredBytes = 0;
            if (_disposed || _nativeBeginText is null) return UnrealNativeObjectStatus.TextUnavailable;
            if (!_nativeObjectScopes.Contains(scope) || arguments.Length > 16 || input.Length > _nativeTextMaxInputBytes)
                return !_nativeObjectScopes.Contains(scope) ? UnrealNativeObjectStatus.InvalidScope : UnrealNativeObjectStatus.InvalidArgument;
            if (!_nativeObjectArguments)
                foreach (var argument in arguments)
                    if (argument.Kind == UnrealNativeValueKindV3.NativeObject) return UnrealNativeObjectStatus.TextUnavailable;
            fixed (UnrealNativeValueV3* argumentPointer = arguments)
            fixed (byte* inputPointer = input)
            {
                var status = _nativeBeginText(_context, scope, target.Value, memberId, argumentPointer,
                    (uint)arguments.Length, inputPointer, (uint)input.Length, out token, out requiredBytes);
                if (status == UnrealNativeObjectStatus.Success)
                {
                    if (requiredBytes > _nativeTextMaxOutputBytes || token == 0)
                    {
                        if (token != 0 && _nativeDiscardText is not null)
                            _nativeDiscardText(_context, scope, token);
                        token = 0;
                        return UnrealNativeObjectStatus.InvalidArgument;
                    }
                }
                return status;
            }
        }
    }

    public unsafe UnrealNativeObjectStatus ReadNativeObjectText(ulong scope, ulong token, Span<byte> output,
        out uint requiredBytes, out UnrealNativeObjectCallResult result)
    {
        lock (_lifetimeGate)
        {
            requiredBytes = 0; result = default;
            if (_disposed || _nativeReadText is null) return UnrealNativeObjectStatus.TextUnavailable;
            if (!_nativeObjectScopes.Contains(scope) || output.Length > _nativeTextMaxOutputBytes)
                return !_nativeObjectScopes.Contains(scope) ? UnrealNativeObjectStatus.InvalidScope : UnrealNativeObjectStatus.InvalidArgument;
            fixed (byte* outputPointer = output)
            {
                var status = _nativeReadText(_context, scope, token, outputPointer, (uint)output.Length,
                    out requiredBytes, out var value, out var handle);
                if (status == UnrealNativeObjectStatus.Success) result = new(value, new(handle));
                return status;
            }
        }
    }

    public UnrealNativeObjectStatus DiscardNativeObjectText(ulong scope, ulong token)
    {
        lock (_lifetimeGate)
        {
            if (_disposed || _nativeDiscardText is null) return UnrealNativeObjectStatus.TextUnavailable;
            if (!_nativeObjectScopes.Contains(scope)) return UnrealNativeObjectStatus.InvalidScope;
            return _nativeDiscardText(_context, scope, token);
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
