using System.Runtime.InteropServices;
using System.Text;
using Finare.Unreal;
using Transport = Finare.Unreal.InProcessUnrealNativeInvocationTransport;

namespace Finare.Tests;

public sealed partial class InProcessUnrealNativeInvocationTransportTests
{
    [UnrealCallbackType("/Script/Fixture.Query__DelegateSignature")]
    private delegate bool Query(int value, ref string text, out UnrealVector position);

    [Fact]
    public void OlderNativeTableDoesNotPretendToSupportCallbacks()
    {
        using var transport = new Transport((nint)0x1234, _tablePointer);
        Assert.False(transport.SupportsCallbacks);
        Assert.Equal(UnrealCallbackStatus.Unavailable, transport.OpenCallbackScope(out var scope));
        Assert.Equal(0UL, scope);
        Assert.Equal(42, new NativeUnrealInvocationBridge(transport).Invoke<int>(UnrealObjectHandle.Create(4, 9), AddPath, [17, 25]));
    }

    [Theory]
    [InlineData("version")] [InlineData("size")] [InlineData("request")]
    [InlineData("response")] [InlineData("lifetime")] [InlineData("entrypoint")]
    public void CallbackTableRejectsIncompatibleLayoutBeforeRetainingContext(string mutation)
    {
        using var fixture = new CallbackTableFixture(this);
        var table = Marshal.PtrToStructure<UnrealNativeCallbackTransportTable>(fixture.CallbackPointer);
        switch (mutation)
        {
            case "version": table.AbiVersion++; break;
            case "size": table.TableSize--; break;
            case "request": table.RequestSize--; break;
            case "response": table.ResponseSize--; break;
            case "lifetime": table.RetainContext = 0; break;
            case "entrypoint": table.ReleaseCallback = 0; break;
        }
        Marshal.StructureToPtr(table, fixture.CallbackPointer, false);
        Assert.Throws<InvalidOperationException>(() => new Transport((nint)0x1234, fixture.ParentPointer));
        Assert.Equal(0, _retainedContexts);
    }

    [Fact]
    public unsafe void CallbackTableRoundTripsOwnedOutputsAndRejectsRetiredIdentities()
    {
        using var fixture = new CallbackTableFixture(this);
        using var transport = new Transport((nint)0x1234, fixture.ParentPointer);
        using var scope = new UnrealCallbackScope(transport);
        int calls = 0;
        Query handler = (int value, ref string text, out UnrealVector position) =>
        {
            calls++;
            text = new string('x', 9000) + text;
            position = new(value, 2, 3);
            return value == 7;
        };
        using var lease = scope.Create(handler);
        Assert.Equal(UnrealCallbackDescriptor.For<Query>().SignaturePath, fixture.Path);
        var stale = fixture.Saved;
        GC.Collect(); GC.WaitForPendingFinalizers();
        var response = fixture.Dispatch(stale);
        try
        {
            Assert.Equal(UnrealCallbackStatus.Success, response.Status);
            Assert.Equal(1, calls);
            var cells = new ReadOnlySpan<UnrealNativeValueV3>((void*)response.Results, (int)response.ResultCount);
            var bytes = new ReadOnlySpan<byte>((void*)response.ByteBuffer, (int)response.ByteCount);
            Assert.Equal(3, cells.Length);
            Assert.True(UnrealNativeValueCodecV3.TryDecode<bool>(cells[0], bytes, bytes.Length, out var result) && result);
            Assert.True(UnrealNativeValueCodecV3.TryDecode<string>(cells[1], bytes, bytes.Length, out var text));
            Assert.Equal(new string('x', 9000) + "tail", text);
            Assert.True(UnrealNativeValueCodecV3.TryDecode<UnrealVector>(cells[2], bytes, bytes.Length, out var position));
            Assert.Equal(new UnrealVector(7, 2, 3), position);
            Assert.Null(transport.LastCallbackError);
        }
        finally { ReleaseCallbackResponse(response); }
        lease.Dispose();
        Assert.Equal(1, fixture.Releases);
        using var next = scope.Create(handler);
        Assert.NotEqual(stale.Identity, fixture.Saved.Identity);
        Assert.Equal(UnrealCallbackStatus.InvalidHandle, fixture.Dispatch(stale).Status);
        Assert.Equal(UnrealCallbackStatus.WrongSignature, fixture.Dispatch(fixture.Saved, corruptScope: true).Status);
        Assert.Equal(1, calls);
        Assert.Contains("scope", transport.LastCallbackError);
    }

    [Fact]
    public void CallbackSelfDisposalAndExceptionsStayInsideTheAbi()
    {
        using var fixture = new CallbackTableFixture(this);
        using var transport = new Transport((nint)0x1234, fixture.ParentPointer);
        using var scope = new UnrealCallbackScope(transport);
        UnrealCallback<Query>? lease = null;
        Query self = (int value, ref string text, out UnrealVector position) =>
        {
            lease!.Dispose();
            GC.Collect();
            text = "released"; position = new(1, 2, 3);
            return true;
        };
        lease = scope.Create(self);
        var saved = fixture.Saved;
        var response = fixture.Dispatch(saved);
        try { Assert.Equal(UnrealCallbackStatus.Success, response.Status); }
        finally { ReleaseCallbackResponse(response); }
        Assert.Equal(UnrealCallbackStatus.InvalidHandle, fixture.Dispatch(saved).Status);
        Query failure = (int value, ref string text, out UnrealVector position) => throw new InvalidOperationException("callback failure fixture");
        using var throwing = scope.Create(failure);
        response = fixture.Dispatch(fixture.Saved);
        Assert.Equal(UnrealCallbackStatus.HandlerFailed, response.Status);
        Assert.Equal(0, response.Owner);
        Assert.Equal(0U, response.ResultCount);
        Assert.Contains("callback failure fixture", transport.LastCallbackError);
    }

    [Fact]
    public void CallbackTransportDisposalIsRetryableAndUnbindsBeforeDroppingHandlers()
    {
        using var fixture = new CallbackTableFixture(this);
        using var transport = new Transport((nint)0x1234, fixture.ParentPointer);
        using var scope = new UnrealCallbackScope(transport);
        using var lease = scope.Create<Query>((int value, ref string text, out UnrealVector position) => { position = default; return true; });
        var saved = fixture.Saved;
        fixture.Status = UnrealCallbackStatus.WrongThread;
        Assert.Equal(UnrealCallbackStatus.WrongThread, Assert.Throws<UnrealCallbackException>(() => transport.Dispose()).Status);
        Assert.True(transport.SupportsCallbacks);
        Assert.Equal(1, _retainedContexts);
        var response = fixture.Dispatch(saved);
        try { Assert.Equal(UnrealCallbackStatus.Success, response.Status); }
        finally { ReleaseCallbackResponse(response); }
        fixture.Status = UnrealCallbackStatus.Unavailable; // World detached and all native copies disabled.
        transport.Dispose();
        Assert.Equal(0, _retainedContexts);
        Assert.False(transport.SupportsCallbacks);
        Assert.Equal(UnrealCallbackStatus.InvalidHandle, fixture.Dispatch(saved).Status);
    }

    [Fact]
    public void CallbackRegistrationValidatesManagedTypeAndDirectionBeforeNativeEffects()
    {
        using var fixture = new CallbackTableFixture(this);
        using var transport = new Transport((nint)0x1234, fixture.ParentPointer);
        transport.OpenCallbackScope(out ulong scope);
        var descriptor = UnrealCallbackDescriptor.For<Query>();
        Query handler = (int value, ref string text, out UnrealVector position) => { position = default; return true; };
        descriptor = descriptor with { Parameters = [descriptor.Parameters[0], descriptor.Parameters[1] with { Direction = UnrealParameterDirection.Input }, descriptor.Parameters[2]] };
        Assert.Throws<ArgumentException>(() => transport.RegisterCallback(scope, descriptor, handler, out _));
        Assert.Equal(0, fixture.Registrations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackReentryFromAnotherThreadDoesNotHoldForwardLifetimeLock(bool disposeInsideCallback)
    {
        using var fixture = new CallbackTableFixture(this);
        using var transport = new Transport((nint)0x1234, fixture.ParentPointer);
        using var scope = new UnrealCallbackScope(transport);
        var component = new Finare.Unreal.Generated.FinareComponent(
            UnrealObjectHandle.Create(4, 9), new NativeUnrealInvocationBridge(transport));
        int retainedDuringCallback = 0;
        using var lease = scope.Create<Query>((int value, ref string text, out UnrealVector position) =>
        {
            // The native caller waits on this thread, just like a queued UE game-thread call.
            component.ScriptAssetPath = "callback-reentered";
            text = component.ScriptAssetPath;
            if (disposeInsideCallback) transport.Dispose();
            retainedDuringCallback = _retainedContexts;
            position = new(value, 2, 3);
            return true;
        });
        Task? dispatch = null;
        bool completedBeforeNativeReturn = false;
        UnrealCallbackStatus callbackStatus = UnrealCallbackStatus.Unavailable;
        fixture.DuringInvoke = () =>
        {
            dispatch = Task.Run(() =>
            {
                var response = fixture.Dispatch(fixture.Saved);
                callbackStatus = response.Status;
                ReleaseCallbackResponse(response);
            });
            // Bound the failure so the pre-fix deadlock is observable without hanging the suite.
            completedBeforeNativeReturn = dispatch.Wait(TimeSpan.FromSeconds(1));
        };
        Assert.Equal(42, await Task.Run(() => component.Add(17, 25)).WaitAsync(TimeSpan.FromSeconds(5)));
        await dispatch!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(completedBeforeNativeReturn, "The forward-call lifetime lock blocked callback-to-native reentry on another thread.");
        Assert.Equal(UnrealCallbackStatus.Success, callbackStatus);
        Assert.Equal("callback-reentered", _scriptAsset);
        Assert.Equal(1, retainedDuringCallback); // Disposal cannot free the native context under the outer call.
        Assert.Equal(disposeInsideCallback ? 0 : 1, _retainedContexts);
        if (disposeInsideCallback) Assert.Throws<ObjectDisposedException>(() => component.Add(1, 2));
    }

    private static void ReleaseCallbackResponse(UnrealNativeCallbackResponse response)
    {
        if (response.Release != 0) Marshal.GetDelegateForFunctionPointer<UnrealCallbackMarshaller.ReleaseResponse>(response.Release)(response.Owner);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestTableV2Callbacks
    {
        public TestTableV2AsyncServices Base;
        public nint NativeObjectTransport, CallbackTransport;
    }

    private sealed unsafe class CallbackTableFixture : IDisposable
    {
        internal nint ParentPointer, CallbackPointer;
        internal UnrealCallbackStatus Status = UnrealCallbackStatus.Success;
        internal (Transport.NativeCallback? Callback, nint Identity, ulong Token, ulong Scope) Saved;
        internal string? Path;
        internal int Releases, Registrations;
        internal Action? DuringInvoke;
        private ulong _scope, _token;
        private readonly Transport.NativeOpenCallbackScope _open;
        private readonly Transport.NativeCloseCallbackScope _close;
        private readonly Transport.NativeRegisterCallback _register;
        private readonly Transport.NativeReleaseCallback _release;
        private readonly UnrealNativeInvokeByIdV2 _invoke;

        internal CallbackTableFixture(InProcessUnrealNativeInvocationTransportTests owner)
        {
            _open = (nint context, out ulong scope) => { scope = Status == UnrealCallbackStatus.Success ? ++_scope : 0; return Status; };
            _close = (_, _) => Status;
            _release = (_, _, _) => { if (Status == UnrealCallbackStatus.Success) Releases++; return Status; };
            _invoke = (context, target, member, arguments, count, callback, data) =>
            {
                DuringInvoke?.Invoke();
                owner._fastInvoke(context, target, member, arguments, count, callback, data);
            };
            _register = (nint context, ulong scope, byte* path, uint length, Transport.NativeCallback callback, nint data, out ulong token) =>
            {
                Registrations++;
                Path = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(path, (int)length));
                token = ++_token; Saved = (callback, data, token, scope);
                return Status;
            };
            var table = new UnrealNativeCallbackTransportTable
            {
                AbiVersion = 1, TableSize = 64, RequestSize = 64, ResponseSize = 56,
                RetainContext = Marshal.GetFunctionPointerForDelegate(owner._retain),
                ReleaseContext = Marshal.GetFunctionPointerForDelegate(owner._release),
                OpenScope = Marshal.GetFunctionPointerForDelegate(_open), CloseScope = Marshal.GetFunctionPointerForDelegate(_close),
                RegisterCallback = Marshal.GetFunctionPointerForDelegate(_register), ReleaseCallback = Marshal.GetFunctionPointerForDelegate(_release),
            };
            CallbackPointer = Marshal.AllocHGlobal(64);
            Marshal.StructureToPtr(table, CallbackPointer, false);
            var parent = new TestTableV2Callbacks
            {
                Base = new TestTableV2AsyncServices
                {
                    AbiVersion = 2, TableSize = (uint)Marshal.SizeOf<TestTableV2Callbacks>(), ValueSize = (uint)Marshal.SizeOf<TestValue>(),
                    ReadProperty = Marshal.GetFunctionPointerForDelegate(owner._read), WriteProperty = Marshal.GetFunctionPointerForDelegate(owner._write),
                    Invoke = Marshal.GetFunctionPointerForDelegate(owner._invoke), RetainContext = table.RetainContext, ReleaseContext = table.ReleaseContext,
                    ReadPropertyById = Marshal.GetFunctionPointerForDelegate(owner._fastRead), WritePropertyById = Marshal.GetFunctionPointerForDelegate(owner._fastWrite),
                    InvokeById = Marshal.GetFunctionPointerForDelegate(_invoke),
                },
                CallbackTransport = CallbackPointer,
            };
            ParentPointer = Marshal.AllocHGlobal(Marshal.SizeOf<TestTableV2Callbacks>());
            Marshal.StructureToPtr(parent, ParentPointer, false);
        }

        internal UnrealNativeCallbackResponse Dispatch((Transport.NativeCallback? Callback, nint Identity, ulong Token, ulong Scope) saved, bool corruptScope = false)
        {
            Span<byte> bytes = stackalloc byte[64];
            Span<UnrealNativeValueV3> cells = stackalloc UnrealNativeValueV3[3];
            cells.Clear();
            int count = 0;
            UnrealNativeValueCodecV3.TryEncode(7, bytes, ref count, out cells[0], out _);
            UnrealNativeValueCodecV3.TryEncode("tail", bytes, ref count, out cells[1], out _);
            fixed (byte* arena = bytes)
            fixed (UnrealNativeValueV3* values = cells)
            {
                var request = new UnrealNativeCallbackRequest
                {
                    AbiVersion = 1, Size = 64, Scope = saved.Scope + (corruptScope ? 1UL : 0UL), Token = saved.Token,
                    SignatureId = UnrealCallbackDescriptor.For<Query>().SignatureId,
                    ArgumentCount = 3, Arguments = (nint)values, ByteCount = (uint)count, ByteBuffer = (nint)arena,
                };
                UnrealNativeCallbackResponse response = default;
                saved.Callback!(saved.Identity, (nint)(&request), (nint)(&response));
                return response;
            }
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(ParentPointer); Marshal.FreeHGlobal(CallbackPointer);
            GC.KeepAlive(_open); GC.KeepAlive(_close); GC.KeepAlive(_register); GC.KeepAlive(_release);
            GC.KeepAlive(_invoke);
        }
    }
}
