using System.Runtime.InteropServices;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed partial class InProcessUnrealNativeInvocationTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedVariableResultsUseOneIdentityAcrossRetriesAndRetainDisposedContext(bool withOutput)
    {
        using var fixture = new CorrelatedTableFixture(this);
        using var transport = new InProcessUnrealNativeInvocationTransport((nint)0x1234, fixture.Parent);
        fixture.FirstCall = () => transport.Dispose(); // Simulate disposal inside a reverse callback.
        var bridge = new NativeUnrealInvocationBridge(transport);
        var target = UnrealObjectHandle.Create(4, 9);
        const string path = "/Script/Fixture:Reply";
        ulong member = UnrealMemberId.FromPath(path);
        string actual = withOutput
            ? (string)bridge.InvokeWithOutputs(target, member, path, typeof(void),
                [new(typeof(string), UnrealParameterDirection.Output, null)]).Outputs[0]!
            : bridge.Invoke<string>(target, member, path);
        Assert.Equal(new string('x', 9000), actual);
        Assert.Equal(2, fixture.Ids.Count);
        Assert.NotEqual(0UL, fixture.Ids[0]);
        Assert.Equal(fixture.Ids[0], fixture.Ids[1]);
        Assert.Equal(new[] { 1, 1 }, fixture.RetainedDuringCalls);
        Assert.Empty(fixture.Pending);
        Assert.Equal(1, fixture.Releases);
        Assert.Equal(0, _retainedContexts);
        Assert.Throws<ObjectDisposedException>(() => bridge.Invoke<string>(target, member, path));
    }

    [Fact]
    public void CorrelatedScopesSeparateCallsAndReleaseAbandonedOrMalformedResponses()
    {
        using var fixture = new CorrelatedTableFixture(this);
        using var transport = new InProcessUnrealNativeInvocationTransport((nint)0x1234, fixture.Parent);
        var factory = (IUnrealNativeCallFrameScopeFactory)transport;
        using (var first = factory.BeginCallFrameInvocation()!)
        using (var second = factory.BeginCallFrameInvocation()!)
        {
            var results = new UnrealNativeValueV3[1];
            UnrealNativeValueCodecV3.TryCreateResultDescriptor<string>(out results[0], out _);
            var target = UnrealObjectHandle.Create(4, 9);
            Assert.Equal(UnrealNativeCallStatusV3.BufferTooSmall,
                first.ExecuteById(UnrealNativeOperationV3.Invoke, target, 1, [], results, new byte[64], 0).Status);
            Assert.Equal(UnrealNativeCallStatusV3.BufferTooSmall,
                second.ExecuteById(UnrealNativeOperationV3.Invoke, target, 1, [], results, new byte[64], 0).Status);
            Assert.NotEqual(fixture.Ids[0], fixture.Ids[1]);
            Assert.Equal(2, fixture.Pending.Count);
        }
        Assert.Empty(fixture.Pending);
        Assert.Equal(2, fixture.Releases);
        fixture.Malformed = true;
        var bridge = new NativeUnrealInvocationBridge(transport);
        const string path = "/Script/Fixture:Reply";
        Assert.Throws<InvalidOperationException>(() => bridge.Invoke<string>(
            UnrealObjectHandle.Create(4, 9), UnrealMemberId.FromPath(path), path));
        Assert.Empty(fixture.Pending);
        Assert.Equal(3, fixture.Releases);
    }

    [Theory]
    [InlineData(80U, false, true)]
    [InlineData(96U, true, true)]
    [InlineData(80U, true, false)]
    public void CorrelatedSuffixRequiresCapabilitySizeAndBothEntrypoints(uint size, bool missingEntry, bool capability)
    {
        using var fixture = new CorrelatedTableFixture(this, size, missingEntry, capability);
        if (capability)
        {
            Assert.Throws<InvalidOperationException>(() =>
                new InProcessUnrealNativeInvocationTransport((nint)0x1234, fixture.Parent));
            Assert.Equal(0, _retainedContexts);
        }
        else
        {
            using var transport = new InProcessUnrealNativeInvocationTransport((nint)0x1234, fixture.Parent);
            Assert.Null(((IUnrealNativeCallFrameScopeFactory)transport).BeginCallFrameInvocation());
        }
        Assert.Equal(96, Marshal.SizeOf<UnrealNativeCorrelatedTransportTableV3>());
        Assert.Equal((nint)80, Marshal.OffsetOf<UnrealNativeCorrelatedTransportTableV3>(nameof(UnrealNativeCorrelatedTransportTableV3.InvokeCorrelatedById)));
        Assert.Equal((nint)88, Marshal.OffsetOf<UnrealNativeCorrelatedTransportTableV3>(nameof(UnrealNativeCorrelatedTransportTableV3.ReleaseCorrelatedResult)));
    }

    private sealed unsafe class CorrelatedTableFixture : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Invoke(nint context, ulong id, ulong target, ulong member, UnrealNativeCallFrameV3* frame);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Release(nint context, ulong id);
        private readonly Invoke _invoke;
        private readonly Release _release;
        private readonly nint _table;
        internal readonly nint Parent;
        internal readonly HashSet<ulong> Pending = [];
        internal readonly List<ulong> Ids = [];
        internal readonly List<int> RetainedDuringCalls = [];
        internal Action? FirstCall;
        internal bool Malformed;
        internal int Releases;

        internal CorrelatedTableFixture(InProcessUnrealNativeInvocationTransportTests owner,
            uint size = 96, bool missingEntry = false, bool capability = true)
        {
            _invoke = (_, id, _, _, frame) =>
            {
                bool first = Pending.Add(id);
                Ids.Add(id);
                if (first) FirstCall?.Invoke();
                RetainedDuringCalls.Add(owner._retainedContexts);
                if (Malformed)
                {
                    frame->RequiredByteCapacity = uint.MaxValue;
                    frame->Status = UnrealNativeCallStatusV3.BufferTooSmall;
                    return;
                }
                if (frame->ByteCapacity < 9000)
                {
                    frame->ResultCount = 0;
                    frame->RequiredResultCapacity = 1;
                    frame->RequiredByteCapacity = 9000;
                    frame->Status = UnrealNativeCallStatusV3.BufferTooSmall;
                    return;
                }
                int cursor = 0;
                var arena = new Span<byte>((void*)frame->ByteBuffer, (int)frame->ByteCapacity);
                if (!UnrealNativeValueCodecV3.TryEncode(new string('x', 9000), arena, ref cursor, out var result, out _))
                { frame->Status = UnrealNativeCallStatusV3.InvalidFrame; return; }
                *(UnrealNativeValueV3*)frame->Results = result;
                frame->ResultCount = 1;
                frame->ByteCount = (uint)cursor;
                frame->Status = UnrealNativeCallStatusV3.Success;
                Pending.Remove(id);
            };
            _release = (_, id) => { Releases++; Pending.Remove(id); };
            nint legacy = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, ulong, UnrealNativeCallFrameV3*, void>)&ExecuteByIdV3Unmanaged;
            var table = new UnrealNativeCorrelatedTransportTableV3
            {
                Base = new() { Base = new() { Base = new()
                {
                    AbiVersion = 3, TableSize = size, ValueSize = 48, CallFrameSize = 72,
                    Capabilities = UnrealNativeCapabilitiesV3.FixedScalars | UnrealNativeCapabilitiesV3.Utf8Text |
                        UnrealNativeCapabilitiesV3.Utf8Results | UnrealNativeCapabilitiesV3.OutputAndReference |
                        (capability ? UnrealNativeCapabilitiesV3.CorrelatedResults : 0),
                    RetainContext = Marshal.GetFunctionPointerForDelegate(owner._retain),
                    ReleaseContext = Marshal.GetFunctionPointerForDelegate(owner._release),
                    ReadPropertyById = legacy, WritePropertyById = legacy, InvokeById = legacy,
                } } },
                InvokeCorrelatedById = Marshal.GetFunctionPointerForDelegate(_invoke),
                ReleaseCorrelatedResult = missingEntry ? 0 : Marshal.GetFunctionPointerForDelegate(_release),
            };
            _table = Marshal.AllocHGlobal(Marshal.SizeOf<UnrealNativeCorrelatedTransportTableV3>());
            Marshal.StructureToPtr(table, _table, false);
            Parent = Marshal.AllocHGlobal(Marshal.SizeOf<TestTableV2Extended>());
            Marshal.StructureToPtr(new TestTableV2Extended
            {
                AbiVersion = 2, TableSize = (uint)Marshal.SizeOf<TestTableV2Extended>(), ValueSize = (uint)Marshal.SizeOf<TestValue>(),
                ReadProperty = Marshal.GetFunctionPointerForDelegate(owner._read),
                WriteProperty = Marshal.GetFunctionPointerForDelegate(owner._write),
                Invoke = Marshal.GetFunctionPointerForDelegate(owner._invoke),
                RetainContext = Marshal.GetFunctionPointerForDelegate(owner._retain),
                ReleaseContext = Marshal.GetFunctionPointerForDelegate(owner._release),
                ReadPropertyById = Marshal.GetFunctionPointerForDelegate(owner._fastRead),
                WritePropertyById = Marshal.GetFunctionPointerForDelegate(owner._fastWrite),
                InvokeById = Marshal.GetFunctionPointerForDelegate(owner._fastInvoke), CallFrameTransport = _table,
            }, Parent, false);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Parent);
            Marshal.FreeHGlobal(_table);
            GC.KeepAlive(_invoke); GC.KeepAlive(_release);
        }
    }
}
