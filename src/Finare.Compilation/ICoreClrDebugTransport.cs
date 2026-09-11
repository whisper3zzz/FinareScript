using Finare.Core;
using Finare.CSharp;

namespace Finare.Runtime;

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
