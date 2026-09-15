using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Finare.Core;
using Finare.Runtime;

namespace Finare.Unreal;

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
    DebugNotConfigured = 9,
    DebugExecutionBusy = 10,
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

internal sealed record UnrealManagedReloadPhase(
    string Name,
    string State,
    long DurationMicroseconds,
    string Detail);

internal sealed record UnrealManagedReloadTransaction(
    string SchemaVersion,
    string State,
    int FromGeneration,
    int CandidateGeneration,
    int ActiveGeneration,
    IReadOnlyList<UnrealManagedReloadPhase> Phases,
    string? Failure,
    CoreClrGenerationPrewarmSnapshot? Prewarm,
    CoreClrProgramLoaderPrewarmSnapshot? ProgramLoaderPrewarm);

public static partial class UnrealWorldManagedEntryPoint
{
    private static readonly ConcurrentDictionary<ulong, WorldEntry> Worlds = new();
    private static readonly ConcurrentDictionary<ulong, string> BoundaryErrors = new();
    private static readonly object LifecycleDiagnosticsGate = new();
    private static readonly List<WeakReference> RetiredLoadContexts = [];
    private static readonly FinareManagedDependencyCache DependencyCache =
        FinareManagedDependencyCache.Shared;
    private static readonly CoreClrProgramLoaderPrewarmOperation ProgramLoaderPrewarm =
        CoreClrProgramLoaderPrewarmOperation.Create(
            Path.GetDirectoryName(typeof(UnrealWorldManagedEntryPoint).Assembly.Location) ??
                Environment.CurrentDirectory,
            DependencyCache,
            ResolveProgramLoaderPrewarmOptions());
    private static readonly bool TraceProgramLoadPhases = string.Equals(
        Environment.GetEnvironmentVariable("FINARE_TRACE_PROGRAM_LOAD_PHASES"),
        "1",
        StringComparison.Ordinal);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateWorld(ulong worldId, nint context, nint transportTable)
    {
        _ = ProgramLoaderPrewarm.Start();
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
    public static int CollectRetiredLoadContexts() => CollectRetiredLoadContextsCore();

    internal static int CollectRetiredLoadContextsForTests() => CollectRetiredLoadContextsCore();

    private static int CollectRetiredLoadContextsCore()
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
                ReadUtf8(typeNameData, typeNameLength),
                UnrealBlueprintOverrideActivationContract.Empty);
        }
        catch (UnrealBlueprintOverrideActivationException exception)
        {
            ReportBoundaryFailure(worldId, instanceId, nameof(CreateScript), exception);
            return (int)UnrealWorldManagedEntryStatus.ScriptContractMismatch;
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, instanceId, nameof(CreateScript), exception);
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    /// <summary>
    /// Optional v0.1 creation boundary. Native generated Blueprint classes use
    /// this entry when they have a persisted inherited-event contract. The
    /// legacy CreateScript export remains available for old callers, but it
    /// passes an explicit empty contract and therefore cannot bypass validation.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateScriptWithBlueprintContract(
        ulong worldId,
        ulong instanceId,
        ulong host,
        ulong target,
        nint assemblyPathData,
        int assemblyPathLength,
        nint typeNameData,
        int typeNameLength,
        nint contractData,
        int contractLength)
    {
        ClearBoundaryFailure(worldId);
        if (instanceId == 0 || host == 0 || target == 0 ||
            contractData == nint.Zero ||
            contractLength <= 0 ||
            contractLength > UnrealBlueprintOverrideActivationContract.MaximumUtf8Bytes)
        {
            return (int)UnrealWorldManagedEntryStatus.InvalidArgument;
        }
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }

        try
        {
            UnrealBlueprintOverrideActivationContract contract =
                UnrealBlueprintOverrideActivationContract.Parse(
                    ReadUtf8(contractData, contractLength));
            return (int)entry.CreateScript(
                instanceId,
                new UnrealObjectHandle(host),
                new UnrealObjectHandle(target),
                ReadUtf8(assemblyPathData, assemblyPathLength),
                ReadUtf8(typeNameData, typeNameLength),
                contract);
        }
        catch (UnrealBlueprintOverrideActivationException exception)
        {
            ReportBoundaryFailure(
                worldId,
                instanceId,
                nameof(CreateScriptWithBlueprintContract),
                exception);
            return (int)UnrealWorldManagedEntryStatus.ScriptContractMismatch;
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(
                worldId,
                instanceId,
                nameof(CreateScriptWithBlueprintContract),
                exception);
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
    public static int ConfigureScriptDebug(
        ulong worldId,
        ulong instanceId,
        nint configurationData,
        int configurationLength)
    {
        ClearBoundaryFailure(worldId);
        if (instanceId == 0 || configurationData == nint.Zero || configurationLength <= 0)
        {
            return (int)UnrealWorldManagedEntryStatus.InvalidArgument;
        }
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            return (int)entry.ConfigureScriptDebug(
                instanceId,
                ReadUtf8(configurationData, configurationLength));
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, instanceId, nameof(ConfigureScriptDebug), exception);
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BeginScriptDebugTick(
        ulong worldId,
        ulong instanceId,
        float deltaSeconds)
    {
        ClearBoundaryFailure(worldId);
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            return (int)entry.BeginScriptDebugTick(instanceId, deltaSeconds);
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, instanceId, nameof(BeginScriptDebugTick), exception);
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int GetScriptDebugState(
        ulong worldId,
        ulong instanceId,
        nint buffer,
        int bufferCapacity)
    {
        if (worldId == 0 || instanceId == 0 || bufferCapacity < 0 ||
            !Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return 0;
        }
        try
        {
            string? json = entry.GetScriptDebugState(instanceId);
            if (string.IsNullOrEmpty(json))
            {
                return 0;
            }
            int requiredBytes = Encoding.UTF8.GetByteCount(json);
            if (buffer == nint.Zero || bufferCapacity <= requiredBytes)
            {
                return requiredBytes;
            }
            var destination = new Span<byte>((void*)buffer, bufferCapacity);
            int written = Encoding.UTF8.GetBytes(json, destination);
            destination[written] = 0;
            return written;
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, instanceId, nameof(GetScriptDebugState), exception);
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int GetScriptReloadState(
        ulong worldId,
        ulong instanceId,
        nint buffer,
        int bufferCapacity)
    {
        if (worldId == 0 || instanceId == 0 || bufferCapacity < 0 ||
            !Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return 0;
        }
        try
        {
            string? json = entry.GetScriptReloadState(instanceId);
            if (string.IsNullOrEmpty(json))
            {
                return 0;
            }
            int requiredBytes = Encoding.UTF8.GetByteCount(json);
            if (buffer == nint.Zero || bufferCapacity <= requiredBytes)
            {
                return requiredBytes;
            }
            var destination = new Span<byte>((void*)buffer, bufferCapacity);
            int written = Encoding.UTF8.GetBytes(json, destination);
            destination[written] = 0;
            return written;
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, instanceId, nameof(GetScriptReloadState), exception);
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int SetScriptProfilerEnabled(
        ulong worldId,
        ulong instanceId,
        int enabled)
    {
        ClearBoundaryFailure(worldId);
        if (worldId == 0 || instanceId == 0 || enabled is < 0 or > 1 ||
            !Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.InvalidArgument;
        }
        try
        {
            return (int)entry.SetScriptProfilerEnabled(instanceId, enabled != 0);
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(
                worldId,
                instanceId,
                nameof(SetScriptProfilerEnabled),
                exception);
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int GetScriptProfilerTrace(
        ulong worldId,
        ulong instanceId,
        nint buffer,
        int bufferCapacity)
    {
        if (worldId == 0 || instanceId == 0 || bufferCapacity < 0 ||
            !Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return 0;
        }
        try
        {
            string? json = entry.GetScriptProfilerTrace(instanceId);
            if (string.IsNullOrEmpty(json))
            {
                return 0;
            }
            int requiredBytes = Encoding.UTF8.GetByteCount(json);
            if (buffer == nint.Zero || bufferCapacity <= requiredBytes)
            {
                return requiredBytes;
            }
            var destination = new Span<byte>((void*)buffer, bufferCapacity);
            int written = Encoding.UTF8.GetBytes(json, destination);
            destination[written] = 0;
            return written;
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(
                worldId,
                instanceId,
                nameof(GetScriptProfilerTrace),
                exception);
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int ControlScriptDebug(
        ulong worldId,
        ulong instanceId,
        int control,
        nint pauseIdData,
        int pauseIdLength)
    {
        ClearBoundaryFailure(worldId);
        if (instanceId == 0 || !Enum.IsDefined((UnrealWorldDebugControl)control) ||
            (control != (int)UnrealWorldDebugControl.Pause &&
             (pauseIdData == nint.Zero || pauseIdLength <= 0)))
        {
            return (int)UnrealWorldManagedEntryStatus.InvalidArgument;
        }
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            return (int)entry.ControlScriptDebug(
                instanceId,
                (UnrealWorldDebugControl)control,
                pauseIdLength > 0 ? ReadUtf8(pauseIdData, pauseIdLength) : null);
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, instanceId, nameof(ControlScriptDebug), exception);
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int DisableScriptDebug(ulong worldId, ulong instanceId)
    {
        ClearBoundaryFailure(worldId);
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            return (int)entry.DisableScriptDebug(instanceId);
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, instanceId, nameof(DisableScriptDebug), exception);
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int IsScriptDebugActive(ulong worldId, ulong instanceId)
    {
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return -(int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        return entry.IsScriptDebugActive(instanceId);
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
    public static unsafe int InvokeScriptFunction(
        ulong worldId,
        ulong instanceId,
        nint functionNameData,
        int functionNameLength,
        nint arguments,
        int argumentCount,
        nint resultData)
    {
        ClearBoundaryFailure(worldId);
        if (instanceId == 0 || functionNameData == 0 ||
            functionNameLength is <= 0 or > 1024 ||
            argumentCount is < 0 or > UnrealManagedFunctionDispatcher.MaximumArgumentCount ||
            (argumentCount > 0 && arguments == 0) || resultData == 0)
        {
            return (int)UnrealWorldManagedEntryStatus.InvalidArgument;
        }
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            UnrealWorldManagedEntryStatus status = entry.InvokeFunction(
                instanceId,
                ReadUtf8(functionNameData, functionNameLength),
                arguments,
                argumentCount,
                out UnrealManagedFunctionValueV1 result);
            *(UnrealManagedFunctionValueV1*)resultData = result;
            return (int)status;
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, instanceId, nameof(InvokeScriptFunction), exception);
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int InvokeScriptFunctionV2(
        ulong worldId,
        ulong instanceId,
        nint assemblyPathData,
        int assemblyPathLength,
        nint typeNameData,
        int typeNameLength,
        nint functionNameData,
        int functionNameLength,
        nint frameAddress)
    {
        ClearBoundaryFailure(worldId);
        bool staticCall = instanceId == 0;
        if (functionNameData == 0 || functionNameLength is <= 0 or > 1024 ||
            frameAddress == 0 ||
            (staticCall && (assemblyPathData == 0 || assemblyPathLength is <= 0 or > 32768 ||
                typeNameData == 0 || typeNameLength is <= 0 or > 4096)))
        {
            return (int)UnrealWorldManagedEntryStatus.InvalidArgument;
        }
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            string functionName = ReadUtf8(functionNameData, functionNameLength);
            return (int)(staticCall
                ? entry.InvokeStaticFunctionV2(
                    ReadUtf8(assemblyPathData, assemblyPathLength),
                    ReadUtf8(typeNameData, typeNameLength),
                    functionName,
                    frameAddress)
                : entry.InvokeFunctionV2(
                    instanceId,
                    functionName,
                    frameAddress));
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, instanceId, nameof(InvokeScriptFunctionV2), exception);
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
    public static int PrepareScriptReload(
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
            int generation = entry.PrepareScriptReload(
                instanceId,
                ReadUtf8(assemblyPathData, assemblyPathLength),
                ReadUtf8(typeNameData, typeNameLength));
            return generation > 0
                ? generation
                : -(int)UnrealWorldManagedEntryStatus.ScriptNotFound;
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(
                worldId,
                instanceId,
                nameof(PrepareScriptReload),
                exception);
            return -(int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int CommitScriptReload(
        ulong worldId,
        ulong instanceId,
        int candidateGeneration)
    {
        ClearBoundaryFailure(worldId);
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return -(int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            int generation = entry.CommitScriptReload(instanceId, candidateGeneration);
            return generation > 0
                ? generation
                : -(int)UnrealWorldManagedEntryStatus.ScriptNotFound;
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(
                worldId,
                instanceId,
                nameof(CommitScriptReload),
                exception);
            return -(int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int AbortScriptReload(
        ulong worldId,
        ulong instanceId,
        int candidateGeneration)
    {
        ClearBoundaryFailure(worldId);
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            return (int)entry.AbortScriptReload(instanceId, candidateGeneration);
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(
                worldId,
                instanceId,
                nameof(AbortScriptReload),
                exception);
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int FinalizeScriptReload(
        ulong worldId,
        ulong instanceId,
        int candidateGeneration)
    {
        ClearBoundaryFailure(worldId);
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            return (int)entry.FinalizeScriptReload(instanceId, candidateGeneration);
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(
                worldId,
                instanceId,
                nameof(FinalizeScriptReload),
                exception);
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int PrepareStaticLibraryReload(
        ulong worldId,
        nint assemblyPathData,
        int assemblyPathLength,
        nint typeNameData,
        int typeNameLength)
    {
        ClearBoundaryFailure(worldId);
        if (assemblyPathData == 0 || assemblyPathLength is <= 0 or > 32768 ||
            typeNameData == 0 || typeNameLength is <= 0 or > 4096)
        {
            return -(int)UnrealWorldManagedEntryStatus.InvalidArgument;
        }
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return -(int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            return entry.PrepareStaticLibraryReload(
                ReadUtf8(assemblyPathData, assemblyPathLength),
                ReadUtf8(typeNameData, typeNameLength));
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, 0, nameof(PrepareStaticLibraryReload), exception);
            return -(int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int CommitStaticLibraryReload(
        ulong worldId,
        nint assemblyPathData,
        int assemblyPathLength,
        nint typeNameData,
        int typeNameLength,
        int candidateGeneration)
    {
        ClearBoundaryFailure(worldId);
        if (assemblyPathData == 0 || assemblyPathLength is <= 0 or > 32768 ||
            typeNameData == 0 || typeNameLength is <= 0 or > 4096 || candidateGeneration <= 0)
        {
            return -(int)UnrealWorldManagedEntryStatus.InvalidArgument;
        }
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return -(int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            return entry.CommitStaticLibraryReload(
                ReadUtf8(assemblyPathData, assemblyPathLength),
                ReadUtf8(typeNameData, typeNameLength),
                candidateGeneration);
        }
        catch (KeyNotFoundException exception)
        {
            ReportBoundaryFailure(worldId, 0, nameof(CommitStaticLibraryReload), exception);
            return -(int)UnrealWorldManagedEntryStatus.ScriptNotFound;
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, 0, nameof(CommitStaticLibraryReload), exception);
            return -(int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int AbortStaticLibraryReload(
        ulong worldId,
        nint assemblyPathData,
        int assemblyPathLength,
        nint typeNameData,
        int typeNameLength,
        int candidateGeneration)
    {
        ClearBoundaryFailure(worldId);
        if (assemblyPathData == 0 || assemblyPathLength is <= 0 or > 32768 ||
            typeNameData == 0 || typeNameLength is <= 0 or > 4096 || candidateGeneration <= 0)
        {
            return (int)UnrealWorldManagedEntryStatus.InvalidArgument;
        }
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            entry.AbortStaticLibraryReload(
                ReadUtf8(assemblyPathData, assemblyPathLength),
                ReadUtf8(typeNameData, typeNameLength),
                candidateGeneration);
            return (int)UnrealWorldManagedEntryStatus.Success;
        }
        catch (KeyNotFoundException exception)
        {
            ReportBoundaryFailure(worldId, 0, nameof(AbortStaticLibraryReload), exception);
            return (int)UnrealWorldManagedEntryStatus.ScriptNotFound;
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, 0, nameof(AbortStaticLibraryReload), exception);
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int FinalizeStaticLibraryReload(
        ulong worldId,
        nint assemblyPathData,
        int assemblyPathLength,
        nint typeNameData,
        int typeNameLength,
        int candidateGeneration)
    {
        ClearBoundaryFailure(worldId);
        if (assemblyPathData == 0 || assemblyPathLength is <= 0 or > 32768 ||
            typeNameData == 0 || typeNameLength is <= 0 or > 4096 || candidateGeneration <= 0)
        {
            return (int)UnrealWorldManagedEntryStatus.InvalidArgument;
        }
        if (!Worlds.TryGetValue(worldId, out WorldEntry? entry))
        {
            return (int)UnrealWorldManagedEntryStatus.WorldNotFound;
        }
        try
        {
            entry.FinalizeStaticLibraryReload(
                ReadUtf8(assemblyPathData, assemblyPathLength),
                ReadUtf8(typeNameData, typeNameLength),
                candidateGeneration);
            return (int)UnrealWorldManagedEntryStatus.Success;
        }
        catch (KeyNotFoundException exception)
        {
            ReportBoundaryFailure(worldId, 0, nameof(FinalizeStaticLibraryReload), exception);
            return (int)UnrealWorldManagedEntryStatus.ScriptNotFound;
        }
        catch (Exception exception)
        {
            ReportBoundaryFailure(worldId, 0, nameof(FinalizeStaticLibraryReload), exception);
            return (int)UnrealWorldManagedEntryStatus.InternalFailure;
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
        _ = CaptureBoundaryFailure(worldId, instanceId, operation, exception);
    }

    private static string CaptureBoundaryFailure(
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
            "FINARE_MANAGED_DIAGNOSTIC_PATH");
        if (string.IsNullOrWhiteSpace(diagnosticPath))
        {
            return detail;
        }
        try
        {
            File.AppendAllText(Path.GetFullPath(diagnosticPath), message);
        }
        catch
        {
            // Boundary reporting must never replace the original host status.
        }
        return detail;
    }

    private sealed class WorldEntry : IDisposable
    {
        private readonly object _gate = new();
        private readonly ulong _worldId;
        private readonly InProcessUnrealNativeInvocationTransport _transport;
        private readonly Dictionary<ulong, ManagedScriptInstance> _scripts = [];
        private readonly Dictionary<string, ManagedStaticLibrary> _staticLibraries =
            new(StringComparer.Ordinal);
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
            string typeName,
            UnrealBlueprintOverrideActivationContract expectedBlueprintOverrideContract)
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
                    typeName,
                    expectedBlueprintOverrideContract);
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
                if (script.ConsumePendingEventFailure() is { } pendingEventFailure)
                {
                    ReportBoundaryFailure(
                        _worldId,
                        instanceId,
                        nameof(TickScript),
                        pendingEventFailure);
                    return UnrealWorldManagedEntryStatus.InternalFailure;
                }
                script.Tick(deltaSeconds);
                if (script.ConsumePendingEventFailure() is { } eventFailure)
                {
                    ReportBoundaryFailure(
                        _worldId,
                        instanceId,
                        nameof(TickScript),
                        eventFailure);
                    return UnrealWorldManagedEntryStatus.InternalFailure;
                }
                return UnrealWorldManagedEntryStatus.Success;
            }
        }

        public UnrealWorldManagedEntryStatus ConfigureScriptDebug(
            ulong instanceId,
            string configurationJson)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_scripts.TryGetValue(instanceId, out ManagedScriptInstance? script))
                {
                    return UnrealWorldManagedEntryStatus.ScriptNotFound;
                }
                script.ConfigureDebug(configurationJson);
                return UnrealWorldManagedEntryStatus.Success;
            }
        }

        public UnrealWorldManagedEntryStatus BeginScriptDebugTick(
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
                if (!script.IsDebugConfigured)
                {
                    return UnrealWorldManagedEntryStatus.DebugNotConfigured;
                }
                if (script.IsDebugActive)
                {
                    return UnrealWorldManagedEntryStatus.DebugExecutionBusy;
                }
                script.BeginDebugTick(deltaSeconds);
                return UnrealWorldManagedEntryStatus.Success;
            }
        }

        public string? GetScriptDebugState(ulong instanceId)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _scripts.TryGetValue(instanceId, out ManagedScriptInstance? script)
                    ? script.GetDebugState()
                    : null;
            }
        }

        public string? GetScriptReloadState(ulong instanceId)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _scripts.TryGetValue(instanceId, out ManagedScriptInstance? script)
                    ? script.GetReloadState()
                    : null;
            }
        }

        public UnrealWorldManagedEntryStatus SetScriptProfilerEnabled(
            ulong instanceId,
            bool enabled)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_scripts.TryGetValue(instanceId, out ManagedScriptInstance? script))
                {
                    return UnrealWorldManagedEntryStatus.ScriptNotFound;
                }
                script.SetProfilerEnabled(enabled);
                Bridge.SetProfilerEnabled(
                    _scripts.Values.Any(static candidate => candidate.IsProfilerEnabled));
                return UnrealWorldManagedEntryStatus.Success;
            }
        }

        public string? GetScriptProfilerTrace(ulong instanceId)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _scripts.TryGetValue(instanceId, out ManagedScriptInstance? script)
                    ? script.GetProfilerTrace()
                    : null;
            }
        }

        public UnrealWorldManagedEntryStatus ControlScriptDebug(
            ulong instanceId,
            UnrealWorldDebugControl control,
            string? pauseId)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_scripts.TryGetValue(instanceId, out ManagedScriptInstance? script))
                {
                    return UnrealWorldManagedEntryStatus.ScriptNotFound;
                }
                if (!script.IsDebugConfigured)
                {
                    return UnrealWorldManagedEntryStatus.DebugNotConfigured;
                }
                script.ControlDebug(control, pauseId);
                return UnrealWorldManagedEntryStatus.Success;
            }
        }

        public UnrealWorldManagedEntryStatus DisableScriptDebug(ulong instanceId)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_scripts.TryGetValue(instanceId, out ManagedScriptInstance? script))
                {
                    return UnrealWorldManagedEntryStatus.ScriptNotFound;
                }
                script.DisableDebug();
                return UnrealWorldManagedEntryStatus.Success;
            }
        }

        public int IsScriptDebugActive(ulong instanceId)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return !_scripts.TryGetValue(instanceId, out ManagedScriptInstance? script)
                    ? -(int)UnrealWorldManagedEntryStatus.ScriptNotFound
                    : script.IsDebugActive ? 1 : 0;
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

        internal UnrealWorldManagedEntryStatus InvokeFunction(
            ulong instanceId,
            string functionName,
            nint arguments,
            int argumentCount,
            out UnrealManagedFunctionValueV1 result)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                result = default;
                if (!_scripts.TryGetValue(instanceId, out ManagedScriptInstance? script))
                {
                    return UnrealWorldManagedEntryStatus.ScriptNotFound;
                }
                result = script.InvokeFunction(functionName, arguments, argumentCount);
                return UnrealWorldManagedEntryStatus.Success;
            }
        }

        internal UnrealWorldManagedEntryStatus InvokeFunctionV2(
            ulong instanceId,
            string functionName,
            nint frameAddress)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_scripts.TryGetValue(instanceId, out ManagedScriptInstance? script))
                {
                    return UnrealWorldManagedEntryStatus.ScriptNotFound;
                }
                script.InvokeFunctionV2(functionName, frameAddress);
                return UnrealWorldManagedEntryStatus.Success;
            }
        }

        internal UnrealWorldManagedEntryStatus InvokeStaticFunctionV2(
            string assemblyPath,
            string typeName,
            string functionName,
            nint frameAddress)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                string fullAssemblyPath = Path.GetFullPath(assemblyPath);
                string key = fullAssemblyPath + "\0" + typeName;
                if (!_staticLibraries.TryGetValue(key, out ManagedStaticLibrary? library))
                {
                    library = ManagedStaticLibrary.Create(
                        _worldId,
                        fullAssemblyPath,
                        typeName, Bridge);
                    _staticLibraries.Add(key, library);
                }
                library.Invoke(functionName, frameAddress);
                return UnrealWorldManagedEntryStatus.Success;
            }
        }

        internal int PrepareStaticLibraryReload(string assemblyPath, string typeName)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                string key = GetStaticLibraryKey(assemblyPath, typeName);
                return _staticLibraries.TryGetValue(key, out ManagedStaticLibrary? library)
                    ? library.PrepareReload()
                    : 0;
            }
        }

        internal int CommitStaticLibraryReload(
            string assemblyPath,
            string typeName,
            int candidateGeneration)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                string key = GetStaticLibraryKey(assemblyPath, typeName);
                return RequireStaticLibrary(key).CommitPreparedReload(candidateGeneration);
            }
        }

        internal void AbortStaticLibraryReload(
            string assemblyPath,
            string typeName,
            int candidateGeneration)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                string key = GetStaticLibraryKey(assemblyPath, typeName);
                RequireStaticLibrary(key).AbortPreparedReload(candidateGeneration);
            }
        }

        internal void FinalizeStaticLibraryReload(
            string assemblyPath,
            string typeName,
            int candidateGeneration)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                string key = GetStaticLibraryKey(assemblyPath, typeName);
                RequireStaticLibrary(key).FinalizePreparedReload(candidateGeneration);
            }
        }

        private static string GetStaticLibraryKey(string assemblyPath, string typeName) =>
            Path.GetFullPath(assemblyPath) + "\0" + typeName;

        private ManagedStaticLibrary RequireStaticLibrary(string key) =>
            _staticLibraries.TryGetValue(key, out ManagedStaticLibrary? library)
                ? library
                : throw new KeyNotFoundException(
                    "The managed static library cache slot is not loaded for this World.");

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

        public int PrepareScriptReload(
            ulong instanceId,
            string assemblyPath,
            string typeName)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _scripts.TryGetValue(instanceId, out ManagedScriptInstance? script)
                    ? script.PrepareReload(assemblyPath, typeName)
                    : 0;
            }
        }

        public int CommitScriptReload(ulong instanceId, int candidateGeneration)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _scripts.TryGetValue(instanceId, out ManagedScriptInstance? script)
                    ? script.CommitPreparedReload(candidateGeneration)
                    : 0;
            }
        }

        public UnrealWorldManagedEntryStatus AbortScriptReload(
            ulong instanceId,
            int candidateGeneration)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_scripts.TryGetValue(instanceId, out ManagedScriptInstance? script))
                {
                    return UnrealWorldManagedEntryStatus.ScriptNotFound;
                }
                script.AbortPreparedReload(candidateGeneration);
                return UnrealWorldManagedEntryStatus.Success;
            }
        }

        public UnrealWorldManagedEntryStatus FinalizeScriptReload(
            ulong instanceId,
            int candidateGeneration)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_scripts.TryGetValue(instanceId, out ManagedScriptInstance? script))
                {
                    return UnrealWorldManagedEntryStatus.ScriptNotFound;
                }
                script.FinalizePreparedReload(candidateGeneration);
                return UnrealWorldManagedEntryStatus.Success;
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
                Bridge.SetProfilerEnabled(
                    _scripts.Values.Any(static candidate => candidate.IsProfilerEnabled));
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
                foreach (ManagedStaticLibrary library in _staticLibraries.Values)
                {
                    try
                    {
                        library.Dispose();
                    }
                    catch (Exception exception)
                    {
                        (failures ??= []).Add(exception);
                    }
                }
                _staticLibraries.Clear();
                Bridge.SetProfilerEnabled(false);
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

    internal sealed class ManagedScriptInstance : IDisposable
    {
        private readonly ulong _worldId;
        private readonly ulong _instanceId;
        private readonly IUnrealInvocationBridge _bridge;
        private readonly UnrealObjectHandle _host;
        private readonly UnrealObjectHandle _target;
        private readonly UnrealBlueprintOverrideActivationContract? _expectedBlueprintOverrideContract;
        private string _assemblyPath;
        private string _typeName;
        private ScriptGeneration _current;
        private readonly UnrealWorldDebugController _debug;
        private readonly UnrealGameplayProfiler _profiler;
        private UnrealManagedReloadTransaction? _lastReloadTransaction;
        private CoreClrGenerationPrewarmOperation? _lastReloadPrewarm;
        private PreparedScriptReload? _preparedReload;
        private bool _started;

        private ManagedScriptInstance(
            ulong worldId,
            ulong instanceId,
            IUnrealInvocationBridge bridge,
            UnrealObjectHandle host,
            UnrealObjectHandle target,
            string assemblyPath,
            string typeName,
            UnrealBlueprintOverrideActivationContract? expectedBlueprintOverrideContract)
        {
            _worldId = worldId;
            _instanceId = instanceId;
            _bridge = bridge;
            _host = host;
            _target = target;
            _expectedBlueprintOverrideContract = expectedBlueprintOverrideContract;
            _assemblyPath = Path.GetFullPath(assemblyPath);
            _typeName = typeName;
            _current = LoadGeneration(1, _assemblyPath, _typeName);
            _debug = new UnrealWorldDebugController(worldId, instanceId);
            _profiler = new UnrealGameplayProfiler(
                worldId,
                instanceId,
                (NativeUnrealInvocationBridge)bridge);
        }

        public static ManagedScriptInstance Create(
            ulong worldId,
            ulong instanceId,
            IUnrealInvocationBridge bridge,
            UnrealObjectHandle host,
            UnrealObjectHandle target,
            string assemblyPath,
            string typeName,
            UnrealBlueprintOverrideActivationContract? expectedBlueprintOverrideContract = null)
            => new(
                worldId,
                instanceId,
                bridge,
                host,
                target,
                assemblyPath,
                typeName,
                expectedBlueprintOverrideContract);

        public void Start()
        {
            StartGeneration(_current);
            _started = true;
        }

        public void Tick(float deltaSeconds)
        {
            if (_preparedReload is not null)
            {
                throw new InvalidOperationException(
                    "The managed script is inside a two-phase reload transaction.");
            }
            if (!_started)
            {
                throw new InvalidOperationException("The managed script is not started.");
            }
            ScriptGeneration current = _current;
            _ = current.Prewarm.ObserveFirstTick();
            _profiler.ExecuteTick(
                current.Script,
                current.Generation,
                _typeName,
                deltaSeconds);
            _ = current.Prewarm.Start();
        }

        public Exception? ConsumePendingEventFailure() =>
            _current.Bridge.ConsumePendingEventFailure();

        public bool IsProfilerEnabled => _profiler.Enabled;

        public void SetProfilerEnabled(bool enabled) => _profiler.SetEnabled(enabled);

        public string GetProfilerTrace() => _profiler.ExportJson();

        public bool IsDebugConfigured => _debug.IsConfigured;

        public bool IsDebugActive => _debug.IsActive;

        public void ConfigureDebug(string configurationJson) =>
            _debug.Configure(configurationJson, _current.Generation);

        public void BeginDebugTick(float deltaSeconds)
        {
            if (!_started)
            {
                throw new InvalidOperationException("The managed script is not started.");
            }
            ScriptGeneration generation = _current;
            _debug.BeginTick(
                generation.Generation,
                deltaSeconds,
                value => _profiler.ExecuteTick(
                    generation.Script,
                    generation.Generation,
                    _typeName,
                    value,
                    attachProbe: false),
                exception => CaptureBoundaryFailure(
                    _worldId,
                    _instanceId,
                    nameof(BeginScriptDebugTick),
                    exception),
                _profiler);
        }

        public string GetDebugState() => _debug.GetSnapshotJson();

        public string GetReloadState()
        {
            UnrealManagedReloadTransaction? transaction = _lastReloadTransaction;
            if (transaction is null)
            {
                return string.Empty;
            }
            return JsonSerializer.Serialize(
                transaction with
                {
                    Prewarm = _lastReloadPrewarm?.GetSnapshot(),
                    ProgramLoaderPrewarm = ProgramLoaderPrewarm.GetSnapshot(),
                },
                UnifiedJson.CreateOptions(writeIndented: false));
        }

        public void ControlDebug(UnrealWorldDebugControl control, string? pauseId) =>
            _debug.Control(control, pauseId);

        public void DisableDebug() => _debug.Disable();

        public void DispatchEvent(UnrealScriptEvent scriptEvent)
        {
            if (_preparedReload is not null)
            {
                throw new InvalidOperationException(
                    "The managed script is inside a two-phase reload transaction.");
            }
            if (!_started)
            {
                throw new InvalidOperationException("The managed script is not started.");
            }
            if (_current.Script is IUnrealManagedEventSink sink)
            {
                sink.OnUnrealEvent(scriptEvent);
            }
        }

        internal UnrealManagedFunctionValueV1 InvokeFunction(
            string functionName,
            nint arguments,
            int argumentCount)
        {
            if (_preparedReload is not null)
            {
                throw new InvalidOperationException(
                    "The managed script is inside a two-phase reload transaction.");
            }
            if (!_started)
            {
                throw new InvalidOperationException("The managed script is not started.");
            }
            return _current.Functions.Invoke(
                _current.Script,
                functionName,
                arguments,
                argumentCount);
        }

        internal void InvokeFunctionV2(string functionName, nint frameAddress)
        {
            if (_preparedReload is not null)
            {
                throw new InvalidOperationException(
                    "The managed script is inside a two-phase reload transaction.");
            }
            if (!_started)
            {
                throw new InvalidOperationException("The managed script is not started.");
            }
            _current.FunctionsV2.Invoke(
                _current.Script,
                functionName,
                frameAddress);
        }

        public int Reload(string assemblyPath, string typeName)
        {
            int candidateGeneration = PrepareReload(assemblyPath, typeName);
            try
            {
                int generation = CommitPreparedReload(candidateGeneration);
                FinalizePreparedReload(candidateGeneration);
                return generation;
            }
            catch
            {
                if (_preparedReload is not null)
                {
                    try
                    {
                        AbortPreparedReload(candidateGeneration);
                    }
                    catch
                    {
                        // Preserve the original boundary failure. Abort records
                        // its own failed restoration in the reload receipt.
                    }
                }
                throw;
            }
        }

        public int PrepareReload(string assemblyPath, string typeName)
        {
            if (_debug.IsActive)
            {
                throw new InvalidOperationException(
                    "A live World debug execution must be disabled before script reload.");
            }
            if (_preparedReload is not null)
            {
                throw new InvalidOperationException(
                    "A managed reload candidate is already prepared for this script instance.");
            }
            _debug.Disable();
            string nextAssemblyPath = Path.GetFullPath(assemblyPath);
            ScriptGeneration previous = _current;
            int candidateGeneration = checked(previous.Generation + 1);
            var phases = new List<UnrealManagedReloadPhase>();
            ScriptGeneration? next = null;
            long phaseStart = Stopwatch.GetTimestamp();
            bool generationLoaded = false;
            try
            {
                next = LoadGeneration(candidateGeneration, nextAssemblyPath, typeName);
                generationLoaded = true;
                phases.Add(CompletePhase(
                    "generationLoad",
                    phaseStart,
                    "succeeded",
                    "Loaded the candidate assembly into a collectible script context."));
                if (_expectedBlueprintOverrideContract is not null)
                {
                    phases.Add(new UnrealManagedReloadPhase(
                        "activationContractValidation",
                        "succeeded",
                        0,
                        "The loaded managed type matches the persisted Blueprint override contract before activation."));
                }
                phaseStart = Stopwatch.GetTimestamp();
                ValidateBlueprintOverrideCompatibility(previous, next);
                phases.Add(CompletePhase(
                    "overrideContractValidation",
                    phaseStart,
                    "succeeded",
                    "The complete Blueprint override route set and V2 ABI signatures match the active generation."));
            }
            catch (Exception exception)
            {
                string failedPhase = exception is UnrealBlueprintOverrideActivationException
                    ? "activationContractValidation"
                    : generationLoaded
                        ? "overrideContractValidation"
                        : "generationLoad";
                phases.Add(CompletePhase(
                    failedPhase,
                    phaseStart,
                    "failed",
                    exception.Message));
                if (next is not null)
                {
                    try
                    {
                        next.Dispose();
                    }
                    catch (Exception cleanupException)
                    {
                        phases.Add(new UnrealManagedReloadPhase(
                            "candidateRetirement",
                            "failed",
                            0,
                            cleanupException.Message));
                    }
                }
                phases.Add(new UnrealManagedReloadPhase(
                    "rollback",
                    "unchanged",
                    0,
                    "The active generation was never stopped, swapped, or retired."));
                SetReloadTransaction(
                    "rolledBack",
                    previous.Generation,
                    candidateGeneration,
                    previous.Generation,
                    phases,
                    exception);
                throw;
            }

            bool previousWasStarted = _started;
            bool previousStopAttempted = false;
            bool candidateStartAttempted = false;
            string activePhase = "oldGenerationStop";
            try
            {
                if (_started)
                {
                    activePhase = "oldGenerationStop";
                    phaseStart = Stopwatch.GetTimestamp();
                    _started = false;
                    previousStopAttempted = true;
                    StopGeneration(previous);
                    phases.Add(CompletePhase(
                        "oldGenerationStop",
                        phaseStart,
                        "succeeded",
                        "Stopped the old script and released every generation-owned event subscription."));
                }

                activePhase = "candidateStart";
                phaseStart = Stopwatch.GetTimestamp();
                UnrealScriptContext context = CreateContext(next);
                candidateStartAttempted = true;
                next.Script.OnStart(context);
                phases.Add(CompletePhase(
                    "candidateStart",
                    phaseStart,
                    "succeeded",
                    "Started the candidate script against its generation-owned bridge."));

                activePhase = "eventRebinding";
                phaseStart = Stopwatch.GetTimestamp();
                IReadOnlyList<IUnrealEventSubscription> subscriptions =
                    UnrealEventBinder.BindAttributedEvents(next.Script, context);
                phases.Add(CompletePhase(
                    "eventRebinding",
                    phaseStart,
                    "succeeded",
                    $"Bound {subscriptions.Count} reflected event subscription(s)."));
                activePhase = "backgroundPrewarm";
                phaseStart = Stopwatch.GetTimestamp();
                bool prewarmStarted = next.Prewarm.Start();
                CoreClrGenerationPrewarmSnapshot prewarm = next.Prewarm.GetSnapshot();
                phases.Add(CompletePhase(
                    "backgroundPrewarm",
                    phaseStart,
                    prewarmStarted ? "scheduled" : "disabled",
                    prewarmStarted
                        ? $"Scheduled at most {prewarm.MaximumMethodCount} method(s) with a " +
                            $"{prewarm.TimeBudgetNanoseconds / 1_000_000d:0.###} ms budget."
                        : "Background prewarm is disabled for the controlled compatibility lane."));
                phases.Add(new UnrealManagedReloadPhase(
                    "oldGenerationRetention",
                    "retained",
                    0,
                    "The stopped previous generation remains loaded and recoverable until the complete batch commits."));
            }
            catch (Exception exception)
            {
                phases.Add(CompletePhase(
                    activePhase,
                    phaseStart,
                    "failed",
                    exception.Message));
                List<Exception>? cleanupFailures = null;
                try
                {
                    if (candidateStartAttempted)
                    {
                        StopGeneration(next);
                    }
                    else
                    {
                        next.Bridge.ClearSubscriptions();
                    }
                }
                catch (Exception cleanupException)
                {
                    (cleanupFailures ??= []).Add(cleanupException);
                }
                try
                {
                    next.Dispose();
                }
                catch (Exception cleanupException)
                {
                    (cleanupFailures ??= []).Add(cleanupException);
                }

                phaseStart = Stopwatch.GetTimestamp();
                try
                {
                    if (previousWasStarted && previousStopAttempted)
                    {
                        StartGeneration(previous);
                        _started = true;
                    }
                    else
                    {
                        _started = previousWasStarted;
                    }
                    phases.Add(CompletePhase(
                        "rollback",
                        phaseStart,
                        previousStopAttempted ? "restored" : "unchanged",
                        previousStopAttempted
                            ? "Restarted the previous generation and rebound its events."
                            : "The active generation was never stopped."));
                }
                catch (Exception restoreException)
                {
                    _started = false;
                    phases.Add(CompletePhase(
                        "rollback",
                        phaseStart,
                        "failed",
                        restoreException.Message));
                    var restorationFailures = new List<Exception> { exception };
                    if (cleanupFailures is not null)
                    {
                        restorationFailures.AddRange(cleanupFailures);
                    }
                    restorationFailures.Add(restoreException);
                    var aggregate = new AggregateException(
                        "The candidate generation failed and the previous generation could not be restored.",
                        restorationFailures);
                    SetReloadTransaction(
                        "failed",
                        previous.Generation,
                        candidateGeneration,
                        previous.Generation,
                        phases,
                        aggregate,
                        next.Prewarm);
                    throw aggregate;
                }

                Exception reported;
                if (cleanupFailures is null)
                {
                    reported = exception;
                }
                else
                {
                    cleanupFailures.Insert(0, exception);
                    reported = new AggregateException(
                        "The candidate generation failed and its resources did not retire cleanly.",
                        cleanupFailures);
                }
                SetReloadTransaction(
                    "rolledBack",
                    previous.Generation,
                    candidateGeneration,
                    previous.Generation,
                    phases,
                    reported,
                    next.Prewarm);
                throw reported;
            }

            _preparedReload = new PreparedScriptReload(
                previous,
                next,
                _assemblyPath,
                _typeName,
                nextAssemblyPath,
                typeName,
                previousWasStarted,
                previousStopAttempted,
                phases);
            phases.Add(new UnrealManagedReloadPhase(
                "batchPrepare",
                "succeeded",
                0,
                "The candidate completed load, start, and event rebinding without publishing its generation."));
            SetReloadTransaction(
                "prepared",
                previous.Generation,
                candidateGeneration,
                previous.Generation,
                phases,
                failure: null,
                next.Prewarm);
            return candidateGeneration;
        }

        public int CommitPreparedReload(int candidateGeneration)
        {
            PreparedScriptReload prepared = RequirePreparedReload(candidateGeneration);
            if (prepared.IsCommitted)
            {
                return prepared.Candidate.Generation;
            }

            long phaseStart = Stopwatch.GetTimestamp();
            _current = prepared.Candidate;
            _assemblyPath = prepared.CandidateAssemblyPath;
            _typeName = prepared.CandidateTypeName;
            _started = true;
            prepared.IsCommitted = true;
            prepared.Phases.Add(CompletePhase(
                "generationSwap",
                phaseStart,
                "succeeded",
                "Published the prepared candidate while retaining the stopped previous generation for batch rollback."));
            prepared.Phases.Add(new UnrealManagedReloadPhase(
                "batchCommit",
                "succeeded",
                0,
                "Committed the prepared generation without invoking script code or retiring the rollback generation."));
            SetReloadTransaction(
                "commitPendingFinalize",
                prepared.Previous.Generation,
                prepared.Candidate.Generation,
                prepared.Candidate.Generation,
                prepared.Phases,
                failure: null,
                prepared.Candidate.Prewarm);
            return prepared.Candidate.Generation;
        }

        public void AbortPreparedReload(int candidateGeneration)
        {
            PreparedScriptReload prepared = RequirePreparedReload(candidateGeneration);
            if (prepared.IsCommitted)
            {
                _current = prepared.Previous;
                _assemblyPath = prepared.PreviousAssemblyPath;
                _typeName = prepared.PreviousTypeName;
            }
            _started = false;

            List<Exception>? failures = null;
            long phaseStart = Stopwatch.GetTimestamp();
            try
            {
                StopGeneration(prepared.Candidate);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            try
            {
                prepared.Candidate.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                if (prepared.PreviousWasStarted && prepared.PreviousStopAttempted)
                {
                    StartGeneration(prepared.Previous);
                    _started = true;
                }
                else
                {
                    _started = prepared.PreviousWasStarted;
                }
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
                _started = false;
            }

            _preparedReload = null;
            string rollbackState = failures is null ? "restored" : "failed";
            string rollbackDetail = failures is null
                ? "Discarded the candidate and restarted the retained previous generation without changing its identity."
                : "The previous generation identity was restored, but candidate cleanup or lifecycle restart reported a failure.";
            prepared.Phases.Add(CompletePhase(
                "batchRollback",
                phaseStart,
                rollbackState,
                rollbackDetail));
            Exception? failure = failures is null
                ? null
                : new AggregateException(
                    "The two-phase reload batch could not restore every lifecycle resource cleanly.",
                    failures);
            SetReloadTransaction(
                failures is null ? "rolledBack" : "failed",
                prepared.Previous.Generation,
                prepared.Candidate.Generation,
                prepared.Previous.Generation,
                prepared.Phases,
                failure,
                prepared.Candidate.Prewarm);
            if (failure is not null)
            {
                throw failure;
            }
        }

        public void FinalizePreparedReload(int candidateGeneration)
        {
            PreparedScriptReload prepared = RequirePreparedReload(candidateGeneration);
            if (!prepared.IsCommitted)
            {
                throw new InvalidOperationException(
                    "A managed reload candidate must be committed before finalization.");
            }

            _preparedReload = null;
            long phaseStart = Stopwatch.GetTimestamp();
            Exception? retirementFailure = null;
            try
            {
                prepared.Previous.Dispose();
            }
            catch (Exception exception)
            {
                retirementFailure = exception;
            }
            prepared.Phases.Add(CompletePhase(
                "oldContextRetirement",
                phaseStart,
                retirementFailure is null ? "scheduled" : "retained",
                retirementFailure is null
                    ? "Released the retained old generation and requested collectible-context unload after the whole batch committed."
                    : "The candidate remains active, but old-generation cleanup reported a retained resource."));
            SetReloadTransaction(
                "committed",
                prepared.Previous.Generation,
                prepared.Candidate.Generation,
                prepared.Candidate.Generation,
                prepared.Phases,
                retirementFailure,
                prepared.Candidate.Prewarm);
        }

        private PreparedScriptReload RequirePreparedReload(int candidateGeneration)
        {
            PreparedScriptReload prepared = _preparedReload ??
                throw new InvalidOperationException(
                    "No managed reload candidate is prepared for this script instance.");
            if (candidateGeneration <= 0 ||
                prepared.Candidate.Generation != candidateGeneration)
            {
                throw new InvalidOperationException(
                    $"Prepared reload token '{candidateGeneration}' does not match candidate generation '{prepared.Candidate.Generation}'.");
            }
            return prepared;
        }

        public void Dispose()
        {
            if (_preparedReload is not null)
            {
                try
                {
                    AbortPreparedReload(_preparedReload.Candidate.Generation);
                }
                catch
                {
                    // Continue with active-generation teardown below. The
                    // boundary caller reports aggregate World cleanup failures.
                }
            }
            try
            {
                _debug.Dispose();
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

        private sealed class PreparedScriptReload(
            ScriptGeneration previous,
            ScriptGeneration candidate,
            string previousAssemblyPath,
            string previousTypeName,
            string candidateAssemblyPath,
            string candidateTypeName,
            bool previousWasStarted,
            bool previousStopAttempted,
            List<UnrealManagedReloadPhase> phases)
        {
            public ScriptGeneration Previous { get; } = previous;

            public ScriptGeneration Candidate { get; } = candidate;

            public string PreviousAssemblyPath { get; } = previousAssemblyPath;

            public string PreviousTypeName { get; } = previousTypeName;

            public string CandidateAssemblyPath { get; } = candidateAssemblyPath;

            public string CandidateTypeName { get; } = candidateTypeName;

            public bool PreviousWasStarted { get; } = previousWasStarted;

            public bool PreviousStopAttempted { get; } = previousStopAttempted;

            public List<UnrealManagedReloadPhase> Phases { get; } = phases;

            public bool IsCommitted { get; set; }
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

        private static void StopGeneration(ScriptGeneration generation)
        {
            List<Exception>? failures = null;
            try
            {
                generation.Script.OnStop();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            try
            {
                generation.Bridge.ClearSubscriptions();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            if (failures is not null)
            {
                throw new AggregateException(
                    "The managed generation did not stop cleanly.",
                    failures);
            }
        }

        private static UnrealManagedReloadPhase CompletePhase(
            string name,
            long startedTimestamp,
            string state,
            string detail)
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
            long microseconds = Math.Max(
                0,
                (long)Math.Ceiling(
                    elapsedTicks * 1_000_000d / Stopwatch.Frequency));
            return new UnrealManagedReloadPhase(name, state, microseconds, detail);
        }

        private void SetReloadTransaction(
            string state,
            int fromGeneration,
            int candidateGeneration,
            int activeGeneration,
            IReadOnlyList<UnrealManagedReloadPhase> phases,
            Exception? failure,
            CoreClrGenerationPrewarmOperation? prewarm = null)
        {
            _lastReloadPrewarm = prewarm;
            _lastReloadTransaction = new UnrealManagedReloadTransaction(
                "unifiedscript.managed-reload-transaction/v0.1",
                state,
                fromGeneration,
                candidateGeneration,
                activeGeneration,
                phases,
                failure?.Message,
                Prewarm: null,
                ProgramLoaderPrewarm: null);
        }

        private ScriptGeneration LoadGeneration(
            int generation,
            string assemblyPath,
            string typeName)
        {
            long phaseStarted = Stopwatch.GetTimestamp();
            var loadContext = new ScriptAssemblyLoadContext(
                _worldId,
                _instanceId,
                generation,
                assemblyPath);
            long contextTicks = Stopwatch.GetTimestamp() - phaseStarted;
            try
            {
                phaseStarted = Stopwatch.GetTimestamp();
                Assembly assembly = loadContext.LoadMainAssembly();
                long assemblyTicks = Stopwatch.GetTimestamp() - phaseStarted;
                phaseStarted = Stopwatch.GetTimestamp();
                loadContext.ValidateEntryType(assembly, typeName);
                Type type = assembly.GetType(typeName, throwOnError: true)!;
                long typeTicks = Stopwatch.GetTimestamp() - phaseStarted;
                _expectedBlueprintOverrideContract?.ValidateType(type);
                phaseStarted = Stopwatch.GetTimestamp();
                if (Activator.CreateInstance(type) is not IUnrealManagedScript script)
                {
                    throw new InvalidOperationException(
                        $"Managed script type '{typeName}' does not implement {nameof(IUnrealManagedScript)}.");
                }
                long activationTicks = Stopwatch.GetTimestamp() - phaseStarted;
                phaseStarted = Stopwatch.GetTimestamp();
                var generationBridge = new UnrealGenerationInvocationBridge(_bridge);
                UnrealManagedFunctionDispatcher functions =
                    UnrealManagedFunctionDispatcher.Create(type);
                UnrealManagedFunctionDispatcherV2 functionsV2 =
                    UnrealManagedFunctionDispatcherV2.Create(type, staticOnly: false);
                CoreClrGenerationPrewarmOperation prewarm =
                    CoreClrGenerationPrewarmOperation.Create(
                        type,
                        ResolvePrewarmOptions());
                long preparationTicks = Stopwatch.GetTimestamp() - phaseStarted;
                if (TraceProgramLoadPhases)
                {
                    Console.Error.WriteLine(
                        $"Finare managed load phases: Type={typeName}, " +
                        $"Mode={(assemblyPath.EndsWith(".uschunk", StringComparison.OrdinalIgnoreCase) ? "chunk" : "loose")}, " +
                        $"ContextMs={StopwatchTicksToMilliseconds(contextTicks):0.######}, " +
                        $"AssemblyMs={StopwatchTicksToMilliseconds(assemblyTicks):0.######}, " +
                        $"TypeMs={StopwatchTicksToMilliseconds(typeTicks):0.######}, " +
                        $"ActivationMs={StopwatchTicksToMilliseconds(activationTicks):0.######}, " +
                        $"PreparationMs={StopwatchTicksToMilliseconds(preparationTicks):0.######}");
                }
                return new ScriptGeneration(
                    generation,
                    loadContext,
                    script,
                    generationBridge,
                    functions,
                    functionsV2,
                    functionsV2.BlueprintOverrides,
                    prewarm);
            }
            catch
            {
                RetireLoadContext(loadContext);
                throw;
            }
        }

        private static double StopwatchTicksToMilliseconds(long ticks) =>
            Math.Max(0, ticks) * 1000d / Stopwatch.Frequency;

        private static void ValidateBlueprintOverrideCompatibility(
            ScriptGeneration previous,
            ScriptGeneration candidate)
        {
            const string recovery =
                "Stop the affected instance, synchronize the Script Class, and restart it to apply an override contract change.";
            IReadOnlyDictionary<string, UnrealBlueprintOverrideRoute> previousRoutes =
                previous.BlueprintOverrides;
            IReadOnlyDictionary<string, UnrealBlueprintOverrideRoute> candidateRoutes =
                candidate.BlueprintOverrides;
            foreach ((string routeKey, UnrealBlueprintOverrideRoute previousRoute) in previousRoutes)
            {
                if (!candidateRoutes.TryGetValue(routeKey, out UnrealBlueprintOverrideRoute? candidateRoute))
                {
                    throw new InvalidOperationException(
                        $"Blueprint override route '{previousRoute.DisplayName}' was removed " +
                        $"by managed method '{previousRoute.ManagedMethodIdentity}'. " +
                        $"The active generation remains running. {recovery}");
                }

                var differences = new List<string>();
                bool managedMethodIdentityMatches = previousRoute.StableManagedMethodIdentity is not null &&
                    candidateRoute.StableManagedMethodIdentity is not null
                    ? string.Equals(
                        previousRoute.StableManagedMethodIdentity,
                        candidateRoute.StableManagedMethodIdentity,
                        StringComparison.Ordinal)
                    : previousRoute.StableManagedMethodIdentity is null &&
                        candidateRoute.StableManagedMethodIdentity is null &&
                        string.Equals(
                            previousRoute.ManagedMethodIdentity,
                            candidateRoute.ManagedMethodIdentity,
                            StringComparison.Ordinal);
                if (!managedMethodIdentityMatches)
                {
                    string previousStableIdentity = previousRoute.StableManagedMethodIdentity ?? "<legacy full identity>";
                    string candidateStableIdentity = candidateRoute.StableManagedMethodIdentity ?? "<legacy full identity>";
                    differences.Add(
                        $"managed method changed from '{previousRoute.ManagedMethodIdentity}' " +
                        $"to '{candidateRoute.ManagedMethodIdentity}' " +
                        $"(stable identity '{previousStableIdentity}' to '{candidateStableIdentity}')");
                }
                if (!string.Equals(
                        previousRoute.DeclaringClassPath,
                        candidateRoute.DeclaringClassPath,
                        StringComparison.Ordinal))
                {
                    differences.Add(
                        $"declaring class changed from '{previousRoute.DeclaringClassPath}' " +
                        $"to '{candidateRoute.DeclaringClassPath}'");
                }
                if (!string.Equals(
                        previousRoute.FunctionName,
                        candidateRoute.FunctionName,
                        StringComparison.Ordinal))
                {
                    differences.Add(
                        $"function name changed from '{previousRoute.FunctionName}' " +
                        $"to '{candidateRoute.FunctionName}'");
                }
                if (previousRoute.SuperMode != candidateRoute.SuperMode)
                {
                    differences.Add(
                        $"SuperMode changed from '{previousRoute.SuperMode}' " +
                        $"to '{candidateRoute.SuperMode}'");
                }
                if (!string.Equals(
                        previousRoute.AbiSignature,
                        candidateRoute.AbiSignature,
                        StringComparison.Ordinal))
                {
                    differences.Add("the V2 ABI signature changed");
                }
                if (differences.Count != 0)
                {
                    throw new InvalidOperationException(
                        $"Blueprint override contract changed for route '{previousRoute.DisplayName}' " +
                        $"(managed method '{previousRoute.ManagedMethodIdentity}'): " +
                        string.Join("; ", differences) +
                        $". The active generation remains running. {recovery}");
                }
            }

            foreach ((string routeKey, UnrealBlueprintOverrideRoute candidateRoute) in candidateRoutes)
            {
                if (!previousRoutes.ContainsKey(routeKey))
                {
                    throw new InvalidOperationException(
                        $"Blueprint override route '{candidateRoute.DisplayName}' was added " +
                        $"by managed method '{candidateRoute.ManagedMethodIdentity}'. " +
                        $"The active generation remains running. {recovery}");
                }
            }
        }

        private static CoreClrGenerationPrewarmOptions ResolvePrewarmOptions() =>
            string.Equals(
                Environment.GetEnvironmentVariable(
                    "FINARE_DISABLE_BACKGROUND_PREWARM"),
                "1",
                StringComparison.Ordinal)
                ? CoreClrGenerationPrewarmOptions.Disabled
                : CoreClrGenerationPrewarmOptions.Default;
    }

    internal sealed class ManagedStaticLibrary : IDisposable
    {
        private readonly ulong _worldId;
        private readonly string _assemblyPath;
        private readonly string _typeName;
        private readonly IUnrealInvocationBridge? _bridge;
        private StaticLibraryGeneration? _current;
        private PreparedStaticLibraryReload? _preparedReload;
        private int _nextGeneration;
        private bool _disposed;

        private ManagedStaticLibrary(
            ulong worldId,
            string assemblyPath,
            string typeName,
            StaticLibraryGeneration current,
            IUnrealInvocationBridge? bridge)
        {
            _worldId = worldId;
            _assemblyPath = assemblyPath;
            _typeName = typeName;
            _current = current;
            _bridge = bridge;
            _nextGeneration = checked(current.Generation + 1);
        }

        public static ManagedStaticLibrary Create(
            ulong worldId,
            string assemblyPath,
            string typeName,
            IUnrealInvocationBridge? bridge = null)
        {
            string fullAssemblyPath = Path.GetFullPath(assemblyPath);
            StaticLibraryGeneration current = LoadGeneration(
                worldId,
                fullAssemblyPath,
                typeName,
                generation: 1, bridge);
            return new ManagedStaticLibrary(
                worldId,
                fullAssemblyPath,
                typeName,
                current, bridge);
        }

        public void Invoke(string functionName, nint frameAddress)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = _current ?? throw new ObjectDisposedException(nameof(ManagedStaticLibrary));
            current.Functions.Invoke(target: null, functionName, frameAddress, current.Bridge);
        }

        internal int PrepareReload()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_preparedReload is not null)
            {
                throw new InvalidOperationException(
                    "A managed static library reload candidate is already prepared.");
            }

            int candidateGeneration = _nextGeneration;
            _nextGeneration = checked(_nextGeneration + 1);
            StaticLibraryGeneration candidate = LoadGeneration(
                _worldId,
                _assemblyPath,
                _typeName,
                candidateGeneration, _bridge);
            try
            {
                ValidateCompatible(
                    _current ?? throw new ObjectDisposedException(nameof(ManagedStaticLibrary)),
                    candidate);
                _preparedReload = new PreparedStaticLibraryReload(candidate);
                return candidateGeneration;
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
        }

        internal int CommitPreparedReload(int candidateGeneration)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PreparedStaticLibraryReload prepared = RequirePreparedReload(candidateGeneration);
            if (prepared.IsCommitted)
            {
                return prepared.Candidate.Generation;
            }

            prepared.Previous = _current ??
                throw new ObjectDisposedException(nameof(ManagedStaticLibrary));
            _current = prepared.Candidate;
            prepared.IsCommitted = true;
            return prepared.Candidate.Generation;
        }

        internal void AbortPreparedReload(int candidateGeneration)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PreparedStaticLibraryReload prepared = RequirePreparedReload(candidateGeneration);
            if (prepared.IsCommitted)
            {
                _current = prepared.Previous ??
                    throw new InvalidOperationException(
                        "A committed static library reload has no retained previous generation.");
            }

            _preparedReload = null;
            prepared.Candidate.Dispose();
        }

        internal void FinalizePreparedReload(int candidateGeneration)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PreparedStaticLibraryReload prepared = RequirePreparedReload(candidateGeneration);
            if (!prepared.IsCommitted)
            {
                throw new InvalidOperationException(
                    "A managed static library reload candidate must be committed before finalization.");
            }

            _preparedReload = null;
            prepared.Previous?.Dispose();
        }

        internal IReadOnlyList<WeakReference> GetLoadContextReferencesForTests()
        {
            var references = new List<WeakReference>();
            if (_current is not null)
            {
                references.Add(new WeakReference(_current.LoadContext));
            }
            if (_preparedReload is not null)
            {
                references.Add(new WeakReference(_preparedReload.Candidate.LoadContext));
                if (_preparedReload.Previous is not null)
                {
                    references.Add(new WeakReference(_preparedReload.Previous.LoadContext));
                }
            }
            return references;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            PreparedStaticLibraryReload? prepared = _preparedReload;
            _preparedReload = null;
            StaticLibraryGeneration? current = _current;
            _current = null;
            List<Exception>? failures = null;
            try
            {
                prepared?.Candidate.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            try
            {
                prepared?.Previous?.Dispose();
                if (prepared is not null)
                {
                    prepared.Previous = null;
                }
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            try
            {
                current?.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            if (failures is not null)
            {
                throw new AggregateException(
                    "One or more static library generations failed during World teardown.",
                    failures);
            }
        }

        private PreparedStaticLibraryReload RequirePreparedReload(int candidateGeneration)
        {
            PreparedStaticLibraryReload prepared = _preparedReload ??
                throw new InvalidOperationException(
                    "No managed static library reload candidate is prepared.");
            if (candidateGeneration <= 0 ||
                prepared.Candidate.Generation != candidateGeneration)
            {
                throw new InvalidOperationException(
                    $"Prepared static library reload token '{candidateGeneration}' does not match " +
                    $"candidate generation '{prepared.Candidate.Generation}'.");
            }
            return prepared;
        }

        private static StaticLibraryGeneration LoadGeneration(
            ulong worldId,
            string assemblyPath,
            string typeName,
            int generation, IUnrealInvocationBridge? bridge)
        {
            var loadContext = new ScriptAssemblyLoadContext(
                worldId,
                instanceId: 0,
                generation,
                assemblyPath);
            try
            {
                Assembly assembly = loadContext.LoadMainAssembly();
                loadContext.ValidateEntryType(assembly, typeName);
                Type type = assembly.GetType(typeName, throwOnError: true)!;
                ValidateStaticLibraryType(type, typeName);
                UnrealManagedFunctionDispatcherV2 functions =
                    UnrealManagedFunctionDispatcherV2.Create(type, staticOnly: true);
                return new StaticLibraryGeneration(generation, loadContext, functions,
                    bridge is null ? null : new UnrealGenerationInvocationBridge(bridge));
            }
            catch
            {
                RetireLoadContext(loadContext);
                throw;
            }
        }

        private static void ValidateStaticLibraryType(Type type, string typeName)
        {
            UnrealScriptClassAttribute? scriptClass =
                type.GetCustomAttribute<UnrealScriptClassAttribute>(inherit: false);
            if (!type.IsAbstract || !type.IsSealed ||
                scriptClass?.Kind != UnrealScriptClassKind.BlueprintFunctionLibrary)
            {
                throw new InvalidOperationException(
                    $"Managed static library '{typeName}' must be a static class marked " +
                    $"[{nameof(UnrealScriptClassAttribute)}({nameof(UnrealScriptClassKind.BlueprintFunctionLibrary)})].");
            }
        }

        private static void ValidateCompatible(
            StaticLibraryGeneration previous,
            StaticLibraryGeneration candidate)
        {
            foreach ((string functionName, UnrealManagedFunctionDispatcherV2.UnrealManagedFunctionSignature signature) in
                     previous.Functions.Signatures)
            {
                if (!candidate.Functions.Signatures.TryGetValue(
                        functionName,
                        out UnrealManagedFunctionDispatcherV2.UnrealManagedFunctionSignature? candidateSignature))
                {
                    throw new InvalidOperationException(
                        $"Static library reload removes callable function '{functionName}'.");
                }
                if (!string.Equals(
                        signature.Canonical,
                        candidateSignature.Canonical,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Static library reload changes callable function '{functionName}' signature.");
                }
            }
        }

        private sealed class PreparedStaticLibraryReload(StaticLibraryGeneration candidate)
        {
            public StaticLibraryGeneration Candidate { get; } = candidate;

            public StaticLibraryGeneration? Previous { get; set; }

            public bool IsCommitted { get; set; }
        }

        private sealed class StaticLibraryGeneration(
            int generation,
            ScriptAssemblyLoadContext loadContext,
            UnrealManagedFunctionDispatcherV2 functions,
            UnrealGenerationInvocationBridge? bridge) : IDisposable
        {
            private bool _disposed;

            public int Generation { get; } = generation;

            public ScriptAssemblyLoadContext LoadContext { get; } = loadContext;

            public UnrealManagedFunctionDispatcherV2 Functions { get; } = functions;
            public UnrealGenerationInvocationBridge? Bridge { get; } = bridge;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                try { Bridge?.Dispose(); }
                finally { RetireLoadContext(LoadContext); }
            }
        }
    }

    private sealed record ScriptGeneration(
        int Generation,
        ScriptAssemblyLoadContext LoadContext,
        IUnrealManagedScript Script,
        UnrealGenerationInvocationBridge Bridge,
        UnrealManagedFunctionDispatcher Functions,
        UnrealManagedFunctionDispatcherV2 FunctionsV2,
        IReadOnlyDictionary<string, UnrealBlueprintOverrideRoute> BlueprintOverrides,
        CoreClrGenerationPrewarmOperation Prewarm) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            List<Exception>? failures = null;
            try
            {
                Prewarm.Cancel();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            try
            {
                Bridge.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            try
            {
                RetireLoadContext(LoadContext);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            if (failures is not null)
            {
                throw new AggregateException(
                    "The managed script generation did not retire cleanly.",
                    failures);
            }
        }
    }

    private sealed class ScriptAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver? _resolver;
        private readonly string _programPath;
        private readonly string _programDirectory;
        private readonly FinareChunk? _chunk;
        private readonly FinareRuntimeManifest? _manifest;
        private readonly FinareRuntimeDependencyManifest? _dependencies;
        private readonly FinareManagedDependencyCache _dependencyCache;

        public ScriptAssemblyLoadContext(
            ulong worldId,
            ulong instanceId,
            int generation,
            string programPath,
            FinareManagedDependencyCache? dependencyCache = null)
            : base(
                $"Finare.Unreal.World.{worldId}.Script.{instanceId}.Generation.{generation}",
                isCollectible: true)
        {
            _dependencyCache = dependencyCache ?? DependencyCache;
            _programPath = programPath;
            _programDirectory = Path.GetDirectoryName(programPath) ?? Environment.CurrentDirectory;
            if (!string.Equals(
                Path.GetExtension(programPath),
                ".uschunk",
                StringComparison.OrdinalIgnoreCase))
            {
                _resolver = new AssemblyDependencyResolver(programPath);
                return;
            }

            using FileStream stream = OpenSharedRead(programPath);
            _chunk = FinareProgramChunk.Read(stream);
            _manifest = FinareRuntimeManifestBinary.Decode(_chunk.GetRequiredSection(
                FinareChunkSectionKind.RuntimeManifest).Data.Span);
            FinareRuntimeDeclarationDocument declarations =
                FinareRuntimeDeclarationBinary.Decode(_chunk.GetRequiredSection(
                    FinareChunkSectionKind.DeclarationData).Data.Span);
            _ = FinareRuntimeBindingManifestBinary.Decode(_chunk.GetRequiredSection(
                FinareChunkSectionKind.BindingManifest).Data.Span);
            _dependencies = FinareRuntimeDependencyManifestBinary.Decode(
                _chunk.GetRequiredSection(
                    FinareChunkSectionKind.DependencyManifest).Data.Span);
            if (_manifest.SourceCompilationId != declarations.SourceCompilationId ||
                _manifest.SemanticConfigurationId != declarations.SemanticConfigurationId)
            {
                throw new InvalidDataException(
                    "Compiled chunk runtime manifest and declarations have different identities.");
            }
        }

        public Assembly LoadMainAssembly()
        {
            if (_chunk is null)
            {
                return LoadAssemblyFile(_programPath);
            }

            using var assemblyStream = new MemoryStream(_chunk.GetRequiredSection(
                FinareChunkSectionKind.ManagedAssembly).Data.ToArray(), writable: false);
            if (!_chunk.TryGetSection(
                FinareChunkSectionKind.PortablePdb,
                out FinareChunkSection? pdbSection) || pdbSection is null)
            {
                return LoadFromStream(assemblyStream);
            }

            using var pdbStream = new MemoryStream(pdbSection.Data.ToArray(), writable: false);
            return LoadFromStream(assemblyStream, pdbStream);
        }

        public void ValidateEntryType(Assembly assembly, string typeName)
        {
            if (_manifest is null)
            {
                return;
            }

            if (!string.Equals(
                assembly.GetName().Name,
                _manifest.AssemblyName,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Chunk assembly '{assembly.GetName().Name}' does not match manifest " +
                    $"'{_manifest.AssemblyName}'.");
            }
            if (!_manifest.EntryTypeNames.Contains(typeName, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Managed script type '{typeName}' is not a declared chunk entry type.");
            }
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            Assembly? sharedContract = new[]
                {
                    typeof(IUnrealManagedScript).Assembly,
                    typeof(global::Finare.VisualAttribute).Assembly,
                    typeof(global::Finare.Runtime.CoreClrDebugProbe).Assembly,
                }
                .Distinct()
                .FirstOrDefault(candidate => AssemblyName.ReferenceMatchesDefinition(
                    candidate.GetName(),
                    assemblyName));
            if (sharedContract is not null)
            {
                if (_dependencies is not null &&
                    string.IsNullOrWhiteSpace(sharedContract.Location))
                {
                    throw new InvalidDataException(
                        $"Chunk dependency '{assemblyName.Name}' has no verifiable location.");
                }
                if (_dependencies is not null)
                {
                    _ = ValidateDependency(
                        sharedContract.Location,
                        assemblyName,
                        sharedContract);
                }
                return sharedContract;
            }

            string? dependencyPath = _resolver?.ResolveAssemblyToPath(assemblyName);
            if (dependencyPath is null && assemblyName.Name is not null)
            {
                string adjacentPath = Path.Combine(_programDirectory, assemblyName.Name + ".dll");
                if (File.Exists(adjacentPath))
                {
                    dependencyPath = adjacentPath;
                }
            }
            if (dependencyPath is null)
            {
                return null;
            }
            FinareManagedDependencyVerification? verification =
                ValidateDependency(dependencyPath, assemblyName);
            return verification is null
                ? LoadAssemblyFile(dependencyPath)
                : LoadAssemblySnapshot(verification.Dependency);
        }

        private FinareManagedDependencyVerification? ValidateDependency(
            string path,
            AssemblyName requestedName,
            Assembly? sharedAssembly = null)
        {
            if (_dependencies is null || requestedName.Name is null)
            {
                return null;
            }

            FinareRuntimeDependency? dependency = _dependencies.Dependencies
                .SingleOrDefault(candidate =>
                    candidate.Kind == FinareRuntimeDependencyKind.ManagedAssembly &&
                    string.Equals(candidate.Name, requestedName.Name, StringComparison.OrdinalIgnoreCase));
            if (dependency is null)
            {
                throw new InvalidDataException(
                    $"Chunk dependency '{requestedName.Name}' is not declared.");
            }

            return _dependencyCache.Verify(path, dependency, sharedAssembly);
        }

        private Assembly LoadAssemblySnapshot(
            FinareVerifiedManagedDependency dependency)
        {
            using var stream = new MemoryStream(dependency.Content, writable: false);
            return LoadFromStream(stream);
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

    private static CoreClrProgramLoaderPrewarmOptions ResolveProgramLoaderPrewarmOptions() =>
        string.Equals(
            Environment.GetEnvironmentVariable(
                "FINARE_DISABLE_PROGRAM_LOADER_PREWARM"),
            "1",
            StringComparison.Ordinal)
            ? CoreClrProgramLoaderPrewarmOptions.Disabled
            : CoreClrProgramLoaderPrewarmOptions.Default;

    internal static CoreClrProgramLoaderPrewarmSnapshot
        GetProgramLoaderPrewarmSnapshotForTests() => ProgramLoaderPrewarm.GetSnapshot();

    internal static string ValidateCompiledProgramForTests(
        string programPath,
        string typeName,
        FinareManagedDependencyCache? dependencyCache = null)
    {
        var loadContext = new ScriptAssemblyLoadContext(
            1,
            1,
            1,
            programPath,
            dependencyCache);
        try
        {
            Assembly assembly = loadContext.LoadMainAssembly();
            loadContext.ValidateEntryType(assembly, typeName);
            return assembly.GetType(typeName, throwOnError: true)!.FullName!;
        }
        finally
        {
            RetireLoadContext(loadContext);
        }
    }
}
