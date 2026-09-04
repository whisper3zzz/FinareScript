using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnifiedScript.CSharp;
using UnifiedScript.Graph;
using UnifiedScript.Semantics;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealNativeObjectGenerationTests
{
    private const string Owner = "/Native/UEUnifiedScriptRuntime.NativeCounter";
    private const string Child = "/Native/UEUnifiedScriptRuntime.NativeCounterChild";
    private static UnrealReflectionManifest Reflection() => UnrealReflectionJson.Deserialize(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "UnrealFixtures/UEUnifiedScriptRuntime.reflection.json")));
    private static UnrealApiContract Contract() => UnrealApiContractJson.Deserialize(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "UnrealFixtures/UEUnifiedScriptRuntime.api-contract.json")));

    [Fact]
    public void GeneratedObjectCallsProjectThroughVisualSemanticsWithoutBecomingUObjects()
    {
        var projection = UnrealApiContractProjector.Project(Reflection(), Contract());
        string bindings = new UnrealBindingGenerator().Generate(projection.Reflection).Source;
        const string gameplay = """
            using UnifiedScript;
            using UnifiedScript.Unreal;
            using UnifiedScript.Unreal.Generated;
            public sealed class NativeVisualGameplay
            {
                [Visual]
                public NativeCounter Create(IUnrealInvocationBridge bridge, int value)
                {
                    return NativeCounter.Create(bridge, value);
                }
                [Visual]
                public int Update(NativeCounter counter, int delta)
                {
                    NativeCounterChild child = counter.GetChild();
                    child.Value = child.Add(delta);
                    int result = counter.Scale(child.Value);
                    child.Dispose();
                    return result;
                }
                [Visual]
                public void Release(NativeCounter counter)
                {
                    counter.Dispose();
                }
            }
            """;
        var result = new CSharpSemanticPipeline().Analyze(new CSharpCompilationRequest("NativeVisual",
            [new("Bindings.cs", bindings), new("Gameplay.cs", gameplay)], [typeof(UnrealObject).Assembly.Location]));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var types = result.Semantics!.Types.Where(t => t.MetadataName == "UnifiedScript.Unreal.Generated.NativeCounter").ToArray();
        Assert.NotEmpty(types);
        Assert.All(types, type =>
        {
            Assert.True(type.IsGraphSafe);
            Assert.Equal(UnifiedTypeKind.Object, type.Kind);
        });
        var graph = new HirGraphProjector().Project(result.Hir!, result.Semantics);
        Assert.Equal(3, graph.Functions.Count);
        Assert.Contains(graph.Functions.SelectMany(f => f.Nodes), n => n.Kind == "invocation" && n.Properties["method"].Contains("NativeCounter.Create"));
        Assert.DoesNotContain("NativeCounter : global::UnifiedScript.Unreal.UnrealObject", bindings);
    }

    [Fact]
    public void OrdinaryObjectsGenerateTypedConstructorsMethodsOwnershipAndNodes()
    {
        var contract = Contract();
        string json = UnrealApiContractJson.Serialize(contract);
        Assert.Equal(json, UnrealApiContractJson.Serialize(UnrealApiContractJson.Deserialize(json)));
        var projection = UnrealApiContractProjector.Project(Reflection(), contract);
        var managed = new UnrealBindingGenerator().Generate(projection.Reflection);
        var native = new UnrealNativeThunkGenerator().Generate(projection.NativeReflection);
        Assert.Empty(managed.Diagnostics);
        Assert.Empty(native.Diagnostics);
        Compile(managed.Source);
        Assert.Contains("public NativeCounter(global::UnifiedScript.Unreal.IUnrealInvocationBridge", managed.Source);
        Assert.Contains("NativeCounter : global::UnifiedScript.Unreal.UnrealNativeObject", managed.Source);
        Assert.Contains("static FUnifiedScriptNativeObjectLibrary Library", native.Source);
        Assert.Contains("Registry.RegisterOwned", native.Source);
        Assert.Contains("Result == Status::Success || Result == Status::WrongOwner", native.Source);
        Assert.Contains("Registry.RegisterBorrowed", native.Source);
        Assert.Contains("static_cast<int32 (UE::UnifiedScript::NativeExample::FCounter::*)(int32) const>", native.Source);
        Assert.Contains("static_cast<double (UE::UnifiedScript::NativeExample::FCounter::*)(double) const>", native.Source);
        Assert.Contains("static_assert(!std::is_base_of_v<UObject", native.Source);
        var nodes = UnrealApiNodeCatalogGenerator.Generate(projection);
        var owner = Assert.Single(nodes.Types, t => t.Path == Owner);
        var constructor = Assert.Single(owner.Nodes, n => n.MemberPath == Owner + ":Create(int32)");
        Assert.Equal(UnrealNativeOperation.Constructor, constructor.NativeOperation);
        Assert.Equal(UnrealNativeReturnOwnership.Owned, constructor.ReturnOwnership);
        Assert.Contains(constructor.Pins, p => p.Type?.Kind == UnrealValueKind.InvocationContext);
        Assert.Equal(UnrealNativeReturnOwnership.Borrowed,
            Assert.Single(owner.Nodes, n => n.MemberPath == Owner + ":GetChild()").ReturnOwnership);
        var coverage = UnrealBindingCoverageReport.Create(projection, managed, native);
        var entries = coverage.Entries.Where(e => e.Path.StartsWith("/Native/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(14, entries.Length);
        Assert.All(entries, e => Assert.Equal(UnrealBindingDisposition.DirectThunk, e.Disposition));
        Assert.Equal(native.Source, new UnrealNativeThunkGenerator().Generate(projection.NativeReflection).Source);
    }

    [Fact]
    public void NativeReturnDependencyDoesNotImplicitlyExposeChildMembers()
    {
        var projection = UnrealApiContractProjector.Project(Reflection(), Contract() with
        {
            Types = [new(Owner, ["GetChild"])],
            CoverageBaseline = null,
        });
        var child = Assert.Single(projection.Reflection.Types, t => t.Path == Child);
        Assert.Empty(child.Properties);
        Assert.Empty(child.Functions);
        Assert.Equal(2, projection.Reflection.Types.Count);
        Compile(new UnrealBindingGenerator().Generate(projection.Reflection).Source);
    }

    [Fact]
    public void PolicyDenyRemovesGeneratedNativeMethodFromEveryConsumer()
    {
        var contract = Contract();
        contract = contract with { Policy = new(UnrealApiPolicyAction.Allow,
            [new(Owner + ":Clone()", UnrealApiPolicyAction.Deny, DiagnosticCode: "USUE5999", Reason: "Not shipped")]) };
        var projection = UnrealApiContractProjector.Project(Reflection(), contract);
        var managed = new UnrealBindingGenerator().Generate(projection.Reflection);
        var native = new UnrealNativeThunkGenerator().Generate(projection.NativeReflection);
        Assert.DoesNotContain("@Clone()", managed.Source);
        Assert.DoesNotContain("&UE::UnifiedScript::NativeExample::FCounter::Clone", native.Source);
        Assert.DoesNotContain(UnrealApiNodeCatalogGenerator.Generate(projection).Types.SelectMany(t => t.Nodes), n => n.MemberPath == Owner + ":Clone()");
        Assert.Equal(UnrealBindingDisposition.Rejected, Assert.Single(UnrealBindingCoverageReport.Create(projection, managed, native).Entries,
            e => e.Path == Owner + ":Clone()").Disposition);
    }

    [Theory]
    [InlineData("ownership")]
    [InlineData("static-borrow")]
    [InlineData("pure-factory")]
    [InlineData("legacy-schema")]
    [InlineData("unknown-type")]
    [InlineData("wrong-cpp-name")]
    [InlineData("unsupported-return")]
    [InlineData("fallback")]
    [InlineData("callframe")]
    public void InvalidObjectContractsFailBeforeGeneration(string change)
    {
        var contract = Contract();
        var types = contract.NativeExtensions!.Types.ToArray();
        int index = Array.FindIndex(types, t => t.Path == Owner);
        var type = types[index];
        var functions = type.Functions.ToArray();
        int childIndex = Array.FindIndex(functions, f => f.Declaration.Name == "GetChild");
        var function = functions[childIndex];
        switch (change)
        {
            case "ownership": function = function with { ReturnOwnership = UnrealNativeReturnOwnership.Value }; break;
            case "static-borrow": function = function with { Declaration = function.Declaration with { Traits = function.Declaration.Traits with { Static = true } } }; break;
            case "pure-factory": function = function with { Declaration = function.Declaration with { Traits = function.Declaration.Traits with { Pure = true } } }; break;
            case "legacy-schema": contract = contract with { NativeExtensions = contract.NativeExtensions with { SchemaVersion = UnrealNativeExtensionManifest.LegacySchemaVersion } }; break;
            case "unknown-type": function = function with { Declaration = function.Declaration with { ReturnType = function.Declaration.ReturnType with { TypePath = "/Native/Missing.Type" } } }; break;
            case "wrong-cpp-name": type = type with { NativeName = "UE::WrongCounter" }; break;
            case "unsupported-return": function = function with { ReturnOwnership = UnrealNativeReturnOwnership.Value, Declaration = function.Declaration with { ReturnType = new(UnrealValueKind.String, "FString") } }; break;
            case "fallback": case "callframe": contract = contract with { Policy = new(UnrealApiPolicyAction.Allow,
                [new(function.Declaration.Path, UnrealApiPolicyAction.Allow, change == "fallback" ? UnrealBindingRoute.ReflectionFallback : UnrealBindingRoute.CallFrame)]) }; break;
        }
        functions[childIndex] = function;
        types[index] = type with { Functions = functions };
        contract = contract with { NativeExtensions = contract.NativeExtensions with { Types = types } };
        Assert.Throws<JsonException>(() => UnrealApiContractProjector.Project(Reflection(), contract));
    }

    [Fact]
    public void KeywordTypesAndBridgeNamedParametersKeepManagedAndVisualNamesAligned()
    {
        string json = UnrealApiContractJson.Serialize(Contract()).Replace("NativeCounter", "class", StringComparison.Ordinal)
            .Replace("\"Initial\"", "\"__bridge\"", StringComparison.Ordinal);
        var projection = UnrealApiContractProjector.Project(Reflection(), UnrealApiContractJson.Deserialize(json));
        var managed = new UnrealBindingGenerator().Generate(projection.Reflection);
        Compile(managed.Source);
        Assert.Contains("class @class :", managed.Source);
        Assert.Equal("@class", Assert.Single(UnrealApiNodeCatalogGenerator.Generate(projection).Types,
            t => t.Path == "/Native/UEUnifiedScriptRuntime.class").ManagedTypeName);
    }

    [Fact]
    public void LegacyUObjectExtensionSchemaStillRoundTripsWithoutObjectMetadata()
    {
        var contract = Contract();
        contract = contract with
        {
            Types = contract.Types.Where(t => !t.Path.StartsWith("/Native/", StringComparison.Ordinal)).ToArray(),
            NativeExtensions = contract.NativeExtensions! with
            {
                SchemaVersion = UnrealNativeExtensionManifest.LegacySchemaVersion,
                Types = contract.NativeExtensions.Types.Where(t => t.Kind == UnrealReflectedTypeKind.Class).ToArray(),
            },
        };
        var json = UnrealApiContractJson.Serialize(contract);
        Assert.DoesNotContain("returnOwnership", json);
        Assert.DoesNotContain("nativeObject", json);
        Assert.Equal(json, UnrealApiContractJson.Serialize(UnrealApiContractJson.Deserialize(json)));
        Assert.NotEmpty(UnrealApiContractProjector.Project(Reflection(), contract).Reflection.Types);
    }

    private static void Compile(string source)
    {
        var compilation = new CSharpCompilationHost().Create(CSharpSyntaxTree.ParseText(source),
            [MetadataReference.CreateFromFile(typeof(UnrealObject).Assembly.Location)]);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }
}
