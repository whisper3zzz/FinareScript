using Finare.CSharp;
using System.Text.Json;
using Finare.Graph;
using Finare.Unreal;
using Finare.Unreal.Generated;

namespace Finare.Tests;

public sealed class UnrealByteBufferTests
{
    [Fact]
    public void CopyBorrowAndSliceHaveExplicitOwnership()
    {
        byte[] bytes = [8, 0, 255, 4];
        var copy = UnrealByteBuffer.CopyFrom(bytes);
        var borrowed = UnrealByteBuffer.Borrow(bytes).Slice(1, 2);
        bytes[1] = 7;
        Assert.Equal((byte)0, copy[1]);
        Assert.Equal(new byte[] { 7, 255 }, borrowed.ToArray());
        Assert.True(borrowed.IsBorrowed);
        Assert.False(copy.IsBorrowed);
        Assert.Equal(0, default(UnrealByteBuffer).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => copy.Slice(3, 2));
        Assert.Throws<ArgumentNullException>(() => UnrealByteBuffer.Borrow(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnrealByteBuffer.Borrow(new byte[UnrealByteBuffer.MaximumLength + 1]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65536)]
    [InlineData(2097152)]
    [InlineData(UnrealByteBuffer.MaximumLength)]
    public void CodecUsesContiguousBytesAndReturnsIndependentSnapshots(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i % 251);
        byte[] arena = new byte[length + 5];
        int cursor = 5;
        var buffer = UnrealByteBuffer.Borrow(bytes);
        Assert.True(UnrealNativeValueCodecV3.TryEncode(buffer, arena, ref cursor, out var cell, out var capability));
        Assert.Equal(UnrealNativeCapabilitiesV3.ByteBuffers, capability);
        Assert.Equal(25, (int)cell.Kind);
        Assert.Equal((ulong)length, cell.Payload1);
        Assert.Equal(length + 5, cursor);
        Assert.True(UnrealNativeValueCodecV3.TryDecode<UnrealByteBuffer>(cell, arena, cursor, out var decoded));
        Assert.False(decoded.IsBorrowed);
        Assert.Equal(bytes, decoded.ToArray());
        Array.Fill(arena, (byte)99);
        Assert.Equal(bytes, decoded.ToArray());
        Assert.True(UnrealNativeValueCodecV3.TryCreateResultDescriptor<UnrealByteBuffer>(out var descriptor, out capability));
        Assert.Equal(UnrealNativeValueKindV3.ByteBuffer, descriptor.Kind);
        Assert.Equal(0UL, descriptor.Payload1);
        cursor = 0;
        if (length > 0)
        {
            Assert.False(UnrealNativeValueCodecV3.TryEncode(buffer, Span<byte>.Empty, ref cursor, out cell, out _));
            Assert.Equal(0, cursor);
            Assert.Equal(default, cell);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void MalformedByteCellsFailWithoutPublishingData(int mutation)
    {
        var value = new UnrealNativeValueV3 { Kind = UnrealNativeValueKindV3.ByteBuffer, Flags = UnrealNativeValueFlagsV3.ArenaBacked, Payload1 = 1 };
        switch (mutation)
        {
            case 0: value.Payload0 = ulong.MaxValue; break;
            case 1: value.Payload1 = ulong.MaxValue; break;
            case 2: value.Flags |= UnrealNativeValueFlagsV3.Null; break;
            case 3: value.Reserved16 = 1; break;
            case 4: value.Auxiliary = 1; break;
            case 5: value.TypeId = 1; break;
            case 6: value.Payload2 = 1; break;
            case 7: value.Payload3 = 48; break;
        }
        Assert.False(UnrealNativeValueCodecV3.TryDecode<UnrealByteBuffer>(value, new byte[1], 1, out var decoded));
        Assert.Equal(0, decoded.Length);
        Assert.False(UnrealNativeValueCodecV3.TryDecodeObject(typeof(UnrealByteBuffer), value, new byte[1], 1, out var boxed));
        Assert.Null(boxed);
    }

    [Fact]
    public void OldTransportRejectsGeneratedBinaryCallsAndPropertiesBeforeInvocation()
    {
        var transport = new BufferTransport(false);
        var bridge = new NativeUnrealInvocationBridge(transport);
        var fixture = new FinareNativeExtensionFixture(UnrealObjectHandle.Create(0, 1), bridge);
        Assert.Throws<NotSupportedException>(() => fixture.StoreBytes(default));
        Assert.Throws<NotSupportedException>(() => fixture.ReadBytes());
        Assert.Throws<NotSupportedException>(() => fixture.Payload);
        Assert.Throws<NotSupportedException>(() => fixture.Payload = default);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public void GeneratedCallsAllocateForLargeBinaryInput()
    {
        var transport = new BufferTransport(true);
        var fixture = new FinareNativeExtensionFixture(UnrealObjectHandle.Create(0, 1), new NativeUnrealInvocationBridge(transport));
        byte[] bytes = new byte[65536];
        bytes[60000] = 231;
        fixture.StoreBytes(UnrealByteBuffer.Borrow(bytes));
        Assert.Equal(bytes, transport.LastInput.ToArray());
        bytes[60000] = 0;
        Assert.Equal((byte)231, transport.LastInput[60000]);
        fixture.Payload = UnrealByteBuffer.Borrow(bytes);
        Assert.Equal(2, transport.Calls);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void DirectTypedOverloadsUseTheSameBinaryRoute(int arity)
    {
        var transport = new BufferTransport(true);
        var bridge = new NativeUnrealInvocationBridge(transport);
        var handle = UnrealObjectHandle.Create(0, 1);
        const string path = "/Script/Fixture.Binary:Echo";
        ulong id = UnrealMemberId.FromPath(path);
        var buffer = UnrealByteBuffer.CopyFrom(new byte[8192]);
        var result = arity switch
        {
            1 => bridge.Invoke<UnrealByteBuffer, UnrealByteBuffer>(handle, id, path, buffer),
            2 => bridge.Invoke<UnrealByteBuffer, int, UnrealByteBuffer>(handle, id, path, 1, buffer),
            3 => bridge.Invoke<UnrealByteBuffer, int, bool, UnrealByteBuffer>(handle, id, path, 1, true, buffer),
            _ => bridge.Invoke<UnrealByteBuffer, int, bool, float, UnrealByteBuffer>(handle, id, path, 1, true, 2f, buffer),
        };
        Assert.Equal(buffer.Length, result.Length);
        switch (arity)
        {
            case 1: bridge.Invoke(handle, id, path, buffer); break;
            case 2: bridge.Invoke(handle, id, path, buffer, 1); break;
            case 3: bridge.Invoke(handle, id, path, buffer, 1, true); break;
            case 4: bridge.Invoke(handle, id, path, buffer, 1, true, 2f); break;
        }
        Assert.Equal(buffer.Length, bridge.Invoke<UnrealByteBuffer>(handle, id, path).Length);
        Assert.Equal(0, bridge.LegacyInvocationCount);
        var old = new NativeUnrealInvocationBridge(new BufferTransport(false));
        Assert.Throws<NotSupportedException>(() => old.Invoke(handle, id, path, buffer));
        Assert.Throws<NotSupportedException>(() => old.Invoke<UnrealByteBuffer>(handle, id, path));
    }

    [Fact]
    public void OversizedBinaryRequestsAndRetriesAreBounded()
    {
        var transport = new BufferTransport(true, oversizedResult: true);
        var bridge = new NativeUnrealInvocationBridge(transport);
        var handle = UnrealObjectHandle.Create(0, 1);
        var fixture = new FinareNativeExtensionFixture(handle, bridge);
        Assert.Throws<NotSupportedException>(() => fixture.ReadBytes());
        Assert.Equal(1, transport.Calls);
        Assert.Throws<NotSupportedException>(() => fixture.Payload);
        Assert.Equal(2, transport.Calls);
        var buffer = UnrealByteBuffer.Borrow(new byte[UnrealByteBuffer.MaximumLength]);
        Assert.Throws<NotSupportedException>(() => bridge.InvokeWithOutputs(handle, 1, "/Script/Fixture.Binary:Many",
            typeof(void), Enumerable.Repeat(new UnrealOutputArgument(typeof(UnrealByteBuffer), UnrealParameterDirection.Input, buffer), 5).ToArray()));
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public void MalformedLaterOutputDoesNotPartiallyPublishRefOrOutValues()
    {
        var transport = new BufferTransport(true, malformedOutput: true);
        var fixture = new FinareNativeExtensionFixture(UnrealObjectHandle.Create(0, 1), new NativeUnrealInvocationBridge(transport));
        var input = UnrealByteBuffer.CopyFrom(new byte[] { 3, 4 });
        var inOut = UnrealByteBuffer.CopyFrom(new byte[] { 99 });
        int calls = 123;
        Assert.Throws<InvalidOperationException>(() => fixture.TransformBytes(input, ref inOut, out calls));
        Assert.Equal(new byte[] { 99 }, inOut.ToArray());
        Assert.Equal(123, calls);
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData("container")]
    [InlineData("reflected-state")]
    [InlineData("wrong-header")]
    public void UnsupportedBufferDeclarationsFailBeforeGeneration(string shape)
    {
        var value = new UnrealTypeReference(UnrealValueKind.ByteBuffer, "FFinareByteBuffer") { HeaderPath = "FinareByteBuffer.h" };
        if (shape == "container") value = new(UnrealValueKind.Array, "TArray<FFinareByteBuffer>") { ElementType = value };
        if (shape == "wrong-header") value = value with { HeaderPath = "Other.h" };
        var manifest = UnrealReflectionManifest.Create(new(5, 8, 1, "test"), ["Fixture"],
            [new("/Script/Fixture.Binary", "Binary", "UBinary", "Fixture", UnrealReflectedTypeKind.Class, null,
                new(false, true), [new("/Script/Fixture.Binary:Payload", "Payload", value, new(true, true, false))], [], "Binary.h")]);
        Assert.Throws<JsonException>(() => new UnrealBindingGenerator().Generate(manifest));
    }

    [Fact]
    public void BinaryValuesAreVisualCallBoundaries()
    {
        var reflection = UnrealReflectionJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UnrealFixtures/FinareRuntime.reflection.json")));
        var contract = UnrealApiContractJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UnrealFixtures/FinareRuntime.api-contract.json")));
        var bindings = new UnrealBindingGenerator().Generate(UnrealApiContractProjector.Project(reflection, contract).Reflection).Source;
        const string source = """
            using Finare;
            using Finare.Unreal;
            using Finare.Unreal.Generated;
            public class PacketLogic
            {
                [Visual]
                public int Length(UnrealByteBuffer buffer) { return buffer.Length; }
                [Visual]
                public UnrealByteBuffer Read(FinareNativeExtensionFixture target) { return target.ReadBytes(); }
            }
            """;
        var result = new CSharpSemanticPipeline().Analyze(new CSharpCompilationRequest("BinaryVisual",
            [new("Bindings.cs", bindings), new("PacketLogic.cs", source)], [typeof(UnrealByteBuffer).Assembly.Location]));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var graph = new HirGraphProjector().Project(result.Hir!, result.Semantics!);
        Assert.Equal(2, graph.Functions.Count);
        Assert.Contains(graph.Functions.SelectMany(f => f.Nodes), n => n.Kind == "invocation" && n.Properties["method"].Contains("ReadBytes"));
    }

    private sealed class BufferTransport(bool supported, bool oversizedResult = false, bool malformedOutput = false) : IUnrealNativeInvocationTransport, IUnrealNativeCallFrameTransportV3
    {
        public int Calls { get; private set; }
        public UnrealByteBuffer LastInput { get; private set; }
        public UnrealNativeCapabilitiesV3 Capabilities => UnrealNativeCapabilitiesV3.OutputAndReference | UnrealNativeCapabilitiesV3.FixedScalars |
            (supported ? UnrealNativeCapabilitiesV3.ByteBuffers : UnrealNativeCapabilitiesV3.None);
        public UnrealNativeCallOutcomeV3 ExecuteById(UnrealNativeOperationV3 operation, UnrealObjectHandle target,
            ulong memberId, ReadOnlySpan<UnrealNativeValueV3> arguments, Span<UnrealNativeValueV3> results,
            Span<byte> byteBuffer, int inputByteCount)
        {
            Calls++;
            foreach (var argument in arguments)
            {
                if (argument.Kind != UnrealNativeValueKindV3.ByteBuffer) continue;
                Assert.True(UnrealNativeValueCodecV3.TryDecode<UnrealByteBuffer>(argument, byteBuffer, inputByteCount, out var decoded));
                LastInput = decoded;
                break;
            }
            int required = inputByteCount + results.Length * LastInput.Length;
            if (oversizedResult || required > byteBuffer.Length)
                return new(UnrealNativeCallStatusV3.BufferTooSmall, UnrealInvocationError.None, UnrealObjectHandleError.None,
                    0, inputByteCount, results.Length, oversizedResult ? int.MaxValue : required, "");
            int cursor = inputByteCount;
            for (int i = 0; i < results.Length; i++)
                Assert.True(UnrealNativeValueCodecV3.TryEncode(LastInput, byteBuffer, ref cursor, out results[i], out _));
            if (malformedOutput) results[^1].Payload0 = ulong.MaxValue;
            return UnrealNativeCallOutcomeV3.Succeeded(results.Length, cursor);
        }
        public UnrealInvocationResponse ReadProperty(UnrealObjectHandle target, string propertyPath) => throw new InvalidOperationException("Unexpected legacy route");
        public UnrealInvocationResponse WriteProperty(UnrealObjectHandle target, string propertyPath, UnrealInvocationValue value) => throw new InvalidOperationException("Unexpected legacy route");
        public UnrealInvocationResponse Invoke(UnrealObjectHandle target, string functionPath, IReadOnlyList<UnrealInvocationValue> arguments) => throw new InvalidOperationException("Unexpected legacy route");
    }
}
