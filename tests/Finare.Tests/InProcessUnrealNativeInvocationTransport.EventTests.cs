using System.Runtime.InteropServices;
using System.Text;
using Finare.Unreal;

namespace Finare.Tests;

public sealed partial class InProcessUnrealNativeInvocationTransportTests
{
    private const string ReflectedEventPath =
        "/Script/Fixture.FixtureComponent:OnChanged";

    [Fact]
    public void AbiV4EventFailureIsLatchedPerGenerationAndReleasedOnDispose()
    {
        var registrations = new List<EventRegistration>();
        ulong nextSubscriptionId = 0;
        UnrealNativeSubscribeEventV4 subscribe =
            (_, source, eventId, _, _, callback, userData) =>
            {
                var registration = new EventRegistration(
                    ++nextSubscriptionId,
                    source,
                    eventId,
                    callback,
                    userData);
                registrations.Add(registration);
                return registration.SubscriptionId;
            };
        UnrealNativeUnsubscribeEventV4 unsubscribe = (_, subscriptionId) =>
        {
            EventRegistration? registration = registrations.Find(
                candidate => candidate.SubscriptionId == subscriptionId);
            if (registration is null)
            {
                return 0;
            }
            registration.Unsubscribed = true;
            return 1;
        };

        nint eventTablePointer = nint.Zero;
        nint tablePointer = nint.Zero;
        try
        {
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
            eventTablePointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<UnrealNativeEventTransportTableV4>());
            Marshal.StructureToPtr(eventTable, eventTablePointer, false);

            var table = new TestTableV2AsyncServices
            {
                AbiVersion = 2,
                TableSize = checked((uint)Marshal.SizeOf<TestTableV2AsyncServices>()),
                ValueSize = checked((uint)Marshal.SizeOf<TestValue>()),
                ReadProperty = Marshal.GetFunctionPointerForDelegate(_read),
                WriteProperty = Marshal.GetFunctionPointerForDelegate(_write),
                Invoke = Marshal.GetFunctionPointerForDelegate(_invoke),
                RetainContext = Marshal.GetFunctionPointerForDelegate(_retain),
                ReleaseContext = Marshal.GetFunctionPointerForDelegate(_release),
                ReadPropertyById = Marshal.GetFunctionPointerForDelegate(_fastRead),
                WritePropertyById = Marshal.GetFunctionPointerForDelegate(_fastWrite),
                InvokeById = Marshal.GetFunctionPointerForDelegate(_fastInvoke),
                EventTransport = eventTablePointer,
            };
            tablePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TestTableV2AsyncServices>());
            Marshal.StructureToPtr(table, tablePointer, false);

            using var transport = new InProcessUnrealNativeInvocationTransport(
                new nint(0x1234),
                tablePointer);
            var descriptor = new UnrealEventDescriptor(
                UnrealMemberId.FromPath(ReflectedEventPath),
                ReflectedEventPath,
                ReflectedEventPath + "__DelegateSignature",
                typeof(void),
                UnrealDelegateKind.Multicast,
                []);
            UnrealObjectHandle source = UnrealObjectHandle.Create(4, 9);

            using var firstGeneration = new UnrealGenerationInvocationBridge(
                new NativeUnrealInvocationBridge(transport));
            int firstCalls = 0;
            var firstFailure = new InvalidOperationException("first generation event failure");
            IUnrealEventSubscription firstSubscription = firstGeneration.Subscribe(
                source,
                descriptor,
                (Action)(() =>
                {
                    firstCalls++;
                    if (firstCalls == 1)
                    {
                        throw firstFailure;
                    }
                }));

            EventRegistration firstRegistration = Assert.Single(registrations);
            Dispatch(firstRegistration);
            Assert.Same(firstFailure, firstSubscription.LastDispatchException);

            // A successful native event must not erase a failure before the owning
            // generation's tick has reported it.
            Dispatch(firstRegistration);
            Assert.Equal(2, firstCalls);
            Assert.Same(firstFailure, firstSubscription.LastDispatchException);
            Assert.Same(firstFailure, firstGeneration.ConsumePendingEventFailure());
            Assert.Null(firstGeneration.ConsumePendingEventFailure());

            using var secondGeneration = new UnrealGenerationInvocationBridge(
                new NativeUnrealInvocationBridge(transport));
            var secondFailure = new InvalidOperationException("second generation event failure");
            IUnrealEventSubscription secondSubscription = secondGeneration.Subscribe(
                source,
                descriptor,
                (Action)(() => throw secondFailure));
            EventRegistration secondRegistration = Assert.Single(
                registrations,
                registration => registration.SubscriptionId != firstRegistration.SubscriptionId);

            Dispatch(secondRegistration);
            Assert.Same(secondFailure, secondSubscription.LastDispatchException);
            Assert.Same(secondFailure, secondGeneration.ConsumePendingEventFailure());
            Assert.Null(firstGeneration.ConsumePendingEventFailure());

            firstGeneration.Dispose();
            Assert.True(firstRegistration.Unsubscribed);
            secondGeneration.Dispose();
            Assert.True(secondRegistration.Unsubscribed);
        }
        finally
        {
            if (tablePointer != nint.Zero)
            {
                Marshal.FreeHGlobal(tablePointer);
            }
            if (eventTablePointer != nint.Zero)
            {
                Marshal.FreeHGlobal(eventTablePointer);
            }
            GC.KeepAlive(subscribe);
            GC.KeepAlive(unsubscribe);
        }
    }

    [Fact]
    public unsafe void AbiV4EventFailureReachesOwnerTickAndDoesNotPolluteAnotherOwner()
    {
        var registrations = new List<EventRegistration>();
        ulong nextSubscriptionId = 0;
        UnrealNativeSubscribeEventV4 subscribe =
            (_, source, eventId, _, _, callback, userData) =>
            {
                var registration = new EventRegistration(
                    ++nextSubscriptionId,
                    source,
                    eventId,
                    callback,
                    userData);
                registrations.Add(registration);
                return registration.SubscriptionId;
            };
        UnrealNativeUnsubscribeEventV4 unsubscribe = (_, subscriptionId) =>
        {
            EventRegistration? registration = registrations.Find(
                candidate => candidate.SubscriptionId == subscriptionId);
            if (registration is null)
            {
                return 0;
            }
            registration.Unsubscribed = true;
            return 1;
        };

        nint eventTablePointer = nint.Zero;
        nint tablePointer = nint.Zero;
        ulong worldId = 0;
        try
        {
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
            eventTablePointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<UnrealNativeEventTransportTableV4>());
            Marshal.StructureToPtr(eventTable, eventTablePointer, false);

            var table = new TestTableV2AsyncServices
            {
                AbiVersion = 2,
                TableSize = checked((uint)Marshal.SizeOf<TestTableV2AsyncServices>()),
                ValueSize = checked((uint)Marshal.SizeOf<TestValue>()),
                ReadProperty = Marshal.GetFunctionPointerForDelegate(_read),
                WriteProperty = Marshal.GetFunctionPointerForDelegate(_write),
                Invoke = Marshal.GetFunctionPointerForDelegate(_invoke),
                RetainContext = Marshal.GetFunctionPointerForDelegate(_retain),
                ReleaseContext = Marshal.GetFunctionPointerForDelegate(_release),
                ReadPropertyById = Marshal.GetFunctionPointerForDelegate(_fastRead),
                WritePropertyById = Marshal.GetFunctionPointerForDelegate(_fastWrite),
                InvokeById = Marshal.GetFunctionPointerForDelegate(_fastInvoke),
                EventTransport = eventTablePointer,
            };
            tablePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TestTableV2AsyncServices>());
            Marshal.StructureToPtr(table, tablePointer, false);

            worldId = 991;
            delegate* unmanaged[Cdecl]<ulong, nint, nint, int> createWorld =
                &UnrealWorldManagedEntryPoint.CreateWorld;
            delegate* unmanaged[Cdecl]<ulong, ulong, ulong, ulong, nint, int, nint, int, int> createScript =
                &UnrealWorldManagedEntryPoint.CreateScript;
            delegate* unmanaged[Cdecl]<ulong, ulong, float, int> tickScript =
                &UnrealWorldManagedEntryPoint.TickScript;
            delegate* unmanaged[Cdecl]<ulong, nint, int, int> getLastError =
                &UnrealWorldManagedEntryPoint.GetLastError;
            delegate* unmanaged[Cdecl]<ulong, int> destroyWorld =
                &UnrealWorldManagedEntryPoint.DestroyWorld;

            Assert.Equal(
                (int)UnrealWorldManagedEntryStatus.Success,
                createWorld(worldId, new nint(0x1234), tablePointer));

            string assemblyPath = typeof(AbiV4EventFailureScript).Assembly.Location;
            string typeName = typeof(AbiV4EventFailureScript).FullName!;
            nint assemblyData = Marshal.StringToCoTaskMemUTF8(assemblyPath);
            nint typeData = Marshal.StringToCoTaskMemUTF8(typeName);
            try
            {
                Assert.Equal(
                    (int)UnrealWorldManagedEntryStatus.Success,
                    createScript(
                        worldId,
                        1,
                        UnrealObjectHandle.Create(1, 991).Value,
                        UnrealObjectHandle.Create(2, 991).Value,
                        assemblyData,
                        Encoding.UTF8.GetByteCount(assemblyPath),
                        typeData,
                        Encoding.UTF8.GetByteCount(typeName)));
                Assert.Equal(
                    (int)UnrealWorldManagedEntryStatus.Success,
                    createScript(
                        worldId,
                        2,
                        UnrealObjectHandle.Create(3, 991).Value,
                        UnrealObjectHandle.Create(4, 991).Value,
                        assemblyData,
                        Encoding.UTF8.GetByteCount(assemblyPath),
                        typeData,
                        Encoding.UTF8.GetByteCount(typeName)));
            }
            finally
            {
                Marshal.FreeCoTaskMem(assemblyData);
                Marshal.FreeCoTaskMem(typeData);
            }

            Assert.Equal(2, registrations.Count);
            Dispatch(registrations[0]);
            Assert.Equal(
                (int)UnrealWorldManagedEntryStatus.InternalFailure,
                tickScript(worldId, 1, 0.016f));
            string? firstError = ReadLastError(getLastError, worldId);
            Assert.Contains("AbiV4EventFailureScript event failure", firstError);
            Assert.Contains("TickScript", firstError);
            Assert.Contains("instanceId", firstError);

            // The second owner has no event failure, and a later successful event
            // for the first owner does not resurrect or erase its already-reported
            // diagnostic.
            Assert.Equal(
                (int)UnrealWorldManagedEntryStatus.Success,
                tickScript(worldId, 2, 0.016f));
            Assert.Null(ReadLastError(getLastError, worldId));
            Dispatch(registrations[0]);
            Assert.Equal(
                (int)UnrealWorldManagedEntryStatus.Success,
                tickScript(worldId, 1, 0.016f));
            Assert.Null(ReadLastError(getLastError, worldId));

            Assert.Equal(
                (int)UnrealWorldManagedEntryStatus.Success,
                destroyWorld(worldId));
            worldId = 0;
            Assert.All(registrations, registration => Assert.True(registration.Unsubscribed));
        }
        finally
        {
            if (worldId != 0)
            {
                delegate* unmanaged[Cdecl]<ulong, int> destroyWorld =
                    &UnrealWorldManagedEntryPoint.DestroyWorld;
                _ = destroyWorld(worldId);
            }
            if (tablePointer != nint.Zero)
            {
                Marshal.FreeHGlobal(tablePointer);
            }
            if (eventTablePointer != nint.Zero)
            {
                Marshal.FreeHGlobal(eventTablePointer);
            }
            GC.KeepAlive(subscribe);
            GC.KeepAlive(unsubscribe);
        }
    }

    [Fact]
    public void AbiV4EventClosureUsesGenerationOwnerAndPersistsStateAcrossReload()
    {
        var registrations = new List<EventRegistration>();
        ulong nextSubscriptionId = 0;
        UnrealNativeSubscribeEventV4 subscribe =
            (_, source, eventId, _, _, callback, userData) =>
            {
                var registration = new EventRegistration(
                    ++nextSubscriptionId,
                    source,
                    eventId,
                    callback,
                    userData);
                registrations.Add(registration);
                return registration.SubscriptionId;
            };
        UnrealNativeUnsubscribeEventV4 unsubscribe = (_, subscriptionId) =>
        {
            EventRegistration? registration = registrations.Find(
                candidate => candidate.SubscriptionId == subscriptionId);
            if (registration is null)
            {
                return 0;
            }
            registration.Unsubscribed = true;
            return 1;
        };

        nint eventTablePointer = nint.Zero;
        nint tablePointer = nint.Zero;
        nint asyncTablePointer = nint.Zero;
        try
        {
            UnrealNativeAsyncSubmitV5 asyncSubmit = static (nint _, in UnrealAsyncRequestV5 _) => 1;
            UnrealNativeAsyncPumpOwnerV5 asyncPump = static (
                nint _, ulong _, uint _, float _) => 1;
            UnrealNativeAsyncCancelV5 asyncCancel = static (
                nint _, ulong _, uint _, ulong _) => 1;
            UnrealNativeAsyncPollV5 asyncPoll = static (
                nint _, ulong _, uint _, out UnrealAsyncCompletionViewV5 view) =>
            {
                view = new UnrealAsyncCompletionViewV5
                {
                    AbiVersion = UnrealAsyncServiceAbiV5.Version,
                    StructSize = UnrealAsyncServiceAbiV5.CompletionViewSize,
                    WorldHostId = 992,
                };
                return 1;
            };
            UnrealNativeAsyncCancelOwnerV5 asyncCancelOwner = static (
                nint _, ulong _, uint _) => 0;
            var asyncTable = new UnrealAsyncTransportTableV5
            {
                AbiVersion = UnrealAsyncServiceAbiV5.Version,
                TableSize = UnrealAsyncServiceAbiV5.TransportTableSize,
                RequestSize = UnrealAsyncServiceAbiV5.RequestSize,
                CompletionSize = UnrealAsyncServiceAbiV5.CompletionSize,
                CompletionViewSize = UnrealAsyncServiceAbiV5.CompletionViewSize,
                Capabilities = UnrealAsyncServiceAbiV5.RequiredCapabilities,
                RetainContext = Marshal.GetFunctionPointerForDelegate(_retain),
                ReleaseContext = Marshal.GetFunctionPointerForDelegate(_release),
                SubmitRequest = Marshal.GetFunctionPointerForDelegate(asyncSubmit),
                PumpOwner = Marshal.GetFunctionPointerForDelegate(asyncPump),
                CancelRequest = Marshal.GetFunctionPointerForDelegate(asyncCancel),
                PollCompletions = Marshal.GetFunctionPointerForDelegate(asyncPoll),
                CancelOwner = Marshal.GetFunctionPointerForDelegate(asyncCancelOwner),
            };
            asyncTablePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnrealAsyncTransportTableV5>());
            Marshal.StructureToPtr(asyncTable, asyncTablePointer, false);

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
            eventTablePointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<UnrealNativeEventTransportTableV4>());
            Marshal.StructureToPtr(eventTable, eventTablePointer, false);

            var table = new TestTableV2AsyncServices
            {
                AbiVersion = 2,
                TableSize = checked((uint)Marshal.SizeOf<TestTableV2AsyncServices>()),
                ValueSize = checked((uint)Marshal.SizeOf<TestValue>()),
                ReadProperty = Marshal.GetFunctionPointerForDelegate(_read),
                WriteProperty = Marshal.GetFunctionPointerForDelegate(_write),
                Invoke = Marshal.GetFunctionPointerForDelegate(_invoke),
                RetainContext = Marshal.GetFunctionPointerForDelegate(_retain),
                ReleaseContext = Marshal.GetFunctionPointerForDelegate(_release),
                ReadPropertyById = Marshal.GetFunctionPointerForDelegate(_fastRead),
                WritePropertyById = Marshal.GetFunctionPointerForDelegate(_fastWrite),
                InvokeById = Marshal.GetFunctionPointerForDelegate(_fastInvoke),
                EventTransport = eventTablePointer,
                AsyncTransport = asyncTablePointer,
            };
            tablePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TestTableV2AsyncServices>());
            Marshal.StructureToPtr(table, tablePointer, false);

            using var transport = new InProcessUnrealNativeInvocationTransport(
                new nint(0x1234),
                tablePointer);
            var bridge = new NativeUnrealInvocationBridge(transport);
            string assemblyPath = typeof(AbiV4StatefulEventScript).Assembly.Location;
            string typeName = typeof(AbiV4StatefulEventScript).FullName!;
            using var instance = UnrealWorldManagedEntryPoint.ManagedScriptInstance.Create(
                worldId: 992,
                instanceId: 7,
                bridge,
                UnrealObjectHandle.Create(1, 992),
                UnrealObjectHandle.Create(2, 992),
                assemblyPath,
                typeName);

            instance.Start();
            EventRegistration first = Assert.Single(registrations);
            Dispatch(first);
            instance.Tick(0.016f);
            Assert.Equal(1, _eventStateValue);

            Assert.Equal(2, instance.Reload(assemblyPath, typeName));
            Assert.True(first.Unsubscribed);
            EventRegistration second = Assert.Single(
                registrations,
                registration => registration.SubscriptionId != first.SubscriptionId);
            Dispatch(second);
            instance.Tick(0.016f);
            Assert.Equal(2, _eventStateValue);
        }
        finally
        {
            if (tablePointer != nint.Zero)
            {
                Marshal.FreeHGlobal(tablePointer);
            }
            if (eventTablePointer != nint.Zero)
            {
                Marshal.FreeHGlobal(eventTablePointer);
            }
            if (asyncTablePointer != nint.Zero)
            {
                Marshal.FreeHGlobal(asyncTablePointer);
            }
            GC.KeepAlive(subscribe);
            GC.KeepAlive(unsubscribe);
        }
    }

    private static unsafe string? ReadLastError(
        delegate* unmanaged[Cdecl]<ulong, nint, int, int> getLastError,
        ulong worldId)
    {
        int requiredBytes = getLastError(worldId, nint.Zero, 0);
        if (requiredBytes <= 0)
        {
            return null;
        }
        nint buffer = Marshal.AllocHGlobal(requiredBytes + 1);
        try
        {
            int written = getLastError(worldId, buffer, requiredBytes + 1);
            return written > 0
                ? Marshal.PtrToStringUTF8(buffer, written)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void Dispatch(EventRegistration registration)
    {
        var frame = new UnrealNativeEventFrameV4
        {
            AbiVersion = UnrealNativeEventAbiV4.Version,
            FrameSize = UnrealNativeEventAbiV4.FrameSize,
            SubscriptionId = registration.SubscriptionId,
            Source = registration.Source,
            EventId = registration.EventId,
        };
        nint framePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnrealNativeEventFrameV4>());
        try
        {
            Marshal.StructureToPtr(frame, framePointer, false);
            registration.Callback(registration.UserData, framePointer);
        }
        finally
        {
            Marshal.FreeHGlobal(framePointer);
        }
    }

    private sealed class EventRegistration(
        ulong subscriptionId,
        ulong source,
        ulong eventId,
        UnrealNativeEventCallbackV4 callback,
        nint userData)
    {
        public ulong SubscriptionId { get; } = subscriptionId;

        public ulong Source { get; } = source;

        public ulong EventId { get; } = eventId;

        public UnrealNativeEventCallbackV4 Callback { get; } = callback;

        public nint UserData { get; } = userData;

        public bool Unsubscribed { get; set; }
    }

    private sealed class AbiV4EventWrapper(
        UnrealObjectHandle handle,
        IUnrealInvocationBridge bridge) : UnrealObject(handle, bridge)
    {
        public delegate void ChangedHandler();

        [UnrealEventEndpoint(ReflectedEventPath)]
        public IUnrealEventSubscription SubscribeChanged(ChangedHandler handler) =>
            SubscribeEvent(
                new UnrealEventDescriptor(
                    UnrealMemberId.FromPath(ReflectedEventPath),
                    ReflectedEventPath,
                    ReflectedEventPath + "__DelegateSignature",
                    typeof(void),
                    UnrealDelegateKind.Multicast,
                    []),
                handler);
    }

    private sealed class AbiV4EventFailureScript : IUnrealManagedScript
    {
        private int _remainingFailures = 1;

        [UnrealEvent(typeof(AbiV4EventWrapper), ReflectedEventPath)]
        private void OnChanged()
        {
            if (Interlocked.Exchange(ref _remainingFailures, 0) != 0)
            {
                throw new InvalidOperationException(
                    "AbiV4EventFailureScript event failure");
            }
        }

        public void OnStart(UnrealScriptContext context)
        {
        }

        public void OnTick(float deltaSeconds)
        {
        }

        public void OnStop()
        {
        }
    }

    [ScriptAsset("Tests/StatefulEvent")]
    [UnrealScriptClass(
        UnrealScriptClassKind.Actor,
        GeneratedClassPath = "/Game/Tests/BP_StatefulEvent.BP_StatefulEvent_C")]
    private sealed class AbiV4StatefulEventScript : UnrealActorScript
    {
        [Expose]
        public int Count { get; set; }

        private IUnrealEventSubscription? _subscription;

        protected override void OnBeginPlay()
        {
            var wrapper = new AbiV4EventWrapper(TargetHandle, Bridge);
            _subscription = wrapper.SubscribeChanged(() => ++Count);
        }

        protected override void OnEndPlay()
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }
}
