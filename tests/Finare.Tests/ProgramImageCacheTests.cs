using System.Text.Json;
using Finare.Core;
using Finare.CSharp;
using Finare.Runtime;

namespace Finare.Tests;

public sealed class ProgramImageCacheTests
{
    private const string Source = """
        using Finare;

        namespace Tests.Reload;

        public static class Rules
        {
            [Visual]
            public static int Run(int value)
            {
                return value + 1;
            }
        }
        """;

    [Fact]
    public void GetOrAdd_ReusesImageByDeterministicRequestFingerprint()
    {
        var cache = new CoreClrProgramImageCache();
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            Source,
            "Tests/Reload/Rules.cs");

        ProgramImageCacheResult first = cache.GetOrAdd(request);
        ProgramImageCacheResult second = cache.GetOrAdd(request);

        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        Assert.Equal(first.ImageKey, second.ImageKey);
        Assert.Same(first.Image, second.Image);
        Assert.Equal(1, cache.Count);
        Assert.Equal(1, cache.MissCount);
        Assert.Equal(1, cache.HitCount);
    }

    [Fact]
    public void GetOrAdd_SourceChangeProducesAnotherContentAddress()
    {
        var cache = new CoreClrProgramImageCache();

        ProgramImageCacheResult first = cache.GetOrAdd(CSharpCompilationRequest.Single(
            Source,
            "Tests/Reload/Rules.cs"));
        ProgramImageCacheResult changed = cache.GetOrAdd(CSharpCompilationRequest.Single(
            Source.Replace("value + 1", "value + 2", StringComparison.Ordinal),
            "Tests/Reload/Rules.cs"));

        Assert.False(changed.CacheHit);
        Assert.NotEqual(first.ImageKey, changed.ImageKey);
        Assert.Equal(2, cache.Count);
        Assert.Equal(2, cache.MissCount);
    }

    [Fact]
    public void GetOrAdd_EvictsLeastRecentlyUsedRequestAtCapacity()
    {
        var cache = new CoreClrProgramImageCache(capacity: 1);
        CSharpCompilationRequest firstRequest = CSharpCompilationRequest.Single(
            Source,
            "Tests/Reload/Rules.cs");
        CSharpCompilationRequest secondRequest = CSharpCompilationRequest.Single(
            Source.Replace("value + 1", "value + 2", StringComparison.Ordinal),
            "Tests/Reload/Rules.cs");

        _ = cache.GetOrAdd(firstRequest);
        _ = cache.GetOrAdd(secondRequest);
        ProgramImageCacheResult reloaded = cache.GetOrAdd(firstRequest);

        Assert.False(reloaded.CacheHit);
        Assert.Equal(1, cache.Count);
        Assert.Equal(3, cache.MissCount);
        Assert.Equal(0, cache.HitCount);
    }

    [Fact]
    public void ProgramImageKey_UsesStrictScalarJsonAndRoundTrips()
    {
        ProgramImageKey key = new CoreClrProgramImageCache()
            .GetOrAdd(CSharpCompilationRequest.Single(Source, "Tests/Reload/Rules.cs"))
            .ImageKey;
        JsonSerializerOptions options = UnifiedJson.CreateOptions();

        string json = JsonSerializer.Serialize(key, options);
        ProgramImageKey roundTrip = JsonSerializer.Deserialize<ProgramImageKey>(json, options);

        Assert.Equal(key, roundTrip);
        Assert.Equal($"\"{key}\"", json);
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ProgramImageKey>("\"sha256:invalid\"", options));
    }
}
