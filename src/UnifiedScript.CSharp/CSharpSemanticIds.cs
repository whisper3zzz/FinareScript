using Microsoft.CodeAnalysis;
using UnifiedScript.Core;

namespace UnifiedScript.CSharp;

internal static class CSharpSemanticIds
{
    public static SemanticId ForSymbol(string kind, string qualifiedName) =>
        SemanticId.FromKey($"symbol|{kind}|{qualifiedName}");

    public static SemanticId ForNode(string filePath, string ownerKey, SyntaxNode node, string? role = null)
    {
        string normalizedPath = filePath.Replace('\\', '/');
        string key = string.Join(
            '|',
            "syntax",
            normalizedPath,
            ownerKey,
            role ?? node.RawKind.ToString(System.Globalization.CultureInfo.InvariantCulture),
            node.SpanStart.ToString(System.Globalization.CultureInfo.InvariantCulture),
            node.Span.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return SemanticId.FromKey(key);
    }
}

