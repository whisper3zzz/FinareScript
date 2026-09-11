using Finare.Core;

namespace Finare.Cli;

internal enum AuthoringFailureKind
{
    InvalidInput,
    Conflict,
    ExecutionFailed,
}

internal sealed class AuthoringProtocolException(
    AuthoringFailureKind failureKind,
    string code,
    string message,
    IReadOnlyList<UnifiedDiagnostic>? diagnostics = null,
    string rollbackState = "unchanged",
    ReloadTransactionResponse? reloadTransaction = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public AuthoringFailureKind FailureKind { get; } = failureKind;

    public string Code { get; } = code;

    public IReadOnlyList<UnifiedDiagnostic> Diagnostics { get; } = diagnostics ?? [];

    public string RollbackState { get; } = rollbackState;

    public ReloadTransactionResponse? ReloadTransaction { get; } = reloadTransaction;

    public AuthoringProtocolException WithRollback(
        string state,
        ReloadTransactionResponse? reloadTransaction = null) => new(
        FailureKind,
        Code,
        Message,
        Diagnostics,
        state,
        reloadTransaction ?? ReloadTransaction,
        this);
}
