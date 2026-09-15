using Finare.CSharp;
using Finare.Unreal;

namespace Finare.Cli;

internal static class UnrealSourceBindingInput
{
    // An in-memory selection seed, never a project-owned list or generated lock file.
    internal static UnrealApiContract CreateSeed(string managedNamespace,
        string nativeHeader = "FinareGeneratedThunks.h",
        string nativeNamespace = "UE::Finare::Generated") => new(
            UnrealApiContract.CurrentSchemaVersion, "SourceBindings", managedNamespace,
            nativeHeader, nativeNamespace, []);

    internal static CSharpCompilationRequest Load(
        IReadOnlyList<string> projectPaths, string configuration, string? dotNetPath)
    {
        var sources = new List<CSharpSourceDocument>();
        var requests = new List<CSharpCompilationRequest>();
        foreach (string path in projectPaths.Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            CSharpCompilationRequest request;
            try
            {
                request = new CSharpProjectLoader().Load(path,
                    options: new CSharpProjectLoadOptions(configuration, Restore: true,
                        Build: false, DotNetPath: dotNetPath)).Request;
            }
            catch (CSharpProjectLoadException exception)
            {
                throw new InvalidDataException(exception.Message, exception);
            }
            requests.Add(request);
            sources.AddRange(request.Sources.Select(source => new CSharpSourceDocument(
                $"{Path.GetFileNameWithoutExtension(path)}/{source.LogicalPath}", source.Source)));
        }
        if (requests.Count == 0 ||
            requests.Select(request => request.Settings.ConfigurationKey).Distinct().Count() != 1 ||
            requests.Select(request => request.IncludeDefaultReferences).Distinct().Count() != 1)
        {
            throw new ArgumentException("Source binding projects must use compatible C# settings and reference policies.");
        }
        string[] references = requests.SelectMany(request => request.ReferencePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        return new CSharpCompilationRequest("Finare.SourceBindings", sources, references,
            settings: requests[0].Settings, runtimeReferencePaths: references,
            includeDefaultReferences: requests[0].IncludeDefaultReferences,
            sourceGeneratorPaths: requests.SelectMany(request => request.SourceGeneratorPaths));
    }
}
