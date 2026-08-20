using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using UnifiedScript;

namespace UnifiedScript.CSharp;

public sealed class CSharpCompilationHost
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> PlatformReferences =
        new(CreatePlatformReferences);

    public CSharpCompilation Create(
        SyntaxTree syntaxTree,
        IEnumerable<MetadataReference>? additionalReferences = null)
    {
        ArgumentNullException.ThrowIfNull(syntaxTree);
        var references = new List<MetadataReference>(PlatformReferences.Value);
        if (additionalReferences is not null)
        {
            references.AddRange(additionalReferences);
        }

        return CreateCore("UnifiedScript.Analysis", [syntaxTree], references);
    }

    public CSharpCompilation Create(
        CSharpCompilationRequest request,
        IReadOnlyList<SyntaxTree> syntaxTrees,
        IEnumerable<MetadataReference>? additionalReferences = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        if (syntaxTrees.Count != request.Sources.Count)
        {
            throw new ArgumentException(
                "The syntax tree count must match the compilation request source count.",
                nameof(syntaxTrees));
        }

        var references = new List<MetadataReference>(PlatformReferences.Value);
        references.AddRange(request.ReferencePaths.Select(
            static path => MetadataReference.CreateFromFile(path)));
        if (additionalReferences is not null)
        {
            references.AddRange(additionalReferences);
        }

        return CreateCore(request.AssemblyName, syntaxTrees, references);
    }

    public CSharpCompilation Create(CSharpCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SyntaxTree[] syntaxTrees = request.Sources
            .Select(static source => CSharpSyntaxTree.ParseText(
                SourceText.From(source.Source, System.Text.Encoding.UTF8),
                CSharpToHirLowerer.ParseOptions,
                source.LogicalPath))
            .ToArray();
        return Create(request, syntaxTrees);
    }

    private static CSharpCompilation CreateCore(
        string assemblyName,
        IEnumerable<SyntaxTree> syntaxTrees,
        IEnumerable<MetadataReference> references) => CSharpCompilation.Create(
            assemblyName,
            syntaxTrees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: false,
                deterministic: true,
                nullableContextOptions: NullableContextOptions.Enable));

    private static IReadOnlyList<MetadataReference> CreatePlatformReferences()
    {
        string? trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(trustedAssemblies))
        {
            throw new InvalidOperationException("The runtime did not expose trusted platform assemblies.");
        }

        var paths = trustedAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(VisualAttribute).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return paths.Select(static path => MetadataReference.CreateFromFile(path)).ToArray();
    }
}
