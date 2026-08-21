using System.Text.Json;
using UnifiedScript.Core;
using UnifiedScript.CSharp;

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
