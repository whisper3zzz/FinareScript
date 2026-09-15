using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Finare.Core;
using Finare.CSharp;
using Finare.Semantics;

namespace Finare.Runtime;

public static class CoreClrDebugFrameOperations
{
    public const string Reload = "reload";
    public const string SetBreakpoint = "set-breakpoint";
    public const string RemoveBreakpoint = "remove-breakpoint";
    public const string ResolveMethod = "resolve-method";
    public const string Start = "start";
    public const string WaitForPause = "wait-for-pause";
    public const string StackTrace = "stack-trace";
    public const string Locals = "locals";
    public const string Evaluate = "evaluate";
    public const string Pause = "pause";
    public const string Continue = "continue";
    public const string StepOver = "step-over";
    public const string StepInto = "step-into";
    public const string StepOut = "step-out";
    public const string WaitForCompletion = "wait-for-completion";
}

public sealed class CoreClrDebugRemoteException : Exception
{
    public CoreClrDebugRemoteException(
        string code,
        string message,
        string remoteType)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteType);
        Code = code;
        RemoteType = remoteType;
    }

    public string Code { get; }

    public string RemoteType { get; }
}

public sealed class CoreClrDebugRemoteTransport : ICoreClrDebugTransport
{
    private static readonly JsonSerializerOptions JsonOptions =
        UnifiedJson.CreateOptions(writeIndented: false);
    private readonly Func<CancellationToken, ValueTask<Stream>> connect;
    private long nextRequestId;
    private int disposed;

    public CoreClrDebugRemoteTransport(string host, int port)
        : this(cancellationToken => ConnectTcpAsync(host, port, cancellationToken))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
    }

    public CoreClrDebugRemoteTransport(
        Func<CancellationToken, ValueTask<Stream>> connect)
    {
        this.connect = connect ?? throw new ArgumentNullException(nameof(connect));
    }

    public CoreClrDebugReloadResult Reload(CSharpCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Call<CoreClrDebugReloadResult>(
            CoreClrDebugFrameOperations.Reload,
            DebugWireCompilation.From(request));
    }

    public BreakpointBindingSnapshot SetBreakpoint(SemanticBreakpoint breakpoint) =>
        Call<BreakpointBindingSnapshot>(
            CoreClrDebugFrameOperations.SetBreakpoint,
            breakpoint);

    public bool RemoveBreakpoint(SemanticId breakpointId) =>
        Call<DebugWireBooleanResponse>(
            CoreClrDebugFrameOperations.RemoveBreakpoint,
            new DebugWireSemanticIdRequest(breakpointId)).Value;

    public SemanticId ResolveVisualMethodId(string qualifiedMethodName) =>
        Call<DebugWireSemanticIdResponse>(
            CoreClrDebugFrameOperations.ResolveMethod,
            new DebugWireStringRequest(qualifiedMethodName)).Value;

    public CoreClrDebugExecution Start(ExecutionInvocation invocation) =>
        Call<CoreClrDebugExecution>(
            CoreClrDebugFrameOperations.Start,
            DebugWireExecutionInvocation.From(invocation));

    public Task<CoreClrDebugPause> WaitForPauseAsync(
        long executionId,
        CancellationToken cancellationToken = default) =>
        CallAsync<CoreClrDebugPause>(
            CoreClrDebugFrameOperations.WaitForPause,
            new DebugWireExecutionRequest(executionId),
            cancellationToken);

    public IReadOnlyList<CoreClrDebugStackFrame> GetStackTrace(
        SemanticId generationId,
        SemanticId pauseId) =>
        Call<DebugWireStackResponse>(
            CoreClrDebugFrameOperations.StackTrace,
            new DebugWirePauseRequest(generationId, pauseId)).Frames;

    public IReadOnlyList<CoreClrDebugValue> GetLocals(
        SemanticId generationId,
        SemanticId pauseId,
        long frameId) =>
        Call<DebugWireLocalsResponse>(
            CoreClrDebugFrameOperations.Locals,
            new DebugWireFrameRequest(generationId, pauseId, frameId)).Locals;

    public CoreClrDebugEvaluation Evaluate(
        SemanticId generationId,
        SemanticId pauseId,
        long frameId,
        string expression) =>
        Call<CoreClrDebugEvaluation>(
            CoreClrDebugFrameOperations.Evaluate,
            new DebugWireEvaluateRequest(
                generationId,
                pauseId,
                frameId,
                expression));

    public void Pause(SemanticId generationId, long executionId) =>
        CallVoid(
            CoreClrDebugFrameOperations.Pause,
            new DebugWireGenerationExecutionRequest(generationId, executionId));

    public void Continue(SemanticId generationId, SemanticId pauseId) =>
        CallVoid(
            CoreClrDebugFrameOperations.Continue,
            new DebugWirePauseRequest(generationId, pauseId));

    public void StepOver(SemanticId generationId, SemanticId pauseId) =>
        CallVoid(
            CoreClrDebugFrameOperations.StepOver,
            new DebugWirePauseRequest(generationId, pauseId));

    public void StepInto(SemanticId generationId, SemanticId pauseId) =>
        CallVoid(
            CoreClrDebugFrameOperations.StepInto,
            new DebugWirePauseRequest(generationId, pauseId));

    public void StepOut(SemanticId generationId, SemanticId pauseId) =>
        CallVoid(
            CoreClrDebugFrameOperations.StepOut,
            new DebugWirePauseRequest(generationId, pauseId));

    public Task<ExecutionResult> WaitForCompletionAsync(
        long executionId,
        CancellationToken cancellationToken = default) =>
        CallAsync<ExecutionResult>(
            CoreClrDebugFrameOperations.WaitForCompletion,
            new DebugWireExecutionRequest(executionId),
            cancellationToken);

    public void Dispose() => Interlocked.Exchange(ref disposed, 1);

    private TResponse Call<TResponse>(string operation, object request) =>
        CallAsync<TResponse>(operation, request, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    private void CallVoid(string operation, object request) =>
        _ = Call<DebugWireEmptyResponse>(operation, request);

    private async Task<TResponse> CallAsync<TResponse>(
        string operation,
        object request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        long requestId = Interlocked.Increment(ref nextRequestId);
        if (requestId <= 0)
        {
            throw new InvalidOperationException("Remote debug request ID overflowed.");
        }
        JsonElement payload = JsonSerializer.SerializeToElement(
            request,
            request.GetType(),
            JsonOptions);
        CoreClrDebugFrame outbound = CoreClrDebugFrame.Request(
            requestId,
            operation,
            payload);
        await using Stream stream = await connect(cancellationToken);
        await CoreClrDebugFrameCodec.WriteAsync(stream, outbound, cancellationToken);
        CoreClrDebugFrame inbound =
            await CoreClrDebugFrameCodec.ReadAsync(stream, cancellationToken) ??
            throw new CoreClrDebugFrameProtocolException(
                "UDBF1001",
                "Remote debug connection ended before a response frame arrived.");
        if (inbound.Kind != CoreClrDebugFrameKind.Response ||
            inbound.RequestId != requestId ||
            !string.Equals(inbound.Operation, operation, StringComparison.Ordinal))
        {
            throw new CoreClrDebugFrameProtocolException(
                "UDBF1005",
                "Remote debug response does not correlate to its request.");
        }
        if (inbound.Succeeded != true)
        {
            ThrowRemote(inbound.Error ?? throw new CoreClrDebugFrameProtocolException(
                "UDBF1004",
                "Failed remote debug response omitted its error."));
        }
        try
        {
            return inbound.Payload.Deserialize<TResponse>(JsonOptions) ??
                throw new JsonException("Remote debug response payload was empty.");
        }
        catch (JsonException exception)
        {
            throw new CoreClrDebugFrameProtocolException(
                "UDBF1004",
                $"Remote debug response payload is invalid: {exception.Message}");
        }
    }

    private static void ThrowRemote(CoreClrDebugFrameError error)
    {
        if (error.Code.Length == 7 &&
            error.Code.StartsWith("UDB", StringComparison.Ordinal) &&
            error.Code.AsSpan(3).ToString().All(char.IsAsciiDigit))
        {
            throw new BreakpointProtocolException(error.Code, error.Message);
        }
        throw new CoreClrDebugRemoteException(
            error.Code,
            error.Message,
            error.Type);
    }

    private static async ValueTask<Stream> ConnectTcpAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        try
        {
            await socket.ConnectAsync(host, port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

public sealed class CoreClrDebugFrameServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        UnifiedJson.CreateOptions(writeIndented: false);
    private readonly ICoreClrDebugTransport transport;
    private readonly bool ownsTransport;
    private int disposed;

    public CoreClrDebugFrameServer(
        ICoreClrDebugTransport transport,
        bool ownsTransport = false)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.ownsTransport = ownsTransport;
    }

    public async Task ServeConnectionAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        CoreClrDebugFrame? received =
            await CoreClrDebugFrameCodec.ReadAsync(stream, cancellationToken);
        if (received is null)
        {
            return;
        }
        CoreClrDebugFrame request = received;
        if (request.Kind != CoreClrDebugFrameKind.Request)
        {
            throw new CoreClrDebugFrameProtocolException(
                "UDBF1004",
                "A debug server connection must begin with one request frame.");
        }

        using var requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        byte[] unexpectedInput = new byte[1];
        Task<int> disconnect = stream.ReadAsync(
            unexpectedInput,
            requestCancellation.Token).AsTask();
        Task<CoreClrDebugFrame> dispatch = CreateResponseAsync(
            request,
            requestCancellation.Token);
        Task first = await Task.WhenAny(dispatch, disconnect);
        if (first == disconnect)
        {
            requestCancellation.Cancel();
            try
            {
                _ = await dispatch;
            }
            catch (OperationCanceledException)
            {
            }
            return;
        }

        CoreClrDebugFrame response = await dispatch;
        await CoreClrDebugFrameCodec.WriteAsync(
            stream,
            response,
            cancellationToken);
        requestCancellation.Cancel();
        try
        {
            _ = await disconnect;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0 && ownsTransport)
        {
            transport.Dispose();
        }
    }

    private async Task<CoreClrDebugFrame> CreateResponseAsync(
        CoreClrDebugFrame request,
        CancellationToken cancellationToken)
    {
        try
        {
            JsonElement payload = await DispatchAsync(request, cancellationToken);
            return CoreClrDebugFrame.Success(
                request.RequestId,
                request.Operation,
                payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CoreClrDebugFrame.Failure(
                request.RequestId,
                request.Operation,
                MapError(exception));
        }
    }

    private async Task<JsonElement> DispatchAsync(
        CoreClrDebugFrame request,
        CancellationToken cancellationToken)
    {
        object response = request.Operation switch
        {
            CoreClrDebugFrameOperations.Reload => transport.Reload(
                Deserialize<DebugWireCompilation>(request).ToCompilationRequest()),
            CoreClrDebugFrameOperations.SetBreakpoint => transport.SetBreakpoint(
                Deserialize<SemanticBreakpoint>(request)),
            CoreClrDebugFrameOperations.RemoveBreakpoint =>
                new DebugWireBooleanResponse(transport.RemoveBreakpoint(
                    Deserialize<DebugWireSemanticIdRequest>(request).Value)),
            CoreClrDebugFrameOperations.ResolveMethod =>
                new DebugWireSemanticIdResponse(transport.ResolveVisualMethodId(
                    Deserialize<DebugWireStringRequest>(request).Value)),
            CoreClrDebugFrameOperations.Start => transport.Start(
                Deserialize<DebugWireExecutionInvocation>(request).ToInvocation()),
            CoreClrDebugFrameOperations.WaitForPause => await transport.WaitForPauseAsync(
                Deserialize<DebugWireExecutionRequest>(request).ExecutionId,
                cancellationToken),
            CoreClrDebugFrameOperations.StackTrace => GetStackTrace(request),
            CoreClrDebugFrameOperations.Locals => GetLocals(request),
            CoreClrDebugFrameOperations.Evaluate => Evaluate(request),
            CoreClrDebugFrameOperations.Pause => Pause(request),
            CoreClrDebugFrameOperations.Continue => Resume(request, null),
            CoreClrDebugFrameOperations.StepOver =>
                Resume(request, CoreClrDebugStepKind.Over),
            CoreClrDebugFrameOperations.StepInto =>
                Resume(request, CoreClrDebugStepKind.Into),
            CoreClrDebugFrameOperations.StepOut =>
                Resume(request, CoreClrDebugStepKind.Out),
            CoreClrDebugFrameOperations.WaitForCompletion =>
                await transport.WaitForCompletionAsync(
                    Deserialize<DebugWireExecutionRequest>(request).ExecutionId,
                    cancellationToken),
            _ => throw new CoreClrDebugFrameProtocolException(
                "UDBF1006",
                $"Unknown remote debug operation '{request.Operation}'."),
        };
        return JsonSerializer.SerializeToElement(
            response,
            response.GetType(),
            JsonOptions);
    }

    private DebugWireStackResponse GetStackTrace(CoreClrDebugFrame request)
    {
        DebugWirePauseRequest value = Deserialize<DebugWirePauseRequest>(request);
        return new DebugWireStackResponse(
            transport.GetStackTrace(value.GenerationId, value.PauseId));
    }

    private DebugWireLocalsResponse GetLocals(CoreClrDebugFrame request)
    {
        DebugWireFrameRequest value = Deserialize<DebugWireFrameRequest>(request);
        return new DebugWireLocalsResponse(
            transport.GetLocals(value.GenerationId, value.PauseId, value.FrameId));
    }

    private CoreClrDebugEvaluation Evaluate(CoreClrDebugFrame request)
    {
        DebugWireEvaluateRequest value = Deserialize<DebugWireEvaluateRequest>(request);
        return transport.Evaluate(
            value.GenerationId,
            value.PauseId,
            value.FrameId,
            value.Expression);
    }

    private DebugWireEmptyResponse Pause(CoreClrDebugFrame request)
    {
        DebugWireGenerationExecutionRequest value =
            Deserialize<DebugWireGenerationExecutionRequest>(request);
        transport.Pause(value.GenerationId, value.ExecutionId);
        return new DebugWireEmptyResponse();
    }

    private DebugWireEmptyResponse Resume(
        CoreClrDebugFrame request,
        CoreClrDebugStepKind? stepKind)
    {
        DebugWirePauseRequest value = Deserialize<DebugWirePauseRequest>(request);
        switch (stepKind)
        {
            case CoreClrDebugStepKind.Over:
                transport.StepOver(value.GenerationId, value.PauseId);
                break;
            case CoreClrDebugStepKind.Into:
                transport.StepInto(value.GenerationId, value.PauseId);
                break;
            case CoreClrDebugStepKind.Out:
                transport.StepOut(value.GenerationId, value.PauseId);
                break;
            case null:
                transport.Continue(value.GenerationId, value.PauseId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(stepKind));
        }
        return new DebugWireEmptyResponse();
    }

    private static T Deserialize<T>(CoreClrDebugFrame request)
    {
        try
        {
            return request.Payload.Deserialize<T>(JsonOptions) ??
                throw new JsonException("Debug request payload was empty.");
        }
        catch (JsonException exception)
        {
            throw new CoreClrDebugFrameProtocolException(
                "UDBF1004",
                $"Debug request payload is invalid: {exception.Message}");
        }
    }

    private static CoreClrDebugFrameError MapError(Exception exception) => exception switch
    {
        CoreClrDebugFrameProtocolException protocol => new(
            protocol.Code,
            protocol.Message,
            protocol.GetType().FullName!),
        BreakpointProtocolException breakpoint => new(
            breakpoint.Code,
            breakpoint.Message,
            breakpoint.GetType().FullName!),
        ExecutionInvocationException invocation => new(
            invocation.Code,
            invocation.Message,
            invocation.GetType().FullName!),
        ExecutionCompilationException compilation => new(
            "UDBF2001",
            compilation.Message,
            compilation.GetType().FullName!),
        JsonException or ArgumentException or InvalidDataException => new(
            "UDBF1004",
            exception.Message,
            exception.GetType().FullName!),
        _ => new(
            "UDBF2000",
            exception.Message,
            exception.GetType().FullName ?? exception.GetType().Name),
    };
}

public sealed class CoreClrDebugTcpServer : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly TcpListener listener;
    private readonly CoreClrDebugFrameServer frameServer;
    private readonly SemaphoreSlim connectionSlots;
    private readonly HashSet<Task> activeConnections = [];
    private bool started;
    private bool disposed;

    public CoreClrDebugTcpServer(
        ICoreClrDebugTransport transport,
        int port,
        int maximumConnections = 32,
        bool ownsTransport = false)
    {
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        if (maximumConnections is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConnections));
        }
        listener = new TcpListener(IPAddress.Loopback, port);
        frameServer = new CoreClrDebugFrameServer(transport, ownsTransport);
        connectionSlots = new SemaphoreSlim(maximumConnections, maximumConnections);
        MaximumConnections = maximumConnections;
    }

    public IPEndPoint LocalEndpoint => started
        ? (IPEndPoint)listener.LocalEndpoint
        : throw new InvalidOperationException("The remote debug TCP server is not started.");

    public int MaximumConnections { get; }

    public int ActiveConnectionCount
    {
        get
        {
            lock (gate)
            {
                return activeConnections.Count;
            }
        }
    }

    public Exception? LastConnectionError { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
        {
            return;
        }
        listener.Start();
        started = true;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Start();
        using CancellationTokenRegistration registration =
            cancellationToken.Register(listener.Stop);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                try
                {
                    await connectionSlots.WaitAsync(cancellationToken);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
                client.NoDelay = true;
                Task connection = ServeClientAsync(client, cancellationToken);
                lock (gate)
                {
                    activeConnections.Add(connection);
                }
                _ = connection.ContinueWith(
                    completed =>
                    {
                        lock (gate)
                        {
                            activeConnections.Remove(completed);
                        }
                        connectionSlots.Release();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            listener.Stop();
            Task[] pending;
            lock (gate)
            {
                pending = activeConnections.ToArray();
            }
            await Task.WhenAll(pending);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        listener.Stop();
        Task[] pending;
        lock (gate)
        {
            pending = activeConnections.ToArray();
        }
        await Task.WhenAll(pending);
        frameServer.Dispose();
        connectionSlots.Dispose();
    }

    private async Task ServeClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await frameServer.ServeConnectionAsync(
                    client.GetStream(),
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is CoreClrDebugFrameProtocolException or IOException or
                SocketException or OperationCanceledException)
            {
                LastConnectionError = exception;
            }
        }
    }
}

internal sealed record DebugWireSource(
    string LogicalPath,
    string Source,
    IdentitySidecarDocument? IdentitySidecar,
    IReadOnlyList<CSharpIdentityPin> IdentityPins);

internal sealed record DebugWireCompilationSettings(
    string TargetFramework,
    string LanguageVersion,
    IReadOnlyList<string> PreprocessorSymbols,
    string NullableContext,
    bool AllowUnsafe,
    bool CheckOverflow,
    string OptimizationLevel)
{
    public static DebugWireCompilationSettings From(CSharpCompilationSettings value) => new(
        value.TargetFramework,
        value.LanguageVersion.ToString(),
        value.PreprocessorSymbols,
        value.NullableContext.ToString(),
        value.AllowUnsafe,
        value.CheckOverflow,
        value.OptimizationLevel.ToString());

    public CSharpCompilationSettings ToSettings()
    {
        if (!Enum.TryParse(LanguageVersion, out LanguageVersion languageVersion) ||
            !Enum.TryParse(NullableContext, out NullableContextOptions nullableContext) ||
            !Enum.TryParse(OptimizationLevel, out OptimizationLevel optimizationLevel))
        {
            throw new InvalidDataException(
                "Remote compilation settings contain an unknown enum value.");
        }
        if (PreprocessorSymbols is null || PreprocessorSymbols.Count > 256)
        {
            throw new InvalidDataException(
                "Remote compilation settings exceed 256 preprocessor symbols.");
        }
        return new CSharpCompilationSettings(
            TargetFramework,
            languageVersion,
            PreprocessorSymbols,
            nullableContext,
            AllowUnsafe,
            CheckOverflow,
            optimizationLevel);
    }
}

internal sealed record DebugWireCompilation(
    string AssemblyName,
    IReadOnlyList<DebugWireSource> Sources,
    IReadOnlyList<string> ReferencePaths,
    IReadOnlyList<string> RuntimeReferencePaths,
    IReadOnlyList<MethodEffectRule> EffectRules,
    DebugWireCompilationSettings Settings,
    bool IncludeDefaultReferences,
    IReadOnlyList<string>? SourceGeneratorPaths = null)
{
    public static DebugWireCompilation From(CSharpCompilationRequest value) => new(
        value.AssemblyName,
        value.Sources.Select(static source => new DebugWireSource(
            source.LogicalPath,
            source.Source,
            source.IdentitySidecar,
            source.IdentityPins)).ToArray(),
        value.ReferencePaths,
        value.RuntimeReferencePaths,
        value.EffectCatalog.Rules,
        DebugWireCompilationSettings.From(value.Settings),
        value.IncludeDefaultReferences, value.SourceGeneratorPaths);

    public CSharpCompilationRequest ToCompilationRequest()
    {
        if (string.IsNullOrWhiteSpace(AssemblyName) ||
            Sources is not { Count: > 0 and <= 256 } ||
            Sources.Any(static source =>
                source is null ||
                string.IsNullOrWhiteSpace(source.LogicalPath) ||
                source.Source is null) ||
            ReferencePaths is null || ReferencePaths.Count > 1024 ||
            RuntimeReferencePaths is null || RuntimeReferencePaths.Count > 1024 ||
            SourceGeneratorPaths is { Count: > 1024 } ||
            EffectRules is null || EffectRules.Count > 4096 ||
            Settings is null)
        {
            throw new InvalidDataException(
                "Remote compilation request exceeds a source, reference, or effect bound.");
        }
        return new CSharpCompilationRequest(
            AssemblyName,
            Sources.Select(static source => new CSharpSourceDocument(
                source.LogicalPath,
                source.Source,
                source.IdentitySidecar,
                source.IdentityPins)),
            ReferencePaths,
            new MethodEffectCatalog(EffectRules),
            Settings.ToSettings(),
            RuntimeReferencePaths,
            IncludeDefaultReferences,
            SourceGeneratorPaths);
    }
}

internal sealed record DebugWireSemanticIdRequest(SemanticId Value);
internal sealed record DebugWireSemanticIdResponse(SemanticId Value);
internal sealed record DebugWireStringRequest(string Value);
internal sealed record DebugWireBooleanResponse(bool Value);
internal sealed record DebugWireExecutionRequest(long ExecutionId);
internal sealed record DebugWireGenerationExecutionRequest(
    SemanticId GenerationId,
    long ExecutionId);
internal sealed record DebugWirePauseRequest(
    SemanticId GenerationId,
    SemanticId PauseId);
internal sealed record DebugWireFrameRequest(
    SemanticId GenerationId,
    SemanticId PauseId,
    long FrameId);
internal sealed record DebugWireEvaluateRequest(
    SemanticId GenerationId,
    SemanticId PauseId,
    long FrameId,
    string Expression);
internal sealed record DebugWireStackResponse(
    IReadOnlyList<CoreClrDebugStackFrame> Frames);
internal sealed record DebugWireLocalsResponse(
    IReadOnlyList<CoreClrDebugValue> Locals);
internal sealed record DebugWireEmptyResponse;

internal sealed record DebugWireScriptStateOverride(
    SemanticId StateId,
    string Json);

internal sealed record DebugWireScriptAssetReceiver(
    SemanticId AssetId,
    IReadOnlyList<DebugWireScriptStateOverride> StateOverrides);

internal sealed record DebugWireExecutionInvocation(
    SemanticId MethodId,
    IReadOnlyList<string> Arguments,
    DebugWireScriptAssetReceiver? Receiver)
{
    public static DebugWireExecutionInvocation From(ExecutionInvocation value) => new(
        value.MethodId,
        value.Arguments.Select(static argument => argument.Json).ToArray(),
        value.Receiver is null
            ? null
            : new DebugWireScriptAssetReceiver(
                value.Receiver.AssetId,
                value.Receiver.StateOverrides.Select(static state =>
                    new DebugWireScriptStateOverride(state.StateId, state.Json)).ToArray()));

    public ExecutionInvocation ToInvocation()
    {
        if (MethodId.IsEmpty || Arguments is null || Arguments.Count > 256 ||
            Arguments.Any(static argument => argument is null) ||
            Receiver is not null &&
            (Receiver.AssetId.IsEmpty || Receiver.StateOverrides is null ||
             Receiver.StateOverrides.Count > 4096 ||
             Receiver.StateOverrides.Any(static state =>
                 state is null || state.StateId.IsEmpty || state.Json is null)))
        {
            throw new InvalidDataException(
                "Remote execution invocation exceeds a bound or contains an invalid identity.");
        }
        return new ExecutionInvocation(
            MethodId,
            Arguments.Select(static argument => new ExecutionArgument(argument)),
            Receiver is null
                ? null
                : new ScriptAssetReceiver(
                    Receiver.AssetId,
                    Receiver.StateOverrides.Select(static state =>
                        new ScriptStateOverride(state.StateId, state.Json))));
    }
}
