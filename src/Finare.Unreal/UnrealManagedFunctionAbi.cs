using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Finare.Unreal;

internal enum UnrealManagedFunctionValueKindV1 : uint
{
    Void = 0,
    Boolean = 1,
    Byte = 2,
    Int32 = 3,
    Int64 = 4,
    Single = 5,
    Double = 6,
}

/// <summary>
/// Fixed 16-byte native-to-managed function value. The first dispatch tranche
/// deliberately covers scalar values without JSON, reflection invocation, or
/// per-call boxing. Strings, handles, and structures extend this ABI in later
/// reviewed revisions instead of changing v1 in place.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
internal readonly struct UnrealManagedFunctionValueV1
{
    public UnrealManagedFunctionValueV1(
        UnrealManagedFunctionValueKindV1 kind,
        ulong payload)
    {
        Kind = kind;
        Reserved = 0;
        Payload = payload;
    }

    public UnrealManagedFunctionValueKindV1 Kind { get; }

    public uint Reserved { get; }

    public ulong Payload { get; }

    public static UnrealManagedFunctionValueV1 Void => default;

    public static UnrealManagedFunctionValueV1 FromBoolean(bool value) =>
        new(UnrealManagedFunctionValueKindV1.Boolean, value ? 1UL : 0UL);

    public static UnrealManagedFunctionValueV1 FromByte(byte value) =>
        new(UnrealManagedFunctionValueKindV1.Byte, value);

    public static UnrealManagedFunctionValueV1 FromInt32(int value) =>
        new(UnrealManagedFunctionValueKindV1.Int32, unchecked((ulong)(long)value));

    public static UnrealManagedFunctionValueV1 FromInt64(long value) =>
        new(UnrealManagedFunctionValueKindV1.Int64, unchecked((ulong)value));

    public static UnrealManagedFunctionValueV1 FromSingle(float value) =>
        new(UnrealManagedFunctionValueKindV1.Single, BitConverter.SingleToUInt32Bits(value));

    public static UnrealManagedFunctionValueV1 FromDouble(double value) =>
        new(UnrealManagedFunctionValueKindV1.Double, BitConverter.DoubleToUInt64Bits(value));
}

internal sealed class UnrealManagedFunctionDispatcher
{
    public const int MaximumArgumentCount = 4;

    private readonly IReadOnlyDictionary<string, FunctionContract> _functions;

    private UnrealManagedFunctionDispatcher(
        IReadOnlyDictionary<string, FunctionContract> functions)
    {
        _functions = functions;
    }

    public static UnrealManagedFunctionDispatcher Create(Type scriptType)
    {
        ArgumentNullException.ThrowIfNull(scriptType);
        var functions = new Dictionary<string, FunctionContract>(StringComparer.Ordinal);
        foreach (MethodInfo method in scriptType.GetMethods(
                     BindingFlags.Instance |
                     BindingFlags.Public |
                     BindingFlags.DeclaredOnly))
        {
            if (!IsReviewedBlueprintCallable(method) ||
                method.GetCustomAttributes<UnrealEventAttribute>(inherit: false).Any())
            {
                continue;
            }
            if (!FunctionContract.TryCreate(method, out FunctionContract? contract) ||
                contract is null)
            {
                continue;
            }
            if (!functions.TryAdd(method.Name, contract))
            {
                throw new InvalidOperationException(
                    $"Blueprint-callable managed methods cannot overload '{scriptType.FullName}.{method.Name}'. " +
                    "Use distinct method names so one stable Unreal UFunction owns each call contract.");
            }
        }
        return new UnrealManagedFunctionDispatcher(functions);
    }

    private static bool IsReviewedBlueprintCallable(MethodInfo method) =>
        method.GetCustomAttribute<global::Finare.VisualAttribute>(inherit: false) is not null ||
        method.GetCustomAttribute<UnrealBlueprintEventAttribute>(inherit: false) is not null ||
        method.GetCustomAttribute<UnrealBlueprintOverrideAttribute>(inherit: false) is not null ||
        method.GetCustomAttribute<UnrealRpcAttribute>(inherit: false) is not null ||
        method.GetCustomAttribute<UnrealRepNotifyAttribute>(inherit: false) is not null ||
        method.GetCustomAttributes<UnrealInputActionAttribute>(inherit: false).Any() ||
        method.GetCustomAttributes<UnrealInterfaceFunctionAttribute>(inherit: false).Any();

    public unsafe UnrealManagedFunctionValueV1 Invoke(
        object target,
        string functionName,
        nint arguments,
        int argumentCount)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        if (!_functions.TryGetValue(functionName, out FunctionContract? contract))
        {
            throw new MissingMethodException(
                $"Managed Script Class '{target.GetType().FullName}' has no reviewed Blueprint-callable function '{functionName}'.");
        }
        if (argumentCount != contract.ArgumentKinds.Count ||
            argumentCount is < 0 or > MaximumArgumentCount ||
            (argumentCount > 0 && arguments == 0))
        {
            throw new ArgumentException(
                $"Managed function '{target.GetType().FullName}.{functionName}' expects " +
                $"{contract.ArgumentKinds.Count} arguments, received {argumentCount}.",
                nameof(argumentCount));
        }

        UnrealManagedFunctionValueV1* values =
            (UnrealManagedFunctionValueV1*)arguments;
        for (int index = 0; index < argumentCount; ++index)
        {
            UnrealManagedFunctionValueV1 value = values[index];
            UnrealManagedFunctionValueKindV1 expected = contract.ArgumentKinds[index];
            if (value.Reserved != 0 || value.Kind != expected ||
                (expected == UnrealManagedFunctionValueKindV1.Boolean && value.Payload > 1) ||
                (expected == UnrealManagedFunctionValueKindV1.Byte && value.Payload > byte.MaxValue))
            {
                throw new ArgumentException(
                    $"Managed function '{target.GetType().FullName}.{functionName}' argument {index} " +
                    $"requires {expected}, received {value.Kind}.",
                    nameof(arguments));
            }
        }

        if (target is UnrealObjectScript objectScript)
        {
            objectScript.BeginGeneratedFunctionCall();
            try
            {
                return contract.Invoke(target, arguments);
            }
            finally
            {
                objectScript.EndGeneratedFunctionCall();
            }
        }
        return contract.Invoke(target, arguments);
    }

    private delegate UnrealManagedFunctionValueV1 InvokeFunction(object target, nint arguments);

    private sealed record FunctionContract(
        IReadOnlyList<UnrealManagedFunctionValueKindV1> ArgumentKinds,
        InvokeFunction Invoke)
    {
        public static bool TryCreate(
            MethodInfo method,
            out FunctionContract? contract)
        {
            contract = null;
            ParameterInfo[] parameters = method.GetParameters();
            if (method.IsStatic || method.ContainsGenericParameters ||
                parameters.Length > MaximumArgumentCount ||
                !TryResolveKind(method.ReturnType, allowVoid: true, out _))
            {
                return false;
            }

            var kinds = new UnrealManagedFunctionValueKindV1[parameters.Length];
            var target = Expression.Parameter(typeof(object), "target");
            var arguments = Expression.Parameter(typeof(nint), "arguments");
            var callArguments = new Expression[parameters.Length];
            for (int index = 0; index < parameters.Length; ++index)
            {
                Type parameterType = parameters[index].ParameterType;
                if (parameterType.IsByRef ||
                    !TryResolveKind(parameterType, allowVoid: false, out kinds[index]) ||
                    ReadMethod(parameterType) is not MethodInfo reader)
                {
                    return false;
                }
                callArguments[index] = Expression.Call(
                    reader,
                    arguments,
                    Expression.Constant(index));
            }

            MethodCallExpression call = Expression.Call(
                Expression.Convert(
                    target,
                    method.DeclaringType ?? throw new InvalidOperationException(
                        $"Managed function '{method.Name}' has no declaring type.")),
                method,
                callArguments);
            Expression body;
            if (method.ReturnType == typeof(void))
            {
                body = Expression.Block(
                    call,
                    Expression.Default(typeof(UnrealManagedFunctionValueV1)));
            }
            else if (PackMethod(method.ReturnType) is MethodInfo packer)
            {
                body = Expression.Call(packer, call);
            }
            else
            {
                return false;
            }

            InvokeFunction invoker = Expression.Lambda<InvokeFunction>(
                body,
                target,
                arguments).Compile();
            contract = new FunctionContract(kinds, invoker);
            return true;
        }
    }

    private static bool TryResolveKind(
        Type type,
        bool allowVoid,
        out UnrealManagedFunctionValueKindV1 kind)
    {
        kind = type == typeof(void) && allowVoid
            ? UnrealManagedFunctionValueKindV1.Void
            : type == typeof(bool)
                ? UnrealManagedFunctionValueKindV1.Boolean
                : type == typeof(byte)
                    ? UnrealManagedFunctionValueKindV1.Byte
                    : type == typeof(int)
                        ? UnrealManagedFunctionValueKindV1.Int32
                        : type == typeof(long)
                            ? UnrealManagedFunctionValueKindV1.Int64
                            : type == typeof(float)
                                ? UnrealManagedFunctionValueKindV1.Single
                                : type == typeof(double)
                                    ? UnrealManagedFunctionValueKindV1.Double
                                    : (UnrealManagedFunctionValueKindV1)uint.MaxValue;
        return kind != (UnrealManagedFunctionValueKindV1)uint.MaxValue;
    }

    private static MethodInfo? ReadMethod(Type type) => type == typeof(bool)
        ? GetHelper(nameof(ReadBoolean))
        : type == typeof(byte)
            ? GetHelper(nameof(ReadByte))
            : type == typeof(int)
                ? GetHelper(nameof(ReadInt32))
                : type == typeof(long)
                    ? GetHelper(nameof(ReadInt64))
                    : type == typeof(float)
                        ? GetHelper(nameof(ReadSingle))
                        : type == typeof(double)
                            ? GetHelper(nameof(ReadDouble))
                            : null;

    private static MethodInfo? PackMethod(Type type) => type == typeof(bool)
        ? GetHelper(nameof(PackBoolean))
        : type == typeof(byte)
            ? GetHelper(nameof(PackByte))
            : type == typeof(int)
                ? GetHelper(nameof(PackInt32))
                : type == typeof(long)
                    ? GetHelper(nameof(PackInt64))
                    : type == typeof(float)
                        ? GetHelper(nameof(PackSingle))
                        : type == typeof(double)
                            ? GetHelper(nameof(PackDouble))
                            : null;

    private static MethodInfo GetHelper(string name) =>
        typeof(UnrealManagedFunctionDispatcher).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic) ??
        throw new MissingMethodException(typeof(UnrealManagedFunctionDispatcher).FullName, name);

    private static bool ReadBoolean(nint arguments, int index) =>
        ReadValue(arguments, index).Payload != 0;

    private static byte ReadByte(nint arguments, int index) =>
        (byte)ReadValue(arguments, index).Payload;

    private static int ReadInt32(nint arguments, int index) =>
        unchecked((int)ReadValue(arguments, index).Payload);

    private static long ReadInt64(nint arguments, int index) =>
        unchecked((long)ReadValue(arguments, index).Payload);

    private static float ReadSingle(nint arguments, int index) =>
        BitConverter.UInt32BitsToSingle((uint)ReadValue(arguments, index).Payload);

    private static double ReadDouble(nint arguments, int index) =>
        BitConverter.UInt64BitsToDouble(ReadValue(arguments, index).Payload);

    private static unsafe UnrealManagedFunctionValueV1 ReadValue(
        nint arguments,
        int index) => ((UnrealManagedFunctionValueV1*)arguments)[index];

    private static UnrealManagedFunctionValueV1 PackBoolean(bool value) =>
        UnrealManagedFunctionValueV1.FromBoolean(value);

    private static UnrealManagedFunctionValueV1 PackByte(byte value) =>
        UnrealManagedFunctionValueV1.FromByte(value);

    private static UnrealManagedFunctionValueV1 PackInt32(int value) =>
        UnrealManagedFunctionValueV1.FromInt32(value);

    private static UnrealManagedFunctionValueV1 PackInt64(long value) =>
        UnrealManagedFunctionValueV1.FromInt64(value);

    private static UnrealManagedFunctionValueV1 PackSingle(float value) =>
        UnrealManagedFunctionValueV1.FromSingle(value);

    private static UnrealManagedFunctionValueV1 PackDouble(double value) =>
        UnrealManagedFunctionValueV1.FromDouble(value);
}

/// <summary>
/// Version-two Blueprint call dispatcher. It reuses the published ABI v3
/// value cell and borrowed byte arena so strings, handles, vectors, and later
/// generated structs share one wire contract with reflected Unreal calls.
/// Every delegate is compiled once per collectible generation; no MethodInfo
/// invocation occurs on the call path.
/// </summary>
internal sealed class UnrealManagedFunctionDispatcherV2
{
    public const int MaximumArgumentCount = 4;

    private readonly string _typeName;
    private readonly bool _staticOnly;
    private readonly IReadOnlyDictionary<string, FunctionContract> _functions;
    private readonly IReadOnlyDictionary<string, UnrealManagedFunctionSignature> _signatures;

    private UnrealManagedFunctionDispatcherV2(
        Type scriptType,
        bool staticOnly,
        IReadOnlyDictionary<string, FunctionContract> functions,
        IReadOnlyDictionary<string, UnrealManagedFunctionSignature> signatures,
        IReadOnlyDictionary<string, UnrealBlueprintOverrideRoute> blueprintOverrides)
    {
        _typeName = scriptType.FullName ?? scriptType.Name;
        _staticOnly = staticOnly;
        _functions = functions;
        _signatures = signatures;
        BlueprintOverrides = blueprintOverrides;
    }

    internal IReadOnlyDictionary<string, UnrealManagedFunctionSignature> Signatures => _signatures;

    internal IReadOnlyDictionary<string, UnrealBlueprintOverrideRoute> BlueprintOverrides { get; }

    public static UnrealManagedFunctionDispatcherV2 Create(
        Type scriptType,
        bool staticOnly)
    {
        ArgumentNullException.ThrowIfNull(scriptType);
        IReadOnlyDictionary<string, UnrealBlueprintOverrideRoute> blueprintOverrides =
            UnrealBlueprintOverrideContract.Create(scriptType);
        var functions = new Dictionary<string, FunctionContract>(StringComparer.Ordinal);
        var signatures = new Dictionary<string, UnrealManagedFunctionSignature>(StringComparer.Ordinal);
        BindingFlags flags = BindingFlags.Public | BindingFlags.FlattenHierarchy |
            (staticOnly ? BindingFlags.Static : BindingFlags.Instance);
        foreach (MethodInfo method in scriptType.GetMethods(flags))
        {
            if (method.IsStatic != staticOnly ||
                !IsReviewedBlueprintCallable(method) ||
                method.GetCustomAttributes<UnrealEventAttribute>(inherit: false).Any())
            {
                continue;
            }
            if (!FunctionContract.TryCreate(
                    method,
                    out FunctionContract? contract,
                    out UnrealManagedFunctionSignature? signature) ||
                contract is null || signature is null)
            {
                if (method.GetCustomAttribute<UnrealBlueprintOverrideAttribute>(inherit: false) is not null)
                {
                    throw new InvalidOperationException(
                        $"Blueprint override method '{method.DeclaringType?.FullName}.{method.Name}' " +
                        "does not satisfy the V2 managed function contract; its return or " +
                        "parameter shape is unsupported or cannot be bound.");
                }
                continue;
            }
            if (!functions.TryAdd(method.Name, contract))
            {
                throw new InvalidOperationException(
                    $"Blueprint-callable managed methods cannot overload '{scriptType.FullName}.{method.Name}'. " +
                    "Use distinct method names so one stable Unreal UFunction owns each call contract.");
            }
            signatures.Add(method.Name, signature);
        }
        return new UnrealManagedFunctionDispatcherV2(
            scriptType,
            staticOnly,
            functions,
            signatures,
            blueprintOverrides);
    }

    internal static bool TryCreateReviewedSignature(
        MethodInfo method,
        out UnrealManagedFunctionSignature? signature)
    {
        return FunctionContract.TryCreateSignatureOnly(method, out signature);
    }

    private static bool IsReviewedBlueprintCallable(MethodInfo method) =>
        method.GetCustomAttribute<global::Finare.VisualAttribute>(inherit: false) is not null ||
        method.GetCustomAttribute<UnrealBlueprintEventAttribute>(inherit: false) is not null ||
        method.GetCustomAttribute<UnrealBlueprintOverrideAttribute>(inherit: false) is not null ||
        method.GetCustomAttribute<UnrealRpcAttribute>(inherit: false) is not null ||
        method.GetCustomAttribute<UnrealRepNotifyAttribute>(inherit: false) is not null ||
        method.GetCustomAttributes<UnrealInputActionAttribute>(inherit: false).Any() ||
        method.GetCustomAttributes<UnrealInterfaceFunctionAttribute>(inherit: false).Any();

    public void Invoke(object? target, string functionName, nint frameAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        if (_staticOnly ? target is not null : target is null)
        {
            throw new ArgumentException(
                _staticOnly
                    ? $"Static managed library '{_typeName}' cannot receive an instance target."
                    : $"Managed Script Class '{_typeName}' requires an instance target.",
                nameof(target));
        }
        if (!_functions.TryGetValue(functionName, out FunctionContract? contract))
        {
            throw new MissingMethodException(
                $"Managed Script Class '{_typeName}' has no reviewed Blueprint-callable function '{functionName}'.");
        }
        ValidateFrame(frameAddress, contract.ArgumentCount, contract.ResultCount);

        if (target is UnrealObjectScript objectScript)
        {
            objectScript.BeginGeneratedFunctionCall();
            try
            {
                contract.Invoke(target, frameAddress);
            }
            finally
            {
                objectScript.EndGeneratedFunctionCall();
            }
            return;
        }
        contract.Invoke(target, frameAddress);
    }

    private delegate void InvokeFunction(object? target, nint frame);

    internal sealed record UnrealManagedFunctionSignature(string Canonical);

    private sealed record FunctionContract(
        int ArgumentCount,
        int ResultCount,
        InvokeFunction Invoke)
    {
        public static bool TryCreate(
            MethodInfo method,
            out FunctionContract? contract,
            out UnrealManagedFunctionSignature? signature)
        {
            contract = null;
            signature = null;
            ParameterInfo[] parameters = method.GetParameters();
            if (!TryValidateShape(method, parameters))
            {
                return false;
            }

            var target = Expression.Parameter(typeof(object), "target");
            var frame = Expression.Parameter(typeof(nint), "frame");
            var callArguments = new Expression[parameters.Length];
            var variables = new List<ParameterExpression>();
            var prefix = new List<Expression>();
            var outputVariables = new List<(ParameterExpression Variable, Type Type)>();
            for (int index = 0; index < parameters.Length; ++index)
            {
                ParameterInfo parameter = parameters[index];
                Type parameterType = parameter.ParameterType;
                Type valueType = parameterType.IsByRef
                    ? parameterType.GetElementType() ?? typeof(void)
                    : parameterType;
                if (valueType == typeof(void) || !IsSupported(valueType) ||
                    parameterType.IsByRef && parameter.IsIn && !parameter.IsOut)
                {
                    return false;
                }
                if (!parameterType.IsByRef)
                {
                    callArguments[index] = Expression.Call(
                        GetGenericHelper(nameof(ReadArgument)).MakeGenericMethod(valueType),
                        frame,
                        Expression.Constant(index));
                    continue;
                }

                ParameterExpression variable = Expression.Variable(
                    valueType,
                    $"parameter{index}");
                variables.Add(variable);
                if (!parameter.IsOut)
                {
                    prefix.Add(Expression.Assign(
                        variable,
                        Expression.Call(
                            GetGenericHelper(nameof(ReadArgument)).MakeGenericMethod(valueType),
                            frame,
                            Expression.Constant(index))));
                }
                callArguments[index] = variable;
                outputVariables.Add((variable, valueType));
            }

            MethodCallExpression call = method.IsStatic
                ? Expression.Call(method, callArguments)
                : Expression.Call(
                    Expression.Convert(
                        target,
                        method.DeclaringType ?? throw new InvalidOperationException(
                            $"Managed function '{method.Name}' has no declaring type.")),
                    method,
                    callArguments);
            var bodyExpressions = new List<Expression>(prefix.Count + outputVariables.Count + 3);
            bodyExpressions.AddRange(prefix);
            int resultIndex = 0;
            if (method.ReturnType == typeof(void))
            {
                bodyExpressions.Add(call);
            }
            else
            {
                ParameterExpression returnValue = Expression.Variable(
                    method.ReturnType,
                    "returnValue");
                variables.Add(returnValue);
                bodyExpressions.Add(Expression.Assign(returnValue, call));
                bodyExpressions.Add(Expression.Call(
                    GetGenericHelper(nameof(WriteResultAt)).MakeGenericMethod(method.ReturnType),
                    returnValue,
                    frame,
                    Expression.Constant(resultIndex++)));
            }
            foreach ((ParameterExpression variable, Type valueType) in outputVariables)
            {
                bodyExpressions.Add(Expression.Call(
                    GetGenericHelper(nameof(WriteResultAt)).MakeGenericMethod(valueType),
                    variable,
                    frame,
                    Expression.Constant(resultIndex++)));
            }
            bodyExpressions.Add(Expression.Call(
                GetHelper(nameof(CompleteResults)),
                frame,
                Expression.Constant(resultIndex)));
            Expression body = Expression.Block(variables, bodyExpressions);
            if (!TryCreateSignature(method, out signature))
            {
                return false;
            }
            contract = new FunctionContract(
                parameters.Length,
                resultIndex,
                Expression.Lambda<InvokeFunction>(body, target, frame).Compile());
            return true;
        }

        internal static bool TryCreateSignatureOnly(
            MethodInfo method,
            out UnrealManagedFunctionSignature? signature)
        {
            signature = null;
            ParameterInfo[] parameters = method.GetParameters();
            if (!TryValidateShape(method, parameters) || !TryCreateSignature(method, out signature))
            {
                signature = null;
                return false;
            }
            return true;
        }

        private static bool TryValidateShape(MethodInfo method, ParameterInfo[] parameters)
        {
            if (method.ContainsGenericParameters ||
                parameters.Length > MaximumArgumentCount ||
                (method.ReturnType != typeof(void) && !IsSupported(method.ReturnType)))
            {
                return false;
            }
            foreach (ParameterInfo parameter in parameters)
            {
                Type parameterType = parameter.ParameterType;
                Type valueType = parameterType.IsByRef
                    ? parameterType.GetElementType() ?? typeof(void)
                    : parameterType;
                if (valueType == typeof(void) || !IsSupported(valueType) ||
                    parameterType.IsByRef && parameter.IsIn && !parameter.IsOut)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryCreateSignature(
            MethodInfo method,
            out UnrealManagedFunctionSignature? signature)
        {
            signature = null;
            if (!TryCreateTypeIdentity(method.ReturnType, out string returnIdentity))
            {
                return false;
            }

            var builder = new System.Text.StringBuilder();
            AppendPart(builder, method.Name);
            AppendPart(builder, returnIdentity);
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Type parameterType = parameter.ParameterType;
                Type valueType = parameterType.IsByRef
                    ? parameterType.GetElementType() ?? typeof(void)
                    : parameterType;
                if (!TryCreateTypeIdentity(valueType, out string parameterIdentity))
                {
                    return false;
                }
                string direction = parameterType.IsByRef
                    ? parameter.IsOut ? "out" : "ref"
                    : "in";
                AppendPart(builder, parameter.Name ?? string.Empty);
                AppendPart(builder, direction);
                AppendPart(builder, parameterIdentity);
            }
            signature = new UnrealManagedFunctionSignature(builder.ToString());
            return true;
        }

        private static bool TryCreateTypeIdentity(Type type, out string identity)
        {
            if (type == typeof(void))
            {
                identity = "void";
                return true;
            }
            return UnrealNativeValueCodecV3.TryCreateContractIdentity(type, out identity);
        }

        private static void AppendPart(System.Text.StringBuilder builder, string value) =>
            builder.Append(value.Length).Append(':').Append(value).Append(';');
    }

    private static bool IsSupported(Type type) =>
        UnrealNativeValueCodecV3.TryCreateResultDescriptor(type, out _, out _);

    private static MethodInfo GetHelper(string name) =>
        typeof(UnrealManagedFunctionDispatcherV2).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic) ??
        throw new MissingMethodException(typeof(UnrealManagedFunctionDispatcherV2).FullName, name);

    private static MethodInfo GetGenericHelper(string name) => GetHelper(name);

    private static unsafe void ValidateFrame(
        nint frameAddress,
        int argumentCount,
        int requiredResults)
    {
        if (frameAddress == 0)
        {
            throw new ArgumentNullException(nameof(frameAddress));
        }
        UnrealNativeCallFrameV3* frame = (UnrealNativeCallFrameV3*)frameAddress;
        if (frame->Flags != 0 || frame->Reserved8 != 0 || frame->Reserved32 != 0 ||
            frame->ArgumentCount != argumentCount ||
            argumentCount is < 0 or > MaximumArgumentCount ||
            (argumentCount > 0 && frame->Arguments == 0) ||
            frame->ResultCapacity < requiredResults ||
            (requiredResults > 0 && frame->Results == 0) ||
            frame->ByteCount > frame->ByteCapacity ||
            (frame->ByteCapacity > 0 && frame->ByteBuffer == 0))
        {
            frame->Status = UnrealNativeCallStatusV3.InvalidFrame;
            throw new ArgumentException(
                "The managed Blueprint function call frame is malformed: " +
                $"arguments={frame->ArgumentCount}/{argumentCount}, " +
                $"argumentPointer={(frame->Arguments != 0 ? "set" : "null")}, " +
                $"results={frame->ResultCapacity}/{requiredResults}, " +
                $"resultPointer={(frame->Results != 0 ? "set" : "null")}, " +
                $"bytes={frame->ByteCount}/{frame->ByteCapacity}, " +
                $"bytePointer={(frame->ByteBuffer != 0 ? "set" : "null")}, " +
                $"flags={frame->Flags}, reserved8={frame->Reserved8}, reserved32={frame->Reserved32}.",
                nameof(frameAddress));
        }
        frame->ResultCount = 0;
        frame->RequiredResultCapacity = (uint)requiredResults;
        frame->RequiredByteCapacity = frame->ByteCount;
        frame->InvocationError = 0;
        frame->HandleError = 0;
        frame->Status = UnrealNativeCallStatusV3.Uninitialized;
        frame->ErrorMessageOffset = 0;
        frame->ErrorMessageLength = 0;
    }

    private static unsafe T ReadArgument<T>(nint frameAddress, int index)
    {
        UnrealNativeCallFrameV3* frame = (UnrealNativeCallFrameV3*)frameAddress;
        var arguments = new ReadOnlySpan<UnrealNativeValueV3>(
            (void*)frame->Arguments,
            checked((int)frame->ArgumentCount));
        ReadOnlySpan<byte> arena = frame->ByteCount == 0
            ? ReadOnlySpan<byte>.Empty
            : new ReadOnlySpan<byte>((void*)frame->ByteBuffer, checked((int)frame->ByteCount));
        if ((uint)index >= frame->ArgumentCount ||
            !UnrealNativeValueCodecV3.TryDecode(
                in arguments[index],
                arena,
                checked((int)frame->ByteCount),
                out T decoded))
        {
            string detail = (uint)index < frame->ArgumentCount
                ? UnrealNativeValueCodecV3.DescribeDecodeFailure(
                    typeof(T),
                    in arguments[index],
                    arena,
                    checked((int)frame->ByteCount))
                : "the argument index is outside the call frame";
            throw new ArgumentException(
                $"Managed Blueprint function argument {index} cannot decode as '{typeof(T).FullName}': {detail}",
                nameof(frameAddress));
        }
        return decoded;
    }

    private static unsafe void WriteResultAt<T>(T value, nint frameAddress, int resultIndex)
    {
        UnrealNativeCallFrameV3* frame = (UnrealNativeCallFrameV3*)frameAddress;
        if (resultIndex < 0 || (uint)resultIndex >= frame->ResultCapacity ||
            frame->Results == 0)
        {
            frame->Status = UnrealNativeCallStatusV3.InvalidFrame;
            throw new ArgumentOutOfRangeException(
                nameof(resultIndex),
                "The managed Blueprint function result index is outside the borrowed result cells.");
        }
        Span<byte> arena = frame->ByteCapacity == 0
            ? Span<byte>.Empty
            : new Span<byte>((void*)frame->ByteBuffer, checked((int)frame->ByteCapacity));
        int byteCount = checked((int)frame->ByteCount);
        if (!UnrealNativeValueCodecV3.TryEncode(
                value,
                arena,
                ref byteCount,
                out UnrealNativeValueV3 encoded,
                out _))
        {
            frame->Status = UnrealNativeCallStatusV3.BufferTooSmall;
            frame->RequiredByteCapacity = frame->ByteCapacity == uint.MaxValue
                ? uint.MaxValue
                : frame->ByteCapacity + 1;
            throw new ArgumentException(
                $"Managed Blueprint function result '{typeof(T).FullName}' does not fit its borrowed arena.",
                nameof(frameAddress));
        }
        ((UnrealNativeValueV3*)frame->Results)[resultIndex] = encoded;
        frame->ByteCount = checked((uint)byteCount);
        frame->RequiredByteCapacity = frame->ByteCount;
    }

    private static unsafe void CompleteResults(nint frameAddress, int resultCount)
    {
        UnrealNativeCallFrameV3* frame = (UnrealNativeCallFrameV3*)frameAddress;
        if (resultCount < 0 || (uint)resultCount > frame->ResultCapacity)
        {
            frame->Status = UnrealNativeCallStatusV3.InvalidFrame;
            throw new ArgumentOutOfRangeException(nameof(resultCount));
        }
        frame->ResultCount = checked((uint)resultCount);
        frame->RequiredResultCapacity = frame->ResultCount;
        frame->RequiredByteCapacity = frame->ByteCount;
        frame->Status = UnrealNativeCallStatusV3.Success;
    }
}
