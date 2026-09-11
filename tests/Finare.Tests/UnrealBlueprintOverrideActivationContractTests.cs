using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Finare.CSharp;
using System.Text.Json;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed class UnrealBlueprintOverrideActivationContractTests
{
    [Fact]
    public void ContractRoundTripsAndIgnoresClrStructPropertyNames()
    {
        UnrealBlueprintOverrideActivationContract expected =
            UnrealBlueprintOverrideActivationContract.Create(typeof(ActivationStructScript));
        string json = expected.ToJson();
        UnrealBlueprintOverrideActivationContract parsed =
            UnrealBlueprintOverrideActivationContract.Parse(json);

        parsed.ValidateType(typeof(ActivationStructRenamedPropertyScript));
        Assert.Contains(
            "\"schema\":\"unifiedscript.blueprint-override-contract/v0.1\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains("\"nativeName\":\"FActivationPayload\"", json, StringComparison.Ordinal);
        Assert.Contains("\"nativeName\":\"NativeCount\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Count\"", json, StringComparison.Ordinal);
        Assert.Equal(json, parsed.ToJson());
    }

    [Fact]
    public void ComplexNativeShapesCompareEnumStructSoftAndCollectionIdentity()
    {
        UnrealBlueprintOverrideActivationContract expected =
            UnrealBlueprintOverrideActivationContract.Create(typeof(ActivationComplexScript));
        expected.ValidateType(typeof(ActivationComplexScript));
        string json = expected.ToJson();
        Assert.Contains("\"kind\":\"enum\"", json, StringComparison.Ordinal);
        Assert.Contains("\"nativeName\":\"FSoftObjectPath\"", json, StringComparison.Ordinal);
        Assert.Contains("\"nativeName\":\"FSoftClassPath\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"array\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"set\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"map\"", json, StringComparison.Ordinal);

        AssertShapeMismatch(
            expected,
            typeof(ActivationComplexOtherEnumScript),
            "returnType.key.typePath");
        AssertShapeMismatch(
            expected,
            typeof(ActivationComplexReorderedStructScript),
            "parameters[0].type.fields[0].nativeName");
        AssertShapeMismatch(
            expected,
            typeof(ActivationComplexStringArrayScript),
            "returnType.value.element.nativeName");
        AssertShapeMismatch(
            expected,
            typeof(ActivationComplexStringSetScript),
            "parameters[1].type.element.nativeName");

        UnrealBlueprintOverrideActivationContract mutatedSoftObject =
            UnrealBlueprintOverrideActivationContract.Parse(
                ReplaceFirst(
                    json,
                    "\"nativeName\":\"FSoftObjectPath\"",
                    "\"nativeName\":\"FString\""));
        UnrealBlueprintOverrideActivationException softFailure =
            Assert.Throws<UnrealBlueprintOverrideActivationException>(
                () => mutatedSoftObject.ValidateType(typeof(ActivationComplexScript)));
        Assert.Contains("returnType.value.element.nativeName", softFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyContractRejectsUnexpectedOverrideAndMatchingEmptyType()
    {
        UnrealBlueprintOverrideActivationContract.Empty.ValidateType(typeof(NoOverrideScript));

        UnrealBlueprintOverrideActivationException failure =
            Assert.Throws<UnrealBlueprintOverrideActivationException>(
                () => UnrealBlueprintOverrideActivationContract.Empty.ValidateType(
                    typeof(ActivationStableScript)));
        Assert.Contains("unexpected route", failure.Message, StringComparison.Ordinal);
        Assert.Contains("synchronize", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(typeof(ActivationRefScript), "parameters[0].direction")]
    [InlineData(typeof(ActivationLongScript), "returnType.nativeName")]
    [InlineData(typeof(ActivationRouteCaseScript), "declaringClassPath")]
    [InlineData(typeof(ActivationRenamedMethodScript), "managedName")]
    public void TypeMismatchReportsFirstConcreteDifference(Type candidateType, string expectedPath)
    {
        UnrealBlueprintOverrideActivationContract expected =
            UnrealBlueprintOverrideActivationContract.Create(typeof(ActivationStableScript));

        UnrealBlueprintOverrideActivationException failure =
            Assert.Throws<UnrealBlueprintOverrideActivationException>(
                () => expected.ValidateType(candidateType));

        Assert.Contains(expectedPath, failure.Message, StringComparison.Ordinal);
        Assert.Contains("expected", failure.Message, StringComparison.Ordinal);
        Assert.Contains("actual", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Stop the affected instance", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParserRejectsUnsupportedSchemaVoidParameterAndTooManyParameters()
    {
        AssertContractRejected(
            """
            {"schema":"unifiedscript.blueprint-override-contract/v9","overrides":[]}
            """,
            "schema");
        AssertContractRejected(
            """
            {"schema":"unifiedscript.blueprint-override-contract/v0.1","overrides":[
              {"managedName":"Evaluate","declaringClassPath":"/Script/Engine.Actor",
               "functionName":"ReceiveBeginPlay","superMode":"explicit",
               "returnType":{"kind":"void","nativeName":"","typePath":""},
               "parameters":[{"direction":"in","type":{"kind":"void","nativeName":"","typePath":""}}]}]}
            """,
            "void");
        AssertContractRejected(
            """
            {"schema":"unifiedscript.blueprint-override-contract/v0.1","overrides":[
              {"managedName":"Evaluate","declaringClassPath":"/Script/Engine.Actor",
               "functionName":"ReceiveBeginPlay","superMode":"explicit",
               "returnType":{"kind":"value","nativeName":"int32","typePath":""},
               "parameters":[
                 {"direction":"in","type":{"kind":"value","nativeName":"int32","typePath":""}},
                 {"direction":"in","type":{"kind":"value","nativeName":"int32","typePath":""}},
                 {"direction":"in","type":{"kind":"value","nativeName":"int32","typePath":""}},
                 {"direction":"in","type":{"kind":"value","nativeName":"int32","typePath":""}},
                 {"direction":"in","type":{"kind":"value","nativeName":"int32","typePath":""}}]}]}
            """,
            "parameters");
    }

    [Fact]
    public void ParserRejectsUnknownPropertiesDuplicateRoutesAndInvalidNestedShapes()
    {
        AssertContractRejected(
            """
            {"schema":"unifiedscript.blueprint-override-contract/v0.1","overrides":[],"extra":1}
            """,
            "unknown property");
        AssertContractRejected(
            """
            {"schema":"unifiedscript.blueprint-override-contract/v0.1","overrides":[
              {"managedName":"First","declaringClassPath":"/Script/Engine.Actor",
               "functionName":"ReceiveBeginPlay","superMode":"explicit",
               "returnType":{"kind":"void","nativeName":"","typePath":""},"parameters":[]},
              {"managedName":"Second","declaringClassPath":"/Script/engine.actor",
               "functionName":"receivebeginplay","superMode":"explicit",
               "returnType":{"kind":"void","nativeName":"","typePath":""},"parameters":[]}]}
            """,
            "Duplicate Blueprint override route");
        AssertContractRejected(
            """
            {"schema":"unifiedscript.blueprint-override-contract/v0.1","overrides":[
              {"managedName":"Evaluate","declaringClassPath":"/Script/Engine.Actor",
               "functionName":"ReceiveBeginPlay","superMode":"explicit",
               "returnType":{"kind":"array","nativeName":"TArray<int32>","typePath":"",
                             "element":{"kind":"void","nativeName":"","typePath":""}},
               "parameters":[]}]}
            """,
            "void is only valid");
    }

    [Fact]
    public void ParserEnforcesNestedShapeDepthSixteen()
    {
        string shape = """{"kind":"value","nativeName":"int32","typePath":""}""";
        for (int index = 0; index < 17; index++)
        {
            shape =
                $$"""{"kind":"array","nativeName":"TArray<int32>","typePath":"","element":{{shape}}}""";
        }
        string json =
            $$"""{"schema":"unifiedscript.blueprint-override-contract/v0.1","overrides":[{"managedName":"Evaluate","declaringClassPath":"/Script/Engine.Actor","functionName":"ReceiveBeginPlay","superMode":"explicit","returnType":{{shape}},"parameters":[]}]}""";

        AssertContractRejected(json, "depth exceeds");
    }

    [Fact]
    public void ParsedShapeMismatchNamesNestedDifference()
    {
        UnrealBlueprintOverrideActivationContract expected =
            UnrealBlueprintOverrideActivationContract.Create(typeof(ActivationStableScript));
        using JsonDocument document = JsonDocument.Parse(expected.ToJson());
        string json = expected.ToJson().Replace(
            "\"nativeName\":\"int32\"",
            "\"nativeName\":\"FString\"",
            StringComparison.Ordinal);
        UnrealBlueprintOverrideActivationContract candidate =
            UnrealBlueprintOverrideActivationContract.Parse(json);

        UnrealBlueprintOverrideActivationException failure =
            Assert.Throws<UnrealBlueprintOverrideActivationException>(
                () => candidate.ValidateType(typeof(ActivationStableScript)));
        Assert.Contains("returnType.nativeName", failure.Message, StringComparison.Ordinal);
        Assert.Equal(
            JsonValueKind.Array,
            document.RootElement.GetProperty("overrides").ValueKind);
    }

    [Fact]
    public void ExpectedContractRejectsBeforeConstructorAndMatchingContractStarts()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "FinareActivationContract",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string assemblyPath = Path.Combine(directory, "ActivationFixture.dll");
        string markerPath = Path.Combine(directory, "activation.marker");
        const string markerVariable = "FINARE_ACTIVATION_CONTRACT_MARKER";
        string? previousMarker = Environment.GetEnvironmentVariable(markerVariable);
        Environment.SetEnvironmentVariable(markerVariable, markerPath);
        try
        {
            EmitActivationFixture(assemblyPath);
            UnrealBlueprintOverrideActivationContract mismatch =
                UnrealBlueprintOverrideActivationContract.Parse(
                    CreateFixtureContractJson("ref"));
            var bridge = new NativeUnrealInvocationBridge(new ActivationTransport(), 501);
            UnrealObjectHandle host = UnrealObjectHandle.Create(1, 501);

            Assert.Throws<UnrealBlueprintOverrideActivationException>(
                () => UnrealWorldManagedEntryPoint.ManagedScriptInstance.Create(
                    501,
                    1,
                    bridge,
                    host,
                    host,
                    assemblyPath,
                    "ActivationFixture.ReloadScript",
                    mismatch));
            Assert.False(File.Exists(markerPath));

            UnrealBlueprintOverrideActivationContract matching =
                UnrealBlueprintOverrideActivationContract.Parse(
                    CreateFixtureContractJson("in"));
            using UnrealWorldManagedEntryPoint.ManagedScriptInstance instance =
                UnrealWorldManagedEntryPoint.ManagedScriptInstance.Create(
                    501,
                    2,
                    bridge,
                    host,
                    host,
                    assemblyPath,
                    "ActivationFixture.ReloadScript",
                    matching);
            instance.Start();
            Assert.True(File.Exists(markerPath));
            Assert.Contains("start", File.ReadAllText(markerPath), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(markerVariable, previousMarker);
            TryDelete(directory);
        }
    }

    private static void AssertContractRejected(string json, string reason)
    {
        UnrealBlueprintOverrideActivationException failure =
            Assert.Throws<UnrealBlueprintOverrideActivationException>(
                () => UnrealBlueprintOverrideActivationContract.Parse(json));
        Assert.Contains(reason, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertShapeMismatch(
        UnrealBlueprintOverrideActivationContract expected,
        Type candidateType,
        string path)
    {
        UnrealBlueprintOverrideActivationException failure =
            Assert.Throws<UnrealBlueprintOverrideActivationException>(
                () => expected.ValidateType(candidateType));
        Assert.Contains(path, failure.Message, StringComparison.Ordinal);
        Assert.Contains("expected", failure.Message, StringComparison.Ordinal);
        Assert.Contains("actual", failure.Message, StringComparison.Ordinal);
    }

    private static string ReplaceFirst(string value, string oldValue, string newValue)
    {
        int index = value.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Could not find '{oldValue}' in generated contract.");
        return value[..index] + newValue + value[(index + oldValue.Length)..];
    }

    private static string CreateFixtureContractJson(string direction) =>
        """
        {"schema":"unifiedscript.blueprint-override-contract/v0.1","overrides":[
          {"managedName":"Evaluate","declaringClassPath":"/Script/Engine.Actor",
           "functionName":"ReceiveBeginPlay","superMode":"explicit",
           "returnType":{"kind":"value","nativeName":"int32","typePath":""},
           "parameters":[{"direction":"__DIRECTION__",
                          "type":{"kind":"value","nativeName":"int32","typePath":""}}]}]}
        """.Replace("__DIRECTION__", direction, StringComparison.Ordinal);

    private static void EmitActivationFixture(string assemblyPath)
    {
        const string source = """
            using System;
            using System.IO;
            using Finare.Unreal;

            namespace ActivationFixture;

            public sealed class ReloadScript : IUnrealManagedScript
            {
                public ReloadScript()
                {
                    string? marker = Environment.GetEnvironmentVariable(
                        "FINARE_ACTIVATION_CONTRACT_MARKER");
                    if (!string.IsNullOrEmpty(marker))
                    {
                        File.AppendAllText(marker, "ctor\n");
                    }
                }

                public void OnStart(UnrealScriptContext context)
                {
                    string? marker = Environment.GetEnvironmentVariable(
                        "FINARE_ACTIVATION_CONTRACT_MARKER");
                    if (!string.IsNullOrEmpty(marker))
                    {
                        File.AppendAllText(marker, "start\n");
                    }
                }

                public void OnTick(float deltaSeconds) { }
                public void OnStop() { }

                [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
                public int Evaluate(int value) => value + 1;
            }
            """;
        CSharpCompilation compilation = new CSharpCompilationHost().Create(
            CSharpSyntaxTree.ParseText(source, path: "ActivationFixture.cs"),
            [MetadataReference.CreateFromFile(typeof(UnrealScriptClassAttribute).Assembly.Location)]);
        using FileStream stream = new(
            assemblyPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        EmitResult result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // The collectible context can finish unloading after the test.
        }
    }

    private sealed class NoOverrideScript : IUnrealManagedScript
    {
        public void OnStart(UnrealScriptContext context) { }
        public void OnTick(float deltaSeconds) { }
        public void OnStop() { }
    }

    private sealed class ActivationTransport : IUnrealNativeInvocationTransport
    {
        public UnrealInvocationResponse ReadProperty(
            UnrealObjectHandle target,
            string propertyPath) =>
            UnrealInvocationResponse.Failed(UnrealInvocationError.PropertyNotFound, propertyPath);

        public UnrealInvocationResponse WriteProperty(
            UnrealObjectHandle target,
            string propertyPath,
            UnrealInvocationValue value) =>
            UnrealInvocationResponse.Succeeded(UnrealInvocationValue.Void);

        public UnrealInvocationResponse Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<UnrealInvocationValue> arguments) =>
            UnrealInvocationResponse.Succeeded(UnrealInvocationValue.Void);
    }

    private sealed class ActivationStableScript : IUnrealManagedScript
    {
        [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
        public int Evaluate(int value) => value + 1;

        public void OnStart(UnrealScriptContext context) { }
        public void OnTick(float deltaSeconds) { }
        public void OnStop() { }
    }

    private sealed class ActivationRefScript : IUnrealManagedScript
    {
        [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
        public int Evaluate(ref int value) => ++value;

        public void OnStart(UnrealScriptContext context) { }
        public void OnTick(float deltaSeconds) { }
        public void OnStop() { }
    }

    private sealed class ActivationLongScript : IUnrealManagedScript
    {
        [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
        public long Evaluate(int value) => value + 1;

        public void OnStart(UnrealScriptContext context) { }
        public void OnTick(float deltaSeconds) { }
        public void OnStop() { }
    }

    private sealed class ActivationRouteCaseScript : IUnrealManagedScript
    {
        [UnrealBlueprintOverride("/Script/engine.actor", "receivebeginplay")]
        public int Evaluate(int value) => value + 1;

        public void OnStart(UnrealScriptContext context) { }
        public void OnTick(float deltaSeconds) { }
        public void OnStop() { }
    }

    private sealed class ActivationRenamedMethodScript : IUnrealManagedScript
    {
        [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
        public int Renamed(int value) => value + 1;

        public void OnStart(UnrealScriptContext context) { }
        public void OnTick(float deltaSeconds) { }
        public void OnStop() { }
    }

    [UnrealStruct("/Script/Tests.ActivationPayload", "FActivationPayload")]
    private readonly struct ActivationPayload
    {
        [UnrealStructField(0, "NativeCount")]
        public int Count { get; }

        [UnrealStructField(1, "NativeLabel")]
        public string Label { get; }

        public ActivationPayload(int count, string label)
        {
            Count = count;
            Label = label;
        }
    }

    [UnrealStruct("/Script/Tests.ActivationPayload", "FActivationPayload")]
    private readonly struct ActivationPayloadRenamedProperties
    {
        [UnrealStructField(0, "NativeCount")]
        public int DifferentCount { get; }

        [UnrealStructField(1, "NativeLabel")]
        public string DifferentLabel { get; }

        public ActivationPayloadRenamedProperties(int count, string label)
        {
            DifferentCount = count;
            DifferentLabel = label;
        }
    }

    private sealed class ActivationStructScript : IUnrealManagedScript
    {
        [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
        public ActivationPayload Evaluate(ActivationPayload value) => value;

        public void OnStart(UnrealScriptContext context) { }
        public void OnTick(float deltaSeconds) { }
        public void OnStop() { }
    }

    private sealed class ActivationStructRenamedPropertyScript : IUnrealManagedScript
    {
        [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
        public ActivationPayloadRenamedProperties Evaluate(
            ActivationPayloadRenamedProperties value) => value;

        public void OnStart(UnrealScriptContext context) { }
        public void OnTick(float deltaSeconds) { }
        public void OnStop() { }
    }

    [UnrealEnum("/Script/Tests.EActivation", "EActivation")]
    private readonly struct ActivationEnum : IUnrealEnumValue
    {
        public ActivationEnum(long value) => Value = value;
        public long Value { get; }
    }

    [UnrealEnum("/Script/Tests.EOtherActivation", "EOtherActivation")]
    private readonly struct ActivationOtherEnum : IUnrealEnumValue
    {
        public ActivationOtherEnum(long value) => Value = value;
        public long Value { get; }
    }

    private sealed class ActivationComplexScript : IUnrealManagedScript
    {
        [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
        public IReadOnlyDictionary<ActivationEnum, IReadOnlyList<UnrealSoftObjectHandle>> Evaluate(
            ActivationPayload value,
            ref IReadOnlySet<UnrealSoftClassHandle> classes) =>
            new Dictionary<ActivationEnum, IReadOnlyList<UnrealSoftObjectHandle>>();

        public void OnStart(UnrealScriptContext context) { }
        public void OnTick(float deltaSeconds) { }
        public void OnStop() { }
    }

    private sealed class ActivationComplexOtherEnumScript : IUnrealManagedScript
    {
        [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
        public IReadOnlyDictionary<ActivationOtherEnum, IReadOnlyList<UnrealSoftObjectHandle>> Evaluate(
            ActivationPayload value,
            ref IReadOnlySet<UnrealSoftClassHandle> classes) =>
            new Dictionary<ActivationOtherEnum, IReadOnlyList<UnrealSoftObjectHandle>>();

        public void OnStart(UnrealScriptContext context) { }
        public void OnTick(float deltaSeconds) { }
        public void OnStop() { }
    }

    private sealed class ActivationComplexReorderedStructScript : IUnrealManagedScript
    {
        [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
        public IReadOnlyDictionary<ActivationEnum, IReadOnlyList<UnrealSoftObjectHandle>> Evaluate(
            ActivationPayloadReorderedProperties value,
            ref IReadOnlySet<UnrealSoftClassHandle> classes) =>
            new Dictionary<ActivationEnum, IReadOnlyList<UnrealSoftObjectHandle>>();

        public void OnStart(UnrealScriptContext context) { }
        public void OnTick(float deltaSeconds) { }
        public void OnStop() { }
    }

    private sealed class ActivationComplexStringArrayScript : IUnrealManagedScript
    {
        [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
        public IReadOnlyDictionary<ActivationEnum, IReadOnlyList<string>> Evaluate(
            ActivationPayload value,
            ref IReadOnlySet<UnrealSoftClassHandle> classes) =>
            new Dictionary<ActivationEnum, IReadOnlyList<string>>();

        public void OnStart(UnrealScriptContext context) { }
        public void OnTick(float deltaSeconds) { }
        public void OnStop() { }
    }

    private sealed class ActivationComplexStringSetScript : IUnrealManagedScript
    {
        [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
        public IReadOnlyDictionary<ActivationEnum, IReadOnlyList<UnrealSoftObjectHandle>> Evaluate(
            ActivationPayload value,
            ref IReadOnlySet<string> classes) =>
            new Dictionary<ActivationEnum, IReadOnlyList<UnrealSoftObjectHandle>>();

        public void OnStart(UnrealScriptContext context) { }
        public void OnTick(float deltaSeconds) { }
        public void OnStop() { }
    }

    [UnrealStruct("/Script/Tests.ActivationPayload", "FActivationPayload")]
    private readonly struct ActivationPayloadReorderedProperties
    {
        [UnrealStructField(0, "NativeLabel")]
        public string Label { get; }

        [UnrealStructField(1, "NativeCount")]
        public int Count { get; }

        public ActivationPayloadReorderedProperties(string label, int count)
        {
            Label = label;
            Count = count;
        }
    }
}
