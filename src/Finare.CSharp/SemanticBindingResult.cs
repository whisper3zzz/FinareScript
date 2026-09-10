using Finare.Core;
using Finare.Semantics;

namespace Finare.CSharp;

public sealed record SemanticBindingResult(
    BoundCompilation Compilation,
    IReadOnlyList<UnifiedDiagnostic> Diagnostics);
