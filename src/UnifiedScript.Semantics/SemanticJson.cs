using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.Semantics;

public static class SemanticJson
{
    public static string Serialize(BoundCompilation compilation, bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        return JsonSerializer.Serialize(compilation, UnifiedJson.CreateOptions(writeIndented));
    }
}
