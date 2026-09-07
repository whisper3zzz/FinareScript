using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using UnifiedScript.CSharp;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealBlueprintOverrideReloadContractTests
{
    public static IEnumerable<object[]> IncompatibleCandidates()
    {
        yield return ["added", "added"];
        yield return ["removed", "removed"];
        yield return ["retargeted", "removed"];
        yield return ["pathcase", "declaring class changed"];
        yield return ["functioncase", "function name changed"];
        yield return ["renamed", "managed method changed"];
        yield return ["return", "V2 ABI signature changed"];
        yield return ["ref", "V2 ABI signature changed"];
        yield return ["type", "V2 ABI signature changed"];
    }

    [Fact]
    public unsafe void BodyOnlyReplacementKeepsOverrideContractAndCanReload()
    {
        string directory = CreateTempDirectory();
        string assemblyPath = Path.Combine(directory, "OverrideReloadFixture.dll");
        const string typeName = "OverrideReloadFixture.ReloadScript";
        EmitOverrideScript(assemblyPath, "body1");
        var transport = new OverrideReloadTransport();
        var bridge = new NativeUnrealInvocationBridge(transport, worldHostId: 401);
        UnrealObjectHandle host = UnrealObjectHandle.Create(1, 401);
        using var instance = UnrealWorldManagedEntryPoint.ManagedScriptInstance.Create(
            worldId: 401,
            instanceId: 1,
            bridge,
            host,
            host,
            assemblyPath,
            typeName);
        try
        {
            instance.Start();
            Assert.Equal("body1-start", transport.LastMarker);
            Assert.Equal(2, InvokeEvaluate(instance, 1));

            EmitOverrideScript(assemblyPath, "body2");
            int candidate = instance.PrepareReload(assemblyPath, typeName);

            Assert.Equal(2, candidate);
            Assert.Equal("body2-start", transport.LastMarker);
            Assert.Contains(
                ReadReloadState(instance).GetProperty("phases").EnumerateArray(),
                static phase => phase.GetProperty("name").GetString() == "overrideContractValidation" &&
                    phase.GetProperty("state").GetString() == "succeeded");

            Assert.Equal(candidate, instance.CommitPreparedReload(candidate));
            instance.FinalizePreparedReload(candidate);
            Assert.Equal(3, InvokeEvaluate(instance, 1));

            EmitOverrideScript(assemblyPath, "body1");
            int abortedCandidate = instance.PrepareReload(assemblyPath, typeName);
            instance.AbortPreparedReload(abortedCandidate);
            Assert.Equal(3, InvokeEvaluate(instance, 1));
            instance.Tick(0.016f);
            Assert.Equal("body2-tick-1", transport.LastMarker);
        }
        finally
        {
            instance.Dispose();
            TryDelete(directory);
        }
    }

    [Fact]
    public void PersistedActivationContractRejectsCandidateBeforeOldStopThenAllowsCompatibleBody()
    {
        string directory = CreateTempDirectory();
        string assemblyPath = Path.Combine(directory, "OverrideReloadFixture.dll");
        const string typeName = "OverrideReloadFixture.ReloadScript";
        EmitOverrideScript(assemblyPath, "body1");
        var transport = new OverrideReloadTransport();
        var bridge = new NativeUnrealInvocationBridge(transport, worldHostId: 402);
        UnrealObjectHandle host = UnrealObjectHandle.Create(1, 402);
        UnrealBlueprintOverrideActivationContract expected =
            CreateExpectedActivationContract();
        using var instance = UnrealWorldManagedEntryPoint.ManagedScriptInstance.Create(
            worldId: 402,
            instanceId: 3,
            bridge,
            host,
            host,
            assemblyPath,
            typeName,
            expected);
        try
        {
            instance.Start();
            Assert.Equal("body1-start", transport.LastMarker);
            Assert.Equal(2, InvokeEvaluate(instance, 1));

            EmitOverrideScript(assemblyPath, "return");
            UnrealBlueprintOverrideActivationException failure =
                Assert.Throws<UnrealBlueprintOverrideActivationException>(
                () => instance.PrepareReload(assemblyPath, typeName));
            Assert.Contains("returnType.nativeName", failure.Message, StringComparison.Ordinal);
            Assert.Equal("body1-start", transport.LastMarker);
            Assert.Equal(1, instance.GetDiagnosticContext().Generation);
            Assert.Contains(
                ReadReloadState(instance).GetProperty("phases").EnumerateArray(),
                static phase => phase.GetProperty("name").GetString() == "activationContractValidation" &&
                    phase.GetProperty("state").GetString() == "failed");

            EmitOverrideScript(assemblyPath, "body2");
            int compatibleCandidate = instance.PrepareReload(assemblyPath, typeName);
            Assert.Equal(compatibleCandidate, instance.CommitPreparedReload(compatibleCandidate));
            instance.FinalizePreparedReload(compatibleCandidate);
            Assert.Equal(3, InvokeEvaluate(instance, 1));
        }
        finally
        {
            instance.Dispose();
            TryDelete(directory);
        }
    }

    [Theory]
    [MemberData(nameof(IncompatibleCandidates))]
    public void IncompatibleOverrideRouteSetLeavesOldGenerationRunning(
        string variant,
        string expectedReason)
    {
        string directory = CreateTempDirectory();
        string assemblyPath = Path.Combine(directory, "OverrideReloadFixture.dll");
        const string typeName = "OverrideReloadFixture.ReloadScript";
        EmitOverrideScript(assemblyPath, "body1");
        var transport = new OverrideReloadTransport();
        var bridge = new NativeUnrealInvocationBridge(transport, worldHostId: 410);
        UnrealObjectHandle host = UnrealObjectHandle.Create(1, 410);
        using var instance = UnrealWorldManagedEntryPoint.ManagedScriptInstance.Create(
            worldId: 410,
            instanceId: 2,
            bridge,
            host,
            host,
            assemblyPath,
            typeName);
        try
        {
            instance.Start();
            Assert.Equal("body1-start", transport.LastMarker);
            Assert.Equal(2, InvokeEvaluate(instance, 1));

            EmitOverrideScript(assemblyPath, variant);
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
                instance.PrepareReload(assemblyPath, typeName));

            Assert.Contains(expectedReason, failure.Message, StringComparison.Ordinal);
            Assert.Equal("body1-start", transport.LastMarker);
            Assert.Equal(1, instance.GetDiagnosticContext().Generation);
            JsonElement state = ReadReloadState(instance);
            Assert.Equal("rolledBack", state.GetProperty("state").GetString());
            Assert.Contains(
                state.GetProperty("phases").EnumerateArray(),
                static phase => phase.GetProperty("name").GetString() == "overrideContractValidation" &&
                    phase.GetProperty("state").GetString() == "failed");
            Assert.Contains(
                state.GetProperty("phases").EnumerateArray(),
                static phase => phase.GetProperty("name").GetString() == "rollback" &&
                    phase.GetProperty("state").GetString() == "unchanged");
            Assert.DoesNotContain(
                state.GetProperty("phases").EnumerateArray(),
                static phase => phase.GetProperty("name").GetString() == "oldGenerationStop");

            instance.Tick(0.016f);
            Assert.Equal("body1-tick-1", transport.LastMarker);
            Assert.DoesNotContain("body1-stop", transport.History);
            Assert.DoesNotContain($"{variant}-start", transport.History);

            EmitOverrideScript(assemblyPath, "body2");
            int compatibleCandidate = instance.PrepareReload(assemblyPath, typeName);
            Assert.Equal(2, compatibleCandidate);
            Assert.Equal(compatibleCandidate, instance.CommitPreparedReload(compatibleCandidate));
            instance.FinalizePreparedReload(compatibleCandidate);
            Assert.Equal(3, InvokeEvaluate(instance, 1));
        }
        finally
        {
            instance.Dispose();
            TryDelete(directory);
        }
    }

    [Fact]
    public void MalformedOverrideDeclarationsFailBeforeDispatcherCanOmitThem()
    {
        AssertInvalidDispatcher<PrivateOverrideReloadScript>("public instance method");
        AssertInvalidDispatcher<StaticOverrideReloadScript>("public instance method");
        AssertInvalidDispatcher<GenericOverrideReloadScript>("cannot be generic");
        AssertInvalidDispatcher<InvalidPathOverrideReloadScript>("invalid declaring class path");
        AssertInvalidDispatcher<InvalidSuperModeOverrideReloadScript>("SuperMode=Explicit");
        AssertInvalidDispatcher<ConflictingOverrideReloadScript>("UnrealBlueprintEventAttribute");
        AssertInvalidDispatcher<MultipleEventConflictOverrideReloadScript>("UnrealEventAttribute");
        AssertInvalidDispatcher<DuplicateOverrideReloadScript>("Duplicate Blueprint override route");
        AssertInvalidDispatcher<HiddenOverrideReloadScript>("hidden from the active managed instance dispatch surface");
    }

    [Fact]
    public void V2DispatcherStoresImmutableOverrideRouteMetadata()
    {
        UnrealManagedFunctionDispatcherV2 dispatcher =
            UnrealManagedFunctionDispatcherV2.Create(
                typeof(OverrideStableReloadScript),
                staticOnly: false);

        UnrealBlueprintOverrideRoute route = Assert.Single(dispatcher.BlueprintOverrides).Value;
        Assert.Equal(
            "UnifiedScript.Tests.OverrideStableReloadScript.Evaluate",
            route.ManagedMethodIdentity);
        Assert.Equal("/Script/Engine.Actor", route.DeclaringClassPath);
        Assert.Equal("ReceiveBeginPlay", route.FunctionName);
        Assert.Equal(UnrealBlueprintEventSuperMode.Explicit, route.SuperMode);
        Assert.Contains("Evaluate", route.AbiSignature, StringComparison.Ordinal);
    }

    private static void AssertInvalidDispatcher<T>(string reason)
    {
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            UnrealManagedFunctionDispatcherV2.Create(typeof(T), staticOnly: false));
        Assert.Contains(reason, failure.Message, StringComparison.Ordinal);
    }

    private static JsonElement ReadReloadState(
        UnrealWorldManagedEntryPoint.ManagedScriptInstance instance)
    {
        using JsonDocument document = JsonDocument.Parse(instance.GetReloadState());
        return document.RootElement.Clone();
    }

    private static unsafe int InvokeEvaluate(
        UnrealWorldManagedEntryPoint.ManagedScriptInstance instance,
        int value)
    {
        Span<UnrealNativeValueV3> arguments = stackalloc UnrealNativeValueV3[1];
        Span<UnrealNativeValueV3> results = stackalloc UnrealNativeValueV3[1];
        int byteCount = 0;
        Assert.True(UnrealNativeValueCodecV3.TryEncode(
            value,
            Span<byte>.Empty,
            ref byteCount,
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
            instance.InvokeFunctionV2("Evaluate", (nint)(&frame));
            Assert.Equal(UnrealNativeCallStatusV3.Success, frame.Status);
            Assert.True(UnrealNativeValueCodecV3.TryDecode(
                in results[0],
                ReadOnlySpan<byte>.Empty,
                0,
                out int result));
            return result;
        }
    }

    private static void EmitOverrideScript(string assemblyPath, string variant)
    {
        string method = variant switch
        {
            "body1" => "public int Evaluate(int value) => value + 1;",
            "body2" => "public int Evaluate(int value) => value + 2;",
            "added" => "public int Evaluate(int value) => value + 1; [UnrealBlueprintOverride(\"/Script/Engine.Actor\", \"ReceiveTick\")] public int Added(int value) => value;",
            "removed" => "public int Evaluate(int value) => value + 1;",
            "retargeted" => "public int Evaluate(int value) => value + 1;",
            "pathcase" => "public int Evaluate(int value) => value + 1;",
            "functioncase" => "public int Evaluate(int value) => value + 1;",
            "renamed" => "public int Renamed(int value) => value + 1;",
            "return" => "public long Evaluate(int value) => value + 1;",
            "ref" => "public int Evaluate(ref int value) => value + 1;",
            "type" => "public int Evaluate(bool value) => value ? 1 : 0;",
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };
        if (variant is not "removed")
        {
            string path = variant == "pathcase"
                ? "/Script/Engine.actor"
                : "/Script/Engine.Actor";
            string function = variant switch
            {
                "retargeted" => "ReceiveTick",
                "functioncase" => "receivebeginplay",
                _ => "ReceiveBeginPlay",
            };
            method =
                $"[UnrealBlueprintOverride(\"{path}\", \"{function}\")] {method}";
        }

        string source = $$"""
            using UnifiedScript.Unreal;

            namespace OverrideReloadFixture;

            public sealed class ReloadScript : IUnrealManagedScript
            {
                private UnrealScriptContext _context;
                private int _ticks;

                public void OnStart(UnrealScriptContext context)
                {
                    _context = context;
                    _context.Bridge.WriteProperty(_context.Host, "Marker", "{{variant}}-start");
                }

                public void OnTick(float deltaSeconds) =>
                    _context.Bridge.WriteProperty(
                        _context.Host,
                        "Marker",
                        "{{variant}}-tick-" + (++_ticks));

                public void OnStop() =>
                    _context.Bridge.WriteProperty(_context.Host, "Marker", "{{variant}}-stop");

                {{method}}
            }
            """;
        CSharpCompilation compilation = new CSharpCompilationHost().Create(
            CSharpSyntaxTree.ParseText(source, path: "OverrideReloadFixture.cs"),
            [MetadataReference.CreateFromFile(typeof(UnrealScriptClassAttribute).Assembly.Location)]);
        using FileStream stream = new(
            assemblyPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        EmitResult result = compilation.Emit(stream);
        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics));
    }

    private static UnrealBlueprintOverrideActivationContract CreateExpectedActivationContract() =>
        UnrealBlueprintOverrideActivationContract.Parse(
            """
            {"schema":"unifiedscript.blueprint-override-contract/v0.1","overrides":[
              {"managedName":"Evaluate","declaringClassPath":"/Script/Engine.Actor",
               "functionName":"ReceiveBeginPlay","superMode":"explicit",
               "returnType":{"kind":"value","nativeName":"int32","typePath":""},
               "parameters":[{"direction":"in",
                              "type":{"kind":"value","nativeName":"int32","typePath":""}}]}]}
            """);

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "UnifiedScriptBlueprintOverrideReload",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // The collectible context can finish unloading after the test;
            // the directory is isolated to this test and can be cleaned later.
        }
    }

    private sealed class OverrideReloadTransport : IUnrealNativeInvocationTransport
    {
        public string LastMarker { get; private set; } = string.Empty;

        public List<string> History { get; } = [];

        public UnrealInvocationResponse ReadProperty(
            UnrealObjectHandle target,
            string propertyPath) => UnrealInvocationResponse.Failed(
            UnrealInvocationError.PropertyNotFound,
            propertyPath);

        public UnrealInvocationResponse WriteProperty(
            UnrealObjectHandle target,
            string propertyPath,
            UnrealInvocationValue value)
        {
            LastMarker = value.StringValue ?? string.Empty;
            History.Add(LastMarker);
            return UnrealInvocationResponse.Succeeded(UnrealInvocationValue.Void);
        }

        public UnrealInvocationResponse Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<UnrealInvocationValue> arguments) =>
            UnrealInvocationResponse.Failed(
                UnrealInvocationError.FunctionNotFound,
                functionPath);
    }
}

public class OverrideStableReloadScript : IUnrealManagedScript
{
    private UnrealScriptContext _context;

    public void OnStart(UnrealScriptContext context)
    {
        _context = context;
        _context.Bridge.WriteProperty(_context.Host, "Marker", "stable-start");
    }

    public void OnTick(float deltaSeconds) =>
        _context.Bridge.WriteProperty(_context.Host, "Marker", "stable-tick");

    public void OnStop() =>
        _context.Bridge.WriteProperty(_context.Host, "Marker", "stable-stop");

    [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
    public int Evaluate(int value) => value + 1;
}

public sealed class PrivateOverrideReloadScript : IUnrealManagedScript
{
    public void OnStart(UnrealScriptContext context)
    {
    }

    public void OnTick(float deltaSeconds)
    {
    }

    public void OnStop()
    {
    }

    [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
    private int Evaluate(int value) => value;
}

public sealed class StaticOverrideReloadScript : IUnrealManagedScript
{
    public void OnStart(UnrealScriptContext context)
    {
    }

    public void OnTick(float deltaSeconds)
    {
    }

    public void OnStop()
    {
    }

    [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
    public static int Evaluate(int value) => value;
}

public sealed class GenericOverrideReloadScript : IUnrealManagedScript
{
    public void OnStart(UnrealScriptContext context)
    {
    }

    public void OnTick(float deltaSeconds)
    {
    }

    public void OnStop()
    {
    }

    [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
    public int Evaluate<T>(int value) => value;
}

public sealed class InvalidPathOverrideReloadScript : IUnrealManagedScript
{
    public void OnStart(UnrealScriptContext context)
    {
    }

    public void OnTick(float deltaSeconds)
    {
    }

    public void OnStop()
    {
    }

    [UnrealBlueprintOverride("/Game/../Broken/Broken.Broken_C", "ReceiveBeginPlay")]
    public int Evaluate(int value) => value;
}

public sealed class InvalidSuperModeOverrideReloadScript : IUnrealManagedScript
{
    public void OnStart(UnrealScriptContext context)
    {
    }

    public void OnTick(float deltaSeconds)
    {
    }

    public void OnStop()
    {
    }

    [UnrealBlueprintOverride(
        "/Script/Engine.Actor",
        "ReceiveBeginPlay",
        SuperMode = (UnrealBlueprintEventSuperMode)999)]
    public int Evaluate(int value) => value;
}

public sealed class ConflictingOverrideReloadScript : IUnrealManagedScript
{
    public void OnStart(UnrealScriptContext context)
    {
    }

    public void OnTick(float deltaSeconds)
    {
    }

    public void OnStop()
    {
    }

    [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
    [UnrealBlueprintEvent]
    public int Evaluate(int value) => value;
}

public sealed class MultipleEventConflictOverrideReloadScript : IUnrealManagedScript
{
    public void OnStart(UnrealScriptContext context)
    {
    }

    public void OnTick(float deltaSeconds)
    {
    }

    public void OnStop()
    {
    }

    [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
    [UnrealEvent(typeof(UnrealObject), "First")]
    [UnrealEvent(typeof(UnrealObject), "Second")]
    public int Evaluate(int value) => value;
}

public sealed class DuplicateOverrideReloadScript : IUnrealManagedScript
{
    public void OnStart(UnrealScriptContext context)
    {
    }

    public void OnTick(float deltaSeconds)
    {
    }

    public void OnStop()
    {
    }

    [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
    public int Evaluate(int value) => value;

    [UnrealBlueprintOverride("/Script/Engine.actor", "receivebeginplay")]
    public int EvaluateOther(int value) => value;
}

public class HiddenOverrideBaseReloadScript
{
    [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
    public int Evaluate(int value) => value;
}

public sealed class HiddenOverrideReloadScript : HiddenOverrideBaseReloadScript, IUnrealManagedScript
{
    public new int Evaluate(int value) => value + 1;

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
