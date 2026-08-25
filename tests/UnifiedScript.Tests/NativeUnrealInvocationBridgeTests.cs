using UnifiedScript.Unreal;
using GeneratedComponent = UnifiedScript.Unreal.Generated.UnifiedScriptComponent;
using GeneratedInterop = UnifiedScript.Unreal.Generated.UnifiedScriptInteropFixture;

namespace UnifiedScript.Tests;

public sealed class NativeUnrealInvocationBridgeTests
{
    private static readonly UnrealObjectHandle Handle =
        UnrealObjectHandle.Create(slotIndex: 2, generation: 7);

    [Fact]
    public void CheckedInGeneratedWrapperUsesManagedNativeAdapter()
    {
        var transport = new FixtureTransport();
        var wrapper = new GeneratedComponent(
            Handle,
            new NativeUnrealInvocationBridge(transport));

        wrapper.ScriptAssetPath = "/Game/Scripts/DamagePolicy";
        wrapper.bStartAutomatically = false;

        Assert.Equal("/Game/Scripts/DamagePolicy", wrapper.ScriptAssetPath);
        Assert.False(wrapper.bStartAutomatically);
        Assert.Equal("generation-7", wrapper.ActiveGeneration);
        Assert.Equal(42, wrapper.Add(19, 23));

        wrapper.RequestReload();

        Assert.True(transport.ReloadRequested);
        Assert.All(transport.Targets, target => Assert.Equal(Handle, target));
        Assert.Equal(
            [
                FixtureTransport.ScriptAssetPath,
                FixtureTransport.StartAutomaticallyPath,
                FixtureTransport.ScriptAssetPath,
                FixtureTransport.StartAutomaticallyPath,
                FixtureTransport.ActiveGenerationPath,
                FixtureTransport.AddPath,
                FixtureTransport.ReloadPath,
            ],
            transport.MemberPaths);
    }

    [Fact]
    public void GeneratedWrapperPrefersStableMemberIdsWhenAbiV2IsAvailable()
    {
        var transport = new FastFixtureTransport();
        var wrapper = new GeneratedComponent(
            Handle,
            new NativeUnrealInvocationBridge(transport));

        wrapper.ScriptAssetPath = "/Game/Scripts/Fast";
        Assert.Equal("/Game/Scripts/Fast", wrapper.ScriptAssetPath);
        Assert.Equal(42, wrapper.Add(20, 22));
        wrapper.RequestReload();

        Assert.Equal(
            [
                UnrealMemberId.FromPath(FixtureTransport.ScriptAssetPath),
                UnrealMemberId.FromPath(FixtureTransport.ScriptAssetPath),
                UnrealMemberId.FromPath(FixtureTransport.AddPath),
                UnrealMemberId.FromPath(FixtureTransport.ReloadPath),
            ],
            transport.MemberIds);
        Assert.Empty(transport.LegacyMemberPaths);
        Assert.True(transport.ReloadRequested);
    }

    [Fact]
    public void NativeFailurePreservesInvocationAndHandleCategories()
    {
        var transport = new FixtureTransport
        {
            NextFailure = UnrealInvocationResponse.Failed(
                UnrealInvocationError.ObjectHandleFailure,
                "The UObject generation is stale.",
                UnrealObjectHandleError.StaleHandle),
        };
        var wrapper = new GeneratedComponent(
            Handle,
            new NativeUnrealInvocationBridge(transport));

        UnrealInvocationException exception = Assert.Throws<UnrealInvocationException>(
            () => _ = wrapper.ScriptAssetPath);

        Assert.Equal(UnrealInvocationError.ObjectHandleFailure, exception.Error);
        Assert.Equal(UnrealObjectHandleError.StaleHandle, exception.HandleError);
        Assert.Contains("stale", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdapterRejectsUnsupportedManagedValuesBeforeTransport()
    {
        var transport = new FixtureTransport();
        IUnrealInvocationBridge bridge = new NativeUnrealInvocationBridge(transport);

        UnrealInvocationException exception = Assert.Throws<UnrealInvocationException>(() =>
            bridge.WriteProperty(Handle, FixtureTransport.ScriptAssetPath, 1.5m));

        Assert.Equal(UnrealInvocationError.UnsupportedValueType, exception.Error);
        Assert.Empty(transport.MemberPaths);
    }

    [Fact]
    public void AdapterRoundTripsCommonScalarObjectAndCoreStructValues()
    {
        IUnrealInvocationBridge bridge = new NativeUnrealInvocationBridge(new EchoTransport());

        Assert.Equal((sbyte)-8, Echo((sbyte)-8));
        Assert.Equal((byte)8, Echo((byte)8));
        Assert.Equal((short)-16, Echo((short)-16));
        Assert.Equal((ushort)16, Echo((ushort)16));
        Assert.Equal(32U, Echo(32U));
        Assert.Equal(-64L, Echo(-64L));
        Assert.Equal(64UL, Echo(64UL));
        Assert.Equal(1.25f, Echo(1.25f));
        Assert.Equal(2.5, Echo(2.5));
        Assert.Equal(new UnrealName("Player"), Echo(new UnrealName("Player")));
        Assert.Equal(new UnrealText("Ready"), Echo(new UnrealText("Ready")));
        Assert.Equal(Handle, Echo(Handle));
        Assert.Equal(new UnrealClassHandle(Handle.Value), Echo(new UnrealClassHandle(Handle.Value)));
        Assert.Equal(
            new UnrealEnumValue("/Script/FixtureRuntime.EFixtureMode", 2),
            Echo(new UnrealEnumValue("/Script/FixtureRuntime.EFixtureMode", 2)));
        Assert.Equal(new UnrealVector(1.0, 2.0, 3.0), Echo(new UnrealVector(1.0, 2.0, 3.0)));
        Assert.Equal(new UnrealRotator(10.0, 20.0, 30.0), Echo(new UnrealRotator(10.0, 20.0, 30.0)));
        Assert.Equal(UnrealTransform.Identity, Echo(UnrealTransform.Identity));

        T Echo<T>(T value) => bridge.Invoke<T>(
            Handle,
            "/Script/FixtureRuntime.FixtureObject:Echo",
            [value]);
    }

    [Fact]
    public void GeneratedFixedCallFramePathHasZeroSteadyStateManagedAllocations()
    {
        var transport = new CallFrameFixtureTransport();
        var bridge = new NativeUnrealInvocationBridge(transport);
        var component = new GeneratedComponent(Handle, bridge);
        var interop = new GeneratedInterop(Handle, bridge);

        for (int index = 0; index < 256; index++)
        {
            _ = component.Add(index, 1);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int index = 0; index < 10_000; index++)
        {
            checksum += component.Add(index, 1);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(50_005_000, checksum);
        Assert.Equal(0, allocated);
        Assert.Equal(10_256, bridge.CallFrameInvocationCount);
        Assert.Equal(0, bridge.LegacyInvocationCount);
        Assert.Equal(0, transport.LegacyCallCount);

        UnrealVector vector = interop.AddVector(
            new UnrealVector(1.0, 2.0, 3.0),
            new UnrealVector(10.0, 20.0, 30.0));
        Assert.Equal(new UnrealVector(11.0, 22.0, 33.0), vector);
        Assert.Equal(UnrealTransform.Identity, interop.EchoTransform(UnrealTransform.Identity));
        Assert.Equal(Handle, interop.EchoObject(Handle));
        var classHandle = new UnrealClassHandle(Handle.Value);
        Assert.Equal(classHandle, interop.EchoClass(classHandle));
        Assert.Equal(5, interop.StringLength("可视化脚本"));
        Assert.Equal(6, interop.SumArray([1, 2, 3]));
        Assert.Equal(3, interop.CountSet(new HashSet<int> { 1, 2, 3 }));
        Assert.Equal(10, interop.SumMap(new Dictionary<int, int> { [1] = 2, [3] = 4 }));
        Assert.Equal([1, 2, 3], interop.EchoArray([1, 2, 3]));
        Assert.True(interop.EchoSet(new HashSet<int> { 1, 2, 3 }).SetEquals([1, 2, 3]));
        Assert.Equal(
            new Dictionary<int, int> { [1] = 2, [3] = 4 },
            interop.EchoMap(new Dictionary<int, int> { [1] = 2, [3] = 4 }));
        Assert.Equal(Enumerable.Range(0, 100), interop.MakeArray(100));
        Assert.Equal(1, transport.MakeArrayDispatchCount);
        int accumulator = 10;
        Assert.True(interop.MutateValues(7, out int doubled, ref accumulator));
        Assert.Equal(14, doubled);
        Assert.Equal(17, accumulator);
        Assert.Equal(0, transport.LegacyCallCount);
    }

    [Fact]
    public void ManagedErrorCodesMatchNativeContract()
    {
        Assert.Equal(3, (byte)UnrealInvocationValueKind.String);
        Assert.Equal(20, (byte)UnrealInvocationValueKind.Transform);
        Assert.Equal(6, (byte)UnrealObjectHandleError.StaleHandle);
        Assert.Equal(1, (byte)UnrealInvocationError.ObjectHandleFailure);
        Assert.Equal(11, (byte)UnrealInvocationError.ValueTypeMismatch);
        Assert.Equal(13, (byte)UnrealInvocationError.UnsupportedParameter);
        Assert.Equal(14, (byte)UnrealInvocationError.TransportUnavailable);
    }

    private sealed class EchoTransport : IUnrealNativeInvocationTransport
    {
        public UnrealInvocationResponse ReadProperty(
            UnrealObjectHandle target,
            string propertyPath) =>
            throw new NotSupportedException();

        public UnrealInvocationResponse WriteProperty(
            UnrealObjectHandle target,
            string propertyPath,
            UnrealInvocationValue value) =>
            throw new NotSupportedException();

        public UnrealInvocationResponse Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<UnrealInvocationValue> arguments) =>
            arguments.Count == 1
                ? UnrealInvocationResponse.Succeeded(arguments[0])
                : UnrealInvocationResponse.Failed(
                    UnrealInvocationError.ArgumentCountMismatch,
                    "Echo requires one argument.");
    }

    private sealed class CallFrameFixtureTransport
        : IUnrealNativeInvocationTransport, IUnrealNativeCallFrameTransportV3
    {
        private static readonly ulong AddId = UnrealMemberId.FromPath(FixtureTransport.AddPath);
        private static readonly ulong AddVectorId = UnrealMemberId.FromPath(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:AddVector");
        private static readonly ulong EchoTransformId = UnrealMemberId.FromPath(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:EchoTransform");
        private static readonly ulong EchoObjectId = UnrealMemberId.FromPath(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:EchoObject");
        private static readonly ulong EchoClassId = UnrealMemberId.FromPath(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:EchoClass");
        private static readonly ulong StringLengthId = UnrealMemberId.FromPath(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:StringLength");
        private static readonly ulong SumArrayId = UnrealMemberId.FromPath(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:SumArray");
        private static readonly ulong CountSetId = UnrealMemberId.FromPath(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:CountSet");
        private static readonly ulong SumMapId = UnrealMemberId.FromPath(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:SumMap");
        private static readonly ulong MutateValuesId = UnrealMemberId.FromPath(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:MutateValues");
        private static readonly ulong EchoArrayId = UnrealMemberId.FromPath(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:EchoArray");
        private static readonly ulong EchoSetId = UnrealMemberId.FromPath(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:EchoSet");
        private static readonly ulong EchoMapId = UnrealMemberId.FromPath(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:EchoMap");
        private static readonly ulong MakeArrayId = UnrealMemberId.FromPath(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:MakeArray");

        private bool _makeArrayPending;

        public UnrealNativeCapabilitiesV3 Capabilities =>
            UnrealNativeCapabilitiesV3.FixedScalars |
            UnrealNativeCapabilitiesV3.ObjectHandles |
            UnrealNativeCapabilitiesV3.VectorAndRotator |
            UnrealNativeCapabilitiesV3.Transform |
            UnrealNativeCapabilitiesV3.Utf8Text |
            UnrealNativeCapabilitiesV3.Containers |
            UnrealNativeCapabilitiesV3.OutputAndReference |
            UnrealNativeCapabilitiesV3.Utf8Results |
            UnrealNativeCapabilitiesV3.ContainerResults;

        public int LegacyCallCount { get; private set; }

        public int MakeArrayDispatchCount { get; private set; }

        public UnrealNativeCallOutcomeV3 ExecuteById(
            UnrealNativeOperationV3 operation,
            UnrealObjectHandle target,
            ulong memberId,
            ReadOnlySpan<UnrealNativeValueV3> arguments,
            Span<UnrealNativeValueV3> results,
            Span<byte> byteBuffer,
            int inputByteCount)
        {
            if (operation != UnrealNativeOperationV3.Invoke)
            {
                return Invalid();
            }
            if (memberId == MutateValuesId &&
                arguments.Length == 2 &&
                results.Length == 3 &&
                arguments[0].Kind == UnrealNativeValueKindV3.Int32 &&
                arguments[1].Kind == UnrealNativeValueKindV3.Int32 &&
                results[0].Kind == UnrealNativeValueKindV3.Boolean &&
                results[1].Kind == UnrealNativeValueKindV3.Int32 &&
                results[2].Kind == UnrealNativeValueKindV3.Int32)
            {
                int input = unchecked((int)arguments[0].Payload0);
                int accumulator = unchecked((int)arguments[1].Payload0);
                results[0] = new UnrealNativeValueV3
                {
                    Kind = UnrealNativeValueKindV3.Boolean,
                    Payload0 = 1,
                };
                results[1] = new UnrealNativeValueV3
                {
                    Kind = UnrealNativeValueKindV3.Int32,
                    Payload0 = unchecked((ulong)(input * 2)),
                };
                results[2] = new UnrealNativeValueV3
                {
                    Kind = UnrealNativeValueKindV3.Int32,
                    Payload0 = unchecked((ulong)(accumulator + input)),
                };
                return UnrealNativeCallOutcomeV3.Succeeded(3, inputByteCount);
            }
            if (results.Length != 1)
            {
                return Invalid();
            }
            if (memberId == AddId &&
                arguments.Length == 2 &&
                arguments[0].Kind == UnrealNativeValueKindV3.Int32 &&
                arguments[1].Kind == UnrealNativeValueKindV3.Int32)
            {
                results[0] = new UnrealNativeValueV3
                {
                    Kind = UnrealNativeValueKindV3.Int32,
                    Payload0 = unchecked((uint)(
                        unchecked((int)arguments[0].Payload0) +
                        unchecked((int)arguments[1].Payload0))),
                };
                return UnrealNativeCallOutcomeV3.Succeeded(1, inputByteCount);
            }
            if (memberId == AddVectorId &&
                arguments.Length == 2 &&
                arguments[0].Kind == UnrealNativeValueKindV3.Vector3 &&
                arguments[1].Kind == UnrealNativeValueKindV3.Vector3)
            {
                results[0] = new UnrealNativeValueV3
                {
                    Kind = UnrealNativeValueKindV3.Vector3,
                    Payload0 = AddDoubleBits(arguments[0].Payload0, arguments[1].Payload0),
                    Payload1 = AddDoubleBits(arguments[0].Payload1, arguments[1].Payload1),
                    Payload2 = AddDoubleBits(arguments[0].Payload2, arguments[1].Payload2),
                };
                return UnrealNativeCallOutcomeV3.Succeeded(1, inputByteCount);
            }
            if (memberId == EchoTransformId &&
                arguments.Length == 1 &&
                arguments[0].Kind == UnrealNativeValueKindV3.Transform)
            {
                results[0] = arguments[0];
                return UnrealNativeCallOutcomeV3.Succeeded(1, inputByteCount);
            }
            if ((memberId == EchoObjectId || memberId == EchoClassId) &&
                arguments.Length == 1 &&
                arguments[0].Kind == results[0].Kind)
            {
                results[0] = arguments[0];
                return UnrealNativeCallOutcomeV3.Succeeded(1, inputByteCount);
            }
            if (memberId == StringLengthId &&
                arguments.Length == 1 &&
                arguments[0].Kind == UnrealNativeValueKindV3.String &&
                arguments[0].Flags == UnrealNativeValueFlagsV3.ArenaBacked &&
                arguments[0].Payload0 <= (ulong)inputByteCount &&
                arguments[0].Payload1 <= (ulong)inputByteCount - arguments[0].Payload0)
            {
                int offset = checked((int)arguments[0].Payload0);
                int length = checked((int)arguments[0].Payload1);
                results[0] = new UnrealNativeValueV3
                {
                    Kind = UnrealNativeValueKindV3.Int32,
                    Payload0 = (ulong)System.Text.Encoding.UTF8.GetString(
                        byteBuffer.Slice(offset, length)).Length,
                };
                return UnrealNativeCallOutcomeV3.Succeeded(1, inputByteCount);
            }
            if ((memberId == EchoArrayId || memberId == EchoSetId || memberId == EchoMapId) &&
                arguments.Length == 1 &&
                results.Length == 1 &&
                arguments[0].Kind == results[0].Kind &&
                arguments[0].TypeId == results[0].TypeId)
            {
                results[0] = arguments[0];
                return UnrealNativeCallOutcomeV3.Succeeded(1, inputByteCount);
            }
            if (memberId == MakeArrayId &&
                arguments.Length == 1 &&
                results.Length == 1 &&
                arguments[0].Kind == UnrealNativeValueKindV3.Int32 &&
                results[0].Kind == UnrealNativeValueKindV3.Array &&
                results[0].TypeId == UnrealTypeIdV3.FromPath("TArray<int32>"))
            {
                int count = Math.Clamp(unchecked((int)arguments[0].Payload0), 0, 4096);
                int requiredBytes = checked(count * UnrealNativeAbiV3.ValueSize);
                if (!_makeArrayPending)
                {
                    MakeArrayDispatchCount++;
                }
                if (byteBuffer.Length < requiredBytes)
                {
                    _makeArrayPending = true;
                    return new UnrealNativeCallOutcomeV3(
                        UnrealNativeCallStatusV3.BufferTooSmall,
                        UnrealInvocationError.None,
                        UnrealObjectHandleError.None,
                        0,
                        inputByteCount,
                        1,
                        requiredBytes,
                        string.Empty);
                }
                _makeArrayPending = false;
                for (int index = 0; index < count; index++)
                {
                    var cell = new UnrealNativeValueV3
                    {
                        Kind = UnrealNativeValueKindV3.Int32,
                        Payload0 = unchecked((ulong)index),
                    };
                    System.Runtime.InteropServices.MemoryMarshal.Write(
                        byteBuffer.Slice(
                            index * UnrealNativeAbiV3.ValueSize,
                            UnrealNativeAbiV3.ValueSize),
                        in cell);
                }
                results[0] = new UnrealNativeValueV3
                {
                    Kind = UnrealNativeValueKindV3.Array,
                    Flags = UnrealNativeValueFlagsV3.ArenaBacked,
                    TypeId = UnrealTypeIdV3.FromPath("TArray<int32>"),
                    Payload1 = (ulong)requiredBytes,
                    Payload2 = (ulong)count,
                    Payload3 = UnrealNativeAbiV3.ValueSize,
                };
                return UnrealNativeCallOutcomeV3.Succeeded(1, requiredBytes);
            }
            int arraySum = 0;
            int setCount = 0;
            int mapSum = 0;
            if (arguments.Length == 1 &&
                ((memberId == SumArrayId && TryReadInt32Container(
                    arguments[0],
                    UnrealNativeValueKindV3.Array,
                    "TArray<int32>",
                    byteBuffer,
                    inputByteCount,
                    out arraySum,
                    out _)) ||
                 (memberId == CountSetId && TryReadInt32Container(
                    arguments[0],
                    UnrealNativeValueKindV3.Set,
                    "TSet<int32>",
                    byteBuffer,
                    inputByteCount,
                    out _,
                    out setCount)) ||
                 (memberId == SumMapId && TryReadInt32Container(
                    arguments[0],
                    UnrealNativeValueKindV3.Map,
                    "TMap<int32,int32>",
                    byteBuffer,
                    inputByteCount,
                    out mapSum,
                    out _))))
            {
                int value = memberId == SumArrayId ? arraySum :
                    memberId == CountSetId ? setCount : mapSum;
                results[0] = new UnrealNativeValueV3
                {
                    Kind = UnrealNativeValueKindV3.Int32,
                    Payload0 = unchecked((ulong)value),
                };
                return UnrealNativeCallOutcomeV3.Succeeded(1, inputByteCount);
            }
            return Invalid();
        }

        private static bool TryReadInt32Container(
            in UnrealNativeValueV3 container,
            UnrealNativeValueKindV3 expectedKind,
            string nativeName,
            ReadOnlySpan<byte> arena,
            int byteCount,
            out int sum,
            out int count)
        {
            sum = 0;
            count = 0;
            int cellsPerEntry = expectedKind == UnrealNativeValueKindV3.Map ? 2 : 1;
            if (container.Kind != expectedKind ||
                container.Flags != UnrealNativeValueFlagsV3.ArenaBacked ||
                container.TypeId != UnrealTypeIdV3.FromPath(nativeName) ||
                container.Payload2 > int.MaxValue ||
                container.Payload3 != UnrealNativeAbiV3.ValueSize)
            {
                return false;
            }
            int entryCount = (int)container.Payload2;
            int cellCount = checked(entryCount * cellsPerEntry);
            int descriptorBytes = checked(cellCount * UnrealNativeAbiV3.ValueSize);
            if (container.Payload0 > (ulong)byteCount ||
                container.Payload1 != (ulong)descriptorBytes ||
                container.Payload1 > (ulong)byteCount - container.Payload0)
            {
                return false;
            }
            int offset = checked((int)container.Payload0);
            for (int index = 0; index < cellCount; index++)
            {
                UnrealNativeValueV3 cell = System.Runtime.InteropServices.MemoryMarshal.Read<
                    UnrealNativeValueV3>(arena.Slice(
                        offset + index * UnrealNativeAbiV3.ValueSize,
                        UnrealNativeAbiV3.ValueSize));
                if (cell.Kind != UnrealNativeValueKindV3.Int32)
                {
                    return false;
                }
                sum += unchecked((int)cell.Payload0);
            }
            count = entryCount;
            return true;
        }

        public UnrealInvocationResponse ReadProperty(
            UnrealObjectHandle target,
            string propertyPath)
        {
            LegacyCallCount++;
            throw new InvalidOperationException("The fixed generated call should use ABI v3.");
        }

        public UnrealInvocationResponse WriteProperty(
            UnrealObjectHandle target,
            string propertyPath,
            UnrealInvocationValue value)
        {
            LegacyCallCount++;
            throw new InvalidOperationException("The fixed generated call should use ABI v3.");
        }

        public UnrealInvocationResponse Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<UnrealInvocationValue> arguments)
        {
            LegacyCallCount++;
            throw new InvalidOperationException("The fixed generated call should use ABI v3.");
        }

        private static ulong AddDoubleBits(ulong left, ulong right) =>
            unchecked((ulong)BitConverter.DoubleToInt64Bits(
                BitConverter.Int64BitsToDouble(unchecked((long)left)) +
                BitConverter.Int64BitsToDouble(unchecked((long)right))));

        private static UnrealNativeCallOutcomeV3 Invalid() =>
            new(
                UnrealNativeCallStatusV3.InvalidFrame,
                UnrealInvocationError.ValueTypeMismatch,
                UnrealObjectHandleError.None,
                0,
                0,
                0,
                0,
                "Unexpected test call frame.");
    }

    private sealed class FixtureTransport : IUnrealNativeInvocationTransport
    {
        private const string TypePath =
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent";

        public const string ActiveGenerationPath = TypePath + ":ActiveGeneration";
        public const string ScriptAssetPath = TypePath + ":ScriptAssetPath";
        public const string StartAutomaticallyPath = TypePath + ":bStartAutomatically";
        public const string AddPath = TypePath + ":Add";
        public const string ReloadPath = TypePath + ":RequestReload";

        private string _scriptAssetPath = string.Empty;
        private bool _startAutomatically = true;

        public List<UnrealObjectHandle> Targets { get; } = [];

        public List<string> MemberPaths { get; } = [];

        public bool ReloadRequested { get; private set; }

        public UnrealInvocationResponse? NextFailure { get; init; }

        public UnrealInvocationResponse ReadProperty(
            UnrealObjectHandle target,
            string propertyPath)
        {
            if (TryBegin(target, propertyPath, out UnrealInvocationResponse failure))
            {
                return failure;
            }
            return propertyPath switch
            {
                ActiveGenerationPath => UnrealInvocationResponse.Succeeded(
                    UnrealInvocationValue.FromString("generation-7")),
                ScriptAssetPath => UnrealInvocationResponse.Succeeded(
                    UnrealInvocationValue.FromString(_scriptAssetPath)),
                StartAutomaticallyPath => UnrealInvocationResponse.Succeeded(
                    UnrealInvocationValue.FromBoolean(_startAutomatically)),
                _ => UnrealInvocationResponse.Failed(
                    UnrealInvocationError.PropertyNotFound,
                    $"Property '{propertyPath}' was not found."),
            };
        }

        public UnrealInvocationResponse WriteProperty(
            UnrealObjectHandle target,
            string propertyPath,
            UnrealInvocationValue value)
        {
            if (TryBegin(target, propertyPath, out UnrealInvocationResponse failure))
            {
                return failure;
            }
            switch (propertyPath)
            {
                case ScriptAssetPath when value.Kind == UnrealInvocationValueKind.String:
                    _scriptAssetPath = value.StringValue!;
                    return UnrealInvocationResponse.Succeeded(UnrealInvocationValue.Void);
                case StartAutomaticallyPath when value.Kind == UnrealInvocationValueKind.Boolean:
                    _startAutomatically = value.BooleanValue;
                    return UnrealInvocationResponse.Succeeded(UnrealInvocationValue.Void);
                default:
                    return UnrealInvocationResponse.Failed(
                        UnrealInvocationError.ValueTypeMismatch,
                        $"Property '{propertyPath}' rejected {value.Kind}.");
            }
        }

        public UnrealInvocationResponse Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<UnrealInvocationValue> arguments)
        {
            if (TryBegin(target, functionPath, out UnrealInvocationResponse failure))
            {
                return failure;
            }
            if (functionPath == AddPath &&
                arguments.Count == 2 &&
                arguments.All(value => value.Kind == UnrealInvocationValueKind.Int32))
            {
                return UnrealInvocationResponse.Succeeded(
                    UnrealInvocationValue.FromInt32(
                        arguments[0].Int32Value + arguments[1].Int32Value));
            }
            if (functionPath == ReloadPath && arguments.Count == 0)
            {
                ReloadRequested = true;
                return UnrealInvocationResponse.Succeeded(UnrealInvocationValue.Void);
            }
            return UnrealInvocationResponse.Failed(
                UnrealInvocationError.ArgumentCountMismatch,
                $"Function '{functionPath}' rejected its arguments.");
        }

        private bool TryBegin(
            UnrealObjectHandle target,
            string memberPath,
            out UnrealInvocationResponse failure)
        {
            Targets.Add(target);
            MemberPaths.Add(memberPath);
            if (NextFailure is UnrealInvocationResponse nextFailure)
            {
                failure = nextFailure;
                return true;
            }
            failure = default;
            return false;
        }
    }

    private sealed class FastFixtureTransport
        : IUnrealNativeInvocationTransport, IUnrealNativeFastInvocationTransport
    {
        private string _scriptAssetPath = string.Empty;

        public bool SupportsFastInvocation => true;

        public List<ulong> MemberIds { get; } = [];

        public List<string> LegacyMemberPaths { get; } = [];

        public bool ReloadRequested { get; private set; }

        public UnrealInvocationResponse ReadProperty(
            UnrealObjectHandle target,
            string propertyPath)
        {
            LegacyMemberPaths.Add(propertyPath);
            throw new InvalidOperationException("The generated wrapper should select ABI v2.");
        }

        public UnrealInvocationResponse WriteProperty(
            UnrealObjectHandle target,
            string propertyPath,
            UnrealInvocationValue value)
        {
            LegacyMemberPaths.Add(propertyPath);
            throw new InvalidOperationException("The generated wrapper should select ABI v2.");
        }

        public UnrealInvocationResponse Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<UnrealInvocationValue> arguments)
        {
            LegacyMemberPaths.Add(functionPath);
            throw new InvalidOperationException("The generated wrapper should select ABI v2.");
        }

        public UnrealInvocationResponse ReadPropertyById(
            UnrealObjectHandle target,
            ulong memberId)
        {
            MemberIds.Add(memberId);
            return memberId == UnrealMemberId.FromPath(FixtureTransport.ScriptAssetPath)
                ? UnrealInvocationResponse.Succeeded(
                    UnrealInvocationValue.FromString(_scriptAssetPath))
                : UnrealInvocationResponse.Failed(
                    UnrealInvocationError.InvalidMemberPath,
                    "Fast member ID was not registered.");
        }

        public UnrealInvocationResponse WritePropertyById(
            UnrealObjectHandle target,
            ulong memberId,
            UnrealInvocationValue value)
        {
            MemberIds.Add(memberId);
            if (memberId == UnrealMemberId.FromPath(FixtureTransport.ScriptAssetPath) &&
                value.Kind == UnrealInvocationValueKind.String)
            {
                _scriptAssetPath = value.StringValue!;
                return UnrealInvocationResponse.Succeeded(UnrealInvocationValue.Void);
            }
            return UnrealInvocationResponse.Failed(
                UnrealInvocationError.ValueTypeMismatch,
                "Fast property value did not match.");
        }

        public UnrealInvocationResponse InvokeById(
            UnrealObjectHandle target,
            ulong memberId,
            IReadOnlyList<UnrealInvocationValue> arguments)
        {
            MemberIds.Add(memberId);
            if (memberId == UnrealMemberId.FromPath(FixtureTransport.AddPath) &&
                arguments.Count == 2)
            {
                return UnrealInvocationResponse.Succeeded(
                    UnrealInvocationValue.FromInt32(
                        arguments[0].Int32Value + arguments[1].Int32Value));
            }
            if (memberId == UnrealMemberId.FromPath(FixtureTransport.ReloadPath) &&
                arguments.Count == 0)
            {
                ReloadRequested = true;
                return UnrealInvocationResponse.Succeeded(UnrealInvocationValue.Void);
            }
            return UnrealInvocationResponse.Failed(
                UnrealInvocationError.ArgumentCountMismatch,
                "Fast function arguments did not match.");
        }
    }
}
