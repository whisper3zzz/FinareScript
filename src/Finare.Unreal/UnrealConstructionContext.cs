using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Finare.Unreal;

/// <summary>
/// Synchronous access to a native UObject constructor. Objects belong to UE;
/// every managed view expires when OnConstruct returns. No World is started.
/// </summary>
public sealed class UnrealConstructionContext : IDisposable
{
    private readonly ConstructionBridge _bridge;
    private readonly Func<int, UnrealObjectHandle, UnrealObjectHandle, string, string, UnrealObjectHandle> _execute;
    private readonly UnrealObjectHandle _host;
    private readonly bool _isClassDefaultObject;

    internal UnrealConstructionContext(IUnrealInvocationBridge bridge, UnrealObjectHandle host, bool isClassDefaultObject,
        Func<int, UnrealObjectHandle, UnrealObjectHandle, string, string, UnrealObjectHandle> execute)
    {
        _bridge = new ConstructionBridge(bridge);
        _host = host;
        _isClassDefaultObject = isClassDefaultObject;
        _execute = execute;
    }

    public IUnrealInvocationBridge Bridge { get { _bridge.ValidateAccess(_host); return _bridge; } }
    public UnrealObject Host => UnrealObject.Wrap<UnrealObject>(_host, Bridge)!;
    public bool IsClassDefaultObject { get { _bridge.ValidateAccess(_host); return _isClassDefaultObject; } }
    internal UnrealObjectHandle HostHandle { get { _bridge.ValidateAccess(_host); return _host; } }

    public T CreateDefaultSubobject<T>(string name) where T : UnrealObject
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _bridge.ValidateAccess(_host);
        string path = typeof(T).GetCustomAttribute<UnrealTypeAttribute>(false)?.Path
            ?? throw new ArgumentException($"{typeof(T).FullName} has no native Unreal type identity.");
        return UnrealObject.Wrap<T>(_execute(1, _host, default, name, path), _bridge)
            ?? throw new InvalidOperationException($"UE did not create default subobject '{name}'.");
    }

    public T? GetDefaultSubobject<T>(string name) where T : UnrealObject
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _bridge.ValidateAccess(_host);
        return UnrealObject.Wrap<T>(_execute(2, _host, default, name, ""), _bridge);
    }

    public void SetupAttachment(UnrealObject child, UnrealObject parent, string socketName = "")
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(parent);
        _bridge.ValidateAccess(_host);
        _execute(3, child.Handle, parent.Handle, socketName, "");
    }

    public void Dispose() => _bridge.Dispose();

    // Deliberately exposes no World, callback, async, native ownership or lease services.
    private sealed class ConstructionBridge(IUnrealInvocationBridge inner) :
        IUnrealInvocationBridge, IUnrealObjectBridgeScope, IUnrealObjectTypeBridge, IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private bool _disposed;
        public object IdentityScope => this;
        public void ValidateAccess(UnrealObjectHandle handle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("Native construction access must remain on its calling thread.");
            if (!handle.IsWellFormed) throw new ArgumentException("A live constructor handle is required.");
        }
        public T ReadProperty<T>(UnrealObjectHandle target, string path)
        { ValidateAccess(target); return inner.ReadProperty<T>(target, path); }
        public void WriteProperty<T>(UnrealObjectHandle target, string path, T value)
        { ValidateAccess(target); inner.WriteProperty(target, path, value); }
        public T Invoke<T>(UnrealObjectHandle target, string path, IReadOnlyList<object?> arguments)
        { ValidateAccess(target); return inner.Invoke<T>(target, path, arguments); }
        public void Invoke(UnrealObjectHandle target, string path, IReadOnlyList<object?> arguments)
        { ValidateAccess(target); inner.Invoke(target, path, arguments); }
        public IReadOnlyList<string> GetObjectTypePaths(UnrealObjectHandle target)
        { ValidateAccess(target); return ((IUnrealObjectTypeBridge)inner).GetObjectTypePaths(target); }
        public bool IsObjectOfClass(UnrealObjectHandle target, string classPath)
        { ValidateAccess(target); return ((IUnrealObjectTypeBridge)inner).IsObjectOfClass(target, classPath); }
        public void Dispose() => _disposed = true;
    }
}

public abstract partial class UnrealObjectScript
{
    private UnrealConstructionContext? _constructionContext;

    /// <summary>
    /// Called inside the native constructor, including CDO construction, before
    /// gameplay. Use only UE constructor-safe operations; managed fields and
    /// resources are not transferred to the later gameplay instance.
    /// </summary>
    protected virtual void OnConstruct(UnrealConstructionContext context) { }

    internal void RunConstruction(UnrealConstructionContext context)
    {
        if (_constructionContext is not null || IsRunning)
            throw new InvalidOperationException("A construction entry cannot be nested on the same script.");
        _constructionContext = context;
        ActivateReflectedPropertyAccess();
        try
        {
            MethodInfo? hook = GetType().GetMethod("OnConstruct", BindingFlags.NonPublic | BindingFlags.Instance,
                null, [typeof(UnrealConstructionContext)], null);
            if (hook?.IsDefined(typeof(AsyncStateMachineAttribute), false) == true)
                throw new InvalidOperationException("OnConstruct must be synchronous; async void construction is unsupported.");
            OnConstruct(context);
        }
        finally { _constructionContext = null; context.Dispose(); }
    }
}

public static partial class UnrealWorldManagedEntryPoint
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeConstructionOperations
    {
        public uint Version;
        public uint Size;
        public nint Execute;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int HasConstruction(byte* program, int programLength, byte* type, int typeLength,
        byte* error, int errorCapacity)
    {
        try
        {
            string path = ConstructionText(program, programLength);
            string typeName = ConstructionText(type, typeLength);
            var loader = new ScriptAssemblyLoadContext(0, 0, 0, path);
            try
            {
                Assembly assembly = loader.LoadMainAssembly();
                loader.ValidateEntryType(assembly, typeName);
                Type scriptType = assembly.GetType(typeName, throwOnError: true)!;
                return typeof(UnrealObjectScript).IsAssignableFrom(scriptType) &&
                    scriptType.GetMethod("OnConstruct", BindingFlags.NonPublic | BindingFlags.Instance,
                        null, [typeof(UnrealConstructionContext)], null)?.DeclaringType != typeof(UnrealObjectScript) ? 1 : 0;
            }
            finally { RetireLoadContext(loader); }
        }
        catch (Exception exception) { ConstructionError(error, errorCapacity, exception); return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int ConstructScript(nint nativeContext, nint transportTable, nint operations,
        ulong host, int isClassDefaultObject, byte* program, int programLength, byte* type, int typeLength,
        byte* error, int errorCapacity)
    {
        try
        {
            if (operations == 0 || nativeContext == 0 || transportTable == 0)
                throw new ArgumentException("Construction transport is missing.");
            var table = Marshal.PtrToStructure<NativeConstructionOperations>(operations);
            if (table.Version != 1 || table.Size < sizeof(NativeConstructionOperations) || table.Execute == 0)
                throw new ArgumentException("Construction transport ABI mismatch.");
            string path = ConstructionText(program, programLength);
            string typeName = ConstructionText(type, typeLength);
            var loader = new ScriptAssemblyLoadContext(0, 0, 0, path);
            try
            {
                Assembly assembly = loader.LoadMainAssembly();
                loader.ValidateEntryType(assembly, typeName);
                var script = Activator.CreateInstance(assembly.GetType(typeName, throwOnError: true)!) as UnrealObjectScript
                    ?? throw new InvalidOperationException("Native construction requires UnrealObjectScript.");
                using var transport = new InProcessUnrealNativeInvocationTransport(nativeContext, transportTable);
                var bridge = new NativeUnrealInvocationBridge(transport);
                using var context = new UnrealConstructionContext(bridge, new(host), isClassDefaultObject != 0,
                    (op, target, other, name, classPath) => ExecuteConstruction(table.Execute, nativeContext, op, target, other, name, classPath));
                script.RunConstruction(context);
                return 0;
            }
            finally { RetireLoadContext(loader); }
        }
        catch (Exception exception) { ConstructionError(error, errorCapacity, exception); return 1; }
    }

    private static unsafe UnrealObjectHandle ExecuteConstruction(nint execute, nint context, int op,
        UnrealObjectHandle target, UnrealObjectHandle other, string name, string classPath)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name), pathBytes = Encoding.UTF8.GetBytes(classPath);
        byte* error = stackalloc byte[4096];
        error[0] = 0;
        ulong result = 0;
        fixed (byte* nameData = nameBytes, pathData = pathBytes)
        {
            var entry = (delegate* unmanaged[Cdecl]<nint, int, ulong, ulong, byte*, int, byte*, int, ulong*, byte*, int, int>)execute;
            if (entry(context, op, target.Value, other.Value, nameData, nameBytes.Length, pathData, pathBytes.Length, &result, error, 4096) != 0)
                throw new InvalidOperationException(Marshal.PtrToStringUTF8((nint)error) ?? "Native construction failed.");
        }
        return new(result);
    }

    private static unsafe string ConstructionText(byte* text, int length)
    {
        if (text == null || length <= 0) throw new ArgumentException("Construction program and type are required.");
        return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(text, length));
    }
    private static unsafe void ConstructionError(byte* error, int capacity, Exception exception)
    {
        if (error == null || capacity <= 0) return;
        byte[] text = Encoding.UTF8.GetBytes(exception.GetBaseException().Message);
        int count = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, count).CopyTo(new Span<byte>(error, count));
        error[count] = 0;
    }
}
