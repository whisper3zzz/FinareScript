using System.Reflection;
using System.Runtime.CompilerServices;

namespace Finare.Unreal;

[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class UnrealValueAttribute : Attribute;

[UnrealValue]
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

/// <summary>Generation-safe handle whose native value is required to be a UClass.</summary>
[UnrealValue]
public readonly record struct UnrealClassHandle(ulong Value)
{
    public UnrealObjectHandle ObjectHandle => new(Value);

    public bool IsNull => ObjectHandle.IsNull;

    public bool IsWellFormed => ObjectHandle.IsWellFormed;

    public uint SlotIndex => ObjectHandle.SlotIndex;

    public uint Generation => ObjectHandle.Generation;

    public static UnrealClassHandle Create(uint slotIndex, uint generation) =>
        new(UnrealObjectHandle.Create(slotIndex, generation).Value);
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
/// Optional lifetime and identity contract for bridges that expose UObject
/// views. Identity is separate from the bridge instance so generation bridges
/// over one World can exchange objects while disposed generations still fail.
/// </summary>
public interface IUnrealObjectBridgeScope
{
    object IdentityScope { get; }

    void ValidateAccess(UnrealObjectHandle handle);
}

/// <summary>Native type queries used to validate typed managed views.</summary>
public interface IUnrealObjectTypeBridge
{
    IReadOnlyList<string> GetObjectTypePaths(UnrealObjectHandle target);

    bool IsObjectOfClass(UnrealObjectHandle target, string classPath);
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

/// <summary>
/// Optional allocation-free generated invocation path. Generic argument
/// overloads avoid the params object[] boundary for supported ABI v3 values.
/// </summary>
public interface IUnrealTypedInvocationBridge
{
    bool SupportsTypedInvocation { get; }

    TResult Invoke<TResult>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath);

    TResult Invoke<TResult, T0>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T0 argument0);

    TResult Invoke<TResult, T0, T1>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T0 argument0,
        T1 argument1);

    TResult Invoke<TResult, T0, T1, T2>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T0 argument0,
        T1 argument1,
        T2 argument2);

    TResult Invoke<TResult, T0, T1, T2, T3>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T0 argument0,
        T1 argument1,
        T2 argument2,
        T3 argument3);

    void Invoke(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath);

    void Invoke<T0>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T0 argument0);

    void Invoke<T0, T1>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T0 argument0,
        T1 argument1);

    void Invoke<T0, T1, T2>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T0 argument0,
        T1 argument1,
        T2 argument2);

    void Invoke<T0, T1, T2, T3>(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        T0 argument0,
        T1 argument1,
        T2 argument2,
        T3 argument3);
}

public interface IUnrealInvocationMetrics
{
    long CallFrameInvocationCount { get; }

    long LegacyInvocationCount { get; }
}

public readonly record struct UnrealOutputArgument(
    Type Type,
    UnrealParameterDirection Direction,
    object? Value);

public sealed record UnrealOutputInvocationResult(
    object? ReturnValue,
    IReadOnlyList<object?> Outputs)
{
    public object? GetOutput(int index) => Outputs[index];
};

/// <summary>
/// ABI v3-only multi-result path used by generated wrappers for output and
/// input/output UFunction parameters.
/// </summary>
public interface IUnrealOutputInvocationBridge
{
    UnrealOutputInvocationResult InvokeWithOutputs(
        UnrealObjectHandle target,
        ulong memberId,
        string memberPath,
        Type returnType,
        IReadOnlyList<UnrealOutputArgument> arguments);
}

/// <summary>
/// Optional generation-owned route for explicitly invoking the inherited
/// Blueprint implementation of a managed override.
/// </summary>
public interface IUnrealParentInvocationBridge
{
    bool SupportsParentInvocation { get; }

    UnrealOutputInvocationResult InvokeParentWithOutputs(
        UnrealObjectHandle host,
        ulong memberId,
        string memberPath,
        Type returnType,
        IReadOnlyList<UnrealOutputArgument> arguments);
}

public sealed record UnrealEventParameterDescriptor(
    string Name,
    Type Type,
    UnrealParameterDirection Direction);

/// <summary>
/// Generated, engine-version-specific contract for one reflected UE delegate property.
/// The stable event ID is derived from the canonical owner/member path.
/// </summary>
public sealed record UnrealEventDescriptor(
    ulong EventId,
    string EventPath,
    string SignaturePath,
    Type ReturnType,
    UnrealDelegateKind Kind,
    IReadOnlyList<UnrealEventParameterDescriptor> Parameters);

/// <summary>
/// Optional bridge capability used by generated wrappers to bind reflected UE events.
/// Implementations own native proxy creation and must make disposal idempotent.
/// </summary>
public interface IUnrealEventBridge
{
    IUnrealEventSubscription Subscribe(
        UnrealObjectHandle source,
        UnrealEventDescriptor descriptor,
        Delegate handler);
}

/// <summary>
/// Internal generation-aware event route. The owner is carried separately
/// from the delegate because generated scripts may subscribe with a closure;
/// relying on Delegate.Target would then lose the state synchronization owner.
/// </summary>
internal interface IUnrealOwnedEventBridge
{
    IUnrealEventSubscription Subscribe(
        UnrealObjectHandle source,
        UnrealEventDescriptor descriptor,
        Delegate handler,
        UnrealObjectScript owner);
}

public interface IUnrealEventSubscription : IDisposable
{
    ulong SubscriptionId { get; }

    bool IsActive { get; }

    Exception? LastDispatchException { get; }
}

public enum UnrealEventSource
{
    Target,
    Host,
}

/// <summary>
/// Marks a generated wrapper subscription method as the managed endpoint for one
/// canonical reflected UE delegate property. Event authoring resolves endpoints
/// by path, so generated C# member-name collision suffixes do not affect assets.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class UnrealEventEndpointAttribute(string eventPath) : Attribute
{
    public string EventPath { get; } = string.IsNullOrWhiteSpace(eventPath)
        ? throw new ArgumentException("A canonical Unreal event path is required.", nameof(eventPath))
        : eventPath;
}

/// <summary>
/// Declares a visual method as a handler for a reflected UE delegate. The World
/// host binds the method after OnStart, once script state has been initialized.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class UnrealEventAttribute(Type sourceType, string eventPath) : Attribute
{
    public Type SourceType { get; } = sourceType is not null &&
        typeof(UnrealObject).IsAssignableFrom(sourceType)
            ? sourceType
            : throw new ArgumentException(
                $"An {nameof(UnrealObject)} wrapper type is required.",
                nameof(sourceType));

    public string EventPath { get; } = string.IsNullOrWhiteSpace(eventPath)
        ? throw new ArgumentException("A canonical Unreal event path is required.", nameof(eventPath))
        : eventPath;

    public UnrealEventSource Source { get; init; } = UnrealEventSource.Target;

    /// <summary>
    /// Optional field or property on the script that contains a wrapper instance.
    /// When omitted, SourceType is constructed for the script Host or Target handle.
    /// </summary>
    public string? SourceMember { get; init; }
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
    public bool IsInterface { get; init; }
    public string Path { get; } = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("An Unreal type path is required.", nameof(path))
        : path;
}

/// <summary>Registers native-only paths once per wrapper, preventing reflected fallback.</summary>
public interface IUnrealNativeBindingBridge
{
    void RequireNativeBindings(IReadOnlyList<string> memberPaths);
}

[UnrealType("/Script/CoreUObject.Object")]
public class UnrealObject
{
    private static readonly ConditionalWeakTable<Assembly, ObjectViewTypes> ViewTypes = new();

    private sealed class ObjectViewTypes(Assembly assembly)
    {
        internal readonly Dictionary<string, Type[]> ByPath = assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(UnrealObject).IsAssignableFrom(t))
            .Select(t => (Type: t, Attribute: t.GetCustomAttribute<UnrealTypeAttribute>()))
            .Where(t => t.Attribute is not null)
            .GroupBy(t => t.Attribute!.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Type).ToArray(), StringComparer.Ordinal);
    }
    protected UnrealObject(UnrealObjectHandle handle, IUnrealInvocationBridge bridge)
    {
        if (!handle.IsWellFormed)
        {
            throw new ArgumentException(
                "A managed Unreal wrapper requires a well-formed object handle.",
                nameof(handle));
        }

        _handle = handle;
        Bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _identityScope = UnrealObjectBridgeAccess.Identity(Bridge);
        UnrealObjectBridgeAccess.Validate(Bridge, _handle);
    }

    private readonly UnrealObjectHandle _handle;
    private readonly object _identityScope;

    public UnrealObjectHandle Handle
    {
        get
        {
            EnsureAccessible();
            return _handle;
        }
    }

    protected IUnrealInvocationBridge Bridge { get; }

    /// <summary>
    /// Materializes a managed view only after the bridge confirms the native
    /// class relationship. A null handle maps to null for nullable UObject
    /// properties and parameters.
    /// </summary>
    public static T? Wrap<T>(
        UnrealObjectHandle handle,
        IUnrealInvocationBridge bridge)
        where T : UnrealObject
    {
        if (handle.IsNull)
        {
            return null;
        }
        ArgumentNullException.ThrowIfNull(bridge);
        UnrealObjectBridgeAccess.Validate(bridge, handle);
        if (typeof(T) == typeof(UnrealObject))
        {
            ValidateNativeObject(bridge, handle);
            return (T)(object)new UnrealObject(handle, bridge);
        }

        ValidateNativeType<T>(bridge, handle);

        // An ephemeron cache keeps lookup cheap without rooting collectible
        // assemblies through the Type objects stored in each value.
        Type requested = typeof(T);
        IReadOnlyList<string> paths = ((IUnrealObjectTypeBridge)bridge).GetObjectTypePaths(handle);
        ObjectViewTypes candidates = ViewTypes.GetValue(requested.Assembly, a => new ObjectViewTypes(a));
        Type selected = paths.Select(path => candidates.ByPath.TryGetValue(path, out Type[]? types)
            ? types.FirstOrDefault(requested.IsAssignableFrom) : null).FirstOrDefault(t => t is not null) ?? requested;

        try
        {
            return (T?)Activator.CreateInstance(
                selected,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [handle, bridge],
                culture: null);
        }
        catch (MissingMethodException exception)
        {
            throw new InvalidOperationException(
                $"Managed Unreal view '{typeof(T).FullName}' must expose a (UnrealObjectHandle, IUnrealInvocationBridge) constructor.",
                exception);
        }
    }

    public T? As<T>() where T : UnrealObject
    {
        EnsureAccessible();
        ValidateNativeType<T>(Bridge, _handle);
        return this is T typed ? typed : Wrap<T>(_handle, Bridge);
    }

    public bool IsA<T>() where T : UnrealObject
    {
        EnsureAccessible();
        try
        {
            ValidateNativeType<T>(Bridge, _handle);
            return true;
        }
        catch (InvalidCastException)
        {
            return false;
        }
    }

    private static void ValidateNativeObject(
        IUnrealInvocationBridge bridge,
        UnrealObjectHandle handle)
    {
        if (bridge is not IUnrealObjectTypeBridge typeBridge ||
            typeBridge.GetObjectTypePaths(handle).Count == 0)
        {
            throw new InvalidCastException(
                $"Native object handle {handle.Value} could not be resolved to a loaded Unreal object.");
        }
    }

    private static void ValidateNativeType<T>(
        IUnrealInvocationBridge bridge,
        UnrealObjectHandle handle)
        where T : UnrealObject
    {
        if (typeof(T) == typeof(UnrealObject))
        {
            ValidateNativeObject(bridge, handle);
            return;
        }
        UnrealTypeAttribute? type = typeof(T).GetCustomAttribute<UnrealTypeAttribute>();
        if (type is null)
        {
            throw new InvalidOperationException(
                $"Managed Unreal view '{typeof(T).FullName}' is missing {nameof(UnrealTypeAttribute)}.");
        }
        if (bridge is not IUnrealObjectTypeBridge typeBridge)
        {
            throw new NotSupportedException(
                "Typed Unreal object views require a bridge with native type-query support.");
        }
        if (!typeBridge.IsObjectOfClass(handle, type.Path))
        {
            throw new InvalidCastException(
                $"Native object handle {handle.Value} is not an instance of Unreal class '{type.Path}'.");
        }
    }

    internal IUnrealInvocationBridge InvocationBridge => Bridge;
    internal static UnrealObject InvocationTarget(UnrealObjectHandle handle, IUnrealInvocationBridge bridge) => new(handle, bridge);

    internal void EnsureAccessible() => UnrealObjectBridgeAccess.Validate(Bridge, _handle);

    internal static bool AreCompatible(
        UnrealObject source,
        IUnrealInvocationBridge destination)
    {
        source.EnsureAccessible();
        ArgumentNullException.ThrowIfNull(destination);
        UnrealObjectBridgeAccess.Validate(destination, source._handle);
        return ReferenceEquals(
            source._identityScope,
            UnrealObjectBridgeAccess.Identity(destination));
    }

    public override bool Equals(object? obj) =>
        obj is UnrealObject other &&
        _handle == other._handle &&
        ReferenceEquals(
            _identityScope,
            other._identityScope);

    public override int GetHashCode() =>
        HashCode.Combine(
            _handle,
            RuntimeHelpers.GetHashCode(_identityScope));

    public static bool operator ==(UnrealObject? left, UnrealObject? right) =>
        ReferenceEquals(left, right) || left is not null && left.Equals(right);

    public static bool operator !=(UnrealObject? left, UnrealObject? right) => !(left == right);

    private static class UnrealObjectBridgeAccess
    {
        internal static object Identity(IUnrealInvocationBridge bridge) =>
            bridge is IUnrealObjectBridgeScope scoped
                ? scoped.IdentityScope
                : bridge;

        internal static void Validate(
            IUnrealInvocationBridge bridge,
            UnrealObjectHandle handle)
        {
            if (!handle.IsWellFormed)
            {
                throw new ObjectDisposedException(
                    nameof(UnrealObject),
                    "The managed Unreal object handle is null or malformed.");
            }
            if (bridge is IUnrealObjectBridgeScope scoped)
            {
                scoped.ValidateAccess(handle);
            }
        }
    }

    protected void RequireNativeBindings(params string[] memberPaths)
    {
        if (Bridge is not IUnrealNativeBindingBridge nativeBridge)
        {
            throw new NotSupportedException("Native extension bindings require a generated native invocation bridge; reflection is not a substitute.");
        }
        nativeBridge.RequireNativeBindings(memberPaths);
    }

    protected T ReadProperty<T>(string propertyPath) =>
        UnrealObjectInvocationAdapter.ReadProperty<T>(this, 0, propertyPath, false);

    protected T ReadProperty<T>(ulong memberId, string propertyPath) =>
        UnrealObjectInvocationAdapter.ReadProperty<T>(this, memberId, propertyPath, true);

    protected void WriteProperty<T>(string propertyPath, T value) =>
        UnrealObjectInvocationAdapter.WriteProperty(this, 0, propertyPath, value, false);

    protected void WriteProperty<T>(
        ulong memberId,
        string propertyPath,
        T value)
    {
        UnrealObjectInvocationAdapter.WriteProperty(this, memberId, propertyPath, value, true);
    }

    protected T Invoke<T>(string functionPath, params object?[] arguments) =>
        UnrealObjectInvocationAdapter.Invoke<T>(this, 0, functionPath, arguments, false);

    protected T Invoke<T>(
        ulong memberId,
        string functionPath)
    {
        if (UnrealObjectProjection<T>.Required)
        {
            return (T)UnrealObjectInvocationAdapter.InvokeWithOutputs(this, memberId, functionPath, typeof(T), []).ReturnValue!;
        }
        return Bridge is IUnrealTypedInvocationBridge typedBridge &&
        typedBridge.SupportsTypedInvocation
            ? typedBridge.Invoke<T>(Handle, memberId, functionPath)
            : Bridge is IUnrealFastInvocationBridge fastBridge &&
              fastBridge.SupportsFastInvocation
                ? fastBridge.Invoke<T>(Handle, memberId, functionPath, [])
                : Bridge.Invoke<T>(Handle, functionPath, []);
    }

    protected TResult Invoke<TResult, T0>(
        ulong memberId,
        string functionPath,
        T0 argument0)
    {
        if (UnrealObjectProjection<TResult>.Required || UnrealObjectProjection<T0>.Required)
        {
            return (TResult)UnrealObjectInvocationAdapter.InvokeWithOutputs(this, memberId, functionPath, typeof(TResult), [new(typeof(T0), UnrealParameterDirection.Input, argument0)]).ReturnValue!;
        }
        return Bridge is IUnrealTypedInvocationBridge typedBridge &&
        typedBridge.SupportsTypedInvocation
            ? typedBridge.Invoke<TResult, T0>(Handle, memberId, functionPath, argument0)
            : InvokeLegacyResult<TResult>(memberId, functionPath, [argument0]);
    }

    protected TResult Invoke<TResult, T0, T1>(
        ulong memberId,
        string functionPath,
        T0 argument0,
        T1 argument1)
    {
        if (UnrealObjectProjection<TResult>.Required || UnrealObjectProjection<T0>.Required || UnrealObjectProjection<T1>.Required)
        {
            return (TResult)UnrealObjectInvocationAdapter.InvokeWithOutputs(this, memberId, functionPath, typeof(TResult), [new(typeof(T0), UnrealParameterDirection.Input, argument0), new(typeof(T1), UnrealParameterDirection.Input, argument1)]).ReturnValue!;
        }
        return Bridge is IUnrealTypedInvocationBridge typedBridge &&
        typedBridge.SupportsTypedInvocation
            ? typedBridge.Invoke<TResult, T0, T1>(
                Handle,
                memberId,
                functionPath,
                argument0,
                argument1)
            : InvokeLegacyResult<TResult>(memberId, functionPath, [argument0, argument1]);
    }

    protected TResult Invoke<TResult, T0, T1, T2>(
        ulong memberId,
        string functionPath,
        T0 argument0,
        T1 argument1,
        T2 argument2)
    {
        if (UnrealObjectProjection<TResult>.Required || UnrealObjectProjection<T0>.Required || UnrealObjectProjection<T1>.Required || UnrealObjectProjection<T2>.Required)
        {
            return (TResult)UnrealObjectInvocationAdapter.InvokeWithOutputs(this, memberId, functionPath, typeof(TResult), [new(typeof(T0), UnrealParameterDirection.Input, argument0), new(typeof(T1), UnrealParameterDirection.Input, argument1), new(typeof(T2), UnrealParameterDirection.Input, argument2)]).ReturnValue!;
        }
        return Bridge is IUnrealTypedInvocationBridge typedBridge &&
        typedBridge.SupportsTypedInvocation
            ? typedBridge.Invoke<TResult, T0, T1, T2>(
                Handle,
                memberId,
                functionPath,
                argument0,
                argument1,
                argument2)
            : InvokeLegacyResult<TResult>(memberId, functionPath, [argument0, argument1, argument2]);
    }

    protected TResult Invoke<TResult, T0, T1, T2, T3>(
        ulong memberId,
        string functionPath,
        T0 argument0,
        T1 argument1,
        T2 argument2,
        T3 argument3)
    {
        if (UnrealObjectProjection<TResult>.Required || UnrealObjectProjection<T0>.Required || UnrealObjectProjection<T1>.Required || UnrealObjectProjection<T2>.Required || UnrealObjectProjection<T3>.Required)
        {
            return (TResult)UnrealObjectInvocationAdapter.InvokeWithOutputs(this, memberId, functionPath, typeof(TResult), [new(typeof(T0), UnrealParameterDirection.Input, argument0), new(typeof(T1), UnrealParameterDirection.Input, argument1), new(typeof(T2), UnrealParameterDirection.Input, argument2), new(typeof(T3), UnrealParameterDirection.Input, argument3)]).ReturnValue!;
        }
        return Bridge is IUnrealTypedInvocationBridge typedBridge &&
        typedBridge.SupportsTypedInvocation
            ? typedBridge.Invoke<TResult, T0, T1, T2, T3>(
                Handle,
                memberId,
                functionPath,
                argument0,
                argument1,
                argument2,
                argument3)
            : InvokeLegacyResult<TResult>(memberId, functionPath, [argument0, argument1, argument2, argument3]);
    }

    protected T Invoke<T>(
        ulong memberId,
        string functionPath,
        params object?[] arguments) =>
        UnrealObjectInvocationAdapter.Invoke<T>(this, memberId, functionPath, arguments, true);

    protected void Invoke(string functionPath, params object?[] arguments) =>
        UnrealObjectInvocationAdapter.InvokeVoid(this, 0, functionPath, arguments, false);

    protected void Invoke(
        ulong memberId,
        string functionPath)
    {
        if (Bridge is IUnrealTypedInvocationBridge typedBridge &&
            typedBridge.SupportsTypedInvocation)
        {
            typedBridge.Invoke(Handle, memberId, functionPath);
            return;
        }
        InvokeLegacy(memberId, functionPath, []);
    }

    protected void Invoke(
        ulong memberId,
        string functionPath,
        params object?[] arguments) =>
        UnrealObjectInvocationAdapter.InvokeVoid(this, memberId, functionPath, arguments, true);

    protected void Invoke<T0>(
        ulong memberId,
        string functionPath,
        T0 argument0)
    {
        if (UnrealObjectProjection<T0>.Required)
        {
            UnrealObjectInvocationAdapter.InvokeWithOutputs(this, memberId, functionPath, typeof(void), [new(typeof(T0), UnrealParameterDirection.Input, argument0)]);
            return;
        }
        if (Bridge is IUnrealTypedInvocationBridge typedBridge &&
            typedBridge.SupportsTypedInvocation)
        {
            typedBridge.Invoke(Handle, memberId, functionPath, argument0);
            return;
        }
        InvokeLegacy(memberId, functionPath, [argument0]);
    }

    protected void Invoke<T0, T1>(
        ulong memberId,
        string functionPath,
        T0 argument0,
        T1 argument1)
    {
        if (UnrealObjectProjection<T0>.Required || UnrealObjectProjection<T1>.Required)
        {
            UnrealObjectInvocationAdapter.InvokeWithOutputs(this, memberId, functionPath, typeof(void), [new(typeof(T0), UnrealParameterDirection.Input, argument0), new(typeof(T1), UnrealParameterDirection.Input, argument1)]);
            return;
        }
        if (Bridge is IUnrealTypedInvocationBridge typedBridge &&
            typedBridge.SupportsTypedInvocation)
        {
            typedBridge.Invoke(Handle, memberId, functionPath, argument0, argument1);
            return;
        }
        InvokeLegacy(memberId, functionPath, [argument0, argument1]);
    }

    protected void Invoke<T0, T1, T2>(
        ulong memberId,
        string functionPath,
        T0 argument0,
        T1 argument1,
        T2 argument2)
    {
        if (UnrealObjectProjection<T0>.Required || UnrealObjectProjection<T1>.Required || UnrealObjectProjection<T2>.Required)
        {
            UnrealObjectInvocationAdapter.InvokeWithOutputs(this, memberId, functionPath, typeof(void), [new(typeof(T0), UnrealParameterDirection.Input, argument0), new(typeof(T1), UnrealParameterDirection.Input, argument1), new(typeof(T2), UnrealParameterDirection.Input, argument2)]);
            return;
        }
        if (Bridge is IUnrealTypedInvocationBridge typedBridge &&
            typedBridge.SupportsTypedInvocation)
        {
            typedBridge.Invoke(Handle, memberId, functionPath, argument0, argument1, argument2);
            return;
        }
        InvokeLegacy(memberId, functionPath, [argument0, argument1, argument2]);
    }

    protected void Invoke<T0, T1, T2, T3>(
        ulong memberId,
        string functionPath,
        T0 argument0,
        T1 argument1,
        T2 argument2,
        T3 argument3)
    {
        if (UnrealObjectProjection<T0>.Required || UnrealObjectProjection<T1>.Required || UnrealObjectProjection<T2>.Required || UnrealObjectProjection<T3>.Required)
        {
            UnrealObjectInvocationAdapter.InvokeWithOutputs(this, memberId, functionPath, typeof(void), [new(typeof(T0), UnrealParameterDirection.Input, argument0), new(typeof(T1), UnrealParameterDirection.Input, argument1), new(typeof(T2), UnrealParameterDirection.Input, argument2), new(typeof(T3), UnrealParameterDirection.Input, argument3)]);
            return;
        }
        if (Bridge is IUnrealTypedInvocationBridge typedBridge &&
            typedBridge.SupportsTypedInvocation)
        {
            typedBridge.Invoke(
                Handle,
                memberId,
                functionPath,
                argument0,
                argument1,
                argument2,
                argument3);
            return;
        }
        InvokeLegacy(memberId, functionPath, [argument0, argument1, argument2, argument3]);
    }

    private T InvokeLegacyResult<T>(ulong memberId, string functionPath, IReadOnlyList<object?> arguments) =>
        Bridge is IUnrealFastInvocationBridge fast && fast.SupportsFastInvocation
            ? fast.Invoke<T>(Handle, memberId, functionPath, arguments)
            : Bridge.Invoke<T>(Handle, functionPath, arguments);

    private void InvokeLegacy(ulong memberId, string functionPath, IReadOnlyList<object?> arguments)
    {
        if (Bridge is IUnrealFastInvocationBridge fast && fast.SupportsFastInvocation)
            fast.Invoke(Handle, memberId, functionPath, arguments);
        else Bridge.Invoke(Handle, functionPath, arguments);
    }

    protected UnrealOutputInvocationResult InvokeWithOutputs(
        ulong memberId,
        string functionPath,
        Type returnType,
        IReadOnlyList<UnrealOutputArgument> arguments)
    {
        return UnrealObjectInvocationAdapter.InvokeWithOutputs(
            this, memberId, functionPath, returnType, arguments);
    }

    protected IUnrealEventSubscription SubscribeEvent(
        UnrealEventDescriptor descriptor,
        Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(handler);
        if (Bridge is not IUnrealEventBridge eventBridge)
        {
            throw new NotSupportedException(
                "The Unreal invocation bridge does not support reflected delegate subscriptions.");
        }

        Delegate projected = UnrealObjectDelegateAdapter.Adapt(handler, Bridge);
        if (!ReferenceEquals(projected, handler))
            descriptor = descriptor with
            {
                ReturnType = UnrealObjectInvocationAdapter.WireType(descriptor.ReturnType),
                Parameters = descriptor.Parameters.Select(p => p with
                { Type = UnrealObjectInvocationAdapter.WireType(p.Type) }).ToArray(),
            };
        return eventBridge.Subscribe(Handle, descriptor, projected);
    }

}

/// <summary>
/// Context-bound access to static Unreal functions. The handle supplies World
/// identity and lifetime but is not required to be an instance of the static
/// function's declaring UClass.
/// </summary>
public abstract class UnrealStaticService : UnrealObject
{
    protected UnrealStaticService(UnrealObjectHandle context, IUnrealInvocationBridge bridge)
        : base(context, bridge)
    {
    }
}
