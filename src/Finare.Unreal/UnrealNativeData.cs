using System.Collections;
using System.Runtime.InteropServices;

namespace Finare.Unreal;

public delegate void UnrealSpanAction<T>(Span<T> values) where T : unmanaged;
public delegate void UnrealReadOnlySpanAction<T>(ReadOnlySpan<T> values) where T : unmanaged;

internal interface IUnrealNativeDataProvider
{
    IUnrealNativeDataTransport NativeData { get; }
}
internal interface IUnrealNativeDataTransport
{
    object? AccessData(UnrealObjectHandle owner, string path, byte operation, Type resultType,
        IReadOnlyList<UnrealOutputArgument> arguments);
    void BorrowData<T>(UnrealObjectHandle owner, string path, bool write, UnrealSpanAction<T> action) where T : unmanaged;
}

/// <summary>A generation-bound logical property location, never a stored native pointer.</summary>
public class UnrealNativeView
{
    public UnrealNativeView(UnrealObject owner, string path)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        owner.EnsureAccessible();
    }
    internal UnrealObject Owner { get; }
    protected UnrealObject OwnerForView => Owner;
    internal string Path { get; }
    internal IUnrealNativeDataTransport Transport
    {
        get
        {
            Owner.EnsureAccessible();
            return Owner.InvocationBridge is IUnrealNativeDataProvider provider ? provider.NativeData :
                throw new NotSupportedException("The installed World bridge does not support native data views.");
        }
    }
    internal T Call<T>(byte operation, params UnrealOutputArgument[] arguments)
    {
        IUnrealNativeDataTransport transport = Transport;
        var wire = arguments.Select(a => new UnrealOutputArgument(UnrealObjectInvocationAdapter.WireType(a.Type), a.Direction,
            UnrealObjectInvocationAdapter.Convert(a.Value, a.Type, Owner.InvocationBridge, false))).ToArray();
        object? result = transport.AccessData(Owner.Handle, Path, operation, UnrealObjectInvocationAdapter.WireType(typeof(T)), wire);
        return (T)UnrealObjectInvocationAdapter.Convert(result, typeof(T), Owner.InvocationBridge, true)!;
    }
    internal void Change(byte operation, params UnrealOutputArgument[] arguments)
    {
        IUnrealNativeDataTransport transport = Transport;
        var wire = arguments.Select(a => new UnrealOutputArgument(UnrealObjectInvocationAdapter.WireType(a.Type), a.Direction,
            UnrealObjectInvocationAdapter.Convert(a.Value, a.Type, Owner.InvocationBridge, false))).ToArray();
        transport.AccessData(Owner.Handle, Path, operation, typeof(void), wire);
    }
    internal static UnrealOutputArgument Input<T>(T value) => new(typeof(T), UnrealParameterDirection.Input, value);
    protected string FieldPath(string name)
    {
        if (string.IsNullOrEmpty(name) || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("A native field name is required.", nameof(name));
        return Path + "." + name;
    }
    public UnrealValueView<T> Field<T>(string name) => new(Owner, FieldPath(name));
    public UnrealArrayView<T> Array<T>(string name) => new(Owner, FieldPath(name));
    public UnrealSetView<T> Set<T>(string name) => new(Owner, FieldPath(name));
    public UnrealMapView<K, V> Map<K, V>(string name) where K : notnull => new(Owner, FieldPath(name));
    public UnrealNativeView Struct(string name) => new(Owner, FieldPath(name));
}

public sealed class UnrealValueView<T>(UnrealObject owner, string path) : UnrealNativeView(owner, path)
{
    public T Value { get => Call<T>(0); set => Change(1, Input(value)); }
}

/// <summary>Native array operations. Element views represent a current index, not a durable element identity.</summary>
public sealed class UnrealArrayView<T>(UnrealObject owner, string path) : UnrealNativeView(owner, path), IReadOnlyList<T>
{
    public int Count => Call<int>(2);
    public T this[int index] { get => Element(index).Value; set => Element(index).Value = value; }
    public UnrealValueView<T> Element(int index) => new(Owner, IndexPath(index));
    public UnrealNativeView ElementFields(int index) => new(Owner, IndexPath(index));
    private string IndexPath(int index) => index >= 0 ? Path + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]" :
        throw new ArgumentOutOfRangeException(nameof(index));
    public void Add(T value) => Change(3, Input(value));
    public void RemoveAt(int index) => Change(4, Input(index));
    public void Clear() => Change(5);
    public IReadOnlyList<T> Snapshot() => Call<IReadOnlyList<T>>(0);
    public IEnumerator<T> GetEnumerator() { for (int i = 0; i < Count; ++i) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class UnrealSetView<T>(UnrealObject owner, string path) : UnrealNativeView(owner, path), IReadOnlyCollection<T>
{
    public int Count => Call<int>(2);
    public bool Add(T value) => Call<bool>(3, Input(value));
    public bool Remove(T value) => Call<bool>(4, Input(value));
    public bool Contains(T value) => Call<bool>(6, Input(value));
    public void Clear() => Change(5);
    public IReadOnlySet<T> Snapshot() => Call<IReadOnlySet<T>>(0);
    public IEnumerator<T> GetEnumerator() { for (int i = 0; i < Count; ++i) yield return Call<T>(8, Input(i)); }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class UnrealMapView<K, V>(UnrealObject owner, string path) : UnrealNativeView(owner, path), IReadOnlyCollection<KeyValuePair<K, V>> where K : notnull
{
    public int Count => Call<int>(2);
    public V this[K key] { get => Call<V>(6, Input(key)); set => Change(7, Input(key), Input(value)); }
    public bool Remove(K key) => Call<bool>(4, Input(key));
    public void Clear() => Change(5);
    public IReadOnlyDictionary<K, V> Snapshot() => Call<IReadOnlyDictionary<K, V>>(0);
    public IEnumerator<KeyValuePair<K, V>> GetEnumerator()
    {
        for (int i = 0; i < Count; ++i) { K key = Call<K>(8, Input(i)); yield return new(key, this[key]); }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class UnrealNativeMemory
{
    /// <summary>Direct native storage for this synchronous callback; UE reentry is forbidden until it returns.</summary>
    public static void WithSpan<T>(this UnrealArrayView<T> view, UnrealSpanAction<T> action) where T : unmanaged =>
        Borrow(view, true, action);
    public static void WithSpan<T>(this UnrealValueView<T> view, UnrealSpanAction<T> action) where T : unmanaged =>
        Borrow(view, true, action);
    public static void ReadSpan<T>(this UnrealValueView<T> view, UnrealReadOnlySpanAction<T> action) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        Borrow<T>(view, false, span => action(span));
    }
    public static void ReadSpan<T>(this UnrealArrayView<T> view, UnrealReadOnlySpanAction<T> action) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        Borrow<T>(view, false, span => action(span));
    }
    private static void Borrow<T>(UnrealNativeView view, bool write, UnrealSpanAction<T> action) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(view); ArgumentNullException.ThrowIfNull(action);
        view.Transport.BorrowData(view.Owner.Handle, view.Path, write, action);
    }
    internal static byte ElementKind<T>() where T : unmanaged => typeof(T) == typeof(byte) ? (byte)1 :
        typeof(T) == typeof(int) ? (byte)2 : typeof(T) == typeof(float) ? (byte)3 : typeof(T) == typeof(double) ? (byte)4 :
        typeof(T) == typeof(long) ? (byte)5 : typeof(T) == typeof(uint) ? (byte)6 :
        throw new NotSupportedException("Native spans require an exact byte, int, uint, long, float or double element layout.");
}

public sealed partial class NativeUnrealInvocationBridge : IUnrealNativeDataProvider
{
    IUnrealNativeDataTransport IUnrealNativeDataProvider.NativeData => _transport as IUnrealNativeDataTransport ??
        throw new NotSupportedException("The installed native transport does not support native data access.");
}
internal sealed partial class UnrealGenerationInvocationBridge : IUnrealNativeDataProvider
{
    IUnrealNativeDataTransport IUnrealNativeDataProvider.NativeData => LiveInner is IUnrealNativeDataProvider provider ? provider.NativeData :
        throw new NotSupportedException("The World bridge does not support native data access.");
}
