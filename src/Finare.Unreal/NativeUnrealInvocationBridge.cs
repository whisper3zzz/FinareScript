using System.Buffers;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Finare.Unreal;

public enum UnrealInvocationValueKind : byte
{
    Void = 0,
    Boolean = 1,
    Int32 = 2,
    String = 3,
    Int8 = 4,
    UInt8 = 5,
    Int16 = 6,
    UInt16 = 7,
    UInt32 = 8,
    Int64 = 9,
    UInt64 = 10,
    Float32 = 11,
    Float64 = 12,
    Name = 13,
    Text = 14,
    Object = 15,
    Class = 16,
    Enum = 17,
    Vector = 18,
    Rotator = 19,
    Transform = 20,
    Array = 22,
    Set = 23,
    Map = 24,
    SoftObject = 27,
    SoftClass = 28,
}

public enum UnrealObjectHandleError : byte
{
    None = 0,
    NotGameThread = 1,
    RegistryWorldUnavailable = 2,
    NullObject = 3,
    WorldMismatch = 4,
    InvalidHandle = 5,
    StaleHandle = 6,
    InvalidScope = 7,
}

public enum UnrealInvocationError : byte
{
    None = 0,
    ObjectHandleFailure = 1,
    InvalidMemberPath = 2,
    TargetTypeNotFound = 3,
    TargetTypeMismatch = 4,
    PropertyNotFound = 5,
    PropertyNotReadable = 6,
    PropertyNotWritable = 7,
    FunctionNotFound = 8,
    FunctionNotCallable = 9,
    UnsupportedValueType = 10,
    ValueTypeMismatch = 11,
    ArgumentCountMismatch = 12,
    UnsupportedParameter = 13,
    TransportUnavailable = 14,
}

public readonly record struct UnrealInvocationValue
{
    private UnrealInvocationValue(
        UnrealInvocationValueKind kind,
        bool booleanValue = default,
        int int32Value = default,
        long signedIntegerValue = default,
        ulong unsignedIntegerValue = default,
        double floatingPointValue = default,
        string? stringValue = default,
        UnrealObjectHandle objectHandle = default,
        UnrealVector vectorValue = default,
        UnrealRotator rotatorValue = default,
        UnrealTransform transformValue = default,
        ulong enumTypeId = 0,
        ReadOnlyMemory<byte>? containerPayload = null)
    {
        Kind = kind;
        BooleanValue = booleanValue;
        Int32Value = int32Value;
        SignedIntegerValue = signedIntegerValue;
        UnsignedIntegerValue = unsignedIntegerValue;
        FloatingPointValue = floatingPointValue;
        StringValue = stringValue;
        ObjectHandle = objectHandle;
        VectorValue = vectorValue;
        RotatorValue = rotatorValue;
        TransformValue = transformValue;
        EnumTypeId = enumTypeId;
        ContainerPayload = containerPayload;
    }

    public UnrealInvocationValueKind Kind { get; }

    public bool BooleanValue { get; }

    public int Int32Value { get; }

    public long SignedIntegerValue { get; }

    public ulong UnsignedIntegerValue { get; }

    public double FloatingPointValue { get; }

    public string? StringValue { get; }

    public UnrealObjectHandle ObjectHandle { get; }

    public UnrealVector VectorValue { get; }

    public UnrealRotator RotatorValue { get; }

    public UnrealTransform TransformValue { get; }
    public ulong EnumTypeId { get; }

    internal ReadOnlyMemory<byte>? ContainerPayload { get; }

    public static UnrealInvocationValue Void => default;

    public static UnrealInvocationValue FromBoolean(bool value) =>
        new(UnrealInvocationValueKind.Boolean, booleanValue: value);

    public static UnrealInvocationValue FromInt32(int value) =>
        new(UnrealInvocationValueKind.Int32, int32Value: value);

    public static UnrealInvocationValue FromString(string value) =>
        new(
            UnrealInvocationValueKind.String,
            stringValue: value ?? throw new ArgumentNullException(nameof(value)));

    public static UnrealInvocationValue FromSoftObject(UnrealSoftObjectHandle value) =>
        new(UnrealInvocationValueKind.SoftObject, stringValue: value.AssetPath ?? string.Empty);

    public static UnrealInvocationValue FromSoftClass(UnrealSoftClassHandle value) =>
        new(UnrealInvocationValueKind.SoftClass, stringValue: value.AssetPath ?? string.Empty);

    public static UnrealInvocationValue FromInt8(sbyte value) =>
        new(UnrealInvocationValueKind.Int8, signedIntegerValue: value);

    public static UnrealInvocationValue FromUInt8(byte value) =>
        new(UnrealInvocationValueKind.UInt8, unsignedIntegerValue: value);

    public static UnrealInvocationValue FromInt16(short value) =>
        new(UnrealInvocationValueKind.Int16, signedIntegerValue: value);

    public static UnrealInvocationValue FromUInt16(ushort value) =>
        new(UnrealInvocationValueKind.UInt16, unsignedIntegerValue: value);

    public static UnrealInvocationValue FromUInt32(uint value) =>
        new(UnrealInvocationValueKind.UInt32, unsignedIntegerValue: value);

    public static UnrealInvocationValue FromInt64(long value) =>
        new(UnrealInvocationValueKind.Int64, signedIntegerValue: value);

    public static UnrealInvocationValue FromUInt64(ulong value) =>
        new(UnrealInvocationValueKind.UInt64, unsignedIntegerValue: value);

    public static UnrealInvocationValue FromFloat32(float value) =>
        new(UnrealInvocationValueKind.Float32, floatingPointValue: value);

    public static UnrealInvocationValue FromFloat64(double value) =>
        new(UnrealInvocationValueKind.Float64, floatingPointValue: value);

    public static UnrealInvocationValue FromName(UnrealName value) =>
        new(UnrealInvocationValueKind.Name, stringValue: value.Value);

    public static UnrealInvocationValue FromText(UnrealText value) =>
        new(UnrealInvocationValueKind.Text, stringValue: value.Value);

    public static UnrealInvocationValue FromObjectHandle(
        UnrealObjectHandle value,
        bool isClass = false) =>
        new(
            isClass ? UnrealInvocationValueKind.Class : UnrealInvocationValueKind.Object,
            objectHandle: value);

    public static UnrealInvocationValue FromEnum(UnrealEnumValue value) =>
        string.IsNullOrWhiteSpace(value.TypePath)
            ? throw new ArgumentException("A typed Unreal enum value is required.", nameof(value))
            : new(
                UnrealInvocationValueKind.Enum,
                signedIntegerValue: value.Value,
                stringValue: value.TypePath,
                enumTypeId: UnrealMemberId.FromPath(value.TypePath));

    public static UnrealInvocationValue FromEnumTypeId(ulong typeId, long value) =>
        typeId == 0 ? throw new ArgumentException("An enum type ID is required.", nameof(typeId)) :
        new(UnrealInvocationValueKind.Enum, signedIntegerValue: value, enumTypeId: typeId);

    public static UnrealInvocationValue FromVector(UnrealVector value) =>
        new(UnrealInvocationValueKind.Vector, vectorValue: value);

    public static UnrealInvocationValue FromRotator(UnrealRotator value) =>
        new(UnrealInvocationValueKind.Rotator, rotatorValue: value);

    public static UnrealInvocationValue FromTransform(UnrealTransform value) =>
        new(UnrealInvocationValueKind.Transform, transformValue: value);

    internal static UnrealInvocationValue FromContainerPayload(
        UnrealInvocationValueKind kind,
        byte[] payload) =>
        kind is UnrealInvocationValueKind.Array or UnrealInvocationValueKind.Set or UnrealInvocationValueKind.Map
            ? new(
                kind,
                containerPayload: (payload ?? throw new ArgumentNullException(nameof(payload))).ToArray())
            : throw new ArgumentOutOfRangeException(nameof(kind));
}

public readonly record struct UnrealInvocationResponse
{
    private UnrealInvocationResponse(
        bool isSuccess,
        UnrealInvocationValue value,
        UnrealInvocationError error,
        UnrealObjectHandleError handleError,
        string message)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
        HandleError = handleError;
        Message = message;
    }

    public bool IsSuccess { get; }

    public UnrealInvocationValue Value { get; }

    public UnrealInvocationError Error { get; }

    public UnrealObjectHandleError HandleError { get; }

    public string Message { get; }

    public static UnrealInvocationResponse Succeeded(UnrealInvocationValue value) =>
        new(
            true,
            value,
            UnrealInvocationError.None,
            UnrealObjectHandleError.None,
            string.Empty);

    public static UnrealInvocationResponse Failed(
        UnrealInvocationError error,
        string message,
        UnrealObjectHandleError handleError = UnrealObjectHandleError.None)
    {
        if (error == UnrealInvocationError.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(error),
                "A failed Unreal invocation requires a non-success error category.");
        }
        if (error == UnrealInvocationError.ObjectHandleFailure &&
            handleError == UnrealObjectHandleError.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(handleError),
                "An object-handle failure requires its lifecycle error category.");
        }
        if (error != UnrealInvocationError.ObjectHandleFailure &&
            handleError != UnrealObjectHandleError.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(handleError),
                "Only an object-handle failure may carry a lifecycle error category.");
        }

        return new UnrealInvocationResponse(
            false,
            UnrealInvocationValue.Void,
            error,
            handleError,
            string.IsNullOrWhiteSpace(message)
                ? "The native Unreal invocation failed."
                : message);
    }
}

public interface IUnrealNativeInvocationTransport
{
    UnrealInvocationResponse ReadProperty(
        UnrealObjectHandle target,
        string propertyPath);

    UnrealInvocationResponse WriteProperty(
        UnrealObjectHandle target,
        string propertyPath,
        UnrealInvocationValue value);

    UnrealInvocationResponse Invoke(
        UnrealObjectHandle target,
        string functionPath,
        IReadOnlyList<UnrealInvocationValue> arguments);
}

public interface IUnrealNativeFastInvocationTransport
{
    bool SupportsFastInvocation { get; }

    UnrealInvocationResponse ReadPropertyById(
        UnrealObjectHandle target,
        ulong memberId);

    UnrealInvocationResponse WritePropertyById(
        UnrealObjectHandle target,
        ulong memberId,
        UnrealInvocationValue value);

    UnrealInvocationResponse InvokeById(
        UnrealObjectHandle target,
        ulong memberId,
        IReadOnlyList<UnrealInvocationValue> arguments);
}

public sealed class UnrealInvocationException : Exception
{
    public UnrealInvocationException(
        UnrealInvocationError error,
        UnrealObjectHandleError handleError,
        string message,
        UnrealObjectHandle target = default,
        ulong? memberId = null,
        string? memberPath = null)
        : base(message)
    {
        Error = error;
        HandleError = handleError;
        Target = target;
        MemberId = memberId;
        MemberPath = memberPath ?? string.Empty;
    }

    public UnrealInvocationError Error { get; }

    public UnrealObjectHandleError HandleError { get; }

    public UnrealObjectHandle Target { get; }

    public ulong? MemberId { get; }

    public string MemberPath { get; }
}

/// <summary>
/// Converts generated-wrapper CLR values to the owned native invocation
/// contract. A world/runtime host supplies the concrete process transport.
/// </summary>
public sealed class NativeUnrealInvocationBridge
    : IUnrealInvocationBridge,
      IUnrealNativeBindingBridge,
      IUnrealNativeObjectProvider,
      IUnrealNativeObjectScopeFactory,
      IUnrealObjectReferenceScopeFactory,
      IUnrealCallbackProvider,
      IUnrealCallbackScopeFactory,
      IUnrealFastInvocationBridge,
      IUnrealTypedInvocationBridge,
      IUnrealOutputInvocationBridge,
      IUnrealParentInvocationBridge,
      IUnrealEventBridge,
      IUnrealOwnedEventBridge,
      IUnrealInvocationMetrics,
      IUnrealInvocationProfiler,
      IUnrealWorldDataPlaneProvider,
      IUnrealAsyncServiceProvider
{
    private const int CallFrameArenaByteCapacity = 4096;
    private const int ParentInputArenaByteCapacity = 64 * 1024;
    private readonly IUnrealNativeInvocationTransport _transport;
    private readonly ConcurrentDictionary<string, byte> _nativeOnlyPaths = new(StringComparer.Ordinal);
    private readonly object _nativeObjectGate = new();
    private UnrealNativeObjectScope? _nativeObjects;
    private UnrealCallbackScope? _callbacks;
    private long _callFrameInvocationCount;
    private long _legacyInvocationCount;
    private long _directThunkProfilerCount;
    private long _directThunkProfilerTicks;
    private long _callFrameProfilerCount;
    private long _callFrameProfilerTicks;
    private long _reflectionProfilerCount;
    private long _reflectionProfilerTicks;
    private int _profilerEnabled;

    public NativeUnrealInvocationBridge(
        IUnrealNativeInvocationTransport transport,
        ulong worldHostId = 0)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        DataPlane = new UnrealWorldDataPlane(
            transport as IUnrealSharedSnapshotTransport,
            worldHostId);
    }

    public UnrealWorldDataPlane DataPlane { get; }

    public bool SupportsParentInvocation =>
        _transport is IUnrealNativeCallFrameTransportV3 frameTransport &&
        _transport is IUnrealNativeCallFrameScopeFactory &&
        (frameTransport.Capabilities &
            (UnrealNativeCapabilitiesV3.BlueprintParentCalls |
             UnrealNativeCapabilitiesV3.CorrelatedResults)) ==
        (UnrealNativeCapabilitiesV3.BlueprintParentCalls |
         UnrealNativeCapabilitiesV3.CorrelatedResults);

    public UnrealCallbackScope Callbacks
    {
        get { lock (_nativeObjectGate) return _callbacks ??= CreateCallbackScope(); }
    }

    UnrealCallbackScope IUnrealCallbackScopeFactory.CreateCallbackScope() => CreateCallbackScope();

    private UnrealCallbackScope CreateCallbackScope() =>
        _transport is IUnrealNativeCallbackTransport callbacks ? new(callbacks) :
        throw new NotSupportedException("The installed transport does not support typed callbacks.");

    public UnrealNativeObjectScope NativeObjects
    {
        get
        {
            lock (_nativeObjectGate) return _nativeObjects ??= CreateNativeObjectScope();
        }
    }

    UnrealNativeObjectScope IUnrealNativeObjectScopeFactory.CreateNativeObjectScope() => CreateNativeObjectScope();

    private UnrealNativeObjectScope CreateNativeObjectScope() =>
        _transport is IUnrealNativeObjectTransport native
            ? new UnrealNativeObjectScope(native)
            : throw new NotSupportedException("The installed transport does not support native objects.");

    UnrealObjectReferenceScope IUnrealObjectReferenceScopeFactory.GetObjectReferenceScope()
    {
        lock (_nativeObjectGate)
        {
            // A World bridge is a scope factory, not the owner of a generation's
            // lease scope. Every generation must receive a distinct native scope;
            // otherwise disposing one generation retires leases owned by another.
            return _transport is IUnrealObjectReferenceTransport references
                ? new UnrealObjectReferenceScope(references)
                : throw new NotSupportedException(
                    "The installed transport does not support UObject reference leases.");
        }
    }

    public void RequireNativeBindings(IReadOnlyList<string> memberPaths)
    {
        ArgumentNullException.ThrowIfNull(memberPaths);
        if (!SupportsFastInvocation)
        {
            throw new NotSupportedException("Native extensions require generated native binding support in the installed transport.");
        }
        foreach (string path in memberPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
        }
        foreach (string path in memberPaths) _nativeOnlyPaths.TryAdd(path, 0);
    }

    private void RequireReflectionAllowed(string memberPath)
    {
        if (_nativeOnlyPaths.ContainsKey(memberPath))
        {
            throw new NotSupportedException($"Native-only member '{memberPath}' has no available generated route. Rebuild/deploy its native extension; reflection fallback is forbidden.");
        }
    }

    IUnrealAsyncServiceTransport? IUnrealAsyncServiceProvider.AsyncTransport =>
        _transport as IUnrealAsyncServiceTransport;

    public bool SupportsFastInvocation =>
        _transport is IUnrealNativeCallFrameTransportV3 callFrameTransport &&
        callFrameTransport.Capabilities != UnrealNativeCapabilitiesV3.None ||
        _transport is IUnrealNativeFastInvocationTransport fastTransport &&
        fastTransport.SupportsFastInvocation;

    public bool SupportsTypedInvocation =>
        _transport is IUnrealNativeCallFrameTransportV3 callFrameTransport &&
        callFrameTransport.Capabilities != UnrealNativeCapabilitiesV3.None;

    public long CallFrameInvocationCount => Interlocked.Read(ref _callFrameInvocationCount);

    public long LegacyInvocationCount => Interlocked.Read(ref _legacyInvocationCount);

    void IUnrealInvocationProfiler.SetProfilerEnabled(bool enabled)
    {
        Volatile.Write(ref _profilerEnabled, enabled ? 1 : 0);
        if (_transport is IUnrealGameplayTransportProfiler transportProfiler)
        {
            transportProfiler.SetProfilerEnabled(enabled);
        }
    }

    UnrealInvocationProfilerSnapshot IUnrealInvocationProfiler.GetProfilerSnapshot() =>
        GetProfilerSnapshot();

    internal void SetProfilerEnabled(bool enabled) =>
        ((IUnrealInvocationProfiler)this).SetProfilerEnabled(enabled);

    internal UnrealInvocationProfilerSnapshot GetProfilerSnapshot()
    {
        UnrealGameplayTransportProfilerSnapshot transport =
            _transport is IUnrealGameplayTransportProfiler transportProfiler
                ? transportProfiler.GetProfilerSnapshot()
                : default;
        return new UnrealInvocationProfilerSnapshot(
            Interlocked.Read(ref _directThunkProfilerCount),
            Interlocked.Read(ref _directThunkProfilerTicks),
            Interlocked.Read(ref _callFrameProfilerCount),
            Interlocked.Read(ref _callFrameProfilerTicks),
            Interlocked.Read(ref _reflectionProfilerCount),
            Interlocked.Read(ref _reflectionProfilerTicks),
            transport.CommandCount,
            transport.CommandStopwatchTicks,
            transport.EventCount,
            transport.EventStopwatchTicks,
            transport.TraceCount,
            transport.TraceStopwatchTicks,
            transport.OverlapCount,
            transport.OverlapStopwatchTicks);
    }

    public IUnrealEventSubscription Subscribe(
        UnrealObjectHandle source,
        UnrealEventDescriptor descriptor,
        Delegate handler)
    {
        if (_transport is not IUnrealNativeEventTransport eventTransport ||
            !eventTransport.SupportsEvents)
        {
            throw new NotSupportedException(
                "The native Unreal transport does not support reflected delegate subscriptions.");
        }
        return eventTransport.Subscribe(source, descriptor, handler);
    }

    IUnrealEventSubscription IUnrealOwnedEventBridge.Subscribe(
        UnrealObjectHandle source,
        UnrealEventDescriptor descriptor,
        Delegate handler,
        UnrealObjectScript owner)
    {
        if (_transport is not IUnrealNativeEventTransport eventTransport ||
            !eventTransport.SupportsEvents)
        {
            throw new NotSupportedException(
                "The native Unreal transport does not support reflected delegate subscriptions.");
        }
        return _transport is IUnrealOwnedEventBridge owned
            ? owned.Subscribe(source, descriptor, handler, owner)
            : eventTransport.Subscribe(source, descriptor, handler);
    }

    public T ReadProperty<T>(UnrealObjectHandle target, string propertyPath)
    {
        ValidateRequest(target, propertyPath);
        RequireReflectionAllowed(propertyPath);
        long profilerStart = BeginProfilerSample();
        try
        {
            UnrealInvocationValue value = RequireSuccess(
                _transport.ReadProperty(target, propertyPath),
                target,
                null,
                propertyPath);
            return ConvertFromNative<T>(value, propertyPath, useSoftKinds: true);
        }
        finally
        {
            CompleteReflectionProfilerSample(profilerStart);
        }
    }

    public void WriteProperty<T>(
        UnrealObjectHandle target,
        string propertyPath,
        T value)
    {
        ValidateRequest(target, propertyPath);
        RequireReflectionAllowed(propertyPath);
        long profilerStart = BeginProfilerSample();
        try
        {
            UnrealInvocationValue nativeValue = ConvertToNative(value, propertyPath, useSoftKinds: true);
            UnrealInvocationValue result = RequireSuccess(
                _transport.WriteProperty(target, propertyPath, nativeValue),
                target,
                null,
                propertyPath);
            RequireVoid(result, propertyPath);
        }
        finally
        {
            CompleteReflectionProfilerSample(profilerStart);
        }
    }

    public T Invoke<T>(
        UnrealObjectHandle target,
        string functionPath,
        IReadOnlyList<object?> arguments)
    {
        ValidateRequest(target, functionPath);
        RequireReflectionAllowed(functionPath);
        long profilerStart = BeginProfilerSample();
        try
        {
            IReadOnlyList<UnrealInvocationValue> nativeArguments = ConvertArguments(
                arguments,
                functionPath);
            UnrealInvocationValue result = RequireSuccess(
                _transport.Invoke(target, functionPath, nativeArguments),
                target,
                null,
                functionPath);
            return ConvertFromNative<T>(result, functionPath);
        }
        finally
        {
            CompleteReflectionProfilerSample(profilerStart);
        }
    }

    public void Invoke(
        UnrealObjectHandle target,
        string functionPath,
        IReadOnlyList<object?> arguments)
    {
        ValidateRequest(target, functionPath);
        RequireReflectionAllowed(functionPath);
        long profilerStart = BeginProfilerSample();
        try
        {
            IReadOnlyList<UnrealInvocationValue> nativeArguments = ConvertArguments(
                arguments,
                functionPath);
            UnrealInvocationValue result = RequireSuccess(
                _transport.Invoke(target, functionPath, nativeArguments),
                target,
                null,
                functionPath);
            RequireVoid(result, functionPath);
        }
        finally
        {
            CompleteReflectionProfilerSample(profilerStart);
        }
    }

    public T ReadProperty<T>(
        UnrealObjectHandle target,
        ulong memberId,
        string propertyPath)
    {
        ValidateFastRequest(target, memberId, propertyPath);
        if (typeof(T) == typeof(UnrealByteBuffer)) RequireByteBuffers();
        Span<byte> arena = stackalloc byte[CallFrameArenaByteCapacity];
        if (TryExecuteCallFrameResult<T>(
                UnrealNativeOperationV3.ReadProperty,
                target,
                memberId,
                ReadOnlySpan<UnrealNativeValueV3>.Empty,
                arena,
                0,
                UnrealNativeCapabilitiesV3.None,
                out T callFrameResult,
                propertyPath))
        {
            return callFrameResult;
        }
        Interlocked.Increment(ref _legacyInvocationCount);
        if (!TryGetFastTransport(out IUnrealNativeFastInvocationTransport? transport))
        {
            return ReadProperty<T>(target, propertyPath);
        }
        long profilerStart = BeginProfilerSample();
        UnrealInvocationResponse response = transport!.ReadPropertyById(target, memberId);
        if (IsMissingGeneratedMember(response))
        {
            return ReadProperty<T>(target, propertyPath);
        }
        CompleteDirectProfilerSample(profilerStart);
        UnrealInvocationValue value = RequireSuccess(response, target, memberId, propertyPath);
        return ConvertFromNative<T>(value, propertyPath);
    }

    public void WriteProperty<T>(
        UnrealObjectHandle target,
        ulong memberId,
        string propertyPath,
        T value)
    {
        ValidateFastRequest(target, memberId, propertyPath);
        if (typeof(T) == typeof(UnrealByteBuffer)) RequireByteBuffers();
        int arenaCapacity = value is UnrealByteBuffer buffer ? Math.Max(CallFrameArenaByteCapacity, buffer.Length) : CallFrameArenaByteCapacity;
        Span<byte> arena = arenaCapacity == CallFrameArenaByteCapacity ? stackalloc byte[CallFrameArenaByteCapacity] : new byte[arenaCapacity];
        int byteCount = 0;
        if (UnrealNativeValueCodecV3.TryEncode(
                value,
                arena,
                ref byteCount,
                out UnrealNativeValueV3 argument,
                out UnrealNativeCapabilitiesV3 requiredCapability) &&
            TryExecuteCallFrameVoid(
                UnrealNativeOperationV3.WriteProperty,
                target,
                memberId,
                MemoryMarshal.CreateReadOnlySpan(ref argument, 1),
                arena,
                byteCount,
                requiredCapability,
                propertyPath))
        {
            return;
        }
        Interlocked.Increment(ref _legacyInvocationCount);
        UnrealInvocationValue nativeValue = ConvertToNative(value, propertyPath);
        if (!TryGetFastTransport(out IUnrealNativeFastInvocationTransport? transport))
        {
            WriteProperty(target, propertyPath, value);
            return;
        }
        long profilerStart = BeginProfilerSample();
        UnrealInvocationResponse response = transport!.WritePropertyById(
            target,
            memberId,
            nativeValue);
        if (IsMissingGeneratedMember(response))
        {
            WriteProperty(target, propertyPath, value);
            return;
        }
        CompleteDirectProfilerSample(profilerStart);
        UnrealInvocationValue result = RequireSuccess(response, target, memberId, propertyPath);
        RequireVoid(result, propertyPath);
    }

    public T Invoke<T>(
        UnrealObjectHandle target,
        ulong memberId,
        string functionPath,
        IReadOnlyList<object?> arguments)
    {
        ValidateFastRequest(target, memberId, functionPath);
        if (!TryGetFastTransport(out IUnrealNativeFastInvocationTransport? transport))
        {
            return Invoke<T>(target, functionPath, arguments);
        }
        IReadOnlyList<UnrealInvocationValue> nativeArguments = ConvertArguments(
            arguments,
            functionPath);
        long profilerStart = BeginProfilerSample();
        UnrealInvocationResponse response = transport!.InvokeById(
            target,
            memberId,
            nativeArguments);
        if (IsMissingGeneratedMember(response))
        {
            return Invoke<T>(target, functionPath, arguments);
        }
        CompleteDirectProfilerSample(profilerStart);
        UnrealInvocationValue result = RequireSuccess(response, target, memberId, functionPath);
        return ConvertFromNative<T>(result, functionPath);
    }

    public void Invoke(
        UnrealObjectHandle target,
        ulong memberId,
        string functionPath,
        IReadOnlyList<object?> arguments)
    {
        ValidateFastRequest(target, memberId, functionPath);
        if (!TryGetFastTransport(out IUnrealNativeFastInvocationTransport? transport))
        {
            Invoke(target, functionPath, arguments);
            return;
        }
        IReadOnlyList<UnrealInvocationValue> nativeArguments = ConvertArguments(
            arguments,
            functionPath);
        long profilerStart = BeginProfilerSample();
        UnrealInvocationResponse response = transport!.InvokeById(
            target,
            memberId,
            nativeArguments);
        if (IsMissingGeneratedMember(response))
        {
            Invoke(target, functionPath, arguments);
            return;
        }
        CompleteDirectProfilerSample(profilerStart);
        UnrealInvocationValue result = RequireSuccess(response, target, memberId, functionPath);
        RequireVoid(result, functionPath);
    }

    public TResult Invoke<TResult>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath)
    {
        ValidateFastRequest(target, memberId, memberPath);
        if (typeof(TResult) == typeof(UnrealByteBuffer))
        {
            return (TResult)InvokeWithOutputs(target, memberId, memberPath, typeof(TResult), []).ReturnValue!;
        }
        if (TryExecuteFixedResult(
                target,
                memberId,
                ReadOnlySpan<UnrealNativeValueV3>.Empty,
                out TResult fixedResult))
        {
            return fixedResult;
        }
        Span<byte> arena = stackalloc byte[CallFrameArenaByteCapacity];
        if (TryExecuteCallFrameResult(
                UnrealNativeOperationV3.Invoke,
                target,
                memberId,
                ReadOnlySpan<UnrealNativeValueV3>.Empty,
                arena,
                0,
                UnrealNativeCapabilitiesV3.None,
                out TResult result))
        {
            return result;
        }
        Interlocked.Increment(ref _legacyInvocationCount);
        return Invoke<TResult>(
            target,
            memberId,
            memberPath,
            (IReadOnlyList<object?>)Array.Empty<object?>());
    }

    public TResult Invoke<TResult, T0>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T0 argument0)
    {
        ValidateFastRequest(target, memberId, memberPath);
        if (typeof(TResult) == typeof(UnrealByteBuffer) || typeof(T0) == typeof(UnrealByteBuffer))
        {
            return (TResult)InvokeWithOutputs(target, memberId, memberPath, typeof(TResult), [new(typeof(T0), UnrealParameterDirection.Input, argument0)]).ReturnValue!;
        }
        Span<UnrealNativeValueV3> arguments = stackalloc UnrealNativeValueV3[1];
        if (UnrealNativeFixedValueCodecV3.TryEncode(argument0, out arguments[0]) &&
            TryExecuteFixedResult(target, memberId, arguments, out TResult fixedResult))
        {
            return fixedResult;
        }
        Span<byte> arena = stackalloc byte[CallFrameArenaByteCapacity];
        int byteCount = 0;
        if (UnrealNativeValueCodecV3.TryEncode(
                argument0,
                arena,
                ref byteCount,
                out arguments[0],
                out UnrealNativeCapabilitiesV3 capabilities) &&
            TryExecuteCallFrameResult(
                UnrealNativeOperationV3.Invoke,
                target,
                memberId,
                arguments,
                arena,
                byteCount,
                capabilities,
                out TResult result))
        {
            return result;
        }
        Interlocked.Increment(ref _legacyInvocationCount);
        return Invoke<TResult>(
            target,
            memberId,
            memberPath,
            (IReadOnlyList<object?>)new object?[] { argument0 });
    }

    public TResult Invoke<TResult, T0, T1>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T0 argument0,
        T1 argument1)
    {
        ValidateFastRequest(target, memberId, memberPath);
        if (typeof(TResult) == typeof(UnrealByteBuffer) || typeof(T0) == typeof(UnrealByteBuffer) || typeof(T1) == typeof(UnrealByteBuffer))
        {
            return (TResult)InvokeWithOutputs(target, memberId, memberPath, typeof(TResult), [new(typeof(T0), UnrealParameterDirection.Input, argument0), new(typeof(T1), UnrealParameterDirection.Input, argument1)]).ReturnValue!;
        }
        if (typeof(TResult) == typeof(int) &&
            typeof(T0) == typeof(int) &&
            typeof(T1) == typeof(int) &&
            _transport is IUnrealNativeScalarInvocationTransportV3 scalarTransport &&
            scalarTransport.SupportsInt32x2Invocation)
        {
            int nativeArgument0 = Unsafe.As<T0, int>(ref argument0);
            int nativeArgument1 = Unsafe.As<T1, int>(ref argument1);
            long profilerStart = BeginProfilerSample();
            UnrealNativeCallOutcomeV3 scalarOutcome =
                scalarTransport.ExecuteInt32x2ById(
                    target,
                    memberId,
                    nativeArgument0,
                    nativeArgument1,
                    out int scalarResult);
            if (!IsMissingGeneratedMember(scalarOutcome))
            {
                CompleteDirectProfilerSample(profilerStart);
                RequireCallFrameSuccess(
                    scalarOutcome,
                    expectedResultCount: 1,
                    target,
                    memberId,
                    memberPath);
                Interlocked.Increment(ref _callFrameInvocationCount);
                return Unsafe.As<int, TResult>(ref scalarResult);
            }
        }
        Span<UnrealNativeValueV3> arguments = stackalloc UnrealNativeValueV3[2];
        if (UnrealNativeFixedValueCodecV3.TryEncode(argument0, out arguments[0]) &&
            UnrealNativeFixedValueCodecV3.TryEncode(argument1, out arguments[1]) &&
            TryExecuteFixedResult(target, memberId, arguments, out TResult fixedResult))
        {
            return fixedResult;
        }
        Span<byte> arena = stackalloc byte[CallFrameArenaByteCapacity];
        int byteCount = 0;
        if (UnrealNativeValueCodecV3.TryEncode(
                argument0,
                arena,
                ref byteCount,
                out arguments[0],
                out UnrealNativeCapabilitiesV3 capability0) &&
            UnrealNativeValueCodecV3.TryEncode(
                argument1,
                arena,
                ref byteCount,
                out arguments[1],
                out UnrealNativeCapabilitiesV3 capability1) &&
            TryExecuteCallFrameResult(
                UnrealNativeOperationV3.Invoke,
                target,
                memberId,
                arguments,
                arena,
                byteCount,
                capability0 | capability1,
                out TResult result))
        {
            return result;
        }
        Interlocked.Increment(ref _legacyInvocationCount);
        return Invoke<TResult>(
            target,
            memberId,
            memberPath,
            (IReadOnlyList<object?>)new object?[] { argument0, argument1 });
    }

    public TResult Invoke<TResult, T0, T1, T2>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T0 argument0,
        T1 argument1,
        T2 argument2)
    {
        ValidateFastRequest(target, memberId, memberPath);
        if (typeof(TResult) == typeof(UnrealByteBuffer) || typeof(T0) == typeof(UnrealByteBuffer) || typeof(T1) == typeof(UnrealByteBuffer) || typeof(T2) == typeof(UnrealByteBuffer))
        {
            return (TResult)InvokeWithOutputs(target, memberId, memberPath, typeof(TResult), [new(typeof(T0), UnrealParameterDirection.Input, argument0), new(typeof(T1), UnrealParameterDirection.Input, argument1), new(typeof(T2), UnrealParameterDirection.Input, argument2)]).ReturnValue!;
        }
        Span<UnrealNativeValueV3> arguments = stackalloc UnrealNativeValueV3[3];
        if (UnrealNativeFixedValueCodecV3.TryEncode(argument0, out arguments[0]) &&
            UnrealNativeFixedValueCodecV3.TryEncode(argument1, out arguments[1]) &&
            UnrealNativeFixedValueCodecV3.TryEncode(argument2, out arguments[2]) &&
            TryExecuteFixedResult(target, memberId, arguments, out TResult fixedResult))
        {
            return fixedResult;
        }
        Span<byte> arena = stackalloc byte[CallFrameArenaByteCapacity];
        int byteCount = 0;
        if (TryEncodeThree(
                argument0,
                argument1,
                argument2,
                arguments,
                arena,
                ref byteCount,
                out UnrealNativeCapabilitiesV3 capabilities) &&
            TryExecuteCallFrameResult(
                UnrealNativeOperationV3.Invoke,
                target,
                memberId,
                arguments,
                arena,
                byteCount,
                capabilities,
                out TResult result))
        {
            return result;
        }
        Interlocked.Increment(ref _legacyInvocationCount);
        return Invoke<TResult>(
            target,
            memberId,
            memberPath,
            (IReadOnlyList<object?>)new object?[] { argument0, argument1, argument2 });
    }

    public TResult Invoke<TResult, T0, T1, T2, T3>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T0 argument0,
        T1 argument1,
        T2 argument2,
        T3 argument3)
    {
        ValidateFastRequest(target, memberId, memberPath);
        if (typeof(TResult) == typeof(UnrealByteBuffer) || typeof(T0) == typeof(UnrealByteBuffer) || typeof(T1) == typeof(UnrealByteBuffer) || typeof(T2) == typeof(UnrealByteBuffer) || typeof(T3) == typeof(UnrealByteBuffer))
        {
            return (TResult)InvokeWithOutputs(target, memberId, memberPath, typeof(TResult), [new(typeof(T0), UnrealParameterDirection.Input, argument0), new(typeof(T1), UnrealParameterDirection.Input, argument1), new(typeof(T2), UnrealParameterDirection.Input, argument2), new(typeof(T3), UnrealParameterDirection.Input, argument3)]).ReturnValue!;
        }
        Span<UnrealNativeValueV3> arguments = stackalloc UnrealNativeValueV3[4];
        if (UnrealNativeFixedValueCodecV3.TryEncode(argument0, out arguments[0]) &&
            UnrealNativeFixedValueCodecV3.TryEncode(argument1, out arguments[1]) &&
            UnrealNativeFixedValueCodecV3.TryEncode(argument2, out arguments[2]) &&
            UnrealNativeFixedValueCodecV3.TryEncode(argument3, out arguments[3]) &&
            TryExecuteFixedResult(target, memberId, arguments, out TResult fixedResult))
        {
            return fixedResult;
        }
        Span<byte> arena = stackalloc byte[CallFrameArenaByteCapacity];
        int byteCount = 0;
        if (TryEncodeFour(
                argument0,
                argument1,
                argument2,
                argument3,
                arguments,
                arena,
                ref byteCount,
                out UnrealNativeCapabilitiesV3 capabilities) &&
            TryExecuteCallFrameResult(
                UnrealNativeOperationV3.Invoke,
                target,
                memberId,
                arguments,
                arena,
                byteCount,
                capabilities,
                out TResult result))
        {
            return result;
        }
        Interlocked.Increment(ref _legacyInvocationCount);
        return Invoke<TResult>(
            target,
            memberId,
            memberPath,
            (IReadOnlyList<object?>)new object?[]
            {
                argument0,
                argument1,
                argument2,
                argument3,
            });
    }

    public void Invoke(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath)
    {
        ValidateFastRequest(target, memberId, memberPath);
        if (TryExecuteFixedVoid(
                target,
                memberId,
                ReadOnlySpan<UnrealNativeValueV3>.Empty))
        {
            return;
        }
        if (TryExecuteCallFrameVoid(
                UnrealNativeOperationV3.Invoke,
                target,
                memberId,
                ReadOnlySpan<UnrealNativeValueV3>.Empty,
                Span<byte>.Empty,
                0,
                UnrealNativeCapabilitiesV3.None))
        {
            return;
        }
        Interlocked.Increment(ref _legacyInvocationCount);
        Invoke(
            target,
            memberId,
            memberPath,
            (IReadOnlyList<object?>)Array.Empty<object?>());
    }

    public void Invoke<T0>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T0 argument0)
    {
        ValidateFastRequest(target, memberId, memberPath);
        if (typeof(T0) == typeof(UnrealByteBuffer))
        {
            InvokeWithOutputs(target, memberId, memberPath, typeof(void), [new(typeof(T0), UnrealParameterDirection.Input, argument0)]);
            return;
        }
        Span<UnrealNativeValueV3> arguments = stackalloc UnrealNativeValueV3[1];
        if (UnrealNativeFixedValueCodecV3.TryEncode(argument0, out arguments[0]) &&
            TryExecuteFixedVoid(target, memberId, arguments))
        {
            return;
        }
        Span<byte> arena = stackalloc byte[CallFrameArenaByteCapacity];
        int byteCount = 0;
        if (UnrealNativeValueCodecV3.TryEncode(
                argument0,
                arena,
                ref byteCount,
                out arguments[0],
                out UnrealNativeCapabilitiesV3 capabilities) &&
            TryExecuteCallFrameVoid(
                UnrealNativeOperationV3.Invoke,
                target,
                memberId,
                arguments,
                arena,
                byteCount,
                capabilities))
        {
            return;
        }
        Interlocked.Increment(ref _legacyInvocationCount);
        Invoke(
            target,
            memberId,
            memberPath,
            (IReadOnlyList<object?>)new object?[] { argument0 });
    }

    public void Invoke<T0, T1>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T0 argument0,
        T1 argument1)
    {
        ValidateFastRequest(target, memberId, memberPath);
        if (typeof(T0) == typeof(UnrealByteBuffer) || typeof(T1) == typeof(UnrealByteBuffer))
        {
            InvokeWithOutputs(target, memberId, memberPath, typeof(void), [new(typeof(T0), UnrealParameterDirection.Input, argument0), new(typeof(T1), UnrealParameterDirection.Input, argument1)]);
            return;
        }
        Span<UnrealNativeValueV3> arguments = stackalloc UnrealNativeValueV3[2];
        if (UnrealNativeFixedValueCodecV3.TryEncode(argument0, out arguments[0]) &&
            UnrealNativeFixedValueCodecV3.TryEncode(argument1, out arguments[1]) &&
            TryExecuteFixedVoid(target, memberId, arguments))
        {
            return;
        }
        Span<byte> arena = stackalloc byte[CallFrameArenaByteCapacity];
        int byteCount = 0;
        if (UnrealNativeValueCodecV3.TryEncode(
                argument0,
                arena,
                ref byteCount,
                out arguments[0],
                out UnrealNativeCapabilitiesV3 capability0) &&
            UnrealNativeValueCodecV3.TryEncode(
                argument1,
                arena,
                ref byteCount,
                out arguments[1],
                out UnrealNativeCapabilitiesV3 capability1) &&
            TryExecuteCallFrameVoid(
                UnrealNativeOperationV3.Invoke,
                target,
                memberId,
                arguments,
                arena,
                byteCount,
                capability0 | capability1))
        {
            return;
        }
        Interlocked.Increment(ref _legacyInvocationCount);
        Invoke(
            target,
            memberId,
            memberPath,
            (IReadOnlyList<object?>)new object?[] { argument0, argument1 });
    }

    public void Invoke<T0, T1, T2>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T0 argument0,
        T1 argument1,
        T2 argument2)
    {
        ValidateFastRequest(target, memberId, memberPath);
        if (typeof(T0) == typeof(UnrealByteBuffer) || typeof(T1) == typeof(UnrealByteBuffer) || typeof(T2) == typeof(UnrealByteBuffer))
        {
            InvokeWithOutputs(target, memberId, memberPath, typeof(void), [new(typeof(T0), UnrealParameterDirection.Input, argument0), new(typeof(T1), UnrealParameterDirection.Input, argument1), new(typeof(T2), UnrealParameterDirection.Input, argument2)]);
            return;
        }
        Span<UnrealNativeValueV3> arguments = stackalloc UnrealNativeValueV3[3];
        if (UnrealNativeFixedValueCodecV3.TryEncode(argument0, out arguments[0]) &&
            UnrealNativeFixedValueCodecV3.TryEncode(argument1, out arguments[1]) &&
            UnrealNativeFixedValueCodecV3.TryEncode(argument2, out arguments[2]) &&
            TryExecuteFixedVoid(target, memberId, arguments))
        {
            return;
        }
        Span<byte> arena = stackalloc byte[CallFrameArenaByteCapacity];
        int byteCount = 0;
        if (TryEncodeThree(
                argument0,
                argument1,
                argument2,
                arguments,
                arena,
                ref byteCount,
                out UnrealNativeCapabilitiesV3 capabilities) &&
            TryExecuteCallFrameVoid(
                UnrealNativeOperationV3.Invoke,
                target,
                memberId,
                arguments,
                arena,
                byteCount,
                capabilities))
        {
            return;
        }
        Interlocked.Increment(ref _legacyInvocationCount);
        Invoke(
            target,
            memberId,
            memberPath,
            (IReadOnlyList<object?>)new object?[] { argument0, argument1, argument2 });
    }

    public void Invoke<T0, T1, T2, T3>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T0 argument0,
        T1 argument1,
        T2 argument2,
        T3 argument3)
    {
        ValidateFastRequest(target, memberId, memberPath);
        if (typeof(T0) == typeof(UnrealByteBuffer) || typeof(T1) == typeof(UnrealByteBuffer) || typeof(T2) == typeof(UnrealByteBuffer) || typeof(T3) == typeof(UnrealByteBuffer))
        {
            InvokeWithOutputs(target, memberId, memberPath, typeof(void), [new(typeof(T0), UnrealParameterDirection.Input, argument0), new(typeof(T1), UnrealParameterDirection.Input, argument1), new(typeof(T2), UnrealParameterDirection.Input, argument2), new(typeof(T3), UnrealParameterDirection.Input, argument3)]);
            return;
        }
        Span<UnrealNativeValueV3> arguments = stackalloc UnrealNativeValueV3[4];
        if (UnrealNativeFixedValueCodecV3.TryEncode(argument0, out arguments[0]) &&
            UnrealNativeFixedValueCodecV3.TryEncode(argument1, out arguments[1]) &&
            UnrealNativeFixedValueCodecV3.TryEncode(argument2, out arguments[2]) &&
            UnrealNativeFixedValueCodecV3.TryEncode(argument3, out arguments[3]) &&
            TryExecuteFixedVoid(target, memberId, arguments))
        {
            return;
        }
        Span<byte> arena = stackalloc byte[CallFrameArenaByteCapacity];
        int byteCount = 0;
        if (TryEncodeFour(
                argument0,
                argument1,
                argument2,
                argument3,
                arguments,
                arena,
                ref byteCount,
                out UnrealNativeCapabilitiesV3 capabilities) &&
            TryExecuteCallFrameVoid(
                UnrealNativeOperationV3.Invoke,
                target,
                memberId,
                arguments,
                arena,
                byteCount,
                capabilities))
        {
            return;
        }
        Interlocked.Increment(ref _legacyInvocationCount);
        Invoke(
            target,
            memberId,
            memberPath,
            (IReadOnlyList<object?>)new object?[]
            {
                argument0,
                argument1,
                argument2,
                argument3,
            });
    }

    public UnrealOutputInvocationResult InvokeWithOutputs(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        Type returnType,
        IReadOnlyList<UnrealOutputArgument> arguments)
        => InvokeWithOutputsCore(
            UnrealNativeOperationV3.Invoke,
            target,
            memberId,
            memberPath,
            returnType,
            arguments);

    public UnrealOutputInvocationResult InvokeParentWithOutputs(
        UnrealObjectHandle host,
        ulong memberId,
        string memberPath,
        Type returnType,
        IReadOnlyList<UnrealOutputArgument> arguments)
        => InvokeWithOutputsCore(
            UnrealNativeOperationV3.InvokeParent,
            host,
            memberId,
            memberPath,
            returnType,
            arguments);

    private UnrealOutputInvocationResult InvokeWithOutputsCore(
        UnrealNativeOperationV3 operation,
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        Type returnType,
        IReadOnlyList<UnrealOutputArgument> arguments)
    {
        ValidateFastRequest(target, memberId, memberPath);
        ArgumentNullException.ThrowIfNull(returnType);
        ArgumentNullException.ThrowIfNull(arguments);

        int inputCount = 0;
        int outputCount = 0;
        foreach (UnrealOutputArgument argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument.Type);
            switch (argument.Direction)
            {
                case UnrealParameterDirection.Input:
                    inputCount++;
                    break;
                case UnrealParameterDirection.Output:
                    outputCount++;
                    break;
                case UnrealParameterDirection.InputOutput:
                    inputCount++;
                    outputCount++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(arguments),
                        "An output invocation contains an invalid parameter direction.");
            }
        }
        int resultCount = checked(outputCount + (returnType == typeof(void) ? 0 : 1));
        if (inputCount > 64 || resultCount > 64)
        {
            throw new NotSupportedException(
                "ABI v3 supports at most 64 input and 64 result cells per invocation.");
        }

        Span<UnrealNativeValueV3> encodedArguments =
            inputCount == 0 ? Span<UnrealNativeValueV3>.Empty : stackalloc UnrealNativeValueV3[inputCount];
        Span<UnrealNativeValueV3> results =
            resultCount == 0 ? Span<UnrealNativeValueV3>.Empty : stackalloc UnrealNativeValueV3[resultCount];
        int arenaCapacity = CallFrameArenaByteCapacity;
        if (operation != UnrealNativeOperationV3.InvokeParent)
        {
            foreach (var argument in arguments)
                if (argument.Direction != UnrealParameterDirection.Output && argument.Value is UnrealByteBuffer buffer)
                    arenaCapacity = checked(arenaCapacity + buffer.Length);
            if (arenaCapacity > UnrealByteBuffer.MaximumCallBytes)
                throw new NotSupportedException("Binary invocation input arena exceeds 64 MiB.");
        }
        // Encode parent inputs once into the bounded managed-function arena.
        // The call arena is sized after encoding so a large input remains
        // valid without preallocating the output retry buffer.
        byte[]? parentInputArena = operation == UnrealNativeOperationV3.InvokeParent
            ? ArrayPool<byte>.Shared.Rent(ParentInputArenaByteCapacity)
            : null;
        IUnrealNativeCallFrameScope? invocation = null;
        try
        {
            Span<byte> arena = parentInputArena is not null
                ? parentInputArena.AsSpan(0, ParentInputArenaByteCapacity)
                : arenaCapacity == CallFrameArenaByteCapacity
                    ? stackalloc byte[CallFrameArenaByteCapacity]
                    : new byte[arenaCapacity];
            int inputByteCount = 0;
            int inputIndex = 0;
            int resultIndex = 0;
            UnrealNativeCapabilitiesV3 requiredCapabilities =
                UnrealNativeCapabilitiesV3.OutputAndReference |
                (operation == UnrealNativeOperationV3.InvokeParent
                    ? UnrealNativeCapabilitiesV3.BlueprintParentCalls |
                      UnrealNativeCapabilitiesV3.CorrelatedResults
                    : UnrealNativeCapabilitiesV3.None);

            if (returnType != typeof(void))
            {
                if (!UnrealNativeValueCodecV3.TryCreateResultDescriptor(
                        returnType,
                        out results[resultIndex++],
                        out UnrealNativeCapabilitiesV3 returnCapability))
                {
                    throw new NotSupportedException(
                        $"Generated output invocation '{memberPath}' has an unsupported return type.");
                }
                requiredCapabilities |= returnCapability;
            }

            foreach (UnrealOutputArgument argument in arguments)
            {
                if (argument.Direction != UnrealParameterDirection.Output)
                {
                    if (!UnrealNativeValueCodecV3.TryEncodeObject(
                            argument.Value,
                            argument.Type,
                            arena,
                            ref inputByteCount,
                            out encodedArguments[inputIndex++],
                            out UnrealNativeCapabilitiesV3 inputCapability))
                    {
                        throw new NotSupportedException(
                            $"Generated output invocation '{memberPath}' has an unsupported input value.");
                    }
                    requiredCapabilities |= inputCapability;
                }
                if (argument.Direction != UnrealParameterDirection.Input)
                {
                    if (!UnrealNativeValueCodecV3.TryCreateResultDescriptor(
                            argument.Type,
                            out results[resultIndex++],
                            out UnrealNativeCapabilitiesV3 outputCapability))
                    {
                        throw new NotSupportedException(
                            $"Generated output invocation '{memberPath}' has an unsupported output type.");
                    }
                    requiredCapabilities |= outputCapability;
                }
            }

            if (parentInputArena is not null)
            {
                if (inputByteCount > ParentInputArenaByteCapacity)
                    throw new NotSupportedException(
                        "Blueprint parent invocation input arena exceeds 64 KiB.");
                arenaCapacity = Math.Max(CallFrameArenaByteCapacity, inputByteCount);
                arena = parentInputArena.AsSpan(0, arenaCapacity);
            }

            if (!TryGetCallFrameTransport(
                    requiredCapabilities,
                    out IUnrealNativeCallFrameTransportV3? transport))
            {
                throw new NotSupportedException(
                    $"The native Unreal transport does not advertise the required ABI v3 capabilities: {requiredCapabilities}.");
            }
            if (operation == UnrealNativeOperationV3.InvokeParent)
            {
                if (transport is not IUnrealNativeCallFrameScopeFactory scopeFactory ||
                    (invocation = scopeFactory.BeginCallFrameInvocation()) is null)
                {
                    throw new NotSupportedException(
                        "Blueprint parent invocation requires correlated ABI v3 transport support.");
                }
            }
            else
            {
                invocation = (transport as IUnrealNativeCallFrameScopeFactory)
                    ?.BeginCallFrameInvocation();
            }
            transport = invocation ?? transport;
            long profilerStart = BeginProfilerSample();
            UnrealNativeCallOutcomeV3 outcome = transport!.ExecuteById(
                operation,
                target,
                memberId,
                encodedArguments,
                results,
                arena,
                inputByteCount);
            byte[]? retryArena = null;
            if (outcome.Status == UnrealNativeCallStatusV3.BufferTooSmall &&
                outcome.RequiredByteCapacity > arena.Length)
            {
                ValidateBinaryRetryCapacity(requiredCapabilities, outcome.RequiredByteCapacity);
                retryArena = GC.AllocateUninitializedArray<byte>(
                    outcome.RequiredByteCapacity);
                arena[..inputByteCount].CopyTo(retryArena);
                outcome = transport.ExecuteById(
                    operation,
                    target,
                    memberId,
                    encodedArguments,
                    results,
                    retryArena,
                    inputByteCount);
            }
            RequireCallFrameSuccess(outcome, resultCount, target, memberId, memberPath);
            ReadOnlySpan<byte> resultArena = retryArena is null ? arena : retryArena;

            resultIndex = 0;
            object? returnValue = null;
            if (returnType != typeof(void) &&
                !UnrealNativeValueCodecV3.TryDecodeObject(
                    returnType,
                    in results[resultIndex++],
                    resultArena,
                    outcome.ByteCount,
                    out returnValue))
            {
                throw new InvalidOperationException(
                    "The ABI v3 transport returned an incompatible generated return value.");
            }
            var outputs = new object?[outputCount];
            int outputIndex = 0;
            foreach (UnrealOutputArgument argument in arguments)
            {
                if (argument.Direction == UnrealParameterDirection.Input)
                {
                    continue;
                }
                if (!UnrealNativeValueCodecV3.TryDecodeObject(
                        argument.Type,
                        in results[resultIndex++],
                        resultArena,
                        outcome.ByteCount,
                        out outputs[outputIndex++]))
                {
                    throw new InvalidOperationException(
                        "The ABI v3 transport returned an incompatible generated output value.");
                }
            }
            Interlocked.Increment(ref _callFrameInvocationCount);
            CompleteCallFrameProfilerSample(profilerStart);
            return new UnrealOutputInvocationResult(returnValue, outputs);
        }
        finally
        {
            try
            {
                invocation?.Dispose();
            }
            finally
            {
                if (parentInputArena is not null)
                {
                    ArrayPool<byte>.Shared.Return(parentInputArena);
                }
            }
        }
    }

    private void RequireByteBuffers()
    {
        if (!TryGetCallFrameTransport(UnrealNativeCapabilitiesV3.ByteBuffers, out _))
            throw new NotSupportedException("The native transport does not advertise binary buffer support.");
    }

    private static void ValidateBinaryRetryCapacity(UnrealNativeCapabilitiesV3 capabilities, int capacity)
    {
        if ((capabilities & UnrealNativeCapabilitiesV3.ByteBuffers) != 0 && capacity > UnrealByteBuffer.MaximumCallBytes)
            throw new NotSupportedException("Binary invocation input/output arena exceeds 64 MiB.");
    }

    private bool TryExecuteFixedResult<TResult>(
        UnrealObjectHandle target,
        ulong memberId,
        ReadOnlySpan<UnrealNativeValueV3> arguments,
        out TResult result)
    {
        result = default!;
        if (_transport is not IUnrealNativeFixedInvocationTransportV3 transport ||
            !transport.SupportsFixedInvocation ||
            !UnrealNativeFixedValueCodecV3.TryCreateResultDescriptor<TResult>(
                out UnrealNativeValueV3 descriptor))
        {
            return false;
        }

        Span<UnrealNativeValueV3> results = stackalloc UnrealNativeValueV3[1];
        results[0] = descriptor;
        long profilerStart = BeginProfilerSample();
        UnrealNativeCallOutcomeV3 outcome = transport.ExecuteFixedById(
            target,
            memberId,
            arguments,
            results);
        if (IsMissingGeneratedMember(outcome))
        {
            return false;
        }
        RequireCallFrameSuccess(outcome, expectedResultCount: 1, target, memberId);
        if (!UnrealNativeFixedValueCodecV3.TryDecode(in results[0], out result))
        {
            throw new InvalidOperationException(
                "The ABI v3 fixed-value transport returned an incompatible result.");
        }
        Interlocked.Increment(ref _callFrameInvocationCount);
        CompleteDirectProfilerSample(profilerStart);
        return true;
    }

    private bool TryExecuteFixedVoid(
        UnrealObjectHandle target,
        ulong memberId,
        ReadOnlySpan<UnrealNativeValueV3> arguments)
    {
        if (_transport is not IUnrealNativeFixedInvocationTransportV3 transport ||
            !transport.SupportsFixedInvocation)
        {
            return false;
        }
        long profilerStart = BeginProfilerSample();
        UnrealNativeCallOutcomeV3 outcome = transport.ExecuteFixedById(
            target,
            memberId,
            arguments,
            Span<UnrealNativeValueV3>.Empty);
        if (IsMissingGeneratedMember(outcome))
        {
            return false;
        }
        RequireCallFrameSuccess(outcome, expectedResultCount: 0, target, memberId);
        Interlocked.Increment(ref _callFrameInvocationCount);
        CompleteDirectProfilerSample(profilerStart);
        return true;
    }

    private bool TryExecuteCallFrameResult<TResult>(
        UnrealNativeOperationV3 operation,
        UnrealObjectHandle target,
        ulong memberId,
        ReadOnlySpan<UnrealNativeValueV3> arguments,
        Span<byte> arena,
        int inputByteCount,
        UnrealNativeCapabilitiesV3 argumentCapabilities,
        out TResult result,
        string? memberPath = null)
    {
        result = default!;
        if (!UnrealNativeValueCodecV3.TryCreateResultDescriptor<TResult>(
                out UnrealNativeValueV3 descriptor,
                out UnrealNativeCapabilitiesV3 resultCapability) ||
            !TryGetCallFrameTransport(
                argumentCapabilities | resultCapability,
                out IUnrealNativeCallFrameTransportV3? transport))
        {
            return false;
        }

        Span<UnrealNativeValueV3> results = stackalloc UnrealNativeValueV3[1];
        results[0] = descriptor;
        using var invocation = operation == UnrealNativeOperationV3.Invoke
            ? (transport as IUnrealNativeCallFrameScopeFactory)?.BeginCallFrameInvocation() : null;
        transport = invocation ?? transport;
        long profilerStart = BeginProfilerSample();
        UnrealNativeCallOutcomeV3 outcome = transport!.ExecuteById(
            operation,
            target,
            memberId,
            arguments,
            results,
            arena,
            inputByteCount);
        byte[]? retryArena = null;
        if (outcome.Status == UnrealNativeCallStatusV3.BufferTooSmall &&
            outcome.RequiredByteCapacity > arena.Length)
        {
            ValidateBinaryRetryCapacity(argumentCapabilities | resultCapability, outcome.RequiredByteCapacity);
            retryArena = GC.AllocateUninitializedArray<byte>(
                outcome.RequiredByteCapacity);
            arena[..inputByteCount].CopyTo(retryArena);
            results[0] = descriptor;
            outcome = transport.ExecuteById(
                operation,
                target,
                memberId,
                arguments,
                results,
                retryArena,
                inputByteCount);
        }
        if (IsMissingGeneratedMember(outcome))
        {
            return false;
        }
        RequireCallFrameSuccess(
            outcome,
            expectedResultCount: 1,
            target,
            memberId,
            memberPath);
        ReadOnlySpan<byte> resultArena = retryArena is null ? arena : retryArena;
        if (!UnrealNativeValueCodecV3.TryDecode(
                in results[0],
                resultArena,
                outcome.ByteCount,
                out result))
        {
            throw new InvalidOperationException(
                "The ABI v3 transport returned an incompatible fixed-size result.");
        }
        Interlocked.Increment(ref _callFrameInvocationCount);
        CompleteCallFrameProfilerSample(profilerStart);
        return true;
    }

    private bool TryExecuteCallFrameVoid(
        UnrealNativeOperationV3 operation,
        UnrealObjectHandle target,
        ulong memberId,
        ReadOnlySpan<UnrealNativeValueV3> arguments,
        Span<byte> arena,
        int inputByteCount,
        UnrealNativeCapabilitiesV3 argumentCapabilities,
        string? memberPath = null)
    {
        if (!TryGetCallFrameTransport(
                argumentCapabilities,
                out IUnrealNativeCallFrameTransportV3? transport))
        {
            return false;
        }
        long profilerStart = BeginProfilerSample();
        UnrealNativeCallOutcomeV3 outcome = transport!.ExecuteById(
            operation,
            target,
            memberId,
            arguments,
            Span<UnrealNativeValueV3>.Empty,
            arena,
            inputByteCount);
        if (IsMissingGeneratedMember(outcome))
        {
            return false;
        }
        RequireCallFrameSuccess(
            outcome,
            expectedResultCount: 0,
            target,
            memberId,
            memberPath);
        Interlocked.Increment(ref _callFrameInvocationCount);
        CompleteCallFrameProfilerSample(profilerStart);
        return true;
    }

    private bool TryGetCallFrameTransport(
        UnrealNativeCapabilitiesV3 requiredCapabilities,
        out IUnrealNativeCallFrameTransportV3? transport)
    {
        transport = _transport as IUnrealNativeCallFrameTransportV3;
        return transport is not null &&
            transport.Capabilities != UnrealNativeCapabilitiesV3.None &&
            (transport.Capabilities & requiredCapabilities) == requiredCapabilities;
    }

    private bool TryGetFastTransport(
        out IUnrealNativeFastInvocationTransport? transport)
    {
        transport = _transport as IUnrealNativeFastInvocationTransport;
        return transport is not null && transport.SupportsFastInvocation;
    }

    private long BeginProfilerSample() =>
        Volatile.Read(ref _profilerEnabled) != 0 ? Stopwatch.GetTimestamp() : 0;

    private void CompleteDirectProfilerSample(long started) =>
        CompleteProfilerSample(
            started,
            ref _directThunkProfilerCount,
            ref _directThunkProfilerTicks);

    private void CompleteCallFrameProfilerSample(long started) =>
        CompleteProfilerSample(
            started,
            ref _callFrameProfilerCount,
            ref _callFrameProfilerTicks);

    private void CompleteReflectionProfilerSample(long started) =>
        CompleteProfilerSample(
            started,
            ref _reflectionProfilerCount,
            ref _reflectionProfilerTicks);

    private static void CompleteProfilerSample(
        long started,
        ref long count,
        ref long stopwatchTicks)
    {
        if (started == 0)
        {
            return;
        }
        Interlocked.Increment(ref count);
        Interlocked.Add(
            ref stopwatchTicks,
            Math.Max(0, Stopwatch.GetTimestamp() - started));
    }

    private static bool IsMissingGeneratedMember(UnrealInvocationResponse response) =>
        !response.IsSuccess && response.Error == UnrealInvocationError.InvalidMemberPath;

    private static bool IsMissingGeneratedMember(UnrealNativeCallOutcomeV3 outcome) =>
        outcome.Status == UnrealNativeCallStatusV3.InvocationFailure &&
        outcome.InvocationError == UnrealInvocationError.InvalidMemberPath;

    private static void RequireCallFrameSuccess(
        UnrealNativeCallOutcomeV3 outcome,
        int expectedResultCount,
        UnrealObjectHandle target,
        ulong? memberId,
        string? memberPath = null)
    {
        if (outcome.Status == UnrealNativeCallStatusV3.Success)
        {
            if (outcome.InvocationError != UnrealInvocationError.None ||
                outcome.HandleError != UnrealObjectHandleError.None ||
                outcome.ResultCount != expectedResultCount)
            {
                throw new InvalidOperationException(
                    "The ABI v3 transport returned an inconsistent success outcome.");
            }
            return;
        }
        if (outcome.Status == UnrealNativeCallStatusV3.InvocationFailure)
        {
            throw new UnrealInvocationException(
                outcome.InvocationError,
                outcome.HandleError,
                string.IsNullOrWhiteSpace(outcome.Message)
                    ? "The ABI v3 Unreal invocation failed."
                    : outcome.Message,
                target,
                memberId,
                memberPath);
        }
        if (outcome.Status == UnrealNativeCallStatusV3.TransportUnavailable)
        {
            throw new UnrealInvocationException(
                UnrealInvocationError.TransportUnavailable,
                UnrealObjectHandleError.None,
                string.IsNullOrWhiteSpace(outcome.Message)
                    ? "The ABI v3 Unreal transport is unavailable."
                    : outcome.Message,
                target,
                memberId,
                memberPath);
        }
        throw new InvalidOperationException(
            $"The ABI v3 transport rejected the call with status {outcome.Status}; " +
            $"required results={outcome.RequiredResultCapacity}, bytes={outcome.RequiredByteCapacity}.");
    }

    private static bool TryEncodeThree<T0, T1, T2>(
        T0 argument0,
        T1 argument1,
        T2 argument2,
        Span<UnrealNativeValueV3> arguments,
        Span<byte> arena,
        ref int byteCount,
        out UnrealNativeCapabilitiesV3 capabilities)
    {
        capabilities = UnrealNativeCapabilitiesV3.None;
        if (!UnrealNativeValueCodecV3.TryEncode(
                argument0,
                arena,
                ref byteCount,
                out arguments[0],
                out UnrealNativeCapabilitiesV3 capability0) ||
            !UnrealNativeValueCodecV3.TryEncode(
                argument1,
                arena,
                ref byteCount,
                out arguments[1],
                out UnrealNativeCapabilitiesV3 capability1) ||
            !UnrealNativeValueCodecV3.TryEncode(
                argument2,
                arena,
                ref byteCount,
                out arguments[2],
                out UnrealNativeCapabilitiesV3 capability2))
        {
            return false;
        }
        capabilities = capability0 | capability1 | capability2;
        return true;
    }

    private static bool TryEncodeFour<T0, T1, T2, T3>(
        T0 argument0,
        T1 argument1,
        T2 argument2,
        T3 argument3,
        Span<UnrealNativeValueV3> arguments,
        Span<byte> arena,
        ref int byteCount,
        out UnrealNativeCapabilitiesV3 capabilities)
    {
        capabilities = UnrealNativeCapabilitiesV3.None;
        if (!TryEncodeThree(
                argument0,
                argument1,
                argument2,
                arguments,
                arena,
                ref byteCount,
                out capabilities) ||
            !UnrealNativeValueCodecV3.TryEncode(
                argument3,
                arena,
                ref byteCount,
                out arguments[3],
                out UnrealNativeCapabilitiesV3 capability3))
        {
            return false;
        }
        capabilities |= capability3;
        return true;
    }

    private static void ValidateFastRequest(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath)
    {
        ValidateRequest(target, memberPath);
        if (memberId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(memberId),
                "A generated Unreal member ID must be non-zero.");
        }
    }

    private IUnrealNativeFastInvocationTransport RequireFastTransport(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath)
    {
        ValidateFastRequest(target, memberId, memberPath);
        if (_transport is IUnrealNativeFastInvocationTransport fastTransport &&
            fastTransport.SupportsFastInvocation)
        {
            return fastTransport;
        }
        throw new NotSupportedException(
            "The native Unreal invocation transport does not expose ABI v2 fast members.");
    }

    private static void ValidateRequest(UnrealObjectHandle target, string memberPath)
    {
        if (!target.IsWellFormed)
        {
            throw new UnrealInvocationException(
                UnrealInvocationError.ObjectHandleFailure,
                UnrealObjectHandleError.InvalidHandle,
                "The managed Unreal invocation target handle is invalid.",
                target,
                null,
                memberPath);
        }
        if (string.IsNullOrWhiteSpace(memberPath))
        {
            throw new ArgumentException(
                "A generated Unreal member path is required.",
                nameof(memberPath));
        }
    }

    private static IReadOnlyList<UnrealInvocationValue> ConvertArguments(
        IReadOnlyList<object?> arguments,
        string memberPath)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var converted = new UnrealInvocationValue[arguments.Count];
        for (int index = 0; index < arguments.Count; index++)
        {
            converted[index] = ConvertToNative(arguments[index], memberPath);
        }
        return converted;
    }

    private static UnrealInvocationValue ConvertToNative<T>(
        T value,
        string memberPath,
        bool useSoftKinds = false)
    {
        if (IsContainerType(typeof(T)))
        {
            if (TryEncodeContainerPayload(
                    value,
                    typeof(T),
                    memberPath,
                    out UnrealInvocationValue container))
            {
                return container;
            }
            throw UnsupportedValue(memberPath, typeof(T));
        }
        return value switch
        {
            bool booleanValue => UnrealInvocationValue.FromBoolean(booleanValue),
            sbyte int8Value => UnrealInvocationValue.FromInt8(int8Value),
            byte uint8Value => UnrealInvocationValue.FromUInt8(uint8Value),
            short int16Value => UnrealInvocationValue.FromInt16(int16Value),
            ushort uint16Value => UnrealInvocationValue.FromUInt16(uint16Value),
            int int32Value => UnrealInvocationValue.FromInt32(int32Value),
            uint uint32Value => UnrealInvocationValue.FromUInt32(uint32Value),
            long int64Value => UnrealInvocationValue.FromInt64(int64Value),
            ulong uint64Value => UnrealInvocationValue.FromUInt64(uint64Value),
            float float32Value => UnrealInvocationValue.FromFloat32(float32Value),
            double float64Value => UnrealInvocationValue.FromFloat64(float64Value),
            string stringValue => UnrealInvocationValue.FromString(stringValue),
            UnrealSoftObjectHandle softObject when useSoftKinds =>
                UnrealInvocationValue.FromSoftObject(softObject),
            UnrealSoftClassHandle softClass when useSoftKinds =>
                UnrealInvocationValue.FromSoftClass(softClass),
            UnrealSoftObjectHandle softObject =>
                UnrealInvocationValue.FromString(softObject.AssetPath ?? string.Empty),
            UnrealSoftClassHandle softClass =>
                UnrealInvocationValue.FromString(softClass.AssetPath ?? string.Empty),
            UnrealName nameValue => UnrealInvocationValue.FromName(nameValue),
            UnrealText textValue => UnrealInvocationValue.FromText(textValue),
            UnrealObjectHandle objectHandle => UnrealInvocationValue.FromObjectHandle(objectHandle),
            UnrealClassHandle classHandle =>
                UnrealInvocationValue.FromObjectHandle(classHandle.ObjectHandle, isClass: true),
            UnrealEnumValue enumValue when !string.IsNullOrWhiteSpace(enumValue.TypePath) =>
                UnrealInvocationValue.FromEnum(enumValue),
            IUnrealEnumValue enumValue when TryGetEnumPath(enumValue.GetType(), out string enumPath) =>
                UnrealInvocationValue.FromEnum(new UnrealEnumValue(enumPath, enumValue.Value)),
            UnrealVector vectorValue => UnrealInvocationValue.FromVector(vectorValue),
            UnrealRotator rotatorValue => UnrealInvocationValue.FromRotator(rotatorValue),
            UnrealTransform transformValue => UnrealInvocationValue.FromTransform(transformValue),
            _ => throw UnsupportedValue(memberPath, typeof(T)),
        };
    }

    private static T ConvertFromNative<T>(
        UnrealInvocationValue value,
        string memberPath,
        bool useSoftKinds = false)
    {
        if (value.Kind is UnrealInvocationValueKind.Array or UnrealInvocationValueKind.Set or UnrealInvocationValueKind.Map &&
            TryDecodeContainerPayload(value, typeof(T), out object? decodedContainer))
        {
            return (T)decodedContainer!;
        }
        if (value.Kind == UnrealInvocationValueKind.Enum &&
            TryGetEnumPath(typeof(T), out string expectedEnumPath) &&
            value.EnumTypeId == UnrealMemberId.FromPath(expectedEnumPath) &&
            (value.StringValue is null || string.Equals(value.StringValue, expectedEnumPath, StringComparison.Ordinal)) &&
            TryCreateTypedEnum(
                typeof(T),
                expectedEnumPath,
                value.SignedIntegerValue,
                out object? typedEnum))
        {
            return (T)typedEnum!;
        }
        object? converted = value.Kind switch
        {
            UnrealInvocationValueKind.Boolean when typeof(T) == typeof(bool) =>
                value.BooleanValue,
            UnrealInvocationValueKind.Int32 when typeof(T) == typeof(int) =>
                value.Int32Value,
            UnrealInvocationValueKind.Int8 when typeof(T) == typeof(sbyte) =>
                (sbyte)value.SignedIntegerValue,
            UnrealInvocationValueKind.UInt8 when typeof(T) == typeof(byte) =>
                (byte)value.UnsignedIntegerValue,
            UnrealInvocationValueKind.Int16 when typeof(T) == typeof(short) =>
                (short)value.SignedIntegerValue,
            UnrealInvocationValueKind.UInt16 when typeof(T) == typeof(ushort) =>
                (ushort)value.UnsignedIntegerValue,
            UnrealInvocationValueKind.UInt32 when typeof(T) == typeof(uint) =>
                (uint)value.UnsignedIntegerValue,
            UnrealInvocationValueKind.Int64 when typeof(T) == typeof(long) =>
                value.SignedIntegerValue,
            UnrealInvocationValueKind.UInt64 when typeof(T) == typeof(ulong) =>
                value.UnsignedIntegerValue,
            UnrealInvocationValueKind.Float32 when typeof(T) == typeof(float) =>
                (float)value.FloatingPointValue,
            UnrealInvocationValueKind.Float64 when typeof(T) == typeof(double) =>
                value.FloatingPointValue,
            UnrealInvocationValueKind.String when typeof(T) == typeof(string) =>
                value.StringValue,
            UnrealInvocationValueKind.String when !useSoftKinds && typeof(T) == typeof(UnrealSoftObjectHandle) =>
                new UnrealSoftObjectHandle(value.StringValue ?? string.Empty),
            UnrealInvocationValueKind.String when !useSoftKinds && typeof(T) == typeof(UnrealSoftClassHandle) =>
                new UnrealSoftClassHandle(value.StringValue ?? string.Empty),
            UnrealInvocationValueKind.SoftObject when typeof(T) == typeof(UnrealSoftObjectHandle) =>
                new UnrealSoftObjectHandle(value.StringValue ?? string.Empty),
            UnrealInvocationValueKind.SoftClass when typeof(T) == typeof(UnrealSoftClassHandle) =>
                new UnrealSoftClassHandle(value.StringValue ?? string.Empty),
            UnrealInvocationValueKind.Name when typeof(T) == typeof(UnrealName) =>
                new UnrealName(value.StringValue ?? string.Empty),
            UnrealInvocationValueKind.Text when typeof(T) == typeof(UnrealText) =>
                new UnrealText(value.StringValue ?? string.Empty),
            UnrealInvocationValueKind.Object or UnrealInvocationValueKind.Class
                when typeof(T) == typeof(UnrealObjectHandle) => value.ObjectHandle,
            UnrealInvocationValueKind.Class
                when typeof(T) == typeof(UnrealClassHandle) =>
                new UnrealClassHandle(value.ObjectHandle.Value),
            UnrealInvocationValueKind.Enum
                when typeof(T) == typeof(UnrealEnumValue) &&
                     !string.IsNullOrWhiteSpace(value.StringValue) =>
                new UnrealEnumValue(value.StringValue ?? string.Empty, value.SignedIntegerValue),
            UnrealInvocationValueKind.Vector when typeof(T) == typeof(UnrealVector) =>
                value.VectorValue,
            UnrealInvocationValueKind.Rotator when typeof(T) == typeof(UnrealRotator) =>
                value.RotatorValue,
            UnrealInvocationValueKind.Transform when typeof(T) == typeof(UnrealTransform) =>
                value.TransformValue,
            _ => null,
        };
        if (converted is null)
        {
            throw new UnrealInvocationException(
                UnrealInvocationError.ValueTypeMismatch,
                UnrealObjectHandleError.None,
                $"Native member '{memberPath}' returned {value.Kind}, not {typeof(T).FullName}.");
        }
        return (T)converted;
    }

    private static bool IsContainerType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() is
            { } definition && (definition == typeof(IReadOnlyList<>) ||
                definition == typeof(IReadOnlySet<>) ||
                definition == typeof(IReadOnlyDictionary<,>));

    private static bool TryEncodeContainerPayload<T>(
        T value,
        Type declaredType,
        string memberPath,
        out UnrealInvocationValue converted)
    {
        converted = default;
        int rootSize = Marshal.SizeOf<UnrealNativeValueV3>();
        int maximumArena = UnrealByteBuffer.MaximumCallBytes - rootSize;
        if (maximumArena <= 0)
            return false;
        if (!UnrealNativeValueCodecV3.TryValidateContainerInput(value, declaredType))
            return false;
        int capacity = Math.Min(CallFrameArenaByteCapacity, maximumArena);
        while (capacity > 0)
        {
            byte[] arena = new byte[capacity];
            int byteCount = 0;
            if (UnrealNativeValueCodecV3.TryEncodeObject(
                    value,
                    declaredType,
                    arena,
                    ref byteCount,
                    out UnrealNativeValueV3 encoded,
                    out _)
                && encoded.Kind is UnrealNativeValueKindV3.Array or
                    UnrealNativeValueKindV3.Set or UnrealNativeValueKindV3.Map)
            {
                if (byteCount < 0 || byteCount > maximumArena)
                    return false;
                byte[] payload = new byte[checked(rootSize + byteCount)];
                MemoryMarshal.Write(payload.AsSpan(), in encoded);
                arena.AsSpan(0, byteCount).CopyTo(payload.AsSpan(rootSize));
                UnrealInvocationValueKind kind = encoded.Kind switch
                {
                    UnrealNativeValueKindV3.Array => UnrealInvocationValueKind.Array,
                    UnrealNativeValueKindV3.Set => UnrealInvocationValueKind.Set,
                    _ => UnrealInvocationValueKind.Map,
                };
                converted = UnrealInvocationValue.FromContainerPayload(kind, payload);
                return true;
            }
            if (capacity == maximumArena)
                break;
            int nextCapacity = Math.Min(maximumArena, checked(capacity * 2));
            if (nextCapacity == capacity)
                break;
            capacity = nextCapacity;
        }
        return false;
    }

    private static bool TryDecodeContainerPayload(
        UnrealInvocationValue value,
        Type declaredType,
        out object? converted)
    {
        converted = null;
        if (!IsContainerType(declaredType) || value.ContainerPayload is null)
            return false;
        int rootSize = Marshal.SizeOf<UnrealNativeValueV3>();
        ReadOnlyMemory<byte> payload = value.ContainerPayload.Value;
        if (payload.Length < rootSize || payload.Length > UnrealByteBuffer.MaximumCallBytes)
            return false;
        UnrealNativeValueV3 root = MemoryMarshal.Read<UnrealNativeValueV3>(payload.Span);
        UnrealNativeValueKindV3 expectedKind = value.Kind switch
        {
            UnrealInvocationValueKind.Array => UnrealNativeValueKindV3.Array,
            UnrealInvocationValueKind.Set => UnrealNativeValueKindV3.Set,
            UnrealInvocationValueKind.Map => UnrealNativeValueKindV3.Map,
            _ => UnrealNativeValueKindV3.Void,
        };
        if (root.Kind != expectedKind || root.Flags != UnrealNativeValueFlagsV3.ArenaBacked)
            return false;
        return UnrealNativeValueCodecV3.TryDecodeObject(
            declaredType,
            in root,
            payload.Span[rootSize..],
            payload.Length - rootSize,
            out converted);
    }

    private static bool TryGetEnumPath(Type type, out string path)
    {
        UnrealEnumAttribute? attribute = type.GetCustomAttribute<UnrealEnumAttribute>();
        path = attribute?.Path ?? string.Empty;
        return !string.IsNullOrWhiteSpace(path) &&
            typeof(IUnrealEnumValue).IsAssignableFrom(type);
    }

    private static bool TryCreateTypedEnum(
        Type type,
        string typePath,
        long value,
        out object? result)
    {
        result = null;
        if (!TryGetEnumPath(type, out string expectedPath) ||
            !string.Equals(typePath, expectedPath, StringComparison.Ordinal))
        {
            return false;
        }
        ConstructorInfo? constructor = type.GetConstructor([typeof(long)]);
        if (constructor is null)
        {
            return false;
        }
        result = constructor.Invoke([value]);
        return true;
    }

    private static UnrealInvocationValue RequireSuccess(
        UnrealInvocationResponse response,
        UnrealObjectHandle target,
        ulong? memberId,
        string memberPath)
    {
        if (response.IsSuccess)
        {
            if (response.Error != UnrealInvocationError.None ||
                response.HandleError != UnrealObjectHandleError.None)
            {
                throw new InvalidOperationException(
                    "The native invocation transport returned an inconsistent success response.");
            }
            return response.Value;
        }
        if (response.Error == UnrealInvocationError.None)
        {
            throw new InvalidOperationException(
                "The native invocation transport returned a failure without an error category.");
        }
        throw new UnrealInvocationException(
            response.Error,
            response.HandleError,
            response.Message,
            target,
            memberId,
            memberPath);
    }

    private static void RequireVoid(
        UnrealInvocationValue value,
        string memberPath)
    {
        if (value.Kind != UnrealInvocationValueKind.Void)
        {
            throw new UnrealInvocationException(
                UnrealInvocationError.ValueTypeMismatch,
                UnrealObjectHandleError.None,
                $"Native void member '{memberPath}' returned {value.Kind}.");
        }
    }

    private static UnrealInvocationException UnsupportedValue(
        string memberPath,
        Type managedType) =>
        new(
            UnrealInvocationError.UnsupportedValueType,
            UnrealObjectHandleError.None,
            $"Managed value type '{managedType.FullName}' is not supported by native member '{memberPath}'.");
}
