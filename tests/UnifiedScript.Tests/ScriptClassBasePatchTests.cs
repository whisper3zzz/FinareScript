using UnifiedScript.Core;
using UnifiedScript.CSharp;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class ScriptClassBasePatchTests
{
    private const string AttributePath = "Game/PartialBase.Attributes.cs";
    private const string MembersPath = "Game/PartialBase.Members.cs";

    [Fact]
    public void UpdateClassBaseEditsTheOwnAttributeAcrossPartialSourcesAndPreservesIds()
    {
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreateRequest(
            """
            using UnifiedScript;

            namespace Game;

            // Keep this comment and the Script Asset declaration in the first partial.
            [ScriptAsset("Game/PartialBase")]
            public partial class PartialBase
            {
            }
            """,
            """
            using UnifiedScript;
            using U = UnifiedScript.Unreal;

            namespace Game;

            // The class binding intentionally lives in the other partial source.
            [U.UnrealScriptClass(
                U.UnrealScriptClassKind.Actor,
                GeneratedClassPath = "/Game/Tests/BP_PartialBase.BP_PartialBase_C")]
            public partial class PartialBase
            {
                // This member source must remain byte-for-byte unchanged.
                public int Count { get; set; } = 7;

                [Visual]
                public int ReadCount()
                {
                    return Count;
                }
            }
            """);
        ScriptAssetDeclaration before = Assert.Single(original.ScriptAssets!.Assets);
        ScriptClassBindingDeclaration initialBinding = Assert.IsType<ScriptClassBindingDeclaration>(
            before.ClassBinding);
        Assert.Null(initialBinding.BaseClassPath);
        string attributeSourceBefore = Source(request, AttributePath);
        SemanticId stateId = before.States.Single(state => state.Name == "Count").Id;
        SemanticId functionId = before.Functions.Single(function => function.Name == "ReadCount").Id;

        CSharpPatchResult added = ApplyBasePatch(
            request,
            original,
            before,
            initialBinding,
            "/Script/Engine.Actor");
        Assert.True(added.Succeeded, Diagnostics(added));
        AssertPatchedContract(added, before, stateId, functionId, "/Script/Engine.Actor");
        Assert.True(
            Source(added, MembersPath).Contains("BaseClassPath", StringComparison.Ordinal),
            Source(added, MembersPath));
        Assert.Equal(attributeSourceBefore, Source(added, AttributePath));

        ScriptAssetDeclaration changedBefore = Assert.Single(added.Analysis!.ScriptAssets!.Assets);
        CSharpPatchResult changed = ApplyBasePatch(
            added.PatchedRequest!,
            added.Analysis,
            changedBefore,
            changedBefore.ClassBinding!,
            "/Script/Engine.Object");
        Assert.True(changed.Succeeded, Diagnostics(changed));
        AssertPatchedContract(changed, before, stateId, functionId, "/Script/Engine.Object");
        Assert.True(
            Source(changed, MembersPath).Contains("/Script/Engine.Object", StringComparison.Ordinal),
            Source(changed, MembersPath));
        Assert.DoesNotContain("/Script/Engine.Actor", Source(changed, MembersPath));
        Assert.Equal(attributeSourceBefore, Source(changed, AttributePath));
        Assert.Contains("class binding intentionally", Source(changed, MembersPath));

        ScriptAssetDeclaration removedBefore = Assert.Single(changed.Analysis!.ScriptAssets!.Assets);
        CSharpPatchResult removed = ApplyBasePatch(
            changed.PatchedRequest!,
            changed.Analysis,
            removedBefore,
            removedBefore.ClassBinding!,
            null);
        Assert.True(removed.Succeeded, Diagnostics(removed));
        AssertPatchedContract(removed, before, stateId, functionId, null);
        Assert.DoesNotContain("BaseClassPath", Source(removed, MembersPath));
        Assert.Equal(attributeSourceBefore, Source(removed, AttributePath));
        Assert.Contains("class binding intentionally", Source(removed, MembersPath));
    }

    [Fact]
    public void MixedClassBaseAndExistingOperationsUseSchema04AndRoundTrip()
    {
        (CSharpCompilationRequest request, CSharpAnalysisResult analysis) = CreateRequest(
            """
            using UnifiedScript;
            using UnifiedScript.Unreal;
            [ScriptAsset("Game/Mixed")]
            [UnrealScriptClass(UnrealScriptClassKind.Actor)]
            public partial class Mixed { }
            """,
            """
            using UnifiedScript;
            public partial class Mixed
            {
                public int Value { get; set; } = 1;
            }
            """);
        ScriptAssetDeclaration asset = Assert.Single(analysis.ScriptAssets!.Assets);
        ScriptStateDeclaration state = Assert.Single(asset.States);
        Assert.Throws<System.Text.Json.JsonException>(() => DeclarationPatchDocument.Create(
            analysis.ScriptAssets.SourceCompilationId,
            analysis.ScriptAssets.SemanticConfigurationId,
            [new UpdateScriptClassBasePatchOperation(
                asset.Id,
                new ScriptAssetPatchPrecondition(asset.QualifiedName, asset.AssetPath),
                asset.ClassBinding!,
                null)]));
        DeclarationPatchDocument patch = DeclarationPatchDocument.Create(
            analysis.ScriptAssets.SourceCompilationId,
            analysis.ScriptAssets.SemanticConfigurationId,
            [
                new UpdateScriptClassBasePatchOperation(
                    asset.Id,
                    new ScriptAssetPatchPrecondition(asset.QualifiedName, asset.AssetPath),
                    asset.ClassBinding!,
                    "/Script/Engine.Actor"),
                new UpdateScriptStatePatchOperation(
                    state.Id,
                    new ScriptStatePatchPrecondition(
                        state.Name,
                        state.TypeMetadataName,
                        state.TypeKind,
                        state.DefaultValue,
                        state.Exposure),
                    new ScriptStatePatchSpec(
                        "RenamedValue",
                        "int",
                        ScriptStateTypeKind.Integer,
                        state.DefaultValue,
                        state.Exposure)),
            ]);

        Assert.Equal(DeclarationPatchDocument.ClassBindingSchemaVersion, patch.SchemaVersion);
        DeclarationPatchDocument roundTrip = DeclarationPatchJson.Deserialize(
            DeclarationPatchJson.Serialize(patch, writeIndented: false));
        Assert.Equal(patch.SchemaVersion, roundTrip.SchemaVersion);
        Assert.Contains(roundTrip.Operations, static operation =>
            operation is UpdateScriptClassBasePatchOperation);
        Assert.Contains(roundTrip.Operations, static operation =>
            operation is UpdateScriptStatePatchOperation);

        CSharpPatchResult applied = new CSharpDeclarationPatchApplier().Apply(request, roundTrip);
        Assert.True(applied.Succeeded, Diagnostics(applied));
        ScriptAssetDeclaration appliedAsset = Assert.Single(applied.Analysis!.ScriptAssets!.Assets);
        Assert.Equal("/Script/Engine.Actor", appliedAsset.ClassBinding!.BaseClassPath);
        Assert.Contains(appliedAsset.States, state => state.Name == "RenamedValue");

        DeclarationPatchDocument oldOnly = DeclarationPatchDocument.Create(
            analysis.ScriptAssets.SourceCompilationId,
            analysis.ScriptAssets.SemanticConfigurationId,
            [new UpdateScriptStatePatchOperation(
                state.Id,
                new ScriptStatePatchPrecondition(
                    state.Name,
                    state.TypeMetadataName,
                    state.TypeKind,
                    state.DefaultValue,
                    state.Exposure),
                new ScriptStatePatchSpec(
                    "LegacyRenamedValue",
                    "int",
                    ScriptStateTypeKind.Integer,
                    state.DefaultValue,
                    state.Exposure))]);
        DeclarationPatchDocument v04OldOnly = DeclarationPatchJson.Deserialize(
            DeclarationPatchJson.Serialize(oldOnly, writeIndented: false).Replace(
                DeclarationPatchDocument.CurrentSchemaVersion,
                DeclarationPatchDocument.ClassBindingSchemaVersion,
                StringComparison.Ordinal));
        Assert.Equal(DeclarationPatchDocument.CurrentSchemaVersion, v04OldOnly.SchemaVersion);

        Assert.Throws<System.Text.Json.JsonException>(() => DeclarationPatchDocument.Create(
            analysis.ScriptAssets.SourceCompilationId,
            analysis.ScriptAssets.SemanticConfigurationId,
            [
                patch.Operations.OfType<UpdateScriptClassBasePatchOperation>().Single(),
                new UpdateScriptAssetPatchOperation(
                    asset.Id,
                    new ScriptAssetPatchPrecondition(asset.QualifiedName, asset.AssetPath),
                    "Game/MixedRenamed"),
            ]));
    }

    [Fact]
    public void WorkspaceCommitUndoRedoPreservesClassBindingAndMemberIds()
    {
        (CSharpCompilationRequest request, _) = CreateRequest(
            """
            using UnifiedScript;
            using UnifiedScript.Unreal;
            [ScriptAsset("Game/WorkspaceBound")]
            [UnrealScriptClass(UnrealScriptClassKind.Actor)]
            public partial class WorkspaceBound { }
            """,
            """
            using UnifiedScript;
            public partial class WorkspaceBound
            {
                public int Value { get; set; } = 1;
            }
            """);
        var workspace = new CSharpAuthoringWorkspace(request);
        ScriptAssetDeclaration baselineAsset = Assert.Single(
            workspace.Current.Analysis.ScriptAssets!.Assets);
        SemanticId baselineStateId = Assert.Single(baselineAsset.States).Id;
        DeclarationPatchDocument patch = DeclarationPatchDocument.Create(
            workspace.Current.Analysis.ScriptAssets.SourceCompilationId,
            workspace.Current.Analysis.ScriptAssets.SemanticConfigurationId,
            [new UpdateScriptClassBasePatchOperation(
                baselineAsset.Id,
                new ScriptAssetPatchPrecondition(
                    baselineAsset.QualifiedName,
                    baselineAsset.AssetPath),
                baselineAsset.ClassBinding!,
                "/Script/Engine.Actor")]);

        CSharpAuthoringMutation mutation = workspace.Prepare(patch);
        Assert.True(mutation.Succeeded, Diagnostics(mutation.Diagnostics));
        workspace.Commit(mutation, "workspace-class-base", "Set class base");
        ScriptAssetDeclaration committed = Assert.Single(
            workspace.Current.Analysis.ScriptAssets!.Assets);
        Assert.Equal("/Script/Engine.Actor", committed.ClassBinding!.BaseClassPath);
        Assert.Equal(baselineStateId, Assert.Single(committed.States).Id);

        workspace.Commit(workspace.PrepareUndo(), "workspace-class-base-undo");
        ScriptAssetDeclaration undone = Assert.Single(
            workspace.Current.Analysis.ScriptAssets!.Assets);
        Assert.Null(undone.ClassBinding!.BaseClassPath);
        Assert.Equal(baselineStateId, Assert.Single(undone.States).Id);

        workspace.Commit(workspace.PrepareRedo(), "workspace-class-base-redo");
        ScriptAssetDeclaration redone = Assert.Single(
            workspace.Current.Analysis.ScriptAssets!.Assets);
        Assert.Equal("/Script/Engine.Actor", redone.ClassBinding!.BaseClassPath);
        Assert.Equal(baselineStateId, Assert.Single(redone.States).Id);
    }

    [Fact]
    public void UpdateClassBaseRejectsAnAssetWithoutItsOwnClassBindingAttribute()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            """
            using UnifiedScript;

            [ScriptAsset("Game/Unbound")]
            public sealed class Unbound
            {
                public int Value { get; set; } = 1;
            }
            """,
            "Game/Unbound.cs");
        CSharpAnalysisResult analysis = Analyze(request);
        ScriptAssetDeclaration asset = Assert.Single(analysis.ScriptAssets!.Assets);
        Assert.Null(asset.ClassBinding);
        DeclarationPatchDocument patch = DeclarationPatchDocument.Create(
            analysis.ScriptAssets.SourceCompilationId,
            analysis.ScriptAssets.SemanticConfigurationId,
            [new UpdateScriptClassBasePatchOperation(
                asset.Id,
                new ScriptAssetPatchPrecondition(asset.QualifiedName, asset.AssetPath),
                new ScriptClassBindingDeclaration("Actor"),
                "/Script/Engine.Actor")]);

        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);
        AssertFailure(result, DeclarationPatchDiagnosticCodes.DeclarationPreconditionFailed);
    }

    [Fact]
    public void RemovingClassBasePreservesCommentsAroundArgumentAndNeighbor()
    {
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreateRequest(
            """
            using UnifiedScript;
            using UnifiedScript.Unreal;
            [ScriptAsset("Game/Commented")]
            public partial class Commented { }
            """,
            """
            using UnifiedScript;
            using U = UnifiedScript.Unreal;
            [U.UnrealScriptClass(
                U.UnrealScriptClassKind.Actor,
                /* keep-before-name */ BaseClassPath /* keep-before-equals */ = /* keep-after-equals */ "/Script/Engine.Actor" /* after-value */, /* after-comma */
                GeneratedClassPath = "/Game/Tests/BP_Commented.BP_Commented_C")]
            public partial class Commented
            {
                public int Value { get; set; } = 1;
            }
            """);
        ScriptAssetDeclaration asset = Assert.Single(original.ScriptAssets!.Assets);
        CSharpPatchResult result = ApplyBasePatch(
            request,
            original,
            asset,
            asset.ClassBinding!,
            null);

        Assert.True(result.Succeeded, Diagnostics(result));
        string source = Source(result, MembersPath);
        Assert.DoesNotContain("BaseClassPath", source);
        Assert.Contains("keep-before-name", source);
        Assert.Contains("keep-before-equals", source);
        Assert.Contains("keep-after-equals", source);
        Assert.Equal(1, Count(source, "after-value"));
        Assert.Equal(1, Count(source, "after-comma"));
        Assert.Contains("GeneratedClassPath", source);

        (CSharpCompilationRequest lastRequest, CSharpAnalysisResult lastAnalysis) = CreateRequest(
            """
            using UnifiedScript;
            using UnifiedScript.Unreal;
            [ScriptAsset("Game/CommentedLast")]
            public partial class CommentedLast { }
            """,
            """
            using UnifiedScript;
            using U = UnifiedScript.Unreal;
            [U.UnrealScriptClass(
                U.UnrealScriptClassKind.Actor,
                GeneratedClassPath = "/Game/Tests/BP_CommentedLast.BP_CommentedLast_C",
                BaseClassPath /* last-before-equals */ = /* last-after-equals */ "/Script/Engine.Actor" /* last-after-value */)]
            public partial class CommentedLast { }
            """);
        ScriptAssetDeclaration lastAsset = Assert.Single(lastAnalysis.ScriptAssets!.Assets);
        CSharpPatchResult lastRemoved = ApplyBasePatch(
            lastRequest,
            lastAnalysis,
            lastAsset,
            lastAsset.ClassBinding!,
            null);
        Assert.True(lastRemoved.Succeeded, Diagnostics(lastRemoved));
        string lastSource = Source(lastRemoved, MembersPath);
        Assert.DoesNotContain("BaseClassPath", lastSource);
        Assert.Equal(1, Count(lastSource, "last-before-equals"));
        Assert.Equal(1, Count(lastSource, "last-after-equals"));
        Assert.Equal(1, Count(lastSource, "last-after-value"));
        Assert.Contains("GeneratedClassPath", lastSource);
    }

    [Fact]
    public void ReplacingClassBasePreservesCommentsInsideConstantExpression()
    {
        (CSharpCompilationRequest request, CSharpAnalysisResult analysis) = CreateRequest(
            """
            using UnifiedScript;
            using UnifiedScript.Unreal;
            [ScriptAsset("Game/ExpressionBound")]
            public partial class ExpressionBound { }
            """,
            """
            using UnifiedScript;
            using U = UnifiedScript.Unreal;
            [U.UnrealScriptClass(
                U.UnrealScriptClassKind.Actor,
                BaseClassPath = (
                    "/Script/Engine." // keep-parent-note
                    + "Actor"),
                GeneratedClassPath = "/Game/Tests/BP_ExpressionBound.BP_ExpressionBound_C")]
            public partial class ExpressionBound { }
            """);
        ScriptAssetDeclaration asset = Assert.Single(analysis.ScriptAssets!.Assets);
        Assert.Equal("/Script/Engine.Actor", asset.ClassBinding!.BaseClassPath);
        CSharpPatchResult result = ApplyBasePatch(
            request,
            analysis,
            asset,
            asset.ClassBinding,
            "/Script/Engine.Object");

        Assert.True(result.Succeeded, Diagnostics(result));
        string source = Source(result, MembersPath);
        Assert.Contains("/Script/Engine.Object", source);
        Assert.Equal(1, Count(source, "keep-parent-note"));
        Assert.Equal("/Script/Engine.Object", Assert.Single(
            result.Analysis!.ScriptAssets!.Assets).ClassBinding!.BaseClassPath);
    }

    [Theory]
    [InlineData(DeclarationPatchDocument.LegacySchemaVersion)]
    [InlineData(DeclarationPatchDocument.EventSchemaVersion)]
    [InlineData(DeclarationPatchDocument.CurrentSchemaVersion)]
    public void UpdateClassBaseRejectsStaleBindingInvalidPathMissingOwnerAndOldSchemaWithoutPublishing(
        string oldSchema)
    {
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreateRequest(
            """
            using UnifiedScript;
            using UnifiedScript.Unreal;

            [ScriptAsset("Game/Bound")]
            [UnrealScriptClass(UnrealScriptClassKind.Actor, BaseClassPath = "/Script/Engine.Actor")]
            public partial class Bound
            {
                public int Value { get; set; } = 1;
            }
            """,
            """
            using UnifiedScript;

            public partial class Bound
            {
                // A separate partial still contributes to the same declaration.
                public int Other { get; set; } = 2;
            }
            """);
        ScriptAssetDeclaration asset = Assert.Single(original.ScriptAssets!.Assets);
        ScriptClassBindingDeclaration binding = asset.ClassBinding!;
        string originalAttribute = Source(request, AttributePath);
        string originalMembers = Source(request, MembersPath);

        CSharpPatchResult stale = ApplyBasePatch(
            request,
            original,
            asset,
            binding with { GeneratedClassPath = "/Game/Stale.Stale_C" },
            "/Script/Engine.Object");
        AssertFailure(stale, DeclarationPatchDiagnosticCodes.DeclarationPreconditionFailed);
        Assert.Equal(originalAttribute, Source(request, AttributePath));
        Assert.Equal(originalMembers, Source(request, MembersPath));

        CSharpPatchResult invalidPath = ApplyBasePatch(
            request,
            original,
            asset,
            binding,
            "/Script/Engine.Actor/Invalid");
        Assert.False(invalidPath.Succeeded);
        Assert.Null(invalidPath.PatchedRequest);
        Assert.Contains(invalidPath.Diagnostics, diagnostic =>
            diagnostic.Code == DeclarationDiagnosticCodes.InvalidScriptClassBinding);
        Assert.Equal(originalAttribute, Source(request, AttributePath));

        DeclarationPatchDocument oldSchemaPatch = new(
            oldSchema,
            original.ScriptAssets.SourceCompilationId,
            original.ScriptAssets.SemanticConfigurationId,
            [new UpdateScriptClassBasePatchOperation(
                asset.Id,
                new ScriptAssetPatchPrecondition(asset.QualifiedName, asset.AssetPath),
                binding,
                "/Script/Engine.Object")]);
        Assert.Throws<System.Text.Json.JsonException>(() => DeclarationPatchJson.Serialize(oldSchemaPatch));
    }

    private static CSharpPatchResult ApplyBasePatch(
        CSharpCompilationRequest request,
        CSharpAnalysisResult analysis,
        ScriptAssetDeclaration asset,
        ScriptClassBindingDeclaration expectedBinding,
        string? replacement)
    {
        DeclarationPatchDocument patch = DeclarationPatchDocument.Create(
            analysis.ScriptAssets!.SourceCompilationId,
            analysis.ScriptAssets.SemanticConfigurationId,
            [new UpdateScriptClassBasePatchOperation(
                asset.Id,
                new ScriptAssetPatchPrecondition(asset.QualifiedName, asset.AssetPath),
                expectedBinding,
                replacement)]);
        Assert.Equal(DeclarationPatchDocument.ClassBindingSchemaVersion, patch.SchemaVersion);
        return new CSharpDeclarationPatchApplier().Apply(request, patch);
    }

    private static void AssertPatchedContract(
        CSharpPatchResult result,
        ScriptAssetDeclaration before,
        SemanticId stateId,
        SemanticId functionId,
        string? expectedBase)
    {
        ScriptAssetDeclaration after = Assert.Single(result.Analysis!.ScriptAssets!.Assets);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.QualifiedName, after.QualifiedName);
        Assert.Equal(before.AssetPath, after.AssetPath);
        Assert.Equal(expectedBase, after.ClassBinding!.BaseClassPath);
        Assert.Equal(before.ClassBinding!.Kind, after.ClassBinding.Kind);
        Assert.Equal(before.ClassBinding.GeneratedClassPath, after.ClassBinding.GeneratedClassPath);
        Assert.Equal(stateId, after.States.Single(state => state.Name == "Count").Id);
        Assert.Equal(functionId, after.Functions.Single(function => function.Name == "ReadCount").Id);
    }

    private static (CSharpCompilationRequest Request, CSharpAnalysisResult Analysis) CreateRequest(
        string attributeSource,
        string membersSource)
    {
        CSharpSourceDocument[] sources =
        [
            new(AttributePath, attributeSource, IdentitySidecarDocument.CreateEmpty(AttributePath)),
            new(MembersPath, membersSource, IdentitySidecarDocument.CreateEmpty(MembersPath)),
        ];
        CSharpCompilationRequest identityRequest = new(
            "ScriptClassBasePatchTests",
            sources,
            [typeof(UnrealObjectScript).Assembly.Location]);
        CSharpAnalysisResult identity = Analyze(identityRequest);
        CSharpSourceDocument[] persistedSources = sources.Select(source =>
        {
            IdentitySidecarDocument sidecar = identity.IdentitySidecars.Single(
                candidate => candidate.SourcePath == source.LogicalPath);
            return new CSharpSourceDocument(source.LogicalPath, source.Source, sidecar);
        }).ToArray();
        CSharpCompilationRequest request = new(
            identityRequest.AssemblyName,
            persistedSources,
            identityRequest.ReferencePaths,
            identityRequest.EffectCatalog,
            identityRequest.Settings,
            identityRequest.RuntimeReferencePaths,
            identityRequest.IncludeDefaultReferences);
        return (request, Analyze(request));
    }

    private static CSharpAnalysisResult Analyze(CSharpCompilationRequest request)
    {
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(result.Succeeded, Diagnostics(result));
        return result;
    }

    private static string Source(CSharpCompilationRequest request, string path) =>
        request.Sources.Single(source => source.LogicalPath == path).Source;

    private static string Source(CSharpPatchResult result, string path) =>
        Source(result.PatchedRequest!, path);

    private static string Diagnostics(CSharpPatchResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics);

    private static string Diagnostics(IReadOnlyList<UnifiedDiagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics);

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static string Diagnostics(CSharpAnalysisResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics);

    private static void AssertFailure(CSharpPatchResult result, string code)
    {
        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }
}
