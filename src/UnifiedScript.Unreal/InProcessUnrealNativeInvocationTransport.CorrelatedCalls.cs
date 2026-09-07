using System.Runtime.InteropServices;

namespace UnifiedScript.Unreal;

internal interface IUnrealNativeCallFrameScope : IUnrealNativeCallFrameTransportV3, IDisposable { }
internal interface IUnrealNativeCallFrameScopeFactory
{
    IUnrealNativeCallFrameScope? BeginCallFrameInvocation();
}

public sealed partial class InProcessUnrealNativeInvocationTransport : IUnrealNativeCallFrameScopeFactory
{
    private nint _invokeCorrelatedByIdV3, _releaseCorrelatedResultV3;
    private static long _nextCallFrameIdentity;

    private void LoadCorrelatedCalls(nint pointer, UnrealNativeTransportTableV3 header)
    {
        if ((header.Capabilities & UnrealNativeCapabilitiesV3.CorrelatedResults) == 0) return;
        if (header.TableSize < UnrealNativeAbiV3.CorrelatedTransportTableSize)
            throw new InvalidOperationException("Native correlated-result capability has no compatible table suffix.");
        var table = Marshal.PtrToStructure<UnrealNativeCorrelatedTransportTableV3>(pointer);
        if (table.InvokeCorrelatedById == 0 || table.ReleaseCorrelatedResult == 0)
            throw new InvalidOperationException("Native correlated-result entrypoints are incomplete.");
        _invokeCorrelatedByIdV3 = table.InvokeCorrelatedById;
        _releaseCorrelatedResultV3 = table.ReleaseCorrelatedResult;
    }

    IUnrealNativeCallFrameScope? IUnrealNativeCallFrameScopeFactory.BeginCallFrameInvocation()
    {
        if (_invokeCorrelatedByIdV3 == 0) return null;
        long id;
        while (true)
        {
            long previous = Volatile.Read(ref _nextCallFrameIdentity);
            if (previous == long.MaxValue) throw new InvalidOperationException("Native invocation identity exhausted.");
            id = previous + 1;
            if (Interlocked.CompareExchange(ref _nextCallFrameIdentity, id, previous) == previous) break;
        }
        var lifetime = EnterNativeInvocation();
        try { return new CorrelatedInvocation(this, (ulong)id, lifetime); }
        catch { lifetime.Dispose(); throw; }
    }

    private sealed class CorrelatedInvocation(InProcessUnrealNativeInvocationTransport owner,
        ulong id, NativeInvocationLease lifetime) : IUnrealNativeCallFrameScope
    {
        private readonly int _threadId = Environment.CurrentManagedThreadId;
        private bool _pending, _disposed;
        public UnrealNativeCapabilitiesV3 Capabilities => owner.Capabilities;

        public UnrealNativeCallOutcomeV3 ExecuteById(UnrealNativeOperationV3 operation, UnrealObjectHandle target,
            ulong memberId, ReadOnlySpan<UnrealNativeValueV3> arguments, Span<UnrealNativeValueV3> results,
            Span<byte> byteBuffer, int inputByteCount)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Environment.CurrentManagedThreadId != _threadId ||
                operation is not (UnrealNativeOperationV3.Invoke or UnrealNativeOperationV3.InvokeParent))
                throw new InvalidOperationException("A synchronous correlated invocation must stay on its originating thread and use Invoke or InvokeParent.");
            // Invalid retry frames can be rejected before the native cache is inspected.
            // Preserve cleanup after a retry or an uncertain native response.
            bool hadPending = _pending;
            _pending = true;
            var result = owner.ExecuteByIdCore(operation, target, memberId, arguments, results, byteBuffer, inputByteCount, id);
            _pending = hadPending || result.Status == UnrealNativeCallStatusV3.BufferTooSmall;
            return result;
        }

        public unsafe void Dispose()
        {
            if (_disposed) return;
            if (Environment.CurrentManagedThreadId != _threadId)
                throw new InvalidOperationException("Dispose the invocation on its originating thread.");
            _disposed = true;
            try
            {
                if (_pending)
                    ((delegate* unmanaged[Cdecl]<nint, ulong, void>)owner._releaseCorrelatedResultV3)(owner._context, id);
            }
            finally { lifetime.Dispose(); }
        }
    }
}
