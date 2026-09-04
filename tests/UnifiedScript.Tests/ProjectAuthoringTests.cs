using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UnifiedScript.Cli;
using UnifiedScript.Core;
using UnifiedScript.CSharp;
using UnifiedScript.Graph;
using UnifiedScript.Runtime;

namespace UnifiedScript.Tests;

public sealed class ProjectAuthoringTests
{
    [Fact]
    public async Task ProjectGraphAndSourceEditsPublishExecutableChunksWithStableIdentities()
    {
        string directory = Path.Combine(Path.GetTempPath(), "UnifiedScript.ProjectAuthoring", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = """
            using UnifiedScript;
            namespace ProjectFixture;
            [ScriptAsset("Tests/ProjectPlayground")]
            public class Playground
            {
                [Visual]
                public int Evaluate()
                {
            #if PROJECT_AUTHORING
                    return Helpers.Adjust(10);
            #else
                    return -1;
            #endif
                }
            }
            """;
        string primary = Path.Combine(directory, "Playground.cs");
        string helper = Path.Combine(directory, "Helpers.cs");
        string project = Path.Combine(directory, "Playground.csproj");
        string chunk = Path.Combine(directory, "output", "Playground.uschunk");
        string helpers = "namespace ProjectFixture; public static class Helpers { public static int Adjust(int value) => Math.Abs(value) + 2; }";
        await File.WriteAllTextAsync(primary, source);
        await File.WriteAllTextAsync(helper, helpers);
        await File.WriteAllTextAsync(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>
                <DefineConstants>PROJECT_AUTHORING</DefineConstants><Nullable>enable</Nullable></PropertyGroup>
              <ItemGroup><Reference Include="UnifiedScript.Core"><HintPath>{System.Security.SecurityElement.Escape(typeof(VisualAttribute).Assembly.Location)}</HintPath></Reference></ItemGroup>
            </Project>
            """);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        try
        {
            ProjectAuthoringInput input = ProjectAuthoringInput.Load(project, primary);
            Assert.Contains("PROJECT_AUTHORING", input.Request.Settings.PreprocessorSymbols);
            Assert.Contains(input.Request.Sources, file => file.LogicalPath.EndsWith("GlobalUsings.g.cs", StringComparison.Ordinal));
            Assert.Contains("Helpers.cs", input.ReadOnlySources);
            Assert.False(input.Request.IncludeDefaultReferences);
            Assert.Throws<ArgumentException>(() => ProjectAuthoringInput.Load(project, project,
                options: new CSharpProjectLoadOptions(Restore: false, Build: false)));

            using var server = new CoreClrDebugHttpServer(input.Request, input.SourcePath, input.IdentityPath,
                directory, port, new AuthoringArtifactOutputs(
                    Path.ChangeExtension(chunk, ".asset.json"), Path.ChangeExtension(chunk, ".surface.json"),
                    Path.ChangeExtension(chunk, ".dll"), Path.ChangeExtension(chunk, ".pdb"),
                    Path.ChangeExtension(chunk, ".history.json"), CompiledChunkPath: chunk),
                input.SourceFiles, readOnlySources: input.ReadOnlySources, publishInitial: true);
            using var cancellation = new CancellationTokenSource();
            Task running = server.RunAsync(cancellation.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
                AuthoringWorkspaceResponse baseline = await Workspace(client);
                Assert.Equal("Playground.cs", baseline.SourcePath);
                Assert.Equal(12, ExecuteChunk(chunk));
                byte[] originalChunk = await File.ReadAllBytesAsync(chunk);
                DateTime helperWrite = File.GetLastWriteTimeUtc(helper);
                GraphNode literal = baseline.Graph.Functions.Single().Nodes.Single(node =>
                    node.Kind == "literal" && node.Properties.GetValueOrDefault("value") == "10");
                string patch = SemanticPatchJson.Serialize(SemanticPatchDocument.Create(
                    baseline.ScriptAssets.SourceCompilationId, baseline.ScriptAssets.SemanticConfigurationId,
                    [new ReplaceLiteralPatchOperation(literal.Id, "10", "20")]));
                AuthoringWorkspaceResponse graphed = await Apply(client, baseline, "semantic-patch", patch, "graph-20");
                Assert.Equal(22, ExecuteChunk(chunk));
                Assert.Contains("Adjust(20)", await File.ReadAllTextAsync(primary));
                Assert.Equal(helpers, await File.ReadAllTextAsync(helper));
                Assert.Equal(helperWrite, File.GetLastWriteTimeUtc(helper));
                Assert.Equal(literal.Id, graphed.Graph.Functions.Single().Nodes.Single(node =>
                    node.Kind == "literal" && node.Properties.GetValueOrDefault("value") == "20").Id);
                string sourcePatch = CSharpSourceTextPatchJson.Serialize(CSharpSourceTextPatchDocument.Create(
                    graphed.SourcePath, graphed.SourceText, graphed.SourceText.Replace("Adjust(20)", "Adjust(30)", StringComparison.Ordinal)));
                AuthoringWorkspaceResponse edited = await Apply(client, graphed, "source-text", sourcePatch, "source-30");
                Assert.Equal(32, ExecuteChunk(chunk));
                Assert.NotEqual(originalChunk, await File.ReadAllBytesAsync(chunk));

                // A normal project emit must pick up the exact identities saved by Graph.
                string emitted = Path.Combine(directory, "ordinary-build");
                Assert.Equal(0, Program.Main(["emit", project, "--out", emitted, "--no-build", "--no-restore"]));
                using (CoreClrCompiledChunkSession normal = new CoreClrCompiledChunkHost().Load(
                    File.ReadAllBytes(Path.Combine(emitted, "Playground.uschunk")), [typeof(VisualAttribute).Assembly.Location]))
                    Assert.Equal(edited.ScriptAssets.SourceCompilationId, normal.Declarations.SourceCompilationId);

                byte[] beforeRejected = await File.ReadAllBytesAsync(chunk);
                string secondaryPatch = CSharpSourceTextPatchJson.Serialize(CSharpSourceTextPatchDocument.Create(
                    "Helpers.cs", helpers, helpers.Replace("+ 2", "+ 3", StringComparison.Ordinal)));
                using HttpRequestMessage readonlyPreview = Request("api/authoring/preview/source-text", secondaryPatch, edited.Revision, "readonly-helper");
                using HttpResponseMessage rejected = await client.SendAsync(readonlyPreview);
                Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
                Assert.Contains("UAW1017", await rejected.Content.ReadAsStringAsync());
                AuthoringWorkspaceResponse unchanged = await Workspace(client);
                Assert.Equal(edited.Revision, unchanged.Revision);
                Assert.Equal(edited.Generation.GenerationId, unchanged.Generation.GenerationId);
                Assert.Equal(helpers, await File.ReadAllTextAsync(helper));
                Assert.Equal(beforeRejected, await File.ReadAllBytesAsync(chunk));

                // Undo also republishes a matching chunk, rather than leaving the newest runtime behind.
                using HttpRequestMessage undo = Request("api/authoring/undo", "{}", edited.Revision, "undo-source");
                using HttpResponseMessage undone = await client.SendAsync(undo);
                undone.EnsureSuccessStatusCode();
                Assert.Equal(22, ExecuteChunk(chunk));
            }
            finally { cancellation.Cancel(); await running; }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ProjectDebugServeRejectsManualSourceAndReferenceOverrides()
    {
        Assert.Equal(64, Program.Main(["debug-serve", "Main.cs", "--root", ".", "--project", "Game.csproj", "--source", "Guess.cs"]));
        Assert.Equal(64, Program.Main(["debug-serve", "Main.cs", "--root", ".", "--project", "Game.csproj", "--reference", "Guess.dll"]));
        Assert.Equal(64, Program.Main(["debug-serve", "Main.cs", "--root", ".", "--configuration", "Debug"]));
        Assert.Equal(64, Program.Main(["debug-serve", "Main.cs", "--root", ".", "--project", "Game.csproj", "--configuration", "Debug", "--configuration", "Release"]));
    }

    private static int ExecuteChunk(string path)
    {
        using CoreClrCompiledChunkSession session = new CoreClrCompiledChunkHost().Load(
            File.ReadAllBytes(path), [typeof(VisualAttribute).Assembly.Location]);
        object instance = session.CreateEntryInstance("ProjectFixture.Playground");
        return (int)instance.GetType().GetMethod("Evaluate")!.Invoke(instance, null)!;
    }

    private static async Task<AuthoringWorkspaceResponse> Workspace(HttpClient client) =>
        JsonSerializer.Deserialize<AuthoringWorkspaceResponse>(await client.GetStringAsync("api/authoring/workspace"), UnifiedJson.CreateOptions())!;

    private static HttpRequestMessage Request(string path, string content, long revision, string id, string? preview = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
        message.Headers.Add("X-UnifiedScript-Baseline-Revision", revision.ToString());
        message.Headers.Add("X-UnifiedScript-Transaction-Id", id);
        if (preview is not null) message.Headers.Add("X-UnifiedScript-Preview-Token", preview);
        return message;
    }

    private static async Task<AuthoringWorkspaceResponse> Apply(HttpClient client, AuthoringWorkspaceResponse baseline, string kind, string content, string id)
    {
        using HttpRequestMessage preview = Request($"api/authoring/preview/{kind}", content, baseline.Revision, id);
        using HttpResponseMessage previewResult = await client.SendAsync(preview);
        previewResult.EnsureSuccessStatusCode();
        AuthoringPreviewResponse accepted = JsonSerializer.Deserialize<AuthoringPreviewResponse>(
            await previewResult.Content.ReadAsStringAsync(), UnifiedJson.CreateOptions())!;
        using HttpRequestMessage commit = Request($"api/authoring/{kind}", content, baseline.Revision, id, accepted.PreviewToken);
        using HttpResponseMessage result = await client.SendAsync(commit);
        result.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<AuthoringWorkspaceResponse>(await result.Content.ReadAsStringAsync(), UnifiedJson.CreateOptions())!;
    }
}
