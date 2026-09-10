using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Finare.Authoring;
using Finare.Core;
using Finare.CSharp;
using Finare.Graph;
using Finare.Runtime;
using Finare.Unreal;

namespace Finare.Cli;

internal sealed class CoreClrDebugHttpServer : IDisposable
{
    private const int MaximumRequestBytes = 1024 * 1024;
    private const string BaselineRevisionHeader = "X-Finare-Baseline-Revision";
    private const string TransactionIdHeader = "X-Finare-Transaction-Id";
    private const string TransactionLabelHeader = "X-Finare-Transaction-Label";
    private const string GroupIdHeader = "X-Finare-Group-Id";
    private const string PreviewTokenHeader = "X-Finare-Preview-Token";
    private static readonly JsonSerializerOptions JsonOptions = UnifiedJson.CreateOptions();
    private readonly HttpListener listener = new();
    private readonly string contentRoot;
    private readonly AuthoringSessionController session;
    private readonly UnrealApiBrowserDocument? apiBrowser;
    private bool disposed;

    public CoreClrDebugHttpServer(
        CSharpCompilationRequest request,
        string sourcePath,
        string? identityPath,
        string contentRoot,
        int port,
        AuthoringArtifactOutputs? artifactOutputs = null,
        IReadOnlyDictionary<string, string>? sourceFiles = null,
        UnrealApiBrowserDocument? apiBrowser = null,
        IReadOnlySet<string>? readOnlySources = null,
        bool publishInitial = false,
        Func<ProjectAuthoringInput>? refreshProject = null)
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
            sourceFiles,
            readOnlySources,
            refreshProject);
        this.apiBrowser = apiBrowser;
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        if (publishInitial)
        {
            try
            {
                // A competing server must be rejected before publishing its outputs.
                listener.Start();
                session.PublishInitialArtifacts();
            }
            catch { listener.Close(); session.Dispose(); throw; }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!listener.IsListening) listener.Start();
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
        string path = context.Request.Url?.AbsolutePath ?? "/";
        try
        {
            AddCommonHeaders(context.Response);
            if (path.StartsWith("/api/unreal/", StringComparison.Ordinal))
            {
                await HandleUnrealApiAsync(context, path, cancellationToken);
                return;
            }
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
            if (path.StartsWith("/api/authoring/", StringComparison.Ordinal) &&
                exception is not AuthoringProtocolException &&
                exception is JsonException or ArgumentException or InvalidDataException)
            {
                exception = new AuthoringProtocolException(
                    HttpStatusCode.BadRequest,
                    "UAW1008",
                    exception.Message,
                    innerException: exception);
            }
            await WriteErrorAsync(context.Response, exception, cancellationToken);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task HandleUnrealApiAsync(
        HttpListenerContext context,
        string path,
        CancellationToken cancellationToken)
    {
        if (context.Request.HttpMethod == "POST" &&
            path == "/api/unreal/authoring-insight")
        {
            UnrealAuthoringInsightRequest request =
                await ReadJsonAsync<UnrealAuthoringInsightRequest>(
                    context.Request,
                    cancellationToken);
            AuthoringSurfaceDocument surface = session.Workspace.Surface;
            UnrealAuthoringInsightLocation[] locations = surface.Assets
                .SelectMany(static asset => asset.Functions)
                .SelectMany(function => function.Actions
                    .Where(static action => action.DebugTargetId is not null)
                    .Select(action => new UnrealAuthoringInsightLocation(
                        function.Id.Value,
                        action.DebugTargetId!.Value.Value,
                        action.Id.Value,
                        action.Title,
                        action.Summary,
                        action.Category.ToString(),
                        action.Span)))
                .GroupBy(static location => (location.MethodId, location.NodeId))
                .Where(static group => group.Count() == 1)
                .Select(static group => group.Single())
                .ToArray();
            UnrealAuthoringInsightSnapshot snapshot = UnrealAuthoringInsight.Create(
                surface.SourceCompilationId,
                surface.SemanticConfigurationId,
                locations,
                request,
                apiBrowser);
            await WriteJsonAsync(context.Response, snapshot, cancellationToken);
            return;
        }
        if (context.Request.HttpMethod != "GET")
        {
            context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            context.Response.Headers[HttpResponseHeader.Allow] =
                path == "/api/unreal/authoring-insight" ? "POST" : "GET";
            return;
        }
        if (path != "/api/unreal/browser")
        {
            throw new HttpProtocolException(HttpStatusCode.NotFound, "Unknown Unreal API endpoint.");
        }
        if (apiBrowser is null)
        {
            throw new HttpProtocolException(
                HttpStatusCode.NotFound,
                "No generated Unreal API browser was configured for this authoring session.");
        }

        await WriteJsonAsync(context.Response, apiBrowser, cancellationToken);
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

        if (path == "/api/debug/evaluate")
        {
            CoreClrDebugEvaluation evaluation = await session.EvaluateAsync(
                await ReadJsonAsync<DebugEvaluateRequest>(context.Request, cancellationToken),
                cancellationToken);
            await WriteJsonAsync(context.Response, evaluation, cancellationToken);
            return;
        }

        if (path == "/api/debug/publish-live-world")
        {
            DebugLiveWorldPublishResponse published = await session.PublishLiveWorldAsync(
                await ReadJsonAsync<DebugLiveWorldPublishRequest>(
                    context.Request,
                    cancellationToken),
                cancellationToken);
            await WriteJsonAsync(context.Response, published, cancellationToken);
            return;
        }

        DebugSessionResponse response = path switch
        {
            "/api/debug/start" => await session.StartAsync(
                await ReadJsonAsync<DebugStartRequest>(context.Request, cancellationToken),
                cancellationToken),
            "/api/debug/wait" => await session.WaitAsync(
                await ReadJsonAsync<DebugExecutionRequest>(context.Request, cancellationToken),
                cancellationToken),
            "/api/debug/pause" => await session.PauseAsync(
                await ReadJsonAsync<DebugExecutionRequest>(context.Request, cancellationToken),
                cancellationToken),
            "/api/debug/continue" => await session.ResumeAsync(
                await ReadJsonAsync<DebugResumeRequest>(context.Request, cancellationToken),
                stepKind: null,
                cancellationToken),
            "/api/debug/step-in" => await session.ResumeAsync(
                await ReadJsonAsync<DebugResumeRequest>(context.Request, cancellationToken),
                CoreClrDebugStepKind.Into,
                cancellationToken),
            "/api/debug/step-over" => await session.ResumeAsync(
                await ReadJsonAsync<DebugResumeRequest>(context.Request, cancellationToken),
                CoreClrDebugStepKind.Over,
                cancellationToken),
            "/api/debug/step-out" => await session.ResumeAsync(
                await ReadJsonAsync<DebugResumeRequest>(context.Request, cancellationToken),
                CoreClrDebugStepKind.Out,
                cancellationToken),
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

        AuthoringTransactionContext transaction = ReadAuthoringTransactionContext(
            context.Request,
            GetDefaultAuthoringDescription(path));
        if (path == "/api/authoring/refresh")
        {
            await WriteJsonAsync(context.Response, await session.RefreshAsync(transaction, cancellationToken), cancellationToken);
            return;
        }
        if (path == "/api/authoring/ai-semantic-proposal/draft")
        {
            string draftJson = await ReadJsonTextAsync(context.Request, cancellationToken);
            AiSemanticPatchProposalDocument proposal = AiSemanticPatchProposalDocument.Create(
                AiSemanticPatchProposalJson.DeserializeDraft(draftJson));
            await WriteJsonAsync(context.Response, proposal, cancellationToken);
            return;
        }
        if (path.StartsWith("/api/authoring/preview/", StringComparison.Ordinal))
        {
            if (path == "/api/authoring/preview/ai-semantic-proposal")
            {
                string proposalJson = await ReadJsonTextAsync(context.Request, cancellationToken);
                AiSemanticPatchProposalDocument proposal =
                    AiSemanticPatchProposalJson.Deserialize(proposalJson);
                AuthoringPreviewResponse proposalPreview = await session.PreviewAsync(
                    proposal.Plan,
                    transaction with { BoundPayloadId = proposal.ProposalId },
                    cancellationToken);
                await WriteJsonAsync(
                    context.Response,
                    new AuthoringAiSemanticProposalPreviewResponse(
                        "unifiedscript.ai-semantic-proposal-preview/v0.1",
                        proposal,
                        proposalPreview),
                    cancellationToken);
                return;
            }
            AuthoringPreviewResponse preview;
            if (path == "/api/authoring/preview/group/commit")
            {
                preview = await session.PreviewGroupCommitAsync(transaction, cancellationToken);
            }
            else if (path == "/api/authoring/preview/undo")
            {
                preview = await session.PreviewHistoryAsync(
                    CSharpAuthoringHistoryDirection.Undo,
                    transaction,
                    cancellationToken);
            }
            else if (path == "/api/authoring/preview/redo")
            {
                preview = await session.PreviewHistoryAsync(
                    CSharpAuthoringHistoryDirection.Redo,
                    transaction,
                    cancellationToken);
            }
            else
            {
                string previewJson = await ReadJsonTextAsync(context.Request, cancellationToken);
                preview = path switch
                {
                    "/api/authoring/preview/declaration-patch" => await session.PreviewAsync(
                        DeclarationPatchJson.Deserialize(previewJson),
                        transaction,
                        cancellationToken),
                    "/api/authoring/preview/semantic-patch" => await session.PreviewAsync(
                        SemanticPatchJson.Deserialize(previewJson),
                        transaction,
                        cancellationToken),
                    "/api/authoring/preview/semantic-patch-plan" => await session.PreviewAsync(
                        SemanticPatchPlanJson.Deserialize(previewJson),
                        transaction,
                        cancellationToken),
                    "/api/authoring/preview/source-text" => await session.PreviewAsync(
                        CSharpSourceTextPatchJson.Deserialize(previewJson),
                        transaction,
                        cancellationToken),
                    _ => throw new HttpProtocolException(
                        HttpStatusCode.NotFound,
                        "Unknown authoring preview endpoint."),
                };
            }
            await WriteJsonAsync(context.Response, preview, cancellationToken);
            return;
        }
        if (path == "/api/authoring/ai-semantic-proposal")
        {
            string proposalJson = await ReadJsonTextAsync(context.Request, cancellationToken);
            AiSemanticPatchProposalDocument proposal =
                AiSemanticPatchProposalJson.Deserialize(proposalJson);
            AuthoringWorkspaceResponse proposalWorkspace = await session.ApplyAsync(
                proposal.Plan,
                transaction with { BoundPayloadId = proposal.ProposalId },
                cancellationToken);
            await WriteJsonAsync(
                context.Response,
                new AuthoringAiSemanticProposalCommitResponse(
                    "unifiedscript.ai-semantic-proposal-commit/v0.1",
                    proposal.ProposalId,
                    proposalWorkspace),
                cancellationToken);
            return;
        }
        AuthoringWorkspaceResponse response;
        if (path == "/api/authoring/group/begin")
        {
            response = await session.BeginGroupAsync(transaction, cancellationToken);
        }
        else if (path == "/api/authoring/group/commit")
        {
            response = await session.CommitGroupAsync(transaction, cancellationToken);
        }
        else if (path == "/api/authoring/group/cancel")
        {
            response = await session.CancelGroupAsync(transaction, cancellationToken);
        }
        else if (path == "/api/authoring/undo")
        {
            response = await session.NavigateHistoryAsync(
                CSharpAuthoringHistoryDirection.Undo,
                transaction,
                cancellationToken);
        }
        else if (path == "/api/authoring/redo")
        {
            response = await session.NavigateHistoryAsync(
                CSharpAuthoringHistoryDirection.Redo,
                transaction,
                cancellationToken);
        }
        else
        {
            string json = await ReadJsonTextAsync(context.Request, cancellationToken);
            if (path == "/api/authoring/source-text" && transaction.PreviewToken is null)
            {
                throw new AuthoringProtocolException(
                    HttpStatusCode.Conflict,
                    "UAW1012",
                    "A C# source edit must commit the exact candidate accepted from its diff preview; no source, artifact, history, or runtime generation was changed.");
            }
            response = path switch
            {
                "/api/authoring/declaration-patch" => await session.ApplyAsync(
                    DeclarationPatchJson.Deserialize(json),
                    transaction,
                    cancellationToken),
                "/api/authoring/semantic-patch" => await session.ApplyAsync(
                    SemanticPatchJson.Deserialize(json),
                    transaction,
                    cancellationToken),
                "/api/authoring/semantic-patch-plan" => await session.ApplyAsync(
                    SemanticPatchPlanJson.Deserialize(json),
                    transaction,
                    cancellationToken),
                "/api/authoring/source-text" => await session.ApplyAsync(
                    CSharpSourceTextPatchJson.Deserialize(json),
                    transaction,
                    cancellationToken),
                _ => throw new HttpProtocolException(HttpStatusCode.NotFound, "Unknown authoring endpoint."),
            };
        }
        await WriteJsonAsync(context.Response, response, cancellationToken);
    }

    private static string GetDefaultAuthoringDescription(string path) => path switch
    {
        "/api/authoring/declaration-patch" or
        "/api/authoring/preview/declaration-patch" => "Declaration patch",
        "/api/authoring/semantic-patch" or
        "/api/authoring/preview/semantic-patch" => "Semantic patch",
        "/api/authoring/semantic-patch-plan" or
        "/api/authoring/preview/semantic-patch-plan" => "Semantic patch plan",
        "/api/authoring/source-text" or
        "/api/authoring/preview/source-text" => "C# source edit",
        "/api/authoring/ai-semantic-proposal/draft" => "Draft AI semantic proposal",
        "/api/authoring/ai-semantic-proposal" or
        "/api/authoring/preview/ai-semantic-proposal" => "AI semantic proposal",
        "/api/authoring/group/begin" => "Begin grouped authoring transaction",
        "/api/authoring/group/commit" or
        "/api/authoring/preview/group/commit" => "Commit grouped authoring transaction",
        "/api/authoring/group/cancel" => "Cancel grouped authoring transaction",
        "/api/authoring/undo" or "/api/authoring/preview/undo" => "Undo authoring transaction",
        "/api/authoring/redo" or "/api/authoring/preview/redo" => "Redo authoring transaction",
        _ => "Authoring transaction",
    };

    private static AuthoringTransactionContext ReadAuthoringTransactionContext(
        HttpListenerRequest request,
        string defaultDescription)
    {
        string? baselineText = request.Headers[BaselineRevisionHeader];
        long? baselineRevision = null;
        if (!string.IsNullOrWhiteSpace(baselineText))
        {
            if (!long.TryParse(baselineText, out long parsedBaseline) || parsedBaseline < 1)
            {
                throw new AuthoringProtocolException(
                    HttpStatusCode.BadRequest,
                    "UAW1008",
                    $"Header '{BaselineRevisionHeader}' must contain a positive revision number.");
            }
            baselineRevision = parsedBaseline;
        }

        string transactionId = request.Headers[TransactionIdHeader] ?? $"http-{Guid.NewGuid():N}";
        string description = request.Headers[TransactionLabelHeader] ?? defaultDescription;
        string? groupId = request.Headers[GroupIdHeader];
        string? previewToken = request.Headers[PreviewTokenHeader];
        if (string.IsNullOrWhiteSpace(transactionId) ||
            string.IsNullOrWhiteSpace(description) ||
            transactionId.Length > 128 ||
            description.Length > 256)
        {
            throw new AuthoringProtocolException(
                HttpStatusCode.BadRequest,
                "UAW1008",
                "Authoring transaction IDs and labels must be non-empty and at most 128 and 256 characters.");
        }
        if (groupId is not null && (string.IsNullOrWhiteSpace(groupId) || groupId.Length > 128))
        {
            throw new AuthoringProtocolException(
                HttpStatusCode.BadRequest,
                "UAW1008",
                $"Header '{GroupIdHeader}' must contain a non-empty group ID of at most 128 characters.");
        }
        if (previewToken is not null &&
            (previewToken.Length != 64 || previewToken.Any(static character => !Uri.IsHexDigit(character))))
        {
            throw new AuthoringProtocolException(
                HttpStatusCode.BadRequest,
                "UAW1008",
                $"Header '{PreviewTokenHeader}' must contain one SHA-256 preview token.");
        }

        return new AuthoringTransactionContext(
            baselineRevision,
            transactionId,
            description,
            groupId,
            previewToken?.ToLowerInvariant(),
            null);
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

    private async Task WriteErrorAsync(
        HttpListenerResponse response,
        Exception exception,
        CancellationToken cancellationToken)
    {
        HttpStatusCode status = exception switch
        {
            AuthoringProtocolException authoring => authoring.Status,
            HttpProtocolException protocol => protocol.Status,
            JsonException or ArgumentException or InvalidDataException or
                ExecutionInvocationException => HttpStatusCode.BadRequest,
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
        AuthoringProtocolException? authoringFailure = exception as AuthoringProtocolException;
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new DebugErrorResponse(
                new DebugError(code, exception.Message),
                authoringFailure?.Diagnostics,
                authoringFailure is null ? null : CreateQuickFixes(authoringFailure),
                authoringFailure is null
                    ? null
                    : session.CreateRollbackResponse(authoringFailure.RollbackState),
                authoringFailure?.ReloadTransaction),
            JsonOptions);
        response.StatusCode = (int)status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body, cancellationToken);
    }

    private static IReadOnlyList<AuthoringQuickFixResponse> CreateQuickFixes(
        AuthoringProtocolException exception)
    {
        var fixes = new List<AuthoringQuickFixResponse>();
        if (exception.Diagnostics.Any(static diagnostic =>
                diagnostic.Span.FilePath != "<unknown>"))
        {
            fixes.Add(new AuthoringQuickFixResponse(
                "open-first-diagnostic",
                "Open Source",
                "openFirstDiagnostic"));
        }
        if (exception.Code is "UAW1005" or "UAW1006" or "UAW1009" or "UAW1010" or "UAW1012")
        {
            fixes.Add(new AuthoringQuickFixResponse(
                "refresh-workspace",
                "Refresh Surface",
                "refreshWorkspace"));
        }
        return fixes;
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

    private sealed record AuthoringTransactionContext(
        long? BaselineRevision,
        string TransactionId,
        string Description,
        string? GroupId,
        string? PreviewToken,
        SemanticId? BoundPayloadId);

    private sealed class AuthoringSessionController : IDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly CoreClrDebugLoopbackTransport transport = new();
        private CSharpAuthoringWorkspace authoring;
        private readonly string sourcePath;
        private string sourceLogicalPath;
        private IReadOnlyDictionary<string, string> sourceFiles;
        private IReadOnlySet<string> readOnlySources;
        private readonly Func<ProjectAuthoringInput>? refreshProject;
        private readonly Dictionary<string, string> persistedSources = new(StringComparer.Ordinal);
        private readonly string? identityPath;
        private readonly AuthoringArtifactOutputs artifactOutputs;
        private readonly CSharpProgramEmitter emitter = new();
        private readonly CSharpGraphRoundTripInspector roundTripInspector = new();
        private readonly Dictionary<string, PreparedAuthoringPreview> preparedPreviews =
            new(StringComparer.Ordinal);
        private readonly List<SemanticId> breakpointIds = [];
        private IReadOnlyList<SemanticBreakpointBinding> bindings = [];
        private CoreClrDebugReloadResult reload;
        private CoreClrDebugExecution? execution;
        private CoreClrDebugPause? pause;
        private bool waitInProgress;
        private ActiveAuthoringGroup? activeGroup;
        private string? persistedIdentity;
        private string historyPersistenceState;
        private string? historyPersistenceMessage;

        public AuthoringSessionController(
            CSharpCompilationRequest request,
            string sourcePath,
            string? identityPath,
            AuthoringArtifactOutputs artifactOutputs,
            IReadOnlyDictionary<string, string>? sourceFiles,
            IReadOnlySet<string>? readOnlySources,
            Func<ProjectAuthoringInput>? refreshProject)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentNullException.ThrowIfNull(artifactOutputs);

            this.sourcePath = Path.GetFullPath(sourcePath);
            this.identityPath = identityPath is null ? null : Path.GetFullPath(identityPath);
            this.artifactOutputs = artifactOutputs.Normalize();
            this.refreshProject = refreshProject;
            this.readOnlySources = readOnlySources ?? new HashSet<string>(StringComparer.Ordinal);
            string normalizedSourcePath = this.sourcePath.Replace('\\', '/');
            CSharpSourceDocument primarySource = request.Sources.SingleOrDefault(source =>
                    string.Equals(
                        Path.GetFullPath(sourceFiles is not null && sourceFiles.TryGetValue(source.LogicalPath, out string? physical)
                            ? physical : source.LogicalPath).Replace('\\', '/'),
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
            if (this.artifactOutputs.HistoryPath is null)
            {
                authoring = new CSharpAuthoringWorkspace(request);
                historyPersistenceState = "disabled";
            }
            else if (!File.Exists(this.artifactOutputs.HistoryPath))
            {
                authoring = new CSharpAuthoringWorkspace(request);
                historyPersistenceState = "empty";
            }
            else
            {
                try
                {
                    CSharpAuthoringHistoryDocument history =
                        CSharpAuthoringHistoryJson.Deserialize(
                            File.ReadAllText(this.artifactOutputs.HistoryPath));
                    authoring = CSharpAuthoringWorkspace.Restore(request, history);
                    historyPersistenceState = "restored";
                }
                catch (Exception exception) when (
                    exception is JsonException or InvalidDataException or ArgumentException)
                {
                    authoring = new CSharpAuthoringWorkspace(request);
                    historyPersistenceState = "rejected";
                    historyPersistenceMessage = exception.Message;
                }
            }
            reload = transport.Reload(authoring.Current.Request);
            Health = CreateHealth(reload.Generation);
            Workspace = CreateWorkspace(authoring.Current, reload.Generation);
        }

        public DebugHealthResponse Health { get; private set; }

        public AuthoringWorkspaceResponse Workspace { get; private set; }

        public void PublishInitialArtifacts()
        {
            EnsureFilesUnchanged();
            Persist(authoring.Current, authoring.ExportHistory(), PrepareGraphLayout(authoring.Current, authoring.Current));
        }

        public async Task<AuthoringWorkspaceResponse> RefreshAsync(
            AuthoringTransactionContext transaction, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                ValidateTransaction(transaction);
                if (execution is not null)
                    throw new AuthoringProtocolException(HttpStatusCode.Conflict, "UAW1001",
                        "Finish the active debug execution before refreshing project inputs.");
                ProjectAuthoringInput input;
                CSharpAuthoringMutation mutation;
                try
                {
                    input = LoadRefreshInput();
                    mutation = authoring.PrepareRefresh(input.Request);
                }
                catch (Exception exception) when (exception is CSharpProjectLoadException or IOException or ArgumentException or JsonException)
                {
                    throw new AuthoringProtocolException(HttpStatusCode.Conflict, "UAW1018",
                        $"Project refresh failed: {exception.Message}. Existing authoring and disk edits were preserved.", innerException: exception);
                }
                if (!mutation.Succeeded || mutation.Snapshot is null)
                    throw new AuthoringProtocolException(HttpStatusCode.Conflict, "UAW1018",
                        "External sources did not compile; the old workspace and runtime remain active. Fix the disk sources and refresh again.", mutation.Diagnostics);

                CSharpAuthoringSnapshot candidate = mutation.Snapshot;
                string primaryLogical = input.SourceFiles.Single(pair =>
                    string.Equals(pair.Value, sourcePath, StringComparison.OrdinalIgnoreCase)).Key;
                bool samePaths = SamePaths(sourceFiles, input.SourceFiles) &&
                    authoring.Current.Request.ReferencePaths.SequenceEqual(input.Request.ReferencePaths, StringComparer.OrdinalIgnoreCase) &&
                    authoring.Current.Request.RuntimeReferencePaths.SequenceEqual(input.Request.RuntimeReferencePaths, StringComparer.OrdinalIgnoreCase);
                if (samePaths && candidate.Request.ConfigurationId == authoring.Current.Request.ConfigurationId &&
                    candidate.Analysis.ScriptAssets!.SourceCompilationId == authoring.Current.Analysis.ScriptAssets!.SourceCompilationId)
                {
                    // A normal build may rewrite an equivalent sidecar. Adopt its bytes,
                    // but do not publish or invent another runtime generation for a no-op.
                    persistedIdentity = ReadIdentityText();
                    return Workspace;
                }

                bool rebase = !samePaths || candidate.Request.ConfigurationId != authoring.Current.Request.ConfigurationId ||
                    candidate.Request.Sources.Any(source => source.LogicalPath != primaryLogical &&
                        (!persistedSources.TryGetValue(source.LogicalPath, out string? before) || before != source.Source));
                CSharpAuthoringWorkspace? rebased = rebase
                    ? authoring.Rebase(mutation, transaction.TransactionId, "Refresh project from disk") : null;
                CSharpAuthoringHistoryDocument history = rebased?.ExportHistory() ??
                    authoring.PreviewCommit(mutation, transaction.TransactionId, "Refresh source from disk");
                AuthoringOutput? archive = null;
                if (rebase && artifactOutputs.HistoryPath is not null &&
                    (authoring.History.CanUndo || authoring.History.CanRedo))
                {
                    byte[] previousHistory = Encoding.UTF8.GetBytes(CSharpAuthoringHistoryJson.Serialize(authoring.ExportHistory()) + "\n");
                    string digest = Convert.ToHexString(SHA256.HashData(previousHistory)).ToLowerInvariant();
                    archive = new AuthoringOutput(Path.Combine(artifactOutputs.HistoryPath + ".retired", digest + ".json"), previousHistory);
                }
                var imported = new ImportedProjectInput(input, primaryLogical,
                    input.Request.Sources.ToDictionary(source => source.LogicalPath, source => source.Source, StringComparer.Ordinal), archive);
                PreparedGraphLayout? layout = PrepareGraphLayout(authoring.Current, candidate);
                long previousGeneration = reload.Generation.Number;
                long started = Stopwatch.GetTimestamp();
                CoreClrDebugReloadResult refreshed;
                try
                {
                    refreshed = transport.Reload(candidate.Request, () =>
                    {
                        ValidateGraphLayoutBaseline(layout);
                        Persist(candidate, history, layout, imported);
                    });
                }
                catch (Exception exception)
                {
                    throw new AuthoringProtocolException(HttpStatusCode.Conflict, "UAW1018",
                        $"Project refresh was not published: {exception.Message}. The old loaded generation and external source edits were preserved.",
                        exception is ExecutionCompilationException compilation ? compilation.Diagnostics : [], innerException: exception);
                }
                if (rebased is not null) authoring = rebased;
                else authoring.Commit(mutation, transaction.TransactionId, "Refresh source from disk");
                preparedPreviews.Clear();
                reload = refreshed;
                bindings = reload.Breakpoints.Bindings;
                Health = CreateHealth(reload.Generation);
                historyPersistenceState = artifactOutputs.HistoryPath is null ? "disabled" : "persisted";
                historyPersistenceMessage = rebase
                    ? archive is null ? "Project context changed; Undo starts at the refreshed inputs."
                        : $"Project context changed; previous Undo history is retained at '{archive.Path}'."
                    : null;
                var receipt = new ReloadTransactionResponse("unifiedscript.reload-transaction/v0.1", transaction.TransactionId,
                    "authoring", "published", previousGeneration, refreshed.Generation.Number, refreshed.Generation.Number,
                    [CompleteReloadPhase("projectRefresh", started, "succeeded", "Read current inputs, compiled, published matching artifacts and adopted the new project snapshot.")], null);
                Workspace = CreateWorkspace(authoring.Current, reload.Generation, receipt);
                return Workspace;
            }
            finally { gate.Release(); }
        }

        private ProjectAuthoringInput LoadRefreshInput()
        {
            if (refreshProject is not null) return refreshProject();
            CSharpCompilationRequest previous = authoring.Current.Request;
            CSharpSourceDocument[] sources = previous.Sources.Select(source => new CSharpSourceDocument(
                source.LogicalPath, File.ReadAllText(sourceFiles[source.LogicalPath]),
                source.LogicalPath == sourceLogicalPath && identityPath is not null && File.Exists(identityPath)
                    ? IdentitySidecarJson.Deserialize(File.ReadAllText(identityPath)) : source.IdentitySidecar,
                source.IdentityPins)).ToArray();
            return new ProjectAuthoringInput(new CSharpCompilationRequest(previous.AssemblyName, sources,
                previous.ReferencePaths, previous.EffectCatalog, previous.Settings, previous.RuntimeReferencePaths,
                previous.IncludeDefaultReferences), sourcePath, identityPath ?? sourcePath + ".usids", sourceFiles, readOnlySources);
        }

        private string? ReadIdentityText() => identityPath is not null && File.Exists(identityPath) ? File.ReadAllText(identityPath) : null;

        private static bool SamePaths(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
            left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out string? path) &&
                string.Equals(pair.Value, path, StringComparison.OrdinalIgnoreCase));

        private static string InputIdentity(ProjectAuthoringInput input) => string.Join("|",
            input.Request.ConfigurationId.Value,
            string.Join("|", input.SourceFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)),
            string.Join("|", input.Request.Sources.Select(source => source.LogicalPath + ":" +
                CSharpSourceTextPatchJson.ComputeContentDigest(source.Source) + ":" +
                (source.IdentitySidecar is null ? "" : IdentitySidecarJson.Serialize(source.IdentitySidecar)))));

        private sealed record ImportedProjectInput(ProjectAuthoringInput Input, string PrimaryLogical,
            IReadOnlyDictionary<string, string> DiskSources, AuthoringOutput? ArchivedHistory);

        public AuthoringRollbackResponse CreateRollbackResponse(string state)
        {
            string message = state switch
            {
                "unchanged" =>
                    "The rejected transaction performed no source, artifact, history, or runtime-generation write.",
                "restored" =>
                    "The failed commit restored the previous source, artifacts, history, and runtime program as the reported generation.",
                "failed" =>
                    "The transaction failed and the previous runtime program could not be fully restored.",
                _ => "The transaction rollback state is unknown.",
            };
            return new AuthoringRollbackResponse(
                state,
                Workspace.Revision,
                reload.Generation.Number,
                message);
        }

        public async Task<AuthoringWorkspaceResponse> BeginGroupAsync(
            AuthoringTransactionContext transaction,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            await gate.WaitAsync(cancellationToken);
            try
            {
                EnsureAuthoringAvailable();
                if (activeGroup is not null)
                {
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1009",
                        $"Authoring group '{activeGroup.Id}' is already active; commit or cancel it first.");
                }
                if (transaction.GroupId is not null)
                {
                    throw new AuthoringProtocolException(
                        HttpStatusCode.BadRequest,
                        "UAW1008",
                        "Beginning an authoring group must not send an existing group ID.");
                }
                ValidateTransaction(transaction);
                EnsureFilesUnchanged();
                activeGroup = new ActiveAuthoringGroup(
                    transaction.TransactionId,
                    transaction.Description,
                    authoring.Current.Revision,
                    authoring.CreateDraft(),
                    0);
                preparedPreviews.Clear();
                Workspace = CreateWorkspace(activeGroup.Draft.Current, reload.Generation);
                return Workspace;
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<AuthoringWorkspaceResponse> CommitGroupAsync(
            AuthoringTransactionContext transaction,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            await gate.WaitAsync(cancellationToken);
            try
            {
                EnsureAuthoringAvailable();
                ActiveAuthoringGroup group = RequireActiveGroup(transaction);
                EnsureFilesUnchanged();
                if (group.StagedOperationCount == 0)
                {
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1011",
                        "A grouped authoring transaction must stage at least one accepted edit before commit.");
                }

                CSharpAuthoringMutation mutation = PrepareMutation(
                    authoring,
                    _ => authoring.PrepareDraftCommit(group.Draft),
                    transaction,
                    publishesRuntime: true,
                    out PreparedGraphLayout? graphLayout);
                CSharpAuthoringHistoryDocument persistedHistory = authoring.PreviewCommit(
                    mutation,
                    group.Id,
                    group.Description);
                AuthoringWorkspaceResponse committed = CommitPreparedSnapshot(
                    mutation.Snapshot!,
                    () => authoring.Commit(mutation, group.Id, group.Description),
                    persistedHistory,
                    group.Id,
                    graphLayout);
                activeGroup = null;
                preparedPreviews.Clear();
                Workspace = CreateWorkspace(
                    authoring.Current,
                    reload.Generation,
                    committed.ReloadTransaction);
                return Workspace;
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<AuthoringWorkspaceResponse> CancelGroupAsync(
            AuthoringTransactionContext transaction,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            await gate.WaitAsync(cancellationToken);
            try
            {
                _ = RequireActiveGroup(transaction);
                activeGroup = null;
                preparedPreviews.Clear();
                Workspace = CreateWorkspace(authoring.Current, reload.Generation);
                return Workspace;
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<DebugSessionResponse> StartAsync(
            DebugStartRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (activeGroup is not null)
                {
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1009",
                        "Commit or cancel the active authoring group before starting debug execution.");
                }
                if (execution is not null)
                {
                    throw new BreakpointProtocolException(
                        "UDB1004",
                        "The current debug execution is running or paused; finish it before starting again.");
                }
                if (request.ExpectedSourceCompilationId is { } expectedCompilation &&
                    expectedCompilation != reload.Generation.SourceCompilationId)
                {
                    throw new BreakpointProtocolException(
                        "UDB1002",
                        $"The loaded source compilation is '{reload.Generation.SourceCompilationId}', not '{expectedCompilation}'.");
                }
                if (request.ExpectedSemanticConfigurationId is { } expectedConfiguration &&
                    expectedConfiguration != reload.Generation.SemanticConfigurationId)
                {
                    throw new BreakpointProtocolException(
                        "UDB1002",
                        $"The loaded semantic configuration is '{reload.Generation.SemanticConfigurationId}', not '{expectedConfiguration}'.");
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
                return DebugSessionResponse.Running(
                    reload.Generation,
                    execution.ExecutionId,
                    bindings);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<DebugSessionResponse> WaitAsync(
            DebugExecutionRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            CoreClrDebugExecution currentExecution;
            IReadOnlyList<SemanticBreakpointBinding> currentBindings;
            await gate.WaitAsync(cancellationToken);
            try
            {
                currentExecution = RequireExecution(request.GenerationId, request.ExecutionId);
                if (waitInProgress)
                {
                    throw new BreakpointProtocolException(
                        "UDB1011",
                        "A wait request is already active for the current debug execution.");
                }

                waitInProgress = true;
                currentBindings = bindings.ToArray();
            }
            finally
            {
                gate.Release();
            }

            DebugSessionResponse? response = null;
            try
            {
                response = await WaitForNextStateAsync(
                    currentExecution,
                    currentBindings,
                    cancellationToken);
                return response;
            }
            finally
            {
                await gate.WaitAsync(CancellationToken.None);
                try
                {
                    waitInProgress = false;
                    if (response is not null && execution?.ExecutionId == currentExecution.ExecutionId)
                    {
                        if (response.State == "paused")
                        {
                            pause = response.Pause;
                        }
                        else if (response.State == "completed")
                        {
                            execution = null;
                            pause = null;
                        }
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
        }

        public async Task<DebugSessionResponse> PauseAsync(
            DebugExecutionRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            await gate.WaitAsync(cancellationToken);
            try
            {
                CoreClrDebugExecution currentExecution = RequireExecution(
                    request.GenerationId,
                    request.ExecutionId);
                if (pause is not null)
                {
                    throw new BreakpointProtocolException(
                        "UDB1006",
                        "The debug execution is already paused.");
                }

                transport.Pause(reload.Generation.GenerationId, currentExecution.ExecutionId);
                return DebugSessionResponse.Running(
                    reload.Generation,
                    currentExecution.ExecutionId,
                    bindings);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<DebugSessionResponse> ResumeAsync(
            DebugResumeRequest request,
            CoreClrDebugStepKind? stepKind,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            await gate.WaitAsync(cancellationToken);
            try
            {
                CoreClrDebugExecution currentExecution = RequireExecution(
                    request.GenerationId,
                    request.ExecutionId);
                CoreClrDebugPause currentPause = RequirePause(request.PauseId);
                switch (stepKind)
                {
                    case CoreClrDebugStepKind.Into:
                        transport.StepInto(reload.Generation.GenerationId, currentPause.PauseId);
                        break;
                    case CoreClrDebugStepKind.Over:
                        transport.StepOver(reload.Generation.GenerationId, currentPause.PauseId);
                        break;
                    case CoreClrDebugStepKind.Out:
                        transport.StepOut(reload.Generation.GenerationId, currentPause.PauseId);
                        break;
                    case null:
                        transport.Continue(reload.Generation.GenerationId, currentPause.PauseId);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(stepKind));
                }

                pause = null;
                return DebugSessionResponse.Running(
                    reload.Generation,
                    currentExecution.ExecutionId,
                    bindings);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<CoreClrDebugEvaluation> EvaluateAsync(
            DebugEvaluateRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            await gate.WaitAsync(cancellationToken);
            try
            {
                _ = RequireExecution(request.GenerationId, request.ExecutionId);
                CoreClrDebugPause currentPause = RequirePause(request.PauseId);
                return transport.Evaluate(
                    reload.Generation.GenerationId,
                    currentPause.PauseId,
                    request.FrameId,
                    request.Expression);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<DebugLiveWorldPublishResponse> PublishLiveWorldAsync(
            DebugLiveWorldPublishRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (activeGroup is not null)
                {
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1009",
                        "Commit or cancel the active authoring group before publishing a live World debug image.");
                }
                if (execution is not null)
                {
                    throw new BreakpointProtocolException(
                        "UDB1004",
                        "Finish the loopback execution before publishing a live World debug image.");
                }
                if (request.ExpectedSourceCompilationId != reload.Generation.SourceCompilationId ||
                    request.ExpectedSemanticConfigurationId !=
                    reload.Generation.SemanticConfigurationId)
                {
                    throw new BreakpointProtocolException(
                        "UDB1002",
                        "The live World publish request belongs to stale source or semantic configuration.");
                }
                if (artifactOutputs.ManagedAssemblyPath is null ||
                    artifactOutputs.PortablePdbPath is null)
                {
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1004",
                        "Live World debugging requires both --managed-out and --pdb-out.");
                }

                EnsureFilesUnchanged();
                string transactionId = $"live-world-{Guid.NewGuid():N}";
                var phases = new List<ReloadTransactionPhaseResponse>();
                long phaseStart = Stopwatch.GetTimestamp();
                CSharpProgramEmitResult emitted;
                try
                {
                    emitted = emitter.Emit(
                        authoring.Current.Request,
                        new CSharpDebugInstrumentation(
                            typeof(CoreClrDebugProbe).Assembly.Location));
                }
                catch (Exception exception)
                {
                    phases.Add(CompleteReloadPhase(
                        "compile",
                        phaseStart,
                        "failed",
                        exception.Message));
                    phases.Add(new ReloadTransactionPhaseResponse(
                        "rollback",
                        "unchanged",
                        0,
                        "The published live World PE/PDB remained unchanged."));
                    var failedTransaction = new ReloadTransactionResponse(
                        "unifiedscript.reload-transaction/v0.1",
                        transactionId,
                        "liveWorldDebug",
                        "rolledBack",
                        reload.Generation.Number,
                        reload.Generation.Number,
                        reload.Generation.Number,
                        phases,
                        exception.Message);
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1004",
                        "The instrumented live World assembly could not be emitted; the active artifact was not changed.",
                        reloadTransaction: failedTransaction,
                        innerException: exception);
                }
                if (!emitted.Succeeded || emitted.Image is null)
                {
                    const string message =
                        "The instrumented live World assembly could not be emitted; the active artifact was not changed.";
                    phases.Add(CompleteReloadPhase(
                        "compile",
                        phaseStart,
                        "failed",
                        $"The compiler returned {emitted.Diagnostics.Count} diagnostic(s)."));
                    phases.Add(new ReloadTransactionPhaseResponse(
                        "rollback",
                        "unchanged",
                        0,
                        "The published live World PE/PDB remained unchanged."));
                    var failedTransaction = new ReloadTransactionResponse(
                        "unifiedscript.reload-transaction/v0.1",
                        transactionId,
                        "liveWorldDebug",
                        "rolledBack",
                        reload.Generation.Number,
                        reload.Generation.Number,
                        reload.Generation.Number,
                        phases,
                        message);
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1004",
                        message,
                        emitted.Diagnostics,
                        reloadTransaction: failedTransaction);
                }
                phases.Add(CompleteReloadPhase(
                    "compile",
                    phaseStart,
                    "succeeded",
                    "Emitted the instrumented live World PE and portable PDB in memory."));
                phaseStart = Stopwatch.GetTimestamp();
                try
                {
                    WriteTransactionally(
                    [
                        new AuthoringOutput(
                            artifactOutputs.ManagedAssemblyPath,
                            emitted.Image.PortableExecutable),
                        new AuthoringOutput(
                            artifactOutputs.PortablePdbPath,
                            emitted.Image.PortablePdb),
                    ]);
                }
                catch (Exception exception)
                {
                    phases.Add(CompleteReloadPhase(
                        "artifactPublication",
                        phaseStart,
                        "failed",
                        exception.Message));
                    phases.Add(new ReloadTransactionPhaseResponse(
                        "rollback",
                        "restored",
                        0,
                        "The transactional writer restored the previous live World PE/PDB."));
                    var failedTransaction = new ReloadTransactionResponse(
                        "unifiedscript.reload-transaction/v0.1",
                        transactionId,
                        "liveWorldDebug",
                        "rolledBack",
                        reload.Generation.Number,
                        reload.Generation.Number,
                        reload.Generation.Number,
                        phases,
                        exception.Message);
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1013",
                        "The live World debug artifact publication failed; the previous PE/PDB was restored.",
                        rollbackState: "restored",
                        reloadTransaction: failedTransaction,
                        innerException: exception);
                }
                phases.Add(CompleteReloadPhase(
                    "artifactPublication",
                    phaseStart,
                    "succeeded",
                    "Atomically replaced the live World PE and portable PDB."));
                var transaction = new ReloadTransactionResponse(
                    "unifiedscript.reload-transaction/v0.1",
                    transactionId,
                    "liveWorldDebug",
                    "published",
                    reload.Generation.Number,
                    reload.Generation.Number,
                    reload.Generation.Number,
                    phases,
                    null);
                return new DebugLiveWorldPublishResponse(
                    "unifiedscript.live-world-debug-publish/v0.1",
                    reload.Generation,
                    artifactOutputs.ManagedAssemblyPath,
                    artifactOutputs.PortablePdbPath,
                    transaction);
            }
            finally
            {
                gate.Release();
            }
        }

        public Task<AuthoringWorkspaceResponse> ApplyAsync(
            DeclarationPatchDocument patch,
            AuthoringTransactionContext transaction,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(patch);
            return ApplyAsync(workspace => workspace.Prepare(patch), transaction, cancellationToken);
        }

        public Task<AuthoringWorkspaceResponse> ApplyAsync(
            SemanticPatchDocument patch,
            AuthoringTransactionContext transaction,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(patch);
            return ApplyAsync(workspace => workspace.Prepare(patch), transaction, cancellationToken);
        }

        public Task<AuthoringWorkspaceResponse> ApplyAsync(
            SemanticPatchPlanDocument plan,
            AuthoringTransactionContext transaction,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(plan);
            return ApplyAsync(workspace => workspace.Prepare(plan), transaction, cancellationToken);
        }

        public Task<AuthoringWorkspaceResponse> ApplyAsync(
            CSharpSourceTextPatchDocument patch,
            AuthoringTransactionContext transaction,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(patch);
            return ApplyAsync(workspace => workspace.Prepare(patch), transaction, cancellationToken);
        }

        public Task<AuthoringPreviewResponse> PreviewAsync(
            DeclarationPatchDocument patch,
            AuthoringTransactionContext transaction,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(patch);
            return PreviewAsync(workspace => workspace.Prepare(patch), transaction, cancellationToken);
        }

        public Task<AuthoringPreviewResponse> PreviewAsync(
            SemanticPatchDocument patch,
            AuthoringTransactionContext transaction,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(patch);
            return PreviewAsync(workspace => workspace.Prepare(patch), transaction, cancellationToken);
        }

        public Task<AuthoringPreviewResponse> PreviewAsync(
            SemanticPatchPlanDocument plan,
            AuthoringTransactionContext transaction,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(plan);
            return PreviewAsync(workspace => workspace.Prepare(plan), transaction, cancellationToken);
        }

        public Task<AuthoringPreviewResponse> PreviewAsync(
            CSharpSourceTextPatchDocument patch,
            AuthoringTransactionContext transaction,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(patch);
            return PreviewAsync(workspace => workspace.Prepare(patch), transaction, cancellationToken);
        }

        public async Task<AuthoringPreviewResponse> PreviewGroupCommitAsync(
            AuthoringTransactionContext transaction,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            await gate.WaitAsync(cancellationToken);
            try
            {
                EnsureAuthoringAvailable();
                ActiveAuthoringGroup group = RequireActiveGroup(transaction);
                EnsureFilesUnchanged();
                if (group.StagedOperationCount == 0)
                {
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1011",
                        "A grouped authoring transaction must stage at least one accepted edit before commit.");
                }

                CSharpAuthoringMutation mutation = authoring.PrepareDraftCommit(group.Draft);
                return CreatePreview(
                    authoring.Current,
                    mutation.Snapshot!,
                    mutation.Diagnostics,
                    transaction,
                    publishesRuntime: true);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<AuthoringPreviewResponse> PreviewHistoryAsync(
            CSharpAuthoringHistoryDirection direction,
            AuthoringTransactionContext transaction,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            await gate.WaitAsync(cancellationToken);
            try
            {
                EnsureAuthoringAvailable();
                ValidateTransaction(transaction);
                EnsureFilesUnchanged();
                CSharpAuthoringHistoryState history = authoring.History;
                if ((direction == CSharpAuthoringHistoryDirection.Undo && !history.CanUndo) ||
                    (direction == CSharpAuthoringHistoryDirection.Redo && !history.CanRedo))
                {
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1007",
                        direction == CSharpAuthoringHistoryDirection.Undo
                            ? "There is no committed authoring transaction to undo."
                            : "There is no undone authoring transaction to redo.");
                }

                CSharpAuthoringHistoryMutation mutation =
                    direction == CSharpAuthoringHistoryDirection.Undo
                        ? authoring.PrepareUndo()
                        : authoring.PrepareRedo();
                return CreatePreview(
                    authoring.Current,
                    mutation.Snapshot,
                    [],
                    transaction,
                    publishesRuntime: true);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<AuthoringWorkspaceResponse> NavigateHistoryAsync(
            CSharpAuthoringHistoryDirection direction,
            AuthoringTransactionContext transaction,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            await gate.WaitAsync(cancellationToken);
            try
            {
                EnsureAuthoringAvailable();
                ValidateTransaction(transaction);
                EnsureFilesUnchanged();
                CSharpAuthoringHistoryState history = authoring.History;
                if ((direction == CSharpAuthoringHistoryDirection.Undo && !history.CanUndo) ||
                    (direction == CSharpAuthoringHistoryDirection.Redo && !history.CanRedo))
                {
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1007",
                        direction == CSharpAuthoringHistoryDirection.Undo
                            ? "There is no committed authoring transaction to undo."
                            : "There is no undone authoring transaction to redo.");
                }

                CSharpAuthoringHistoryMutation mutation =
                    direction == CSharpAuthoringHistoryDirection.Undo
                        ? authoring.PrepareUndo()
                        : authoring.PrepareRedo();
                mutation = mutation with
                {
                    Snapshot = ResolvePreparedSnapshot(
                        authoring.Current,
                        mutation.Snapshot,
                        transaction,
                        publishesRuntime: true,
                        out PreparedGraphLayout? graphLayout),
                };
                CSharpAuthoringHistoryDocument persistedHistory = authoring.PreviewCommit(
                    mutation,
                    transaction.TransactionId);
                return CommitPreparedSnapshot(
                    mutation.Snapshot,
                    () => authoring.Commit(mutation, transaction.TransactionId),
                    persistedHistory,
                    transaction.TransactionId,
                    graphLayout);
            }
            finally
            {
                gate.Release();
            }
        }

        public void Dispose()
        {
            // Request handlers are intentionally detached by the listener. A long-poll
            // handler can still be unwinding through its gate-protected cleanup after
            // the listener has closed, so disposing the semaphore here would race that
            // cleanup. SemaphoreSlim owns no external handle until WaitHandle is used.
            transport.Dispose();
        }

        private async Task<AuthoringPreviewResponse> PreviewAsync(
            Func<CSharpAuthoringWorkspace, CSharpAuthoringMutation> prepare,
            AuthoringTransactionContext transaction,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            await gate.WaitAsync(cancellationToken);
            try
            {
                EnsureAuthoringAvailable();
                if (transaction.GroupId is not null)
                {
                    ActiveAuthoringGroup group = RequireActiveGroup(
                        transaction,
                        requireGroupOperationId: false);
                    EnsureFilesUnchanged();
                    if (string.Equals(transaction.TransactionId, group.Id, StringComparison.Ordinal))
                    {
                        throw new AuthoringProtocolException(
                            HttpStatusCode.BadRequest,
                            "UAW1008",
                            "A staged operation ID must differ from its grouped transaction ID.");
                    }
                    if (group.Draft.ContainsOperationId(transaction.TransactionId))
                    {
                        throw new AuthoringProtocolException(
                            HttpStatusCode.Conflict,
                            "UAW1006",
                            $"Staged authoring operation ID '{transaction.TransactionId}' was already accepted.");
                    }

                    CSharpAuthoringMutation stagedMutation = prepare(group.Draft);
                    if (!stagedMutation.Succeeded || stagedMutation.Snapshot is null)
                    {
                        throw new AuthoringProtocolException(
                            HttpStatusCode.Conflict,
                            "UAW1003",
                            "The staged semantic patch was rejected; the grouped draft was not changed.",
                            stagedMutation.Diagnostics);
                    }
                    return CreatePreview(
                        group.Draft.Current,
                        stagedMutation.Snapshot,
                        stagedMutation.Diagnostics,
                        transaction,
                        publishesRuntime: false);
                }

                ValidateTransaction(transaction);
                EnsureFilesUnchanged();
                CSharpAuthoringMutation mutation = prepare(authoring);
                if (!mutation.Succeeded || mutation.Snapshot is null)
                {
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1003",
                        "The semantic patch was rejected; no source or runtime generation was changed.",
                        mutation.Diagnostics);
                }
                return CreatePreview(
                    authoring.Current,
                    mutation.Snapshot,
                    mutation.Diagnostics,
                    transaction,
                    publishesRuntime: true);
            }
            finally
            {
                gate.Release();
            }
        }

        private AuthoringPreviewResponse CreatePreview(
            CSharpAuthoringSnapshot baseline,
            CSharpAuthoringSnapshot candidate,
            IReadOnlyList<UnifiedDiagnostic> diagnostics,
            AuthoringTransactionContext transaction,
            bool publishesRuntime)
        {
            ValidateEditableSources(candidate);
            Dictionary<string, string> beforeSources = baseline.Request.Sources.ToDictionary(
                static source => source.LogicalPath,
                static source => source.Source,
                StringComparer.Ordinal);
            Dictionary<string, string> afterSources = candidate.Request.Sources.ToDictionary(
                static source => source.LogicalPath,
                static source => source.Source,
                StringComparer.Ordinal);
            string[] paths = beforeSources.Keys
                .Concat(afterSources.Keys)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            List<AuthoringPreviewFile> files = paths
                .Select(path => new AuthoringPreviewFile(
                    path,
                    beforeSources.GetValueOrDefault(path, string.Empty),
                    afterSources.GetValueOrDefault(path, string.Empty)))
                .Where(static file => !string.Equals(
                    file.BeforeText,
                    file.AfterText,
                    StringComparison.Ordinal))
                .ToList();
            PreparedGraphLayout? graphLayout = publishesRuntime
                ? PrepareGraphLayout(baseline, candidate)
                : null;
            CSharpGraphRoundTripReport roundTrip = graphLayout?.Report ??
                roundTripInspector.Inspect(baseline, candidate).Report;
            if (graphLayout is not null && !string.Equals(
                    graphLayout.BaselineText,
                    graphLayout.CandidateText,
                    StringComparison.Ordinal))
            {
                files.Add(new AuthoringPreviewFile(
                    graphLayout.Path,
                    graphLayout.BaselineText,
                    graphLayout.CandidateText));
            }
            string previewToken = ComputePreviewToken(
                baseline,
                candidate,
                transaction,
                publishesRuntime,
                graphLayout);
            if (preparedPreviews.Count >= 32)
            {
                preparedPreviews.Clear();
            }
            preparedPreviews[previewToken] = new PreparedAuthoringPreview(
                baseline.Revision,
                transaction.BaselineRevision,
                transaction.TransactionId,
                transaction.Description,
                transaction.GroupId,
                transaction.BoundPayloadId,
                publishesRuntime,
                candidate,
                diagnostics,
                graphLayout);
            return new AuthoringPreviewResponse(
                "unifiedscript.authoring-preview/v0.3",
                baseline.Revision,
                candidate.Revision,
                transaction.TransactionId,
                transaction.Description,
                publishesRuntime,
                previewToken,
                new SemanticGraphDiffer().Compare(baseline.Graph, candidate.Graph),
                roundTrip,
                files,
                diagnostics);
        }

        private static string ComputePreviewToken(
            CSharpAuthoringSnapshot baseline,
            CSharpAuthoringSnapshot candidate,
            AuthoringTransactionContext transaction,
            bool publishesRuntime,
            PreparedGraphLayout? graphLayout)
        {
            var tokenSource = new StringBuilder();
            AppendTokenPart(tokenSource, baseline.Revision.ToString());
            AppendTokenPart(tokenSource, candidate.Revision.ToString());
            AppendTokenPart(tokenSource, transaction.TransactionId);
            AppendTokenPart(tokenSource, transaction.Description);
            AppendTokenPart(tokenSource, transaction.GroupId ?? string.Empty);
            AppendTokenPart(tokenSource, transaction.BaselineRevision?.ToString() ?? string.Empty);
            AppendTokenPart(tokenSource, transaction.BoundPayloadId?.Value ?? string.Empty);
            AppendTokenPart(tokenSource, publishesRuntime ? "publish" : "draft");
            foreach (CSharpSourceDocument source in candidate.Request.Sources.OrderBy(
                static source => source.LogicalPath,
                StringComparer.Ordinal))
            {
                AppendTokenPart(tokenSource, source.LogicalPath);
                AppendTokenPart(tokenSource, source.Source);
                AppendTokenPart(
                    tokenSource,
                    source.IdentitySidecar is null
                        ? string.Empty
                        : IdentitySidecarJson.Serialize(source.IdentitySidecar, writeIndented: false));
            }
            AppendTokenPart(
                tokenSource,
                ScriptAssetJson.Serialize(candidate.Analysis.ScriptAssets!, writeIndented: false));
            AppendTokenPart(tokenSource, GraphJson.Serialize(candidate.Graph, writeIndented: false));
            if (graphLayout is not null)
            {
                AppendTokenPart(tokenSource, graphLayout.Path);
                AppendTokenPart(tokenSource, graphLayout.BaselineDigest);
                AppendTokenPart(tokenSource, graphLayout.CandidateText);
            }
            return Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(tokenSource.ToString())))
                .ToLowerInvariant();
        }

        private static void AppendTokenPart(StringBuilder builder, string value)
        {
            builder.Append(value.Length).Append(':').Append(value).Append(';');
        }

        private PreparedGraphLayout? PrepareGraphLayout(
            CSharpAuthoringSnapshot baseline,
            CSharpAuthoringSnapshot candidate)
        {
            if (artifactOutputs.GraphLayoutPath is null)
            {
                return null;
            }

            string path = artifactOutputs.GraphLayoutPath;
            string baselineText = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            try
            {
                GraphLayoutDocument baselineLayout = baselineText.Length == 0
                    ? GraphLayoutDocument.CreateEmpty()
                    : GraphLayoutJson.Deserialize(baselineText);
                CSharpGraphRoundTripInspection inspection = roundTripInspector.Inspect(
                    baseline,
                    candidate,
                    baselineLayout);
                string baselineCanonical = GraphLayoutJson.Serialize(
                    baselineLayout,
                    writeIndented: false);
                string candidateCanonical = GraphLayoutJson.Serialize(
                    inspection.CandidateLayout!,
                    writeIndented: false);
                string candidateText = string.Equals(
                        baselineCanonical,
                        candidateCanonical,
                        StringComparison.Ordinal)
                    ? baselineText
                    : GraphLayoutJson.Serialize(
                        inspection.CandidateLayout!,
                        writeIndented: true) + "\n";
                return new PreparedGraphLayout(
                    path,
                    ComputeTextDigest(baselineText),
                    baselineText,
                    candidateText,
                    inspection.Report);
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException or ArgumentException)
            {
                throw new AuthoringProtocolException(
                    HttpStatusCode.Conflict,
                    "UAW1016",
                    $"The graph layout cannot join the authoring transaction: {exception.Message}",
                    innerException: exception);
            }
        }

        private static string ComputeTextDigest(string text) =>
            "sha256:" + Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(text)))
                .ToLowerInvariant();

        private static void ValidateGraphLayoutBaseline(PreparedGraphLayout? graphLayout)
        {
            if (graphLayout is null)
            {
                return;
            }
            string currentText = File.Exists(graphLayout.Path)
                ? File.ReadAllText(graphLayout.Path)
                : string.Empty;
            if (!string.Equals(
                    ComputeTextDigest(currentText),
                    graphLayout.BaselineDigest,
                    StringComparison.Ordinal))
            {
                throw new AuthoringProtocolException(
                    HttpStatusCode.Conflict,
                    "UAW1015",
                    "The graph layout changed after the diff preview; refresh and review the synchronized layout again.");
            }
        }

        private CSharpAuthoringMutation PrepareMutation(
            CSharpAuthoringWorkspace workspace,
            Func<CSharpAuthoringWorkspace, CSharpAuthoringMutation> prepare,
            AuthoringTransactionContext transaction,
            bool publishesRuntime,
            out PreparedGraphLayout? graphLayout)
        {
            if (transaction.PreviewToken is null)
            {
                CSharpAuthoringMutation mutation = prepare(workspace);
                graphLayout = publishesRuntime && mutation.Succeeded && mutation.Snapshot is not null
                    ? PrepareGraphLayout(workspace.Current, mutation.Snapshot)
                    : null;
                return mutation;
            }
            PreparedAuthoringPreview prepared = TakePreparedPreview(
                workspace.Current,
                transaction,
                publishesRuntime);
            graphLayout = prepared.GraphLayout;
            return new CSharpAuthoringMutation(
                workspace.Current.Revision,
                prepared.Candidate,
                prepared.Diagnostics);
        }

        private CSharpAuthoringSnapshot ResolvePreparedSnapshot(
            CSharpAuthoringSnapshot baseline,
            CSharpAuthoringSnapshot fallbackCandidate,
            AuthoringTransactionContext transaction,
            bool publishesRuntime,
            out PreparedGraphLayout? graphLayout)
        {
            if (transaction.PreviewToken is null)
            {
                graphLayout = publishesRuntime
                    ? PrepareGraphLayout(baseline, fallbackCandidate)
                    : null;
                return fallbackCandidate;
            }
            PreparedAuthoringPreview prepared = TakePreparedPreview(
                baseline,
                transaction,
                publishesRuntime);
            graphLayout = prepared.GraphLayout;
            return prepared.Candidate;
        }

        private PreparedAuthoringPreview TakePreparedPreview(
            CSharpAuthoringSnapshot baseline,
            AuthoringTransactionContext transaction,
            bool publishesRuntime)
        {
            if (transaction.PreviewToken is null ||
                !preparedPreviews.Remove(
                    transaction.PreviewToken,
                    out PreparedAuthoringPreview? prepared) ||
                prepared.BaselineRevision != baseline.Revision ||
                prepared.ClientBaselineRevision != transaction.BaselineRevision ||
                !string.Equals(
                    prepared.TransactionId,
                    transaction.TransactionId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    prepared.Description,
                    transaction.Description,
                    StringComparison.Ordinal) ||
                !string.Equals(prepared.GroupId, transaction.GroupId, StringComparison.Ordinal) ||
                prepared.BoundPayloadId != transaction.BoundPayloadId ||
                prepared.PublishesRuntime != publishesRuntime)
            {
                throw new AuthoringProtocolException(
                    HttpStatusCode.Conflict,
                    "UAW1012",
                    "The candidate no longer matches the confirmed diff preview; no source, artifact, history, or runtime generation was changed.");
            }
            ValidateGraphLayoutBaseline(prepared.GraphLayout);
            return prepared;
        }

        private async Task<AuthoringWorkspaceResponse> ApplyAsync(
            Func<CSharpAuthoringWorkspace, CSharpAuthoringMutation> prepare,
            AuthoringTransactionContext transaction,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            await gate.WaitAsync(cancellationToken);
            try
            {
                EnsureAuthoringAvailable();
                if (transaction.GroupId is not null)
                {
                    ActiveAuthoringGroup group = RequireActiveGroup(
                        transaction,
                        requireGroupOperationId: false);
                    EnsureFilesUnchanged();
                    if (string.Equals(transaction.TransactionId, group.Id, StringComparison.Ordinal))
                    {
                        throw new AuthoringProtocolException(
                            HttpStatusCode.BadRequest,
                            "UAW1008",
                            "A staged operation ID must differ from its grouped transaction ID.");
                    }
                    if (group.Draft.ContainsOperationId(transaction.TransactionId))
                    {
                        throw new AuthoringProtocolException(
                            HttpStatusCode.Conflict,
                            "UAW1006",
                            $"Staged authoring operation ID '{transaction.TransactionId}' was already accepted.");
                    }

                    CSharpAuthoringMutation stagedMutation = PrepareMutation(
                        group.Draft,
                        prepare,
                        transaction,
                        publishesRuntime: false,
                        out _);
                    if (!stagedMutation.Succeeded || stagedMutation.Snapshot is null)
                    {
                        throw new AuthoringProtocolException(
                            HttpStatusCode.Conflict,
                            "UAW1003",
                            "The staged semantic patch was rejected; the grouped draft was not changed.",
                            stagedMutation.Diagnostics);
                    }
                    group.Draft.Commit(
                        stagedMutation,
                        transaction.TransactionId,
                        transaction.Description);
                    activeGroup = group with
                    {
                        StagedOperationCount = checked(group.StagedOperationCount + 1),
                    };
                    preparedPreviews.Clear();
                    Workspace = CreateWorkspace(activeGroup.Draft.Current, reload.Generation);
                    return Workspace;
                }

                ValidateTransaction(transaction);
                EnsureFilesUnchanged();
                CSharpAuthoringMutation mutation = PrepareMutation(
                    authoring,
                    prepare,
                    transaction,
                    publishesRuntime: true,
                    out PreparedGraphLayout? graphLayout);
                if (!mutation.Succeeded || mutation.Snapshot is null)
                {
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1003",
                        "The semantic patch was rejected; no source or runtime generation was changed.",
                        mutation.Diagnostics);
                }
                CSharpAuthoringHistoryDocument persistedHistory = authoring.PreviewCommit(
                    mutation,
                    transaction.TransactionId,
                    transaction.Description);
                return CommitPreparedSnapshot(
                    mutation.Snapshot,
                    () => authoring.Commit(
                        mutation,
                        transaction.TransactionId,
                        transaction.Description),
                    persistedHistory,
                    transaction.TransactionId,
                    graphLayout);
            }
            finally
            {
                gate.Release();
            }
        }

        private void EnsureAuthoringAvailable()
        {
            if (execution is not null)
            {
                throw new AuthoringProtocolException(
                    HttpStatusCode.Conflict,
                    "UAW1001",
                    "Finish the active debug execution before changing the authoring workspace.");
            }
        }

        private CoreClrDebugExecution RequireExecution(
            SemanticId generationId,
            long executionId)
        {
            if (generationId.IsEmpty || generationId != reload.Generation.GenerationId)
            {
                throw new BreakpointProtocolException(
                    "UDB1002",
                    "The debug command belongs to a stale or different CoreCLR program generation.");
            }

            CoreClrDebugExecution current = execution ??
                throw new BreakpointProtocolException(
                    "UDB1005",
                    "There is no active debug execution.");
            if (executionId <= 0 || current.ExecutionId != executionId)
            {
                throw new BreakpointProtocolException(
                    "UDB1005",
                    $"Debug execution '{executionId}' is stale or does not exist.");
            }

            return current;
        }

        private CoreClrDebugPause RequirePause(SemanticId pauseId)
        {
            CoreClrDebugPause current = pause ??
                throw new BreakpointProtocolException(
                    "UDB1006",
                    "The debug execution is not paused.");
            if (pauseId.IsEmpty || current.PauseId != pauseId)
            {
                throw new BreakpointProtocolException(
                    "UDB1006",
                    $"Pause '{pauseId}' is stale or is not the current suspended execution.");
            }

            return current;
        }

        private void ValidateTransaction(AuthoringTransactionContext transaction)
        {
            if (activeGroup is not null)
            {
                throw new AuthoringProtocolException(
                    HttpStatusCode.Conflict,
                    "UAW1009",
                    $"Authoring group '{activeGroup.Id}' is active; use its group ID or commit/cancel it first.");
            }
            if (transaction.BaselineRevision is long baselineRevision &&
                baselineRevision != authoring.Current.Revision)
            {
                throw new AuthoringProtocolException(
                    HttpStatusCode.Conflict,
                    "UAW1005",
                    $"Authoring transaction revision '{baselineRevision}' is stale; " +
                    $"the current revision is '{authoring.Current.Revision}'.");
            }

            if (authoring.ContainsOperationId(transaction.TransactionId))
            {
                throw new AuthoringProtocolException(
                    HttpStatusCode.Conflict,
                    "UAW1006",
                    $"Authoring transaction ID '{transaction.TransactionId}' was already committed.");
            }
        }

        private ActiveAuthoringGroup RequireActiveGroup(
            AuthoringTransactionContext transaction,
            bool requireGroupOperationId = true)
        {
            ActiveAuthoringGroup group = activeGroup ??
                throw new AuthoringProtocolException(
                    HttpStatusCode.Conflict,
                    "UAW1010",
                    "There is no active grouped authoring transaction.");
            if (!string.Equals(transaction.GroupId, group.Id, StringComparison.Ordinal))
            {
                throw new AuthoringProtocolException(
                    HttpStatusCode.Conflict,
                    "UAW1009",
                    $"Request group '{transaction.GroupId ?? "<none>"}' does not match active group '{group.Id}'.");
            }
            if (requireGroupOperationId &&
                !string.Equals(transaction.TransactionId, group.Id, StringComparison.Ordinal))
            {
                throw new AuthoringProtocolException(
                    HttpStatusCode.BadRequest,
                    "UAW1008",
                    "Commit and cancel requests must use the grouped transaction ID as their operation ID.");
            }
            if (transaction.BaselineRevision is long baselineRevision &&
                baselineRevision != group.Draft.Current.Revision)
            {
                throw new AuthoringProtocolException(
                    HttpStatusCode.Conflict,
                    "UAW1005",
                    $"Grouped draft revision '{baselineRevision}' is stale; " +
                    $"the current draft revision is '{group.Draft.Current.Revision}'.");
            }
            return group;
        }

        private AuthoringWorkspaceResponse CommitPreparedSnapshot(
            CSharpAuthoringSnapshot snapshot,
            Action commit,
            CSharpAuthoringHistoryDocument persistedHistory,
            string transactionId,
            PreparedGraphLayout? graphLayout)
        {
            CSharpAuthoringSnapshot previous = authoring.Current;
            long fromGeneration = reload.Generation.Number;
            var phases = new List<ReloadTransactionPhaseResponse>();
            long phaseStart = Stopwatch.GetTimestamp();
            CoreClrDebugReloadResult nextReload;
            try
            {
                nextReload = transport.Reload(snapshot.Request);
                phases.Add(CompleteReloadPhase(
                    "compile",
                    phaseStart,
                    "succeeded",
                    "Compiled and loaded the candidate Workbench CoreCLR generation."));
            }
            catch (Exception exception)
            {
                phases.Add(CompleteReloadPhase(
                    "compile",
                    phaseStart,
                    "failed",
                    exception.Message));
                phases.Add(new ReloadTransactionPhaseResponse(
                    "rollback",
                    "unchanged",
                    0,
                    "No source, artifact, history, or active generation was changed."));
                var failedTransaction = new ReloadTransactionResponse(
                    "unifiedscript.reload-transaction/v0.1",
                    transactionId,
                    "authoring",
                    "rolledBack",
                    fromGeneration,
                    fromGeneration + 1,
                    fromGeneration,
                    phases,
                    exception.Message);
                throw new AuthoringProtocolException(
                    HttpStatusCode.Conflict,
                    "UAW1004",
                    "The candidate runtime generation was rejected; the previous source, artifacts, history, and runtime generation remain active.",
                    reloadTransaction: failedTransaction,
                    innerException: exception);
            }
            phaseStart = Stopwatch.GetTimestamp();
            try
            {
                ValidateGraphLayoutBaseline(graphLayout);
                Persist(snapshot, persistedHistory, graphLayout);
                phases.Add(CompleteReloadPhase(
                    "artifactPublication",
                    phaseStart,
                    "succeeded",
                    "Atomically published source, identities, graph layout, authoring artifacts, PE/PDB, and history."));
            }
            catch (Exception exception)
            {
                phases.Add(CompleteReloadPhase(
                    "artifactPublication",
                    phaseStart,
                    "failed",
                    exception.Message));
                phaseStart = Stopwatch.GetTimestamp();
                try
                {
                    reload = transport.Reload(previous.Request);
                    Health = CreateHealth(reload.Generation);
                    Workspace = CreateWorkspace(previous, reload.Generation);
                    phases.Add(CompleteReloadPhase(
                        "rollback",
                        phaseStart,
                        "restored",
                        "Restored the previous runtime program as a new Workbench generation."));
                }
                catch (Exception restoreException)
                {
                    phases.Add(CompleteReloadPhase(
                        "rollback",
                        phaseStart,
                        "failed",
                        restoreException.Message));
                    var failedTransaction = new ReloadTransactionResponse(
                        "unifiedscript.reload-transaction/v0.1",
                        transactionId,
                        "authoring",
                        "failed",
                        fromGeneration,
                        nextReload.Generation.Number,
                        reload.Generation.Number,
                        phases,
                        restoreException.Message);
                    throw new AuthoringProtocolException(
                        HttpStatusCode.InternalServerError,
                        "UAW1014",
                        "The authoring commit failed and the previous runtime program could not be fully restored.",
                        rollbackState: "failed",
                        reloadTransaction: failedTransaction,
                        innerException: new AggregateException(exception, restoreException));
                }

                var restoredTransaction = new ReloadTransactionResponse(
                    "unifiedscript.reload-transaction/v0.1",
                    transactionId,
                    "authoring",
                    "rolledBack",
                    fromGeneration,
                    nextReload.Generation.Number,
                    reload.Generation.Number,
                    phases,
                    exception.Message);
                if (exception is AuthoringProtocolException authoringException)
                {
                    throw authoringException.WithRollback("restored", restoredTransaction);
                }
                throw new AuthoringProtocolException(
                    HttpStatusCode.Conflict,
                    "UAW1013",
                    "The authoring commit failed; the previous source, artifacts, history, and runtime program were restored as a new generation.",
                    rollbackState: "restored",
                    reloadTransaction: restoredTransaction,
                    innerException: exception);
            }

            phaseStart = Stopwatch.GetTimestamp();
            commit();
            preparedPreviews.Clear();
            if (artifactOutputs.HistoryPath is not null)
            {
                historyPersistenceState = "persisted";
                historyPersistenceMessage = null;
            }
            reload = nextReload;
            bindings = reload.Breakpoints.Bindings;
            Health = CreateHealth(reload.Generation);
            phases.Add(CompleteReloadPhase(
                "workbenchGenerationSwap",
                phaseStart,
                "succeeded",
                "Committed authoring history and exposed the candidate Workbench generation."));
            var transaction = new ReloadTransactionResponse(
                "unifiedscript.reload-transaction/v0.1",
                transactionId,
                "authoring",
                "published",
                fromGeneration,
                reload.Generation.Number,
                reload.Generation.Number,
                phases,
                null);
            Workspace = CreateWorkspace(authoring.Current, reload.Generation, transaction);
            return Workspace;
        }

        private static ReloadTransactionPhaseResponse CompleteReloadPhase(
            string name,
            long startedTimestamp,
            string state,
            string detail)
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
            long microseconds = Math.Max(
                0,
                (long)Math.Ceiling(elapsedTicks * 1_000_000d / Stopwatch.Frequency));
            return new ReloadTransactionPhaseResponse(
                name,
                state,
                microseconds,
                detail.Length <= 2048 ? detail : detail[..2048]);
        }

        private void EnsureFilesUnchanged()
        {
            foreach ((string logicalPath, string physicalPath) in sourceFiles)
            {
                string diskSource = File.ReadAllText(physicalPath);
                if (!string.Equals(diskSource, persistedSources[logicalPath], StringComparison.Ordinal))
                {
                    string message =
                        $"Source '{logicalPath}' changed outside the workbench; reload before saving.";
                    throw new AuthoringProtocolException(
                        HttpStatusCode.Conflict,
                        "UAW1002",
                        message,
                        [new UnifiedDiagnostic(
                            "UAW1002",
                            UnifiedDiagnosticSeverity.Error,
                            message,
                            SourceSpan.Unknown(logicalPath))]);
                }
            }
            string? diskIdentity = identityPath is null || !File.Exists(identityPath)
                ? null
                : File.ReadAllText(identityPath);
            if (!string.Equals(diskIdentity, persistedIdentity, StringComparison.Ordinal))
            {
                const string message =
                    "The identity sidecar changed outside the workbench; reload before saving.";
                throw new AuthoringProtocolException(
                    HttpStatusCode.Conflict,
                    "UAW1002",
                    message,
                    [new UnifiedDiagnostic(
                        "UAW1002",
                        UnifiedDiagnosticSeverity.Error,
                        message,
                        SourceSpan.Unknown(sourceLogicalPath))]);
            }
        }

        private void ValidateEditableSources(CSharpAuthoringSnapshot snapshot)
        {
            foreach (CSharpSourceDocument source in snapshot.Request.Sources)
            {
                if (readOnlySources.Contains(source.LogicalPath) && source.Source != persistedSources[source.LogicalPath])
                    throw new AuthoringProtocolException(HttpStatusCode.Conflict, "UAW1017",
                        $"'{source.LogicalPath}' is a project compilation input; open that source in its own authoring session before editing it.");
            }
        }

        private void Persist(
            CSharpAuthoringSnapshot snapshot,
            CSharpAuthoringHistoryDocument history,
            PreparedGraphLayout? graphLayout,
            ImportedProjectInput? imported = null)
        {
            if (imported is null) ValidateEditableSources(snapshot);
            IReadOnlyDictionary<string, string> files = imported?.Input.SourceFiles ?? sourceFiles;
            IReadOnlyDictionary<string, string> baseline = imported?.DiskSources ?? persistedSources;
            var artifactPaths = new[] { artifactOutputs.DeclarationPath, artifactOutputs.SurfacePath,
                artifactOutputs.ManagedAssemblyPath, artifactOutputs.PortablePdbPath,
                artifactOutputs.HistoryPath, artifactOutputs.GraphLayoutPath, artifactOutputs.CompiledChunkPath, identityPath };
            foreach (string path in artifactPaths.OfType<string>())
                if (files.Values.Any(source => string.Equals(source, path, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"Authoring artifact '{path}' cannot overwrite a compilation source.");
            var outputs = snapshot.Request.Sources
                .Where(source => source.Source != baseline[source.LogicalPath])
                .Select(source => new AuthoringOutput(
                    files[source.LogicalPath],
                    Encoding.UTF8.GetBytes(source.Source)))
                .ToList();
            string? nextIdentity = null;
            if (identityPath is not null)
            {
                IdentitySidecarDocument sidecar = snapshot.Analysis.IdentitySidecars.Single(item =>
                    string.Equals(item.SourcePath, imported?.PrimaryLogical ?? sourceLogicalPath, StringComparison.Ordinal));
                nextIdentity = IdentitySidecarJson.Serialize(sidecar) + "\n";
                outputs.Add(new AuthoringOutput(
                    identityPath,
                    Encoding.UTF8.GetBytes(nextIdentity)));
            }

            ScriptAssetDocument scriptAssets = snapshot.Analysis.ScriptAssets!;
            AuthoringSurfaceDocument surface = new AuthoringSurfaceProjector().Project(
                scriptAssets,
                snapshot.Graph,
                snapshot.Analysis.DeclarationOwners);
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

            if (graphLayout is not null && !string.Equals(
                    graphLayout.BaselineText,
                    graphLayout.CandidateText,
                    StringComparison.Ordinal))
            {
                outputs.Add(new AuthoringOutput(
                    graphLayout.Path,
                    Encoding.UTF8.GetBytes(graphLayout.CandidateText)));
            }

            if (artifactOutputs.ManagedAssemblyPath is not null ||
                artifactOutputs.PortablePdbPath is not null || artifactOutputs.CompiledChunkPath is not null)
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
                if (artifactOutputs.CompiledChunkPath is not null)
                {
                    CSharpProgramChunkEmitResult chunk = new CSharpProgramChunkCompiler().Compile(
                        emitted.Image, new CSharpProgramChunkOptions(
                            RuntimeReferencePaths: snapshot.Request.RuntimeReferencePaths));
                    outputs.Add(new AuthoringOutput(artifactOutputs.CompiledChunkPath, chunk.Content));
                }
            }

            if (artifactOutputs.HistoryPath is not null)
            {
                outputs.Add(new AuthoringOutput(
                    artifactOutputs.HistoryPath,
                    Encoding.UTF8.GetBytes(CSharpAuthoringHistoryJson.Serialize(history) + "\n")));
            }

            if (imported is not null)
            {
                if (InputIdentity(imported.Input) != InputIdentity(LoadRefreshInput()))
                    throw new AuthoringProtocolException(HttpStatusCode.Conflict, "UAW1002",
                        "Project inputs changed while refreshing; retry after saving. No external source edit was overwritten.");
                if (imported.ArchivedHistory is not null) outputs.Add(imported.ArchivedHistory);
            }
            WriteTransactionally(outputs);
            if (imported is not null)
            {
                sourceFiles = imported.Input.SourceFiles;
                readOnlySources = imported.Input.ReadOnlySources;
                sourceLogicalPath = imported.PrimaryLogical;
                persistedSources.Clear();
            }
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
            CoreClrProgramGeneration generation,
            ReloadTransactionResponse? reloadTransaction = null)
        {
            ScriptAssetDocument scriptAssets = snapshot.Analysis.ScriptAssets!;
            AuthoringSurfaceDocument surface = new AuthoringSurfaceProjector().Project(
                scriptAssets,
                snapshot.Graph,
                snapshot.Analysis.DeclarationOwners);
            return new AuthoringWorkspaceResponse(
                "unifiedscript.authoring-workspace/v0.5",
                snapshot.Revision,
                sourceLogicalPath,
                snapshot.Request.Sources.Single(source =>
                    string.Equals(source.LogicalPath, sourceLogicalPath, StringComparison.Ordinal)).Source,
                scriptAssets,
                snapshot.Graph,
                surface,
                snapshot.Analysis.Profile,
                CreateHistoryResponse(),
                generation,
                activeGroup is null
                    ? null
                    : new AuthoringGroupResponse(
                        activeGroup.Id,
                        activeGroup.Description,
                        activeGroup.BaselineRevision,
                        activeGroup.StagedOperationCount),
                reloadTransaction);
        }

        private AuthoringHistoryResponse CreateHistoryResponse()
        {
            CSharpAuthoringHistoryState history = authoring.History;
            return new AuthoringHistoryResponse(
                history.CanUndo,
                history.CanRedo,
                history.UndoDepth,
                history.RedoDepth,
                history.UndoTransactionId,
                history.UndoDescription,
                history.RedoTransactionId,
                history.RedoDescription,
                authoring.LastOperationId,
                historyPersistenceState,
                historyPersistenceMessage);
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
            CoreClrDebugExecution currentExecution,
            IReadOnlyList<SemanticBreakpointBinding> bindings,
            CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            Task<CoreClrDebugPause> pauseTask = transport.WaitForPauseAsync(
                currentExecution.ExecutionId,
                timeout.Token);
            Task<ExecutionResult> completionTask = transport.WaitForCompletionAsync(
                currentExecution.ExecutionId,
                timeout.Token);
            try
            {
                Task completed = await Task.WhenAny(pauseTask, completionTask);
                timeout.Cancel();
                if (completed == pauseTask && pauseTask.IsCompletedSuccessfully)
                {
                    CoreClrDebugPause nextPause = await pauseTask;
                    IReadOnlyList<CoreClrDebugStackFrame> frames = transport.GetStackTrace(
                        reload.Generation.GenerationId,
                        nextPause.PauseId);
                    DebugFrameLocalsResponse[] frameLocals = frames
                        .Where(static frame => frame.HasLocals)
                        .Select(frame => new DebugFrameLocalsResponse(
                            frame.FrameId,
                            transport.GetLocals(
                                reload.Generation.GenerationId,
                                nextPause.PauseId,
                                frame.FrameId)))
                        .ToArray();
                    IReadOnlyList<CoreClrDebugValue> locals = frameLocals.FirstOrDefault()?.Locals ?? [];
                    return DebugSessionResponse.Paused(
                        reload.Generation,
                        currentExecution.ExecutionId,
                        nextPause,
                        frames,
                        locals,
                        frameLocals,
                        bindings);
                }

                ExecutionResult result = await completionTask;
                return DebugSessionResponse.Completed(
                    reload.Generation,
                    currentExecution.ExecutionId,
                    result,
                    bindings);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return DebugSessionResponse.Running(
                    reload.Generation,
                    currentExecution.ExecutionId,
                    bindings);
            }
        }

        private sealed record AuthoringOutput(string Path, byte[] Content);

        private sealed record PendingAuthoringOutput(
            string Path,
            string TemporaryPath,
            string BackupPath);

        private sealed record ActiveAuthoringGroup(
            string Id,
            string Description,
            long BaselineRevision,
            CSharpAuthoringWorkspace Draft,
            int StagedOperationCount);

        private sealed record PreparedAuthoringPreview(
            long BaselineRevision,
            long? ClientBaselineRevision,
            string TransactionId,
            string Description,
            string? GroupId,
            SemanticId? BoundPayloadId,
            bool PublishesRuntime,
            CSharpAuthoringSnapshot Candidate,
            IReadOnlyList<UnifiedDiagnostic> Diagnostics,
            PreparedGraphLayout? GraphLayout);

        private sealed record PreparedGraphLayout(
            string Path,
            string BaselineDigest,
            string BaselineText,
            string CandidateText,
            CSharpGraphRoundTripReport Report);
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
        IReadOnlyList<UnifiedDiagnostic>? diagnostics = null,
        string rollbackState = "unchanged",
        ReloadTransactionResponse? reloadTransaction = null,
        Exception? innerException = null)
        : Exception(message, innerException)
    {
        public HttpStatusCode Status { get; } = status;

        public string Code { get; } = code;

        public IReadOnlyList<UnifiedDiagnostic> Diagnostics { get; } = diagnostics ?? [];

        public string RollbackState { get; } = rollbackState;

        public ReloadTransactionResponse? ReloadTransaction { get; } = reloadTransaction;

        public AuthoringProtocolException WithRollback(
            string state,
            ReloadTransactionResponse? reloadTransaction = null) => new(
            Status,
            Code,
            Message,
            Diagnostics,
            state,
            reloadTransaction ?? ReloadTransaction,
            this);
    }
}

internal sealed record AuthoringArtifactOutputs(
    string? DeclarationPath = null,
    string? SurfacePath = null,
    string? ManagedAssemblyPath = null,
    string? PortablePdbPath = null,
    string? HistoryPath = null,
    string? GraphLayoutPath = null,
    string? CompiledChunkPath = null)
{
    public AuthoringArtifactOutputs Normalize() => new(
        NormalizePath(DeclarationPath),
        NormalizePath(SurfacePath),
        NormalizePath(ManagedAssemblyPath),
        NormalizePath(PortablePdbPath),
        NormalizePath(HistoryPath),
        NormalizePath(GraphLayoutPath),
        NormalizePath(CompiledChunkPath));

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
    VisualProfileReport Profile,
    AuthoringHistoryResponse History,
    CoreClrProgramGeneration Generation,
    AuthoringGroupResponse? Group,
    ReloadTransactionResponse? ReloadTransaction);

internal sealed record ReloadTransactionPhaseResponse(
    string Name,
    string State,
    long DurationMicroseconds,
    string Detail);

internal sealed record ReloadTransactionResponse(
    string SchemaVersion,
    string TransactionId,
    string Source,
    string State,
    long FromGeneration,
    long CandidateGeneration,
    long ActiveGeneration,
    IReadOnlyList<ReloadTransactionPhaseResponse> Phases,
    string? Failure);

internal sealed record AuthoringPreviewResponse(
    string SchemaVersion,
    long BaselineRevision,
    long CandidateRevision,
    string TransactionId,
    string Description,
    bool PublishesRuntime,
    string PreviewToken,
    SemanticGraphDiffDocument SemanticDiff,
    CSharpGraphRoundTripReport RoundTrip,
    IReadOnlyList<AuthoringPreviewFile> Files,
    IReadOnlyList<UnifiedDiagnostic> Diagnostics);

internal sealed record AuthoringAiSemanticProposalPreviewResponse(
    string SchemaVersion,
    AiSemanticPatchProposalDocument Proposal,
    AuthoringPreviewResponse Preview);

internal sealed record AuthoringAiSemanticProposalCommitResponse(
    string SchemaVersion,
    SemanticId ProposalId,
    AuthoringWorkspaceResponse Workspace);

internal sealed record AuthoringPreviewFile(
    string Path,
    string BeforeText,
    string AfterText);

internal sealed record AuthoringGroupResponse(
    string Id,
    string Description,
    long BaselineRevision,
    int StagedOperationCount);

internal sealed record AuthoringHistoryResponse(
    bool CanUndo,
    bool CanRedo,
    int UndoDepth,
    int RedoDepth,
    string? UndoTransactionId,
    string? UndoDescription,
    string? RedoTransactionId,
    string? RedoDescription,
    string? LastOperationId,
    string PersistenceState,
    string? PersistenceMessage);

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
    IReadOnlyList<SemanticId>? BreakpointNodeIds,
    SemanticId? ExpectedSourceCompilationId = null,
    SemanticId? ExpectedSemanticConfigurationId = null);

internal sealed record DebugExecutionRequest(
    SemanticId GenerationId,
    long ExecutionId);

internal sealed record DebugResumeRequest(
    SemanticId GenerationId,
    long ExecutionId,
    SemanticId PauseId);

internal sealed record DebugEvaluateRequest(
    SemanticId GenerationId,
    long ExecutionId,
    SemanticId PauseId,
    long FrameId,
    string Expression);

internal sealed record DebugLiveWorldPublishRequest(
    SemanticId ExpectedSourceCompilationId,
    SemanticId ExpectedSemanticConfigurationId);

internal sealed record DebugLiveWorldPublishResponse(
    string SchemaVersion,
    CoreClrProgramGeneration Generation,
    string AssemblyPath,
    string PortablePdbPath,
    ReloadTransactionResponse ReloadTransaction);

internal sealed record DebugReceiverRequest(
    SemanticId AssetId,
    IReadOnlyList<DebugStateOverrideRequest>? StateOverrides);

internal sealed record DebugStateOverrideRequest(SemanticId StateId, JsonElement Value);

internal sealed record DebugFrameLocalsResponse(
    long FrameId,
    IReadOnlyList<CoreClrDebugValue> Locals);

internal sealed record DebugSessionResponse(
    string SchemaVersion,
    string State,
    CoreClrProgramGeneration Generation,
    long ExecutionId,
    CoreClrDebugPause? Pause,
    IReadOnlyList<CoreClrDebugStackFrame> Frames,
    IReadOnlyList<CoreClrDebugValue> Locals,
    IReadOnlyList<DebugFrameLocalsResponse> FrameLocals,
    ExecutionResult? Result,
    IReadOnlyList<SemanticBreakpointBinding> Breakpoints)
{
    private const string CurrentSchemaVersion = "unifiedscript.debug-session/v0.2";

    public static DebugSessionResponse Running(
        CoreClrProgramGeneration generation,
        long executionId,
        IReadOnlyList<SemanticBreakpointBinding> breakpoints) => new(
        CurrentSchemaVersion,
        "running",
        generation,
        executionId,
        null,
        [],
        [],
        [],
        null,
        breakpoints);

    public static DebugSessionResponse Paused(
        CoreClrProgramGeneration generation,
        long executionId,
        CoreClrDebugPause pause,
        IReadOnlyList<CoreClrDebugStackFrame> frames,
        IReadOnlyList<CoreClrDebugValue> locals,
        IReadOnlyList<DebugFrameLocalsResponse> frameLocals,
        IReadOnlyList<SemanticBreakpointBinding> breakpoints) => new(
        CurrentSchemaVersion,
        "paused",
        generation,
        executionId,
        pause,
        frames,
        locals,
        frameLocals,
        null,
        breakpoints);

    public static DebugSessionResponse Completed(
        CoreClrProgramGeneration generation,
        long executionId,
        ExecutionResult result,
        IReadOnlyList<SemanticBreakpointBinding> breakpoints) => new(
        CurrentSchemaVersion,
        "completed",
        generation,
        executionId,
        null,
        [],
        [],
        [],
        result,
        breakpoints);
}

internal sealed record DebugErrorResponse(
    DebugError Error,
    IReadOnlyList<UnifiedDiagnostic>? Diagnostics = null,
    IReadOnlyList<AuthoringQuickFixResponse>? QuickFixes = null,
    AuthoringRollbackResponse? Rollback = null,
    ReloadTransactionResponse? ReloadTransaction = null);

internal sealed record DebugError(string Code, string Message);

internal sealed record AuthoringQuickFixResponse(
    string Id,
    string Label,
    string Kind);

internal sealed record AuthoringRollbackResponse(
    string State,
    long Revision,
    long Generation,
    string Message);
