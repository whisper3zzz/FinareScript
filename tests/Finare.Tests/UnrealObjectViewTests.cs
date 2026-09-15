using System.Reflection;
using Finare.Unreal;

namespace Finare.Tests;

public sealed class UnrealObjectViewTests
{
    private static readonly UnrealObjectHandle Handle =
        UnrealObjectHandle.Create(slotIndex: 7, generation: 3);

    [Fact]
    public void ReloadContractsRetainContainerElementObjectTypes()
    {
        Assert.Equal(UnrealObjectInvocationAdapter.WireType(typeof(IReadOnlyList<BaseView>)),
            UnrealObjectInvocationAdapter.WireType(typeof(IReadOnlyList<DerivedView>)));
        Assert.True(UnrealObjectInvocationAdapter.TryCreateContractIdentity(typeof(IReadOnlyList<BaseView>), out string baseList));
        Assert.True(UnrealObjectInvocationAdapter.TryCreateContractIdentity(typeof(IReadOnlyList<DerivedView>), out string derivedList));
        Assert.NotEqual(baseList, derivedList);
        Assert.True(UnrealObjectInvocationAdapter.TryCreateContractIdentity(typeof(IReadOnlyDictionary<int, BaseView>), out string baseMap));
        Assert.True(UnrealObjectInvocationAdapter.TryCreateContractIdentity(typeof(IReadOnlyDictionary<int, DerivedView>), out string derivedMap));
        Assert.NotEqual(baseMap, derivedMap);
        Assert.True(UnrealObjectInvocationAdapter.TryCreateContractIdentity(typeof(BaseReferenceStruct), out string baseStruct));
        Assert.True(UnrealObjectInvocationAdapter.TryCreateContractIdentity(typeof(DerivedReferenceStruct), out string derivedStruct));
        Assert.NotEqual(baseStruct, derivedStruct);
    }

    [UnrealWireType(typeof(ReferenceWireStruct))]
    private readonly record struct BaseReferenceStruct([property: UnrealStructField(0)] BaseView? Target);

    [UnrealWireType(typeof(ReferenceWireStruct))]
    private readonly record struct DerivedReferenceStruct([property: UnrealStructField(0)] DerivedView? Target);

    [UnrealStruct("/Script/Fixture.ReferenceStruct", "FReferenceStruct")]
    private readonly record struct ReferenceWireStruct([property: UnrealStructField(0)] UnrealObjectHandle Target);

    [Fact]
    public void WrapValidatesNativeTypeAndSupportsDerivedViews()
    {
        var bridge = new TestBridge([typeof(BaseView).GetCustomAttribute<UnrealTypeAttribute>()!.Path,
            typeof(DerivedView).GetCustomAttribute<UnrealTypeAttribute>()!.Path]);

        DerivedView view = UnrealObject.Wrap<DerivedView>(Handle, bridge)!;

        Assert.True(view.IsA<DerivedView>());
        Assert.True(view.IsA<BaseView>());
        Assert.Same(view, view.As<DerivedView>());
        Assert.Same(view, view.As<BaseView>());
    }

    [Fact]
    public void WrapRejectsWrongNativeTypeAndNullIsNullable()
    {
        var bridge = new TestBridge(["/Script/Fixture.Base"]);

        Assert.Null(UnrealObject.Wrap<DerivedView>(default, bridge));
        Assert.Throws<InvalidCastException>(() => UnrealObject.Wrap<DerivedView>(Handle, bridge));
    }

    [Fact]
    public void EqualityUsesStableScopeAndHandleAfterGenerationIsDisposed()
    {
        var scope = new object();
        var firstBridge = new TestBridge(["/Script/Fixture.Base"], scope);
        var secondBridge = new TestBridge(["/Script/Fixture.Base"], scope);
        BaseView first = UnrealObject.Wrap<BaseView>(Handle, firstBridge)!;
        BaseView second = UnrealObject.Wrap<BaseView>(Handle, secondBridge)!;

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        firstBridge.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = first.Handle);
        Assert.Equal(first, second);
        Assert.Throws<ObjectDisposedException>(() => UnrealObject.AreCompatible(first, secondBridge));
    }

    [Fact]
    public void DifferentBridgeScopesCannotExchangeObjectViews()
    {
        var sourceBridge = new TestBridge(["/Script/Fixture.Base"]);
        var destinationBridge = new TestBridge(["/Script/Fixture.Base"]);
        BaseView source = UnrealObject.Wrap<BaseView>(Handle, sourceBridge)!;

        Assert.False(UnrealObject.AreCompatible(source, destinationBridge));
    }

    [Fact]
    public void ViewDiscoveryDoesNotRootAnUnloadedGeneratedAssembly()
    {
        WeakReference weak = PopulateCollectibleViewCache();
        for (int i = 0; i < 15 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference PopulateCollectibleViewCache()
    {
        const string source = """
            using Finare.Unreal;
            [UnrealType("/Script/Fixture.Base")]
            public class Probe : UnrealObject {
                public Probe(UnrealObjectHandle h, IUnrealInvocationBridge b) : base(h,b) {}
            }
            """;
        var compilation = new Finare.CSharp.CSharpCompilationHost().Create(
            Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source),
            [Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(UnrealObject).Assembly.Location)]);
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        stream.Position = 0;
        var context = new System.Runtime.Loader.AssemblyLoadContext("ObjectViewProbe", isCollectible: true);
        Type type = context.LoadFromStream(stream).GetType("Probe")!;
        var bridge = new TestBridge(["/Script/Fixture.Base"]);
        Assert.NotNull(typeof(UnrealObject).GetMethod("Wrap")!.MakeGenericMethod(type).Invoke(null, [Handle, bridge]));
        var weak = new WeakReference(context);
        context.Unload();
        return weak;
    }

    [Fact]
    public void BaseResultSelectsActualDerivedTypeAndIdentityOperatorsAgree()
    {
        var bridge = new TestBridge(["/Script/Fixture.Derived", "/Script/Fixture.Base"]);
        BaseView first = UnrealObject.Wrap<BaseView>(Handle, bridge)!;
        BaseView second = UnrealObject.Wrap<BaseView>(Handle, bridge)!;
        Assert.IsType<DerivedView>(first);
        Assert.True(first == second);
        Assert.False(first != second);
        bridge.Dispose();
        Assert.True(first == second);
        Assert.Throws<ObjectDisposedException>(() => first.IsA<BaseView>());
    }

    [Fact]
    public void ObjectContainersAndNestedStructsRoundTripWithoutLosingTypeOrNull()
    {
        var bridge = new TestBridge(["/Script/Fixture.Derived", "/Script/Fixture.Base"]);
        BaseView view = UnrealObject.Wrap<BaseView>(Handle, bridge)!;
        var snapshot = new ViewStruct(view, new Dictionary<int, IReadOnlyList<BaseView?>> { [1] = new BaseView?[] { view, null } });
        object wire = UnrealObjectInvocationAdapter.Convert(snapshot, typeof(ViewStruct), bridge, false)!;
        Assert.IsType<WireStruct>(wire);
        var restored = (ViewStruct)UnrealObjectInvocationAdapter.Convert(wire, typeof(ViewStruct), bridge, true)!;
        Assert.Equal(view, restored.Target);
        Assert.IsType<DerivedView>(restored.Target);
        Assert.Equal(new BaseView?[] { view, null }, restored.Groups[1]);

        var set = new HashSet<BaseView?> { view, null };
        object wireSet = UnrealObjectInvocationAdapter.Convert(set, typeof(IReadOnlySet<BaseView?>), bridge, false)!;
        Assert.IsType<HashSet<UnrealObjectHandle>>(wireSet);
        var restoredSet = (IReadOnlySet<BaseView?>)UnrealObjectInvocationAdapter.Convert(wireSet, typeof(IReadOnlySet<BaseView?>), bridge, true)!;
        Assert.True(restoredSet.SetEquals(set));
        Assert.Throws<InvalidOperationException>(() => UnrealObjectInvocationAdapter.Convert(snapshot, typeof(ViewStruct),
            new TestBridge(["/Script/Fixture.Base"]), false));
        bridge.Dispose();
        Assert.Throws<ObjectDisposedException>(() => UnrealObjectInvocationAdapter.Convert(snapshot, typeof(ViewStruct), bridge, false));
    }

    [Fact]
    public void ForwardOutputsAreTypedAndForeignArgumentsFailBeforeInvocation()
    {
        var bridge = new TestBridge(["/Script/Fixture.Derived", "/Script/Fixture.Base"]);
        BaseView owner = UnrealObject.Wrap<BaseView>(Handle, bridge)!;
        bridge.Response = new(Handle, new object?[] { Handle });
        var result = UnrealObjectInvocationAdapter.InvokeWithOutputs(owner, 1, "Echo", typeof(BaseView),
            [new(typeof(BaseView), UnrealParameterDirection.InputOutput, owner)]);
        Assert.Equal(typeof(UnrealObjectHandle), bridge.LastReturnType);
        Assert.Equal(typeof(UnrealObjectHandle), bridge.LastArguments![0].Type);
        Assert.Equal(Handle, bridge.LastArguments[0].Value);
        Assert.IsType<DerivedView>(result.ReturnValue);
        Assert.Equal(owner, result.Outputs[0]);
        var foreign = UnrealObject.Wrap<BaseView>(Handle, new TestBridge(["/Script/Fixture.Base"]))!;
        Assert.Throws<InvalidOperationException>(() => UnrealObjectInvocationAdapter.InvokeWithOutputs(owner, 1, "Echo", typeof(BaseView),
            [new(typeof(BaseView), UnrealParameterDirection.Input, foreign)]));
        Assert.Equal(1, bridge.Calls);
        bridge.Response = new(Handle, Array.Empty<object?>());
        Assert.Throws<InvalidOperationException>(() => UnrealObjectInvocationAdapter.InvokeWithOutputs(owner, 1, "Echo", typeof(BaseView),
            [new(typeof(BaseView), UnrealParameterDirection.Output, null)]));
    }

    private delegate BaseView? ObjectCallback(BaseView? input, out BaseView? output, ref BaseView? reference);

    private static class ObjectLibrary
    {
        [Finare.Visual]
        public static BaseView? Echo(BaseView? input, out BaseView? output, ref BaseView? reference)
        {
            Assert.IsType<DerivedView>(input);
            output = reference = input;
            return input;
        }
    }

    [Fact]
    public unsafe void BlueprintDispatcherProjectsTypedArgumentsReturnsAndOutputCells()
    {
        var bridge = new TestBridge(["/Script/Fixture.Derived", "/Script/Fixture.Base"]);
        var dispatcher = UnrealManagedFunctionDispatcherV2.Create(typeof(ObjectLibrary), staticOnly: true);
        int byteCount = 0;
        Assert.True(UnrealNativeValueCodecV3.TryEncode(Handle, Span<byte>.Empty, ref byteCount, out var input, out _));
        var args = new UnrealNativeValueV3[] { input, default, default };
        Assert.True(UnrealNativeValueCodecV3.TryEncode(default(UnrealObjectHandle), Span<byte>.Empty, ref byteCount, out args[2], out _));
        var results = new UnrealNativeValueV3[3];
        fixed (UnrealNativeValueV3* argumentPointer = args)
        fixed (UnrealNativeValueV3* resultPointer = results)
        {
            var frame = new UnrealNativeCallFrameV3
            {
                Arguments = (nint)argumentPointer, ArgumentCount = 3,
                Results = (nint)resultPointer, ResultCapacity = 3,
            };
            dispatcher.Invoke(null, "Echo", (nint)(&frame), bridge);
            Assert.Equal(3U, frame.ResultCount);
            foreach (var result in results)
            {
                Assert.True(UnrealNativeValueCodecV3.TryDecode(result, ReadOnlySpan<byte>.Empty, 0, out UnrealObjectHandle handle));
                Assert.Equal(Handle, handle);
            }
        }
    }

    [Fact]
    public void ReverseCallsUseTypedObjectsAndReturnWireHandlesIncludingRefOut()
    {
        var bridge = new TestBridge(["/Script/Fixture.Derived", "/Script/Fixture.Base"]);
        ObjectCallback handler = (BaseView? input, out BaseView? output, ref BaseView? reference) =>
        {
            Assert.IsType<DerivedView>(input);
            Assert.Null(reference);
            reference = output = input;
            return input;
        };
        Delegate wire = UnrealObjectDelegateAdapter.Adapt(handler, bridge);
        object?[] args = [Handle, null, default(UnrealObjectHandle)];
        Assert.Equal(Handle, wire.DynamicInvoke(args));
        Assert.Equal(Handle, args[1]);
        Assert.Equal(Handle, args[2]);
        bridge.Dispose();
        Assert.IsType<ObjectDisposedException>(Assert.Throws<TargetInvocationException>(() => wire.DynamicInvoke(args)).InnerException);
    }

    [UnrealWireType(typeof(WireStruct))]
    private readonly record struct ViewStruct(
        [property: UnrealStructField(0)] BaseView? Target,
        [property: UnrealStructField(1)] IReadOnlyDictionary<int, IReadOnlyList<BaseView?>> Groups);

    private readonly record struct WireStruct(
        [property: UnrealStructField(0)] UnrealObjectHandle Target,
        [property: UnrealStructField(1)] IReadOnlyDictionary<int, IReadOnlyList<UnrealObjectHandle>> Groups);

    [UnrealType("/Script/Fixture.Base")]
    private class BaseView : UnrealObject
    {
        public BaseView(UnrealObjectHandle handle, IUnrealInvocationBridge bridge)
            : base(handle, bridge)
        {
        }
    }

    [UnrealType("/Script/Fixture.Derived")]
    private sealed class DerivedView : BaseView
    {
        public DerivedView(UnrealObjectHandle handle, IUnrealInvocationBridge bridge)
            : base(handle, bridge)
        {
        }
    }

    private sealed class TestBridge :
        IUnrealInvocationBridge,
        IUnrealObjectBridgeScope,
        IUnrealObjectTypeBridge,
        IUnrealOutputInvocationBridge
    {
        private readonly HashSet<string> _typePaths;
        private bool _disposed;

        public TestBridge(IEnumerable<string> typePaths, object? identityScope = null)
        {
            _typePaths = typePaths.ToHashSet(StringComparer.Ordinal);
            IdentityScope = identityScope ?? new object();
        }

        public object IdentityScope { get; }

        public UnrealOutputInvocationResult Response { get; set; } = new(null, Array.Empty<object?>());
        public Type? LastReturnType { get; private set; }
        public IReadOnlyList<UnrealOutputArgument>? LastArguments { get; private set; }
        public int Calls { get; private set; }

        public UnrealOutputInvocationResult InvokeWithOutputs(UnrealObjectHandle target, ulong id, string path,
            Type returnType, IReadOnlyList<UnrealOutputArgument> arguments)
        {
            Calls++;
            LastReturnType = returnType;
            LastArguments = arguments;
            return Response;
        }

        public void ValidateAccess(UnrealObjectHandle handle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!handle.IsWellFormed)
            {
                throw new ObjectDisposedException(nameof(TestBridge));
            }
        }

        public IReadOnlyList<string> GetObjectTypePaths(UnrealObjectHandle target) =>
            _typePaths.ToArray();

        public bool IsObjectOfClass(UnrealObjectHandle target, string classPath) =>
            _typePaths.Contains(classPath);

        public T ReadProperty<T>(UnrealObjectHandle target, string propertyPath) => default!;

        public void WriteProperty<T>(UnrealObjectHandle target, string propertyPath, T value)
        {
        }

        public T Invoke<T>(UnrealObjectHandle target, string functionPath, IReadOnlyList<object?> arguments) => default!;

        public void Invoke(UnrealObjectHandle target, string functionPath, IReadOnlyList<object?> arguments)
        {
        }

        public void Dispose() => _disposed = true;
    }
}
