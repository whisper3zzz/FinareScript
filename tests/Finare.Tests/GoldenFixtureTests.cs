using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Finare.CSharp;
using Finare.Core;
using Finare.Graph;
using Finare.Semantics;

namespace Finare.Tests;

public sealed class GoldenFixtureTests
{
    [Fact]
    public void MinimalVisualProgram_MatchesGoldenArtifactDigests()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        string source = File.ReadAllText(Path.Combine(fixtureDirectory, "GoldenVisual.cs"));
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(fixtureDirectory, "GoldenVisual.golden.json")));
        string sourcePath = manifest.RootElement.GetProperty("sourcePath").GetString()!;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(source, sourcePath);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string hir = HirJson.Serialize(HirDocument.Create(result.Hir!)) + "\n";
        string semantics = SemanticJson.Serialize(result.Semantics!) + "\n";
        string graph = GraphJson.Serialize(new HirGraphProjector().Project(result.Hir!, result.Semantics!)) + "\n";
        JsonElement artifacts = manifest.RootElement.GetProperty("artifacts");
        AssertGolden(hir, artifacts.GetProperty("hir"));
        AssertGolden(semantics, artifacts.GetProperty("semantics"));
        AssertGolden(graph, artifacts.GetProperty("graph"));
    }

    private static void AssertGolden(string content, JsonElement golden)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        string digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Assert.Equal(golden.GetProperty("sha256").GetString(), digest);
        Assert.Equal(golden.GetProperty("utf8Bytes").GetInt32(), bytes.Length);
        Assert.Contains(golden.GetProperty("schemaVersion").GetString()!, content);
    }
}
