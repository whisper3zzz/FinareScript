using System.Text.Json;
using Finare.Core;

namespace Finare.Tests;

[Trait("Category", "Compiler")]
public sealed class SemanticIdTests
{
    [Fact]
    public void New_ProducesValidDistinctRandomIds()
    {
        SemanticId first = SemanticId.New();
        SemanticId second = SemanticId.New();

        Assert.False(first.IsEmpty);
        Assert.NotEqual(first, second);
        Assert.True(SemanticId.TryParse(first.Value, out _));
    }

    [Fact]
    public void FromKey_IsDeterministicAndDistinct()
    {
        SemanticId first = SemanticId.FromKey("method|Samples.Door.BeginPlay()");
        SemanticId again = SemanticId.FromKey("method|Samples.Door.BeginPlay()");
        SemanticId other = SemanticId.FromKey("method|Samples.Door.Tick(float)");

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.Matches("^us_[0-9a-f]{32}$", first.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("us_not_hex")]
    public void TryParse_RejectsInvalidValues(string? value)
    {
        Assert.False(SemanticId.TryParse(value, out _));
    }

    [Fact]
    public void Json_UsesValidatedStringRepresentation()
    {
        SemanticId id = SemanticId.FromKey("method|Samples.Door.BeginPlay()");
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented: false);

        string json = JsonSerializer.Serialize(id, options);

        Assert.Equal($"\"{id.Value}\"", json);
        Assert.Equal(id, JsonSerializer.Deserialize<SemanticId>(json, options));
    }
}
