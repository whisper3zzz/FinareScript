using Finare.CSharp;

namespace Finare.Runtime;

public static class CoreClrCompilationExtensions
{
    public static CoreClrExecutionSession CompileAndLoad(
        this CoreClrExecutionHost host,
        CSharpCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(request);
        CSharpProgramEmitResult emitted = new CSharpProgramEmitter().Emit(request);
        if (!emitted.Succeeded || emitted.Image is null)
        {
            throw new ExecutionCompilationException(emitted.Diagnostics);
        }

        return host.Load(emitted.Image, request.RuntimeReferencePaths);
    }
}
