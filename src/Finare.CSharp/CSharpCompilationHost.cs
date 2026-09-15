using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Finare;

namespace Finare.CSharp;

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

        return CreateCore(
            "Finare.Analysis",
            [syntaxTree],
            references,
            CSharpCompilationSettings.Default);
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

        var references = request.IncludeDefaultReferences
            ? new List<MetadataReference>(PlatformReferences.Value)
            : [];
        references.AddRange(request.ReferencePaths.Select(
            static path => MetadataReference.CreateFromFile(path)));
        if (additionalReferences is not null)
        {
            references.AddRange(additionalReferences);
        }

        CSharpCompilation compilation = CreateCore(request.AssemblyName, syntaxTrees, references, request.Settings);
        return CSharpSourceGeneratorRunner.Run(compilation, request);
    }

    public CSharpCompilation Create(CSharpCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SyntaxTree[] syntaxTrees = request.Sources
            .Select(source => CSharpSyntaxTree.ParseText(
                SourceText.From(source.Source, System.Text.Encoding.UTF8),
                request.Settings.ParseOptions,
                source.LogicalPath))
            .ToArray();
        return Create(request, syntaxTrees);
    }

    private static CSharpCompilation CreateCore(
        string assemblyName,
        IEnumerable<SyntaxTree> syntaxTrees,
        IEnumerable<MetadataReference> references,
        CSharpCompilationSettings settings) => CSharpCompilation.Create(
            assemblyName,
            syntaxTrees,
            references
                .GroupBy(
                    static reference => reference.Display ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First()),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: settings.OptimizationLevel,
                checkOverflow: settings.CheckOverflow,
                allowUnsafe: settings.AllowUnsafe,
                deterministic: true,
                nullableContextOptions: settings.NullableContext));

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
