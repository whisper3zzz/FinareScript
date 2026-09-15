using Finare.Cli;
using Finare.CSharp;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed class UnrealInterfaceSupportTests
{
    [Fact]
    public void ScalarInterfaceValuesUseTypedViewsAndGeneratedExecuteCalls()
    {
        UnrealReflectionManifest manifest = CreateManifest(includeUnsupportedShapes: false);

        UnrealBindingGenerationResult managed = new UnrealBindingGenerator().Generate(manifest);
        UnrealNativeThunkGenerationResult native = new UnrealNativeThunkGenerator().Generate(manifest);

        Assert.Empty(managed.Diagnostics);
        Assert.Empty(native.Diagnostics);
        Assert.Contains("FinareInteropContract? InterfaceValue", managed.Source, StringComparison.Ordinal);
        Assert.Contains("FinareInteropContract? EchoInterface", managed.Source, StringComparison.Ordinal);
        Assert.Contains("IsInterface = true", managed.Source, StringComparison.Ordinal);
        Assert.Contains("#include \"UObject/Interface.h\"", native.Source, StringComparison.Ordinal);
        Assert.Contains("DecodeInterface<IFinareInteropContract>", native.Source, StringComparison.Ordinal);
        Assert.Contains("EncodeInterface<IFinareInteropContract>", native.Source, StringComparison.Ordinal);
        Assert.Contains("IFinareInteropContract::Execute_Transform", native.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeInterfaceFunctionsUseTheNativeInterfaceVtable()
    {
        UnrealReflectionManifest manifest = CreateManifest(includeUnsupportedShapes: false);
        UnrealReflectedType interfaceType = manifest.Types.Single(static type => type.Traits.Interface);
        UnrealReflectedFunction nativeFunction = interfaceType.Functions.Single() with
        {
            Traits = interfaceType.Functions.Single().Traits with { NativeInterfaceCall = true },
        };
        manifest = manifest with
        {
            Types = manifest.Types.Select(type => type.Path == interfaceType.Path
                ? type with { Functions = [nativeFunction] }
                : type).ToArray(),
        };

        UnrealNativeThunkGenerationResult native = new UnrealNativeThunkGenerator().Generate(manifest);

        Assert.Empty(native.Diagnostics);
        Assert.Contains("IFinareInteropContract* NativeInterface", native.Source, StringComparison.Ordinal);
        Assert.Contains("NativeInterface->Transform(Argument0)", native.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("Execute_Transform", native.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeInterfaceCallTraitRejectsInvalidOwnersAndBindings()
    {
        UnrealReflectionManifest manifest = CreateManifest(includeUnsupportedShapes: false);
        UnrealReflectedType owner = manifest.Types.Single(static type => !type.Traits.Interface);
        UnrealReflectedFunction function = owner.Functions.Single() with
        {
            Traits = owner.Functions.Single().Traits with { NativeInterfaceCall = true },
        };
        UnrealReflectionManifest invalidOwner = manifest with
        {
            Types = manifest.Types.Select(type => type.Path == owner.Path
                ? type with { Functions = [function] }
                : type).ToArray(),
        };

        Assert.Throws<System.Text.Json.JsonException>(
            () => UnrealReflectionJson.NormalizeAndValidate(invalidOwner));

        UnrealReflectedType interfaceType = manifest.Types.Single(static type => type.Traits.Interface);
        UnrealReflectedFunction invalidStatic = interfaceType.Functions.Single() with
        {
            Traits = interfaceType.Functions.Single().Traits with
            {
                Static = true,
                NativeInterfaceCall = true,
            },
        };
        UnrealReflectionManifest invalidStaticManifest = manifest with
        {
            Types = manifest.Types.Select(type => type.Path == interfaceType.Path
                ? type with { Functions = [invalidStatic] }
                : type).ToArray(),
        };

        Assert.Throws<System.Text.Json.JsonException>(
            () => UnrealReflectionJson.NormalizeAndValidate(invalidStaticManifest));

        UnrealReflectedFunction invalidBinding = interfaceType.Functions.Single() with
        {
            Traits = interfaceType.Functions.Single().Traits with { NativeInterfaceCall = true },
            NativeBinding = new UnrealNativeFunctionBinding("FixtureExtension", false, []),
        };
        UnrealReflectionManifest invalidBindingManifest = manifest with
        {
            Types = manifest.Types.Select(type => type.Path == interfaceType.Path
                ? type with { Functions = [invalidBinding] }
                : type).ToArray(),
        };

        Assert.Throws<System.Text.Json.JsonException>(
            () => UnrealReflectionJson.NormalizeAndValidate(invalidBindingManifest));
    }

    [Fact]
    public void NativeInterfaceCallTraitRoundTripsAndDefaultsToOmitted()
    {
        UnrealReflectionManifest manifest = CreateManifest(includeUnsupportedShapes: false);
        string legacyJson = UnrealReflectionJson.Serialize(manifest);
        Assert.DoesNotContain("nativeInterfaceCall", legacyJson, StringComparison.Ordinal);

        UnrealReflectedType interfaceType = manifest.Types.Single(static type => type.Traits.Interface);
        UnrealReflectedFunction nativeFunction = interfaceType.Functions.Single() with
        {
            Traits = interfaceType.Functions.Single().Traits with { NativeInterfaceCall = true },
        };
        UnrealReflectionManifest nativeManifest = manifest with
        {
            Types = manifest.Types.Select(type => type.Path == interfaceType.Path
                ? type with { Functions = [nativeFunction] }
                : type).ToArray(),
        };

        string json = UnrealReflectionJson.Serialize(nativeManifest);
        Assert.Contains("nativeInterfaceCall", json, StringComparison.Ordinal);
        UnrealReflectionManifest roundTrip = UnrealReflectionJson.Deserialize(json);
        Assert.True(roundTrip.Types.Single(static type => type.Traits.Interface).Functions.Single()
            .Traits.NativeInterfaceCall);
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
            "TArray<TScriptInterface<IFinareInteropContract>>");
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
            "TScriptInterface<IFinareInteropContract>",
            "/Script/FixtureRuntime.FinareInteropContract",
            HeaderPath: "FinareInteropFixture.h");
        UnrealTypeReference nestedStruct = new(
            UnrealValueKind.Struct,
            "FNestedInterfaceStruct",
            nestedStructPath,
            HeaderPath: "FinareInteropFixture.h");
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
            "FinareInteropFixture.h");
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
                    HeaderPath: "FinareInteropFixture.h"),
                new UnrealPropertyAccess(true, true, true))],
            [],
            "FinareInteropFixture.h");
        const string callbackPath = "/Script/FixtureRuntime.NestedInterfaceCallback";
        UnrealTypeReference callback = new(
            UnrealValueKind.Callback,
            "FNestedInterfaceCallback",
            callbackPath,
            HeaderPath: "FinareInteropFixture.h")
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
            "Finare.Tests.Generated",
            "FinareGeneratedThunks.h",
            "UE::Finare::Generated",
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
                type.Path == "/Script/FixtureRuntime.FinareInteropContract");
        UnrealApiNodeCatalog nodes = UnrealApiNodeCatalogGenerator.Generate(projection);
        UnrealApiNodeDescriptor node = Assert.Single(
            nodes.Types.SelectMany(static type => type.Nodes),
            static item => item.MemberPath.EndsWith(":EchoInterface"));
        UnrealApiNodePin interfacePin = Assert.Single(
            node.Pins,
            static pin => pin.Name == "Value" && pin.Direction == "input");
        Assert.Equal(UnrealValueKind.Interface, interfacePin.Type!.Kind);
        Assert.Equal(
            "/Script/FixtureRuntime.FinareInteropContract",
            interfacePin.Type.TypePath);

        UnrealApiDependencyRequestSet requests = new UnrealApiSourceDependencyScanner().Scan(
            "InterfaceSource",
            [new CSharpSourceDocument(
                "Gameplay/InterfaceUse.cs",
                """
                using Finare.Unreal;
                using UnrealApi = Finare.Tests.Generated;

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
        const string interfacePath = "/Script/FixtureRuntime.FinareInteropContract";
        const string ownerPath = "/Script/FixtureRuntime.InterfaceOwner";
        UnrealTypeReference interfaceValue = new(
            UnrealValueKind.Interface,
            "TScriptInterface<IFinareInteropContract>",
            interfacePath,
            HeaderPath: "FinareInteropFixture.h");
        UnrealTypeReference int32 = new(UnrealValueKind.Int32, "int32");
        var interfaceType = new UnrealReflectedType(
            interfacePath,
            "FinareInteropContract",
            "UFinareInteropContract",
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
            "FinareInteropFixture.h");

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
                "TArray<TScriptInterface<IFinareInteropContract>>",
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
                HeaderPath: "FinareInteropFixture.h")
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
            "FinareInteropFixture.h")
        {
            Delegates = delegates,
        };
        return UnrealReflectionManifest.Create(
            new UnrealEngineIdentity(5, 8, 1, "fixture"),
            [module],
            [interfaceType, ownerType]);
    }
}
