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
        string supportPath = Path.Combine(directory, "Support.cs");
        string identityPath = sourcePath + ".usids";
        string declarationPath = Path.Combine(directory, "DamagePolicy.asset.json");
        string surfacePath = Path.Combine(directory, "DamagePolicy.surface.json");
        string managedPath = Path.Combine(directory, "UnifiedScript.Analysis.dll");
        string pdbPath = Path.Combine(directory, "UnifiedScript.Analysis.pdb");
        string historyPath = Path.Combine(directory, "DamagePolicy.authoring-history.json");
        string contentRoot = Path.Combine(directory, "workbench");
        Directory.CreateDirectory(contentRoot);
        await File.WriteAllTextAsync(sourcePath, Source);
        const string supportSource = "namespace Tests.HttpAuthoring; internal static class Support { public const int Value = 1; }";
        await File.WriteAllTextAsync(supportPath, supportSource);
        await File.WriteAllTextAsync(Path.Combine(contentRoot, "index.html"), "<!doctype html><title>test</title>");
        string logicalPath = sourcePath.Replace('\\', '/');
        string logicalSupportPath = supportPath.Replace('\\', '/');
        var request = new CSharpCompilationRequest(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(
                logicalPath,
                Source,
                IdentitySidecarDocument.CreateEmpty(logicalPath)),
             new CSharpSourceDocument(
                 logicalSupportPath,
                 supportSource,
                 IdentitySidecarDocument.CreateEmpty(logicalSupportPath))]);
        int port = ReserveLoopbackPort();

        try
        {
            using var server = new CoreClrDebugHttpServer(
                request,
                sourcePath,
                identityPath,
                contentRoot,
                port,
                new AuthoringArtifactOutputs(
                    declarationPath,
                    surfacePath,
                    managedPath,
                    pdbPath,
                    historyPath),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [logicalPath] = sourcePath,
                    [logicalSupportPath] = supportPath,
                });
            using var cancellation = new CancellationTokenSource();
            Task serverTask = server.RunAsync(cancellation.Token);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

            AuthoringWorkspaceResponse baseline = await GetWorkspaceAsync(client);
            Assert.Equal("unifiedscript.authoring-workspace/v0.3", baseline.SchemaVersion);
            Assert.False(baseline.History.CanUndo);
            Assert.False(baseline.History.CanRedo);
            Assert.Equal(0, baseline.History.UndoDepth);
            Assert.Equal(0, baseline.History.RedoDepth);
            Assert.Equal("empty", baseline.History.PersistenceState);
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
                DeclarationPatchJson.Serialize(declarationPatch),
                baseline.Revision,
                "txn-declaration",
                "Update MaxDamage");

            Assert.Equal(2, declared.Revision);
            Assert.Equal(2, declared.Generation.Number);
            Assert.True(declared.History.CanUndo);
            Assert.False(declared.History.CanRedo);
            Assert.Equal(1, declared.History.UndoDepth);
            Assert.Equal("txn-declaration", declared.History.UndoTransactionId);
            Assert.Equal("Update MaxDamage", declared.History.UndoDescription);
            Assert.Equal("txn-declaration", declared.History.LastOperationId);
            Assert.Equal("persisted", declared.History.PersistenceState);
            Assert.Contains("= 120;", await File.ReadAllTextAsync(sourcePath), StringComparison.Ordinal);
            Assert.True(File.Exists(identityPath));
            Assert.Equal(
                CSharpAuthoringHistoryDocument.CurrentSchemaVersion,
                CSharpAuthoringHistoryJson.Deserialize(
                    await File.ReadAllTextAsync(historyPath)).SchemaVersion);
            Assert.Equal(
                "unifiedscript.script-asset/v0.1",
                ScriptAssetJson.Deserialize(await File.ReadAllTextAsync(declarationPath)).SchemaVersion);
            Assert.Equal(
                AuthoringSurfaceDocument.CurrentSchemaVersion,
                AuthoringSurfaceJson.Deserialize(await File.ReadAllTextAsync(surfacePath)).SchemaVersion);
            Assert.True(new FileInfo(managedPath).Length > 0);
            Assert.True(new FileInfo(pdbPath).Length > 0);

            GraphNode minimum = declared.Graph.Functions[0].Nodes.Single(static node =>
                node.Kind == "literal" && node.Properties.GetValueOrDefault("value") == "0f");
            SemanticPatchDocument semanticPatch = SemanticPatchDocument.Create(
                declared.ScriptAssets.SourceCompilationId,
                declared.ScriptAssets.SemanticConfigurationId,
                [new ReplaceLiteralPatchOperation(minimum.Id, "0f", "10f")]);
            AuthoringWorkspaceResponse behavior = await PostAsync(
                client,
                "api/authoring/semantic-patch",
                SemanticPatchJson.Serialize(semanticPatch),
                declared.Revision,
                "txn-semantic",
                "Raise clamp minimum");

            Assert.Equal(3, behavior.Revision);
            Assert.Equal(3, behavior.Generation.Number);
            Assert.Contains(
                "Math.Clamp(scaled, 10f, MaxDamage)",
                await File.ReadAllTextAsync(sourcePath),
                StringComparison.Ordinal);
            AuthoringSurfaceDocument persistedSurface = AuthoringSurfaceJson.Deserialize(
                await File.ReadAllTextAsync(surfacePath));
            Assert.Equal(
                behavior.Surface.SourceCompilationId,
                persistedSurface.SourceCompilationId);

            GraphNode plannedMinimum = behavior.Graph.Functions[0].Nodes.Single(static node =>
                node.Kind == "literal" && node.Properties.GetValueOrDefault("value") == "10f");
            SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
                behavior.ScriptAssets.SourceCompilationId,
                behavior.ScriptAssets.SemanticConfigurationId,
                [new ReplaceLiteralPatchPlanStep(
                    "raiseMinimum",
                    plannedMinimum.Id,
                    "10f",
                    "12f")]);
            AuthoringWorkspaceResponse planned = await PostAsync(
                client,
                "api/authoring/semantic-patch-plan",
                SemanticPatchPlanJson.Serialize(plan),
                behavior.Revision,
                "txn-plan",
                "Apply clamp plan");

            Assert.Equal(4, planned.Revision);
            Assert.Equal(4, planned.Generation.Number);
            Assert.Contains(
                "Math.Clamp(scaled, 12f, MaxDamage)",
                await File.ReadAllTextAsync(sourcePath),
                StringComparison.Ordinal);

            GraphFunction behaviorFunction = Assert.Single(planned.Graph.Functions);
            GraphNode returnNode = behaviorFunction.Nodes.Single(static node => node.Kind == "return");
            GraphEdge returnEdge = behaviorFunction.Edges.Single(edge =>
                edge.Kind == GraphEdgeKind.Data &&
                edge.ToNode == returnNode.Id &&
                edge.ToPort == "value");
            GraphNode reconnectSource = behaviorFunction.Nodes.Single(static node =>
                node.Kind == "literal" && node.Properties.GetValueOrDefault("value") == "12f");
            GraphPort reconnectSourcePort = reconnectSource.Ports.Single(static port =>
                port.Direction == GraphPortDirection.Output && port.Kind == GraphPortKind.Data);
            AuthoringFunctionSurface surfaceFunction = Assert.Single(
                Assert.Single(behavior.Surface.Assets).Functions);
            AuthoringActionSurface surfaceReturn = surfaceFunction.Actions.Single(action =>
                action.Id == returnNode.Id);
            AuthoringPortSurface surfaceReturnPort = surfaceReturn.Ports.Single(static port =>
                port.Direction == GraphPortDirection.Input &&
                port.Kind == GraphPortKind.Data &&
                port.Name == "value");
            SemanticPatchDocument rewirePatch = SemanticPatchDocument.Create(
                planned.ScriptAssets.SourceCompilationId,
                planned.ScriptAssets.SemanticConfigurationId,
                [new ReconnectDataEdgePatchOperation(
                    returnNode.Id,
                    returnEdge.ToPort,
                    returnEdge.Id,
                    returnEdge.FromNode,
                    returnEdge.FromPort,
                    reconnectSource.Id,
                    reconnectSourcePort.Name,
                    surfaceReturnPort.TypeId!.Value)]);
            AuthoringWorkspaceResponse rewired = await PostAsync(
                client,
                "api/authoring/semantic-patch",
                SemanticPatchJson.Serialize(rewirePatch),
                planned.Revision,
                "txn-rewire",
                "Reconnect return value");

            Assert.Equal(5, rewired.Revision);
            Assert.Equal(5, rewired.Generation.Number);
            Assert.Equal(4, rewired.History.UndoDepth);
            Assert.Equal("txn-rewire", rewired.History.UndoTransactionId);
            Assert.Contains("return 12f;", await File.ReadAllTextAsync(sourcePath), StringComparison.Ordinal);
            Assert.Equal(
                rewired.Surface.SourceCompilationId,
                AuthoringSurfaceJson.Deserialize(
                    await File.ReadAllTextAsync(surfacePath)).SourceCompilationId);

            using (HttpRequestMessage staleRequest = AuthoringRequest(
                "api/authoring/semantic-patch",
                SemanticPatchJson.Serialize(rewirePatch),
                planned.Revision,
                "txn-stale",
                "Stale edit"))
            using (HttpResponseMessage staleResponse = await client.SendAsync(staleRequest))
            {
                Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
                Assert.Contains(
                    "UAW1005",
                    await staleResponse.Content.ReadAsStringAsync(),
                    StringComparison.Ordinal);
            }

            using (HttpRequestMessage duplicateRequest = AuthoringRequest(
                "api/authoring/semantic-patch",
                SemanticPatchJson.Serialize(rewirePatch),
                rewired.Revision,
                "txn-rewire",
                "Duplicate edit"))
            using (HttpResponseMessage duplicateResponse = await client.SendAsync(duplicateRequest))
            {
                Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
                Assert.Contains(
                    "UAW1006",
                    await duplicateResponse.Content.ReadAsStringAsync(),
                    StringComparison.Ordinal);
            }

            AuthoringWorkspaceResponse undone = await PostAsync(
                client,
                "api/authoring/undo",
                "{}",
                rewired.Revision,
                "op-undo-rewire",
                "Undo return reconnection");

            Assert.Equal(6, undone.Revision);
            Assert.Equal(6, undone.Generation.Number);
            Assert.Contains("return scaled;", await File.ReadAllTextAsync(sourcePath), StringComparison.Ordinal);
            Assert.Equal(3, undone.History.UndoDepth);
            Assert.Equal(1, undone.History.RedoDepth);
            Assert.Equal("txn-rewire", undone.History.RedoTransactionId);
            Assert.Equal("op-undo-rewire", undone.History.LastOperationId);

            AuthoringWorkspaceResponse redone = await PostAsync(
                client,
                "api/authoring/redo",
                "{}",
                undone.Revision,
                "op-redo-rewire",
                "Redo return reconnection");

            Assert.Equal(7, redone.Revision);
            Assert.Equal(7, redone.Generation.Number);
            Assert.Contains("return 12f;", await File.ReadAllTextAsync(sourcePath), StringComparison.Ordinal);
            Assert.Equal(4, redone.History.UndoDepth);
            Assert.Equal(0, redone.History.RedoDepth);
            Assert.Equal("op-redo-rewire", redone.History.LastOperationId);
            Assert.Equal(
                redone.Surface.SourceCompilationId,
                AuthoringSurfaceJson.Deserialize(
                    await File.ReadAllTextAsync(surfacePath)).SourceCompilationId);
            string surfaceBeforeConflict = await File.ReadAllTextAsync(surfacePath);
            byte[] assemblyBeforeConflict = await File.ReadAllBytesAsync(managedPath);

            await File.AppendAllTextAsync(supportPath, "\n// external change\n");
            GraphNode currentMinimum = redone.Graph.Functions[0].Nodes.First(static node =>
                node.Kind == "literal" && node.Properties.GetValueOrDefault("value") == "12f");
            SemanticPatchDocument conflictingPatch = SemanticPatchDocument.Create(
                redone.ScriptAssets.SourceCompilationId,
                redone.ScriptAssets.SemanticConfigurationId,
                [new ReplaceLiteralPatchOperation(currentMinimum.Id, "12f", "20f")]);
            using HttpRequestMessage conflictRequest = AuthoringRequest(
                "api/authoring/semantic-patch",
                SemanticPatchJson.Serialize(conflictingPatch),
                redone.Revision,
                "txn-external-conflict",
                "Conflicting edit");
            using HttpResponseMessage conflict = await client.SendAsync(conflictRequest);

            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            string errorJson = await conflict.Content.ReadAsStringAsync();
            Assert.Contains("UAW1002", errorJson, StringComparison.Ordinal);
            Assert.DoesNotContain("20f", await File.ReadAllTextAsync(sourcePath), StringComparison.Ordinal);
            Assert.Equal(surfaceBeforeConflict, await File.ReadAllTextAsync(surfacePath));
            Assert.Equal(assemblyBeforeConflict, await File.ReadAllBytesAsync(managedPath));

            cancellation.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PersistedAuthoringHistorySurvivesRestartAndRejectsAStaleJournal()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"unifiedscript-authoring-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "DamagePolicy.cs");
        string identityPath = sourcePath + ".usids";
        string historyPath = sourcePath + ".ushistory.json";
        string contentRoot = Path.Combine(directory, "workbench");
        Directory.CreateDirectory(contentRoot);
        await File.WriteAllTextAsync(sourcePath, Source);
        await File.WriteAllTextAsync(
            Path.Combine(contentRoot, "index.html"),
            "<!doctype html><title>test</title>");
        string logicalPath = sourcePath.Replace('\\', '/');

        try
        {
            AuthoringWorkspaceResponse edited;
            int firstPort = ReserveLoopbackPort();
            using (var server = new CoreClrDebugHttpServer(
                CreateRequestFromDisk(sourcePath, identityPath),
                sourcePath,
                identityPath,
                contentRoot,
                firstPort,
                new AuthoringArtifactOutputs(HistoryPath: historyPath)))
            using (var cancellation = new CancellationTokenSource())
            using (var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{firstPort}/"),
            })
            {
                Task serverTask = server.RunAsync(cancellation.Token);
                AuthoringWorkspaceResponse baseline = await GetWorkspaceAsync(client);
                GraphNode minimum = baseline.Graph.Functions[0].Nodes.Single(static node =>
                    node.Kind == "literal" &&
                    node.Properties.GetValueOrDefault("value") == "0f");
                SemanticPatchDocument patch = SemanticPatchDocument.Create(
                    baseline.ScriptAssets.SourceCompilationId,
                    baseline.ScriptAssets.SemanticConfigurationId,
                    [new ReplaceLiteralPatchOperation(minimum.Id, "0f", "10f")]);
                edited = await PostAsync(
                    client,
                    "api/authoring/semantic-patch",
                    SemanticPatchJson.Serialize(patch),
                    baseline.Revision,
                    "txn-before-restart",
                    "Edit before restart");
                cancellation.Cancel();
                await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.True(File.Exists(historyPath));
            Assert.Equal(2, edited.Revision);

            int secondPort = ReserveLoopbackPort();
            using (var server = new CoreClrDebugHttpServer(
                CreateRequestFromDisk(sourcePath, identityPath),
                sourcePath,
                identityPath,
                contentRoot,
                secondPort,
                new AuthoringArtifactOutputs(HistoryPath: historyPath)))
            using (var cancellation = new CancellationTokenSource())
            using (var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{secondPort}/"),
            })
            {
                Task serverTask = server.RunAsync(cancellation.Token);
                AuthoringWorkspaceResponse restored = await GetWorkspaceAsync(client);
                Assert.Equal(2, restored.Revision);
                Assert.Equal("restored", restored.History.PersistenceState);
                Assert.True(restored.History.CanUndo);
                Assert.Equal("txn-before-restart", restored.History.UndoTransactionId);
                Assert.Equal("txn-before-restart", restored.History.LastOperationId);

                AuthoringWorkspaceResponse undone = await PostAsync(
                    client,
                    "api/authoring/undo",
                    "{}",
                    restored.Revision,
                    "op-after-restart",
                    "Undo after restart");
                Assert.Equal(3, undone.Revision);
                Assert.Equal("persisted", undone.History.PersistenceState);
                Assert.True(undone.History.CanRedo);
                Assert.Contains(
                    "Math.Clamp(scaled, 0f, MaxDamage)",
                    await File.ReadAllTextAsync(sourcePath));
                cancellation.Cancel();
                await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            }

            await File.AppendAllTextAsync(sourcePath, "\n// external edit after shutdown\n");
            int stalePort = ReserveLoopbackPort();
            using (var server = new CoreClrDebugHttpServer(
                CreateRequestFromDisk(sourcePath, identityPath),
                sourcePath,
                identityPath,
                contentRoot,
                stalePort,
                new AuthoringArtifactOutputs(HistoryPath: historyPath)))
            using (var cancellation = new CancellationTokenSource())
            using (var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{stalePort}/"),
            })
            {
                Task serverTask = server.RunAsync(cancellation.Token);
                AuthoringWorkspaceResponse rejected = await GetWorkspaceAsync(client);
                Assert.Equal(1, rejected.Revision);
                Assert.Equal("rejected", rejected.History.PersistenceState);
                Assert.NotNull(rejected.History.PersistenceMessage);
                Assert.False(rejected.History.CanUndo);
                cancellation.Cancel();
                await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        CSharpCompilationRequest CreateRequestFromDisk(string path, string idsPath)
        {
            string logical = path.Replace('\\', '/');
            IdentitySidecarDocument identity = File.Exists(idsPath)
                ? IdentitySidecarJson.Deserialize(File.ReadAllText(idsPath))
                : IdentitySidecarDocument.CreateEmpty(logical);
            return new CSharpCompilationRequest(
                "UnifiedScript.Analysis",
                [new CSharpSourceDocument(logical, File.ReadAllText(path), identity)]);
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
        string json,
        long baselineRevision,
        string transactionId,
        string description)
    {
        using HttpRequestMessage request = AuthoringRequest(
            path,
            json,
            baselineRevision,
            transactionId,
            description);
        using HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return Deserialize(await response.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage AuthoringRequest(
        string path,
        string json,
        long baselineRevision,
        string transactionId,
        string description)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent(json),
        };
        request.Headers.Add("X-UnifiedScript-Baseline-Revision", baselineRevision.ToString());
        request.Headers.Add("X-UnifiedScript-Transaction-Id", transactionId);
        request.Headers.Add("X-UnifiedScript-Transaction-Label", description);
        return request;
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
