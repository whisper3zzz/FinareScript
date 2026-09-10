using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Finare.Unreal;

public sealed partial class InProcessUnrealNativeInvocationTransport : IUnrealNativeCallbackTransport
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate UnrealCallbackStatus NativeOpenCallbackScope(nint context, out ulong scope);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate UnrealCallbackStatus NativeCloseCallbackScope(nint context, ulong scope);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void NativeCallback(nint userData, nint request, nint response);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal unsafe delegate UnrealCallbackStatus NativeRegisterCallback(nint context, ulong scope,
        byte* path, uint length, NativeCallback callback, nint userData, out ulong token);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate UnrealCallbackStatus NativeReleaseCallback(nint context, ulong scope, ulong token);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTransportTableV2Callbacks
    {
        public NativeTransportTableV2NativeObjects Base;
        public nint CallbackTransport;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CallbackHeader { public uint AbiVersion, TableSize, RequestSize, ResponseSize; }

    private NativeOpenCallbackScope? _openCallbackScope;
    private NativeCloseCallbackScope? _closeCallbackScope;
    private NativeRegisterCallback? _registerCallback;
    private NativeReleaseCallback? _releaseCallback;
    private readonly HashSet<ulong> _callbackScopes = [];
    private readonly Dictionary<ulong, CallbackBox> _callbacks = [];
    private string? _lastCallbackError;
    private static readonly NativeCallback CallbackThunk = ReceiveCallback;
    // Never use a recycled GCHandle as a native callback identity. Even a stale native
    // dispatch must not dereference freed managed state or reach a different handler.
    private static readonly ConcurrentDictionary<nint, CallbackBox> CallbackTargets = new();
    private static long _nextCallbackIdentity;

    public bool SupportsCallbacks => !_disposed && _registerCallback is not null;
    public string? LastCallbackError => Volatile.Read(ref _lastCallbackError);
    internal static int ActiveCallbackTargets => CallbackTargets.Count;

    private void LoadCallbacks(nint tablePointer, NativeTransportTableV1 parent)
    {
        if (parent.AbiVersion != 2 || parent.TableSize < Marshal.SizeOf<NativeTransportTableV2Callbacks>()) return;
        var extension = Marshal.PtrToStructure<NativeTransportTableV2Callbacks>(tablePointer);
        if (extension.CallbackTransport == 0) return;
        var header = Marshal.PtrToStructure<CallbackHeader>(extension.CallbackTransport);
        if (header.AbiVersion != UnrealNativeCallbackAbi.Version || header.TableSize < UnrealNativeCallbackAbi.TableSize ||
            header.RequestSize != UnrealNativeCallbackAbi.RequestSize || header.ResponseSize != UnrealNativeCallbackAbi.ResponseSize)
            throw new InvalidOperationException("The native callback ABI has an incompatible layout.");
        var table = Marshal.PtrToStructure<UnrealNativeCallbackTransportTable>(extension.CallbackTransport);
        if (table.RetainContext != parent.RetainContext || table.ReleaseContext != parent.ReleaseContext ||
            table.OpenScope == 0 || table.CloseScope == 0 || table.RegisterCallback == 0 || table.ReleaseCallback == 0)
            throw new InvalidOperationException("The native callback table is incomplete or belongs to another context ABI.");
        _openCallbackScope = Marshal.GetDelegateForFunctionPointer<NativeOpenCallbackScope>(table.OpenScope);
        _closeCallbackScope = Marshal.GetDelegateForFunctionPointer<NativeCloseCallbackScope>(table.CloseScope);
        _registerCallback = Marshal.GetDelegateForFunctionPointer<NativeRegisterCallback>(table.RegisterCallback);
        _releaseCallback = Marshal.GetDelegateForFunctionPointer<NativeReleaseCallback>(table.ReleaseCallback);
    }

    public UnrealCallbackStatus OpenCallbackScope(out ulong scope)
    {
        lock (_lifetimeGate)
        {
            scope = 0;
            if (_disposed || _openCallbackScope is null) return UnrealCallbackStatus.Unavailable;
            var status = _openCallbackScope(_context, out scope);
            if (status == UnrealCallbackStatus.Success && (scope == 0 || !_callbackScopes.Add(scope)))
                throw new InvalidOperationException("Native callback transport returned an invalid scope identity.");
            return status;
        }
    }

    public unsafe UnrealCallbackStatus RegisterCallback(ulong scope, UnrealCallbackDescriptor descriptor, Delegate handler, out ulong token)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lifetimeGate)
        {
            token = 0;
            if (_disposed || _registerCallback is null) return UnrealCallbackStatus.Unavailable;
            if (!_callbackScopes.Contains(scope)) return UnrealCallbackStatus.InvalidScope;
            descriptor = descriptor with { Parameters = descriptor.Parameters.ToArray() };
            ValidateCallbackHandler(descriptor, handler);
            byte[] path = Encoding.UTF8.GetBytes(descriptor.SignaturePath);
            if (path.Length is 0 or > 4096 || path.Contains((byte)0)) return UnrealCallbackStatus.InvalidArgument;
            long id;
            while (true)
            {
                long previous = Volatile.Read(ref _nextCallbackIdentity);
                if (previous == long.MaxValue) return UnrealCallbackStatus.Exhausted;
                id = previous + 1;
                if (Interlocked.CompareExchange(ref _nextCallbackIdentity, id, previous) == previous) break;
            }
            var box = new CallbackBox(this, (nint)id, scope, descriptor, handler);
            if (!CallbackTargets.TryAdd(box.Identity, box)) throw new InvalidOperationException("Duplicate callback identity.");
            bool published = false;
            try
            {
                fixed (byte* bytes = path)
                {
                    var status = _registerCallback(_context, scope, bytes, (uint)path.Length, CallbackThunk, box.Identity, out token);
                    if (status != UnrealCallbackStatus.Success) return status;
                }
                if (token == 0 || _callbacks.ContainsKey(token))
                    throw new InvalidOperationException("Native callback transport returned an invalid token identity.");
                box.Token = token;
                _callbacks.Add(token, box);
                published = true;
                return UnrealCallbackStatus.Success;
            }
            finally
            {
                if (!published) CallbackTargets.TryRemove(box.Identity, out _);
            }
        }
    }

    public UnrealCallbackStatus ReleaseCallback(ulong scope, ulong token)
    {
        lock (_lifetimeGate)
        {
            if (_disposed || _releaseCallback is null) return UnrealCallbackStatus.Unavailable;
            if (!_callbackScopes.Contains(scope)) return UnrealCallbackStatus.InvalidScope;
            if (!_callbacks.TryGetValue(token, out var box)) return UnrealCallbackStatus.InvalidHandle;
            if (box.Scope != scope) return UnrealCallbackStatus.WrongOwner;
            var status = _releaseCallback(_context, scope, token);
            if (status is UnrealCallbackStatus.Success or UnrealCallbackStatus.InvalidHandle or UnrealCallbackStatus.InvalidScope or UnrealCallbackStatus.Unavailable)
                ForgetCallback(box);
            return status;
        }
    }

    public UnrealCallbackStatus CloseCallbackScope(ulong scope)
    {
        lock (_lifetimeGate)
        {
            if (_disposed || _closeCallbackScope is null) return UnrealCallbackStatus.Unavailable;
            if (!_callbackScopes.Contains(scope)) return UnrealCallbackStatus.InvalidScope;
            var status = _closeCallbackScope(_context, scope);
            if (status is UnrealCallbackStatus.Success or UnrealCallbackStatus.InvalidScope or UnrealCallbackStatus.Unavailable)
            {
                // Native unbinding has completed before the final strong managed root is removed.
                foreach (var box in _callbacks.Values.Where(b => b.Scope == scope).ToArray()) ForgetCallback(box);
                _callbackScopes.Remove(scope);
            }
            return status;
        }
    }

    private void ForgetCallback(CallbackBox box)
    {
        CallbackTargets.TryRemove(box.Identity, out _);
        _callbacks.Remove(box.Token);
    }

    private void CloseCallbackScopes()
    {
        foreach (ulong scope in _callbackScopes.ToArray())
        {
            var status = CloseCallbackScope(scope);
            if (status is not (UnrealCallbackStatus.InvalidScope or UnrealCallbackStatus.Unavailable))
                UnrealCallbackScope.RequireSuccess(status, "dispose transport scope");
        }
    }

    private static void ValidateCallbackHandler(UnrealCallbackDescriptor descriptor, Delegate handler)
    {
        var invoke = handler.GetType().GetMethod("Invoke")!;
        var parameters = invoke.GetParameters();
        if (invoke.ReturnType != descriptor.ReturnType || parameters.Length != descriptor.Parameters.Count || parameters.Length > 16)
            throw new ArgumentException("The callback handler does not match its reflected signature.", nameof(handler));
        for (int i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var type = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
            var direction = parameter.IsOut ? UnrealParameterDirection.Output : parameter.ParameterType.IsByRef
                ? UnrealParameterDirection.InputOutput : UnrealParameterDirection.Input;
            if (type != descriptor.Parameters[i].Type || direction != descriptor.Parameters[i].Direction)
                throw new ArgumentException($"Callback parameter {i} does not match its reflected type/direction.", nameof(handler));
        }
        foreach (var type in descriptor.Parameters.Select(p => p.Type).Append(descriptor.ReturnType).Where(t => t != typeof(void)))
            if (!UnrealNativeValueCodecV3.TryCreateResultDescriptor(type, out _, out _))
                throw new NotSupportedException($"Callback value type '{type}' has no ABI v3 codec.");
    }

    private static unsafe void ReceiveCallback(nint identity, nint requestPointer, nint responsePointer)
    {
        if (responsePointer == 0) return;
        var response = new UnrealNativeCallbackResponse
        {
            AbiVersion = UnrealNativeCallbackAbi.Version, Size = UnrealNativeCallbackAbi.ResponseSize,
            Status = UnrealCallbackStatus.InvalidHandle,
        };
        CallbackBox? box = null;
        try
        {
            // Keep a local strong reference across self-disposal/reentrant generation cleanup.
            if (requestPointer != 0 && CallbackTargets.TryGetValue(identity, out box))
            {
                var request = *(UnrealNativeCallbackRequest*)requestPointer;
                if (box.Token != 0 && request.Token == box.Token && request.Scope == box.Scope && request.SignatureId == box.Descriptor.SignatureId)
                {
                    response = UnrealCallbackMarshaller.Invoke(box.Descriptor, box.Handler, request, out string? error);
                    Volatile.Write(ref box.Owner._lastCallbackError, error);
                }
                else
                {
                    response.Status = UnrealCallbackStatus.WrongSignature;
                    Volatile.Write(ref box.Owner._lastCallbackError, "Callback token, scope or signature does not match its registration.");
                }
            }
        }
        catch (Exception exception)
        {
            response.Status = UnrealCallbackStatus.HandlerFailed;
            if (box is not null) Volatile.Write(ref box.Owner._lastCallbackError, exception.ToString());
        }
        *(UnrealNativeCallbackResponse*)responsePointer = response;
    }

    private sealed class CallbackBox(InProcessUnrealNativeInvocationTransport owner, nint identity,
        ulong scope, UnrealCallbackDescriptor descriptor, Delegate handler)
    {
        internal readonly InProcessUnrealNativeInvocationTransport Owner = owner;
        internal readonly nint Identity = identity;
        internal readonly ulong Scope = scope;
        internal readonly UnrealCallbackDescriptor Descriptor = descriptor;
        internal readonly Delegate Handler = handler;
        internal ulong Token;
    }
}
