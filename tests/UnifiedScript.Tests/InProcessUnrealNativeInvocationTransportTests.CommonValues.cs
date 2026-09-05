using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UnifiedScript.Unreal;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Tests;

public sealed partial class InProcessUnrealNativeInvocationTransportTests
{
    [Fact]
    public void PropertyPathPreflightsNullMapValuesBeforeArenaGrowthOrNativeWrite()
    {
        int writeCallbacks = 0;
        IReadOnlyDictionary<UnrealName, string> map = new Dictionary<UnrealName, string>
        {
            [new UnrealName("MissingValue")] = null!,
        };

        WithCommonValueLoopback(
            transport =>
            {
                var bridge = new NativeUnrealInvocationBridge(transport);
                long before = GC.GetAllocatedBytesForCurrentThread();
                Assert.Throws<UnrealInvocationException>(() => bridge.WriteProperty(
                    UnrealObjectHandle.Create(4, 9),
                    "property",
                    map));
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.InRange(allocated, 0, 2 * 1024 * 1024);
            },
            onWrite: () => writeCallbacks++);

        Assert.Equal(0, writeCallbacks);
    }

    [Fact]
    public void LegacyPropertyPathCarriesPackedV3CollectionPayloadsThroughBridge()
    {
        WithPackedCollectionLoopback(
            UnrealInvocationValueKind.Array,
            PackCollection<IReadOnlyList<int>>([1, 2, 3]),
            transport =>
            {
                var bridge = new NativeUnrealInvocationBridge(transport);
                UnrealObjectHandle target = UnrealObjectHandle.Create(4, 9);
                Assert.Equal(
                    [1, 2, 3],
                    bridge.ReadProperty<IReadOnlyList<int>>(target, "property"));

                IReadOnlyList<int> replacement = [7, 11];
                bridge.WriteProperty(target, "property", replacement);
                Assert.Equal(replacement, bridge.ReadProperty<IReadOnlyList<int>>(target, "property"));
            });

        WithPackedCollectionLoopback(
            UnrealInvocationValueKind.Set,
            PackCollection<IReadOnlySet<UnrealVector>>(new HashSet<UnrealVector>
            {
                new(1, 2, 3),
                new(1, 2, 4),
            }),
            transport =>
            {
                var bridge = new NativeUnrealInvocationBridge(transport);
                IReadOnlySet<UnrealVector> decoded = bridge.ReadProperty<IReadOnlySet<UnrealVector>>(
                    UnrealObjectHandle.Create(4, 9),
                    "property");
                Assert.Equal(2, decoded.Count);
                Assert.Contains(new UnrealVector(1, 2, 4), decoded);
            });

        WithPackedCollectionLoopback(
            UnrealInvocationValueKind.Map,
            PackCollection<IReadOnlyDictionary<UnrealName, UnrealSoftObjectHandle>>(
                new Dictionary<UnrealName, UnrealSoftObjectHandle>
                {
                    [new UnrealName("Mesh")] = new("/Game/Props/Cube.Cube"),
                }),
            transport =>
            {
                var bridge = new NativeUnrealInvocationBridge(transport);
                IReadOnlyDictionary<UnrealName, UnrealSoftObjectHandle> decoded =
                    bridge.ReadProperty<IReadOnlyDictionary<UnrealName, UnrealSoftObjectHandle>>(
                        UnrealObjectHandle.Create(4, 9),
                        "property");
                Assert.Equal("/Game/Props/Cube.Cube", decoded.Single().Value.AssetPath);
            });
    }

    [Fact]
    public void PackedCollectionPropertyPathRejectsWrongRootAndMalformedArena()
    {
        byte[] wrongKind = PackCollection<IReadOnlyList<int>>([1, 2]);
        UnrealNativeValueV3 root = MemoryMarshal.Read<UnrealNativeValueV3>(wrongKind);
        root.Kind = UnrealNativeValueKindV3.Set;
        MemoryMarshal.Write(wrongKind.AsSpan(), in root);
        WithPackedCollectionLoopback(
            UnrealInvocationValueKind.Array,
            wrongKind,
            transport => Assert.Throws<UnrealInvocationException>(() =>
                new NativeUnrealInvocationBridge(transport).ReadProperty<IReadOnlyList<int>>(
                    UnrealObjectHandle.Create(4, 9),
                    "property")));

        byte[] wrongType = PackCollection<IReadOnlyList<int>>([1, 2]);
        root = MemoryMarshal.Read<UnrealNativeValueV3>(wrongType);
        root.TypeId = UnrealTypeIdV3.FromPath("TArray<float>");
        MemoryMarshal.Write(wrongType.AsSpan(), in root);
        WithPackedCollectionLoopback(
            UnrealInvocationValueKind.Array,
            wrongType,
            transport => Assert.Throws<UnrealInvocationException>(() =>
                new NativeUnrealInvocationBridge(transport).ReadProperty<IReadOnlyList<int>>(
                    UnrealObjectHandle.Create(4, 9),
                    "property")));

        byte[] truncated = PackCollection<IReadOnlyList<int>>([1, 2]);
        WithPackedCollectionLoopback(
            UnrealInvocationValueKind.Array,
            truncated[..^1],
            transport => Assert.Throws<UnrealInvocationException>(() =>
                new NativeUnrealInvocationBridge(transport).ReadProperty<IReadOnlyList<int>>(
                    UnrealObjectHandle.Create(4, 9),
                    "property")));

        byte[] badOffset = PackCollection<IReadOnlyList<int>>([1, 2]);
        root = MemoryMarshal.Read<UnrealNativeValueV3>(badOffset);
        root.Payload0 = (ulong)badOffset.Length;
        MemoryMarshal.Write(badOffset.AsSpan(), in root);
        WithPackedCollectionLoopback(
            UnrealInvocationValueKind.Array,
            badOffset,
            transport => Assert.Throws<UnrealInvocationException>(() =>
                new NativeUnrealInvocationBridge(transport).ReadProperty<IReadOnlyList<int>>(
                    UnrealObjectHandle.Create(4, 9),
                    "property")));

        byte[] lateLeaf = PackCollection<IReadOnlyList<int>>([1, 2]);
        root = MemoryMarshal.Read<UnrealNativeValueV3>(lateLeaf);
        int secondCellOffset = checked(
            Marshal.SizeOf<UnrealNativeValueV3>() +
            (int)root.Payload0 +
            UnrealNativeAbiV3.ValueSize);
        UnrealNativeValueV3 invalidCell = MemoryMarshal.Read<UnrealNativeValueV3>(
            lateLeaf.AsSpan(secondCellOffset));
        invalidCell.Kind = UnrealNativeValueKindV3.Boolean;
        MemoryMarshal.Write(lateLeaf.AsSpan(secondCellOffset), in invalidCell);
        WithPackedCollectionLoopback(
            UnrealInvocationValueKind.Array,
            lateLeaf,
            transport => Assert.Throws<UnrealInvocationException>(() =>
                new NativeUnrealInvocationBridge(transport).ReadProperty<IReadOnlyList<int>>(
                    UnrealObjectHandle.Create(4, 9),
                "property")));
    }

    [Fact]
    public void PackedCollectionPayloadTakesAnOwnedSnapshotAtCallbackBoundary()
    {
        byte[] source = PackCollection<IReadOnlyList<int>>([13, 17]);
        var bridge = new NativeUnrealInvocationBridge(new OwnedCollectionPayloadTransport(source));

        Assert.Equal(
            [13, 17],
            bridge.ReadProperty<IReadOnlyList<int>>(
                UnrealObjectHandle.Create(4, 9),
                "property"));
    }

    private static byte[] PackCollection<T>(T value)
    {
        byte[] arena = new byte[4096];
        int byteCount = 0;
        Assert.True(UnrealNativeValueCodecV3.TryEncode(
            value,
            arena,
            ref byteCount,
            out UnrealNativeValueV3 root,
            out _));
        int rootSize = Marshal.SizeOf<UnrealNativeValueV3>();
        byte[] payload = new byte[checked(rootSize + byteCount)];
        MemoryMarshal.Write(payload.AsSpan(), in root);
        arena.AsSpan(0, byteCount).CopyTo(payload.AsSpan(rootSize));
        return payload;
    }

    private void WithPackedCollectionLoopback(
        UnrealInvocationValueKind kind,
        byte[] payload,
        Action<InProcessUnrealNativeInvocationTransport> test)
    {
        WithCommonValueLoopback(test, kind, initialBytes: payload);
    }

    private sealed class OwnedCollectionPayloadTransport(byte[] payload)
        : IUnrealNativeInvocationTransport
    {
        public UnrealInvocationResponse ReadProperty(
            UnrealObjectHandle target,
            string propertyPath)
        {
            UnrealInvocationValue value = UnrealInvocationValue.FromContainerPayload(
                UnrealInvocationValueKind.Array,
                payload);
            UnrealNativeValueV3 root = MemoryMarshal.Read<UnrealNativeValueV3>(payload);
            root.TypeId = UnrealTypeIdV3.FromPath("TArray<float>");
            MemoryMarshal.Write(payload.AsSpan(), in root);
            return UnrealInvocationResponse.Succeeded(value);
        }

        public UnrealInvocationResponse WriteProperty(
            UnrealObjectHandle target,
            string propertyPath,
            UnrealInvocationValue value) =>
            UnrealInvocationResponse.Succeeded(UnrealInvocationValue.Void);

        public UnrealInvocationResponse Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<UnrealInvocationValue> arguments) =>
            UnrealInvocationResponse.Failed(
                UnrealInvocationError.FunctionNotFound,
                "Not used by this test.");
    }

    public static IEnumerable<object[]> LegacyCommonValues()
    {
        yield return [UnrealInvocationValue.FromInt8(sbyte.MinValue)];
        yield return [UnrealInvocationValue.FromInt8(sbyte.MaxValue)];
        yield return [UnrealInvocationValue.FromUInt8(byte.MaxValue)];
        yield return [UnrealInvocationValue.FromInt16(short.MinValue)];
        yield return [UnrealInvocationValue.FromInt16(short.MaxValue)];
        yield return [UnrealInvocationValue.FromUInt16(ushort.MaxValue)];
        yield return [UnrealInvocationValue.FromUInt32(uint.MaxValue)];
        yield return [UnrealInvocationValue.FromInt64(long.MinValue)];
        yield return [UnrealInvocationValue.FromInt64(long.MaxValue)];
        yield return [UnrealInvocationValue.FromUInt64(ulong.MaxValue)];
        yield return [UnrealInvocationValue.FromRotator(new(-90, 123.125, 360))];
        yield return [UnrealInvocationValue.FromTransform(new(new(.5, .5, .5, .5), new(11, -22, 33), new(2, 3, 4)))];
        yield return [UnrealInvocationValue.FromEnum(new UnrealEnumValue("/Script/UEUnifiedScriptRuntime.EUnifiedScriptInteropMode", 1))];
        yield return [UnrealInvocationValue.FromEnumTypeId(UnrealMemberId.FromPath("/Script/UEUnifiedScriptRuntime.EUnifiedScriptInteropMode"), 2)];
    }

    [Theory]
    [MemberData(nameof(LegacyCommonValues))]
    public void LegacyCommonValueCodecRoundTripsOwnedPayloads(UnrealInvocationValue value)
    {
        WithCommonValueLoopback(transport =>
        {
            var target = UnrealObjectHandle.Create(4, 9);
            Assert.True(transport.WriteProperty(target, "property", value).IsSuccess);
            GC.Collect();
            Assert.Equal(value, transport.ReadProperty(target, "property").Value);
            Assert.Equal(value, transport.Invoke(target, "function", [value]).Value);
        });
    }

    [Fact]
    public void BoxedTypedEnumArgumentsKeepTheirIdentityThroughTheRealValueCodec()
    {
        WithCommonValueLoopback(transport =>
        {
            var bridge = new NativeUnrealInvocationBridge(transport);
            var target = UnrealObjectHandle.Create(4, 9);
            Assert.Equal(EUnifiedScriptInteropMode.Active,
                bridge.Invoke<EUnifiedScriptInteropMode>(target, "function", [EUnifiedScriptInteropMode.Active]));
            ulong typeId = UnrealMemberId.FromPath("/Script/UEUnifiedScriptRuntime.EUnifiedScriptInteropMode");
            Assert.True(transport.WriteProperty(target, "property", UnrealInvocationValue.FromEnumTypeId(typeId, 2)).IsSuccess);
            Assert.Equal(EUnifiedScriptInteropMode.Suspended, bridge.ReadProperty<EUnifiedScriptInteropMode>(target, "property"));
            Assert.Throws<UnrealInvocationException>(() => bridge.ReadProperty<UnrealEnumValue>(target, "property"));
            Assert.True(transport.WriteProperty(target, "property", UnrealInvocationValue.FromEnumTypeId(typeId + 1, 2)).IsSuccess);
            Assert.Throws<UnrealInvocationException>(() => bridge.ReadProperty<EUnifiedScriptInteropMode>(target, "property"));
        });
    }

    [Theory]
    [InlineData(UnrealInvocationValueKind.Int8, 128, 0)]
    [InlineData(UnrealInvocationValueKind.UInt8, -1, 0)]
    [InlineData(UnrealInvocationValueKind.Int16, 32768, 0)]
    [InlineData(UnrealInvocationValueKind.UInt16, 65536, 0)]
    [InlineData(UnrealInvocationValueKind.Int64, 0, 7)]
    [InlineData(UnrealInvocationValueKind.UInt64, 0, 9)]
    [InlineData(UnrealInvocationValueKind.Rotator, 0, 23)]
    [InlineData(UnrealInvocationValueKind.Transform, 0, 79)]
    [InlineData(UnrealInvocationValueKind.Enum, 0, 15)]
    [InlineData(UnrealInvocationValueKind.Enum, 0, 16)] // zero type ID
    [InlineData(UnrealInvocationValueKind.Enum, 0, 4113)]
    public void LegacyCommonValuesRejectMalformedResults(UnrealInvocationValueKind kind, int cell, int bytes)
    {
        WithCommonValueLoopback(transport => Assert.Throws<InvalidOperationException>(() =>
            transport.ReadProperty(UnrealObjectHandle.Create(4, 9), "property")), kind, cell, new byte[bytes]);
    }

    [Fact]
    public void EnumPayloadRejectsDisagreementBetweenPathAndTypeId()
    {
        byte[] path = System.Text.Encoding.UTF8.GetBytes("/Script/Other.Mode");
        byte[] payload = new byte[16 + path.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), 1);
        path.CopyTo(payload, 16);
        WithCommonValueLoopback(transport => Assert.Throws<InvalidOperationException>(() =>
            transport.ReadProperty(UnrealObjectHandle.Create(4, 9), "property")), UnrealInvocationValueKind.Enum, 0, payload);
    }

    private void WithCommonValueLoopback(Action<InProcessUnrealNativeInvocationTransport> test,
        UnrealInvocationValueKind initialKind = UnrealInvocationValueKind.Void, int initialCell = 0, byte[]? initialBytes = null,
        Action? onWrite = null)
    {
        byte kind = (byte)initialKind;
        int cell = initialCell;
        byte[] bytes = initialBytes ?? [];
        void Store(nint pointer)
        {
            TestValue value = Marshal.PtrToStructure<TestValue>(pointer);
            kind = value.Kind;
            cell = value.Int32Value;
            bytes = new byte[value.StringLength];
            if (bytes.Length > 0) Marshal.Copy(value.StringData, bytes, 0, bytes.Length);
        }
        void Reply(UnrealNativeResultCallbackV1 callback, nint userData) =>
            InvokeCallback(callback, userData, true, (UnrealInvocationValueKind)kind, int32Value: cell, valuePayload: bytes);
        UnrealNativeReadPropertyV1 read = (context, target, path, length, callback, userData) => Reply(callback, userData);
        UnrealNativeWritePropertyV1 write = (context, target, path, length, value, callback, userData) =>
        {
            onWrite?.Invoke();
            Store(value);
            InvokeCallback(callback, userData, true);
        };
        UnrealNativeInvokeV1 invoke = (context, target, path, length, values, count, callback, userData) =>
        {
            Assert.Equal(1, count);
            Store(values);
            Reply(callback, userData);
        };
        var table = new TestTable
        {
            AbiVersion = 1, TableSize = (uint)Marshal.SizeOf<TestTable>(), ValueSize = (uint)Marshal.SizeOf<TestValue>(),
            ReadProperty = Marshal.GetFunctionPointerForDelegate(read), WriteProperty = Marshal.GetFunctionPointerForDelegate(write),
            Invoke = Marshal.GetFunctionPointerForDelegate(invoke), RetainContext = Marshal.GetFunctionPointerForDelegate(_retain),
            ReleaseContext = Marshal.GetFunctionPointerForDelegate(_release),
        };
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TestTable>());
        try
        {
            Marshal.StructureToPtr(table, pointer, false);
            using var transport = new InProcessUnrealNativeInvocationTransport(new nint(0x1234), pointer);
            test(transport);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
            GC.KeepAlive(read); GC.KeepAlive(write); GC.KeepAlive(invoke);
        }
    }
}
