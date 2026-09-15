using System.Text.Json;
using Finare.Core;
using Finare.Core.Hir;
using Finare.CSharp;
using Finare.Graph;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Compiler")]
public sealed class CSharpScriptAssetLowererTests
{
    [Fact]
    public void UnrealScriptStateWithoutTheGeneratorIsRejectedClearly()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Game/LegacyState")]
            public sealed class LegacyState : UnrealObjectScript
            {
                public int Value { get; set; } = 1;
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "LegacyState",
                [new CSharpSourceDocument("Game/LegacyState.cs", source)],
                [typeof(UnrealObjectScript).Assembly.Location]));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == DeclarationDiagnosticCodes.UnsupportedStateShape &&
            diagnostic.Message.Contains("must be partial", StringComparison.Ordinal));
    }

    [Fact]
    public void TypedObjectAndInterfaceStateRetainNativeIdentityAcrossSerialization()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;
            using System.Collections.Generic;
            [UnrealType("/Script/Fixture.Actor")]
            public class Actor : UnrealObject {
                public Actor(UnrealObjectHandle h, IUnrealInvocationBridge b) : base(h,b) {}
            }
            [UnrealType("/Script/Fixture.Contract", IsInterface = true)]
            public class Contract : UnrealObject {
                public Contract(UnrealObjectHandle h, IUnrealInvocationBridge b) : base(h,b) {}
            }
            [ScriptAsset("Game/TypedState")]
            [UnrealScriptClass(UnrealScriptClassKind.Actor)]
            public partial class Behavior : UnrealActorScript {
                [Expose] public partial Actor? Target { get; set; } = null;
                [Expose] public partial Contract? Service { get; set; } = null;
                [Expose] public partial IReadOnlyList<Actor?> Actors { get; set; } = [];
                [Visual] public Actor? Echo(Actor? value) { return value; }
            }
            """;
        var request = new CSharpCompilationRequest("TypedState", [new CSharpSourceDocument("TypedState.cs", source)],
            [typeof(UnrealObject).Assembly.Location],
            sourceGeneratorPaths: [typeof(Finare.Unreal.Generators.UnrealNativePropertyGenerator).Assembly.Location]);
        var result = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var roundTrip = ScriptAssetJson.Deserialize(ScriptAssetJson.Serialize(result.ScriptAssets!));
        var asset = Assert.Single(roundTrip.Assets);
        byte[] binary = Finare.Runtime.FinareRuntimeDeclarationBinary.Encode(Finare.Runtime.FinareRuntimeDeclarationBinary.FromScriptAssets(roundTrip));
        Assert.Equal(6, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(binary.AsSpan(10, 2)));
        var runtime = Finare.Runtime.FinareRuntimeDeclarationBinary.Decode(binary);
        Assert.Equal("/Script/Fixture.Actor", Assert.Single(Assert.Single(runtime.Assets).States, s => s.Name == "Target").UnrealType!.UnrealTypePath);
        var target = Assert.Single(asset.States, s => s.Name == "Target");
        Assert.Equal(ScriptStateTypeKind.Object, target.TypeKind);
        Assert.Equal(ScriptFunctionValueTypeKind.Object, target.UnrealType!.Kind);
        Assert.Equal("/Script/Fixture.Actor", target.UnrealType.UnrealTypePath);
        Assert.Equal(JsonValueKind.Null, target.DefaultValue.ValueKind);
        Assert.Equal(ScriptFunctionValueTypeKind.Interface, Assert.Single(asset.States, s => s.Name == "Service").UnrealType!.Kind);
        Assert.Equal(ScriptFunctionValueTypeKind.Object, Assert.Single(asset.States, s => s.Name == "Actors").UnrealType!.TypeArguments![0].UnrealType!.Kind);
        Assert.Equal("/Script/Fixture.Actor", Assert.Single(asset.Functions).ReturnUnrealType!.UnrealTypePath);
    }

    private const string Source = """
        using System;
        using Finare;

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
            using Finare;

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
            using Finare;

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
    public void ScriptStateProjectionWalksOwnersAndAppliesDerivedConstructorDefaults()
    {
        const string source = """
            using System.Collections.Generic;
            using Finare;
            using Finare.Unreal;

            namespace Game;

            public partial class FrameworkHelper : UnrealActorScript
            {
                [Expose]
                public partial int ExposedHelper { get; set; } = 5;

                public int HiddenHelper { get; set; } = 6;
            }

            [ScriptAsset("Game/BaseState")]
            public partial class BaseState : FrameworkHelper
            {
                public partial int Seed { get; set; } = 3;
            }

            [ScriptAsset("Game/MiddleState")]
            public partial class MiddleState : BaseState
            {
                public partial IReadOnlyList<int> Values { get; set; } =
                    new List<int> { 2, 4 };
                public MiddleState()
                {
                    base.Seed = 7;
                    Values = new List<int> { 6, 8 };
                }
            }

            [ScriptAsset("Game/ConcreteState")]
            [UnrealScriptClass(
                UnrealScriptClassKind.Actor,
                GeneratedClassPath = "/Game/Tests/BP_ConcreteState.BP_ConcreteState_C")]
            public sealed partial class ConcreteState : MiddleState
            {
                public partial IReadOnlySet<int> Enabled { get; set; } =
                    new HashSet<int> { 11, 13 };
                public partial IReadOnlyDictionary<int, int> Weights { get; set; } =
                    new Dictionary<int, int> { { 17, 19 } };
                public ConcreteState()
                {
                    Seed = 9;
                    if (Seed < 0) return;
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "InheritedStateProjection",
                [new CSharpSourceDocument("Game/InheritedStateProjection.cs", source)],
                [typeof(UnrealObjectScript).Assembly.Location],
                sourceGeneratorPaths: [typeof(Finare.Unreal.Generators.UnrealNativePropertyGenerator).Assembly.Location]));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        ScriptAssetDeclaration concrete = result.ScriptAssets!.Assets.Single(
            static asset => asset.QualifiedName == "Game.ConcreteState");
        IReadOnlyDictionary<string, ScriptStateDeclaration> states = concrete.States
            .ToDictionary(static state => state.Name, StringComparer.Ordinal);
        Assert.Equal(9, states["Seed"].DefaultValue.GetInt32());
        Assert.Equal(5, states["ExposedHelper"].DefaultValue.GetInt32());
        Assert.DoesNotContain("HiddenHelper", states.Keys);
        Assert.DoesNotContain("Context", states.Keys);
        Assert.DoesNotContain("Bridge", states.Keys);
        Assert.DoesNotContain("HostHandle", states.Keys);
        Assert.DoesNotContain("TargetHandle", states.Keys);
        Assert.Equal([6, 8], states["Values"].DefaultValue.EnumerateArray()
            .Select(static value => value.GetInt32()).ToArray());
        Assert.Equal([11, 13], states["Enabled"].DefaultValue.EnumerateArray()
            .Select(static value => value.GetInt32()).ToArray());
        Assert.Equal(17, states["Weights"].DefaultValue[0].GetProperty("key").GetInt32());
        Assert.Equal(19, states["Weights"].DefaultValue[0].GetProperty("value").GetInt32());

        ScriptAssetDeclaration baseAsset = result.ScriptAssets.Assets.Single(
            static asset => asset.QualifiedName == "Game.BaseState");
        Assert.Equal(states["Seed"].Id, baseAsset.States.Single(static state => state.Name == "Seed").Id);
    }

    [Fact]
    public void ExposedFieldsStaticMembersAndUnaryConstructorWritesAreRejectedWithoutPublication()
    {
        const string source = """
            using Finare;

            public class InvalidHelper
            {
                [Expose]
                public int Field = 1;

                [Expose]
                public static int StaticValue { get; set; } = 2;
            }

            [ScriptAsset("Game.InvalidInheritedState")]
            public sealed class InvalidInheritedState : InvalidHelper
            {
                public int Seed { get; set; } = 1;

                public InvalidInheritedState()
                {
                    Seed++;
                }
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "InvalidInheritedState",
                [new CSharpSourceDocument("Game/InvalidInheritedState.cs", source)],
                [typeof(UnrealObjectScript).Assembly.Location]));

        Assert.False(result.Succeeded);
        Assert.Null(result.ScriptAssets);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == DeclarationDiagnosticCodes.UnsupportedStateShape &&
            diagnostic.Message.Contains("Field", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == DeclarationDiagnosticCodes.UnsupportedStateShape &&
            diagnostic.Message.Contains("StaticValue", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == DeclarationDiagnosticCodes.StateDefaultRequired &&
            diagnostic.Message.Contains("increment/decrement", StringComparison.Ordinal));
    }

    [Fact]
    public void StateHidingOverrideAndUnrealNameCollisionsAreRejected()
    {
        const string newSource = """
            using Finare;

            [ScriptAsset("Game.NewBase")]
            public class NewBase
            {
                public int Value { get; set; } = 1;
            }

            [ScriptAsset("Game.NewLeaf")]
            public sealed class NewLeaf : NewBase
            {
                public new int Value { get; set; } = 2;
            }
            """;
        const string overrideSource = """
            using Finare;

            [ScriptAsset("Game.OverrideBase")]
            public class OverrideBase
            {
                public virtual int Value { get; set; } = 1;
            }

            [ScriptAsset("Game.OverrideLeaf")]
            public sealed class OverrideLeaf : OverrideBase
            {
                public override int Value { get; set; } = 2;
            }
            """;
        const string unrealCaseSource = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Game.UnrealBase")]
            [UnrealScriptClass(
                UnrealScriptClassKind.Actor,
                GeneratedClassPath = "/Game/Tests/BP_UnrealBase.BP_UnrealBase_C")]
            public class UnrealBase : UnrealActorScript
            {
                public int Value { get; set; } = 1;
            }

            [ScriptAsset("Game.UnrealLeaf")]
            [UnrealScriptClass(
                UnrealScriptClassKind.Actor,
                GeneratedClassPath = "/Game/Tests/BP_UnrealLeaf.BP_UnrealLeaf_C")]
            public sealed class UnrealLeaf : UnrealBase
            {
                public int value { get; set; } = 2;
            }
            """;

        foreach ((string Name, string Source) candidate in new[]
        {
            ("new", newSource),
            ("override", overrideSource),
            ("unreal-case", unrealCaseSource),
        })
        {
            CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
                new CSharpCompilationRequest(
                    $"StateCollision_{candidate.Name}",
                    [new CSharpSourceDocument($"Game/{candidate.Name}.cs", candidate.Source)],
                    [typeof(UnrealObjectScript).Assembly.Location]));

            Assert.False(result.Succeeded, candidate.Name);
            Assert.Null(result.ScriptAssets);
            Assert.Contains(result.Diagnostics, diagnostic =>
                diagnostic.Code == DeclarationDiagnosticCodes.UnsupportedStateShape &&
                diagnostic.Message.Contains("conflicts", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void CommonUnrealValueAndReferenceStatesLowerWithTypedDefaults()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

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
    public void SoftReferenceStatesLowerAsExactHandlesAndPaths()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Game/SoftReferenceState")]
            public sealed class SoftReferenceState
            {
                public UnrealSoftObjectHandle Mesh { get; set; } =
                    new("/Engine/BasicShapes/Cube.Cube");
                public UnrealSoftClassHandle ActorClass { get; set; } =
                    new("/Script/Engine.StaticMeshActor");
                public UnrealSoftObjectHandle Empty { get; set; } = new();
            }
            """;
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "SoftReferenceState",
                [new CSharpSourceDocument("Game/SoftReferenceState.cs", source)],
                [typeof(UnrealObjectScript).Assembly.Location]));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        IReadOnlyDictionary<string, ScriptStateDeclaration> states = Assert.Single(
                result.ScriptAssets!.Assets)
            .States.ToDictionary(static state => state.Name, StringComparer.Ordinal);
        Assert.Equal(ScriptStateTypeKind.SoftObject, states["Mesh"].TypeKind);
        Assert.Equal("Finare.Unreal.UnrealSoftObjectHandle", states["Mesh"].TypeMetadataName);
        Assert.Null(states["Mesh"].UnrealType);
        Assert.Equal("/Engine/BasicShapes/Cube.Cube", states["Mesh"].DefaultValue.GetString());
        Assert.Equal(ScriptStateTypeKind.SoftClass, states["ActorClass"].TypeKind);
        Assert.Equal("/Script/Engine.StaticMeshActor", states["ActorClass"].DefaultValue.GetString());
        Assert.Equal(string.Empty, states["Empty"].DefaultValue.GetString());
    }

    [Fact]
    public void SoftReferenceDefaultRejectsNonCanonicalPath()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Game/InvalidSoftReference")]
            public sealed class InvalidSoftReference
            {
                public UnrealSoftObjectHandle Mesh { get; set; } = new("Game/BasicShapes/Cube.Cube");
            }
            """;
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "InvalidSoftReference",
                [new CSharpSourceDocument("Game/InvalidSoftReference.cs", source)],
                [typeof(UnrealObjectScript).Assembly.Location]));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == DeclarationDiagnosticCodes.StateDefaultRequired);
    }

    [Fact]
    public void CommonUnrealFunctionValuesAndDirectionsLowerIntoDeclarationIr()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Game/CommonFunctionShape")]
            public sealed class CommonFunctionShape
            {
                [Visual]
                public UnrealClassHandle Resolve(
                    UnrealName name,
                    ref UnrealRotator rotation,
                    out UnrealText label,
                    UnrealTransform transform)
                {
                    label = default;
                    return default;
                }
            }
            """;
        var request = new CSharpCompilationRequest(
            "CommonUnrealFunctionShape",
            [new CSharpSourceDocument("Game/CommonFunctionShape.cs", source)],
            [typeof(UnrealObjectScript).Assembly.Location]);

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        ScriptFunctionDeclaration function = Assert.Single(
            Assert.Single(result.ScriptAssets!.Assets).Functions);
        Assert.Equal("Finare.Unreal.UnrealClassHandle", function.ReturnTypeMetadataName);
        Assert.Equal(
            [
                ScriptParameterDirection.Input,
                ScriptParameterDirection.InputOutput,
                ScriptParameterDirection.Output,
                ScriptParameterDirection.Input,
            ],
            function.Parameters.Select(static parameter => parameter.Direction));
        Assert.Equal(
            [
                "Finare.Unreal.UnrealName",
                "Finare.Unreal.UnrealRotator",
                "Finare.Unreal.UnrealText",
                "Finare.Unreal.UnrealTransform",
            ],
            function.Parameters.Select(static parameter => parameter.TypeMetadataName));
        Assert.Contains("ref Finare.Unreal.UnrealRotator", function.QualifiedName);
        Assert.Contains("out Finare.Unreal.UnrealText", function.QualifiedName);
    }

    [Fact]
    public void CSharpInParameterFailsTheBlueprintFunctionContract()
    {
        const string source = """
            using Finare;

            [ScriptAsset("Game/InvalidInParameter")]
            public sealed class InvalidInParameter
            {
                [Visual]
                public int Resolve(in int value)
                {
                    return value;
                }
            }
            """;
        var request = new CSharpCompilationRequest(
            "InvalidInParameter",
            [new CSharpSourceDocument("Game/InvalidInParameter.cs", source)]);

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == VisualProfileDiagnosticCodes.ParameterModifierNotSupported);
    }

    [Fact]
    public void PersistentObjectStateRejectsProcessLocalHandleDefaults()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

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
    [InlineData("if (System.DateTime.UtcNow.Ticks > 0) return; Count = 7;")]
    [InlineData("goto Complete; Count = 7; Complete: ;")]
    public void NonDeterministicConstructorDefaultsAreRejected(string constructorBody)
    {
        string source = $$"""
            using Finare;

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
            using Finare;
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
            using Finare;
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
            using Finare;
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
            using Finare;
            using Finare.Unreal;

            namespace Game;

            [ScriptAsset("Game/Object", ClassId = "us_00000000000000000000000000000001")]
            [UnrealScriptClass(UnrealScriptClassKind.Object)]
            public sealed partial class ObjectBehavior : UnrealObjectScript
            {
                [Expose(InstanceEditable = true)]
                public partial int StartValue { get; set; } = 1;

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
                    "/Script/FinareRuntime.FinareInputActionFixture",
                    TriggerEvent = UnrealInputTriggerEvent.Triggered,
                    ValueType = UnrealInputValueType.Axis1D,
                    ConsumeInput = true)]
                public void Move(float value) { }

                [UnrealInterfaceFunction(
                    "/Script/FinareRuntime.FinareInteropContract",
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
                    "/Script/FinareRuntime.FinareComponent:OnManagedSignal")]
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
            [typeof(UnrealActorScript).Assembly.Location],
            sourceGeneratorPaths: [typeof(Finare.Unreal.Generators.UnrealNativePropertyGenerator).Assembly.Location]);

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
            "/Script/FinareRuntime.FinareInteropContract",
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
            "/Script/CoreUObject.Object",
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
            using Finare;
            using Finare.Unreal;

            namespace Game;

            [ScriptAsset("Game/LifecycleActor", ClassId = "us_00000000000000000000000000000007")]
            [UnrealScriptClass(UnrealScriptClassKind.Actor)]
            public sealed partial class LifecycleActor : UnrealActorScript
            {
                [Expose]
                public partial int Value { get; set; } = 0;

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
            [typeof(UnrealActorScript).Assembly.Location],
            sourceGeneratorPaths: [typeof(Finare.Unreal.Generators.UnrealNativePropertyGenerator).Assembly.Location]);
        var editedRequest = new CSharpCompilationRequest(
            "StableGameplayContract",
            [new CSharpSourceDocument(
                "Game/LifecycleActor.cs",
                source.Replace("Value += 1", "Value += 2", StringComparison.Ordinal)
                    .Replace("Value = amount", "Value = amount + 1", StringComparison.Ordinal))],
            [typeof(UnrealActorScript).Assembly.Location],
            sourceGeneratorPaths: [typeof(Finare.Unreal.Generators.UnrealNativePropertyGenerator).Assembly.Location]);

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
            using Finare;
            using Finare.Unreal;

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
                    "/Script/FinareRuntime.FinareComponent:OnManagedSignal",
                    Source = UnrealEventSource.Host)]
                private void OnSignal(int value, UnrealObjectHandle context) { }

                [UnrealInterfaceFunction(
                    "/Script/FinareRuntime.FinareInteropContract",
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
            using Finare;
            using Finare.Unreal;

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
            "Game.DerivedBehavior.Offset(Finare.Unreal.UnrealVector)",
            inherited.QualifiedName);
        Assert.DoesNotContain(derivedAsset.Functions, static function =>
            function.Name is "Hidden" or "StaticBase");
    }

    [Fact]
    public void GeneratedEnumAndStructSignaturesRetainExactUnrealFieldIdentity()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

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

    [Fact]
    public void GeneratedUnrealEnumDefaultsUseConstantAttributeValuesAndZeroConstructors()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            namespace Game.Generated;

            [UnrealEnum("/Script/FixtureRuntime.EFixtureMode", "EFixtureMode")]
            [UnrealValue]
            public readonly record struct FixtureMode(long Value) : IUnrealEnumValue
            {
                [UnrealEnumConstant(7)]
                public static FixtureMode Active { get; } = new(7);
            }

            [ScriptAsset("Game/TypedState")]
            public sealed class TypedState
            {
                public FixtureMode Mode { get; set; } = FixtureMode.Active;
                public FixtureMode Zero { get; set; } = new(0);
            }
            """;
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "TypedEnumState",
                [new CSharpSourceDocument("Game/TypedState.cs", source)],
                [typeof(UnrealObjectScript).Assembly.Location]));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        IReadOnlyDictionary<string, ScriptStateDeclaration> states = Assert.Single(
                result.ScriptAssets!.Assets).States
            .ToDictionary(static state => state.Name, StringComparer.Ordinal);
        Assert.Equal(ScriptStateTypeKind.Enum, states["Mode"].TypeKind);
        Assert.Equal("7", states["Mode"].DefaultValue.GetString());
        Assert.Equal(ScriptFunctionValueTypeKind.Enum, states["Mode"].UnrealType?.Kind);
        Assert.Equal("/Script/FixtureRuntime.EFixtureMode", states["Mode"].UnrealType?.UnrealTypePath);
        Assert.Equal("0", states["Zero"].DefaultValue.GetString());
    }

    [Fact]
    public void CommonUnrealStructDefaultsPreserveRotatorAndTransformIdentityShape()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Game/SpatialState")]
            public sealed class SpatialState
            {
                public UnrealRotator Rotation { get; set; } = new(10, 20, 30);
                public UnrealTransform Transform { get; set; } = UnrealTransform.Identity;
                public UnrealTransform Reordered { get; set; } = new(
                    Scale3D: new UnrealVector(2, 3, 4),
                    Translation: new UnrealVector(5, 6, 7),
                    Rotation: new UnrealQuaternion(0, 0, 0.7071067811865475, 0.7071067811865475));
            }
            """;
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "SpatialState",
                [new CSharpSourceDocument("Game/SpatialState.cs", source)],
                [typeof(UnrealObjectScript).Assembly.Location]));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        IReadOnlyDictionary<string, ScriptStateDeclaration> states = Assert.Single(
                result.ScriptAssets!.Assets).States
            .ToDictionary(static state => state.Name, StringComparer.Ordinal);
        Assert.Equal(10, states["Rotation"].DefaultValue.GetProperty("pitch").GetDouble());
        JsonElement transform = states["Transform"].DefaultValue;
        Assert.Equal(1, transform.GetProperty("scale3D").GetProperty("x").GetDouble());
        Assert.Equal(1, transform.GetProperty("scale3D").GetProperty("y").GetDouble());
        Assert.Equal(1, transform.GetProperty("scale3D").GetProperty("z").GetDouble());
        Assert.Equal(1, transform.GetProperty("rotation").GetProperty("w").GetDouble());
        JsonElement reordered = states["Reordered"].DefaultValue;
        Assert.Equal(0.7071067811865475, reordered.GetProperty("rotation").GetProperty("z").GetDouble());
        Assert.Equal(5, reordered.GetProperty("translation").GetProperty("x").GetDouble());
        Assert.Equal(4, reordered.GetProperty("scale3D").GetProperty("z").GetDouble());
    }

    [Fact]
    public void InvalidUnrealTransformDefaultsAreRejectedWithoutInventingIdentity()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Game/InvalidSpatialState")]
            public sealed class InvalidSpatialState
            {
                public UnrealTransform ZeroDefault { get; set; } = default(UnrealTransform);
                public UnrealTransform EmptyConstructor { get; set; } = new UnrealTransform();
                public UnrealTransform NonNormalized { get; set; } = new(
                    new UnrealQuaternion(0, 0, 0, 0.5),
                    new UnrealVector(0, 0, 0),
                    new UnrealVector(1, 1, 1));
            }
            """;
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "InvalidSpatialState",
                [new CSharpSourceDocument("Game/InvalidSpatialState.cs", source)],
                [typeof(UnrealObjectScript).Assembly.Location]));

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.Diagnostics.Count(static diagnostic =>
            diagnostic.Code == DeclarationDiagnosticCodes.StateDefaultRequired));
        Assert.All(
            result.Diagnostics.Where(static diagnostic =>
                diagnostic.Code == DeclarationDiagnosticCodes.StateDefaultRequired),
            static diagnostic => Assert.Contains(
                "finite components and a normalized quaternion; use UnrealTransform.Identity",
                diagnostic.Message,
                StringComparison.Ordinal));
    }

    [Fact]
    public void GeneratedEnumDefaultCanBeLoweredFromCompiledBindingsAssembly()
    {
        const string generatedEnumSource = """
            using Finare.Unreal;

            namespace ExternalBindings.Generated;

            [UnrealEnum("/Script/FixtureRuntime.EExternalMode", "EExternalMode")]
            [UnrealValue]
            public readonly record struct EExternalMode(long Value) : IUnrealEnumValue
            {
                [UnrealEnumConstant(17)]
                public static EExternalMode Active { get; } = new(17);
            }
            """;
        string unrealAssembly = typeof(UnrealObjectScript).Assembly.Location;
        CSharpProgramEmitResult emitted = new CSharpProgramEmitter().Emit(
            new CSharpCompilationRequest(
                "ExternalGeneratedBindings",
                [new CSharpSourceDocument("GeneratedBindings.cs", generatedEnumSource)],
                [unrealAssembly],
                runtimeReferencePaths: [unrealAssembly]));
        Assert.True(emitted.Succeeded, string.Join(Environment.NewLine, emitted.Diagnostics));
        string generatedBindings = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}.ExternalGeneratedBindings.dll");
        File.WriteAllBytes(generatedBindings, Assert.IsType<CSharpProgramImage>(emitted.Image).PortableExecutable);
        const string source = """
            using Finare;
            using ExternalBindings.Generated;

            [ScriptAsset("Game/CompiledEnumState")]
            public sealed class CompiledEnumState
            {
                public EExternalMode Mode { get; set; } = EExternalMode.Active;
            }
            """;
        try
        {
            CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
                new CSharpCompilationRequest(
                    "CompiledEnumState",
                    [new CSharpSourceDocument("Game/CompiledEnumState.cs", source)],
                    [unrealAssembly, generatedBindings],
                    runtimeReferencePaths: [unrealAssembly, generatedBindings]));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
            ScriptStateDeclaration state = Assert.Single(Assert.Single(result.ScriptAssets!.Assets).States);
            Assert.Equal("17", state.DefaultValue.GetString());
            Assert.Equal("/Script/FixtureRuntime.EExternalMode", state.UnrealType?.UnrealTypePath);
            Assert.Equal(ScriptFunctionValueTypeKind.Enum, state.UnrealType?.Kind);
        }
        finally
        {
            File.Delete(generatedBindings);
        }
    }

    [Fact]
    public void CollectionStateDefaultsPreserveLeafMetadataAndTypedJson()
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

            [ScriptAsset("Game.CollectionState")]
            public sealed class CollectionState
            {
                public IReadOnlyList<UnrealVector> Points { get; set; } =
                    new List<UnrealVector> { new(1, 2, 3) };
                public IReadOnlySet<UnrealName> Enabled { get; set; } =
                    new HashSet<UnrealName> { new("Alpha"), new("Beta") };
                public IReadOnlyDictionary<UnrealName, UnrealSoftObjectHandle> Assets { get; set; } =
                    new Dictionary<UnrealName, UnrealSoftObjectHandle>
                    {
                        { new UnrealName("Mesh"), new UnrealSoftObjectHandle("/Game/Props/Cube.Cube") },
                    };
                public IReadOnlyList<CollectionMode> Modes { get; set; } =
                    new List<CollectionMode> { CollectionMode.Active };
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "CollectionState",
                [new CSharpSourceDocument("Game/CollectionState.cs", source)],
                [typeof(UnrealObjectScript).Assembly.Location]));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        IReadOnlyDictionary<string, ScriptStateDeclaration> states = Assert.Single(
                result.ScriptAssets!.Assets).States
            .ToDictionary(static state => state.Name, StringComparer.Ordinal);

        ScriptStateDeclaration points = states["Points"];
        Assert.Equal(ScriptStateTypeKind.Array, points.TypeKind);
        Assert.Equal(ScriptFunctionValueTypeKind.Array, points.UnrealType?.Kind);
        Assert.Equal("Finare.Unreal.UnrealVector", points.UnrealType?.TypeArguments![0].TypeMetadataName);
        Assert.Equal(1, points.DefaultValue.GetArrayLength());
        Assert.Equal(3, points.DefaultValue[0].GetProperty("z").GetDouble());

        ScriptStateDeclaration enabled = states["Enabled"];
        Assert.Equal(ScriptStateTypeKind.Set, enabled.TypeKind);
        Assert.Equal(2, enabled.DefaultValue.GetArrayLength());
        Assert.Equal("Alpha", enabled.DefaultValue[0].GetString());

        ScriptStateDeclaration assets = states["Assets"];
        Assert.Equal(ScriptStateTypeKind.Map, assets.TypeKind);
        Assert.Equal("Finare.Unreal.UnrealSoftObjectHandle",
            assets.UnrealType?.TypeArguments![1].TypeMetadataName);
        Assert.Equal("/Game/Props/Cube.Cube",
            assets.DefaultValue[0].GetProperty("value").GetString());

        ScriptStateDeclaration modes = states["Modes"];
        Assert.Equal(ScriptFunctionValueTypeKind.Enum, modes.UnrealType?.TypeArguments![0].UnrealType?.Kind);
        Assert.Equal("7", modes.DefaultValue[0].GetString());
    }

    [Fact]
    public void CollectionStateRejectsNestedAndUnreviewedLeafShapes()
    {
        const string source = """
            using System.Collections.Generic;
            using Finare;
            using Finare.Unreal;

            [UnrealStruct("/Script/FixtureRuntime.FixturePayload", "FFixturePayload")]
            [UnrealValue]
            public readonly record struct FixturePayload(
                [property: UnrealStructField(0, "Count")] int Count);

            [ScriptAsset("Game.InvalidCollectionState")]
            public sealed class InvalidCollectionState
            {
                public IReadOnlyList<IReadOnlyList<int>> Nested { get; set; } =
                    new List<IReadOnlyList<int>>();
                public IReadOnlySet<FixturePayload> Structs { get; set; } =
                    new HashSet<FixturePayload>();
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "InvalidCollectionState",
                [new CSharpSourceDocument("Game/InvalidCollectionState.cs", source)],
                [typeof(UnrealObjectScript).Assembly.Location]));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == DeclarationDiagnosticCodes.UnsupportedStateType ||
            diagnostic.Code == DeclarationDiagnosticCodes.StateDefaultRequired);
    }

    [Fact]
    public void CollectionStateInvalidDefaultsBecomeDiagnosticsWithoutThrowing()
    {
        const string source = """
            using System.Collections.Generic;
            using Finare;
            using Finare.Unreal;

            sealed class NameComparer : IEqualityComparer<UnrealName>
            {
                public bool Equals(UnrealName left, UnrealName right) => left.Value == right.Value;
                public int GetHashCode(UnrealName value) => value.Value.GetHashCode();
            }

            [ScriptAsset("Game.InvalidCollectionDefaults")]
            public sealed class InvalidCollectionDefaults
            {
                public IReadOnlySet<UnrealName> DuplicateNames { get; set; } =
                    new HashSet<UnrealName> { new("Alpha"), new("alpha") };
                public IReadOnlySet<UnrealName> CustomComparer { get; set; } =
                    new HashSet<UnrealName>(new NameComparer());
                public IReadOnlyList<short> UnsupportedInteger { get; set; } =
                    new List<short> { 1 };
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "InvalidCollectionDefaults",
                [new CSharpSourceDocument("Game/InvalidCollectionDefaults.cs", source)],
                [typeof(UnrealObjectScript).Assembly.Location]));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code is DeclarationDiagnosticCodes.StateDefaultRequired or
                DeclarationDiagnosticCodes.UnsupportedStateType);
    }

    [Fact]
    public void CollectionDefaultsRejectSameNamedNonGenericImplementations()
    {
        const string source = """
            using System.Collections.Generic;
            using Finare;
            using Finare.Unreal;

            namespace FakeCollections
            {
                public sealed class List<T> : System.Collections.Generic.List<T> { }
            }

            [ScriptAsset("Game.FakeCollectionDefaults")]
            public sealed class FakeCollectionDefaults
            {
                public IReadOnlyList<int> Values { get; set; } =
                    new FakeCollections.List<int> { 1, 2 };
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "FakeCollectionDefaults",
                [new CSharpSourceDocument("Game/FakeCollectionDefaults.cs", source)],
                [typeof(UnrealObjectScript).Assembly.Location]));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == DeclarationDiagnosticCodes.StateDefaultRequired);
    }

    [Theory]
    [InlineData("IReadOnlyList<int>", ScriptFunctionValueTypeKind.Array, "TArray", 1)]
    [InlineData("IReadOnlySet<UnrealName>", ScriptFunctionValueTypeKind.Set, "TSet", 1)]
    [InlineData("IReadOnlyDictionary<string, UnrealSoftObjectHandle>", ScriptFunctionValueTypeKind.Map, "TMap", 2)]
    [InlineData("IReadOnlyList<FixturePayload>", ScriptFunctionValueTypeKind.Array, "TArray", 1)]
    public void ContainerFunctionsRetainTypedArgumentsDirectionsAndJsonIdentity(
        string valueType, ScriptFunctionValueTypeKind kind, string nativeName, int arity)
    {
        CSharpAnalysisResult result = AnalyzeFunctionShape(valueType);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        ScriptAssetDocument document = result.ScriptAssets!;
        ScriptFunctionDeclaration function = Assert.Single(document.Assets.Single(
            static asset => asset.QualifiedName == "Game.TypedLibrary").Functions);
        ScriptFunctionValueTypeDeclaration declaration = Assert.IsType<
            ScriptFunctionValueTypeDeclaration>(function.ReturnUnrealType);
        Assert.Equal(kind, declaration.Kind);
        Assert.Equal(nativeName, declaration.UnrealNativeName);
        Assert.Empty(declaration.UnrealTypePath);
        Assert.Empty(declaration.Fields);
        Assert.Equal(arity, declaration.TypeArguments!.Count);
        Assert.Equal(
            [ScriptParameterDirection.InputOutput, ScriptParameterDirection.Output],
            function.Parameters.Select(static parameter => parameter.Direction));
        Assert.All(function.Parameters, parameter => Assert.Equal(
            declaration.Kind, parameter.UnrealType!.Kind));
        if (valueType.Contains("FixturePayload", StringComparison.Ordinal))
        {
            ScriptFunctionValueTypeDeclaration element = declaration.TypeArguments[0].UnrealType!;
            Assert.Equal("/Script/FixtureRuntime.FixturePayload", element.UnrealTypePath);
            Assert.Equal("Count", Assert.Single(element.Fields).NativeName);
        }
        string json = ScriptAssetJson.Serialize(document);
        Assert.Equal(json, ScriptAssetJson.Serialize(ScriptAssetJson.Deserialize(json)));
        Assert.Contains("\"typeArguments\"", json);
    }

    [Theory]
    [InlineData("IReadOnlyList<IReadOnlyList<int>>", "nested containers are not supported")]
    [InlineData("IReadOnlyDictionary<bool, int>", "map key type has no Blueprint hash identity")]
    [InlineData("IReadOnlySet<bool>", "set element type has no Blueprint hash identity")]
    [InlineData("IReadOnlySet<UnrealText>", "set element type has no Blueprint hash identity")]
    public void UnsupportedFunctionValueShapesFailBeforeBlueprintEmission(
        string valueType, string expectedMessage)
    {
        CSharpAnalysisResult result = AnalyzeFunctionShape(valueType);

        Assert.False(result.Succeeded);
        UnifiedDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            static item => item.Code == DeclarationDiagnosticCodes.UnsupportedFunctionSignature);
        Assert.Contains(expectedMessage, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedParameterShapeRetainsSpecificReasonWithValidReturn()
    {
        const string source = """
            using System.Collections.Generic;
            using Finare;
            using Finare.Unreal;
            namespace Game;
            [ScriptAsset("Game/TypedLibrary", ClassId = "us_00000000000000000000000000000006")]
            [UnrealScriptClass(UnrealScriptClassKind.BlueprintFunctionLibrary)]
            public static class TypedLibrary
            {
                [Visual]
                public static int Echo(ref IReadOnlySet<UnrealText> value, out int copy)
                {
                    copy = 0;
                    return 0;
                }
            }
            """;
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "FunctionParameterShape",
                [new CSharpSourceDocument("Game/TypedLibrary.cs", source)],
                [typeof(UnrealObjectScript).Assembly.Location]));

        Assert.False(result.Succeeded);
        UnifiedDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            static item => item.Code == DeclarationDiagnosticCodes.UnsupportedFunctionSignature);
        Assert.Contains(
            "set element type has no Blueprint hash identity",
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedFunctionLeafFailsTheGraphSafeContract()
    {
        CSharpAnalysisResult result = AnalyzeFunctionShape("System.DateTime");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == SemanticDiagnosticCodes.GraphUnsafeSignature);
    }

    [Theory]
    [InlineData("UnrealSoftObjectHandle")]
    [InlineData("UnrealSoftClassHandle")]
    public void SoftReferenceFunctionsPreserveBuiltInTypeAndDirections(string valueType)
    {
        CSharpAnalysisResult result = AnalyzeFunctionShape(valueType);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        ScriptFunctionDeclaration function = Assert.Single(result.ScriptAssets!.Assets.Single(
            static asset => asset.QualifiedName == "Game.TypedLibrary").Functions);
        Assert.Equal($"Finare.Unreal.{valueType}", function.ReturnTypeMetadataName);
        Assert.Null(function.ReturnUnrealType);
        Assert.All(function.Parameters, parameter =>
            Assert.Equal(function.ReturnTypeMetadataName, parameter.TypeMetadataName));
    }

    [Fact]
    public void ContainerJsonRejectsMismatchedKindArityAndArgumentIdentity()
    {
        CSharpAnalysisResult result = AnalyzeFunctionShape("IReadOnlyList<int>");
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string json = ScriptAssetJson.Serialize(result.ScriptAssets!);
        Assert.Throws<System.Text.Json.JsonException>(() => ScriptAssetJson.Deserialize(
            json.Replace("\"kind\": \"array\"", "\"kind\": \"map\"", StringComparison.Ordinal)));
        Assert.Throws<System.Text.Json.JsonException>(() => ScriptAssetJson.Deserialize(
            json.Replace("\"TArray\"", "\"TSet\"", StringComparison.Ordinal)));
        Assert.Throws<System.Text.Json.JsonException>(() => ScriptAssetJson.Deserialize(
            json.Replace("\"typeMetadataName\": \"System.Int32\"",
                "\"typeMetadataName\": \"System.Boolean\"", StringComparison.Ordinal)));
    }

    private static CSharpAnalysisResult AnalyzeFunctionShape(string valueType)
    {
        string source = $$"""
            using System.Collections.Generic;
            using Finare;
            using Finare.Unreal;
            namespace Game;
            [UnrealStruct("/Script/FixtureRuntime.FixturePayload", "FFixturePayload")]
            [UnrealValue]
            public readonly record struct FixturePayload(
                [property: UnrealStructField(0, "Count")] int Count);
            [ScriptAsset("Game/TypedLibrary", ClassId = "us_00000000000000000000000000000006")]
            [UnrealScriptClass(UnrealScriptClassKind.BlueprintFunctionLibrary)]
            public static class TypedLibrary
            {
                [Visual]
                public static {{valueType}} Echo(ref {{valueType}} value, out {{valueType}} copy)
                {
                    copy = value;
                    return value;
                }
            }
            """;
        return new CSharpSemanticPipeline().Analyze(new CSharpCompilationRequest(
            "FunctionShape", [new CSharpSourceDocument("Game/TypedLibrary.cs", source)],
            [typeof(UnrealObjectScript).Assembly.Location]));
    }

    [Theory]
    [InlineData("us_0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("us_short")]
    public void InvalidExplicitScriptClassIdFailsBeforeEmission(string classId)
    {
        string source = $$"""
            using Finare;
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
            using Finare;
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
