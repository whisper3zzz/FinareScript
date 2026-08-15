using UnifiedScript.Core;

namespace UnifiedScript.CSharp;

public sealed record CSharpIdentityPin(
    SemanticId Id,
    string Kind,
    SourceSpan CurrentSpan);
