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

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void UnrealNativeEventCallbackV4(nint userData, nint frame);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate ulong UnrealNativeSubscribeEventV4(
    nint context,
    ulong source,
    ulong eventId,
    nint eventPathData,
    int eventPathLength,
    UnrealNativeEventCallbackV4 callback,
    nint userData);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate byte UnrealNativeUnsubscribeEventV4(
    nint context,
    ulong subscriptionId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate byte UnrealNativeAcquireTransformSnapshotV4(
    nint context,
    out UnrealSharedSnapshotViewV4 view);

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
      IUnrealNativeScalarInvocationTransportV3,
      IUnrealNativeFixedInvocationTransportV3,
      IUnrealNativeEventTransport,
      IUnrealSharedSnapshotTransport,
      IDisposable
{
    private const uint MinimumAbiVersion = 1;
    private const uint MaximumAbiVersion = 2;

    private static readonly UnrealNativeResultCallbackV1 ResultCallback = ReceiveResult;
    private static readonly UnrealNativeEventCallbackV4 EventCallback = ReceiveEvent;

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
    private readonly nint _invokeInt32x2ByIdV3;
    private readonly nint _invokeFixedByIdV3;
    private readonly UnrealNativeCapabilitiesV3 _capabilitiesV3;
    private readonly UnrealNativeSubscribeEventV4? _subscribeEvent;
    private readonly UnrealNativeUnsubscribeEventV4? _unsubscribeEvent;
    private readonly UnrealNativeAcquireTransformSnapshotV4? _acquireTransformSnapshot;
    private readonly UnrealNativeReleaseContextV1 _releaseContext;
    private readonly object _lifetimeGate = new();
    private readonly HashSet<NativeEventSubscription> _eventSubscriptions = [];
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
                    if (tableV3.TableSize >= UnrealNativeAbiV3.ScalarTransportTableSize)
                    {
                        UnrealNativeScalarTransportTableV3 scalarTable =
                            Marshal.PtrToStructure<UnrealNativeScalarTransportTableV3>(
                                extended.CallFrameTransport);
                        _invokeInt32x2ByIdV3 = scalarTable.InvokeInt32x2ById;
                    }
                    if (tableV3.TableSize >= UnrealNativeAbiV3.FixedTransportTableSize)
                    {
                        UnrealNativeFixedTransportTableV3 fixedTable =
                            Marshal.PtrToStructure<UnrealNativeFixedTransportTableV3>(
                                extended.CallFrameTransport);
                        _invokeFixedByIdV3 = fixedTable.InvokeFixedById;
                    }
                }
            }

            uint eventV2Size = checked(
                (uint)Marshal.SizeOf<NativeTransportTableV2Events>());
            if (table.TableSize >= eventV2Size)
            {
                NativeTransportTableV2Events eventExtension =
                    Marshal.PtrToStructure<NativeTransportTableV2Events>(tablePointer);
                if (eventExtension.EventTransport != nint.Zero)
                {
                    UnrealNativeEventTransportTableV4 eventTable =
                        Marshal.PtrToStructure<UnrealNativeEventTransportTableV4>(
                            eventExtension.EventTransport);
                    if (eventTable.AbiVersion != UnrealNativeEventAbiV4.Version ||
                        eventTable.TableSize < UnrealNativeEventAbiV4.TransportTableSize ||
                        eventTable.FrameSize != UnrealNativeEventAbiV4.FrameSize ||
                        eventTable.ValueSize != UnrealNativeAbiV3.ValueSize ||
                        eventTable.RetainContext != table.RetainContext ||
                        eventTable.ReleaseContext != table.ReleaseContext ||
                        eventTable.Subscribe == nint.Zero ||
                        eventTable.Unsubscribe == nint.Zero)
                    {
                        throw new InvalidOperationException(
                            "The optional native Unreal event ABI v4 table has an incompatible layout.");
                    }
                    _subscribeEvent = Marshal.GetDelegateForFunctionPointer<
                        UnrealNativeSubscribeEventV4>(eventTable.Subscribe);
                    _unsubscribeEvent = Marshal.GetDelegateForFunctionPointer<
                        UnrealNativeUnsubscribeEventV4>(eventTable.Unsubscribe);
                }
            }

            uint sharedDataV2Size = checked(
                (uint)Marshal.SizeOf<NativeTransportTableV2SharedData>());
            if (table.TableSize >= sharedDataV2Size)
            {
                NativeTransportTableV2SharedData sharedExtension =
                    Marshal.PtrToStructure<NativeTransportTableV2SharedData>(tablePointer);
                if (sharedExtension.SharedDataTransport != nint.Zero)
                {
                    UnrealSharedDataTransportTableV4 sharedTable =
                        Marshal.PtrToStructure<UnrealSharedDataTransportTableV4>(
                            sharedExtension.SharedDataTransport);
                    const UnrealSharedDataCapabilitiesV4 requiredCapabilities =
                        UnrealSharedDataCapabilitiesV4.TransformSnapshot |
                        UnrealSharedDataCapabilitiesV4.DoubleBuffered |
                        UnrealSharedDataCapabilitiesV4.WorldScopedHandles;
                    if (sharedTable.AbiVersion != UnrealSharedDataAbiV4.Version ||
                        sharedTable.TableSize < UnrealSharedDataAbiV4.TransportTableSize ||
                        sharedTable.SnapshotViewSize != UnrealSharedDataAbiV4.SnapshotViewSize ||
                        sharedTable.TransformEntrySize != UnrealSharedDataAbiV4.TransformEntrySize ||
                        sharedTable.TransformSchemaId != UnrealSharedDataAbiV4.TransformSnapshotSchemaId ||
                        (sharedTable.Capabilities & requiredCapabilities) != requiredCapabilities ||
                        sharedTable.RetainContext != table.RetainContext ||
                        sharedTable.ReleaseContext != table.ReleaseContext ||
                        sharedTable.AcquireTransformSnapshot == nint.Zero)
                    {
                        throw new InvalidOperationException(
                            "The optional native Shared Data ABI v4 table has an incompatible layout.");
                    }
                    _acquireTransformSnapshot = Marshal.GetDelegateForFunctionPointer<
                        UnrealNativeAcquireTransformSnapshotV4>(
                            sharedTable.AcquireTransformSnapshot);
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

    public bool SupportsEvents => _subscribeEvent is not null && _unsubscribeEvent is not null;

    public bool SupportsSharedData => _acquireTransformSnapshot is not null;

    bool IUnrealSharedSnapshotTransport.TryAcquireTransformSnapshot(
        out UnrealSharedSnapshotViewV4 view)
    {
        lock (_lifetimeGate)
        {
            ThrowIfDisposed();
            if (_acquireTransformSnapshot is null)
            {
                view = default;
                return false;
            }
            return _acquireTransformSnapshot(_context, out view) != 0;
        }
    }

    public IUnrealEventSubscription Subscribe(
        UnrealObjectHandle source,
        UnrealEventDescriptor descriptor,
        Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(handler);
        if (!source.IsWellFormed)
        {
            throw new ArgumentException(
                "A reflected event subscription requires a live source handle.",
                nameof(source));
        }
        if (descriptor.EventId == 0 ||
            descriptor.EventId != UnrealMemberId.FromPath(descriptor.EventPath))
        {
            throw new ArgumentException(
                "The reflected event descriptor has an invalid stable identity.",
                nameof(descriptor));
        }
        ValidateEventHandler(descriptor, handler);

        lock (_lifetimeGate)
        {
            ThrowIfDisposed();
            if (_subscribeEvent is null || _unsubscribeEvent is null)
            {
                throw new NotSupportedException(
                    "The native Unreal transport does not advertise reflected event ABI v4.");
            }

            var box = new EventCallbackBox(descriptor, handler);
            GCHandle callbackHandle = GCHandle.Alloc(box);
            try
            {
                using var path = new Utf8Buffer(descriptor.EventPath);
                ulong subscriptionId = _subscribeEvent(
                    _context,
                    source.Value,
                    descriptor.EventId,
                    path.Pointer,
                    path.Length,
                    EventCallback,
                    GCHandle.ToIntPtr(callbackHandle));
                if (subscriptionId == 0)
                {
                    throw new InvalidOperationException(
                        $"Native Unreal event subscription failed for '{descriptor.EventPath}'.");
                }

                var subscription = new NativeEventSubscription(
                    this,
                    subscriptionId,
                    box,
                    callbackHandle);
                _eventSubscriptions.Add(subscription);
                return subscription;
            }
            catch
            {
                if (callbackHandle.IsAllocated)
                {
                    callbackHandle.Free();
                }
                throw;
            }
        }
    }

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

    bool IUnrealNativeScalarInvocationTransportV3.SupportsInt32x2Invocation =>
        _invokeInt32x2ByIdV3 != nint.Zero;

    unsafe UnrealNativeCallOutcomeV3
        IUnrealNativeScalarInvocationTransportV3.ExecuteInt32x2ById(
            UnrealObjectHandle target,
            ulong memberId,
            int argument0,
            int argument1,
            out int result)
    {
        if (_invokeInt32x2ByIdV3 == nint.Zero)
        {
            throw new NotSupportedException(
                "The native Unreal transport does not expose the int32x2 scalar lane.");
        }
        var execute =
            (delegate* unmanaged[Cdecl]<
                nint, ulong, ulong, int, int, int*, byte*, byte*, byte>)
            _invokeInt32x2ByIdV3;

        lock (_lifetimeGate)
        {
            ThrowIfDisposed();
            int returnValue = 0;
            byte invocationError = 0;
            byte handleError = 0;
            UnrealNativeCallStatusV3 status = (UnrealNativeCallStatusV3)execute(
                _context,
                target.Value,
                memberId,
                argument0,
                argument1,
                &returnValue,
                &invocationError,
                &handleError);
            if (!Enum.IsDefined(status))
            {
                throw new InvalidOperationException(
                    "The native Unreal scalar transport returned an invalid status.");
            }
            result = returnValue;
            return new UnrealNativeCallOutcomeV3(
                status,
                (UnrealInvocationError)invocationError,
                (UnrealObjectHandleError)handleError,
                status == UnrealNativeCallStatusV3.Success ? 1 : 0,
                0,
                0,
                0,
                string.Empty);
        }
    }

    bool IUnrealNativeFixedInvocationTransportV3.SupportsFixedInvocation =>
        _invokeFixedByIdV3 != nint.Zero;

    unsafe UnrealNativeCallOutcomeV3
        IUnrealNativeFixedInvocationTransportV3.ExecuteFixedById(
            UnrealObjectHandle target,
            ulong memberId,
            ReadOnlySpan<UnrealNativeValueV3> arguments,
            Span<UnrealNativeValueV3> results)
    {
        if (_invokeFixedByIdV3 == nint.Zero)
        {
            throw new NotSupportedException(
                "The native Unreal transport does not expose the fixed-value lane.");
        }
        if (arguments.Length > UnrealNativeAbiV3.MaximumFixedArgumentCount ||
            results.Length > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(arguments),
                "Fixed invocation accepts at most four arguments and one result.");
        }
        var execute =
            (delegate* unmanaged[Cdecl]<
                nint, ulong, ulong, UnrealNativeValueV3*, uint,
                UnrealNativeValueV3*, byte*, byte*, byte>)
            _invokeFixedByIdV3;

        lock (_lifetimeGate)
        {
            ThrowIfDisposed();
            byte invocationError = 0;
            byte handleError = 0;
            fixed (UnrealNativeValueV3* argumentPointer = arguments)
            fixed (UnrealNativeValueV3* resultPointer = results)
            {
                UnrealNativeCallStatusV3 status = (UnrealNativeCallStatusV3)execute(
                    _context,
                    target.Value,
                    memberId,
                    argumentPointer,
                    checked((uint)arguments.Length),
                    resultPointer,
                    &invocationError,
                    &handleError);
                if (!Enum.IsDefined(status))
                {
                    throw new InvalidOperationException(
                        "The native Unreal fixed-value transport returned an invalid status.");
                }
                return new UnrealNativeCallOutcomeV3(
                    status,
                    (UnrealInvocationError)invocationError,
                    (UnrealObjectHandleError)handleError,
                    status == UnrealNativeCallStatusV3.Success ? results.Length : 0,
                    0,
                    0,
                    0,
                    string.Empty);
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
            if (_unsubscribeEvent is not null)
            {
                foreach (NativeEventSubscription subscription in _eventSubscriptions.ToArray())
                {
                    _unsubscribeEvent(_context, subscription.SubscriptionId);
                    subscription.Detach();
                }
                _eventSubscriptions.Clear();
            }
            _disposed = true;
            _releaseContext(_context);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void Unsubscribe(NativeEventSubscription subscription)
    {
        lock (_lifetimeGate)
        {
            if (!subscription.IsActive || !_eventSubscriptions.Remove(subscription))
            {
                return;
            }
            if (!_disposed && _unsubscribeEvent is not null)
            {
                _unsubscribeEvent(_context, subscription.SubscriptionId);
            }
            subscription.Detach();
        }
    }

    private static void ValidateEventHandler(
        UnrealEventDescriptor descriptor,
        Delegate handler)
    {
        System.Reflection.MethodInfo invoke = handler.GetType().GetMethod("Invoke") ??
            throw new ArgumentException("The event handler is not a delegate.", nameof(handler));
        System.Reflection.ParameterInfo[] parameters = invoke.GetParameters();
        if (invoke.ReturnType != descriptor.ReturnType ||
            parameters.Length != descriptor.Parameters.Count)
        {
            throw new ArgumentException(
                "The event handler does not match the reflected delegate signature.",
                nameof(handler));
        }
        for (int index = 0; index < parameters.Length; index++)
        {
            Type expected = descriptor.Parameters[index].Type;
            Type actual = parameters[index].ParameterType;
            if (actual.IsByRef)
            {
                actual = actual.GetElementType()!;
            }
            if (actual != expected)
            {
                throw new ArgumentException(
                    $"Event handler parameter {index} does not match '{descriptor.Parameters[index].Name}'.",
                    nameof(handler));
            }
        }
    }

    private static unsafe void ReceiveEvent(nint userData, nint framePointer)
    {
        if (userData == nint.Zero || framePointer == nint.Zero)
        {
            return;
        }
        EventCallbackBox? box = null;
        try
        {
            box = GCHandle.FromIntPtr(userData).Target as EventCallbackBox;
            if (box is null)
            {
                return;
            }
            UnrealNativeEventFrameV4 frame =
                Marshal.PtrToStructure<UnrealNativeEventFrameV4>(framePointer);
            if (frame.AbiVersion != UnrealNativeEventAbiV4.Version ||
                frame.FrameSize < UnrealNativeEventAbiV4.FrameSize ||
                frame.Reserved != 0 || frame.Reserved2 != 0 ||
                frame.SubscriptionId == 0 || frame.Source == 0 ||
                frame.EventId != box.Descriptor.EventId ||
                frame.ArgumentCount != box.Descriptor.Parameters.Count ||
                (frame.ArgumentCount > 0 && frame.Arguments == nint.Zero) ||
                (frame.ByteCount > 0 && frame.ByteBuffer == nint.Zero) ||
                frame.ArgumentCount > int.MaxValue || frame.ByteCount > int.MaxValue)
            {
                throw new InvalidOperationException(
                    "The native Unreal event ABI v4 callback returned an invalid frame.");
            }

            var values = new ReadOnlySpan<UnrealNativeValueV3>(
                (void*)frame.Arguments,
                checked((int)frame.ArgumentCount));
            var arena = new ReadOnlySpan<byte>(
                (void*)frame.ByteBuffer,
                checked((int)frame.ByteCount));
            object?[] arguments = new object?[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                if (!UnrealNativeValueCodecV3.TryDecodeObject(
                        box.Descriptor.Parameters[index].Type,
                        values[index],
                        arena,
                        arena.Length,
                        out arguments[index]))
                {
                    throw new InvalidOperationException(
                        $"Native Unreal event parameter '{box.Descriptor.Parameters[index].Name}' could not be decoded.");
                }
            }
            box.Handler.DynamicInvoke(arguments);
            box.LastDispatchException = null;
        }
        catch (Exception exception)
        {
            if (box is not null)
            {
                box.LastDispatchException =
                    exception is System.Reflection.TargetInvocationException invocation &&
                    invocation.InnerException is not null
                        ? invocation.InnerException
                        : exception;
            }
        }
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
    private struct NativeTransportTableV2Events
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
        public nint EventTransport;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTransportTableV2SharedData
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
        public nint EventTransport;
        public nint SharedDataTransport;
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

    private sealed class EventCallbackBox(
        UnrealEventDescriptor descriptor,
        Delegate handler)
    {
        public UnrealEventDescriptor Descriptor { get; } = descriptor;

        public Delegate Handler { get; } = handler;

        public Exception? LastDispatchException;
    }

    private sealed class NativeEventSubscription(
        InProcessUnrealNativeInvocationTransport owner,
        ulong subscriptionId,
        EventCallbackBox box,
        GCHandle callbackHandle) : IUnrealEventSubscription
    {
        private InProcessUnrealNativeInvocationTransport? _owner = owner;
        private GCHandle _callbackHandle = callbackHandle;

        public ulong SubscriptionId { get; } = subscriptionId;

        public bool IsActive => _owner is not null;

        public Exception? LastDispatchException =>
            Volatile.Read(ref box.LastDispatchException);

        public void Dispose() => _owner?.Unsubscribe(this);

        public void Detach()
        {
            _owner = null;
            if (_callbackHandle.IsAllocated)
            {
                _callbackHandle.Free();
            }
        }
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
