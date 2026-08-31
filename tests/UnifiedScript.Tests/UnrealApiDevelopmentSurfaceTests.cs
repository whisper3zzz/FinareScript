using System.Text.Json;
using UnifiedScript.Cli;
using UnifiedScript.CSharp;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealApiDevelopmentSurfaceTests
{
    [Fact]
    public void SourceUseCreatesDeterministicSafeDevelopmentOverlay()
    {
        UnrealReflectionManifest reflection = LoadReflection();
        UnrealApiContract contract = CreateContract();
        CSharpSourceDocument source = Source(
            "Gameplay/AutoSurface.cs",
            """
            using UnrealApi = UnifiedScript.Tests.AutoSurface;

            namespace Gameplay;

            public sealed class AutoSurface
            {
                public void Run(UnrealApi.FixtureDerived target)
                {
                    _ = target.Spawn(1, false);
                    _ = target.EchoOuter(default);
                }
            }
            """);
        var scanner = new UnrealApiSourceDependencyScanner();

        UnrealApiDependencyRequestSet first = scanner.Scan(
            "AutoSurfaceProject",
            [source],
            reflection,
            contract);
        UnrealApiDependencyRequestSet second = scanner.Scan(
            "AutoSurfaceProject",
            [source],
            reflection,
            contract);

        Assert.Equal(
            UnrealApiDependencyRequestJson.Serialize(first),
            UnrealApiDependencyRequestJson.Serialize(second));
        Assert.Equal(2, first.Requests.Count);
        Assert.All(first.Requests, static request =>
        {
            Assert.Equal("/Script/BindingMatrix.FixtureDerived", request.SuggestedTypePath);
            Assert.Equal("generated-binding", request.SuggestedRoute);
            Assert.Single(request.Locations);
        });
        Assert.Contains(first.Requests, static request => request.MemberName == "Spawn");
        Assert.Contains(first.Requests, static request => request.MemberName == "EchoOuter");

        UnrealApiDevelopmentSurfaceResult result =
            new UnrealApiDevelopmentSurfacePlanner().Plan(reflection, first, contract);
        Assert.True(result.Applied);
        Assert.Equal(1, result.Summary.SelectedCount);
        Assert.Equal(1, result.Summary.AlreadySelectedCount);
        Assert.Equal(0, result.Summary.OpenDecisionCount);
        UnrealApiContractType fixture = Assert.Single(result.EffectiveContract.Types);
        Assert.Equal(["EchoOuter", "Spawn"], fixture.Members);
        _ = UnrealApiContractProjector.Project(reflection, result.EffectiveContract);

        string requestsJson = UnrealApiDependencyRequestJson.Serialize(first);
        string reportJson = UnrealApiDevelopmentSurfaceJson.Serialize(result);
        Assert.Equal(
            requestsJson,
            UnrealApiDependencyRequestJson.Serialize(
                UnrealApiDependencyRequestJson.Deserialize(requestsJson)));
        Assert.Equal(
            reportJson,
            UnrealApiDevelopmentSurfaceJson.Serialize(
                UnrealApiDevelopmentSurfaceJson.Deserialize(reportJson)));
        Assert.Throws<JsonException>(() => UnrealApiDependencyRequestJson.Deserialize(
            requestsJson.Replace(
                "\"projectName\"",
                "\"unknown\": true, \"projectName\"",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void ManagedK2NameResolvesToReflectedIdentityAcrossGeneratedSurfaces()
    {
        UnrealReflectionManifest reflection = AddK2Function(LoadReflection());
        UnrealApiContract contract = CreateContract();
        UnrealApiDependencyRequestSet requests = new UnrealApiSourceDependencyScanner().Scan(
            "K2NameProject",
            [Source(
                "Gameplay/K2Name.cs",
                """
                using UnrealApi = UnifiedScript.Tests.AutoSurface;

                public static class K2Name
                {
                    public static void Run(UnrealApi.FixtureDerived target) =>
                        target.GetActorLocation();
                }
                """)],
            reflection,
            contract);

        UnrealApiDependencyRequest request = Assert.Single(requests.Requests);
        Assert.Equal("GetActorLocation", request.MemberName);
        UnrealApiDevelopmentSurfaceResult result =
            new UnrealApiDevelopmentSurfacePlanner().Plan(reflection, requests, contract);

        Assert.True(result.Applied);
        UnrealApiDevelopmentSurfaceDecision decision = Assert.Single(result.Decisions);
        Assert.Equal("selected", decision.Status);
        Assert.Equal("K2_GetActorLocation", decision.Member);
        Assert.Contains(
            result.EffectiveContract.Types,
            static type => type.Path == "/Script/BindingMatrix.FixtureDerived" &&
                type.Members.Contains("K2_GetActorLocation", StringComparer.Ordinal));

        UnrealApiContractProjection projection =
            UnrealApiContractProjector.Project(reflection, result.EffectiveContract);
        UnrealBindingGenerationResult managed = new UnrealBindingGenerator().Generate(
            projection.Reflection,
            new UnrealBindingGeneratorOptions(projection.Contract.ManagedNamespace));
        Assert.Contains(" GetActorLocation()", managed.Source, StringComparison.Ordinal);
        Assert.Contains(
            "\"/Script/BindingMatrix.FixtureDerived:K2_GetActorLocation\"",
            managed.Source,
            StringComparison.Ordinal);
        UnrealApiNodeDescriptor node = Assert.Single(
            UnrealApiNodeCatalogGenerator.Generate(projection).Types
                .Single(static type => type.Path == "/Script/BindingMatrix.FixtureDerived")
                .Nodes,
            static node => node.MemberPath ==
                "/Script/BindingMatrix.FixtureDerived:K2_GetActorLocation");
        Assert.Equal("ue.call|FixtureDerived|GetActorLocation", node.Id);
        Assert.Contains(
            "TypedTarget->K2_GetActorLocation()",
            new UnrealNativeThunkGenerator().Generate(
                projection.NativeReflection,
                new UnrealNativeThunkGeneratorOptions(
                    projection.Contract.NativeHeader,
                    projection.Contract.NativeNamespace)).Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AmbiguousOrUnsupportedUseDoesNotPartiallyApplyOverlay()
    {
        UnrealReflectionManifest reflection = LoadReflection();
        UnrealApiContract contract = CreateContract();
        UnrealApiDependencyRequestSet requests = new UnrealApiSourceDependencyScanner().Scan(
            "BlockedSurfaceProject",
            [Source(
                "Gameplay/BlockedSurface.cs",
                """
                using UnrealApi = UnifiedScript.Tests.AutoSurface;

                public sealed class BlockedSurface
                {
                    public void Run(UnrealApi.FixtureDerived target)
                    {
                        _ = target.Scale(default);
                        target.DoesNotExist();
                    }
                }
                """)],
            reflection,
            contract);

        UnrealApiDevelopmentSurfaceResult result =
            new UnrealApiDevelopmentSurfacePlanner().Plan(reflection, requests, contract);

        Assert.False(result.Applied);
        Assert.Equal(2, result.Summary.OpenDecisionCount);
        Assert.Equal(
            UnrealApiContractJson.Serialize(contract),
            UnrealApiContractJson.Serialize(result.EffectiveContract));
        UnrealApiDevelopmentSurfaceDecision ambiguous = Assert.Single(
            result.Decisions,
            static decision => decision.Status == "ambiguous-member");
        Assert.Equal(["Scale(float)", "Scale(int32)"], ambiguous.Candidates);
        UnrealApiDevelopmentSurfaceDecision unsupported = Assert.Single(
            result.Decisions,
            static decision => decision.Status == "unresolved-member");
        Assert.Contains("DoesNotExist", unsupported.Message, StringComparison.Ordinal);
        Assert.Equal("USUE3102", unsupported.Code);
        Assert.Equal("error", unsupported.Severity);
        Assert.Single(unsupported.Locations);
        Assert.All(requests.Requests.SelectMany(static request => request.Locations), static location =>
        {
            Assert.Equal("Gameplay/BlockedSurface.cs", location.SourcePath);
            Assert.True(location.StartLine > 0);
            Assert.True(location.StartColumn > 0);
        });
    }

    [Fact]
    public void ScannerRequiresExplicitGeneratedNamespaceAndIgnoresGeneratedFiles()
    {
        UnrealReflectionManifest reflection = LoadReflection();
        UnrealApiContract contract = CreateContract();
        var scanner = new UnrealApiSourceDependencyScanner();

        UnrealApiDependencyRequestSet requests = scanner.Scan(
            "SafeScannerProject",
            [
                Source(
                    "Gameplay/UserType.cs",
                    """
                    public sealed class FixtureDerived
                    {
                        public void Spawn() { }
                    }

                    public static class UserCode
                    {
                        public static void Run(FixtureDerived target) => target.Spawn();
                    }
                    """),
                Source(
                    "Generated/Unreal.bindings.generated.cs",
                    """
                    // <auto-generated />
                    namespace UnifiedScript.Tests.AutoSurface;
                    public sealed class FixtureDerived { }
                    """),
            ],
            reflection,
            contract);

        Assert.Empty(requests.Requests);
    }

    [Fact]
    public void ScannerSupportsOrdinaryAndGlobalImportsWithoutCapturingUserOrReferencedTypes()
    {
        UnrealReflectionManifest reflection = AddReferencedCollisionType(LoadReflection());
        UnrealApiContract contract = CreateContract();

        UnrealApiDependencyRequestSet requests = new UnrealApiSourceDependencyScanner().Scan(
            "ImportedSurfaceProject",
            [
                Source(
                    "Gameplay/GlobalUsings.cs",
                    "global using UnifiedScript.Tests.AutoSurface;"),
                Source(
                    "Gameplay/Imported.cs",
                    """
                    namespace Gameplay;

                    public static class Imported
                    {
                        public static void Run(FixtureDerived target)
                        {
                            _ = target.Spawn();
                            var created = new FixtureDerived(default!, default!);
                            _ = created.EchoOuter(default);
                        }
                    }
                    """),
                Source(
                    "Gameplay/Shadowed.cs",
                    """
                    namespace Gameplay.Shadowed;

                    public sealed class FixtureDerived
                    {
                        public void UnsafeValue() { }
                    }

                    public static class UserCode
                    {
                        public static void Run(FixtureDerived target) => target.UnsafeValue();
                    }
                    """),
                Source(
                    "Gameplay/ReferencedType.cs",
                    """
                    using System;
                    using UnifiedScript.Tests.AutoSurface;

                    namespace Gameplay.Referenced;

                    public static class ReferencedType
                    {
                        public static int Run(Version target) => target.Major;
                    }
                    """),
            ],
            reflection,
            contract);

        Assert.Equal(["EchoOuter", "Spawn"], requests.Requests
            .Select(static request => request.MemberName)
            .Order(StringComparer.Ordinal));
        Assert.All(requests.Requests.SelectMany(static request => request.Locations),
            static location => Assert.Equal("Gameplay/Imported.cs", location.SourcePath));
    }

    [Fact]
    public void ScannerSelectsExactOverloadsFromInvocationArgumentTypes()
    {
        UnrealReflectionManifest reflection = LoadReflection();
        UnrealApiContract contract = CreateContract();
        UnrealApiDependencyRequestSet requests = new UnrealApiSourceDependencyScanner().Scan(
            "OverloadSurfaceProject",
            [Source(
                "Gameplay/Overloads.cs",
                """
                using UnifiedScript.Tests.AutoSurface;

                namespace Gameplay;

                public static class Overloads
                {
                    public static void Run(FixtureDerived target)
                    {
                        _ = target.Scale(1);
                        _ = target.Scale(1.0f);
                    }
                }
                """)],
            reflection,
            contract);

        Assert.Equal(["Scale(float)", "Scale(int32)"], requests.Requests
            .Select(static request => request.MemberName)
            .Order(StringComparer.Ordinal));
        UnrealApiDevelopmentSurfaceResult result =
            new UnrealApiDevelopmentSurfacePlanner().Plan(reflection, requests, contract);
        Assert.True(result.Applied);
        UnrealApiContractType fixture = Assert.Single(result.EffectiveContract.Types);
        Assert.Contains("Scale(float)", fixture.Members, StringComparer.Ordinal);
        Assert.Contains("Scale(int32)", fixture.Members, StringComparer.Ordinal);
    }

    [Fact]
    public void OrdinarySourceExpandsRepresentativeBlueprintSignatureMatrix()
    {
        UnrealReflectionManifest reflection = AddAutomaticSurfaceMembers(LoadReflection());
        UnrealApiContract contract = CreateContract();
        UnrealApiDependencyRequestSet requests = new UnrealApiSourceDependencyScanner().Scan(
            "SignatureMatrixProject",
            [Source(
                "Gameplay/SignatureMatrix.cs",
                """
                using UnifiedScript.Tests.AutoSurface;

                namespace Gameplay;

                public static class SignatureMatrix
                {
                    public static void Run(FixtureDerived target)
                    {
                        _ = target.ClassValue;
                        _ = target.MapValue;
                        _ = target.Mode;
                        _ = target.ObjectValue;
                        _ = target.SoftClass;
                        _ = target.SoftObject;
                        _ = target.Tags;
                        target.BatchMove(default!);
                        _ = target.EchoOuter(default);
                        int value = 1;
                        _ = target.Mutate(ref value, out int doubled);
                        _ = target.SubscribeOnChanged(changed => { });
                        _ = target.Spawn();
                        _ = FixtureDerived.WorldTime(default);
                    }
                }
                """)],
            reflection,
            contract);

        string[] expected =
        [
            "BatchMove",
            "ClassValue",
            "EchoOuter",
            "MapValue",
            "Mode",
            "Mutate",
            "ObjectValue",
            "OnChanged",
            "SoftClass",
            "SoftObject",
            "Spawn",
            "Tags",
            "WorldTime",
        ];
        Assert.Equal(expected, requests.Requests
            .Select(static request => request.MemberName)
            .Order(StringComparer.Ordinal));

        UnrealApiDevelopmentSurfaceResult result =
            new UnrealApiDevelopmentSurfacePlanner().Plan(reflection, requests, contract);
        Assert.True(result.Applied);
        Assert.Equal(0, result.Summary.OpenDecisionCount);
        UnrealApiContractType fixture = Assert.Single(result.EffectiveContract.Types);
        Assert.Equal(expected, fixture.Members);
        UnrealApiContractProjection projection =
            UnrealApiContractProjector.Project(reflection, result.EffectiveContract);
        Assert.Contains(projection.Reflection.Types,
            static type => type.Path == "/Script/BindingMatrix.OuterData");
        Assert.Contains(projection.Reflection.Types,
            static type => type.Path == "/Script/BindingMatrix.FixtureMode");
        UnrealBindingGenerationResult bindings = new UnrealBindingGenerator().Generate(
            projection.Reflection,
            new UnrealBindingGeneratorOptions(projection.Contract.ManagedNamespace));
        Assert.Contains("SubscribeOnChanged", bindings.Source, StringComparison.Ordinal);
        Assert.Contains("IReadOnlySet<int>", bindings.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void PolicyRejectedMemberReturnsSourceLinkedDiagnosticWithoutOverlay()
    {
        UnrealReflectionManifest reflection = LoadReflection();
        UnrealApiContract contract = CreateContract() with
        {
            Policy = new UnrealApiPolicy(UnrealApiPolicyAction.Deny, []),
        };
        UnrealApiDependencyRequestSet requests = new UnrealApiSourceDependencyScanner().Scan(
            "DeniedSurfaceProject",
            [Source(
                "Gameplay/DeniedSurface.cs",
                """
                using UnrealApi = UnifiedScript.Tests.AutoSurface;

                public static class DeniedSurface
                {
                    public static void Run(UnrealApi.FixtureDerived target) =>
                        target.Spawn(1, false);
                }
                """)],
            reflection,
            contract);

        UnrealApiDevelopmentSurfaceResult result =
            new UnrealApiDevelopmentSurfacePlanner().Plan(reflection, requests, contract);

        Assert.False(result.Applied);
        UnrealApiDevelopmentSurfaceDecision decision = Assert.Single(result.Decisions);
        Assert.Equal("unsupported-member", decision.Status);
        Assert.Equal("USUE3105", decision.Code);
        Assert.Equal("error", decision.Severity);
        Assert.Contains("denied", decision.Message, StringComparison.OrdinalIgnoreCase);
        UnrealApiSourceSpan location = Assert.Single(decision.Locations);
        Assert.Equal("Gameplay/DeniedSurface.cs", location.SourcePath);
        Assert.True(location.StartLine > 0);
        Assert.Equal(
            UnrealApiContractJson.Serialize(contract),
            UnrealApiContractJson.Serialize(result.EffectiveContract));
    }

    [Fact]
    public void CliExpandsProjectSourcesWithoutRequiringThemToCompileFirst()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "UnifiedScriptDevelopmentSurfaceTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string projectPath = Path.Combine(root, "Gameplay.csproj");
            string sourcePath = Path.Combine(root, "Gameplay.cs");
            string reflectionPath = Path.Combine(root, "reflection.json");
            string contractPath = Path.Combine(root, "base-contract.json");
            string requestsPath = Path.Combine(root, "api-requests.json");
            string overlayPath = Path.Combine(root, "development-contract.json");
            string reportPath = Path.Combine(root, "development-surface.json");
            File.WriteAllText(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(
                sourcePath,
                """
                using UnifiedScript.Tests.AutoSurface;

                public static class Gameplay
                {
                    public static void Run(FixtureDerived target) =>
                        target.Spawn(1, false);
                }
                """);
            File.WriteAllText(
                reflectionPath,
                UnrealReflectionJson.Serialize(LoadReflection()));
            File.WriteAllText(
                contractPath,
                UnrealApiContractJson.Serialize(CreateContract()));

            int exitCode = UnifiedScript.Cli.Program.Main(
            [
                "ue-surface-expand",
                reflectionPath,
                "--project", projectPath,
                "--base", contractPath,
                "--requests-out", requestsPath,
                "--overlay-out", overlayPath,
                "--report", reportPath,
                "--configuration", "Release",
                "--fail-on-open-decisions",
            ]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(requestsPath));
            Assert.True(File.Exists(overlayPath));
            Assert.True(File.Exists(reportPath));
            UnrealApiDevelopmentSurfaceResult report =
                UnrealApiDevelopmentSurfaceJson.Deserialize(File.ReadAllText(reportPath));
            Assert.True(report.Applied);
            Assert.Equal(1, report.Summary.SelectedCount);
            Assert.Contains(
                UnrealApiContractJson.Deserialize(File.ReadAllText(overlayPath)).Types,
                static type => type.Path == "/Script/BindingMatrix.FixtureDerived" &&
                    type.Members.Contains("Spawn", StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CSharpSourceDocument Source(string path, string source) =>
        new(path, source.ReplaceLineEndings("\n") + "\n");

    private static UnrealReflectionManifest LoadReflection() =>
        UnrealReflectionJson.Deserialize(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "UnrealBindingMatrix.reflection.json")));

    private static UnrealReflectionManifest AddK2Function(UnrealReflectionManifest manifest)
    {
        UnrealReflectedType[] types = manifest.Types.Select(type =>
        {
            if (type.Path != "/Script/BindingMatrix.FixtureDerived")
            {
                return type;
            }
            UnrealReflectedFunction template = type.Functions.Single(
                static function => function.Name == "EchoOuter");
            UnrealReflectedFunction function = template with
            {
                Path = type.Path + ":K2_GetActorLocation",
                Name = "K2_GetActorLocation",
                Parameters = [],
                Metadata = new UnrealBlueprintMetadata(
                    "Get Actor Location",
                    "Transformation",
                    "Returns the Actor location."),
            };
            return type with { Functions = [.. type.Functions, function] };
        }).ToArray();
        return UnrealReflectionJson.Deserialize(UnrealReflectionJson.Serialize(
            UnrealReflectionManifest.Create(manifest.Engine, manifest.Modules, types)));
    }

    private static UnrealReflectionManifest AddAutomaticSurfaceMembers(
        UnrealReflectionManifest manifest)
    {
        UnrealReflectedType[] types = manifest.Types.Select(type =>
        {
            if (type.Path != "/Script/BindingMatrix.FixtureDerived")
            {
                return type;
            }
            var setProperty = new UnrealReflectedProperty(
                type.Path + ":Tags",
                "Tags",
                new UnrealTypeReference(
                    UnrealValueKind.Set,
                    "TSet<int32>",
                    ElementType: new UnrealTypeReference(UnrealValueKind.Int32, "int32")),
                new UnrealPropertyAccess(true, true, true));
            var reflectedDelegate = new UnrealReflectedDelegate(
                type.Path + ":OnChanged",
                "OnChanged",
                "/Script/BindingMatrix.FixtureChanged__DelegateSignature",
                new UnrealTypeReference(UnrealValueKind.Void, "void"),
                [new UnrealReflectedParameter(
                    "Value",
                    new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                    UnrealParameterDirection.Input)],
                new UnrealDelegateTraits(
                    UnrealDelegateKind.Multicast,
                    BlueprintAssignable: true,
                    BlueprintCallable: false,
                    BlueprintAuthorityOnly: false));
            return type with
            {
                Properties = [.. type.Properties, setProperty],
                Delegates = [.. type.Delegates, reflectedDelegate],
            };
        }).ToArray();
        return UnrealReflectionJson.Deserialize(UnrealReflectionJson.Serialize(
            UnrealReflectionManifest.Create(manifest.Engine, manifest.Modules, types)));
    }

    private static UnrealReflectionManifest AddReferencedCollisionType(
        UnrealReflectionManifest manifest)
    {
        var version = new UnrealReflectedType(
            "/Script/BindingMatrix.Version",
            "Version",
            "UVersion",
            "BindingMatrix",
            UnrealReflectedTypeKind.Class,
            "/Script/CoreUObject.Object",
            new UnrealTypeTraits(Abstract: false, BlueprintType: true),
            [new UnrealReflectedProperty(
                "/Script/BindingMatrix.Version:Major",
                "Major",
                new UnrealTypeReference(UnrealValueKind.Int32, "int32"),
                new UnrealPropertyAccess(true, false, true))],
            []);
        return UnrealReflectionJson.Deserialize(UnrealReflectionJson.Serialize(
            UnrealReflectionManifest.Create(
                manifest.Engine,
                manifest.Modules,
                [.. manifest.Types, version])));
    }

    private static UnrealApiContract CreateContract() => new(
        UnrealApiContract.CurrentSchemaVersion,
        "Tests.AutoSurface",
        "UnifiedScript.Tests.AutoSurface",
        "AutoSurfaceThunks.h",
        "UE::UnifiedScript::Generated::AutoSurface",
        [new("/Script/BindingMatrix.FixtureDerived", ["EchoOuter"])]);
}
