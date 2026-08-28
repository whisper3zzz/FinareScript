using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.CSharp;
using UnifiedScript.Runtime;

namespace UnifiedScript.Tests;

public sealed class CoreClrDebugRemoteTransportTests
{
    private const string Path = "Tests/Debug/RemoteRules.cs";
    private const string Source = """
        using UnifiedScript;

        namespace Tests.Debug;

        public static class RemoteRules
        {
            [Visual]
            public static int Run(int value)
            {
                int offset = 1;
                int result = value + offset;
                return result;
            }
        }
        """;

    private const string LongRunningSource = """
        using UnifiedScript;

        namespace Tests.Debug;

        public static class RemoteLoop
        {
            [Visual]
            public static int Run(int count)
            {
                int value = 0;
                while (value < count)
                {
                    value = value + 1;
                }

                return value;
            }
        }
        """;

    [Fact]
    public async Task FrameCodecRoundTripsVersionedRequestAndRejectsMismatchedIdentity()
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new { value = 7 });
        CoreClrDebugFrame expected = CoreClrDebugFrame.Request(
            42,
            CoreClrDebugFrameOperations.Evaluate,
            payload);
        await using var stream = new MemoryStream();

        await CoreClrDebugFrameCodec.WriteAsync(stream, expected);
        byte[] bytes = stream.ToArray();
        Assert.True(bytes.Length > CoreClrDebugFrameCodec.HeaderSize);
        Assert.Equal("USDB", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(
            CoreClrDebugFrameCodec.ProtocolVersion,
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)));
        stream.Position = 0;
        CoreClrDebugFrame actual = Assert.IsType<CoreClrDebugFrame>(
            await CoreClrDebugFrameCodec.ReadAsync(stream));

        Assert.Equal(CoreClrDebugFrame.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.Equal(CoreClrDebugFrameKind.Request, actual.Kind);
        Assert.Equal(42, actual.RequestId);
        Assert.Equal(CoreClrDebugFrameOperations.Evaluate, actual.Operation);
        Assert.Equal(7, actual.Payload.GetProperty("value").GetInt32());

        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16, 8), 43);
        await using var mismatched = new MemoryStream(bytes);
        CoreClrDebugFrameProtocolException exception = await Assert.ThrowsAsync<
            CoreClrDebugFrameProtocolException>(async () =>
                _ = await CoreClrDebugFrameCodec.ReadAsync(mismatched));
        Assert.Equal("UDBF1005", exception.Code);
    }

    [Fact]
    public async Task FrameCodecRejectsOversizedPayloadBeforeAllocatingIt()
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new { value = 7 });
        await using var valid = new MemoryStream();
        await CoreClrDebugFrameCodec.WriteAsync(valid, CoreClrDebugFrame.Request(
            1,
            CoreClrDebugFrameOperations.Pause,
            payload));
        byte[] header = valid.ToArray()[..CoreClrDebugFrameCodec.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(8, 4),
            CoreClrDebugFrameCodec.MaximumPayloadBytes + 1u);
        await using var oversized = new MemoryStream(header);

        CoreClrDebugFrameProtocolException exception = await Assert.ThrowsAsync<
            CoreClrDebugFrameProtocolException>(async () =>
                _ = await CoreClrDebugFrameCodec.ReadAsync(oversized));

        Assert.Equal("UDBF1003", exception.Code);
    }

    [Fact]
    public async Task TcpTransportReloadsBreaksInspectsContinuesAndMapsProtocolErrors()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, Path);
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        HirVariableDeclarationStatement resultDeclaration = analysis.Hir!
            .DescendantsAndSelf()
            .OfType<HirVariableDeclarationStatement>()
            .Single(statement => statement.Name == "result");
        await using var server = new CoreClrDebugTcpServer(
            new CoreClrDebugLoopbackTransport(),
            port: 0,
            ownsTransport: true);
        server.Start();
        using var serverCancellation = new CancellationTokenSource();
        Task serverTask = server.RunAsync(serverCancellation.Token);

        try
        {
            using var remote = new CoreClrDebugRemoteTransport(
                "127.0.0.1",
                server.LocalEndpoint.Port);
            CoreClrDebugReloadResult reload = remote.Reload(request);
            BreakpointBindingSnapshot bindings = remote.SetBreakpoint(
                new SemanticBreakpoint(
                    SemanticId.FromKey("remote-result-breakpoint"),
                    resultDeclaration.Id));
            Assert.Equal(
                BreakpointBindingState.Bound,
                Assert.Single(bindings.Bindings).State);
            SemanticId methodId = remote.ResolveVisualMethodId(
                "Tests.Debug.RemoteRules.Run");
            CoreClrDebugExecution execution = remote.Start(new ExecutionInvocation(
                methodId,
                [new ExecutionArgument("41")]));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            CoreClrDebugPause pause = await remote.WaitForPauseAsync(
                execution.ExecutionId,
                timeout.Token);
            CoreClrDebugStackFrame frame = Assert.Single(remote.GetStackTrace(
                reload.Generation.GenerationId,
                pause.PauseId));
            IReadOnlyList<CoreClrDebugValue> locals = remote.GetLocals(
                reload.Generation.GenerationId,
                pause.PauseId,
                frame.FrameId);

            Assert.Equal(CoreClrDebugPauseReason.Breakpoint, pause.Reason);
            Assert.Equal(resultDeclaration.Id, pause.NodeId);
            Assert.Equal("41", locals.Single(local => local.Name == "value").Display);
            remote.Continue(reload.Generation.GenerationId, pause.PauseId);
            ExecutionResult result = await remote.WaitForCompletionAsync(
                execution.ExecutionId,
                timeout.Token);
            Assert.Equal("42", result.ReturnValue!.Json);

            CoreClrDebugReloadResult next = remote.Reload(request);
            Assert.Equal(2, next.Generation.Number);
            BreakpointProtocolException stale = Assert.Throws<BreakpointProtocolException>(() =>
                remote.Pause(reload.Generation.GenerationId, execution.ExecutionId));
            Assert.Equal("UDB1002", stale.Code);
            Assert.Null(server.LastConnectionError);
        }
        finally
        {
            serverCancellation.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task LongPollConnectionDoesNotBlockIndependentPauseAndContinueRequests()
    {
        await using var server = new CoreClrDebugTcpServer(
            new CoreClrDebugLoopbackTransport(),
            port: 0,
            maximumConnections: 2,
            ownsTransport: true);
        server.Start();
        using var serverCancellation = new CancellationTokenSource();
        Task serverTask = server.RunAsync(serverCancellation.Token);

        try
        {
            using var remote = new CoreClrDebugRemoteTransport(
                "127.0.0.1",
                server.LocalEndpoint.Port);
            CoreClrDebugReloadResult reload = remote.Reload(
                CSharpCompilationRequest.Single(
                    LongRunningSource,
                    "Tests/Debug/RemoteLoop.cs"));
            SemanticId methodId = remote.ResolveVisualMethodId(
                "Tests.Debug.RemoteLoop.Run");
            CoreClrDebugExecution execution = remote.Start(new ExecutionInvocation(
                methodId,
                [new ExecutionArgument("100000")]));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Assert.True(SpinWait.SpinUntil(
                () => server.ActiveConnectionCount == 0,
                TimeSpan.FromSeconds(2)));

            Task<CoreClrDebugPause> longPoll = remote.WaitForPauseAsync(
                execution.ExecutionId,
                timeout.Token);
            Assert.True(SpinWait.SpinUntil(
                () => server.ActiveConnectionCount == 1,
                TimeSpan.FromSeconds(2)));
            remote.Pause(reload.Generation.GenerationId, execution.ExecutionId);
            CoreClrDebugPause pause = await longPoll;

            Assert.Equal(CoreClrDebugPauseReason.Pause, pause.Reason);
            Assert.False(pause.NodeId.IsEmpty);
            remote.Continue(reload.Generation.GenerationId, pause.PauseId);
            ExecutionResult result = await remote.WaitForCompletionAsync(
                execution.ExecutionId,
                timeout.Token);
            Assert.Equal("100000", result.ReturnValue!.Json);
            Assert.Null(server.LastConnectionError);
        }
        finally
        {
            serverCancellation.Cancel();
            await serverTask;
        }
    }
}
