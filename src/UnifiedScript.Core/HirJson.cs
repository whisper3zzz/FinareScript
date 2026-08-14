using System.Text.Json;

namespace UnifiedScript.Core;

public static class HirJson
{
    public static string Serialize(HirDocument document, bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, UnifiedJson.CreateOptions(writeIndented));
    }
}
