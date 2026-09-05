using UnifiedScript.Cli;
using UnifiedScript.CSharp;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealInterfaceSupportTests
{
    [Fact]
    public void ScalarInterfaceValuesUseObjectHandlesAndGeneratedExecuteCalls()
    {
        UnrealReflectionManifest manifest = CreateManifest(includeUnsupportedShapes: false);

        UnrealBindingGenerationResult managed = new UnrealBindingGenerator().Generate(manifest);
        UnrealNativeThunkGenerationResult native = new UnrealNativeThunkGenerator().Generate(manifest);

        Assert.Empty(managed.Diagnostics);
        Assert.Empty(native.Diagnostics);
        Assert.Contains("UnrealObjectHandle InterfaceValue", managed.Source, StringComparison.Ordinal);
        Assert.Contains("UnrealObjectHandle EchoInterface", managed.Source, StringComparison.Ordinal);
        Assert.Contains("#include \"UObject/Interface.h\"", native.Source, StringComparison.Ordinal);
        Assert.Contains("DecodeInterface<IUnifiedScriptInteropContract>", native.Source, StringComparison.Ordinal);
        Assert.Contains("EncodeInterface<IUnifiedScriptInteropContract>", native.Source, StringComparison.Ordinal);
        Assert.Contains("IUnifiedScriptInteropContract::Execute_Transform", native.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedInterfaceContainersAndCallbacksRemainParseableButAreRejected()
    {
        UnrealReflectionManifest manifest = CreateManifest(includeUnsupportedShapes: true);
        string json = UnrealReflectionJson.Serialize(manifest);
        UnrealReflectionManifest normalized = UnrealReflectionJson.Deserialize(json);

        UnrealBindingGenerationResult managed = new UnrealBindingGenerator().Generate(normalized);
        UnrealNativeThunkGenerationResult native = new UnrealNativeThunkGenerator().Generate(normalized);
        UnrealBindingCoverageReport coverage = UnrealBindingCoverageReport.Create(normalized, managed, native);

        Assert.Contains(managed.Diagnostics, static item => item.MemberPath.EndsWith(":InterfaceArray"));
        Assert.Contains(managed.Diagnostics, static item => item.MemberPath.EndsWith(":UseCallback"));
        Assert.Contains(managed.Diagnostics, static item => item.MemberPath.EndsWith(":OnInterface"));
        Assert.Contains(native.Diagnostics, static item => item.MemberPath.EndsWith(":InterfaceArray"));
        Assert.Contains(native.Diagnostics, static item => item.MemberPath.EndsWith(":UseCallback"));
        Assert.All(
            coverage.Entries.Where(static entry =>
                entry.Path.EndsWith(":InterfaceArray") ||
                entry.Path.EndsWith(":UseCallback")),
            static entry => Assert.Equal(UnrealBindingDisposition.Rejected, entry.Disposition));
    }

    [Fact]
    public void InvalidInterfaceContainerShapeIsRejectedStructurally()
    {
        UnrealReflectionManifest manifest = CreateManifest(includeUnsupportedShapes: false);
        UnrealReflectedType owner = manifest.Types.Single(static type => !type.Traits.Interface);
        UnrealTypeReference malformedArray = new(
            UnrealValueKind.Array,
            "TArray<TScriptInterface<IUnifiedScriptInteropContract>>");
        UnrealReflectionManifest malformed = manifest with
        {
            Types = manifest.Types.Select(type => type.Path == owner.Path
                ? type with
                {
                    Properties =
                    [
                        new UnrealReflectedProperty(
                            $"{owner.Path}:MalformedInterfaceArray",
                            "MalformedInterfaceArray",
                            malformedArray,
                            new UnrealPropertyAccess(true, true, true)),
                    ],
                }
                : type).ToArray(),
        };

        Assert.Throws<System.Text.Json.JsonException>(
            () => UnrealReflectionJson.NormalizeAndValidate(malformed));
    }

    [Fact]
    public void InterfaceBearingStructsStayRejectedInNativeCallbackCoverage()
    {
        UnrealReflectionManifest manifest = CreateManifest(includeUnsupportedShapes: false);
        const string interfaceStructPath = "/Script/FixtureRuntime.InterfaceStruct";
        const string nestedStructPath = "/Script/FixtureRuntime.NestedInterfaceStruct";
        UnrealTypeReference interfaceValue = new(
            UnrealValueKind.Interface,
            "TScriptInterface<IUnifiedScriptInteropContract>",
            "/Script/FixtureRuntime.UnifiedScriptInteropContract",
            HeaderPath: "UnifiedScriptInteropFixture.h");
        UnrealTypeReference nestedStruct = new(
            UnrealValueKind.Struct,
            "FNestedInterfaceStruct",
            nestedStructPath,
            HeaderPath: "UnifiedScriptInteropFixture.h");
        UnrealReflectedType interfaceStruct = new(
            interfaceStructPath,
            "InterfaceStruct",
            "FInterfaceStruct",
            "FixtureRuntime",
            UnrealReflectedTypeKind.Struct,
            null,
            new UnrealTypeTraits(false, true),
            [new UnrealReflectedProperty(
                $"{interfaceStructPath}:Value",
                "Value",
                interfaceValue,
                new UnrealPropertyAccess(true, true, true))],
            [],
            "UnifiedScriptInteropFixture.h");
        UnrealReflectedType nestedInterfaceStruct = new(
            nestedStructPath,
            "NestedInterfaceStruct",
            "FNestedInterfaceStruct",
            "FixtureRuntime",
            UnrealReflectedTypeKind.Struct,
            null,
            new UnrealTypeTraits(false, true),
            [new UnrealReflectedProperty(
                $"{nestedStructPath}:Value",
                "Value",
                new UnrealTypeReference(
                    UnrealValueKind.Struct,
                    "FInterfaceStruct",
                    interfaceStructPath,
                    HeaderPath: "UnifiedScriptInteropFixture.h"),
                new UnrealPropertyAccess(true, true, true))],
            [],
            "UnifiedScriptInteropFixture.h");
        const string callbackPath = "/Script/FixtureRuntime.NestedInterfaceCallback";
        UnrealTypeReference callback = new(
            UnrealValueKind.Callback,
            "FNestedInterfaceCallback",
            callbackPath,
            HeaderPath: "UnifiedScriptInteropFixture.h")
        {
            CallbackSignature = new(
                new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                [new UnrealReflectedParameter("Value", nestedStruct, UnrealParameterDirection.Input)]),
        };
        UnrealReflectedType owner = manifest.Types.Single(static type => !type.Traits.Interface) with
        {
            Functions =
            [
                new UnrealReflectedFunction(
                    "/Script/FixtureRuntime.InterfaceOwner:UseNestedCallback",
                    "UseNestedCallback",
                    new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                    [new UnrealReflectedParameter("Callback", callback, UnrealParameterDirection.Input)],
                    new UnrealFunctionTraits(false, false, true)),
            ],
        };
        manifest = manifest with
        {
            Types = [manifest.Types.Single(static type => type.Traits.Interface), owner, interfaceStruct, nestedInterfaceStruct],
        };

        UnrealBindingGenerationResult managed = new UnrealBindingGenerator().Generate(manifest);
        UnrealNativeThunkGenerationResult native = new UnrealNativeThunkGenerator().Generate(manifest);
        UnrealBindingCoverageReport coverage = UnrealBindingCoverageReport.Create(manifest, managed, native);

        Assert.Contains(native.Diagnostics, static item => item.MemberPath.EndsWith(":UseNestedCallback"));
        UnrealBindingCoverageEntry entry = Assert.Single(
            coverage.Entries,
            static item => item.Path.EndsWith(":UseNestedCallback"));
        Assert.Equal(UnrealBindingDisposition.Rejected, entry.Disposition);
    }

    [Fact]
    public void InterfaceOwnerDoesNotClaimReflectionFallback()
    {
        UnrealReflectionManifest manifest = CreateManifest(includeUnsupportedShapes: false);
        UnrealReflectedType interfaceType = manifest.Types.Single(static type => type.Traits.Interface);
        manifest = manifest with
        {
            Types = manifest.Types.Select(type => type.Path == interfaceType.Path
                ? type with
                {
                    Functions =
                    [
                        new UnrealReflectedFunction(
                            $"{type.Path}:PrivateTransform",
                            "PrivateTransform",
                            new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                            [],
                            new UnrealFunctionTraits(false, false, true, UnrealNativeAccess.Private)),
                    ],
                }
                : type).ToArray(),
        };

        UnrealBindingGenerationResult managed = new UnrealBindingGenerator().Generate(manifest);
        UnrealNativeThunkGenerationResult native = new UnrealNativeThunkGenerator().Generate(manifest);
        UnrealBindingCoverageReport report = UnrealBindingCoverageReport.Create(manifest, managed, native);
        UnrealBindingCoverageEntry entry = Assert.Single(
            report.Entries,
            static item => item.Path.EndsWith(":PrivateTransform"));

        Assert.Equal(UnrealBindingDisposition.Rejected, entry.Disposition);
        Assert.Contains("USUE3004", entry.DiagnosticCodes);
        Assert.DoesNotContain("USUE4104", entry.DiagnosticCodes);
    }

    [Fact]
    public void ContractProjectionClosesInterfaceDependencyAndSourceScannerMatchesHandle()
    {
        UnrealReflectionManifest manifest = CreateManifest(includeUnsupportedShapes: true);
        UnrealApiContract contract = new(
            UnrealApiContract.CurrentSchemaVersion,
            "InterfaceFixture",
            "UnifiedScript.Tests.Generated",
            "UnifiedScriptGeneratedThunks.h",
            "UE::UnifiedScript::Generated",
            [new UnrealApiContractType(
                "/Script/FixtureRuntime.InterfaceOwner",
                ["EchoInterface"])])
        {
            Policy = UnrealApiPolicy.AllowSelected,
        };

        UnrealApiContractProjection projection = UnrealApiContractProjector.Project(manifest, contract);
        Assert.Contains(
            projection.SelectedReflection.Types,
            static type => type.Traits.Interface &&
                type.Path == "/Script/FixtureRuntime.UnifiedScriptInteropContract");
        UnrealApiNodeCatalog nodes = UnrealApiNodeCatalogGenerator.Generate(projection);
        UnrealApiNodeDescriptor node = Assert.Single(
            nodes.Types.SelectMany(static type => type.Nodes),
            static item => item.MemberPath.EndsWith(":EchoInterface"));
        UnrealApiNodePin interfacePin = Assert.Single(
            node.Pins,
            static pin => pin.Name == "Value" && pin.Direction == "input");
        Assert.Equal(UnrealValueKind.Interface, interfacePin.Type!.Kind);
        Assert.Equal(
            "/Script/FixtureRuntime.UnifiedScriptInteropContract",
            interfacePin.Type.TypePath);

        UnrealApiDependencyRequestSet requests = new UnrealApiSourceDependencyScanner().Scan(
            "InterfaceSource",
            [new CSharpSourceDocument(
                "Gameplay/InterfaceUse.cs",
                """
                using UnifiedScript.Unreal;
                using UnrealApi = UnifiedScript.Tests.Generated;

                namespace Gameplay;

                public sealed class InterfaceUse
                {
                    public void Run(UnrealApi.InterfaceOwner target, UnrealObjectHandle handle) =>
                        _ = target.EchoInterface(handle);
                }
                """)],
            manifest,
            contract);

        UnrealApiDependencyRequest request = Assert.Single(requests.Requests);
        Assert.Equal("EchoInterface", request.MemberName);
        Assert.Equal("/Script/FixtureRuntime.InterfaceOwner", request.SuggestedTypePath);
    }

    private static UnrealReflectionManifest CreateManifest(bool includeUnsupportedShapes)
    {
        const string module = "FixtureRuntime";
        const string interfacePath = "/Script/FixtureRuntime.UnifiedScriptInteropContract";
        const string ownerPath = "/Script/FixtureRuntime.InterfaceOwner";
        UnrealTypeReference interfaceValue = new(
            UnrealValueKind.Interface,
            "TScriptInterface<IUnifiedScriptInteropContract>",
            interfacePath,
            HeaderPath: "UnifiedScriptInteropFixture.h");
        UnrealTypeReference int32 = new(UnrealValueKind.Int32, "int32");
        var interfaceType = new UnrealReflectedType(
            interfacePath,
            "UnifiedScriptInteropContract",
            "UUnifiedScriptInteropContract",
            module,
            UnrealReflectedTypeKind.Class,
            "/Script/CoreUObject.Interface",
            new UnrealTypeTraits(true, true) { Interface = true },
            [],
            [new UnrealReflectedFunction(
                $"{interfacePath}:Transform",
                "Transform",
                int32,
                [new UnrealReflectedParameter("Value", int32, UnrealParameterDirection.Input)],
                new UnrealFunctionTraits(false, false, true))],
            "UnifiedScriptInteropFixture.h");

        var properties = new List<UnrealReflectedProperty>
        {
            new(
                $"{ownerPath}:InterfaceValue",
                "InterfaceValue",
                interfaceValue,
                new UnrealPropertyAccess(true, true, true)),
        };
        var functions = new List<UnrealReflectedFunction>
        {
            new(
                $"{ownerPath}:EchoInterface",
                "EchoInterface",
                interfaceValue,
                [new UnrealReflectedParameter("Value", interfaceValue, UnrealParameterDirection.Input)],
                new UnrealFunctionTraits(false, false, true)),
        };
        var delegates = new List<UnrealReflectedDelegate>();
        if (includeUnsupportedShapes)
        {
            UnrealTypeReference interfaceArray = new(
                UnrealValueKind.Array,
                "TArray<TScriptInterface<IUnifiedScriptInteropContract>>",
                ElementType: interfaceValue);
            properties.Add(new UnrealReflectedProperty(
                $"{ownerPath}:InterfaceArray",
                "InterfaceArray",
                interfaceArray,
                new UnrealPropertyAccess(true, true, true)));
            UnrealTypeReference callback = new(
                UnrealValueKind.Callback,
                "FInterfaceCallback",
                "/Script/FixtureRuntime.InterfaceCallback",
                HeaderPath: "UnifiedScriptInteropFixture.h")
            {
                CallbackSignature = new(interfaceValue, []),
            };
            functions.Add(new UnrealReflectedFunction(
                $"{ownerPath}:UseCallback",
                "UseCallback",
                int32,
                [new UnrealReflectedParameter("Callback", callback, UnrealParameterDirection.Input)],
                new UnrealFunctionTraits(false, false, true)));
            delegates.Add(new UnrealReflectedDelegate(
                $"{ownerPath}:OnInterface",
                "OnInterface",
                "/Script/FixtureRuntime.OnInterface__DelegateSignature",
                new UnrealTypeReference(UnrealValueKind.Void, "void"),
                [new UnrealReflectedParameter("Value", interfaceValue, UnrealParameterDirection.Input)],
                new UnrealDelegateTraits(
                    UnrealDelegateKind.SingleCast,
                    BlueprintAssignable: true,
                    BlueprintCallable: true,
                    BlueprintAuthorityOnly: false)));
        }

        var ownerType = new UnrealReflectedType(
            ownerPath,
            "InterfaceOwner",
            "UInterfaceOwner",
            module,
            UnrealReflectedTypeKind.Class,
            "/Script/CoreUObject.Object",
            new UnrealTypeTraits(false, true),
            properties,
            functions,
            "UnifiedScriptInteropFixture.h")
        {
            Delegates = delegates,
        };
        return UnrealReflectionManifest.Create(
            new UnrealEngineIdentity(5, 8, 1, "fixture"),
            [module],
            [interfaceType, ownerType]);
    }
}
