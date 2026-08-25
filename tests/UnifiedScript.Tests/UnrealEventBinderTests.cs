using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealEventBinderTests
{
    private const string EventPath = "/Script/Fixture.FixtureComponent:OnChanged";

    [Fact]
    public void BinderResolvesCanonicalGeneratedEndpointAndStrongHandler()
    {
        BinderWrapper.LastHandler = null;
        var script = new AttributedScript();
        UnrealObjectHandle host = UnrealObjectHandle.Create(1, 1);
        UnrealObjectHandle target = UnrealObjectHandle.Create(2, 1);
        var context = new UnrealScriptContext(new FixtureBridge(), host, target, 7, 3);

        IReadOnlyList<IUnrealEventSubscription> subscriptions =
            UnrealEventBinder.BindAttributedEvents(script, context);
        BinderWrapper.LastHandler!(41, target);

        Assert.Single(subscriptions);
        Assert.Equal(41, script.Value);
        Assert.Equal(target, script.Context);
        Assert.Equal(target, BinderWrapper.LastConstructedHandle);
    }

    private sealed class AttributedScript
    {
        public int Value { get; private set; }

        public UnrealObjectHandle Context { get; private set; }

        [UnrealEvent(typeof(BinderWrapper), EventPath)]
        private void OnChanged(int value, UnrealObjectHandle context)
        {
            Value = value;
            Context = context;
        }
    }

    private sealed class BinderWrapper : UnrealObject
    {
        public delegate void ChangedHandler(int value, UnrealObjectHandle context);

        public BinderWrapper(UnrealObjectHandle handle, IUnrealInvocationBridge bridge)
            : base(handle, bridge)
        {
            LastConstructedHandle = handle;
        }

        public static ChangedHandler? LastHandler { get; set; }

        public static UnrealObjectHandle LastConstructedHandle { get; private set; }

        [UnrealEventEndpoint(EventPath)]
        public IUnrealEventSubscription SubscribeChanged(ChangedHandler handler)
        {
            LastHandler = handler;
            return new FixtureSubscription();
        }
    }

    private sealed class FixtureSubscription : IUnrealEventSubscription
    {
        public ulong SubscriptionId => 11;

        public bool IsActive { get; private set; } = true;

        public Exception? LastDispatchException => null;

        public void Dispose() => IsActive = false;
    }

    private sealed class FixtureBridge : IUnrealInvocationBridge
    {
        public T ReadProperty<T>(UnrealObjectHandle target, string propertyPath) => default!;

        public void WriteProperty<T>(UnrealObjectHandle target, string propertyPath, T value)
        {
        }

        public T Invoke<T>(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<object?> arguments) => default!;

        public void Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<object?> arguments)
        {
        }
    }
}
