using UnifiedScript.Unreal;
using GeneratedComponent = UnifiedScript.Unreal.Generated.UnifiedScriptComponent;

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
            bridge.WriteProperty(Handle, FixtureTransport.ScriptAssetPath, 1.5));

        Assert.Equal(UnrealInvocationError.UnsupportedValueType, exception.Error);
        Assert.Empty(transport.MemberPaths);
    }

    [Fact]
    public void ManagedErrorCodesMatchNativeContract()
    {
        Assert.Equal(3, (byte)UnrealInvocationValueKind.String);
        Assert.Equal(6, (byte)UnrealObjectHandleError.StaleHandle);
        Assert.Equal(1, (byte)UnrealInvocationError.ObjectHandleFailure);
        Assert.Equal(11, (byte)UnrealInvocationError.ValueTypeMismatch);
        Assert.Equal(13, (byte)UnrealInvocationError.UnsupportedParameter);
        Assert.Equal(14, (byte)UnrealInvocationError.TransportUnavailable);
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
