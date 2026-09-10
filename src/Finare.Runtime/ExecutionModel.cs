using System.Text.Json;
using Finare.Core;

namespace Finare.Runtime;

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

public sealed record ScriptStateOverride
{
    public ScriptStateOverride(SemanticId stateId, string json)
    {
        if (stateId.IsEmpty)
        {
            throw new ArgumentException("A script state override requires a state ID.", nameof(stateId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using JsonDocument document = JsonDocument.Parse(json);
        StateId = stateId;
        Json = document.RootElement.GetRawText();
    }

    public SemanticId StateId { get; }

    public string Json { get; }
}

public sealed record ScriptAssetReceiver
{
    public ScriptAssetReceiver(
        SemanticId assetId,
        IEnumerable<ScriptStateOverride>? stateOverrides = null)
    {
        if (assetId.IsEmpty)
        {
            throw new ArgumentException("A script asset receiver requires an asset ID.", nameof(assetId));
        }

        ScriptStateOverride[] overrides = (stateOverrides ?? []).ToArray();
        if (overrides.Select(static item => item.StateId).Distinct().Count() != overrides.Length)
        {
            throw new ArgumentException(
                "A script asset receiver cannot override the same state more than once.",
                nameof(stateOverrides));
        }

        AssetId = assetId;
        StateOverrides = overrides;
    }

    public SemanticId AssetId { get; }

    public IReadOnlyList<ScriptStateOverride> StateOverrides { get; }
}

public sealed record ExecutionInvocation
{
    public ExecutionInvocation(
        SemanticId methodId,
        IEnumerable<ExecutionArgument>? arguments = null,
        ScriptAssetReceiver? receiver = null)
    {
        if (methodId.IsEmpty)
        {
            throw new ArgumentException("An execution invocation requires a method ID.", nameof(methodId));
        }

        MethodId = methodId;
        Arguments = (arguments ?? []).ToArray();
        Receiver = receiver;
    }

    public SemanticId MethodId { get; }

    public IReadOnlyList<ExecutionArgument> Arguments { get; }

    public ScriptAssetReceiver? Receiver { get; }
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
