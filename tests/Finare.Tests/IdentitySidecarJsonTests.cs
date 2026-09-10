using System.Text.Json;
using Finare.Core;

namespace Finare.Tests;

public sealed class IdentitySidecarJsonTests
{
    [Fact]
    public void Serialize_NormalizesEntryOrderAndRoundTrips()
    {
        IdentitySidecarDocument empty = IdentitySidecarDocument.CreateEmpty("Game/Script.cs");
        IdentityEntry first = CreateEntry(SemanticId.FromKey("identity-a"), 10);
        IdentityEntry second = CreateEntry(SemanticId.FromKey("identity-b"), 20);
        IdentitySidecarDocument document = empty with
        {
            Generation = 1,
            SourceDigest = Digest('1'),
            Entries = [second, first],
        };

        string json = IdentitySidecarJson.Serialize(document);
        IdentitySidecarDocument roundTrip = IdentitySidecarJson.Deserialize(json);

        Assert.Equal(
            roundTrip.Entries.OrderBy(static entry => entry.Id.Value).Select(static entry => entry.Id),
            roundTrip.Entries.Select(static entry => entry.Id));
        Assert.Equal(json, IdentitySidecarJson.Serialize(roundTrip));
    }

    [Fact]
    public void Deserialize_RejectsIdentitySharedByLiveEntryAndTombstone()
    {
        SemanticId id = SemanticId.FromKey("duplicate-live-tombstone");
        IdentitySidecarDocument document = IdentitySidecarDocument.CreateEmpty("Game/Script.cs") with
        {
            Generation = 1,
            SourceDigest = Digest('2'),
            Entries = [CreateEntry(id, 0)],
            Tombstones = [new IdentityTombstone(id, "literal", "Game.Script.Run()", 1)],
        };
        string json = System.Text.Json.JsonSerializer.Serialize(document, UnifiedJson.CreateOptions());

        Assert.Throws<JsonException>(() => IdentitySidecarJson.Deserialize(json));
    }

    private static IdentityEntry CreateEntry(SemanticId id, int start) => new(
        id,
        "literal",
        "Game.Script.Run()",
        "returnValue",
        Digest('a'),
        Digest('b'),
        Digest('c'),
        new IdentityAnchor(start, 1),
        Digest('d'),
        Digest('e'),
        1);

    private static string Digest(char value) => $"sha256:{new string(value, 64)}";
}
