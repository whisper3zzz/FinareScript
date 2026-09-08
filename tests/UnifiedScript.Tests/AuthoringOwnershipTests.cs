using System.Text.Json;
using UnifiedScript.Authoring;
using UnifiedScript.Core;
using UnifiedScript.CSharp;
using UnifiedScript.Graph;

namespace UnifiedScript.Tests;

public sealed class AuthoringOwnershipTests
{
    [Fact]
    public void AnalysisAndAuthoringSurfaceKeepTheActualSourceOwnerForSharedDeclarations()
    {
        const string source = """
            using System.Collections.Generic;
            using UnifiedScript;

            public abstract class SharedBase
            {
                [Expose]
                public int Seed { get; set; } = 3;

                [Expose]
                public IReadOnlyList<int> Values { get; set; } = new List<int> { 2, 4 };

                public int Hidden { get; set; } = 99;

                [Visual]
                public int ReadSeed(int value) { return Seed + value; }
            }

            [ScriptAsset("Game/ALeaf")]
            public sealed class ALeaf : SharedBase { }

            [ScriptAsset("Game/BLeaf")]
            public sealed class BLeaf : SharedBase { }
            """;

        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(source, "Shared.cs");
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));

        ScriptAssetDocument assets = analysis.ScriptAssets!;
        ScriptAssetDeclaration owner = Assert.Single(assets.Assets, asset =>
            asset.QualifiedName == "SharedBase");
        ScriptStateDeclaration seed = Assert.Single(owner.States, state => state.Name == "Seed");
        ScriptStateDeclaration values = Assert.Single(owner.States, state => state.Name == "Values");
        ScriptFunctionDeclaration readSeed = Assert.Single(owner.Functions, function =>
            function.Name == "ReadSeed");
        Assert.Equal(owner.Id, analysis.DeclarationOwners[seed.Id]);
        Assert.Equal(owner.Id, analysis.DeclarationOwners[readSeed.Id]);
        Assert.DoesNotContain(owner.States, state => state.Name == "Hidden");
        Assert.All(
            assets.Assets.Where(asset => asset.QualifiedName is "ALeaf" or "BLeaf"),
            asset =>
            {
                Assert.Contains(asset.States, state => state.Id == seed.Id);
                Assert.Contains(asset.States, state => state.Id == values.Id);
                Assert.Contains(asset.Functions, function => function.Id == readSeed.Id);
            });

        GraphDocument graph = new HirGraphProjector().Project(
            analysis.Hir!,
            analysis.Semantics!,
            analysis.DeclarationReferences);
        AuthoringSurfaceDocument surface = new AuthoringSurfaceProjector().Project(
            assets,
            graph,
            analysis.DeclarationOwners);
        AuthoringDeclarationOwner stateOwner = Assert.Single(
            surface.DeclarationOwners,
            item => item.DeclarationId == seed.Id);
        AuthoringDeclarationOwner functionOwner = Assert.Single(
            surface.DeclarationOwners,
            item => item.DeclarationId == readSeed.Id);
        Assert.Equal(owner.Id, stateOwner.OwnerAssetId);
        Assert.Equal(owner.Id, functionOwner.OwnerAssetId);

        AuthoringSurfaceDocument roundTripped = AuthoringSurfaceJson.Deserialize(
            AuthoringSurfaceJson.Serialize(surface));
        Assert.Equal(surface.DeclarationOwners, roundTripped.DeclarationOwners);
    }

    [Fact]
    public void SchemaV05RejectsAnOwnerThatDoesNotContainTheDeclarationAndRequiresSharedOwner()
    {
        const string source = """
            using UnifiedScript;
            public class SharedBase { [Expose] public int Seed { get; set; } = 3; }
            [ScriptAsset("Game/ALeaf")] public sealed class ALeaf : SharedBase { }
            [ScriptAsset("Game/BLeaf")] public sealed class BLeaf : SharedBase { }
            """;
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(source, "Shared.cs");
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        GraphDocument graph = new HirGraphProjector().Project(
            analysis.Hir!, analysis.Semantics!, analysis.DeclarationReferences);
        AuthoringSurfaceDocument surface = new AuthoringSurfaceProjector().Project(
            analysis.ScriptAssets!, graph, analysis.DeclarationOwners);
        AuthoringSurfaceDocument legacy = surface with
        {
            SchemaVersion = AuthoringSurfaceDocument.LegacySchemaVersion,
            DeclarationOwners = [],
        };
        Assert.Equal(
            AuthoringSurfaceDocument.LegacySchemaVersion,
            AuthoringSurfaceJson.Deserialize(AuthoringSurfaceJson.Serialize(legacy)).SchemaVersion);
        SemanticId stateId = surface.Assets
            .SelectMany(static asset => asset.States)
            .Select(static state => state.Id)
            .Distinct()
            .Single();

        Assert.Throws<JsonException>(() => AuthoringSurfaceJson.Serialize(
            surface with { DeclarationOwners = [] }));

        SemanticId unrelatedAsset = SemanticId.FromKey("authoring-owner:unrelated");
        Assert.Throws<JsonException>(() => AuthoringSurfaceJson.Serialize(
            surface with
            {
                DeclarationOwners = [new AuthoringDeclarationOwner(stateId, unrelatedAsset)],
            }));
    }

    [Fact]
    public void LegacyV04UniqueDocumentsRemainReadable()
    {
        AuthoringSurfaceDocument unique = new(
            AuthoringSurfaceDocument.LegacySchemaVersion,
            SemanticId.FromKey("authoring-owner:source"),
            SemanticId.FromKey("authoring-owner:configuration"),
            []);
        Assert.Equal(
            AuthoringSurfaceDocument.LegacySchemaVersion,
            AuthoringSurfaceJson.Deserialize(AuthoringSurfaceJson.Serialize(unique)).SchemaVersion);
    }

    [Fact]
    public void SplitPartialOwnerUsesExplicitClassIdAndPatchesOnlyTheOwnerDefault()
    {
        const string classId = "us_4a1f3c2d7e8b4a6190c5d8e2f7a314b6";
        const string overrideSource = """
            using UnifiedScript;
            namespace Game;

            [ScriptAsset("Game/AOverride")]
            public sealed class AOverride : SharedBase
            {
                public AOverride()
                {
                    base.Seed = 77;
                }
            }
            """;
        const string followerSource = """
            using UnifiedScript;
            namespace Game;

            [ScriptAsset("Game/BFollower")]
            public sealed class BFollower : SharedBase { }
            """;
        const string ownerSource = """
            using UnifiedScript;
            namespace Game;

            [ScriptAsset("Game/ZBase", ClassId = "us_4a1f3c2d7e8b4a6190c5d8e2f7a314b6")]
            public abstract partial class SharedBase { }
            """;
        const string stateSource = """
            using System.Collections.Generic;
            using UnifiedScript;
            namespace Game;

            public abstract partial class SharedBase
            {
                [Expose(InstanceEditable = true, Category = "Inherited")]
                public int Seed { get; set; } = 1;

                [Expose(InstanceEditable = true, Category = "Inherited")]
                public IReadOnlyList<int> Values { get; set; } = new List<int> { 2, 4 };

                protected SharedBase()
                {
                    Seed = 3;
                }

                [Visual]
                public int ReadSeed(int value)
                {
                    return Seed + value;
                }

                [Visual]
                public int Forward(int value)
                {
                    return ReadSeed(value);
                }
            }
            """;
        (string Path, string Source)[] sourceDocuments =
        [
            ("Game/AOverride.cs", overrideSource),
            ("Game/BFollower.cs", followerSource),
            ("Game/BaseOwner.cs", ownerSource),
            ("Game/BaseState.cs", stateSource),
        ];
        CSharpCompilationRequest initialRequest = new(
            "AuthoringInheritanceSplit",
            sourceDocuments.Select(item => new CSharpSourceDocument(
                item.Path,
                item.Source,
                IdentitySidecarDocument.CreateEmpty(item.Path))).ToArray());
        CSharpAnalysisResult initialized = new CSharpSemanticPipeline().Analyze(initialRequest);
        Assert.True(initialized.Succeeded, string.Join(Environment.NewLine, initialized.Diagnostics));

        CSharpCompilationRequest request = new(
            "AuthoringInheritanceSplit",
            sourceDocuments.Select(item => new CSharpSourceDocument(
                item.Path,
                item.Source,
                initialized.IdentitySidecars.Single(sidecar => sidecar.SourcePath == item.Path))).ToArray());
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(original.Succeeded, string.Join(Environment.NewLine, original.Diagnostics));

        ScriptAssetDocument assets = original.ScriptAssets!;
        ScriptAssetDeclaration owner = Assert.Single(assets.Assets, asset => asset.AssetPath == "Game/ZBase");
        Assert.Equal(new SemanticId(classId), owner.Id);
        ScriptStateDeclaration ownerSeed = Assert.Single(owner.States, state => state.Name == "Seed");
        Assert.Single(owner.States, state => state.Name == "Values");
        ScriptAssetDeclaration overrideAsset = Assert.Single(assets.Assets, asset => asset.AssetPath == "Game/AOverride");
        ScriptAssetDeclaration followerAsset = Assert.Single(assets.Assets, asset => asset.AssetPath == "Game/BFollower");
        Assert.Equal("Game/AOverride", assets.Assets[0].AssetPath);
        Assert.Equal(3, ownerSeed.DefaultValue.GetInt32());
        Assert.Equal(77, overrideAsset.States.Single(state => state.Id == ownerSeed.Id).DefaultValue.GetInt32());
        Assert.Equal(3, followerAsset.States.Single(state => state.Id == ownerSeed.Id).DefaultValue.GetInt32());
        Assert.Equal(owner.Id, original.DeclarationOwners[ownerSeed.Id]);
        Assert.DoesNotContain(owner.States, state => state.Name == "HiddenHelper");

        GraphDocument graph = new HirGraphProjector().Project(
            original.Hir!, original.Semantics!, original.DeclarationReferences);
        AuthoringSurfaceDocument surface = new AuthoringSurfaceProjector().Project(
            assets, graph, original.DeclarationOwners);
        AuthoringSurfaceDocument roundTripped = AuthoringSurfaceJson.Deserialize(
            AuthoringSurfaceJson.Serialize(surface));
        Assert.Equal(surface.DeclarationOwners, roundTripped.DeclarationOwners);
        Assert.All(
            roundTripped.Assets.SelectMany(asset => asset.States)
                .Where(state => state.Name == "Seed"),
            state => Assert.Equal(ownerSeed.Id, state.Id));

        DeclarationPatchDocument patch = DeclarationPatchDocument.Create(
            assets.SourceCompilationId,
            assets.SemanticConfigurationId,
            [new UpdateScriptStatePatchOperation(
                ownerSeed.Id,
                new ScriptStatePatchPrecondition(
                    ownerSeed.Name,
                    ownerSeed.TypeMetadataName,
                    ownerSeed.TypeKind,
                    ownerSeed.DefaultValue,
                    ownerSeed.Exposure),
                new ScriptStatePatchSpec(
                    "RenamedSeed",
                    "int",
                    ScriptStateTypeKind.Integer,
                    JsonSerializer.SerializeToElement(11),
                    ownerSeed.Exposure))]);
        CSharpPatchResult patched = new CSharpDeclarationPatchApplier().Apply(request, patch);
        Assert.True(patched.Succeeded, string.Join(Environment.NewLine, patched.Diagnostics));
        ScriptAssetDocument patchedAssets = patched.Analysis!.ScriptAssets!;
        Assert.All(patchedAssets.Assets, asset =>
            Assert.Equal(ownerSeed.Id, asset.States.Single(state => state.Name == "RenamedSeed").Id));
        Assert.Equal(11, patchedAssets.Assets.Single(asset => asset.AssetPath == "Game/ZBase")
            .States.Single(state => state.Name == "RenamedSeed").DefaultValue.GetInt32());
        Assert.Equal(11, patchedAssets.Assets.Single(asset => asset.AssetPath == "Game/BFollower")
            .States.Single(state => state.Name == "RenamedSeed").DefaultValue.GetInt32());
        Assert.Equal(77, patchedAssets.Assets.Single(asset => asset.AssetPath == "Game/AOverride")
            .States.Single(state => state.Name == "RenamedSeed").DefaultValue.GetInt32());
        Assert.Contains("base.RenamedSeed = 77", patched.PatchedRequest!.Sources
            .Single(source => source.LogicalPath == "Game/AOverride.cs").Source, StringComparison.Ordinal);
        Assert.Contains("RenamedSeed", patched.PatchedRequest.Sources
            .Single(source => source.LogicalPath == "Game/BaseState.cs").Source, StringComparison.Ordinal);

        AuthoringAssetSurface alteredStateAsset = surface.Assets.Single(asset => asset.AssetPath == "Game/BFollower") with
        {
            States = surface.Assets.Single(asset => asset.AssetPath == "Game/BFollower").States
                .Select(state => state.Id == ownerSeed.Id
                    ? state with { TypeMetadataName = "System.Int64" }
                    : state)
                .ToArray(),
        };
        Assert.Throws<JsonException>(() => AuthoringSurfaceJson.Serialize(surface with
        {
            Assets = surface.Assets
                .Select(asset => asset.AssetPath == alteredStateAsset.AssetPath ? alteredStateAsset : asset)
                .ToArray(),
        }));

        AuthoringFunctionSurface followerFunction = surface.Assets
            .Single(asset => asset.AssetPath == "Game/BFollower")
            .Functions[0];
        AuthoringActionSurface alteredAction = followerFunction.Actions[0] with
        {
            Summary = followerFunction.Actions[0].Summary + " (altered graph)",
        };
        AuthoringAssetSurface alteredFunctionAsset = surface.Assets.Single(asset => asset.AssetPath == "Game/BFollower") with
        {
            Functions = surface.Assets.Single(asset => asset.AssetPath == "Game/BFollower").Functions
                .Select(function => function.Id == followerFunction.Id
                    ? function with
                    {
                        Actions = function.Actions
                            .Select(action => action.Id == alteredAction.Id ? alteredAction : action)
                            .ToArray(),
                    }
                    : function)
                .ToArray(),
        };
        Assert.Throws<JsonException>(() => AuthoringSurfaceJson.Serialize(surface with
        {
            Assets = surface.Assets
                .Select(asset => asset.AssetPath == alteredFunctionAsset.AssetPath ? alteredFunctionAsset : asset)
                .ToArray(),
        }));

        SemanticId foreignAssetId = SemanticId.FromKey("authoring-inheritance:foreign");
        AuthoringAssetSurface foreignAsset = new(
            foreignAssetId,
            "Game.Foreign",
            "Game/Foreign",
            owner.Span,
            [],
            []);
        Assert.Throws<JsonException>(() => AuthoringSurfaceJson.Serialize(surface with
        {
            Assets = surface.Assets.Append(foreignAsset).ToArray(),
            DeclarationOwners = [new AuthoringDeclarationOwner(ownerSeed.Id, foreignAssetId)],
        }));
    }
}
