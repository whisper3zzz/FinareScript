using System.Text.Json;

namespace UnifiedScript.Unreal;

internal static class UnrealCallbackBindings
{
    internal static bool ContainsCallback(UnrealTypeReference type) => type.Kind == UnrealValueKind.Callback ||
        type.ElementType is not null && ContainsCallback(type.ElementType) ||
        type.KeyType is not null && ContainsCallback(type.KeyType) ||
        type.ValueType is not null && ContainsCallback(type.ValueType);

    internal static IEnumerable<UnrealTypeReference> References(UnrealTypeReference type)
    {
        yield return type;
        if (type.ElementType is not null)
            foreach (var nested in References(type.ElementType)) yield return nested;
        if (type.KeyType is not null)
            foreach (var nested in References(type.KeyType)) yield return nested;
        if (type.ValueType is not null)
            foreach (var nested in References(type.ValueType)) yield return nested;
        if (type.CallbackSignature is { } signature)
        {
            foreach (var nested in References(signature.ReturnType)) yield return nested;
            foreach (var parameter in signature.Parameters)
                foreach (var nested in References(parameter.Type)) yield return nested;
        }
    }

    internal static IReadOnlyList<UnrealTypeReference> Collect(IReadOnlyList<UnrealReflectedType> types)
    {
        var callbacks = new Dictionary<string, UnrealTypeReference>(StringComparer.Ordinal);
        foreach (var type in types)
        foreach (var reference in type.Properties.Select(p => p.Type)
            .Concat(type.Functions.SelectMany(f => f.Parameters.Select(p => p.Type).Append(f.ReturnType)))
            .Concat(type.Delegates.SelectMany(d => d.Parameters.Select(p => p.Type).Append(d.ReturnType)))
            .SelectMany(References).Where(r => r.Kind == UnrealValueKind.Callback))
        {
            if (callbacks.TryGetValue(reference.TypePath!, out var previous) &&
                JsonSerializer.Serialize(previous) != JsonSerializer.Serialize(reference))
                throw new JsonException($"USUE5501: Callback '{reference.TypePath}' has conflicting declarations.");
            callbacks[reference.TypePath!] = reference;
        }
        return callbacks.Values.OrderBy(c => c.TypePath, StringComparer.Ordinal).ToArray();
    }
}
