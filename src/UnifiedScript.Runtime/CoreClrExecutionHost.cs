using System.Reflection;
using System.Runtime.Loader;
using UnifiedScript.CSharp;

namespace UnifiedScript.Runtime;

public sealed class CoreClrExecutionHost
{
    private readonly CSharpProgramEmitter emitter = new();

    public CoreClrExecutionSession CompileAndLoad(CSharpCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CSharpProgramEmitResult emitted = emitter.Emit(request);
        if (!emitted.Succeeded || emitted.Image is null)
        {
            throw new ExecutionCompilationException(emitted.Diagnostics);
        }

        var loadContext = new CollectibleProgramLoadContext(request.ReferencePaths);
        try
        {
            using var portableExecutable = new MemoryStream(emitted.Image.PortableExecutable, writable: false);
            using var portablePdb = new MemoryStream(emitted.Image.PortablePdb, writable: false);
            Assembly assembly = loadContext.LoadFromStream(portableExecutable, portablePdb);
            return new CoreClrExecutionSession(loadContext, assembly, emitted.Image);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    private sealed class CollectibleProgramLoadContext : AssemblyLoadContext
    {
        private readonly IReadOnlyDictionary<string, string> referencePaths;

        public CollectibleProgramLoadContext(IEnumerable<string> references)
            : base(isCollectible: true)
        {
            referencePaths = references
                .Select(Path.GetFullPath)
                .ToDictionary(
                    static path => AssemblyName.GetAssemblyName(path).Name ??
                        throw new InvalidOperationException(
                            $"Reference '{path}' has no assembly name."),
                    static path => path,
                    StringComparer.OrdinalIgnoreCase);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            Assembly? shared = Default.Assemblies.FirstOrDefault(assembly =>
                AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
            if (shared is not null)
            {
                return shared;
            }

            return assemblyName.Name is not null &&
                referencePaths.TryGetValue(assemblyName.Name, out string? referencePath)
                    ? LoadFromAssemblyPath(referencePath)
                    : null;
        }
    }
}
