using UnifiedScript.CSharp;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;

namespace UnifiedScript.Tests;

public sealed class CSharpIdentityReconcilerTests
{
    private const string Source = """
        using UnifiedScript;

        namespace Game;

        public static class Calculator
        {
            [Visual]
            public static int Scale(int value)
            {
                return value + 1;
            }
        }
        """;

    [Fact]
    public void Analyze_InitializesAndReusesPersistentIdentitiesForUnchangedSource()
    {
        CSharpAnalysisResult initial = AnalyzeWithIdentity(Source, IdentitySidecarDocument.CreateEmpty(Path));
        IdentitySidecarDocument sidecar = Assert.Single(initial.IdentitySidecars);

        CSharpAnalysisResult repeated = AnalyzeWithIdentity(Source, sidecar);

        Assert.True(sidecar.Entries.Count > 0);
        Assert.Equal(1, sidecar.Generation);
        Assert.Equal(
            initial.Hir!.DescendantsAndSelf().Select(static node => node.Id),
            repeated.Hir!.DescendantsAndSelf().Select(static node => node.Id));
        Assert.Equal(
            IdentitySidecarJson.Serialize(sidecar),
            IdentitySidecarJson.Serialize(Assert.Single(repeated.IdentitySidecars)));
    }

    [Fact]
    public void Analyze_PreservesIdentitiesAcrossCommentAndWhitespaceChanges()
    {
        CSharpAnalysisResult initial = AnalyzeWithIdentity(Source, IdentitySidecarDocument.CreateEmpty(Path));
        IdentitySidecarDocument sidecar = Assert.Single(initial.IdentitySidecars);
        string formatted = Source.Replace(
            "        return value + 1;",
            "        // Graph layout should not depend on this comment.\n\n        return  value + 1;",
            StringComparison.Ordinal);

        CSharpAnalysisResult changed = AnalyzeWithIdentity(formatted, sidecar);

        Assert.True(changed.Succeeded, string.Join(Environment.NewLine, changed.Diagnostics));
        Assert.Equal(
            sidecar.Entries.Select(static entry => entry.Id).ToHashSet(),
            Assert.Single(changed.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet());
    }

    [Fact]
    public void Analyze_TombstonesIdentityForDeletedNode()
    {
        const string original = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run()
                {
                    int removed = 10;
                    return 20;
                }
            }
            """;
        const string changed = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run()
                {
                    return 20;
                }
            }
            """;
        CSharpAnalysisResult initial = AnalyzeWithIdentity(
            original,
            IdentitySidecarDocument.CreateEmpty(Path));
        HirLiteralExpression removedLiteral = Assert.Single(
            initial.Hir!.DescendantsAndSelf().OfType<HirLiteralExpression>(),
            static literal => literal.ValueText == "10");

        CSharpAnalysisResult result = AnalyzeWithIdentity(changed, Assert.Single(initial.IdentitySidecars));

        IdentitySidecarDocument sidecar = Assert.Single(result.IdentitySidecars);
        Assert.DoesNotContain(sidecar.Entries, entry => entry.Id == removedLiteral.Id);
        Assert.Contains(sidecar.Tombstones, tombstone => tombstone.Id == removedLiteral.Id);
    }

    [Fact]
    public void Analyze_DoesNotAdvanceSidecarWhenProposedSourceFailsCompilation()
    {
        CSharpAnalysisResult initial = AnalyzeWithIdentity(Source, IdentitySidecarDocument.CreateEmpty(Path));
        IdentitySidecarDocument sidecar = Assert.Single(initial.IdentitySidecars);
        string invalidSource = Source.Replace("value + 1", "value + \"invalid\"", StringComparison.Ordinal);
        var request = new CSharpCompilationRequest(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(Path, invalidSource, sidecar)]);

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);

        Assert.False(result.Succeeded);
        Assert.Equal(
            IdentitySidecarJson.Serialize(sidecar),
            IdentitySidecarJson.Serialize(Assert.Single(result.IdentitySidecars)));
    }

    [Fact]
    public void Analyze_AmbiguousEvidenceAllocatesNewIdentityInsteadOfGuessing()
    {
        CSharpAnalysisResult initial = AnalyzeWithIdentity(Source, IdentitySidecarDocument.CreateEmpty(Path));
        IdentitySidecarDocument sidecar = Assert.Single(initial.IdentitySidecars);
        IdentityEntry literalEntry = Assert.Single(sidecar.Entries, static entry => entry.Kind == "literal");
        SemanticId competingId = SemanticId.New();
        IdentitySidecarDocument ambiguous = sidecar with
        {
            Entries = [
                .. sidecar.Entries,
                literalEntry with
                {
                    Id = competingId,
                    Anchor = literalEntry.Anchor with { Start = literalEntry.Anchor.Start + 100 },
                },
            ],
        };
        string shifted = Source.Replace(
            "        return value + 1;",
            "        // Shift the literal without changing token context.\n        return value + 1;",
            StringComparison.Ordinal);

        CSharpAnalysisResult result = AnalyzeWithIdentity(shifted, ambiguous);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == IdentityDiagnosticCodes.AmbiguousMatch);
        HirLiteralExpression literal = Assert.Single(
            result.Hir!.DescendantsAndSelf().OfType<HirLiteralExpression>());
        Assert.NotEqual(literalEntry.Id, literal.Id);
        Assert.NotEqual(competingId, literal.Id);
    }

    [Fact]
    public void Analyze_RejectsPinWhoseStoredIdentityHasAnotherKind()
    {
        CSharpAnalysisResult initial = AnalyzeWithIdentity(Source, IdentitySidecarDocument.CreateEmpty(Path));
        IdentitySidecarDocument sidecar = Assert.Single(initial.IdentitySidecars);
        HirLiteralExpression literal = Assert.Single(
            initial.Hir!.DescendantsAndSelf().OfType<HirLiteralExpression>());
        IdentityEntry blockEntry = Assert.Single(sidecar.Entries, static entry => entry.Kind == "block");
        var request = new CSharpCompilationRequest(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(
                Path,
                Source,
                sidecar,
                [new CSharpIdentityPin(blockEntry.Id, "literal", literal.Span)])]);

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == IdentityDiagnosticCodes.InvalidPin);
        Assert.Equal(
            IdentitySidecarJson.Serialize(sidecar),
            IdentitySidecarJson.Serialize(Assert.Single(result.IdentitySidecars)));
    }

    [Fact]
    public void ApplyPatch_PreservesEveryPersistentIdentityWhenLiteralWidthChanges()
    {
        CSharpAnalysisResult initialized = AnalyzeWithIdentity(
            Source,
            IdentitySidecarDocument.CreateEmpty(Path));
        IdentitySidecarDocument sidecar = Assert.Single(initialized.IdentitySidecars);
        var persistedRequest = new CSharpCompilationRequest(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(Path, Source, sidecar)]);
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(persistedRequest);
        HirLiteralExpression literal = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirLiteralExpression>());
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceLiteralPatchOperation(literal.Id, "1", "100")]);

        CSharpPatchResult patched = new CSharpSourcePatchApplier().Apply(persistedRequest, patch);

        Assert.True(patched.Succeeded, string.Join(Environment.NewLine, patched.Diagnostics));
        HirLiteralExpression patchedLiteral = Assert.Single(
            patched.Analysis!.Hir!.DescendantsAndSelf().OfType<HirLiteralExpression>());
        Assert.Equal("100", patchedLiteral.ValueText);
        Assert.Equal(literal.Id, patchedLiteral.Id);
        Assert.Equal(
            Assert.Single(original.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet(),
            Assert.Single(patched.Analysis.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet());
        Assert.Empty(Assert.Single(patched.Analysis.IdentitySidecars).Tombstones);
    }

    private const string Path = "Game/Calculator.cs";

    private static CSharpAnalysisResult AnalyzeWithIdentity(
        string source,
        IdentitySidecarDocument sidecar)
    {
        var request = new CSharpCompilationRequest(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(Path, source, sidecar)]);
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        return result;
    }
}
