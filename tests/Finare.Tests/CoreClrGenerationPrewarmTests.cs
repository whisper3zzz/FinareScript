using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Finare.Runtime;

namespace Finare.Tests;

public sealed class CoreClrGenerationPrewarmTests
{
    [Fact]
    public async Task Operation_StartsExplicitlyAndStopsAtMethodLimit()
    {
        var prepared = new ConcurrentQueue<string>();
        CoreClrGenerationPrewarmOperation operation =
            CoreClrGenerationPrewarmOperation.CreateForTests(
                typeof(PrewarmFixture),
                new CoreClrGenerationPrewarmOptions(
                    maximumMethodCount: 2,
                    timeBudget: TimeSpan.FromMilliseconds(100)),
                method => prepared.Enqueue(method.Name));

        Assert.Equal(
            CoreClrGenerationPrewarmState.Scheduled,
            operation.GetSnapshot().State);
        Assert.False(operation.Completion.IsCompleted);
        Assert.True(operation.Start());
        Assert.False(operation.Start());
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        CoreClrGenerationPrewarmSnapshot snapshot = operation.GetSnapshot();
        Assert.Equal(CoreClrGenerationPrewarmState.MethodLimitReached, snapshot.State);
        Assert.True(snapshot.CandidateMethodCount > snapshot.PreparedMethodCount);
        Assert.Equal(2, snapshot.PreparedMethodCount);
        Assert.Equal(2, prepared.Count);
        string[] expected = typeof(PrewarmFixture).GetMethods(
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly)
            .OrderBy(static method => method.MetadataToken)
            .Where(static method =>
                !method.IsAbstract &&
                !method.ContainsGenericParameters &&
                method.GetMethodBody() is not null)
            .Take(2)
            .Select(static method => method.Name)
            .ToArray();
        Assert.Equal(
            expected,
            prepared.ToArray());
    }

    [Fact]
    public async Task Operation_StopsBetweenMethodsWhenTimeBudgetExpires()
    {
        long timestamp = 0;
        CoreClrGenerationPrewarmOperation operation =
            CoreClrGenerationPrewarmOperation.CreateForTests(
                typeof(PrewarmFixture),
                new CoreClrGenerationPrewarmOptions(
                    maximumMethodCount: 64,
                    timeBudget: TimeSpan.FromMilliseconds(2)),
                _ => Interlocked.Exchange(
                    ref timestamp,
                    Stopwatch.Frequency / 50),
                () => Interlocked.Read(ref timestamp));

        Assert.True(operation.Start());
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        CoreClrGenerationPrewarmSnapshot snapshot = operation.GetSnapshot();
        Assert.Equal(CoreClrGenerationPrewarmState.TimeBudgetReached, snapshot.State);
        Assert.Equal(1, snapshot.PreparedMethodCount);
        Assert.True(snapshot.DurationNanoseconds >= 2_000_000);
    }

    [Fact]
    public async Task Cancellation_IsNonBlockingAndGenerationWorkFinishesCooperatively()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        CoreClrGenerationPrewarmOperation operation =
            CoreClrGenerationPrewarmOperation.CreateForTests(
                typeof(PrewarmFixture),
                new CoreClrGenerationPrewarmOptions(
                    maximumMethodCount: 64,
                    timeBudget: TimeSpan.FromMilliseconds(100)),
                _ =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                });

        Assert.True(operation.Start());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        long started = Stopwatch.GetTimestamp();
        operation.Cancel();
        TimeSpan cancellationDuration = Stopwatch.GetElapsedTime(started);

        Assert.False(operation.Completion.IsCompleted);
        Assert.True(cancellationDuration < TimeSpan.FromMilliseconds(100));
        release.Set();
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            CoreClrGenerationPrewarmState.Cancelled,
            operation.GetSnapshot().State);
    }

    [Fact]
    public async Task PreparationFailure_IsContainedInsideOptionalOptimization()
    {
        CoreClrGenerationPrewarmOperation operation =
            CoreClrGenerationPrewarmOperation.CreateForTests(
                typeof(PrewarmFixture),
                new CoreClrGenerationPrewarmOptions(),
                _ => throw new InvalidOperationException("prewarm fixture failure"));

        Assert.True(operation.Start());
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        CoreClrGenerationPrewarmSnapshot snapshot = operation.GetSnapshot();
        Assert.Equal(CoreClrGenerationPrewarmState.Failed, snapshot.State);
        Assert.Equal(typeof(InvalidOperationException).FullName, snapshot.FailureType);
        Assert.Equal(0, snapshot.PreparedMethodCount);
        Assert.True(operation.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FirstTickObservation_NeverWaitsAndRecordsPreparedCoverage()
    {
        CoreClrGenerationPrewarmOperation completed =
            CoreClrGenerationPrewarmOperation.CreateForTests(
                typeof(PrewarmFixture),
                new CoreClrGenerationPrewarmOptions(
                    maximumMethodCount: 1,
                    timeBudget: TimeSpan.FromMilliseconds(100)),
                _ => { });
        Assert.True(completed.Start());
        await completed.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        CoreClrGenerationPrewarmSnapshot completedSnapshot =
            completed.ObserveFirstTick();
        Assert.True(completedSnapshot.FirstTickObserved);
        Assert.True(completedSnapshot.CompletedBeforeFirstTick);
        Assert.Equal(1, completedSnapshot.PreparedMethodCount);
        Assert.Equal(
            completedSnapshot.PreparedMethodCount,
            completedSnapshot.PreparedBeforeFirstTickCount);

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        CoreClrGenerationPrewarmOperation running =
            CoreClrGenerationPrewarmOperation.CreateForTests(
                typeof(PrewarmFixture),
                new CoreClrGenerationPrewarmOptions(),
                _ =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                });
        Assert.True(running.Start());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        long started = Stopwatch.GetTimestamp();
        CoreClrGenerationPrewarmSnapshot runningSnapshot = running.ObserveFirstTick();
        TimeSpan observationDuration = Stopwatch.GetElapsedTime(started);
        Assert.False(runningSnapshot.CompletedBeforeFirstTick);
        Assert.True(observationDuration < TimeSpan.FromMilliseconds(100));

        running.Cancel();
        release.Set();
        await running.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(running.GetSnapshot().CompletedBeforeFirstTick);
    }

    [Fact]
    public void Options_EnforceHardProductBoundsAndAllowExplicitDisable()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CoreClrGenerationPrewarmOptions(maximumMethodCount: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CoreClrGenerationPrewarmOptions(
                CoreClrGenerationPrewarmOptions.MaximumSupportedMethodCount + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CoreClrGenerationPrewarmOptions(
                timeBudget: TimeSpan.FromMilliseconds(
                    CoreClrGenerationPrewarmOptions.MaximumSupportedTimeBudgetMilliseconds + 1)));
        Assert.Equal(
            0,
            CoreClrGenerationPrewarmOptions.Disabled.MaximumMethodCount);
    }

    [Fact]
    public void CandidateCancellationBeforeActivation_DoesNotScheduleWork()
    {
        int prepared = 0;
        CoreClrGenerationPrewarmOperation operation =
            CoreClrGenerationPrewarmOperation.CreateForTests(
                typeof(PrewarmFixture),
                new CoreClrGenerationPrewarmOptions(),
                _ => Interlocked.Increment(ref prepared));

        operation.Cancel();

        Assert.True(operation.Completion.IsCompletedSuccessfully);
        Assert.False(operation.Start());
        Assert.Equal(CoreClrGenerationPrewarmState.Cancelled, operation.GetSnapshot().State);
        Assert.Equal(0, Volatile.Read(ref prepared));
    }

    private sealed class PrewarmFixture
    {
        public int First(int value) => value + 1;

        private int Second(int value) => value + 2;

        public static int Third(int value) => value + 3;

        public T Generic<T>(T value) => value;
    }
}
