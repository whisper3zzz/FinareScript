using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;

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
    UnrealObjectHandle Host,
    UnrealObjectHandle Target,
    ulong InstanceId,
    int Generation)
{
    public bool HasDistinctTarget => Host != Target;

    public UnrealWorldDataPlane DataPlane =>
        Bridge is IUnrealWorldDataPlaneProvider provider
            ? provider.DataPlane
            : UnrealWorldDataPlane.Unavailable;
}

public interface IUnrealManagedScript
{
    void OnStart(UnrealScriptContext context);

    void OnTick(float deltaSeconds);

    void OnStop();
}

public static class UnrealWorldManagedEntryPoint
{
    private static readonly ConcurrentDictionary<ulong, WorldEntry> Worlds = new();
    private static readonly ConcurrentDictionary<ulong, string> BoundaryErrors = new();
    private static readonly object LifecycleDiagnosticsGate = new();
    private static readonly List<WeakReference> RetiredLoadContexts = [];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateWorld(ulong worldId, nint context, nint transportTable)
    {
        ClearBoundaryFailure(worldId);
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
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, 0, nameof(CreateWorld), exception);
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
        ClearBoundaryFailure(worldId);
        if (!Worlds.TryRemove(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }

        try
        {
            entry.Dispose();
            return (int)UnrealWorldManagedEntryStatus.Success;
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, 0, nameof(DestroyWorld), exception);
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetWorldCount() => Worlds.Count;

    /// <summary>
    /// Forces a diagnostic collection and returns retired collectible script
    /// contexts that remain alive. Production flow never depends on this call;
    /// lifecycle soak tests use it to turn unload intent into observable proof.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int CollectRetiredLoadContexts()
    {
        for (int pass = 0; pass < 3; ++pass)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();

        lock (LifecycleDiagnosticsGate)
        {
            RetiredLoadContexts.RemoveAll(static reference => !reference.IsAlive);
            return RetiredLoadContexts.Count;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateScript(
        ulong worldId,
        ulong instanceId,
        ulong host,
        ulong target,
        nint assemblyPathData,
        int assemblyPathLength,
        nint typeNameData,
        int typeNameLength)
    {
        ClearBoundaryFailure(worldId);
        if (instanceId == 0 || host == 0 || target == 0)
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
                new UnrealObjectHandle(host),
                new UnrealObjectHandle(target),
                ReadUtf8(assemblyPathData, assemblyPathLength),
                ReadUtf8(typeNameData, typeNameLength));
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, instanceId, nameof(CreateScript), exception);
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int TickScript(ulong worldId, ulong instanceId, float deltaSeconds)
    {
        ClearBoundaryFailure(worldId);
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            return (int)entry.TickScript(instanceId, deltaSeconds);
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, instanceId, nameof(TickScript), exception);
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int DispatchEvent(
        ulong worldId,
        ulong instanceId,
        int eventKind,
        ulong source,
        ulong argument)
    {
        ClearBoundaryFailure(worldId);
        if (instanceId == 0 || source == 0 ||
            !Enum.IsDefined((UnrealScriptEventKind)eventKind))
        {
            return (int)UnrealWorldManagedEntryStatus.InvalidArgument;
        }
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            return (int)entry.DispatchEvent(
                instanceId,
                new UnrealScriptEvent(
                    (UnrealScriptEventKind)eventKind,
                    new UnrealObjectHandle(source),
                    new UnrealObjectHandle(argument)));
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, instanceId, nameof(DispatchEvent), exception);
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
        ClearBoundaryFailure(worldId);
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
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, instanceId, nameof(ReloadScript), exception);
            return -(int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int DestroyScript(ulong worldId, ulong instanceId)
    {
        ClearBoundaryFailure(worldId);
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            return (int)entry.DestroyScript(instanceId);
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, instanceId, nameof(DestroyScript), exception);
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetScriptCount(ulong worldId)
        => Worlds.TryGetValue(worldId, out WorldEntry? entry)
            ? entry.ScriptCount
            : -(int)UnrealWorldManagedEntryStatus.WorldNotFound;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetInvocationMetrics(
        ulong worldId,
        nint callFrameInvocationCount,
        nint legacyInvocationCount)
    {
        ClearBoundaryFailure(worldId);
        if (callFrameInvocationCount == nint.Zero || legacyInvocationCount == nint.Zero)
        {
            return (int)UnrealWorldManagedEntryStatus.InvalidArgument;
        }
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            entry.GetInvocationMetrics(out long callFrame, out long legacy);
            Marshal.WriteInt64(callFrameInvocationCount, callFrame);
            Marshal.WriteInt64(legacyInvocationCount, legacy);
            return (int)UnrealWorldManagedEntryStatus.Success;
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, 0, nameof(GetInvocationMetrics), exception);
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int GetLastError(
        ulong worldId,
        nint buffer,
        int bufferCapacity)
    {
        if (worldId == 0 || bufferCapacity < 0 ||
            !BoundaryErrors.TryGetValue(worldId, out string? message))
        {
            return 0;
        }

        int requiredBytes = Encoding.UTF8.GetByteCount(message);
        if (buffer == nint.Zero || bufferCapacity <= requiredBytes)
        {
            return requiredBytes;
        }

        var destination = new Span<byte>((void*)buffer, bufferCapacity);
        int written = Encoding.UTF8.GetBytes(message, destination);
        destination[written] = 0;
        BoundaryErrors.TryRemove(worldId, out _);
        return written;
    }

    private static string ReadUtf8(nint data, int length)
    {
        if (length <= 0 || data == nint.Zero)
        {
            throw new ArgumentException("A non-empty UTF-8 slice is required.");
        }
        return Marshal.PtrToStringUTF8(data, length) ??
            throw new InvalidOperationException("The UTF-8 slice could not be decoded.");
    }

    private static void ClearBoundaryFailure(ulong worldId)
    {
        if (worldId != 0)
        {
            BoundaryErrors.TryRemove(worldId, out _);
        }
    }

    private static void ReportBoundaryFailure(
        ulong worldId,
        ulong instanceId,
        string operation,
        Exception exception)
    {
        UnrealRuntimeDiagnosticContext? diagnosticContext = null;
        if (worldId != 0 && instanceId != 0 &&
            Worlds.TryGetValue(worldId, out WorldEntry? world))
        {
            diagnosticContext = world.TryGetDiagnosticContext(instanceId);
        }
        string detail = UnrealRuntimeDiagnosticBuilder.CreateJson(
            worldId,
            instanceId,
            operation,
            exception,
            diagnosticContext);
        if (worldId != 0)
        {
            BoundaryErrors[worldId] = detail;
        }
        string message = $"[{DateTimeOffset.UtcNow:O}] {detail}{Environment.NewLine}";
        Console.Error.WriteLine(message);
        string? diagnosticPath = Environment.GetEnvironmentVariable(
            "UNIFIEDSCRIPT_MANAGED_DIAGNOSTIC_PATH");
        if (string.IsNullOrWhiteSpace(diagnosticPath))
        {
            return;
        }
        try
        {
            File.AppendAllText(Path.GetFullPath(diagnosticPath), message);
        }
        catch
        {
            // Boundary reporting must never replace the original host status.
        }
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
            Bridge = new NativeUnrealInvocationBridge(_transport, worldId);
        }

        public NativeUnrealInvocationBridge Bridge { get; }

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

        public void GetInvocationMetrics(out long callFrame, out long legacy)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                IUnrealInvocationMetrics metrics = Bridge;
                callFrame = metrics.CallFrameInvocationCount;
                legacy = metrics.LegacyInvocationCount;
            }
        }

        public UnrealRuntimeDiagnosticContext? TryGetDiagnosticContext(ulong instanceId)
        {
            lock (_gate)
            {
                return _scripts.TryGetValue(instanceId, out ManagedScriptInstance? script)
                    ? script.GetDiagnosticContext()
                    : null;
            }
        }

        public UnrealWorldManagedEntryStatus CreateScript(
            ulong instanceId,
            UnrealObjectHandle host,
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
                    host,
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

        public UnrealWorldManagedEntryStatus DispatchEvent(
            ulong instanceId,
            UnrealScriptEvent scriptEvent)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_scripts.TryGetValue(instanceId, out ManagedScriptInstance? script))
                {
                    return UnrealWorldManagedEntryStatus.ScriptNotFound;
                }
                script.DispatchEvent(scriptEvent);
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
        private readonly UnrealObjectHandle _host;
        private readonly UnrealObjectHandle _target;
        private string _assemblyPath;
        private string _typeName;
        private ScriptGeneration _current;
        private bool _started;

        private ManagedScriptInstance(
            ulong worldId,
            ulong instanceId,
            IUnrealInvocationBridge bridge,
            UnrealObjectHandle host,
            UnrealObjectHandle target,
            string assemblyPath,
            string typeName)
        {
            _worldId = worldId;
            _instanceId = instanceId;
            _bridge = bridge;
            _host = host;
            _target = target;
            _assemblyPath = Path.GetFullPath(assemblyPath);
            _typeName = typeName;
            _current = LoadGeneration(1, _assemblyPath, _typeName);
        }

        public static ManagedScriptInstance Create(
            ulong worldId,
            ulong instanceId,
            IUnrealInvocationBridge bridge,
            UnrealObjectHandle host,
            UnrealObjectHandle target,
            string assemblyPath,
            string typeName)
            => new(worldId, instanceId, bridge, host, target, assemblyPath, typeName);

        public void Start()
        {
            StartGeneration(_current);
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

        public void DispatchEvent(UnrealScriptEvent scriptEvent)
        {
            if (!_started)
            {
                throw new InvalidOperationException("The managed script is not started.");
            }
            if (_current.Script is IUnrealManagedEventSink sink)
            {
                sink.OnUnrealEvent(scriptEvent);
            }
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
                previous.Bridge.ClearSubscriptions();
                _started = false;
            }

            try
            {
                StartGeneration(next);
                _started = true;
            }
            catch
            {
                next.Dispose();
                StartGeneration(previous);
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

        private UnrealScriptContext CreateContext(ScriptGeneration generation)
            => new(generation.Bridge, _host, _target, _instanceId, generation.Generation);

        public UnrealRuntimeDiagnosticContext GetDiagnosticContext() => new(
            _current.Generation,
            _assemblyPath,
            _typeName,
            _host,
            _target);

        private void StartGeneration(ScriptGeneration generation)
        {
            UnrealScriptContext context = CreateContext(generation);
            generation.Script.OnStart(context);
            _ = UnrealEventBinder.BindAttributedEvents(generation.Script, context);
        }

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
                return new ScriptGeneration(
                    generation,
                    loadContext,
                    script,
                    new UnrealGenerationInvocationBridge(_bridge));
            }
            catch
            {
                RetireLoadContext(loadContext);
                throw;
            }
        }
    }

    private sealed record ScriptGeneration(
        int Generation,
        ScriptAssemblyLoadContext LoadContext,
        IUnrealManagedScript Script,
        UnrealGenerationInvocationBridge Bridge) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Bridge.Dispose();
            RetireLoadContext(LoadContext);
        }
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
            Assembly? sharedContract = new[]
                {
                    typeof(IUnrealManagedScript).Assembly,
                    typeof(global::UnifiedScript.VisualAttribute).Assembly,
                }
                .Distinct()
                .FirstOrDefault(candidate => AssemblyName.ReferenceMatchesDefinition(
                    candidate.GetName(),
                    assemblyName));
            if (sharedContract is not null)
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

    private static void RetireLoadContext(AssemblyLoadContext loadContext)
    {
        lock (LifecycleDiagnosticsGate)
        {
            RetiredLoadContexts.Add(new WeakReference(loadContext));
        }
        loadContext.Unload();
    }
}
