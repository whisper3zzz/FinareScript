using System.IO.Compression;
using System.Text;
using System.Text.Json;
using UnifiedScript.Core;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnifiedScriptDiagnosticsBundleTests
{
    [Fact]
    public void SafeDefaultBundleIsDeterministicAllowlistedBoundedAndRedacted()
    {
        using var fixture = new TemporaryDirectory();
        string input = Path.Combine(fixture.Path, "input");
        Directory.CreateDirectory(input);
        WriteSensitiveCiReport(Path.Combine(input, "a-ci.json"));
        File.WriteAllText(
            Path.Combine(input, "b-budget.json"),
            """
            {
              "schemaVersion": "unifiedscript.performance-budget-report/v0.1",
              "status": "qualified",
              "weightedRatio": 0.55
            }
            """);
        File.WriteAllText(
            Path.Combine(input, "source-derived.json"),
            """
            {
              "schemaVersion": "unifiedscript.hir/v0.9",
              "sourceText": "proprietary source"
            }
            """);
        File.WriteAllText(Path.Combine(input, "malformed.json"), "{broken");
        File.WriteAllText(Path.Combine(input, "runtime.log"), "token=raw-secret");
        File.WriteAllText(Path.Combine(input, "Gameplay.cs"), "class SecretGameplay {}");

        UnifiedScriptDiagnosticsPolicy policy = LoadPolicy();
        UnifiedScriptDiagnosticsBundleResult first =
            new UnifiedScriptDiagnosticsBundleBuilder().Build(input, policy);
        UnifiedScriptDiagnosticsBundleResult second =
            new UnifiedScriptDiagnosticsBundleBuilder().Build(input, policy);

        Assert.Equal(first.Archive, second.Archive);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal("safe-default", first.Manifest.PrivacyMode);
        Assert.Equal(6, first.Manifest.ScannedFileCount);
        Assert.Equal(2, first.Manifest.ReportCount);
        Assert.Equal(2, first.Manifest.Skipped.NonJson);
        Assert.Equal(1, first.Manifest.Skipped.InvalidJson);
        Assert.Equal(1, first.Manifest.Skipped.UnapprovedSchema);
        Assert.All(first.Manifest.Reports, static report =>
            Assert.StartsWith("reports/", report.Path, StringComparison.Ordinal));
        Assert.True(first.Manifest.Reports.Sum(static report => report.RedactionCount) >= 4);

        using var archive = new ZipArchive(
            new MemoryStream(first.Archive, writable: false),
            ZipArchiveMode.Read);
        Assert.Equal(
            ["manifest.json", "reports/0001-report.json", "reports/0002-report.json"],
            archive.Entries.Select(static entry => entry.FullName).ToArray());
        Assert.All(archive.Entries, static entry =>
            Assert.Equal(new DateTime(2000, 1, 1, 0, 0, 0), entry.LastWriteTime.DateTime));

        string bundleText = string.Join(
            "\n",
            archive.Entries.Select(ReadEntry));
        Assert.DoesNotContain("D:\\Private\\Game", bundleText, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-secret", bundleText, StringComparison.Ordinal);
        Assert.DoesNotContain("proprietary source", bundleText, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretGameplay", bundleText, StringComparison.Ordinal);
        Assert.DoesNotContain("ordinary but proprietary", bundleText, StringComparison.Ordinal);
        Assert.Contains("\\u003Credacted\\u003E", bundleText, StringComparison.Ordinal);
        Assert.Contains("\\u003Credacted-message\\u003E", bundleText, StringComparison.Ordinal);
        Assert.Contains("\\u003Credacted-string\\u003E", bundleText, StringComparison.Ordinal);
        Assert.Contains("\"sourceRevision\": \"abc123\"", bundleText, StringComparison.Ordinal);
        Assert.Contains("\"weightedRatio\": 0.55", bundleText, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundsSkipReportCountInputBytesAndSanitizedBundleOverflow()
    {
        using var fixture = new TemporaryDirectory();
        string input = Path.Combine(fixture.Path, "input");
        Directory.CreateDirectory(input);
        WriteSensitiveCiReport(Path.Combine(input, "a.json"));
        WriteSensitiveCiReport(Path.Combine(input, "b.json"));
        UnifiedScriptDiagnosticsPolicy policy = LoadPolicy() with
        {
            MaximumReportCount = 1,
        };

        UnifiedScriptDiagnosticsBundleResult result =
            new UnifiedScriptDiagnosticsBundleBuilder().Build(input, policy);

        Assert.Equal(1, result.Manifest.ReportCount);
        Assert.Equal(1, result.Manifest.Skipped.ReportLimit);
        using var archive = new ZipArchive(
            new MemoryStream(result.Archive, writable: false),
            ZipArchiveMode.Read);
        Assert.Equal(2, archive.Entries.Count);

        UnifiedScriptDiagnosticsBundleResult inputBound =
            new UnifiedScriptDiagnosticsBundleBuilder().Build(input, LoadPolicy() with
            {
                MaximumReportBytes = 64,
                MaximumBundleBytes = 64,
            });
        Assert.Equal(0, inputBound.Manifest.ReportCount);
        Assert.Equal(2, inputBound.Manifest.Skipped.Oversized);

        UnifiedScriptDiagnosticsBundleResult bundleBound =
            new UnifiedScriptDiagnosticsBundleBuilder().Build(input, LoadPolicy() with
            {
                MaximumReportBytes = result.Manifest.ReportBytes,
                MaximumBundleBytes = result.Manifest.ReportBytes,
            });
        Assert.Equal(1, bundleBound.Manifest.ReportCount);
        Assert.Equal(1, bundleBound.Manifest.Skipped.Oversized);
    }

    [Fact]
    public void CliUsesBundledPolicyAndWritesTheSameArchiveTwice()
    {
        using var fixture = new TemporaryDirectory();
        string input = Path.Combine(fixture.Path, "input");
        Directory.CreateDirectory(input);
        WriteSensitiveCiReport(Path.Combine(input, "ci.json"));
        string firstPath = Path.Combine(fixture.Path, "first.zip");
        string secondPath = Path.Combine(fixture.Path, "second.zip");

        int firstExit = UnifiedScript.Cli.Program.Main([
            "diagnostics-bundle",
            input,
            "--out",
            firstPath,
        ]);
        int secondExit = UnifiedScript.Cli.Program.Main([
            "diagnostics-bundle",
            input,
            "--out",
            secondPath,
        ]);

        Assert.Equal(0, firstExit);
        Assert.Equal(0, secondExit);
        Assert.Equal(File.ReadAllBytes(firstPath), File.ReadAllBytes(secondPath));
    }

    [Fact]
    public void PolicyRejectsDuplicateOrNonUnifiedScriptSchemaSelectors()
    {
        UnifiedScriptDiagnosticsPolicy policy = LoadPolicy();
        Assert.Contains(
            "unifiedscript.generation-prewarm-trial/v0.1",
            policy.AllowedSchemas);
        Assert.Contains(
            "unifiedscript.generation-prewarm-experiment/v0.1",
            policy.AllowedSchemas);
        Assert.Throws<JsonException>(() => UnifiedScriptDiagnosticsPolicyJson.Deserialize(
            UnifiedScriptDiagnosticsPolicyJson.Serialize(policy with
            {
                AllowedSchemas = [policy.AllowedSchemas[0], policy.AllowedSchemas[0]],
            })));
        Assert.Throws<JsonException>(() => UnifiedScriptDiagnosticsPolicyJson.Deserialize(
            UnifiedScriptDiagnosticsPolicyJson.Serialize(policy with
            {
                AllowedSchemas = ["thirdparty.report/v1"],
            })));
    }

    private static string PolicyPath =>
        Path.Combine(AppContext.BaseDirectory, "unifiedscript.diagnostics-policy.json");

    private static UnifiedScriptDiagnosticsPolicy LoadPolicy() =>
        UnifiedScriptDiagnosticsPolicyJson.Deserialize(File.ReadAllText(PolicyPath));

    private static void WriteSensitiveCiReport(string path) => File.WriteAllText(
        path,
        """
        {
          "schemaVersion": "unifiedscript.ci-lane/v0.1",
          "status": "failed",
          "sourceRevision": "abc123",
          "configuration": "D:\\Private\\Game\\Build",
          "engineRoot": "D:\\Private\\Game\\UE_5.8",
          "sourceText": "class Proprietary {}",
          "token": "raw-secret",
          "unexpectedPayload": "ordinary but proprietary",
          "failure": {
            "message": "Build failed under D:\\Private\\Game\\Project"
          }
        }
        """);

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using Stream input = entry.Open();
        using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"UnifiedScriptDiagnostics-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
