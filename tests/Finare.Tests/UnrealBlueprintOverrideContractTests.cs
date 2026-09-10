using System.Text.Json;
using Finare.Core;
using Finare.Core.Hir;
using Finare.CSharp;
using Finare.Unreal;

namespace Finare.Tests;

public sealed class UnrealBlueprintOverrideContractTests
{
    private const string ValidSource = """
        using Finare;
        using Finare.Unreal;

        namespace Game;

        [ScriptAsset("Game/OverrideActor", ClassId = "us_00000000000000000000000000000071")]
        [UnrealScriptClass(UnrealScriptClassKind.Actor)]
        public sealed class OverrideActor : UnrealActorScript
        {
            [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
            public void Activate(int value)
            {
                int copy = value;
            }

            protected override void OnBeginPlay() { }
            protected override void OnTick(float deltaSeconds) { }
            protected override void OnEndPlay() { }
        }
        """;

    [Fact]
    public void OverrideWithoutVisualEntersHirDeclarationAndV2Schema()
    {
        CSharpAnalysisResult result = Analyze(ValidSource);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirMethodDeclaration method = Assert.Single(
            Assert.Single(result.Hir!.Types).Methods,
            static candidate => candidate.Name == "Activate");
        Assert.Contains(
            method.Attributes!,
            static attribute => attribute.Name == "UnrealBlueprintOverride");

        ScriptAssetDeclaration asset = Assert.Single(result.ScriptAssets!.Assets);
        ScriptFunctionDeclaration function = Assert.Single(asset.Functions);
        ScriptBlueprintEventDeclaration blueprintEvent = Assert.IsType<ScriptBlueprintEventDeclaration>(
            function.BlueprintEvent);
        ScriptFunctionOverrideDeclaration blueprintOverride = Assert.IsType<ScriptFunctionOverrideDeclaration>(
            blueprintEvent.Override);
        Assert.Equal(ScriptAssetDocument.OverrideSchemaVersion, result.ScriptAssets.SchemaVersion);
        Assert.Equal("/Script/Engine.Actor", blueprintOverride.DeclaringTypePath);
        Assert.Equal("ReceiveBeginPlay", blueprintOverride.FunctionName);

        string json = ScriptAssetJson.Serialize(result.ScriptAssets);
        Assert.Contains("\"override\"", json, StringComparison.Ordinal);
        Assert.Contains("\"declaringTypePath\"", json, StringComparison.Ordinal);
        Assert.Equal(json, ScriptAssetJson.Serialize(ScriptAssetJson.Deserialize(json)));
    }

    [Fact]
    public void OverrideFunctionIdAndTargetSurviveBodyEdit()
    {
        CSharpAnalysisResult first = Analyze(ValidSource);
        CSharpAnalysisResult edited = Analyze(
            ValidSource.Replace("int copy = value;", "int copy = value + 1;", StringComparison.Ordinal));

        Assert.True(first.Succeeded, string.Join(Environment.NewLine, first.Diagnostics));
        Assert.True(edited.Succeeded, string.Join(Environment.NewLine, edited.Diagnostics));
        ScriptFunctionDeclaration firstFunction = Assert.Single(
            Assert.Single(first.ScriptAssets!.Assets).Functions);
        ScriptFunctionDeclaration editedFunction = Assert.Single(
            Assert.Single(edited.ScriptAssets!.Assets).Functions);
        Assert.Equal(firstFunction.Id, editedFunction.Id);
        Assert.Equal(
            Assert.IsType<ScriptFunctionOverrideDeclaration>(
                Assert.IsType<ScriptBlueprintEventDeclaration>(firstFunction.BlueprintEvent).Override),
            Assert.IsType<ScriptFunctionOverrideDeclaration>(
                Assert.IsType<ScriptBlueprintEventDeclaration>(editedFunction.BlueprintEvent).Override));
    }

    [Fact]
    public void OverrideAcceptsConstNameofAndExplicitSuperModeArguments()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            public static class OverrideTargets
            {
                public const string ClassPath = "/Plugin/MyPlugin/BP/BP.BP_C";
                public const string FunctionName = nameof(ReceiveBeginPlay);

                public static void ReceiveBeginPlay() { }
            }

            [ScriptAsset("Game/ConstOverride", ClassId = "us_00000000000000000000000000000074")]
            [UnrealScriptClass(UnrealScriptClassKind.Actor)]
            public sealed class ConstOverride : UnrealActorScript
            {
                [UnrealBlueprintOverride(
                    OverrideTargets.ClassPath,
                    OverrideTargets.FunctionName,
                    SuperMode = UnrealBlueprintEventSuperMode.Explicit)]
                public void Activate() { }

                protected override void OnBeginPlay() { }
                protected override void OnTick(float deltaSeconds) { }
                protected override void OnEndPlay() { }
            }
            """;

        CSharpAnalysisResult result = Analyze(source);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        ScriptBlueprintEventDeclaration declaration = Assert.IsType<ScriptBlueprintEventDeclaration>(
            Assert.Single(Assert.Single(result.ScriptAssets!.Assets).Functions).BlueprintEvent);
        ScriptFunctionOverrideDeclaration blueprintOverride = Assert.IsType<ScriptFunctionOverrideDeclaration>(
            declaration.Override);
        Assert.Equal("/Plugin/MyPlugin/BP/BP.BP_C", blueprintOverride.DeclaringTypePath);
        Assert.Equal("ReceiveBeginPlay", blueprintOverride.FunctionName);
        Assert.Equal(ScriptBlueprintEventSuperMode.Explicit, declaration.SuperMode);
    }

    [Fact]
    public void ExistingBlueprintEventKeepsV01Shape()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Game/ExistingEvent", ClassId = "us_00000000000000000000000000000072")]
            [UnrealScriptClass(UnrealScriptClassKind.Actor)]
            public sealed class ExistingEvent : UnrealActorScript
            {
                [UnrealBlueprintEvent]
                public void Activate() { }

                protected override void OnBeginPlay() { }
                protected override void OnTick(float deltaSeconds) { }
                protected override void OnEndPlay() { }
            }
            """;

        CSharpAnalysisResult result = Analyze(source);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(ScriptAssetDocument.CurrentSchemaVersion, result.ScriptAssets!.SchemaVersion);
        string json = ScriptAssetJson.Serialize(result.ScriptAssets);
        Assert.DoesNotContain("\"override\"", json, StringComparison.Ordinal);
        Assert.Equal(json, ScriptAssetJson.Serialize(ScriptAssetJson.Deserialize(json)));
    }

    [Theory]
    [InlineData("/Game/Invalid/BP", "ReceiveBeginPlay")]
    [InlineData("/Script/Engine.Actor", "Receive-BeginPlay")]
    [InlineData("", "ReceiveBeginPlay")]
    public void InvalidOverrideTargetProducesSourceDiagnostic(string declaringClassPath, string functionName)
    {
        string source = ValidSource
            .Replace("/Script/Engine.Actor", declaringClassPath, StringComparison.Ordinal)
            .Replace("ReceiveBeginPlay", functionName, StringComparison.Ordinal);

        CSharpAnalysisResult result = Analyze(source);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == DeclarationDiagnosticCodes.InvalidBlueprintOverride);
    }

    [Fact]
    public void DuplicateOverrideRoutesProduceSourceDiagnostic()
    {
        const string source = """
            using Finare;
            using Finare.Unreal;

            [ScriptAsset("Game/DuplicateOverride", ClassId = "us_00000000000000000000000000000073")]
            [UnrealScriptClass(UnrealScriptClassKind.Actor)]
            public sealed class DuplicateOverride : UnrealActorScript
            {
                [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
                public void First() { }

                [UnrealBlueprintOverride("/Script/engine.actor", "receivebeginplay")]
                public void Second() { }

                protected override void OnBeginPlay() { }
                protected override void OnTick(float deltaSeconds) { }
                protected override void OnEndPlay() { }
            }
            """;

        CSharpAnalysisResult result = Analyze(source);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == DeclarationDiagnosticCodes.InvalidBlueprintOverride &&
                diagnostic.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) &&
                diagnostic.Message.Contains("Second", StringComparison.Ordinal) &&
                diagnostic.Span.Start.Line > 1);
    }

    [Fact]
    public void ConflictingBlueprintEventAndOverrideProduceSourceDiagnostic()
    {
        string source = ValidSource.Replace(
            "[UnrealBlueprintOverride(\"/Script/Engine.Actor\", \"ReceiveBeginPlay\")]",
            "[UnrealBlueprintEvent]\n    [UnrealBlueprintOverride(\"/Script/Engine.Actor\", \"ReceiveBeginPlay\")]",
            StringComparison.Ordinal);

        CSharpAnalysisResult result = Analyze(source);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == VisualProfileDiagnosticCodes.InvalidBlueprintOverride);
    }

    [Fact]
    public void OverrideRejectsUnsupportedSuperMode()
    {
        string source = ValidSource.Replace(
            "[UnrealBlueprintOverride(\"/Script/Engine.Actor\", \"ReceiveBeginPlay\")]",
            "[UnrealBlueprintOverride(\"/Script/Engine.Actor\", \"ReceiveBeginPlay\", SuperMode = (UnrealBlueprintEventSuperMode)999)]",
            StringComparison.Ordinal);

        CSharpAnalysisResult result = Analyze(source);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == DeclarationDiagnosticCodes.InvalidBlueprintOverride);
    }

    [Fact]
    public void CoreJsonRejectsV01OverridesAndMalformedTargets()
    {
        CSharpAnalysisResult result = Analyze(ValidSource);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string json = ScriptAssetJson.Serialize(result.ScriptAssets!);

        string v01 = json.Replace(
            ScriptAssetDocument.OverrideSchemaVersion,
            ScriptAssetDocument.CurrentSchemaVersion,
            StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => ScriptAssetJson.Deserialize(v01));

        string invalidClass = json.Replace(
            "/Script/Engine.Actor",
            "/Game/Invalid/BP",
            StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => ScriptAssetJson.Deserialize(invalidClass));

        string invalidFunction = json.Replace(
            "ReceiveBeginPlay",
            "Receive-BeginPlay",
            StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => ScriptAssetJson.Deserialize(invalidFunction));

        ScriptAssetDocument scriptAssets = result.ScriptAssets!;
        ScriptAssetDeclaration asset = Assert.Single(scriptAssets.Assets);
        ScriptFunctionDeclaration function = Assert.Single(asset.Functions);
        ScriptFunctionDeclaration duplicate = function with
        {
            Id = SemanticId.FromKey("duplicate-blueprint-override-function"),
            Name = "Other",
            QualifiedName = "Game.OverrideActor.Other",
        };
        ScriptAssetDocument duplicateDocument = scriptAssets with
        {
            Assets = [asset with { Functions = [function, duplicate] }],
        };
        JsonException duplicateError = Assert.Throws<JsonException>(
            () => ScriptAssetJson.NormalizeAndValidate(duplicateDocument));
        Assert.Contains("duplicate", duplicateError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public unsafe void BothManagedDispatchersReviewBlueprintOverrideMethods()
    {
        var v1 = UnrealManagedFunctionDispatcher.Create(typeof(OverrideDispatcherFixture));
        Span<UnrealManagedFunctionValueV1> arguments =
        [
            UnrealManagedFunctionValueV1.FromInt32(4),
        ];
        fixed (UnrealManagedFunctionValueV1* pointer = arguments)
        {
            UnrealManagedFunctionValueV1 result = v1.Invoke(
                new OverrideDispatcherFixture(),
                nameof(OverrideDispatcherFixture.CallParent),
                (nint)pointer,
                arguments.Length);
            Assert.Equal(UnrealManagedFunctionValueKindV1.Int32, result.Kind);
            Assert.Equal(5UL, result.Payload);
        }

        UnrealManagedFunctionDispatcherV2 v2 = UnrealManagedFunctionDispatcherV2.Create(
            typeof(OverrideDispatcherFixture),
            staticOnly: false);
        Assert.Contains(
            nameof(OverrideDispatcherFixture.CallParent),
            v2.Signatures.Keys);
    }

    private static CSharpAnalysisResult Analyze(string source) =>
        new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "BlueprintOverrideContract",
                [new CSharpSourceDocument("Game/BlueprintOverride.cs", source)],
                [typeof(UnrealActorScript).Assembly.Location]));

    private sealed class OverrideDispatcherFixture
    {
        [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
        public int CallParent(int value) => value + 1;
    }
}
