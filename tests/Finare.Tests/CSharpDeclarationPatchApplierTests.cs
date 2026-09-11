using System.Text.Json;
using Finare.Core;
using Finare.CSharp;
using Finare.Graph;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Authoring")]
public sealed class CSharpDeclarationPatchApplierTests
{
    private const string Path = "Game/CombatPolicy.cs";
    private const string Source = """
        using Finare;

        namespace Game;

        public enum CombatMode
        {
            Normal,
            Critical,
        }

        [ScriptAsset("Combat/Policy")]
        public sealed class CombatPolicy
        {
            [Expose(SaveGame = true)]
            public int MaxDamage { get; set; } = 999;

            public bool Critical { get; set; } = false;

            [Visual]
            public int Clamp(int value)
            {
                return value > MaxDamage ? MaxDamage : value;
            }
        }
        """;

    [Fact]
    public void ApplyPreservesReplicationConditionThroughStateRename()
    {
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest();
        ScriptStateDeclaration state = State(original, "MaxDamage");
        var exposure = new ScriptExposure(true, false, true, "Network", Finare.ReplicationCondition.OwnerOnly);
        var patch = Patch(original, new UpdateScriptStatePatchOperation(state.Id, Expected(state),
            Spec("OwnerDamage", "int", ScriptStateTypeKind.Integer, 10, exposure)));
        var result = new CSharpDeclarationPatchApplier().Apply(request, patch);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(exposure, State(result.Analysis!, "OwnerDamage").Exposure);
        Assert.Contains("ReplicationCondition = global::Finare.ReplicationCondition.OwnerOnly",
            Assert.Single(result.PatchedRequest!.Sources).Source);

        var renamed = State(result.Analysis!, "OwnerDamage");
        var again = new CSharpDeclarationPatchApplier().Apply(result.PatchedRequest, Patch(result.Analysis!,
            new UpdateScriptStatePatchOperation(renamed.Id, Expected(renamed),
                Spec("PrivateDamage", "int", ScriptStateTypeKind.Integer, 12, renamed.Exposure))));
        Assert.True(again.Succeeded, string.Join(Environment.NewLine, again.Diagnostics));
        Assert.Equal(state.Id, State(again.Analysis!, "PrivateDamage").Id);
        Assert.Equal(exposure, State(again.Analysis!, "PrivateDamage").Exposure);
    }

    [Fact]
    public void ApplyResolvesInheritedStateAndFunctionByTheirSourceOwners()
    {
        const string source = """
            using Finare;

            namespace Game;

            [ScriptAsset("Game/BasePolicy")]
            public class BasePolicy
            {
                public int Seed { get; set; } = 3;

                [Visual]
                public int ReadSeed()
                {
                    return Seed;
                }

                [Visual]
                public int ReadSeedCaller()
                {
                    return ReadSeed();
                }
            }

            [ScriptAsset("Game/LeafPolicy")]
            public sealed class LeafPolicy : BasePolicy
            {
                public int LeafValue { get; set; } = 4;

                [Visual]
                public int LeafRead()
                {
                    return LeafValue;
                }
            }
            """;
        CSharpCompilationRequest initialRequest = new(
            "InheritedDeclarationPatch",
            [new CSharpSourceDocument(
                "Game/InheritedDeclarationPatch.cs",
                source,
                IdentitySidecarDocument.CreateEmpty("Game/InheritedDeclarationPatch.cs"))]);
        CSharpAnalysisResult initial = new CSharpSemanticPipeline().Analyze(initialRequest);
        IdentitySidecarDocument sidecar = Assert.Single(initial.IdentitySidecars);
        CSharpCompilationRequest request = new(
            "InheritedDeclarationPatch",
            [new CSharpSourceDocument("Game/InheritedDeclarationPatch.cs", source, sidecar)]);
        CSharpAnalysisResult original = Analyze(request);
        ScriptAssetDeclaration baseAsset = original.ScriptAssets!.Assets.Single(
            static asset => asset.QualifiedName == "Game.BasePolicy");
        ScriptAssetDeclaration leafAsset = original.ScriptAssets.Assets.Single(
            static asset => asset.QualifiedName == "Game.LeafPolicy");
        ScriptStateDeclaration baseState = baseAsset.States.Single(static state => state.Name == "Seed");
        ScriptStateDeclaration leafState = leafAsset.States.Single(static state => state.Name == "Seed");
        ScriptFunctionDeclaration inheritedFunction = leafAsset.Functions.Single(
            static function => function.Name == "ReadSeed");
        Assert.Equal(baseState.Id, leafState.Id);

        DeclarationPatchDocument patch = Patch(
            original,
            new UpdateScriptStatePatchOperation(
                leafState.Id,
                Expected(baseState),
                Spec("RenamedSeed", "int", ScriptStateTypeKind.Integer, 5, baseState.Exposure)),
            new UpdateScriptFunctionPatchOperation(
                inheritedFunction.Id,
                Expected(inheritedFunction),
                new ScriptFunctionPatchSpec(
                    "ReadRenamedSeed",
                    "int",
                    IsStatic: false,
                    [])));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.All(result.Analysis!.ScriptAssets!.Assets, asset =>
        {
            Assert.Equal(
                baseState.Id,
                asset.States.Single(static state => state.Name == "RenamedSeed").Id);
            Assert.Contains(asset.Functions, static function => function.Name == "ReadRenamedSeed");
        });
        Assert.All(
            result.Analysis.ScriptAssets.Assets.SelectMany(static asset => asset.Functions)
                .Where(static function => function.Name == "ReadRenamedSeed"),
            function => Assert.Equal(inheritedFunction.Id, function.Id));
        Assert.Contains(
            "return RenamedSeed;",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "return ReadRenamedSeed();",
            Assert.Single(result.PatchedRequest.Sources).Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyUpdatesInheritedOwnerWhilePreservingDerivedConstructorOverride()
    {
        const string derivedSource = """
            using Finare;

            namespace Game;

            [ScriptAsset("Game/FollowerLeaf")]
            public sealed class FollowerLeaf : SharedBase
            {
            }

            [ScriptAsset("Game/AOverrideLeaf")]
            public sealed class OverrideLeaf : SharedBase
            {
                public OverrideLeaf()
                {
                    base.Seed = 77;
                }
            }
            """;
        const string ownerSource = """
            using Finare;

            namespace Game;

            [ScriptAsset("Game/SharedBase")]
            public partial class SharedBase
            {
            }
            """;
        const string stateSource = """
            using Finare;

            namespace Game;

            public partial class SharedBase
            {
                public int Seed { get; set; } = 3;
            }
            """;
        const string constructorSource = """
            namespace Game;

            public partial class SharedBase
            {
                public SharedBase()
                {
                    Seed = 5;
                    Seed = 3;
                }
            }
            """;
        const string derivedPath = "Game/Derived.cs";
        const string ownerPath = "Game/BaseOwner.cs";
        const string statePath = "Game/BaseState.cs";
        const string constructorPath = "Game/BaseConstructor.cs";
        CSharpCompilationRequest initialRequest = new(
            "InheritedOwnerPatch",
            [
                new CSharpSourceDocument(
                    derivedPath,
                    derivedSource,
                    IdentitySidecarDocument.CreateEmpty(derivedPath)),
                new CSharpSourceDocument(
                    ownerPath,
                    ownerSource,
                    IdentitySidecarDocument.CreateEmpty(ownerPath)),
                new CSharpSourceDocument(
                    statePath,
                    stateSource,
                    IdentitySidecarDocument.CreateEmpty(statePath)),
                new CSharpSourceDocument(
                    constructorPath,
                    constructorSource,
                    IdentitySidecarDocument.CreateEmpty(constructorPath)),
            ]);
        CSharpAnalysisResult initialized = Analyze(initialRequest);
        CSharpCompilationRequest request = new(
            "InheritedOwnerPatch",
            initialRequest.Sources.Select(source => new CSharpSourceDocument(
                source.LogicalPath,
                source.Source,
                initialized.IdentitySidecars.Single(sidecar =>
                    sidecar.SourcePath == source.LogicalPath))).ToArray());
        CSharpAnalysisResult original = Analyze(request);
        ScriptAssetDeclaration baseAsset = original.ScriptAssets!.Assets.Single(
            static asset => asset.QualifiedName == "Game.SharedBase");
        ScriptStateDeclaration baseState = baseAsset.States.Single();
        ScriptStateDeclaration followerState = original.ScriptAssets.Assets.Single(
            static asset => asset.QualifiedName == "Game.FollowerLeaf").States.Single();
        ScriptStateDeclaration overrideState = original.ScriptAssets.Assets.Single(
            static asset => asset.QualifiedName == "Game.OverrideLeaf").States.Single();
        Assert.Equal(baseState.Id, followerState.Id);
        Assert.Equal(baseState.Id, overrideState.Id);
        Assert.Equal(3, followerState.DefaultValue.GetInt32());
        Assert.Equal(77, overrideState.DefaultValue.GetInt32());
        Assert.Equal("Game/AOverrideLeaf", original.ScriptAssets.Assets[0].AssetPath);
        Assert.Equal(77, original.ScriptAssets.Assets[0].States.Single().DefaultValue.GetInt32());

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(
            request,
            Patch(
                original,
                new UpdateScriptStatePatchOperation(
                    followerState.Id,
                    Expected(baseState),
                    Spec("RenamedSeed", "int", ScriptStateTypeKind.Integer, 11, baseState.Exposure))));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        ScriptAssetDocument after = result.Analysis!.ScriptAssets!;
        Assert.Equal(
            11,
            after.Assets.Single(static asset => asset.QualifiedName == "Game.SharedBase")
                .States.Single().DefaultValue.GetInt32());
        Assert.Equal(
            11,
            after.Assets.Single(static asset => asset.QualifiedName == "Game.FollowerLeaf")
                .States.Single().DefaultValue.GetInt32());
        Assert.Equal(
            77,
            after.Assets.Single(static asset => asset.QualifiedName == "Game.OverrideLeaf")
                .States.Single().DefaultValue.GetInt32());
        Assert.All(after.Assets, asset => Assert.Equal(baseState.Id, asset.States.Single().Id));
        Assert.Contains("base.RenamedSeed = 77", result.PatchedRequest!.Sources.Single(
            static source => source.LogicalPath == derivedPath).Source, StringComparison.Ordinal);
        string patchedState = result.PatchedRequest.Sources.Single(
            static source => source.LogicalPath == statePath).Source;
        Assert.Contains("public int RenamedSeed { get; set; } = 3;", patchedState, StringComparison.Ordinal);
        string patchedConstructor = result.PatchedRequest.Sources.Single(
            static source => source.LogicalPath == constructorPath).Source;
        Assert.Contains("RenamedSeed = 5;", patchedConstructor, StringComparison.Ordinal);
        Assert.Contains("RenamedSeed = 11;", patchedConstructor, StringComparison.Ordinal);

        string[] sourceBeforeFailure = request.Sources.Select(static source => source.Source).ToArray();
        string[] sidecarsBeforeFailure = request.Sources
            .Select(source => source.IdentitySidecar is null
                ? string.Empty
                : IdentitySidecarJson.Serialize(source.IdentitySidecar))
            .ToArray();
        CSharpPatchResult invalidRemoval = new CSharpDeclarationPatchApplier().Apply(
            request,
            Patch(
                original,
                new RemoveScriptStatePatchOperation(
                    baseState.Id,
                    Expected(baseState) with { Name = "WrongSharedName" })));

        AssertFailure(invalidRemoval, DeclarationPatchDiagnosticCodes.StatePreconditionFailed);
        Assert.Equal(sourceBeforeFailure, request.Sources.Select(static source => source.Source).ToArray());
        Assert.Equal(
            sidecarsBeforeFailure,
            request.Sources.Select(source => source.IdentitySidecar is null
                ? string.Empty
                : IdentitySidecarJson.Serialize(source.IdentitySidecar)).ToArray());

        CSharpPatchResult invalidUpdate = new CSharpDeclarationPatchApplier().Apply(
            request,
            Patch(
                original,
                new UpdateScriptStatePatchOperation(
                    baseState.Id,
                    Expected(baseState) with
                    {
                        DefaultValue = JsonSerializer.SerializeToElement(999),
                    },
                    Spec("RenamedSeed", "int", ScriptStateTypeKind.Integer, 11, baseState.Exposure))));

        AssertFailure(invalidUpdate, DeclarationPatchDiagnosticCodes.StatePreconditionFailed);
        Assert.Equal(sourceBeforeFailure, request.Sources.Select(static source => source.Source).ToArray());
        Assert.Equal(
            sidecarsBeforeFailure,
            request.Sources.Select(source => source.IdentitySidecar is null
                ? string.Empty
                : IdentitySidecarJson.Serialize(source.IdentitySidecar)).ToArray());
    }

    [Fact]
    public void ApplyUpdatesMultipleDefaultsInOneConstructorAfterAnRhsLengthChange()
    {
        const string path = "Game/BatchConstructorDefaults.cs";
        const string source = """
            using Finare;

            namespace Game;

            [ScriptAsset("Game/BatchConstructorDefaults")]
            public sealed class BatchConstructorDefaults
            {
                public int First { get; set; } = 0;
                public int Second { get; set; } = 0;

                public BatchConstructorDefaults()
                {
                    First = 1;
                    Second = 2;
                }
            }
            """;
        CSharpCompilationRequest initialRequest = new(
            "BatchConstructorDefaults",
            [new CSharpSourceDocument(
                path,
                source,
                IdentitySidecarDocument.CreateEmpty(path))]);
        CSharpAnalysisResult initial = Analyze(initialRequest);
        IdentitySidecarDocument sidecar = Assert.Single(initial.IdentitySidecars);
        CSharpCompilationRequest request = new(
            "BatchConstructorDefaults",
            [new CSharpSourceDocument(path, source, sidecar)]);
        CSharpAnalysisResult original = Analyze(request);
        ScriptAssetDeclaration asset = Assert.Single(original.ScriptAssets!.Assets);
        ScriptStateDeclaration first = asset.States.Single(static state => state.Name == "First");
        ScriptStateDeclaration second = asset.States.Single(static state => state.Name == "Second");

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(
            request,
            Patch(
                original,
                new UpdateScriptStatePatchOperation(
                    first.Id,
                    Expected(first),
                    Spec("First", "int", ScriptStateTypeKind.Integer, 1000, first.Exposure)),
                new UpdateScriptStatePatchOperation(
                    second.Id,
                    Expected(second),
                    Spec("RenamedSecond", "int", ScriptStateTypeKind.Integer, 22, second.Exposure))));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        ScriptAssetDeclaration patchedAsset = Assert.Single(result.Analysis!.ScriptAssets!.Assets);
        Assert.Equal(1000, patchedAsset.States.Single(static state => state.Name == "First")
            .DefaultValue.GetInt32());
        Assert.Equal(22, patchedAsset.States.Single(static state => state.Name == "RenamedSecond")
            .DefaultValue.GetInt32());
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("First = 1000;", patchedSource, StringComparison.Ordinal);
        Assert.Contains("RenamedSecond = 22;", patchedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyUpdatesContractRenamesReferencesAndPreservesStateIdentity()
    {
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest();
        ScriptStateDeclaration state = State(original, "MaxDamage");
        var exposure = new ScriptExposure(true, true, false, "Damage");
        DeclarationPatchDocument patch = Patch(
            original,
            new UpdateScriptStatePatchOperation(
                state.Id,
                Expected(state),
                Spec("DamageLimit", "int", ScriptStateTypeKind.Integer, 1200, exposure)));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string source = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("public int DamageLimit { get; set; } = 1200;", source);
        Assert.Contains("\n    public int DamageLimit", source);
        Assert.Contains("value > DamageLimit ? DamageLimit : value", source);
        Assert.Contains("InstanceEditable = true", source);
        Assert.Contains("SaveGame = true", source);
        Assert.Contains("Category = \"Damage\"", source);
        ScriptStateDeclaration changed = State(result.Analysis!, "DamageLimit");
        Assert.Equal(state.Id, changed.Id);
        Assert.Equal(1200, changed.DefaultValue.GetInt32());
        Assert.Equal(exposure, changed.Exposure);
    }

    [Fact]
    public void ApplyAddsAndRemovesStatesAsOneTransaction()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, Path);
        CSharpAnalysisResult original = Analyze(request);
        ScriptAssetDeclaration asset = Assert.Single(original.ScriptAssets!.Assets);
        ScriptStateDeclaration removed = State(original, "Critical");
        DeclarationPatchDocument patch = Patch(
            original,
            new AddScriptStatePatchOperation(
                asset.Id,
                Spec(
                    "Armor",
                    "float",
                    ScriptStateTypeKind.FloatingPoint,
                    2.5,
                    new ScriptExposure(true, false, false, "Defense"))),
            new RemoveScriptStatePatchOperation(removed.Id, Expected(removed)));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string source = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("public float Armor { get; set; } = 2.5f;", source);
        Assert.DoesNotContain("public bool Critical", source);
        Assert.Equal(["Armor", "MaxDamage"], Assert.Single(result.Analysis!.ScriptAssets!.Assets)
            .States.Select(static state => state.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ApplyChangesScalarTypeAndAddsEnumState()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, Path);
        CSharpAnalysisResult original = Analyze(request);
        ScriptAssetDeclaration asset = Assert.Single(original.ScriptAssets!.Assets);
        ScriptStateDeclaration critical = State(original, "Critical");
        DeclarationPatchDocument patch = Patch(
            original,
            new UpdateScriptStatePatchOperation(
                critical.Id,
                Expected(critical),
                Spec("Critical", "int", ScriptStateTypeKind.Integer, 1, ScriptExposure.None)),
            new AddScriptStatePatchOperation(
                asset.Id,
                Spec("Mode", "CombatMode", ScriptStateTypeKind.Enum, "Normal", ScriptExposure.None)));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        ScriptStateDeclaration changed = State(result.Analysis!, "Critical");
        ScriptStateDeclaration added = State(result.Analysis!, "Mode");
        Assert.Equal("System.Int32", changed.TypeMetadataName);
        Assert.Equal(1, changed.DefaultValue.GetInt32());
        Assert.Equal("Game.CombatMode", added.TypeMetadataName);
        Assert.Equal("Normal", added.DefaultValue.GetString());
        string source = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("public CombatMode Mode { get; set; } = CombatMode.Normal;", source);
    }

    [Fact]
    public void ApplyRendersTypedUnrealEnumNumericDefaultFromDeclarationPatch()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [UnrealEnum("/Script/FixtureRuntime.EFixtureMode", "EFixtureMode")]
            [UnrealValue]
            public readonly record struct FixtureMode(long Value) : IUnrealEnumValue
            {
                [UnrealEnumConstant(7)]
                public static FixtureMode Active { get; } = new(7);
            }

            [ScriptAsset("Game/TypedPatch")]
            public sealed class TypedPatch
            {
                public FixtureMode Mode { get; set; } = FixtureMode.Active;
            }
            """;
        CSharpCompilationRequest request = new(
            "TypedPatch",
            [new CSharpSourceDocument("Game/TypedPatch.cs", source)],
            [typeof(UnrealObjectScript).Assembly.Location]);
        CSharpAnalysisResult original = Analyze(request);
        ScriptAssetDeclaration asset = Assert.Single(original.ScriptAssets!.Assets);
        ScriptStateDeclaration state = Assert.Single(asset.States);
        DeclarationPatchDocument patch = Patch(
            original,
            new UpdateScriptStatePatchOperation(
                state.Id,
                Expected(state),
                Spec("Mode", "FixtureMode", ScriptStateTypeKind.Enum, "9", state.Exposure)));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "public FixtureMode Mode { get; set; } = new FixtureMode(9L);",
            Assert.Single(result.PatchedRequest!.Sources).Source);
    }

    [Fact]
    public void ApplyAddsCommonUnrealStateShapesWithOrdinaryCSharpDefaults()
    {
        var request = new CSharpCompilationRequest(
            "CommonStatePatch",
            [new CSharpSourceDocument(Path, Source)],
            [typeof(UnrealObjectScript).Assembly.Location]);
        CSharpAnalysisResult original = Analyze(request);
        ScriptAssetDeclaration asset = Assert.Single(original.ScriptAssets!.Assets);
        var vector = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["x"] = 1.5,
            ["y"] = 2.5,
            ["z"] = 3.5,
        };
        DeclarationPatchDocument patch = Patch(
            original,
            new AddScriptStatePatchOperation(asset.Id, Spec(
                "RowName",
                "global::Finare.Unreal.UnrealName",
                ScriptStateTypeKind.Name,
                "Player",
                ScriptExposure.None)),
            new AddScriptStatePatchOperation(asset.Id, Spec(
                "DisplayText",
                "global::Finare.Unreal.UnrealText",
                ScriptStateTypeKind.Text,
                "Ready",
                ScriptExposure.None)),
            new AddScriptStatePatchOperation(asset.Id, Spec<object?>(
                "Target",
                "global::Finare.Unreal.UnrealObjectHandle",
                ScriptStateTypeKind.Object,
                null,
                ScriptExposure.None)),
            new AddScriptStatePatchOperation(asset.Id, Spec<object?>(
                "ActorClass",
                "global::Finare.Unreal.UnrealClassHandle",
                ScriptStateTypeKind.Class,
                null,
                ScriptExposure.None)),
            new AddScriptStatePatchOperation(asset.Id, Spec(
                "Offset",
                "global::Finare.Unreal.UnrealVector",
                ScriptStateTypeKind.Struct,
                vector,
                ScriptExposure.None)));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string source = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("new global::Finare.Unreal.UnrealName(\"Player\")", source);
        Assert.Contains("new global::Finare.Unreal.UnrealText(\"Ready\")", source);
        Assert.Contains("UnrealObjectHandle Target { get; set; } = default;", source);
        Assert.Contains("UnrealClassHandle ActorClass { get; set; } = default;", source);
        Assert.Contains(
            "new global::Finare.Unreal.UnrealVector(1.5d, 2.5d, 3.5d)",
            source);
        Assert.Equal(5, Assert.Single(result.Analysis!.ScriptAssets!.Assets).States.Count - 2);
    }

    [Fact]
    public void ApplyUpdatesSoftReferenceDefaultAndPreservesIdentityOnRoundTrip()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Game/SoftPatch")]
            public sealed class SoftPatch
            {
                [Expose(SaveGame = true)]
                public UnrealSoftObjectHandle Mesh { get; set; } =
                    new("/Game/Props/OldMesh.OldMesh");
            }
            """;
        CSharpCompilationRequest request = new(
            "SoftPatch",
            [new CSharpSourceDocument("Game/SoftPatch.cs", source)],
            [typeof(UnrealObjectScript).Assembly.Location]);
        CSharpAnalysisResult original = Analyze(request);
        ScriptStateDeclaration state = Assert.Single(Assert.Single(original.ScriptAssets!.Assets).States);
        DeclarationPatchDocument patch = Patch(
            original,
            new UpdateScriptStatePatchOperation(
                state.Id,
                Expected(state),
                Spec(
                    "Mesh",
                    "Finare.Unreal.UnrealSoftObjectHandle",
                    ScriptStateTypeKind.SoftObject,
                    "/Game/Props/NewMesh.NewMesh",
                    state.Exposure)));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains(
            "new Finare.Unreal.UnrealSoftObjectHandle(\"/Game/Props/NewMesh.NewMesh\")",
            patchedSource,
            StringComparison.Ordinal);
        ScriptStateDeclaration changed = Assert.Single(Assert.Single(result.Analysis!.ScriptAssets!.Assets).States);
        Assert.Equal(state.Id, changed.Id);
        Assert.Equal(ScriptStateTypeKind.SoftObject, changed.TypeKind);
        Assert.Equal("/Game/Props/NewMesh.NewMesh", changed.DefaultValue.GetString());

        CSharpAnalysisResult roundTripped = Analyze(result.PatchedRequest!);
        ScriptStateDeclaration roundTrippedState =
            Assert.Single(Assert.Single(roundTripped.ScriptAssets!.Assets).States);
        Assert.Equal(state.Id, roundTrippedState.Id);
        Assert.Equal("/Game/Props/NewMesh.NewMesh", roundTrippedState.DefaultValue.GetString());
    }

    [Fact]
    public void ApplyCollectionDefaultsUsesTypedLeafEmittersAndRoundTrips()
    {
        const string source = """
            using System.Collections.Generic;
            using Finare;
            using Finare.Unreal;

            [UnrealEnum("/Script/FixtureRuntime.ECollectionMode", "ECollectionMode")]
            [UnrealValue]
            public readonly record struct CollectionMode(long Value) : IUnrealEnumValue
            {
                [UnrealEnumConstant(7)]
                public static CollectionMode Active { get; } = new(7);
            }

            [ScriptAsset("Game.CollectionPatch")]
            public sealed class CollectionPatch
            {
                public int Existing { get; set; } = 1;
            }
            """;
        CSharpCompilationRequest request = new(
            "CollectionPatch",
            [new CSharpSourceDocument("Game/CollectionPatch.cs", source)],
            [typeof(UnrealObjectScript).Assembly.Location]);
        CSharpAnalysisResult original = Analyze(request);
        ScriptAssetDeclaration asset = Assert.Single(original.ScriptAssets!.Assets);
        JsonElement points = JsonDocument.Parse("[{\"x\":1,\"y\":2,\"z\":3}]").RootElement.Clone();
        JsonElement modes = JsonDocument.Parse("[\"7\"]").RootElement.Clone();
        JsonElement assets = JsonDocument.Parse(
            "[{\"key\":\"Mesh\",\"value\":\"/Game/Props/Cube.Cube\"}]").RootElement.Clone();
        DeclarationPatchDocument patch = Patch(
            original,
            new AddScriptStatePatchOperation(asset.Id, new ScriptStatePatchSpec(
                "Points",
                "IReadOnlyList<UnrealVector>",
                ScriptStateTypeKind.Array,
                points,
                ScriptExposure.None)),
            new AddScriptStatePatchOperation(asset.Id, new ScriptStatePatchSpec(
                "Modes",
                "IReadOnlyList<CollectionMode>",
                ScriptStateTypeKind.Array,
                modes,
                ScriptExposure.None)),
            new AddScriptStatePatchOperation(asset.Id, new ScriptStatePatchSpec(
                "Assets",
                "IReadOnlyDictionary<UnrealName, UnrealSoftObjectHandle>",
                ScriptStateTypeKind.Map,
                assets,
                ScriptExposure.None)));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("new UnrealVector(1d, 2d, 3d)", patchedSource, StringComparison.Ordinal);
        Assert.Contains("new CollectionMode(7L)", patchedSource, StringComparison.Ordinal);
        Assert.Contains("new UnrealName(\"Mesh\")", patchedSource, StringComparison.Ordinal);
        Assert.Contains(
            "new UnrealSoftObjectHandle(\"/Game/Props/Cube.Cube\")",
            patchedSource,
            StringComparison.Ordinal);
        CSharpAnalysisResult roundTripped = Analyze(result.PatchedRequest!);
        IReadOnlyDictionary<string, ScriptStateDeclaration> states =
            Assert.Single(roundTripped.ScriptAssets!.Assets).States.ToDictionary(
                static state => state.Name,
                StringComparer.Ordinal);
        Assert.Equal(3, states["Points"].DefaultValue[0].GetProperty("z").GetDouble());
        Assert.Equal("7", states["Modes"].DefaultValue[0].GetString());
        Assert.Equal("/Game/Props/Cube.Cube",
            states["Assets"].DefaultValue[0].GetProperty("value").GetString());
    }

    [Fact]
    public void ApplyCollectionUpdateResolvesUsingAliasAndPreservesStateIdentity()
    {
        const string source = """
            using System.Collections.Generic;
            using Finare;
            using Finare.Unreal;
            using PointsAlias = System.Collections.Generic.IReadOnlyList<Finare.Unreal.UnrealVector>;

            [ScriptAsset("Game.CollectionAlias")]
            public sealed class CollectionAlias
            {
                public PointsAlias Points { get; set; } =
                    new List<UnrealVector> { new(1, 2, 3) };
            }
            """;
        CSharpCompilationRequest request = new(
            "CollectionAlias",
            [new CSharpSourceDocument("Game/CollectionAlias.cs", source)],
            [typeof(UnrealObjectScript).Assembly.Location]);
        CSharpAnalysisResult original = Analyze(request);
        ScriptAssetDeclaration asset = Assert.Single(original.ScriptAssets!.Assets);
        ScriptStateDeclaration state = Assert.Single(asset.States);
        JsonElement replacement = JsonDocument.Parse("[{\"x\":4,\"y\":5,\"z\":6}]").RootElement.Clone();
        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(
            request,
            Patch(original, new UpdateScriptStatePatchOperation(
                state.Id,
                Expected(state),
                Spec("Points", "PointsAlias", ScriptStateTypeKind.Array, replacement, state.Exposure))));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(state.Id, State(result.Analysis!, "Points").Id);
        Assert.Equal(6, State(result.Analysis!, "Points").DefaultValue[0].GetProperty("z").GetInt32());
    }

    [Fact]
    public void ApplyRejectsStaleSourceConfigurationAndStatePreconditions()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, Path);
        CSharpAnalysisResult original = Analyze(request);
        ScriptStateDeclaration state = State(original, "MaxDamage");
        ScriptStatePatchSpec replacement = Spec(
            state.Name,
            "int",
            ScriptStateTypeKind.Integer,
            1000,
            state.Exposure);

        CSharpPatchResult staleSource = new CSharpDeclarationPatchApplier().Apply(
            request,
            new DeclarationPatchDocument(
                DeclarationPatchDocument.CurrentSchemaVersion,
                SemanticId.FromKey("stale"),
                original.ScriptAssets!.SemanticConfigurationId,
                [new UpdateScriptStatePatchOperation(state.Id, Expected(state), replacement)]));
        CSharpPatchResult staleConfiguration = new CSharpDeclarationPatchApplier().Apply(
            request,
            new DeclarationPatchDocument(
                DeclarationPatchDocument.CurrentSchemaVersion,
                original.ScriptAssets!.SourceCompilationId,
                SemanticId.FromKey("stale"),
                [new UpdateScriptStatePatchOperation(state.Id, Expected(state), replacement)]));
        CSharpPatchResult staleState = new CSharpDeclarationPatchApplier().Apply(
            request,
            Patch(
                original,
                new UpdateScriptStatePatchOperation(
                    state.Id,
                    Expected(state) with { DefaultValue = JsonSerializer.SerializeToElement(1) },
                    replacement)));

        AssertFailure(staleSource, DeclarationPatchDiagnosticCodes.SourceConflict);
        AssertFailure(staleConfiguration, DeclarationPatchDiagnosticCodes.ConfigurationConflict);
        AssertFailure(staleState, DeclarationPatchDiagnosticCodes.StatePreconditionFailed);
    }

    [Fact]
    public void ApplyRejectsRenameWithoutPersistentIdentity()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, Path);
        CSharpAnalysisResult original = Analyze(request);
        ScriptStateDeclaration state = State(original, "MaxDamage");
        DeclarationPatchDocument patch = Patch(
            original,
            new UpdateScriptStatePatchOperation(
                state.Id,
                Expected(state),
                Spec("Limit", "int", state.TypeKind, 999, state.Exposure)));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        AssertFailure(result, DeclarationPatchDiagnosticCodes.PersistentIdentityRequired);
    }

    [Fact]
    public void ApplyRollsBackWhenReplacementDoesNotCompile()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, Path);
        CSharpAnalysisResult original = Analyze(request);
        ScriptStateDeclaration state = State(original, "MaxDamage");
        DeclarationPatchDocument patch = Patch(
            original,
            new UpdateScriptStatePatchOperation(
                state.Id,
                Expected(state),
                Spec("MaxDamage", "MissingType", ScriptStateTypeKind.Integer, 10, state.Exposure)));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code.StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(Source, Assert.Single(request.Sources).Source);
    }

    [Fact]
    public void ApplyReturnsCompilationFailureForCollidingRenamedState()
    {
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest();
        ScriptStateDeclaration state = State(original, "MaxDamage");
        DeclarationPatchDocument patch = Patch(
            original,
            new UpdateScriptStatePatchOperation(
                state.Id,
                Expected(state),
                Spec("Critical", "int", state.TypeKind, 999, state.Exposure)));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == DeclarationPatchDiagnosticCodes.InvalidReplacement);
    }

    [Fact]
    public void ApplyAddsRoundTrippableUnrealEventEntryMethod()
    {
        const string eventSource = """
            using Finare;
            using Finare.Unreal;

            namespace Game;

            public sealed class FixtureComponent : UnrealObject
            {
                public FixtureComponent(UnrealObjectHandle handle, IUnrealInvocationBridge bridge)
                    : base(handle, bridge) { }

                public delegate void ChangedHandler(int value, UnrealObjectHandle context);

                [UnrealEventEndpoint("/Script/Fixture.FixtureComponent:OnChanged")]
                public IUnrealEventSubscription SubscribeChanged(ChangedHandler handler) =>
                    throw new System.NotImplementedException();
            }

            [ScriptAsset("Combat/EventPolicy")]
            public sealed class EventPolicy
            {
                [Visual]
                public int Preview(int value)
                {
                    return value;
                }
            }
            """;
        var request = new CSharpCompilationRequest(
            "UnrealEventDeclarationPatchTests",
            [new CSharpSourceDocument("Game/EventPolicy.cs", eventSource)],
            [typeof(UnrealObject).Assembly.Location]);
        CSharpAnalysisResult original = Analyze(request);
        ScriptAssetDeclaration asset = Assert.Single(original.ScriptAssets!.Assets);
        DeclarationPatchDocument patch = Patch(
            original,
            new AddUnrealEventHandlerPatchOperation(
                asset.Id,
                new UnrealEventHandlerPatchSpec(
                    "OnChanged",
                    "global::Game.FixtureComponent",
                    "/Script/Fixture.FixtureComponent:OnChanged",
                    "target",
                    null,
                    [
                        new("value", "int"),
                        new("context", "global::Finare.Unreal.UnrealObjectHandle"),
                    ])));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string source = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("[global::Finare.Visual]", source, StringComparison.Ordinal);
        Assert.Contains(
            "[global::Finare.Unreal.UnrealEvent(typeof(global::Game.FixtureComponent), \"/Script/Fixture.FixtureComponent:OnChanged\")]",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void OnChanged(int value, global::Finare.Unreal.UnrealObjectHandle context)",
            source,
            StringComparison.Ordinal);
        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis!.Hir!,
            result.Analysis.Semantics!,
            result.Analysis.DeclarationReferences);
        GraphNode eventEntry = Assert.Single(
            graph.Functions
                .Single(function => function.QualifiedName.EndsWith(".OnChanged", StringComparison.Ordinal))
                .Nodes,
            static node => node.Kind == "unrealEventEntry");
        Assert.Equal(
            "/Script/Fixture.FixtureComponent:OnChanged",
            eventEntry.Properties["unrealEventPath"]);
    }

    [Fact]
    public void ApplyCreatesRunnableActorScriptAssetWithStableLifecycleHooks()
    {
        const string source = """
            namespace Game;

            public static class ExistingSourceAnchor
            {
            }
            """;
        var request = new CSharpCompilationRequest(
            "CreateScriptAssetTests",
            [new CSharpSourceDocument("Game/Scripts.cs", source)],
            [typeof(UnrealActorScript).Assembly.Location]);
        CSharpAnalysisResult original = Analyze(request);
        Assert.Empty(original.ScriptAssets!.Assets);
        DeclarationPatchDocument patch = Patch(
            original,
            new AddScriptAssetPatchOperation(
                "Game/Scripts.cs",
                new ScriptAssetPatchSpec(
                    "Game",
                    "PlayerBehavior",
                    "Gameplay/PlayerBehavior",
                    "global::Finare.Unreal.UnrealActorScript",
                    IncludeActorLifecycle: true)));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        ScriptAssetDeclaration asset = Assert.Single(result.Analysis!.ScriptAssets!.Assets);
        Assert.Equal("Game.PlayerBehavior", asset.QualifiedName);
        Assert.Equal("Gameplay/PlayerBehavior", asset.AssetPath);
        Assert.Equal(
            ["OnBeginPlay", "OnEndPlay", "OnTick"],
            asset.LifecycleHooks!.Select(static hook => hook.ManagedName)
                .Order(StringComparer.Ordinal));
        Assert.Empty(asset.Functions);
        string patched = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("public sealed class PlayerBehavior", patched, StringComparison.Ordinal);
        Assert.Contains("protected override void OnBeginPlay()", patched, StringComparison.Ordinal);
        Assert.Contains("protected override void OnTick(float deltaSeconds)", patched, StringComparison.Ordinal);
        Assert.Contains("protected override void OnEndPlay()", patched, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyCreatesRunnableActorComponentScriptAssetWithLifecycleHooks()
    {
        const string source = "namespace Game; public static class ExistingSourceAnchor { }";
        var request = new CSharpCompilationRequest(
            "CreateComponentScriptAssetTests",
            [new CSharpSourceDocument("Game/Scripts.cs", source)],
            [typeof(UnrealActorComponentScript).Assembly.Location]);
        CSharpAnalysisResult original = Analyze(request);
        DeclarationPatchDocument patch = Patch(
            original,
            new AddScriptAssetPatchOperation(
                "Game/Scripts.cs",
                new ScriptAssetPatchSpec(
                    "Game",
                    "MovementComponentBehavior",
                    "Gameplay/MovementComponentBehavior",
                    "global::Finare.Unreal.UnrealActorComponentScript",
                    IncludeActorLifecycle: true)));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patched = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains(
            "public sealed class MovementComponentBehavior : global::Finare.Unreal.UnrealActorComponentScript",
            patched,
            StringComparison.Ordinal);
        Assert.Contains("protected override void OnBeginPlay()", patched, StringComparison.Ordinal);
        Assert.Contains("protected override void OnTick(float deltaSeconds)", patched, StringComparison.Ordinal);
        Assert.Contains("protected override void OnEndPlay()", patched, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyAuthorsUpdatesAndRemovesVisualFunctionAndAssetPath()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, Path);
        CSharpAnalysisResult original = Analyze(request);
        ScriptAssetDeclaration asset = Assert.Single(original.ScriptAssets!.Assets);
        DeclarationPatchDocument add = Patch(
            original,
            new UpdateScriptAssetPatchOperation(
                asset.Id,
                new ScriptAssetPatchPrecondition(asset.QualifiedName, asset.AssetPath),
                "Combat/PlayerPolicy"),
            new AddScriptFunctionPatchOperation(
                asset.Id,
                new ScriptFunctionPatchSpec(
                    "ComputeArmor",
                    "int",
                    IsStatic: false,
                    [new ScriptFunctionParameterPatchSpec("bonus", "int")])));

        CSharpPatchResult added = new CSharpDeclarationPatchApplier().Apply(request, add);

        Assert.True(added.Succeeded, string.Join(Environment.NewLine, added.Diagnostics));
        ScriptAssetDeclaration changedAsset = Assert.Single(added.Analysis!.ScriptAssets!.Assets);
        Assert.Equal("Combat/PlayerPolicy", changedAsset.AssetPath);
        ScriptFunctionDeclaration function = changedAsset.Functions.Single(
            static candidate => candidate.Name == "ComputeArmor");
        Assert.Contains("return 0;", Assert.Single(added.PatchedRequest!.Sources).Source);

        DeclarationPatchDocument update = Patch(
            added.Analysis,
            new UpdateScriptFunctionPatchOperation(
                function.Id,
                Expected(function),
                new ScriptFunctionPatchSpec(
                    "ResolveArmor",
                    "float",
                    IsStatic: true,
                    [new ScriptFunctionParameterPatchSpec("amount", "float")])));
        CSharpPatchResult updated = new CSharpDeclarationPatchApplier().Apply(
            added.PatchedRequest,
            update);

        Assert.True(updated.Succeeded, string.Join(Environment.NewLine, updated.Diagnostics));
        ScriptFunctionDeclaration renamed = Assert.Single(
            Assert.Single(updated.Analysis!.ScriptAssets!.Assets).Functions,
            static candidate => candidate.Name == "ResolveArmor");
        Assert.True(renamed.IsStatic);
        Assert.Equal("System.Single", renamed.ReturnTypeMetadataName);
        Assert.Equal("amount", Assert.Single(renamed.Parameters).Name);

        DeclarationPatchDocument remove = Patch(
            updated.Analysis,
            new RemoveScriptFunctionPatchOperation(renamed.Id, Expected(renamed)));
        CSharpPatchResult removed = new CSharpDeclarationPatchApplier().Apply(
            updated.PatchedRequest!,
            remove);

        Assert.True(removed.Succeeded, string.Join(Environment.NewLine, removed.Diagnostics));
        Assert.DoesNotContain(
            Assert.Single(removed.Analysis!.ScriptAssets!.Assets).Functions,
            static candidate => candidate.Name == "ResolveArmor");
    }

    private static (CSharpCompilationRequest Request, CSharpAnalysisResult Analysis) CreatePersistentRequest()
    {
        var initialRequest = new CSharpCompilationRequest(
            "DeclarationPatchTests",
            [new CSharpSourceDocument(Path, Source, IdentitySidecarDocument.CreateEmpty(Path))]);
        CSharpAnalysisResult initial = Analyze(initialRequest);
        IdentitySidecarDocument sidecar = Assert.Single(initial.IdentitySidecars);
        var request = new CSharpCompilationRequest(
            "DeclarationPatchTests",
            [new CSharpSourceDocument(Path, Source, sidecar)]);
        return (request, Analyze(request));
    }

    private static CSharpAnalysisResult Analyze(CSharpCompilationRequest request)
    {
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        return result;
    }

    private static ScriptStateDeclaration State(CSharpAnalysisResult result, string name) =>
        Assert.Single(result.ScriptAssets!.Assets).States.Single(state => state.Name == name);

    private static DeclarationPatchDocument Patch(
        CSharpAnalysisResult result,
        params DeclarationPatchOperation[] operations) => DeclarationPatchDocument.Create(
        result.ScriptAssets!.SourceCompilationId,
        result.ScriptAssets.SemanticConfigurationId,
        operations);

    private static ScriptStatePatchPrecondition Expected(ScriptStateDeclaration state) => new(
        state.Name,
        state.TypeMetadataName,
        state.TypeKind,
        state.DefaultValue,
        state.Exposure);

    private static ScriptFunctionPatchPrecondition Expected(
        ScriptFunctionDeclaration function) => new(
        function.Name,
        function.ReturnTypeMetadataName,
        function.IsStatic,
        function.Parameters.Select(static parameter =>
            new ScriptFunctionParameterPatchPrecondition(
                parameter.Name,
                parameter.TypeMetadataName)).ToArray());

    private static ScriptStatePatchSpec Spec<T>(
        string name,
        string typeName,
        ScriptStateTypeKind typeKind,
        T defaultValue,
        ScriptExposure exposure) => new(
        name,
        typeName,
        typeKind,
        JsonSerializer.SerializeToElement(defaultValue),
        exposure);

    private static void AssertFailure(CSharpPatchResult result, string code)
    {
        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }
}
