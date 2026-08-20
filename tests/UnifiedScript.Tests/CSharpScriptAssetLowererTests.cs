using UnifiedScript.Core;
using UnifiedScript.CSharp;

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
        Assert.Equal(
            "Game.Combat.DamagePolicy.ApplyDamage(System.Single,System.Boolean)",
            function.QualifiedName);
        Assert.Equal(["System.Single", "System.Boolean"],
            function.Parameters.Select(static parameter => parameter.TypeMetadataName));
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
}
