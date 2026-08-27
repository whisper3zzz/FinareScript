using System.Runtime.InteropServices;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealAsyncServiceAbiV5Tests
{
    [Fact]
    public void AbiLayoutsAreStableAndAppendOnly()
    {
        Assert.Equal(UnrealAsyncServiceAbiV5.RequestSize, Marshal.SizeOf<UnrealAsyncRequestV5>());
        Assert.Equal(UnrealAsyncServiceAbiV5.CompletionSize, Marshal.SizeOf<UnrealAsyncCompletionV5>());
        Assert.Equal(UnrealAsyncServiceAbiV5.CompletionViewSize, Marshal.SizeOf<UnrealAsyncCompletionViewV5>());
        Assert.Equal(UnrealAsyncServiceAbiV5.TransportTableSize, Marshal.SizeOf<UnrealAsyncTransportTableV5>());
        Assert.Equal(40, OffsetOf<UnrealAsyncRequestV5>(nameof(UnrealAsyncRequestV5.PayloadData)));
        Assert.Equal(24, OffsetOf<UnrealAsyncCompletionV5>(nameof(UnrealAsyncCompletionV5.ResultHandle)));
        Assert.Equal(16, OffsetOf<UnrealAsyncCompletionViewV5>(nameof(UnrealAsyncCompletionViewV5.Completions)));
        Assert.Equal(24, OffsetOf<UnrealAsyncTransportTableV5>(nameof(UnrealAsyncTransportTableV5.Capabilities)));
    }

    [Fact]
    public async Task ScopeCompletesDelayAndAssetLoadThenCancelsItsGeneration()
    {
        var transport = new FixtureAsyncTransport(worldHostId: 73);
        var bridge = new FixtureBridge(transport);
        using var lifetime = new CancellationTokenSource();
        using var scope = new UnrealAsyncScope(bridge, 41, 3, lifetime.Token);

        Task delay = scope.DelayAsync(0.5f, 0.0f, CancellationToken.None);
        Task<UnrealObjectHandle> load = scope.LoadObjectAsync(
            "/Game/Fixture.Asset",
            1.0f,
            CancellationToken.None);

        scope.Advance(0.25f);
        Assert.False(delay.IsCompleted);
        Assert.Equal(UnrealObjectHandle.Create(7, 9), await load);

        scope.Advance(0.25f);
        await delay;

        Task abandoned = scope.DelayAsync(10.0f, 0.0f, CancellationToken.None);
        scope.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.Equal((ulong)41, transport.LastCanceledOwner);
        Assert.Equal((uint)3, transport.LastCanceledGeneration);
        Assert.Empty(transport.Pending);
    }

    [Fact]
    public async Task ScopeMapsTimeoutAndCallerCancellationWithoutStaleCompletion()
    {
        var transport = new FixtureAsyncTransport(worldHostId: 91);
        var bridge = new FixtureBridge(transport);
        using var lifetime = new CancellationTokenSource();
        using var scope = new UnrealAsyncScope(bridge, 52, 4, lifetime.Token);

        Task timeout = scope.DelayAsync(5.0f, 0.5f, CancellationToken.None);
        scope.Advance(0.5f);
        await Assert.ThrowsAsync<TimeoutException>(() => timeout);

        using var requestCancellation = new CancellationTokenSource();
        Task canceled = scope.DelayAsync(5.0f, 0.0f, requestCancellation.Token);
        requestCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        scope.Advance(0.0f);
        Assert.Equal(1, transport.CancelRequestCount);
    }

    private static int OffsetOf<T>(string fieldName) where T : struct =>
        checked((int)Marshal.OffsetOf<T>(fieldName));

    private sealed class FixtureBridge(IUnrealAsyncServiceTransport transport)
        : IUnrealInvocationBridge,
          IUnrealAsyncServiceProvider
    {
        IUnrealAsyncServiceTransport? IUnrealAsyncServiceProvider.AsyncTransport => transport;

        public T ReadProperty<T>(UnrealObjectHandle target, string propertyPath) =>
            throw new NotSupportedException();

        public void WriteProperty<T>(UnrealObjectHandle target, string propertyPath, T value) =>
            throw new NotSupportedException();

        public T Invoke<T>(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<object?> arguments) => throw new NotSupportedException();

        public void Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<object?> arguments) => throw new NotSupportedException();
    }

    private sealed class FixtureAsyncTransport(ulong worldHostId)
        : IUnrealAsyncServiceTransport
    {
        private readonly List<UnrealAsyncCompletionV5> _completions = [];

        public Dictionary<ulong, PendingFixture> Pending { get; } = [];

        public UnrealAsyncCapabilitiesV5 AsyncCapabilities =>
            UnrealAsyncServiceAbiV5.RequiredCapabilities;

        public ulong LastCanceledOwner { get; private set; }

        public uint LastCanceledGeneration { get; private set; }

        public int CancelRequestCount { get; private set; }

        public bool Submit(
            ulong ownerInstanceId,
            uint ownerGeneration,
            ulong requestId,
            UnrealAsyncOperationV5 operation,
            float durationSeconds,
            float timeoutSeconds,
            string payload)
        {
            Pending.Add(
                requestId,
                new PendingFixture(
                    ownerInstanceId,
                    ownerGeneration,
                    requestId,
                    operation,
                    durationSeconds,
                    timeoutSeconds,
                    payload));
            return true;
        }

        public bool PumpOwner(
            ulong ownerInstanceId,
            uint ownerGeneration,
            float deltaSeconds)
        {
            List<PendingFixture> finished = [];
            foreach (PendingFixture pending in Pending.Values)
            {
                if (pending.OwnerInstanceId != ownerInstanceId ||
                    pending.OwnerGeneration != ownerGeneration)
                {
                    continue;
                }
                if (pending.Operation is UnrealAsyncOperationV5.LoadObject or
                    UnrealAsyncOperationV5.LoadClass)
                {
                    finished.Add(pending with
                    {
                        Status = UnrealAsyncCompletionStatusV5.Succeeded,
                    });
                    continue;
                }
                pending.RemainingSeconds -= deltaSeconds;
                if (pending.RemainingSeconds <= 0.0f)
                {
                    finished.Add(pending with
                    {
                        Status = UnrealAsyncCompletionStatusV5.Succeeded,
                    });
                    continue;
                }
                if (pending.TimeoutSeconds > 0.0f)
                {
                    pending.TimeoutSeconds -= deltaSeconds;
                    if (pending.TimeoutSeconds <= 0.0f)
                    {
                        finished.Add(pending with
                        {
                            Status = UnrealAsyncCompletionStatusV5.TimedOut,
                        });
                    }
                }
            }
            foreach (PendingFixture pending in finished)
            {
                Pending.Remove(pending.RequestId);
                _completions.Add(new UnrealAsyncCompletionV5
                {
                    RequestId = pending.RequestId,
                    OwnerInstanceId = pending.OwnerInstanceId,
                    OwnerGeneration = pending.OwnerGeneration,
                    Operation = pending.Operation,
                    Status = pending.Status,
                    ResultHandle = pending.Operation is UnrealAsyncOperationV5.LoadObject or
                        UnrealAsyncOperationV5.LoadClass
                            ? UnrealObjectHandle.Create(7, 9).Value
                            : 0,
                });
            }
            return true;
        }

        public bool Cancel(
            ulong ownerInstanceId,
            uint ownerGeneration,
            ulong requestId)
        {
            ++CancelRequestCount;
            return Pending.Remove(requestId);
        }

        public bool Poll(
            ulong ownerInstanceId,
            uint ownerGeneration,
            out ulong returnedWorldHostId,
            out UnrealAsyncCompletionV5[] completions)
        {
            returnedWorldHostId = worldHostId;
            completions = [.. _completions.Where(completion =>
                completion.OwnerInstanceId == ownerInstanceId &&
                completion.OwnerGeneration == ownerGeneration)];
            _completions.RemoveAll(completion =>
                completion.OwnerInstanceId == ownerInstanceId &&
                completion.OwnerGeneration == ownerGeneration);
            return true;
        }

        public uint CancelOwner(ulong ownerInstanceId, uint ownerGeneration)
        {
            LastCanceledOwner = ownerInstanceId;
            LastCanceledGeneration = ownerGeneration;
            ulong[] owned = [.. Pending.Values
                .Where(pending =>
                    pending.OwnerInstanceId == ownerInstanceId &&
                    pending.OwnerGeneration == ownerGeneration)
                .Select(static pending => pending.RequestId)];
            foreach (ulong requestId in owned)
            {
                Pending.Remove(requestId);
            }
            return checked((uint)owned.Length);
        }
    }

    private sealed record PendingFixture(
        ulong OwnerInstanceId,
        uint OwnerGeneration,
        ulong RequestId,
        UnrealAsyncOperationV5 Operation,
        float InitialRemainingSeconds,
        float InitialTimeoutSeconds,
        string Payload)
    {
        public float RemainingSeconds { get; set; } = InitialRemainingSeconds;

        public float TimeoutSeconds { get; set; } = InitialTimeoutSeconds;

        public UnrealAsyncCompletionStatusV5 Status { get; init; }
    }
}
