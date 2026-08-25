using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealScriptObjectModelTests
{
    [Fact]
    public void ActorScriptMapsWorldOwnedContextToActorLifecycle()
    {
        var script = new ProbeActorScript();
        IUnrealManagedScript lifecycle = script;
        var host = UnrealObjectHandle.Create(3, 11);
        var target = UnrealObjectHandle.Create(4, 12);
        var bridge = new RejectingBridge();

        lifecycle.OnStart(new UnrealScriptContext(bridge, host, target, 27, 5));
        lifecycle.OnTick(0.25f);

        Assert.True(script.IsRunning);
        Assert.Equal(host, script.ObservedHost);
        Assert.Equal(target, script.ObservedTarget);
        Assert.Equal((ulong)27, script.ObservedInstanceId);
        Assert.Equal(5, script.ObservedGeneration);
        Assert.Equal(0.25f, script.LastDeltaSeconds);
        Assert.False(script.Lifetime.IsCancellationRequested);

        lifecycle.OnStop();

        Assert.False(script.IsRunning);
        Assert.True(script.WasCanceledDuringEndPlay);
        Assert.Equal(1, script.BeginPlayCount);
        Assert.Equal(1, script.TickCount);
        Assert.Equal(1, script.EndPlayCount);
        Assert.Throws<InvalidOperationException>(() => _ = script.TargetHandle);
    }

    [Fact]
    public void FailedBeginPlayRollsBackTheManagedLifetime()
    {
        var script = new FailingActorScript();
        IUnrealManagedScript lifecycle = script;
        var handle = UnrealObjectHandle.Create(0, 1);
        var context = new UnrealScriptContext(
            new RejectingBridge(),
            handle,
            handle,
            1,
            1);

        Assert.Throws<ExpectedFailureException>(() => lifecycle.OnStart(context));
        Assert.False(script.IsRunning);
        Assert.Throws<InvalidOperationException>(() => _ = script.Lifetime);
    }

    private sealed class ProbeActorScript : UnrealActorScript
    {
        public int BeginPlayCount { get; private set; }

        public int TickCount { get; private set; }

        public int EndPlayCount { get; private set; }

        public UnrealObjectHandle ObservedHost { get; private set; }

        public UnrealObjectHandle ObservedTarget { get; private set; }

        public ulong ObservedInstanceId { get; private set; }

        public int ObservedGeneration { get; private set; }

        public float LastDeltaSeconds { get; private set; }

        public bool WasCanceledDuringEndPlay { get; private set; }

        protected override void OnBeginPlay()
        {
            ++BeginPlayCount;
            ObservedHost = HostHandle;
            ObservedTarget = TargetHandle;
            ObservedInstanceId = InstanceId;
            ObservedGeneration = Generation;
        }

        protected override void OnTick(float deltaSeconds)
        {
            ++TickCount;
            LastDeltaSeconds = deltaSeconds;
        }

        protected override void OnEndPlay()
        {
            ++EndPlayCount;
            WasCanceledDuringEndPlay = Lifetime.IsCancellationRequested;
        }
    }

    private sealed class FailingActorScript : UnrealActorScript
    {
        protected override void OnBeginPlay() => throw new ExpectedFailureException();
    }

    private sealed class ExpectedFailureException : Exception
    {
    }

    private sealed class RejectingBridge : IUnrealInvocationBridge
    {
        public T ReadProperty<T>(UnrealObjectHandle target, string propertyPath) =>
            throw new NotSupportedException();

        public void WriteProperty<T>(
            UnrealObjectHandle target,
            string propertyPath,
            T value) => throw new NotSupportedException();

        public T Invoke<T>(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<object?> arguments) => throw new NotSupportedException();

        public void Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<object?> arguments) => throw new NotSupportedException();
    }
}
