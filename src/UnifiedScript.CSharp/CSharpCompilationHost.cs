using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

        return CSharpCompilation.Create(
            "UnifiedScript.Analysis",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: false,
                deterministic: true,
                nullableContextOptions: NullableContextOptions.Enable));
    }

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
