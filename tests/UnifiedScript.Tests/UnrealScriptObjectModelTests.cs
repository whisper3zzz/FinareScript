using UnifiedScript;
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
        Task latent = script.StartDelay(0.5f);
        lifecycle.OnTick(0.25f);

        Assert.True(script.IsRunning);
        Assert.Equal(host, script.ObservedHost);
        Assert.Equal(target, script.ObservedTarget);
        Assert.Equal((ulong)27, script.ObservedInstanceId);
        Assert.Equal(5, script.ObservedGeneration);
        Assert.Equal(0.25f, script.LastDeltaSeconds);
        Assert.False(script.Lifetime.IsCancellationRequested);
        Assert.False(latent.IsCompleted);

        ((IUnrealManagedEventSink)script).OnUnrealEvent(new UnrealScriptEvent(
            UnrealScriptEventKind.ActorBeginOverlap,
            target,
            UnrealObjectHandle.Create(5, 13)));
        Assert.Equal(1, script.BeginOverlapCount);
        Assert.True(script.LastOtherActor.IsWellFormed);

        lifecycle.OnTick(0.25f);
        Assert.True(latent.IsCompletedSuccessfully);
        Task canceledByStop = script.StartDelay(10.0f);

        lifecycle.OnStop();

        Assert.False(script.IsRunning);
        Assert.True(script.WasCanceledDuringEndPlay);
        Assert.True(canceledByStop.IsCanceled);
        Assert.Equal(1, script.BeginPlayCount);
        Assert.Equal(2, script.TickCount);
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

    [Fact]
    public void ActorComponentScriptMapsWorldOwnedContextToComponentLifecycle()
    {
        var script = new ProbeActorComponentScript();
        IUnrealManagedScript lifecycle = script;
        var host = UnrealObjectHandle.Create(7, 21);
        var target = UnrealObjectHandle.Create(8, 22);

        lifecycle.OnStart(new UnrealScriptContext(new RejectingBridge(), host, target, 31, 6));
        lifecycle.OnTick(0.125f);
        lifecycle.OnStop();

        Assert.Equal(1, script.BeginPlayCount);
        Assert.Equal(1, script.TickCount);
        Assert.Equal(1, script.EndPlayCount);
        Assert.Equal(0.125f, script.LastDeltaSeconds);
        Assert.True(script.WasCanceledDuringEndPlay);
    }

    [Fact]
    public void ScriptClassAttributePinsStableReviewedClassKinds()
    {
        Assert.Equal(1, (int)UnrealScriptClassKind.Actor);
        Assert.Equal(2, (int)UnrealScriptClassKind.ActorComponent);
        Assert.Equal(3, (int)UnrealScriptClassKind.Object);
        Assert.Equal(4, (int)UnrealScriptClassKind.BlueprintFunctionLibrary);

        UnrealScriptClassAttribute attribute = Assert.Single(
            typeof(AnnotatedFunctionLibrary)
                .GetCustomAttributes(typeof(UnrealScriptClassAttribute), inherit: false)
                .Cast<UnrealScriptClassAttribute>());
        Assert.Equal(UnrealScriptClassKind.BlueprintFunctionLibrary, attribute.Kind);
        Assert.Equal(
            "/Script/Engine.BlueprintFunctionLibrary",
            attribute.BaseClassPath);
        Assert.Equal("/Game/Tests/BP_TestLibrary.BP_TestLibrary_C", attribute.GeneratedClassPath);
    }

    [Fact]
    public void ActorScriptSynchronizesReviewedStateWithItsGeneratedProxy()
    {
        var script = new StateActorScript();
        IUnrealManagedScript lifecycle = script;
        var host = UnrealObjectHandle.Create(10, 30);
        var target = UnrealObjectHandle.Create(11, 31);
        var bridge = new StateBridge();
        bridge.Set(target, StateActorPath + ":Count", 41);
        bridge.Set(target, StateActorPath + ":Enabled", true);
        bridge.Set(target, StateActorPath + ":ByteValue", (byte)4);
        bridge.Set(target, StateActorPath + ":Large", 9L);
        bridge.Set(target, StateActorPath + ":Ratio", 1.5f);
        bridge.Set(target, StateActorPath + ":Precision", 2.25d);
        bridge.Set(target, StateActorPath + ":Label", "Ready");
        bridge.Set(target, StateActorPath + ":RowName", new UnrealName("Player"));
        bridge.Set(target, StateActorPath + ":DisplayText", new UnrealText("Loaded"));
        bridge.Set(target, StateActorPath + ":ObjectValue", UnrealObjectHandle.Create(20, 40));
        bridge.Set(target, StateActorPath + ":ClassValue", UnrealClassHandle.Create(21, 41));
        bridge.Set(target, StateActorPath + ":Offset", new UnrealVector(1.0, 2.0, 3.0));

        lifecycle.OnStart(new UnrealScriptContext(bridge, host, target, 71, 3));

        Assert.Equal(42, script.Count);
        Assert.False(script.Enabled);
        Assert.Equal((byte)5, script.ByteValue);
        Assert.Equal(11L, script.Large);
        Assert.Equal(3.0f, script.Ratio);
        Assert.Equal(2.75d, script.Precision);
        Assert.Equal("Ready!", script.Label);
        Assert.Equal(new UnrealName("Player.Managed"), script.RowName);
        Assert.Equal(new UnrealText("Loaded!"), script.DisplayText);
        Assert.True(script.ObjectValue.IsNull);
        Assert.Equal(UnrealClassHandle.Create(21, 41), script.ClassValue);
        Assert.Equal(new UnrealVector(2.0, 4.0, 6.0), script.Offset);
        Assert.Equal(42, bridge.Get<int>(target, StateActorPath + ":Count"));
        Assert.False(bridge.Get<bool>(target, StateActorPath + ":Enabled"));
        Assert.Equal((byte)5, bridge.Get<byte>(target, StateActorPath + ":ByteValue"));
        Assert.Equal(11L, bridge.Get<long>(target, StateActorPath + ":Large"));
        Assert.Equal(3.0f, bridge.Get<float>(target, StateActorPath + ":Ratio"));
        Assert.Equal(2.75d, bridge.Get<double>(target, StateActorPath + ":Precision"));
        Assert.Equal("Ready!", bridge.Get<string>(target, StateActorPath + ":Label"));
        Assert.Equal(new UnrealName("Player.Managed"),
            bridge.Get<UnrealName>(target, StateActorPath + ":RowName"));
        Assert.Equal(new UnrealText("Loaded!"),
            bridge.Get<UnrealText>(target, StateActorPath + ":DisplayText"));
        Assert.True(bridge.Get<UnrealObjectHandle>(target, StateActorPath + ":ObjectValue").IsNull);
        Assert.Equal(UnrealClassHandle.Create(21, 41),
            bridge.Get<UnrealClassHandle>(target, StateActorPath + ":ClassValue"));
        Assert.Equal(new UnrealVector(2.0, 4.0, 6.0),
            bridge.Get<UnrealVector>(target, StateActorPath + ":Offset"));

        bridge.Set(target, StateActorPath + ":Count", 100);
        bridge.Set(target, StateActorPath + ":Enabled", true);
        lifecycle.OnTick(0.25f);

        Assert.Equal(102, script.Count);
        Assert.False(script.Enabled);
        Assert.Equal(102, bridge.Get<int>(target, StateActorPath + ":Count"));
        Assert.False(bridge.Get<bool>(target, StateActorPath + ":Enabled"));
        lifecycle.OnStop();
    }

    [Fact]
    public void ComponentScriptSynchronizesStateThroughItsHostHandle()
    {
        var script = new StateComponentScript();
        IUnrealManagedScript lifecycle = script;
        var host = UnrealObjectHandle.Create(12, 32);
        var owner = UnrealObjectHandle.Create(13, 33);
        var bridge = new StateBridge();
        bridge.Set(host, StateComponentPath + ":Value", 7);

        lifecycle.OnStart(new UnrealScriptContext(bridge, host, owner, 72, 4));

        Assert.Equal(8, script.Value);
        Assert.Equal(8, bridge.Get<int>(host, StateComponentPath + ":Value"));
        Assert.DoesNotContain(bridge.Writes, write => write.Handle == owner);
        lifecycle.OnStop();
    }

    [Fact]
    public void UnsupportedScriptStateFailsBeforeManagedLifecycleStarts()
    {
        var script = new UnsupportedStateScript();
        IUnrealManagedScript lifecycle = script;
        var bridge = new StateBridge();

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            lifecycle.OnStart(new UnrealScriptContext(
                bridge,
                UnrealObjectHandle.Create(14, 34),
                UnrealObjectHandle.Create(15, 35),
                73,
                5)));

        Assert.Contains("reviewed Blueprint state matrix", error.Message);
        Assert.Equal(0, script.BeginPlayCount);
    }

    private const string StateActorPath = "/Game/Tests/BP_StateActor.BP_StateActor_C";
    private const string StateComponentPath = "/Game/Tests/BP_StateComponent.BP_StateComponent_C";

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

        public int BeginOverlapCount { get; private set; }

        public UnrealObjectHandle LastOtherActor { get; private set; }

        public Task StartDelay(float seconds) => DelayAsync(seconds);

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

        protected override void OnActorBeginOverlap(UnrealObjectHandle otherActor)
        {
            ++BeginOverlapCount;
            LastOtherActor = otherActor;
        }
    }

    private sealed class FailingActorScript : UnrealActorScript
    {
        protected override void OnBeginPlay() => throw new ExpectedFailureException();
    }

    private sealed class ProbeActorComponentScript : UnrealActorComponentScript
    {
        public int BeginPlayCount { get; private set; }

        public int TickCount { get; private set; }

        public int EndPlayCount { get; private set; }

        public float LastDeltaSeconds { get; private set; }

        public bool WasCanceledDuringEndPlay { get; private set; }

        protected override void OnBeginPlay() => ++BeginPlayCount;

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

    [ScriptAsset("Tests/StateActor")]
    [UnrealScriptClass(
        UnrealScriptClassKind.Actor,
        GeneratedClassPath = StateActorPath)]
    private sealed class StateActorScript : UnrealActorScript
    {
        [Expose(InstanceEditable = true)]
        public int Count { get; set; } = 1;

        [Expose]
        public bool Enabled { get; set; }

        [Expose]
        public byte ByteValue { get; set; }

        [Expose]
        public long Large { get; set; }

        [Expose]
        public float Ratio { get; set; }

        [Expose]
        public double Precision { get; set; }

        [Expose]
        public string Label { get; set; } = string.Empty;

        [Expose]
        public UnrealName RowName { get; set; }

        [Expose]
        public UnrealText DisplayText { get; set; }

        [Expose]
        public UnrealObjectHandle ObjectValue { get; set; }

        [Expose]
        public UnrealClassHandle ClassValue { get; set; }

        [Expose]
        public UnrealVector Offset { get; set; }

        protected override void OnBeginPlay()
        {
            ++Count;
            Enabled = !Enabled;
            ++ByteValue;
            Large += 2;
            Ratio *= 2;
            Precision += 0.5;
            Label += "!";
            RowName = new UnrealName(RowName.Value + ".Managed");
            DisplayText = new UnrealText(DisplayText.Value + "!");
            ObjectValue = default;
            Offset = new UnrealVector(Offset.X * 2, Offset.Y * 2, Offset.Z * 2);
        }

        protected override void OnTick(float deltaSeconds)
        {
            Count += 2;
            Enabled = !Enabled;
        }
    }

    [ScriptAsset("Tests/StateComponent")]
    [UnrealScriptClass(
        UnrealScriptClassKind.ActorComponent,
        GeneratedClassPath = StateComponentPath)]
    private sealed class StateComponentScript : UnrealActorComponentScript
    {
        [Expose]
        public int Value { get; set; }

        protected override void OnBeginPlay() => ++Value;
    }

    [ScriptAsset("Tests/UnsupportedState")]
    [UnrealScriptClass(
        UnrealScriptClassKind.Actor,
        GeneratedClassPath = "/Game/Tests/BP_UnsupportedState.BP_UnsupportedState_C")]
    private sealed class UnsupportedStateScript : UnrealActorScript
    {
        public int BeginPlayCount { get; private set; }

        [Expose]
        public decimal Unsupported { get; set; }

        protected override void OnBeginPlay() => ++BeginPlayCount;
    }

    [UnrealScriptClass(
        UnrealScriptClassKind.BlueprintFunctionLibrary,
        BaseClassPath = "/Script/Engine.BlueprintFunctionLibrary",
        GeneratedClassPath = "/Game/Tests/BP_TestLibrary.BP_TestLibrary_C")]
    private static class AnnotatedFunctionLibrary
    {
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

    private sealed class StateBridge : IUnrealInvocationBridge
    {
        private readonly Dictionary<(UnrealObjectHandle Handle, string Path), object> _values = [];

        public List<(UnrealObjectHandle Handle, string Path, object Value)> Writes { get; } = [];

        public void Set<T>(UnrealObjectHandle handle, string path, T value)
            where T : notnull => _values[(handle, path)] = value;

        public T Get<T>(UnrealObjectHandle handle, string path) =>
            (T)_values[(handle, path)];

        public T ReadProperty<T>(UnrealObjectHandle target, string propertyPath) =>
            Get<T>(target, propertyPath);

        public void WriteProperty<T>(UnrealObjectHandle target, string propertyPath, T value)
        {
            object boxed = value!;
            _values[(target, propertyPath)] = boxed;
            Writes.Add((target, propertyPath, boxed));
        }

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
