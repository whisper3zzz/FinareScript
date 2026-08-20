using System.Text.Json;
using UnifiedScript.CSharp;

namespace UnifiedScript.Tests;

public sealed class CSharpProgramSourceMapJsonTests
{
    private const string Source = """
        using UnifiedScript;

        namespace Tests.SourceMaps;

        public static class Rules
        {
            [Visual]
            public static int Identity(int value)
            {
                return value;
            }
        }
        """;

    [Fact]
    public void SourceMapJson_RoundTripsToCanonicalDeterministicJson()
    {
        CSharpProgramSourceMap sourceMap = EmitSourceMap();

        string json = CSharpProgramSourceMapJson.Serialize(sourceMap);
        CSharpProgramSourceMap roundTrip = CSharpProgramSourceMapJson.Deserialize(json);

        Assert.Equal(CSharpProgramSourceMap.CurrentSchemaVersion, roundTrip.SchemaVersion);
        Assert.Equal(json, CSharpProgramSourceMapJson.Serialize(roundTrip));
    }

    [Fact]
    public void SourceMapJson_RejectsUnknownSchemaVersion()
    {
        string json = CSharpProgramSourceMapJson.Serialize(EmitSourceMap()).Replace(
            CSharpProgramSourceMap.CurrentSchemaVersion,
            "unifiedscript.source-map/v0.0",
            StringComparison.Ordinal);

        JsonException exception = Assert.Throws<JsonException>(() =>
            CSharpProgramSourceMapJson.Deserialize(json));

        Assert.Contains("Unsupported source-map schema", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceMapJson_RejectsSemanticNodeWithDanglingIlOffset()
    {
        CSharpProgramSourceMap sourceMap = EmitSourceMap();
        CSharpMethodSourceMap method = Assert.Single(sourceMap.Methods);
        CSharpNodeIlMap node = Assert.Single(method.Nodes.Take(1));
        CSharpMethodSourceMap invalidMethod = method with
        {
            Nodes = [node with { IlOffsets = [int.MaxValue] }, .. method.Nodes.Skip(1)],
        };

        JsonException exception = Assert.Throws<JsonException>(() =>
            CSharpProgramSourceMapJson.Serialize(sourceMap with { Methods = [invalidMethod] }));

        Assert.Contains("invalid semantic-node mapping", exception.Message, StringComparison.Ordinal);
    }

    private static CSharpProgramSourceMap EmitSourceMap()
    {
        CSharpProgramEmitResult result = new CSharpProgramEmitter().Emit(
            CSharpCompilationRequest.Single(Source, "Tests/SourceMaps/Rules.cs"));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        return Assert.IsType<CSharpProgramImage>(result.Image).SourceMap;
    }
}
