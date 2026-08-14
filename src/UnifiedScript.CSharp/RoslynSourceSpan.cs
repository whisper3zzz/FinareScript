using Microsoft.CodeAnalysis;
using UnifiedScript.Core;

namespace UnifiedScript.CSharp;

internal static class RoslynSourceSpan
{
    public static SourceSpan FromNode(SyntaxNode node, string fallbackFilePath)
    {
        FileLinePositionSpan lineSpan = node.GetLocation().GetLineSpan();
        string path = string.IsNullOrEmpty(lineSpan.Path) ? fallbackFilePath : lineSpan.Path;
        return new SourceSpan(
            path,
            new SourcePosition(lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1),
            new SourcePosition(lineSpan.EndLinePosition.Line + 1, lineSpan.EndLinePosition.Character + 1));
    }

    public static SourceSpan FromDiagnostic(Diagnostic diagnostic, string fallbackFilePath)
    {
        FileLinePositionSpan lineSpan = diagnostic.Location.GetLineSpan();
        string path = string.IsNullOrEmpty(lineSpan.Path) ? fallbackFilePath : lineSpan.Path;
        return new SourceSpan(
            path,
            new SourcePosition(lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1),
            new SourcePosition(lineSpan.EndLinePosition.Line + 1, lineSpan.EndLinePosition.Character + 1));
    }
}

