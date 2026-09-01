using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.CSharp;
using UnifiedScript.Graph;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class CSharpScriptAssetLowererTests
{
    private const string Source = """
        using System;
        using UnifiedScript;

        namespace Game.Combat;

        public enum DamageMode
        {
            Normal,
            Critical,
        }

        [ScriptAsset("Combat/DamagePolicy")]
        public sealed class DamagePolicy
        {
            [Expose(InstanceEditable = true, Category = "Damage")]
            public float BaseDamage { get; set; } = 20f;

            [Expose(SaveGame = true)]
            public int MaxDamage { get; set; } = 999;

            public DamageMode Mode { get; set; } = DamageMode.Normal;

            [Visual]
            public float ApplyDamage(float incomingDamage, bool critical)
            {
                float scaled = incomingDamage * BaseDamage;
                if (critical)
                {
                    scaled *= 2f;
                }

                return Math.Clamp(scaled, 0f, MaxDamage);
            }
        }
        """;

    [Fact]
    public void AnalysisLowersAssetStateExposureDefaultsAndFunctionSignature()
    {
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(Source, "Game/DamagePolicy.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        ScriptAssetDocument document = Assert.IsType<ScriptAssetDocument>(result.ScriptAssets);
        ScriptAssetDeclaration asset = Assert.Single(document.Assets);
        Assert.Equal("Game.Combat.DamagePolicy", asset.QualifiedName);
        Assert.Equal("Combat/DamagePolicy", asset.AssetPath);
        Assert.Equal(result.Hir!.Id, document.SourceCompilationId);
        Assert.Equal(result.Semantics!.ConfigurationId, document.SemanticConfigurationId);

        Assert.Equal(["BaseDamage", "MaxDamage", "Mode"], asset.States.Select(static state => state.Name));
        ScriptStateDeclaration baseDamage = asset.States.Single(static state => state.Name == "BaseDamage");
        Assert.Equal("System.Single", baseDamage.TypeMetadataName);
        Assert.Equal(ScriptStateTypeKind.FloatingPoint, baseDamage.TypeKind);
        Assert.Equal(20f, baseDamage.DefaultValue.GetSingle());
        Assert.True(baseDamage.Exposure.InstanceEditable);
        Assert.Equal("Damage", baseDamage.Exposure.Category);

        ScriptStateDeclaration maxDamage = asset.States.Single(static state => state.Name == "MaxDamage");
        Assert.True(maxDamage.Exposure.SaveGame);
        Assert.False(maxDamage.Exposure.InstanceEditable);

        ScriptStateDeclaration mode = asset.States.Single(static state => state.Name == "Mode");
        Assert.Equal(ScriptStateTypeKind.Enum, mode.TypeKind);
        Assert.Equal("Normal", mode.DefaultValue.GetString());
        Assert.Equal(ScriptExposure.None, mode.Exposure);

        ScriptFunctionDeclaration function = Assert.Single(asset.Functions);
        Assert.Equal("ApplyDamage", function.Name);
        Assert.Equal("System.Single", function.ReturnTypeMetadataName);
        Assert.True(function.IsPublic);
        Assert.False(function.IsEvent);
        Assert.Equal(
            "Game.Combat.DamagePolicy.ApplyDamage(System.Single,System.Boolean)",
            function.QualifiedName);
        Assert.Equal(["System.Single", "System.Boolean"],
            function.Parameters.Select(static parameter => parameter.TypeMetadataName));
    }

    [Fact]
    public void ParameterlessConstructorAssignmentsBecomeCanonicalStateDefaults()
    {
        const string source = """
            using UnifiedScript;

            namespace Game;

            public enum StartMode
            {
                Idle,
                Active,
            }

            [ScriptAsset("Game/ConstructorDefaults")]
            public sealed class ConstructorDefaults
            {
                [Expose(InstanceEditable = true)]
                public int Count { get; set; }

                public string Label { get; set; } = "Initializer";

                public StartMode Mode { get; set; }

                public ConstructorDefaults()
                {
                    Count = 7;
                    this.Label = "Constructor";
                    Mode = StartMode.Active;
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "Game/ConstructorDefaults.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        ScriptAssetDeclaration asset = Assert.Single(result.ScriptAssets!.Assets);
        Assert.Equal(7, asset.States.Single(static state => state.Name == "Count")
            .DefaultValue.GetInt32());
        Assert.Equal("Constructor", asset.States.Single(static state => state.Name == "Label")
            .DefaultValue.GetString());
        Assert.Equal("Active", asset.States.Single(static state => state.Name == "Mode")
            .DefaultValue.GetString());
    }

    [Fact]
    public void ConstructorDefaultBodyEditsPreserveStateIdentity()
    {
        const string source = """
            using UnifiedScript;

            [ScriptAsset("Game/ConstructorIdentity")]
            public sealed class ConstructorIdentity
            {
                public int Count { get; set; }

                public ConstructorIdentity() => Count = 7;
            }
            """;

        CSharpAnalysisResult first = new CSharpSemanticPipeline().Analyze(
            source,
            "Game/ConstructorIdentity.cs");
        CSharpAnalysisResult edited = new CSharpSemanticPipeline().Analyze(
            source.Replace("Count = 7", "Count = 11", StringComparison.Ordinal),
            "Game/ConstructorIdentity.cs");

        Assert.True(first.Succeeded, string.Join(Environment.NewLine, first.Diagnostics));
        Assert.True(edited.Succeeded, string.Join(Environment.NewLine, edited.Diagnostics));
        ScriptStateDeclaration firstState = Assert.Single(first.ScriptAssets!.Assets).States.Single();
        ScriptStateDeclaration editedState = Assert.Single(edited.ScriptAssets!.Assets).States.Single();
        Assert.Equal(firstState.Id, editedState.Id);
        Assert.Equal(7, firstState.DefaultValue.GetInt32());
        Assert.Equal(11, editedState.DefaultValue.GetInt32());
    }

    [Fact]
    public void CommonUnrealValueAndReferenceStatesLowerWithTypedDefaults()
    {
        const string source = """
            using UnifiedScript;
            using UnifiedScript.Unreal;

            [ScriptAsset("Game/CommonState")]
            public sealed class CommonState
            {
                public UnrealName RowName { get; set; } = new("Player");

                public UnrealText Label { get; set; }

                public UnrealObjectHandle Target { get; set; } = default;

                public UnrealClassHandle ActorClass { get; set; } = new();

                public UnrealVector Offset { get; set; } = UnrealVector.Zero;

                public CommonState()
                {
                    Label = new UnrealText("Ready");
                    Offset = new UnrealVector(1.5, 2.5, 3.5);
                }
            }
            """;
        var request = new CSharpCompilationRequest(
            "CommonUnrealState",
            [new CSharpSourceDocument("Game/CommonState.cs", source)],
            [typeof(UnrealObjectScript).Assembly.Location]);

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        IReadOnlyDictionary<string, ScriptStateDeclaration> states = Assert.Single(
                result.ScriptAssets!.Assets)
            .States.ToDictionary(static state => state.Name, StringComparer.Ordinal);
        Assert.Equal(ScriptStateTypeKind.Name, states["RowName"].TypeKind);
        Assert.Equal("Player", states["RowName"].DefaultValue.GetString());
        Assert.Equal(ScriptStateTypeKind.Text, states["Label"].TypeKind);
        Assert.Equal("Ready", states["Label"].DefaultValue.GetString());
        Assert.Equal(ScriptStateTypeKind.Object, states["Target"].TypeKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, states["Target"].DefaultValue.ValueKind);
        Assert.Equal(ScriptStateTypeKind.Class, states["ActorClass"].TypeKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, states["ActorClass"].DefaultValue.ValueKind);
        ScriptStateDeclaration offset = states["Offset"];
        Assert.Equal(ScriptStateTypeKind.Struct, offset.TypeKind);
        Assert.Equal(1.5, offset.DefaultValue.GetProperty("x").GetDouble());
        Assert.Equal(2.5, offset.DefaultValue.GetProperty("y").GetDouble());
        Assert.Equal(3.5, offset.DefaultValue.GetProperty("z").GetDouble());
        Assert.Equal("/Script/CoreUObject.Vector", offset.UnrealType?.UnrealTypePath);
        Assert.Equal(["X", "Y", "Z"],
            offset.UnrealType?.Fields.Select(static field => field.NativeName));
    }

    [Fact]
    public void PersistentObjectStateRejectsProcessLocalHandleDefaults()
    {
        const string source = """
            using UnifiedScript;
            using UnifiedScript.Unreal;

            [ScriptAsset("Game/InvalidHandleDefault")]
            public sealed class InvalidHandleDefault
            {
                public UnrealObjectHandle Target { get; set; } = new(1);
            }
            """;
        var request = new CSharpCompilationRequest(
            "InvalidHandleDefault",
            [new CSharpSourceDocument("Game/InvalidHandleDefault.cs", source)],
            [typeof(UnrealObjectScript).Assembly.Location]);

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == DeclarationDiagnosticCodes.StateDefaultRequired);
    }

    [Theory]
    [InlineData("if (System.DateTime.UtcNow.Ticks > 0) { Count = 7; }")]
    [InlineData("Count = System.Environment.TickCount;")]
    [InlineData("void SetDefault() { Count = 7; } SetDefault();")]
    public void NonDeterministicConstructorDefaultsAreRejected(string constructorBody)
    {
        string source = $$"""
            using UnifiedScript;

            [ScriptAsset("Game/InvalidConstructorDefault")]
            public sealed class InvalidConstructorDefault
            {
                public int Count { get; set; }

                public InvalidConstructorDefault()
                {
                    {{constructorBody}}
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            source,
            "Game/InvalidConstructorDefault.cs");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == DeclarationDiagnosticCodes.StateDefaultRequired);
        Assert.Null(result.ScriptAssets);
    }

    [Fact]
    public void GraphStateReferencesCarryDeclarationIdentity()
    {
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            Source,
            "Game/DamagePolicy.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        GraphFunction function = Assert.Single(new HirGraphProjector().Project(
            result.Hir!,
            result.Semantics!,
            result.DeclarationReferences).Functions);
        IReadOnlyDictionary<string, SemanticId> declarationIds = Assert.Single(
                result.ScriptAssets!.Assets)
            .States
            .ToDictionary(static state => state.Name, static state => state.Id, StringComparer.Ordinal);

        foreach (string name in new[] { "BaseDamage", "MaxDamage" })
        {
            SemanticId declarationId = declarationIds[name];
            GraphNode reference = Assert.Single(function.Nodes, node =>
                node.Kind == "identifier" &&
                node.Properties.GetValueOrDefault("name") == name);
            Assert.Equal(declarationId.Value, reference.Properties["declarationId"]);
        }

        Assert.DoesNotContain(function.Nodes, static node =>
            node.Properties.GetValueOrDefault("name") == "scaled" &&
            node.Properties.ContainsKey("declarationId"));

        HirExpressionStatement multiplyStatement = result.Hir!
            .DescendantsAndSelf()
            .OfType<HirExpressionStatement>()
            .Single();
        GraphNode multiplyAssignment = function.Nodes.Single(node =>
            node.Kind == "assignment" &&
            node.Properties.GetValueOrDefault("operator") == "*=");
        Assert.Equal(
            multiplyStatement.Id.Value,
            multiplyAssignment.Properties["debugTargetId"]);
    }

    [Fact]
    public void StateIdentityIsStableAcrossFormattingAndDocumentOrder()
    {
        var first = new CSharpCompilationRequest(
            "StableDeclarations",
            [
                new CSharpSourceDocument("Game/DamagePolicy.cs", Source),
                new CSharpSourceDocument("Game/Other.cs", "namespace Game; public static class Other { }")
            ]);
        var second = new CSharpCompilationRequest(
            "StableDeclarations",
            [
                new CSharpSourceDocument("Game/Other.cs", "namespace Game; public static class Other { }") ,
                new CSharpSourceDocument("Game/DamagePolicy.cs", Source.Replace(
                    "public float BaseDamage",
                    "public    float    BaseDamage",
                    StringComparison.Ordinal))
            ]);

        CSharpAnalysisResult firstResult = new CSharpSemanticPipeline().Analyze(first);
        CSharpAnalysisResult secondResult = new CSharpSemanticPipeline().Analyze(second);

        Assert.True(firstResult.Succeeded);
        Assert.True(secondResult.Succeeded);
        SemanticId firstId = Assert.Single(firstResult.ScriptAssets!.Assets)
            .States.Single(static state => state.Name == "BaseDamage").Id;
        SemanticId secondId = Assert.Single(secondResult.ScriptAssets!.Assets)
            .States.Single(static state => state.Name == "BaseDamage").Id;
        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public void IdentitySidecarPreservesStateIdAcrossRename()
    {
        const string path = "Game/DamagePolicy.cs";
        var initialRequest = new CSharpCompilationRequest(
            "PersistentDeclarations",
            [new CSharpSourceDocument(path, Source, IdentitySidecarDocument.CreateEmpty(path))]);
        CSharpAnalysisResult initial = new CSharpSemanticPipeline().Analyze(initialRequest);
        Assert.True(initial.Succeeded, string.Join(Environment.NewLine, initial.Diagnostics));
        IdentitySidecarDocument sidecar = Assert.Single(initial.IdentitySidecars);
        SemanticId initialId = Assert.Single(initial.ScriptAssets!.Assets)
            .States.Single(static state => state.Name == "BaseDamage").Id;

        string renamedSource = Source.Replace("BaseDamage", "BasePower", StringComparison.Ordinal);
        var renamedRequest = new CSharpCompilationRequest(
            "PersistentDeclarations",
            [new CSharpSourceDocument(path, renamedSource, sidecar)]);
        CSharpAnalysisResult renamed = new CSharpSemanticPipeline().Analyze(renamedRequest);

        Assert.True(renamed.Succeeded, string.Join(Environment.NewLine, renamed.Diagnostics));
        ScriptStateDeclaration renamedState = Assert.Single(renamed.ScriptAssets!.Assets)
            .States.Single(static state => state.Name == "BasePower");
        Assert.Equal(initialId, renamedState.Id);
        Assert.Equal(3, Assert.Single(renamed.IdentitySidecars).Entries.Count(static entry =>
            entry.Kind == "state"));
    }

    [Theory]
    [InlineData(
        "public int MissingDefault { get; set; }",
        DeclarationDiagnosticCodes.StateDefaultRequired)]
    [InlineData(
        "public System.Collections.Generic.List<int> Items { get; set; } = new();",
        DeclarationDiagnosticCodes.UnsupportedStateType)]
    [InlineData(
        "public int Computed => 42;",
        DeclarationDiagnosticCodes.UnsupportedStateShape)]
    [InlineData(
        "[Expose(Category = \" \" )] public int Count { get; set; } = 1;",
        DeclarationDiagnosticCodes.InvalidExposureMetadata)]
    public void InvalidStateContractsProduceStableDiagnostics(string declaration, string expectedCode)
    {
        string source = $$"""
            using UnifiedScript;
            [ScriptAsset("Tests/Invalid")]
            public sealed class InvalidAsset
            {
                {{declaration}}
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(source, "InvalidAsset.cs");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
        Assert.Null(result.ScriptAssets);
    }

    [Fact]
    public void DuplicateAssetPathsFailTheCompilationContract()
    {
        const string source = """
            using UnifiedScript;
            [ScriptAsset("Combat/Shared")]
            public sealed class First { }
            [ScriptAsset("Combat/Shared")]
            public sealed class Second { }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(source, "Duplicate.cs");

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Diagnostics.Count(static diagnostic =>
            diagnostic.Code == DeclarationDiagnosticCodes.DuplicateAssetPath));
    }

    [Fact]
    public void ExplicitScriptClassIdBecomesTheAssetSemanticIdentity()
    {
        const string source = """
            using UnifiedScript;
            [ScriptAsset("Gameplay/Door", ClassId = "us_0123456789abcdef0123456789abcdef")]
            public sealed class Door { }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(source, "Door.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(
            "us_0123456789abcdef0123456789abcdef",
            Assert.Single(result.ScriptAssets!.Assets).Id.Value);
    }

    [Fact]
    public void ReviewedUnrealProxyClassSourcesCompileAndLowerTogether()
    {
        const string source = """
            using UnifiedScript;
            using UnifiedScript.Unreal;

            namespace Game;

            [ScriptAsset("Game/Object", ClassId = "us_00000000000000000000000000000001")]
            [UnrealScriptClass(UnrealScriptClassKind.Object)]
            public sealed class ObjectBehavior : UnrealObjectScript
            {
                [Expose(InstanceEditable = true)]
                public int StartValue { get; set; } = 1;

                [Visual]
                public int AddOne(int value) { return value + 1; }

                protected override void OnInitialized() { }
                protected override void OnUpdate(float deltaSeconds) { }
            }

            [ScriptAsset("Game/Actor", ClassId = "us_00000000000000000000000000000002")]
            [UnrealScriptClass(UnrealScriptClassKind.Actor)]
            public sealed class ActorBehavior : UnrealActorScript
            {
                [UnrealBlueprintEvent]
                public void Activate(int amount) { }

                [UnrealInputAction(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInputActionFixture",
                    TriggerEvent = UnrealInputTriggerEvent.Triggered,
                    ValueType = UnrealInputValueType.Axis1D,
                    ConsumeInput = true)]
                public void Move(float value) { }

                [UnrealInterfaceFunction(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropContract",
                    "TransformManagedValue")]
                public int TransformManagedValue(int value) { return value * 2; }

                protected override void OnBeginPlay() { }
                protected override void OnTick(float deltaSeconds) { }
                protected override void OnEndPlay() { }
            }

            [ScriptAsset("Game/Component", ClassId = "us_00000000000000000000000000000003")]
            [UnrealScriptClass(UnrealScriptClassKind.ActorComponent)]
            public sealed class ComponentBehavior : UnrealActorComponentScript
            {
                [Visual]
                [UnrealEvent(
                    typeof(UnrealObject),
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:OnManagedSignal")]
                private void OnSignal(int value, UnrealObjectHandle context) { }

                protected override void OnBeginPlay() { }
                protected override void OnTick(float deltaSeconds) { }
                protected override void OnEndPlay() { }
            }

            [ScriptAsset("Game/Library", ClassId = "us_00000000000000000000000000000004")]
            [UnrealScriptClass(UnrealScriptClassKind.BlueprintFunctionLibrary)]
            public static class FunctionLibrary
            {
                [Visual]
                public static int AddOne(int value) { return value + 1; }
            }
            """;
        var request = new CSharpCompilationRequest(
            "ReviewedUnrealProxyClasses",
            [new CSharpSourceDocument("Game/ReviewedClasses.cs", source)],
            [typeof(UnrealActorScript).Assembly.Location]);

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(4, result.ScriptAssets!.Assets.Count);
        ScriptAssetDeclaration library = result.ScriptAssets.Assets.Single(
            static asset => asset.QualifiedName == "Game.FunctionLibrary");
        Assert.True(Assert.Single(library.Functions).IsStatic);
        Assert.True(Assert.Single(library.Functions).IsPublic);
        Assert.False(Assert.Single(library.Functions).IsEvent);
        Assert.Empty(library.States);
        ScriptAssetDeclaration actor = result.ScriptAssets.Assets.Single(
            static asset => asset.QualifiedName == "Game.ActorBehavior");
        ScriptFunctionDeclaration blueprintEvent = actor.Functions.Single(
            static function => function.Name == "Activate");
        Assert.Equal("Activate", blueprintEvent.Name);
        Assert.Equal(
            ScriptBlueprintEventSuperMode.Explicit,
            Assert.IsType<ScriptBlueprintEventDeclaration>(blueprintEvent.BlueprintEvent).SuperMode);
        Assert.Equal(
            [ScriptLifecycleHookKind.BeginPlay, ScriptLifecycleHookKind.Tick,
                ScriptLifecycleHookKind.EndPlay],
            actor.LifecycleHooks!.Select(static hook => hook.Kind));
        Assert.Equal(
            ["OnBeginPlay", "OnTick", "OnEndPlay"],
            actor.LifecycleHooks!.Select(static hook => hook.ManagedName));
        ScriptInputActionBindingDeclaration input = Assert.Single(
            actor.Functions.Single(static function => function.Name == "Move").InputActions!);
        Assert.Equal(ScriptInputTriggerEvent.Triggered, input.TriggerEvent);
        Assert.Equal(ScriptInputValueType.Axis1D, input.ValueType);
        Assert.True(input.ConsumeInput);
        ScriptInterfaceFunctionDeclaration interfaceFunction = Assert.Single(
            actor.Functions.Single(static function =>
                function.Name == "TransformManagedValue").InterfaceFunctions!);
        Assert.Equal(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropContract",
            interfaceFunction.InterfacePath);
        Assert.Equal("TransformManagedValue", interfaceFunction.FunctionName);
        ScriptFunctionDeclaration eventFunction = Assert.Single(
            result.ScriptAssets.Assets.Single(
                static asset => asset.QualifiedName == "Game.ComponentBehavior").Functions);
        Assert.False(eventFunction.IsPublic);
        Assert.True(eventFunction.IsEvent);
        ScriptDelegateBindingDeclaration delegateBinding = Assert.Single(
            eventFunction.DelegateBindings!);
        Assert.Equal(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent",
            delegateBinding.SourceTypePath);
        Assert.Equal(ScriptDelegateSource.Target, delegateBinding.Source);
        Assert.Equal(
            "AddOne",
            Assert.Single(result.ScriptAssets.Assets
                .Single(static asset => asset.QualifiedName == "Game.ObjectBehavior")
            .Functions).Name);
    }

    [Fact]
    public void GameplayLifecycleAndBlueprintEventIdsSurviveBodyEdits()
    {
        const string source = """
            using UnifiedScript;
            using UnifiedScript.Unreal;

            namespace Game;

            [ScriptAsset("Game/LifecycleActor", ClassId = "us_00000000000000000000000000000007")]
            [UnrealScriptClass(UnrealScriptClassKind.Actor)]
            public sealed class LifecycleActor : UnrealActorScript
            {
                [Expose]
                public int Value { get; set; } = 0;

                [UnrealBlueprintEvent]
                public void Activate(int amount) { Value = amount; }

                protected override void OnBeginPlay() { Value = 1; }
                protected override void OnTick(float deltaSeconds) { Value += 1; }
                protected override void OnEndPlay() { Value = 0; }
            }
            """;
        var firstRequest = new CSharpCompilationRequest(
            "StableGameplayContract",
            [new CSharpSourceDocument("Game/LifecycleActor.cs", source)],
            [typeof(UnrealActorScript).Assembly.Location]);
        var editedRequest = new CSharpCompilationRequest(
            "StableGameplayContract",
            [new CSharpSourceDocument(
                "Game/LifecycleActor.cs",
                source.Replace("Value += 1", "Value += 2", StringComparison.Ordinal)
                    .Replace("Value = amount", "Value = amount + 1", StringComparison.Ordinal))],
            [typeof(UnrealActorScript).Assembly.Location]);

        CSharpAnalysisResult first = new CSharpSemanticPipeline().Analyze(firstRequest);
        CSharpAnalysisResult edited = new CSharpSemanticPipeline().Analyze(editedRequest);

        Assert.True(first.Succeeded, string.Join(Environment.NewLine, first.Diagnostics));
        Assert.True(edited.Succeeded, string.Join(Environment.NewLine, edited.Diagnostics));
        ScriptAssetDeclaration firstAsset = Assert.Single(first.ScriptAssets!.Assets);
        ScriptAssetDeclaration editedAsset = Assert.Single(edited.ScriptAssets!.Assets);
        Assert.Equal(
            firstAsset.LifecycleHooks!.Select(static hook => (hook.Kind, hook.Id)),
            editedAsset.LifecycleHooks!.Select(static hook => (hook.Kind, hook.Id)));
        Assert.Equal(Assert.Single(firstAsset.Functions).Id, Assert.Single(editedAsset.Functions).Id);
        Assert.Contains("\"blueprintEvent\"", ScriptAssetJson.Serialize(first.ScriptAssets));
        Assert.Contains("\"lifecycleHooks\"", ScriptAssetJson.Serialize(first.ScriptAssets));
    }

    [Fact]
    public void InputDelegateAndInterfaceBindingIdsSurviveBodyEdits()
    {
        const string source = """
            using UnifiedScript;
            using UnifiedScript.Unreal;

            [ScriptAsset("Game/Contracts", ClassId = "us_00000000000000000000000000000008")]
            [UnrealScriptClass(UnrealScriptClassKind.Actor)]
            public sealed class GameplayContracts : UnrealActorScript
            {
                [UnrealInputAction(
                    "/Game/Input/IA_Move.IA_Move",
                    ValueType = UnrealInputValueType.Axis2D)]
                public void Move(UnrealVector value) { }

                [Visual]
                [UnrealEvent(
                    typeof(UnrealObject),
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:OnManagedSignal",
                    Source = UnrealEventSource.Host)]
                private void OnSignal(int value, UnrealObjectHandle context) { }

                [UnrealInterfaceFunction(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropContract",
                    "TransformManagedValue")]
                public int TransformManagedValue(int value) { return value + 1; }
            }
            """;
        var firstRequest = new CSharpCompilationRequest(
            "StableGameplayBindings",
            [new CSharpSourceDocument("Game/Contracts.cs", source)],
            [typeof(UnrealActorScript).Assembly.Location]);
        var editedRequest = new CSharpCompilationRequest(
            "StableGameplayBindings",
            [new CSharpSourceDocument(
                "Game/Contracts.cs",
                source.Replace("return value + 1", "return value + 2", StringComparison.Ordinal))],
            [typeof(UnrealActorScript).Assembly.Location]);

        CSharpAnalysisResult first = new CSharpSemanticPipeline().Analyze(firstRequest);
        CSharpAnalysisResult edited = new CSharpSemanticPipeline().Analyze(editedRequest);

        Assert.True(first.Succeeded, string.Join(Environment.NewLine, first.Diagnostics));
        Assert.True(edited.Succeeded, string.Join(Environment.NewLine, edited.Diagnostics));
        ScriptAssetDeclaration firstAsset = Assert.Single(first.ScriptAssets!.Assets);
        ScriptAssetDeclaration editedAsset = Assert.Single(edited.ScriptAssets!.Assets);
        Assert.Equal(
            Assert.Single(firstAsset.Functions.Single(static function =>
                function.Name == "Move").InputActions!).Id,
            Assert.Single(editedAsset.Functions.Single(static function =>
                function.Name == "Move").InputActions!).Id);
        Assert.Equal(
            Assert.Single(firstAsset.Functions.Single(static function =>
                function.Name == "OnSignal").DelegateBindings!).Id,
            Assert.Single(editedAsset.Functions.Single(static function =>
                function.Name == "OnSignal").DelegateBindings!).Id);
        Assert.Equal(
            Assert.Single(firstAsset.Functions.Single(static function =>
                function.Name == "TransformManagedValue").InterfaceFunctions!).Id,
            Assert.Single(editedAsset.Functions.Single(static function =>
                function.Name == "TransformManagedValue").InterfaceFunctions!).Id);
        string json = ScriptAssetJson.Serialize(first.ScriptAssets);
        Assert.Contains("\"inputActions\"", json);
        Assert.Contains("\"delegateBindings\"", json);
        Assert.Contains("\"interfaceFunctions\"", json);
    }

    [Fact]
    public void PublicVisualBaseMethodsLowerIntoTheDerivedScriptClass()
    {
        const string source = """
            using UnifiedScript;
            using UnifiedScript.Unreal;

            namespace Game;

            public abstract class SharedBehavior
            {
                [Visual]
                public UnrealVector Offset(UnrealVector value)
                {
                    return value;
                }

                [Visual]
                protected int Hidden(int value)
                {
                    return value;
                }

                [Visual]
                public static int StaticBase(int value)
                {
                    return value;
                }
            }

            [ScriptAsset("Game/Derived", ClassId = "us_00000000000000000000000000000005")]
            [UnrealScriptClass(UnrealScriptClassKind.Object)]
            public sealed class DerivedBehavior : SharedBehavior
            {
                [Visual]
                public int Own(int value)
                {
                    return value;
                }
            }
            """;
        var request = new CSharpCompilationRequest(
            "InheritedScriptClassFunctions",
            [new CSharpSourceDocument("Game/Inherited.cs", source)],
            [typeof(UnrealObjectScript).Assembly.Location]);

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        ScriptAssetDeclaration baseAsset = result.ScriptAssets!.Assets.Single(
            static asset => asset.QualifiedName == "Game.SharedBehavior");
        ScriptAssetDeclaration derivedAsset = result.ScriptAssets.Assets.Single(
            static asset => asset.QualifiedName == "Game.DerivedBehavior");
        Assert.Equal(["Offset", "Own"],
            derivedAsset.Functions.Select(static function => function.Name).Order());
        ScriptFunctionDeclaration inherited = derivedAsset.Functions.Single(
            static function => function.Name == "Offset");
        Assert.Equal(
            baseAsset.Functions.Single(static function => function.Name == "Offset").Id,
            inherited.Id);
        Assert.Equal(
            "Game.DerivedBehavior.Offset(UnifiedScript.Unreal.UnrealVector)",
            inherited.QualifiedName);
        Assert.DoesNotContain(derivedAsset.Functions, static function =>
            function.Name is "Hidden" or "StaticBase");
    }

    [Fact]
    public void GeneratedEnumAndStructSignaturesRetainExactUnrealFieldIdentity()
    {
        const string source = """
            using UnifiedScript;
            using UnifiedScript.Unreal;

            namespace Game.Generated;

            [UnrealEnum("/Script/FixtureRuntime.EFixtureMode", "EFixtureMode")]
            [UnrealValue]
            public readonly record struct FixtureMode(long Value) : IUnrealEnumValue;

            [UnrealStruct("/Script/FixtureRuntime.FixturePayload", "FFixturePayload")]
            [UnrealValue]
            public readonly record struct FixturePayload(
                [property: UnrealStructField(0, "Count")] int Count,
                [property: UnrealStructField(1, "Mode")] FixtureMode Mode);

            [ScriptAsset("Game/TypedLibrary", ClassId = "us_00000000000000000000000000000006")]
            [UnrealScriptClass(UnrealScriptClassKind.BlueprintFunctionLibrary)]
            public static class TypedLibrary
            {
                [Visual]
                public static FixturePayload Echo(FixturePayload value) { return value; }
            }
            """;
        var request = new CSharpCompilationRequest(
            "GeneratedFunctionValueTypes",
            [new CSharpSourceDocument("Game/TypedLibrary.cs", source)],
            [typeof(UnrealObjectScript).Assembly.Location]);

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        ScriptFunctionDeclaration function = Assert.Single(result.ScriptAssets!.Assets
            .Single(static asset => asset.QualifiedName == "Game.Generated.TypedLibrary")
            .Functions);
        ScriptFunctionValueTypeDeclaration returnType = Assert.IsType<
            ScriptFunctionValueTypeDeclaration>(function.ReturnUnrealType);
        Assert.Equal(ScriptFunctionValueTypeKind.Struct, returnType.Kind);
        Assert.Equal("/Script/FixtureRuntime.FixturePayload", returnType.UnrealTypePath);
        Assert.Equal(["Count", "Mode"],
            returnType.Fields.Select(static field => field.NativeName));
        ScriptFunctionValueTypeDeclaration nestedEnum = Assert.IsType<
            ScriptFunctionValueTypeDeclaration>(returnType.Fields[1].UnrealType);
        Assert.Equal(ScriptFunctionValueTypeKind.Enum, nestedEnum.Kind);
        Assert.Equal("/Script/FixtureRuntime.EFixtureMode", nestedEnum.UnrealTypePath);
        ScriptFunctionValueTypeDeclaration parameterType = Assert.IsType<
            ScriptFunctionValueTypeDeclaration>(Assert.Single(function.Parameters).UnrealType);
        Assert.Equal(returnType.Kind, parameterType.Kind);
        Assert.Equal(returnType.UnrealTypePath, parameterType.UnrealTypePath);
        Assert.Equal(
            returnType.Fields.Select(static field => field.NativeName),
            parameterType.Fields.Select(static field => field.NativeName));
    }

    [Theory]
    [InlineData("us_0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("us_short")]
    public void InvalidExplicitScriptClassIdFailsBeforeEmission(string classId)
    {
        string source = $$"""
            using UnifiedScript;
            [ScriptAsset("Gameplay/Door", ClassId = "{{classId}}")]
            public sealed class Door { }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(source, "Door.cs");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == DeclarationDiagnosticCodes.InvalidScriptClassId);
    }

    [Fact]
    public void DuplicateExplicitScriptClassIdsFailBeforeEmission()
    {
        const string source = """
            using UnifiedScript;
            [ScriptAsset("Gameplay/First", ClassId = "us_0123456789abcdef0123456789abcdef")]
            public sealed class First { }
            [ScriptAsset("Gameplay/Second", ClassId = "us_0123456789abcdef0123456789abcdef")]
            public sealed class Second { }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(source, "DuplicateIds.cs");

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Diagnostics.Count(static diagnostic =>
            diagnostic.Code == DeclarationDiagnosticCodes.DuplicateScriptClassId));
    }
}
