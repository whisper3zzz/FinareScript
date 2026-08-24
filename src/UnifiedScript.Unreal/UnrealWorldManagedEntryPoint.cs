using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace UnifiedScript.Unreal;

public enum UnrealWorldManagedEntryStatus
{
    Success = 0,
    InvalidArgument = 1,
    DuplicateWorld = 2,
    NativeContextUnavailable = 3,
    WorldNotFound = 4,
    InternalFailure = 5,
    DuplicateScript = 6,
    ScriptNotFound = 7,
    ScriptContractMismatch = 8,
}

public readonly record struct UnrealScriptContext(
    IUnrealInvocationBridge Bridge,
    UnrealObjectHandle Target,
    ulong InstanceId,
    int Generation);

public interface IUnrealManagedScript
{
    void OnStart(UnrealScriptContext context);

    void OnTick(float deltaSeconds);

    void OnStop();
}

public static class UnrealWorldManagedEntryPoint
{
    private static readonly ConcurrentDictionary<ulong, WorldEntry> Worlds = new();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateWorld(ulong worldId, nint context, nint transportTable)
    {
        if (worldId == 0 || context == nint.Zero || transportTable == nint.Zero)
        {
            return (int)UnrealWorldManagedEntryStatus.InvalidArgument;
        }

        WorldEntry entry;
        try
        {
            entry = new WorldEntry(worldId, context, transportTable);
        }
        catch (InvalidOperationException)
        {
            return (int)UnrealWorldManagedEntryStatus.NativeContextUnavailable;
        }
        catch
        {
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }

        if (Worlds.TryAdd(worldId, entry))
        {
            return (int)UnrealWorldManagedEntryStatus.Success;
        }

        entry.Dispose();
        return (int)UnrealWorldManagedEntryStatus.DuplicateWorld;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int DestroyWorld(ulong worldId)
    {
        if (!Worlds.TryRemove(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }

        try
        {
            entry.Dispose();
            return (int)UnrealWorldManagedEntryStatus.Success;
        }
        catch
        {
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetWorldCount() => Worlds.Count;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateScript(
        ulong worldId,
        ulong instanceId,
        ulong target,
        nint assemblyPathData,
        int assemblyPathLength,
        nint typeNameData,
        int typeNameLength)
    {
        if (instanceId == 0 || target == 0)
        {
            return (int)UnrealWorldManagedEntryStatus.InvalidArgument;
        }
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }

        try
        {
            return (int)entry.CreateScript(
                instanceId,
                new UnrealObjectHandle(target),
                ReadUtf8(assemblyPathData, assemblyPathLength),
                ReadUtf8(typeNameData, typeNameLength));
        }
        catch
        {
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int TickScript(ulong worldId, ulong instanceId, float deltaSeconds)
    {
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            return (int)entry.TickScript(instanceId, deltaSeconds);
        }
        catch
        {
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int ReloadScript(
        ulong worldId,
        ulong instanceId,
        nint assemblyPathData,
        int assemblyPathLength,
        nint typeNameData,
        int typeNameLength)
    {
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return -(int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            int generation = entry.ReloadScript(
                instanceId,
                ReadUtf8(assemblyPathData, assemblyPathLength),
                ReadUtf8(typeNameData, typeNameLength));
            return generation > 0
                ? generation
                : -(int)UnrealWorldManagedEntryStatus.ScriptNotFound;
        }
        catch
        {
            return -(int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int DestroyScript(ulong worldId, ulong instanceId)
    {
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            return (int)entry.DestroyScript(instanceId);
        }
        catch
        {
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetScriptCount(ulong worldId)
        => Worlds.TryGetValue(worldId, out WorldEntry? entry)
            ? entry.ScriptCount
            : -(int)UnrealWorldManagedEntryStatus.WorldNotFound;

    private static string ReadUtf8(nint data, int length)
    {
        if (length <= 0 || data == nint.Zero)
        {
            throw new ArgumentException("A non-empty UTF-8 slice is required.");
        }
        return Marshal.PtrToStringUTF8(data, length) ??
            throw new InvalidOperationException("The UTF-8 slice could not be decoded.");
    }

    private sealed class WorldEntry : IDisposable
    {
        private readonly object _gate = new();
        private readonly ulong _worldId;
        private readonly InProcessUnrealNativeInvocationTransport _transport;
        private readonly Dictionary<ulong, ManagedScriptInstance> _scripts = [];
        private bool _disposed;

        public WorldEntry(ulong worldId, nint context, nint transportTable)
        {
            _worldId = worldId;
            _transport = new InProcessUnrealNativeInvocationTransport(
                context,
                transportTable);
            Bridge = new NativeUnrealInvocationBridge(_transport);
        }

        public IUnrealInvocationBridge Bridge { get; }

        public int ScriptCount
        {
            get
            {
                lock (_gate)
                {
                    return _scripts.Count;
                }
            }
        }

        public UnrealWorldManagedEntryStatus CreateScript(
            ulong instanceId,
            UnrealObjectHandle target,
            string assemblyPath,
            string typeName)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_scripts.ContainsKey(instanceId))
                {
                    return UnrealWorldManagedEntryStatus.DuplicateScript;
                }

                ManagedScriptInstance script = ManagedScriptInstance.Create(
                    _worldId,
                    instanceId,
                    Bridge,
                    target,
                    assemblyPath,
                    typeName);
                try
                {
                    script.Start();
                }
                catch
                {
                    script.Dispose();
                    throw;
                }
                _scripts.Add(instanceId, script);
                return UnrealWorldManagedEntryStatus.Success;
            }
        }

        public UnrealWorldManagedEntryStatus TickScript(
            ulong instanceId,
            float deltaSeconds)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_scripts.TryGetValue(instanceId, out ManagedScriptInstance? script))
                {
                    return UnrealWorldManagedEntryStatus.ScriptNotFound;
                }
                script.Tick(deltaSeconds);
                return UnrealWorldManagedEntryStatus.Success;
            }
        }

        public int ReloadScript(
            ulong instanceId,
            string assemblyPath,
            string typeName)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _scripts.TryGetValue(instanceId, out ManagedScriptInstance? script)
                    ? script.Reload(assemblyPath, typeName)
                    : 0;
            }
        }

        public UnrealWorldManagedEntryStatus DestroyScript(ulong instanceId)
        {
            lock (_gate)
            {
                if (!_scripts.Remove(instanceId, out ManagedScriptInstance? script))
                {
                    return UnrealWorldManagedEntryStatus.ScriptNotFound;
                }
                script.Dispose();
                return UnrealWorldManagedEntryStatus.Success;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                List<Exception>? failures = null;
                foreach (ManagedScriptInstance script in _scripts.Values)
                {
                    try
                    {
                        script.Dispose();
                    }
                    catch (Exception exception)
                    {
                        (failures ??= []).Add(exception);
                    }
                }
                _scripts.Clear();
                _transport.Dispose();
                if (failures is not null)
                {
                    throw new AggregateException(
                        "One or more managed scripts failed during World teardown.",
                        failures);
                }
            }
        }
    }

    private sealed class ManagedScriptInstance : IDisposable
    {
        private readonly ulong _worldId;
        private readonly ulong _instanceId;
        private readonly IUnrealInvocationBridge _bridge;
        private readonly UnrealObjectHandle _target;
        private string _assemblyPath;
        private string _typeName;
        private ScriptGeneration _current;
        private bool _started;

        private ManagedScriptInstance(
            ulong worldId,
            ulong instanceId,
            IUnrealInvocationBridge bridge,
            UnrealObjectHandle target,
            string assemblyPath,
            string typeName)
        {
            _worldId = worldId;
            _instanceId = instanceId;
            _bridge = bridge;
            _target = target;
            _assemblyPath = Path.GetFullPath(assemblyPath);
            _typeName = typeName;
            _current = LoadGeneration(1, _assemblyPath, _typeName);
        }

        public static ManagedScriptInstance Create(
            ulong worldId,
            ulong instanceId,
            IUnrealInvocationBridge bridge,
            UnrealObjectHandle target,
            string assemblyPath,
            string typeName)
            => new(worldId, instanceId, bridge, target, assemblyPath, typeName);

        public void Start()
        {
            _current.Script.OnStart(CreateContext(_current.Generation));
            _started = true;
        }

        public void Tick(float deltaSeconds)
        {
            if (!_started)
            {
                throw new InvalidOperationException("The managed script is not started.");
            }
            _current.Script.OnTick(deltaSeconds);
        }

        public int Reload(string assemblyPath, string typeName)
        {
            string nextAssemblyPath = Path.GetFullPath(assemblyPath);
            ScriptGeneration next = LoadGeneration(
                checked(_current.Generation + 1),
                nextAssemblyPath,
                typeName);
            ScriptGeneration previous = _current;
            if (_started)
            {
                previous.Script.OnStop();
                _started = false;
            }

            try
            {
                next.Script.OnStart(CreateContext(next.Generation));
                _started = true;
            }
            catch
            {
                next.Dispose();
                previous.Script.OnStart(CreateContext(previous.Generation));
                _started = true;
                throw;
            }

            _current = next;
            _assemblyPath = nextAssemblyPath;
            _typeName = typeName;
            previous.Dispose();
            return next.Generation;
        }

        public void Dispose()
        {
            try
            {
                if (_started)
                {
                    _current.Script.OnStop();
                    _started = false;
                }
            }
            finally
            {
                _current.Dispose();
            }
        }

        private UnrealScriptContext CreateContext(int generation)
            => new(_bridge, _target, _instanceId, generation);

        private ScriptGeneration LoadGeneration(
            int generation,
            string assemblyPath,
            string typeName)
        {
            var loadContext = new ScriptAssemblyLoadContext(
                _worldId,
                _instanceId,
                generation,
                assemblyPath);
            try
            {
                Assembly assembly = loadContext.LoadMainAssembly();
                Type type = assembly.GetType(typeName, throwOnError: true)!;
                if (Activator.CreateInstance(type) is not IUnrealManagedScript script)
                {
                    throw new InvalidOperationException(
                        $"Managed script type '{typeName}' does not implement {nameof(IUnrealManagedScript)}.");
                }
                return new ScriptGeneration(generation, loadContext, script);
            }
            catch
            {
                loadContext.Unload();
                throw;
            }
        }
    }

    private sealed record ScriptGeneration(
        int Generation,
        ScriptAssemblyLoadContext LoadContext,
        IUnrealManagedScript Script) : IDisposable
    {
        public void Dispose() => LoadContext.Unload();
    }

    private sealed class ScriptAssemblyLoadContext(
        ulong worldId,
        ulong instanceId,
        int generation,
        string assemblyPath)
        : AssemblyLoadContext(
            $"UnifiedScript.Unreal.World.{worldId}.Script.{instanceId}.Generation.{generation}",
            isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);
        private readonly string _assemblyPath = assemblyPath;

        public Assembly LoadMainAssembly() => LoadAssemblyFile(_assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            Assembly sharedContract = typeof(IUnrealManagedScript).Assembly;
            if (AssemblyName.ReferenceMatchesDefinition(
                    sharedContract.GetName(),
                    assemblyName))
            {
                return sharedContract;
            }

            string? dependencyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return dependencyPath is null ? null : LoadAssemblyFile(dependencyPath);
        }

        private Assembly LoadAssemblyFile(string path)
        {
            using FileStream assemblyStream = OpenSharedRead(path);
            string pdbPath = Path.ChangeExtension(path, ".pdb");
            if (!File.Exists(pdbPath))
            {
                return LoadFromStream(assemblyStream);
            }

            using FileStream pdbStream = OpenSharedRead(pdbPath);
            return LoadFromStream(assemblyStream, pdbStream);
        }

        private static FileStream OpenSharedRead(string path)
            => new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
    }
}
