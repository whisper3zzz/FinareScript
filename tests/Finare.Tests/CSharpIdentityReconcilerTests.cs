using Finare.CSharp;
using Finare.Core;
using Finare.Core.Hir;

namespace Finare.Tests;

[Trait("Category", "Authoring")]
public sealed class CSharpIdentityReconcilerTests
{
    private const string Source = """
        using Finare;

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
        CSharpAnalysisResult deterministic = new CSharpSemanticPipeline().Analyze(Source, Path);
        CSharpAnalysisResult initial = AnalyzeWithIdentity(Source, IdentitySidecarDocument.CreateEmpty(Path));
        IdentitySidecarDocument sidecar = Assert.Single(initial.IdentitySidecars);

        CSharpAnalysisResult repeated = AnalyzeWithIdentity(Source, sidecar);

        Assert.True(sidecar.Entries.Count > 0);
        Assert.Equal(1, sidecar.Generation);
        Assert.Equal(
            deterministic.Hir!.DescendantsAndSelf().Select(static node => node.Id),
            initial.Hir!.DescendantsAndSelf().Select(static node => node.Id));
        Assert.Equal(
            initial.Hir!.DescendantsAndSelf().Select(static node => node.Id),
            repeated.Hir!.DescendantsAndSelf().Select(static node => node.Id));
        Assert.Equal(
            IdentitySidecarJson.Serialize(sidecar),
            IdentitySidecarJson.Serialize(Assert.Single(repeated.IdentitySidecars)));
    }

    [Fact]
    public void Analyze_DoesNotPersistStateIdentityForSameNamedForeignAttributes()
    {
        const string source = """
            using System;

            namespace Other
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class ScriptAssetAttribute : Attribute
                {
                    public ScriptAssetAttribute(string path) { }
                }

                [AttributeUsage(AttributeTargets.Property)]
                public sealed class ExposeAttribute : Attribute { }
            }

            namespace Game
            {
                [Other.ScriptAsset("Game/Foreign")]
                public sealed class Foreign
                {
                    [Other.Expose]
                    public int Value { get; set; } = 7;
                }
            }
            """;

        CSharpAnalysisResult result = AnalyzeWithIdentity(
            source,
            IdentitySidecarDocument.CreateEmpty(Path));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.DoesNotContain(
            Assert.Single(result.IdentitySidecars).Entries,
            static entry => entry.Kind == "state");
        Assert.Empty(result.ScriptAssets!.Assets);
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
            using Finare;

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
            using Finare;

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
            "Finare.Analysis",
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
            "Finare.Analysis",
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
            "Finare.Analysis",
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

    [Fact]
    public void Analyze_PreservesConditionalAccessIdentitiesAcrossFormattingChanges()
    {
        const string source = """
            #nullable enable

            using Finare;

            static class Rules
            {
                [Visual]
                static int? Measure(string? value)
                {
                    return value?.Length;
                }
            }
            """;
        const string changed = """
            #nullable enable

            using Finare;

            static class Rules
            {
                [Visual]
                static int? Measure(string? value)
                {
                    // The visual identity must not depend on line wrapping.
                    return value
                        ?.Length;
                }
            }
            """;
        CSharpAnalysisResult initial = AnalyzeWithIdentity(
            source,
            IdentitySidecarDocument.CreateEmpty(Path));
        IdentitySidecarDocument sidecar = Assert.Single(initial.IdentitySidecars);
        HirConditionalAccessExpression initialAccess = Assert.Single(
            initial.Hir!.DescendantsAndSelf().OfType<HirConditionalAccessExpression>());
        HirConditionalMemberBindingExpression initialBinding =
            Assert.IsType<HirConditionalMemberBindingExpression>(initialAccess.WhenNotNull);

        CSharpAnalysisResult formatted = AnalyzeWithIdentity(changed, sidecar);

        HirConditionalAccessExpression formattedAccess = Assert.Single(
            formatted.Hir!.DescendantsAndSelf().OfType<HirConditionalAccessExpression>());
        HirConditionalMemberBindingExpression formattedBinding =
            Assert.IsType<HirConditionalMemberBindingExpression>(formattedAccess.WhenNotNull);
        Assert.Equal(initialAccess.Id, formattedAccess.Id);
        Assert.Equal(initialBinding.Id, formattedBinding.Id);
        IdentitySidecarDocument formattedSidecar = Assert.Single(formatted.IdentitySidecars);
        Assert.Contains(formattedSidecar.Entries, static entry =>
            entry.Kind == "conditionalAccess");
        Assert.Contains(formattedSidecar.Entries, static entry =>
            entry.Kind == "conditionalMemberBinding");
        Assert.Empty(formattedSidecar.Tombstones);
    }

    [Fact]
    public void Analyze_PreservesLoopAndTransferIdentitiesAcrossFormattingChanges()
    {
        const string source = """
            using Finare;

            static class Rules
            {
                [Visual]
                static void Run(bool ready)
                {
                    while (ready)
                    {
                        if (ready)
                        {
                            continue;
                        }

                        break;
                    }
                }
            }
            """;
        const string changed = """
            using Finare;

            static class Rules
            {
                [Visual]
                static void Run(bool ready)
                {
                    // Formatting and comments must not recreate graph nodes.
                    while (ready)
                    {
                        if (ready) { continue; }

                        break;
                    }
                }
            }
            """;
        IdentitySidecarDocument empty = IdentitySidecarDocument.CreateEmpty(Path);
        CSharpAnalysisResult initial = AnalyzeWithIdentity(source, empty);
        IdentitySidecarDocument sidecar = Assert.Single(initial.IdentitySidecars);
        HirCompilationUnit initialHir = initial.Hir!;
        SemanticId loopId = Assert.Single(initialHir.DescendantsAndSelf().OfType<HirWhileStatement>()).Id;
        SemanticId breakId = Assert.Single(initialHir.DescendantsAndSelf().OfType<HirBreakStatement>()).Id;
        SemanticId continueId = Assert.Single(initialHir.DescendantsAndSelf().OfType<HirContinueStatement>()).Id;

        CSharpAnalysisResult formatted = AnalyzeWithIdentity(changed, sidecar);
        HirCompilationUnit formattedHir = formatted.Hir!;

        Assert.Equal(loopId, Assert.Single(
            formattedHir.DescendantsAndSelf().OfType<HirWhileStatement>()).Id);
        Assert.Equal(breakId, Assert.Single(
            formattedHir.DescendantsAndSelf().OfType<HirBreakStatement>()).Id);
        Assert.Equal(continueId, Assert.Single(
            formattedHir.DescendantsAndSelf().OfType<HirContinueStatement>()).Id);
        Assert.Empty(Assert.Single(formatted.IdentitySidecars).Tombstones);
    }

    [Fact]
    public void Analyze_PreservesForClauseIdentitiesAcrossFormattingChanges()
    {
        const string source = """
            using Finare;

            static class Rules
            {
                [Visual]
                static int Run(int count)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (i == 2)
                        {
                            continue;
                        }
                    }

                    return count;
                }
            }
            """;
        const string changed = """
            using Finare;

            static class Rules
            {
                [Visual]
                static int Run(int count)
                {
                    // Clause formatting cannot recreate the visual loop.
                    for ( int i=0 ; i < count ; i++ )
                    {
                        if (i == 2) { continue; }
                    }

                    return count;
                }
            }
            """;
        CSharpAnalysisResult initial = AnalyzeWithIdentity(
            source,
            IdentitySidecarDocument.CreateEmpty(Path));
        HirForStatement initialLoop = Assert.Single(
            initial.Hir!.DescendantsAndSelf().OfType<HirForStatement>());
        HirForVariableDeclaration initialDeclaration = Assert.IsType<HirForVariableDeclaration>(
            initialLoop.Declaration);
        HirForVariableDeclarator initialVariable = Assert.Single(initialDeclaration.Variables);
        HirUnaryExpression initialIncrement = Assert.IsType<HirUnaryExpression>(
            Assert.Single(initialLoop.Incrementors));

        CSharpAnalysisResult formatted = AnalyzeWithIdentity(
            changed,
            Assert.Single(initial.IdentitySidecars));
        HirForStatement formattedLoop = Assert.Single(
            formatted.Hir!.DescendantsAndSelf().OfType<HirForStatement>());
        HirForVariableDeclaration formattedDeclaration = Assert.IsType<HirForVariableDeclaration>(
            formattedLoop.Declaration);
        HirForVariableDeclarator formattedVariable = Assert.Single(formattedDeclaration.Variables);
        HirUnaryExpression formattedIncrement = Assert.IsType<HirUnaryExpression>(
            Assert.Single(formattedLoop.Incrementors));

        Assert.Equal(initialLoop.Id, formattedLoop.Id);
        Assert.Equal(initialDeclaration.Id, formattedDeclaration.Id);
        Assert.Equal(initialVariable.Id, formattedVariable.Id);
        Assert.Equal(initialIncrement.Id, formattedIncrement.Id);
        IdentitySidecarDocument sidecar = Assert.Single(formatted.IdentitySidecars);
        Assert.Contains(sidecar.Entries, static entry => entry.Kind == "for");
        Assert.Contains(sidecar.Entries, static entry => entry.Kind == "forVariableDeclaration");
        Assert.Contains(sidecar.Entries, static entry => entry.Kind == "forVariable");
        Assert.Empty(sidecar.Tombstones);
    }

    [Fact]
    public void Analyze_PreservesDoWhileAndTransferIdentitiesAcrossFormattingChanges()
    {
        const string source = """
            using Finare;

            static class Rules
            {
                [Visual]
                static void Run(bool ready)
                {
                    do
                    {
                        if (ready)
                        {
                            continue;
                        }

                        break;
                    }
                    while (ready);
                }
            }
            """;
        const string changed = """
            using Finare;

            static class Rules
            {
                [Visual]
                static void Run(bool ready)
                {
                    // Post-test formatting cannot recreate visual identities.
                    do { if (ready) { continue; }

                        break; }
                    while ( ready );
                }
            }
            """;
        CSharpAnalysisResult initial = AnalyzeWithIdentity(
            source,
            IdentitySidecarDocument.CreateEmpty(Path));
        HirDoWhileStatement initialLoop = Assert.Single(
            initial.Hir!.DescendantsAndSelf().OfType<HirDoWhileStatement>());
        HirBreakStatement initialBreak = Assert.Single(
            initialLoop.DescendantsAndSelf().OfType<HirBreakStatement>());
        HirContinueStatement initialContinue = Assert.Single(
            initialLoop.DescendantsAndSelf().OfType<HirContinueStatement>());

        CSharpAnalysisResult formatted = AnalyzeWithIdentity(
            changed,
            Assert.Single(initial.IdentitySidecars));
        HirDoWhileStatement formattedLoop = Assert.Single(
            formatted.Hir!.DescendantsAndSelf().OfType<HirDoWhileStatement>());

        Assert.Equal(initialLoop.Id, formattedLoop.Id);
        Assert.Equal(initialLoop.Body.Id, formattedLoop.Body.Id);
        Assert.Equal(
            initialBreak.Id,
            Assert.Single(formattedLoop.DescendantsAndSelf().OfType<HirBreakStatement>()).Id);
        Assert.Equal(
            initialContinue.Id,
            Assert.Single(formattedLoop.DescendantsAndSelf().OfType<HirContinueStatement>()).Id);
        IdentitySidecarDocument sidecar = Assert.Single(formatted.IdentitySidecars);
        Assert.Contains(sidecar.Entries, static entry => entry.Kind == "doWhile");
        Assert.Empty(sidecar.Tombstones);
    }

    [Fact]
    public void Analyze_PreservesForEachVariableCollectionAndBodyAcrossFormattingChanges()
    {
        const string source = """
            using Finare;

            static class Rules
            {
                [Visual]
                static void Run(string values)
                {
                    foreach (char value in values)
                    {
                        if (value == 'x')
                        {
                            continue;
                        }
                    }
                }
            }
            """;
        const string changed = """
            using Finare;

            static class Rules
            {
                [Visual]
                static void Run(string values)
                {
                    // Formatting does not recreate the visual foreach.
                    foreach( char value  in  values )
                    { if (value == 'x') { continue; } }
                }
            }
            """;
        CSharpAnalysisResult initial = AnalyzeWithIdentity(
            source,
            IdentitySidecarDocument.CreateEmpty(Path));
        HirForEachStatement initialLoop = Assert.Single(
            initial.Hir!.DescendantsAndSelf().OfType<HirForEachStatement>());

        CSharpAnalysisResult formatted = AnalyzeWithIdentity(
            changed,
            Assert.Single(initial.IdentitySidecars));
        HirForEachStatement formattedLoop = Assert.Single(
            formatted.Hir!.DescendantsAndSelf().OfType<HirForEachStatement>());

        Assert.Equal(initialLoop.Id, formattedLoop.Id);
        Assert.Equal(initialLoop.Variable.Id, formattedLoop.Variable.Id);
        Assert.Equal(initialLoop.Collection.Id, formattedLoop.Collection.Id);
        Assert.Equal(initialLoop.Body.Id, formattedLoop.Body.Id);
        Assert.Equal(
            Assert.Single(initialLoop.DescendantsAndSelf().OfType<HirContinueStatement>()).Id,
            Assert.Single(formattedLoop.DescendantsAndSelf().OfType<HirContinueStatement>()).Id);
        IdentitySidecarDocument sidecar = Assert.Single(formatted.IdentitySidecars);
        Assert.Contains(sidecar.Entries, static entry => entry.Kind == "forEach");
        Assert.Contains(sidecar.Entries, static entry => entry.Kind == "forEachVariable");
        Assert.Empty(sidecar.Tombstones);
    }

    private const string Path = "Game/Calculator.cs";

    private static CSharpAnalysisResult AnalyzeWithIdentity(
        string source,
        IdentitySidecarDocument sidecar)
    {
        var request = new CSharpCompilationRequest(
            "Finare.Analysis",
            [new CSharpSourceDocument(Path, source, sidecar)]);
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        return result;
    }
}
