using System.Text.Json;
using UnifiedScript.Core;
using UnifiedScript.CSharp;
using UnifiedScript.Graph;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class CSharpDeclarationPatchApplierTests
{
    private const string Path = "Game/CombatPolicy.cs";
    private const string Source = """
        using UnifiedScript;

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
        var exposure = new ScriptExposure(true, false, true, "Network", UnifiedScript.ReplicationCondition.OwnerOnly);
        var patch = Patch(original, new UpdateScriptStatePatchOperation(state.Id, Expected(state),
            Spec("OwnerDamage", "int", ScriptStateTypeKind.Integer, 10, exposure)));
        var result = new CSharpDeclarationPatchApplier().Apply(request, patch);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(exposure, State(result.Analysis!, "OwnerDamage").Exposure);
        Assert.Contains("ReplicationCondition = global::UnifiedScript.ReplicationCondition.OwnerOnly",
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
            using UnifiedScript;
            using UnifiedScript.Unreal;

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
                "global::UnifiedScript.Unreal.UnrealName",
                ScriptStateTypeKind.Name,
                "Player",
                ScriptExposure.None)),
            new AddScriptStatePatchOperation(asset.Id, Spec(
                "DisplayText",
                "global::UnifiedScript.Unreal.UnrealText",
                ScriptStateTypeKind.Text,
                "Ready",
                ScriptExposure.None)),
            new AddScriptStatePatchOperation(asset.Id, Spec<object?>(
                "Target",
                "global::UnifiedScript.Unreal.UnrealObjectHandle",
                ScriptStateTypeKind.Object,
                null,
                ScriptExposure.None)),
            new AddScriptStatePatchOperation(asset.Id, Spec<object?>(
                "ActorClass",
                "global::UnifiedScript.Unreal.UnrealClassHandle",
                ScriptStateTypeKind.Class,
                null,
                ScriptExposure.None)),
            new AddScriptStatePatchOperation(asset.Id, Spec(
                "Offset",
                "global::UnifiedScript.Unreal.UnrealVector",
                ScriptStateTypeKind.Struct,
                vector,
                ScriptExposure.None)));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string source = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("new global::UnifiedScript.Unreal.UnrealName(\"Player\")", source);
        Assert.Contains("new global::UnifiedScript.Unreal.UnrealText(\"Ready\")", source);
        Assert.Contains("UnrealObjectHandle Target { get; set; } = default;", source);
        Assert.Contains("UnrealClassHandle ActorClass { get; set; } = default;", source);
        Assert.Contains(
            "new global::UnifiedScript.Unreal.UnrealVector(1.5d, 2.5d, 3.5d)",
            source);
        Assert.Equal(5, Assert.Single(result.Analysis!.ScriptAssets!.Assets).States.Count - 2);
    }

    [Fact]
    public void ApplyUpdatesSoftReferenceDefaultAndPreservesIdentityOnRoundTrip()
    {
        const string source = """
            using UnifiedScript;
            using UnifiedScript.Unreal;

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
                    "UnifiedScript.Unreal.UnrealSoftObjectHandle",
                    ScriptStateTypeKind.SoftObject,
                    "/Game/Props/NewMesh.NewMesh",
                    state.Exposure)));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains(
            "new UnifiedScript.Unreal.UnrealSoftObjectHandle(\"/Game/Props/NewMesh.NewMesh\")",
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
            using UnifiedScript;
            using UnifiedScript.Unreal;

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
                        new("context", "global::UnifiedScript.Unreal.UnrealObjectHandle"),
                    ])));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string source = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("[global::UnifiedScript.Visual]", source, StringComparison.Ordinal);
        Assert.Contains(
            "[global::UnifiedScript.Unreal.UnrealEvent(typeof(global::Game.FixtureComponent), \"/Script/Fixture.FixtureComponent:OnChanged\")]",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void OnChanged(int value, global::UnifiedScript.Unreal.UnrealObjectHandle context)",
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
                    "global::UnifiedScript.Unreal.UnrealActorScript",
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
                    "global::UnifiedScript.Unreal.UnrealActorComponentScript",
                    IncludeActorLifecycle: true)));

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patched = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains(
            "public sealed class MovementComponentBehavior : global::UnifiedScript.Unreal.UnrealActorComponentScript",
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
