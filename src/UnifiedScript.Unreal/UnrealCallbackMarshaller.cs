using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;

namespace UnifiedScript.Unreal;

/// <summary>One handler invocation, followed by bounded encoding and exact-once owned response release.</summary>
internal static class UnrealCallbackMarshaller
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ReleaseResponse(nint owner);
    private static readonly ReleaseResponse Release = ReleaseOwned;
    private static readonly nint ReleasePointer = Marshal.GetFunctionPointerForDelegate(Release);
    private static readonly ConcurrentDictionary<nint, OwnedResponse> Responses = new();
    private static long _nextResponse;
    internal static int PendingResponses => Responses.Count;

    internal static unsafe UnrealNativeCallbackResponse Invoke(
        UnrealCallbackDescriptor descriptor, Delegate handler, in UnrealNativeCallbackRequest request, out string? error)
    {
        var response = new UnrealNativeCallbackResponse
        {
            AbiVersion = UnrealNativeCallbackAbi.Version, Size = UnrealNativeCallbackAbi.ResponseSize,
            Status = UnrealCallbackStatus.InvalidArgument,
        };
        error = null;
        if (request.AbiVersion != UnrealNativeCallbackAbi.Version || request.Size != UnrealNativeCallbackAbi.RequestSize ||
            request.Reserved != 0 || request.Token == 0 || request.Scope == 0 || request.SignatureId != descriptor.SignatureId ||
            request.ArgumentCount != descriptor.Parameters.Count || request.ArgumentCount > UnrealNativeCallbackAbi.MaximumArguments ||
            request.ArgumentCount != 0 && request.Arguments == 0 || request.ByteCount > UnrealNativeCallbackAbi.MaximumBytes ||
            request.ByteCount != 0 && request.ByteBuffer == 0)
        {
            error = "Invalid native callback frame or signature.";
            return response;
        }
        try
        {
            var cells = new ReadOnlySpan<UnrealNativeValueV3>((void*)request.Arguments, (int)request.ArgumentCount);
            var bytes = new ReadOnlySpan<byte>((void*)request.ByteBuffer, (int)request.ByteCount);
            var arguments = new object?[descriptor.Parameters.Count];
            for (int i = 0; i < arguments.Length; i++)
            {
                var parameter = descriptor.Parameters[i];
                if (parameter.Direction == UnrealParameterDirection.Output)
                {
                    arguments[i] = parameter.Type.IsValueType ? Activator.CreateInstance(parameter.Type) : null;
                    continue;
                }
                if (cells[i].Reserved16 != 0 || !UnrealNativeValueCodecV3.TryDecodeObject(parameter.Type, cells[i], bytes, bytes.Length, out arguments[i]))
                {
                    error = $"Native callback argument {i} does not match '{parameter.Type}'.";
                    return response;
                }
            }
            object? returnValue = handler.DynamicInvoke(arguments);
            // Collect values once. Growing the byte arena must never invoke the handler again.
            var values = new List<(Type Type, object? Value)>();
            if (descriptor.ReturnType != typeof(void)) values.Add((descriptor.ReturnType, returnValue));
            for (int i = 0; i < arguments.Length; i++)
                if (descriptor.Parameters[i].Direction != UnrealParameterDirection.Input)
                    values.Add((descriptor.Parameters[i].Type, arguments[i]));
            var outputs = new UnrealNativeValueV3[values.Count];
            for (int capacity = 4096; capacity <= UnrealNativeCallbackAbi.MaximumBytes; capacity *= 2)
            {
                var arena = new byte[capacity];
                int count = 0;
                bool encoded = true;
                for (int i = 0; i < values.Count; i++)
                    if (!UnrealNativeValueCodecV3.TryEncodeObject(values[i].Value, values[i].Type, arena, ref count, out outputs[i], out _))
                    {
                        encoded = false;
                        break;
                    }
                if (!encoded) continue;
                var owned = new OwnedResponse(outputs, arena.AsSpan(0, count));
                try
                {
                    long id;
                    while (true)
                    {
                        long previous = Volatile.Read(ref _nextResponse);
                        if (previous == long.MaxValue) throw new InvalidOperationException("Callback response identity exhausted.");
                        id = previous + 1;
                        if (Interlocked.CompareExchange(ref _nextResponse, id, previous) == previous) break;
                    }
                    if (!Responses.TryAdd((nint)id, owned)) throw new InvalidOperationException("Duplicate callback response identity.");
                    response.Status = UnrealCallbackStatus.Success;
                    response.ResultCount = (uint)outputs.Length;
                    response.Results = owned.Cells;
                    response.ByteBuffer = owned.Bytes;
                    response.ByteCount = (uint)count;
                    response.Owner = (nint)id;
                    response.Release = ReleasePointer;
                    return response;
                }
                catch { owned.Dispose(); throw; }
            }
            error = "Callback output is unsupported or exceeds the 16 MiB response limit.";
        }
        catch (Exception exception)
        {
            error = (exception is TargetInvocationException { InnerException: { } inner } ? inner : exception).ToString();
            response.Status = UnrealCallbackStatus.HandlerFailed;
        }
        return response;
    }

    private static void ReleaseOwned(nint id)
    {
        if (Responses.TryRemove(id, out var response)) response.Dispose();
    }

    private sealed class OwnedResponse : IDisposable
    {
        internal nint Cells { get; private set; }
        internal nint Bytes { get; private set; }
        internal unsafe OwnedResponse(ReadOnlySpan<UnrealNativeValueV3> cells, ReadOnlySpan<byte> bytes)
        {
            try
            {
                if (!cells.IsEmpty)
                {
                    Cells = Marshal.AllocHGlobal(checked(cells.Length * UnrealNativeAbiV3.ValueSize));
                    cells.CopyTo(new Span<UnrealNativeValueV3>((void*)Cells, cells.Length));
                }
                if (!bytes.IsEmpty)
                {
                    Bytes = Marshal.AllocHGlobal(bytes.Length);
                    bytes.CopyTo(new Span<byte>((void*)Bytes, bytes.Length));
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (Cells != 0) Marshal.FreeHGlobal(Cells);
            if (Bytes != 0) Marshal.FreeHGlobal(Bytes);
            Cells = Bytes = 0;
        }
    }
}
