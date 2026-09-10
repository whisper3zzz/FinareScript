using System.Text.Json;
using Finare.Core;
using Finare.Core.Hir;
using Finare.CSharp;

namespace Finare.Tests;

public sealed class CSharpAuthoringWorkspaceTests
{
    private const string Source = """
        using System;
        using Finare;

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
    public void SourceTextMutationPreservesStableIdentitiesAndUnchangedComments()
    {
        const string path = "Tests/Authoring/DamagePolicy.cs";
        var workspace = new CSharpAuthoringWorkspace(CSharpCompilationRequest.Single(Source, path));
        CSharpAuthoringSnapshot baseline = workspace.Current;
        ScriptStateDeclaration baselineState = baseline.Analysis.ScriptAssets!.Assets[0].States
            .Single(static state => state.Name == "BaseDamage");
        ScriptFunctionDeclaration baselineFunction = baseline.Analysis.ScriptAssets.Assets[0].Functions
            .Single(static function => function.Name == "ApplyDamage");
        string edited = Source.Replace(
            "float scaled = incomingDamage * BaseDamage;",
            "// Keep this authored comment.\n        float scaled = incomingDamage * BaseDamage + 1;",
            StringComparison.Ordinal);
        CSharpSourceTextPatchDocument patch = CSharpSourceTextPatchDocument.Create(
            path,
            Source,
            edited);

        CSharpAuthoringMutation mutation = workspace.Prepare(patch);

        Assert.True(mutation.Succeeded);
        Assert.Same(baseline, workspace.Current);
        Assert.Equal(2, mutation.Snapshot!.Revision);
        Assert.Contains("// Keep this authored comment.", mutation.Snapshot.Request.Sources[0].Source);
        ScriptAssetDeclaration candidateAsset = mutation.Snapshot.Analysis.ScriptAssets!.Assets[0];
        Assert.Equal(
            baselineState.Id,
            candidateAsset.States.Single(static state => state.Name == "BaseDamage").Id);
        Assert.Equal(
            baselineFunction.Id,
            candidateAsset.Functions.Single(static function => function.Name == "ApplyDamage").Id);
    }

    [Fact]
    public void SourceTextMutationRejectsStaleDigestUnknownPathNoOpAndCompilerErrors()
    {
        const string path = "Tests/Authoring/DamagePolicy.cs";
        var workspace = new CSharpAuthoringWorkspace(CSharpCompilationRequest.Single(Source, path));

        CSharpAuthoringMutation stale = workspace.Prepare(new CSharpSourceTextPatchDocument(
            CSharpSourceTextPatchDocument.CurrentSchemaVersion,
            path,
            CSharpSourceTextPatchJson.ComputeContentDigest(Source + "// stale"),
            Source.Replace("= 20;", "= 21;", StringComparison.Ordinal)));
        CSharpAuthoringMutation unknown = workspace.Prepare(CSharpSourceTextPatchDocument.Create(
            "Tests/Authoring/Other.cs",
            Source,
            Source.Replace("= 20;", "= 21;", StringComparison.Ordinal)));
        CSharpAuthoringMutation noOp = workspace.Prepare(
            CSharpSourceTextPatchDocument.Create(path, Source, Source));
        CSharpAuthoringMutation invalid = workspace.Prepare(CSharpSourceTextPatchDocument.Create(
            path,
            Source,
            Source.Replace("return scaled;", "return MissingSymbol;", StringComparison.Ordinal)));

        Assert.Contains(stale.Diagnostics, static diagnostic =>
            diagnostic.Code == CSharpSourceTextPatchDiagnosticCodes.BaselineMismatch);
        Assert.Contains(unknown.Diagnostics, static diagnostic =>
            diagnostic.Code == CSharpSourceTextPatchDiagnosticCodes.SourceNotFound);
        Assert.Contains(noOp.Diagnostics, static diagnostic =>
            diagnostic.Code == CSharpSourceTextPatchDiagnosticCodes.NoChange);
        Assert.False(invalid.Succeeded);
        Assert.Contains(invalid.Diagnostics, static diagnostic =>
            diagnostic.Severity == UnifiedDiagnosticSeverity.Error);
        Assert.Equal(1, workspace.Current.Revision);
        Assert.Equal(Source, workspace.Current.Request.Sources[0].Source);
    }

    [Fact]
    public void SourceTextPatchJsonIsBoundedNormalizedAndRejectsUnknownProperties()
    {
        CSharpSourceTextPatchDocument patch = CSharpSourceTextPatchDocument.Create(
            "Tests\\Authoring\\DamagePolicy.cs",
            Source,
            Source.Replace("= 20;", "= 21;", StringComparison.Ordinal));

        string json = CSharpSourceTextPatchJson.Serialize(patch, writeIndented: false);
        CSharpSourceTextPatchDocument roundTrip = CSharpSourceTextPatchJson.Deserialize(json);

        Assert.Equal("Tests/Authoring/DamagePolicy.cs", roundTrip.SourcePath);
        Assert.Equal(CSharpSourceTextPatchJson.ComputeContentDigest(Source), roundTrip.BaselineContentDigest);
        Assert.Throws<JsonException>(() => CSharpSourceTextPatchJson.Deserialize(
            json[..^1] + ",\"unexpected\":true}"));
        Assert.Throws<JsonException>(() => CSharpSourceTextPatchJson.NormalizeAndValidate(
            patch with { SourcePath = "../DamagePolicy.cs" }));
        Assert.Throws<JsonException>(() => CSharpSourceTextPatchJson.NormalizeAndValidate(
            patch with { SourceText = new string('x', CSharpSourceTextPatchJson.MaximumSourceCharacters + 1) }));
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

    [Fact]
    public void CommittedTransactionCanBeUndoneAndRedoneWithoutChangingStableIdentities()
    {
        const string path = "Tests/Authoring/DamagePolicy.cs";
        var workspace = new CSharpAuthoringWorkspace(CSharpCompilationRequest.Single(
            Source,
            path,
            identitySidecar: IdentitySidecarDocument.CreateEmpty(path)));
        CSharpAuthoringSnapshot baseline = workspace.Current;
        ScriptStateDeclaration baselineState = baseline.Analysis.ScriptAssets!.Assets[0].States
            .Single(static state => state.Name == "BaseDamage");
        HirLiteralExpression clampMinimum = baseline.Analysis.Hir!
            .DescendantsAndSelf()
            .OfType<HirLiteralExpression>()
            .Single(static literal => literal.ValueText == "0f");
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            baseline.Analysis.ScriptAssets.SourceCompilationId,
            baseline.Analysis.ScriptAssets.SemanticConfigurationId,
            [new ReplaceLiteralPatchOperation(clampMinimum.Id, "0f", "10f")]);

        CSharpAuthoringMutation edit = workspace.Prepare(patch);
        workspace.Commit(edit, "txn-clamp-minimum", "Raise clamp minimum");

        Assert.Equal(2, workspace.Current.Revision);
        Assert.True(workspace.History.CanUndo);
        Assert.False(workspace.History.CanRedo);
        Assert.Equal("txn-clamp-minimum", workspace.History.UndoTransactionId);

        CSharpAuthoringHistoryMutation undo = workspace.PrepareUndo();
        workspace.Commit(undo);

        Assert.Equal(3, workspace.Current.Revision);
        Assert.Contains("Math.Clamp(scaled, 0f, MaxDamage)", workspace.Current.Request.Sources[0].Source);
        Assert.Equal(
            baselineState.Id,
            workspace.Current.Analysis.ScriptAssets!.Assets[0].States
                .Single(static state => state.Name == "BaseDamage").Id);
        Assert.False(workspace.History.CanUndo);
        Assert.True(workspace.History.CanRedo);

        CSharpAuthoringHistoryMutation redo = workspace.PrepareRedo();
        workspace.Commit(redo);

        Assert.Equal(4, workspace.Current.Revision);
        Assert.Contains("Math.Clamp(scaled, 10f, MaxDamage)", workspace.Current.Request.Sources[0].Source);
        Assert.True(workspace.History.CanUndo);
        Assert.False(workspace.History.CanRedo);
    }

    [Fact]
    public void NewTransactionAfterUndoClearsRedoHistory()
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
        workspace.Commit(workspace.Prepare(firstPatch), "txn-first", "First edit");
        workspace.Commit(workspace.PrepareUndo());

        CSharpAuthoringSnapshot restored = workspace.Current;
        HirLiteralExpression restoredClampMinimum = restored.Analysis.Hir!
            .DescendantsAndSelf()
            .OfType<HirLiteralExpression>()
            .Single(static literal => literal.ValueText == "0f");
        SemanticPatchDocument branchPatch = SemanticPatchDocument.Create(
            restored.Analysis.ScriptAssets!.SourceCompilationId,
            restored.Analysis.ScriptAssets.SemanticConfigurationId,
            [new ReplaceLiteralPatchOperation(restoredClampMinimum.Id, "0f", "20f")]);
        workspace.Commit(workspace.Prepare(branchPatch), "txn-branch", "Branch edit");

        Assert.Equal(4, workspace.Current.Revision);
        Assert.Contains("Math.Clamp(scaled, 20f, MaxDamage)", workspace.Current.Request.Sources[0].Source);
        Assert.False(workspace.History.CanRedo);
        Assert.Throws<InvalidOperationException>(() => workspace.PrepareRedo());
    }

    [Fact]
    public void DuplicateTransactionIdIsRejectedWithoutChangingTheWorkspace()
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
        workspace.Commit(workspace.Prepare(firstPatch), "txn-duplicate", "First edit");
        CSharpAuthoringSnapshot current = workspace.Current;
        HirLiteralExpression clampMaximum = current.Analysis.Hir!
            .DescendantsAndSelf()
            .OfType<HirLiteralExpression>()
            .Single(static literal => literal.ValueText == "10f");
        SemanticPatchDocument secondPatch = SemanticPatchDocument.Create(
            current.Analysis.ScriptAssets!.SourceCompilationId,
            current.Analysis.ScriptAssets.SemanticConfigurationId,
            [new ReplaceLiteralPatchOperation(clampMaximum.Id, "10f", "20f")]);
        CSharpAuthoringMutation duplicate = workspace.Prepare(secondPatch);

        Assert.Throws<InvalidOperationException>(() =>
            workspace.Commit(duplicate, "txn-duplicate", "Duplicate edit"));
        Assert.Same(current, workspace.Current);
        Assert.Equal(1, workspace.History.UndoDepth);
    }

    [Fact]
    public void DraftGroupsSequentialDeclarationAndBehaviorEditsIntoOneHistoryEntry()
    {
        const string path = "Tests/Authoring/DamagePolicy.cs";
        var workspace = new CSharpAuthoringWorkspace(CSharpCompilationRequest.Single(
            Source,
            path,
            identitySidecar: IdentitySidecarDocument.CreateEmpty(path)));
        CSharpAuthoringWorkspace draft = workspace.CreateDraft();
        ScriptStateDeclaration maxDamage = draft.Current.Analysis.ScriptAssets!.Assets[0].States
            .Single(static state => state.Name == "MaxDamage");
        DeclarationPatchDocument declarationPatch = DeclarationPatchDocument.Create(
            draft.Current.Analysis.ScriptAssets.SourceCompilationId,
            draft.Current.Analysis.ScriptAssets.SemanticConfigurationId,
            [new UpdateScriptStatePatchOperation(
                maxDamage.Id,
                Expected(maxDamage),
                new ScriptStatePatchSpec(
                    maxDamage.Name,
                    maxDamage.TypeMetadataName,
                    maxDamage.TypeKind,
                    Json("120"),
                    maxDamage.Exposure))]);
        draft.Commit(draft.Prepare(declarationPatch), "stage-state", "Stage state edit");

        HirLiteralExpression minimum = draft.Current.Analysis.Hir!
            .DescendantsAndSelf()
            .OfType<HirLiteralExpression>()
            .Single(static literal => literal.ValueText == "0f");
        SemanticPatchDocument behaviorPatch = SemanticPatchDocument.Create(
            draft.Current.Analysis.ScriptAssets!.SourceCompilationId,
            draft.Current.Analysis.ScriptAssets.SemanticConfigurationId,
            [new ReplaceLiteralPatchOperation(minimum.Id, "0f", "10f")]);
        draft.Commit(draft.Prepare(behaviorPatch), "stage-behavior", "Stage behavior edit");

        Assert.Equal(1, workspace.Current.Revision);
        Assert.Equal(3, draft.Current.Revision);
        Assert.DoesNotContain("= 120;", workspace.Current.Request.Sources[0].Source);
        CSharpAuthoringMutation grouped = workspace.PrepareDraftCommit(draft);
        Assert.Equal(1, grouped.BaselineRevision);
        Assert.Equal(2, grouped.Snapshot!.Revision);

        workspace.Commit(grouped, "group-damage-tuning", "Damage tuning");

        Assert.Equal(2, workspace.Current.Revision);
        Assert.Contains("= 120;", workspace.Current.Request.Sources[0].Source);
        Assert.Contains("Math.Clamp(scaled, 10f, MaxDamage)", workspace.Current.Request.Sources[0].Source);
        Assert.Equal(1, workspace.History.UndoDepth);
        Assert.Equal("group-damage-tuning", workspace.History.UndoTransactionId);
        Assert.Equal("Damage tuning", workspace.History.UndoDescription);

        workspace.Commit(workspace.PrepareUndo(), "undo-group-damage-tuning");

        Assert.Contains("= 999;", workspace.Current.Request.Sources[0].Source);
        Assert.Contains("Math.Clamp(scaled, 0f, MaxDamage)", workspace.Current.Request.Sources[0].Source);
    }

    [Fact]
    public void PersistedHistoryRestoresRevisionUndoRedoAndOperationIdempotency()
    {
        const string path = "Tests/Authoring/DamagePolicy.cs";
        var workspace = new CSharpAuthoringWorkspace(CSharpCompilationRequest.Single(
            Source,
            path,
            identitySidecar: IdentitySidecarDocument.CreateEmpty(path)));
        HirLiteralExpression minimum = workspace.Current.Analysis.Hir!
            .DescendantsAndSelf()
            .OfType<HirLiteralExpression>()
            .Single(static literal => literal.ValueText == "0f");
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            workspace.Current.Analysis.ScriptAssets!.SourceCompilationId,
            workspace.Current.Analysis.ScriptAssets.SemanticConfigurationId,
            [new ReplaceLiteralPatchOperation(minimum.Id, "0f", "10f")]);
        workspace.Commit(workspace.Prepare(patch), "txn-persisted", "Persisted edit");
        string json = CSharpAuthoringHistoryJson.Serialize(workspace.ExportHistory());

        CSharpAuthoringWorkspace restored = CSharpAuthoringWorkspace.Restore(
            workspace.Current.Request,
            CSharpAuthoringHistoryJson.Deserialize(json));

        Assert.Equal(2, restored.Current.Revision);
        Assert.Equal(1, restored.History.UndoDepth);
        Assert.Equal("txn-persisted", restored.History.UndoTransactionId);
        Assert.Equal("txn-persisted", restored.LastOperationId);
        Assert.True(restored.ContainsOperationId("txn-persisted"));
        Assert.Throws<InvalidOperationException>(() =>
            restored.Commit(
                restored.PrepareUndo(),
                "txn-persisted"));

        CSharpAuthoringHistoryMutation undo = restored.PrepareUndo();
        restored.Commit(undo, "op-persisted-undo");
        Assert.Contains("Math.Clamp(scaled, 0f, MaxDamage)", restored.Current.Request.Sources[0].Source);
        Assert.Equal(3, restored.Current.Revision);
        Assert.Equal(1, restored.History.RedoDepth);

        CSharpAuthoringWorkspace restoredAgain = CSharpAuthoringWorkspace.Restore(
            restored.Current.Request,
            CSharpAuthoringHistoryJson.Deserialize(
                CSharpAuthoringHistoryJson.Serialize(restored.ExportHistory())));
        restoredAgain.Commit(restoredAgain.PrepareRedo(), "op-persisted-redo");

        Assert.Equal(4, restoredAgain.Current.Revision);
        Assert.Contains(
            "Math.Clamp(scaled, 10f, MaxDamage)",
            restoredAgain.Current.Request.Sources[0].Source);
        Assert.Equal("op-persisted-redo", restoredAgain.LastOperationId);
    }

    [Fact]
    public void PersistedHistoryRejectsExternalSourceAndTamperedSnapshot()
    {
        const string path = "Tests/Authoring/DamagePolicy.cs";
        var workspace = new CSharpAuthoringWorkspace(CSharpCompilationRequest.Single(
            Source,
            path,
            identitySidecar: IdentitySidecarDocument.CreateEmpty(path)));
        HirLiteralExpression minimum = workspace.Current.Analysis.Hir!
            .DescendantsAndSelf()
            .OfType<HirLiteralExpression>()
            .Single(static literal => literal.ValueText == "0f");
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            workspace.Current.Analysis.ScriptAssets!.SourceCompilationId,
            workspace.Current.Analysis.ScriptAssets.SemanticConfigurationId,
            [new ReplaceLiteralPatchOperation(minimum.Id, "0f", "10f")]);
        workspace.Commit(workspace.Prepare(patch), "txn-history-integrity", "Integrity edit");
        CSharpAuthoringHistoryDocument history = workspace.ExportHistory();
        CSharpSourceDocument current = Assert.Single(workspace.Current.Request.Sources);
        var externallyChanged = new CSharpCompilationRequest(
            workspace.Current.Request.AssemblyName,
            [new CSharpSourceDocument(
                current.LogicalPath,
                current.Source + "\n// external source change\n",
                current.IdentitySidecar)],
            workspace.Current.Request.ReferencePaths,
            workspace.Current.Request.EffectCatalog,
            workspace.Current.Request.Settings,
            workspace.Current.Request.RuntimeReferencePaths,
            workspace.Current.Request.IncludeDefaultReferences);

        Assert.Throws<InvalidDataException>(() =>
            CSharpAuthoringWorkspace.Restore(externallyChanged, history));

        CSharpAuthoringHistorySnapshotDocument snapshot = history.Snapshots[0];
        CSharpAuthoringHistorySourceDocument source = snapshot.Sources[0];
        CSharpAuthoringHistorySnapshotDocument tampered = snapshot with
        {
            Sources = [source with { Source = source.Source + "\n// tampered\n" }],
        };
        CSharpAuthoringHistoryDocument tamperedHistory = history with
        {
            Snapshots = [tampered, .. history.Snapshots.Skip(1)],
        };

        Assert.Throws<JsonException>(() =>
            CSharpAuthoringHistoryJson.NormalizeAndValidate(tamperedHistory));
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
