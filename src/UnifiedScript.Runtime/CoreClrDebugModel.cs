using UnifiedScript.Core;

namespace UnifiedScript.Runtime;

public enum CoreClrDebugPauseReason
{
    Breakpoint,
    Pause,
    Step,
}

public enum CoreClrDebugValueKind
{
    Null,
    Scalar,
    Object,
    Array,
    Cycle,
    Unavailable,
}

public sealed record CoreClrDebugValue(
    string Name,
    string TypeMetadataName,
    string Display,
    CoreClrDebugValueKind Kind,
    IReadOnlyList<CoreClrDebugValue> Children,
    bool Truncated);

public sealed record CoreClrDebugStackFrame(
    long FrameId,
    int Index,
    string MethodMetadataName,
    int MetadataToken,
    int IlOffset,
    SourceSpan? SourceSpan,
    bool HasLocals);

public sealed record CoreClrDebugPause(
    string SchemaVersion,
    CoreClrProgramGeneration Generation,
    long ExecutionId,
    SemanticId PauseId,
    CoreClrDebugPauseReason Reason,
    SemanticId MethodId,
    SemanticId NodeId,
    IReadOnlyList<SemanticId> BreakpointIds)
{
    public const string CurrentSchemaVersion = "unifiedscript.debug-pause/v0.1";
}

public sealed record CoreClrDebugExecution(
    CoreClrProgramGeneration Generation,
    long ExecutionId);

public sealed record CoreClrDebugReloadResult(
    CoreClrProgramGeneration Generation,
    BreakpointBindingSnapshot Breakpoints,
    IReadOnlyList<IdentitySidecarDocument> IdentitySidecars);

public interface ICoreClrDebugTransport : IDisposable
{
    CoreClrDebugReloadResult Reload(UnifiedScript.CSharp.CSharpCompilationRequest request);

    BreakpointBindingSnapshot SetBreakpoint(SemanticBreakpoint breakpoint);

    bool RemoveBreakpoint(SemanticId breakpointId);

    SemanticId ResolveVisualMethodId(string qualifiedMethodName);

    CoreClrDebugExecution Start(ExecutionInvocation invocation);

    Task<CoreClrDebugPause> WaitForPauseAsync(
        long executionId,
        CancellationToken cancellationToken = default);

    IReadOnlyList<CoreClrDebugStackFrame> GetStackTrace(
        SemanticId generationId,
        SemanticId pauseId);

    IReadOnlyList<CoreClrDebugValue> GetLocals(
        SemanticId generationId,
        SemanticId pauseId,
        long frameId);

    void Pause(SemanticId generationId, long executionId);

    void Continue(SemanticId generationId, SemanticId pauseId);

    void StepOver(SemanticId generationId, SemanticId pauseId);

    Task<ExecutionResult> WaitForCompletionAsync(
        long executionId,
        CancellationToken cancellationToken = default);
}
