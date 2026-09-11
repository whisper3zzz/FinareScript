using Finare.Core;

namespace Finare.Cli;

internal sealed record AuthoringTransactionContext(
    long? BaselineRevision,
    string TransactionId,
    string Description,
    string? GroupId,
    string? PreviewToken,
    SemanticId? BoundPayloadId);