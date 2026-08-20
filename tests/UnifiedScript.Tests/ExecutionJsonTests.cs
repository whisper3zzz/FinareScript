using System.Text.Json;
using UnifiedScript.Core;
using UnifiedScript.Runtime;

namespace UnifiedScript.Tests;

public sealed class ExecutionJsonTests
{
    [Fact]
    public void Serialize_RoundTripsReturnedAndFaultedResults()
    {
        SemanticId compilationId = SemanticId.FromKey("execution:compilation");
        SemanticId configurationId = SemanticId.FromKey("execution:configuration");
        SemanticId methodId = SemanticId.FromKey("execution:method");
        ExecutionResult[] results =
        [
            ExecutionResult.Returned(
                compilationId,
                configurationId,
                methodId,
                new ExecutionValue("System.Int32", "42")),
            ExecutionResult.Faulted(
                compilationId,
                configurationId,
                methodId,
                new ExecutionFault("System.InvalidOperationException", "failed")),
        ];

        foreach (ExecutionResult result in results)
        {
            string json = ExecutionJson.Serialize(result);

            Assert.Equal(result, ExecutionJson.Deserialize(json));
            Assert.Contains(ExecutionResult.CurrentSchemaVersion, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Deserialize_RejectsMismatchedOutcomePayload()
    {
        SemanticId compilationId = SemanticId.FromKey("execution:compilation");
        SemanticId configurationId = SemanticId.FromKey("execution:configuration");
        SemanticId methodId = SemanticId.FromKey("execution:method");
        string json = ExecutionJson.Serialize(ExecutionResult.Returned(
                compilationId,
                configurationId,
                methodId,
                new ExecutionValue("System.Int32", "42")))
            .Replace("\"returnValue\": {", "\"returnValue\": null,\n  \"ignored\": {", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => ExecutionJson.Deserialize(json));
    }

    [Fact]
    public void Deserialize_RejectsOlderSchema()
    {
        ExecutionResult result = ExecutionResult.Returned(
            SemanticId.FromKey("execution:compilation"),
            SemanticId.FromKey("execution:configuration"),
            SemanticId.FromKey("execution:method"),
            new ExecutionValue("System.Int32", "42"));
        string json = ExecutionJson.Serialize(result).Replace(
            ExecutionResult.CurrentSchemaVersion,
            "unifiedscript.execution-result/v0.0",
            StringComparison.Ordinal);

        JsonException exception = Assert.Throws<JsonException>(() => ExecutionJson.Deserialize(json));

        Assert.Contains("Unsupported execution-result schema", exception.Message);
    }
}
