namespace UnifiedScript.Core;

public enum UnifiedDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record UnifiedDiagnostic(
    string Code,
    UnifiedDiagnosticSeverity Severity,
    string Message,
    SourceSpan Span,
    SemanticId? SemanticId = null);

