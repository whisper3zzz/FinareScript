using System.Reflection;
using UnifiedScript.CSharp;
using UnifiedScript.Core;

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

        return Load(emitted.Image, request.RuntimeReferencePaths);
    }

    public CoreClrExecutionSession Load(
        CSharpProgramImage image,
        IEnumerable<string>? referencePaths = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.PortableExecutable is null || image.PortableExecutable.Length == 0 ||
            image.PortablePdb is null || image.PortablePdb.Length == 0 ||
            image.Hir is null || image.Semantics is null || image.SourceMap is null ||
            image.ScriptAssets is null || image.IdentitySidecars is null)
        {
            throw new ArgumentException("A CoreCLR program image has invalid required fields.",
                nameof(image));
        }

        CSharpProgramSourceMap sourceMap = CSharpProgramSourceMapJson.NormalizeAndValidate(
            image.SourceMap);
        ScriptAssetDocument scriptAssets = ScriptAssetJson.NormalizeAndValidate(image.ScriptAssets);
        if (!string.Equals(image.AssemblyName, sourceMap.AssemblyName, StringComparison.Ordinal) ||
            image.Hir.Id != image.Semantics.SourceCompilationId ||
            sourceMap.SourceCompilationId != image.Semantics.SourceCompilationId ||
            sourceMap.SemanticConfigurationId != image.Semantics.ConfigurationId ||
            scriptAssets.SourceCompilationId != image.Semantics.SourceCompilationId ||
            scriptAssets.SemanticConfigurationId != image.Semantics.ConfigurationId)
        {
            throw new ArgumentException(
                "The CoreCLR program image artifacts do not share compilation identity.",
                nameof(image));
        }

        var loadContext = new CoreClrProgramLoadContext(referencePaths ?? []);
        try
        {
            using var portableExecutable = new MemoryStream(image.PortableExecutable, writable: false);
            using var portablePdb = new MemoryStream(image.PortablePdb, writable: false);
            Assembly assembly = loadContext.LoadFromStream(portableExecutable, portablePdb);
            return new CoreClrExecutionSession(loadContext, assembly, image);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

}
