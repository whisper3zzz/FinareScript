using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Finare.Core;

namespace Finare.Runtime;

public sealed record FinareCompiledChunkProfilePhase(
    string Phase,
    long DurationNanoseconds,
    long BytesRead,
    long BytesCopied,
    int WorkItems);

public sealed record FinareCompiledChunkProfileTrial(
    int Trial,
    bool ProcessFirstLoad,
    long TotalLoadNanoseconds,
    long TotalProfileNanoseconds,
    long CurrentThreadAllocatedBytes,
    long TotalCopyBytes,
    int DependencyCacheHitCount,
    int DependencyCacheMissCount,
    IReadOnlyList<FinareCompiledChunkProfilePhase> Phases);

public sealed record FinareCompiledChunkLoadProfileReport(
    string SchemaVersion,
    string ProfilerVersion,
    string ChunkFormatVersion,
    string ContentDigest,
    int ByteLength,
    int TrialCount,
    bool EntryMethodJitPreparation,
    IReadOnlyList<FinareCompiledChunkProfilePhase> MedianPhases,
    IReadOnlyList<FinareCompiledChunkProfileTrial> Trials)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.compiled-chunk-load-profile/v0.1";
}

public static class FinareCompiledChunkLoadProfileJson
{
    public static string Serialize(
        FinareCompiledChunkLoadProfileReport report,
        bool writeIndented = true) =>
        JsonSerializer.Serialize(report, CreateOptions(writeIndented))
            .ReplaceLineEndings("\n") + "\n";

    public static FinareCompiledChunkLoadProfileReport Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        FinareCompiledChunkLoadProfileReport? report =
            JsonSerializer.Deserialize<FinareCompiledChunkLoadProfileReport>(
                json,
                CreateOptions(writeIndented: false));
        if (report is null ||
            report.SchemaVersion !=
                FinareCompiledChunkLoadProfileReport.CurrentSchemaVersion ||
            report.ProfilerVersion != "1.0" ||
            report.TrialCount <= 0 ||
            report.Trials.Count != report.TrialCount ||
            report.MedianPhases.Count == 0 ||
            !ProgramImageKey.TryParse(report.ContentDigest, out _) ||
            report.ByteLength <= 0)
        {
            throw new JsonException("The compiled chunk load profile is invalid.");
        }
        return report;
    }

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
}

public sealed class FinareCompiledChunkLoadProfiler
{
    public FinareCompiledChunkLoadProfileReport Profile(
        string chunkPath,
        IEnumerable<string>? referencePaths = null,
        int trialCount = 3,
        bool prepareEntryMethods = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkPath);
        if (trialCount is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(trialCount),
                "Compiled chunk profiling requires between 1 and 50 trials.");
        }

        string fullChunkPath = Path.GetFullPath(chunkPath);
        if (!File.Exists(fullChunkPath))
        {
            throw new FileNotFoundException(
                "The compiled chunk profile input does not exist.",
                fullChunkPath);
        }
        string[] references = (referencePaths ?? [])
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (string reference in references)
        {
            if (!File.Exists(reference))
            {
                throw new FileNotFoundException(
                    "A compiled chunk profile reference does not exist.",
                    reference);
            }
        }

        var trials = new List<FinareCompiledChunkProfileTrial>(trialCount);
        string? contentDigest = null;
        string? formatVersion = null;
        int? byteLength = null;
        for (int trial = 1; trial <= trialCount; trial++)
        {
            long totalStarted = Stopwatch.GetTimestamp();
            long openStarted = Stopwatch.GetTimestamp();
            using var stream = new FileStream(
                fullChunkPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            long fileOpenTicks = ElapsedStopwatchTicks(openStarted);
            CoreClrCompiledChunkLoadResult load =
                new CoreClrCompiledChunkHost().LoadProfiled(stream, references);
            CoreClrCompiledChunkSession session = load.Session;
            CoreClrCompiledChunkPrewarmProfile prewarm;
            long unloadTicks;
            try
            {
                prewarm = prepareEntryMethods
                    ? session.PrewarmEntryMethods()
                    : new CoreClrCompiledChunkPrewarmProfile(0, 0, 0, 0);
            }
            finally
            {
                long unloadStarted = Stopwatch.GetTimestamp();
                session.Dispose();
                unloadTicks = ElapsedStopwatchTicks(unloadStarted);
            }

            contentDigest ??= session.Chunk.ContentDigest;
            formatVersion ??= session.Chunk.FormatVersion;
            byteLength ??= session.Chunk.ByteLength;
            if (!string.Equals(
                    contentDigest,
                    session.Chunk.ContentDigest,
                    StringComparison.Ordinal) ||
                byteLength.Value != session.Chunk.ByteLength)
            {
                throw new InvalidDataException(
                    "The compiled chunk changed while profiling trials were running.");
            }

            IReadOnlyList<FinareCompiledChunkProfilePhase> phases = CreatePhases(
                fileOpenTicks,
                load.Profile,
                prewarm,
                unloadTicks);
            trials.Add(new FinareCompiledChunkProfileTrial(
                trial,
                ProcessFirstLoad: trial == 1,
                TicksToNanoseconds(load.Profile.TotalStopwatchTicks),
                TicksToNanoseconds(ElapsedStopwatchTicks(totalStarted)),
                checked(
                    load.Profile.CurrentThreadAllocatedBytes +
                    prewarm.CurrentThreadAllocatedBytes),
                load.Profile.TotalCopyBytes,
                load.Profile.DependencyCacheHitCount,
                load.Profile.DependencyCacheMissCount,
                phases));
        }

        string[] phaseNames = trials[0].Phases
            .Select(static phase => phase.Phase)
            .ToArray();
        FinareCompiledChunkProfilePhase[] medians = phaseNames.Select(name =>
        {
            FinareCompiledChunkProfilePhase[] values = trials
                .Select(trial => trial.Phases.Single(phase => phase.Phase == name))
                .ToArray();
            return new FinareCompiledChunkProfilePhase(
                name,
                Median(values.Select(static value => value.DurationNanoseconds)),
                Median(values.Select(static value => value.BytesRead)),
                Median(values.Select(static value => value.BytesCopied)),
                checked((int)Median(values.Select(static value => (long)value.WorkItems))));
        }).ToArray();

        return new FinareCompiledChunkLoadProfileReport(
            FinareCompiledChunkLoadProfileReport.CurrentSchemaVersion,
            "1.0",
            formatVersion ?? throw new InvalidOperationException(
                "Chunk profiling produced no format version."),
            contentDigest ?? throw new InvalidOperationException(
                "Chunk profiling produced no content digest."),
            byteLength ?? throw new InvalidOperationException(
                "Chunk profiling produced no byte length."),
            trialCount,
            prepareEntryMethods,
            medians,
            trials);
    }

    private static IReadOnlyList<FinareCompiledChunkProfilePhase> CreatePhases(
        long fileOpenTicks,
        CoreClrCompiledChunkLoadProfile load,
        CoreClrCompiledChunkPrewarmProfile prewarm,
        long unloadTicks)
    {
        FinareChunkReadProfile read = load.ChunkRead;
        return
        [
            Phase("fileOpen", fileOpenTicks),
            Phase("streamRead", read.StreamReadStopwatchTicks, read.StreamReadBytes),
            Phase(
                "inputMaterialization",
                read.InputMaterializationStopwatchTicks,
                bytesCopied: read.InputMaterializationBytes),
            Phase("headerValidation", read.HeaderValidationStopwatchTicks),
            Phase("wholeFileDigest", read.WholeFileDigestStopwatchTicks),
            Phase(
                "storageMaterialization",
                read.StorageMaterializationStopwatchTicks,
                bytesCopied: read.StorageMaterializationBytes),
            Phase("directoryValidation", read.DirectoryValidationStopwatchTicks),
            Phase("sectionDigests", read.SectionDigestStopwatchTicks),
            Phase("programValidation", load.ProgramValidationStopwatchTicks),
            Phase("runtimeManifestDecode", load.RuntimeManifestDecodeStopwatchTicks),
            Phase("declarationDecode", load.DeclarationDecodeStopwatchTicks),
            Phase("bindingDecode", load.BindingDecodeStopwatchTicks),
            Phase("dependencyDecode", load.DependencyDecodeStopwatchTicks),
            Phase("identityValidation", load.IdentityValidationStopwatchTicks),
            Phase(
                "dependencyValidation",
                load.DependencyValidationStopwatchTicks,
                bytesRead: load.DependencySnapshotCopyBytes,
                bytesCopied: load.DependencySnapshotCopyBytes,
                workItems: checked(
                    load.DependencyCacheHitCount + load.DependencyCacheMissCount)),
            Phase("loadContextCreation", load.LoadContextCreationStopwatchTicks),
            Phase(
                "managedAssemblyMaterialization",
                load.ManagedAssemblyMaterializationStopwatchTicks,
                bytesCopied: load.ManagedAssemblyCopyBytes),
            Phase(
                "portablePdbMaterialization",
                load.PortablePdbMaterializationStopwatchTicks,
                bytesCopied: load.PortablePdbCopyBytes),
            Phase("assemblyLoad", load.AssemblyLoadStopwatchTicks),
            Phase("entryTypeValidation", load.EntryTypeValidationStopwatchTicks),
            Phase(
                "entryMethodJitPreparation",
                prewarm.StopwatchTicks,
                workItems: prewarm.PreparedMethodCount),
            Phase("unloadRequest", unloadTicks),
        ];
    }

    private static FinareCompiledChunkProfilePhase Phase(
        string name,
        long stopwatchTicks,
        long bytesRead = 0,
        long bytesCopied = 0,
        int workItems = 0) => new(
            name,
            TicksToNanoseconds(stopwatchTicks),
            bytesRead,
            bytesCopied,
            workItems);

    private static long Median(IEnumerable<long> values)
    {
        long[] ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("A profiling median requires at least one value.");
        }
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : checked((ordered[middle - 1] + ordered[middle]) / 2);
    }

    private static long ElapsedStopwatchTicks(long started) =>
        Math.Max(0, Stopwatch.GetTimestamp() - started);

    private static long TicksToNanoseconds(long ticks) => checked((long)Math.Round(
        ticks * (1_000_000_000d / Stopwatch.Frequency)));
}
