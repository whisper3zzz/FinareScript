using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UnifiedScript.Authoring;
using UnifiedScript.Cli;
using UnifiedScript.Core;
using UnifiedScript.CSharp;
using UnifiedScript.Graph;

namespace UnifiedScript.Tests;

public sealed class CoreClrAuthoringHttpServerTests
{
    private const string Source = """
        using System;
        using UnifiedScript;

        namespace Tests.HttpAuthoring;

        [ScriptAsset("Tests/DamagePolicy")]
        public sealed class DamagePolicy
        {
            public int BaseDamage { get; set; } = 20;

            public int MaxDamage { get; set; } = 999;

            [Visual]
            public float ApplyDamage(int incomingDamage)
            {
                float scaled = incomingDamage * BaseDamage;
                scaled = Math.Clamp(scaled, 0f, MaxDamage);
                return scaled;
            }
        }
        """;

    [Fact]
    public async Task AuthoringEndpointsPersistReloadAndRejectExternalConflicts()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"unifiedscript-authoring-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "DamagePolicy.cs");
        string identityPath = sourcePath + ".usids";
        string contentRoot = Path.Combine(directory, "workbench");
        Directory.CreateDirectory(contentRoot);
        await File.WriteAllTextAsync(sourcePath, Source);
        await File.WriteAllTextAsync(Path.Combine(contentRoot, "index.html"), "<!doctype html><title>test</title>");
        string logicalPath = sourcePath.Replace('\\', '/');
        var request = new CSharpCompilationRequest(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(
                logicalPath,
                Source,
                IdentitySidecarDocument.CreateEmpty(logicalPath))]);
        int port = ReserveLoopbackPort();

        try
        {
            using var server = new CoreClrDebugHttpServer(
                request,
                sourcePath,
                identityPath,
                contentRoot,
                port);
            using var cancellation = new CancellationTokenSource();
            Task serverTask = server.RunAsync(cancellation.Token);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

            AuthoringWorkspaceResponse baseline = await GetWorkspaceAsync(client);
            Assert.Equal("unifiedscript.authoring-workspace/v0.2", baseline.SchemaVersion);
            Assert.Equal(AuthoringSurfaceDocument.CurrentSchemaVersion, baseline.Surface.SchemaVersion);
            ScriptAssetDeclaration asset = Assert.Single(baseline.ScriptAssets.Assets);
            AuthoringAssetSurface surfaceAsset = Assert.Single(baseline.Surface.Assets);
            Assert.Equal(asset.Id, surfaceAsset.Id);
            Assert.Contains(
                Assert.Single(surfaceAsset.Functions).Actions,
                static action => action.EditCapabilities.Count > 0);
            ScriptStateDeclaration maxDamage = asset.States.Single(static state =>
                state.Name == "MaxDamage");
            DeclarationPatchDocument declarationPatch = DeclarationPatchDocument.Create(
                baseline.ScriptAssets.SourceCompilationId,
                baseline.ScriptAssets.SemanticConfigurationId,
                [new UpdateScriptStatePatchOperation(
                    maxDamage.Id,
                    new ScriptStatePatchPrecondition(
                        maxDamage.Name,
                        maxDamage.TypeMetadataName,
                        maxDamage.TypeKind,
                        maxDamage.DefaultValue,
                        maxDamage.Exposure),
                    new ScriptStatePatchSpec(
                        maxDamage.Name,
                        "int",
                        maxDamage.TypeKind,
                        Json("120"),
                        maxDamage.Exposure))]);
            GraphNode clampAssignment = Assert.Single(
                baseline.Graph.Functions[0].Nodes,
                static node => node.Kind == "assignment");
            var startRequest = new DebugStartRequest(
                baseline.Graph.Functions[0].Id,
                [Json("12")],
                new DebugReceiverRequest(asset.Id, []),
                [new SemanticId(clampAssignment.Properties["debugTargetId"])]);
            using HttpResponseMessage startResponse = await client.PostAsync(
                "api/debug/start",
                JsonContent(JsonSerializer.Serialize(startRequest, UnifiedJson.CreateOptions())));
            startResponse.EnsureSuccessStatusCode();
            DebugSessionResponse paused = JsonSerializer.Deserialize<DebugSessionResponse>(
                await startResponse.Content.ReadAsStringAsync(),
                UnifiedJson.CreateOptions())!;
            Assert.Equal("paused", paused.State);
            using HttpResponseMessage activeConflict = await client.PostAsync(
                "api/authoring/declaration-patch",
                JsonContent(DeclarationPatchJson.Serialize(declarationPatch)));
            Assert.Equal(HttpStatusCode.Conflict, activeConflict.StatusCode);
            Assert.Contains(
                "UAW1001",
                await activeConflict.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
            using HttpResponseMessage continueResponse = await client.PostAsync(
                "api/debug/continue",
                JsonContent("{}"));
            continueResponse.EnsureSuccessStatusCode();
            DebugSessionResponse completed = JsonSerializer.Deserialize<DebugSessionResponse>(
                await continueResponse.Content.ReadAsStringAsync(),
                UnifiedJson.CreateOptions())!;
            Assert.Equal("240", completed.Result!.ReturnValue!.Json);

            AuthoringWorkspaceResponse declared = await PostAsync(
                client,
                "api/authoring/declaration-patch",
                DeclarationPatchJson.Serialize(declarationPatch));

            Assert.Equal(2, declared.Revision);
            Assert.Equal(2, declared.Generation.Number);
            Assert.Contains("= 120;", await File.ReadAllTextAsync(sourcePath), StringComparison.Ordinal);
            Assert.True(File.Exists(identityPath));

            GraphNode minimum = declared.Graph.Functions[0].Nodes.Single(static node =>
                node.Kind == "literal" && node.Properties.GetValueOrDefault("value") == "0f");
            SemanticPatchDocument semanticPatch = SemanticPatchDocument.Create(
                declared.ScriptAssets.SourceCompilationId,
                declared.ScriptAssets.SemanticConfigurationId,
                [new ReplaceLiteralPatchOperation(minimum.Id, "0f", "10f")]);
            AuthoringWorkspaceResponse behavior = await PostAsync(
                client,
                "api/authoring/semantic-patch",
                SemanticPatchJson.Serialize(semanticPatch));

            Assert.Equal(3, behavior.Revision);
            Assert.Equal(3, behavior.Generation.Number);
            Assert.Contains(
                "Math.Clamp(scaled, 10f, MaxDamage)",
                await File.ReadAllTextAsync(sourcePath),
                StringComparison.Ordinal);

            await File.AppendAllTextAsync(sourcePath, "\n// external change\n");
            GraphNode currentMinimum = behavior.Graph.Functions[0].Nodes.Single(static node =>
                node.Kind == "literal" && node.Properties.GetValueOrDefault("value") == "10f");
            SemanticPatchDocument conflictingPatch = SemanticPatchDocument.Create(
                behavior.ScriptAssets.SourceCompilationId,
                behavior.ScriptAssets.SemanticConfigurationId,
                [new ReplaceLiteralPatchOperation(currentMinimum.Id, "10f", "20f")]);
            using HttpResponseMessage conflict = await client.PostAsync(
                "api/authoring/semantic-patch",
                JsonContent(SemanticPatchJson.Serialize(conflictingPatch)));

            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            string errorJson = await conflict.Content.ReadAsStringAsync();
            Assert.Contains("UAW1002", errorJson, StringComparison.Ordinal);
            Assert.DoesNotContain("20f", await File.ReadAllTextAsync(sourcePath), StringComparison.Ordinal);

            cancellation.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<AuthoringWorkspaceResponse> GetWorkspaceAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("api/authoring/workspace");
        response.EnsureSuccessStatusCode();
        return Deserialize(await response.Content.ReadAsStringAsync());
    }

    private static async Task<AuthoringWorkspaceResponse> PostAsync(
        HttpClient client,
        string path,
        string json)
    {
        using HttpResponseMessage response = await client.PostAsync(path, JsonContent(json));
        response.EnsureSuccessStatusCode();
        return Deserialize(await response.Content.ReadAsStringAsync());
    }

    private static AuthoringWorkspaceResponse Deserialize(string json) =>
        JsonSerializer.Deserialize<AuthoringWorkspaceResponse>(json, UnifiedJson.CreateOptions()) ??
        throw new InvalidOperationException("The authoring workspace response was empty.");

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static JsonElement Json(string value) =>
        JsonDocument.Parse(value).RootElement.Clone();

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
