using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Finare.Unreal;

public sealed partial class InProcessUnrealNativeInvocationTransport : IUnrealNativeDataTransport
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeDataParent { public NativeTransportTableV2ObjectReferences Base; public nint Data; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeDataTable { public uint Version, Size; public nint Execute, Borrow; }
    private NativeDataTable _nativeData;
    [ThreadStatic] private static bool _nativeMemoryCallback;

    private void LoadNativeData(nint pointer, NativeTransportTableV1 parent)
    {
        if (parent.AbiVersion != 2 || parent.TableSize < Marshal.SizeOf<NativeDataParent>()) return;
        nint data = Marshal.PtrToStructure<NativeDataParent>(pointer).Data;
        if (data == 0) return;
        var table = Marshal.PtrToStructure<NativeDataTable>(data);
        if (table.Version != 1 || table.Size < Marshal.SizeOf<NativeDataTable>() || table.Execute == 0 || table.Borrow == 0)
            throw new InvalidOperationException("Incompatible native data transport table.");
        _nativeData = table;
    }

    unsafe object? IUnrealNativeDataTransport.AccessData(UnrealObjectHandle owner, string path, byte operation,
        Type resultType, IReadOnlyList<UnrealOutputArgument> arguments)
    {
        using var invocation = EnterNativeInvocation();
        if (_nativeData.Execute == 0) throw new NotSupportedException("Native data views require the native data transport.");
        if (arguments.Count > 2) throw new ArgumentException("Native data operations accept at most two values.", nameof(arguments));
        using var nativePath = new Utf8Buffer(path);
        int capacity = 4096;
        while (capacity <= UnrealByteBuffer.MaximumCallBytes)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);
            try
            {
                Span<byte> arena = buffer.AsSpan(0, capacity);
                var inputs = new UnrealNativeValueV3[arguments.Count];
                int used = 0; bool encoded = true;
                for (int i = 0; i < arguments.Count; ++i)
                    if (!UnrealNativeValueCodecV3.TryEncodeObject(arguments[i].Value, arguments[i].Type, arena, ref used, out inputs[i], out _)) { encoded = false; break; }
                if (!encoded) { capacity = checked(capacity * 2); continue; }
                UnrealNativeValueV3 result = default;
                fixed (UnrealNativeValueV3* values = inputs)
                fixed (byte* bytes = arena)
                {
                    var frame = new UnrealNativeCallFrameV3
                    {
                        Arguments = (nint)values, ArgumentCount = (uint)inputs.Length,
                        Results = (nint)(&result), ResultCapacity = 1, ResultCount = 1,
                        ByteBuffer = (nint)bytes, ByteCapacity = (uint)capacity, ByteCount = (uint)used,
                    };
                    ((delegate* unmanaged[Cdecl]<nint, ulong, nint, int, byte, UnrealNativeCallFrameV3*, void>)_nativeData.Execute)
                        (_context, owner.Value, nativePath.Pointer, nativePath.Length, operation, &frame);
                    if (frame.Status == UnrealNativeCallStatusV3.BufferTooSmall && operation is 0 or 6 or 8 &&
                        frame.RequiredByteCapacity > capacity && frame.RequiredByteCapacity <= UnrealByteBuffer.MaximumCallBytes)
                    { capacity = checked((int)frame.RequiredByteCapacity); continue; }
                    if (frame.Status != UnrealNativeCallStatusV3.Success)
                    {
                        string message = frame.ErrorMessageLength > 0 && (ulong)frame.ErrorMessageOffset + frame.ErrorMessageLength <= (ulong)arena.Length
                            ? Encoding.UTF8.GetString(arena.Slice((int)frame.ErrorMessageOffset, (int)frame.ErrorMessageLength)) : frame.Status.ToString();
                        throw new InvalidOperationException($"Native data access '{path}' failed: {message}");
                    }
                    if (frame.ResultCount != 1 || frame.ByteCount > capacity) throw new InvalidOperationException("Invalid native data result frame.");
                    if (resultType == typeof(void)) return null;
                    if (!UnrealNativeValueCodecV3.TryDecodeObject(resultType, result, arena, (int)frame.ByteCount, out object? decoded))
                        throw new InvalidOperationException($"Native data result at '{path}' does not match '{resultType}'.");
                    return decoded;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        throw new ArgumentException("Native data input cannot be encoded within the bounded arena.");
    }

    private sealed class MemoryCallbackBox(Action<nint, int> action)
    {
        internal Action<nint, int> Action = action;
        internal ExceptionDispatchInfo? Error;
        internal int Calls;
    }
    public unsafe void BorrowNativeObjectMemory<T>(ulong scope, UnrealNativeObjectHandle target, ulong member, bool write, UnrealSpanAction<T> action) where T : unmanaged
    {
        using var invocation = EnterNativeInvocation();
        ArgumentNullException.ThrowIfNull(action);
        if (_nativeObjectBorrow == 0) throw new NotSupportedException("Native C++ buffer borrowing is unavailable.");
        lock (_lifetimeGate)
            if (!_nativeObjectScopes.Contains(scope)) throw new ObjectDisposedException(nameof(UnrealNativeObjectScope));
        byte kind = UnrealNativeMemory.ElementKind<T>();
        var box = new MemoryCallbackBox((pointer, count) => action(new Span<T>((void*)pointer, count)));
        GCHandle handle = GCHandle.Alloc(box);
        try
        {
            var status = ((delegate* unmanaged[Cdecl]<nint, ulong, ulong, ulong, byte, uint, byte, nint, nint, UnrealNativeObjectStatus>)_nativeObjectBorrow)
                (_context, scope, target.Value, member, kind, (uint)sizeof(T), write ? (byte)1 : (byte)0,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, void>)&NativeMemoryCallback, GCHandle.ToIntPtr(handle));
            box.Error?.Throw();
            UnrealNativeObjectScope.RequireSuccess(status, "borrow memory");
            if (box.Calls != 1) throw new InvalidOperationException("Native memory callback was not invoked exactly once.");
        }
        finally { handle.Free(); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void NativeMemoryCallback(nint user, nint pointer, int count)
    {
        var box = (MemoryCallbackBox)GCHandle.FromIntPtr(user).Target!;
        try
        {
            if (++box.Calls != 1 || count < 0 || (count > 0 && pointer == 0)) throw new InvalidOperationException("Invalid native memory callback.");
            _nativeMemoryCallback = true;
            box.Action(pointer, count);
        }
        catch (Exception error) { box.Error = ExceptionDispatchInfo.Capture(error); }
        finally { _nativeMemoryCallback = false; }
    }
    unsafe void IUnrealNativeDataTransport.BorrowData<T>(UnrealObjectHandle owner, string path, bool write, UnrealSpanAction<T> action)
    {
        using var invocation = EnterNativeInvocation();
        if (_nativeData.Borrow == 0) throw new NotSupportedException("The native transport does not support borrowed memory.");
        byte kind = UnrealNativeMemory.ElementKind<T>();
        var box = new MemoryCallbackBox((pointer, count) => action(new Span<T>((void*)pointer, count)));
        GCHandle handle = GCHandle.Alloc(box);
        try
        {
            using var nativePath = new Utf8Buffer(path);
            byte* error = stackalloc byte[1024]; error[0] = 0;
            byte result = ((delegate* unmanaged[Cdecl]<nint, ulong, nint, int, byte, uint, byte, nint, nint, byte*, int, byte>)_nativeData.Borrow)
                (_context, owner.Value, nativePath.Pointer, nativePath.Length, kind, (uint)sizeof(T), write ? (byte)1 : (byte)0,
                 (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, void>)&NativeMemoryCallback, GCHandle.ToIntPtr(handle), error, 1024);
            box.Error?.Throw();
            if (result == 0 || box.Calls != 1) throw new InvalidOperationException("Native memory borrow failed: " + Marshal.PtrToStringUTF8((nint)error));
        }
        finally { handle.Free(); }
    }
}
