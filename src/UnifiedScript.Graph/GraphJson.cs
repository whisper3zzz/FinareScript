using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.Graph;

public static class GraphJson
{
    public static string Serialize(GraphDocument document, bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, UnifiedJson.CreateOptions(writeIndented));
    }

    public static GraphDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        GraphDocument? document = JsonSerializer.Deserialize<GraphDocument>(
            json,
            UnifiedJson.CreateOptions());
        if (document is null)
        {
            throw new JsonException("The graph document was empty.");
        }

        return document;
    }
}
