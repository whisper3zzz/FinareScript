using System.Net;
using System.Net.Sockets;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using Finare.Authoring;
using Finare.Cli;
using Finare.Core;
using Finare.CSharp;
using Finare.Graph;
using Finare.Runtime;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Authoring")]
[Collection("Authoring HTTP servers")]
public sealed class CoreClrAuthoringHttpServerTests
{
    private const string Source = """
        using System;
        using Finare;

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
    public async Task GeneratedUnrealApiBrowserIsServedReadOnly()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"finare-api-browser-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "DamagePolicy.cs");
        string contentRoot = Path.Combine(directory, "workbench");
        Directory.CreateDirectory(contentRoot);
        await File.WriteAllTextAsync(sourcePath, Source);
        await File.WriteAllTextAsync(
            Path.Combine(contentRoot, "index.html"),
            "<!doctype html><title>test</title>");
        string logicalPath = sourcePath.Replace('\\', '/');
        var request = new CSharpCompilationRequest(
            "Finare.Analysis",
            [new CSharpSourceDocument(
                logicalPath,
                Source,
                IdentitySidecarDocument.CreateEmpty(logicalPath))]);
        var browser = new UnrealApiBrowserDocument(
            UnrealApiBrowserDocument.CurrentSchemaVersion,
            "Tests.Runtime",
            "Tests.Generated",
            new UnrealEngineIdentity(5, 8, 1, "//UE5/Release-5.8"),
            new UnrealApiBrowserSources(
                UnrealApiContract.CurrentSchemaVersion,
                UnrealApiNodeCatalog.CurrentSchemaVersion,
                UnrealBindingCoverageReport.CurrentSchemaVersion,
                new string('a', 64)),
            new UnrealApiBrowserSummary(0, 0, 0, 0, 0, 0, 0, 0, 0),
            [],
            []);
        int port = ReserveLoopbackPort();

        try
        {
            using var server = new CoreClrDebugHttpServer(
                request,
                sourcePath,
                identityPath: null,
                contentRoot,
                port,
                apiBrowser: browser);
            using var cancellation = new CancellationTokenSource();
            Task serverTask = server.RunAsync(cancellation.Token);
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            };

            string json = await client.GetStringAsync("api/unreal/browser");
            UnrealApiBrowserDocument served = UnrealApiBrowserJson.Deserialize(json);
            Assert.Equal(browser.ContractName, served.ContractName);
            AuthoringWorkspaceResponse workspace = await GetWorkspaceAsync(client);
            AuthoringFunctionSurface function = workspace.Surface.Assets
                .SelectMany(static asset => asset.Functions)
                .Single();
            AuthoringActionSurface action = function.Actions.First(static candidate =>
                candidate.DebugTargetId is not null);
            var pressure = new UnrealGameplayProfileBufferPressure(0, 0, 0, 0);
            var profileNode = new UnrealGameplayProfileNode(
                function.Id.Value,
                action.DebugTargetId!.Value.Value,
                1,
                20_000,
                [new UnrealGameplayProfileBoundary("directThunk", 1, 5_000)]);
            var profileFrame = new UnrealGameplayProfileFrame(
                1,
                "1",
                1,
                "Tests.HttpAuthoring.DamagePolicy",
                0.016f,
                "ManagedTick",
                [],
                20_000,
                0,
                profileNode.Boundaries,
                [profileNode],
                pressure);
            var trace = new UnrealGameplayProfileTrace(
                UnrealGameplayProfileTrace.CurrentSchemaVersion,
                "http-insight",
                DateTimeOffset.UnixEpoch,
                "world-test",
                "script-test",
                1,
                0,
                [profileFrame],
                new UnrealGameplayProfileAggregate(
                    1,
                    20_000,
                    0,
                    [],
                    profileNode.Boundaries,
                    [profileNode],
                    pressure));
            using HttpResponseMessage insightResponse = await client.PostAsync(
                "api/unreal/authoring-insight",
                JsonContent(JsonSerializer.Serialize(
                    new UnrealAuthoringInsightRequest(
                        UnrealAuthoringInsightRequest.CurrentSchemaVersion,
                        trace,
                        function.Id.Value,
                        action.DebugTargetId.Value.Value),
                    UnifiedJson.CreateOptions(writeIndented: false))));
            insightResponse.EnsureSuccessStatusCode();
            UnrealAuthoringInsightSnapshot insight = UnrealAuthoringInsightJson.Deserialize(
                await insightResponse.Content.ReadAsStringAsync());
            UnrealAuthoringInsightNode insightNode = Assert.Single(insight.Nodes);
            Assert.Equal(action.Id.Value, insightNode.ActionId);
            Assert.Equal(action.Span, insightNode.SourceSpan);
            Assert.True(insightNode.Focused);
            using HttpResponseMessage rejected = await client.PostAsync(
                "api/unreal/browser",
                JsonContent("{}"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, rejected.StatusCode);

            cancellation.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DebugStartUsesSelectedInheritedReceiverAssetAndGeneration()
    {
        const string inheritedSource = """
            using Finare;

            namespace Tests.HttpInherited;

            [ScriptAsset("Tests/HttpInheritedBase")]
            public class InheritedBase
            {
                protected int BaseValue { get; set; } = 3;

                [Visual]
                public int ReadBaseValue()
                {
                    return BaseValue;
                }
            }

            [ScriptAsset("Tests/HttpInheritedOverride")]
            public sealed class InheritedOverride : InheritedBase
            {
                public InheritedOverride() { BaseValue = 77; }
            }

            [ScriptAsset("Tests/HttpInheritedFollower")]
            public sealed class InheritedFollower : InheritedBase { }
            """;
        string directory = Path.Combine(Path.GetTempPath(), $"finare-inherited-debug-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "Inherited.cs");
        string contentRoot = Path.Combine(directory, "workbench");
        Directory.CreateDirectory(contentRoot);
        await File.WriteAllTextAsync(sourcePath, inheritedSource);
        await File.WriteAllTextAsync(Path.Combine(contentRoot, "index.html"), "<!doctype html><title>test</title>");
        string logicalPath = sourcePath.Replace('\\', '/');
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            inheritedSource,
            logicalPath,
            identitySidecar: IdentitySidecarDocument.CreateEmpty(logicalPath));
        int port = ReserveLoopbackPort();

        try
        {
            using var server = new CoreClrDebugHttpServer(request, sourcePath, null, contentRoot, port);
            using var cancellation = new CancellationTokenSource();
            Task serverTask = server.RunAsync(cancellation.Token);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

            AuthoringWorkspaceResponse workspace = await GetWorkspaceAsync(client);
            ScriptAssetDeclaration baseAsset = workspace.ScriptAssets.Assets.Single(
                static asset => asset.QualifiedName == "Tests.HttpInherited.InheritedBase");
            ScriptAssetDeclaration overrideAsset = workspace.ScriptAssets.Assets.Single(
                static asset => asset.QualifiedName == "Tests.HttpInherited.InheritedOverride");
            ScriptStateDeclaration state = Assert.Single(overrideAsset.States);
            SemanticId methodId = Assert.Single(baseAsset.Functions).Id;
            Assert.Contains(overrideAsset.Functions, function => function.Id == methodId);
            Assert.Equal("3", Assert.Single(baseAsset.States).DefaultValue.GetRawText());
            Assert.Equal("77", state.DefaultValue.GetRawText());

            var startRequest = new DebugStartRequest(
                methodId,
                [],
                new DebugReceiverRequest(
                    overrideAsset.Id,
                    [new DebugStateOverrideRequest(state.Id, Json("99"))]),
                [],
                workspace.ScriptAssets.SourceCompilationId,
                workspace.ScriptAssets.SemanticConfigurationId);
            using HttpResponseMessage startResponse = await client.PostAsync(
                "api/debug/start",
                JsonContent(JsonSerializer.Serialize(startRequest, UnifiedJson.CreateOptions())));
            startResponse.EnsureSuccessStatusCode();
            DebugSessionResponse started = JsonSerializer.Deserialize<DebugSessionResponse>(
                await startResponse.Content.ReadAsStringAsync(), UnifiedJson.CreateOptions())!;
            Assert.Equal(workspace.Generation.GenerationId, started.Generation.GenerationId);
            Assert.Equal(workspace.Generation.Number, started.Generation.Number);

            using HttpResponseMessage waitResponse = await client.PostAsync(
                "api/debug/wait",
                JsonContent(JsonSerializer.Serialize(
                    new DebugExecutionRequest(started.Generation.GenerationId, started.ExecutionId),
                    UnifiedJson.CreateOptions())));
            waitResponse.EnsureSuccessStatusCode();
            DebugSessionResponse completed = JsonSerializer.Deserialize<DebugSessionResponse>(
                await waitResponse.Content.ReadAsStringAsync(), UnifiedJson.CreateOptions())!;
            Assert.Equal("completed", completed.State);
            Assert.Equal("99", completed.Result?.ReturnValue?.Json);
            Assert.Equal(started.Generation.GenerationId, completed.Generation.GenerationId);

            cancellation.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SourceTextEndpointRequiresAndCommitsTheExactPreviewCandidate()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"finare-source-text-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "DamagePolicy.cs");
        string identityPath = sourcePath + ".usids";
        string historyPath = sourcePath + ".ushistory.json";
        string layoutPath = Path.Combine(directory, "DamagePolicy.usgraph");
        string contentRoot = Path.Combine(directory, "workbench");
        Directory.CreateDirectory(contentRoot);
        await File.WriteAllTextAsync(sourcePath, Source);
        await File.WriteAllTextAsync(
            Path.Combine(contentRoot, "index.html"),
            "<!doctype html><title>test</title>");
        string logicalPath = sourcePath.Replace('\\', '/');
        var request = new CSharpCompilationRequest(
            "Finare.Analysis",
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
                port,
                new AuthoringArtifactOutputs(
                    HistoryPath: historyPath,
                    GraphLayoutPath: layoutPath));
            using var cancellation = new CancellationTokenSource();
            Task serverTask = server.RunAsync(cancellation.Token);
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            };

            AuthoringWorkspaceResponse baseline = await GetWorkspaceAsync(client);
            ScriptAssetDeclaration baselineAsset = Assert.Single(baseline.ScriptAssets.Assets);
            SemanticId baselineFunctionId = Assert.Single(baselineAsset.Functions).Id;
            GraphFunction baselineGraphFunction = Assert.Single(baseline.Graph.Functions);
            GraphLayoutDocument baselineLayout = new(
                GraphLayoutDocument.CurrentSchemaVersion,
                [new GraphFunctionLayout(
                    baselineGraphFunction.Id,
                    baselineGraphFunction.Nodes.Select((node, index) => new GraphNodeLayout(
                        node.Id,
                        GraphNodePlacement.Placed,
                        new GraphLayoutPoint(index * 240, index * 120),
                        new GraphLayoutSize(220, 90),
                        false)).ToArray(),
                    [new GraphCommentRegion(
                        "damage-flow",
                        "Damage flow",
                        new GraphLayoutBounds(-40, -40, 1200, 720),
                        baselineGraphFunction.Nodes.Select(static node => node.Id).ToArray(),
                        false)],
                    baselineGraphFunction.Edges.Count == 0
                        ? []
                        : [new GraphRoutingHint(
                            baselineGraphFunction.Edges[0].Id,
                            [new GraphLayoutPoint(180, 80)])])],
                []);
            string baselineLayoutText = GraphLayoutJson.Serialize(baselineLayout) + "\n";
            await File.WriteAllTextAsync(layoutPath, baselineLayoutText);
            string editedSource = baseline.SourceText.Replace(
                "float scaled = incomingDamage * BaseDamage;",
                "// Authored in the transactional C# view.\n        float scaled = incomingDamage * BaseDamage + 1;",
                StringComparison.Ordinal);
            string patchJson = CSharpSourceTextPatchJson.Serialize(
                CSharpSourceTextPatchDocument.Create(
                    baseline.SourcePath,
                    baseline.SourceText,
                    editedSource));
            AuthoringPreviewResponse preview = await PreviewAsync(
                client,
                "api/authoring/preview/source-text",
                patchJson,
                baseline.Revision,
                "txn-source-text",
                "Edit C# source");

            Assert.Equal(baseline.Revision, preview.BaselineRevision);
            Assert.Equal(baseline.Revision + 1, preview.CandidateRevision);
            Assert.True(preview.PublishesRuntime);
            Assert.Equal("unifiedscript.authoring-preview/v0.3", preview.SchemaVersion);
            AuthoringPreviewFile file = Assert.Single(preview.Files, candidate =>
                candidate.Path == logicalPath);
            Assert.Equal(logicalPath, file.Path);
            Assert.Equal(Source, file.BeforeText);
            Assert.Equal(editedSource, file.AfterText);
            AuthoringPreviewFile layoutPreview = Assert.Single(preview.Files, candidate =>
                candidate.Path == Path.GetFullPath(layoutPath));
            Assert.Equal(baselineLayoutText, layoutPreview.BeforeText);
            Assert.True(preview.RoundTrip.LayoutChecked);
            Assert.True(preview.RoundTrip.PreservedSemanticIds > 0);
            Assert.Equal(1, preview.RoundTrip.AddedSourceComments);
            Assert.Equal(0, preview.RoundTrip.RemovedSourceComments);
            Assert.Equal(1, preview.RoundTrip.PreservedGraphComments);
            Assert.True(preview.RoundTrip.PreservedNodeLayouts > 0);
            Assert.True(preview.RoundTrip.NewUnplacedNodes > 0);
            Assert.Equal(Source, await File.ReadAllTextAsync(sourcePath));

            using (HttpRequestMessage unconfirmed = AuthoringRequest(
                "api/authoring/source-text",
                patchJson,
                baseline.Revision,
                "txn-source-text-unconfirmed",
                "Unconfirmed C# edit"))
            using (HttpResponseMessage rejected = await client.SendAsync(unconfirmed))
            {
                Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
                Assert.Contains(
                    "UAW1012",
                    await rejected.Content.ReadAsStringAsync(),
                    StringComparison.Ordinal);
            }
            Assert.Equal(Source, await File.ReadAllTextAsync(sourcePath));

            AuthoringWorkspaceResponse committed = await PostAsync(
                client,
                "api/authoring/source-text",
                patchJson,
                baseline.Revision,
                "txn-source-text",
                "Edit C# source",
                previewToken: preview.PreviewToken);

            Assert.Equal(baseline.Revision + 1, committed.Revision);
            Assert.Equal(editedSource, committed.SourceText);
            Assert.Equal(editedSource, await File.ReadAllTextAsync(sourcePath));
            Assert.Equal(
                baselineFunctionId,
                Assert.Single(Assert.Single(committed.ScriptAssets.Assets).Functions).Id);
            Assert.Contains("// Authored in the transactional C# view.", committed.SourceText);
            Assert.Equal("txn-source-text", committed.History.UndoTransactionId);
            Assert.True(File.Exists(identityPath));
            Assert.True(File.Exists(historyPath));
            GraphLayoutDocument sourceEditedLayout = GraphLayoutJson.Deserialize(
                await File.ReadAllTextAsync(layoutPath));
            GraphFunctionLayout sourceEditedFunctionLayout = Assert.Single(
                sourceEditedLayout.Functions);
            Assert.Equal("Damage flow", Assert.Single(sourceEditedFunctionLayout.CommentRegions).Title);
            Assert.Contains(
                sourceEditedFunctionLayout.Nodes,
                static node => node.Placement == GraphNodePlacement.Unplaced);

            GraphNode addedLiteral = Assert.Single(
                committed.Graph.Functions.SelectMany(static function => function.Nodes),
                static node => node.Kind == "literal" &&
                    node.Properties.GetValueOrDefault("value") == "1");
            SemanticPatchDocument graphPatch = SemanticPatchDocument.Create(
                committed.ScriptAssets.SourceCompilationId,
                committed.ScriptAssets.SemanticConfigurationId,
                [new ReplaceLiteralPatchOperation(addedLiteral.Id, "1", "2")]);
            string graphPatchJson = SemanticPatchJson.Serialize(graphPatch);
            AuthoringPreviewResponse graphPreview = await PreviewAsync(
                client,
                "api/authoring/preview/semantic-patch",
                graphPatchJson,
                committed.Revision,
                "txn-graph-round-trip-stale",
                "Edit Graph literal");
            Assert.True(graphPreview.RoundTrip.LayoutChecked);
            Assert.Equal(1, graphPreview.RoundTrip.PreservedSourceComments);
            Assert.Equal(0, graphPreview.RoundTrip.RemovedSourceComments);
            Assert.Equal(1, graphPreview.RoundTrip.PreservedGraphComments);
            Assert.Equal(0, graphPreview.RoundTrip.NewUnplacedNodes);
            string sourceEditedLayoutText = await File.ReadAllTextAsync(layoutPath);
            await File.AppendAllTextAsync(layoutPath, "\n");
            using (HttpRequestMessage staleLayoutRequest = AuthoringRequest(
                "api/authoring/semantic-patch",
                graphPatchJson,
                committed.Revision,
                "txn-graph-round-trip-stale",
                "Edit Graph literal",
                previewToken: graphPreview.PreviewToken))
            using (HttpResponseMessage staleLayoutResponse = await client.SendAsync(staleLayoutRequest))
            {
                Assert.Equal(HttpStatusCode.Conflict, staleLayoutResponse.StatusCode);
                Assert.Contains(
                    "UAW1015",
                    await staleLayoutResponse.Content.ReadAsStringAsync(),
                    StringComparison.Ordinal);
            }
            Assert.Equal(editedSource, await File.ReadAllTextAsync(sourcePath));

            await File.WriteAllTextAsync(layoutPath, sourceEditedLayoutText);
            AuthoringPreviewResponse confirmedGraphPreview = await PreviewAsync(
                client,
                "api/authoring/preview/semantic-patch",
                graphPatchJson,
                committed.Revision,
                "txn-graph-round-trip",
                "Edit Graph literal");
            AuthoringWorkspaceResponse graphCommitted = await PostAsync(
                client,
                "api/authoring/semantic-patch",
                graphPatchJson,
                committed.Revision,
                "txn-graph-round-trip",
                "Edit Graph literal",
                previewToken: confirmedGraphPreview.PreviewToken);
            Assert.Equal(
                editedSource.Replace("+ 1;", "+ 2;", StringComparison.Ordinal),
                graphCommitted.SourceText);
            Assert.Contains("// Authored in the transactional C# view.", graphCommitted.SourceText);
            GraphNode replacedLiteral = Assert.Single(
                graphCommitted.Graph.Functions.SelectMany(static function => function.Nodes),
                static node => node.Kind == "literal" &&
                    node.Properties.GetValueOrDefault("value") == "2");
            Assert.Equal(addedLiteral.Id, replacedLiteral.Id);
            Assert.Equal(sourceEditedLayoutText, await File.ReadAllTextAsync(layoutPath));

            cancellation.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AuthoringEndpointsPersistReloadAndRejectExternalConflicts()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"finare-authoring-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "DamagePolicy.cs");
        string supportPath = Path.Combine(directory, "Support.cs");
        string identityPath = sourcePath + ".usids";
        string declarationPath = Path.Combine(directory, "DamagePolicy.asset.json");
        string surfacePath = Path.Combine(directory, "DamagePolicy.surface.json");
        string managedPath = Path.Combine(directory, "Finare.Analysis.dll");
        string pdbPath = Path.Combine(directory, "Finare.Analysis.pdb");
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
            "Finare.Analysis",
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
            Assert.Equal("unifiedscript.authoring-workspace/v0.5", baseline.SchemaVersion);
            Assert.Equal(logicalPath, baseline.SourcePath);
            Assert.Equal(Source, baseline.SourceText);
            Assert.Equal(
                VisualCSharpProfileContract.CurrentSchemaVersion,
                baseline.Profile.Profile.SchemaVersion);
            Assert.True(baseline.Profile.Succeeded);
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
            var staleStartRequest = new DebugStartRequest(
                baseline.Graph.Functions[0].Id,
                [Json("12")],
                new DebugReceiverRequest(asset.Id, []),
                [new SemanticId(clampAssignment.Properties["debugTargetId"])],
                SemanticId.FromKey("stale-native-surface"),
                baseline.ScriptAssets.SemanticConfigurationId);
            using HttpResponseMessage staleStartResponse = await client.PostAsync(
                "api/debug/start",
                JsonContent(JsonSerializer.Serialize(
                    staleStartRequest,
                    UnifiedJson.CreateOptions())));
            Assert.Equal(HttpStatusCode.Conflict, staleStartResponse.StatusCode);
            Assert.Contains(
                "UDB1002",
                await staleStartResponse.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
            var startRequest = new DebugStartRequest(
                baseline.Graph.Functions[0].Id,
                [Json("12")],
                new DebugReceiverRequest(asset.Id, []),
                [new SemanticId(clampAssignment.Properties["debugTargetId"])],
                baseline.ScriptAssets.SourceCompilationId,
                baseline.ScriptAssets.SemanticConfigurationId);
            using HttpResponseMessage startResponse = await client.PostAsync(
                "api/debug/start",
                JsonContent(JsonSerializer.Serialize(startRequest, UnifiedJson.CreateOptions())));
            startResponse.EnsureSuccessStatusCode();
            DebugSessionResponse running = JsonSerializer.Deserialize<DebugSessionResponse>(
                await startResponse.Content.ReadAsStringAsync(),
                UnifiedJson.CreateOptions())!;
            Assert.Equal("unifiedscript.debug-session/v0.2", running.SchemaVersion);
            Assert.Equal("running", running.State);
            using HttpResponseMessage waitResponse = await client.PostAsync(
                "api/debug/wait",
                JsonContent(JsonSerializer.Serialize(
                    new DebugExecutionRequest(
                        running.Generation.GenerationId,
                        running.ExecutionId),
                    UnifiedJson.CreateOptions())));
            waitResponse.EnsureSuccessStatusCode();
            DebugSessionResponse paused = JsonSerializer.Deserialize<DebugSessionResponse>(
                await waitResponse.Content.ReadAsStringAsync(),
                UnifiedJson.CreateOptions())!;
            Assert.Equal("paused", paused.State);
            DebugFrameLocalsResponse frameLocals = Assert.Single(paused.FrameLocals);
            Assert.Equal(Assert.Single(paused.Frames).FrameId, frameLocals.FrameId);
            using HttpResponseMessage evaluateResponse = await client.PostAsync(
                "api/debug/evaluate",
                JsonContent(JsonSerializer.Serialize(
                    new DebugEvaluateRequest(
                        paused.Generation.GenerationId,
                        paused.ExecutionId,
                        paused.Pause!.PauseId,
                        frameLocals.FrameId,
                        "scaled"),
                    UnifiedJson.CreateOptions())));
            evaluateResponse.EnsureSuccessStatusCode();
            CoreClrDebugEvaluation evaluation = JsonSerializer.Deserialize<CoreClrDebugEvaluation>(
                await evaluateResponse.Content.ReadAsStringAsync(),
                UnifiedJson.CreateOptions())!;
            Assert.Equal("240", evaluation.Value.Display);
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
                JsonContent(JsonSerializer.Serialize(
                    new DebugResumeRequest(
                        paused.Generation.GenerationId,
                        paused.ExecutionId,
                        paused.Pause.PauseId),
                    UnifiedJson.CreateOptions())));
            continueResponse.EnsureSuccessStatusCode();
            DebugSessionResponse resumed = JsonSerializer.Deserialize<DebugSessionResponse>(
                await continueResponse.Content.ReadAsStringAsync(),
                UnifiedJson.CreateOptions())!;
            Assert.Equal("running", resumed.State);
            using HttpResponseMessage completionWaitResponse = await client.PostAsync(
                "api/debug/wait",
                JsonContent(JsonSerializer.Serialize(
                    new DebugExecutionRequest(
                        resumed.Generation.GenerationId,
                        resumed.ExecutionId),
                    UnifiedJson.CreateOptions())));
            completionWaitResponse.EnsureSuccessStatusCode();
            DebugSessionResponse completed = JsonSerializer.Deserialize<DebugSessionResponse>(
                await completionWaitResponse.Content.ReadAsStringAsync(),
                UnifiedJson.CreateOptions())!;
            Assert.Equal("240", completed.Result!.ReturnValue!.Json);

            string sourceBeforeLivePublish = await File.ReadAllTextAsync(sourcePath);
            using HttpResponseMessage staleLivePublishResponse = await client.PostAsync(
                "api/debug/publish-live-world",
                JsonContent(JsonSerializer.Serialize(
                    new DebugLiveWorldPublishRequest(
                        SemanticId.FromKey("stale-live-world-source"),
                        baseline.ScriptAssets.SemanticConfigurationId),
                    UnifiedJson.CreateOptions())));
            Assert.Equal(HttpStatusCode.Conflict, staleLivePublishResponse.StatusCode);
            Assert.Contains(
                "UDB1002",
                await staleLivePublishResponse.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            using HttpResponseMessage livePublishResponse = await client.PostAsync(
                "api/debug/publish-live-world",
                JsonContent(JsonSerializer.Serialize(
                    new DebugLiveWorldPublishRequest(
                        baseline.ScriptAssets.SourceCompilationId,
                        baseline.ScriptAssets.SemanticConfigurationId),
                    UnifiedJson.CreateOptions())));
            livePublishResponse.EnsureSuccessStatusCode();
            DebugLiveWorldPublishResponse livePublish =
                JsonSerializer.Deserialize<DebugLiveWorldPublishResponse>(
                    await livePublishResponse.Content.ReadAsStringAsync(),
                    UnifiedJson.CreateOptions())!;
            Assert.Equal("unifiedscript.live-world-debug-publish/v0.1", livePublish.SchemaVersion);
            Assert.Equal(baseline.ScriptAssets.SourceCompilationId, livePublish.Generation.SourceCompilationId);
            Assert.Equal(
                baseline.ScriptAssets.SemanticConfigurationId,
                livePublish.Generation.SemanticConfigurationId);
            Assert.Equal(Path.GetFullPath(managedPath), livePublish.AssemblyPath);
            Assert.Equal(Path.GetFullPath(pdbPath), livePublish.PortablePdbPath);
            Assert.Equal(
                "unifiedscript.reload-transaction/v0.1",
                livePublish.ReloadTransaction.SchemaVersion);
            Assert.Equal("liveWorldDebug", livePublish.ReloadTransaction.Source);
            Assert.Equal("published", livePublish.ReloadTransaction.State);
            Assert.Equal(
                ["compile", "artifactPublication"],
                livePublish.ReloadTransaction.Phases.Select(static phase => phase.Name));
            Assert.All(
                livePublish.ReloadTransaction.Phases,
                static phase =>
                {
                    Assert.Equal("succeeded", phase.State);
                    Assert.True(phase.DurationMicroseconds >= 0);
                });
            Assert.Equal(sourceBeforeLivePublish, await File.ReadAllTextAsync(sourcePath));
            Assert.True(new FileInfo(managedPath).Length > 0);
            Assert.True(new FileInfo(pdbPath).Length > 0);
            using (var assemblyStream = File.OpenRead(managedPath))
            using (var peReader = new PEReader(assemblyStream))
            {
                MetadataReader metadata = peReader.GetMetadataReader();
                Assert.Contains(metadata.AssemblyReferences, handle =>
                    metadata.GetString(metadata.GetAssemblyReference(handle).Name) ==
                    "Finare.Runtime");
            }
            using (var pdbStream = File.OpenRead(pdbPath))
            using (MetadataReaderProvider pdbProvider =
                MetadataReaderProvider.FromPortablePdbStream(pdbStream))
            {
                Assert.NotEmpty(pdbProvider.GetMetadataReader().Documents);
            }

            AuthoringPreviewResponse declarationPreview = await PreviewAsync(
                client,
                "api/authoring/preview/declaration-patch",
                DeclarationPatchJson.Serialize(declarationPatch),
                baseline.Revision,
                "txn-declaration",
                "Update MaxDamage");
            Assert.Equal("unifiedscript.authoring-preview/v0.3", declarationPreview.SchemaVersion);
            Assert.Equal(baseline.Revision, declarationPreview.BaselineRevision);
            Assert.Equal(baseline.Revision + 1, declarationPreview.CandidateRevision);
            Assert.True(declarationPreview.PublishesRuntime);
            Assert.Equal(64, declarationPreview.PreviewToken.Length);
            Assert.Equal(
                SemanticGraphDiffDocument.CurrentSchemaVersion,
                declarationPreview.SemanticDiff.SchemaVersion);
            Assert.Equal(GraphDocument.CurrentSchemaVersion, declarationPreview.SemanticDiff.GraphSchemaVersion);
            Assert.Equal(baseline.Graph.SourceCompilationId, declarationPreview.SemanticDiff.BaselineCompilationId);
            Assert.Equal(0, declarationPreview.SemanticDiff.Summary.ChangeCount);
            Assert.Empty(declarationPreview.SemanticDiff.Functions);
            AuthoringPreviewFile declarationFile = Assert.Single(declarationPreview.Files);
            Assert.Equal(logicalPath, declarationFile.Path);
            Assert.Equal(Source, declarationFile.BeforeText);
            Assert.Contains("= 120;", declarationFile.AfterText, StringComparison.Ordinal);
            Assert.Equal(Source, await File.ReadAllTextAsync(sourcePath));
            Assert.False(File.Exists(identityPath));
            Assert.False(File.Exists(declarationPath));

            using (HttpRequestMessage mismatchedPreviewRequest = AuthoringRequest(
                "api/authoring/declaration-patch",
                DeclarationPatchJson.Serialize(declarationPatch),
                baseline.Revision,
                "txn-declaration",
                "Update MaxDamage",
                previewToken: new string('0', 64)))
            using (HttpResponseMessage mismatchedPreviewResponse =
                await client.SendAsync(mismatchedPreviewRequest))
            {
                Assert.Equal(HttpStatusCode.Conflict, mismatchedPreviewResponse.StatusCode);
                DebugErrorResponse mismatch = JsonSerializer.Deserialize<DebugErrorResponse>(
                    await mismatchedPreviewResponse.Content.ReadAsStringAsync(),
                    UnifiedJson.CreateOptions())!;
                Assert.Equal("UAW1012", mismatch.Error.Code);
                Assert.Equal("unchanged", mismatch.Rollback!.State);
                Assert.Equal(baseline.Revision, mismatch.Rollback.Revision);
                Assert.Equal(baseline.Generation.Number, mismatch.Rollback.Generation);
                Assert.Contains(
                    mismatch.QuickFixes!,
                    static fix => fix.Kind == "refreshWorkspace");
                Assert.Equal(Source, await File.ReadAllTextAsync(sourcePath));
            }

            AuthoringWorkspaceResponse declared = await PostAsync(
                client,
                "api/authoring/declaration-patch",
                DeclarationPatchJson.Serialize(declarationPatch),
                baseline.Revision,
                "txn-declaration",
                "Update MaxDamage",
                previewToken: declarationPreview.PreviewToken);

            Assert.Equal(2, declared.Revision);
            Assert.Equal(2, declared.Generation.Number);
            Assert.True(declared.History.CanUndo);
            Assert.False(declared.History.CanRedo);
            Assert.Equal(1, declared.History.UndoDepth);
            Assert.Equal("txn-declaration", declared.History.UndoTransactionId);
            Assert.Equal("Update MaxDamage", declared.History.UndoDescription);
            Assert.Equal("txn-declaration", declared.History.LastOperationId);
            Assert.Equal("persisted", declared.History.PersistenceState);
            Assert.Equal(await File.ReadAllTextAsync(sourcePath), declared.SourceText);
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
            Assert.Equal(await File.ReadAllTextAsync(sourcePath), behavior.SourceText);
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
            AiSemanticPatchProposalDraft proposalDraft = AiSemanticPatchProposalDraft.Create(
                "Raise clamp minimum",
                "Replace the selected stable-ID literal with a typed patch-plan step.",
                new AiSemanticPatchProposalProducer("http-test-agent", "1.0", "fixture-model"),
                plan);
            AiSemanticPatchProposalDocument proposal;
            using (HttpRequestMessage draftRequest = AuthoringRequest(
                "api/authoring/ai-semantic-proposal/draft",
                AiSemanticPatchProposalJson.SerializeDraft(proposalDraft),
                behavior.Revision,
                "txn-plan",
                "Apply AI clamp plan"))
            using (HttpResponseMessage draftResponse = await client.SendAsync(draftRequest))
            {
                draftResponse.EnsureSuccessStatusCode();
                proposal = AiSemanticPatchProposalJson.Deserialize(
                    await draftResponse.Content.ReadAsStringAsync());
            }
            Assert.Contains(
                "Math.Clamp(scaled, 10f, MaxDamage)",
                await File.ReadAllTextAsync(sourcePath),
                StringComparison.Ordinal);

            using (HttpRequestMessage staleProposalRequest = AuthoringRequest(
                "api/authoring/preview/ai-semantic-proposal",
                AiSemanticPatchProposalJson.Serialize(proposal),
                behavior.Revision - 1,
                "txn-plan-stale",
                "Apply stale AI clamp plan"))
            using (HttpResponseMessage staleProposalResponse =
                await client.SendAsync(staleProposalRequest))
            {
                Assert.Equal(HttpStatusCode.Conflict, staleProposalResponse.StatusCode);
                DebugErrorResponse error = JsonSerializer.Deserialize<DebugErrorResponse>(
                    await staleProposalResponse.Content.ReadAsStringAsync(),
                    UnifiedJson.CreateOptions())!;
                Assert.Equal("UAW1005", error.Error.Code);
            }

            AuthoringAiSemanticProposalPreviewResponse aiPreview;
            using (HttpRequestMessage previewRequest = AuthoringRequest(
                "api/authoring/preview/ai-semantic-proposal",
                AiSemanticPatchProposalJson.Serialize(proposal),
                behavior.Revision,
                "txn-plan",
                "Apply AI clamp plan"))
            using (HttpResponseMessage previewResponse = await client.SendAsync(previewRequest))
            {
                previewResponse.EnsureSuccessStatusCode();
                aiPreview = JsonSerializer.Deserialize<AuthoringAiSemanticProposalPreviewResponse>(
                    await previewResponse.Content.ReadAsStringAsync(),
                    UnifiedJson.CreateOptions())!;
            }
            Assert.Equal(proposal.ProposalId, aiPreview.Proposal.ProposalId);
            Assert.Contains("12f", Assert.Single(aiPreview.Preview.Files).AfterText);
            Assert.Contains(
                "Math.Clamp(scaled, 10f, MaxDamage)",
                await File.ReadAllTextAsync(sourcePath),
                StringComparison.Ordinal);

            AiSemanticPatchProposalDocument substitutedProposal =
                AiSemanticPatchProposalDocument.Create(proposalDraft with
                {
                    Intent = "A different, independently identified proposal intent.",
                });
            using (HttpRequestMessage substitutedRequest = AuthoringRequest(
                "api/authoring/ai-semantic-proposal",
                AiSemanticPatchProposalJson.Serialize(substitutedProposal),
                behavior.Revision,
                "txn-plan",
                "Apply AI clamp plan",
                previewToken: aiPreview.Preview.PreviewToken))
            using (HttpResponseMessage substitutedResponse = await client.SendAsync(substitutedRequest))
            {
                Assert.Equal(HttpStatusCode.Conflict, substitutedResponse.StatusCode);
                DebugErrorResponse error = JsonSerializer.Deserialize<DebugErrorResponse>(
                    await substitutedResponse.Content.ReadAsStringAsync(),
                    UnifiedJson.CreateOptions())!;
                Assert.Equal("UAW1012", error.Error.Code);
            }

            using (HttpRequestMessage previewRequest = AuthoringRequest(
                "api/authoring/preview/ai-semantic-proposal",
                AiSemanticPatchProposalJson.Serialize(proposal),
                behavior.Revision,
                "txn-plan",
                "Apply AI clamp plan"))
            using (HttpResponseMessage previewResponse = await client.SendAsync(previewRequest))
            {
                previewResponse.EnsureSuccessStatusCode();
                aiPreview = JsonSerializer.Deserialize<AuthoringAiSemanticProposalPreviewResponse>(
                    await previewResponse.Content.ReadAsStringAsync(),
                    UnifiedJson.CreateOptions())!;
            }
            AuthoringAiSemanticProposalCommitResponse aiCommit;
            using (HttpRequestMessage commitRequest = AuthoringRequest(
                "api/authoring/ai-semantic-proposal",
                AiSemanticPatchProposalJson.Serialize(proposal),
                behavior.Revision,
                "txn-plan",
                "Apply AI clamp plan",
                previewToken: aiPreview.Preview.PreviewToken))
            using (HttpResponseMessage commitResponse = await client.SendAsync(commitRequest))
            {
                commitResponse.EnsureSuccessStatusCode();
                aiCommit = JsonSerializer.Deserialize<AuthoringAiSemanticProposalCommitResponse>(
                    await commitResponse.Content.ReadAsStringAsync(),
                    UnifiedJson.CreateOptions())!;
            }
            Assert.Equal(proposal.ProposalId, aiCommit.ProposalId);
            AuthoringWorkspaceResponse planned = aiCommit.Workspace;

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
            DebugErrorResponse externalConflict = JsonSerializer.Deserialize<DebugErrorResponse>(
                errorJson,
                UnifiedJson.CreateOptions())!;
            UnifiedDiagnostic externalDiagnostic = Assert.Single(externalConflict.Diagnostics!);
            Assert.Equal("UAW1002", externalDiagnostic.Code);
            Assert.Equal(logicalSupportPath, externalDiagnostic.Span.FilePath);
            Assert.Equal("unchanged", externalConflict.Rollback!.State);
            Assert.Equal(redone.Revision, externalConflict.Rollback.Revision);
            Assert.Contains(
                externalConflict.QuickFixes!,
                static fix => fix.Kind == "openFirstDiagnostic");
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
    public async Task GroupedAuthoringStagesWithoutWritesThenCommitsAndUndoesAtomically()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"finare-authoring-group-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "DamagePolicy.cs");
        string identityPath = sourcePath + ".usids";
        string historyPath = Path.Combine(directory, "DamagePolicy.authoring-history.json");
        string contentRoot = Path.Combine(directory, "workbench");
        Directory.CreateDirectory(contentRoot);
        await File.WriteAllTextAsync(sourcePath, Source);
        await File.WriteAllTextAsync(Path.Combine(contentRoot, "index.html"), "<!doctype html><title>test</title>");
        string logicalPath = sourcePath.Replace('\\', '/');
        var compilation = CSharpCompilationRequest.Single(
            Source,
            logicalPath,
            identitySidecar: IdentitySidecarDocument.CreateEmpty(logicalPath));
        int port = ReserveLoopbackPort();

        try
        {
            using var server = new CoreClrDebugHttpServer(
                compilation,
                sourcePath,
                identityPath,
                contentRoot,
                port,
                new AuthoringArtifactOutputs(HistoryPath: historyPath));
            using var cancellation = new CancellationTokenSource();
            Task serverTask = server.RunAsync(cancellation.Token);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

            AuthoringWorkspaceResponse baseline = await GetWorkspaceAsync(client);
            AuthoringWorkspaceResponse begun = await PostAsync(
                client,
                "api/authoring/group/begin",
                "{}",
                baseline.Revision,
                "group-damage-tuning",
                "Damage tuning");
            Assert.Equal(1, begun.Revision);
            Assert.Equal(1, begun.Generation.Number);
            Assert.Equal("group-damage-tuning", begun.Group!.Id);
            Assert.Equal(0, begun.Group.StagedOperationCount);

            GraphNode minimum = begun.Graph.Functions[0].Nodes.Single(static node =>
                node.Kind == "literal" && node.Properties.GetValueOrDefault("value") == "0f");
            SemanticPatchDocument behaviorPatch = SemanticPatchDocument.Create(
                begun.ScriptAssets.SourceCompilationId,
                begun.ScriptAssets.SemanticConfigurationId,
                [new ReplaceLiteralPatchOperation(minimum.Id, "0f", "10f")]);
            using (HttpRequestMessage ungroupedRequest = AuthoringRequest(
                "api/authoring/semantic-patch",
                SemanticPatchJson.Serialize(behaviorPatch),
                begun.Revision,
                "ungrouped-minimum",
                "Ungrouped minimum"))
            using (HttpResponseMessage ungroupedResponse = await client.SendAsync(ungroupedRequest))
            {
                Assert.Equal(HttpStatusCode.Conflict, ungroupedResponse.StatusCode);
                Assert.Contains(
                    "UAW1009",
                    await ungroupedResponse.Content.ReadAsStringAsync(),
                    StringComparison.Ordinal);
                Assert.Equal(Source, await File.ReadAllTextAsync(sourcePath));
            }

            AuthoringPreviewResponse behaviorPreview = await PreviewAsync(
                client,
                "api/authoring/preview/semantic-patch",
                SemanticPatchJson.Serialize(behaviorPatch),
                begun.Revision,
                "stage-minimum",
                "Stage minimum",
                begun.Group.Id);
            Assert.False(behaviorPreview.PublishesRuntime);
            Assert.Equal(1, behaviorPreview.SemanticDiff.Summary.ModifiedFunctions);
            Assert.Contains(
                behaviorPreview.SemanticDiff.Functions.SelectMany(static function => function.Changes),
                static change => change.Category == SemanticGraphChangeCategory.Value &&
                    change.Facet == "property:value" && change.Before == "0f" && change.After == "10f");
            Assert.Contains(
                "Math.Clamp(scaled, 10f, MaxDamage)",
                Assert.Single(behaviorPreview.Files).AfterText,
                StringComparison.Ordinal);
            Assert.Equal(Source, await File.ReadAllTextAsync(sourcePath));

            AuthoringWorkspaceResponse stagedBehavior = await PostAsync(
                client,
                "api/authoring/semantic-patch",
                SemanticPatchJson.Serialize(behaviorPatch),
                begun.Revision,
                "stage-minimum",
                "Stage minimum",
                begun.Group.Id,
                behaviorPreview.PreviewToken);
            Assert.Equal(2, stagedBehavior.Revision);
            Assert.Equal(1, stagedBehavior.Generation.Number);
            Assert.Equal(1, stagedBehavior.Group!.StagedOperationCount);
            Assert.Equal(Source, await File.ReadAllTextAsync(sourcePath));
            Assert.False(File.Exists(identityPath));

            ScriptStateDeclaration maxDamage = stagedBehavior.ScriptAssets.Assets[0].States
                .Single(static state => state.Name == "MaxDamage");
            DeclarationPatchDocument declarationPatch = DeclarationPatchDocument.Create(
                stagedBehavior.ScriptAssets.SourceCompilationId,
                stagedBehavior.ScriptAssets.SemanticConfigurationId,
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
                        maxDamage.TypeMetadataName,
                        maxDamage.TypeKind,
                        Json("120"),
                        maxDamage.Exposure))]);
            using (HttpRequestMessage staleGroupedRequest = AuthoringRequest(
                "api/authoring/declaration-patch",
                DeclarationPatchJson.Serialize(declarationPatch),
                begun.Revision,
                "stage-maximum-stale",
                "Stage maximum from stale surface",
                begun.Group.Id))
            using (HttpResponseMessage staleGroupedResponse = await client.SendAsync(staleGroupedRequest))
            {
                Assert.Equal(HttpStatusCode.Conflict, staleGroupedResponse.StatusCode);
                Assert.Contains(
                    "UAW1005",
                    await staleGroupedResponse.Content.ReadAsStringAsync(),
                    StringComparison.Ordinal);
                AuthoringWorkspaceResponse unchangedDraft = await GetWorkspaceAsync(client);
                Assert.Equal(stagedBehavior.Revision, unchangedDraft.Revision);
                Assert.Equal(1, unchangedDraft.Group!.StagedOperationCount);
                Assert.Equal(Source, await File.ReadAllTextAsync(sourcePath));
            }

            AuthoringWorkspaceResponse stagedDeclaration = await PostAsync(
                client,
                "api/authoring/declaration-patch",
                DeclarationPatchJson.Serialize(declarationPatch),
                stagedBehavior.Revision,
                "stage-maximum",
                "Stage maximum",
                begun.Group.Id);
            Assert.Equal(3, stagedDeclaration.Revision);
            Assert.Equal(1, stagedDeclaration.Generation.Number);
            Assert.Equal(2, stagedDeclaration.Group!.StagedOperationCount);
            Assert.Equal(Source, await File.ReadAllTextAsync(sourcePath));

            AuthoringPreviewResponse groupPreview = await PreviewAsync(
                client,
                "api/authoring/preview/group/commit",
                "{}",
                stagedDeclaration.Revision,
                begun.Group.Id,
                begun.Group.Description,
                begun.Group.Id);
            Assert.True(groupPreview.PublishesRuntime);
            AuthoringPreviewFile groupFile = Assert.Single(groupPreview.Files);
            Assert.Equal(Source, groupFile.BeforeText);
            Assert.Contains("Math.Clamp(scaled, 10f, MaxDamage)", groupFile.AfterText);
            Assert.Contains("= 120;", groupFile.AfterText);
            Assert.Equal(Source, await File.ReadAllTextAsync(sourcePath));

            AuthoringWorkspaceResponse committed = await PostAsync(
                client,
                "api/authoring/group/commit",
                "{}",
                stagedDeclaration.Revision,
                begun.Group.Id,
                begun.Group.Description,
                begun.Group.Id,
                groupPreview.PreviewToken);
            Assert.Null(committed.Group);
            Assert.Equal(2, committed.Revision);
            Assert.Equal(2, committed.Generation.Number);
            Assert.Equal(1, committed.History.UndoDepth);
            Assert.Equal("group-damage-tuning", committed.History.UndoTransactionId);
            Assert.Equal("Damage tuning", committed.History.UndoDescription);
            Assert.NotNull(committed.ReloadTransaction);
            Assert.Equal(
                "unifiedscript.reload-transaction/v0.1",
                committed.ReloadTransaction.SchemaVersion);
            Assert.Equal("group-damage-tuning", committed.ReloadTransaction.TransactionId);
            Assert.Equal("authoring", committed.ReloadTransaction.Source);
            Assert.Equal("published", committed.ReloadTransaction.State);
            Assert.Equal(
                ["compile", "artifactPublication", "workbenchGenerationSwap"],
                committed.ReloadTransaction.Phases.Select(static phase => phase.Name));
            Assert.All(
                committed.ReloadTransaction.Phases,
                static phase => Assert.Equal("succeeded", phase.State));
            string committedSource = await File.ReadAllTextAsync(sourcePath);
            Assert.Contains("Math.Clamp(scaled, 10f, MaxDamage)", committedSource);
            Assert.Contains("= 120;", committedSource);

            AuthoringWorkspaceResponse undone = await PostAsync(
                client,
                "api/authoring/undo",
                "{}",
                committed.Revision,
                "undo-group-damage-tuning",
                "Undo damage tuning");
            Assert.Equal(Source, await File.ReadAllTextAsync(sourcePath));
            Assert.Equal(1, undone.History.RedoDepth);

            AuthoringWorkspaceResponse cancelBegun = await PostAsync(
                client,
                "api/authoring/group/begin",
                "{}",
                undone.Revision,
                "group-cancelled",
                "Cancelled tuning");
            GraphNode cancelMinimum = cancelBegun.Graph.Functions[0].Nodes.Single(static node =>
                node.Kind == "literal" && node.Properties.GetValueOrDefault("value") == "0f");
            SemanticPatchDocument cancelPatch = SemanticPatchDocument.Create(
                cancelBegun.ScriptAssets.SourceCompilationId,
                cancelBegun.ScriptAssets.SemanticConfigurationId,
                [new ReplaceLiteralPatchOperation(cancelMinimum.Id, "0f", "5f")]);
            AuthoringWorkspaceResponse cancelStaged = await PostAsync(
                client,
                "api/authoring/semantic-patch",
                SemanticPatchJson.Serialize(cancelPatch),
                cancelBegun.Revision,
                "stage-cancelled",
                "Stage cancelled edit",
                cancelBegun.Group!.Id);
            AuthoringWorkspaceResponse cancelled = await PostAsync(
                client,
                "api/authoring/group/cancel",
                "{}",
                cancelStaged.Revision,
                cancelBegun.Group.Id,
                cancelBegun.Group.Description,
                cancelBegun.Group.Id);
            Assert.Null(cancelled.Group);
            Assert.Equal(undone.Revision, cancelled.Revision);
            Assert.Equal(undone.Generation.Number, cancelled.Generation.Number);
            Assert.Equal(Source, await File.ReadAllTextAsync(sourcePath));

            AuthoringWorkspaceResponse conflictBegun = await PostAsync(
                client,
                "api/authoring/group/begin",
                "{}",
                cancelled.Revision,
                "group-external-conflict",
                "Externally conflicting damage tuning");
            GraphNode conflictMinimum = conflictBegun.Graph.Functions[0].Nodes.Single(static node =>
                node.Kind == "literal" && node.Properties.GetValueOrDefault("value") == "0f");
            SemanticPatchDocument conflictBehaviorPatch = SemanticPatchDocument.Create(
                conflictBegun.ScriptAssets.SourceCompilationId,
                conflictBegun.ScriptAssets.SemanticConfigurationId,
                [new ReplaceLiteralPatchOperation(conflictMinimum.Id, "0f", "5f")]);
            AuthoringWorkspaceResponse conflictBehavior = await PostAsync(
                client,
                "api/authoring/semantic-patch",
                SemanticPatchJson.Serialize(conflictBehaviorPatch),
                conflictBegun.Revision,
                "stage-conflicting-minimum",
                "Stage conflicting minimum",
                conflictBegun.Group!.Id);
            ScriptStateDeclaration conflictMaximum = conflictBehavior.ScriptAssets.Assets[0].States
                .Single(static state => state.Name == "MaxDamage");
            DeclarationPatchDocument conflictDeclarationPatch = DeclarationPatchDocument.Create(
                conflictBehavior.ScriptAssets.SourceCompilationId,
                conflictBehavior.ScriptAssets.SemanticConfigurationId,
                [new UpdateScriptStatePatchOperation(
                    conflictMaximum.Id,
                    new ScriptStatePatchPrecondition(
                        conflictMaximum.Name,
                        conflictMaximum.TypeMetadataName,
                        conflictMaximum.TypeKind,
                        conflictMaximum.DefaultValue,
                        conflictMaximum.Exposure),
                    new ScriptStatePatchSpec(
                        conflictMaximum.Name,
                        conflictMaximum.TypeMetadataName,
                        conflictMaximum.TypeKind,
                        Json("140"),
                        conflictMaximum.Exposure))]);
            AuthoringWorkspaceResponse conflictDeclaration = await PostAsync(
                client,
                "api/authoring/declaration-patch",
                DeclarationPatchJson.Serialize(conflictDeclarationPatch),
                conflictBehavior.Revision,
                "stage-conflicting-maximum",
                "Stage conflicting maximum",
                conflictBegun.Group.Id);
            AuthoringPreviewResponse conflictPreview = await PreviewAsync(
                client,
                "api/authoring/preview/group/commit",
                "{}",
                conflictDeclaration.Revision,
                conflictBegun.Group.Id,
                conflictBegun.Group.Description,
                conflictBegun.Group.Id);
            Assert.Contains("Math.Clamp(scaled, 5f, MaxDamage)", Assert.Single(conflictPreview.Files).AfterText);
            Assert.Contains("= 140;", Assert.Single(conflictPreview.Files).AfterText);
            string identityBeforeExternalConflict = await File.ReadAllTextAsync(identityPath);
            string historyBeforeExternalConflict = await File.ReadAllTextAsync(historyPath);
            await File.AppendAllTextAsync(sourcePath, "\n// external grouped edit\n");

            using (HttpRequestMessage externalConflictRequest = AuthoringRequest(
                "api/authoring/group/commit",
                "{}",
                conflictDeclaration.Revision,
                conflictBegun.Group.Id,
                conflictBegun.Group.Description,
                conflictBegun.Group.Id,
                conflictPreview.PreviewToken))
            using (HttpResponseMessage externalConflictResponse = await client.SendAsync(externalConflictRequest))
            {
                Assert.Equal(HttpStatusCode.Conflict, externalConflictResponse.StatusCode);
                DebugErrorResponse error = JsonSerializer.Deserialize<DebugErrorResponse>(
                    await externalConflictResponse.Content.ReadAsStringAsync(),
                    UnifiedJson.CreateOptions())!;
                Assert.Equal("UAW1002", error.Error.Code);
                Assert.Equal("unchanged", error.Rollback!.State);
                Assert.Equal(conflictDeclaration.Revision, error.Rollback.Revision);
            }

            string externallyEditedSource = await File.ReadAllTextAsync(sourcePath);
            Assert.Contains("// external grouped edit", externallyEditedSource);
            Assert.DoesNotContain("Math.Clamp(scaled, 5f, MaxDamage)", externallyEditedSource);
            Assert.DoesNotContain("= 140;", externallyEditedSource);
            Assert.Equal(identityBeforeExternalConflict, await File.ReadAllTextAsync(identityPath));
            Assert.Equal(historyBeforeExternalConflict, await File.ReadAllTextAsync(historyPath));
            AuthoringWorkspaceResponse conflictedDraft = await GetWorkspaceAsync(client);
            Assert.Equal(conflictDeclaration.Revision, conflictedDraft.Revision);
            Assert.Equal(2, conflictedDraft.Group!.StagedOperationCount);
            Assert.Equal(undone.Generation.Number, conflictedDraft.Generation.Number);
            AuthoringWorkspaceResponse conflictCancelled = await PostAsync(
                client,
                "api/authoring/group/cancel",
                "{}",
                conflictDeclaration.Revision,
                conflictBegun.Group.Id,
                conflictBegun.Group.Description,
                conflictBegun.Group.Id);
            Assert.Null(conflictCancelled.Group);
            Assert.Equal(cancelled.Revision, conflictCancelled.Revision);

            cancellation.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PreparedPreviewCommitsTheIdentityExactCandidate()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"finare-authoring-prepared-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "DamagePolicy.cs");
        string identityPath = sourcePath + ".usids";
        string contentRoot = Path.Combine(directory, "workbench");
        Directory.CreateDirectory(contentRoot);
        await File.WriteAllTextAsync(sourcePath, Source);
        await File.WriteAllTextAsync(
            Path.Combine(contentRoot, "index.html"),
            "<!doctype html><title>test</title>");
        string logicalPath = sourcePath.Replace('\\', '/');
        int port = ReserveLoopbackPort();

        try
        {
            using var server = new CoreClrDebugHttpServer(
                CSharpCompilationRequest.Single(
                    Source,
                    logicalPath,
                    identitySidecar: IdentitySidecarDocument.CreateEmpty(logicalPath)),
                sourcePath,
                identityPath,
                contentRoot,
                port);
            using var cancellation = new CancellationTokenSource();
            Task serverTask = server.RunAsync(cancellation.Token);
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            };

            AuthoringWorkspaceResponse baseline = await GetWorkspaceAsync(client);
            AuthoringFunctionSurface function = Assert.Single(
                Assert.Single(baseline.Surface.Assets).Functions);
            AuthoringRegionSurface body = Assert.Single(
                function.Regions,
                static region => region.Kind == GraphRegionKind.Block &&
                    region.ParentRegionId is not null);
            SemanticId firstStatementId = body.DirectStatementIds[0];
            SemanticId intTypeId = function.Parameters
                .Single(static parameter => parameter.Name == "incomingDamage")
                .TypeId!.Value;
            SemanticPatchDocument insertion = SemanticPatchDocument.Create(
                baseline.ScriptAssets.SourceCompilationId,
                baseline.ScriptAssets.SemanticConfigurationId,
                [new InsertStatementPatchOperation(
                    body.SourceNodeId,
                    null,
                    firstStatementId,
                    new LocalDeclarationPatchStatement(
                        "previewed",
                        "int",
                        new LiteralPatchExpression("7", intTypeId),
                        intTypeId))]);

            AuthoringPreviewResponse preview = await PreviewAsync(
                client,
                "api/authoring/preview/semantic-patch",
                SemanticPatchJson.Serialize(insertion),
                baseline.Revision,
                "txn-identity-exact-preview",
                "Insert previewed local");
            Assert.Contains("int previewed = 7;", Assert.Single(preview.Files).AfterText);
            Assert.Equal(Source, await File.ReadAllTextAsync(sourcePath));

            AuthoringWorkspaceResponse committed = await PostAsync(
                client,
                "api/authoring/semantic-patch",
                SemanticPatchJson.Serialize(insertion),
                baseline.Revision,
                "txn-identity-exact-preview",
                "Insert previewed local",
                previewToken: preview.PreviewToken);
            Assert.Equal(baseline.Revision + 1, committed.Revision);
            Assert.Contains("int previewed = 7;", await File.ReadAllTextAsync(sourcePath));
            IdentitySidecarDocument sidecar = IdentitySidecarJson.Deserialize(
                await File.ReadAllTextAsync(identityPath));
            Assert.True(
                sidecar.Entries.Count(static entry => entry.Kind == "variableDeclaration") >= 2);

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
            $"finare-authoring-history-{Guid.NewGuid():N}");
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
                "Finare.Analysis",
                [new CSharpSourceDocument(logical, File.ReadAllText(path), identity)]);
        }
    }

    [Fact]
    public async Task NativePublicationReservationBlocksAuthoringUntilCancelled()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"finare-native-reservation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "DamagePolicy.cs");
        string identityPath = Path.Combine(directory, "DamagePolicy.usids");
        string historyPath = Path.Combine(directory, "history.json");
        string layoutPath = Path.Combine(directory, "layout.json");
        string contentRoot = Path.Combine(directory, "workbench");
        Directory.CreateDirectory(contentRoot);
        await File.WriteAllTextAsync(sourcePath, Source);
        await File.WriteAllTextAsync(Path.Combine(contentRoot, "index.html"), "<!doctype html><title>test</title>");
        string logicalPath = sourcePath.Replace('\\', '/');
        int port = ReserveLoopbackPort();

        try
        {
            using var server = new CoreClrDebugHttpServer(
                CSharpCompilationRequest.Single(
                    Source,
                    logicalPath,
                    identitySidecar: IdentitySidecarDocument.CreateEmpty(logicalPath)),
                sourcePath,
                identityPath,
                contentRoot,
                port,
                new AuthoringArtifactOutputs(HistoryPath: historyPath, GraphLayoutPath: layoutPath));
            using var cancellation = new CancellationTokenSource();
            Task serverTask = server.RunAsync(cancellation.Token);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

            AuthoringWorkspaceResponse baseline = await GetWorkspaceAsync(client);
            string editedSource = baseline.SourceText.Replace(
                "float scaled = incomingDamage * BaseDamage;",
                "float scaled = incomingDamage * BaseDamage + 3;",
                StringComparison.Ordinal);
            string patchJson = CSharpSourceTextPatchJson.Serialize(
                CSharpSourceTextPatchDocument.Create(baseline.SourcePath, baseline.SourceText, editedSource));
            AuthoringPreviewResponse preview = await PreviewAsync(
                client,
                "api/authoring/preview/source-text",
                patchJson,
                baseline.Revision,
                "native-reservation",
                "Native reservation");

            using (HttpRequestMessage prepareRequest = AuthoringRequest(
                       "api/authoring/native/prepare",
                       "{}",
                       baseline.Revision,
                       "native-reservation",
                       "Native reservation",
                       previewToken: preview.PreviewToken))
            using (HttpResponseMessage prepareResponse = await client.SendAsync(prepareRequest))
            {
                prepareResponse.EnsureSuccessStatusCode();
            }

            async Task AssertReservedAsync(string path, string body, string transactionId)
            {
                using HttpRequestMessage request = AuthoringRequest(
                    path,
                    body,
                    baseline.Revision,
                    transactionId,
                    "Blocked while native publication is reserved");
                using HttpResponseMessage response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                DebugErrorResponse error = JsonSerializer.Deserialize<DebugErrorResponse>(
                    await response.Content.ReadAsStringAsync(),
                    UnifiedJson.CreateOptions())!;
                Assert.Equal("UAW1022", error.Error.Code);
                Assert.Contains("native publication is reserved", error.Error.Message, StringComparison.OrdinalIgnoreCase);
            }

            await AssertReservedAsync("api/authoring/preview/source-text", patchJson, "reserved-source-preview");
            await AssertReservedAsync("api/authoring/group/begin", "{}", "reserved-group");
            await AssertReservedAsync("api/authoring/preview/undo", "{}", "reserved-undo");
            await AssertReservedAsync("api/authoring/refresh", "{}", "reserved-refresh");

            using (HttpRequestMessage cancelRequest = AuthoringRequest(
                       "api/authoring/native/cancel",
                       "{}",
                       baseline.Revision,
                       "native-reservation",
                       "Native reservation",
                       previewToken: preview.PreviewToken))
            using (HttpResponseMessage cancelResponse = await client.SendAsync(cancelRequest))
            {
                cancelResponse.EnsureSuccessStatusCode();
                NativePublicationFinishResponse cancelled = JsonSerializer.Deserialize<NativePublicationFinishResponse>(
                    await cancelResponse.Content.ReadAsStringAsync(),
                    UnifiedJson.CreateOptions())!;
                Assert.Equal("cancelled", cancelled.State);
                Assert.Null(cancelled.Workspace);
            }

            using (HttpRequestMessage cancelRetryRequest = AuthoringRequest(
                       "api/authoring/native/cancel",
                       "{}",
                       baseline.Revision,
                       "native-reservation",
                       "Native reservation",
                       previewToken: preview.PreviewToken))
            using (HttpResponseMessage cancelRetryResponse = await client.SendAsync(cancelRetryRequest))
            {
                cancelRetryResponse.EnsureSuccessStatusCode();
                NativePublicationFinishResponse cancelledAgain = JsonSerializer.Deserialize<NativePublicationFinishResponse>(
                    await cancelRetryResponse.Content.ReadAsStringAsync(),
                    UnifiedJson.CreateOptions())!;
                Assert.Equal("cancelled", cancelledAgain.State);
                Assert.Equal("native-reservation", cancelledAgain.TransactionId);
            }

            AuthoringPreviewResponse afterCancel = await PreviewAsync(
                client,
                "api/authoring/preview/source-text",
                patchJson,
                baseline.Revision,
                "after-native-reservation",
                "After native reservation");
            Assert.Equal(baseline.Revision + 1, afterCancel.CandidateRevision);
            Assert.Equal(Source, await File.ReadAllTextAsync(sourcePath));

            cancellation.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task NativePublicationPreparesBytesAndAdoptsIdempotently()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"finare-native-publication-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "DamagePolicy.cs");
        string identityPath = Path.Combine(directory, "DamagePolicy.usids");
        string historyPath = Path.Combine(directory, "history.json");
        string layoutPath = Path.Combine(directory, "layout.json");
        string contentRoot = Path.Combine(directory, "workbench");
        Directory.CreateDirectory(contentRoot);
        await File.WriteAllTextAsync(sourcePath, Source);
        await File.WriteAllTextAsync(Path.Combine(contentRoot, "index.html"), "<!doctype html><title>test</title>");
        string logicalPath = sourcePath.Replace('\\', '/');
        var request = new CSharpCompilationRequest(
            "Finare.NativePublication",
            [new CSharpSourceDocument(logicalPath, Source, IdentitySidecarDocument.CreateEmpty(logicalPath))]);
        int port = ReserveLoopbackPort();

        try
        {
            using var server = new CoreClrDebugHttpServer(
                request,
                sourcePath,
                identityPath,
                contentRoot,
                port,
                new AuthoringArtifactOutputs(HistoryPath: historyPath, GraphLayoutPath: layoutPath));
            using var cancellation = new CancellationTokenSource();
            _ = server.RunAsync(cancellation.Token);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            AuthoringWorkspaceResponse baseline = await GetWorkspaceAsync(client);
            string editedSource = baseline.SourceText.Replace(
                "float scaled = incomingDamage * BaseDamage;",
                "float scaled = incomingDamage * BaseDamage + 2;",
                StringComparison.Ordinal);
            string patchJson = CSharpSourceTextPatchJson.Serialize(
                CSharpSourceTextPatchDocument.Create(baseline.SourcePath, baseline.SourceText, editedSource));
            AuthoringPreviewResponse preview = await PreviewAsync(
                client,
                "api/authoring/preview/source-text",
                patchJson,
                baseline.Revision,
                "native-publication",
                "Native publication");

            using (HttpRequestMessage prepareRequest = AuthoringRequest(
                       "api/authoring/native/prepare",
                       JsonSerializer.Serialize(
                           new NativePublicationPrepareRequest(preview.PreviewToken),
                           UnifiedJson.CreateOptions(writeIndented: false)),
                       baseline.Revision,
                       "native-publication",
                       "Native publication",
                       previewToken: preview.PreviewToken))
            using (HttpResponseMessage prepareResponse = await client.SendAsync(prepareRequest))
            {
                prepareResponse.EnsureSuccessStatusCode();
                NativePublicationPrepareResponse prepared = JsonSerializer.Deserialize<NativePublicationPrepareResponse>(
                    await prepareResponse.Content.ReadAsStringAsync(), UnifiedJson.CreateOptions())!;
                Assert.Equal("unifiedscript.native-publication-prepare/v0.1", prepared.SchemaVersion);
                Assert.Equal(preview.PreviewToken, prepared.PreviewToken);
                Assert.True(prepared.Outputs.Count > 0);
                Assert.Equal(Source, await File.ReadAllTextAsync(sourcePath));
                foreach (NativePublicationOutput output in prepared.Outputs)
                    await File.WriteAllBytesAsync(output.Path, output.Content);

                using (HttpRequestMessage changedCancelRequest = AuthoringRequest(
                           "api/authoring/native/cancel",
                           "{}",
                           baseline.Revision,
                           "native-publication",
                           "Native publication",
                           previewToken: preview.PreviewToken))
                using (HttpResponseMessage changedCancelResponse = await client.SendAsync(changedCancelRequest))
                {
                    Assert.Equal(HttpStatusCode.Conflict, changedCancelResponse.StatusCode);
                    DebugErrorResponse changedCancelError = JsonSerializer.Deserialize<DebugErrorResponse>(
                        await changedCancelResponse.Content.ReadAsStringAsync(), UnifiedJson.CreateOptions())!;
                    Assert.Equal("UAW1023", changedCancelError.Error.Code);
                }

                using (HttpRequestMessage changedFailureRequest = AuthoringRequest(
                           "api/authoring/native/finish",
                           JsonSerializer.Serialize(
                               new NativePublicationFinishRequest(preview.PreviewToken, false),
                               UnifiedJson.CreateOptions(writeIndented: false)),
                           baseline.Revision,
                           "native-publication",
                           "Native publication",
                           previewToken: preview.PreviewToken))
                using (HttpResponseMessage changedFailureResponse = await client.SendAsync(changedFailureRequest))
                {
                    Assert.Equal(HttpStatusCode.Conflict, changedFailureResponse.StatusCode);
                    DebugErrorResponse changedFailureError = JsonSerializer.Deserialize<DebugErrorResponse>(
                        await changedFailureResponse.Content.ReadAsStringAsync(), UnifiedJson.CreateOptions())!;
                    Assert.Equal("UAW1023", changedFailureError.Error.Code);
                }

                using HttpRequestMessage foreignFinishRequest = AuthoringRequest(
                    "api/authoring/native/finish",
                    JsonSerializer.Serialize(
                        new NativePublicationFinishRequest(preview.PreviewToken, true),
                        UnifiedJson.CreateOptions(writeIndented: false)),
                    baseline.Revision,
                    "foreign-native-publication",
                    "Foreign native publication",
                    previewToken: preview.PreviewToken);
                using HttpResponseMessage foreignFinishResponse = await client.SendAsync(foreignFinishRequest);
                Assert.Equal(HttpStatusCode.Conflict, foreignFinishResponse.StatusCode);
                DebugErrorResponse foreignError = JsonSerializer.Deserialize<DebugErrorResponse>(
                    await foreignFinishResponse.Content.ReadAsStringAsync(), UnifiedJson.CreateOptions())!;
                Assert.Equal("UAW1021", foreignError.Error.Code);
            }

            string finishJson = JsonSerializer.Serialize(
                new NativePublicationFinishRequest(preview.PreviewToken, true),
                UnifiedJson.CreateOptions(writeIndented: false));
            using (HttpRequestMessage finishRequest = AuthoringRequest(
                       "api/authoring/native/finish",
                       finishJson,
                       baseline.Revision,
                       "native-publication",
                       "Native publication",
                       previewToken: preview.PreviewToken))
            using (HttpResponseMessage finishResponse = await client.SendAsync(finishRequest))
            {
                finishResponse.EnsureSuccessStatusCode();
                NativePublicationFinishResponse finished = JsonSerializer.Deserialize<NativePublicationFinishResponse>(
                    await finishResponse.Content.ReadAsStringAsync(), UnifiedJson.CreateOptions())!;
                Assert.Equal("adopted", finished.State);
                Assert.Equal(baseline.Revision + 1, finished.Workspace!.Revision);
            }

            using (HttpRequestMessage retryRequest = AuthoringRequest(
                       "api/authoring/native/finish",
                       finishJson,
                       baseline.Revision,
                       "native-publication",
                       "Native publication",
                       previewToken: preview.PreviewToken))
            using (HttpResponseMessage retryResponse = await client.SendAsync(retryRequest))
            {
                retryResponse.EnsureSuccessStatusCode();
                NativePublicationFinishResponse retry = JsonSerializer.Deserialize<NativePublicationFinishResponse>(
                    await retryResponse.Content.ReadAsStringAsync(), UnifiedJson.CreateOptions())!;
                Assert.Equal("adopted", retry.State);
                Assert.Equal(baseline.Revision + 1, retry.Workspace!.Revision);
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task NativePublicationMovesTheRealUndoRedoHistoryCursor()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"finare-native-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "DamagePolicy.cs");
        string identityPath = Path.Combine(directory, "DamagePolicy.usids");
        string historyPath = Path.Combine(directory, "history.json");
        string layoutPath = Path.Combine(directory, "layout.json");
        string contentRoot = Path.Combine(directory, "workbench");
        Directory.CreateDirectory(contentRoot);
        await File.WriteAllTextAsync(sourcePath, Source);
        await File.WriteAllTextAsync(Path.Combine(contentRoot, "index.html"), "<!doctype html><title>test</title>");
        string logicalPath = sourcePath.Replace('\\', '/');
        int port = ReserveLoopbackPort();

        try
        {
            using var server = new CoreClrDebugHttpServer(
                CSharpCompilationRequest.Single(
                    Source,
                    logicalPath,
                    identitySidecar: IdentitySidecarDocument.CreateEmpty(logicalPath)),
                sourcePath,
                identityPath,
                contentRoot,
                port,
                new AuthoringArtifactOutputs(HistoryPath: historyPath, GraphLayoutPath: layoutPath));
            using var cancellation = new CancellationTokenSource();
            Task serverTask = server.RunAsync(cancellation.Token);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

            AuthoringWorkspaceResponse baseline = await GetWorkspaceAsync(client);
            string editedSource = baseline.SourceText.Replace(
                "float scaled = incomingDamage * BaseDamage;",
                "float scaled = incomingDamage * BaseDamage + 2;",
                StringComparison.Ordinal);
            string patchJson = CSharpSourceTextPatchJson.Serialize(
                CSharpSourceTextPatchDocument.Create(baseline.SourcePath, baseline.SourceText, editedSource));
            AuthoringPreviewResponse editPreview = await PreviewAsync(
                client,
                "api/authoring/preview/source-text",
                patchJson,
                baseline.Revision,
                "native-history-edit",
                "Native history edit");
            AuthoringWorkspaceResponse edited = await PublishNativeAsync(
                editPreview,
                baseline.Revision,
                "native-history-edit",
                "Native history edit");
            Assert.True(edited.History.CanUndo);
            Assert.False(edited.History.CanRedo);
            Assert.Equal(editedSource, await File.ReadAllTextAsync(sourcePath));

            AuthoringPreviewResponse undoPreview = await PreviewAsync(
                client,
                "api/authoring/preview/undo",
                "{}",
                edited.Revision,
                "native-history-undo",
                "Native history undo");
            Assert.Contains(
                "float scaled = incomingDamage * BaseDamage;",
                undoPreview.Files.Single(file => file.AfterText.Contains(
                    "namespace Tests.HttpAuthoring;",
                    StringComparison.Ordinal)).AfterText);
            AuthoringWorkspaceResponse undone = await PublishNativeAsync(
                undoPreview,
                edited.Revision,
                "native-history-undo",
                "Native history undo");
            Assert.True(undone.History.CanRedo);
            Assert.False(undone.History.CanUndo);
            Assert.Equal(Source, await File.ReadAllTextAsync(sourcePath));

            AuthoringPreviewResponse redoPreview = await PreviewAsync(
                client,
                "api/authoring/preview/redo",
                "{}",
                undone.Revision,
                "native-history-redo",
                "Native history redo");
            Assert.Contains(
                "float scaled = incomingDamage * BaseDamage + 2;",
                redoPreview.Files.Single(file => file.AfterText.Contains(
                    "namespace Tests.HttpAuthoring;",
                    StringComparison.Ordinal)).AfterText);
            AuthoringWorkspaceResponse redone = await PublishNativeAsync(
                redoPreview,
                undone.Revision,
                "native-history-redo",
                "Native history redo");
            Assert.True(redone.History.CanUndo);
            Assert.False(redone.History.CanRedo);
            Assert.Equal(editedSource, await File.ReadAllTextAsync(sourcePath));

            cancellation.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

            async Task<AuthoringWorkspaceResponse> PublishNativeAsync(
                AuthoringPreviewResponse preview,
                long revision,
                string transactionId,
                string description)
            {
                NativePublicationPrepareResponse prepared;
                using (HttpRequestMessage prepareRequest = AuthoringRequest(
                           "api/authoring/native/prepare",
                           "{}",
                           revision,
                           transactionId,
                           description,
                           previewToken: preview.PreviewToken))
                using (HttpResponseMessage prepareResponse = await client.SendAsync(prepareRequest))
                {
                    prepareResponse.EnsureSuccessStatusCode();
                    prepared = JsonSerializer.Deserialize<NativePublicationPrepareResponse>(
                        await prepareResponse.Content.ReadAsStringAsync(),
                        UnifiedJson.CreateOptions())!;
                }

                foreach (NativePublicationOutput output in prepared.Outputs)
                    await File.WriteAllBytesAsync(output.Path, output.Content);

                string finishJson = JsonSerializer.Serialize(
                    new NativePublicationFinishRequest(preview.PreviewToken, true),
                    UnifiedJson.CreateOptions(writeIndented: false));
                using HttpRequestMessage finishRequest = AuthoringRequest(
                    "api/authoring/native/finish",
                    finishJson,
                    revision,
                    transactionId,
                    description,
                    previewToken: preview.PreviewToken);
                using HttpResponseMessage finishResponse = await client.SendAsync(finishRequest);
                finishResponse.EnsureSuccessStatusCode();
                NativePublicationFinishResponse finished = JsonSerializer.Deserialize<NativePublicationFinishResponse>(
                    await finishResponse.Content.ReadAsStringAsync(),
                    UnifiedJson.CreateOptions())!;
                Assert.Equal("adopted", finished.State);
                return finished.Workspace!;
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
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
        string description,
        string? groupId = null,
        string? previewToken = null)
    {
        using HttpRequestMessage request = AuthoringRequest(
            path,
            json,
            baselineRevision,
            transactionId,
            description,
            groupId,
            previewToken);
        using HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return Deserialize(await response.Content.ReadAsStringAsync());
    }

    private static async Task<AuthoringPreviewResponse> PreviewAsync(
        HttpClient client,
        string path,
        string json,
        long baselineRevision,
        string transactionId,
        string description,
        string? groupId = null)
    {
        using HttpRequestMessage request = AuthoringRequest(
            path,
            json,
            baselineRevision,
            transactionId,
            description,
            groupId);
        using HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<AuthoringPreviewResponse>(
            await response.Content.ReadAsStringAsync(),
            UnifiedJson.CreateOptions()) ??
            throw new InvalidOperationException("The authoring preview response was empty.");
    }

    private static HttpRequestMessage AuthoringRequest(
        string path,
        string json,
        long baselineRevision,
        string transactionId,
        string description,
        string? groupId = null,
        string? previewToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent(json),
        };
        request.Headers.Add("X-Finare-Baseline-Revision", baselineRevision.ToString());
        request.Headers.Add("X-Finare-Transaction-Id", transactionId);
        request.Headers.Add("X-Finare-Transaction-Label", description);
        if (groupId is not null)
        {
            request.Headers.Add("X-Finare-Group-Id", groupId);
        }
        if (previewToken is not null)
        {
            request.Headers.Add("X-Finare-Preview-Token", previewToken);
        }
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
