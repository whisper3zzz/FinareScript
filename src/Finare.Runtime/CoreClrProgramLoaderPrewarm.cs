using System.Diagnostics;
using System.Reflection;
using Finare.Core;

namespace Finare.Runtime;

public enum CoreClrProgramLoaderPrewarmState
{
    Scheduled,
    Running,
    Completed,
    DependencyLimitReached,
    ByteBudgetReached,
    TimeBudgetReached,
    CompletedWithFailures,
    Disabled,
}

public sealed record CoreClrProgramLoaderPrewarmOptions(
    int MaximumDependencyCount,
    long MaximumDependencyBytes,
    int TimeBudgetMilliseconds)
{
    public const int DefaultMaximumDependencyCount = 32;

    public const long DefaultMaximumDependencyBytes = 64L * 1024 * 1024;

    public const int DefaultTimeBudgetMilliseconds = 50;

    public const int HardMaximumDependencyCount = 256;

    public const long HardMaximumDependencyBytes = 512L * 1024 * 1024;

    public const int HardMaximumTimeBudgetMilliseconds = 500;

    public static CoreClrProgramLoaderPrewarmOptions Default { get; } = new(
        DefaultMaximumDependencyCount,
        DefaultMaximumDependencyBytes,
        DefaultTimeBudgetMilliseconds);

    public static CoreClrProgramLoaderPrewarmOptions Disabled { get; } = new(0, 0, 0);

    internal bool IsDisabled =>
        MaximumDependencyCount == 0 ||
        MaximumDependencyBytes == 0 ||
        TimeBudgetMilliseconds == 0;

    internal void Validate()
    {
        if (MaximumDependencyCount is < 0 or > HardMaximumDependencyCount)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDependencyCount));
        }
        if (MaximumDependencyBytes is < 0 or > HardMaximumDependencyBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDependencyBytes));
        }
        if (TimeBudgetMilliseconds is < 0 or > HardMaximumTimeBudgetMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(TimeBudgetMilliseconds));
        }
    }
}

public sealed record CoreClrProgramLoaderPrewarmSnapshot(
    CoreClrProgramLoaderPrewarmState State,
    int MaximumDependencyCount,
    long MaximumDependencyBytes,
    long TimeBudgetNanoseconds,
    bool CodecPrepared,
    int CandidateDependencyCount,
    int PreparedDependencyCount,
    int CacheHitCount,
    long PreparedDependencyBytes,
    int FailureCount,
    long DurationNanoseconds,
    string? FailureType);

/// <summary>
/// Performs bounded process-wide preparation for the compiled-program loader.
/// It warms the strict binary codecs and fills the immutable dependency cache
/// from the installed managed-runtime directory. Script activation observes
/// the resulting cache but never waits for this operation.
/// </summary>
public sealed class CoreClrProgramLoaderPrewarmOperation
{
    private const string WarmupChunkBase64 = """
        VVNDSFVOSwABAAAAYAAAAAAAAAAFAAAAQAAAAAAAAABgAAAAAAAAAKABAAAAAAAAwQYAAAAAAABmRAsvl+/V8/GsFLs5txoe
        SVO43+0OeW0OH3+TSM9YCwAAAAAAAAAAAQAAAAEAAACgAQAAAAAAAPIAAAAAAAAA8gAAAAAAAAAxmlLybGl/X/z8j3OrPVhg
        4pcnThAdEkPncftK8N5RWAIAAAABAAAAoAIAAAAAAAABAAAAAAAAAAEAAAAAAAAAS/USLzRFVMU73i67jNK349FgCtYxw4Wl
        18ziPHeFRZoDAAAAAQAAALACAAAAAAAAJgEAAAAAAAAmAQAAAAAAAILMTo340K9TacgAXWjGZ6x06TmOyZnjY5VDeDNSFxLl
        BAAAAAEAAADgAwAAAAAAABQAAAAAAAAAFAAAAAAAAABmtOpE3tqjzCfmzn2a6bo4e1lXLHKYhxE8y04YIT+7ngUAAAABAAAA
        AAQAAAAAAADBAgAAAAAAAMECAAAAAAAAHQzl9rY9yjGj23XEre263BaW8eNHBoF98L1zz7PSrNRVU1JNQU4AAAEAAAAAAAAA
        GQAAAFVuaWZpZWRTY3JpcHQuVW5yZWFsLkRlbW8jAAAAdXNfNDllNTgzOGRiZWM4YTBhZjJjZWNjZTY1ZGYxMDk0ZGQjAAAA
        dXNfZWNjMmZlNjAzYmI5MjU3ZWQwZDRhZDNjNzlmYWQzMTlHAAAAc2hhMjU2OjlmNjYxNTY5YzkyOWFkNTZkNjUzYTU1MzYx
        MzRmNGYxNjNlOTY2NTI4Y2YxYWFiZGNmYWEzNGQxZDkxYWM5YjIBAAAAJAAAAFVuaWZpZWRTY3JpcHQuVW5yZWFsLkRlbW8u
        RGVtb1NjcmlwdAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAFVTUkRFQwAAAQAAAAAAAAAjAAAAdXNfNDllNTgzOGRi
        ZWM4YTBhZjJjZWNjZTY1ZGYxMDk0ZGQjAAAAdXNfZWNjMmZlNjAzYmI5MjU3ZWQwZDRhZDNjNzlmYWQzMTkBAAAAIwAAAHVz
        X2NlZmM5ZWU1ODA1ZTNlZDU2MTAxNTQxNDVlYTk4NmRkJAAAAFVuaWZpZWRTY3JpcHQuVW5yZWFsLkRlbW8uRGVtb1Njcmlw
        dAsAAABVbnJlYWwvRGVtbwEAAAAjAAAAdXNfZDQ0NjNkNTczOTdjNzcyNjRhMGUyODJjYTZjMjk0MzIMAAAASW5pdGlhbFZh
        bHVlAwAAAGludAwAAABTeXN0ZW0uSW50MzIBAgoAAAAAAAAAAwEEAAAARGVtbwAAAAAAAAAAAABVU1JCTkQAAAEAAAAAAAAA
        AAAAAAAAAAAAAAAAAAAAAFVTUkRFUAAAAQAAAAAAAAAGAAAAAQAAABQAAABVbmlmaWVkU2NyaXB0LkNTaGFycAcAAAAxLjAu
        MC4wRwAAAHNoYTI1NjpmZTlhMjAyODU5YzQyY2FkYWYxYjBhMzVjZGI1ZTVhMzUyOWJiZmMzMzU5ZmMzNjFiMjcyYTE0MDll
        YWQ5NjI2AQAAABIAAABVbmlmaWVkU2NyaXB0LkNvcmUHAAAAMS4wLjAuMEcAAABzaGEyNTY6NWYxMzgwMDhiM2Y0NDRiODEx
        OGVjYTgzZDFjOTAyNmJlZjM3Mjg0NTA0NWQ1MGY4NmYzOGY4Y2IwNDM1ZTU0NwEAAAATAAAAVW5pZmllZFNjcmlwdC5HcmFw
        aAcAAAAxLjAuMC4wRwAAAHNoYTI1NjpiYzAwYjg2NmIyOWJmOGFiMDg3OTA5M2U0ZmJhYTUzMDMyNTMwYjljZDdmMWI4NmVi
        NjY4NDk0OTlhMDkzOGFjAQAAABUAAABVbmlmaWVkU2NyaXB0LlJ1bnRpbWUHAAAAMS4wLjAuMEcAAABzaGEyNTY6NTRkZWNm
        YzJlZWU5Nzk1ZGQyNTlkMTZkNjlkYjYxMmJlMzcyY2RlYjQ4NTM5MTc4Zjc0MTZiMjVlODZlODg5OQEAAAAXAAAAVW5pZmll
        ZFNjcmlwdC5TZW1hbnRpY3MHAAAAMS4wLjAuMEcAAABzaGEyNTY6NzVjZmY2ZDBlMGY5NTQ2MDI4NGM2MDQ5ZmRjZjNmOWNm
        ZTdjODgwMjM0YWM2YTkzNzVmZTZhNzM1NWYzODRkOAEAAAAUAAAAVW5pZmllZFNjcmlwdC5VbnJlYWwHAAAAMS4wLjAuMEcA
        AABzaGEyNTY6MDdmNjM4ZGYyM2ZlZWQ3ZTBhMWJjNDczMWExNDE2MzEyM2NkYTdlNzc5ZDEzYTUzOWEwNTY3YTMwNWMyZDY4
        Mw==
        """;

    private readonly object gate = new();
    private readonly string assemblyDirectory;
    private readonly FinareManagedDependencyCache dependencyCache;
    private readonly CoreClrProgramLoaderPrewarmOptions options;
    private readonly TaskCompletionSource completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource codecPreparationRequested = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private CoreClrProgramLoaderPrewarmState state;
    private bool codecPrepared;
    private int candidateDependencyCount;
    private int preparedDependencyCount;
    private int cacheHitCount;
    private long preparedDependencyBytes;
    private int failureCount;
    private long durationNanoseconds;
    private string? failureType;
    private bool started;
    private bool codecRequested;

    private CoreClrProgramLoaderPrewarmOperation(
        string assemblyDirectory,
        FinareManagedDependencyCache dependencyCache,
        CoreClrProgramLoaderPrewarmOptions options)
    {
        this.assemblyDirectory = Path.GetFullPath(assemblyDirectory);
        this.dependencyCache = dependencyCache;
        this.options = options;
        state = options.IsDisabled
            ? CoreClrProgramLoaderPrewarmState.Disabled
            : CoreClrProgramLoaderPrewarmState.Scheduled;
        if (options.IsDisabled)
        {
            completion.SetResult();
        }
    }

    public Task Completion => completion.Task;

    public static CoreClrProgramLoaderPrewarmOperation Create(
        string assemblyDirectory,
        FinareManagedDependencyCache? dependencyCache = null,
        CoreClrProgramLoaderPrewarmOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyDirectory);
        CoreClrProgramLoaderPrewarmOptions resolvedOptions =
            options ?? CoreClrProgramLoaderPrewarmOptions.Default;
        resolvedOptions.Validate();
        return new CoreClrProgramLoaderPrewarmOperation(
            assemblyDirectory,
            dependencyCache ?? FinareManagedDependencyCache.Shared,
            resolvedOptions);
    }

    public bool Start()
    {
        bool scheduled = StartDependencyPreparation();
        _ = StartCodecPreparation();
        return scheduled;
    }

    public bool StartDependencyPreparation()
    {
        lock (gate)
        {
            if (started || options.IsDisabled)
            {
                return false;
            }
            started = true;
            try
            {
                _ = Task.Run(ExecuteAsync);
                return true;
            }
            catch (Exception exception)
            {
                state = CoreClrProgramLoaderPrewarmState.CompletedWithFailures;
                failureCount = 1;
                failureType = exception.GetType().FullName;
                completion.SetResult();
                return false;
            }
        }
    }

    public bool StartCodecPreparation()
    {
        _ = StartDependencyPreparation();
        lock (gate)
        {
            if (options.IsDisabled || codecRequested)
            {
                return false;
            }
            codecRequested = true;
            codecPreparationRequested.TrySetResult();
            return true;
        }
    }

    public CoreClrProgramLoaderPrewarmSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return new CoreClrProgramLoaderPrewarmSnapshot(
                state,
                options.MaximumDependencyCount,
                options.MaximumDependencyBytes,
                options.TimeBudgetMilliseconds * 1_000_000L,
                codecPrepared,
                candidateDependencyCount,
                preparedDependencyCount,
                cacheHitCount,
                preparedDependencyBytes,
                failureCount,
                durationNanoseconds,
                failureType);
        }
    }

    private async Task ExecuteAsync()
    {
        long activeStopwatchTicks = 0;
        lock (gate)
        {
            state = CoreClrProgramLoaderPrewarmState.Running;
        }

        CoreClrProgramLoaderPrewarmState finalState =
            CoreClrProgramLoaderPrewarmState.Completed;
        try
        {
            long phaseStartedTimestamp = Stopwatch.GetTimestamp();
            string[] candidates = Directory.Exists(assemblyDirectory)
                ? Directory.GetFiles(
                        assemblyDirectory,
                        "Finare.*.dll",
                        SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
            activeStopwatchTicks = checked(
                activeStopwatchTicks + ElapsedStopwatchTicks(phaseStartedTimestamp));
            lock (gate)
            {
                candidateDependencyCount = candidates.Length;
            }

            foreach (string candidate in candidates)
            {
                if (preparedDependencyCount >= options.MaximumDependencyCount)
                {
                    finalState = CoreClrProgramLoaderPrewarmState.DependencyLimitReached;
                    break;
                }
                long byteLength = new FileInfo(candidate).Length;
                if (byteLength <= 0 ||
                    byteLength > options.MaximumDependencyBytes - preparedDependencyBytes)
                {
                    finalState = CoreClrProgramLoaderPrewarmState.ByteBudgetReached;
                    break;
                }
                if (StopwatchTicksToNanoseconds(activeStopwatchTicks) >=
                    options.TimeBudgetMilliseconds * 1_000_000L)
                {
                    finalState = CoreClrProgramLoaderPrewarmState.TimeBudgetReached;
                    break;
                }

                try
                {
                    phaseStartedTimestamp = Stopwatch.GetTimestamp();
                    Assembly? sharedAssembly = FindLoadedAssembly(candidate);
                    FinareManagedDependencyVerification verification =
                        dependencyCache.Preload(candidate, sharedAssembly);
                    activeStopwatchTicks = checked(
                        activeStopwatchTicks +
                        ElapsedStopwatchTicks(phaseStartedTimestamp));
                    lock (gate)
                    {
                        preparedDependencyCount++;
                        preparedDependencyBytes = checked(
                            preparedDependencyBytes + byteLength);
                        if (verification.CacheHit)
                        {
                            cacheHitCount++;
                        }
                    }
                }
                catch (Exception exception)
                {
                    activeStopwatchTicks = checked(
                        activeStopwatchTicks +
                        ElapsedStopwatchTicks(phaseStartedTimestamp));
                    lock (gate)
                    {
                        failureCount++;
                        failureType ??= exception.GetType().FullName;
                    }
                }
            }

            await codecPreparationRequested.Task.ConfigureAwait(false);
            phaseStartedTimestamp = Stopwatch.GetTimestamp();
            WarmBinaryCodec();
            activeStopwatchTicks = checked(
                activeStopwatchTicks + ElapsedStopwatchTicks(phaseStartedTimestamp));
            lock (gate)
            {
                codecPrepared = true;
            }
            if (finalState == CoreClrProgramLoaderPrewarmState.Completed &&
                StopwatchTicksToNanoseconds(activeStopwatchTicks) >=
                    options.TimeBudgetMilliseconds * 1_000_000L)
            {
                finalState = CoreClrProgramLoaderPrewarmState.TimeBudgetReached;
            }
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                failureCount++;
                failureType ??= exception.GetType().FullName;
            }
        }
        finally
        {
            lock (gate)
            {
                durationNanoseconds = StopwatchTicksToNanoseconds(activeStopwatchTicks);
                state = failureCount > 0
                    ? CoreClrProgramLoaderPrewarmState.CompletedWithFailures
                    : finalState;
            }
            completion.TrySetResult();
        }
    }

    private static void WarmBinaryCodec()
    {
        byte[] content = Convert.FromBase64String(WarmupChunkBase64);
        FinareChunk chunk = FinareProgramChunk.Read(content);
        _ = FinareRuntimeManifestBinary.Decode(chunk.GetRequiredSection(
            FinareChunkSectionKind.RuntimeManifest).Data.Span);
        _ = FinareRuntimeDeclarationBinary.Decode(chunk.GetRequiredSection(
            FinareChunkSectionKind.DeclarationData).Data.Span);
        _ = FinareRuntimeBindingManifestBinary.Decode(chunk.GetRequiredSection(
            FinareChunkSectionKind.BindingManifest).Data.Span);
        _ = FinareRuntimeDependencyManifestBinary.Decode(chunk.GetRequiredSection(
            FinareChunkSectionKind.DependencyManifest).Data.Span);
        _ = typeof(CoreClrProgramLoaderPrewarmOperation).Assembly.GetType(
            typeof(CoreClrProgramLoaderPrewarmOperation).FullName!,
            throwOnError: true,
            ignoreCase: false);
    }

    private static Assembly? FindLoadedAssembly(string candidatePath)
    {
        string fullPath = Path.GetFullPath(candidatePath);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(assembly.Location) &&
                    string.Equals(
                        Path.GetFullPath(assembly.Location),
                        fullPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return assembly;
                }
            }
            catch (NotSupportedException)
            {
                // Dynamic assemblies do not expose a stable file location.
            }
        }
        return null;
    }

    private static long ElapsedStopwatchTicks(long startedTimestamp) =>
        Math.Max(0, Stopwatch.GetTimestamp() - startedTimestamp);

    private static long StopwatchTicksToNanoseconds(long elapsedTicks)
    {
        return Math.Max(
            0,
            (long)Math.Ceiling(elapsedTicks * 1_000_000_000d / Stopwatch.Frequency));
    }
}
