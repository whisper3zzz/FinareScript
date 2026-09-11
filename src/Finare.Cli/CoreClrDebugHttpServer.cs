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
    private readonly AuthoringSession session;
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
        Func<ProjectAuthoringInput>? refreshProject = null,
        IReadOnlyDictionary<string, string>? identityFiles = null)
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

        session = new AuthoringSession(
            request,
            sourcePath,
            identityPath,
            artifactOutputs ?? new AuthoringArtifactOutputs(),
            sourceFiles,
            readOnlySources,
            refreshProject,
            identityFiles);
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
                    AuthoringFailureKind.InvalidInput,
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
        if (path == "/api/authoring/native/prepare")
        {
            NativePublicationPrepareResponse prepared = await session.PrepareNativePublicationAsync(
                transaction,
                cancellationToken);
            await WriteJsonAsync(context.Response, prepared, cancellationToken);
            return;
        }
        if (path == "/api/authoring/native/finish")
        {
            NativePublicationFinishRequest finish = await ReadJsonAsync<NativePublicationFinishRequest>(
                context.Request,
                cancellationToken);
            NativePublicationFinishResponse result = await session.FinishNativePublicationAsync(
                transaction,
                finish,
                cancellationToken);
            await WriteJsonAsync(context.Response, result, cancellationToken);
            return;
        }
        if (path == "/api/authoring/native/cancel")
        {
            NativePublicationFinishResponse result = await session.CancelNativePublicationAsync(
                transaction,
                cancellationToken);
            await WriteJsonAsync(context.Response, result, cancellationToken);
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
                    AuthoringFailureKind.Conflict,
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
                    AuthoringFailureKind.InvalidInput,
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
                AuthoringFailureKind.InvalidInput,
                "UAW1008",
                "Authoring transaction IDs and labels must be non-empty and at most 128 and 256 characters.");
        }
        if (groupId is not null && (string.IsNullOrWhiteSpace(groupId) || groupId.Length > 128))
        {
            throw new AuthoringProtocolException(
                AuthoringFailureKind.InvalidInput,
                "UAW1008",
                $"Header '{GroupIdHeader}' must contain a non-empty group ID of at most 128 characters.");
        }
        if (previewToken is not null &&
            (previewToken.Length != 64 || previewToken.Any(static character => !Uri.IsHexDigit(character))))
        {
            throw new AuthoringProtocolException(
                AuthoringFailureKind.InvalidInput,
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
            AuthoringProtocolException authoring => authoring.FailureKind switch
            {
                AuthoringFailureKind.InvalidInput => HttpStatusCode.BadRequest,
                AuthoringFailureKind.Conflict => HttpStatusCode.Conflict,
                AuthoringFailureKind.ExecutionFailed => HttpStatusCode.InternalServerError,
                _ => HttpStatusCode.InternalServerError,
            },
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
