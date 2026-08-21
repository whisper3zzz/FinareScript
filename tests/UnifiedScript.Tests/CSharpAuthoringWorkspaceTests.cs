using System.Text.Json;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.CSharp;

namespace UnifiedScript.Tests;

public sealed class CSharpAuthoringWorkspaceTests
{
    private const string Source = """
        using System;
        using UnifiedScript;

        namespace Tests.Authoring;

        [ScriptAsset("Tests/DamagePolicy")]
        public sealed class DamagePolicy
        {
            public int BaseDamage { get; set; } = 20;

            public int MaxDamage { get; set; } = 999;

            [Visual]
            public float ApplyDamage(int incomingDamage)
            {
                float scaled = incomingDamage * BaseDamage;
                scaled = Math.Clamp(scaled, 0f, MaxDamage);
                return scaled;
            }
        }
        """;

    [Fact]
    public void DeclarationMutationIsPreparedThenCommittedWithFreshArtifacts()
    {
        var workspace = new CSharpAuthoringWorkspace(CSharpCompilationRequest.Single(
            Source,
            "Tests/Authoring/DamagePolicy.cs"));
        CSharpAuthoringSnapshot baseline = workspace.Current;
        ScriptStateDeclaration maxDamage = Assert.Single(
            baseline.Analysis.ScriptAssets!.Assets[0].States,
            static state => state.Name == "MaxDamage");
        DeclarationPatchDocument patch = DeclarationPatchDocument.Create(
            baseline.Analysis.ScriptAssets.SourceCompilationId,
            baseline.Analysis.ScriptAssets.SemanticConfigurationId,
            [new UpdateScriptStatePatchOperation(
                maxDamage.Id,
                Expected(maxDamage),
                new ScriptStatePatchSpec(
                    "MaxDamage",
                    "int",
                    ScriptStateTypeKind.Integer,
                    Json("120"),
                    maxDamage.Exposure))]);

        CSharpAuthoringMutation mutation = workspace.Prepare(patch);

        Assert.True(mutation.Succeeded);
        Assert.Same(baseline, workspace.Current);
        Assert.Equal(2, mutation.Snapshot!.Revision);
        Assert.Contains("= 120;", mutation.Snapshot.Request.Sources[0].Source, StringComparison.Ordinal);
        Assert.NotEqual(
            baseline.Graph.SourceCompilationId,
            mutation.Snapshot.Graph.SourceCompilationId);

        workspace.Commit(mutation);

        Assert.Same(mutation.Snapshot, workspace.Current);
        Assert.Equal(
            workspace.Current.Analysis.ScriptAssets!.SourceCompilationId,
            workspace.Current.Graph.SourceCompilationId);
    }

    [Fact]
    public void SemanticMutationRegeneratesGraphAndRejectsAStaleCommit()
    {
        var workspace = new CSharpAuthoringWorkspace(CSharpCompilationRequest.Single(
            Source,
            "Tests/Authoring/DamagePolicy.cs"));
        CSharpAuthoringSnapshot baseline = workspace.Current;
        HirLiteralExpression clampMinimum = baseline.Analysis.Hir!
            .DescendantsAndSelf()
            .OfType<HirLiteralExpression>()
            .Single(static literal => literal.ValueText == "0f");
        SemanticPatchDocument firstPatch = SemanticPatchDocument.Create(
            baseline.Analysis.ScriptAssets!.SourceCompilationId,
            baseline.Analysis.ScriptAssets.SemanticConfigurationId,
            [new ReplaceLiteralPatchOperation(clampMinimum.Id, "0f", "10f")]);
        SemanticPatchDocument secondPatch = SemanticPatchDocument.Create(
            baseline.Analysis.ScriptAssets.SourceCompilationId,
            baseline.Analysis.ScriptAssets.SemanticConfigurationId,
            [new ReplaceLiteralPatchOperation(clampMinimum.Id, "0f", "20f")]);

        CSharpAuthoringMutation first = workspace.Prepare(firstPatch);
        CSharpAuthoringMutation stale = workspace.Prepare(secondPatch);
        workspace.Commit(first);

        Assert.Contains("Math.Clamp(scaled, 10f, MaxDamage)", workspace.Current.Request.Sources[0].Source);
        Assert.Contains(
            workspace.Current.Graph.Functions.SelectMany(static function => function.Nodes),
            static node => node.Kind == "literal" &&
                node.Properties.GetValueOrDefault("value") == "10f");
        Assert.Throws<InvalidOperationException>(() => workspace.Commit(stale));
    }

    [Fact]
    public void FailedMutationLeavesTheWorkspaceOnItsOriginalRevision()
    {
        var workspace = new CSharpAuthoringWorkspace(CSharpCompilationRequest.Single(
            Source,
            "Tests/Authoring/DamagePolicy.cs"));
        CSharpAuthoringSnapshot baseline = workspace.Current;
        ScriptStateDeclaration state = Assert.Single(
            baseline.Analysis.ScriptAssets!.Assets[0].States,
            static candidate => candidate.Name == "MaxDamage");
        ScriptStatePatchPrecondition wrongExpected = Expected(state) with { Name = "ChangedElsewhere" };
        DeclarationPatchDocument patch = DeclarationPatchDocument.Create(
            baseline.Analysis.ScriptAssets.SourceCompilationId,
            baseline.Analysis.ScriptAssets.SemanticConfigurationId,
            [new RemoveScriptStatePatchOperation(state.Id, wrongExpected)]);

        CSharpAuthoringMutation mutation = workspace.Prepare(patch);

        Assert.False(mutation.Succeeded);
        Assert.Null(mutation.Snapshot);
        Assert.Same(baseline, workspace.Current);
        Assert.Contains(mutation.Diagnostics, static diagnostic =>
            diagnostic.Code == DeclarationPatchDiagnosticCodes.StatePreconditionFailed);
    }

    [Fact]
    public void EmptyIdentitySidecarIsStabilizedBeforeTheFirstRename()
    {
        const string path = "Tests/Authoring/DamagePolicy.cs";
        var workspace = new CSharpAuthoringWorkspace(CSharpCompilationRequest.Single(
            Source,
            path,
            identitySidecar: IdentitySidecarDocument.CreateEmpty(path)));
        CSharpAuthoringSnapshot baseline = workspace.Current;
        ScriptStateDeclaration state = Assert.Single(
            baseline.Analysis.ScriptAssets!.Assets[0].States,
            static candidate => candidate.Name == "BaseDamage");
        DeclarationPatchDocument patch = DeclarationPatchDocument.Create(
            baseline.Analysis.ScriptAssets.SourceCompilationId,
            baseline.Analysis.ScriptAssets.SemanticConfigurationId,
            [new UpdateScriptStatePatchOperation(
                state.Id,
                Expected(state),
                new ScriptStatePatchSpec(
                    "DamagePerHit",
                    "int",
                    ScriptStateTypeKind.Integer,
                    state.DefaultValue,
                    state.Exposure))]);

        CSharpAuthoringMutation mutation = workspace.Prepare(patch);
        workspace.Commit(mutation);

        ScriptStateDeclaration renamed = Assert.Single(
            workspace.Current.Analysis.ScriptAssets!.Assets[0].States,
            static candidate => candidate.Name == "DamagePerHit");
        Assert.Equal(state.Id, renamed.Id);
        Assert.Contains("incomingDamage * DamagePerHit", workspace.Current.Request.Sources[0].Source);
    }

    [Fact]
    public void AddedStateIdentityIsStabilizedForTheNextRemoval()
    {
        const string path = "Tests/Authoring/DamagePolicy.cs";
        var workspace = new CSharpAuthoringWorkspace(CSharpCompilationRequest.Single(
            Source,
            path,
            identitySidecar: IdentitySidecarDocument.CreateEmpty(path)));
        ScriptAssetDocument baseline = workspace.Current.Analysis.ScriptAssets!;
        DeclarationPatchDocument add = DeclarationPatchDocument.Create(
            baseline.SourceCompilationId,
            baseline.SemanticConfigurationId,
            [new AddScriptStatePatchOperation(
                baseline.Assets[0].Id,
                new ScriptStatePatchSpec(
                    "ArmorRatio",
                    "float",
                    ScriptStateTypeKind.FloatingPoint,
                    Json("0.25"),
                    ScriptExposure.None))]);
        CSharpAuthoringMutation addedMutation = workspace.Prepare(add);
        workspace.Commit(addedMutation);
        ScriptStateDeclaration added = workspace.Current.Analysis.ScriptAssets!.Assets[0].States
            .Single(static state => state.Name == "ArmorRatio");
        DeclarationPatchDocument remove = DeclarationPatchDocument.Create(
            workspace.Current.Analysis.ScriptAssets.SourceCompilationId,
            workspace.Current.Analysis.ScriptAssets.SemanticConfigurationId,
            [new RemoveScriptStatePatchOperation(added.Id, Expected(added))]);

        CSharpAuthoringMutation removedMutation = workspace.Prepare(remove);
        workspace.Commit(removedMutation);

        Assert.DoesNotContain(
            workspace.Current.Analysis.ScriptAssets!.Assets[0].States,
            static state => state.Name == "ArmorRatio");
        Assert.DoesNotContain("ArmorRatio", workspace.Current.Request.Sources[0].Source);
    }

    private static ScriptStatePatchPrecondition Expected(ScriptStateDeclaration state) => new(
        state.Name,
        state.TypeMetadataName,
        state.TypeKind,
        state.DefaultValue,
        state.Exposure);

    private static JsonElement Json(string value) =>
        JsonDocument.Parse(value).RootElement.Clone();
}
