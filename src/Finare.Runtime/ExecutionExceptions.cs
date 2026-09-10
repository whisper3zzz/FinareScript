using Finare.Core;

namespace Finare.Runtime;

public sealed class ExecutionCompilationException : Exception
{
    public ExecutionCompilationException(IReadOnlyList<UnifiedDiagnostic> diagnostics)
        : base("The Finare program failed analysis or CoreCLR image emission.")
    {
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public IReadOnlyList<UnifiedDiagnostic> Diagnostics { get; }
}

public sealed class ExecutionInvocationException : Exception
{
    public ExecutionInvocationException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}
