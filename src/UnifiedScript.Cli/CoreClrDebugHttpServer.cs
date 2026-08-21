using System.Net;
using System.Text;
using System.Text.Json;
using UnifiedScript.Core;
using UnifiedScript.CSharp;
using UnifiedScript.Runtime;

namespace UnifiedScript.Cli;

internal sealed class CoreClrDebugHttpServer : IDisposable
{
    private const int MaximumRequestBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = UnifiedJson.CreateOptions();
    private readonly HttpListener listener = new();
    private readonly string contentRoot;
    private readonly DebugSessionController debug;
    private bool disposed;

    public CoreClrDebugHttpServer(
        CSharpCompilationRequest request,
        string contentRoot,
        int port)
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

        debug = new DebugSessionController(request);
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
        debug.Dispose();
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
            await WriteJsonAsync(context.Response, debug.Health, cancellationToken);
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
            "/api/debug/start" => await debug.StartAsync(
                await ReadJsonAsync<DebugStartRequest>(context.Request, cancellationToken),
                cancellationToken),
            "/api/debug/continue" => await debug.ResumeAsync(stepOver: false, cancellationToken),
            "/api/debug/step-over" => await debug.ResumeAsync(stepOver: true, cancellationToken),
            _ => throw new HttpProtocolException(HttpStatusCode.NotFound, "Unknown debug endpoint."),
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
        if (request.ContentLength64 > MaximumRequestBytes)
        {
            throw new HttpProtocolException(HttpStatusCode.RequestEntityTooLarge, "Debug request is too large.");
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
                throw new HttpProtocolException(HttpStatusCode.RequestEntityTooLarge, "Debug request is too large.");
            }

            limited.Write(buffer, 0, read);
        }

        T? value = JsonSerializer.Deserialize<T>(limited.ToArray(), JsonOptions);
        return value ?? throw new JsonException("The debug request body was empty.");
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
            HttpProtocolException protocol => protocol.Status,
            JsonException or ArgumentException or ExecutionInvocationException => HttpStatusCode.BadRequest,
            BreakpointProtocolException => HttpStatusCode.Conflict,
            _ => HttpStatusCode.InternalServerError,
        };
        string code = exception switch
        {
            ExecutionInvocationException invocation => invocation.Code,
            BreakpointProtocolException breakpoint => breakpoint.Code,
            _ => "UDB5000",
        };
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new DebugErrorResponse(new DebugError(code, exception.Message)),
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

    private sealed class DebugSessionController : IDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly CoreClrDebugLoopbackTransport transport = new();
        private readonly CoreClrDebugReloadResult reload;
        private readonly List<SemanticId> breakpointIds = [];
        private IReadOnlyList<SemanticBreakpointBinding> bindings = [];
        private CoreClrDebugExecution? execution;
        private CoreClrDebugPause? pause;

        public DebugSessionController(CSharpCompilationRequest request)
        {
            reload = transport.Reload(request);
            Health = new DebugHealthResponse(
                "unifiedscript.debug-health/v0.1",
                "ready",
                reload.Generation.GenerationId,
                reload.Generation.SourceCompilationId,
                reload.Generation.SemanticConfigurationId);
        }

        public DebugHealthResponse Health { get; }

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

        public void Dispose()
        {
            gate.Dispose();
            transport.Dispose();
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
    }

    private sealed class HttpProtocolException(HttpStatusCode status, string message)
        : Exception(message)
    {
        public HttpStatusCode Status { get; } = status;
    }
}

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

internal sealed record DebugErrorResponse(DebugError Error);

internal sealed record DebugError(string Code, string Message);
