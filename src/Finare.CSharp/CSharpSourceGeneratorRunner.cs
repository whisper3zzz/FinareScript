using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Finare.CSharp;

/// <summary>Runs project-supplied Roslyn generators without engine dependencies.</summary>
internal static class CSharpSourceGeneratorRunner
{
    internal static CSharpCompilation Run(CSharpCompilation compilation, CSharpCompilationRequest request)
    {
        if (request.SourceGeneratorPaths.Count == 0) return compilation;
        using var loader = new GeneratorLoader(request.SourceGeneratorPaths);
        var generators = new List<ISourceGenerator>();
        var failures = new List<string>();
        foreach (string path in request.SourceGeneratorPaths)
        {
            var reference = new AnalyzerFileReference(path, loader);
            reference.AnalyzerLoadFailed += (_, failure) => failures.Add(failure.Message);
            generators.AddRange(reference.GetGenerators(LanguageNames.CSharp));
        }
        if (failures.Count != 0)
            throw new InvalidOperationException("Cannot load project source generators: " + string.Join(Environment.NewLine, failures));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, parseOptions: request.Settings.ParseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation generated, out var diagnostics);
        Diagnostic[] errors = diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).ToArray();
        if (errors.Length != 0)
            throw new InvalidOperationException("C# source generation failed: " +
                string.Join(Environment.NewLine, errors.Select(d => d.ToString())));
        return (CSharpCompilation)generated;
    }

    // Each compilation loads the current generator bytes. A process-wide
    // Assembly.LoadFrom cache would silently reuse old code after a rebuild.
    private sealed class GeneratorLoader : AssemblyLoadContext, IAnalyzerAssemblyLoader, IDisposable
    {
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);

        internal GeneratorLoader(IEnumerable<string> paths) : base(isCollectible: true)
        {
            foreach (string path in paths) AddDependencyLocation(path);
        }

        public void AddDependencyLocation(string fullPath) => _directories.Add(Path.GetDirectoryName(fullPath)!);

        public Assembly LoadFromPath(string fullPath)
        {
            if (_loaded.TryGetValue(fullPath, out Assembly? assembly)) return assembly;
            using FileStream stream = File.OpenRead(fullPath);
            assembly = LoadFromStream(stream);
            _loaded.Add(fullPath, assembly);
            return assembly;
        }

        protected override Assembly? Load(AssemblyName name)
        {
            // Roslyn interfaces must have the compiler host's type identity.
            if (name.Name?.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) == true)
                return Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
            if (name.Name is null || name.Name == "netstandard" ||
                name.Name.StartsWith("System.", StringComparison.Ordinal) || name.Name == "System") return null;
            foreach (string directory in _directories)
            {
                string candidate = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(candidate)) return LoadFromPath(candidate);
            }
            return null;
        }

        public void Dispose() => Unload();
    }
}
