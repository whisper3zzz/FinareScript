using UnifiedScript.Core;
using UnifiedScript.Semantics;

namespace UnifiedScript.CSharp;

public sealed record SemanticBindingResult(
    BoundCompilation Compilation,
    IReadOnlyList<UnifiedDiagnostic> Diagnostics);
