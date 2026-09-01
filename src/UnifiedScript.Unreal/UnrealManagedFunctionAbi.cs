using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;

namespace UnifiedScript.Unreal;

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
            if (method.GetCustomAttribute<global::UnifiedScript.VisualAttribute>(inherit: false) is null ||
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
