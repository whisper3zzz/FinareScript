namespace UnifiedScript.Unreal;

public readonly record struct UnrealObjectHandle(ulong Value)
{
    public bool IsNull => Value == 0;

    public bool IsWellFormed => EncodedSlot != 0 && Generation != 0;

    public uint SlotIndex => IsWellFormed
        ? EncodedSlot - 1
        : throw new InvalidOperationException("The Unreal object handle is not well formed.");

    public uint Generation => (uint)(Value >> 32);

    public static UnrealObjectHandle Create(uint slotIndex, uint generation)
    {
        if (slotIndex == uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slotIndex),
                "The maximum slot value is reserved by the native handle encoding.");
        }

        if (generation == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                "An Unreal object handle generation must be non-zero.");
        }

        return new UnrealObjectHandle(((ulong)generation << 32) | slotIndex + 1UL);
    }

    private uint EncodedSlot => (uint)Value;
}

public interface IUnrealInvocationBridge
{
    T ReadProperty<T>(UnrealObjectHandle target, string propertyPath);

    void WriteProperty<T>(UnrealObjectHandle target, string propertyPath, T value);

    T Invoke<T>(
        UnrealObjectHandle target,
        string functionPath,
        IReadOnlyList<object?> arguments);

    void Invoke(
        UnrealObjectHandle target,
        string functionPath,
        IReadOnlyList<object?> arguments);
}

/// <summary>
/// Optional generated-member fast path. Member IDs are stable FNV-1a hashes of
/// the canonical reflection paths emitted into generated wrappers.
/// </summary>
public interface IUnrealFastInvocationBridge
{
    bool SupportsFastInvocation { get; }

    T ReadProperty<T>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath);

    void WriteProperty<T>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T value);

    T Invoke<T>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        IReadOnlyList<object?> arguments);

    void Invoke(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        IReadOnlyList<object?> arguments);
}

public static class UnrealMemberId
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong FromPath(string memberPath)
    {
        if (string.IsNullOrWhiteSpace(memberPath))
        {
            throw new ArgumentException(
                "A canonical Unreal member path is required.",
                nameof(memberPath));
        }

        ulong hash = OffsetBasis;
        foreach (byte value in global::System.Text.Encoding.UTF8.GetBytes(memberPath))
        {
            hash ^= value;
            hash = unchecked(hash * Prime);
        }
        return hash != 0
            ? hash
            : throw new InvalidOperationException(
                $"Unreal member path '{memberPath}' produced the reserved null ID.");
    }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class UnrealTypeAttribute(string path) : Attribute
{
    public string Path { get; } = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("An Unreal type path is required.", nameof(path))
        : path;
}

public abstract class UnrealObject
{
    protected UnrealObject(UnrealObjectHandle handle, IUnrealInvocationBridge bridge)
    {
        if (!handle.IsWellFormed)
        {
            throw new ArgumentException(
                "A managed Unreal wrapper requires a well-formed object handle.",
                nameof(handle));
        }

        Handle = handle;
        Bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    public UnrealObjectHandle Handle { get; }

    protected IUnrealInvocationBridge Bridge { get; }

    protected T ReadProperty<T>(string propertyPath) =>
        Bridge.ReadProperty<T>(Handle, propertyPath);

    protected T ReadProperty<T>(ulong memberId, string propertyPath) =>
        Bridge is IUnrealFastInvocationBridge fastBridge &&
        fastBridge.SupportsFastInvocation
            ? fastBridge.ReadProperty<T>(Handle, memberId, propertyPath)
            : Bridge.ReadProperty<T>(Handle, propertyPath);

    protected void WriteProperty<T>(string propertyPath, T value) =>
        Bridge.WriteProperty(Handle, propertyPath, value);

    protected void WriteProperty<T>(
        ulong memberId,
        string propertyPath,
        T value)
    {
        if (Bridge is IUnrealFastInvocationBridge fastBridge &&
            fastBridge.SupportsFastInvocation)
        {
            fastBridge.WriteProperty(Handle, memberId, propertyPath, value);
            return;
        }
        Bridge.WriteProperty(Handle, propertyPath, value);
    }

    protected T Invoke<T>(string functionPath, params object?[] arguments) =>
        Bridge.Invoke<T>(Handle, functionPath, arguments);

    protected T Invoke<T>(
        ulong memberId,
        string functionPath,
        params object?[] arguments) =>
        Bridge is IUnrealFastInvocationBridge fastBridge &&
        fastBridge.SupportsFastInvocation
            ? fastBridge.Invoke<T>(Handle, memberId, functionPath, arguments)
            : Bridge.Invoke<T>(Handle, functionPath, arguments);

    protected void Invoke(string functionPath, params object?[] arguments) =>
        Bridge.Invoke(Handle, functionPath, arguments);

    protected void Invoke(
        ulong memberId,
        string functionPath,
        params object?[] arguments)
    {
        if (Bridge is IUnrealFastInvocationBridge fastBridge &&
            fastBridge.SupportsFastInvocation)
        {
            fastBridge.Invoke(Handle, memberId, functionPath, arguments);
            return;
        }
        Bridge.Invoke(Handle, functionPath, arguments);
    }
}
