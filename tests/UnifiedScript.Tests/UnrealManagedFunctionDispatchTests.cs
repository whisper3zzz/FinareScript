using UnifiedScript;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealManagedFunctionDispatchTests
{
    [Fact]
    public unsafe void ScalarDispatchUsesFixedAbiWithoutReflectionInvokeOrBoxing()
    {
        var transport = new FunctionTransport();
        var bridge = new NativeUnrealInvocationBridge(transport, worldHostId: 71);
        UnrealObjectHandle handle = UnrealObjectHandle.Create(1, 1);
        using var instance = UnrealWorldManagedEntryPoint.ManagedScriptInstance.Create(
            worldId: 71,
            instanceId: 91,
            bridge,
            handle,
            handle,
            typeof(ManagedFunctionDispatchScript).Assembly.Location,
            typeof(ManagedFunctionDispatchScript).FullName!);
        instance.Start();

        Span<UnrealManagedFunctionValueV1> mixArguments =
        [
            UnrealManagedFunctionValueV1.FromInt32(20),
            UnrealManagedFunctionValueV1.FromBoolean(true),
            UnrealManagedFunctionValueV1.FromByte(2),
            UnrealManagedFunctionValueV1.FromInt64(5),
        ];
        fixed (UnrealManagedFunctionValueV1* pointer = mixArguments)
        {
            UnrealManagedFunctionValueV1 result = instance.InvokeFunction(
                nameof(ManagedFunctionDispatchScript.Mix),
                (nint)pointer,
                mixArguments.Length);
            Assert.Equal(UnrealManagedFunctionValueKindV1.Int64, result.Kind);
            Assert.Equal(47, unchecked((long)result.Payload));
        }

        Span<UnrealManagedFunctionValueV1> floatingArguments =
        [
            UnrealManagedFunctionValueV1.FromDouble(2.5),
            UnrealManagedFunctionValueV1.FromSingle(4.0f),
        ];
        fixed (UnrealManagedFunctionValueV1* pointer = floatingArguments)
        {
            UnrealManagedFunctionValueV1 result = instance.InvokeFunction(
                nameof(ManagedFunctionDispatchScript.Multiply),
                (nint)pointer,
                floatingArguments.Length);
            Assert.Equal(UnrealManagedFunctionValueKindV1.Double, result.Kind);
            Assert.Equal(10.0, BitConverter.UInt64BitsToDouble(result.Payload));
        }

        UnrealManagedFunctionValueV1 inverted = instance.InvokeFunction(
            nameof(ManagedFunctionDispatchScript.AlwaysTrue),
            arguments: 0,
            argumentCount: 0);
        Assert.Equal(UnrealManagedFunctionValueKindV1.Boolean, inverted.Kind);
        Assert.Equal(1UL, inverted.Payload);
    }

    [Fact]
    public unsafe void DispatchRejectsWrongAbiAndNonCallableVisualMethods()
    {
        var bridge = new NativeUnrealInvocationBridge(
            new FunctionTransport(),
            worldHostId: 72);
        UnrealObjectHandle handle = UnrealObjectHandle.Create(1, 1);
        using var instance = UnrealWorldManagedEntryPoint.ManagedScriptInstance.Create(
            worldId: 72,
            instanceId: 92,
            bridge,
            handle,
            handle,
            typeof(ManagedFunctionDispatchScript).Assembly.Location,
            typeof(ManagedFunctionDispatchScript).FullName!);
        instance.Start();

        Span<UnrealManagedFunctionValueV1> wrong =
        [
            UnrealManagedFunctionValueV1.FromInt32(1),
            UnrealManagedFunctionValueV1.FromInt32(2),
        ];
        fixed (UnrealManagedFunctionValueV1* pointer = wrong)
        {
            ArgumentException? error = null;
            try
            {
                _ = instance.InvokeFunction(
                    nameof(ManagedFunctionDispatchScript.Multiply),
                    (nint)pointer,
                    wrong.Length);
            }
            catch (ArgumentException exception)
            {
                error = exception;
            }
            Assert.NotNull(error);
        }
        Assert.Throws<MissingMethodException>(() => instance.InvokeFunction(
            nameof(ManagedFunctionDispatchScript.UnsupportedString),
            arguments: 0,
            argumentCount: 0));
    }

    [Fact]
    public void CallableOverloadsFailBeforeManagedLifecycleStarts()
    {
        var bridge = new NativeUnrealInvocationBridge(
            new FunctionTransport(),
            worldHostId: 73);
        UnrealObjectHandle handle = UnrealObjectHandle.Create(1, 1);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            UnrealWorldManagedEntryPoint.ManagedScriptInstance.Create(
                worldId: 73,
                instanceId: 93,
                bridge,
                handle,
                handle,
                typeof(OverloadedManagedFunctionScript).Assembly.Location,
                typeof(OverloadedManagedFunctionScript).FullName!));

        Assert.Contains("cannot overload", error.Message, StringComparison.Ordinal);
    }

    private sealed class FunctionTransport : IUnrealNativeInvocationTransport
    {
        public UnrealInvocationResponse ReadProperty(
            UnrealObjectHandle target,
            string propertyPath) => UnrealInvocationResponse.Failed(
                UnrealInvocationError.PropertyNotFound,
                propertyPath);

        public UnrealInvocationResponse WriteProperty(
            UnrealObjectHandle target,
            string propertyPath,
            UnrealInvocationValue value) =>
            UnrealInvocationResponse.Succeeded(UnrealInvocationValue.Void);

        public UnrealInvocationResponse Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<UnrealInvocationValue> arguments) =>
            UnrealInvocationResponse.Failed(
                UnrealInvocationError.FunctionNotFound,
                functionPath);
    }
}

public sealed class ManagedFunctionDispatchScript : IUnrealManagedScript
{
    [Visual]
    public long Mix(int value, bool doubleValue, byte increment, long bias) =>
        (doubleValue ? value * 2L : value) + increment + bias;

    [Visual]
    public double Multiply(double left, float right) => left * right;

    [Visual]
    public bool AlwaysTrue() => true;

    [Visual]
    public string UnsupportedString(string value) => value;

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

public sealed class OverloadedManagedFunctionScript : IUnrealManagedScript
{
    [Visual]
    public int Resolve(int value) => value;

    [Visual]
    public float Resolve(float value) => value;

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
