using Finare.Core;

namespace Finare.Runtime;

public enum CoreClrDebugPauseReason
{
    Breakpoint,
    Pause,
    Step,
}

public enum CoreClrDebugStepKind
{
    Into,
    Over,
    Out,
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
    SemanticId? MethodId,
    SemanticId? NodeId,
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
    CoreClrDebugStepKind? StepKind,
    SemanticId MethodId,
    SemanticId NodeId,
    IReadOnlyList<SemanticId> BreakpointIds)
{
    public const string CurrentSchemaVersion = "unifiedscript.debug-pause/v0.2";
}

public sealed record CoreClrDebugEvaluation(
    string SchemaVersion,
    CoreClrProgramGeneration Generation,
    SemanticId PauseId,
    long FrameId,
    string Expression,
    CoreClrDebugValue Value)
{
    public const string CurrentSchemaVersion = "unifiedscript.debug-evaluation/v0.1";
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
    CoreClrDebugReloadResult Reload(Finare.CSharp.CSharpCompilationRequest request);

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

    CoreClrDebugEvaluation Evaluate(
        SemanticId generationId,
        SemanticId pauseId,
        long frameId,
        string expression);

    void Pause(SemanticId generationId, long executionId);

    void Continue(SemanticId generationId, SemanticId pauseId);

    void StepOver(SemanticId generationId, SemanticId pauseId);

    void StepInto(SemanticId generationId, SemanticId pauseId);

    void StepOut(SemanticId generationId, SemanticId pauseId);

    Task<ExecutionResult> WaitForCompletionAsync(
        long executionId,
        CancellationToken cancellationToken = default);
}
