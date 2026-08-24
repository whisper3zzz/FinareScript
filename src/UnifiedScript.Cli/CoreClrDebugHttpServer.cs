using System.Net;
using System.Text;
using System.Text.Json;
using UnifiedScript.Authoring;
using UnifiedScript.Core;
using UnifiedScript.CSharp;
using UnifiedScript.Graph;
using UnifiedScript.Runtime;

namespace UnifiedScript.Cli;

internal sealed class CoreClrDebugHttpServer : IDisposable
{
    private const int MaximumRequestBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = UnifiedJson.CreateOptions();
    private readonly HttpListener listener = new();
    private readonly string contentRoot;
    private readonly AuthoringSessionController session;
    private bool disposed;

    public CoreClrDebugHttpServer(
        CSharpCompilationRequest request,
        string sourcePath,
        string? identityPath,
        string contentRoot,
        int port,
        AuthoringArtifactOutputs? artifactOutputs = null,
        IReadOnlyDictionary<string, string>? sourceFiles = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        if (port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "The debug server port must be 1024-65535.");
        }

        this.contentRoot = Path.GetFullPath(contentRoot);
        if (!Directory.Exists(this.contentRoot))
        {
            throw new DirectoryNotFoundException(
                $"The visual workbench root does not exist: {this.contentRoot}");
        }

        session = new AuthoringSessionController(
            request,
            sourcePath,
            identityPath,
            artifactOutputs ?? new AuthoringArtifactOutputs(),
            sourceFiles);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        listener.Start();
        using CancellationTokenRegistration registration = cancellationToken.Register(listener.Stop);
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
                break;
            }

            _ = HandleAsync(context, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        listener.Close();
        session.Dispose();
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            AddCommonHeaders(context.Response);
            string path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.StartsWith("/api/debug/", StringComparison.Ordinal))
            {
                await HandleDebugApiAsync(context, path, cancellationToken);
                return;
            }

            if (path.StartsWith("/api/authoring/", StringComparison.Ordinal))
            {
                await HandleAuthoringApiAsync(context, path, cancellationToken);
                return;
            }

            await ServeStaticAsync(context, path, cancellationToken);
        }
        catch (Exception exception)
        {
            await WriteErrorAsync(context.Response, exception, cancellationToken);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task HandleDebugApiAsync(
        HttpListenerContext context,
        string path,
        CancellationToken cancellationToken)
    {
        if (context.Request.HttpMethod == "GET" && path == "/api/debug/health")
        {
            await WriteJsonAsync(context.Response, session.Health, cancellationToken);
            return;
        }

        if (context.Request.HttpMethod != "POST")
        {
            context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            context.Response.Headers[HttpResponseHeader.Allow] = "POST";
            return;
        }

        DebugSessionResponse response = path switch
        {
            "/api/debug/start" => await session.StartAsync(
                await ReadJsonAsync<DebugStartRequest>(context.Request, cancellationToken),
                cancellationToken),
            "/api/debug/continue" => await session.ResumeAsync(stepOver: false, cancellationToken),
            "/api/debug/step-over" => await session.ResumeAsync(stepOver: true, cancellationToken),
            _ => throw new HttpProtocolException(HttpStatusCode.NotFound, "Unknown debug endpoint."),
        };
        await WriteJsonAsync(context.Response, response, cancellationToken);
    }

    private async Task HandleAuthoringApiAsync(
        HttpListenerContext context,
        string path,
        CancellationToken cancellationToken)
    {
        if (context.Request.HttpMethod == "GET" && path == "/api/authoring/workspace")
        {
            await WriteJsonAsync(context.Response, session.Workspace, cancellationToken);
            return;
        }

        if (context.Request.HttpMethod != "POST")
        {
            context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            context.Response.Headers[HttpResponseHeader.Allow] = "GET, POST";
            return;
        }

        string json = await ReadJsonTextAsync(context.Request, cancellationToken);
        AuthoringWorkspaceResponse response = path switch
        {
            "/api/authoring/declaration-patch" => await session.ApplyAsync(
                DeclarationPatchJson.Deserialize(json),
                cancellationToken),
            "/api/authoring/semantic-patch" => await session.ApplyAsync(
                SemanticPatchJson.Deserialize(json),
                cancellationToken),
            "/api/authoring/semantic-patch-plan" => await session.ApplyAsync(
                SemanticPatchPlanJson.Deserialize(json),
                cancellationToken),
            _ => throw new HttpProtocolException(HttpStatusCode.NotFound, "Unknown authoring endpoint."),
        };
        await WriteJsonAsync(context.Response, response, cancellationToken);
    }

    private async Task ServeStaticAsync(
        HttpListenerContext context,
        string path,
        CancellationToken cancellationToken)
    {
        if (context.Request.HttpMethod is not ("GET" or "HEAD"))
        {
            context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            context.Response.Headers[HttpResponseHeader.Allow] = "GET, HEAD";
            return;
        }

        string relativePath = Uri.UnescapeDataString(path == "/" ? "index.html" : path.TrimStart('/'));
        string candidate = Path.GetFullPath(Path.Combine(contentRoot, relativePath));
        string rootPrefix = contentRoot.EndsWith(Path.DirectorySeparatorChar)
            ? contentRoot
            : contentRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.Ordinal) || !File.Exists(candidate))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        context.Response.ContentType = GetContentType(candidate);
        context.Response.Headers[HttpResponseHeader.CacheControl] = "no-store";
        context.Response.ContentLength64 = new FileInfo(candidate).Length;
        if (context.Request.HttpMethod == "GET")
        {
            await using FileStream stream = File.OpenRead(candidate);
            await stream.CopyToAsync(context.Response.OutputStream, cancellationToken);
        }
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpListenerRequest request,
        CancellationToken cancellationToken)
    {
        string json = await ReadJsonTextAsync(request, cancellationToken);
        T? value = JsonSerializer.Deserialize<T>(json, JsonOptions);
        return value ?? throw new JsonException("The request body was empty.");
    }

    private static async Task<string> ReadJsonTextAsync(
        HttpListenerRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength64 > MaximumRequestBytes)
        {
            throw new HttpProtocolException(HttpStatusCode.RequestEntityTooLarge, "Request body is too large.");
        }

        using var limited = new MemoryStream();
        byte[] buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = await request.InputStream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > MaximumRequestBytes)
            {
                throw new HttpProtocolException(HttpStatusCode.RequestEntityTooLarge, "Request body is too large.");
            }

            limited.Write(buffer, 0, read);
        }

        if (limited.Length == 0)
        {
            throw new JsonException("The request body was empty.");
        }

        return Encoding.UTF8.GetString(limited.ToArray());
    }

    private static async Task WriteJsonAsync<T>(
        HttpListenerResponse response,
        T value,
        CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body, cancellationToken);
    }

    private static async Task WriteErrorAsync(
        HttpListenerResponse response,
        Exception exception,
        CancellationToken cancellationToken)
    {
        HttpStatusCode status = exception switch
        {
            AuthoringProtocolException authoring => authoring.Status,
            HttpProtocolException protocol => protocol.Status,
            JsonException or ArgumentException or ExecutionInvocationException => HttpStatusCode.BadRequest,
            BreakpointProtocolException => HttpStatusCode.Conflict,
            _ => HttpStatusCode.InternalServerError,
        };
        string code = exception switch
        {
            AuthoringProtocolException authoring => authoring.Code,
            ExecutionInvocationException invocation => invocation.Code,
            BreakpointProtocolException breakpoint => breakpoint.Code,
            _ => "UDB5000",
        };
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new DebugErrorResponse(
                new DebugError(code, exception.Message),
                (exception as AuthoringProtocolException)?.Diagnostics),
            JsonOptions);
        response.StatusCode = (int)status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body, cancellationToken);
    }

    private static void AddCommonHeaders(HttpListenerResponse response)
    {
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'";
    }

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".cs" or ".md" or ".txt" => "text/plain; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        _ => "application/octet-stream",
    };

    private sealed class AuthoringSessionController : IDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly CoreClrDebugLoopbackTransport transport = new();
        private readonly CSharpAuthoringWorkspace authoring;
        private readonly string sourcePath;
        private readonly string sourceLogicalPath;
        private readonly IReadOnlyDictionary<string, string> sourceFiles;
        private readonly Dictionary<string, string> persistedSources = new(StringComparer.Ordinal);
        private readonly string? identityPath;
        private readonly AuthoringArtifactOutputs artifactOutputs;
        private readonly CSharpProgramEmitter emitter = new();
        private readonly List<SemanticId> breakpointIds = [];
        private IReadOnlyList<SemanticBreakpointBinding> bindings = [];
        private CoreClrDebugReloadResult reload;
        private CoreClrDebugExecution? execution;
        private CoreClrDebugPause? pause;
        private string? persistedIdentity;

        public AuthoringSessionController(
            CSharpCompilationRequest request,
            string sourcePath,
            string? identityPath,
            AuthoringArtifactOutputs artifactOutputs,
            IReadOnlyDictionary<string, string>? sourceFiles)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentNullException.ThrowIfNull(artifactOutputs);

            this.sourcePath = Path.GetFullPath(sourcePath);
            this.identityPath = identityPath is null ? null : Path.GetFullPath(identityPath);
            this.artifactOutputs = artifactOutputs.Normalize();
            string normalizedSourcePath = this.sourcePath.Replace('\\', '/');
            CSharpSourceDocument primarySource = request.Sources.SingleOrDefault(source =>
                    string.Equals(
                        Path.GetFullPath(source.LogicalPath).Replace('\\', '/'),
                        normalizedSourcePath,
                        StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(
                    $"The authoring source '{this.sourcePath}' is not present in the compilation request.",
                    nameof(sourcePath));
            sourceLogicalPath = primarySource.LogicalPath;
            var normalizedSourceFiles = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string logicalPath, string physicalPath) in sourceFiles ??
                new Dictionary<string, string> { [sourceLogicalPath] = this.sourcePath })
            {
                normalizedSourceFiles.Add(logicalPath.Replace('\\', '/'), Path.GetFullPath(physicalPath));
            }
            this.sourceFiles = normalizedSourceFiles;
            foreach (CSharpSourceDocument source in request.Sources)
            {
                if (!this.sourceFiles.TryGetValue(source.LogicalPath, out string? physicalPath))
                {
                    throw new ArgumentException(
                        $"Compilation source '{source.LogicalPath}' has no physical authoring path.",
                        nameof(sourceFiles));
                }
                string diskSource = File.ReadAllText(physicalPath);
                if (!string.Equals(source.Source, diskSource, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Compilation source '{source.LogicalPath}' does not match its file on disk.",
                        nameof(request));
                }
                persistedSources.Add(source.LogicalPath, diskSource);
            }
            persistedIdentity = this.identityPath is null
                ? null
                : File.Exists(this.identityPath)
                    ? File.ReadAllText(this.identityPath)
                    : null;
            authoring = new CSharpAuthoringWorkspace(request);
            reload = transport.Reload(authoring.Current.Request);
            Health = CreateHealth(reload.Generation);
            Workspace = CreateWorkspace(authoring.Current, reload.Generation);
        }

        public DebugHealthResponse Health { get; private set; }

        public AuthoringWorkspaceResponse Workspace { get; private set; }

        public async Task<DebugSessionResponse> StartAsync(
            DebugStartRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (execution is not null && pause is not null)
                {
                    throw new BreakpointProtocolException(
                        "UDB1004",
                        "The current debug execution is paused; continue it before starting again.");
                }

                foreach (SemanticId breakpointId in breakpointIds)
                {
                    _ = transport.RemoveBreakpoint(breakpointId);
                }

                breakpointIds.Clear();
                bindings = (request.BreakpointNodeIds ?? [])
                    .Distinct()
                    .Select(nodeId =>
                    {
                        SemanticId breakpointId = SemanticId.FromKey(
                            $"visual-workbench-breakpoint/v0.1|{nodeId}");
                        breakpointIds.Add(breakpointId);
                        return transport.SetBreakpoint(
                                new SemanticBreakpoint(breakpointId, nodeId))
                            .Bindings
                            .Single(binding => binding.BreakpointId == breakpointId);
                    })
                    .ToArray();
                if (bindings.Count == 0 || bindings.All(static binding =>
                        binding.State != BreakpointBindingState.Bound))
                {
                    throw new BreakpointProtocolException(
                        "UDB1007",
                        "At least one enabled visual breakpoint must bind to a statement probe.");
                }

                ExecutionArgument[] arguments = (request.Arguments ?? [])
                    .Select(static argument => new ExecutionArgument(argument.GetRawText()))
                    .ToArray();
                ScriptAssetReceiver? receiver = request.Receiver is null
                    ? null
                    : new ScriptAssetReceiver(
                        request.Receiver.AssetId,
                        (request.Receiver.StateOverrides ?? []).Select(static state =>
                            new ScriptStateOverride(state.StateId, state.Value.GetRawText())));
                execution = transport.Start(new ExecutionInvocation(
                    request.MethodId,
                    arguments,
                    receiver));
                pause = null;
                return await WaitForNextStateAsync(bindings, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<DebugSessionResponse> ResumeAsync(
            bool stepOver,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                CoreClrDebugExecution currentExecution = execution ??
                    throw new BreakpointProtocolException("UDB1005", "There is no active debug execution.");
                CoreClrDebugPause currentPause = pause ??
                    throw new BreakpointProtocolException("UDB1006", "The debug execution is not paused.");
                if (stepOver)
                {
                    transport.StepOver(reload.Generation.GenerationId, currentPause.PauseId);
                }
                else
                {
                    transport.Continue(reload.Generation.GenerationId, currentPause.PauseId);
                }

                pause = null;
                return await WaitForNextStateAsync(bindings, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        public Task<AuthoringWorkspaceResponse> ApplyAsync(
            DeclarationPatchDocument patch,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(patch);
            return ApplyAsync(() => authoring.Prepare(patch), cancellationToken);
        }

        public Task<AuthoringWorkspaceResponse> ApplyAsync(
            SemanticPatchDocument patch,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(patch);
            return ApplyAsync(() => authoring.Prepare(patch), cancellationToken);
        }

        public Task<AuthoringWorkspaceResponse> ApplyAsync(
            SemanticPatchPlanDocument plan,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(plan);
            return ApplyAsync(() => authoring.Prepare(plan), cancellationToken);
        }

        public void Dispose()
        {
            gate.Dispose();
            transport.Dispose();
        }

        private async Task<AuthoringWorkspaceResponse> ApplyAsync(
            Func<CSharpAuthoringMutation> prepare,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (execution is not null)
                {
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1001",
                        "Finish the active debug execution before changing the authoring workspace.");
                }

                EnsureFilesUnchanged();
                CSharpAuthoringMutation mutation = prepare();
                if (!mutation.Succeeded || mutation.Snapshot is null)
                {
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1003",
                        "The semantic patch was rejected; no source or runtime generation was changed.",
                        mutation.Diagnostics);
                }

                CSharpAuthoringSnapshot previous = authoring.Current;
                CoreClrDebugReloadResult nextReload = transport.Reload(mutation.Snapshot.Request);
                try
                {
                    Persist(mutation.Snapshot);
                }
                catch
                {
                    reload = transport.Reload(previous.Request);
                    Health = CreateHealth(reload.Generation);
                    Workspace = CreateWorkspace(previous, reload.Generation);
                    throw;
                }

                authoring.Commit(mutation);
                reload = nextReload;
                bindings = reload.Breakpoints.Bindings;
                Health = CreateHealth(reload.Generation);
                Workspace = CreateWorkspace(authoring.Current, reload.Generation);
                return Workspace;
            }
            finally
            {
                gate.Release();
            }
        }

        private void EnsureFilesUnchanged()
        {
            foreach ((string logicalPath, string physicalPath) in sourceFiles)
            {
                string diskSource = File.ReadAllText(physicalPath);
                if (!string.Equals(diskSource, persistedSources[logicalPath], StringComparison.Ordinal))
                {
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1002",
                        $"Source '{logicalPath}' changed outside the workbench; reload before saving.");
                }
            }
            string? diskIdentity = identityPath is null || !File.Exists(identityPath)
                ? null
                : File.ReadAllText(identityPath);
            if (!string.Equals(diskIdentity, persistedIdentity, StringComparison.Ordinal))
            {
                throw new AuthoringProtocolException(
                    HttpStatusCode.Conflict,
                    "UAW1002",
                    "The identity sidecar changed outside the workbench; reload before saving.");
            }
        }

        private void Persist(CSharpAuthoringSnapshot snapshot)
        {
            var outputs = snapshot.Request.Sources
                .Select(source => new AuthoringOutput(
                    sourceFiles[source.LogicalPath],
                    Encoding.UTF8.GetBytes(source.Source)))
                .ToList();
            string? nextIdentity = null;
            if (identityPath is not null)
            {
                IdentitySidecarDocument sidecar = snapshot.Analysis.IdentitySidecars.Single(item =>
                    string.Equals(item.SourcePath, sourceLogicalPath, StringComparison.Ordinal));
                nextIdentity = IdentitySidecarJson.Serialize(sidecar) + "\n";
                outputs.Add(new AuthoringOutput(
                    identityPath,
                    Encoding.UTF8.GetBytes(nextIdentity)));
            }

            ScriptAssetDocument scriptAssets = snapshot.Analysis.ScriptAssets!;
            AuthoringSurfaceDocument surface = new AuthoringSurfaceProjector().Project(
                scriptAssets,
                snapshot.Graph);
            if (artifactOutputs.DeclarationPath is not null)
            {
                outputs.Add(new AuthoringOutput(
                    artifactOutputs.DeclarationPath,
                    Encoding.UTF8.GetBytes(ScriptAssetJson.Serialize(scriptAssets) + "\n")));
            }

            if (artifactOutputs.SurfacePath is not null)
            {
                outputs.Add(new AuthoringOutput(
                    artifactOutputs.SurfacePath,
                    Encoding.UTF8.GetBytes(AuthoringSurfaceJson.Serialize(surface) + "\n")));
            }

            if (artifactOutputs.ManagedAssemblyPath is not null ||
                artifactOutputs.PortablePdbPath is not null)
            {
                CSharpProgramEmitResult emitted = emitter.Emit(snapshot.Request);
                if (!emitted.Succeeded || emitted.Image is null)
                {
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1004",
                        "The candidate managed assembly could not be emitted; no authoring artifact was changed.",
                        emitted.Diagnostics);
                }

                if (artifactOutputs.ManagedAssemblyPath is not null)
                {
                    outputs.Add(new AuthoringOutput(
                        artifactOutputs.ManagedAssemblyPath,
                        emitted.Image.PortableExecutable));
                }

                if (artifactOutputs.PortablePdbPath is not null)
                {
                    outputs.Add(new AuthoringOutput(
                        artifactOutputs.PortablePdbPath,
                        emitted.Image.PortablePdb));
                }
            }

            WriteTransactionally(outputs);
            foreach (CSharpSourceDocument source in snapshot.Request.Sources)
            {
                persistedSources[source.LogicalPath] = source.Source;
            }
            persistedIdentity = nextIdentity;
        }

        private static DebugHealthResponse CreateHealth(CoreClrProgramGeneration generation) => new(
            "unifiedscript.debug-health/v0.1",
            "ready",
            generation.GenerationId,
            generation.SourceCompilationId,
            generation.SemanticConfigurationId);

        private AuthoringWorkspaceResponse CreateWorkspace(
            CSharpAuthoringSnapshot snapshot,
            CoreClrProgramGeneration generation)
        {
            ScriptAssetDocument scriptAssets = snapshot.Analysis.ScriptAssets!;
            AuthoringSurfaceDocument surface = new AuthoringSurfaceProjector().Project(
                scriptAssets,
                snapshot.Graph);
            return new AuthoringWorkspaceResponse(
                "unifiedscript.authoring-workspace/v0.2",
                snapshot.Revision,
                sourceLogicalPath,
                snapshot.Request.Sources.Single(source =>
                    string.Equals(source.LogicalPath, sourceLogicalPath, StringComparison.Ordinal)).Source,
                scriptAssets,
                snapshot.Graph,
                surface,
                generation);
        }

        private static void WriteTransactionally(IReadOnlyList<AuthoringOutput> outputs)
        {
            string[] fullPaths = outputs.Select(output => Path.GetFullPath(output.Path)).ToArray();
            if (fullPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fullPaths.Length)
            {
                throw new ArgumentException("Authoring output paths must be distinct.", nameof(outputs));
            }

            var pending = new List<PendingAuthoringOutput>(outputs.Count);
            var moved = new List<PendingAuthoringOutput>(outputs.Count);
            bool committed = false;
            try
            {
                for (int index = 0; index < outputs.Count; index++)
                {
                    string fullPath = fullPaths[index];
                    string directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
                    Directory.CreateDirectory(directory);
                    string nonce = Guid.NewGuid().ToString("N");
                    string temporary = Path.Combine(
                        directory,
                        $".{Path.GetFileName(fullPath)}.{nonce}.tmp");
                    string backup = Path.Combine(
                        directory,
                        $".{Path.GetFileName(fullPath)}.{nonce}.bak");
                    File.WriteAllBytes(temporary, outputs[index].Content);
                    pending.Add(new PendingAuthoringOutput(fullPath, temporary, backup));
                }

                foreach (PendingAuthoringOutput output in pending)
                {
                    if (File.Exists(output.Path))
                    {
                        File.Move(output.Path, output.BackupPath);
                    }

                    File.Move(output.TemporaryPath, output.Path);
                    moved.Add(output);
                }

                committed = true;
            }
            catch
            {
                foreach (PendingAuthoringOutput output in moved.AsEnumerable().Reverse())
                {
                    TryDelete(output.Path);
                }

                foreach (PendingAuthoringOutput output in pending.AsEnumerable().Reverse())
                {
                    if (File.Exists(output.BackupPath))
                    {
                        File.Move(output.BackupPath, output.Path, overwrite: true);
                    }
                }

                throw;
            }
            finally
            {
                foreach (PendingAuthoringOutput output in pending)
                {
                    TryDelete(output.TemporaryPath);
                    if (committed)
                    {
                        TryDelete(output.BackupPath);
                    }
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        private async Task<DebugSessionResponse> WaitForNextStateAsync(
            IReadOnlyList<SemanticBreakpointBinding> bindings,
            CancellationToken cancellationToken)
        {
            CoreClrDebugExecution currentExecution = execution ??
                throw new BreakpointProtocolException("UDB1005", "There is no active debug execution.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            Task<CoreClrDebugPause> pauseTask = transport.WaitForPauseAsync(
                currentExecution.ExecutionId,
                timeout.Token);
            Task<ExecutionResult> completionTask = transport.WaitForCompletionAsync(
                currentExecution.ExecutionId,
                timeout.Token);
            Task completed = await Task.WhenAny(pauseTask, completionTask);
            timeout.Cancel();
            if (completed == pauseTask && pauseTask.IsCompletedSuccessfully)
            {
                pause = await pauseTask;
                IReadOnlyList<CoreClrDebugStackFrame> frames = transport.GetStackTrace(
                    reload.Generation.GenerationId,
                    pause.PauseId);
                IReadOnlyList<CoreClrDebugValue> locals = frames.Count > 0 && frames[0].HasLocals
                    ? transport.GetLocals(
                        reload.Generation.GenerationId,
                        pause.PauseId,
                        frames[0].FrameId)
                    : [];
                return DebugSessionResponse.Paused(
                    reload.Generation,
                    currentExecution.ExecutionId,
                    pause,
                    frames,
                    locals,
                    bindings);
            }

            ExecutionResult result = await completionTask;
            execution = null;
            pause = null;
            return DebugSessionResponse.Completed(
                reload.Generation,
                currentExecution.ExecutionId,
                result,
                bindings);
        }

        private sealed record AuthoringOutput(string Path, byte[] Content);

        private sealed record PendingAuthoringOutput(
            string Path,
            string TemporaryPath,
            string BackupPath);
    }

    private sealed class HttpProtocolException(HttpStatusCode status, string message)
        : Exception(message)
    {
        public HttpStatusCode Status { get; } = status;
    }

    private sealed class AuthoringProtocolException(
        HttpStatusCode status,
        string code,
        string message,
        IReadOnlyList<UnifiedDiagnostic>? diagnostics = null)
        : Exception(message)
    {
        public HttpStatusCode Status { get; } = status;

        public string Code { get; } = code;

        public IReadOnlyList<UnifiedDiagnostic> Diagnostics { get; } = diagnostics ?? [];
    }
}

internal sealed record AuthoringArtifactOutputs(
    string? DeclarationPath = null,
    string? SurfacePath = null,
    string? ManagedAssemblyPath = null,
    string? PortablePdbPath = null)
{
    public AuthoringArtifactOutputs Normalize() => new(
        NormalizePath(DeclarationPath),
        NormalizePath(SurfacePath),
        NormalizePath(ManagedAssemblyPath),
        NormalizePath(PortablePdbPath));

    private static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
}

internal sealed record AuthoringWorkspaceResponse(
    string SchemaVersion,
    long Revision,
    string SourcePath,
    string SourceText,
    ScriptAssetDocument ScriptAssets,
    GraphDocument Graph,
    AuthoringSurfaceDocument Surface,
    CoreClrProgramGeneration Generation);

internal sealed record DebugHealthResponse(
    string SchemaVersion,
    string Status,
    SemanticId GenerationId,
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId);

internal sealed record DebugStartRequest(
    SemanticId MethodId,
    IReadOnlyList<JsonElement>? Arguments,
    DebugReceiverRequest? Receiver,
    IReadOnlyList<SemanticId>? BreakpointNodeIds);

internal sealed record DebugReceiverRequest(
    SemanticId AssetId,
    IReadOnlyList<DebugStateOverrideRequest>? StateOverrides);

internal sealed record DebugStateOverrideRequest(SemanticId StateId, JsonElement Value);

internal sealed record DebugSessionResponse(
    string SchemaVersion,
    string State,
    CoreClrProgramGeneration Generation,
    long ExecutionId,
    CoreClrDebugPause? Pause,
    IReadOnlyList<CoreClrDebugStackFrame> Frames,
    IReadOnlyList<CoreClrDebugValue> Locals,
    ExecutionResult? Result,
    IReadOnlyList<SemanticBreakpointBinding> Breakpoints)
{
    public static DebugSessionResponse Paused(
        CoreClrProgramGeneration generation,
        long executionId,
        CoreClrDebugPause pause,
        IReadOnlyList<CoreClrDebugStackFrame> frames,
        IReadOnlyList<CoreClrDebugValue> locals,
        IReadOnlyList<SemanticBreakpointBinding> breakpoints) => new(
        "unifiedscript.debug-session/v0.1",
        "paused",
        generation,
        executionId,
        pause,
        frames,
        locals,
        null,
        breakpoints);

    public static DebugSessionResponse Completed(
        CoreClrProgramGeneration generation,
        long executionId,
        ExecutionResult result,
        IReadOnlyList<SemanticBreakpointBinding> breakpoints) => new(
        "unifiedscript.debug-session/v0.1",
        "completed",
        generation,
        executionId,
        null,
        [],
        [],
        result,
        breakpoints);
}

internal sealed record DebugErrorResponse(
    DebugError Error,
    IReadOnlyList<UnifiedDiagnostic>? Diagnostics = null);

internal sealed record DebugError(string Code, string Message);
