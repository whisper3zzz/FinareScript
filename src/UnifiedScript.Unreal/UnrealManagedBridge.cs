namespace UnifiedScript.Unreal;

public readonly record struct UnrealObjectHandle(nint Value)
{
    public bool IsNull => Value == 0;
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
        if (handle.IsNull)
        {
            throw new ArgumentException("A managed Unreal wrapper requires a live object handle.", nameof(handle));
        }

        Handle = handle;
        Bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    public UnrealObjectHandle Handle { get; }

    protected IUnrealInvocationBridge Bridge { get; }

    protected T ReadProperty<T>(string propertyPath) =>
        Bridge.ReadProperty<T>(Handle, propertyPath);

    protected void WriteProperty<T>(string propertyPath, T value) =>
        Bridge.WriteProperty(Handle, propertyPath, value);

    protected T Invoke<T>(string functionPath, params object?[] arguments) =>
        Bridge.Invoke<T>(Handle, functionPath, arguments);

    protected void Invoke(string functionPath, params object?[] arguments) =>
        Bridge.Invoke(Handle, functionPath, arguments);
}
