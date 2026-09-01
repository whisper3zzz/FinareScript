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

    [Fact]
    public unsafe void V2DispatchesStaticStringHandleVectorAndInheritedFunctions()
    {
        UnrealManagedFunctionDispatcherV2 library =
            UnrealManagedFunctionDispatcherV2.Create(
                typeof(ManagedFunctionLibrary),
                staticOnly: true);
        Span<UnrealNativeValueV3> arguments = stackalloc UnrealNativeValueV3[3];
        Span<UnrealNativeValueV3> results = stackalloc UnrealNativeValueV3[1];
        Span<byte> arena = stackalloc byte[1024];
        int inputBytes = 0;
        Assert.True(UnrealNativeValueCodecV3.TryEncode(
            UnrealObjectHandle.Create(3, 2),
            arena,
            ref inputBytes,
            out arguments[0],
            out _));
        Assert.True(UnrealNativeValueCodecV3.TryEncode(
            new UnrealVector(1.5, 2.5, 3.5),
            arena,
            ref inputBytes,
            out arguments[1],
            out _));
        Assert.True(UnrealNativeValueCodecV3.TryEncode(
            "bridge",
            arena,
            ref inputBytes,
            out arguments[2],
            out _));

        fixed (UnrealNativeValueV3* argumentPointer = arguments)
        fixed (UnrealNativeValueV3* resultPointer = results)
        fixed (byte* arenaPointer = arena)
        {
            var frame = new UnrealNativeCallFrameV3
            {
                Arguments = (nint)argumentPointer,
                ArgumentCount = (uint)arguments.Length,
                Results = (nint)resultPointer,
                ResultCapacity = (uint)results.Length,
                ByteBuffer = (nint)arenaPointer,
                ByteCapacity = (uint)arena.Length,
                ByteCount = (uint)inputBytes,
            };
            library.Invoke(
                target: null,
                nameof(ManagedFunctionLibrary.Describe),
                (nint)(&frame));

            Assert.Equal(UnrealNativeCallStatusV3.Success, frame.Status);
            Assert.Equal(1U, frame.ResultCount);
            Assert.True(UnrealNativeValueCodecV3.TryDecode(
                in results[0],
                arena,
                checked((int)frame.ByteCount),
                out string description));
            Assert.Equal("bridge:3:1.5,2.5,3.5", description);
        }

        var inheritedTarget = new InheritedManagedFunctionScript();
        UnrealManagedFunctionDispatcherV2 inherited =
            UnrealManagedFunctionDispatcherV2.Create(
                typeof(InheritedManagedFunctionScript),
                staticOnly: false);
        arguments = stackalloc UnrealNativeValueV3[1];
        results = stackalloc UnrealNativeValueV3[1];
        inputBytes = 0;
        Assert.True(UnrealNativeValueCodecV3.TryEncode(
            new UnrealVector(4.0, 5.0, 6.0),
            Span<byte>.Empty,
            ref inputBytes,
            out arguments[0],
            out _));
        fixed (UnrealNativeValueV3* argumentPointer = arguments)
        fixed (UnrealNativeValueV3* resultPointer = results)
        {
            var frame = new UnrealNativeCallFrameV3
            {
                Arguments = (nint)argumentPointer,
                ArgumentCount = 1,
                Results = (nint)resultPointer,
                ResultCapacity = 1,
            };
            inherited.Invoke(
                inheritedTarget,
                nameof(InheritedManagedFunctionBase.Offset),
                (nint)(&frame));
            Assert.Equal(UnrealNativeCallStatusV3.Success, frame.Status);
            Assert.True(UnrealNativeValueCodecV3.TryDecode(
                in results[0],
                ReadOnlySpan<byte>.Empty,
                0,
                out UnrealVector offset));
            Assert.Equal(new UnrealVector(5.0, 7.0, 9.0), offset);
        }
    }

    [Fact]
    public unsafe void V2DispatchesStrongEnumAndRecursiveStructValues()
    {
        UnrealManagedFunctionDispatcherV2 library =
            UnrealManagedFunctionDispatcherV2.Create(
                typeof(TypedManagedFunctionLibrary),
                staticOnly: true);
        var expected = new ManagedFunctionPayload(
            17,
            ManagedFunctionMode.Suspended,
            new UnrealVector(1.0, 2.0, 3.0));
        Span<UnrealNativeValueV3> arguments = stackalloc UnrealNativeValueV3[1];
        Span<UnrealNativeValueV3> results = stackalloc UnrealNativeValueV3[1];
        Span<byte> arena = stackalloc byte[2048];
        int inputBytes = 0;
        Assert.True(UnrealNativeValueCodecV3.TryEncode(
            expected,
            arena,
            ref inputBytes,
            out arguments[0],
            out UnrealNativeCapabilitiesV3 required));
        Assert.True(required.HasFlag(UnrealNativeCapabilitiesV3.Enums));
        Assert.True(required.HasFlag(UnrealNativeCapabilitiesV3.Structs));

        fixed (UnrealNativeValueV3* argumentPointer = arguments)
        fixed (UnrealNativeValueV3* resultPointer = results)
        fixed (byte* arenaPointer = arena)
        {
            var frame = new UnrealNativeCallFrameV3
            {
                Arguments = (nint)argumentPointer,
                ArgumentCount = 1,
                Results = (nint)resultPointer,
                ResultCapacity = 1,
                ByteBuffer = (nint)arenaPointer,
                ByteCapacity = (uint)arena.Length,
                ByteCount = (uint)inputBytes,
            };
            library.Invoke(null, nameof(TypedManagedFunctionLibrary.Echo), (nint)(&frame));

            Assert.Equal(UnrealNativeCallStatusV3.Success, frame.Status);
            Assert.True(UnrealNativeValueCodecV3.TryDecode(
                in results[0],
                arena,
                checked((int)frame.ByteCount),
                out ManagedFunctionPayload actual));
            Assert.Equal(expected, actual);
        }
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

public static class ManagedFunctionLibrary
{
    [Visual]
    public static string Describe(
        UnrealObjectHandle handle,
        UnrealVector vector,
        string label) =>
        $"{label}:{handle.SlotIndex}:{vector.X:0.0},{vector.Y:0.0},{vector.Z:0.0}";
}

[UnrealEnum("/Script/FixtureRuntime.EManagedFunctionMode", "EManagedFunctionMode")]
[UnrealValue]
public readonly record struct ManagedFunctionMode(long Value) : IUnrealEnumValue
{
    public static ManagedFunctionMode Suspended { get; } = new(2);
}

[UnrealStruct("/Script/FixtureRuntime.ManagedFunctionPayload", "FManagedFunctionPayload")]
[UnrealValue]
public readonly record struct ManagedFunctionPayload(
    [property: UnrealStructField(0, "Count")] int Count,
    [property: UnrealStructField(1, "Mode")] ManagedFunctionMode Mode,
    [property: UnrealStructField(2, "Location")] UnrealVector Location);

public static class TypedManagedFunctionLibrary
{
    [Visual]
    public static ManagedFunctionPayload Echo(ManagedFunctionPayload value) => value;
}

public abstract class InheritedManagedFunctionBase : IUnrealManagedScript
{
    [Visual]
    public UnrealVector Offset(UnrealVector value) =>
        new(value.X + 1.0, value.Y + 2.0, value.Z + 3.0);

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

public sealed class InheritedManagedFunctionScript : InheritedManagedFunctionBase
{
}
