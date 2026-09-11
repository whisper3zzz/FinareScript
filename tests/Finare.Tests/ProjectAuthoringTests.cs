using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Finare.Authoring;
using Finare.Cli;
using Finare.Core;
using Finare.CSharp;
using Finare.Graph;
using Finare.Runtime;

namespace Finare.Tests;

[Trait("Category", "Authoring")]
[Collection("Authoring HTTP servers")]
public sealed class ProjectAuthoringTests
{
    [Fact]
    public void ProjectAuthoringInputRejectsAmbiguousScriptAssetSelection()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Finare.ProjectAuthoringAmbiguous", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string primary = Path.Combine(directory, "Multiple.cs");
            string project = Path.Combine(directory, "Multiple.csproj");
            File.WriteAllText(primary, "using Finare; namespace PartialFixture; [ScriptAsset(\"Tests/One\")] public class One { } [ScriptAsset(\"Tests/Two\")] public class Two { }");
            File.WriteAllText(project, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
                  <ItemGroup>
                    <Reference Include="Finare.Core"><HintPath>{System.Security.SecurityElement.Escape(typeof(VisualAttribute).Assembly.Location)}</HintPath></Reference>
                    <Reference Include="Finare.Unreal"><HintPath>{System.Security.SecurityElement.Escape(typeof(Finare.Unreal.UnrealScriptClassAttribute).Assembly.Location)}</HintPath></Reference>
                  </ItemGroup>
                </Project>
                """);

            ArgumentException error = Assert.Throws<ArgumentException>(() => ProjectAuthoringInput.Load(
                project,
                primary,
                options: new CSharpProjectLoadOptions(Restore: true, Build: false)));
            Assert.Contains("multiple", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ScriptAsset", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ProjectAuthoringInputOpensOnlyTheSemanticBindingOwnerPartial()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Finare.ProjectAuthoringPartial", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string primary = Path.Combine(directory, "Partial.cs");
            string binding = Path.Combine(directory, "Partial.Binding.cs");
            string helper = Path.Combine(directory, "Helpers.cs");
            string project = Path.Combine(directory, "Partial.csproj");
            File.WriteAllText(primary, "using Finare; namespace PartialFixture; [ScriptAsset(\"Tests/Partial\")] public partial class Partial { }");
            File.WriteAllText(binding, "using Finare.Unreal; namespace PartialFixture; [UnrealScriptClass(UnrealScriptClassKind.Actor, GeneratedClassPath=\"/Game/Partial.Partial_C\")] public partial class Partial { }");
            File.WriteAllText(helper, "namespace PartialFixture; public static class Helpers { public static int Value => 1; }");
            File.WriteAllText(project, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
                  <ItemGroup>
                    <Reference Include="Finare.Core"><HintPath>{System.Security.SecurityElement.Escape(typeof(VisualAttribute).Assembly.Location)}</HintPath></Reference>
                    <Reference Include="Finare.Unreal"><HintPath>{System.Security.SecurityElement.Escape(typeof(Finare.Unreal.UnrealScriptClassAttribute).Assembly.Location)}</HintPath></Reference>
                  </ItemGroup>
                </Project>
                """);

            ProjectAuthoringInput input = ProjectAuthoringInput.Load(
                project,
                primary,
                options: new CSharpProjectLoadOptions(Restore: true, Build: false));

            Assert.DoesNotContain("Partial.cs", input.ReadOnlySources);
            Assert.DoesNotContain("Partial.Binding.cs", input.ReadOnlySources);
            Assert.Contains("Helpers.cs", input.ReadOnlySources);
            string bindingLogical = input.SourceFiles.Single(pair => pair.Value == binding).Key;
            string helperLogical = input.SourceFiles.Single(pair => pair.Value == helper).Key;
            Assert.Equal(binding + ".usids", input.IdentityFiles[bindingLogical]);
            Assert.Equal(helper + ".usids", input.IdentityFiles[helperLogical]);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PartialBindingOwnerSourceAndSidecarPublishThroughProjectHttpSession()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Finare.ProjectAuthoringPartialHttp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string primary = Path.Combine(directory, "Partial.cs");
        string binding = Path.Combine(directory, "Partial.Binding.cs");
        string helper = Path.Combine(directory, "Helpers.cs");
        string project = Path.Combine(directory, "Partial.csproj");
        File.WriteAllText(primary, "using Finare; namespace PartialFixture; [ScriptAsset(\"Tests/Partial\")] public partial class Partial { }");
        string bindingSource = "using Finare.Unreal; namespace PartialFixture; [UnrealScriptClass(UnrealScriptClassKind.Actor, GeneratedClassPath=\"/Game/Partial.Partial_C\")] public partial class Partial { public int Marker { get; set; } = 1; }";
        File.WriteAllText(binding, bindingSource);
        File.WriteAllText(helper, "namespace PartialFixture; public static class Helpers { public static int Value => 1; }");
        File.WriteAllText(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
              <ItemGroup>
                <Reference Include="Finare.Core"><HintPath>{System.Security.SecurityElement.Escape(typeof(VisualAttribute).Assembly.Location)}</HintPath></Reference>
                <Reference Include="Finare.Unreal"><HintPath>{System.Security.SecurityElement.Escape(typeof(Finare.Unreal.UnrealScriptClassAttribute).Assembly.Location)}</HintPath></Reference>
              </ItemGroup>
            </Project>
            """);
        try
        {
            ProjectAuthoringInput input = ProjectAuthoringInput.Load(
                project, primary, options: new CSharpProjectLoadOptions(Restore: true, Build: false));
            CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(input.Request);
            Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
            string bindingLogical = input.SourceFiles.Single(pair => pair.Value == binding).Key;
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            using var server = new CoreClrDebugHttpServer(
                input.Request, input.SourcePath, input.IdentityPath, directory, port,
                sourceFiles: input.SourceFiles,
                readOnlySources: input.ReadOnlySources,
                publishInitial: true,
                refreshProject: () => ProjectAuthoringInput.Load(
                    project, primary, options: new CSharpProjectLoadOptions(Restore: false, Build: false)),
                identityFiles: input.IdentityFiles);
            using var cancellation = new CancellationTokenSource();
            Task running = server.RunAsync(cancellation.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
                AuthoringWorkspaceResponse baseline = await Workspace(client);
                string changed = bindingSource.Replace("{ get; set; } = 1;", "{ get; set; } = 2;", StringComparison.Ordinal);
                string patch = CSharpSourceTextPatchJson.Serialize(
                    CSharpSourceTextPatchDocument.Create(bindingLogical, bindingSource, changed));
                AuthoringWorkspaceResponse after = await Apply(client, baseline, "source-text", patch, "partial-owner-edit");
                Assert.Equal(changed, await File.ReadAllTextAsync(binding));
                Assert.True(File.Exists(input.IdentityFiles[bindingLogical]));
                Assert.NotEmpty(await File.ReadAllTextAsync(input.IdentityFiles[bindingLogical]));
                using HttpRequestMessage helperRequest = Request(
                    "api/authoring/preview/source-text",
                    CSharpSourceTextPatchJson.Serialize(CSharpSourceTextPatchDocument.Create(
                        input.SourceFiles.Single(pair => pair.Value == helper).Key,
                        await File.ReadAllTextAsync(helper),
                        "namespace PartialFixture; public static class Helpers { public static int Value => 2; }")),
                    after.Revision, "partial-helper-rejected");
                using HttpResponseMessage rejected = await client.SendAsync(helperRequest);
                Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
                Assert.Contains("UAW1017", await rejected.Content.ReadAsStringAsync());
            }
            finally
            {
                cancellation.Cancel();
                await running;
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PartialBindingOwnerPreservesCustomSidecarsAcrossNativeHistoryAndRefresh()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Finare.ProjectAuthoringSidecars", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string sourceDirectory = Path.Combine(directory, "Visual");
        Directory.CreateDirectory(sourceDirectory);
        string primary = Path.Combine(sourceDirectory, "Partial.cs");
        string binding = Path.Combine(sourceDirectory, "Partial.Binding.cs");
        string helper = Path.Combine(directory, "Helpers.cs");
        string project = Path.Combine(directory, "Partial.csproj");
        string primaryIdentity = Path.Combine(directory, "identities", "primary.custom.usids");
        string bindingIdentity = binding + ".usids";
        string primaryLogical = "Visual/Partial.cs";
        string bindingLogical = "Visual/Partial.Binding.cs";
        string primarySource = "using Finare; namespace PartialFixture; [ScriptAsset(\"Tests/Partial\")] public partial class Partial { }";
        string bindingSource = "using Finare.Unreal; namespace PartialFixture; [UnrealScriptClass(UnrealScriptClassKind.Actor, GeneratedClassPath=\"/Game/Partial.Partial_C\")] public partial class Partial { public int Marker { get; set; } = 1; }";
        string helperSource = "namespace PartialFixture; public static class Helpers { public static int Value => 1; }";
        File.WriteAllText(primary, primarySource);
        File.WriteAllText(binding, bindingSource);
        File.WriteAllText(helper, helperSource);
        File.WriteAllText(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
              <ItemGroup>
                <Reference Include="Finare.Core"><HintPath>{System.Security.SecurityElement.Escape(typeof(VisualAttribute).Assembly.Location)}</HintPath></Reference>
                <Reference Include="Finare.Unreal"><HintPath>{System.Security.SecurityElement.Escape(typeof(Finare.Unreal.UnrealScriptClassAttribute).Assembly.Location)}</HintPath></Reference>
              </ItemGroup>
            </Project>
            """);
        Directory.CreateDirectory(Path.GetDirectoryName(primaryIdentity)!);
        File.WriteAllText(primaryIdentity, IdentitySidecarJson.Serialize(
            IdentitySidecarDocument.CreateEmpty(primaryLogical)) + "\n");
        File.WriteAllText(bindingIdentity, IdentitySidecarJson.Serialize(
            IdentitySidecarDocument.CreateEmpty(bindingLogical)) + "\n");

        try
        {
            ProjectAuthoringInput input = ProjectAuthoringInput.Load(
                project,
                primary,
                primaryIdentity,
                new CSharpProjectLoadOptions(Restore: true, Build: false));
            string bindingLogicalPath = input.SourceFiles.Single(pair => pair.Value == binding).Key;
            string helperLogicalPath = input.SourceFiles.Single(pair => pair.Value == helper).Key;
            Assert.Equal(primaryLogical, input.SourceFiles.Single(pair => pair.Value == primary).Key);
            Assert.Equal(bindingLogical, bindingLogicalPath);
            Assert.DoesNotContain(bindingLogicalPath, input.ReadOnlySources);
            Assert.Contains(helperLogicalPath, input.ReadOnlySources);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            using var server = new CoreClrDebugHttpServer(
                input.Request,
                input.SourcePath,
                input.IdentityPath,
                directory,
                port,
                sourceFiles: input.SourceFiles,
                readOnlySources: input.ReadOnlySources,
                publishInitial: true,
                refreshProject: () => ProjectAuthoringInput.Load(
                    project,
                    primary,
                    primaryIdentity,
                    new CSharpProjectLoadOptions(Restore: false, Build: false)),
                identityFiles: input.IdentityFiles);
            using var cancellation = new CancellationTokenSource();
            Task running = server.RunAsync(cancellation.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
                AuthoringWorkspaceResponse baseline = await Workspace(client);
                Assert.Equal(primaryLogical, baseline.SourcePath);
                Assert.Null(baseline.ScriptAssets.Assets.Single().ClassBinding!.BaseClassPath);
                SemanticId assetId = baseline.ScriptAssets.Assets.Single().Id;
                IdentitySidecarDocument initialPrimary = IdentitySidecarJson.Deserialize(
                    await File.ReadAllTextAsync(primaryIdentity));
                IdentitySidecarDocument initialBinding = IdentitySidecarJson.Deserialize(
                    await File.ReadAllTextAsync(bindingIdentity));
                Assert.Equal(primaryLogical, initialPrimary.SourcePath);
                Assert.Equal(bindingLogical, initialBinding.SourcePath);

                string added = AddBaseClassPath(bindingSource, "/Script/Engine.Actor");
                AuthoringWorkspaceResponse addedWorkspace = await PublishNativeSource(
                    client, baseline, bindingLogicalPath, bindingSource, added, "base-add");
                Assert.Equal("/Script/Engine.Actor", addedWorkspace.ScriptAssets.Assets.Single().ClassBinding!.BaseClassPath);
                Assert.Equal(assetId, addedWorkspace.ScriptAssets.Assets.Single().Id);
                Assert.Equal(added, await File.ReadAllTextAsync(binding));
                Assert.Equal(primaryLogical, IdentitySidecarJson.Deserialize(await File.ReadAllTextAsync(primaryIdentity)).SourcePath);
                Assert.Equal(bindingLogical, IdentitySidecarJson.Deserialize(await File.ReadAllTextAsync(bindingIdentity)).SourcePath);
                Assert.Equal(initialPrimary.Entries.Select(entry => entry.Id),
                    IdentitySidecarJson.Deserialize(await File.ReadAllTextAsync(primaryIdentity)).Entries.Select(entry => entry.Id));
                Assert.Equal(initialBinding.Entries.Select(entry => entry.Id),
                    IdentitySidecarJson.Deserialize(await File.ReadAllTextAsync(bindingIdentity)).Entries.Select(entry => entry.Id));

                string changed = AddBaseClassPath(added, "/Script/Engine.Pawn");
                AuthoringWorkspaceResponse changedWorkspace = await PublishNativeSource(
                    client, addedWorkspace, bindingLogicalPath, added, changed, "base-change");
                Assert.Equal("/Script/Engine.Pawn", changedWorkspace.ScriptAssets.Assets.Single().ClassBinding!.BaseClassPath);

                string cleared = RemoveBaseClassPath(changed);
                AuthoringWorkspaceResponse clearedWorkspace = await PublishNativeSource(
                    client, changedWorkspace, bindingLogicalPath, changed, cleared, "base-clear");
                Assert.Null(clearedWorkspace.ScriptAssets.Assets.Single().ClassBinding!.BaseClassPath);

                // Prepare a further owner change, then cancel before adopting any
                // candidate bytes. The owner source and both sidecars must stay at
                // the last adopted generation.
                string pending = AddBaseClassPath(cleared, "/Script/Engine.Actor");
                byte[] sourceBeforeCancel = await File.ReadAllBytesAsync(binding);
                byte[] primaryBeforeCancel = await File.ReadAllBytesAsync(primaryIdentity);
                byte[] bindingBeforeCancel = await File.ReadAllBytesAsync(bindingIdentity);
                AuthoringPreviewResponse pendingPreview = await PreviewSource(
                    client, clearedWorkspace, bindingLogicalPath, cleared, pending, "base-cancel");
                NativePublicationPrepareResponse pendingPrepare = await PrepareNative(client, pendingPreview, clearedWorkspace.Revision, "base-cancel");
                Assert.Contains(pendingPrepare.Outputs, output => string.Equals(output.Path, bindingIdentity, StringComparison.OrdinalIgnoreCase));
                using (HttpRequestMessage cancelRequest = Request(
                           "api/authoring/native/cancel", "{}", clearedWorkspace.Revision, "base-cancel",
                           pendingPreview.PreviewToken))
                using (HttpResponseMessage cancelResponse = await client.SendAsync(cancelRequest))
                {
                    cancelResponse.EnsureSuccessStatusCode();
                    NativePublicationFinishResponse cancelled = JsonSerializer.Deserialize<NativePublicationFinishResponse>(
                        await cancelResponse.Content.ReadAsStringAsync(), UnifiedJson.CreateOptions())!;
                    Assert.Equal("cancelled", cancelled.State);
                }
                Assert.Equal(sourceBeforeCancel, await File.ReadAllBytesAsync(binding));
                Assert.Equal(primaryBeforeCancel, await File.ReadAllBytesAsync(primaryIdentity));
                Assert.Equal(bindingBeforeCancel, await File.ReadAllBytesAsync(bindingIdentity));

                AuthoringPreviewResponse undoPreview = await Preview(client, "api/authoring/preview/undo", "{}",
                    clearedWorkspace.Revision, "base-undo");
                AuthoringWorkspaceResponse undone = await PublishNativePreview(
                    client, undoPreview, clearedWorkspace.Revision, "base-undo");
                Assert.Equal("/Script/Engine.Pawn", undone.ScriptAssets.Assets.Single().ClassBinding!.BaseClassPath);
                Assert.Equal(assetId, undone.ScriptAssets.Assets.Single().Id);
                AuthoringPreviewResponse redoPreview = await Preview(client, "api/authoring/preview/redo", "{}",
                    undone.Revision, "base-redo");
                AuthoringWorkspaceResponse redone = await PublishNativePreview(
                    client, redoPreview, undone.Revision, "base-redo");
                Assert.Null(redone.ScriptAssets.Assets.Single().ClassBinding!.BaseClassPath);
                Assert.Equal(assetId, redone.ScriptAssets.Assets.Single().Id);

                // A sidecar changed outside the session must fail before a source
                // preview can become a candidate. Restore the exact bytes after
                // observing the protocol conflict.
                byte[] adoptedBindingSidecar = await File.ReadAllBytesAsync(bindingIdentity);
                await File.WriteAllBytesAsync(bindingIdentity, adoptedBindingSidecar.Concat([(byte)' ']).ToArray());
                try
                {
                    string currentBinding = await File.ReadAllTextAsync(binding);
                    using HttpRequestMessage externalConflict = Request(
                        "api/authoring/preview/source-text",
                        CSharpSourceTextPatchJson.Serialize(CSharpSourceTextPatchDocument.Create(
                            bindingLogicalPath, currentBinding, currentBinding + "\n")),
                        redone.Revision,
                        "external-sidecar-conflict");
                    using HttpResponseMessage conflict = await client.SendAsync(externalConflict);
                    Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
                    Assert.Contains("UAW1002", await conflict.Content.ReadAsStringAsync());
                }
                finally
                {
                    await File.WriteAllBytesAsync(bindingIdentity, adoptedBindingSidecar);
                }

                // Move the UnrealScriptClass attribute into a new partial source
                // without an identity sidecar. Refresh must publish that new
                // owner sidecar, keep the asset identity stable, and preserve
                // the original Marker/helper sources.
                string newOwner = Path.Combine(sourceDirectory, "Partial.Owner.cs");
                string newOwnerSource = "using Finare.Unreal; namespace PartialFixture; [UnrealScriptClass(UnrealScriptClassKind.Actor, GeneratedClassPath=\"/Game/Partial.Partial_C\")] public partial class Partial { }";
                string retainedBindingSource = "namespace PartialFixture; public partial class Partial { public int Marker { get; set; } = 1; }";
                await File.WriteAllTextAsync(binding, retainedBindingSource);
                await File.WriteAllTextAsync(newOwner, newOwnerSource);
                if (File.Exists(bindingIdentity)) File.Delete(bindingIdentity);
                string newOwnerIdentity = newOwner + ".usids";
                Assert.False(File.Exists(newOwnerIdentity));
                AuthoringWorkspaceResponse refreshed = await Refresh(client, redone);
                Assert.True(File.Exists(newOwnerIdentity));
                IdentitySidecarDocument refreshedBinding = IdentitySidecarJson.Deserialize(
                    await File.ReadAllTextAsync(newOwnerIdentity));
                string newOwnerLogical = ProjectAuthoringInput.Load(
                    project,
                    primary,
                    primaryIdentity,
                    new CSharpProjectLoadOptions(Restore: false, Build: false))
                    .SourceFiles.Single(pair => pair.Value == newOwner).Key;
                Assert.Equal(newOwnerLogical, refreshedBinding.SourcePath);
                ProjectAuthoringInput refreshedInput = ProjectAuthoringInput.Load(
                    project,
                    primary,
                    primaryIdentity,
                    new CSharpProjectLoadOptions(Restore: false, Build: false));
                Assert.Equal(newOwnerLogical, refreshedInput.SourceFiles.Single(pair => pair.Value == newOwner).Key);
                Assert.DoesNotContain(newOwnerLogical, refreshedInput.ReadOnlySources);
                Assert.Contains(refreshedInput.SourceFiles.Single(pair => pair.Value == helper).Key, refreshedInput.ReadOnlySources);
                Assert.Equal(assetId, refreshed.ScriptAssets.Assets.Single().Id);
                Assert.Equal(retainedBindingSource, await File.ReadAllTextAsync(binding));
                Assert.Equal(helperSource, await File.ReadAllTextAsync(helper));

                string editedOwnerSource = newOwnerSource.Replace(
                    "public partial class Partial { }",
                    "public partial class Partial { public int OwnerMarker { get; set; } = 3; }",
                    StringComparison.Ordinal);
                AuthoringWorkspaceResponse editedOwner = await Apply(
                    client,
                    refreshed,
                    "source-text",
                    CSharpSourceTextPatchJson.Serialize(CSharpSourceTextPatchDocument.Create(
                        newOwnerLogical, newOwnerSource, editedOwnerSource)),
                    "new-owner-edit");
                Assert.Equal(editedOwnerSource, await File.ReadAllTextAsync(newOwner));
                Assert.Equal(assetId, editedOwner.ScriptAssets.Assets.Single().Id);
            }
            finally
            {
                cancellation.Cancel();
                await running;
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectGraphAndSourceEditsPublishExecutableChunksWithStableIdentities()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Finare.ProjectAuthoring", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = """
            using Finare;
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
              <ItemGroup><Reference Include="Finare.Core"><HintPath>{System.Security.SecurityElement.Escape(typeof(VisualAttribute).Assembly.Location)}</HintPath></Reference></ItemGroup>
            </Project>
            """);
        try
        {
            ProjectAuthoringInput input = ProjectAuthoringInput.Load(project, primary);
            Assert.Contains("PROJECT_AUTHORING", input.Request.Settings.PreprocessorSymbols);
            Assert.Contains(input.Request.Sources, file => file.LogicalPath.EndsWith("GlobalUsings.g.cs", StringComparison.Ordinal));
            Assert.Contains("Helpers.cs", input.ReadOnlySources);
            Assert.False(input.Request.IncludeDefaultReferences);
            Assert.Throws<ArgumentException>(() => ProjectAuthoringInput.Load(project, project,
                options: new CSharpProjectLoadOptions(Restore: false, Build: false)));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            Action? afterRefreshRead = null;
            using var server = new CoreClrDebugHttpServer(input.Request, input.SourcePath, input.IdentityPath,
                directory, port, new AuthoringArtifactOutputs(
                    Path.ChangeExtension(chunk, ".asset.json"), Path.ChangeExtension(chunk, ".surface.json"),
                    Path.ChangeExtension(chunk, ".dll"), Path.ChangeExtension(chunk, ".pdb"),
                    Path.ChangeExtension(chunk, ".history.json"), CompiledChunkPath: chunk),
                input.SourceFiles, readOnlySources: input.ReadOnlySources, publishInitial: true,
                refreshProject: () =>
                {
                    ProjectAuthoringInput snapshot = ProjectAuthoringInput.Load(project, primary,
                        options: new CSharpProjectLoadOptions(Restore: false, Build: false));
                    afterRefreshRead?.Invoke();
                    return snapshot;
                },
                identityFiles: input.IdentityFiles);
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
                AuthoringSurfaceDocument emittedSurface = AuthoringSurfaceJson.Deserialize(
                    await File.ReadAllTextAsync(Path.Combine(emitted, "Playground.surface.json")));
                Assert.Equal(edited.ScriptAssets.SourceCompilationId, emittedSurface.SourceCompilationId);
                AuthoringAssetSurface emittedAsset = Assert.Single(emittedSurface.Assets);
                Assert.Equal(edited.ScriptAssets.Assets.Single().Id, emittedAsset.Id);
                Assert.All(
                    emittedSurface.DeclarationOwners,
                    owner => Assert.Equal(emittedAsset.Id, owner.OwnerAssetId));
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

                AuthoringWorkspaceResponse beforeRefresh = await Workspace(client);
                string diskSource = await File.ReadAllTextAsync(primary);
                await File.WriteAllTextAsync(primary, diskSource.Replace("Adjust(20)", "Adjust(40)", StringComparison.Ordinal));
                DateTime externalWrite = File.GetLastWriteTimeUtc(primary);
                AuthoringWorkspaceResponse refreshed = await Refresh(client, beforeRefresh);
                Assert.Equal(42, ExecuteChunk(chunk));
                Assert.Equal(externalWrite, File.GetLastWriteTimeUtc(primary));
                Assert.Contains("Adjust(40)", refreshed.SourceText);
                // External text uses conservative sidecar reconciliation, not the
                // explicit node pins carried by a semantic Graph operation.
                Assert.Equal(beforeRefresh.Graph.Functions.Single().Id, refreshed.Graph.Functions.Single().Id);
                Assert.Contains(refreshed.Graph.Functions.Single().Nodes, node =>
                    node.Kind == "literal" && node.Properties.GetValueOrDefault("value") == "40");
                AuthoringWorkspaceResponse noChange = await Refresh(client, refreshed);
                Assert.Equal(refreshed.Revision, noChange.Revision);
                Assert.Equal(refreshed.Generation.GenerationId, noChange.Generation.GenerationId);

                byte[] refreshedChunk = await File.ReadAllBytesAsync(chunk);
                string invalidSource = refreshed.SourceText + "\nnot valid C#";
                await File.WriteAllTextAsync(primary, invalidSource);
                using (HttpRequestMessage invalid = Request("api/authoring/refresh", "{}", refreshed.Revision, "invalid-refresh"))
                using (HttpResponseMessage failure = await client.SendAsync(invalid))
                {
                    Assert.Equal(HttpStatusCode.Conflict, failure.StatusCode);
                    Assert.Contains("UAW1018", await failure.Content.ReadAsStringAsync());
                }
                Assert.Equal(invalidSource, await File.ReadAllTextAsync(primary));
                Assert.Equal(refreshedChunk, await File.ReadAllBytesAsync(chunk));
                Assert.Equal(refreshed.Generation.GenerationId, (await Workspace(client)).Generation.GenerationId);
                await File.WriteAllTextAsync(primary, refreshed.SourceText);

                // A second IDE save during compilation cannot be replaced by a
                // candidate based on the first read, or retire the old program.
                string concurrentSource = refreshed.SourceText.Replace("Adjust(40)", "Adjust(51)", StringComparison.Ordinal);
                await File.WriteAllTextAsync(primary, refreshed.SourceText.Replace("Adjust(40)", "Adjust(50)", StringComparison.Ordinal));
                afterRefreshRead = () =>
                {
                    afterRefreshRead = null;
                    File.WriteAllText(primary, concurrentSource);
                };
                using (HttpRequestMessage racing = Request("api/authoring/refresh", "{}", refreshed.Revision, "racing-refresh"))
                using (HttpResponseMessage failure = await client.SendAsync(racing))
                {
                    Assert.Equal(HttpStatusCode.Conflict, failure.StatusCode);
                    Assert.Contains("Project inputs changed while refreshing", await failure.Content.ReadAsStringAsync());
                }
                Assert.Equal(concurrentSource, await File.ReadAllTextAsync(primary));
                Assert.Equal(refreshedChunk, await File.ReadAllBytesAsync(chunk));
                Assert.Equal(refreshed.Generation.GenerationId, (await Workspace(client)).Generation.GenerationId);
                await File.WriteAllTextAsync(primary, refreshed.SourceText);

                // Context changes must re-evaluate MSBuild, preserve old history, and
                // never retire the old loaded program if publication cannot complete.
                string originalProject = await File.ReadAllTextAsync(project);
                await File.WriteAllTextAsync(project, originalProject.Replace("PROJECT_AUTHORING", "OTHER_CONTEXT", StringComparison.Ordinal));
                string archiveDirectory = Path.ChangeExtension(chunk, ".history.json") + ".retired";
                await File.WriteAllTextAsync(archiveDirectory, "block directory creation");
                using (HttpRequestMessage blocked = Request("api/authoring/refresh", "{}", refreshed.Revision, "blocked-publication"))
                using (HttpResponseMessage failure = await client.SendAsync(blocked))
                    Assert.Equal(HttpStatusCode.Conflict, failure.StatusCode);
                Assert.Equal(refreshedChunk, await File.ReadAllBytesAsync(chunk));
                Assert.Equal(refreshed.Generation.GenerationId, (await Workspace(client)).Generation.GenerationId);
                File.Delete(archiveDirectory);
                AuthoringWorkspaceResponse reconfigured = await Refresh(client, refreshed);
                Assert.Equal(-1, ExecuteChunk(chunk));
                Assert.False(reconfigured.History.CanUndo);
                Assert.NotEqual(refreshed.ScriptAssets.SemanticConfigurationId, reconfigured.ScriptAssets.SemanticConfigurationId);
                string archivedPath = Assert.Single(Directory.GetFiles(archiveDirectory, "*.json"));
                CSharpAuthoringHistoryDocument archived = CSharpAuthoringHistoryJson.Deserialize(await File.ReadAllTextAsync(archivedPath));
                Assert.NotEmpty(archived.UndoHistory);
                Assert.Equal(refreshed.ScriptAssets.SemanticConfigurationId, archived.SemanticConfigurationId);
                using (HttpRequestMessage replay = Request("api/authoring/refresh", "{}", reconfigured.Revision, "graph-20"))
                using (HttpResponseMessage failure = await client.SendAsync(replay))
                    Assert.Contains("UAW1006", await failure.Content.ReadAsStringAsync());

                await File.WriteAllTextAsync(project, originalProject);
                await File.WriteAllTextAsync(Path.Combine(directory, "Extra.cs"), "namespace ProjectFixture; public class Extra { }");
                await File.WriteAllTextAsync(helper, helpers.Replace("+ 2", "+ 5", StringComparison.Ordinal));
                AuthoringWorkspaceResponse expanded = await Refresh(client, reconfigured);
                Assert.Equal(45, ExecuteChunk(chunk));
                Assert.True(expanded.Revision > reconfigured.Revision);
                ProjectAuthoringInput currentInput = ProjectAuthoringInput.Load(project, primary,
                    options: new CSharpProjectLoadOptions(Restore: false, Build: false));
                CSharpAuthoringWorkspace restored = CSharpAuthoringWorkspace.Restore(currentInput.Request,
                    CSharpAuthoringHistoryJson.Deserialize(await File.ReadAllTextAsync(Path.ChangeExtension(chunk, ".history.json"))));
                Assert.Equal(expanded.Revision, restored.Current.Revision);
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

    private static async Task<AuthoringWorkspaceResponse> Refresh(HttpClient client, AuthoringWorkspaceResponse baseline)
    {
        using HttpRequestMessage request = Request("api/authoring/refresh", "{}", baseline.Revision, Guid.NewGuid().ToString("N"));
        using HttpResponseMessage result = await client.SendAsync(request);
        string json = await result.Content.ReadAsStringAsync();
        Assert.True(result.IsSuccessStatusCode, json);
        return JsonSerializer.Deserialize<AuthoringWorkspaceResponse>(json, UnifiedJson.CreateOptions())!;
    }

    private static async Task<AuthoringPreviewResponse> PreviewSource(
        HttpClient client,
        AuthoringWorkspaceResponse baseline,
        string logicalPath,
        string before,
        string after,
        string transactionId) => await Preview(
            client,
            "api/authoring/preview/source-text",
            CSharpSourceTextPatchJson.Serialize(CSharpSourceTextPatchDocument.Create(logicalPath, before, after)),
            baseline.Revision,
            transactionId);

    private static async Task<AuthoringPreviewResponse> Preview(
        HttpClient client,
        string path,
        string content,
        long revision,
        string transactionId)
    {
        using HttpRequestMessage request = Request(path, content, revision, transactionId);
        using HttpResponseMessage response = await client.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, json);
        return JsonSerializer.Deserialize<AuthoringPreviewResponse>(json, UnifiedJson.CreateOptions())!;
    }

    private static async Task<NativePublicationPrepareResponse> PrepareNative(
        HttpClient client,
        AuthoringPreviewResponse preview,
        long revision,
        string transactionId)
    {
        using HttpRequestMessage request = Request(
            "api/authoring/native/prepare",
            JsonSerializer.Serialize(new NativePublicationPrepareRequest(preview.PreviewToken), UnifiedJson.CreateOptions(false)),
            revision,
            transactionId,
            preview.PreviewToken);
        using HttpResponseMessage response = await client.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, json);
        return JsonSerializer.Deserialize<NativePublicationPrepareResponse>(json, UnifiedJson.CreateOptions())!;
    }

    private static async Task<AuthoringWorkspaceResponse> PublishNativeSource(
        HttpClient client,
        AuthoringWorkspaceResponse baseline,
        string logicalPath,
        string before,
        string after,
        string transactionId)
    {
        AuthoringPreviewResponse preview = await PreviewSource(
            client, baseline, logicalPath, before, after, transactionId);
        return await PublishNativePreview(client, preview, baseline.Revision, transactionId);
    }

    private static async Task<AuthoringWorkspaceResponse> PublishNativePreview(
        HttpClient client,
        AuthoringPreviewResponse preview,
        long revision,
        string transactionId)
    {
        NativePublicationPrepareResponse prepared = await PrepareNative(client, preview, revision, transactionId);
        foreach (NativePublicationOutput output in prepared.Outputs)
            await File.WriteAllBytesAsync(output.Path, output.Content);

        using HttpRequestMessage request = Request(
            "api/authoring/native/finish",
            JsonSerializer.Serialize(new NativePublicationFinishRequest(preview.PreviewToken, true), UnifiedJson.CreateOptions(false)),
            revision,
            transactionId,
            preview.PreviewToken);
        using HttpResponseMessage response = await client.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, json);
        NativePublicationFinishResponse finished = JsonSerializer.Deserialize<NativePublicationFinishResponse>(
            json, UnifiedJson.CreateOptions())!;
        Assert.Equal("adopted", finished.State);
        return finished.Workspace!;
    }

    private static string AddBaseClassPath(string source, string baseClassPath) =>
        SetBaseClassPath(source, baseClassPath);

    private static string RemoveBaseClassPath(string source) =>
        SetBaseClassPath(source, null);

    private static string SetBaseClassPath(string source, string? baseClassPath)
    {
        const string marker = ", BaseClassPath=\"";
        int markerStart = source.IndexOf(marker, StringComparison.Ordinal);
        if (markerStart >= 0)
        {
            int quoteEnd = source.IndexOf('"', markerStart + marker.Length);
            source = source.Remove(markerStart, quoteEnd - markerStart + 1);
        }
        if (baseClassPath is null)
            return source;

        const string generated = "GeneratedClassPath=\"";
        int generatedStart = source.IndexOf(generated, StringComparison.Ordinal);
        int generatedQuoteEnd = source.IndexOf('"', generatedStart + generated.Length);
        return source.Insert(generatedQuoteEnd + 1, $", BaseClassPath=\"{baseClassPath}\"");
    }

    private static HttpRequestMessage Request(string path, string content, long revision, string id, string? preview = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
        message.Headers.Add("X-Finare-Baseline-Revision", revision.ToString());
        message.Headers.Add("X-Finare-Transaction-Id", id);
        message.Headers.Add("X-Finare-Transaction-Label", id);
        if (preview is not null) message.Headers.Add("X-Finare-Preview-Token", preview);
        return message;
    }

    private static async Task<AuthoringWorkspaceResponse> Apply(HttpClient client, AuthoringWorkspaceResponse baseline, string kind, string content, string id)
    {
        using HttpRequestMessage preview = Request($"api/authoring/preview/{kind}", content, baseline.Revision, id);
        using HttpResponseMessage previewResult = await client.SendAsync(preview);
        string previewJson = await previewResult.Content.ReadAsStringAsync();
        Assert.True(previewResult.IsSuccessStatusCode, previewJson);
        AuthoringPreviewResponse accepted = JsonSerializer.Deserialize<AuthoringPreviewResponse>(
            previewJson, UnifiedJson.CreateOptions())!;
        using HttpRequestMessage commit = Request($"api/authoring/{kind}", content, baseline.Revision, id, accepted.PreviewToken);
        using HttpResponseMessage result = await client.SendAsync(commit);
        string resultJson = await result.Content.ReadAsStringAsync();
        Assert.True(result.IsSuccessStatusCode, resultJson);
        return JsonSerializer.Deserialize<AuthoringWorkspaceResponse>(resultJson, UnifiedJson.CreateOptions())!;
    }
}
