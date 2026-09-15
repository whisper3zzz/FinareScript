using Finare;
using System.Collections.Generic;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed partial class UnrealScriptObjectModelTests
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
    public void FailedBeginPlayDoesNotFlushStateAndPreservesInitializationFailureDuringCleanup()
    {
        var script = new FailingStartStateScript();
        IUnrealManagedScript lifecycle = script;
        UnrealObjectHandle target = UnrealObjectHandle.Create(23, 43);
        var bridge = new StateBridge();
        bridge.Set(target, FailingStartStateScript.StatePath + ":Count", 5);

        ExpectedFailureException failure = Assert.Throws<ExpectedFailureException>(() =>
            lifecycle.OnStart(new UnrealScriptContext(
                bridge,
                target,
                target,
                94,
                1)));

        Assert.NotNull(failure);
        // A failed lifecycle does not roll back writes that already reached
        // the live Unreal object. Cleanup only tears down the managed lease.
        Assert.Equal(6, bridge.Get<int>(target, FailingStartStateScript.StatePath + ":Count"));
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

        lifecycle.OnStart(new UnrealScriptContext(bridge, target, target, 71, 3));

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
    public void ActorScriptSynchronizesInheritedStateDeclaredByEachOwner()
    {
        var script = new InheritedLeafStateScript();
        IUnrealManagedScript lifecycle = script;
        UnrealObjectHandle host = UnrealObjectHandle.Create(30, 50);
        UnrealObjectHandle target = UnrealObjectHandle.Create(31, 51);
        var bridge = new StateBridge();
        bridge.Set(target, InheritedLeafStatePath + ":BaseCount", 40);
        bridge.Set(target, InheritedLeafStatePath + ":LeafCount", 2);

        lifecycle.OnStart(new UnrealScriptContext(bridge, target, target, 81, 1));

        Assert.Equal(41, script.BaseCount);
        Assert.Equal(4, script.LeafCount);
        Assert.Equal(41, bridge.Get<int>(target, InheritedLeafStatePath + ":BaseCount"));
        Assert.Equal(4, bridge.Get<int>(target, InheritedLeafStatePath + ":LeafCount"));
        lifecycle.OnStop();
    }

    [Fact]
    public void InheritedStateUsesSelectedLeafClassIdentity()
    {
        var script = new StableInheritedLeafStateScript();
        IUnrealManagedScript lifecycle = script;
        UnrealObjectHandle target = UnrealObjectHandle.Create(34, 54);
        var bridge = new StateBridge();
        bridge.Set(target, StableInheritedLeafMemberPrefix + ":BaseCount", 40);

        lifecycle.OnStart(new UnrealScriptContext(
            bridge,
            target,
            target,
            83,
            1));

        Assert.Equal(41, script.BaseCount);
        Assert.Equal(41, bridge.Get<int>(target, StableInheritedLeafMemberPrefix + ":BaseCount"));
        Assert.Contains(
            bridge.Writes,
            write => write.Path == StableInheritedLeafMemberPrefix + ":BaseCount");
        lifecycle.OnStop();

        var renamedScript = new StableRenamedInheritedLeafStateScript();
        IUnrealManagedScript renamedLifecycle = renamedScript;
        UnrealObjectHandle renamedTarget = UnrealObjectHandle.Create(38, 58);
        var renamedBridge = new StateBridge();
        renamedBridge.Set(renamedTarget, StableInheritedLeafMemberPrefix + ":BaseCount", 12);
        renamedLifecycle.OnStart(new UnrealScriptContext(
            renamedBridge,
            renamedTarget,
            renamedTarget,
            85,
            1));
        Assert.Equal(13, renamedScript.BaseCount);
        Assert.Equal(13, renamedBridge.Get<int>(
            renamedTarget,
            StableInheritedLeafMemberPrefix + ":BaseCount"));
        renamedLifecycle.OnTick(0.1f);
        Assert.Equal(14, renamedScript.BaseCount);
        Assert.Contains(
            renamedBridge.Writes,
            write => write.Path == StableInheritedLeafMemberPrefix + ":BaseCount");
        renamedLifecycle.OnStop();
    }

    [Fact]
    public void RpcUsesSelectedLeafClassIdentity()
    {
        var script = new StableRpcScript();
        IUnrealManagedScript lifecycle = script;
        var bridge = new StateBridge();
        UnrealObjectHandle target = UnrealObjectHandle.Create(37, 57);
        lifecycle.OnStart(new UnrealScriptContext(
            bridge,
            target,
            target,
            84,
            1));

        script.TriggerRpc(7);

        var invoke = Assert.Single(bridge.Invokes);
        Assert.Equal(target, invoke.Handle);
        Assert.Equal(StableRpcMemberPrefix + ":ServerAdd", invoke.Path);
        Assert.Equal(7, Assert.Single(invoke.Arguments));
        lifecycle.OnStop();
    }

    [Fact]
    public void ActorScriptSynchronizesInheritedCollectionsAcrossStartTickAndStop()
    {
        var script = new InheritedCollectionLeafScript();
        IUnrealManagedScript lifecycle = script;
        UnrealObjectHandle host = UnrealObjectHandle.Create(32, 52);
        UnrealObjectHandle target = UnrealObjectHandle.Create(33, 53);
        var bridge = new StateBridge();
        bridge.Set(target, InheritedCollectionPath + ":Values", (IReadOnlyList<int>)[1, 2]);
        bridge.Set(target, InheritedCollectionPath + ":Enabled", (IReadOnlySet<int>)new HashSet<int> { 3, 4 });
        bridge.Set(
            target,
            InheritedCollectionPath + ":Weights",
            (IReadOnlyDictionary<int, int>)new Dictionary<int, int> { [5] = 6 });
        bridge.Set(target, InheritedCollectionPath + ":TickCount", 0);

        lifecycle.OnStart(new UnrealScriptContext(bridge, target, target, 82, 1));

        Assert.Equal([11, 22], bridge.Get<IReadOnlyList<int>>(
            target,
            InheritedCollectionPath + ":Values"));
        Assert.Equal(
            [3, 4, 9],
            bridge.Get<IReadOnlySet<int>>(target, InheritedCollectionPath + ":Enabled").Order());
        Assert.Equal(
            new Dictionary<int, int> { [5] = 6, [11] = 22 },
            bridge.Get<IReadOnlyDictionary<int, int>>(
                target,
                InheritedCollectionPath + ":Weights"));

        bridge.Set(target, InheritedCollectionPath + ":Values", (IReadOnlyList<int>)[7, 8]);
        lifecycle.OnTick(0.25f);
        Assert.Equal([17, 28], bridge.Get<IReadOnlyList<int>>(
            target,
            InheritedCollectionPath + ":Values"));
        Assert.Equal(1, bridge.Get<int>(target, InheritedCollectionPath + ":TickCount"));

        lifecycle.OnStop();
        Assert.Equal([99], bridge.Get<IReadOnlyList<int>>(
            target,
            InheritedCollectionPath + ":Values"));
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
    public void ComponentScriptSynchronizesInheritedStateThroughHostAcrossLifecycle()
    {
        var script = new InheritedComponentLeafScript();
        IUnrealManagedScript lifecycle = script;
        UnrealObjectHandle host = UnrealObjectHandle.Create(18, 38);
        UnrealObjectHandle actor = UnrealObjectHandle.Create(19, 39);
        var bridge = new StateBridge();
        bridge.Set(host, InheritedComponentPath + ":BaseValue", 10);
        bridge.Set(host, InheritedComponentPath + ":LeafValue", 2);
        bridge.Set(host, InheritedComponentPath + ":TickCount", 0);

        lifecycle.OnStart(new UnrealScriptContext(bridge, host, actor, 75, 7));

        Assert.Equal(11, bridge.Get<int>(host, InheritedComponentPath + ":BaseValue"));
        Assert.Equal(4, bridge.Get<int>(host, InheritedComponentPath + ":LeafValue"));
        Assert.Equal(0, bridge.Get<int>(host, InheritedComponentPath + ":TickCount"));
        Assert.DoesNotContain(bridge.Writes, write => write.Handle == actor);

        lifecycle.OnTick(0.25f);

        Assert.Equal(21, bridge.Get<int>(host, InheritedComponentPath + ":BaseValue"));
        Assert.Equal(1, bridge.Get<int>(host, InheritedComponentPath + ":TickCount"));
        Assert.DoesNotContain(bridge.Writes, write => write.Handle == actor);

        lifecycle.OnStop();

        Assert.Equal(121, bridge.Get<int>(host, InheritedComponentPath + ":BaseValue"));
        Assert.Equal(1004, bridge.Get<int>(host, InheritedComponentPath + ":LeafValue"));
        Assert.DoesNotContain(bridge.Writes, write => write.Handle == actor);
    }

    [Fact]
    public void ReflectedScriptStateUsesTheLiveNativePropertyOnStart()
    {
        var script = new UnsupportedStateScript();
        IUnrealManagedScript lifecycle = script;
        UnrealObjectHandle target = UnrealObjectHandle.Create(15, 35);
        var bridge = new StateBridge();
        bridge.Set(target, UnsupportedStateScript.StatePath + ":BeginPlayCount", 0);

        lifecycle.OnStart(new UnrealScriptContext(bridge, target, target, 73, 5));

        Assert.Equal(1, script.BeginPlayCount);
        Assert.Equal(1, bridge.Get<int>(
            target,
            UnsupportedStateScript.StatePath + ":BeginPlayCount"));
        lifecycle.OnStop();
    }

    [Fact]
    public void AmbiguousInheritedStateDoesNotCreateAManagedMirror()
    {
        var script = new AmbiguousLeafStateScript();
        IUnrealManagedScript lifecycle = script;
        var bridge = new StateBridge();

        UnrealObjectHandle target = UnrealObjectHandle.Create(17, 37);
        lifecycle.OnStart(new UnrealScriptContext(bridge, target, target, 74, 6));

        Assert.Equal(0, bridge.ReadCount);
        Assert.Empty(bridge.Writes);
        Assert.True(script.IsRunning);
        lifecycle.OnStop();
    }

    [Fact]
    public void NestedLifecycleEventPreservesOuterNativeState()
    {
        var script = new NestedBoundaryScript();
        IUnrealManagedScript lifecycle = script;
        UnrealObjectHandle target = UnrealObjectHandle.Create(17, 37);
        var bridge = new StateBridge();
        bridge.Set(target, NestedBoundaryScript.StatePath + ":Count", 3);

        lifecycle.OnStart(new UnrealScriptContext(
            bridge,
            target,
            target,
            90,
            1));
        bridge.Writes.Clear();

        lifecycle.OnTick(0.1f);

        Assert.Equal(114, script.Count);
        Assert.Equal(114, bridge.Get<int>(target, NestedBoundaryScript.StatePath + ":Count"));
        Assert.Equal(3, bridge.Writes.Count);
        lifecycle.OnStop();
    }

    [Fact]
    public void LifecycleBoundaryRestoresDepthAfterFailure()
    {
        var script = new ThrowOnceBoundaryScript();
        IUnrealManagedScript lifecycle = script;
        UnrealObjectHandle target = UnrealObjectHandle.Create(18, 38);
        var bridge = new StateBridge();
        bridge.Set(target, ThrowOnceBoundaryScript.StatePath + ":Count", 5);

        lifecycle.OnStart(new UnrealScriptContext(
            bridge,
            target,
            target,
            91,
            1));

        Assert.Throws<ExpectedFailureException>(() => lifecycle.OnTick(0.1f));
        Assert.Equal(6, bridge.Get<int>(target, ThrowOnceBoundaryScript.StatePath + ":Count"));
        lifecycle.OnTick(0.1f);

        Assert.Equal(8, script.Count);
        Assert.Equal(8, bridge.Get<int>(target, ThrowOnceBoundaryScript.StatePath + ":Count"));
        lifecycle.OnStop();
    }

    [Fact]
    public void FailedManagedEventDoesNotFlushState()
    {
        var script = new ThrowOnceEventScript();
        IUnrealManagedScript lifecycle = script;
        UnrealObjectHandle target = UnrealObjectHandle.Create(25, 45);
        var bridge = new StateBridge();
        bridge.Set(target, ThrowOnceEventScript.StatePath + ":Count", 5);

        lifecycle.OnStart(new UnrealScriptContext(
            bridge,
            target,
            target,
            95,
            1));

        UnrealScriptEvent scriptEvent = new(
            UnrealScriptEventKind.ActorBeginOverlap,
            target,
            default);
        Assert.Throws<ExpectedFailureException>(() =>
            ((IUnrealManagedEventSink)script).OnUnrealEvent(scriptEvent));
        Assert.Equal(6, bridge.Get<int>(target, ThrowOnceEventScript.StatePath + ":Count"));

        ((IUnrealManagedEventSink)script).OnUnrealEvent(scriptEvent);
        Assert.Equal(7, script.Count);
        Assert.Equal(7, bridge.Get<int>(target, ThrowOnceEventScript.StatePath + ":Count"));
        lifecycle.OnStop();
    }

    [Fact]
    public void NextTickContinuationHydratesBeforeAsyncAdvanceAndFlushesItsState()
    {
        var script = new NextTickStateScript();
        IUnrealManagedScript lifecycle = script;
        UnrealObjectHandle target = UnrealObjectHandle.Create(20, 40);
        var bridge = new StateBridge();
        bridge.Set(target, NextTickStateScript.StatePath + ":Count", 1);

        lifecycle.OnStart(new UnrealScriptContext(
            bridge,
            target,
            target,
            92,
            1));
        bridge.Set(target, NextTickStateScript.StatePath + ":Count", 40);

        lifecycle.OnTick(0.1f);

        Assert.True(script.HasCompletedContinuation());
        Assert.Equal(47, script.Count);
        Assert.Equal(47, bridge.Get<int>(target, NextTickStateScript.StatePath + ":Count"));
        lifecycle.OnStop();
    }

    [Fact]
    public void StopCancellationContinuationRunsInsideStateBoundary()
    {
        var script = new StopContinuationStateScript();
        IUnrealManagedScript lifecycle = script;
        UnrealObjectHandle target = UnrealObjectHandle.Create(22, 42);
        var bridge = new StateBridge();
        bridge.Set(target, StopContinuationStateScript.StatePath + ":Count", 1);

        lifecycle.OnStart(new UnrealScriptContext(
            bridge,
            target,
            target,
            93,
            1));
        bridge.Set(target, StopContinuationStateScript.StatePath + ":Count", 40);

        lifecycle.OnStop();

        Assert.Equal(48, script.GetLastStoppedCount());
        Assert.Equal(48, bridge.Get<int>(target, StopContinuationStateScript.StatePath + ":Count"));
    }

    private const string StateActorPath = "/Game/Tests/BP_StateActor.BP_StateActor_C";
    private const string StateComponentPath = "/Game/Tests/BP_StateComponent.BP_StateComponent_C";
    private const string InheritedComponentPath = "/Game/Tests/BP_InheritedComponent.BP_InheritedComponent_C";
    private const string InheritedLeafStatePath = "/Game/Tests/BP_InheritedLeaf.BP_InheritedLeaf_C";
    private const string InheritedCollectionPath = "/Game/Tests/BP_InheritedCollections.BP_InheritedCollections_C";
    private const string AmbiguousStatePath = "/Game/Tests/BP_AmbiguousState.BP_AmbiguousState_C";
    private const string StableInheritedLeafPath = "/Game/Tests/BP_StableInherited.BP_StableInherited_C";
    private const string StableRpcPath = "/Game/Tests/BP_StableRpc.BP_StableRpc_C";
    private const string StableInheritedLeafMemberPrefix = "finare-class/us_abcdefabcdefabcdefabcdefabcdefab";
    private const string StableRpcMemberPrefix = "finare-class/us_1234567890abcdef1234567890abcdef";

    [ScriptAsset("Tests/NestedBoundary")]
    [UnrealScriptClass(
        UnrealScriptClassKind.Actor,
        GeneratedClassPath = NestedBoundaryScript.StatePath)]
    private sealed partial class NestedBoundaryScript : UnrealActorScript
    {
        public const string StatePath = "/Game/Tests/BP_NestedBoundary.BP_NestedBoundary_C";

        [Expose]
        public partial int Count { get; set; }

        protected override void OnTick(float deltaSeconds)
        {
            Count += 10;
            ((IUnrealManagedEventSink)this).OnUnrealEvent(new UnrealScriptEvent(
                UnrealScriptEventKind.ActorBeginOverlap,
                TargetHandle,
                default));
            Count += 100;
        }

        protected override void OnActorBeginOverlap(UnrealObjectHandle otherActor) => ++Count;
    }

    [ScriptAsset("Tests/FailingStartState")]
    [UnrealScriptClass(
        UnrealScriptClassKind.Actor,
        GeneratedClassPath = FailingStartStateScript.StatePath)]
    private sealed partial class FailingStartStateScript : UnrealActorScript
    {
        public const string StatePath = "/Game/Tests/BP_FailingStartState.BP_FailingStartState_C";

        [Expose]
        public partial int Count { get; set; }

        protected override void OnBeginPlay()
        {
            Lifetime.Register(static () => throw new InvalidOperationException(
                "start cleanup callback failed"));
            ++Count;
            throw new ExpectedFailureException();
        }
    }

    [ScriptAsset("Tests/ThrowOnceBoundary")]
    [UnrealScriptClass(
        UnrealScriptClassKind.Actor,
        GeneratedClassPath = ThrowOnceBoundaryScript.StatePath)]
    private sealed partial class ThrowOnceBoundaryScript : UnrealActorScript
    {
        public const string StatePath = "/Game/Tests/BP_ThrowOnceBoundary.BP_ThrowOnceBoundary_C";

        [Expose]
        public partial int Count { get; set; }

        private bool _throw = true;

        protected override void OnTick(float deltaSeconds)
        {
            ++Count;
            if (_throw)
            {
                _throw = false;
                throw new ExpectedFailureException();
            }
            ++Count;
        }
    }

    [ScriptAsset("Tests/ThrowOnceEvent")]
    [UnrealScriptClass(
        UnrealScriptClassKind.Actor,
        GeneratedClassPath = ThrowOnceEventScript.StatePath)]
    private sealed partial class ThrowOnceEventScript : UnrealActorScript
    {
        public const string StatePath = "/Game/Tests/BP_ThrowOnceEvent.BP_ThrowOnceEvent_C";

        [Expose]
        public partial int Count { get; set; }

        private bool _throw = true;

        protected override void OnActorBeginOverlap(UnrealObjectHandle otherActor)
        {
            ++Count;
            if (_throw)
            {
                _throw = false;
                throw new ExpectedFailureException();
            }
        }
    }

    [ScriptAsset("Tests/NextTickState")]
    [UnrealScriptClass(
        UnrealScriptClassKind.Actor,
        GeneratedClassPath = NextTickStateScript.StatePath)]
    private sealed partial class NextTickStateScript : UnrealActorScript
    {
        public const string StatePath = "/Game/Tests/BP_NextTickState.BP_NextTickState_C";

        [Expose]
        public partial int Count { get; set; }

        private Task? _continuation;

        protected override void OnBeginPlay() => _continuation = AddAfterNextTickAsync();

        public bool HasCompletedContinuation() => _continuation?.IsCompletedSuccessfully == true;

        private async Task AddAfterNextTickAsync()
        {
            await NextTickAsync();
            Count += 7;
        }
    }

    [ScriptAsset("Tests/StopContinuationState")]
    [UnrealScriptClass(
        UnrealScriptClassKind.Actor,
        GeneratedClassPath = StopContinuationStateScript.StatePath)]
    private sealed partial class StopContinuationStateScript : UnrealActorScript
    {
        public const string StatePath = "/Game/Tests/BP_StopContinuationState.BP_StopContinuationState_C";

        [Expose]
        public partial int Count { get; set; }

        private int _lastStoppedCount;

        public int GetLastStoppedCount() => _lastStoppedCount;

        protected override void OnBeginPlay() => _ = ObserveCancellationAsync();

        private async Task ObserveCancellationAsync()
        {
            try
            {
                await DelayAsync(10.0f);
            }
            catch (OperationCanceledException)
            {
                Count += 7;
            }
        }

        protected override void OnEndPlay()
        {
            ++Count;
            _lastStoppedCount = Count;
        }
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
    private sealed partial class StateActorScript : UnrealActorScript
    {
        [Expose(InstanceEditable = true)]
        public partial int Count { get; set; } = 1;

        [Expose]
        public partial bool Enabled { get; set; }

        [Expose]
        public partial byte ByteValue { get; set; }

        [Expose]
        public partial long Large { get; set; }

        [Expose]
        public partial float Ratio { get; set; }

        [Expose]
        public partial double Precision { get; set; }

        [Expose]
        public partial string Label { get; set; } = string.Empty;

        [Expose]
        public partial UnrealName RowName { get; set; }

        [Expose]
        public partial UnrealText DisplayText { get; set; }

        [Expose]
        public partial UnrealObjectHandle ObjectValue { get; set; }

        [Expose]
        public partial UnrealClassHandle ClassValue { get; set; }

        [Expose]
        public partial UnrealVector Offset { get; set; }

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

    [ScriptAsset("Tests/InheritedStateBase")]
    private partial class InheritedBaseStateScript : UnrealActorScript
    {
        public partial int BaseCount { get; set; }
    }

    [ScriptAsset("Tests/InheritedStateLeaf")]
    [UnrealScriptClass(
        UnrealScriptClassKind.Actor,
        GeneratedClassPath = InheritedLeafStatePath)]
    private sealed partial class InheritedLeafStateScript : InheritedBaseStateScript
    {
        public partial int LeafCount { get; set; }

        protected override void OnBeginPlay()
        {
            ++BaseCount;
            LeafCount += 2;
        }
    }

    [ScriptAsset("Tests/StableInheritedBase", ClassId = "us_11111111111111111111111111111111")]
    private partial class StableInheritedBaseStateScript : UnrealActorScript
    {
        [Expose]
        public partial int BaseCount { get; set; }
    }

    [ScriptAsset("Tests/StableInheritedLeaf", ClassId = "us_abcdefabcdefabcdefabcdefabcdefab")]
    [UnrealScriptClass(
        UnrealScriptClassKind.Actor,
        GeneratedClassPath = StableInheritedLeafPath)]
    private sealed partial class StableInheritedLeafStateScript : StableInheritedBaseStateScript
    {
        protected override void OnBeginPlay() => ++BaseCount;

        protected override void OnTick(float deltaSeconds) => ++BaseCount;
    }

    [ScriptAsset("Tests/StableInheritedRenamedLeaf", ClassId = "us_abcdefabcdefabcdefabcdefabcdefab")]
    [UnrealScriptClass(
        UnrealScriptClassKind.Actor,
        GeneratedClassPath = "/Game/Tests/BP_StableInheritedRenamed.BP_StableInheritedRenamed_C")]
    private sealed partial class StableRenamedInheritedLeafStateScript : StableInheritedBaseStateScript
    {
        protected override void OnBeginPlay() => ++BaseCount;

        protected override void OnTick(float deltaSeconds) => ++BaseCount;
    }

    [ScriptAsset("Tests/StableRpc", ClassId = "us_1234567890abcdef1234567890abcdef")]
    [UnrealScriptClass(
        UnrealScriptClassKind.Actor,
        GeneratedClassPath = StableRpcPath)]
    private sealed class StableRpcScript : UnrealActorScript
    {
        public void TriggerRpc(int value) => SendRpc(nameof(ServerAdd), value);

        [UnrealRpc(UnrealRpcTarget.Server)]
        public void ServerAdd(int value) { }
    }

    [ScriptAsset("Tests/InheritedCollectionBase")]
    private partial class InheritedCollectionBaseScript : UnrealActorScript
    {
        public partial IReadOnlyList<int> Values { get; set; } = [0];

        public partial IReadOnlySet<int> Enabled { get; set; } = new HashSet<int>();

        public partial IReadOnlyDictionary<int, int> Weights { get; set; } =
            new Dictionary<int, int>();
    }

    [ScriptAsset("Tests/InheritedCollectionLeaf")]
    [UnrealScriptClass(
        UnrealScriptClassKind.Actor,
        GeneratedClassPath = InheritedCollectionPath)]
    private sealed partial class InheritedCollectionLeafScript : InheritedCollectionBaseScript
    {
        public partial int TickCount { get; set; }

        protected override void OnBeginPlay()
        {
            Values = [Values[0] + 10, Values[1] + 20];
            Enabled = new HashSet<int>(Enabled) { 9 };
            Weights = new Dictionary<int, int>(Weights) { [11] = 22 };
        }

        protected override void OnTick(float deltaSeconds)
        {
            Values = [Values[0] + 10, Values[1] + 20];
            ++TickCount;
        }

        protected override void OnEndPlay()
        {
            Values = [99];
        }
    }

    [ScriptAsset("Tests/AmbiguousStateBase")]
    private partial class AmbiguousBaseStateScript : UnrealActorScript
    {
        public partial int Value { get; set; } = 1;
    }

    [ScriptAsset("Tests/AmbiguousStateLeaf")]
    [UnrealScriptClass(
        UnrealScriptClassKind.Actor,
        GeneratedClassPath = AmbiguousStatePath)]
    private sealed partial class AmbiguousLeafStateScript : AmbiguousBaseStateScript
    {
        public partial int value { get; set; } = 2;
    }

    [ScriptAsset("Tests/StateComponent")]
    [UnrealScriptClass(
        UnrealScriptClassKind.ActorComponent,
        GeneratedClassPath = StateComponentPath)]
    private sealed partial class StateComponentScript : UnrealActorComponentScript
    {
        [Expose]
        public partial int Value { get; set; }

        protected override void OnBeginPlay() => ++Value;
    }

    [ScriptAsset("Tests/InheritedComponentBase")]
    private partial class InheritedComponentBaseScript : UnrealActorComponentScript
    {
        public partial int BaseValue { get; set; }
    }

    [ScriptAsset("Tests/InheritedComponentLeaf")]
    [UnrealScriptClass(
        UnrealScriptClassKind.ActorComponent,
        GeneratedClassPath = InheritedComponentPath)]
    private sealed partial class InheritedComponentLeafScript : InheritedComponentBaseScript
    {
        public partial int LeafValue { get; set; }

        public partial int TickCount { get; set; }

        protected override void OnBeginPlay()
        {
            ++BaseValue;
            LeafValue += 2;
        }

        protected override void OnTick(float deltaSeconds)
        {
            BaseValue += 10;
            ++TickCount;
        }

        protected override void OnEndPlay()
        {
            BaseValue += 100;
            LeafValue += 1000;
        }
    }

    [ScriptAsset("Tests/UnsupportedState")]
    [UnrealScriptClass(
        UnrealScriptClassKind.Actor,
        GeneratedClassPath = "/Game/Tests/BP_UnsupportedState.BP_UnsupportedState_C")]
    private sealed partial class UnsupportedStateScript : UnrealActorScript
    {
        public const string StatePath = "/Game/Tests/BP_UnsupportedState.BP_UnsupportedState_C";

        public partial int BeginPlayCount { get; private set; }

        [Expose]
        public partial decimal Unsupported { get; set; }

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

        public List<(UnrealObjectHandle Handle, string Path, IReadOnlyList<object?> Arguments)> Invokes { get; } = [];

        public int ReadCount { get; private set; }

        public void Set<T>(UnrealObjectHandle handle, string path, T value)
            where T : notnull => _values[(handle, path)] = value;

        public T Get<T>(UnrealObjectHandle handle, string path) =>
            (T)_values[(handle, path)];

        public T ReadProperty<T>(UnrealObjectHandle target, string propertyPath)
        {
            ++ReadCount;
            return Get<T>(target, propertyPath);
        }

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
            IReadOnlyList<object?> arguments) => Invokes.Add((target, functionPath, arguments));
    }
}
