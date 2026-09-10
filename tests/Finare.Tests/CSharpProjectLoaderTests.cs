using Microsoft.CodeAnalysis.CSharp;
using Finare.CSharp;
using Finare.Core;
using Finare.Runtime;

namespace Finare.Tests;

public sealed class CSharpProjectLoaderTests
{
    [Fact]
    public void LoaderUsesMsBuildSourcesConstantsNuGetAndProjectReferences()
    {
        string projectPath = GetSampleProjectPath();
        var loader = new CSharpProjectLoader();

        CSharpProjectLoadResult result = loader.Load(
            projectPath,
            options: new CSharpProjectLoadOptions(Restore: false, Build: false));

        Assert.Equal("Finare.ProjectLoadingDemo", result.Request.AssemblyName);
        Assert.Equal("net10.0", result.TargetFramework);
        Assert.Equal(LanguageVersion.Preview, result.Request.Settings.LanguageVersion);
        Assert.Contains(
            "FINARE_PROJECT_DEMO",
            result.Request.Settings.PreprocessorSymbols);
        Assert.Contains(
            result.Request.Sources,
            static source => source.LogicalPath == "ProjectLoadingDemo.cs");
        Assert.Contains(
            result.Sources,
            static source =>
                source.LogicalPath == "ProjectLoadingDemo.cs" &&
                source.Path.EndsWith("ProjectLoadingDemo.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.References,
            static reference =>
                reference.Kind == CSharpProjectReferenceKind.Package &&
                reference.PackageId == "Microsoft.CodeAnalysis.CSharp");
        Assert.Contains(
            result.References,
            static reference =>
                reference.Kind == CSharpProjectReferenceKind.Project &&
                reference.SourceProjectPath is not null &&
                reference.SourceProjectPath.EndsWith(
                    "Finare.Core.csproj",
                    StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Request.RuntimeReferencePaths,
            static path => path.EndsWith(
                "Finare.Core.dll",
                StringComparison.OrdinalIgnoreCase));

        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(result.Request);
        Assert.True(
            analysis.Succeeded,
            string.Join(Environment.NewLine, analysis.Diagnostics.Select(static item => item.Message)));

        using CoreClrExecutionSession session =
            new CoreClrExecutionHost().CompileAndLoad(result.Request);
        ExecutionResult execution = session.Execute(new ExecutionInvocation(
            session.ResolveVisualMethodId(
                "Finare.ProjectLoadingDemo.ProjectLoadingDemo.Resolve"),
            [new ExecutionArgument("1")]));
        Assert.Equal(ExecutionOutcome.Returned, execution.Outcome);
        Assert.Equal("15", execution.ReturnValue!.Json);
    }

    [Fact]
    public void LoaderIsDeterministicForTheSameEvaluatedProject()
    {
        string projectPath = GetSampleProjectPath();
        var loader = new CSharpProjectLoader();
        var options = new CSharpProjectLoadOptions(Restore: false, Build: false);

        CSharpProjectLoadResult first = loader.Load(projectPath, options: options);
        CSharpProjectLoadResult second = loader.Load(projectPath, options: options);

        Assert.Equal(first.Request.ConfigurationId, second.Request.ConfigurationId);
        Assert.Equal(
            first.Request.Sources.Select(static source => source.LogicalPath),
            second.Request.Sources.Select(static source => source.LogicalPath));
        Assert.Equal(first.Request.ReferencePaths, second.Request.ReferencePaths);
        Assert.Equal(first.Request.RuntimeReferencePaths, second.Request.RuntimeReferencePaths);
    }

    [Fact]
    public void EmitProject_ReconcilesPortableIdentityPathAgainstPhysicalSource()
    {
        string projectPath = GetSampleProjectPath();
        CSharpProjectLoadResult loaded = new CSharpProjectLoader().Load(
            projectPath,
            options: new CSharpProjectLoadOptions(Restore: false, Build: false));
        CSharpProjectSource projectSource = Assert.Single(
            loaded.Sources,
            static source => source.LogicalPath == "ProjectLoadingDemo.cs");
        string repositoryRoot = FindRepositoryRoot();
        string portablePath = Path.GetRelativePath(repositoryRoot, projectSource.Path)
            .Replace(Path.DirectorySeparatorChar, '/');
        CSharpSourceDocument source = Assert.Single(
            loaded.Request.Sources,
            static source => source.LogicalPath == "ProjectLoadingDemo.cs");
        CSharpAnalysisResult initialized = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                loaded.Request.AssemblyName,
                [new CSharpSourceDocument(
                    portablePath,
                    source.Source,
                    IdentitySidecarDocument.CreateEmpty(portablePath))],
                loaded.Request.ReferencePaths,
                settings: loaded.Request.Settings,
                runtimeReferencePaths: loaded.Request.RuntimeReferencePaths,
                includeDefaultReferences: loaded.Request.IncludeDefaultReferences));
        IdentitySidecarDocument identity = Assert.Single(initialized.IdentitySidecars);
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "Finare.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string identityPath = Path.Combine(temporaryDirectory, "ProjectLoadingDemo.cs.usids");
            string outputPath = Path.Combine(temporaryDirectory, "out");
            File.WriteAllText(identityPath, IdentitySidecarJson.Serialize(identity) + "\n");

            int exitCode = Finare.Cli.Program.Main([
                "emit",
                projectPath,
                "--out",
                outputPath,
                "--identity",
                identityPath,
                "--no-restore",
                "--no-build",
            ]);

            Assert.Equal(0, exitCode);
            string emittedIdentityPath = Assert.Single(
                Directory.GetFiles(outputPath, "*.usids", SearchOption.TopDirectoryOnly));
            IdentitySidecarDocument emitted = IdentitySidecarJson.Deserialize(
                File.ReadAllText(emittedIdentityPath));
            Assert.Equal(portablePath, emitted.SourcePath);
            Assert.Equal(identity.SourceDigest, emitted.SourceDigest);
            Assert.Equal(
                identity.Entries.Select(static entry => entry.Id),
                emitted.Entries.Select(static entry => entry.Id));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static string GetSampleProjectPath()
    {
        string repositoryRoot = FindRepositoryRoot();
        return Path.Combine(
            repositoryRoot,
            "samples",
            "ProjectLoading",
            "ProjectLoadingDemo.csproj");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Finare.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return directory.FullName;
    }
}
