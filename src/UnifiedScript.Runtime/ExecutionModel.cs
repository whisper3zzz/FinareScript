using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.Runtime;

public enum ExecutionOutcome
{
    Returned,
    Faulted,
}

public sealed record ExecutionArgument
{
    public ExecutionArgument(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using JsonDocument document = JsonDocument.Parse(json);
        Json = document.RootElement.GetRawText();
    }

    public string Json { get; }
}

public sealed record ExecutionInvocation
{
    public ExecutionInvocation(
        SemanticId methodId,
        IEnumerable<ExecutionArgument>? arguments = null)
    {
        if (methodId.IsEmpty)
        {
            throw new ArgumentException("An execution invocation requires a method ID.", nameof(methodId));
        }

        MethodId = methodId;
        Arguments = (arguments ?? []).ToArray();
    }

    public SemanticId MethodId { get; }

    public IReadOnlyList<ExecutionArgument> Arguments { get; }
}

public sealed record ExecutionValue(
    string TypeMetadataName,
    string Json);

public sealed record ExecutionFault(
    string TypeMetadataName,
    string Message);

public sealed record ExecutionResult(
    string SchemaVersion,
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId,
    SemanticId MethodId,
    ExecutionOutcome Outcome,
    ExecutionValue? ReturnValue,
    ExecutionFault? Fault)
{
    public const string CurrentSchemaVersion = "unifiedscript.execution-result/v0.1";

    public static ExecutionResult Returned(
        SemanticId sourceCompilationId,
        SemanticId semanticConfigurationId,
        SemanticId methodId,
        ExecutionValue returnValue) => new(
            CurrentSchemaVersion,
            sourceCompilationId,
            semanticConfigurationId,
            methodId,
            ExecutionOutcome.Returned,
            returnValue,
            null);

    public static ExecutionResult Faulted(
        SemanticId sourceCompilationId,
        SemanticId semanticConfigurationId,
        SemanticId methodId,
        ExecutionFault fault) => new(
            CurrentSchemaVersion,
            sourceCompilationId,
            semanticConfigurationId,
            methodId,
            ExecutionOutcome.Faulted,
            null,
            fault);
}
