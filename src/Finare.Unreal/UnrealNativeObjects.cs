using System.Runtime.InteropServices;

namespace Finare.Unreal;

public enum UnrealNativeObjectStatus : byte
{
    Success = 0, Unavailable = 1, WrongThread = 2, InvalidScope = 3,
    InvalidHandle = 4, WrongType = 5, WrongOwner = 6, InvalidArgument = 7,
    UnknownMember = 8, DuplicateMember = 9, Exhausted = 10,
    InvalidTextToken = 11, TextBufferTooSmall = 12, TextUnavailable = 13,
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
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);
    internal const int MaximumTextArenaBytes = 16 * 1024 * 1024;
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

    public static UnrealNativeValueV3 EncodeText(string value, IList<byte> arena)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(arena);
        int offset = arena.Count;
        int byteCount;
        try { byteCount = StrictUtf8.GetByteCount(value); }
        catch (System.Text.EncoderFallbackException ex) { throw new ArgumentException("Native text requires valid Unicode scalar values.", nameof(value), ex); }
        if (byteCount > MaximumTextArenaBytes - arena.Count)
            throw new ArgumentException("Native text input exceeds the bounded arena.", nameof(value));
        byte[] bytes = StrictUtf8.GetBytes(value);
        for (int i = 0; i < bytes.Length; i++) arena.Add(bytes[i]);
        return new UnrealNativeValueV3
        {
            Kind = UnrealNativeValueKindV3.String,
            Flags = UnrealNativeValueFlagsV3.ArenaBacked,
            Payload0 = (ulong)offset,
            Payload1 = (ulong)bytes.Length,
        };
    }

    public static string DecodeText(ReadOnlySpan<byte> bytes) => StrictUtf8.GetString(bytes);
    public static string DecodeText(UnrealNativeValueV3 value, ReadOnlySpan<byte> bytes)
    {
        if (value.Kind != UnrealNativeValueKindV3.String || value.Flags != UnrealNativeValueFlagsV3.ArenaBacked ||
            value.Reserved16 != 0 || value.Auxiliary != 0 || value.TypeId != 0 ||
            value.Payload2 != 0 || value.Payload3 != 0 ||
            value.Payload0 > (ulong)bytes.Length || value.Payload1 > (ulong)bytes.Length - value.Payload0)
            throw new InvalidOperationException("Native text result has an invalid descriptor.");
        return DecodeText(bytes.Slice(checked((int)value.Payload0), checked((int)value.Payload1)));
    }
    private static bool IsFixed(UnrealNativeValueV3 value) =>
        value.Flags == 0 && value.Reserved16 == 0 && value.Auxiliary == 0 &&
        (value.Kind is >= UnrealNativeValueKindV3.Boolean and <= UnrealNativeValueKindV3.Enum or
            UnrealNativeValueKindV3.Vector3 or UnrealNativeValueKindV3.Rotator3);
}

public readonly record struct UnrealNativeObjectCallResult(
    UnrealNativeValueV3 Value, UnrealNativeObjectHandle Handle);

public readonly record struct UnrealNativeObjectTextCallResult(
    UnrealNativeObjectCallResult Call, byte[] Bytes)
{
    public string DecodeString() => UnrealNativeObjectValues.DecodeText(Call.Value, Bytes);
}

/// <summary>Managed UTF-8 arena builder used only by generated text calls.</summary>
public sealed class UnrealNativeObjectTextArguments
{
    private readonly List<UnrealNativeValueV3> _values = [];
    private readonly List<byte> _bytes = [];
    public UnrealNativeValueV3 Add(string value) => UnrealNativeObjectValues.EncodeText(value, _bytes);
    public UnrealNativeValueV3 Add<T>(T value) => UnrealNativeObjectValues.Encode(value);
    public UnrealNativeValueV3 AddObject(UnrealNativeObjectScope scope, UnrealNativeObject? value, ulong typeId, bool allowNull = false) =>
        scope.EncodeObject(value, typeId, allowNull);
    public void AddValue(UnrealNativeValueV3 value) => _values.Add(value);
    public UnrealNativeValueV3[] ToValues() => _values.ToArray();
    public byte[] ToBytes() => _bytes.ToArray();
    internal void AddEncoded(UnrealNativeValueV3 value) => _values.Add(value);
}

public sealed class UnrealNativeObjectException(UnrealNativeObjectStatus status, string operation)
    : InvalidOperationException($"Native object {operation} failed: {status}.")
{
    public UnrealNativeObjectStatus Status { get; } = status;
}

/// <summary>Optional native-object transport, distinct from reflected UObject invocation.</summary>
public interface IUnrealNativeObjectTransport
{
    void BorrowNativeObjectMemory<T>(ulong scope, UnrealNativeObjectHandle target, ulong member, bool write, UnrealSpanAction<T> action) where T : unmanaged =>
        throw new NotSupportedException("The native object transport does not expose contiguous memory.");
    bool SupportsNativeObjects { get; }
    // Older transports remain usable for fixed values but cannot accept object cells.
    bool SupportsNativeObjectArguments => false;
    bool SupportsNativeObjectText => false;
    uint NativeObjectTextMaxInputBytes => 0;
    uint NativeObjectTextMaxOutputBytes => 0;
    UnrealNativeObjectStatus OpenNativeObjectScope(out ulong scope);
    UnrealNativeObjectStatus CloseNativeObjectScope(ulong scope);
    UnrealNativeObjectStatus ReleaseNativeObject(ulong scope, UnrealNativeObjectHandle handle);
    UnrealNativeObjectStatus InvokeNativeObject(ulong scope, UnrealNativeObjectHandle target,
        ulong memberId, ReadOnlySpan<UnrealNativeValueV3> arguments, out UnrealNativeObjectCallResult result);
    UnrealNativeObjectStatus BeginNativeObjectText(ulong scope, UnrealNativeObjectHandle target, ulong memberId,
        ReadOnlySpan<UnrealNativeValueV3> arguments, ReadOnlySpan<byte> input,
        out ulong token, out uint requiredBytes) =>
        FailText(out token, out requiredBytes);
    UnrealNativeObjectStatus ReadNativeObjectText(ulong scope, ulong token, Span<byte> output, out uint requiredBytes,
        out UnrealNativeObjectCallResult result)
    { requiredBytes = 0; result = default; return UnrealNativeObjectStatus.TextUnavailable; }
    UnrealNativeObjectStatus DiscardNativeObjectText(ulong scope, ulong token) =>
        UnrealNativeObjectStatus.TextUnavailable;
    private static UnrealNativeObjectStatus FailText(out ulong token, out uint requiredBytes)
    {
        token = 0; requiredBytes = 0;
        return UnrealNativeObjectStatus.TextUnavailable;
    }
}

public interface IUnrealNativeObjectProvider
{
    UnrealNativeObjectScope NativeObjects { get; }
}

internal interface IUnrealNativeObjectScopeFactory
{
    UnrealNativeObjectScope CreateNativeObjectScope(IUnrealInvocationBridge owner);
}

/// <summary>
/// One script generation's native ownership domain. The World transport also
/// tracks this scope, so World teardown cleans up even when managed code forgets
/// Dispose. Disposal is game-thread-only; a failed worker disposal can be retried.
/// </summary>
public sealed class UnrealNativeObjectScope : IDisposable
{
    private readonly IUnrealNativeObjectTransport _transport;
    private readonly IUnrealInvocationBridge? _bridge;
    private readonly ulong _scope;
    private readonly object _gate = new();
    private bool _disposed;

    public UnrealNativeObjectScope(IUnrealNativeObjectTransport transport, IUnrealInvocationBridge? bridge = null)
    {
        _bridge = bridge;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (!transport.SupportsNativeObjects)
            throw new NotSupportedException("The installed runtime does not provide native object bindings.");
        RequireSuccess(transport.OpenNativeObjectScope(out _scope), "open scope");
        if (_scope == 0) throw new InvalidOperationException("Native object transport returned an empty scope.");
    }

    public static UnrealNativeObjectScope FromBridge(IUnrealInvocationBridge bridge) =>
        bridge is IUnrealNativeObjectProvider provider ? provider.NativeObjects :
        throw new NotSupportedException("The bridge does not provide native object bindings.");

    public UnrealNativeValueV3 EncodeCallback<T>(UnrealCallback<T> callback) where T : Delegate
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        var value = callback.AsArgument(_bridge ?? throw new NotSupportedException("Callbacks require a World bridge."));
        return new UnrealNativeValueV3 { Kind = UnrealNativeValueKindV3.Callback,
            TypeId = value.SignatureId, Payload0 = value.Token, Payload1 = value.Scope };
    }

    public UnrealNativeObjectLease Construct(ulong constructorId, ReadOnlySpan<UnrealNativeValueV3> arguments) =>
        Acquire(default, constructorId, arguments);

    public UnrealNativeObjectLease ConstructText(ulong constructorId, UnrealNativeObjectTextArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        using var call = BeginText(default, constructorId, arguments.ToValues(), arguments.ToBytes());
        var result = call.Read();
        if (result.Call.Handle.Value == 0)
            throw new InvalidOperationException("The native text constructor returned no object.");
        return new UnrealNativeObjectLease(this, result.Call.Handle);
    }

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

    internal void BorrowMemory<T>(UnrealNativeObjectHandle target, ulong member, bool write, UnrealSpanAction<T> action) where T : unmanaged
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _transport.BorrowNativeObjectMemory(_scope, target, member, write, action);
        }
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

    public UnrealNativeObjectTextPendingCall BeginText(UnrealNativeObjectHandle target, ulong memberId,
        ReadOnlySpan<UnrealNativeValueV3> arguments, ReadOnlySpan<byte> input)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_transport.SupportsNativeObjectText)
                throw new NotSupportedException("The installed native-object transport does not provide the text tail.");
            if (input.Length > _transport.NativeObjectTextMaxInputBytes)
                throw new UnrealNativeObjectException(UnrealNativeObjectStatus.InvalidArgument, "text input");
            RequireSuccess(_transport.BeginNativeObjectText(_scope, target, memberId, arguments, input,
                out var token, out var requiredBytes), "begin text");
            if (token == 0) throw new InvalidOperationException("Native text transport returned an empty token.");
            if (requiredBytes > UnrealNativeObjectValues.MaximumTextArenaBytes)
            {
                _ = _transport.DiscardNativeObjectText(_scope, token);
                throw new UnrealNativeObjectException(UnrealNativeObjectStatus.InvalidArgument, "begin text result");
            }
            return new UnrealNativeObjectTextPendingCall(this, token, requiredBytes);
        }
    }

    public UnrealNativeObjectLease AcquireText(UnrealNativeObjectHandle target, ulong memberId,
        ReadOnlySpan<UnrealNativeValueV3> arguments, ReadOnlySpan<byte> input)
    {
        using var call = BeginText(target, memberId, arguments, input);
        var result = call.Read();
        if (result.Call.Handle.Value == 0) throw new InvalidOperationException("Native text call returned no object.");
        return new UnrealNativeObjectLease(this, result.Call.Handle);
    }

    internal UnrealNativeObjectStatus ReadText(ulong token, Span<byte> output, out uint requiredBytes,
        out UnrealNativeObjectCallResult result)
    {
        lock (_gate)
        {
            requiredBytes = 0; result = default;
            if (_disposed) return UnrealNativeObjectStatus.InvalidScope;
            return _transport.ReadNativeObjectText(_scope, token, output, out requiredBytes, out result);
        }
    }

    internal void DiscardText(ulong token)
    {
        _ = DiscardTextStatus(token);
    }

    internal UnrealNativeObjectStatus DiscardTextStatus(ulong token)
    {
        lock (_gate)
        {
            if (_disposed) return UnrealNativeObjectStatus.InvalidScope;
            var status = _transport.DiscardNativeObjectText(_scope, token);
            if (status is not (UnrealNativeObjectStatus.Success or UnrealNativeObjectStatus.InvalidTextToken or
                UnrealNativeObjectStatus.InvalidScope or UnrealNativeObjectStatus.Unavailable))
                RequireSuccess(status, "discard text");
            return status;
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

public sealed class UnrealNativeObjectTextPendingCall : IDisposable
{
    private UnrealNativeObjectScope? _scope;
    private ulong _token;
    private readonly uint _requiredBytes;

    internal UnrealNativeObjectTextPendingCall(UnrealNativeObjectScope scope, ulong token, uint requiredBytes)
    {
        _scope = scope; _token = token; _requiredBytes = requiredBytes;
    }

    public uint RequiredBytes => _requiredBytes;
    public UnrealNativeObjectTextCallResult Read()
    {
        var scope = _scope ?? throw new ObjectDisposedException(nameof(UnrealNativeObjectTextPendingCall));
        byte[] bytes = new byte[_requiredBytes];
        return ReadCore(scope, bytes);
    }

    public UnrealNativeObjectTextCallResult Read(Span<byte> destination)
    {
        var scope = _scope ?? throw new ObjectDisposedException(nameof(UnrealNativeObjectTextPendingCall));
        return ReadCore(scope, destination);
    }

    private UnrealNativeObjectTextCallResult ReadCore(UnrealNativeObjectScope scope, Span<byte> bytes)
    {
        var status = scope.ReadText(_token, bytes, out var required, out var call);
        if (status != UnrealNativeObjectStatus.Success)
        {
            if (status == UnrealNativeObjectStatus.TextBufferTooSmall)
                throw new UnrealNativeObjectException(status, $"read text ({required} bytes required)");
            throw new UnrealNativeObjectException(status, "read text");
        }
        _token = 0;
        _scope = null;
        return new(call, bytes.ToArray());
    }
    public void Dispose()
    {
        var scope = _scope;
        if (scope is null || _token == 0) return;
        var status = scope.DiscardTextStatus(_token);
        if (status is UnrealNativeObjectStatus.Success or UnrealNativeObjectStatus.InvalidTextToken or
            UnrealNativeObjectStatus.InvalidScope or UnrealNativeObjectStatus.Unavailable)
        {
            _scope = null;
            _token = 0;
        }
    }
}

/// <summary>Disposing an owned lease destroys it; disposing a borrow only retires that alias.</summary>
public sealed class UnrealNativeObjectLease : IDisposable
{
    internal void BorrowMemory<T>(ulong member, bool write, UnrealSpanAction<T> action) where T : unmanaged
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _scope.BorrowMemory(Handle, member, write, action);
        }
    }
    public UnrealNativeValueV3 EncodeCallback<T>(UnrealCallback<T> callback) where T : Delegate => _scope.EncodeCallback(callback);
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

    public UnrealNativeObjectTextPendingCall BeginText(ulong memberId, ReadOnlySpan<UnrealNativeValueV3> arguments,
        ReadOnlySpan<byte> input) {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _scope.BeginText(Handle, memberId, arguments, input);
        }
    }

    public UnrealNativeObjectLease AcquireText(ulong memberId, ReadOnlySpan<UnrealNativeValueV3> arguments,
        ReadOnlySpan<byte> input) => _scope.AcquireText(Handle, memberId, arguments, input);

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

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealNativeObjectTransportTableWithText
{
    public UnrealNativeObjectTransportTableWithCapabilities Base;
    public nint TextTransport;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealNativeObjectTextTransportTableV1
{
    public uint AbiVersion, TableSize, ValueSize, Reserved;
    public uint MaxInputBytes, MaxOutputBytes, MaxPendingBytes, MaxPendingCount;
    public nint Begin, Read, Discard;
}
