using Microsoft.CodeAnalysis.CSharp;
using UnifiedScript.CSharp;
using UnifiedScript.Runtime;

namespace UnifiedScript.Tests;

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

        Assert.Equal("UnifiedScript.ProjectLoadingDemo", result.Request.AssemblyName);
        Assert.Equal("net10.0", result.TargetFramework);
        Assert.Equal(LanguageVersion.Preview, result.Request.Settings.LanguageVersion);
        Assert.Contains(
            "UNIFIEDSCRIPT_PROJECT_DEMO",
            result.Request.Settings.PreprocessorSymbols);
        Assert.Contains(
            result.Request.Sources,
            static source => source.LogicalPath == "ProjectLoadingDemo.cs");
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
                    "UnifiedScript.Core.csproj",
                    StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(result.Request.RuntimeReferencePaths);

        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(result.Request);
        Assert.True(
            analysis.Succeeded,
            string.Join(Environment.NewLine, analysis.Diagnostics.Select(static item => item.Message)));

        using CoreClrExecutionSession session =
            new CoreClrExecutionHost().CompileAndLoad(result.Request);
        ExecutionResult execution = session.Execute(new ExecutionInvocation(
            session.ResolveVisualMethodId(
                "UnifiedScript.ProjectLoadingDemo.ProjectLoadingDemo.Resolve"),
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

    private static string GetSampleProjectPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UnifiedScript.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return Path.Combine(
            directory.FullName,
            "samples",
            "ProjectLoading",
            "ProjectLoadingDemo.csproj");
    }
}
