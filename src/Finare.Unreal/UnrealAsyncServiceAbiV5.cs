using System.Runtime.InteropServices;

namespace Finare.Unreal;

public enum UnrealAsyncOperationV5 : byte
{
    Delay = 1,
    LoadObject = 2,
    LoadClass = 3,
}

public enum UnrealAsyncCompletionStatusV5 : byte
{
    Succeeded = 1,
    Canceled = 2,
    TimedOut = 3,
    GenerationEnded = 4,
    WorldTornDown = 5,
    Failed = 6,
}

[Flags]
public enum UnrealAsyncCapabilitiesV5 : ulong
{
    None = 0,
    Delay = 1UL << 0,
    AsyncAssetLoad = 1UL << 1,
    Cancellation = 1UL << 2,
    Timeout = 1UL << 3,
    GenerationOwnership = 1UL << 4,
    WorldTeardown = 1UL << 5,
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealAsyncRequestV5
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong RequestId;
    public ulong OwnerInstanceId;
    public uint OwnerGeneration;
    public UnrealAsyncOperationV5 Operation;
    public byte Reserved8;
    public ushort Flags;
    public float DurationSeconds;
    public float TimeoutSeconds;
    public nint PayloadData;
    public uint PayloadLength;
    public uint Reserved32;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealAsyncCompletionV5
{
    public ulong RequestId;
    public ulong OwnerInstanceId;
    public uint OwnerGeneration;
    public UnrealAsyncOperationV5 Operation;
    public UnrealAsyncCompletionStatusV5 Status;
    public byte HandleError;
    public byte Reserved8;
    public ulong ResultHandle;
    public uint ErrorCode;
    public uint Reserved32;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealAsyncCompletionViewV5
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong WorldHostId;
    public nint Completions;
    public uint Count;
    public uint Reserved;
    public ulong Sequence;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealAsyncTransportTableV5
{
    public uint AbiVersion;
    public uint TableSize;
    public uint RequestSize;
    public uint CompletionSize;
    public uint CompletionViewSize;
    public uint Reserved;
    public UnrealAsyncCapabilitiesV5 Capabilities;
    public nint RetainContext;
    public nint ReleaseContext;
    public nint SubmitRequest;
    public nint PumpOwner;
    public nint CancelRequest;
    public nint PollCompletions;
    public nint CancelOwner;
}

public static class UnrealAsyncServiceAbiV5
{
    public const uint Version = 5;
    public const int RequestSize = 56;
    public const int CompletionSize = 40;
    public const int CompletionViewSize = 40;
    public const int TransportTableSize = 88;

    public const UnrealAsyncCapabilitiesV5 RequiredCapabilities =
        UnrealAsyncCapabilitiesV5.Delay |
        UnrealAsyncCapabilitiesV5.AsyncAssetLoad |
        UnrealAsyncCapabilitiesV5.Cancellation |
        UnrealAsyncCapabilitiesV5.Timeout |
        UnrealAsyncCapabilitiesV5.GenerationOwnership |
        UnrealAsyncCapabilitiesV5.WorldTeardown;
}

internal interface IUnrealAsyncServiceTransport
{
    UnrealAsyncCapabilitiesV5 AsyncCapabilities { get; }

    bool Submit(
        ulong ownerInstanceId,
        uint ownerGeneration,
        ulong requestId,
        UnrealAsyncOperationV5 operation,
        float durationSeconds,
        float timeoutSeconds,
        string payload);

    bool PumpOwner(ulong ownerInstanceId, uint ownerGeneration, float deltaSeconds);

    bool Cancel(
        ulong ownerInstanceId,
        uint ownerGeneration,
        ulong requestId);

    bool Poll(
        ulong ownerInstanceId,
        uint ownerGeneration,
        out ulong worldHostId,
        out UnrealAsyncCompletionV5[] completions);

    uint CancelOwner(ulong ownerInstanceId, uint ownerGeneration);
}

internal interface IUnrealAsyncServiceProvider
{
    IUnrealAsyncServiceTransport? AsyncTransport { get; }
}

public sealed class UnrealAsyncServiceException : Exception
{
    internal UnrealAsyncServiceException(
        UnrealAsyncOperationV5 operation,
        uint errorCode,
        UnrealObjectHandleError handleError)
        : base(
            $"Unreal async operation {operation} failed with error {errorCode} " +
            $"and handle error {handleError}.")
    {
        Operation = operation;
        ErrorCode = errorCode;
        HandleError = handleError;
    }

    public UnrealAsyncOperationV5 Operation { get; }

    public uint ErrorCode { get; }

    public UnrealObjectHandleError HandleError { get; }
}

/// <summary>
/// One script-generation-owned latent scope. Native requests are keyed by
/// instance and generation, while the fallback lane keeps unit and tool hosts
/// deterministic when no Unreal service table is present.
/// </summary>
internal sealed class UnrealAsyncScope : IDisposable
{
    private static long _nextRequestId;

    private readonly object _gate = new();
    private readonly IUnrealAsyncServiceTransport? _transport;
    private readonly ulong _ownerInstanceId;
    private readonly uint _ownerGeneration;
    private readonly CancellationToken _lifetime;
    private readonly Dictionary<ulong, PendingRequest> _pending = [];
    private bool _disposed;

    public UnrealAsyncScope(
        IUnrealInvocationBridge bridge,
        ulong ownerInstanceId,
        int ownerGeneration,
        CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        if (ownerInstanceId == 0 || ownerGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerInstanceId));
        }
        _transport = (bridge as IUnrealAsyncServiceProvider)?.AsyncTransport;
        _ownerInstanceId = ownerInstanceId;
        _ownerGeneration = checked((uint)ownerGeneration);
        _lifetime = lifetime;
    }

    public Task DelayAsync(
        float seconds,
        float timeoutSeconds,
        CancellationToken cancellationToken)
    {
        ValidateDuration(seconds, nameof(seconds));
        ValidateTimeout(timeoutSeconds);
        return Submit(
            UnrealAsyncOperationV5.Delay,
            seconds,
            timeoutSeconds,
            string.Empty,
            cancellationToken).Task;
    }

    public Task<UnrealObjectHandle> LoadObjectAsync(
        string objectPath,
        float timeoutSeconds,
        CancellationToken cancellationToken) =>
        LoadAsync(
            UnrealAsyncOperationV5.LoadObject,
            objectPath,
            timeoutSeconds,
            cancellationToken);

    public Task<UnrealObjectHandle> LoadClassAsync(
        string classPath,
        float timeoutSeconds,
        CancellationToken cancellationToken) =>
        LoadAsync(
            UnrealAsyncOperationV5.LoadClass,
            classPath,
            timeoutSeconds,
            cancellationToken);

    public void Advance(float deltaSeconds)
    {
        ValidateDuration(deltaSeconds, nameof(deltaSeconds));
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_transport is not null)
        {
            if (!_transport.PumpOwner(_ownerInstanceId, _ownerGeneration, deltaSeconds) ||
                !_transport.Poll(
                    _ownerInstanceId,
                    _ownerGeneration,
                    out _,
                    out UnrealAsyncCompletionV5[] completions))
            {
                throw new ObjectDisposedException(
                    nameof(UnrealAsyncScope),
                    "The native Unreal async World service is detached.");
            }
            foreach (UnrealAsyncCompletionV5 completion in completions)
            {
                Complete(completion);
            }
            return;
        }

        List<PendingRequest> completed = [];
        List<PendingRequest> timedOut = [];
        lock (_gate)
        {
            foreach (PendingRequest pending in _pending.Values)
            {
                if (pending.Operation != UnrealAsyncOperationV5.Delay)
                {
                    continue;
                }
                pending.RemainingSeconds -= deltaSeconds;
                if (pending.RemainingSeconds <= 0.0f)
                {
                    completed.Add(pending);
                    continue;
                }
                if (pending.TimeoutSeconds > 0.0f)
                {
                    pending.TimeoutSeconds -= deltaSeconds;
                    if (pending.TimeoutSeconds <= 0.0f)
                    {
                        timedOut.Add(pending);
                    }
                }
            }
            foreach (PendingRequest pending in completed)
            {
                _pending.Remove(pending.RequestId);
            }
            foreach (PendingRequest pending in timedOut)
            {
                _pending.Remove(pending.RequestId);
            }
        }
        foreach (PendingRequest pending in completed)
        {
            pending.Succeed(default);
        }
        foreach (PendingRequest pending in timedOut)
        {
            pending.Timeout();
        }
    }

    public void Dispose()
    {
        List<PendingRequest> pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            pending = [.. _pending.Values];
            _pending.Clear();
        }
        _transport?.CancelOwner(_ownerInstanceId, _ownerGeneration);
        foreach (PendingRequest request in pending)
        {
            request.Cancel(_lifetime);
        }
    }

    private Task<UnrealObjectHandle> LoadAsync(
        UnrealAsyncOperationV5 operation,
        string path,
        float timeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateTimeout(timeoutSeconds);
        if (_transport is null ||
            (_transport.AsyncCapabilities & UnrealAsyncCapabilitiesV5.AsyncAssetLoad) == 0)
        {
            throw new NotSupportedException(
                "The current Unreal host does not expose async asset loading.");
        }
        return Submit(operation, 0.0f, timeoutSeconds, path, cancellationToken).Task;
    }

    private PendingRequest Submit(
        UnrealAsyncOperationV5 operation,
        float durationSeconds,
        float timeoutSeconds,
        string payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _lifetime.ThrowIfCancellationRequested();
        ulong requestId = NextRequestId();
        var pending = new PendingRequest(
            requestId,
            operation,
            durationSeconds,
            timeoutSeconds);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending.Add(requestId, pending);
        }

        if (_transport is not null && !_transport.Submit(
                _ownerInstanceId,
                _ownerGeneration,
                requestId,
                operation,
                durationSeconds,
                timeoutSeconds,
                payload))
        {
            lock (_gate)
            {
                _pending.Remove(requestId);
            }
            throw new InvalidOperationException(
                "The native Unreal async service rejected the request.");
        }

        pending.RegisterCancellation(
            cancellationToken,
            () => Cancel(requestId, cancellationToken));
        return pending;
    }

    private void Cancel(ulong requestId, CancellationToken cancellationToken)
    {
        PendingRequest? pending;
        lock (_gate)
        {
            if (!_pending.Remove(requestId, out pending))
            {
                return;
            }
        }
        _transport?.Cancel(_ownerInstanceId, _ownerGeneration, requestId);
        pending.Cancel(cancellationToken);
    }

    private void Complete(UnrealAsyncCompletionV5 completion)
    {
        if (completion.OwnerInstanceId != _ownerInstanceId ||
            completion.OwnerGeneration != _ownerGeneration ||
            completion.Reserved8 != 0 || completion.Reserved32 != 0 ||
            !Enum.IsDefined(completion.Operation) ||
            !Enum.IsDefined(completion.Status))
        {
            throw new InvalidOperationException(
                "The native Unreal async service returned an invalid completion row.");
        }
        PendingRequest? pending;
        lock (_gate)
        {
            if (!_pending.Remove(completion.RequestId, out pending))
            {
                return;
            }
        }
        if (pending.Operation != completion.Operation)
        {
            pending.Fail(new InvalidOperationException(
                "The native Unreal async completion changed operation kind."));
            return;
        }

        switch (completion.Status)
        {
            case UnrealAsyncCompletionStatusV5.Succeeded:
                var handle = new UnrealObjectHandle(completion.ResultHandle);
                if (completion.Operation != UnrealAsyncOperationV5.Delay &&
                    !handle.IsWellFormed)
                {
                    pending.Fail(new InvalidOperationException(
                        "A successful async asset request returned an invalid handle."));
                }
                else
                {
                    pending.Succeed(handle);
                }
                break;
            case UnrealAsyncCompletionStatusV5.Canceled:
            case UnrealAsyncCompletionStatusV5.GenerationEnded:
            case UnrealAsyncCompletionStatusV5.WorldTornDown:
                pending.Cancel(_lifetime);
                break;
            case UnrealAsyncCompletionStatusV5.TimedOut:
                pending.Timeout();
                break;
            case UnrealAsyncCompletionStatusV5.Failed:
                pending.Fail(new UnrealAsyncServiceException(
                    completion.Operation,
                    completion.ErrorCode,
                    Enum.IsDefined((UnrealObjectHandleError)completion.HandleError)
                        ? (UnrealObjectHandleError)completion.HandleError
                        : UnrealObjectHandleError.InvalidHandle));
                break;
        }
    }

    private static ulong NextRequestId()
    {
        while (true)
        {
            long value = Interlocked.Increment(ref _nextRequestId);
            if (value != 0)
            {
                return unchecked((ulong)value);
            }
        }
    }

    private static void ValidateDuration(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0.0f)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateTimeout(float value)
    {
        if (!float.IsFinite(value) || value < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private sealed class PendingRequest(
        ulong requestId,
        UnrealAsyncOperationV5 operation,
        float remainingSeconds,
        float timeoutSeconds)
    {
        private readonly TaskCompletionSource<UnrealObjectHandle> _completion = new();
        private CancellationTokenRegistration _registration;

        public ulong RequestId { get; } = requestId;

        public UnrealAsyncOperationV5 Operation { get; } = operation;

        public float RemainingSeconds { get; set; } = remainingSeconds;

        public float TimeoutSeconds { get; set; } = timeoutSeconds;

        public Task<UnrealObjectHandle> Task => _completion.Task;

        public void RegisterCancellation(CancellationToken token, Action cancel)
        {
            if (token.CanBeCanceled)
            {
                CancellationTokenRegistration registration = token.Register(cancel);
                _registration = registration;
                if (_completion.Task.IsCompleted)
                {
                    registration.Dispose();
                }
            }
        }

        public void Succeed(UnrealObjectHandle handle)
        {
            _registration.Dispose();
            _completion.TrySetResult(handle);
        }

        public void Cancel(CancellationToken token)
        {
            _registration.Dispose();
            _completion.TrySetCanceled(token.IsCancellationRequested
                ? token
                : new CancellationToken(canceled: true));
        }

        public void Timeout()
        {
            _registration.Dispose();
            _completion.TrySetException(new TimeoutException(
                $"Unreal async operation {Operation} timed out."));
        }

        public void Fail(Exception exception)
        {
            _registration.Dispose();
            _completion.TrySetException(exception);
        }
    }
}
