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
}

