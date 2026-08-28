using UnifiedScript.Core;
using UnifiedScript.Runtime;

namespace UnifiedScript.Tests;

public sealed class CoreClrProgramLoaderPrewarmTests
{
    [Fact]
    public async Task Prewarm_IsExplicitBoundedAndPopulatesVerifiedDependencies()
    {
        string directory = CreateDependencyDirectory(
            typeof(SemanticId).Assembly.Location,
            typeof(CoreClrGenerationPrewarmOperation).Assembly.Location);
        try
        {
            var cache = new UnifiedScriptManagedDependencyCache();
            CoreClrProgramLoaderPrewarmOperation operation =
                CoreClrProgramLoaderPrewarmOperation.Create(
                    directory,
                    cache,
                    new CoreClrProgramLoaderPrewarmOptions(8, 32 * 1024 * 1024, 250));

            Assert.Equal(
                CoreClrProgramLoaderPrewarmState.Scheduled,
                operation.GetSnapshot().State);
            Assert.True(operation.Start());
            Assert.False(operation.Start());
            await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));

            CoreClrProgramLoaderPrewarmSnapshot snapshot = operation.GetSnapshot();
            Assert.Equal(CoreClrProgramLoaderPrewarmState.Completed, snapshot.State);
            Assert.True(snapshot.CodecPrepared);
            Assert.Equal(2, snapshot.CandidateDependencyCount);
            Assert.Equal(2, snapshot.PreparedDependencyCount);
            Assert.Equal(0, snapshot.CacheHitCount);
            Assert.True(snapshot.PreparedDependencyBytes > 0);
            Assert.Equal(0, snapshot.FailureCount);
            Assert.Null(snapshot.FailureType);
            Assert.Equal(2, cache.GetSnapshot().EntryCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Prewarm_CanPrepareDependenciesBeforeCodecWorkIsReleased()
    {
        string directory = CreateDependencyDirectory(
            typeof(SemanticId).Assembly.Location,
            typeof(CoreClrGenerationPrewarmOperation).Assembly.Location);
        try
        {
            CoreClrProgramLoaderPrewarmOperation operation =
                CoreClrProgramLoaderPrewarmOperation.Create(
                    directory,
                    new UnifiedScriptManagedDependencyCache(),
                    new CoreClrProgramLoaderPrewarmOptions(8, 32 * 1024 * 1024, 250));

            Assert.True(operation.StartDependencyPreparation());
            await WaitUntilAsync(
                () => operation.GetSnapshot().PreparedDependencyCount == 2,
                TimeSpan.FromSeconds(5));
            Assert.False(operation.GetSnapshot().CodecPrepared);
            Assert.False(operation.Completion.IsCompleted);

            Assert.True(operation.StartCodecPreparation());
            Assert.False(operation.StartCodecPreparation());
            await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(operation.GetSnapshot().CodecPrepared);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Prewarm_EnforcesDependencyAndByteBudgets()
    {
        string directory = CreateDependencyDirectory(
            typeof(SemanticId).Assembly.Location,
            typeof(CoreClrGenerationPrewarmOperation).Assembly.Location);
        try
        {
            CoreClrProgramLoaderPrewarmOperation countBound =
                CoreClrProgramLoaderPrewarmOperation.Create(
                    directory,
                    new UnifiedScriptManagedDependencyCache(),
                    new CoreClrProgramLoaderPrewarmOptions(1, 32 * 1024 * 1024, 250));
            Assert.True(countBound.Start());
            await countBound.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(
                CoreClrProgramLoaderPrewarmState.DependencyLimitReached,
                countBound.GetSnapshot().State);
            Assert.Equal(1, countBound.GetSnapshot().PreparedDependencyCount);

            CoreClrProgramLoaderPrewarmOperation byteBound =
                CoreClrProgramLoaderPrewarmOperation.Create(
                    directory,
                    new UnifiedScriptManagedDependencyCache(),
                    new CoreClrProgramLoaderPrewarmOptions(8, 1, 250));
            Assert.True(byteBound.Start());
            await byteBound.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(
                CoreClrProgramLoaderPrewarmState.ByteBudgetReached,
                byteBound.GetSnapshot().State);
            Assert.Equal(0, byteBound.GetSnapshot().PreparedDependencyCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Prewarm_ContainsInvalidAdjacentAssemblyFailure()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "UnifiedScript.Invalid.dll"), "invalid");
        try
        {
            CoreClrProgramLoaderPrewarmOperation operation =
                CoreClrProgramLoaderPrewarmOperation.Create(
                    directory,
                    new UnifiedScriptManagedDependencyCache(),
                    new CoreClrProgramLoaderPrewarmOptions(8, 1024 * 1024, 250));
            Assert.True(operation.Start());
            await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));

            CoreClrProgramLoaderPrewarmSnapshot snapshot = operation.GetSnapshot();
            Assert.Equal(
                CoreClrProgramLoaderPrewarmState.CompletedWithFailures,
                snapshot.State);
            Assert.True(snapshot.CodecPrepared);
            Assert.Equal(1, snapshot.CandidateDependencyCount);
            Assert.Equal(0, snapshot.PreparedDependencyCount);
            Assert.Equal(1, snapshot.FailureCount);
            Assert.NotNull(snapshot.FailureType);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Options_EnforceHardBoundsAndZeroDisables()
    {
        CoreClrProgramLoaderPrewarmOperation disabled =
            CoreClrProgramLoaderPrewarmOperation.Create(
                Environment.CurrentDirectory,
                options: CoreClrProgramLoaderPrewarmOptions.Disabled);
        Assert.False(disabled.Start());
        Assert.True(disabled.Completion.IsCompletedSuccessfully);
        Assert.Equal(
            CoreClrProgramLoaderPrewarmState.Disabled,
            disabled.GetSnapshot().State);
        Assert.False(disabled.GetSnapshot().CodecPrepared);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CoreClrProgramLoaderPrewarmOperation.Create(
                Environment.CurrentDirectory,
                options: new CoreClrProgramLoaderPrewarmOptions(
                    CoreClrProgramLoaderPrewarmOptions.HardMaximumDependencyCount + 1,
                    1,
                    1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CoreClrProgramLoaderPrewarmOperation.Create(
                Environment.CurrentDirectory,
                options: new CoreClrProgramLoaderPrewarmOptions(
                    1,
                    CoreClrProgramLoaderPrewarmOptions.HardMaximumDependencyBytes + 1,
                    1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CoreClrProgramLoaderPrewarmOperation.Create(
                Environment.CurrentDirectory,
                options: new CoreClrProgramLoaderPrewarmOptions(
                    1,
                    1,
                    CoreClrProgramLoaderPrewarmOptions.HardMaximumTimeBudgetMilliseconds + 1)));
    }

    private static string CreateDependencyDirectory(params string[] dependencies)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (string dependency in dependencies)
        {
            File.Copy(dependency, Path.Combine(directory, Path.GetFileName(dependency)));
        }
        return directory;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + checked((long)timeout.TotalMilliseconds);
        while (!predicate())
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException("The prewarm condition was not reached in time.");
            }
            await Task.Delay(10);
        }
    }
}
