using Finare.Core;
using Finare.CSharp;
using Finare.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Finare.Cli;

/// <summary>A source-oriented editor session compiled in its actual MSBuild project.</summary>
internal sealed record ProjectAuthoringInput(
    CSharpCompilationRequest Request,
    string SourcePath,
    string IdentityPath,
    IReadOnlyDictionary<string, string> SourceFiles,
    IReadOnlySet<string> ReadOnlySources,
    IReadOnlyDictionary<string, string> IdentityFiles)
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
        sources = sources.Select(source => source.IdentitySidecar is not null
            ? source
            : new CSharpSourceDocument(
                source.LogicalPath,
                source.Source,
                IdentitySidecarDocument.CreateEmpty(source.LogicalPath),
                source.IdentityPins)).ToArray();
        CSharpSourceDocument primary = sources[primaryIndex];
        sources[primaryIndex] = new CSharpSourceDocument(primary.LogicalPath, primary.Source,
            primary.IdentitySidecar ?? IdentitySidecarDocument.CreateEmpty(primary.LogicalPath), primary.IdentityPins);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var readOnly = new HashSet<string>(StringComparer.Ordinal);
        var identityFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < sources.Length; index++)
        {
            files.Add(sources[index].LogicalPath, project.Sources[index].Path);
            identityFiles.Add(
                sources[index].LogicalPath,
                index == primaryIndex ? sidecarPath : project.Sources[index].Path + ".usids");
            if (index != primaryIndex) readOnly.Add(sources[index].LogicalPath);
        }

        // A selected script class may be declared across partial files.  The
        // [UnrealScriptClass] attribute belongs to the exact syntax tree that
        // owns the native binding; that one secondary source is editable for
        // this session while unrelated project inputs remain read-only.
        string? bindingOwner = FindBindingAttributeOwner(sources, primaryIndex, template: project.Request);
        if (bindingOwner is not null)
            readOnly.Remove(bindingOwner);

        CSharpCompilationRequest template = project.Request;
        return new ProjectAuthoringInput(new CSharpCompilationRequest(
            template.AssemblyName, sources, template.ReferencePaths, template.EffectCatalog,
            template.Settings, template.RuntimeReferencePaths, template.IncludeDefaultReferences),
            primaryPath, sidecarPath, files, readOnly, identityFiles);
    }

    private static string? FindBindingAttributeOwner(
        IReadOnlyList<CSharpSourceDocument> sources,
        int primaryIndex,
        CSharpCompilationRequest template)
    {
        SyntaxTree[] trees = sources.Select(source => CSharpSyntaxTree.ParseText(
            source.Source,
            template.Settings.ParseOptions,
            source.LogicalPath)).ToArray();
        CSharpCompilation compilation = new CSharpCompilationHost().Create(template, trees);
        SemanticModel primaryModel = compilation.GetSemanticModel(trees[primaryIndex]);
        SyntaxNode root = trees[primaryIndex].GetRoot();
        ISymbol[] selectedAssets = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Select(node => primaryModel.GetDeclaredSymbol(node))
            .OfType<INamedTypeSymbol>()
            .Where(static symbol => symbol.GetAttributes().Any(attribute => IsScriptAssetAttribute(attribute)))
            .Cast<ISymbol>()
            .Distinct(SymbolEqualityComparer.Default)
            .ToArray();
        if (selectedAssets.Length > 1)
        {
            throw new ArgumentException(
                $"The selected source '{sources[primaryIndex].LogicalPath}' contains multiple " +
                "[ScriptAsset] types; choose an explicit asset before opening project authoring.");
        }
        INamedTypeSymbol? selected = selectedAssets.SingleOrDefault() as INamedTypeSymbol;
        if (selected is null)
            return null;

        foreach (AttributeData attribute in selected.GetAttributes())
        {
            if (!IsUnrealScriptClassAttribute(attribute.AttributeClass))
                continue;
            SyntaxTree? ownerTree = attribute.ApplicationSyntaxReference?.SyntaxTree;
            if (ownerTree is null)
                continue;
            string logicalPath = ownerTree.FilePath.Replace('\\', '/');
            if (sources.Any(source => string.Equals(source.LogicalPath, logicalPath, StringComparison.Ordinal)))
                return logicalPath;
        }
        return null;
    }

    private static bool IsScriptAssetAttribute(AttributeData attribute) =>
        string.Equals(attribute.AttributeClass?.ToDisplayString(), "Finare.ScriptAssetAttribute", StringComparison.Ordinal);

    private static bool IsUnrealScriptClassAttribute(INamedTypeSymbol? attributeClass) =>
        string.Equals(attributeClass?.ToDisplayString(), "Finare.Unreal.UnrealScriptClassAttribute", StringComparison.Ordinal);
}
