using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UnifiedScript.Unreal;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Tests;

public sealed partial class InProcessUnrealNativeInvocationTransportTests
{
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
        UnrealInvocationValueKind initialKind = UnrealInvocationValueKind.Void, int initialCell = 0, byte[]? initialBytes = null)
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
