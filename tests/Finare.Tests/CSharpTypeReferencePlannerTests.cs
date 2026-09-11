using Microsoft.CodeAnalysis.CSharp;
using System.Text.Json;
using Finare.CSharp;
using Finare.Core;

namespace Finare.Tests;

[Trait("Category", "Compiler")]
public sealed class CSharpTypeReferencePlannerTests
{
    [Fact]
    public void PlannerRenamesOnlyRoslynBoundTypeDeclarationsAndReferences()
    {
        const string targetSource = """
            namespace Demo;
            public sealed class OldClass
            {
                public OldClass() { }
                public OldClass Clone() => new OldClass();
            }
            """;
        const string consumerSource = """
            namespace Demo;
            public sealed class Consumer
            {
                private OldClass value = new OldClass();
                private const string Unrelated = "OldClass";
                // OldClass in a comment must not be rewritten.
            }
            """;
        string root = Path.Combine(
            Path.GetTempPath(),
            "FinareTypeReferencePlanner",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string targetPath = Path.Combine(root, "OldClass.cs");
            string consumerPath = Path.Combine(root, "Consumer.cs");
            File.WriteAllText(targetPath, targetSource);
            File.WriteAllText(consumerPath, consumerSource);
            var request = new CSharpCompilationRequest(
                "ReferencePlannerFixture",
                [
                    new CSharpSourceDocument("OldClass.cs", targetSource),
                    new CSharpSourceDocument("Consumer.cs", consumerSource),
                ],
                settings: new CSharpCompilationSettings(
                    "net10.0",
                    LanguageVersion.Preview));
            var project = new CSharpProjectLoadResult(
                Path.Combine(root, "Fixture.csproj"),
                "Debug",
                "net10.0",
                request,
                [
                    new CSharpProjectSource("OldClass.cs", targetPath),
                    new CSharpProjectSource("Consumer.cs", consumerPath),
                ],
                []);

            CSharpTypeReferencePlan plan = new CSharpTypeReferencePlanner().Plan(
                project,
                "Demo.OldClass",
                "NewClass");

            Assert.Equal(CSharpTypeReferencePlan.CurrentSchemaVersion, plan.SchemaVersion);
            Assert.Equal(6, plan.EditCount);
            Assert.Contains(
                plan.Documents.SelectMany(static document => document.Edits),
                static edit => edit.Kind == CSharpTypeReferenceEditKind.TypeDeclaration);
            Assert.Contains(
                plan.Documents.SelectMany(static document => document.Edits),
                static edit => edit.Kind == CSharpTypeReferenceEditKind.ConstructorDeclaration);
            string rewrittenConsumer = Apply(
                consumerSource,
                Assert.Single(
                    plan.Documents,
                    static item => item.LogicalPath == "Consumer.cs").Edits);
            Assert.Contains("private NewClass value = new NewClass();", rewrittenConsumer);
            Assert.Contains("\"OldClass\"", rewrittenConsumer);
            Assert.Contains("// OldClass in a comment", rewrittenConsumer);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeletePlanReportsExternalSemanticReferencesWithoutReplacement()
    {
        const string targetSource = "namespace Demo; public sealed class Target { }";
        const string consumerSource =
            "namespace Demo; public sealed class Consumer { Target? Value; }";
        string root = Path.Combine(
            Path.GetTempPath(),
            "FinareDeleteReferencePlanner",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string targetPath = Path.Combine(root, "Target.cs");
            string consumerPath = Path.Combine(root, "Consumer.cs");
            File.WriteAllText(targetPath, targetSource);
            File.WriteAllText(consumerPath, consumerSource);
            var request = new CSharpCompilationRequest(
                "DeleteReferenceFixture",
                [
                    new CSharpSourceDocument("Target.cs", targetSource),
                    new CSharpSourceDocument("Consumer.cs", consumerSource),
                ]);
            var project = new CSharpProjectLoadResult(
                Path.Combine(root, "Fixture.csproj"),
                "Debug",
                "net10.0",
                request,
                [
                    new CSharpProjectSource("Target.cs", targetPath),
                    new CSharpProjectSource("Consumer.cs", consumerPath),
                ],
                []);

            CSharpTypeReferencePlan plan = new CSharpTypeReferencePlanner().Plan(
                project,
                "Demo.Target");

            CSharpTypeReferenceDocument consumer = Assert.Single(
                plan.Documents,
                static item => item.LogicalPath == "Consumer.cs");
            CSharpTypeReferenceEdit reference = Assert.Single(consumer.Edits);
            Assert.Equal(CSharpTypeReferenceEditKind.TypeReference, reference.Kind);
            Assert.Null(reference.Replacement);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CliWritesDeterministicProjectReferencePlan()
    {
        string projectPath = FindRepositoryPath(
            "samples",
            "ProjectLoading",
            "ProjectLoadingDemo.csproj");
        string outputPath = Path.Combine(
            Path.GetTempPath(),
            $"finare-reference-plan-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal(0, Finare.Cli.Program.Main([
                "script-class-reference-plan",
                projectPath,
                "--type",
                "Finare.ProjectLoadingDemo.ProjectLoadingDemo",
                "--new-name",
                "RenamedProjectLoadingDemo",
                "--out",
                outputPath,
            ]));
            CSharpTypeReferencePlan? plan = JsonSerializer.Deserialize<CSharpTypeReferencePlan>(
                File.ReadAllText(outputPath),
                UnifiedJson.CreateOptions());
            Assert.NotNull(plan);
            Assert.Equal(CSharpTypeReferencePlan.CurrentSchemaVersion, plan.SchemaVersion);
            Assert.Equal("RenamedProjectLoadingDemo", plan.ReplacementTypeName);
            Assert.True(plan.EditCount > 0);
            Assert.All(plan.Documents, static document =>
                Assert.StartsWith("sha256:", document.SourceDigest));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static string Apply(
        string source,
        IReadOnlyList<CSharpTypeReferenceEdit> edits)
    {
        foreach (CSharpTypeReferenceEdit edit in edits.OrderByDescending(static item => item.Start))
        {
            source = string.Concat(
                source.AsSpan(0, edit.Start),
                edit.Replacement,
                source.AsSpan(edit.Start + edit.Length));
        }
        return source;
    }

    private static string FindRepositoryPath(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "Finare.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. parts]);
    }
}
