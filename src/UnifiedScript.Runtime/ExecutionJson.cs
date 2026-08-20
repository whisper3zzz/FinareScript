using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.Runtime;

public static class ExecutionJson
{
    public static string Serialize(ExecutionResult result, bool writeIndented = true) =>
        JsonSerializer.Serialize(
            NormalizeAndValidate(result),
            UnifiedJson.CreateOptions(writeIndented));

    public static ExecutionResult Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ExecutionResult? result = JsonSerializer.Deserialize<ExecutionResult>(
            json,
            UnifiedJson.CreateOptions());
        return result is null
            ? throw new JsonException("The execution result was empty.")
            : NormalizeAndValidate(result);
    }

    public static ExecutionResult NormalizeAndValidate(ExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.SchemaVersion != ExecutionResult.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported execution-result schema '{result.SchemaVersion}'. " +
                $"Expected '{ExecutionResult.CurrentSchemaVersion}'.");
        }

        if (result.SourceCompilationId.IsEmpty ||
            result.SemanticConfigurationId.IsEmpty ||
            result.MethodId.IsEmpty)
        {
            throw new JsonException("An execution result has an empty required semantic ID.");
        }

        bool returned = result.Outcome == ExecutionOutcome.Returned &&
            result.ReturnValue is not null &&
            result.Fault is null;
        bool faulted = result.Outcome == ExecutionOutcome.Faulted &&
            result.ReturnValue is null &&
            result.Fault is not null;
        if (!returned && !faulted)
        {
            throw new JsonException(
                "An execution result must contain exactly one returned value or fault.");
        }

        if (result.ReturnValue is ExecutionValue value)
        {
            if (string.IsNullOrWhiteSpace(value.TypeMetadataName) ||
                string.IsNullOrWhiteSpace(value.Json))
            {
                throw new JsonException("An execution return value has invalid required fields.");
            }

            using JsonDocument _ = JsonDocument.Parse(value.Json);
        }

        if (result.Fault is ExecutionFault fault &&
            (string.IsNullOrWhiteSpace(fault.TypeMetadataName) || fault.Message is null))
        {
            throw new JsonException("An execution fault has invalid required fields.");
        }

        return result;
    }
}
