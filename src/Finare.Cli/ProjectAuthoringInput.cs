using Finare.Core;
using Finare.CSharp;
using Finare.Semantics;

namespace Finare.Cli;

/// <summary>A source-oriented editor session compiled in its actual MSBuild project.</summary>
internal sealed record ProjectAuthoringInput(
    CSharpCompilationRequest Request,
    string SourcePath,
    string IdentityPath,
    IReadOnlyDictionary<string, string> SourceFiles,
    IReadOnlySet<string> ReadOnlySources)
{
    public static ProjectAuthoringInput Load(
        string projectPath,
        string sourcePath,
        string? identityPath = null,
        CSharpProjectLoadOptions? options = null,
        MethodEffectCatalog? effects = null)
    {
        CSharpProjectLoadResult project = new CSharpProjectLoader().Load(projectPath, effects, options);
        string primaryPath = Path.GetFullPath(sourcePath);
        int primaryIndex = project.Sources.ToList().FindIndex(source =>
            string.Equals(source.Path, primaryPath, StringComparison.OrdinalIgnoreCase));
        if (primaryIndex < 0)
            throw new ArgumentException($"Authoring source '{primaryPath}' is not an evaluated Compile item in '{projectPath}'.");

        string sidecarPath = Path.GetFullPath(identityPath ?? primaryPath + ".usids");
        string[] explicitIdentities = File.Exists(sidecarPath) ? [sidecarPath] : [];
        CSharpSourceDocument[] sources = Program.AttachIdentitySidecars(
            project.Request.Sources, project.Sources, explicitIdentities);
        CSharpSourceDocument primary = sources[primaryIndex];
        sources[primaryIndex] = new CSharpSourceDocument(primary.LogicalPath, primary.Source,
            primary.IdentitySidecar ?? IdentitySidecarDocument.CreateEmpty(primary.LogicalPath), primary.IdentityPins);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var readOnly = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < sources.Length; index++)
        {
            files.Add(sources[index].LogicalPath, project.Sources[index].Path);
            if (index != primaryIndex) readOnly.Add(sources[index].LogicalPath);
        }
        CSharpCompilationRequest template = project.Request;
        return new ProjectAuthoringInput(new CSharpCompilationRequest(
            template.AssemblyName, sources, template.ReferencePaths, template.EffectCatalog,
            template.Settings, template.RuntimeReferencePaths, template.IncludeDefaultReferences),
            primaryPath, sidecarPath, files, readOnly);
    }
}
