using System.Runtime.InteropServices;
using System.Text;

namespace UnifiedScript.Unreal;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void UnrealNativeResultCallbackV1(
    nint userData,
    byte success,
    byte valueKind,
    byte booleanValue,
    int int32Value,
    nint stringData,
    int stringLength,
    byte error,
    byte handleError,
    nint messageData,
    int messageLength);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void UnrealNativeReadPropertyV1(
    nint context,
    ulong target,
    nint propertyPathData,
    int propertyPathLength,
    UnrealNativeResultCallbackV1 callback,
    nint userData);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void UnrealNativeWritePropertyV1(
    nint context,
    ulong target,
    nint propertyPathData,
    int propertyPathLength,
    nint value,
    UnrealNativeResultCallbackV1 callback,
    nint userData);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void UnrealNativeInvokeV1(
    nint context,
    ulong target,
    nint functionPathData,
    int functionPathLength,
    nint arguments,
    int argumentCount,
    UnrealNativeResultCallbackV1 callback,
    nint userData);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void UnrealNativeReadPropertyByIdV2(
    nint context,
    ulong target,
    ulong memberId,
    UnrealNativeResultCallbackV1 callback,
    nint userData);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void UnrealNativeWritePropertyByIdV2(
    nint context,
    ulong target,
    ulong memberId,
    nint value,
    UnrealNativeResultCallbackV1 callback,
    nint userData);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void UnrealNativeInvokeByIdV2(
    nint context,
    ulong target,
    ulong memberId,
    nint arguments,
    int argumentCount,
    UnrealNativeResultCallbackV1 callback,
    nint userData);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void UnrealNativeExecuteByIdV3(
    nint context,
    ulong target,
    ulong memberId,
    ref UnrealNativeCallFrameV3 frame);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate byte UnrealNativeRetainContextV1(nint context);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void UnrealNativeReleaseContextV1(nint context);

/// <summary>
/// Calls the versioned synchronous function table exported by the UE plugin.
/// The function table is borrowed from the plugin. The context is retained until
/// this transport is disposed, and becomes unavailable after its world detaches.
/// ABI v1 uses canonical member paths; ABI v2 appends generated member-ID thunks.
/// </summary>
public sealed class InProcessUnrealNativeInvocationTransport
    : IUnrealNativeInvocationTransport,
      IUnrealNativeFastInvocationTransport,
      IUnrealNativeCallFrameTransportV3,
      IDisposable
{
    private const uint MinimumAbiVersion = 1;
    private const uint MaximumAbiVersion = 2;

    private static readonly UnrealNativeResultCallbackV1 ResultCallback = ReceiveResult;

    private readonly nint _context;
    private readonly UnrealNativeReadPropertyV1 _readProperty;
    private readonly UnrealNativeWritePropertyV1 _writeProperty;
    private readonly UnrealNativeInvokeV1 _invoke;
    private readonly UnrealNativeReadPropertyByIdV2? _readPropertyById;
    private readonly UnrealNativeWritePropertyByIdV2? _writePropertyById;
    private readonly UnrealNativeInvokeByIdV2? _invokeById;
    private readonly nint _readPropertyByIdV3;
    private readonly nint _writePropertyByIdV3;
    private readonly nint _invokeByIdV3;
    private readonly UnrealNativeCapabilitiesV3 _capabilitiesV3;
    private readonly UnrealNativeReleaseContextV1 _releaseContext;
    private readonly object _lifetimeGate = new();
    private bool _disposed;

    public InProcessUnrealNativeInvocationTransport(nint context, nint tablePointer)
    {
        if (context == nint.Zero)
        {
            throw new ArgumentException(
                "A live native invocation context is required.",
                nameof(context));
        }
        if (tablePointer == nint.Zero)
        {
            throw new ArgumentException(
                "A native invocation function table is required.",
                nameof(tablePointer));
        }

        NativeTransportTableV1 table = Marshal.PtrToStructure<NativeTransportTableV1>(
            tablePointer);
        uint expectedTableSize = checked((uint)Marshal.SizeOf<NativeTransportTableV1>());
        uint expectedValueSize = checked((uint)Marshal.SizeOf<NativeValue>());
        if (table.AbiVersion < MinimumAbiVersion || table.AbiVersion > MaximumAbiVersion)
        {
            throw new NotSupportedException(
                $"Native Unreal transport ABI {table.AbiVersion} is not supported.");
        }
        if (table.TableSize < expectedTableSize)
        {
            throw new InvalidOperationException(
                $"Native Unreal transport table is {table.TableSize} bytes; " +
                $"at least {expectedTableSize} are required.");
        }
        if (table.ValueSize != expectedValueSize)
        {
            throw new InvalidOperationException(
                $"Native Unreal value size is {table.ValueSize} bytes; " +
                $"managed code requires {expectedValueSize}.");
        }
        if (table.ReadProperty == nint.Zero ||
            table.WriteProperty == nint.Zero ||
            table.Invoke == nint.Zero ||
            table.RetainContext == nint.Zero ||
            table.ReleaseContext == nint.Zero)
        {
            throw new InvalidOperationException(
                "The native Unreal transport table is incomplete.");
        }

        _context = context;
        _readProperty = Marshal.GetDelegateForFunctionPointer<UnrealNativeReadPropertyV1>(
            table.ReadProperty);
        _writeProperty = Marshal.GetDelegateForFunctionPointer<UnrealNativeWritePropertyV1>(
            table.WriteProperty);
        _invoke = Marshal.GetDelegateForFunctionPointer<UnrealNativeInvokeV1>(table.Invoke);

        if (table.AbiVersion == 2)
        {
            uint expectedV2TableSize = checked(
                (uint)Marshal.SizeOf<NativeTransportTableV2>());
            if (table.TableSize < expectedV2TableSize)
            {
                throw new InvalidOperationException(
                    $"Native Unreal transport ABI v2 table is {table.TableSize} bytes; " +
                    $"at least {expectedV2TableSize} are required.");
            }

            NativeTransportTableV2 tableV2 =
                Marshal.PtrToStructure<NativeTransportTableV2>(tablePointer);
            if (tableV2.ReadPropertyById == nint.Zero ||
                tableV2.WritePropertyById == nint.Zero ||
                tableV2.InvokeById == nint.Zero)
            {
                throw new InvalidOperationException(
                    "The native Unreal transport ABI v2 fast-member table is incomplete.");
            }
            _readPropertyById = Marshal.GetDelegateForFunctionPointer<
                UnrealNativeReadPropertyByIdV2>(tableV2.ReadPropertyById);
            _writePropertyById = Marshal.GetDelegateForFunctionPointer<
                UnrealNativeWritePropertyByIdV2>(tableV2.WritePropertyById);
            _invokeById = Marshal.GetDelegateForFunctionPointer<
                UnrealNativeInvokeByIdV2>(tableV2.InvokeById);

            uint extendedV2Size = checked((uint)Marshal.SizeOf<NativeTransportTableV2Extended>());
            if (table.TableSize >= extendedV2Size)
            {
                NativeTransportTableV2Extended extended =
                    Marshal.PtrToStructure<NativeTransportTableV2Extended>(tablePointer);
                if (extended.CallFrameTransport != nint.Zero)
                {
                    UnrealNativeTransportTableV3 tableV3 =
                        Marshal.PtrToStructure<UnrealNativeTransportTableV3>(
                            extended.CallFrameTransport);
                    if (tableV3.AbiVersion != UnrealNativeAbiV3.Version ||
                        tableV3.TableSize < UnrealNativeAbiV3.TransportTableSize ||
                        tableV3.ValueSize != UnrealNativeAbiV3.ValueSize ||
                        tableV3.CallFrameSize != UnrealNativeAbiV3.CallFrameSize)
                    {
                        throw new InvalidOperationException(
                            "The optional native Unreal ABI v3 table has an incompatible layout.");
                    }
                    if (tableV3.RetainContext != table.RetainContext ||
                        tableV3.ReleaseContext != table.ReleaseContext ||
                        tableV3.ReadPropertyById == nint.Zero ||
                        tableV3.WritePropertyById == nint.Zero ||
                        tableV3.InvokeById == nint.Zero)
                    {
                        throw new InvalidOperationException(
                            "The optional native Unreal ABI v3 table has an incompatible context or entry points.");
                    }
                    _capabilitiesV3 = tableV3.Capabilities;
                    _readPropertyByIdV3 = tableV3.ReadPropertyById;
                    _writePropertyByIdV3 = tableV3.WritePropertyById;
                    _invokeByIdV3 = tableV3.InvokeById;
                }
            }
        }

        UnrealNativeRetainContextV1 retainContext =
            Marshal.GetDelegateForFunctionPointer<UnrealNativeRetainContextV1>(
                table.RetainContext);
        _releaseContext = Marshal.GetDelegateForFunctionPointer<UnrealNativeReleaseContextV1>(
            table.ReleaseContext);
        if (retainContext(_context) == 0)
        {
            throw new InvalidOperationException(
                "The native Unreal world invocation context is detached.");
        }
    }

    public bool SupportsFastInvocation =>
        _readPropertyById is not null &&
        _writePropertyById is not null &&
        _invokeById is not null;

    public UnrealNativeCapabilitiesV3 Capabilities => _capabilitiesV3;

    public UnrealInvocationResponse ReadProperty(
        UnrealObjectHandle target,
        string propertyPath)
    {
        lock (_lifetimeGate)
        {
            ThrowIfDisposed();
            using var path = new Utf8Buffer(propertyPath);
            return Capture(userData => _readProperty(
                _context,
                target.Value,
                path.Pointer,
                path.Length,
                ResultCallback,
                userData));
        }
    }

    public UnrealInvocationResponse WriteProperty(
        UnrealObjectHandle target,
        string propertyPath,
        UnrealInvocationValue value)
    {
        lock (_lifetimeGate)
        {
            ThrowIfDisposed();
            using var path = new Utf8Buffer(propertyPath);
            using var nativeValue = new NativeValueArrayLease([value]);
            return Capture(userData => _writeProperty(
                _context,
                target.Value,
                path.Pointer,
                path.Length,
                nativeValue.Pointer,
                ResultCallback,
                userData));
        }
    }

    public UnrealInvocationResponse Invoke(
        UnrealObjectHandle target,
        string functionPath,
        IReadOnlyList<UnrealInvocationValue> arguments)
    {
        lock (_lifetimeGate)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(arguments);
            using var path = new Utf8Buffer(functionPath);
            using var nativeArguments = new NativeValueArrayLease(arguments);
            return Capture(userData => _invoke(
                _context,
                target.Value,
                path.Pointer,
                path.Length,
                nativeArguments.Pointer,
                nativeArguments.Count,
                ResultCallback,
                userData));
        }
    }

    public UnrealInvocationResponse ReadPropertyById(
        UnrealObjectHandle target,
        ulong memberId)
    {
        lock (_lifetimeGate)
        {
            ThrowIfDisposed();
            UnrealNativeReadPropertyByIdV2 readProperty = _readPropertyById ??
                throw new NotSupportedException(
                    "The native Unreal transport does not expose ABI v2 fast members.");
            return Capture(userData => readProperty(
                _context,
                target.Value,
                memberId,
                ResultCallback,
                userData));
        }
    }

    public UnrealInvocationResponse WritePropertyById(
        UnrealObjectHandle target,
        ulong memberId,
        UnrealInvocationValue value)
    {
        lock (_lifetimeGate)
        {
            ThrowIfDisposed();
            UnrealNativeWritePropertyByIdV2 writeProperty = _writePropertyById ??
                throw new NotSupportedException(
                    "The native Unreal transport does not expose ABI v2 fast members.");
            using var nativeValue = new NativeValueArrayLease([value]);
            return Capture(userData => writeProperty(
                _context,
                target.Value,
                memberId,
                nativeValue.Pointer,
                ResultCallback,
                userData));
        }
    }

    public UnrealInvocationResponse InvokeById(
        UnrealObjectHandle target,
        ulong memberId,
        IReadOnlyList<UnrealInvocationValue> arguments)
    {
        lock (_lifetimeGate)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(arguments);
            UnrealNativeInvokeByIdV2 invoke = _invokeById ??
                throw new NotSupportedException(
                    "The native Unreal transport does not expose ABI v2 fast members.");
            using var nativeArguments = new NativeValueArrayLease(arguments);
            return Capture(userData => invoke(
                _context,
                target.Value,
                memberId,
                nativeArguments.Pointer,
                nativeArguments.Count,
                ResultCallback,
                userData));
        }
    }

    public unsafe UnrealNativeCallOutcomeV3 ExecuteById(
        UnrealNativeOperationV3 operation,
        UnrealObjectHandle target,
        ulong memberId,
        ReadOnlySpan<UnrealNativeValueV3> arguments,
        Span<UnrealNativeValueV3> results,
        Span<byte> byteBuffer,
        int inputByteCount)
    {
        if (inputByteCount < 0 || inputByteCount > byteBuffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(inputByteCount));
        }
        nint executePointer = operation switch
        {
            UnrealNativeOperationV3.ReadProperty => _readPropertyByIdV3,
            UnrealNativeOperationV3.WriteProperty => _writePropertyByIdV3,
            UnrealNativeOperationV3.Invoke => _invokeByIdV3,
            _ => nint.Zero,
        };
        if (executePointer == nint.Zero)
        {
            throw new NotSupportedException(
                "The native Unreal transport does not expose the requested ABI v3 operation.");
        }
        var execute =
            (delegate* unmanaged[Cdecl]<nint, ulong, ulong, UnrealNativeCallFrameV3*, void>)
            executePointer;

        lock (_lifetimeGate)
        {
            ThrowIfDisposed();
            fixed (UnrealNativeValueV3* argumentPointer = arguments)
            fixed (UnrealNativeValueV3* resultPointer = results)
            fixed (byte* bytePointer = byteBuffer)
            {
                var frame = new UnrealNativeCallFrameV3
                {
                    Arguments = (nint)argumentPointer,
                    ArgumentCount = checked((uint)arguments.Length),
                    Results = (nint)resultPointer,
                    ResultCapacity = checked((uint)results.Length),
                    ResultCount = checked((uint)results.Length),
                    ByteBuffer = (nint)bytePointer,
                    ByteCapacity = checked((uint)byteBuffer.Length),
                    ByteCount = checked((uint)inputByteCount),
                    Status = UnrealNativeCallStatusV3.Uninitialized,
                };
                execute(_context, target.Value, memberId, &frame);
                if (!Enum.IsDefined(frame.Status) ||
                    frame.ResultCount > results.Length ||
                    frame.ByteCount > byteBuffer.Length ||
                    frame.RequiredResultCapacity > int.MaxValue ||
                    frame.RequiredByteCapacity > int.MaxValue)
                {
                    throw new InvalidOperationException(
                        "The native Unreal ABI v3 transport returned an invalid frame.");
                }
                string message = string.Empty;
                if (frame.ErrorMessageLength > 0)
                {
                    ulong end = (ulong)frame.ErrorMessageOffset + frame.ErrorMessageLength;
                    if (end > frame.ByteCount || end > (ulong)byteBuffer.Length)
                    {
                        throw new InvalidOperationException(
                            "The native Unreal ABI v3 transport returned an invalid error slice.");
                    }
                    message = Encoding.UTF8.GetString(byteBuffer.Slice(
                        checked((int)frame.ErrorMessageOffset),
                        checked((int)frame.ErrorMessageLength)));
                }
                return new UnrealNativeCallOutcomeV3(
                    frame.Status,
                    (UnrealInvocationError)frame.InvocationError,
                    (UnrealObjectHandleError)frame.HandleError,
                    checked((int)frame.ResultCount),
                    checked((int)frame.ByteCount),
                    checked((int)frame.RequiredResultCapacity),
                    checked((int)frame.RequiredByteCapacity),
                    message);
            }
        }
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _releaseContext(_context);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static UnrealInvocationResponse Capture(Action<nint> invoke)
    {
        var box = new ResultBox();
        GCHandle handle = GCHandle.Alloc(box);
        try
        {
            invoke(GCHandle.ToIntPtr(handle));
            if (box.Exception is not null)
            {
                throw new InvalidOperationException(
                    "The native Unreal transport returned an invalid response.",
                    box.Exception);
            }
            if (box.Response is not UnrealInvocationResponse response)
            {
                throw new InvalidOperationException(
                    "The native Unreal transport did not complete synchronously.");
            }
            return response;
        }
        finally
        {
            handle.Free();
        }
    }

    private static void ReceiveResult(
        nint userData,
        byte success,
        byte valueKind,
        byte booleanValue,
        int int32Value,
        nint stringData,
        int stringLength,
        byte error,
        byte handleError,
        nint messageData,
        int messageLength)
    {
        var box = (ResultBox)GCHandle.FromIntPtr(userData).Target!;
        if (box.Response is not null || box.Exception is not null)
        {
            box.Exception = new InvalidOperationException(
                "The native Unreal transport invoked its result callback more than once.");
            return;
        }

        try
        {
            if (success > 1 || booleanValue > 1)
            {
                throw new InvalidOperationException(
                    "The native Unreal transport returned a non-boolean ABI flag.");
            }
            var invocationError = (UnrealInvocationError)error;
            var objectHandleError = (UnrealObjectHandleError)handleError;
            if (!Enum.IsDefined(invocationError) || !Enum.IsDefined(objectHandleError))
            {
                throw new InvalidOperationException(
                    "The native Unreal transport returned an unknown error category.");
            }

            if (success != 0)
            {
                if (invocationError != UnrealInvocationError.None ||
                    objectHandleError != UnrealObjectHandleError.None)
                {
                    throw new InvalidOperationException(
                        "The native Unreal transport mixed success and failure categories.");
                }
                box.Response = UnrealInvocationResponse.Succeeded(ReadValue(
                    valueKind,
                    booleanValue,
                    int32Value,
                    stringData,
                    stringLength));
                return;
            }

            box.Response = UnrealInvocationResponse.Failed(
                invocationError,
                ReadUtf8(messageData, messageLength),
                objectHandleError);
        }
        catch (Exception exception)
        {
            box.Exception = exception;
        }
    }

    private static UnrealInvocationValue ReadValue(
        byte kind,
        byte booleanValue,
        int int32Value,
        nint stringData,
        int stringLength) =>
        (UnrealInvocationValueKind)kind switch
        {
            UnrealInvocationValueKind.Void => UnrealInvocationValue.Void,
            UnrealInvocationValueKind.Boolean =>
                UnrealInvocationValue.FromBoolean(booleanValue != 0),
            UnrealInvocationValueKind.Int32 =>
                UnrealInvocationValue.FromInt32(int32Value),
            UnrealInvocationValueKind.String =>
                UnrealInvocationValue.FromString(ReadUtf8(stringData, stringLength)),
            UnrealInvocationValueKind.Name =>
                UnrealInvocationValue.FromName(new UnrealName(ReadUtf8(stringData, stringLength))),
            UnrealInvocationValueKind.Text =>
                UnrealInvocationValue.FromText(new UnrealText(ReadUtf8(stringData, stringLength))),
            _ => throw new InvalidOperationException(
                $"The native Unreal transport returned unknown value kind {kind}."),
        };

    private static string ReadUtf8(nint data, int length)
    {
        if (length < 0 || (length > 0 && data == nint.Zero))
        {
            throw new InvalidOperationException(
                "The native Unreal transport returned an invalid UTF-8 slice.");
        }
        return length == 0
            ? string.Empty
            : Marshal.PtrToStringUTF8(data, length) ?? string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTransportTableV1
    {
        public uint AbiVersion;
        public uint TableSize;
        public uint ValueSize;
        public uint Reserved;
        public nint ReadProperty;
        public nint WriteProperty;
        public nint Invoke;
        public nint RetainContext;
        public nint ReleaseContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTransportTableV2
    {
        public uint AbiVersion;
        public uint TableSize;
        public uint ValueSize;
        public uint Reserved;
        public nint ReadProperty;
        public nint WriteProperty;
        public nint Invoke;
        public nint RetainContext;
        public nint ReleaseContext;
        public nint ReadPropertyById;
        public nint WritePropertyById;
        public nint InvokeById;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTransportTableV2Extended
    {
        public uint AbiVersion;
        public uint TableSize;
        public uint ValueSize;
        public uint Reserved;
        public nint ReadProperty;
        public nint WriteProperty;
        public nint Invoke;
        public nint RetainContext;
        public nint ReleaseContext;
        public nint ReadPropertyById;
        public nint WritePropertyById;
        public nint InvokeById;
        public nint CallFrameTransport;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeValue
    {
        public byte Kind;
        public byte BooleanValue;
        public ushort Reserved;
        public int Int32Value;
        public nint StringData;
        public int StringLength;
    }

    private sealed class ResultBox
    {
        public UnrealInvocationResponse? Response { get; set; }

        public Exception? Exception { get; set; }
    }

    private sealed class Utf8Buffer : IDisposable
    {
        public Utf8Buffer(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            Length = bytes.Length;
            Pointer = Marshal.AllocHGlobal(Math.Max(1, Length));
            if (Length > 0)
            {
                Marshal.Copy(bytes, 0, Pointer, Length);
            }
        }

        public nint Pointer { get; private set; }

        public int Length { get; }

        public void Dispose()
        {
            if (Pointer != nint.Zero)
            {
                Marshal.FreeHGlobal(Pointer);
                Pointer = nint.Zero;
            }
        }
    }

    private sealed class NativeValueLease : IDisposable
    {
        private Utf8Buffer? _string;

        public NativeValueLease(UnrealInvocationValue value)
        {
            var nativeValue = new NativeValue
            {
                Kind = (byte)value.Kind,
                BooleanValue = value.BooleanValue ? (byte)1 : (byte)0,
                Int32Value = value.Int32Value,
            };
            if (value.Kind is UnrealInvocationValueKind.String or
                UnrealInvocationValueKind.Name or UnrealInvocationValueKind.Text)
            {
                _string = new Utf8Buffer(value.StringValue ?? string.Empty);
                nativeValue.StringData = _string.Pointer;
                nativeValue.StringLength = _string.Length;
            }
            Value = nativeValue;
        }

        public NativeValue Value { get; }

        public void Dispose()
        {
            _string?.Dispose();
            _string = null;
        }
    }

    private sealed class NativeValueArrayLease : IDisposable
    {
        private readonly List<NativeValueLease> _values = [];

        public NativeValueArrayLease(IReadOnlyList<UnrealInvocationValue> values)
        {
            Count = values.Count;
            if (Count == 0)
            {
                return;
            }

            int valueSize = Marshal.SizeOf<NativeValue>();
            Pointer = Marshal.AllocHGlobal(checked(valueSize * Count));
            try
            {
                for (int index = 0; index < Count; index++)
                {
                    var lease = new NativeValueLease(values[index]);
                    _values.Add(lease);
                    Marshal.StructureToPtr(
                        lease.Value,
                        Pointer + index * valueSize,
                        false);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public nint Pointer { get; private set; }

        public int Count { get; }

        public void Dispose()
        {
            foreach (NativeValueLease value in _values)
            {
                value.Dispose();
            }
            _values.Clear();
            if (Pointer != nint.Zero)
            {
                Marshal.FreeHGlobal(Pointer);
                Pointer = nint.Zero;
            }
        }
    }
}
