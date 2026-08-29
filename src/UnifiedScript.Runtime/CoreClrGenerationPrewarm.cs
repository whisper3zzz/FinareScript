using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace UnifiedScript.Runtime;

public enum CoreClrGenerationPrewarmState
{
    Scheduled,
    Running,
    Completed,
    MethodLimitReached,
    TimeBudgetReached,
    Cancelled,
    Failed,
    Disabled,
}

public sealed record CoreClrGenerationPrewarmOptions
{
    public const int DefaultMaximumMethodCount = 64;
    public const int MaximumSupportedMethodCount = 1024;
    public const int DefaultTimeBudgetMilliseconds = 8;
    public const int MaximumSupportedTimeBudgetMilliseconds = 100;

    public CoreClrGenerationPrewarmOptions(
        int maximumMethodCount = DefaultMaximumMethodCount,
        TimeSpan? timeBudget = null)
    {
        TimeSpan resolvedBudget = timeBudget ??
            TimeSpan.FromMilliseconds(DefaultTimeBudgetMilliseconds);
        if (maximumMethodCount is < 0 or > MaximumSupportedMethodCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMethodCount),
                maximumMethodCount,
                $"Generation prewarm accepts 0 to {MaximumSupportedMethodCount} methods.");
        }
        if (resolvedBudget <= TimeSpan.Zero ||
            resolvedBudget > TimeSpan.FromMilliseconds(MaximumSupportedTimeBudgetMilliseconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeBudget),
                resolvedBudget,
                $"Generation prewarm requires a positive budget no greater than " +
                $"{MaximumSupportedTimeBudgetMilliseconds} ms.");
        }

        MaximumMethodCount = maximumMethodCount;
        TimeBudget = resolvedBudget;
    }

    public int MaximumMethodCount { get; }

    public TimeSpan TimeBudget { get; }

    public static CoreClrGenerationPrewarmOptions Default { get; } = new();

    public static CoreClrGenerationPrewarmOptions Disabled { get; } = new(0);
}

public sealed record CoreClrGenerationPrewarmSnapshot(
    CoreClrGenerationPrewarmState State,
    int MaximumMethodCount,
    long TimeBudgetNanoseconds,
    int CandidateMethodCount,
    int PreparedMethodCount,
    int SkippedMethodCount,
    long DurationNanoseconds,
    bool FirstTickObserved,
    int PreparedBeforeFirstTickCount,
    bool CompletedBeforeFirstTick,
    string? FailureType);

/// <summary>
/// Performs best-effort JIT preparation on the thread pool after a generation
/// has activated. Method count and elapsed-time checks bound work between JIT
/// operations; cancellation never waits for an in-flight PrepareMethod call.
/// </summary>
public sealed class CoreClrGenerationPrewarmOperation
{
    private readonly object _gate = new();
    private readonly Type[] _entryTypes;
    private readonly CoreClrGenerationPrewarmOptions _options;
    private readonly Action<MethodInfo> _prepareMethod;
    private readonly Func<long> _getTimestamp;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private CoreClrGenerationPrewarmState _state;
    private int _candidateMethodCount;
    private int _preparedMethodCount;
    private int _skippedMethodCount;
    private long _durationNanoseconds;
    private bool _firstTickObserved;
    private int _preparedBeforeFirstTickCount;
    private bool _completedBeforeFirstTick;
    private string? _failureType;

    private CoreClrGenerationPrewarmOperation(
        IEnumerable<Type> entryTypes,
        CoreClrGenerationPrewarmOptions options,
        Action<MethodInfo> prepareMethod,
        Func<long>? getTimestamp = null)
    {
        _entryTypes = entryTypes
            .Select(type => type ?? throw new ArgumentException(
                "Generation prewarm entry types cannot contain null.",
                nameof(entryTypes)))
            .Distinct()
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        if (_entryTypes.Length == 0)
        {
            throw new ArgumentException(
                "Generation prewarm requires at least one entry type.",
                nameof(entryTypes));
        }
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _prepareMethod = prepareMethod ?? throw new ArgumentNullException(nameof(prepareMethod));
        _getTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
        _state = options.MaximumMethodCount == 0
            ? CoreClrGenerationPrewarmState.Disabled
            : CoreClrGenerationPrewarmState.Scheduled;
        if (_state == CoreClrGenerationPrewarmState.Disabled)
        {
            _completion.TrySetResult();
        }
    }

    public Task Completion => _completion.Task;

    public static CoreClrGenerationPrewarmOperation Create(
        Type entryType,
        CoreClrGenerationPrewarmOptions? options = null) =>
        Create([entryType], options);

    public static CoreClrGenerationPrewarmOperation Create(
        IEnumerable<Type> entryTypes,
        CoreClrGenerationPrewarmOptions? options = null) =>
        new(
            entryTypes ?? throw new ArgumentNullException(nameof(entryTypes)),
            options ?? CoreClrGenerationPrewarmOptions.Default,
            static method => RuntimeHelpers.PrepareMethod(method.MethodHandle));

    internal static CoreClrGenerationPrewarmOperation CreateForTests(
        Type entryType,
        CoreClrGenerationPrewarmOptions options,
        Action<MethodInfo> prepareMethod,
        Func<long>? getTimestamp = null) =>
        new([entryType], options, prepareMethod, getTimestamp);

    public bool Start()
    {
        lock (_gate)
        {
            if (_state != CoreClrGenerationPrewarmState.Scheduled)
            {
                return false;
            }
            _state = CoreClrGenerationPrewarmState.Running;
            try
            {
                _ = Task.Run(Run);
                return true;
            }
            catch (Exception exception)
            {
                _state = CoreClrGenerationPrewarmState.Failed;
                _failureType = exception.GetType().FullName;
                _completion.TrySetResult();
                return false;
            }
        }
    }

    public void Cancel()
    {
        _cancellation.Cancel();
        lock (_gate)
        {
            if (_state == CoreClrGenerationPrewarmState.Scheduled)
            {
                _state = CoreClrGenerationPrewarmState.Cancelled;
                _completion.TrySetResult();
            }
        }
    }

    public CoreClrGenerationPrewarmSnapshot ObserveFirstTick()
    {
        lock (_gate)
        {
            if (!_firstTickObserved)
            {
                _firstTickObserved = true;
                _preparedBeforeFirstTickCount = _preparedMethodCount;
                _completedBeforeFirstTick = IsSuccessfulTerminalState(_state) &&
                    _preparedMethodCount > 0;
            }
            return CreateSnapshot();
        }
    }

    public CoreClrGenerationPrewarmSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return CreateSnapshot();
        }
    }

    private void Run()
    {
        long started = _getTimestamp();
        try
        {
            CancellationToken cancellationToken = _cancellation.Token;
            MethodInfo[] methods = _entryTypes
                .SelectMany(static type => type.GetMethods(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly))
                .OrderBy(static method => method.DeclaringType?.FullName, StringComparer.Ordinal)
                .ThenBy(static method => method.MetadataToken)
                .ToArray();
            lock (_gate)
            {
                _candidateMethodCount = methods.Length;
            }

            foreach (MethodInfo method in methods)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Complete(CoreClrGenerationPrewarmState.Cancelled, started);
                    return;
                }
                if (Elapsed(started) >= _options.TimeBudget)
                {
                    Complete(CoreClrGenerationPrewarmState.TimeBudgetReached, started);
                    return;
                }

                int prepared;
                lock (_gate)
                {
                    prepared = _preparedMethodCount;
                }
                if (prepared >= _options.MaximumMethodCount)
                {
                    Complete(CoreClrGenerationPrewarmState.MethodLimitReached, started);
                    return;
                }
                if (method.IsAbstract ||
                    method.ContainsGenericParameters ||
                    method.GetMethodBody() is null)
                {
                    lock (_gate)
                    {
                        _skippedMethodCount++;
                    }
                    continue;
                }

                _prepareMethod(method);
                lock (_gate)
                {
                    _preparedMethodCount++;
                }
            }

            Complete(
                cancellationToken.IsCancellationRequested
                    ? CoreClrGenerationPrewarmState.Cancelled
                    : CoreClrGenerationPrewarmState.Completed,
                started);
        }
        catch (Exception exception)
        {
            Complete(
                _cancellation.IsCancellationRequested
                    ? CoreClrGenerationPrewarmState.Cancelled
                    : CoreClrGenerationPrewarmState.Failed,
                started,
                exception.GetType().FullName);
        }
    }

    private void Complete(
        CoreClrGenerationPrewarmState state,
        long started,
        string? failureType = null)
    {
        lock (_gate)
        {
            _state = state;
            _durationNanoseconds = DurationNanoseconds(started);
            _failureType = failureType;
        }
        _completion.TrySetResult();
    }

    private CoreClrGenerationPrewarmSnapshot CreateSnapshot() => new(
        _state,
        _options.MaximumMethodCount,
        checked((long)Math.Ceiling(_options.TimeBudget.TotalMilliseconds * 1_000_000d)),
        _candidateMethodCount,
        _preparedMethodCount,
        _skippedMethodCount,
        _durationNanoseconds,
        _firstTickObserved,
        _preparedBeforeFirstTickCount,
        _completedBeforeFirstTick,
        _failureType);

    private static bool IsSuccessfulTerminalState(CoreClrGenerationPrewarmState state) =>
        state is CoreClrGenerationPrewarmState.Completed or
            CoreClrGenerationPrewarmState.MethodLimitReached or
            CoreClrGenerationPrewarmState.TimeBudgetReached;

    private TimeSpan Elapsed(long started) =>
        Stopwatch.GetElapsedTime(started, _getTimestamp());

    private long DurationNanoseconds(long started) => checked(
        (long)Math.Ceiling(
            Math.Max(0, _getTimestamp() - started) *
            1_000_000_000d /
            Stopwatch.Frequency));
}
