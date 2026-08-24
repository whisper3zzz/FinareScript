namespace UnifiedScript.Unreal;

public enum UnrealInvocationValueKind : byte
{
    Void = 0,
    Boolean = 1,
    Int32 = 2,
    String = 3,
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
        bool booleanValue,
        int int32Value,
        string? stringValue)
    {
        Kind = kind;
        BooleanValue = booleanValue;
        Int32Value = int32Value;
        StringValue = stringValue;
    }

    public UnrealInvocationValueKind Kind { get; }

    public bool BooleanValue { get; }

    public int Int32Value { get; }

    public string? StringValue { get; }

    public static UnrealInvocationValue Void => default;

    public static UnrealInvocationValue FromBoolean(bool value) =>
        new(UnrealInvocationValueKind.Boolean, value, default, default);

    public static UnrealInvocationValue FromInt32(int value) =>
        new(UnrealInvocationValueKind.Int32, default, value, default);

    public static UnrealInvocationValue FromString(string value) =>
        new(
            UnrealInvocationValueKind.String,
            default,
            default,
            value ?? throw new ArgumentNullException(nameof(value)));
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
        string message)
        : base(message)
    {
        Error = error;
        HandleError = handleError;
    }

    public UnrealInvocationError Error { get; }

    public UnrealObjectHandleError HandleError { get; }
}

/// <summary>
/// Converts generated-wrapper CLR values to the owned native invocation
/// contract. A world/runtime host supplies the concrete process transport.
/// </summary>
public sealed class NativeUnrealInvocationBridge
    : IUnrealInvocationBridge, IUnrealFastInvocationBridge
{
    private readonly IUnrealNativeInvocationTransport _transport;

    public NativeUnrealInvocationBridge(IUnrealNativeInvocationTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public bool SupportsFastInvocation =>
        _transport is IUnrealNativeFastInvocationTransport fastTransport &&
        fastTransport.SupportsFastInvocation;

    public T ReadProperty<T>(UnrealObjectHandle target, string propertyPath)
    {
        ValidateRequest(target, propertyPath);
        UnrealInvocationValue value = RequireSuccess(
            _transport.ReadProperty(target, propertyPath));
        return ConvertFromNative<T>(value, propertyPath);
    }

    public void WriteProperty<T>(
        UnrealObjectHandle target,
        string propertyPath,
        T value)
    {
        ValidateRequest(target, propertyPath);
        UnrealInvocationValue nativeValue = ConvertToNative(value, propertyPath);
        UnrealInvocationValue result = RequireSuccess(
            _transport.WriteProperty(target, propertyPath, nativeValue));
        RequireVoid(result, propertyPath);
    }

    public T Invoke<T>(
        UnrealObjectHandle target,
        string functionPath,
        IReadOnlyList<object?> arguments)
    {
        ValidateRequest(target, functionPath);
        IReadOnlyList<UnrealInvocationValue> nativeArguments = ConvertArguments(
            arguments,
            functionPath);
        UnrealInvocationValue result = RequireSuccess(
            _transport.Invoke(target, functionPath, nativeArguments));
        return ConvertFromNative<T>(result, functionPath);
    }

    public void Invoke(
        UnrealObjectHandle target,
        string functionPath,
        IReadOnlyList<object?> arguments)
    {
        ValidateRequest(target, functionPath);
        IReadOnlyList<UnrealInvocationValue> nativeArguments = ConvertArguments(
            arguments,
            functionPath);
        UnrealInvocationValue result = RequireSuccess(
            _transport.Invoke(target, functionPath, nativeArguments));
        RequireVoid(result, functionPath);
    }

    public T ReadProperty<T>(
        UnrealObjectHandle target,
        ulong memberId,
        string propertyPath)
    {
        IUnrealNativeFastInvocationTransport transport = RequireFastTransport(
            target,
            memberId,
            propertyPath);
        UnrealInvocationValue value = RequireSuccess(
            transport.ReadPropertyById(target, memberId));
        return ConvertFromNative<T>(value, propertyPath);
    }

    public void WriteProperty<T>(
        UnrealObjectHandle target,
        ulong memberId,
        string propertyPath,
        T value)
    {
        IUnrealNativeFastInvocationTransport transport = RequireFastTransport(
            target,
            memberId,
            propertyPath);
        UnrealInvocationValue nativeValue = ConvertToNative(value, propertyPath);
        UnrealInvocationValue result = RequireSuccess(
            transport.WritePropertyById(target, memberId, nativeValue));
        RequireVoid(result, propertyPath);
    }

    public T Invoke<T>(
        UnrealObjectHandle target,
        ulong memberId,
        string functionPath,
        IReadOnlyList<object?> arguments)
    {
        IUnrealNativeFastInvocationTransport transport = RequireFastTransport(
            target,
            memberId,
            functionPath);
        IReadOnlyList<UnrealInvocationValue> nativeArguments = ConvertArguments(
            arguments,
            functionPath);
        UnrealInvocationValue result = RequireSuccess(
            transport.InvokeById(target, memberId, nativeArguments));
        return ConvertFromNative<T>(result, functionPath);
    }

    public void Invoke(
        UnrealObjectHandle target,
        ulong memberId,
        string functionPath,
        IReadOnlyList<object?> arguments)
    {
        IUnrealNativeFastInvocationTransport transport = RequireFastTransport(
            target,
            memberId,
            functionPath);
        IReadOnlyList<UnrealInvocationValue> nativeArguments = ConvertArguments(
            arguments,
            functionPath);
        UnrealInvocationValue result = RequireSuccess(
            transport.InvokeById(target, memberId, nativeArguments));
        RequireVoid(result, functionPath);
    }

    private IUnrealNativeFastInvocationTransport RequireFastTransport(
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
                "The managed Unreal invocation target handle is invalid.");
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

    private static UnrealInvocationValue ConvertToNative<T>(T value, string memberPath) =>
        value switch
        {
            bool booleanValue => UnrealInvocationValue.FromBoolean(booleanValue),
            int int32Value => UnrealInvocationValue.FromInt32(int32Value),
            string stringValue => UnrealInvocationValue.FromString(stringValue),
            _ => throw UnsupportedValue(memberPath, typeof(T)),
        };

    private static T ConvertFromNative<T>(
        UnrealInvocationValue value,
        string memberPath)
    {
        object? converted = value.Kind switch
        {
            UnrealInvocationValueKind.Boolean when typeof(T) == typeof(bool) =>
                value.BooleanValue,
            UnrealInvocationValueKind.Int32 when typeof(T) == typeof(int) =>
                value.Int32Value,
            UnrealInvocationValueKind.String when typeof(T) == typeof(string) =>
                value.StringValue,
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

    private static UnrealInvocationValue RequireSuccess(UnrealInvocationResponse response)
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
            response.Message);
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
