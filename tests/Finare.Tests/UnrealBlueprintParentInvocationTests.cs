using Finare;
using Finare.Unreal;

namespace Finare.Tests;

public sealed class UnrealBlueprintParentInvocationTests
{
    [Fact]
    public void ManagedOverrideHelperUsesHostAndPreservesRefOutResults()
    {
        var bridge = new ParentBridge();
        var script = new ManagedParentCallScript();
        UnrealObjectHandle host = UnrealObjectHandle.Create(11, 3);
        UnrealObjectHandle target = UnrealObjectHandle.Create(17, 3);
        ((IUnrealManagedScript)script).OnStart(new UnrealScriptContext(
            bridge,
            host,
            target,
            InstanceId: 41,
            Generation: 2));

        int accumulator = 5;
        int result = script.RunEvaluate(7, ref accumulator, out int output);

        Assert.Equal(91, result);
        Assert.Equal(71, accumulator);
        Assert.Equal(72, output);
        Assert.Equal(host, bridge.Target);
        Assert.Equal(UnrealMemberId.FromPath(
            "/Script/FinareEventExtensionFixture.AFinareEventExtensionFixture:EvaluateInherited"),
            bridge.MemberId);
        Assert.Equal(
            "/Script/FinareEventExtensionFixture.AFinareEventExtensionFixture:EvaluateInherited",
            bridge.MemberPath);
        Assert.Collection(
            bridge.Arguments,
            argument =>
            {
                Assert.Equal(UnrealParameterDirection.Input, argument.Direction);
                Assert.Equal(7, argument.Value);
            },
            argument =>
            {
                Assert.Equal(UnrealParameterDirection.InputOutput, argument.Direction);
                Assert.Equal(5, argument.Value);
            },
            argument => Assert.Equal(UnrealParameterDirection.Output, argument.Direction));

        ((IUnrealManagedScript)script).OnStop();
    }

    [Fact]
    public void InputOnlyHelperRejectsRefAndOutAndReturnMismatchBeforeBridgeCall()
    {
        var bridge = new ParentBridge();
        var script = new ManagedParentCallScript();
        UnrealObjectHandle handle = UnrealObjectHandle.Create(11, 4);
        ((IUnrealManagedScript)script).OnStart(new UnrealScriptContext(
            bridge,
            handle,
            handle,
            InstanceId: 42,
            Generation: 1));

        Assert.Throws<InvalidOperationException>(() => script.RunInputOnlyWithRef(4));
        Assert.Throws<InvalidOperationException>(() => script.RunWrongReturnType(4));
        Assert.Equal(0, bridge.CallCount);
        ((IUnrealManagedScript)script).OnStop();
    }

    [Fact]
    public void ParentHelperRejectsInitializationAndDestructionCalls()
    {
        var bridge = new ParentBridge();
        UnrealObjectHandle handle = UnrealObjectHandle.Create(11, 5);
        var initializing = new LifecycleParentCallScript(callDuringInitialization: true);
        InvalidOperationException initializeFailure = Assert.Throws<InvalidOperationException>(() =>
            ((IUnrealManagedScript)initializing).OnStart(new UnrealScriptContext(
                bridge,
                handle,
                handle,
                InstanceId: 43,
                Generation: 1)));
        Assert.Contains("initialization", initializeFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, bridge.CallCount);

        var destroying = new LifecycleParentCallScript(callDuringInitialization: false);
        ((IUnrealManagedScript)destroying).OnStart(new UnrealScriptContext(
            bridge,
            handle,
            handle,
            InstanceId: 44,
            Generation: 1));
        InvalidOperationException destroyFailure = Assert.Throws<InvalidOperationException>(
            () => ((IUnrealManagedScript)destroying).OnStop());
        Assert.Contains("destruction", destroyFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, bridge.CallCount);
    }

    [Fact]
    public void NativeParentOutputBridgeUsesDistinctOperationAndCapability()
    {
        var transport = new ParentCallFrameTransport(hasParentCapability: true);
        var bridge = new NativeUnrealInvocationBridge(transport, worldHostId: 52);
        UnrealObjectHandle host = UnrealObjectHandle.Create(2, 6);
        UnrealOutputInvocationResult result = bridge.InvokeParentWithOutputs(
            host,
            UnrealMemberId.FromPath("/Script/Test:Parent"),
            "/Script/Test:Parent",
            typeof(int),
            [new(typeof(int), UnrealParameterDirection.Input, 4)]);

        Assert.Equal(99, result.ReturnValue);
        Assert.Equal(UnrealNativeOperationV3.InvokeParent, transport.LastOperation);
        Assert.Equal(host, transport.LastTarget);

        var unsupportedTransport = new ParentCallFrameTransport(hasParentCapability: false);
        var unsupportedBridge = new NativeUnrealInvocationBridge(unsupportedTransport, worldHostId: 53);
        Assert.Throws<NotSupportedException>(() => unsupportedBridge.InvokeParentWithOutputs(
            host,
            UnrealMemberId.FromPath("/Script/Test:Parent"),
            "/Script/Test:Parent",
            typeof(int),
            [new(typeof(int), UnrealParameterDirection.Input, 4)]));
        Assert.Equal(0, unsupportedTransport.CallCount);
    }

    [Fact]
    public void ParentInvocationFlushesAndHydratesStateAroundTheNativeBridge()
    {
        var bridge = new StateParentBridge();
        UnrealObjectHandle host = UnrealObjectHandle.Create(3, 8);
        UnrealObjectHandle target = UnrealObjectHandle.Create(4, 8);
        bridge.Set(target, StateParentCallScript.StatePath + ":Value", 4);
        var script = new StateParentCallScript();
        IUnrealManagedScript lifecycle = script;
        lifecycle.OnStart(new UnrealScriptContext(bridge, host, target, 45, 1));

        bridge.Set(target, StateParentCallScript.StatePath + ":Value", 8);
        script.BeginGeneratedFunctionCall();
        try
        {
            Assert.Equal(10, script.RunEvaluate(5));
        }
        finally
        {
            script.EndGeneratedFunctionCall();
        }

        Assert.Equal(19, script.Value);
        Assert.Equal(19, bridge.Get<int>(target, StateParentCallScript.StatePath + ":Value"));
        Assert.Equal(
            new object[] { 4, 13, 19 },
            bridge.Writes.Select(write => write.Value).ToArray());
        Assert.Equal(13, bridge.ParentObservedValue);
        lifecycle.OnStop();
    }

    [Fact]
    public void ParentInvocationReentryHydratesParentStateAndReturnsNestedWrites()
    {
        var bridge = new StateParentBridge();
        UnrealObjectHandle host = UnrealObjectHandle.Create(3, 8);
        UnrealObjectHandle target = UnrealObjectHandle.Create(4, 8);
        bridge.Set(target, StateParentCallScript.StatePath + ":Value", 4);
        var script = new StateParentCallScript();
        bridge.ParentReentry = () =>
        {
            script.BeginGeneratedFunctionCall();
            try
            {
                script.ReenterFromNative();
            }
            finally
            {
                script.EndGeneratedFunctionCall();
            }
        };
        IUnrealManagedScript lifecycle = script;
        lifecycle.OnStart(new UnrealScriptContext(bridge, host, target, 49, 1));

        bridge.Set(target, StateParentCallScript.StatePath + ":Value", 8);
        script.BeginGeneratedFunctionCall();
        try
        {
            Assert.Equal(10, script.RunEvaluate(5));
        }
        finally
        {
            script.EndGeneratedFunctionCall();
        }

        Assert.Equal(20, script.Value);
        Assert.Equal(20, bridge.Get<int>(target, StateParentCallScript.StatePath + ":Value"));
        Assert.Equal(13, bridge.ParentObservedValue);
        Assert.Equal(new object[] { 4, 13, 19, 20 },
            bridge.Writes.Select(write => write.Value).ToArray());
        lifecycle.OnStop();
    }

    [Fact]
    public void ParentFailureHydratesNativeStateAndPreservesTheOriginalException()
    {
        var bridge = new StateParentBridge
        {
            ParentStateValue = 23,
            ParentException = new InvalidOperationException("parent body failed"),
        };
        UnrealObjectHandle host = UnrealObjectHandle.Create(3, 8);
        UnrealObjectHandle target = UnrealObjectHandle.Create(4, 8);
        bridge.Set(target, StateParentCallScript.StatePath + ":Value", 4);
        var script = new StateParentCallScript();
        IUnrealManagedScript lifecycle = script;
        lifecycle.OnStart(new UnrealScriptContext(bridge, host, target, 46, 1));
        bridge.Set(target, StateParentCallScript.StatePath + ":Value", 8);

        script.BeginGeneratedFunctionCall();
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => script.RunEvaluate(5));
        script.EndGeneratedFunctionCall();

        Assert.Equal("parent body failed", failure.Message);
        Assert.Equal(13, bridge.ParentObservedValue);
        Assert.Equal(23, script.Value);
        Assert.Equal(23, bridge.Get<int>(target, StateParentCallScript.StatePath + ":Value"));
        lifecycle.OnStop();
    }

    [Fact]
    public void ParentAndHydrateFailurePreservesTheCallFailureFirst()
    {
        var bridge = new StateParentBridge
        {
            ParentStateValue = 29,
            ParentException = new InvalidOperationException("parent body failed first"),
            FailNextHydrateAfterParent = true,
        };
        UnrealObjectHandle host = UnrealObjectHandle.Create(3, 8);
        UnrealObjectHandle target = UnrealObjectHandle.Create(4, 8);
        bridge.Set(target, StateParentCallScript.StatePath + ":Value", 4);
        var script = new StateParentCallScript();
        IUnrealManagedScript lifecycle = script;
        lifecycle.OnStart(new UnrealScriptContext(bridge, host, target, 47, 1));
        bridge.Set(target, StateParentCallScript.StatePath + ":Value", 8);

        AggregateException failure = Assert.Throws<AggregateException>(
            () => script.RunEvaluate(5));

        Assert.Collection(
            failure.InnerExceptions,
            exception => Assert.Equal("parent body failed first", exception.Message),
            exception => Assert.Equal("hydrate failed", exception.Message));
        lifecycle.OnStop();
    }

    [Fact]
    public void GenerationBridgeRejectsBeforeStateFlushAndDisposedBridgeRejectsDirectParentCall()
    {
        var inner = new StateParentBridge(supportsParentInvocation: false);
        var generation = new UnrealGenerationInvocationBridge(inner);
        UnrealObjectHandle host = UnrealObjectHandle.Create(3, 8);
        UnrealObjectHandle target = UnrealObjectHandle.Create(4, 8);
        inner.Set(target, StateParentCallScript.StatePath + ":Value", 4);
        var script = new StateParentCallScript();
        IUnrealManagedScript lifecycle = script;
        lifecycle.OnStart(new UnrealScriptContext(generation, host, target, 48, 1));
        script.BeginGeneratedFunctionCall();
        script.Value = 17;
        int writesBeforeRejectedCall = inner.Writes.Count;

        Assert.Throws<NotSupportedException>(() => script.RunEvaluate(5));
        Assert.Equal(writesBeforeRejectedCall, inner.Writes.Count);
        lifecycle.OnStop();
        generation.Dispose();

        var disposedInner = new ParentBridge();
        var disposedGeneration = new UnrealGenerationInvocationBridge(disposedInner);
        disposedGeneration.Dispose();
        Assert.False(disposedGeneration.SupportsParentInvocation);
        Assert.Throws<ObjectDisposedException>(() => disposedGeneration.InvokeParentWithOutputs(
            host,
            UnrealMemberId.FromPath("/Script/Test:Parent"),
            "/Script/Test:Parent",
            typeof(int),
            [new(typeof(int), UnrealParameterDirection.Input, 4)]));
        Assert.Equal(0, disposedInner.CallCount);
    }

    [Fact]
    public void ParentInvocationRetriesCorrelatedBufferWithoutChangingTheOperation()
    {
        var transport = new ParentCallFrameTransport(
            hasParentCapability: true,
            bufferTooSmallFirst: true);
        var bridge = new NativeUnrealInvocationBridge(transport, worldHostId: 54);
        UnrealOutputInvocationResult result = bridge.InvokeParentWithOutputs(
            UnrealObjectHandle.Create(2, 9),
            UnrealMemberId.FromPath("/Script/Test:Parent"),
            "/Script/Test:Parent",
            typeof(int),
            [new(typeof(int), UnrealParameterDirection.Input, 4)]);

        Assert.Equal(99, result.ReturnValue);
        Assert.Equal(2, transport.CallCount);
        Assert.All(transport.Operations, operation =>
            Assert.Equal(UnrealNativeOperationV3.InvokeParent, operation));
    }

    [Fact]
    public void ParentInvocationSizesPooledArenaToAContainerInputWithoutReenumerating()
    {
        var values = new CountingReadOnlyList(200);
        var transport = new ParentCallFrameTransport(hasParentCapability: true);
        var bridge = new NativeUnrealInvocationBridge(transport, worldHostId: 55);

        UnrealOutputInvocationResult result = bridge.InvokeParentWithOutputs(
            UnrealObjectHandle.Create(2, 10),
            UnrealMemberId.FromPath("/Script/Test:ParentArray"),
            "/Script/Test:ParentArray",
            typeof(int),
            [new(typeof(IReadOnlyList<int>), UnrealParameterDirection.Input, values)]);

        Assert.Equal(99, result.ReturnValue);
        Assert.Equal(1, values.EnumerationCount);
        Assert.Single(transport.InputByteCounts);
        Assert.InRange(transport.InputByteCounts[0], 4097, 64 * 1024 - 1);
        Assert.Equal(transport.InputByteCounts[0], transport.BufferCapacities[0]);
    }

    private sealed class ParentBridge : IUnrealInvocationBridge, IUnrealParentInvocationBridge
    {
        private readonly bool _supportsParentInvocation;

        public ParentBridge(bool supportsParentInvocation = true) =>
            _supportsParentInvocation = supportsParentInvocation;

        public int CallCount { get; private set; }

        public UnrealObjectHandle Target { get; private set; }

        public ulong MemberId { get; private set; }

        public string MemberPath { get; private set; } = string.Empty;

        public IReadOnlyList<UnrealOutputArgument> Arguments { get; private set; } = [];

        public bool SupportsParentInvocation => _supportsParentInvocation;

        public T ReadProperty<T>(UnrealObjectHandle target, string propertyPath) =>
            throw new NotSupportedException();

        public void WriteProperty<T>(UnrealObjectHandle target, string propertyPath, T value) =>
            throw new NotSupportedException();

        public T Invoke<T>(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<object?> arguments) => throw new NotSupportedException();

        public void Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<object?> arguments) => throw new NotSupportedException();

        public UnrealOutputInvocationResult InvokeParentWithOutputs(
            UnrealObjectHandle host,
            ulong memberId,
            string memberPath,
            Type returnType,
            IReadOnlyList<UnrealOutputArgument> arguments)
        {
            CallCount++;
            Target = host;
            MemberId = memberId;
            MemberPath = memberPath;
            Arguments = arguments;
            return new UnrealOutputInvocationResult(91, [71, 72]);
        }
    }

    private sealed class ParentCallFrameTransport(
        bool hasParentCapability,
        bool bufferTooSmallFirst = false) :
        IUnrealNativeInvocationTransport,
        IUnrealNativeCallFrameTransportV3,
        IUnrealNativeCallFrameScopeFactory,
        IUnrealNativeCallFrameScope
    {
        public int CallCount { get; private set; }

        public List<UnrealNativeOperationV3> Operations { get; } = [];

        public List<int> InputByteCounts { get; } = [];

        public List<int> BufferCapacities { get; } = [];

        private bool _returnedBufferTooSmall;

        public UnrealNativeOperationV3 LastOperation { get; private set; }

        public UnrealObjectHandle LastTarget { get; private set; }

        IUnrealNativeCallFrameScope? IUnrealNativeCallFrameScopeFactory.BeginCallFrameInvocation() =>
            hasParentCapability ? this : null;

        public void Dispose()
        {
        }

        public UnrealNativeCapabilitiesV3 Capabilities =>
            UnrealNativeCapabilitiesV3.FixedScalars |
            UnrealNativeCapabilitiesV3.OutputAndReference |
            UnrealNativeCapabilitiesV3.Containers |
            (hasParentCapability
                ? UnrealNativeCapabilitiesV3.BlueprintParentCalls |
                  UnrealNativeCapabilitiesV3.CorrelatedResults
                : 0);

        public UnrealInvocationResponse ReadProperty(
            UnrealObjectHandle target,
            string propertyPath) => throw new NotSupportedException();

        public UnrealInvocationResponse WriteProperty(
            UnrealObjectHandle target,
            string propertyPath,
            UnrealInvocationValue value) => throw new NotSupportedException();

        public UnrealInvocationResponse Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<UnrealInvocationValue> arguments) => throw new NotSupportedException();

        public UnrealNativeCallOutcomeV3 ExecuteById(
            UnrealNativeOperationV3 operation,
            UnrealObjectHandle target,
            ulong memberId,
            ReadOnlySpan<UnrealNativeValueV3> arguments,
            Span<UnrealNativeValueV3> results,
            Span<byte> byteBuffer,
            int inputByteCount)
        {
            CallCount++;
            Operations.Add(operation);
            InputByteCounts.Add(inputByteCount);
            BufferCapacities.Add(byteBuffer.Length);
            LastOperation = operation;
            LastTarget = target;
            if (operation != UnrealNativeOperationV3.InvokeParent || results.Length != 1)
            {
                return new UnrealNativeCallOutcomeV3(
                    UnrealNativeCallStatusV3.InvocationFailure,
                    UnrealInvocationError.FunctionNotCallable,
                    UnrealObjectHandleError.None,
                    0,
                    0,
                    0,
                    0,
                    "Unexpected parent test operation.");
            }
            if (bufferTooSmallFirst && !_returnedBufferTooSmall)
            {
                _returnedBufferTooSmall = true;
                return new UnrealNativeCallOutcomeV3(
                    UnrealNativeCallStatusV3.BufferTooSmall,
                    UnrealInvocationError.None,
                    UnrealObjectHandleError.None,
                    1,
                    0,
                    1,
                    8192,
                    "correlated result requires a retry");
            }
            results[0].Kind = UnrealNativeValueKindV3.Int32;
            results[0].Payload0 = 99;
            return UnrealNativeCallOutcomeV3.Succeeded(1, 0);
        }
    }
}

internal sealed class CountingReadOnlyList(int count) : IReadOnlyList<int>
{
    private readonly int[] _values = Enumerable.Range(0, count).ToArray();

    public int EnumerationCount { get; private set; }

    public int Count => _values.Length;

    public int this[int index] => _values[index];

    public IEnumerator<int> GetEnumerator()
    {
        EnumerationCount++;
        return ((IEnumerable<int>)_values).GetEnumerator();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

[ScriptAsset("Tests/ParentState")]
[UnrealScriptClass(
    UnrealScriptClassKind.Actor,
    GeneratedClassPath = StateParentCallScript.StatePath)]
public sealed class StateParentCallScript : UnrealActorScript
{
    public const string StatePath = "/Game/Tests/BP_ParentState.BP_ParentState_C";

    [Expose]
    public int Value { get; set; }

    [UnrealBlueprintOverride(
        "/Script/FinareEventExtensionFixture.AFinareEventExtensionFixture",
        "EvaluateInheritedAlternate")]
    public int Evaluate(int input)
    {
        Value += 5;
        int parentValue = CallParent<int>(nameof(Evaluate), input);
        ++Value;
        return parentValue;
    }

    public int RunEvaluate(int input) => Evaluate(input);

    public void ReenterFromNative() => ++Value;
}

internal sealed class StateParentBridge : IUnrealInvocationBridge, IUnrealParentInvocationBridge
{
    private readonly bool _supportsParentInvocation;
    private readonly Dictionary<(UnrealObjectHandle Handle, string Path), object> _values = [];

    public StateParentBridge(bool supportsParentInvocation = true) =>
        _supportsParentInvocation = supportsParentInvocation;

    public List<(UnrealObjectHandle Handle, string Path, object Value)> Writes { get; } = [];

    public bool SupportsParentInvocation => _supportsParentInvocation;

    public int ParentStateValue { get; set; } = 18;

    public Exception? ParentException { get; set; }

    public bool FailNextHydrateAfterParent { get; set; }

    public int ParentObservedValue { get; private set; }

    public Action? ParentReentry { get; set; }

    private bool _failNextRead;

    public void Set<T>(UnrealObjectHandle handle, string path, T value)
        where T : notnull => _values[(handle, path)] = value;

    public T Get<T>(UnrealObjectHandle handle, string path) => (T)_values[(handle, path)];

    public T ReadProperty<T>(UnrealObjectHandle target, string propertyPath)
    {
        if (_failNextRead)
        {
            _failNextRead = false;
            throw new InvalidOperationException("hydrate failed");
        }
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
        IReadOnlyList<object?> arguments) => throw new NotSupportedException();

    public UnrealOutputInvocationResult InvokeParentWithOutputs(
        UnrealObjectHandle host,
        ulong memberId,
        string memberPath,
        Type returnType,
        IReadOnlyList<UnrealOutputArgument> arguments)
    {
        Assert.Equal(host, UnrealObjectHandle.Create(3, 8));
        Assert.Equal("/Script/FinareEventExtensionFixture.AFinareEventExtensionFixture:EvaluateInheritedAlternate", memberPath);
        var stateKey = (UnrealObjectHandle.Create(4, 8), StateParentCallScript.StatePath + ":Value");
        ParentObservedValue = Get<int>(stateKey.Item1, stateKey.Item2);
        _values[stateKey] = ParentStateValue;
        ParentReentry?.Invoke();
        if (FailNextHydrateAfterParent)
        {
            _failNextRead = true;
        }
        if (ParentException is not null)
        {
            throw ParentException;
        }
        return new UnrealOutputInvocationResult(10, []);
    }
}

public sealed class ManagedParentCallScript : UnrealObjectScript
{
    [UnrealBlueprintOverride(
        "/Script/FinareEventExtensionFixture.AFinareEventExtensionFixture",
        "EvaluateInherited")]
    public int EvaluateInherited(
        int nativeInput,
        ref int nativeAccumulator,
        out int nativeOutput)
    {
        UnrealOutputInvocationResult result = CallParentWithOutputs(
            nameof(EvaluateInherited),
            nativeInput,
            nativeAccumulator);
        nativeAccumulator = (int)result.Outputs[0]!;
        nativeOutput = (int)result.Outputs[1]!;
        return (int)result.ReturnValue!;
    }

    [UnrealBlueprintOverride(
        "/Script/FinareEventExtensionFixture.AFinareEventExtensionFixture",
        "EvaluateInheritedAlternate")]
    public int InputOnly(int value) => CallParent<int>(nameof(InputOnly), value);

    [UnrealBlueprintOverride(
        "/Script/FinareEventExtensionFixture.AFinareEventExtensionFixture",
        "ExchangeInheritedArray")]
    public int InputOnlyWithRef(ref int value) =>
        CallParent<int>(nameof(InputOnlyWithRef), value);

    public int RunEvaluate(int input, ref int accumulator, out int output) =>
        EvaluateInherited(input, ref accumulator, out output);

    public int RunInputOnlyWithRef(int value)
    {
        int local = value;
        return InputOnlyWithRef(ref local);
    }

    public long RunWrongReturnType(int value) => CallParent<long>(nameof(InputOnly), value);
}

public sealed class LifecycleParentCallScript(bool callDuringInitialization) : UnrealObjectScript
{
    [UnrealBlueprintOverride(
        "/Script/FinareEventExtensionFixture.AFinareEventExtensionFixture",
        "EvaluateInheritedAlternate")]
    public int Evaluate(int value) => CallParent<int>(nameof(Evaluate), value);

    protected override void OnInitialized()
    {
        if (callDuringInitialization)
        {
            _ = Evaluate(1);
        }
    }

    protected override void OnDestroyed()
    {
        if (!callDuringInitialization)
        {
            _ = Evaluate(1);
        }
    }
}
