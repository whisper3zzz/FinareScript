using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed partial class InProcessUnrealNativeInvocationTransportTests
{
    [ThreadStatic] private static NativeObjectAbiFixture? _nativeObjectFixture;

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

        public unsafe NativeObjectAbiFixture(InProcessUnrealNativeInvocationTransportTests owner)
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
                AbiVersion = 1, TableSize = 64, ValueSize = 48,
                RetainContext = parent.Base.RetainContext, ReleaseContext = parent.Base.ReleaseContext,
                OpenScope = (nint)(delegate* unmanaged[Cdecl]<nint, ulong*, byte>)&NativeObjectOpen,
                CloseScope = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, byte>)&NativeObjectClose,
                ReleaseObject = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, ulong, byte>)&NativeObjectRelease,
                Invoke = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, ulong, ulong, UnrealNativeValueV3*, uint, UnrealNativeValueV3*, ulong*, byte>)&NativeObjectInvoke,
            };
            Extension = Marshal.AllocHGlobal(Marshal.SizeOf<UnrealNativeObjectTransportTableV1>());
            Marshal.StructureToPtr(table, Extension, false);
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
}
