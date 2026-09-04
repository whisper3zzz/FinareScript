using System.Runtime.InteropServices;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealCallbackTests
{
    [UnrealCallbackType("/Script/Fixture.Compute__DelegateSignature")]
    private delegate bool Compute(int value, ref string text, out UnrealVector vector);
    [UnrealCallbackType("/Script/Fixture.Notify__DelegateSignature")]
    private delegate void Notify();

    [Fact]
    public void CallbackAbiKeepsValueCellsAndIndependentTablesFixed()
    {
        Assert.Equal(64, Marshal.SizeOf<UnrealNativeCallbackRequest>());
        Assert.Equal(56, Marshal.SizeOf<UnrealNativeCallbackResponse>());
        Assert.Equal(64, Marshal.SizeOf<UnrealNativeCallbackTransportTable>());
        Assert.Equal(48, Marshal.SizeOf<UnrealNativeValueV3>());
        Assert.Equal(27, (byte)UnrealNativeValueKindV3.Callback);
        Assert.Equal(48, Marshal.OffsetOf<UnrealNativeCallbackResponse>(nameof(UnrealNativeCallbackResponse.Release)).ToInt32());
    }

    [Fact]
    public void DescriptorPreservesResultAndReferenceDirections()
    {
        var descriptor = UnrealCallbackDescriptor.For<Compute>();
        Assert.Equal(typeof(bool), descriptor.ReturnType);
        Assert.Equal([UnrealParameterDirection.Input, UnrealParameterDirection.InputOutput, UnrealParameterDirection.Output],
            descriptor.Parameters.Select(p => p.Direction));
        Assert.Equal([typeof(int), typeof(string), typeof(UnrealVector)], descriptor.Parameters.Select(p => p.Type));
        Assert.Throws<ArgumentException>(() => UnrealCallbackDescriptor.For<Func<int, bool>>());
    }

    [Fact]
    public void CallbackArgumentsCheckLiveOwnerAndDoNotBecomeUObjectValues()
    {
        var transport = new Transport();
        using var scope = new UnrealCallbackScope(transport);
        using var otherScope = new UnrealCallbackScope(transport);
        var bridge = new Bridge(scope);
        using var callback = scope.Create<Notify>(() => { });
        var argument = callback.AsArgument(bridge);
        int bytes = 0;
        Assert.True(UnrealNativeValueCodecV3.TryEncode(argument, [], ref bytes, out var cell, out var capability));
        Assert.Equal(UnrealNativeCapabilitiesV3.CallbackArguments, capability);
        Assert.Equal(UnrealNativeValueKindV3.Callback, cell.Kind);
        Assert.Equal(UnrealCallbackDescriptor.For<Notify>().SignatureId, cell.TypeId);
        Assert.NotEqual(0UL, cell.Payload0);
        Assert.NotEqual(0UL, cell.Payload1);
        Assert.False(UnrealNativeValueCodecV3.TryDecode<UnrealObjectHandle>(cell, [], 0, out _));
        Assert.False(UnrealNativeValueCodecV3.TryEncode(callback, [], ref bytes, out _, out _));
        Assert.False(UnrealNativeValueCodecV3.TryEncode(default(UnrealCallbackArgument), [], ref bytes, out _, out _));
        Assert.Equal(UnrealCallbackStatus.WrongOwner,
            Assert.Throws<UnrealCallbackException>(() => callback.AsArgument(new Bridge(otherScope))).Status);
        callback.Dispose();
        callback.Dispose();
        Assert.Equal(1, transport.Releases);
        Assert.Throws<ObjectDisposedException>(() => callback.AsArgument(bridge));
        using var retained = scope.Create<Notify>(() => { });
        scope.Dispose();
        Assert.Empty(transport.Handlers);
        Assert.Throws<ObjectDisposedException>(() => retained.AsArgument(bridge));
    }

    [Fact]
    public void UnsupportedAndWrongThreadOperationsDoNotLoseRetryableOwnership()
    {
        var transport = new Transport { SupportsCallbacks = false };
        Assert.Throws<NotSupportedException>(() => new UnrealCallbackScope(transport));
        Assert.Equal(0UL, transport.Next);
        transport.SupportsCallbacks = true;
        using var scope = new UnrealCallbackScope(transport);
        using var callback = scope.Create<Notify>(() => { });
        transport.Status = UnrealCallbackStatus.WrongThread;
        Assert.Throws<UnrealCallbackException>(() => callback.Dispose());
        Assert.Throws<UnrealCallbackException>(() => scope.Dispose());
        Assert.Single(transport.Handlers);
        callback.AsArgument(new Bridge(scope));
        transport.Status = UnrealCallbackStatus.Success;
        callback.Dispose();
        Assert.Empty(transport.Handlers);
    }

    [Fact]
    public unsafe void ReturnAndRefOutAreEncodedOnceAndResponseReleaseIsIdempotent()
    {
        int calls = 0;
        Compute handler = (int value, ref string text, out UnrealVector vector) =>
        {
            calls++;
            text = new string('x', 9000) + text;
            vector = new(value, 2, 3);
            return value == 7;
        };
        var descriptor = UnrealCallbackDescriptor.For<Compute>();
        var arena = new byte[100];
        var cells = new UnrealNativeValueV3[3];
        int bytes = 0;
        UnrealNativeValueCodecV3.TryEncode(7, arena, ref bytes, out cells[0], out _);
        UnrealNativeValueCodecV3.TryEncode("tail", arena, ref bytes, out cells[1], out _);
        fixed (UnrealNativeValueV3* cellPointer = cells)
        fixed (byte* bytePointer = arena)
        {
            var request = Request(descriptor, (nint)cellPointer, 3, (nint)bytePointer, bytes);
            int initialResponses = UnrealCallbackMarshaller.PendingResponses;
            var response = UnrealCallbackMarshaller.Invoke(descriptor, handler, request, out var error);
            Assert.Null(error);
            Assert.Equal(UnrealCallbackStatus.Success, response.Status);
            Assert.Equal(1, calls);
            var output = new ReadOnlySpan<UnrealNativeValueV3>((void*)response.Results, (int)response.ResultCount);
            var outputBytes = new ReadOnlySpan<byte>((void*)response.ByteBuffer, (int)response.ByteCount);
            try
            {
                Assert.Equal(3, output.Length);
                Assert.True(UnrealNativeValueCodecV3.TryDecode<bool>(output[0], outputBytes, outputBytes.Length, out var result));
                Assert.True(result);
                Assert.True(UnrealNativeValueCodecV3.TryDecode<string>(output[1], outputBytes, outputBytes.Length, out var text));
                Assert.Equal(new string('x', 9000) + "tail", text);
                Assert.True(UnrealNativeValueCodecV3.TryDecode<UnrealVector>(output[2], outputBytes, outputBytes.Length, out var vector));
                Assert.Equal(new UnrealVector(7, 2, 3), vector);
            }
            finally
            {
                var release = Marshal.GetDelegateForFunctionPointer<UnrealCallbackMarshaller.ReleaseResponse>(response.Release);
                release(response.Owner);
                release(response.Owner);
            }
            Assert.Equal(initialResponses, UnrealCallbackMarshaller.PendingResponses);
        }
    }

    [Fact]
    public void BadFramesAndHandlerExceptionsPublishNoPartialResponse()
    {
        var descriptor = UnrealCallbackDescriptor.For<Notify>();
        int calls = 0;
        Notify handler = () => calls++;
        var request = Request(descriptor);
        request.Reserved = 1;
        Assert.Equal(UnrealCallbackStatus.InvalidArgument, UnrealCallbackMarshaller.Invoke(descriptor, handler, request, out _).Status);
        Assert.Equal(0, calls);
        request = Request(descriptor);
        request.SignatureId++;
        Assert.Equal(UnrealCallbackStatus.InvalidArgument, UnrealCallbackMarshaller.Invoke(descriptor, handler, request, out _).Status);
        Assert.Equal(0, calls);
        Notify failure = () => throw new InvalidOperationException("expected callback fault");
        var response = UnrealCallbackMarshaller.Invoke(descriptor, failure, Request(descriptor), out var error);
        Assert.Equal(UnrealCallbackStatus.HandlerFailed, response.Status);
        Assert.Contains("expected callback fault", error);
        Assert.Equal(0, response.Owner);
        Assert.Equal(0, response.Results);
        Assert.Equal(0, response.ByteBuffer);
        Assert.Equal(0U, response.ResultCount);
    }

    private static UnrealNativeCallbackRequest Request(UnrealCallbackDescriptor descriptor, nint arguments = 0,
        int count = 0, nint bytes = 0, int byteCount = 0) => new()
    {
        AbiVersion = UnrealNativeCallbackAbi.Version, Size = UnrealNativeCallbackAbi.RequestSize,
        Token = 10, Scope = 20, SignatureId = descriptor.SignatureId, Arguments = arguments,
        ArgumentCount = (uint)count, ByteBuffer = bytes, ByteCount = (uint)byteCount,
    };

    private sealed class Transport : IUnrealNativeCallbackTransport
    {
        public bool SupportsCallbacks { get; set; } = true;
        public ulong Next;
        public int Releases;
        public UnrealCallbackStatus Status;
        public Dictionary<ulong, (ulong Scope, Delegate Handler)> Handlers = [];
        public UnrealCallbackStatus OpenCallbackScope(out ulong scope) { scope = ++Next; return Status; }
        public UnrealCallbackStatus CloseCallbackScope(ulong scope)
        {
            if (Status != UnrealCallbackStatus.Success) return Status;
            foreach (var token in Handlers.Where(p => p.Value.Scope == scope).Select(p => p.Key).ToArray()) Handlers.Remove(token);
            return Status;
        }
        public UnrealCallbackStatus RegisterCallback(ulong scope, UnrealCallbackDescriptor descriptor, Delegate handler, out ulong token)
        {
            token = ++Next;
            Handlers.Add(token, (scope, handler));
            return Status;
        }
        public UnrealCallbackStatus ReleaseCallback(ulong scope, ulong token)
        {
            if (Status != UnrealCallbackStatus.Success) return Status;
            Releases++;
            return Handlers.Remove(token) ? Status : UnrealCallbackStatus.InvalidHandle;
        }
    }

    private sealed class Bridge(UnrealCallbackScope callbacks) : IUnrealInvocationBridge, IUnrealCallbackProvider
    {
        public UnrealCallbackScope Callbacks { get; } = callbacks;
        public T ReadProperty<T>(UnrealObjectHandle target, string path) => throw new NotSupportedException();
        public void WriteProperty<T>(UnrealObjectHandle target, string path, T value) => throw new NotSupportedException();
        public T Invoke<T>(UnrealObjectHandle target, string path, IReadOnlyList<object?> arguments) => throw new NotSupportedException();
        public void Invoke(UnrealObjectHandle target, string path, IReadOnlyList<object?> arguments) => throw new NotSupportedException();
    }
}
