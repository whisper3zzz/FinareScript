using Finare.Core;
using Finare.CSharp;

namespace Finare.Tests;

[Trait("Category", "Authoring")]
public sealed class CSharpSourceIdentityTransitionPlannerTests
{
    private const string Path = "Game/ActiveBase.cs";

    private const string Source = """
        using Finare;

        namespace Game;

        [ScriptAsset("Game/ActiveBase")]
        public class ActiveBase
        {
            [Expose]
            public int Seed { get; set; } = 3;

            [Visual]
            public int ReadSeed(int value)
            {
                return Seed + value;
            }
        }
        """;

    [Fact]
    public void RenamePinsEveryLiveIdentityAfterClassAndAttributeChanges()
    {
        CSharpCompilationRequest request = CreatePersistentRequest(Source, Path);
        const string renamedPath = "Game/RenamedBase.cs";
        const string renamedSource = """
            using Finare;

            namespace Game;

            [ScriptAsset("Game/RenamedBase")]
            public class RenamedBase
            {
                [Expose]
                public int Seed { get; set; } = 3;

                [Visual]
                public int ReadSeed(int value)
                {
                    return Seed + value;
                }
            }
            """;

        IdentitySidecarDocument before = request.Sources.Single().IdentitySidecar!;
        CSharpSourceIdentityTransitionPlanResult result =
            new CSharpSourceIdentityTransitionPlanner().Plan(
                request,
                [new CSharpSourceIdentityTransition(Path, renamedPath, renamedSource, true)]);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        IdentitySidecarDocument after = Assert.Single(result.IdentitySidecars);
        Assert.Equal(renamedPath, after.SourcePath);
        Assert.Equal(
            before.Entries.Select(static entry => entry.Id).ToHashSet(),
            after.Entries.Select(static entry => entry.Id).ToHashSet());
        Assert.NotEqual(before.SourceDigest, after.SourceDigest);
        Assert.Contains(after.Entries, entry =>
            entry.Kind == "state" && entry.Owner.Contains("RenamedBase", StringComparison.Ordinal));
        Assert.Contains(result.Analysis!.ScriptAssets!.Assets,
            asset => asset.AssetPath == "Game/RenamedBase" &&
                asset.States.Any(state => state.Name == "Seed"));
        Assert.DoesNotContain(result.Request!.Sources, source => source.LogicalPath == Path);
        Assert.Contains(result.Request.Sources, source => source.LogicalPath == renamedPath);
    }

    [Fact]
    public void RenameReconcilesAStaleSidecarBeforePinningTheNewSource()
    {
        CSharpAnalysisResult initialized = AnalyzeWithEmptySidecar(Source, Path);
        IdentitySidecarDocument stale = Assert.Single(initialized.IdentitySidecars);
        string edited = Source.Replace("= 3", "= 4", StringComparison.Ordinal);
        CSharpCompilationRequest request = new(
            "TransitionFixture",
            [new CSharpSourceDocument(Path, edited, stale)]);
        CSharpAnalysisResult reconciled = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(reconciled.Succeeded, string.Join(Environment.NewLine, reconciled.Diagnostics));
        IdentitySidecarDocument current = Assert.Single(reconciled.IdentitySidecars);

        string targetPath = "Game/StaleRenamed.cs";
        string target = edited.Replace("ActiveBase", "StaleRenamed", StringComparison.Ordinal)
            .Replace("Game/ActiveBase", "Game/StaleRenamed", StringComparison.Ordinal);
        CSharpSourceIdentityTransitionPlanResult result =
            new CSharpSourceIdentityTransitionPlanner().Plan(
                request,
                [new CSharpSourceIdentityTransition(Path, targetPath, target, true)]);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(
            current.Entries.Select(static entry => entry.Id).ToHashSet(),
            Assert.Single(result.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet());
    }

    [Fact]
    public void RenameMayKeepTheLogicalPathWhenOnlyTheSourceIdentityChanges()
    {
        CSharpCompilationRequest request = CreatePersistentRequest(Source, Path);
        string edited = Source.Replace("Game/ActiveBase", "Game/ActiveBaseRenamed", StringComparison.Ordinal)
            .Replace("class ActiveBase", "class ActiveBaseRenamed", StringComparison.Ordinal);

        CSharpSourceIdentityTransitionPlanResult result =
            new CSharpSourceIdentityTransitionPlanner().Plan(
                request,
                [new CSharpSourceIdentityTransition(Path, Path, edited, true)]);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(Path, Assert.Single(result.IdentitySidecars).SourcePath);
        Assert.Equal(
            request.Sources.Single().IdentitySidecar!.Entries.Select(static entry => entry.Id).ToHashSet(),
            Assert.Single(result.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet());
    }

    [Fact]
    public void DuplicateKeepsOriginalAndAllocatesIndependentIdsForRenamedSource()
    {
        CSharpCompilationRequest request = CreatePersistentRequest(Source, Path);
        const string duplicatePath = "Game/DuplicateBase.cs";
        string duplicateSource = Source.Replace("ActiveBase", "DuplicateBase", StringComparison.Ordinal)
            .Replace("Game/ActiveBase", "Game/DuplicateBase", StringComparison.Ordinal);

        CSharpSourceIdentityTransitionPlanResult result =
            new CSharpSourceIdentityTransitionPlanner().Plan(
                request,
                [new CSharpSourceIdentityTransition(Path, duplicatePath, duplicateSource, false)]);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(2, result.Request!.Sources.Count);
        Assert.Equal(2, result.Analysis!.IdentitySidecars.Count);
        IdentitySidecarDocument original = result.Analysis.IdentitySidecars.Single(
            sidecar => sidecar.SourcePath == Path);
        IdentitySidecarDocument duplicate = result.Analysis.IdentitySidecars.Single(
            sidecar => sidecar.SourcePath == duplicatePath);
        Assert.Empty(original.Entries.Select(static entry => entry.Id)
            .Intersect(duplicate.Entries.Select(static entry => entry.Id)));
        Assert.Equal(Source, result.Request.Sources.Single(source => source.LogicalPath == Path).Source);
    }

    [Fact]
    public void RenamePreservesRepeatedGraphNodeIdentitiesAcrossSources()
    {
        string source = Source.Replace(
            "return Seed + value;",
            "return Seed + value + Seed + value;",
            StringComparison.Ordinal);
        CSharpCompilationRequest request = CreatePersistentRequest(source, Path);
        string targetPath = "Game/RepeatedRenamed.cs";
        string target = source.Replace("ActiveBase", "RepeatedRenamed", StringComparison.Ordinal)
            .Replace("Game/ActiveBase", "Game/RepeatedRenamed", StringComparison.Ordinal);

        CSharpSourceIdentityTransitionPlanResult result =
            new CSharpSourceIdentityTransitionPlanner().Plan(
                request,
                [new CSharpSourceIdentityTransition(Path, targetPath, target, true)]);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        IdentitySidecarDocument before = request.Sources.Single().IdentitySidecar!;
        IdentitySidecarDocument after = Assert.Single(result.IdentitySidecars);
        Assert.Equal(
            before.Entries.Select(static entry => entry.Id).ToHashSet(),
            after.Entries.Select(static entry => entry.Id).ToHashSet());
        Assert.True(before.Entries.Count(entry => entry.Kind == "identifier") >= 4);
    }

    [Fact]
    public void BatchRenameMapsPartialDeclarationsAndKeepsExternalOwnerState()
    {
        const string basePath = "Game/BaseState.cs";
        const string methodsPath = "Game/BaseMethods.cs";
        const string leafPath = "Game/Leaf.cs";
        const string baseSource = """
            using Finare;
            namespace Game;
            public partial class SharedBase
            {
                [Expose]
                public int Seed { get; set; } = 3;
            }
            """;
        const string methodsSource = """
            using Finare;
            namespace Game;
            public partial class SharedBase
            {
                [Visual]
                public int ReadSeed(int value) { return Seed + value; }
            }
            """;
        const string leafSource = """
            using Finare;
            namespace Game;
            [ScriptAsset("Game/Leaf")]
            public sealed class Leaf : SharedBase { }
            """;
        (string Path, string Source)[] documents =
        [
            (basePath, baseSource),
            (methodsPath, methodsSource),
            (leafPath, leafSource),
        ];
        CSharpCompilationRequest request = CreatePersistentRequest(documents);
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(original.Succeeded, string.Join(Environment.NewLine, original.Diagnostics));
        SemanticId originalSeed = original.ScriptAssets!.Assets
            .SelectMany(static asset => asset.States)
            .Where(static state => state.Name == "Seed")
            .Select(static state => state.Id)
            .Distinct()
            .Single();
        IdentitySidecarDocument baseSidecar = request.Sources.Single(
            source => source.LogicalPath == basePath).IdentitySidecar!;

        CSharpSourceIdentityTransitionPlanResult result =
            new CSharpSourceIdentityTransitionPlanner().Plan(
                request,
                [
                    new CSharpSourceIdentityTransition(
                        basePath,
                        basePath,
                        baseSource.Replace("SharedBase", "RenamedBase", StringComparison.Ordinal),
                        true),
                    new CSharpSourceIdentityTransition(
                        methodsPath,
                        "Game/RenamedMethods.cs",
                        methodsSource.Replace("SharedBase", "RenamedBase", StringComparison.Ordinal),
                        true),
                    new CSharpSourceIdentityTransition(
                        leafPath,
                        "Game/RenamedLeaf.cs",
                        leafSource.Replace("SharedBase", "RenamedBase", StringComparison.Ordinal),
                        true),
                ]);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(
            baseSidecar.Entries.Select(static entry => entry.Id).ToHashSet(),
            result.Analysis!.IdentitySidecars.Single(sidecar => sidecar.SourcePath == basePath)
                .Entries.Select(static entry => entry.Id).ToHashSet());
        Assert.Equal(
            request.Sources.Single(source => source.LogicalPath == methodsPath).IdentitySidecar!
                .Entries.Select(static entry => entry.Id).ToHashSet(),
            result.Analysis.IdentitySidecars.Single(sidecar => sidecar.SourcePath == "Game/RenamedMethods.cs")
                .Entries.Select(static entry => entry.Id).ToHashSet());
        Assert.Contains(
            result.Analysis.IdentitySidecars.Single(sidecar => sidecar.SourcePath == basePath).Entries,
            entry => entry.Kind == "state" && entry.Owner.Contains("RenamedBase", StringComparison.Ordinal));
        Assert.Contains(
            result.Analysis.IdentitySidecars.Single(sidecar => sidecar.SourcePath == "Game/RenamedMethods.cs").Entries,
            entry => entry.Kind == "function" && entry.Owner.Contains("RenamedBase", StringComparison.Ordinal));
        Assert.Equal(
            originalSeed,
            result.Analysis.ScriptAssets!.Assets.SelectMany(static asset => asset.States)
                .Where(static state => state.Name == "Seed")
                .Select(static state => state.Id)
                .Distinct()
                .Single());
        Assert.Contains(result.Analysis.ScriptAssets.Assets,
            asset => asset.QualifiedName == "Game.RenamedBase" || asset.QualifiedName == "RenamedBase");
        Assert.Contains(result.Request!.Sources, source => source.LogicalPath == "Game/RenamedMethods.cs");
        Assert.Contains(result.Request.Sources, source => source.LogicalPath == "Game/RenamedLeaf.cs");
    }

    [Fact]
    public void DuplicateWithIdenticalTypeIsRejectedByTheSemanticCompilation()
    {
        CSharpCompilationRequest request = CreatePersistentRequest(Source, Path);
        const string duplicatePath = "Game/IdenticalDuplicate.cs";

        CSharpSourceIdentityTransitionPlanResult result =
            new CSharpSourceIdentityTransitionPlanner().Plan(
                request,
                [new CSharpSourceIdentityTransition(Path, duplicatePath, Source, false)]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Request);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == CSharpSourceIdentityTransitionPlanner.TransitionPipelineCode);
    }

    [Fact]
    public void DuplicateDerivedSourceGetsIndependentOwnIdsButSharesExternalBaseState()
    {
        const string basePath = "Game/Base.cs";
        const string derivedPath = "Game/Derived.cs";
        const string baseSource = """
            using Finare;
            namespace Game;
            [ScriptAsset("Game/Base")]
            public class Base
            {
                [Expose]
                public int Seed { get; set; } = 3;
                [Visual]
                public int ReadSeed(int value) { return Seed + value; }
            }
            """;
        const string derivedSource = """
            using Finare;
            namespace Game;
            [ScriptAsset("Game/Derived")]
            public sealed class Derived : Base
            {
                [Visual]
                public int ReadDerived(int value) { return ReadSeed(value) + 1; }
            }
            """;
        CSharpCompilationRequest request = CreatePersistentRequest(
        [
            (basePath, baseSource),
            (derivedPath, derivedSource),
        ]);
        IdentitySidecarDocument baseSidecar = request.Sources.Single(
            source => source.LogicalPath == basePath).IdentitySidecar!;
        IdentitySidecarDocument derivedSidecar = request.Sources.Single(
            source => source.LogicalPath == derivedPath).IdentitySidecar!;
        string duplicatePath = "Game/DerivedCopy.cs";
        string duplicateSource = derivedSource
            .Replace("Game/Derived", "Game/DerivedCopy", StringComparison.Ordinal)
            .Replace("class Derived", "class DerivedCopy", StringComparison.Ordinal);

        CSharpSourceIdentityTransitionPlanResult result =
            new CSharpSourceIdentityTransitionPlanner().Plan(
                request,
                [new CSharpSourceIdentityTransition(
                    derivedPath, duplicatePath, duplicateSource, false)]);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(
            baseSidecar.Entries.Select(static entry => entry.Id).ToHashSet(),
            result.Analysis!.IdentitySidecars.Single(sidecar => sidecar.SourcePath == basePath)
                .Entries.Select(static entry => entry.Id).ToHashSet());
        Assert.Equal(
            derivedSidecar.Entries.Select(static entry => entry.Id).ToHashSet(),
            result.Analysis.IdentitySidecars.Single(sidecar => sidecar.SourcePath == derivedPath)
                .Entries.Select(static entry => entry.Id).ToHashSet());
        IdentitySidecarDocument duplicateSidecar = result.Analysis.IdentitySidecars.Single(
            sidecar => sidecar.SourcePath == duplicatePath);
        Assert.Empty(derivedSidecar.Entries.Select(static entry => entry.Id)
            .Intersect(duplicateSidecar.Entries.Select(static entry => entry.Id)));
        SemanticId baseSeed = result.Analysis.ScriptAssets!.Assets
            .Where(asset => asset.AssetPath == "Game/Base")
            .SelectMany(static asset => asset.States)
            .Single(static state => state.Name == "Seed")
            .Id;
        Assert.Equal(
            baseSeed,
            result.Analysis.ScriptAssets.Assets
                .Where(asset => asset.AssetPath == "Game/DerivedCopy")
                .SelectMany(static asset => asset.States)
                .Single(static state => state.Name == "Seed")
                .Id);
        Assert.Equal(
            IdentitySidecarJson.Serialize(baseSidecar),
            IdentitySidecarJson.Serialize(
                result.Analysis.IdentitySidecars.Single(sidecar => sidecar.SourcePath == basePath)));
    }

    [Fact]
    public void DuplicateWithoutAParentSidecarRetainsLegacyNoIdentityBehavior()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, Path);
        CSharpSourceIdentityTransitionPlanResult result =
            new CSharpSourceIdentityTransitionPlanner().Plan(
                request,
                [new CSharpSourceIdentityTransition(
                    Path,
                    "Game/LegacyDuplicate.cs",
                    Source.Replace("ActiveBase", "LegacyDuplicate", StringComparison.Ordinal)
                        .Replace("Game/ActiveBase", "Game/LegacyDuplicate", StringComparison.Ordinal),
                    false)]);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Null(result.Transitions.Single().IdentitySidecar);
        Assert.Empty(result.Analysis!.IdentitySidecars);
    }

    [Fact]
    public void TopologyChangesFailClosedBeforeProducingARequest()
    {
        CSharpCompilationRequest request = CreatePersistentRequest(Source, Path);
        string changed = Source.Replace(
            "return Seed + value;",
            "int result = Seed + value;\n        return result;",
            StringComparison.Ordinal);

        CSharpSourceIdentityTransitionPlanResult result =
            new CSharpSourceIdentityTransitionPlanner().Plan(
                request,
                [new CSharpSourceIdentityTransition(Path, "Game/Changed.cs", changed, true)]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Request);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == CSharpSourceIdentityTransitionPlanner.TopologyMismatchCode);
    }

    [Fact]
    public void TargetPathCollisionIsRejectedWithoutTouchingTheRequest()
    {
        const string existingPath = "Game/Existing.cs";
        const string existingSource = "namespace Game; public class Existing { }";
        CSharpAnalysisResult initialized = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "TransitionFixture",
                [
                    new CSharpSourceDocument(Path, Source, IdentitySidecarDocument.CreateEmpty(Path)),
                    new CSharpSourceDocument(existingPath, existingSource,
                        IdentitySidecarDocument.CreateEmpty(existingPath)),
                ]));
        CSharpCompilationRequest request = new(
            "TransitionFixture",
            [
                new CSharpSourceDocument(Path, Source,
                    initialized.IdentitySidecars.Single(sidecar => sidecar.SourcePath == Path)),
                new CSharpSourceDocument(existingPath, existingSource,
                    initialized.IdentitySidecars.Single(sidecar => sidecar.SourcePath == existingPath)),
            ]);
        CSharpSourceIdentityTransitionPlanResult result =
            new CSharpSourceIdentityTransitionPlanner().Plan(
                request,
                [new CSharpSourceIdentityTransition(Path, existingPath, existingSource, true)]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Request);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == CSharpSourceIdentityTransitionPlanner.InvalidTransitionCode);
    }

    private static CSharpCompilationRequest CreatePersistentRequest(string source, string path)
    {
        CSharpAnalysisResult initialized = AnalyzeWithEmptySidecar(source, path);
        IdentitySidecarDocument sidecar = Assert.Single(initialized.IdentitySidecars);
        return new CSharpCompilationRequest(
            "TransitionFixture",
            [new CSharpSourceDocument(path, source, sidecar)]);
    }

    private static CSharpCompilationRequest CreatePersistentRequest(
        IReadOnlyList<(string Path, string Source)> documents)
    {
        CSharpCompilationRequest initial = new(
            "TransitionFixture",
            documents.Select(item => new CSharpSourceDocument(
                item.Path,
                item.Source,
                IdentitySidecarDocument.CreateEmpty(item.Path))));
        CSharpAnalysisResult initialized = new CSharpSemanticPipeline().Analyze(initial);
        Assert.True(initialized.Succeeded, string.Join(Environment.NewLine, initialized.Diagnostics));
        return new CSharpCompilationRequest(
            "TransitionFixture",
            documents.Select(item => new CSharpSourceDocument(
                item.Path,
                item.Source,
                initialized.IdentitySidecars.Single(sidecar => sidecar.SourcePath == item.Path))));
    }

    private static CSharpAnalysisResult AnalyzeWithEmptySidecar(string source, string path) =>
        new CSharpSemanticPipeline().Analyze(new CSharpCompilationRequest(
            "TransitionFixture",
            [new CSharpSourceDocument(
                path,
                source,
                IdentitySidecarDocument.CreateEmpty(path))]));
}
