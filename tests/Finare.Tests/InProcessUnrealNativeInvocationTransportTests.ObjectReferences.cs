using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Finare.Unreal;

namespace Finare.Tests;

public sealed partial class InProcessUnrealNativeInvocationTransportTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TestObjectReferenceExtension
    {
        public TestTableV2AsyncServices Base;
        public nint NativeObjectTransport;
        public nint CallbackTransport;
        public nint ObjectReferenceTransport;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestObjectReferenceTable
    {
        public uint AbiVersion;
        public uint TableSize;
        public uint ValueSize;
        public uint Reserved;
        public nint RetainContext;
        public nint ReleaseContext;
        public nint OpenScope;
        public nint CloseScope;
        public nint Retain;
        public nint Release;
    }

    [ThreadStatic]
    private static ObjectReferenceFixture? _objectReferenceFixture;

    [Fact]
    public void UObjectReferenceLeaseIsGenerationOwnedAndIndependent()
    {
        using var fixture = new ObjectReferenceFixture(this);
        using var transport = new InProcessUnrealNativeInvocationTransport(new(0x1234), fixture.Parent);
        Assert.True(transport.SupportsObjectReferences);
        using var generation = new UnrealGenerationInvocationBridge(new NativeUnrealInvocationBridge(transport));
        UnrealObjectHandle handle = UnrealObjectHandle.Create(8, 33);

        using (UnrealObjectReference first = UnrealObjectReference.Retain(generation, handle))
        using (UnrealObjectReference second = UnrealObjectReference.Retain(generation, handle))
        {
            Assert.Equal(handle, first.Handle);
            Assert.Equal(1, fixture.OpenCalls);
            Assert.Equal(2, fixture.RetainCalls);
            first.Dispose();
            first.Dispose();
            Assert.Equal(1, fixture.ReleaseCalls);
        }

        Assert.Equal(2, fixture.ReleaseCalls);
        generation.Dispose();
        Assert.Equal(1, fixture.CloseCalls);
        Assert.Equal(0, fixture.ActiveScopes);
    }

    [Fact]
    public void UObjectReferenceHandleRejectsDisposedLeaseOrGenerationScope()
    {
        using var fixture = new ObjectReferenceFixture(this);
        using var transport = new InProcessUnrealNativeInvocationTransport(new(0x1234), fixture.Parent);
        using var generation = new UnrealGenerationInvocationBridge(new NativeUnrealInvocationBridge(transport));
        UnrealObjectHandle handle = UnrealObjectHandle.Create(9, 34);
        UnrealObjectReference lease = UnrealObjectReference.Retain(generation, handle);

        Assert.Equal(handle, lease.Handle);
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = lease.Handle);

        UnrealObjectReference scopeLease = UnrealObjectReference.Retain(generation, handle);
        generation.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = scopeLease.Handle);
        scopeLease.Dispose();
    }

    [Fact]
    public void GenerationsSharingOneWorldBridgeReceiveIndependentReferenceScopes()
    {
        using var fixture = new ObjectReferenceFixture(this);
        using var transport = new InProcessUnrealNativeInvocationTransport(new(0x1234), fixture.Parent);
        var worldBridge = new NativeUnrealInvocationBridge(transport);
        using var firstGeneration = new UnrealGenerationInvocationBridge(worldBridge);
        using var secondGeneration = new UnrealGenerationInvocationBridge(worldBridge);
        UnrealObjectHandle handle = UnrealObjectHandle.Create(11, 35);

        UnrealObjectReference firstLease = UnrealObjectReference.Retain(firstGeneration, handle);
        UnrealObjectReference secondLease = UnrealObjectReference.Retain(secondGeneration, handle);
        Assert.Equal(2, fixture.OpenCalls);
        Assert.Equal(2, fixture.ActiveScopes);

        firstGeneration.Dispose();
        Assert.Equal(1, fixture.CloseCalls);
        Assert.Equal(handle, secondLease.Handle);

        secondGeneration.Dispose();
        Assert.Equal(2, fixture.CloseCalls);
        Assert.Equal(0, fixture.ActiveScopes);

        // The generation cleanup closed both scopes. Lease cleanup must remain
        // idempotent and must not release the other generation's scope.
        firstLease.Dispose();
        secondLease.Dispose();
    }

    [Fact]
    public void WorldBridgeCannotCreateAnUnownedReferenceLease()
    {
        using var fixture = new ObjectReferenceFixture(this);
        using var transport = new InProcessUnrealNativeInvocationTransport(new(0x1234), fixture.Parent);
        var worldBridge = new NativeUnrealInvocationBridge(transport);

        Assert.Throws<NotSupportedException>(() =>
            UnrealObjectReference.Retain(worldBridge, UnrealObjectHandle.Create(12, 36)));
        Assert.Equal(0, fixture.OpenCalls);
        Assert.Equal(0, fixture.ActiveScopes);
    }

    [Fact]
    public unsafe void BorrowedEventUObjectHandleRequiresExplicitLeaseAfterCallback()
    {
        var registrations = new List<EventRegistration>();
        using var fixture = new ObjectReferenceFixture(this);
        UnrealNativeSubscribeEventV4 subscribe =
            (_, source, eventId, _, _, callback, userData) =>
            {
                var registration = new EventRegistration(
                    1, source, eventId, callback, userData);
                registrations.Add(registration);
                return registration.SubscriptionId;
            };
        UnrealNativeUnsubscribeEventV4 unsubscribe = (_, _) => 1;
        var eventTable = new UnrealNativeEventTransportTableV4
        {
            AbiVersion = UnrealNativeEventAbiV4.Version,
            TableSize = UnrealNativeEventAbiV4.TransportTableSize,
            FrameSize = UnrealNativeEventAbiV4.FrameSize,
            ValueSize = UnrealNativeAbiV3.ValueSize,
            RetainContext = Marshal.GetFunctionPointerForDelegate(_retain),
            ReleaseContext = Marshal.GetFunctionPointerForDelegate(_release),
            Subscribe = Marshal.GetFunctionPointerForDelegate(subscribe),
            Unsubscribe = Marshal.GetFunctionPointerForDelegate(unsubscribe),
        };
        nint eventTablePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnrealNativeEventTransportTableV4>());
        Marshal.StructureToPtr(eventTable, eventTablePointer, false);
        var parent = Marshal.PtrToStructure<TestObjectReferenceExtension>(fixture.Parent);
        parent.Base.EventTransport = eventTablePointer;
        parent.Base.ReadProperty = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, nint, int, nint, nint, void>)&EventObjectRead;
        Marshal.StructureToPtr(parent, fixture.Parent, false);
        try
        {
            using var transport = new InProcessUnrealNativeInvocationTransport(new(0x1234), fixture.Parent);
            using var generation = new UnrealGenerationInvocationBridge(new NativeUnrealInvocationBridge(transport));
            UnrealObjectHandle eventHandle = UnrealObjectHandle.Create(10, 20);
            UnrealEventDescriptor descriptor = new(
                UnrealMemberId.FromPath(ReflectedEventPath), ReflectedEventPath,
                ReflectedEventPath + "__DelegateSignature", typeof(void),
                UnrealDelegateKind.Multicast,
                [new UnrealEventParameterDescriptor("Object", typeof(UnrealObjectHandle), UnrealParameterDirection.Input)]);
            UnrealObjectReference? retained = null;
            IUnrealEventSubscription subscription = generation.Subscribe(
                eventHandle, descriptor,
                (Action<UnrealObjectHandle>)(value => retained = UnrealObjectReference.Retain(generation, value)));
            EventRegistration registration = Assert.Single(registrations);
            DispatchObject(registration, eventHandle);
            Assert.NotNull(retained);
            Assert.Equal(1, fixture.ActiveLeases);
            Assert.True(new NativeUnrealInvocationBridge(transport).ReadProperty<int>(eventHandle, "LeaseProbe").Equals(1));
            retained!.Dispose();
            Assert.Equal(0, fixture.ActiveLeases);
            Assert.Throws<UnrealInvocationException>(() =>
                new NativeUnrealInvocationBridge(transport).ReadProperty<int>(eventHandle, "LeaseProbe"));
            subscription.Dispose();

            // The event argument itself is borrowed. Without an explicit lease,
            // the callback's registration is released at callback return and a
            // later bridge read must reject the stale handle.
            IUnrealEventSubscription unretainedSubscription = generation.Subscribe(
                eventHandle, descriptor, (Action<UnrealObjectHandle>)(_ => { }));
            EventRegistration unretainedRegistration = Assert.Single(registrations.Skip(1));
            DispatchObject(unretainedRegistration, eventHandle);
            Assert.Equal(0, fixture.ActiveLeases);
            Assert.Throws<UnrealInvocationException>(() =>
                new NativeUnrealInvocationBridge(transport).ReadProperty<int>(eventHandle, "LeaseProbe"));
            unretainedSubscription.Dispose();
        }
        finally
        {
            Marshal.FreeHGlobal(eventTablePointer);
            GC.KeepAlive(subscribe);
            GC.KeepAlive(unsubscribe);
        }
    }

    [Fact]
    public void OldV2PrefixKeepsTransportUsableButUObjectLeaseReportsNotSupported()
    {
        var oldTable = new TestTableV2AsyncServices
        {
            AbiVersion = 2,
            TableSize = (uint)Marshal.SizeOf<TestTableV2AsyncServices>(),
            ValueSize = (uint)Marshal.SizeOf<TestValue>(),
            ReadProperty = Marshal.GetFunctionPointerForDelegate(_read),
            WriteProperty = Marshal.GetFunctionPointerForDelegate(_write),
            Invoke = Marshal.GetFunctionPointerForDelegate(_invoke),
            RetainContext = Marshal.GetFunctionPointerForDelegate(_retain),
            ReleaseContext = Marshal.GetFunctionPointerForDelegate(_release),
            ReadPropertyById = Marshal.GetFunctionPointerForDelegate(_fastRead),
            WritePropertyById = Marshal.GetFunctionPointerForDelegate(_fastWrite),
            InvokeById = Marshal.GetFunctionPointerForDelegate(_fastInvoke),
        };
        nint oldPointer = Marshal.AllocHGlobal(Marshal.SizeOf<TestTableV2AsyncServices>());
        Marshal.StructureToPtr(oldTable, oldPointer, false);
        try
        {
            using var transport = new InProcessUnrealNativeInvocationTransport(new(0x1234), oldPointer);
            Assert.False(transport.SupportsObjectReferences);
            Assert.True(transport.ReadProperty(UnrealObjectHandle.Create(1, 2), FloatPath).IsSuccess);
            var bridge = new NativeUnrealInvocationBridge(transport);
            Assert.Throws<NotSupportedException>(() =>
                UnrealObjectReference.Retain(bridge, UnrealObjectHandle.Create(1, 2)));
        }
        finally
        {
            Marshal.FreeHGlobal(oldPointer);
        }
    }

    [Fact]
    public void StaleReleaseIsRetrySafeAndGenerationScopesAreIndependent()
    {
        using var fixture = new ObjectReferenceFixture(this);
        using var transport = new InProcessUnrealNativeInvocationTransport(new(0x1234), fixture.Parent);
        var handle = UnrealObjectHandle.Create(3, 5);
        using var first = new UnrealGenerationInvocationBridge(new NativeUnrealInvocationBridge(transport));
        using var second = new UnrealGenerationInvocationBridge(new NativeUnrealInvocationBridge(transport));
        using (var lease = UnrealObjectReference.Retain(first, handle))
        {
            fixture.NextReleaseStatus = UnrealObjectReferenceStatus.WrongThread;
            Assert.Throws<InvalidOperationException>(() => lease.Dispose());
            fixture.NextReleaseStatus = UnrealObjectReferenceStatus.StaleHandle;
            lease.Dispose();
            Assert.Equal(2, fixture.ReleaseCalls);
        }
        using (var lease = UnrealObjectReference.Retain(second, handle))
        {
            Assert.Equal(2, fixture.OpenCalls);
            Assert.Equal(2, fixture.RetainCalls);
        }
        fixture.NextReleaseStatus = UnrealObjectReferenceStatus.Success;
        first.Dispose();
        Assert.Equal(1, fixture.CloseCalls);
        second.Dispose();
        Assert.Equal(2, fixture.CloseCalls);
    }

    private sealed class ObjectReferenceFixture : IDisposable
    {
        public nint Parent { get; }
        public nint Extension { get; }
        public nint Table { get; }
        public int OpenCalls;
        public int CloseCalls;
        public int RetainCalls;
        public int ReleaseCalls;
        public int ActiveScopes;
        public int ActiveLeases;
        public UnrealObjectReferenceStatus NextReleaseStatus = UnrealObjectReferenceStatus.Success;

        public unsafe ObjectReferenceFixture(InProcessUnrealNativeInvocationTransportTests owner)
        {
            _objectReferenceFixture = this;
            var tail = new TestObjectReferenceTable
            {
                AbiVersion = 1,
                TableSize = (uint)Marshal.SizeOf<TestObjectReferenceTable>(),
                ValueSize = 0,
                RetainContext = Marshal.GetFunctionPointerForDelegate(owner._retain),
                ReleaseContext = Marshal.GetFunctionPointerForDelegate(owner._release),
                OpenScope = (nint)(delegate* unmanaged[Cdecl]<nint, ulong*, byte>)&ObjectReferenceOpen,
                CloseScope = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, byte>)&ObjectReferenceClose,
                Retain = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, ulong, byte>)&ObjectReferenceRetain,
                Release = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, ulong, byte>)&ObjectReferenceRelease,
            };
            Table = Marshal.AllocHGlobal(Marshal.SizeOf<TestObjectReferenceTable>());
            Marshal.StructureToPtr(tail, Table, false);
            var parent = new TestObjectReferenceExtension
            {
                Base = new TestTableV2AsyncServices
                {
                    AbiVersion = 2,
                    TableSize = (uint)Marshal.SizeOf<TestObjectReferenceExtension>(),
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
                ObjectReferenceTransport = Table,
            };
            Extension = Marshal.AllocHGlobal(Marshal.SizeOf<TestObjectReferenceExtension>());
            Marshal.StructureToPtr(parent, Extension, false);
            Parent = Extension;
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Extension);
            Marshal.FreeHGlobal(Table);
            _objectReferenceFixture = null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe byte ObjectReferenceOpen(nint context, ulong* scope)
    {
        var fixture = _objectReferenceFixture!;
        *scope = (ulong)++fixture.OpenCalls;
        fixture.ActiveScopes++;
        return (byte)UnrealObjectReferenceStatus.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ObjectReferenceClose(nint context, ulong scope)
    {
        var fixture = _objectReferenceFixture!;
        fixture.CloseCalls++;
        fixture.ActiveScopes--;
        return (byte)UnrealObjectReferenceStatus.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ObjectReferenceRetain(nint context, ulong scope, ulong handle)
    {
        _objectReferenceFixture!.RetainCalls++;
        _objectReferenceFixture.ActiveLeases++;
        return (byte)UnrealObjectReferenceStatus.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ObjectReferenceRelease(nint context, ulong scope, ulong handle)
    {
        var fixture = _objectReferenceFixture!;
        fixture.ReleaseCalls++;
        if (fixture.NextReleaseStatus != UnrealObjectReferenceStatus.WrongThread && fixture.ActiveLeases > 0)
            fixture.ActiveLeases--;
        return (byte)fixture.NextReleaseStatus;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void EventObjectRead(
        nint context, ulong target, nint path, int pathLength,
        nint callback, nint userData)
    {
        bool retained = _objectReferenceFixture?.ActiveLeases > 0;
        InvokeCallback(
            Marshal.GetDelegateForFunctionPointer<UnrealNativeResultCallbackV1>(callback),
            userData, retained, UnrealInvocationValueKind.Int32, int32Value: 1,
            error: retained ? UnrealInvocationError.None : UnrealInvocationError.ObjectHandleFailure,
            handleError: retained ? UnrealObjectHandleError.None : UnrealObjectHandleError.StaleHandle);
    }

    private static unsafe void DispatchObject(EventRegistration registration, UnrealObjectHandle handle)
    {
        UnrealNativeValueV3 argument = new()
        {
            Kind = UnrealNativeValueKindV3.Object,
            Payload0 = handle.Value,
        };
        var frame = new UnrealNativeEventFrameV4
        {
            AbiVersion = UnrealNativeEventAbiV4.Version,
            FrameSize = UnrealNativeEventAbiV4.FrameSize,
            SubscriptionId = registration.SubscriptionId,
            Source = registration.Source,
            EventId = registration.EventId,
            ArgumentCount = 1,
        };
        nint framePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnrealNativeEventFrameV4>());
        nint argumentPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnrealNativeValueV3>());
        try
        {
            Marshal.StructureToPtr(argument, argumentPointer, false);
            frame.Arguments = argumentPointer;
            Marshal.StructureToPtr(frame, framePointer, false);
            registration.Callback(registration.UserData, framePointer);
        }
        finally
        {
            Marshal.FreeHGlobal(argumentPointer);
            Marshal.FreeHGlobal(framePointer);
        }
    }
}
