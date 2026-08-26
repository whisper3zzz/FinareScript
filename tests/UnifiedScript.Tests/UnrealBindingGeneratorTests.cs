using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnifiedScript.CSharp;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealBindingGeneratorTests
{
    [Fact]
    public void GenerateProducesDeterministicCompilableManagedWrapper()
    {
        UnrealReflectionManifest manifest = CreateManifest();
        var generator = new UnrealBindingGenerator();

        UnrealBindingGenerationResult first = generator.Generate(manifest);
        UnrealBindingGenerationResult second = generator.Generate(manifest);

        Assert.Equal(first.Source, second.Source);
        Assert.Empty(first.Diagnostics);
        Assert.Contains(
            "[global::UnifiedScript.Unreal.UnrealTypeAttribute(\"/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent\")]",
            first.Source,
            StringComparison.Ordinal);
        Assert.Contains("public string ScriptAssetPath", first.Source, StringComparison.Ordinal);
        Assert.Contains(
            "get => ReadProperty<string>(0x61C4428EA10A780BUL, \"/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:ScriptAssetPath\");",
            first.Source,
            StringComparison.Ordinal);
        Assert.Contains("public int Add(int left, int right)", first.Source, StringComparison.Ordinal);
        Assert.Contains(
            "Invoke<int, int, int>(0xB324FB910D9CED26UL, \"/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:Add\", left, right);",
            first.Source,
            StringComparison.Ordinal);

        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(first.Source);
        CSharpCompilation compilation = new CSharpCompilationHost().Create(
            syntaxTree,
            [MetadataReference.CreateFromFile(typeof(UnrealObject).Assembly.Location)]);
        Diagnostic[] errors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
    }

    [Fact]
    public void MemberIdsAreStableUtf8Fnv1aValues()
    {
        Assert.Equal(
            0x61C4428EA10A780BUL,
            UnrealMemberId.FromPath(
                "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:ScriptAssetPath"));
        Assert.Equal(
            0xB324FB910D9CED26UL,
            UnrealMemberId.FromPath(
                "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:Add"));
        Assert.Throws<ArgumentException>(() => UnrealMemberId.FromPath(" "));
    }

    [Fact]
    public void GenerateEmitsOutputSignaturesAndDiagnosesUnsupportedTypes()
    {
        UnrealReflectionManifest manifest = CreateManifest();
        UnrealReflectedType type = manifest.Types[0];
        UnrealReflectedProperty unsupportedProperty = new(
            $"{type.Path}:Transform",
            "Transform",
            new UnrealTypeReference(
                UnrealValueKind.Struct,
                "FTransform",
                "/Script/FixtureRuntime.UnsupportedStruct"),
            new UnrealPropertyAccess(Readable: true, Writable: true, EditorVisible: true));
        UnrealReflectedFunction outFunction = new(
            $"{type.Path}:TryRead",
            "TryRead",
            new UnrealTypeReference(UnrealValueKind.Boolean, "bool"),
            [new UnrealReflectedParameter(
                "Value",
                new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                UnrealParameterDirection.Output)],
            new UnrealFunctionTraits(Static: false, Pure: false, Callable: true));
        manifest = manifest with
        {
            Types =
            [
                type with
                {
                    Properties = [.. type.Properties, unsupportedProperty],
                    Functions = [.. type.Functions, outFunction],
                },
            ],
        };

        UnrealBindingGenerationResult result = new UnrealBindingGenerator().Generate(manifest);

        Assert.Equal(["USUE2002"], result.Diagnostics.Select(static item => item.Code));
        Assert.DoesNotContain(" Transform", result.Source, StringComparison.Ordinal);
        Assert.Contains("public bool TryRead(out int value)", result.Source, StringComparison.Ordinal);
        Assert.Contains("UnrealParameterDirection.Output", result.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateUsesStrongManagedTypesForCoreUnrealValues()
    {
        UnrealReflectionManifest manifest = CreateManifest();
        UnrealReflectedType type = manifest.Types[0];
        manifest = manifest with
        {
            Types =
            [
                type with
                {
                    Properties =
                    [
                        .. type.Properties,
                        Property(type, "Location", UnrealValueKind.Struct, "FVector", "/Script/CoreUObject.Vector"),
                        Property(type, "Rotation", UnrealValueKind.Struct, "FRotator", "/Script/CoreUObject.Rotator"),
                        Property(type, "Transform", UnrealValueKind.Struct, "FTransform", "/Script/CoreUObject.Transform"),
                        Property(type, "Mode", UnrealValueKind.Enum, "EFixtureMode", "/Script/FixtureRuntime.EFixtureMode"),
                        Property(type, "Owner", UnrealValueKind.Object, "UObject*", "/Script/CoreUObject.Object"),
                        Property(type, "RowName", UnrealValueKind.Name, "FName"),
                        Property(type, "Label", UnrealValueKind.Text, "FText"),
                        Property(type, "PreciseCount", UnrealValueKind.UInt64, "uint64"),
                    ],
                },
            ],
        };

        UnrealBindingGenerationResult result = new UnrealBindingGenerator().Generate(manifest);

        Assert.Empty(result.Diagnostics);
        Assert.Contains("public global::UnifiedScript.Unreal.UnrealVector Location", result.Source, StringComparison.Ordinal);
        Assert.Contains("public global::UnifiedScript.Unreal.UnrealRotator Rotation", result.Source, StringComparison.Ordinal);
        Assert.Contains("public global::UnifiedScript.Unreal.UnrealTransform Transform", result.Source, StringComparison.Ordinal);
        Assert.Contains("public global::UnifiedScript.Unreal.UnrealEnumValue Mode", result.Source, StringComparison.Ordinal);
        Assert.Contains("public global::UnifiedScript.Unreal.UnrealObjectHandle Owner", result.Source, StringComparison.Ordinal);
        Assert.Contains("public global::UnifiedScript.Unreal.UnrealName RowName", result.Source, StringComparison.Ordinal);
        Assert.Contains("public global::UnifiedScript.Unreal.UnrealText Label", result.Source, StringComparison.Ordinal);
        Assert.Contains("public ulong PreciseCount", result.Source, StringComparison.Ordinal);

        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(result.Source);
        CSharpCompilation compilation = new CSharpCompilationHost().Create(
            syntaxTree,
            [MetadataReference.CreateFromFile(typeof(UnrealObject).Assembly.Location)]);
        Assert.Empty(compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void GenerateRoutesArbitraryArityThroughTypedCallFrame()
    {
        UnrealReflectionManifest manifest = CreateManifest();
        UnrealReflectedType type = manifest.Types[0];
        UnrealReflectedFunction attach = new(
            $"{type.Path}:Attach",
            "Attach",
            new UnrealTypeReference(UnrealValueKind.Boolean, "bool"),
            Enumerable.Range(0, 6)
                .Select(index => new UnrealReflectedParameter(
                    $"Argument{index}",
                    new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                    UnrealParameterDirection.Input))
                .ToArray(),
            new UnrealFunctionTraits(Static: false, Pure: false, Callable: true));
        manifest = manifest with
        {
            Types = [type with { Functions = [attach] }],
        };

        UnrealBindingGenerationResult result = new UnrealBindingGenerator().Generate(manifest);

        Assert.Empty(result.Diagnostics);
        Assert.Contains("InvokeWithOutputs(", result.Source, StringComparison.Ordinal);
        Assert.Contains("UnrealParameterDirection.Input", result.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("=> Invoke<bool>", result.Source, StringComparison.Ordinal);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(result.Source);
        CSharpCompilation compilation = new CSharpCompilationHost().Create(
            syntaxTree,
            [MetadataReference.CreateFromFile(typeof(UnrealObject).Assembly.Location)]);
        Assert.Empty(compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void GenerateEmitsStrongReflectedEventProxyAndSubscription()
    {
        UnrealReflectionManifest manifest = CreateManifest();
        UnrealReflectedType type = manifest.Types[0];
        const string eventPath =
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:OnInteropChanged";
        manifest = manifest with
        {
            Types =
            [
                type with
                {
                    Delegates =
                    [
                        new UnrealReflectedDelegate(
                            eventPath,
                            "OnInteropChanged",
                            "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:OnInteropChanged__DelegateSignature",
                            new UnrealTypeReference(UnrealValueKind.Void, "void"),
                            [
                                new UnrealReflectedParameter(
                                    "NewValue",
                                    new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                                    UnrealParameterDirection.Input),
                                new UnrealReflectedParameter(
                                    "Context",
                                    new UnrealTypeReference(
                                        UnrealValueKind.Object,
                                        "UObject*",
                                        "/Script/CoreUObject.Object"),
                                    UnrealParameterDirection.Input),
                            ],
                            new UnrealDelegateTraits(
                                UnrealDelegateKind.Multicast,
                                BlueprintAssignable: true,
                                BlueprintCallable: true,
                                BlueprintAuthorityOnly: false)),
                    ],
                },
            ],
        };

        UnrealBindingGenerationResult result = new UnrealBindingGenerator().Generate(manifest);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(
            "public delegate void OnInteropChangedHandler(int newValue, global::UnifiedScript.Unreal.UnrealObjectHandle context);",
            result.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "public static global::UnifiedScript.Unreal.UnrealEventDescriptor OnInteropChangedEvent",
            result.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            $"0x{UnrealMemberId.FromPath(eventPath):X16}UL",
            result.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            $"[global::UnifiedScript.Unreal.UnrealEventEndpointAttribute(\"{eventPath}\")]",
            result.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "public global::UnifiedScript.Unreal.IUnrealEventSubscription SubscribeOnInteropChanged(OnInteropChangedHandler handler)",
            result.Source,
            StringComparison.Ordinal);

        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(result.Source);
        CSharpCompilation compilation = new CSharpCompilationHost().Create(
            syntaxTree,
            [MetadataReference.CreateFromFile(typeof(UnrealObject).Assembly.Location)]);
        Assert.Empty(compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void ManagedWrapperRequiresLiveHandleAndBridge()
    {
        Assert.Throws<ArgumentException>(() => new FixtureWrapper(
            new UnrealObjectHandle(0),
            new FixtureBridge()));
        Assert.Throws<ArgumentNullException>(() => new FixtureWrapper(
            UnrealObjectHandle.Create(slotIndex: 0, generation: 1),
            null!));
    }

    [Fact]
    public void ManagedHandleMatchesNativeSlotGenerationEncoding()
    {
        UnrealObjectHandle handle = UnrealObjectHandle.Create(slotIndex: 7, generation: 42);

        Assert.Equal(0x0000002A00000008UL, handle.Value);
        Assert.True(handle.IsWellFormed);
        Assert.False(handle.IsNull);
        Assert.Equal(7U, handle.SlotIndex);
        Assert.Equal(42U, handle.Generation);

        Assert.False(default(UnrealObjectHandle).IsWellFormed);
        Assert.False(new UnrealObjectHandle(1).IsWellFormed);
        Assert.Throws<InvalidOperationException>(() => new UnrealObjectHandle(1).SlotIndex);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            UnrealObjectHandle.Create(uint.MaxValue, generation: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            UnrealObjectHandle.Create(slotIndex: 0, generation: 0));
    }

    [Fact]
    public void CheckedInFixtureMatchesDeterministicGeneratorOutput()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "UnrealFixtures");
        UnrealReflectionManifest manifest = UnrealReflectionJson.Deserialize(File.ReadAllText(
            Path.Combine(fixtureDirectory, "UEUnifiedScriptRuntime.reflection.json")));
        UnrealApiContract contract = UnrealApiContractJson.Deserialize(File.ReadAllText(
            Path.Combine(fixtureDirectory, "UEUnifiedScriptRuntime.api-contract.json")));
        UnrealApiContractProjection projection = UnrealApiContractProjector.Project(
            manifest,
            contract);
        string expected = File.ReadAllText(
            Path.Combine(fixtureDirectory, "UEUnifiedScriptRuntime.bindings.cs"));

        UnrealBindingGenerationResult generated = new UnrealBindingGenerator().Generate(
            projection.Reflection,
            new UnrealBindingGeneratorOptions(projection.Contract.ManagedNamespace));

        Assert.Empty(generated.Diagnostics);
        Assert.Equal(expected.ReplaceLineEndings("\n"), generated.Source);
        Assert.Contains("public string ScriptAssemblyPath", generated.Source, StringComparison.Ordinal);
        Assert.Contains("public string ScriptTypeName", generated.Source, StringComparison.Ordinal);
        Assert.Contains("public bool bScriptRunning", generated.Source, StringComparison.Ordinal);
        Assert.Contains("public bool StartScript()", generated.Source, StringComparison.Ordinal);
        Assert.Contains("public bool ReloadScript()", generated.Source, StringComparison.Ordinal);
        Assert.Contains("public void StopScript()", generated.Source, StringComparison.Ordinal);
    }

    private static UnrealReflectionManifest CreateManifest()
    {
        const string typePath = "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent";
        return UnrealReflectionManifest.Create(
            new UnrealEngineIdentity(5, 7, 4, "UE5"),
            ["UEUnifiedScriptRuntime"],
            [new UnrealReflectedType(
                typePath,
                "UnifiedScriptComponent",
                "UUnifiedScriptComponent",
                "UEUnifiedScriptRuntime",
                UnrealReflectedTypeKind.Class,
                "/Script/Engine.ActorComponent",
                new UnrealTypeTraits(Abstract: false, BlueprintType: true),
                [new UnrealReflectedProperty(
                    $"{typePath}:ScriptAssetPath",
                    "ScriptAssetPath",
                    new UnrealTypeReference(UnrealValueKind.String, "FString"),
                    new UnrealPropertyAccess(Readable: true, Writable: true, EditorVisible: true))],
                [new UnrealReflectedFunction(
                    $"{typePath}:Add",
                    "Add",
                    new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                    [
                        new UnrealReflectedParameter(
                            "Left",
                            new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                            UnrealParameterDirection.Input),
                        new UnrealReflectedParameter(
                            "Right",
                            new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                            UnrealParameterDirection.Input),
                    ],
                    new UnrealFunctionTraits(Static: false, Pure: true, Callable: true))])]);
    }

    private static UnrealReflectedProperty Property(
        UnrealReflectedType owner,
        string name,
        UnrealValueKind kind,
        string nativeName,
        string? typePath = null) =>
        new(
            $"{owner.Path}:{name}",
            name,
            new UnrealTypeReference(kind, nativeName, typePath),
            new UnrealPropertyAccess(true, true, true));

    private sealed class FixtureWrapper : UnrealObject
    {
        public FixtureWrapper(UnrealObjectHandle handle, IUnrealInvocationBridge bridge)
            : base(handle, bridge)
        {
        }
    }

    private sealed class FixtureBridge : IUnrealInvocationBridge
    {
        public T ReadProperty<T>(UnrealObjectHandle target, string propertyPath) => default!;

        public void WriteProperty<T>(UnrealObjectHandle target, string propertyPath, T value)
        {
        }

        public T Invoke<T>(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<object?> arguments) => default!;

        public void Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<object?> arguments)
        {
        }
    }
}
