using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Finare.Unreal;

namespace Finare.Tests;

public sealed partial class InProcessUnrealNativeInvocationTransportTests
{
    [ThreadStatic] private static NativeObjectAbiFixture? _nativeObjectFixture;
    [ThreadStatic] private static TextTailAbiFixture? _textTailFixture;

    [StructLayout(LayoutKind.Sequential)]
    private struct TestNativeObjectExtension
    {
        public TestTableV2AsyncServices Base;
        public nint NativeObjectTransport;
    }

    [Fact]
    public unsafe void NativeObjectOptionalTableInvokesAndDisposesOutstandingScopes()
    {
        using var fixture = new NativeObjectAbiFixture(this);
        var transport = new InProcessUnrealNativeInvocationTransport(new(0x1234), fixture.Parent);
        Assert.True(transport.SupportsNativeObjects);
        Assert.False(transport.SupportsNativeObjectArguments);
        var scope = new UnrealNativeObjectScope(transport);
        var lease = scope.Construct(42, [new() { Kind = UnrealNativeValueKindV3.Int32, Payload0 = 17 }]);
        Assert.Equal(new UnrealNativeObjectHandle(700), lease.Handle);
        Assert.Equal(17UL, lease.Invoke(43, [new() { Kind = UnrealNativeValueKindV3.Int32, Payload0 = 17 }]).Value.Payload0);
        Assert.Equal(2, fixture.Calls);
        Assert.Equal(1, fixture.Open);
        fixture.CloseStatus = UnrealNativeObjectStatus.WrongThread;
        Assert.Throws<UnrealNativeObjectException>(() => transport.Dispose());
        Assert.Equal(1, _retainedContexts);
        fixture.CloseStatus = UnrealNativeObjectStatus.Success;
        transport.Dispose();
        Assert.Equal(0, fixture.Open);
        Assert.Equal(0, _retainedContexts);
        Assert.Equal(UnrealNativeObjectStatus.Unavailable,
            Assert.Throws<UnrealNativeObjectException>(() => lease.Invoke(43, [])).Status);
        lease.Dispose();
        scope.Dispose();
    }

    [Theory]
    [InlineData(0UL, false)]
    [InlineData(1UL, true)]
    [InlineData(2UL, false)]
    public void NativeObjectArgumentsUseTheOptionalCapabilityTail(ulong flags, bool supported)
    {
        using var fixture = new NativeObjectAbiFixture(this, flags);
        using var transport = new InProcessUnrealNativeInvocationTransport(new(0x1234), fixture.Parent);
        Assert.Equal(supported, transport.SupportsNativeObjectArguments);
        using var scope = new UnrealNativeObjectScope(transport);
        var argument = new UnrealNativeValueV3 { Kind = UnrealNativeValueKindV3.NativeObject, TypeId = 42, Payload0 = 7 };
        if (supported) scope.Invoke(default, 123, [argument]);
        else Assert.Equal(UnrealNativeObjectStatus.Unavailable,
            Assert.Throws<UnrealNativeObjectException>(() => scope.Invoke(default, 123, [argument])).Status);
        Assert.Equal(supported ? 1 : 0, fixture.Calls);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("size")]
    [InlineData("value")]
    [InlineData("reserved")]
    [InlineData("context")]
    [InlineData("missing")]
    public void NativeObjectOptionalTableRejectsIncompatibleLayoutBeforeRetaining(string change)
    {
        using var fixture = new NativeObjectAbiFixture(this);
        var table = Marshal.PtrToStructure<UnrealNativeObjectTransportTableV1>(fixture.Extension);
        switch (change)
        {
            case "version": table.AbiVersion++; break;
            case "size": table.TableSize = 16; break;
            case "value": table.ValueSize++; break;
            case "reserved": table.Reserved = 1; break;
            case "context": table.ReleaseContext = 0; break;
            case "missing": table.Invoke = 0; break;
        }
        Marshal.StructureToPtr(table, fixture.Extension, false);
        Assert.Throws<InvalidOperationException>(() => new InProcessUnrealNativeInvocationTransport(new(0x1234), fixture.Parent));
        Assert.Equal(0, _retainedContexts);
    }

    [Theory]
    [InlineData(64u, 0ul)]
    [InlineData(72u, 1ul)]
    public unsafe void OldNativeObjectTablesKeepFixedCallsButRefuseText(uint tableSize, ulong capabilities)
    {
        using var fixture = new TextTailAbiFixture(this, tableSize, capabilities, includeTextTail: false);
        using var transport = new InProcessUnrealNativeInvocationTransport(new(0x1234), fixture.Parent);
        Assert.True(transport.SupportsNativeObjects);
        Assert.False(transport.SupportsNativeObjectText);

        Assert.Equal(UnrealNativeObjectStatus.Success,
            transport.OpenNativeObjectScope(out ulong scope));
        try
        {
            Assert.Equal(UnrealNativeObjectStatus.Success,
                transport.InvokeNativeObject(scope, default, 17, [], out _));
            Assert.Equal(1, fixture.FixedCalls);
            Assert.Equal(UnrealNativeObjectStatus.TextUnavailable,
                transport.BeginNativeObjectText(scope, default, 18, [], [], out ulong token, out uint required));
            Assert.Equal(0UL, token);
            Assert.Equal(0u, required);
            Assert.Equal(0, fixture.BeginCalls);
        }
        finally
        {
            Assert.Equal(UnrealNativeObjectStatus.Success, transport.CloseNativeObjectScope(scope));
        }
    }

    [Fact]
    public unsafe void ValidNativeObjectTextTailIsDiscoveredAndRead()
    {
        using var fixture = new TextTailAbiFixture(this, 80, 2, includeTextTail: true);
        using var transport = new InProcessUnrealNativeInvocationTransport(new(0x1234), fixture.Parent);
        Assert.True(transport.SupportsNativeObjectText);
        Assert.False(transport.SupportsNativeObjectArguments);
        Assert.Equal(UnrealNativeObjectStatus.Success,
            transport.OpenNativeObjectScope(out ulong scope));
        try
        {
            Assert.Equal(UnrealNativeObjectStatus.Success,
                transport.BeginNativeObjectText(scope, default, 19, [], [], out ulong token, out uint required));
            Assert.Equal(71UL, token);
            Assert.Equal(3u, required);
            byte[] output = new byte[3];
            Assert.Equal(UnrealNativeObjectStatus.Success,
                transport.ReadNativeObjectText(scope, token, output, out uint readRequired, out var result));
            Assert.Equal(3u, readRequired);
            Assert.Equal("abc", System.Text.Encoding.ASCII.GetString(output));
            Assert.Equal(UnrealNativeValueKindV3.String, result.Value.Kind);
            Assert.Equal(1, fixture.BeginCalls);
            Assert.Equal(1, fixture.ReadCalls);
        }
        finally
        {
            Assert.Equal(UnrealNativeObjectStatus.Success, transport.CloseNativeObjectScope(scope));
        }
    }

    [Theory]
    [InlineData(79u, 56u, 48u)]
    [InlineData(80u, 32u, 48u)]
    [InlineData(80u, 56u, 47u)]
    public void NativeObjectTextTailRejectsTruncatedOrMalformedLayout(uint outerTableSize, uint textTableSize, uint valueSize)
    {
        using var fixture = new TextTailAbiFixture(this, outerTableSize, 2, includeTextTail: true,
            textTableSize, valueSize);
        if (outerTableSize < 80)
        {
            using var transport = new InProcessUnrealNativeInvocationTransport(new(0x1234), fixture.Parent);
            Assert.False(transport.SupportsNativeObjectText);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() =>
                new InProcessUnrealNativeInvocationTransport(new(0x1234), fixture.Parent));
        }
    }

    [Fact]
    public void NativeObjectTextBeginDiscardsOversizedRequiredResult()
    {
        using var fixture = new TextTailAbiFixture(this, 80, 2, includeTextTail: true)
        {
            RequiredBytes = 16u * 1024u * 1024u + 1u,
        };
        using var transport = new InProcessUnrealNativeInvocationTransport(new(0x1234), fixture.Parent);
        Assert.Equal(UnrealNativeObjectStatus.Success,
            transport.OpenNativeObjectScope(out ulong scope));
        try
        {
            Assert.Equal(UnrealNativeObjectStatus.InvalidArgument,
                transport.BeginNativeObjectText(scope, default, 20, [], [], out ulong token, out uint required));
            Assert.Equal(0UL, token);
            Assert.Equal(16u * 1024u * 1024u + 1u, required);
            Assert.Equal(1, fixture.BeginCalls);
            Assert.Equal(1, fixture.DiscardCalls);
        }
        finally
        {
            Assert.Equal(UnrealNativeObjectStatus.Success, transport.CloseNativeObjectScope(scope));
        }
    }

    [Fact]
    public void NativeObjectTextOnlyCapabilityRejectsObjectCellsBeforeNativeBegin()
    {
        using var fixture = new TextTailAbiFixture(this, 80, 2, includeTextTail: true);
        using var transport = new InProcessUnrealNativeInvocationTransport(new(0x1234), fixture.Parent);
        Assert.Equal(UnrealNativeObjectStatus.Success,
            transport.OpenNativeObjectScope(out ulong scope));
        try
        {
            var argument = new UnrealNativeValueV3
            {
                Kind = UnrealNativeValueKindV3.NativeObject,
                TypeId = 7,
                Payload0 = 9,
            };
            Assert.Equal(UnrealNativeObjectStatus.TextUnavailable,
                transport.BeginNativeObjectText(scope, default, 21, [argument], [], out ulong token, out uint required));
            Assert.Equal(0UL, token);
            Assert.Equal(0u, required);
            Assert.Equal(0, fixture.BeginCalls);
        }
        finally
        {
            Assert.Equal(UnrealNativeObjectStatus.Success, transport.CloseNativeObjectScope(scope));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe byte NativeObjectOpen(nint context, ulong* scope)
    {
        var fixture = _nativeObjectFixture!;
        *scope = (ulong)++fixture.Open;
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte NativeObjectClose(nint context, ulong scope)
    {
        var fixture = _nativeObjectFixture!;
        if (fixture.CloseStatus == UnrealNativeObjectStatus.Success) fixture.Open--;
        return (byte)fixture.CloseStatus;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte NativeObjectRelease(nint context, ulong scope, ulong handle) => 0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe byte NativeObjectInvoke(nint context, ulong scope, ulong target, ulong member,
        UnrealNativeValueV3* arguments, uint count, UnrealNativeValueV3* value, ulong* handle)
    {
        _nativeObjectFixture!.Calls++;
        *handle = target == 0 ? 700UL : 0;
        *value = count == 0 ? default : arguments[0];
        return 0;
    }

    private sealed class NativeObjectAbiFixture : IDisposable
    {
        public nint Parent { get; }
        public nint Extension { get; }
        public int Open, Calls;
        public UnrealNativeObjectStatus CloseStatus;

        public unsafe NativeObjectAbiFixture(InProcessUnrealNativeInvocationTransportTests owner, ulong? capabilities = null)
        {
            _nativeObjectFixture = this;
            var parent = new TestNativeObjectExtension
            {
                Base = new()
                {
                    AbiVersion = 2, TableSize = (uint)Marshal.SizeOf<TestNativeObjectExtension>(),
                    ValueSize = (uint)Marshal.SizeOf<TestValue>(),
                    ReadProperty = Marshal.GetFunctionPointerForDelegate(owner._read),
                    WriteProperty = Marshal.GetFunctionPointerForDelegate(owner._write),
                    Invoke = Marshal.GetFunctionPointerForDelegate(owner._invoke),
                    ReadPropertyById = Marshal.GetFunctionPointerForDelegate(owner._fastRead),
                    WritePropertyById = Marshal.GetFunctionPointerForDelegate(owner._fastWrite),
                    InvokeById = Marshal.GetFunctionPointerForDelegate(owner._fastInvoke),
                    RetainContext = Marshal.GetFunctionPointerForDelegate(owner._retain),
                    ReleaseContext = Marshal.GetFunctionPointerForDelegate(owner._release),
                },
            };
            var table = new UnrealNativeObjectTransportTableV1
            {
                AbiVersion = 1, TableSize = capabilities is null ? 64u : 72u, ValueSize = 48,
                RetainContext = parent.Base.RetainContext, ReleaseContext = parent.Base.ReleaseContext,
                OpenScope = (nint)(delegate* unmanaged[Cdecl]<nint, ulong*, byte>)&NativeObjectOpen,
                CloseScope = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, byte>)&NativeObjectClose,
                ReleaseObject = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, ulong, byte>)&NativeObjectRelease,
                Invoke = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, ulong, ulong, UnrealNativeValueV3*, uint, UnrealNativeValueV3*, ulong*, byte>)&NativeObjectInvoke,
            };
            Extension = Marshal.AllocHGlobal((int)table.TableSize);
            Marshal.StructureToPtr(table, Extension, false);
            if (capabilities is ulong flags) Marshal.WriteInt64(Extension, 64, unchecked((long)flags));
            parent.NativeObjectTransport = Extension;
            Parent = Marshal.AllocHGlobal(Marshal.SizeOf<TestNativeObjectExtension>());
            Marshal.StructureToPtr(parent, Parent, false);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Parent);
            Marshal.FreeHGlobal(Extension);
            _nativeObjectFixture = null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe byte TextTailOpen(nint context, ulong* scope)
    {
        var fixture = _textTailFixture!;
        fixture.OpenCalls++;
        *scope = 1;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte TextTailClose(nint context, ulong scope)
    {
        _textTailFixture!.CloseCalls++;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte TextTailRelease(nint context, ulong scope, ulong handle) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe byte TextTailInvoke(nint context, ulong scope, ulong target, ulong member,
        UnrealNativeValueV3* arguments, uint count, UnrealNativeValueV3* value, ulong* handle)
    {
        var fixture = _textTailFixture!;
        fixture.FixedCalls++;
        *value = count == 0 ? default : arguments[0];
        *handle = 0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe byte TextTailBegin(nint context, ulong scope, ulong target, ulong member,
        UnrealNativeValueV3* arguments, uint count, byte* input, uint inputBytes, ulong* token, uint* requiredBytes)
    {
        var fixture = _textTailFixture!;
        fixture.BeginCalls++;
        *token = fixture.Token;
        *requiredBytes = fixture.RequiredBytes;
        return (byte)fixture.BeginStatus;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe byte TextTailRead(nint context, ulong scope, ulong token, byte* output, uint capacity,
        uint* requiredBytes, UnrealNativeValueV3* value, ulong* handle)
    {
        var fixture = _textTailFixture!;
        fixture.ReadCalls++;
        *requiredBytes = 3;
        if (capacity < 3) return (byte)UnrealNativeObjectStatus.TextBufferTooSmall;
        output[0] = (byte)'a'; output[1] = (byte)'b'; output[2] = (byte)'c';
        *value = new UnrealNativeValueV3
        {
            Kind = UnrealNativeValueKindV3.String,
            Flags = UnrealNativeValueFlagsV3.ArenaBacked,
            Payload1 = 3,
        };
        *handle = 0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte TextTailDiscard(nint context, ulong scope, ulong token)
    {
        _textTailFixture!.DiscardCalls++;
        return 0;
    }

    private sealed class TextTailAbiFixture : IDisposable
    {
        public nint Parent { get; }
        public nint Extension { get; }
        public nint Tail { get; }
        public ulong Token = 71;
        public uint RequiredBytes = 3;
        public UnrealNativeObjectStatus BeginStatus = UnrealNativeObjectStatus.Success;
        public int OpenCalls, CloseCalls, FixedCalls, BeginCalls, ReadCalls, DiscardCalls;

        public unsafe TextTailAbiFixture(InProcessUnrealNativeInvocationTransportTests owner,
            uint outerTableSize, ulong capabilities, bool includeTextTail, uint textTableSize = 56, uint valueSize = 48)
        {
            _textTailFixture = this;
            var parent = new TestNativeObjectExtension
            {
                Base = new TestTableV2AsyncServices
                {
                    AbiVersion = 2,
                    TableSize = (uint)Marshal.SizeOf<TestNativeObjectExtension>(),
                    ValueSize = (uint)Marshal.SizeOf<TestValue>(),
                    ReadProperty = Marshal.GetFunctionPointerForDelegate(owner._read),
                    WriteProperty = Marshal.GetFunctionPointerForDelegate(owner._write),
                    Invoke = Marshal.GetFunctionPointerForDelegate(owner._invoke),
                    RetainContext = Marshal.GetFunctionPointerForDelegate(owner._retain),
                    ReleaseContext = Marshal.GetFunctionPointerForDelegate(owner._release),
                    ReadPropertyById = Marshal.GetFunctionPointerForDelegate(owner._fastRead),
                    WritePropertyById = Marshal.GetFunctionPointerForDelegate(owner._fastWrite),
                    InvokeById = Marshal.GetFunctionPointerForDelegate(owner._fastInvoke),
                },
            };
            var tail = new UnrealNativeObjectTextTransportTableV1
            {
                AbiVersion = 1,
                TableSize = textTableSize,
                ValueSize = valueSize,
                MaxInputBytes = 16u * 1024u * 1024u,
                MaxOutputBytes = 16u * 1024u * 1024u,
                MaxPendingBytes = 16u * 1024u * 1024u,
                MaxPendingCount = 64,
                Begin = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, ulong, ulong, UnrealNativeValueV3*, uint, byte*, uint, ulong*, uint*, byte>)&TextTailBegin,
                Read = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, ulong, byte*, uint, uint*, UnrealNativeValueV3*, ulong*, byte>)&TextTailRead,
                Discard = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, ulong, byte>)&TextTailDiscard,
            };
            Tail = Marshal.AllocHGlobal(Marshal.SizeOf<UnrealNativeObjectTextTransportTableV1>());
            Marshal.StructureToPtr(tail, Tail, false);
            var table = new UnrealNativeObjectTransportTableWithText
            {
                Base = new UnrealNativeObjectTransportTableWithCapabilities
                {
                    Base = new UnrealNativeObjectTransportTableV1
                    {
                        AbiVersion = 1,
                        TableSize = outerTableSize,
                        ValueSize = UnrealNativeAbiV3.ValueSize,
                        RetainContext = parent.Base.RetainContext,
                        ReleaseContext = parent.Base.ReleaseContext,
                        OpenScope = (nint)(delegate* unmanaged[Cdecl]<nint, ulong*, byte>)&TextTailOpen,
                        CloseScope = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, byte>)&TextTailClose,
                        ReleaseObject = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, ulong, byte>)&TextTailRelease,
                        Invoke = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, ulong, ulong, UnrealNativeValueV3*, uint, UnrealNativeValueV3*, ulong*, byte>)&TextTailInvoke,
                    },
                    Capabilities = capabilities,
                },
                TextTransport = includeTextTail ? Tail : 0,
            };
            Extension = Marshal.AllocHGlobal(Marshal.SizeOf<UnrealNativeObjectTransportTableWithText>());
            Marshal.StructureToPtr(table, Extension, false);
            parent.NativeObjectTransport = Extension;
            Parent = Marshal.AllocHGlobal(Marshal.SizeOf<TestNativeObjectExtension>());
            Marshal.StructureToPtr(parent, Parent, false);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Parent);
            Marshal.FreeHGlobal(Extension);
            Marshal.FreeHGlobal(Tail);
            _textTailFixture = null;
        }
    }
}
