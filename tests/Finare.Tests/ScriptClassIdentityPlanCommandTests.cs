using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Finare.Cli;
using Finare.Core;

namespace Finare.Tests;

[Trait("Category", "Authoring")]
public sealed class ScriptClassIdentityPlanCommandTests
{
    [Fact]
    public void MainPlansRenameWithoutChangingSourceOrSidecar()
    {
        using Fixture fixture = Fixture.Create();
        byte[] originalSource = File.ReadAllBytes(fixture.SourcePath);
        byte[] originalSidecar = File.ReadAllBytes(fixture.SidecarPath);
        string targetPath = Path.Combine(fixture.SourceDirectory, "IdentityPlanRenamed.cs");
        string targetSource = fixture.SourceText.Replace("IdentityPlanBase", "IdentityPlanRenamed", StringComparison.Ordinal);
        string requestPath = fixture.WriteRequest(
            fixture.SourcePath,
            targetPath,
            targetSource,
            fixture.Digest(originalSource),
            fixture.Digest(originalSidecar),
            preserveIdentity: true);
        string outputPath = Path.Combine(fixture.Root, "rename.plan.json");

        int exitCode = Program.Main([
            "script-class-identity-plan",
            fixture.ProjectPath,
            "--request", requestPath,
            "--out", outputPath,
            "--dotnet", Fixture.DotNetPath,
        ]);

        Assert.Equal(0, exitCode);
        Assert.Equal(originalSource, File.ReadAllBytes(fixture.SourcePath));
        Assert.Equal(originalSidecar, File.ReadAllBytes(fixture.SidecarPath));
        using JsonDocument output = JsonDocument.Parse(File.ReadAllText(outputPath));
        JsonElement document = output.RootElement.GetProperty("documents")[0];
        Assert.Equal(Fixture.Normalize(targetPath), document.GetProperty("targetPath").GetString());
        Assert.NotNull(document.GetProperty("identitySidecarJson").GetString());
        IdentitySidecarDocument sidecar = IdentitySidecarJson.Deserialize(
            document.GetProperty("identitySidecarJson").GetString()!);
        Assert.Equal(Fixture.Normalize(targetPath), sidecar.SourcePath);
        Assert.Contains(sidecar.Entries, entry => entry.Owner.Contains("IdentityPlanRenamed", StringComparison.Ordinal));
        Assert.Equal(
            fixture.OriginalIds,
            sidecar.Entries.Select(static entry => entry.Id).ToHashSet());
    }

    [Fact]
    public void MainPlansDuplicateWithIndependentOwnedIds()
    {
        using Fixture fixture = Fixture.Create();
        byte[] originalSource = File.ReadAllBytes(fixture.SourcePath);
        byte[] originalSidecar = File.ReadAllBytes(fixture.SidecarPath);
        string targetPath = Path.Combine(fixture.SourceDirectory, "IdentityPlanCopy.cs");
        string targetSource = fixture.SourceText.Replace("IdentityPlanBase", "IdentityPlanCopy", StringComparison.Ordinal);
        string requestPath = fixture.WriteRequest(
            fixture.SourcePath,
            targetPath,
            targetSource,
            fixture.Digest(originalSource),
            fixture.Digest(originalSidecar),
            preserveIdentity: false);
        string outputPath = Path.Combine(fixture.Root, "duplicate.plan.json");

        Assert.Equal(0, Program.Main([
            "script-class-identity-plan",
            fixture.ProjectPath,
            "--request", requestPath,
            "--out", outputPath,
            "--dotnet", Fixture.DotNetPath,
        ]));

        using JsonDocument output = JsonDocument.Parse(File.ReadAllText(outputPath));
        IdentitySidecarDocument duplicateSidecar = IdentitySidecarJson.Deserialize(
            output.RootElement.GetProperty("documents")[0]
                .GetProperty("identitySidecarJson").GetString()!);
        Assert.NotEmpty(duplicateSidecar.Entries);
        Assert.Empty(fixture.OriginalIds.Intersect(
            duplicateSidecar.Entries.Select(static entry => entry.Id)));
        Assert.Equal(originalSource, File.ReadAllBytes(fixture.SourcePath));
        Assert.Equal(originalSidecar, File.ReadAllBytes(fixture.SidecarPath));
        Assert.False(File.Exists(targetPath));
    }

    [Fact]
    public void MainRejectsStalePreconditionsMalformedSidecarAndOutputCollisions()
    {
        using Fixture fixture = Fixture.Create();
        byte[] source = File.ReadAllBytes(fixture.SourcePath);
        byte[] sidecar = File.ReadAllBytes(fixture.SidecarPath);
        string targetPath = Path.Combine(fixture.SourceDirectory, "IdentityPlanRejected.cs");
        string targetSource = fixture.SourceText.Replace("IdentityPlanBase", "IdentityPlanRejected", StringComparison.Ordinal);

        string staleRequest = fixture.WriteRequest(
            fixture.SourcePath,
            targetPath,
            targetSource,
            "sha256:" + new string('0', 64),
            fixture.Digest(sidecar),
            preserveIdentity: true);
        string staleOutput = Path.Combine(fixture.Root, "stale.plan.json");
        Assert.NotEqual(0, RunPlan(fixture, staleRequest, staleOutput));
        Assert.False(File.Exists(staleOutput));

        string missingSidecarRequest = fixture.WriteRequest(
            fixture.SourcePath,
            targetPath,
            targetSource,
            fixture.Digest(source),
            null,
            preserveIdentity: true);
        string missingSidecarOutput = Path.Combine(fixture.Root, "missing-sidecar.plan.json");
        Assert.NotEqual(0, RunPlan(fixture, missingSidecarRequest, missingSidecarOutput));
        Assert.False(File.Exists(missingSidecarOutput));

        string staleSidecarRequest = fixture.WriteRequest(
            fixture.SourcePath,
            targetPath,
            targetSource,
            fixture.Digest(source),
            "sha256:" + new string('0', 64),
            preserveIdentity: true);
        string staleSidecarOutput = Path.Combine(fixture.Root, "stale-sidecar.plan.json");
        Assert.NotEqual(0, RunPlan(fixture, staleSidecarRequest, staleSidecarOutput));
        Assert.False(File.Exists(staleSidecarOutput));

        string missingFieldRequest = fixture.WriteRequest(
            fixture.SourcePath,
            targetPath,
            targetSource,
            fixture.Digest(source),
            fixture.Digest(sidecar),
            preserveIdentity: true);
        fixture.RemoveRequestProperty(missingFieldRequest, "preserveIdentity");
        string missingFieldOutput = Path.Combine(fixture.Root, "missing-field.plan.json");
        Assert.NotEqual(0, RunPlan(fixture, missingFieldRequest, missingFieldOutput));
        Assert.False(File.Exists(missingFieldOutput));

        string missingSidecarFieldRequest = fixture.WriteRequest(
            fixture.SourcePath,
            targetPath,
            targetSource,
            fixture.Digest(source),
            fixture.Digest(sidecar),
            preserveIdentity: true);
        fixture.RemoveRequestProperty(missingSidecarFieldRequest, "sourceSidecarDigest");
        string missingSidecarFieldOutput = Path.Combine(fixture.Root, "missing-sidecar-field.plan.json");
        Assert.NotEqual(0, RunPlan(fixture, missingSidecarFieldRequest, missingSidecarFieldOutput));
        Assert.False(File.Exists(missingSidecarFieldOutput));

        string targetOutputRequest = fixture.WriteRequest(
            fixture.SourcePath,
            targetPath,
            targetSource,
            fixture.Digest(source),
            fixture.Digest(sidecar),
            preserveIdentity: true);
        Assert.NotEqual(0, RunPlan(fixture, targetOutputRequest, targetPath));
        Assert.False(File.Exists(targetPath));
        Assert.NotEqual(0, RunPlan(fixture, targetOutputRequest, targetPath + ".usids"));
        Assert.False(File.Exists(targetPath + ".usids"));

        string nullDocumentRequest = fixture.WriteRequest(
            fixture.SourcePath,
            targetPath,
            targetSource,
            fixture.Digest(source),
            fixture.Digest(sidecar),
            preserveIdentity: true);
        fixture.AppendNullDocument(nullDocumentRequest);
        string nullDocumentOutput = Path.Combine(fixture.Root, "null-document.plan.json");
        Assert.NotEqual(0, RunPlan(fixture, nullDocumentRequest, nullDocumentOutput));
        Assert.False(File.Exists(nullDocumentOutput));

        File.WriteAllText(fixture.SidecarPath, "{not-json");
        string malformedRequest = fixture.WriteRequest(
            fixture.SourcePath,
            targetPath,
            targetSource,
            fixture.Digest(source),
            fixture.Digest(File.ReadAllBytes(fixture.SidecarPath)),
            preserveIdentity: true);
        string malformedOutput = Path.Combine(fixture.Root, "malformed.plan.json");
        Assert.NotEqual(0, RunPlan(fixture, malformedRequest, malformedOutput));
        Assert.False(File.Exists(malformedOutput));
        File.WriteAllBytes(fixture.SidecarPath, sidecar);

        string collisionRequest = fixture.WriteRequest(
            fixture.SourcePath,
            fixture.SourcePath,
            fixture.SourceText,
            fixture.Digest(source),
            fixture.Digest(sidecar),
            preserveIdentity: true);
        Assert.NotEqual(0, RunPlan(fixture, collisionRequest, fixture.SourcePath));
    }

    [Fact]
    public void MainKeepsExplicitNullForLegacyMissingSidecar()
    {
        using Fixture fixture = Fixture.Create();
        File.Delete(fixture.SidecarPath);
        byte[] source = File.ReadAllBytes(fixture.SourcePath);
        string targetPath = Path.Combine(fixture.SourceDirectory, "IdentityPlanLegacy.cs");
        string targetSource = fixture.SourceText.Replace("IdentityPlanBase", "IdentityPlanLegacy", StringComparison.Ordinal);
        string requestPath = fixture.WriteRequest(
            fixture.SourcePath,
            targetPath,
            targetSource,
            fixture.Digest(source),
            null,
            preserveIdentity: true);
        string outputPath = Path.Combine(fixture.Root, "legacy.plan.json");

        Assert.Equal(0, RunPlan(fixture, requestPath, outputPath));
        using JsonDocument output = JsonDocument.Parse(File.ReadAllText(outputPath));
        JsonElement document = output.RootElement.GetProperty("documents")[0];
        Assert.True(document.TryGetProperty("sourceSidecarDigest", out JsonElement digest));
        Assert.Equal(JsonValueKind.Null, digest.ValueKind);
        Assert.True(document.TryGetProperty("identitySidecarJson", out JsonElement identity));
        Assert.Equal(JsonValueKind.Null, identity.ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MainAcceptsStrictUtf16BomSource(bool bigEndian)
    {
        using Fixture fixture = Fixture.Create();
        Encoding encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true, throwOnInvalidBytes: true);
        File.WriteAllBytes(fixture.SourcePath, encoding.GetPreamble().Concat(encoding.GetBytes(fixture.SourceText)).ToArray());
        byte[] source = File.ReadAllBytes(fixture.SourcePath);
        byte[] sidecar = File.ReadAllBytes(fixture.SidecarPath);
        string targetPath = Path.Combine(fixture.SourceDirectory, bigEndian ? "IdentityPlanBe.cs" : "IdentityPlanLe.cs");
        string targetName = bigEndian ? "IdentityPlanBe" : "IdentityPlanLe";
        string targetSource = fixture.SourceText.Replace("IdentityPlanBase", targetName, StringComparison.Ordinal);
        string requestPath = fixture.WriteRequest(
            fixture.SourcePath,
            targetPath,
            targetSource,
            fixture.Digest(source),
            fixture.Digest(sidecar),
            preserveIdentity: true);
        string outputPath = Path.Combine(fixture.Root, targetName + ".plan.json");

        Assert.Equal(0, RunPlan(fixture, requestPath, outputPath));
        Assert.True(File.Exists(outputPath));
    }

    private static int RunPlan(Fixture fixture, string requestPath, string outputPath) =>
        Program.Main([
            "script-class-identity-plan",
            fixture.ProjectPath,
            "--request", requestPath,
            "--out", outputPath,
            "--dotnet", Fixture.DotNetPath,
        ]);

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, string projectPath, string sourcePath, string sidecarPath)
        {
            Root = root;
            ProjectPath = projectPath;
            SourcePath = sourcePath;
            SidecarPath = sidecarPath;
            SourceDirectory = Path.GetDirectoryName(sourcePath)!;
            SourceText = File.ReadAllText(sourcePath);
            OriginalIds = IdentitySidecarJson.Deserialize(File.ReadAllText(sidecarPath))
                .Entries.Select(static entry => entry.Id).ToHashSet();
        }

        public static string DotNetPath { get; } =
            File.Exists("D:/UE_5.8/Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe")
                ? "D:/UE_5.8/Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe"
                : "dotnet";

        public string Root { get; }
        public string ProjectPath { get; }
        public string SourceDirectory { get; }
        public string SourcePath { get; }
        public string SidecarPath { get; }
        public string SourceText { get; }
        public IReadOnlySet<SemanticId> OriginalIds { get; }

        public static Fixture Create()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "FinareScriptClassIdentityPlan",
                Guid.NewGuid().ToString("N"));
            string sourceDirectory = Path.Combine(root, "Classes");
            Directory.CreateDirectory(sourceDirectory);
            string sourcePath = Path.Combine(sourceDirectory, "IdentityPlanBase.cs");
            string sidecarPath = sourcePath + ".usids";
            string projectPath = Path.Combine(root, "IdentityPlanFixture.csproj");
            string corePath = typeof(SemanticId).Assembly.Location;
            File.WriteAllText(sourcePath, """
                using Finare;

                [ScriptAsset("Game/IdentityPlanBase")]
                public class IdentityPlanBase
                {
                    [Expose]
                    public int Seed { get; set; } = 3;

                    [Visual]
                    public int ReadSeed()
                    {
                        return Seed;
                    }
                }
                """);
            File.WriteAllText(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>IdentityPlanFixture</AssemblyName>
                    <RootNamespace>IdentityPlanFixture</RootNamespace>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="Finare.Core">
                      <HintPath>{EscapeXml(corePath)}</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);

            using (Process restore = Process.Start(new ProcessStartInfo
            {
                FileName = DotNetPath,
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "restore", projectPath, "--nologo", "--ignore-failed-sources",
                    "-p:FinareSkipManagedHostStaging=true",
                },
            }) ?? throw new InvalidOperationException("Could not start dotnet restore."))
            {
                string output = restore.StandardOutput.ReadToEnd();
                string error = restore.StandardError.ReadToEnd();
                restore.WaitForExit();
                Assert.True(
                    restore.ExitCode == 0,
                    $"Temporary identity-plan project restore failed: {output}{Environment.NewLine}{error}");
            }

            int identityExit = Program.Main([
                "identity-init",
                sourcePath,
                "--out", sidecarPath,
                "--assembly", "IdentityPlanFixture",
                "--reference", corePath,
            ]);
            Assert.Equal(0, identityExit);
            return new Fixture(root, projectPath, sourcePath, sidecarPath);
        }

        public string WriteRequest(
            string sourcePath,
            string targetPath,
            string targetSource,
            string sourceDigest,
            string? sourceSidecarDigest,
            bool preserveIdentity)
        {
            string path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".request.json");
            var request = new
            {
                schemaVersion = "unifiedscript.csharp-source-identity-request/v0.1",
                documents = new[]
                {
                    new
                    {
                        sourcePath = Normalize(sourcePath),
                        targetPath = Normalize(targetPath),
                        sourceDigest,
                        sourceSidecarDigest,
                        targetSource,
                        preserveIdentity,
                    },
                },
            };
            JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented: true);
            options.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
            File.WriteAllText(path, JsonSerializer.Serialize(request, options) + "\n");
            return path;
        }

        public string Digest(byte[] bytes) =>
            "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        public void RemoveRequestProperty(string requestPath, string propertyName)
        {
            JsonNode root = JsonNode.Parse(File.ReadAllText(requestPath))
                ?? throw new InvalidOperationException("Request JSON was empty.");
            JsonObject document = root["documents"]?[0]?.AsObject()
                ?? throw new InvalidOperationException("Request document was missing.");
            Assert.True(document.Remove(propertyName));
            JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented: true);
            options.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
            File.WriteAllText(requestPath, JsonSerializer.Serialize(root, options) + "\n");
        }

        public void AppendNullDocument(string requestPath)
        {
            JsonNode root = JsonNode.Parse(File.ReadAllText(requestPath))
                ?? throw new InvalidOperationException("Request JSON was empty.");
            JsonArray documents = root["documents"]?.AsArray()
                ?? throw new InvalidOperationException("Request documents were missing.");
            documents.Add(null);
            JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented: true);
            options.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
            File.WriteAllText(requestPath, JsonSerializer.Serialize(root, options) + "\n");
        }

        public static string Normalize(string path) =>
            Path.GetFullPath(path).Replace(Path.DirectorySeparatorChar, '/');

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // The test assertion already reports command failures. Cleanup
                // must not obscure the contract result on locked MSBuild files.
            }
        }

        private static string EscapeXml(string value) =>
            value.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal);
    }
}
